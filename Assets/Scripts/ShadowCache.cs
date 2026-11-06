using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// laneSHADOWCACHE (SHADOWCACHE v1): cached SUN cascades with a camera-coverage guard. ALL switches are DATA (shadow_cache.json, Assets/Data == StreamingAssets) and OFF by default.
// Missing file = everything OFF. Touches render state only (light update mode, Renderer.staticShadowCaster), never the sim.
//   S0 measure   : changes NOTHING. Runs the coverage test on the main camera as if the sun cascades were cached and logs how often a cascade would have to be re-captured.
//   S1 mixed     : sun shadowUpdateMode OnDemand + alwaysDrawDynamicShadows (HDRP "mixed cached"): static environment MeshRenderers are rendered into the cache only when the camera leaves the
//                  captured tile; fighters / everything else are drawn every frame on top. Needs the HDRP asset flag allowDirectionalMixedCachedShadows (checked; without it S1 degrades to S0).
//   S2 stagger   : sun OnDemand, NOT mixed: cascade k is re-rendered every ageFrames[k] frames (and whenever the camera leaves its tile). Everything in a stale cascade, fighters too, is as old as the cache.
// Why a guard: HDRP cascades are fitted to the camera (docs: Shadows-in-HDRP.md "Directional Light Update"); a cached cascade keeps the matrices it was captured with (HDGpuLightsBuilder.LightLoop.cs 596-650)
// and is exact only for receivers inside that tile. This file re-captures a cascade (RequestSubShadowMapRendering) the frame the NEW frustum slice would leave the OLD tile.
// Command line (same exe for every A/B arm):  -shadowcache:S0 | -shadowcache:S1 | -shadowcache:S2 (only these) | -shadowcache:none | -noshadowcache:S1 | -shadowcachereport (log every re-capture)
//   | -shadowcacheab[:periodS] (S1/S2 alternate ON/OFF inside one run and write shadowcache_ab.csv to -benchout:<dir>; read it with ab_report.py).
// Do NOT combine S1 with OPT3's G1 (G1 flips shadowCastingMode while the camera moves: every flip demotes that renderer to dynamic, see audit). G2 (fighter shadow proxies) combines well with S1.
public static class ShadowCache
{
    [Serializable] public class CoverageCfg
    {
        public float marginM = 0.25f;      // slack demanded inside the captured tile (m): PCF footprint + texel snapping + my sphere vs Unity's sphere [guess]. Negative tolerates overshoot
        public bool fromNear = true;       // test every cascade from the camera near plane: a pixel of cascade k-1's depth range that falls outside its sphere is shaded by cascade k
        public float nearM = 0.3f;
        public float sunEpsDeg = 0.05f;    // sun rotation change that forces a full re-capture
        public int maxCameras = 1;         // game cameras that rendered this or last frame; more = cache would be wrong (HDRP keeps ONE cached state per light) -> fall back to every frame
        public int fallbackHoldFrames = 120;
    }
    [Serializable] public class BailoutCfg { public bool enabled = true; public int windowFrames = 90; public float maxRecaptureFrac = 0.6f; public float cooldownS = 4f; }
    [Serializable] public class AbCfg
    {
        public bool enabled = false;       // in-process A/B: S1/S2 alternate ON / OFF every periodS inside ONE run, so run-to-run drift (8 ms across 16 runs in killarms) cancels
        public float periodS = 20f, settleS = 4f;   // frames in the first settleS after a switch are labelled settle=1 and ignored by ab_report.py
        public bool startOn = false;
        public string outFile = "shadowcache_ab.csv";   // written to -benchout:<dir> when given, else Application.persistentDataPath
    }
    [Serializable] public class S0Cfg { public bool enabled = false; }
    [Serializable] public class S1Cfg
    {
        public bool enabled = false;
        public bool requireAssetFlag = true;
        public string arenaRoot = "Arena";
        public string[] groups = new string[0];                 // child names of arenaRoot to scan; empty = the whole arena
        public string[] excludeNameContains = new string[0];    // hierarchy-path substrings that are NEVER static (moving / swapped things)
        public bool excludeRoster = true, excludeAnimators = true, excludeRigidbodies = true;
        public int auditEveryFrames = 6;                        // motion audit cadence for the marked renderers
        public float posEpsM = 0.002f, rotEpsDeg = 0.05f, scaleEps = 0.001f;
    }
    [Serializable] public class S2Cfg { public bool enabled = false; public int[] ageFrames = new int[] { 1, 3, 3, 3 }; }
    [Serializable] public class Cfg
    {
        public bool enabled = false;
        public string sunLight = "Key_ColdSun";
        public int startDelayFrames = 90;
        public bool log = true; public float logEveryS = 10f;
        public string[] disableWhenArgs = new string[0];
        public CoverageCfg coverage = new CoverageCfg(); public BailoutCfg bailout = new BailoutCfg();
        public AbCfg ab = new AbCfg();
        public S0Cfg S0 = new S0Cfg(); public S1Cfg S1 = new S1Cfg(); public S2Cfg S2 = new S2Cfg();
    }

