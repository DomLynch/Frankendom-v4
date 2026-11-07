using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// SHADOWSAVER v1 (laneSHADOWCACHE v2). Shadow savings that must change nothing visible. ALL OFF by default; every number is a row in shadow_saver.json (Assets/Data == StreamingAssets).
// Missing file = everything off. Render state only (Renderer.shadowCastingMode, a runtime Volume), never the sim. Restores everything on disable and on quit.
//   P2 small casters : MeshRenderers whose bounds are at most maxExtentM (and, when minScreenPx > 0, whose projected size from the main camera is under minScreenPx) stop casting. Fighters (Roster),
//                      skinned meshes, rigidbodies, animators and path substrings from data are never touched. Re-evaluated every evalEveryFrames with a hysteresis factor.
//   P3 cascade trim  : a global high-priority Volume overrides ONLY the HDShadowSettings fields whose value is > 0 in the data (cascade count, splits, max distance). 0 = leave the scene's own value.
//   SH shots         : matched-frame capture for the pixel gate. At each sim tick in shots.ticks the sim is frozen (timeScale 0), the picture settles settleFrames, a PNG named
//                      ss_<tag>_t<target>_a<actualTick>.png goes to -benchout:<dir>. The gate only compares files whose actual ticks are equal. Never combine with -perfbench (frozen frames would be timed).
//   P1 punctual cache and S1 mixed sun cache are NOT re-implemented: P1 = perf_fixes.json A1 + A1b (PerfFixes.cs), S1 = shadow_cache.json (ShadowCache.cs). apply_shadowsaver.py flips those flags with one switch.
// Command line: -shadowsaver:P2,P3,SH (only these) | -shadowsaver:none | -noshadowsaver:P2 | -ssset:key=value,key=value (data override for A/B arms, keys in Override()) | -ssshots | -sstag:<tag> | -ssquit | -benchout:<dir>
public static class ShadowSaver
{
    [Serializable] public class P2Cfg
    {
        public bool enabled = false;
        public float maxExtentM = 0f;            // largest bounds side; a renderer above this is never touched. 0 = P2 selects nothing
        public float minScreenPx = 0f;           // > 0: also needs projected size under this many pixels at the main camera; 0 = size test only
        public float hysteresis = 1.25f;         // a skipped renderer comes back when its size exceeds the limit x this
        public int evalEveryFrames = 30;
        public string[] excludePathContains = new string[0];
        public bool excludeRoster = true, excludeRigidbodies = true, excludeAnimators = true;
    }
    [Serializable] public class P3Cfg { public bool enabled = false; public int cascades = 0; public float split0 = 0f, split1 = 0f, split2 = 0f, maxDistance = 0f; public int volumePriority = 1000; }
    [Serializable] public class ShotCfg { public bool enabled = false; public int[] ticks = new int[0]; public int settleFrames = 16; public int holdFrames = 4; public bool quitWhenDone = true; public string namePattern = "ss_{tag}_t{target}_a{actual}.png"; }
    [Serializable] public class Cfg
    {
        public bool enabled = false;
        public int startDelayFrames = 90;
        public bool log = true; public float logEveryS = 10f;
        public string[] disableWhenArgs = new string[0];
        public P2Cfg P2 = new P2Cfg(); public P3Cfg P3 = new P3Cfg(); public ShotCfg SH = new ShotCfg();
    }

