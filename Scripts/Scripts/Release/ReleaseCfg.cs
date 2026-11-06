using System;
using UnityEngine;

// RCHYGIENE v100 (lane laneRCHYGIENE): the few release switches the player build reads at runtime, all in Assets/Data/release.json (StreamingAssets first, via HudCfg.ReadData).
// Today: whether the game keeps running when its window loses focus. ProjectSettings runInBackground is 0 for a release build (a player who alt-tabs must not burn the GPU and keep playing sound),
// but every proof / gate / capture run is driven from outside and must keep running in the background: those are recognised by the harness flags (MenuCfg.Harness) or by one of
// release.json backgroundArgs on the command line (-logFile, -out:, -fast, -flags: ... a name ending in ':' matches as a prefix). The harness classes also set runInBackground themselves; AutoShot (always present) used to force it on for everybody.
public static class ReleaseCfg
{
    [Serializable]
    public class Data
    {
        public bool keepRunningInBackground = false;   // true = never pause on focus loss (a dev machine switch)
        public string[] backgroundArgs = { "-logFile", "-out:", "-fast", "-flags:", "-autoplay" };
    }

    static Data data; static bool loaded;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; loaded = false; }

    public static Data Get()
    {
        if (loaded && data != null) return data;
        loaded = true; data = new Data();
        string txt = HudCfg.ReadData("release.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) { data = d; if (data.backgroundArgs == null) data.backgroundArgs = new string[0]; } }
            catch (Exception e) { Debug.LogWarning("[ReleaseCfg] release.json parse failed, defaults kept: " + e.Message); data = new Data(); }
        }
        return data;
    }

    /// <summary>The value for Application.runInBackground at start-up: true for harness / proof / gate runs, false for a normal player run (unless release.json keepRunningInBackground).</summary>
    public static bool RunInBackground()
    {
        var D = Get(); if (D.keepRunningInBackground) return true;
        if (Application.isBatchMode || MenuCfg.Harness) return true;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
            for (int k = 0; k < D.backgroundArgs.Length; k++)
            {
                string p = D.backgroundArgs[k]; if (string.IsNullOrEmpty(p)) continue;
                if (p[p.Length - 1] == ':' ? args[i].StartsWith(p, StringComparison.Ordinal) : args[i] == p) return true;
            }
        return false;
    }
}