    static Cfg cfg; static HashSet<string> only, off; static bool none, report;
    public static bool Report { get { return report; } }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; only = null; off = null; none = false; report = false; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            var c = new Cfg();
            try
            {
                var txt = HudCfg.ReadData("shadow_cache.json");
                if (!string.IsNullOrEmpty(txt)) { var r = JsonUtility.FromJson<Cfg>(txt); if (r != null) c = r; }
                else c.enabled = false;
            }
            catch (Exception e) { Debug.LogWarning("[ShadowCache] shadow_cache.json: " + e.Message); c.enabled = false; }
            if (c.coverage == null) c.coverage = new CoverageCfg(); if (c.bailout == null) c.bailout = new BailoutCfg();
            if (c.ab == null) c.ab = new AbCfg();
            if (c.S0 == null) c.S0 = new S0Cfg(); if (c.S1 == null) c.S1 = new S1Cfg(); if (c.S2 == null) c.S2 = new S2Cfg();
            if (c.S1.groups == null) c.S1.groups = new string[0]; if (c.S1.excludeNameContains == null) c.S1.excludeNameContains = new string[0];
            if (c.S2.ageFrames == null || c.S2.ageFrames.Length == 0) c.S2.ageFrames = new int[] { 1, 1, 1, 1 };
            if (c.disableWhenArgs == null) c.disableWhenArgs = new string[0];
            cfg = c;
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a.StartsWith("-shadowcache:", StringComparison.OrdinalIgnoreCase)) { var v = a.Substring(13); if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) none = true; else if (!v.Equals("json", StringComparison.OrdinalIgnoreCase)) only = Split(v); }
                else if (a.StartsWith("-noshadowcache:", StringComparison.OrdinalIgnoreCase)) off = Split(a.Substring(15));
                else if (a.Equals("-shadowcachereport", StringComparison.OrdinalIgnoreCase)) report = true;
                else if (a.StartsWith("-shadowcacheab", StringComparison.OrdinalIgnoreCase))   // -shadowcacheab or -shadowcacheab:<periodS>: in-process ON/OFF alternation + frame-ms file
                {
                    cfg.ab.enabled = true; int ix = a.IndexOf(':'); float per;
                    if (ix > 0 && float.TryParse(a.Substring(ix + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out per) && per > 1f) cfg.ab.periodS = per;
                }
            }
            return cfg;
        }
    }
    static HashSet<string> Split(string s) { var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var p in s.Split(',')) if (p.Trim().Length > 0) h.Add(p.Trim()); return h; }

    /// <summary>True when switch `id` is on: command line first, then shadow_cache.json (master "enabled" and the switch's own "enabled").</summary>
    public static bool On(string id)
    {
        var c = C; if (none) return false;
        if (only != null) return only.Contains(id);
        if (off != null && off.Contains(id)) return false;
        if (!c.enabled) return false;
        switch (id) { case "S0": return c.S0.enabled; case "S1": return c.S1.enabled; case "S2": return c.S2.enabled; }
        return false;
    }
    public static string Summary()
    {
        var sb = new System.Text.StringBuilder("[ShadowCache] on:"); bool any = false;
        foreach (var id in new[] { "S0", "S1", "S2" }) if (On(id)) { sb.Append(' ').Append(id); any = true; }
        if (!any) sb.Append(" none"); return sb.ToString();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = C; Debug.Log(Summary());
        if (!On("S0") && !On("S1") && !On("S2")) return;
        foreach (var a in Environment.GetCommandLineArgs()) foreach (var d in c.disableWhenArgs) if (!string.IsNullOrEmpty(d) && a.Equals(d, StringComparison.OrdinalIgnoreCase)) { Debug.Log("[ShadowCache] off for harness arg " + d); return; }
        var go = new GameObject("ShadowCache"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<ShadowCacheRunner>();
    }

    // ------------------------------------------------------------------ geometry (same formulas as _agents/laneSHADOWCACHE/cache_window_model.py)
    public struct Tile { public bool valid; public Vector3 c, right, up; public float r; }

    /// <summary>8 world corners of the camera frustum between view depth n and f (first 4 at n, last 4 at f).</summary>
    public static void SliceCorners(Camera cam, float n, float f, Vector3[] outCorners, Vector3[] tmp)
    {
        cam.CalculateFrustumCorners(new Rect(0f, 0f, 1f, 1f), n, Camera.MonoOrStereoscopicEye.Mono, tmp);
        var t = cam.transform; for (int i = 0; i < 4; i++) outCorners[i] = t.TransformPoint(tmp[i]);
        cam.CalculateFrustumCorners(new Rect(0f, 0f, 1f, 1f), f, Camera.MonoOrStereoscopicEye.Mono, tmp);
        for (int i = 0; i < 4; i++) outCorners[4 + i] = t.TransformPoint(tmp[i]);
    }
    /// <summary>Tight bounding sphere of the slice [n,f] centred on the view axis (radius re-measured on the real corners).</summary>
    public static Tile MakeTile(Camera cam, float n, float f, Vector3[] corners, Transform sun)
    {
        float k = Mathf.Sqrt(1f + cam.aspect * cam.aspect) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float z = (k * k >= (f - n) / (f + n)) ? f : 0.5f * (f + n) * (1f + k * k);
        var t = new Tile { valid = true, c = cam.transform.position + cam.transform.forward * z, right = sun.right, up = sun.up };
        float r = 0f; for (int i = 0; i < 8; i++) r = Mathf.Max(r, (corners[i] - t.c).magnitude); t.r = r; return t;
    }
    /// <summary>True when every corner of the new slice is inside the captured light-space square, with `margin` of slack (negative = tolerate overshoot).</summary>
    public static bool Covered(Tile t, Vector3[] corners, float margin)
    {
        if (!t.valid) return false; float lim = t.r - margin;
        for (int i = 0; i < 8; i++) { var q = corners[i] - t.c; if (Mathf.Abs(Vector3.Dot(q, t.right)) > lim || Mathf.Abs(Vector3.Dot(q, t.up)) > lim) return false; }
        return true;
    }
}

