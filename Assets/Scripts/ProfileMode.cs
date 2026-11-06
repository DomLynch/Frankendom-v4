using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// PROFILE v104 (laneOPT4), NOT COMPILED IN UNITY (Roslyn checked). -profile [-profiledata:<file>] [-profileout:<dir>] [-profiletag:<label>] [-profilewarmup:<s>] [-profileseconds:<s>]
// A passive RECORDER. It does not drive the scene: run it together with a harness that already makes the scene deterministic
//   run_res_ab.py arms  = -perfbench -balanceauto -seeds:1 -balanceseed:1234 (same fight, same lock cam)     PerfAb arms = -perfab (fixed poses, real lock cam, PerfAb.Label hook)
// and it records which harness / seed / resolution / renderscale it saw, so two runs can be proven comparable (profile_report.py checks it).
// SWITCH: Assets/Data/profile.json (StreamingAssets copy) "enabled": false ships. Off (or no -profile on the command line) = no component, no recorder, no file, no allocation: the only
// code that runs is one command-line scan in Boot(). -profile with enabled=false logs one line and does nothing; run_profile.py passes -profiledata with enabled=true instead of editing the shipped file.
// Output (<out>\): profile_frames.csv (one row per frame), profile_passes.csv (per HDRP pass GPU/CPU ms), profile_meta.json (what was and was not available, and why), profile_markers.txt (every marker this build offers).
// WHAT NEEDS WHICH BUILD (honest, from the package source and Unity docs; the run itself reports which columns came out empty):
//   FrameTimingManager cpu main / render thread / gpu  : release OR development, but only if PlayerSettings.enableFrameTimingStats is 1 (BuildCfg hook: -profilebuild). Else the columns stay empty.
//   ProfilerRecorder counters (draw calls, batches, set-pass, triangles, shadow casters, skinning, Animator, scripts, physics, GC alloc): expected DEVELOPMENT build only (ENABLE_PROFILER). A release build is probed anyway.
//   per-HDRP-pass GPU timing: DEVELOPMENT build only. ProfilingSampler.Get() returns null without ENABLE_PROFILER (Core RP ProfilingScope.cs), HDRP's pass samplers are internal enum HDProfileId.
// Every per-frame value is the last COMPLETED frame (FrameTimingManager lags about 3-4 frames; recorders report the previous frame). Statistics are unaffected, per-frame alignment is.
[DefaultExecutionOrder(-90)]
public class ProfileMode : MonoBehaviour
{
    [Serializable] public class CtrRow { public string col; public string[] names; public string kind; }
    [Serializable] public class Cfg
    {
        public bool enabled = false;
        public float warmupSeconds = 20f, measureSeconds = 45f;
        public int minWarmupFrames = 120, maxFrames = 20000;
        public bool quitWhenDone = true, listMarkers = true, frameTiming = true, passes = true;
        public string[] companionArgs = { "-balanceauto", "-perfab", "-perfbench" };
        public string[] quitBlockers = { "-balanceauto", "-perfab" };
        public string outDirName = "profile";
        public CtrRow[] counters = new CtrRow[0];
    }

