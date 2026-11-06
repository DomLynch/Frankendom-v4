using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// PERFAB v102 (lanePERFAB), NOT COMPILED IN UNITY (Roslyn checked). -perfab [-out:<dir>] [-perfabdata:<file>]   (run at 1920x1080 with the default runtime_flags.json:
//   -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 ; build/run with vsync off is forced below)
// One run measures every lanePERF5 switch. Pattern: ShadowDiag (-shadowdiag) / WoundShot (-woundshot).
//  1. Fighters at a pose (perfab.json poses[]: anchor, axis, sepM; the v66 wide pose by default) in front of the REAL lock-on CameraRig, brains off. The flicker lights keep running while frames are timed.
//  2. For every arm in perfab.json arms[] (baseline, each switch alone, bundles, baseline again at the end for the drift/noise floor):
//       restore the boot state (light shadow modes, renderer shadow modes, camera frame settings) -> apply the arm -> warmupFrames (not timed) -> measureFrames (timed, wall ms per frame)
//       -> optional flicker freeze -> ONE real-settings screenshot -> one row in perfab_results.csv (median, p95, mean, max, fps, gpu ms when frame timing is on).
//  3. The arm's switches come from perf5_switches.json (the same "cli" strings apply_perf5.py --arms prints): -qset:k=v,k=v | -perffix:ID,ID | -permem:ID. needsBuild switches (occlusion, sun cache asset)
//     cannot be toggled in a running player: they are listed as skipped rows (and dropped from bundles, noted in the row).
//  4. perfab_summary.txt + exit code (0 done, 2 exception / setup, 3 timeout).  python perfab_report.py <out> turns csv + screenshots into the fps-gain / luma / contrast table.
// ALL numbers are data (Assets/Data/perfab.json, StreamingAssets copy). The defaults below only mirror that file. Nothing on disk besides <out> is written.
[DefaultExecutionOrder(-61)]
public class PerfAb : MonoBehaviour
{
    // ---------------- data ----------------
    [Serializable] public class Pose { public string id = "wide"; public bool enabled = true; public float[] anchor = { 0f, 0f, 17f }; public float[] axis = { 0f, 0f, 1f }; public float sepM = 2.5f; }
    [Serializable] public class ArmRow { public string id; public string[] switches; public string bundle; public string each; public bool enabled = true; public string note;
        public string qset; public string[] shadowOn, shadowOff; }   // PERF6: explicit -qset k=v list + per-arm shadow-caster switch by hierarchy-path substring
    [Serializable] public class Data
    {
        public string switchesFile = "perf5_switches.json";
        public int bootFrames = 240, applySettleFrames = 30, warmupFrames = 120, measureFrames = 300, shotSettleFrames = 3;
        public float timeoutS = 1800f;
        public bool freezeFlickersForShot = true, skipNeedsBuild = true, vsyncOff = true;
        public Pose[] poses = new Pose[0];
        public ArmRow[] arms = new ArmRow[0];
        public float healthFill = 1000f, lockRange = 80f;
        public string csvName = "perfab_results.csv";
    }
    [Serializable] class SwEntry { public string id; public bool needsBuild; public bool notRecommended; public string cli; }
    [Serializable] class BundleRow { public string id; public string[] switches; }
    [Serializable] class SwFile { public SwEntry[] switches; public BundleRow[] bundles; }

    class Arm
    {
        public string id, note = ""; public List<string> sw = new List<string>(); public List<string> dropped = new List<string>();
        public Dictionary<string, string> qset = new Dictionary<string, string>(); public HashSet<string> perffix = new HashSet<string>(StringComparer.OrdinalIgnoreCase), permem = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string skipped; public string[] casterOn, casterOff;   // PERF6
    }
    class LightSnap { public Light l; public HDAdditionalLightData hd; public ShadowUpdateMode um; public bool add; public LightShadows s; }
    class RendSnap { public Renderer r; public ShadowCastingMode m; public bool stat; }
    class CamSnap { public HDAdditionalCameraData hd; public bool custom; public FrameSettings fs; public FrameSettingsOverrideMask mask; }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static string F2(float v) { return v.ToString("F2", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-perfab")) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<PerfAb>() != null) return;
        var go = new GameObject("PerfAb"); DontDestroyOnLoad(go); go.AddComponent<PerfAb>();
    }