public sealed class ShadowCacheRunner : MonoBehaviour
{
    sealed class Mark { public Renderer r; public Transform t; public Vector3 p, s; public Quaternion q; public ShadowCastingMode m; public bool live; }
    const int MaxC = 4;
    ShadowCache.Cfg C;
    Light sun; HDAdditionalLightData hd; Camera cam;
    ShadowUpdateMode origMode; bool origAlways, origSaved;
    bool s0, s1, s2, applied, assetFlag, started;
    int frame, lastEval = -1, otherCamFrame = -100, fallbackUntil = -1;
    float cooldownUntilT = -1f;
    readonly ShadowCache.Tile[] tile = new ShadowCache.Tile[MaxC]; readonly int[] age = new int[MaxC];
    readonly Vector3[] corners = new Vector3[8], tmp4 = new Vector3[4];
    Quaternion sunRot; float lastD = -1f; int lastCount = -1; readonly float[] lastSplit = new float[3];
    // stats
    int evals, capFrames; readonly int[] recap = new int[MaxC]; int rFirst, rCover, rSun, rSet, rDemote, rAge, rMulti, bailouts, demoted;
    readonly Queue<byte> window = new Queue<byte>(); int windowHits;
    Vector3 lastPos, capPos; bool lastPosSet; double speedSum; float speedMax;
    readonly float[] sp = new float[3]; readonly string[] reasons = new string[MaxC];
    double evalMsSum; float lastLog;
    readonly List<Mark> marks = new List<Mark>(); int marked, skippedFlag;
    bool forceAll;
    // in-process A/B recorder
    bool abOn, abOffNow; float abSwitchT, abStartT; readonly List<string> abRows = new List<string>(); string abPath; int abFlushed;

