using System;
using System.IO;
using UnityEngine;

// Performance switches (lane PERF-CPU). Data: perf.json (StreamingAssets in the player, Assets/Data in the editor). Missing file = the defaults below.
//   verboseLogs      false = per-frame / per-event diagnostic Debug.Log lines are skipped (ArmHold, FootGround, FootYaw, finisher piece + look chatter). true = the old output.
//   logStackTrace    "none" | "scriptOnly" | "full" | "" : stack trace attached to Debug.Log and Debug.LogWarning in the player (Unity's default is scriptOnly; collecting a trace costs
//                    roughly 20-200 us per call and the harness logs never read it). Errors and exceptions always keep their traces. "" leaves Unity's setting alone.
//   prewarm          FxPrewarm renders every blood / spark / dust / decal material once, sub-pixel small, shortly after the scene loads (kills the first-use PSO / upload hitch).
//   prewarmInHarness false = FxPrewarm stays off in proof / balance / finisher-auto / steer-auto runs (a prewarm hitch there would shift which frame each sim tick lands on); true = prewarm there too.
//   fxScanPerFrame   FxMaterialGuard checks at most this many renderers per frame (was: all of them in one frame, every scanSeconds).
[Serializable]
public class PerfCfg
{
    public bool verboseLogs = false;
    public string logStackTrace = "none";
    public bool prewarm = true;
    public float prewarmDelaySeconds = 0.75f;
    public int prewarmFrames = 3;
    public float prewarmDistance = 2f;
    public float prewarmSize = 0.00002f;
    public bool prewarmInHarness = false;
    public int fxScanPerFrame = 150;
}

public static class PerfFlags
{
    static PerfCfg cfg;
    public static PerfCfg C { get { if (cfg == null) Load(); return cfg; } }
    public static bool VerboseLogs { get { return C.verboseLogs; } }

    static readonly string[] HarnessArgs = { "-autoplay", "-balanceauto", "-postureproof", "-flowproof", "-camproof", "-finisherauto", "-steerauto", "-verifyGround", "-latencyprobe", "-record", "-cmp", "-headsheet", "-sheet:", "-steershot=", "-steerpreview" };
    // true when the process was started by one of the automated harnesses (their results must not depend on when a hitch lands)
    public static bool HarnessRun()
    {
        if (Time.captureFramerate > 0) return true;
        foreach (var a in Environment.GetCommandLineArgs())
            for (int i = 0; i < HarnessArgs.Length; i++)
                if (a.StartsWith(HarnessArgs[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; }

    static void Load()
    {
        cfg = new PerfCfg();
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "perf.json"), Path.Combine(Application.dataPath, "Data/perf.json") })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var r = JsonUtility.FromJson<PerfCfg>(File.ReadAllText(p).TrimStart((char)0xFEFF));
                if (r != null) cfg = r;
            }
            catch (Exception e) { Debug.LogWarning("PerfFlags: perf.json unreadable: " + e.Message); }
            break;
        }
        if (cfg.fxScanPerFrame < 1) cfg.fxScanPerFrame = 1;
        if (cfg.prewarmFrames < 1) cfg.prewarmFrames = 1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ApplyLogSettings()
    {
        string s = C.logStackTrace; if (string.IsNullOrEmpty(s)) return;
        StackTraceLogType t;
        switch (s.ToLowerInvariant())
        {
            case "none": t = StackTraceLogType.None; break;
            case "full": t = StackTraceLogType.Full; break;
            case "scriptonly": t = StackTraceLogType.ScriptOnly; break;
            default: return;
        }
        Application.SetStackTraceLogType(LogType.Log, t);
        Application.SetStackTraceLogType(LogType.Warning, t);
    }
}
