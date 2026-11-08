using System;
#if !FIXSAVE_NOUNITY
using UnityEngine;
#endif

// FIXSAVE v1 (laneFIXSAVE): the ONE answer to "is this a harness / capture / proof run, and did it redirect its saves".
// Before this there were six definitions that could disagree (GameLoop.HarnessOn, MenuCfg.Harness, SavePaths, CaptureUtil.HarnessRequested, FrameRateCap.Harness, PerfFlags.HarnessRun).
// Those six keep their own jobs (fades, menu, frame cap ...) so normal play and every proof behave exactly as before; RunMode is the SUPERSET used for one question only: may this process write the REAL save files?
//   IsHarness   = batch mode, OR any flag of run_mode.json (exact flags + prefixes; the list is data, extend it there), OR MenuCfg.Harness (the menu.json list, which includes GameLoop.HarnessOn).
//   HasSavePath = the command line carries "-savePath <dir>" / "-savePath:<dir>" with a non-empty dir.
//   RealSaveBlocked = IsHarness and the save dir was NOT redirected (neither -savePath nor SavePaths' own harness_save fallback). LootSave then keeps its state in memory and never touches the real file.
// A normal launch (no flag, not batch mode) returns IsHarness false, RealSaveBlocked false: nothing changes.
public static class RunMode
{
    /// <summary>pure: a harness flag is present and no -savePath was given (what RealSaveBlocked means before SavePaths' own harness_save fallback).</summary>
    public static bool BlockedArgs(string[] args, string[] flags, string[] prefixes) { return IsHarnessArgs(args, flags, prefixes) && !HasSavePathArgs(args); }
    [Serializable] public class Cfg { public string[] flags = new string[0]; public string[] prefixes = new string[0]; public bool batchModeIsHarness = true; }

    // pure core (no UnityEngine): unit-tested by laneFIXSAVE\test\SaveTests.cs
    public static bool IsHarnessArgs(string[] args, string[] flags, string[] prefixes)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i]; if (string.IsNullOrEmpty(a)) continue;
            if (flags != null) for (int k = 0; k < flags.Length; k++) if (flags[k] != null && string.Equals(a, flags[k], StringComparison.OrdinalIgnoreCase)) return true;
            if (prefixes != null) for (int k = 0; k < prefixes.Length; k++) if (!string.IsNullOrEmpty(prefixes[k]) && a.StartsWith(prefixes[k], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    /// <summary>"-savePath <dir>" or "-savePath:<dir>" with a non-empty dir (the same two spellings SavePaths honours).</summary>
    public static bool HasSavePathArgs(string[] args)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string s = args[i]; if (s == null) continue;
            if (string.Equals(s, "-savePath", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && !string.IsNullOrEmpty(args[i + 1])) return true;
            if (s.StartsWith("-savePath:", StringComparison.OrdinalIgnoreCase) && s.Length > 10) return true;
        }
        return false;
    }

#if !FIXSAVE_NOUNITY
    static Cfg cfg; static int harness = -1;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; harness = -1; }

    public static Cfg Config
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new Cfg();
            try { string t = DataPath.ReadText("run_mode.json"); if (t != null) { var c = JsonUtility.FromJson<Cfg>(t.TrimStart((char)0xFEFF)); if (c != null) cfg = c; } }
            catch (Exception e) { SafeFile.WarnOnce("run_mode.json", "[RunMode] run_mode.json unreadable, harness list empty: " + e.Message); }
            if (cfg.flags == null) cfg.flags = new string[0]; if (cfg.prefixes == null) cfg.prefixes = new string[0];
            return cfg;
        }
    }

    public static bool IsHarness
    {
        get
        {
            if (harness >= 0) return harness == 1;
            bool h = false;
            try
            {
                var c = Config;
                h = (c.batchModeIsHarness && Application.isBatchMode) || IsHarnessArgs(Environment.GetCommandLineArgs(), c.flags, c.prefixes);
                if (!h) { try { h = MenuCfg.Harness; } catch (Exception) { } }   // the menu.json list + GameLoop.HarnessOn: anything they call a harness is one here too
            }
            catch (Exception) { h = false; }
            harness = h ? 1 : 0; return h;
        }
    }
    public static bool HasSavePath { get { return HasSavePathArgs(Environment.GetCommandLineArgs()); } }
    /// <summary>True when the save dir is the player's real one (not redirected by -savePath or SavePaths' harness fallback).</summary>
    public static bool RealSaveDir { get { return !SavePaths.Redirected; } }
    /// <summary>A harness run that did not redirect its saves: nothing may be written to (or deleted from) the real save files.</summary>
    public static bool RealSaveBlocked { get { return IsHarness && RealSaveDir; } }
#endif
}