    void OnEnable() { RenderPipelineManager.beginCameraRendering += OnBegin; }
    void OnDisable() { RenderPipelineManager.beginCameraRendering -= OnBegin; AbFlush(); Restore("disable"); }
    void OnDestroy() { RenderPipelineManager.beginCameraRendering -= OnBegin; }
    void OnApplicationQuit() { AbFlush(); Restore("quit"); }

    void Update()
    {
        frame++;
        if (!started && frame >= Mathf.Max(1, ShadowCache.C.startDelayFrames)) { started = true; Init(); }
        if (started && abPath != null && enabled) AbTick();
        if (started && C != null && C.log && Time.unscaledTime - lastLog >= C.logEveryS && evals > 0) { lastLog = Time.unscaledTime; Debug.Log(Stat()); }
    }

    void Init()
    {
        C = ShadowCache.C;
        s0 = ShadowCache.On("S0"); s1 = ShadowCache.On("S1"); s2 = ShadowCache.On("S2") && !s1;
        if (!s0 && !s1 && !s2) { enabled = false; return; }
        foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (l.name == C.sunLight && l.type == LightType.Directional) { sun = l; break; }
        if (sun != null) hd = sun.GetComponent<HDAdditionalLightData>();
        if (sun == null || hd == null || sun.shadows == LightShadows.None) { Debug.LogWarning("[ShadowCache] sun '" + C.sunLight + "' missing / no HDAdditionalLightData / shadows off: nothing to cache, all off"); enabled = false; return; }
        var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        assetFlag = asset != null && asset.currentPlatformRenderPipelineSettings.hdShadowInitParams.allowDirectionalMixedCachedShadows;
        if (s1 && C.S1.requireAssetFlag && !assetFlag)
        {
            Debug.LogWarning("[ShadowCache] S1 needs HDRP asset allowDirectionalMixedCachedShadows=1 (flag=" + assetFlag + ", asset " + (asset != null ? asset.name : "null") + "): S1 NOT applied, running as S0 (measure only)");
            s1 = false; s0 = true;
        }
        origMode = hd.shadowUpdateMode; origAlways = hd.alwaysDrawDynamicShadows; origSaved = true;
        if (s1) MarkStatic();
        Debug.Log("[ShadowCache] init sun=" + sun.name + " mode=" + origMode + " alwaysDrawDynamic=" + origAlways + " assetFlag=" + assetFlag + " S0=" + s0 + " S1=" + s1 + " S2=" + s2 + (s1 ? " staticMarked=" + marked : "")
                  + " margin=" + C.coverage.marginM.ToString("F2") + "m");
        ClearTiles(); forceAll = true;
        if ((s1 || s2) && C.ab.enabled)
        {
            abOn = C.ab.startOn; abOffNow = !abOn; abSwitchT = abStartT = Time.unscaledTime;
            string dir = null; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-benchout:", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(10);
            if (string.IsNullOrEmpty(dir)) dir = Application.persistentDataPath;
            try { System.IO.Directory.CreateDirectory(dir); abPath = System.IO.Path.Combine(dir, C.ab.outFile); System.IO.File.WriteAllText(abPath, "t_s,frame_ms,state,settle,mode\n"); }
            catch (Exception e) { Debug.LogWarning("[ShadowCache] ab file: " + e.Message); abPath = null; }
            Debug.Log("[ShadowCache] ab recorder -> " + abPath + " period=" + C.ab.periodS + "s settle=" + C.ab.settleS + "s startOn=" + abOn);
        }
    }