    static Cfg cfg; static HashSet<string> only, off; static bool none;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; only = null; off = null; none = false; }
    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return a.Substring(p.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static HashSet<string> Split(string s) { var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var p in s.Split(',')) if (p.Trim().Length > 0) h.Add(p.Trim()); return h; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            var c = new Cfg();
            try { var txt = HudCfg.ReadData("shadow_saver.json"); if (!string.IsNullOrEmpty(txt)) { var r = JsonUtility.FromJson<Cfg>(txt); if (r != null) c = r; } else c.enabled = false; }
            catch (Exception e) { Debug.LogWarning("[ShadowSaver] shadow_saver.json: " + e.Message); c.enabled = false; }
            if (c.P2 == null) c.P2 = new P2Cfg(); if (c.P3 == null) c.P3 = new P3Cfg(); if (c.SH == null) c.SH = new ShotCfg();
            if (c.P2.excludePathContains == null) c.P2.excludePathContains = new string[0];
            if (c.SH.ticks == null) c.SH.ticks = new int[0]; if (c.disableWhenArgs == null) c.disableWhenArgs = new string[0];
            cfg = c;
            var o = Arg("-shadowsaver:"); if (o != null) { if (o.Equals("none", StringComparison.OrdinalIgnoreCase)) none = true; else only = Split(o); }
            var n = Arg("-noshadowsaver:"); if (n != null) off = Split(n);
            if (Flag("-ssshots") && only == null && !none) { only = Split("SH"); }
            var kv = Arg("-ssset:"); if (kv != null) foreach (var p in kv.Split(',')) Override(p);
            return cfg;
        }
    }

    // data override for A/B arms: the keys are the json field names, "P2.maxExtentM=0.3". Unknown key = warning, no change.
    static void Override(string pair)
    {
        int i = pair.IndexOf('='); if (i <= 0) return;
        string k = pair.Substring(0, i).Trim(); float v;
        if (!float.TryParse(pair.Substring(i + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return;
        switch (k)
        {
            case "P2.maxExtentM": cfg.P2.maxExtentM = v; break;
            case "P2.minScreenPx": cfg.P2.minScreenPx = v; break;
            case "P2.hysteresis": cfg.P2.hysteresis = v; break;
            case "P2.evalEveryFrames": cfg.P2.evalEveryFrames = Mathf.Max(1, (int)v); break;
            case "P3.cascades": cfg.P3.cascades = (int)v; break;
            case "P3.split0": cfg.P3.split0 = v; break;
            case "P3.split1": cfg.P3.split1 = v; break;
            case "P3.split2": cfg.P3.split2 = v; break;
            case "P3.maxDistance": cfg.P3.maxDistance = v; break;
            case "SH.settleFrames": cfg.SH.settleFrames = Mathf.Max(1, (int)v); break;
            default: Debug.LogWarning("[ShadowSaver] -ssset: unknown key '" + k + "'"); break;
        }
    }

    /// <summary>True when switch `id` (P2, P3, SH) is on: command line first, then shadow_saver.json (master and the switch's own "enabled").</summary>
    public static bool On(string id)
    {
        var c = C; if (none) return false;
        if (only != null) return only.Contains(id);
        if (off != null && off.Contains(id)) return false;
        if (!c.enabled) return false;
        switch (id) { case "P2": return c.P2.enabled; case "P3": return c.P3.enabled; case "SH": return c.SH.enabled; }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = C;
        foreach (var a in c.disableWhenArgs) if (!string.IsNullOrEmpty(a) && Flag(a)) { Debug.Log("[ShadowSaver] off (" + a + ")"); return; }
        if (!On("P2") && !On("P3") && !On("SH")) { Debug.Log("[ShadowSaver] off (shadow_saver.json: all switches false)"); return; }
        var go = new GameObject("ShadowSaver"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<ShadowSaverRunner>();
    }
}

public class ShadowSaverRunner : MonoBehaviour
{
    struct Rec { public Renderer r; public ShadowCastingMode orig; public float ext; public bool skipped; }
    ShadowSaver.Cfg C; int frame; bool started; float lastLog;
    readonly List<Rec> recs = new List<Rec>(); int skippedN, flipsN, evalN, candN;
    Volume vol; Camera cam;
    // shots: state 0 wait for the tick, 1 settle, 2 hold after the capture
    int shotIdx, settle, hold, state; float prevScale = 1f; string tag, dir; bool scaleWarned;

    void Update()
    {
        frame++; C = ShadowSaver.C;
        if (!started && frame >= Mathf.Max(1, C.startDelayFrames)) { started = true; Init(); }
        if (!started) return;
        if (ShadowSaver.On("P2") && C.P2.evalEveryFrames > 0 && frame % C.P2.evalEveryFrames == 0) EvalP2();
        if (ShadowSaver.On("SH")) ShotTick();
        if (C.log && Time.unscaledTime - lastLog >= C.logEveryS && (recs.Count > 0 || vol != null)) { lastLog = Time.unscaledTime; Debug.Log("[ShadowSaver] " + Stat()); }
    }

    void Init()
    {
        if (ShadowSaver.On("P3")) ApplyP3();
        if (ShadowSaver.On("P2")) ScanP2();
        tag = "run";
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-sstag:", StringComparison.OrdinalIgnoreCase)) tag = a.Substring(7);
            if (a.StartsWith("-benchout:", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(10).Trim('"');
        }
        if (string.IsNullOrEmpty(dir)) dir = Application.persistentDataPath;
        try { Directory.CreateDirectory(dir); } catch { }
        Debug.Log("[ShadowSaver] on: P2=" + ShadowSaver.On("P2") + " P3=" + ShadowSaver.On("P3") + " SH=" + ShadowSaver.On("SH") + " " + Stat());
    }

    string Stat() { return "candidates=" + candN + " skipped=" + skippedN + " flips=" + flipsN + " evals=" + evalN + (vol != null ? " p3Volume=on" : ""); }

    // ---- P3 cascade trim: a runtime Volume that overrides only the data rows > 0
    void ApplyP3()
    {
        var p = C.P3; var go = new GameObject("ShadowSaverVolume"); go.transform.SetParent(transform, false);
        vol = go.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = p.volumePriority; vol.weight = 1f;
        var prof = ScriptableObject.CreateInstance<VolumeProfile>(); var sh = prof.Add<HDShadowSettings>(true);
        if (p.cascades > 0) sh.cascadeShadowSplitCount.Override(p.cascades);
        if (p.split0 > 0f) sh.cascadeShadowSplit0.Override(p.split0);
        if (p.split1 > 0f) sh.cascadeShadowSplit1.Override(p.split1);
        if (p.split2 > 0f) sh.cascadeShadowSplit2.Override(p.split2);
        if (p.maxDistance > 0f) sh.maxShadowDistance.Override(p.maxDistance);
        vol.sharedProfile = prof;
        Debug.Log("[ShadowSaver] P3 volume: cascades=" + p.cascades + " split0=" + p.split0 + " split1=" + p.split1 + " split2=" + p.split2 + " maxDistance=" + p.maxDistance + " (0 = scene value)");
    }

    // ---- P2 small casters
    static string PathOf(Transform t) { string p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }

    void ScanP2()
    {
        var p = C.P2; if (p.maxExtentM <= 0f) { Debug.Log("[ShadowSaver] P2 maxExtentM is 0: selects nothing"); return; }
        var skipNames = new HashSet<string>();
        if (p.excludeRoster) { try { foreach (var ch in Roster.All) if (ch != null && !string.IsNullOrEmpty(ch.objectName)) skipNames.Add(ch.objectName); } catch { } }
        foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (r == null || !r.enabled || r.shadowCastingMode != ShadowCastingMode.On) continue;
            var b = r.bounds; float ext = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (ext > p.maxExtentM * p.hysteresis) continue;   // can never qualify
            string path = PathOf(r.transform); bool bad = false;
            foreach (var s in p.excludePathContains) if (!string.IsNullOrEmpty(s) && path.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) { bad = true; break; }
            if (!bad && skipNames.Count > 0) { for (var t = r.transform; t != null && !bad; t = t.parent) if (skipNames.Contains(t.name)) bad = true; }
            if (!bad && p.excludeRigidbodies && r.GetComponentInParent<Rigidbody>() != null) bad = true;
            if (!bad && p.excludeAnimators && r.GetComponentInParent<Animator>() != null) bad = true;
            if (bad) continue;
            recs.Add(new Rec { r = r, orig = r.shadowCastingMode, ext = ext, skipped = false });
        }
        candN = recs.Count; EvalP2();
    }

    void EvalP2()
    {
        var p = C.P2; if (recs.Count == 0) return;
        if (cam == null) cam = Camera.main;
        bool useScreen = p.minScreenPx > 0f && cam != null;
        float pxPerM = useScreen ? cam.pixelHeight / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) : 0f;   // projected pixels per metre at distance 1
        evalN++;
        for (int i = 0; i < recs.Count; i++)
        {
            var e = recs[i]; if (e.r == null) continue;
            float lim = e.skipped ? p.maxExtentM * p.hysteresis : p.maxExtentM;
            bool small = e.ext <= lim;
            if (small && useScreen)
            {
                float d = Mathf.Max(0.01f, Vector3.Distance(cam.transform.position, e.r.bounds.center)), px = e.ext / d * pxPerM;
                small = px < (e.skipped ? p.minScreenPx * p.hysteresis : p.minScreenPx);
            }
            if (small == e.skipped) continue;
            e.skipped = small; e.r.shadowCastingMode = small ? ShadowCastingMode.Off : e.orig; recs[i] = e; skippedN += small ? 1 : -1; flipsN++;
        }
    }

    void RestoreP2() { for (int i = 0; i < recs.Count; i++) { var e = recs[i]; if (e.r != null && e.skipped) e.r.shadowCastingMode = e.orig; } recs.Clear(); skippedN = 0; }

    // ---- SH matched-frame capture
    void ShotTick()
    {
        var s = C.SH; if (shotIdx >= s.ticks.Length) return;
        int tick = Mathf.RoundToInt(Time.fixedTime / Mathf.Max(1e-6f, Time.fixedDeltaTime));
        if (state == 0)
        {
            if (tick >= s.ticks[shotIdx]) { prevScale = Time.timeScale > 0f ? Time.timeScale : 1f; Time.timeScale = 0f; settle = Mathf.Max(1, s.settleFrames); state = 1; }
            return;
        }
        if (state == 1)
        {
            if (Time.timeScale != 0f) { if (!scaleWarned) { scaleWarned = true; Debug.LogWarning("[ShadowSaver] SH: timeScale was changed by another script during the freeze; the actual tick in the file name tells"); } Time.timeScale = 0f; }
            if (--settle > 0) return;
            string name = s.namePattern.Replace("{tag}", tag).Replace("{target}", s.ticks[shotIdx].ToString()).Replace("{actual}", tick.ToString());
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name)); Debug.Log("[ShadowSaver] SH shot " + name); hold = Mathf.Max(1, s.holdFrames); state = 2; return;
        }
        if (--hold <= 0)
        {
            Time.timeScale = prevScale; state = 0; shotIdx++;
            if (shotIdx >= s.ticks.Length && (s.quitWhenDone || Array.IndexOf(Environment.GetCommandLineArgs(), "-ssquit") >= 0)) { Debug.Log("[ShadowSaver] SH done, quitting"); Application.Quit(); }
        }
    }

    void Restore()
    {
        RestoreP2(); if (vol != null) { Destroy(vol.gameObject); vol = null; }
        if (state != 0) { Time.timeScale = prevScale; state = 0; }
    }
    void OnDisable() { Restore(); }
    void OnApplicationQuit() { Restore(); }
}
