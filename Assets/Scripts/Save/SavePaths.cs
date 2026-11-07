using System;
using System.IO;
using UnityEngine;

// SAVEDIR v1: where the game's save files live. DATA SAFETY (Dom): harness runs used to rewrite the player's loot_save.json / zones_save.json / save_slot1.json.
// Redirect ONLY on an explicit "-savePath <dir>" (or "-savePath:<dir>"), or on the harness flags -balanceauto / -perfbench / -physproof / -lumatest
// (those fall back to <persistentDataPath>/harness_save). NOT on Application.isBatchMode. A normal launch returns persistentDataPath unchanged.
public static class SavePaths
{
    static string dir; static bool done;
    public static string Dir
    {
        get
        {
            if (done) return dir;
            string d = null;
            try
            {
                var a = Environment.GetCommandLineArgs();
                bool harness = false;
                for (int i = 0; i < a.Length; i++)
                {
                    string s = a[i];
                    if (string.Equals(s, "-savePath", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length) d = a[i + 1];
                    else if (s.StartsWith("-savePath:", StringComparison.OrdinalIgnoreCase)) d = s.Substring(10);
                    else if (s == "-balanceauto" || s == "-perfbench" || s.StartsWith("-physproof", StringComparison.Ordinal) || s.StartsWith("-lumatest", StringComparison.Ordinal)) harness = true;
                }
                if (string.IsNullOrEmpty(d) && harness) d = Path.Combine(Application.persistentDataPath, "harness_save");
                if (!string.IsNullOrEmpty(d)) { Directory.CreateDirectory(d); Debug.Log("[SavePaths] save dir redirected to " + d); }
            }
            catch (Exception e) { Debug.LogWarning("[SavePaths] " + e.Message); d = null; }
            dir = string.IsNullOrEmpty(d) ? Application.persistentDataPath : d; done = true; return dir;
        }
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { done = false; dir = null; }
}