    void AbTick()
    {
        float now = Time.unscaledTime;
        if (now - abSwitchT >= C.ab.periodS) { abOn = !abOn; abOffNow = !abOn; abSwitchT = now; if (!abOn && applied) { Restore("ab-off"); ClearTiles(); forceAll = true; } Debug.Log("[ShadowCache] ab -> " + (abOn ? "ON" : "OFF") + " t=" + (now - abStartT).ToString("F1")); }
        abRows.Add((now - abStartT).ToString("F3") + "," + (Time.unscaledDeltaTime * 1000f).ToString("F3") + "," + (abOn ? 1 : 0) + "," + ((now - abSwitchT) < C.ab.settleS ? 1 : 0) + "," + (s1 ? "S1" : "S2"));
        if (abRows.Count - abFlushed >= 600) AbFlush();
    }
    void AbFlush()
    {
        if (abPath == null || abRows.Count == abFlushed) return;
        try { var sb = new System.Text.StringBuilder(); for (int i = abFlushed; i < abRows.Count; i++) sb.Append(abRows[i]).Append('\n'); System.IO.File.AppendAllText(abPath, sb.ToString()); abFlushed = abRows.Count; } catch { }
    }

    // ------------------------------------------------------------------ S1: which renderers are static environment
    static string PathOf(Transform t) { var s = t.name; for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s; return s; }
    void MarkStatic()
    {
        var c = C.S1; var skip = new List<string>(); foreach (var s in c.excludeNameContains) if (!string.IsNullOrEmpty(s)) skip.Add(s);
        if (c.excludeRoster) { try { foreach (var ch in Roster.All) if (ch != null && !string.IsNullOrEmpty(ch.objectName)) skip.Add(ch.objectName); } catch { } }
        var rootGo = GameObject.Find(c.arenaRoot); if (rootGo == null) { Debug.LogWarning("[ShadowCache] S1: arena root '" + c.arenaRoot + "' not found, nothing marked"); return; }
        var roots = new List<Transform>();
        if (c.groups.Length == 0) roots.Add(rootGo.transform);
        else foreach (var g in c.groups) { var ch = rootGo.transform.Find(g); if (ch != null) roots.Add(ch); }
        foreach (var root in roots)
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (r == null || !r.enabled || r.shadowCastingMode == ShadowCastingMode.Off || r.staticShadowCaster) continue;
                if (c.excludeRigidbodies && r.GetComponentInParent<Rigidbody>() != null) continue;
                if (c.excludeAnimators && r.GetComponentInParent<Animator>() != null) continue;
                var path = PathOf(r.transform); bool bad = false;
                for (int i = 0; i < skip.Count && !bad; i++) if (path.IndexOf(skip[i], StringComparison.Ordinal) >= 0) bad = true;
                if (bad) { skippedFlag++; continue; }
                r.staticShadowCaster = true; var t = r.transform;
                marks.Add(new Mark { r = r, t = t, p = t.position, q = t.rotation, s = t.lossyScale, m = r.shadowCastingMode, live = true }); marked++;
            }
    }

    int Audit()
    {
        var c = C.S1; int n = 0; float pe2 = c.posEpsM * c.posEpsM;
        for (int i = marks.Count - 1; i >= 0; i--)
        {
            var m = marks[i]; bool bad = false;
            if (m.r == null || m.t == null) bad = true;
            else
            {
                bool live = m.r.enabled && m.r.gameObject.activeInHierarchy;
                if (live != m.live || m.r.shadowCastingMode != m.m) bad = true;
                else if ((m.t.position - m.p).sqrMagnitude > pe2 || Quaternion.Angle(m.t.rotation, m.q) > c.rotEpsDeg || (m.t.lossyScale - m.s).sqrMagnitude > c.scaleEps * c.scaleEps) bad = true;
            }
            if (!bad) continue;
            if (m.r != null) m.r.staticShadowCaster = false;   // from now on it is drawn every frame like a fighter
            marks.RemoveAt(i); n++;
        }
        return n;
    }

    // ------------------------------------------------------------------ per-camera evaluation (before HDRP culls this camera, so the request is in time)
    void OnBegin(ScriptableRenderContext ctx, Camera c)
    {
        if (!started || hd == null || sun == null) return;
        if (c == null || c.cameraType != CameraType.Game) return;
        if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; if (cam == null) cam = UnityEngine.Object.FindFirstObjectByType<Camera>(); }
        if (c != cam) { otherCamFrame = Time.frameCount; return; }
        int fc = Time.frameCount; if (fc == lastEval) return; lastEval = fc;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Evaluate();
        evalMsSum += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    void ClearTiles() { for (int i = 0; i < MaxC; i++) { tile[i] = default(ShadowCache.Tile); age[i] = 0; } }

    void Evaluate()
    {
        evals++; int fc = Time.frameCount; var cc = C.coverage;
        if (evals == 1) Debug.Log("[ShadowCache] main camera = '" + cam.name + "' (Camera.main " + (Camera.main == cam) + ") fov " + cam.fieldOfView.ToString("F1") + " aspect " + cam.aspect.ToString("F2") + " far " + cam.farClipPlane);
        // camera speed stats
        var pos = cam.transform.position; if (lastPosSet) { float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime); float v = (pos - lastPos).magnitude / dt; speedSum += v; if (v > speedMax) speedMax = v; } lastPos = pos; lastPosSet = true;

        // settings of the sun cascades
        float D = 40f; int count = 2; sp[0] = 0.3f; sp[1] = 1f; sp[2] = 1f;
        try
        {
            var st = VolumeManager.instance != null ? VolumeManager.instance.stack : null; var hs = st != null ? st.GetComponent<HDShadowSettings>() : null;
            if (hs != null) { D = hs.maxShadowDistance.value; count = hs.cascadeShadowSplitCount.value; sp[0] = hs.cascadeShadowSplit0.value; sp[1] = hs.cascadeShadowSplit1.value; sp[2] = hs.cascadeShadowSplit2.value; }
        }
        catch { }
        D = Mathf.Min(D, cam.farClipPlane); count = Mathf.Clamp(count, 1, MaxC);

        // reasons that invalidate everything
        string allReason = null;
        if (forceAll) { allReason = "first"; forceAll = false; }
        else if (Quaternion.Angle(sun.transform.rotation, sunRot) > cc.sunEpsDeg) allReason = "sun";
        else if (!Mathf.Approximately(D, lastD) || count != lastCount || !Mathf.Approximately(sp[0], lastSplit[0]) || !Mathf.Approximately(sp[1], lastSplit[1]) || !Mathf.Approximately(sp[2], lastSplit[2])) allReason = "settings";
        if (s1 && C.S1.auditEveryFrames > 0 && (evals % C.S1.auditEveryFrames) == 0) { int d = Audit(); if (d > 0) { demoted += d; if (allReason == null) allReason = "demote"; } }
        bool multi = (fc - otherCamFrame) <= 2 && cc.maxCameras <= 1;
        if (multi) { fallbackUntil = fc + Mathf.Max(1, cc.fallbackHoldFrames); }
        sunRot = sun.transform.rotation; lastD = D; lastCount = count; lastSplit[0] = sp[0]; lastSplit[1] = sp[1]; lastSplit[2] = sp[2];

        // fallback (several game cameras, or bail-out cool-down): plain every-frame sun shadows, nothing is tracked
        bool inFallback = fc < fallbackUntil || Time.unscaledTime < cooldownUntilT || abOffNow;
        if (inFallback) { if (applied) Restore("fallback"); ClearTiles(); forceAll = true; return; }

        // per-cascade slice
        int need = 0, needReal = 0;   // needReal = re-captures that are not the S2 age schedule (the bail-out counts only those)
        for (int k = 0; k < count; k++)
        {
            float f = (k == count - 1) ? D : sp[k] * D; float n = (k == 0 || cc.fromNear) ? cc.nearM : sp[k - 1] * D;
            ShadowCache.SliceCorners(cam, n, f, corners, tmp4);
            age[k]++;
            string why = allReason;
            if (why == null && !tile[k].valid) why = "first";
            if (why == null && !ShadowCache.Covered(tile[k], corners, cc.marginM)) why = "cover";
            if (why == null && s2 && age[k] >= Mathf.Max(1, C.S2.ageFrames[Mathf.Min(k, C.S2.ageFrames.Length - 1)])) why = "age";
            if (why != null) { need |= 1 << k; if (why != "age") needReal |= 1 << k; reasons[k] = why; tile[k] = ShadowCache.MakeTile(cam, n, f, corners, sun.transform); age[k] = 0; recap[k]++; }
        }
        for (int k = count; k < MaxC; k++) tile[k] = default(ShadowCache.Tile);
        if (need != 0)
        {
            capFrames++;
            for (int k = 0; k < count; k++) if ((need & (1 << k)) != 0)
            {
                switch (reasons[k]) { case "first": rFirst++; break; case "cover": rCover++; break; case "sun": rSun++; break; case "settings": rSet++; break; case "demote": rDemote++; break; case "age": rAge++; break; default: rMulti++; break; }
                if (ShadowCache.Report) Debug.Log("[ShadowCache] recapture f" + fc + " c" + k + " " + reasons[k] + " camDrift=" + (pos - capPos).magnitude.ToString("F2") + "m");
            }
            capPos = pos;
        }

        // bail-out window: re-capturing most frames means the cache costs more than it saves
        if (C.bailout.enabled)
        {
            window.Enqueue((byte)(needReal != 0 ? 1 : 0)); if (needReal != 0) windowHits++;
            while (window.Count > Mathf.Max(8, C.bailout.windowFrames)) { if (window.Dequeue() == 1) windowHits--; }
            if (window.Count >= Mathf.Max(8, C.bailout.windowFrames) && windowHits > C.bailout.maxRecaptureFrac * window.Count && (s1 || s2) && applied)
            {
                bailouts++; cooldownUntilT = Time.unscaledTime + C.bailout.cooldownS; window.Clear(); windowHits = 0;
                Debug.Log("[ShadowCache] bail-out #" + bailouts + ": too many re-captures, every-frame shadows for " + C.bailout.cooldownS.ToString("F1") + " s");
            }
        }

        // act
        if (!(s1 || s2)) return;   // S0: measure only
        if (!applied) { Apply(); need = (1 << count) - 1; }
        if (need == 0) return;
        if (need == (1 << count) - 1) { hd.RequestShadowMapRendering(); }
        else for (int k = 0; k < count; k++) if ((need & (1 << k)) != 0) hd.RequestSubShadowMapRendering(k);
    }

    void Apply()
    {
        if (hd == null || applied) return;
        if (s1) { hd.alwaysDrawDynamicShadows = true; hd.shadowUpdateMode = ShadowUpdateMode.OnDemand; }
        else { hd.alwaysDrawDynamicShadows = false; hd.shadowUpdateMode = ShadowUpdateMode.OnDemand; }
        applied = true; Debug.Log("[ShadowCache] applied: sun OnDemand alwaysDrawDynamic=" + hd.alwaysDrawDynamicShadows + (s1 ? " (mixed)" : " (stagger)"));
    }

    void Restore(string why)
    {
        if (!applied || hd == null || !origSaved) { return; }
        hd.shadowUpdateMode = origMode; hd.alwaysDrawDynamicShadows = origAlways; applied = false;
        if (why == "disable" || why == "quit") { foreach (var m in marks) if (m.r != null) m.r.staticShadowCaster = false; marks.Clear(); }
        Debug.Log("[ShadowCache] restored sun mode " + origMode + " (" + why + ")");
    }

    string Stat()
    {
        int frames = Mathf.Max(1, evals - 0); double spd = evals > 1 ? speedSum / (evals - 1) : 0;
        var sb = new System.Text.StringBuilder("[ShadowCache] stat mode=" + (s1 ? "S1" : s2 ? "S2" : "S0") + " applied=" + applied + " evals=" + evals + " captureFrames=" + capFrames + " (" + (100f * capFrames / frames).ToString("F1") + "%)");
        for (int k = 0; k < MaxC; k++) if (recap[k] > 0) sb.Append(" c" + k + "=" + recap[k] + " (every " + ((float)evals / recap[k]).ToString("F1") + " frames)");
        sb.Append(" why first=" + rFirst + " cover=" + rCover + " sun=" + rSun + " settings=" + rSet + " demote=" + rDemote + " age=" + rAge + " fallback=" + rMulti);
        sb.Append(" camSpeed mean=" + spd.ToString("F2") + " max=" + speedMax.ToString("F1") + " m/s bailouts=" + bailouts);
        if (s1) sb.Append(" static=" + marks.Count + "/" + marked + " demoted=" + demoted + " skipped=" + skippedFlag);
        sb.Append(" avgEvalMs=" + (evalMsSum / frames).ToString("F3"));
        return sb.ToString();
    }
}
