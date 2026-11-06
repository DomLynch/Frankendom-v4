using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// KILLREPORT v1 (staged dark, NOT COMPILED; Unity dev compiles at merge). Replaces Assets/Scripts/PerfBench.cs (live copy of 2026-11-06 12:00 kept in _bak).
// -perfbench frame-time recorder. It does not drive gameplay: run it together with the deterministic -balanceauto harness (same seed => same content).
//   -perfbench -benchseconds:90 -benchwarmup:25 -benchtag:<label> -benchout:<dir>
// Writes <dir>\frames_<tag>.csv (UNCHANGED format: one wall ms per line, "F2", '\n') and <dir>\perf_<tag>.json (NOW VALID JSON, one object, json_version 2).
// What changed against the live file (v104a):
//   1. perf json is a single valid object. Before, the v104a keys (refresh_hz ... render_scale_pct) were appended AFTER the closing brace.
//   2. The real internal render size is sampled DURING the bench window (RenderPipelineManager.endCameraRendering -> HDCamera.actualWidth/Height, which is
//      the DRS-scaled size). Before, it was read at Finish via DynamicResolutionHandler.instance, which sits at 100% outside a render, so every arm logged the full size.
//   3. The json is written when the fight ends ("FIGHT END n/N" with n==N on the [balance] log), on quit, or at the end of the window, whichever is first.
//      Before, a quit with 100 frames or fewer wrote nothing, and a fight that ended early depended on OnApplicationQuit alone.
//   4. json written to a .tmp and moved into place after the csv, so a watcher that sees perf_*.json always finds the complete csv.
// New keys: json_version, end_reason (window|fight_end|quit), window_s, short_run, render_w, render_h, render_size (now the measured size), render_sizes (size -> frame count),
//   render_samples, render_scale_req_pct (from -renderscale: / -qset:renderScale=, -1 = not set), render_scale_pct (now MEASURED, render_w / screen width * 100).
// Needs Player Settings "Frame Timing Stats" (ProjectSettings.asset enableFrameTimingStats: 0 today) for gpu_ms; without it gpu_ms is 0 and cpu/wall only.
public class PerfBench : MonoBehaviour
{
    static string Arg(string p) { foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith(p, System.StringComparison.Ordinal)) return a.Substring(p.Length); return null; }
    static bool Flag(string n) { return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), n) >= 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (Flag("-perfbench")) new GameObject("PerfBench").AddComponent<PerfBench>(); }

    float warm = 25f, dur = 90f, t0 = -1f; string tag = "run", dir; bool done, fightEndSeen;
    readonly List<float> wall = new List<float>(8192), gpu = new List<float>(8192), cpu = new List<float>(8192);
    readonly FrameTiming[] ft = new FrameTiming[1];
    readonly Dictionary<long, int> sizes = new Dictionary<long, int>();   // (w << 32 | h) -> frames rendered at that size inside the bench window
    int sizeSamples;

    bool InWindow() { return !done && t0 >= 0f && Time.unscaledTime - t0 >= warm; }

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
        float.TryParse(Arg("-benchwarmup:") ?? "25", NumberStyles.Float, CultureInfo.InvariantCulture, out warm);
        float.TryParse(Arg("-benchseconds:") ?? "90", NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
        tag = Arg("-benchtag:") ?? QualityTiers.Current; dir = Arg("-benchout:") ?? Application.persistentDataPath;
        Directory.CreateDirectory(dir);
    }

    void OnEnable() { RenderPipelineManager.endCameraRendering += OnEndCamera; Application.logMessageReceived += OnLog; }
    void OnDisable() { RenderPipelineManager.endCameraRendering -= OnEndCamera; Application.logMessageReceived -= OnLog; }

    // Real render size: HDCamera.actualWidth/Height is the size after dynamic resolution. Counted only inside the bench window and only for Game cameras.
    void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (!InWindow() || cam == null || cam.cameraType != CameraType.Game) return;
        var hd = HDCamera.GetOrCreate(cam);
        if (hd == null) return;
        long key = ((long)hd.actualWidth << 32) | (uint)hd.actualHeight;
        int n; sizes.TryGetValue(key, out n); sizes[key] = n + 1; sizeSamples++;
    }

    // BalanceAuto logs "[balance] f ... FIGHT END a/b (reason) at s". Last fight over => write now instead of waiting for the process to quit.
    void OnLog(string msg, string stack, LogType type)
    {
        if (done || msg == null || msg.IndexOf("FIGHT END", System.StringComparison.Ordinal) < 0) return;
        fightEndSeen = true;
        var m = System.Text.RegularExpressions.Regex.Match(msg, @"FIGHT END (\d+)/(\d+)");
        if (m.Success && m.Groups[1].Value == m.Groups[2].Value && wall.Count > 0) Finish("fight_end");
    }

    void Update()
    {
        if (done) return;
        if (t0 < 0f) t0 = Time.unscaledTime;
        float el = Time.unscaledTime - t0;
        if (el < warm) return;
        wall.Add(Time.unscaledDeltaTime * 1000f);
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, ft) > 0) { gpu.Add((float)ft[0].gpuFrameTime); cpu.Add((float)ft[0].cpuFrameTime); }
        if (el >= warm + dur) Finish("window");
    }

    // Any frames at all are written on quit (was: only above 100 frames); short_run in the json tells the report to treat a short window with care.
    void OnApplicationQuit() { if (!done && wall.Count > 0) Finish(fightEndSeen ? "fight_end" : "quit"); }

    static float Pct(List<float> sorted, float p) { if (sorted.Count == 0) return 0f; return sorted[Mathf.Clamp(Mathf.CeilToInt(p * sorted.Count) - 1, 0, sorted.Count - 1)]; }
    static float Avg(List<float> l) { if (l.Count == 0) return 0f; double s = 0; foreach (var v in l) s += v; return (float)(s / l.Count); }
    static string Esc(string s) { if (s == null) return ""; return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " "); }

    static float RequestedScalePct()
    {
        var IC = CultureInfo.InvariantCulture; float v;
        string rs = Arg("-renderscale:");
        if (rs != null && float.TryParse(rs, NumberStyles.Float, IC, out v)) return v;
        string qs = Arg("-qset:");
        if (qs != null) { int i = qs.IndexOf("renderScale=", System.StringComparison.Ordinal); if (i >= 0) { string t = qs.Substring(i + 12); int e = t.IndexOfAny(new[] { ',', ';', ' ' }); if (e >= 0) t = t.Substring(0, e); if (float.TryParse(t, NumberStyles.Float, IC, out v)) return v; } }
        return -1f;
    }

    void Finish(string reason)
    {
        if (done) return;
        done = true;
        var w = new List<float>(wall); w.Sort(); var g = new List<float>(gpu); g.Sort();
        int over33 = 0, over100 = 0, over500 = 0; double windowMs = 0; foreach (var v in wall) { windowMs += v; if (v > 33.4f) over33++; if (v > 100f) over100++; if (v > 500f) over500++; }
        float avgMs = Avg(wall), p99 = Pct(w, 0.99f), p999 = Pct(w, 0.999f);
        var IC = CultureInfo.InvariantCulture;

        // Measured render size = the most frequent size seen in the window (ties: the larger area). All sizes are listed in render_sizes.
        long bestKey = 0; int bestN = 0; var szJson = new StringBuilder("{");
        foreach (var kv in sizes)
        {
            int kw = (int)(kv.Key >> 32), kh = (int)(kv.Key & 0xFFFFFFFFL);
            if (szJson.Length > 1) szJson.Append(", ");
            szJson.Append("\"" + kw + "x" + kh + "\": " + kv.Value);
            long area = (long)kw * kh, bestArea = ((bestKey >> 32) * (bestKey & 0xFFFFFFFFL));
            if (kv.Value > bestN || (kv.Value == bestN && area > bestArea)) { bestN = kv.Value; bestKey = kv.Key; }
        }
        szJson.Append("}");
        int rw = sizes.Count > 0 ? (int)(bestKey >> 32) : 0, rh = sizes.Count > 0 ? (int)(bestKey & 0xFFFFFFFFL) : 0;   // 0x0 = no Game camera rendered in the window (e.g. built-in pipeline)

        var sb = new StringBuilder("{\n"); bool first = true;
        void K(string k, string v) { if (!first) sb.Append(",\n"); first = false; sb.Append("  \"" + k + "\": " + v); }
        string S(string v) { return "\"" + Esc(v) + "\""; }
        K("json_version", "2"); K("tag", S(tag)); K("tier", S(QualityTiers.Current)); K("end_reason", S(reason));
        K("gpu", S(SystemInfo.graphicsDeviceName)); K("api", S(SystemInfo.graphicsDeviceType.ToString())); K("vram_mb", SystemInfo.graphicsMemorySize.ToString());
        K("res", S(Screen.width + "x" + Screen.height)); K("frames", wall.Count.ToString()); K("window_s", (windowMs / 1000.0).ToString("F1", IC)); K("short_run", (wall.Count < 200 ? "true" : "false"));
        K("fps_avg", (1000f / Mathf.Max(0.001f, avgMs)).ToString("F1", IC)); K("fps_1pct_low", (1000f / Mathf.Max(0.001f, p99)).ToString("F1", IC)); K("fps_01pct_low", (1000f / Mathf.Max(0.001f, p999)).ToString("F1", IC));
        K("ms_p50", Pct(w, 0.5f).ToString("F2", IC)); K("ms_p95", Pct(w, 0.95f).ToString("F2", IC)); K("ms_p99", p99.ToString("F2", IC)); K("ms_max", (w.Count > 0 ? w[w.Count - 1] : 0f).ToString("F1", IC));
        K("frames_over_33ms", over33.ToString()); K("frames_over_100ms", over100.ToString()); K("frames_over_500ms", over500.ToString());
        K("gpu_ms_avg", Avg(gpu).ToString("F2", IC)); K("gpu_ms_p95", Pct(g, 0.95f).ToString("F2", IC)); K("cpu_ms_avg", Avg(cpu).ToString("F2", IC));
        K("mem_total_mb", (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576L).ToString());
        K("dynres", (DynamicResolutionHandler.instance != null ? "true" : "false"));
        K("refresh_hz", Screen.currentResolution.refreshRateRatio.value.ToString("F1", IC)); K("vsync_count", QualitySettings.vSyncCount.ToString()); K("target_fps", Application.targetFrameRate.ToString());
        K("render_w", rw.ToString()); K("render_h", rh.ToString()); K("render_size", S(rw + "x" + rh)); K("render_sizes", szJson.ToString()); K("render_samples", sizeSamples.ToString());
        K("render_scale_req_pct", RequestedScalePct().ToString("F1", IC)); K("render_scale_pct", (rw > 0 && Screen.width > 0 ? 100f * rw / Screen.width : -1f).ToString("F1", IC));
        sb.Append("\n}\n");

        // csv first (format unchanged), json last through a .tmp so a watcher that sees perf_*.json always finds the complete csv.
        var raw = new StringBuilder(); foreach (var v in wall) raw.Append(v.ToString("F2", IC)).Append('\n');
        File.WriteAllText(Path.Combine(dir, "frames_" + tag + ".csv"), raw.ToString());
        string jp = Path.Combine(dir, "perf_" + tag + ".json"), jt = jp + ".tmp";
        File.WriteAllText(jt, sb.ToString());
        if (File.Exists(jp)) File.Delete(jp);
        File.Move(jt, jp);
        Debug.Log("PERFBENCH " + tag + " fps_avg=" + (1000f / Mathf.Max(0.001f, avgMs)).ToString("F1", IC) + " p99ms=" + p99.ToString("F1", IC) + " gpu_ms=" + Avg(gpu).ToString("F1", IC) + " over500=" + over500 + " render=" + rw + "x" + rh + " end=" + reason);
        if (!Flag("-balanceauto")) Application.Quit(0);
    }
}