    /// <summary>Optional label for the rows being written (PerfAb sets it per arm/pose through the PROFILE hook). Empty = "run".</summary>
    public static string Label = "";
    static ProfileMode inst; static Cfg bootCfg; static string bootSrc;
    /// <summary>Writes the files now. PerfAb ends with Environment.Exit, which skips OnApplicationQuit, so its PROFILE hook calls this first.</summary>
    public static void FlushNow() { if (inst != null) inst.Finish(true); }

    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.Ordinal)) return a.Substring(p.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-profile")) return;
        var c = LoadCfg(out string src);
        if (!c.enabled) { Debug.Log("[profile] -profile ignored: profile.json enabled=false (" + src + "). run_profile.py passes -profiledata with enabled=true."); return; }
        if (FindFirstObjectByType<ProfileMode>() != null) return;
        bootCfg = c; bootSrc = src;   // Awake runs inside AddComponent, so the data is staged first
        var go = new GameObject("ProfileMode"); DontDestroyOnLoad(go); go.AddComponent<ProfileMode>();
    }

    static Cfg LoadCfg(out string src)
    {
        var c = new Cfg(); src = "(defaults)"; string txt = null, over = Arg("-profiledata:");
        if (!string.IsNullOrEmpty(over)) { try { txt = File.ReadAllText(over).TrimStart((char)0xFEFF); src = over; } catch (Exception e) { Debug.LogWarning("[profile] -profiledata unreadable: " + e.Message); } }
        if (txt == null) { txt = HudCfg.ReadData("profile.json"); if (txt != null) src = "profile.json"; }
        if (txt != null) { try { var d = JsonUtility.FromJson<Cfg>(txt); if (d != null) c = d; } catch (Exception e) { Debug.LogWarning("[profile] profile.json parse failed, defaults: " + e.Message); } }
        if (c.counters == null) c.counters = new CtrRow[0];
        c.maxFrames = Mathf.Clamp(c.maxFrames, 100, 200000);
        return c;
    }

    // ---------------- state ----------------
    Cfg cfg; string dataSrc; string dir, tag;
    class Ctr { public CtrRow row; public ProfilerRecorder rec; public string matched = "", status = "not tried"; public double scale = 1.0; public bool ok; }
    class Pass { public string name; public ProfilingSampler s; public List<float> gpu, cpu; }
    readonly List<Ctr> ctrs = new List<Ctr>(); readonly List<Pass> passes = new List<Pass>();
    string passStatus = "off", ftStatus = "off"; int passNull;
    readonly FrameTiming[] ft = new FrameTiming[1];
    float warm, dur, t0 = -1f; int frames, startFrame; bool done, started;
    int nCol; float[] rows; string[] labels; int nRows;   // rows = flat [maxFrames x nCol], allocated once so sampling itself allocates nothing
    Camera cam; int gcc0, gcc1, gcc2;
    int nFtValid, nGpuValid;
    string sceneNote = "";
    static readonly string[] FixedCols = { "frame", "t_s", "wall_ms", "ft_cpu_ms", "ft_cpu_main_ms", "ft_cpu_render_ms", "ft_gpu_ms", "ft_present_wait_ms", "dyn_scale_w", "cam_x", "cam_y", "cam_z", "cam_yaw", "gc_collections" };

    void Awake()
    {
        cfg = bootCfg; dataSrc = bootSrc; inst = this;
        string o = Arg("-profileout:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), cfg.outDirName); } catch { dir = cfg.outDirName; }
        try { Directory.CreateDirectory(dir); } catch (Exception e) { Debug.LogWarning("[profile] cannot create " + dir + ": " + e.Message); }
        tag = Arg("-profiletag:") ?? "run";
        float v; if (float.TryParse(Arg("-profilewarmup:") ?? "", NumberStyles.Float, IC, out v)) cfg.warmupSeconds = v;
        if (float.TryParse(Arg("-profileseconds:") ?? "", NumberStyles.Float, IC, out v)) cfg.measureSeconds = v;
        warm = cfg.warmupSeconds; dur = cfg.measureSeconds;
        Application.runInBackground = true;
        InitCounters(); InitPasses();
        nCol = FixedCols.Length + ctrs.Count;
        rows = new float[cfg.maxFrames * nCol]; labels = new string[cfg.maxFrames];
        bool companion = false; foreach (var a in cfg.companionArgs) if (Flag(a)) companion = true;
        sceneNote = companion ? "scripted by companion harness" : "NO harness on the command line: scene is not scripted, runs are NOT comparable";
        Debug.Log("[profile] on: " + dataSrc + " out=" + dir + " tag=" + tag + " warm=" + warm + "s measure=" + dur + "s counters ok=" + CountOk() + "/" + ctrs.Count + " passes=" + passes.Count + " frameTiming=" + ftStatus + " (" + sceneNote + ")");
    }

    int CountOk() { int n = 0; foreach (var c in ctrs) if (c.ok) n++; return n; }

    // ---------------- ProfilerRecorder counters ----------------
    // names[]: tried in order; a name starting with "~" is a case-insensitive substring match (first marker in the build's list). The matched name is written to profile_meta.json.
    void InitCounters()
    {
        var handles = new List<ProfilerRecorderHandle>(); var names = new List<string>(); var units = new List<ProfilerMarkerDataUnit>();
        try
        {
            ProfilerRecorderHandle.GetAvailable(handles);
            var sb = new StringBuilder();
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h); names.Add(d.Name); units.Add(d.UnitType);
                if (cfg.listMarkers) sb.Append(d.Name).Append('\t').Append(d.UnitType).Append('\n');
            }
            if (cfg.listMarkers) try { File.WriteAllText(Path.Combine(dir, "profile_markers.txt"), "# " + handles.Count + " markers/counters this build offers (name<TAB>unit). Add the right name to profile.json counters[].names to fix an empty column.\n" + sb); } catch { }
        }
        catch (Exception e) { Debug.LogWarning("[profile] marker list failed: " + e.Message); }
        const ProfilerRecorderOptions opt = ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.SumAllSamplesInFrame;
        foreach (var r in cfg.counters)
        {
            var c = new Ctr { row = r };
            ctrs.Add(c);
            if (r == null || string.IsNullOrEmpty(r.col) || r.names == null) { c.status = "bad row"; continue; }
            c.scale = r.kind == "ms" ? 1e-6 : 1.0;   // time markers report ns; count and bytes stay as they are
            int hit = -1;
            foreach (var n in r.names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                bool sub = n[0] == '~'; string key = sub ? n.Substring(1) : n;
                for (int i = 0; i < names.Count && hit < 0; i++)
                    if (sub ? names[i].IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 : names[i] == key) hit = i;
                if (hit >= 0) break;
            }
            if (hit < 0) { c.status = handles.Count == 0 ? "no marker list in this build (release?)" : "no such marker (see profile_markers.txt)"; continue; }
            try
            {
                c.rec = new ProfilerRecorder(handles[hit], 1, opt); c.matched = names[hit];
                bool timeUnit = units[hit] == ProfilerMarkerDataUnit.TimeNanoseconds;
                if (r.kind == "ms" && !timeUnit) c.scale = 1.0;   // not a time unit after all: report raw, status says so
                c.ok = c.rec.Valid; c.status = c.ok ? "ok unit=" + units[hit] : "recorder not valid";
            }
            catch (Exception e) { c.status = "exception " + e.Message; }
        }
    }

    // ---------------- per-HDRP-pass GPU timing (development build) ----------------
    void InitPasses()
    {
        if (!cfg.passes) { passStatus = "off by data"; return; }
        try
        {
            var t = typeof(HDRenderPipeline).Assembly.GetType("UnityEngine.Rendering.HighDefinition.HDProfileId");
            if (t == null) { passStatus = "HDProfileId not found (HDRP changed?)"; return; }
            MethodInfo get = null;
            foreach (var m in typeof(ProfilingSampler).GetMethods(BindingFlags.Public | BindingFlags.Static)) if (m.Name == "Get" && m.IsGenericMethodDefinition) { get = m; break; }
            if (get == null) { passStatus = "ProfilingSampler.Get not found"; return; }
            var g = get.MakeGenericMethod(t); var arg = new object[1];
            foreach (var v in Enum.GetValues(t))
            {
                arg[0] = v; var s = g.Invoke(null, arg) as ProfilingSampler;
                if (s == null) { passNull++; continue; }
                s.enableRecording = true;
                passes.Add(new Pass { name = v.ToString(), s = s, gpu = new List<float>(cfg.maxFrames), cpu = new List<float>(cfg.maxFrames) });
            }
            passStatus = passes.Count > 0 ? "ok (" + passes.Count + " HDRP samplers recording)" : "UNAVAILABLE: ProfilingSampler.Get returned null for all " + passNull + " ids = not a development build (ENABLE_PROFILER off)";
        }
        catch (Exception e) { passStatus = "exception " + e.Message; }
    }

    // ---------------- frame loop ----------------
    void Update()
    {
        if (done) return;
        if (t0 < 0f) t0 = Time.unscaledTime;
        float el = Time.unscaledTime - t0; frames++;
        if (!started)
        {
            if (el < warm || frames < cfg.minWarmupFrames) return;
            started = true; startFrame = Time.frameCount; gcc0 = GC.CollectionCount(0); gcc1 = GC.CollectionCount(1); gcc2 = GC.CollectionCount(2);
            t0 = Time.unscaledTime; el = 0f;
            Debug.Log("[profile] measuring " + dur + " s");
        }
        if (nRows < cfg.maxFrames) Sample(el);
        if (el >= dur || nRows >= cfg.maxFrames) Finish(false);
    }

    void Sample(float el)
    {
        float[] r = rows; int k = nRows * nCol;
        r[k++] = Time.frameCount - startFrame; r[k++] = el; r[k++] = Time.unscaledDeltaTime * 1000f;
        float cpu = float.NaN, cmain = float.NaN, crend = float.NaN, gpu = float.NaN, pw = float.NaN, sw = float.NaN;
        if (cfg.frameTiming)
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, ft) > 0)
                {
                    cpu = (float)ft[0].cpuFrameTime; cmain = (float)ft[0].cpuMainThreadFrameTime; crend = (float)ft[0].cpuRenderThreadFrameTime; gpu = (float)ft[0].gpuFrameTime;
                    pw = (float)ft[0].cpuMainThreadPresentWaitTime; sw = ft[0].widthScale;
                    if (cpu > 0f) nFtValid++; if (gpu > 0f) nGpuValid++;
                    if (cpu <= 0f) cpu = float.NaN; if (cmain <= 0f) cmain = float.NaN; if (crend <= 0f) crend = float.NaN; if (gpu <= 0f) gpu = float.NaN;
                }
            }
            catch (Exception e) { ftStatus = "exception " + e.Message; cfg.frameTiming = false; }
        }
        r[k++] = cpu; r[k++] = cmain; r[k++] = crend; r[k++] = gpu; r[k++] = pw; r[k++] = sw;
        if (cam == null) cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        if (cam != null) { var p = cam.transform.position; r[k++] = p.x; r[k++] = p.y; r[k++] = p.z; r[k++] = cam.transform.eulerAngles.y; } else { r[k++] = float.NaN; r[k++] = float.NaN; r[k++] = float.NaN; r[k++] = float.NaN; }
        r[k++] = GC.CollectionCount(0) - gcc0;
        foreach (var c in ctrs) r[k++] = c.ok && c.rec.Valid ? (float)(c.rec.LastValue * c.scale) : float.NaN;
        labels[nRows] = string.IsNullOrEmpty(Label) ? "run" : Label; nRows++;
        foreach (var ps in passes) { ps.gpu.Add(ps.s.gpuElapsedTime); ps.cpu.Add(ps.s.cpuElapsedTime + ps.s.inlineCpuElapsedTime); }
    }

    void OnApplicationQuit() { if (!done && started && nRows > 10) Finish(true); }
    void OnDestroy() { foreach (var c in ctrs) if (c.rec.Valid) c.rec.Dispose(); }

    static string N(float v) { return float.IsNaN(v) ? "" : v.ToString("0.###", IC); }
    static float Pct(List<float> s, float p) { if (s.Count == 0) return 0f; return s[Mathf.Clamp(Mathf.CeilToInt(p * s.Count) - 1, 0, s.Count - 1)]; }
    static string J(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "'").Replace("\r", " ").Replace("\n", " "); }

    void Finish(bool quitting)
    {
        if (done) return; done = true;
        try
        {
            // frames csv. Written once at the end so the GC column of the measured window never contains this code's own allocations.
            var sb = new StringBuilder(); sb.Append(string.Join(",", FixedCols));
            foreach (var c in ctrs) sb.Append(',').Append(c.row != null ? c.row.col : "?"); sb.Append(",label\n");
            for (int i = 0; i < nRows; i++) { for (int j = 0; j < nCol; j++) { if (j > 0) sb.Append(','); sb.Append(N(rows[i * nCol + j])); } sb.Append(',').Append(labels[i]).Append('\n'); }
            File.WriteAllText(Path.Combine(dir, "profile_frames.csv"), sb.ToString());

            // pass csv: only passes that ever reported a non-zero time
            sb.Length = 0; sb.Append("pass,frames,gpu_mean_ms,gpu_p50_ms,gpu_p95_ms,gpu_max_ms,cpu_mean_ms,cpu_p95_ms,cpu_max_ms\n"); int nz = 0;
            foreach (var ps in passes)
            {
                double gs = 0, cs = 0; float gm = 0, cm = 0; foreach (var v in ps.gpu) { gs += v; if (v > gm) gm = v; } foreach (var v in ps.cpu) { cs += v; if (v > cm) cm = v; }
                if (gm <= 0f && cm <= 0f) continue; nz++;
                var g = new List<float>(ps.gpu); g.Sort(); var c = new List<float>(ps.cpu); c.Sort(); int n = Mathf.Max(1, ps.gpu.Count);
                sb.Append(ps.name).Append(',').Append(ps.gpu.Count).Append(',').Append((gs / n).ToString("0.####", IC)).Append(',').Append(Pct(g, 0.5f).ToString("0.####", IC)).Append(',').Append(Pct(g, 0.95f).ToString("0.####", IC)).Append(',').Append(gm.ToString("0.####", IC))
                  .Append(',').Append((cs / n).ToString("0.####", IC)).Append(',').Append(Pct(c, 0.95f).ToString("0.####", IC)).Append(',').Append(cm.ToString("0.####", IC)).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "profile_passes.csv"), sb.ToString());
            if (passes.Count > 0 && nz == 0) passStatus += "; but every pass read 0 ms (GPU markers not sampled on this API/driver)";

            // meta
            sb.Length = 0; sb.Append("{\n");
            void K(string k, string v) { sb.Append("  \"" + k + "\": " + v + ",\n"); }
            void S(string k, string v) { K(k, "\"" + J(v) + "\""); }
            S("tag", tag); S("data", dataSrc); S("scene", sceneNote); S("args", string.Join(" ", Environment.GetCommandLineArgs(), 1, Environment.GetCommandLineArgs().Length - 1));
            S("unity", Application.unityVersion); S("game_version", Application.version); S("gpu", SystemInfo.graphicsDeviceName); S("api", SystemInfo.graphicsDeviceType.ToString());
            K("vram_mb", SystemInfo.graphicsMemorySize.ToString()); K("is_development_build", Debug.isDebugBuild ? "true" : "false");
            S("res", Screen.width + "x" + Screen.height); K("screen_w", Screen.width.ToString()); K("screen_h", Screen.height.ToString()); S("quality_tier", QualityTiers.Current);
            K("frames", nRows.ToString()); K("seconds", dur.ToString("0.###", IC)); K("warmup_seconds", warm.ToString("0.###", IC)); K("quit_signal", quitting ? "true" : "false");
            K("frame_timing_feature_enabled", FrameTimingManager.IsFeatureEnabled() ? "true" : "false"); K("frame_timing_frames_with_cpu", nFtValid.ToString()); K("frame_timing_frames_with_gpu", nGpuValid.ToString());
            S("frame_timing_status", ftStatus == "off" ? (FrameTimingManager.IsFeatureEnabled() ? (nGpuValid > 0 ? "ok" : "enabled but gpu time never reported") : "DISABLED: PlayerSettings.enableFrameTimingStats is 0 in this build (build with -profilebuild)") : ftStatus);
            S("passes_status", passStatus); K("passes_with_data", nz.ToString());
            K("renderers", FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length.ToString()); K("lights", FindObjectsByType<Light>(FindObjectsSortMode.None).Length.ToString());
            K("skinned_renderers", FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None).Length.ToString()); K("animators", FindObjectsByType<Animator>(FindObjectsSortMode.None).Length.ToString());
            K("gc_collections_gen0", (GC.CollectionCount(0) - gcc0).ToString()); K("gc_collections_gen1", (GC.CollectionCount(1) - gcc1).ToString()); K("gc_collections_gen2", (GC.CollectionCount(2) - gcc2).ToString());
            sb.Append("  \"counters\": [\n");
            for (int i = 0; i < ctrs.Count; i++) sb.Append("    {\"col\": \"" + J(ctrs[i].row != null ? ctrs[i].row.col : "?") + "\", \"ok\": " + (ctrs[i].ok ? "true" : "false") + ", \"matched\": \"" + J(ctrs[i].matched) + "\", \"status\": \"" + J(ctrs[i].status) + "\"}" + (i < ctrs.Count - 1 ? "," : "") + "\n");
            sb.Append("  ],\n  \"end\": true\n}\n");
            File.WriteAllText(Path.Combine(dir, "profile_meta.json"), sb.ToString());
            Debug.Log("[profile] wrote " + nRows + " frames to " + dir + " (passes with data " + nz + ", frame timing gpu frames " + nGpuValid + ")");
        }
        catch (Exception e) { Debug.LogWarning("[profile] write failed: " + e); }
        bool block = false; foreach (var a in cfg.quitBlockers) if (Flag(a)) block = true;
        if (cfg.quitWhenDone && !block && !quitting) Application.Quit(0);
    }
}
