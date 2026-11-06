using System;
using System.IO;
using System.Text;
using UnityEngine;

// RCHYGIENE v100 (lane laneRCHYGIENE): the release-time crash / error logger. Unity glue around CrashLogCore (the file logic: ring buffer, size caps, repeat suppression, crash marker).
// Data: Assets/Data/crash_log.json (CrashLog.Cfg below mirrors it). Output: Application.persistentDataPath/<folder>/session_<stamp>.log (Windows: %USERPROFILE%\AppData\LocalLow\<company>\<product>\logs\).
//
// WHAT IT CATCHES: Application.logMessageReceivedThreaded (main thread AND worker threads): Error / Exception / Assert are written at once with their stack trace and the lines before them
//   (ring buffer of the last ringLines messages of every level); AppDomain.UnhandledException (a thread dying) is written and flushed. It flushes on heartbeat (every heartbeatS), focus loss,
//   app pause and quit. A NATIVE crash (access violation, GPU device lost) cannot be caught from C#: the log then ends at the last heartbeat, running.flag stays behind, and the next start reports
//   LastSessionCrashed; Unity's own crash dump handler writes %TEMP%\<company>\<product>\Crashes (its path is printed in the log header).
// THE MENU FLAG: CrashLog.LastSessionCrashed / LastCrashLog / ShowNotice (true until AckNotice(), which MenuUI calls when the pause menu closes). A harness / proof run does not log and does not arm the flag.
// The Editor does not log unless crash_log.json inEditor is true or -crashlog is on the command line.
public static class CrashLog
{
    [Serializable]
    class Cfg
    {
        public bool enabled = true, inEditor = false, inHarness = false, showNotice = true;
        public string folder = "logs";
        public string[] writeLevels = { "Error", "Exception", "Assert" };                         // LogType names written at once (with stack)
        public string[] ringLevels = { "Log", "Warning", "Error", "Exception", "Assert" };        // LogType names that enter the ring buffer
        public int ringLines = 200, maxMessageChars = 600, maxStackChars = 1800, maxRepeats = 3, maxFiles = 8, maxFileKB = 512, maxTotalKB = 4096, tailReserveKB = 8;
        public float heartbeatS = 5f;
    }

    public static bool LastSessionCrashed { get; private set; }
    public static string LastCrashLog { get; private set; }   // full path of the log the crashed session was writing (may be missing if it was pruned)
    public static string CurrentLog { get { return core != null ? core.Path : null; } }
    static bool acked;
    /// <summary>The menu shows its one-line notice while this is true (first pause after a crash).</summary>
    public static bool ShowNotice { get { return LastSessionCrashed && !acked && cfg != null && cfg.showNotice; } }
    public static void AckNotice() { acked = true; }

