using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// PROPVIS HARNESS (lanePROPVIS, vNN): -propvis:<outdir> [-propvisdata:<json>] [-propvissize:<w>x<h>] [-propviscull[:A,B]]   OFF unless -propvis: is given. Harness only, never in a normal run.
// Why: Dom doubts the 688 placed props (4 fallen pillars remembered, 29 placed; 76 of each boulder) and guesses 60-70 % are never seen. This measures it from real frames.
// What: waits for the real scene, then for each camera of Assets/Data/propvis_cameras.json (lock-cam poses, free cam at player reach, every finisher shot, doorway walk, gate view; all numbers in the json):
//   phase 1: one normal screenshot (the real main camera, real materials, to an offscreen RT of shot.width x shot.height) -> <out>\shots\<cam>.png
//   phase 2: every Renderer gets a PropVis/Id material (one per prop with its number, one shared for everything else = occluder with id 0; skinned meshes, particles, trails off), then a private
//            HDRP camera (no post, no shadows, no fog, no volumetrics, no AA, cleared to 0) renders the ID pass at id.width x id.height into a float RT, and (shadow.enabled) a second pass with log-quantised eye depth.
//            Pixels per prop are counted (scaled to the reference output refWidth x refHeight = 2560x1600). The materials are restored afterwards. Culling and LOD are done by the same HDRP path as the game camera
//            (lodBias, shadow range), so occlusion by walls / floor / other props is real depth-tested occlusion.
//   shadow (MEASURED depth, CALCULATED bounds): from the depth pass a grid of visible surface points is un-projected to world space, a ray goes toward the sun, and every prop whose world bounds (AABB, conservative = upper bound)
//            the ray crosses within shadow.maxCasterM gets one sample; points beyond the shadow range (HDShadowSettings.maxShadowDistance) are skipped. Samples are scaled to reference pixels.
// Output: <out>\propvis_props.csv (per prop: frames seen, peak/mean ref pixels, peak camera, per-kind frames, shadow frames/peak), propvis_cameras.csv, propvis_summary.json, propvis_log.txt, id_<cam>.png (every idPngEvery),
//   shots\*.png. The tiers / map are made by propvis_analyze.py from the CSV. Exit codes: 0 done, 2 setup / exception, 3 timeout, 4 ID encoding check failed.
// -propviscull[:A,B] hides the props of prop_cull.json tiers (default A) in the scene BEFORE any frame is taken (before/after frames for Dom): run twice with different <outdir>.
// Nothing outside <out> is written. Cameras = Assets/Data/propvis_cameras.json, config = Assets/Data/propvis.json (StreamingAssets copies). Props are found by name from props_layout.json (PropsLayout.PlaceOne names the wrapper p.id).
[DefaultExecutionOrder(-57)]
public class PropVis : MonoBehaviour
{
    [Serializable] public class IdCfg { public int width = 1280, height = 800, refWidth = 2560, refHeight = 1600; public float nearM = 0.1f, farM = 1500f; public string shader = "PropVis/Id"; public bool hideSkinned = true; public int idPngEvery = 12; public float residualMax = 0.12f; }
    [Serializable] public class ShotCfg { public bool enabled = true; public int width = 960, height = 600; }
    [Serializable] public class ShadowCfg { public bool enabled = true; public int gridX = 80, gridY = 50; public float maxDistanceM = 0f, maxCasterM = 250f, boundsPadM = 0.05f; public int every = 1, minSamples = 1; }
    [Serializable] public class Cfg
    {
        public bool enabled = true, muteAudio = true;
        public float bootWaitSec = 40f, settleSec = 2.0f, timeoutSec = 840f, budgetSec = 780f, exitGraceSec = 3f;
        public string camerasFile = "propvis_cameras.json", layoutFile = "props_layout.json", cullFile = "prop_cull.json";
        public IdCfg id = new IdCfg(); public ShotCfg shot = new ShotCfg(); public ShadowCfg shadow = new ShadowCfg();
    }
    [Serializable] public class Cam { public string id = "cam", kind = "lock", note; public float[] pos, look; public float fov = 38.6f; }
    [Serializable] public class CamFile { public Cam[] cameras; }
    [Serializable] public class LProp { public string id, asset, group; public bool disabled; public float[] pos; }
    [Serializable] public class LFile { public LProp[] props; }
    [Serializable] public class CullFile { public bool enabled; public string[] activeTiers; public CullTier[] tiers; }
    [Serializable] public class CullTier { public string id; public string[] ids; }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { foreach (var a in Environment.GetCommandLineArgs()) if (a == n || a.StartsWith(n + ":", StringComparison.Ordinal)) return true; return false; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F2", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Arg("-propvis:") == null) return;   // OFF unless -propvis:<dir>
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<PropVis>() != null) return;
            var go = new GameObject("PropVis"); DontDestroyOnLoad(go); go.AddComponent<PropVis>();
        }
        catch (Exception e) { Debug.LogError("[propvis] boot failed: " + e); }
    }

    class Entry
    {
        public int num; public LProp lp; public GameObject go; public Renderer[] rs; public Bounds b; public Material mat;
        public int seen, shSeen; public double sum; public float peak, shPeak; public string peakCam = "", shPeakCam = "";
        public Dictionary<string, int> kindSeen = new Dictionary<string, int>(), kindSh = new Dictionary<string, int>(); public Dictionary<string, float> kindPeak = new Dictionary<string, float>();
    }

    Cfg C = new Cfg(); string dir, shotDir, logPath, dataSrc = "(defaults)";
    float t0, exitAt; bool exiting; int exitCode;
    Camera mainCam; CameraRig rig; Cam[] cams = new Cam[0];
    readonly List<Entry> E = new List<Entry>(); readonly List<string> camRows = new List<string>();
    int idW, idH; float pxScale; string cullNote = "";

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[propvis] " + s);
    }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-propvis:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "propvis"); } catch { dir = "propvis"; }
        shotDir = Path.Combine(dir, "shots");
        try { Directory.CreateDirectory(dir); Directory.CreateDirectory(shotDir); } catch { }
        logPath = Path.Combine(dir, "propvis_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        LoadCfg();
        if (C.muteAudio) AudioListener.volume = 0f;
    }
    void Start() { StartCoroutine(Guarded(Main())); }

    static string ReadData(string name, string cliOverride, out string src)
    {
        var paths = new List<string>(); if (!string.IsNullOrEmpty(cliOverride)) paths.Add(cliOverride);
        paths.Add(Path.Combine(Application.streamingAssetsPath, name)); paths.Add(Path.Combine(Application.dataPath, "Data/" + name));
        foreach (var p in paths) { try { if (File.Exists(p)) { src = p; return DataPath.ReadFile(p).TrimStart('﻿'); } } catch { } }
        src = null; return null;
    }

    void LoadCfg()
    {
        try
        {
            string src; string t = ReadData("propvis.json", Arg("-propvisdata:"), out src);
            if (t != null) { var c = JsonUtility.FromJson<Cfg>(t); if (c != null) { C = c; dataSrc = src; } }
            if (C.id == null) C.id = new IdCfg(); if (C.shot == null) C.shot = new ShotCfg(); if (C.shadow == null) C.shadow = new ShadowCfg();
            string sz = Arg("-propvissize:");
            if (!string.IsNullOrEmpty(sz)) { var p = sz.Split('x'); int w, h; if (p.Length == 2 && int.TryParse(p[0], out w) && int.TryParse(p[1], out h) && w >= 64 && h >= 40) { C.id.width = w; C.id.height = h; } }
        }
        catch (Exception e) { Debug.LogWarning("[propvis] cannot read propvis.json: " + e.Message); }
    }

    IEnumerator Guarded(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Log("EXCEPTION: " + ex); Finish(2, "exception"); yield break; }
            yield return cur;
        }
    }

    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > C.exitGraceSec) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT after " + F(C.timeoutSec) + " s"); WriteOutputs(); Finish(3, "timeout"); }
    }

    void Finish(int code, string why) { if (exiting) return; exiting = true; exitCode = code; exitAt = Time.unscaledTime; Log("EXIT " + code + " (" + why + ")"); }
    IEnumerator Wait(float s) { float u = Time.unscaledTime + s; while (Time.unscaledTime < u && !exiting) yield return null; }

    void Pose(Camera c, Cam k)
    {
        Vector3 p = new Vector3(k.pos[0], k.pos[1], k.pos[2]), l = new Vector3(k.look[0], k.look[1], k.look[2]);
        Vector3 f = l - p; if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
        c.transform.SetPositionAndRotation(p, Quaternion.LookRotation(f, Vector3.up)); c.fieldOfView = k.fov > 1f ? k.fov : 38.6f;
    }

    // ---------------- scene setup ----------------
    void FindProps()
    {
        string src; string t = ReadData(C.layoutFile, null, out src); if (t == null) throw new Exception("no " + C.layoutFile);
        var L = JsonUtility.FromJson<LFile>(t); if (L == null || L.props == null) throw new Exception("bad " + C.layoutFile);
        var want = new Dictionary<string, LProp>(); foreach (var p in L.props) if (p != null && !string.IsNullOrEmpty(p.id) && !p.disabled && !want.ContainsKey(p.id)) want[p.id] = p;
        var seen = new HashSet<string>(); int num = 0;
        foreach (var g in FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            LProp lp; if (!want.TryGetValue(g.gameObject.name, out lp) || !seen.Add(lp.id)) continue;
            var rs = g.GetComponentsInChildren<Renderer>(true); if (rs.Length == 0) continue;
            var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
            E.Add(new Entry { num = 0, lp = lp, go = g.gameObject, rs = rs, b = b });
        }
        E.Sort((a, c) => string.CompareOrdinal(a.lp.id, c.lp.id));
        foreach (var e in E) e.num = ++num;   // ids 1..n (0 = not a prop, max 999)
        Log("props: " + E.Count + " found in the scene of " + want.Count + " enabled layout rows (" + (want.Count - E.Count) + " not found); data " + dataSrc);
        if (E.Count > 998) throw new Exception("more than 998 props: the 3-digit id encoding is too small");
    }

    void ApplyCull()
    {
        string a = Arg("-propviscull:"); bool on = Flag("-propviscull"); if (!on) { cullNote = "none"; return; }
        string src; string t = ReadData(C.cullFile, null, out src); if (t == null) { Log("-propviscull given but no " + C.cullFile); cullNote = "cull file missing"; return; }
        var cf = JsonUtility.FromJson<CullFile>(t); var tiers = string.IsNullOrEmpty(a) ? (cf != null && cf.activeTiers != null && cf.activeTiers.Length > 0 ? cf.activeTiers : new[] { "A" }) : a.Split(',');
        var ids = new HashSet<string>(); if (cf != null && cf.tiers != null) foreach (var ti in cf.tiers) if (ti != null && ti.ids != null && Array.IndexOf(tiers, ti.id) >= 0) foreach (var i in ti.ids) ids.Add(i);
        int hid = 0; foreach (var e in E) if (ids.Contains(e.lp.id)) { e.go.SetActive(false); hid++; }
        cullNote = "tiers " + string.Join("+", tiers) + " hid " + hid + " of " + ids.Count + " listed props";
        Log("CULL ARM: " + cullNote);
    }

    // ---------------- main ----------------
    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((rig = FindFirstObjectByType<CameraRig>()) == null && Camera.main == null)
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSec) { Log("no CameraRig / main camera"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        mainCam = rig != null ? rig.GetComponent<Camera>() : null; if (mainCam == null) mainCam = Camera.main;
        if (!C.enabled) { Log("propvis.json enabled=false"); Finish(0, "disabled"); yield break; }
        if (rig != null) rig.enabled = false;   // the harness owns the camera
        yield return Wait(C.settleSec);

        string src; string ct = ReadData(C.camerasFile, null, out src);
        if (ct == null) { Log("no " + C.camerasFile); Finish(2, "setup"); yield break; }
        var cf = JsonUtility.FromJson<CamFile>(ct); if (cf == null || cf.cameras == null || cf.cameras.Length == 0) { Log("no cameras in " + src); Finish(2, "setup"); yield break; }
        var good = new List<Cam>(); foreach (var k in cf.cameras) if (k != null && k.pos != null && k.pos.Length >= 3 && k.look != null && k.look.Length >= 3) good.Add(k);
        cams = good.ToArray(); Log("cameras: " + cams.Length + " from " + src);

        FindProps(); ApplyCull();
        idW = C.id.width; idH = C.id.height; pxScale = (float)C.id.refWidth * C.id.refHeight / ((float)idW * idH);
        Log("id pass " + idW + "x" + idH + " reference " + C.id.refWidth + "x" + C.id.refHeight + " (1 id pixel = " + F(pxScale) + " ref pixels); shadow " + (C.shadow.enabled ? "on" : "off"));

        // phase 1: normal screenshots
        if (C.shot.enabled)
        {
            var srt = new RenderTexture(C.shot.width, C.shot.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var stex = new Texture2D(C.shot.width, C.shot.height, TextureFormat.RGB24, false);
            for (int i = 0; i < cams.Length && !exiting; i++)
            {
                if (Time.realtimeSinceStartup - t0 > C.budgetSec * 0.45f) { Log("budget: screenshots stopped at camera " + i + " of " + cams.Length); break; }
                Pose(mainCam, cams[i]);
                mainCam.targetTexture = srt; mainCam.Render(); mainCam.targetTexture = null;
                var prev = RenderTexture.active; RenderTexture.active = srt; stex.ReadPixels(new Rect(0, 0, C.shot.width, C.shot.height), 0, 0, false); stex.Apply(false); RenderTexture.active = prev;
                try { File.WriteAllBytes(Path.Combine(shotDir, cams[i].id + ".png"), stex.EncodeToPNG()); } catch (Exception ex) { Log("shot write: " + ex.Message); }
                if (i % 8 == 7) yield return null;
            }
            Destroy(srt); Destroy(stex);
            Log("phase 1 done: screenshots in " + shotDir);
        }

        // phase 2: ID (+ depth) passes
        var idShader = Shader.Find(C.id.shader);
        if (idShader == null) { Log("FAIL shader '" + C.id.shader + "' not found: add it to Always Included Shaders (AlwaysIncludeShaders.Names)"); Finish(2, "shader"); yield break; }
        var occMat = new Material(idShader) { name = "PropVisOcc", hideFlags = HideFlags.HideAndDontSave }; occMat.SetFloat("_IdVal", 0f);
        foreach (var e in E) { e.mat = new Material(idShader) { name = "PropVisId_" + e.num, hideFlags = HideFlags.HideAndDontSave }; e.mat.SetFloat("_IdVal", e.num); }
        Shader.SetGlobalFloat("_PropVisNear", C.id.nearM); float logRange = Mathf.Log(C.id.farM / C.id.nearM, 2f); Shader.SetGlobalFloat("_PropVisLogRange", logRange);

        var camGo = new GameObject("PropVisIdCam"); camGo.hideFlags = HideFlags.HideAndDontSave;
        var cam = camGo.AddComponent<Camera>(); cam.enabled = false; cam.nearClipPlane = C.id.nearM; cam.farClipPlane = C.id.farM; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black; cam.allowMSAA = false; cam.allowHDR = false; cam.cullingMask = ~0;
        var hd = camGo.AddComponent<HDAdditionalCameraData>();
        hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; hd.backgroundColorHDR = Color.black; hd.volumeLayerMask = 0; hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None; hd.customRenderingSettings = true;
        var off = new[] { FrameSettingsField.Postprocess, FrameSettingsField.ShadowMaps, FrameSettingsField.ContactShadows, FrameSettingsField.ScreenSpaceShadows, FrameSettingsField.SSR, FrameSettingsField.SSAO, FrameSettingsField.SSGI, FrameSettingsField.SubsurfaceScattering,
            FrameSettingsField.Volumetrics, FrameSettingsField.AtmosphericScattering, FrameSettingsField.Decals, FrameSettingsField.MotionVectors, FrameSettingsField.Distortion, FrameSettingsField.TransparentObjects, FrameSettingsField.ReflectionProbe,
            FrameSettingsField.PlanarProbe, FrameSettingsField.SkyReflection, FrameSettingsField.Water, FrameSettingsField.LowResTransparent, FrameSettingsField.Refraction, FrameSettingsField.CustomPass, FrameSettingsField.AfterPostprocess };
        foreach (var f in off) { hd.renderingPathCustomFrameSettings.SetEnabled(f, false); hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)f] = true; }
        var rt = new RenderTexture(idW, idH, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); rt.name = "PropVisRT"; rt.Create(); cam.targetTexture = rt;
        var tex = new Texture2D(idW, idH, TextureFormat.RGBAFloat, false);

        // material swap (restored in finally-like code below)
        var swapped = new List<Renderer>(); var saved = new List<Material[]>(); var disabled = new List<Renderer>();
        var propOf = new Dictionary<Renderer, Entry>(); foreach (var e in E) foreach (var r in e.rs) if (r != null) propOf[r] = e;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (r == null || !r.enabled) continue;
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is BillboardRenderer || r.GetType().Name.IndexOf("VFX", StringComparison.OrdinalIgnoreCase) >= 0) { r.enabled = false; disabled.Add(r); continue; }
            if (C.id.hideSkinned && r is SkinnedMeshRenderer) { r.enabled = false; disabled.Add(r); continue; }
            if (r.gameObject.name == "PropVisIdCam") continue;
            var old = r.sharedMaterials; Entry pe; var m = propOf.TryGetValue(r, out pe) ? pe.mat : occMat;
            var nm = new Material[old.Length]; for (int i = 0; i < nm.Length; i++) nm[i] = m;
            swapped.Add(r); saved.Add(old); r.sharedMaterials = nm;
        }
        Log("id pass: " + swapped.Count + " renderers swapped, " + disabled.Count + " hidden (skinned / particles / trails)");

        // sun and shadow range
        Light sun = null; foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l != null && l.enabled && l.type == LightType.Directional && (sun == null || l.intensity > sun.intensity)) sun = l;
        float shadowDist = C.shadow.maxDistanceM;
        if (shadowDist <= 0f) { try { var hs = VolumeManager.instance.stack.GetComponent<HDShadowSettings>(); if (hs != null) shadowDist = hs.maxShadowDistance.value; } catch { } }
        if (shadowDist <= 0f) shadowDist = 150f;
        bool doShadow = C.shadow.enabled && sun != null; Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;
        Log("sun: " + (sun != null ? sun.name : "NONE") + " toSun " + toSun.ToString("F3") + " shadowDist " + F(shadowDist));

        int n = E.Count; var counts = new int[1000]; var shs = new int[n + 1];
        long idPixels = 0, goodPixels = 0, idCount = 0; double resSum = 0; int done = 0; float unprojOk = 0, unprojN = 0;
        var kindCams = new Dictionary<string, int>();
        try
        {
            for (int ci = 0; ci < cams.Length && !exiting; ci++)
            {
                var k = cams[ci];
                if (Time.realtimeSinceStartup - t0 > C.budgetSec) { Log("budget: id pass stopped at camera " + ci + " of " + cams.Length); break; }
                Pose(cam, k);
                Shader.SetGlobalFloat("_PropVisMode", 0f); cam.Render();
                float[] px = ReadRt(rt, tex);
                Array.Clear(counts, 0, counts.Length); int nonzero = 0; double res = 0; long resN = 0;
                var idImg = new int[idW * idH];
                for (int p = 0; p < idW * idH; p++)
                {
                    int v; double r; if (!Decode(px, p, out v, out r)) { v = 0; r = 0; }
                    idImg[p] = v; if (v > 0) nonzero++; res += r; resN++;
                    if (v > 0 && v < counts.Length) counts[v]++;
                }
                idPixels += idW * idH; goodPixels += nonzero; resSum += res; idCount += resN;
                int propsSeen = 0;
                for (int j = 0; j < n; j++)
                {
                    int c = counts[E[j].num]; if (c <= 0) continue;
                    var e = E[j]; float rp = c * pxScale; propsSeen++; e.seen++; e.sum += rp; int kc; e.kindSeen.TryGetValue(k.kind, out kc); e.kindSeen[k.kind] = kc + 1; float kp; e.kindPeak.TryGetValue(k.kind, out kp); if (rp > kp) e.kindPeak[k.kind] = rp;
                    if (rp > e.peak) { e.peak = rp; e.peakCam = k.id; }
                }
                int shHit = 0;
                if (doShadow && ci % Mathf.Max(1, C.shadow.every) == 0)
                {
                    Shader.SetGlobalFloat("_PropVisMode", 1f); cam.Render();
                    float[] dp = ReadRt(rt, tex);
                    shHit = ShadowSamples(cam, k, idImg, dp, toSun, shadowDist, shs, ref unprojOk, ref unprojN, logRange);
                }
                camRows.Add(k.id + "," + k.kind + "," + F(k.pos[0]) + "," + F(k.pos[1]) + "," + F(k.pos[2]) + "," + nonzero + "," + F(100f * nonzero / (idW * idH)) + "," + propsSeen + "," + shHit);
                int kcn; kindCams.TryGetValue(k.kind, out kcn); kindCams[k.kind] = kcn + 1;
                if (C.id.idPngEvery > 0 && ci % C.id.idPngEvery == 0) SaveIdPng(idImg, k.id);
                done++; if (ci % 4 == 3) yield return null;
            }
        }
        finally
        {
            for (int i = 0; i < swapped.Count; i++) if (swapped[i] != null) swapped[i].sharedMaterials = saved[i];
            foreach (var r in disabled) if (r != null) r.enabled = true;
            Shader.SetGlobalFloat("_PropVisMode", 0f);
            cam.targetTexture = null; rt.Release();
        }
        double meanRes = idCount > 0 ? resSum / idCount : 0;
        Log("id pass done: " + done + " cameras, " + F(100f * goodPixels / Mathf.Max(1, (int)idPixels)) + " % of pixels hold an id > 0, mean digit residual " + meanRes.ToString("F4", IC) + (unprojN > 0 ? ", unproject self-check " + F(100f * unprojOk / unprojN) + " % of prop pixels land inside their prop bounds" : ""));
        WriteOutputs(done, meanRes, unprojN > 0 ? unprojOk / unprojN : -1f, shadowDist, sun != null);
        if (meanRes > C.id.residualMax) { Log("FAIL digit residual " + meanRes.ToString("F3", IC) + " > " + C.id.residualMax + ": the ID colours were altered between the shader and the RT (check post / exposure / colour space); counts are NOT trustworthy"); Finish(4, "id encoding"); yield break; }
        if (goodPixels == 0) { Log("FAIL no pixel holds an id > 0: the ID shader did not draw (variant stripped?)"); Finish(4, "no ids"); yield break; }
        Finish(0, "done");
    }

    float[] ReadRt(RenderTexture rt, Texture2D tex)
    {
        var prev = RenderTexture.active; RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false); RenderTexture.active = prev;
        return tex.GetRawTextureData<float>().ToArray();
    }

    // three digit levels (digit/9) -> number 0..999; residual = mean distance of the 3 levels from the nearest integer
    static bool Decode(float[] px, int p, out int v, out double residual)
    {
        int o = p * 4; float a = px[o] * 9f, b = px[o + 1] * 9f, c = px[o + 2] * 9f;
        int d2 = Mathf.Clamp(Mathf.RoundToInt(a), 0, 9), d1 = Mathf.Clamp(Mathf.RoundToInt(b), 0, 9), d0 = Mathf.Clamp(Mathf.RoundToInt(c), 0, 9);
        residual = (Math.Abs(a - d2) + Math.Abs(b - d1) + Math.Abs(c - d0)) / 3.0; v = d2 * 100 + d1 * 10 + d0; return true;
    }

    int ShadowSamples(Camera cam, Cam k, int[] idImg, float[] dp, Vector3 toSun, float shadowDist, int[] shs, ref float unprojOk, ref float unprojN, float logRange)
    {
        int gx = Mathf.Max(4, C.shadow.gridX), gy = Mathf.Max(4, C.shadow.gridY); float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * idW / (float)idH;
        Vector3 pos = cam.transform.position, fw = cam.transform.forward, rt = cam.transform.right, up = cam.transform.up; int n = E.Count;
        var hit = new int[n]; float refPer = (float)C.id.refWidth * C.id.refHeight / (gx * gy); int total = 0; float pad = C.shadow.boundsPadM;
        var smin = new Vector3[n]; var smax = new Vector3[n]; for (int j = 0; j < n; j++) { if (!E[j].go.activeInHierarchy) { smin[j] = smax[j] = Vector3.one * 1e9f; continue; } smin[j] = E[j].b.min - Vector3.one * pad; smax[j] = E[j].b.max + Vector3.one * pad; }   // a culled prop (-propviscull) casts nothing
        for (int iy = 0; iy < gy; iy++)
            for (int ix = 0; ix < gx; ix++)
            {
                int px = Mathf.Min(idW - 1, (int)((ix + 0.5f) / gx * idW)), py = Mathf.Min(idH - 1, (int)((iy + 0.5f) / gy * idH)), p = py * idW + px;
                int dv; double r; Decode(dp, p, out dv, out r); if (dv <= 0) continue;                       // nothing drawn = sky
                float d = C.id.nearM * Mathf.Pow(2f, (dv - 1) / 998f * logRange);
                float u = ((px + 0.5f) / idW) * 2f - 1f, v = ((py + 0.5f) / idH) * 2f - 1f;
                Vector3 P = pos + (fw + rt * (u * tanH) + up * (v * tanV)) * d;
                int pid = idImg[p];
                if (pid > 0 && pid <= n) { unprojN++; var eb = E[pid - 1]; if (P.x >= smin[pid - 1].x - 0.6f * (1f + d * 0.02f) && P.x <= smax[pid - 1].x + 0.6f * (1f + d * 0.02f) && P.y >= smin[pid - 1].y - 0.6f * (1f + d * 0.02f) && P.y <= smax[pid - 1].y + 0.6f * (1f + d * 0.02f) && P.z >= smin[pid - 1].z - 0.6f * (1f + d * 0.02f) && P.z <= smax[pid - 1].z + 0.6f * (1f + d * 0.02f)) unprojOk++; }
                if (d > shadowDist) continue;                                                                   // beyond the sun shadow range: no shadow is drawn there
                total++;
                for (int j = 0; j < n; j++)
                {
                    if (j + 1 == pid) continue;
                    if (RayBox(P + toSun * 0.02f, toSun, smin[j], smax[j], C.shadow.maxCasterM)) hit[j]++;
                }
            }
        for (int j = 0; j < n; j++)
        {
            if (hit[j] < C.shadow.minSamples) continue;
            var e = E[j]; e.shSeen++; int ks; e.kindSh.TryGetValue(k.kind, out ks); e.kindSh[k.kind] = ks + 1; float rp = hit[j] * refPer; shs[j] += hit[j];
            if (rp > e.shPeak) { e.shPeak = rp; e.shPeakCam = k.id; }
        }
        return total;
    }

    static bool RayBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, float maxT)
    {
        float t0 = 0f, t1 = maxT;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.x : a == 1 ? o.y : o.z, da = a == 0 ? d.x : a == 1 ? d.y : d.z, lo = a == 0 ? mn.x : a == 1 ? mn.y : mn.z, hi = a == 0 ? mx.x : a == 1 ? mx.y : mx.z;
            if (Mathf.Abs(da) < 1e-6f) { if (oa < lo || oa > hi) return false; }
            else { float ta = (lo - oa) / da, tb = (hi - oa) / da; if (ta > tb) { float s = ta; ta = tb; tb = s; } if (ta > t0) t0 = ta; if (tb < t1) t1 = tb; if (t0 > t1) return false; }
        }
        return true;
    }

    void SaveIdPng(int[] idImg, string camId)
    {
        try
        {
            var t = new Texture2D(idW, idH, TextureFormat.RGB24, false); var cols = new Color32[idW * idH];
            for (int i = 0; i < cols.Length; i++) { int v = idImg[i]; if (v <= 0) { cols[i] = new Color32(0, 0, 0, 255); continue; } uint h = (uint)v * 2654435761u; cols[i] = new Color32((byte)(64 + (h >> 8 & 0xBF)), (byte)(64 + (h >> 16 & 0xBF)), (byte)(64 + (h >> 24 & 0xBF)), 255); }
            t.SetPixels32(cols); t.Apply(false); File.WriteAllBytes(Path.Combine(dir, "id_" + camId + ".png"), t.EncodeToPNG()); Destroy(t);
        }
        catch (Exception ex) { Log("id png: " + ex.Message); }
    }

    void WriteOutputs(int doneCams = 0, double meanRes = 0, float unproj = -1f, float shadowDist = 0f, bool sun = false)
    {
        try
        {
            var sb = new StringBuilder("num,id,asset,group,x,y,z,frames_seen,peak_ref_px,mean_ref_px,peak_cam,kind_frames,shadow_frames,shadow_peak_ref_px,shadow_peak_cam,kind_peak_px,kind_shadow_frames\n");
            foreach (var e in E)
            {
                var kinds = new StringBuilder(); foreach (var kv in e.kindSeen) { if (kinds.Length > 0) kinds.Append(';'); kinds.Append(kv.Key).Append('=').Append(kv.Value); }
                var kpk = new StringBuilder(); foreach (var kv in e.kindPeak) { if (kpk.Length > 0) kpk.Append(';'); kpk.Append(kv.Key).Append('=').Append(F(kv.Value)); }
                var ksh = new StringBuilder(); foreach (var kv in e.kindSh) { if (ksh.Length > 0) ksh.Append(';'); ksh.Append(kv.Key).Append('=').Append(kv.Value); }
                var lp = e.lp; var pz = lp.pos != null && lp.pos.Length >= 3 ? lp.pos : new float[3];
                sb.Append(e.num).Append(',').Append(lp.id).Append(',').Append(lp.asset).Append(',').Append(lp.group).Append(',').Append(F(e.go.transform.position.x)).Append(',').Append(F(e.go.transform.position.y)).Append(',').Append(F(e.go.transform.position.z)).Append(',')
                  .Append(e.seen).Append(',').Append(F(e.peak)).Append(',').Append(e.seen > 0 ? F((float)(e.sum / e.seen)) : "0.00").Append(',').Append(e.peakCam).Append(',').Append(kinds).Append(',').Append(e.shSeen).Append(',').Append(F(e.shPeak)).Append(',').Append(e.shPeakCam).Append(',').Append(kpk).Append(',').Append(ksh).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "propvis_props.csv"), sb.ToString());
            File.WriteAllText(Path.Combine(dir, "propvis_cameras.csv"), "id,kind,x,y,z,id_pixels,id_pct,props_seen,shadow_samples\n" + string.Join("\n", camRows) + "\n");
            var sj = "{\"cameras_total\":" + cams.Length + ",\"cameras_done\":" + doneCams + ",\"props\":" + E.Count + ",\"idWidth\":" + idW + ",\"idHeight\":" + idH + ",\"refWidth\":" + C.id.refWidth + ",\"refHeight\":" + C.id.refHeight + ",\"meanDigitResidual\":" + meanRes.ToString("F5", IC) +
                     ",\"unprojectSelfCheck\":" + unproj.ToString("F4", IC) + ",\"shadowRangeM\":" + shadowDist.ToString("F1", IC) + ",\"sunFound\":" + (sun ? "true" : "false") + ",\"cull\":\"" + cullNote + "\",\"label\":\"MEASURED: id pass counts; shadow = measured depth + CALCULATED AABB (upper bound)\",\"seconds\":" + (Time.realtimeSinceStartup - t0).ToString("F1", IC) + "}";
            File.WriteAllText(Path.Combine(dir, "propvis_summary.json"), sj);
        }
        catch (Exception ex) { Log("write outputs: " + ex.Message); }
    }
}