    string dir, logPath, csvPath; float t0; bool exiting; int exitCode; float exitAt; Data cfg = new Data(); string dataSrc = "(defaults)";
    DuelFighter D, P; CameraRig rig;
    readonly List<string> summary = new List<string>(); readonly List<LightSnap> lightSnaps = new List<LightSnap>(); readonly List<RendSnap> rendSnaps = new List<RendSnap>(); readonly List<CamSnap> camSnaps = new List<CamSnap>();
    readonly List<MonoBehaviour> flickers = new List<MonoBehaviour>();
    SwFile sw = new SwFile(); readonly FrameTiming[] ft = new FrameTiming[1];

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[perfab] " + s);
    }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "perfab"); } catch { dir = "perfab"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "perfab_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        string txt = null, over = Arg("-perfabdata:");
        if (!string.IsNullOrEmpty(over)) { try { txt = File.ReadAllText(over).TrimStart((char)0xFEFF); dataSrc = over; } catch (Exception e) { Debug.LogWarning("[perfab] -perfabdata unreadable: " + e.Message); } }
        if (txt == null) { txt = HudCfg.ReadData("perfab.json"); if (txt != null) dataSrc = "perfab.json"; }
        if (txt != null) { try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) cfg = d; } catch (Exception e) { Debug.LogWarning("[perfab] perfab.json parse failed, defaults: " + e.Message); } }
        if (cfg.poses == null) cfg.poses = new Pose[0]; if (cfg.arms == null) cfg.arms = new ArmRow[0];
        csvPath = Path.Combine(dir, string.IsNullOrEmpty(cfg.csvName) ? "perfab_results.csv" : cfg.csvName);
    }
    void Start() { StartCoroutine(Guarded(Main())); }
    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > cfg.timeoutS) { Log("TIMEOUT after " + cfg.timeoutS.ToString("F0", IC) + " s"); Finish(3, "timeout"); }
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
    IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    void Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; if (facing.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }
    static Vector3 V3(float[] a, Vector3 d) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : d; }

    // ---------------- arms ----------------
    SwEntry FindSw(string id) { if (sw.switches != null) foreach (var s in sw.switches) if (s != null && s.id == id) return s; return null; }
    BundleRow FindBundle(string id) { if (sw.bundles != null) foreach (var b in sw.bundles) if (b != null && b.id == id) return b; return null; }

    // Merges one switch's cli into the arm. Tokens: -qset:k=v,k=v  -perffix:A,B  -permem:A  (anything else, e.g. "(none: build arm)", is ignored)
    void AddSwitch(Arm a, string id)
    {
        var s = FindSw(id);
        if (s == null) { a.skipped = "unknown switch " + id; return; }
        if (s.needsBuild && cfg.skipNeedsBuild) { a.dropped.Add(id); return; }
        if (a.sw.Contains(id)) return;
        a.sw.Add(id);
        if (string.IsNullOrEmpty(s.cli)) return;
        foreach (var raw in s.cli.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("-qset:", StringComparison.Ordinal)) foreach (var kv in raw.Substring(6).Split(',')) { var p = kv.Split('='); if (p.Length == 2) a.qset[p[0]] = p[1]; }
            else if (raw.StartsWith("-perffix:", StringComparison.Ordinal)) foreach (var x in raw.Substring(9).Split(',')) { if (x.Length > 0) a.perffix.Add(x); }
            else if (raw.StartsWith("-permem:", StringComparison.Ordinal)) foreach (var x in raw.Substring(8).Split(',')) { if (x.Length > 0) a.permem.Add(x); }
        }
    }
    List<Arm> BuildArms()
    {
        var list = new List<Arm>();
        foreach (var row in cfg.arms)
        {
            if (row == null || !row.enabled) continue;
            if (!string.IsNullOrEmpty(row.each))
            {
                if (row.each == "singles" && sw.switches != null)
                    foreach (var s in sw.switches) { if (s == null) continue; var a = new Arm { id = s.id, note = s.notRecommended ? "notRecommended" : "" }; AddSwitch(a, s.id); if (a.dropped.Count > 0) a.skipped = "needs a build (" + s.id + ")"; list.Add(a); }
                continue;
            }
            var arm = new Arm { id = string.IsNullOrEmpty(row.id) ? (string.IsNullOrEmpty(row.bundle) ? "arm" + list.Count : row.bundle) : row.id, note = row.note ?? "" };
            if (!string.IsNullOrEmpty(row.bundle))
            {
                var b = FindBundle(row.bundle);
                if (b == null) { arm.skipped = "unknown bundle " + row.bundle; list.Add(arm); continue; }
                foreach (var id in b.switches) AddSwitch(arm, id);
            }
            if (row.switches != null) foreach (var id in row.switches) AddSwitch(arm, id);
            if (!string.IsNullOrEmpty(row.qset)) foreach (var kv in row.qset.Split(',')) { var q = kv.Split('='); if (q.Length == 2) arm.qset[q[0].Trim()] = q[1].Trim(); }   // PERF6
            arm.casterOn = row.shadowOn; arm.casterOff = row.shadowOff;   // PERF6
            if (arm.dropped.Count > 0) arm.note = (arm.note + " partial: dropped build-only " + string.Join("+", arm.dropped.ToArray())).Trim();
            list.Add(arm);
        }
        return list;
    }

    // -qset values: JsonUtility reads a bool from true/false, not 0/1, so the value is rewritten from the type of the Flags field.
    string NormalisedQset(Arm a, out float lodMul, out int async)
    {
        lodMul = 1f; async = -1; var sb = new StringBuilder();
        foreach (var kv in a.qset)
        {
            FieldInfo fi = typeof(RuntimeQuality.Flags).GetField(kv.Key);
            if (fi == null) { Log("WARN arm " + a.id + ": unknown flag '" + kv.Key + "' in -qset (ignored)"); continue; }
            string v = kv.Value;
            if (fi.FieldType == typeof(bool)) v = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) ? "true" : "false";
            if (sb.Length > 0) sb.Append(',');
            sb.Append(kv.Key).Append('=').Append(v);
            float f;
            if (kv.Key == "lodBiasMul" && float.TryParse(kv.Value, NumberStyles.Float, IC, out f)) lodMul = f;
            if (kv.Key == "asyncCompute") { int n; if (int.TryParse(kv.Value, NumberStyles.Integer, IC, out n)) async = n; }
        }
        return sb.ToString();
    }

    // ---------------- boot-state snapshots (so arm N+1 never inherits arm N) ----------------
    void TakeSnapshots()
    {
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var hd = l.GetComponent<HDAdditionalLightData>(); var ls = new LightSnap { l = l, hd = hd, s = l.shadows };
            if (hd != null) { ls.um = hd.shadowUpdateMode; ls.add = hd.alwaysDrawDynamicShadows; }
            lightSnaps.Add(ls);
        }
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) rendSnaps.Add(new RendSnap { r = r, m = r.shadowCastingMode, stat = r.staticShadowCaster });
        foreach (var c in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var hd = c.GetComponent<HDAdditionalCameraData>(); if (hd == null) continue;
            camSnaps.Add(new CamSnap { hd = hd, custom = hd.customRenderingSettings, fs = hd.renderingPathCustomFrameSettings, mask = hd.renderingPathCustomFrameSettingsOverrideMask });
        }
        Log("snapshots: lights=" + lightSnaps.Count + " renderers=" + rendSnaps.Count + " cameras=" + camSnaps.Count);
    }
    void RestoreSnapshots()
    {
        foreach (var s in lightSnaps)
        {
            if (s.l == null) continue;
            if (s.hd != null) { if (s.hd.shadowUpdateMode != s.um) s.hd.shadowUpdateMode = s.um; if (s.hd.alwaysDrawDynamicShadows != s.add) s.hd.alwaysDrawDynamicShadows = s.add; }
            if (s.l.shadows != s.s) s.l.shadows = s.s;
        }
        foreach (var s in rendSnaps)
        {
            if (s.r == null) continue;
            if (s.r.shadowCastingMode != s.m) s.r.shadowCastingMode = s.m;
            if (s.r.staticShadowCaster != s.stat) s.r.staticShadowCaster = s.stat;
        }
    }
    void RestoreCameras()
    {
        foreach (var s in camSnaps)
        {
            if (s.hd == null) continue;
            s.hd.customRenderingSettings = s.custom; s.hd.renderingPathCustomFrameSettings = s.fs; s.hd.renderingPathCustomFrameSettingsOverrideMask = s.mask;
        }
    }

    // restore -> hooks -> RuntimeQuality.Reapply (fresh volume profiles from the authored ones + flags) -> lod bias -> camera -> PerfFixes / PerfMem runtime halves
    string ApplyArm(Arm a)
    {
        RestoreSnapshots();
        float lodMul; int async; string qs = NormalisedQset(a, out lodMul, out async);
        QualityTiers.AbQset = qs;   // "" = no -qset at all (the command line -qset is ignored: the arm defines everything)
        PerfFixes.AbOnly = a.perffix.Count > 0 ? new HashSet<string>(a.perffix, StringComparer.OrdinalIgnoreCase) : null;
        PerfMem.AbOnly = a.permem.Count > 0 ? new HashSet<string>(a.permem, StringComparer.OrdinalIgnoreCase) : null;
        RuntimeQuality.Reapply();
        QualityTiers.LodBiasMul = lodMul; QualityTiers.ApplyLod();
        RestoreCameras();
        if (async >= 0) QualityTiers.ApplyCamera(async);
        string pf = (a.perffix.Count > 0) ? PerfFixes.Apply() : "";
        string pm = "";
        if (a.permem.Count > 0 && PerfMem.On("A2")) pm = PerfMem.ApplyA2();
        return "qset[" + qs + "] lodBias=" + QualitySettings.lodBias.ToString("F2", IC) + " perffix[" + string.Join(",", new List<string>(a.perffix).ToArray()) + "] permem[" + string.Join(",", new List<string>(a.permem).ToArray()) + "] " + pf + " " + pm + " " + ApplyCasters(a);   // PERF6
    }

    // PERF6: per-arm shadow-caster switch. shadowOn / shadowOff = hierarchy-path substrings (ordinal, e.g. "WorldBehind/" matches every prop under the WorldBehind group).
    // The boot state is restored by RestoreSnapshots() at the start of the next arm. The match count goes into the csv note; 0 matches = "NOMATCH" (setup error, the row is still timed).
    static string PathOf(Transform t) { var sb = new StringBuilder(t.name); for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/"); return sb.ToString(); }
    string ApplyCasters(Arm a)
    {
        bool anyOn = a.casterOn != null && a.casterOn.Length > 0, anyOff = a.casterOff != null && a.casterOff.Length > 0;
        if (!anyOn && !anyOff) return "";
        int nOn = 0, nOff = 0;
        foreach (var s in rendSnaps)
        {
            if (s.r == null) continue;
            string path = PathOf(s.r.transform);
            if (anyOn) foreach (var tok in a.casterOn) if (!string.IsNullOrEmpty(tok) && path.IndexOf(tok, StringComparison.Ordinal) >= 0) { s.r.shadowCastingMode = ShadowCastingMode.On; nOn++; break; }
            if (anyOff) foreach (var tok in a.casterOff) if (!string.IsNullOrEmpty(tok) && path.IndexOf(tok, StringComparison.Ordinal) >= 0) { s.r.shadowCastingMode = ShadowCastingMode.Off; nOff++; break; }
        }
        string r = "casters on=" + nOn + " off=" + nOff + ((anyOn && nOn == 0) || (anyOff && nOff == 0) ? " NOMATCH" : "");
        a.note = ((a.note ?? "") + " " + r).Trim();
        return r;
    }

    // ---------------- measuring ----------------
    static float Pct(List<float> sorted, float p)
    {
        if (sorted.Count == 0) return 0f;
        float x = Mathf.Clamp01(p) * (sorted.Count - 1); int i = (int)x; int j = Mathf.Min(sorted.Count - 1, i + 1); float t = x - i;
        return sorted[i] * (1f - t) + sorted[j] * t;
    }
    class Stats { public int n; public float median, p95, mean, max, gpu; public string shot = ""; }

    IEnumerator Measure(Stats st)
    {
        var ms = new List<float>(cfg.measureFrames); var gpu = new List<float>(cfg.measureFrames);
        yield return null;
        double prev = Time.realtimeSinceStartupAsDouble;
        for (int i = 0; i < cfg.measureFrames; i++)
        {
            yield return null;
            double now = Time.realtimeSinceStartupAsDouble; ms.Add((float)((now - prev) * 1000.0)); prev = now;
            try { FrameTimingManager.CaptureFrameTimings(); if (FrameTimingManager.GetLatestTimings(1, ft) > 0 && ft[0].gpuFrameTime > 0.0) gpu.Add((float)ft[0].gpuFrameTime); } catch { }
        }
        double sum = 0; float mx = 0f; foreach (var v in ms) { sum += v; if (v > mx) mx = v; }
        ms.Sort(); gpu.Sort();
        st.n = ms.Count; st.median = Pct(ms, 0.5f); st.p95 = Pct(ms, 0.95f); st.mean = ms.Count > 0 ? (float)(sum / ms.Count) : 0f; st.max = mx; st.gpu = Pct(gpu, 0.5f);
    }
    IEnumerator Shot(string name, Stats st)
    {
        if (cfg.freezeFlickersForShot) foreach (var f in flickers) if (f != null) f.enabled = false;
        yield return Frames(Mathf.Max(1, cfg.shotSettleFrames));
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        CaptureUtil.HideConsole(); try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("SHOT failed " + name + ": " + e.Message); }
        if (tex != null)
        {
            try { File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG()); st.shot = name + ".png"; } catch (Exception e) { Log("SHOT write failed " + name + ": " + e.Message); }
            Destroy(tex);
        }
        if (cfg.freezeFlickersForShot) foreach (var f in flickers) if (f != null) f.enabled = true;
    }

    void Row(Arm a, Pose p, int order, string status, Stats st, string applied)
    {
        string sws = string.Join("+", a.sw.ToArray());
        string line = string.Join(",", new[] { order.ToString(IC), a.id, p.id, "\"" + sws + "\"", status, st != null ? st.n.ToString(IC) : "0",
            st != null ? F2(st.median) : "", st != null ? F2(st.p95) : "", st != null ? F2(st.mean) : "", st != null ? F2(st.max) : "",
            st != null && st.median > 0f ? F2(1000f / st.median) : "", st != null && st.mean > 0f ? F2(1000f / st.mean) : "", st != null && st.gpu > 0f ? F2(st.gpu) : "",
            st != null ? st.shot : "", "\"" + (a.note ?? "").Replace("\"", "'") + "\"" });
        try { File.AppendAllText(csvPath, line + "\n"); } catch (Exception e) { Log("csv write failed: " + e.Message); }
        string msg = "ROW " + order + " " + a.id + "/" + p.id + " " + status + (st != null ? " median " + F2(st.median) + " ms p95 " + F2(st.p95) + " ms = " + F2(1000f / Mathf.Max(0.001f, st.median)) + " fps" : "") + (applied != null ? "  " + applied : "");
        summary.Add(msg); Log(msg);
    }

    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup; CameraRig rig0 = null;
        while ((D = Find(Roster.PlayerObject)) == null || (P = Find(Roster.EnemyObject)) == null || (rig0 = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > 20f) { Log("fighters / CameraRig not found"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        rig = rig0;
        var swTxt = HudCfg.ReadData(cfg.switchesFile);
        if (swTxt == null) { Log("switch table " + cfg.switchesFile + " not found (install lanePERF5 apply_perf5.py --apply first)"); Finish(2, "no switch table"); yield break; }
        try { sw = JsonUtility.FromJson<SwFile>(swTxt) ?? new SwFile(); } catch (Exception e) { Log("switch table parse failed: " + e.Message); Finish(2, "bad switch table"); yield break; }
        var arms = BuildArms();
        var poses = new List<Pose>(); foreach (var p in cfg.poses) if (p != null && p.enabled) poses.Add(p);
        if (poses.Count == 0 || arms.Count == 0) { Log("no enabled poses / arms in perfab.json"); Finish(2, "no arms"); yield break; }
        Log("data " + dataSrc + " switches=" + (sw.switches != null ? sw.switches.Length : 0) + " arms=" + arms.Count + " poses=" + poses.Count + " boot=" + cfg.bootFrames + " warm=" + cfg.warmupFrames + " measure=" + cfg.measureFrames + " res " + Screen.width + "x" + Screen.height);
        if (cfg.vsyncOff) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
        Time.captureFramerate = 0;
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
            else if (b is DoorwayFlicker || b is AtmosFlicker || b is FurnaceFlicker || b is CrackFlicker || b is Flicker) flickers.Add(b);   // keep running while timed (they cost frame time), frozen only for the screenshot
        }
        D.opponent = P; P.opponent = D; P.maxHealth = cfg.healthFill; P.health = cfg.healthFill; D.maxHealth = cfg.healthFill; D.health = cfg.healthFill;
        rig.lockEnabled = true; rig.lockRange = cfg.lockRange;
        yield return Frames(cfg.bootFrames);
        TakeSnapshots();
        try { File.WriteAllText(csvPath, "order,arm,pose,switches,status,frames,median_ms,p95_ms,mean_ms,max_ms,fps_median,fps_mean,gpu_ms_median,screenshot,note\n"); } catch (Exception e) { Log("csv create failed: " + e.Message); Finish(2, "csv"); yield break; }

        int order = 0;
        foreach (var pose in poses)
        {
            Vector3 anchor = V3(pose.anchor, new Vector3(0f, 0f, 17f)), axis = V3(pose.axis, Vector3.forward); axis.y = 0f; axis.Normalize();
            Place(P, anchor, -axis); Place(D, anchor - axis * pose.sepM, axis);
            D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; D.guardHeld = false; P.guardHeld = false;
            foreach (var a in arms)
            {
                order++;
                if (a.skipped != null) { Row(a, pose, order, "skipped:" + a.skipped, null, null); continue; }
                string applied = ApplyArm(a);
                yield return Frames(cfg.applySettleFrames);
                Place(P, anchor, -axis); Place(D, anchor - axis * pose.sepM, axis);   // hold the pose (re-place after the settle: a profile swap must never move a fighter)
                yield return Frames(cfg.warmupFrames);
                var st = new Stats();
                yield return Measure(st);
                yield return Shot("perfab_" + a.id + "_" + pose.id, st);
                Row(a, pose, order, "ok", st, applied);
            }
        }
        // leave the player as booted
        RestoreSnapshots(); QualityTiers.AbQset = null; PerfFixes.AbOnly = null; PerfMem.AbOnly = null; RuntimeQuality.Reapply(); RestoreCameras();
        Finish(0, "done");
    }

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("perfab_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height + "  gpu " + SystemInfo.graphicsDeviceName);
            sb.AppendLine("VERDICT: " + (code == 0 ? "COMPLETE" : "FAIL") + "   (exit code " + code + ", " + why + ")   numbers: Assets/Data/perfab.json (" + dataSrc + ")");
            sb.AppendLine("next: python perfab_report.py " + dir);
            foreach (var r in summary) sb.AppendLine("  " + r);
            File.WriteAllText(Path.Combine(dir, "perfab_summary.txt"), sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[perfab] summary write failed: " + e.Message); }
        Log("FINISH " + why + " exit " + code);
    }
}
