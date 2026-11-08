using System.IO;
using UnityEngine;

// HCKIT2 v93: the ONE rule for "where does a data json live". Editor: Assets/Data first (the file Dom edits), then StreamingAssets.
// Player: StreamingAssets first (the shipped copy), then <exe>_Data/Data as a last resort. tools/data_gates.py gate A keeps the two copies byte-equal,
// so the order only matters while a copy is stale. Loaders call DataPath.ReadText("x.json") or iterate DataPath.Candidates("x.json").
public static class DataPath
{
    public static string[] Candidates(string file)
    {
        string sa = Path.Combine(Application.streamingAssetsPath, file), dt = Path.Combine(Application.dataPath, "Data/" + file);
#if UNITY_EDITOR
        return new[] { dt, sa };
#else
        return new[] { sa, dt };
#endif
    }
    /// <summary>FIX2 v1: drop-in for File.ReadAllText(path) in the data loaders. Returns the same text and throws the SAME exception (every caller's catch / default path behaves exactly as before), but logs the path + exception once per file (SafeFile.WarnOnce) so a locked / unreadable json no longer vanishes into an empty catch.</summary>
    public static string ReadFile(string path) { try { return File.ReadAllText(path); } catch (System.Exception e) { SafeFile.WarnOnce("readfile:" + path, "[DataPath] " + path + " could not be read (" + e.GetType().Name + ": " + e.Message + "): the loader falls back to its defaults"); throw; } }
    public static string Find(string file) { foreach (var p in Candidates(file)) if (File.Exists(p)) return p; return null; }
    public static string ReadText(string file) { string p = Find(file); if (p == null) return null; try { return File.ReadAllText(p); } catch (System.Exception e) { SafeFile.WarnOnce("read:" + p, "[DataPath] " + file + " exists but could not be read (" + e.GetType().Name + ": " + e.Message + "): callers fall back to their defaults. " + p); return null; } }   // FIXSAVE v1: was a silent catch
}
