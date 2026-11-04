using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// The finisher rotation survives a restart of the game, PER WEAPON (V62). Application.persistentDataPath\finisher_rotation.json (StreamingAssets is read-only in a build):
//   { "version": 2, "savedAt": "...", "rows": [ { "weapon": "sword", "lastIndex": 3, "kills": 5, "lastId": "river_light" }, { "weapon": "maul", ... } ] }
// A row is written whenever the rotation picks an entry for that weapon; every row is read once on the first pick of a run. "-finisherreset" on the command line deletes the file first.
// FinisherAuto sets Suspended so the verify runs never touch the player's rotation; FinisherRotationCheck uses TestMode + PathOverride (a temp file) to exercise exactly this code.
public static class FinisherRotationStore
{
    [Serializable] public class Row { public string weapon = ""; public int lastIndex = -1; public int kills; public string lastId = ""; }
    [Serializable] class FileRows { public int version = 2; public string savedAt = ""; public List<Row> rows = new List<Row>(); }

    public static bool Suspended;
    public static bool TestMode;                 // FinisherRotationCheck: bypass Suspended and rt.persistRotation
    public static string PathOverride;           // FinisherRotationCheck: temp file instead of persistentDataPath
    static bool Off { get { return !TestMode && (Suspended || !RTFinisher.Cfg.persistRotation); } }
    static bool resetChecked;
    static readonly Dictionary<string, Row> cache = new Dictionary<string, Row>();
    static bool cacheLoaded;

    public static string FilePath { get { try { return !string.IsNullOrEmpty(PathOverride) ? PathOverride : Path.Combine(Application.persistentDataPath, "finisher_rotation.json"); } catch { return null; } } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Suspended = false; TestMode = false; PathOverride = null; resetChecked = false; cache.Clear(); cacheLoaded = false; }

    /// <summary>Forget the in-memory copy so the next Load reads the file again (what a game restart does).</summary>
    public static void DropCache() { cache.Clear(); cacheLoaded = false; }

    static void CheckResetFlag()
    {
        if (resetChecked) return; resetChecked = true;
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-finisherreset") >= 0) Clear();
    }

    static void LoadAll()
    {
        if (cacheLoaded) return; cacheLoaded = true; cache.Clear();
        try
        {
            CheckResetFlag();
            string p = FilePath; if (p == null || !File.Exists(p)) return;
            var f = JsonUtility.FromJson<FileRows>(File.ReadAllText(p)); if (f == null || f.rows == null) return;
            foreach (var r in f.rows) if (r != null && !string.IsNullOrEmpty(r.weapon)) cache[r.weapon] = r;
        }
        catch (Exception e) { Debug.LogWarning("[finisher] rotation file unreadable: " + e.Message); cache.Clear(); }
    }

    public static void Load(string weapon, out int lastIndex, out int kills, out string lastId)
    {
        lastIndex = -1; kills = 0; lastId = null;
        if (Off || string.IsNullOrEmpty(weapon)) return;
        LoadAll();
        Row r; if (!cache.TryGetValue(weapon, out r)) return;
        lastIndex = r.lastIndex; kills = Mathf.Max(0, r.kills); lastId = string.IsNullOrEmpty(r.lastId) ? null : r.lastId;
    }

    public static void Save(string weapon, int lastIndex, int kills, string lastId)
    {
        if (Off || string.IsNullOrEmpty(weapon)) return;
        try
        {
            LoadAll();
            cache[weapon] = new Row { weapon = weapon, lastIndex = lastIndex, kills = kills, lastId = lastId ?? "" };
            string p = FilePath; if (p == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var f = new FileRows { savedAt = DateTime.Now.ToString("s") }; foreach (var r in cache.Values) f.rows.Add(r);
            File.WriteAllText(p, JsonUtility.ToJson(f, true));
        }
        catch (Exception e) { Debug.LogWarning("[finisher] rotation file not written: " + e.Message); }
    }

    public static void Clear()
    {
        cache.Clear(); cacheLoaded = false;
        try { string p = FilePath; if (p != null && File.Exists(p)) File.Delete(p); } catch (Exception e) { Debug.LogWarning("[finisher] rotation file not deleted: " + e.Message); }
    }
}