    static Cfg cfg; static CrashLogCore core; static string dir; static int pid;
    static volatile int frame;
    static Application.LogCallback handler; static UnhandledExceptionEventHandler unhandled; static System.Collections.Generic.HashSet<string> writeSet, ringSet;
    static bool shut;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Detach(); cfg = null; core = null; dir = null; LastSessionCrashed = false; LastCrashLog = null; acked = false; shut = false; frame = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        try
        {
            cfg = new Cfg(); string txt = HudCfg.ReadData("crash_log.json");
            if (txt != null) { try { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } catch (Exception e) { Debug.LogWarning("[CrashLog] crash_log.json parse failed, defaults kept: " + e.Message); } }
            if (!cfg.enabled) return;
            if (Application.isEditor && !cfg.inEditor && Array.IndexOf(Environment.GetCommandLineArgs(), "-crashlog") < 0) return;
            if (!cfg.inHarness && MenuCfg.Harness) return;   // proof / gate runs: no files, no crash flag

            if (cfg.writeLevels == null) cfg.writeLevels = new string[0]; if (cfg.ringLevels == null) cfg.ringLevels = new string[0];
            writeSet = new System.Collections.Generic.HashSet<string>(cfg.writeLevels); ringSet = new System.Collections.Generic.HashSet<string>(cfg.ringLevels);
            dir = Path.Combine(Application.persistentDataPath, string.IsNullOrEmpty(cfg.folder) ? "logs" : cfg.folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss"); pid = System.Diagnostics.Process.GetCurrentProcess().Id;

            // the marker first: it tells whether the last session ended cleanly
            string logName = CrashLogCore.FilePrefix + stamp + CrashLogCore.FileExt;
            var prev = CrashLogCore.ArmMarker(dir, stamp, logName, pid, Alive);
            if (prev != null && !prev.wasRunning)
            {
                LastSessionCrashed = true; LastCrashLog = string.IsNullOrEmpty(prev.logFile) ? null : Path.Combine(dir, prev.logFile);
            }

            core = new CrashLogCore(new CrashLogCore.Opts
            {
                dir = dir, stamp = stamp, header = Header(prev),
                ringLines = cfg.ringLines, maxMessageChars = cfg.maxMessageChars, maxStackChars = cfg.maxStackChars, maxRepeats = cfg.maxRepeats, maxFiles = cfg.maxFiles,
                maxFileBytes = cfg.maxFileKB * 1024L, maxTotalBytes = cfg.maxTotalKB * 1024L, tailReserveBytes = cfg.tailReserveKB * 1024L
            });

            handler = OnLog; Application.logMessageReceivedThreaded += handler;
            unhandled = OnUnhandled; AppDomain.CurrentDomain.UnhandledException += unhandled;
            Application.quitting += OnQuit;
            if (LastSessionCrashed) Debug.LogWarning("[CrashLog] the last session did not end cleanly" + (LastCrashLog != null ? ": " + LastCrashLog : ""));
        }
        catch (Exception e) { Debug.LogWarning("[CrashLog] not started: " + e.Message); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootPump()
    {
        if (core == null) return;
        try { var go = new GameObject("CrashLogPump"); go.hideFlags = HideFlags.HideInHierarchy; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<CrashLogPump>(); }
        catch (Exception e) { Debug.LogWarning("[CrashLog] pump not created: " + e.Message); }
    }

    static bool Alive(int p)
    {
        try { var pr = System.Diagnostics.Process.GetProcessById(p); return !pr.HasExited && pr.ProcessName == System.Diagnostics.Process.GetCurrentProcess().ProcessName; }
        catch (Exception) { return false; }
    }

    static string Header(CrashLogCore.Previous prev)
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("=== " + Application.productName + " session log ===");
            sb.AppendLine("version: " + Application.version + "   unity: " + Application.unityVersion + "   platform: " + Application.platform + (Debug.isDebugBuild ? "   DEVELOPMENT BUILD" : ""));
            sb.AppendLine("os: " + SystemInfo.operatingSystem + "   cpu: " + SystemInfo.processorType + " x" + SystemInfo.processorCount + "   ram: " + SystemInfo.systemMemorySize + " MB");
            sb.AppendLine("gpu: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVersion + ")   vram: " + SystemInfo.graphicsMemorySize + " MB");
            sb.AppendLine("screen: " + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode + "   quality: " + QualitySettings.names[Mathf.Clamp(QualitySettings.GetQualityLevel(), 0, QualitySettings.names.Length - 1)]);
            sb.AppendLine("args: " + string.Join(" ", Environment.GetCommandLineArgs()));
            sb.AppendLine("player.log: " + Application.consoleLogPath);
            sb.AppendLine("unity crash dumps: " + Path.Combine(Path.GetTempPath(), Application.companyName, Application.productName, "Crashes"));
            sb.Append("previous session: " + (prev == null ? "ended cleanly (or first run)" : prev.wasRunning ? "another copy of the game is still running" : "DID NOT END CLEANLY, log " + prev.logFile));
        }
        catch (Exception e) { sb.Append("(header incomplete: " + e.Message + ")"); }
        return sb.ToString();
    }

    // ---------------- callbacks ----------------
    static void OnLog(string condition, string stackTrace, LogType type)
    {
        var c = core; if (c == null) return;
        string lvl = type.ToString(); bool err = writeSet != null && writeSet.Contains(lvl);
        if (!err && (ringSet == null || !ringSet.Contains(lvl))) return;
        c.Add(lvl.ToUpperInvariant(), condition, err ? stackTrace : null, err, frame);
    }
    static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        var c = core; if (c == null) return;
        c.Add("UNHANDLED", e.ExceptionObject != null ? e.ExceptionObject.ToString() : "(null)", null, true, frame);
        c.Flush("unhandled exception, terminating=" + e.IsTerminating);
    }
    static void OnQuit() { Shutdown(true); }

    internal static void Tick(int f) { frame = f; }
    internal static void Heartbeat() { var c = core; if (c != null) c.Heartbeat(); }
    internal static void FlushNow(string why) { var c = core; if (c != null) c.Flush(why); }
    internal static float HeartbeatS { get { return cfg != null ? Mathf.Max(0.5f, cfg.heartbeatS) : 5f; } }

    /// <summary>Clean end: write the ring tail and the summary, close the file, delete running.flag.</summary>
    internal static void Shutdown(bool clean)
    {
        if (shut) return; shut = true;
        try { Detach(); if (core != null) core.Close(clean); if (clean && dir != null) CrashLogCore.DisarmMarker(dir, pid); } catch (Exception) { }
    }
    static void Detach()
    {
        try
        {
            if (handler != null) { Application.logMessageReceivedThreaded -= handler; handler = null; }
            if (unhandled != null) { AppDomain.CurrentDomain.UnhandledException -= unhandled; unhandled = null; }
            Application.quitting -= OnQuit;
        }
        catch (Exception) { }
    }
}

/// <summary>Hidden carrier: frame counter for the log stamps, the heartbeat flush, focus / pause / quit flushes.</summary>
public class CrashLogPump : MonoBehaviour
{
    float next;
    void Update()
    {
        CrashLog.Tick(Time.frameCount);
        if (Time.unscaledTime >= next) { next = Time.unscaledTime + CrashLog.HeartbeatS; CrashLog.Heartbeat(); }
    }
    void OnApplicationFocus(bool focus) { if (!focus) CrashLog.FlushNow("focus lost"); }
    void OnApplicationPause(bool paused) { if (paused) CrashLog.FlushNow("app paused"); }
    void OnApplicationQuit() { CrashLog.Shutdown(true); }
}
