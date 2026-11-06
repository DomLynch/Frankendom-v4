using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// TIERS v96 (from PERF-GPU, unchanged logic), NOT COMPILED. -perfbench frame-time recorder. It does not drive gameplay: run it together with the deterministic
// -balanceauto harness (same seed => same content) so every tier sees the same fight.
//   -perfbench -benchseconds:90 -benchwarmup:25 -benchtag:<label> -benchout:<dir>
// Writes <dir>\perf_<tag>.json (and one PERFBENCH log line) after warmup+benchseconds, then quits unless -balanceauto is running (it quits itself).
// Needs Player Settings "Frame Timing Stats" (ProjectSettings.asset enableFrameTimingStats: 0 today) for gpu_ms; without it gpu_ms is 0 and cpu/wall only.
public class PerfBench : MonoBehaviour
{
    static string Arg(string p) { foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith(p, System.StringComparison.Ordinal)) return a.Substring(p.Length); return null; }
    static bool Flag(string n) { return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), n) >= 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (Flag("-perfbench")) new GameObject("PerfBench").AddComponent<PerfBench>(); }

    float warm = 25f, dur = 90f, t0 = -1f; string tag = "run", dir; bool done;
    readonly List<float> wall = new List<float>(8192), gpu = new List<float>(8192), cpu = new List<float>(8192);
    readonly FrameTiming[] ft = new FrameTiming[1];

    void Awake()
    {
        DontDestroyOnLoad(gameObject);
        float.TryParse(Arg("-benchwarmup:") ?? "25", NumberStyles.Float, CultureInfo.InvariantCulture, out warm);
        float.TryParse(Arg("-benchseconds:") ?? "90", NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
        tag = Arg("-benchtag:") ?? QualityTiers.Current; dir = Arg("-benchout:") ?? Application.persistentDataPath;
        Directory.CreateDirectory(dir);
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
        if (el >= warm + dur) Finish();
    }

    void OnApplicationQuit() { if (!done && wall.Count > 100) Finish(); }

    static float Pct(List<float> sorted, float p) { if (sorted.Count == 0) return 0f; return sorted[Mathf.Clamp(Mathf.CeilToInt(p * sorted.Count) - 1, 0, sorted.Count - 1)]; }
    static float Avg(List<float> l) { if (l.Count == 0) return 0f; double s = 0; foreach (var v in l) s += v; return (float)(s / l.Count); }

    void Finish()
    {
        done = true;
        var w = new List<float>(wall); w.Sort(); var g = new List<float>(gpu); g.Sort();
        int over33 = 0, over100 = 0, over500 = 0; foreach (var v in wall) { if (v > 33.4f) over33++; if (v > 100f) over100++; if (v > 500f) over500++; }
        float avgMs = Avg(wall), p99 = Pct(w, 0.99f), p999 = Pct(w, 0.999f);
        var IC = CultureInfo.InvariantCulture; var sb = new StringBuilder("{\n");
        void K(string k, string v) { sb.Append("  \"" + k + "\": " + v + ",\n"); }
        K("tag", "\"" + tag + "\""); K("tier", "\"" + QualityTiers.Current + "\"");
        K("gpu", "\"" + SystemInfo.graphicsDeviceName + "\""); K("api", "\"" + SystemInfo.graphicsDeviceType + "\""); K("vram_mb", SystemInfo.graphicsMemorySize.ToString());
        K("res", "\"" + Screen.width + "x" + Screen.height + "\""); K("frames", wall.Count.ToString());
        K("fps_avg", (1000f / Mathf.Max(0.001f, avgMs)).ToString("F1", IC)); K("fps_1pct_low", (1000f / Mathf.Max(0.001f, p99)).ToString("F1", IC)); K("fps_01pct_low", (1000f / Mathf.Max(0.001f, p999)).ToString("F1", IC));
        K("ms_p50", Pct(w, 0.5f).ToString("F2", IC)); K("ms_p95", Pct(w, 0.95f).ToString("F2", IC)); K("ms_p99", p99.ToString("F2", IC)); K("ms_max", (w.Count > 0 ? w[w.Count - 1] : 0f).ToString("F1", IC));
        K("frames_over_33ms", over33.ToString()); K("frames_over_100ms", over100.ToString()); K("frames_over_500ms", over500.ToString());
        K("gpu_ms_avg", Avg(gpu).ToString("F2", IC)); K("gpu_ms_p95", Pct(g, 0.95f).ToString("F2", IC)); K("cpu_ms_avg", Avg(cpu).ToString("F2", IC));
        K("mem_total_mb", (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576L).ToString());
        sb.Append("  \"dynres\": " + (UnityEngine.Rendering.DynamicResolutionHandler.instance != null ? "true" : "false") + "\n}\n");
        // v104a: display refresh, vsync state, actual internal render size, and the raw per-frame ms (so 180 Hz quantisation is visible).
        var ds = UnityEngine.Rendering.DynamicResolutionHandler.instance; Vector2Int sz = ds != null ? ds.GetScaledSize(new Vector2Int(Screen.width, Screen.height)) : new Vector2Int(Screen.width, Screen.height);
        K("refresh_hz", Screen.currentResolution.refreshRateRatio.value.ToString("F1", IC)); K("vsync_count", QualitySettings.vSyncCount.ToString()); K("target_fps", Application.targetFrameRate.ToString());
        K("render_size", "\"" + sz.x + "x" + sz.y + "\""); K("render_scale_pct", ds != null ? ds.GetCurrentScale().ToString("F3", IC) : "-1");
        var raw = new StringBuilder(); foreach (var v in wall) raw.Append(v.ToString("F2", IC)).Append('\n');
        File.WriteAllText(Path.Combine(dir, "frames_" + tag + ".csv"), raw.ToString());
        File.WriteAllText(Path.Combine(dir, "perf_" + tag + ".json"), sb.ToString());
        Debug.Log("PERFBENCH " + tag + " fps_avg=" + (1000f / Mathf.Max(0.001f, avgMs)).ToString("F1", IC) + " p99ms=" + p99.ToString("F1", IC) + " gpu_ms=" + Avg(gpu).ToString("F1", IC) + " over500=" + over500);
        if (!Flag("-balanceauto")) Application.Quit(0);
    }
}
