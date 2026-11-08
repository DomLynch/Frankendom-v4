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
    public static string Find(string file) { foreach (var p in Candidates(file)) if (File.Exists(p)) return p; return null; }
    public static string ReadText(string file) { string p = Find(file); if (p == null) return null; try { return File.ReadAllText(p); } catch (System.Exception e) { SafeFile.WarnOnce("read:" + p, "[DataPath] " + file + " exists but could not be read (" + e.GetType().Name + ": " + e.Message + "): callers fall back to their defaults. " + p); return null; } }   // FIXSAVE v1: was a silent catch
}
