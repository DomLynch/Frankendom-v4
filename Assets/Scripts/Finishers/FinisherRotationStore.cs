using System;
using System.IO;
using UnityEngine;

// The finisher rotation index survives a restart of the game. Application.persistentDataPath\finisher_rotation.json (StreamingAssets is read-only in a build).
// Written whenever the rotation picks an entry; read once on the first pick of a run. "-finisherreset" on the command line deletes it first.
// FinisherAuto sets Suspended so the verify runs never touch the player's rotation.
public static class FinisherRotationStore
{
    [Serializable] class Row { public int version = 1; public int lastIndex = -1; public int kills; public string lastId = ""; public string savedAt = ""; }

    public static bool Suspended;
    static bool Off { get { return Suspended || !RTFinisher.Cfg.persistRotation; } }
    static bool resetChecked;

    public static string FilePath { get { try { return Path.Combine(Application.persistentDataPath, "finisher_rotation.json"); } catch { return null; } } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Suspended = false; resetChecked = false; }

    static void CheckResetFlag()
    {
        if (resetChecked) return; resetChecked = true;
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-finisherreset") >= 0) Clear();
    }

    public static void Load(out int lastIndex, out int kills, out string lastId)
    {
        lastIndex = -1; kills = 0; lastId = null;
        if (Off) return;
        try
        {
            CheckResetFlag();
            string p = FilePath; if (p == null || !File.Exists(p)) return;
            var r = JsonUtility.FromJson<Row>(File.ReadAllText(p)); if (r == null) return;
            lastIndex = r.lastIndex; kills = Mathf.Max(0, r.kills); lastId = string.IsNullOrEmpty(r.lastId) ? null : r.lastId;
        }
        catch (Exception e) { Debug.LogWarning("[finisher] rotation file unreadable: " + e.Message); lastIndex = -1; kills = 0; lastId = null; }
    }

    public static void Save(int lastIndex, int kills, string lastId)
    {
        if (Off) return;
        try
        {
            string p = FilePath; if (p == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, JsonUtility.ToJson(new Row { lastIndex = lastIndex, kills = kills, lastId = lastId ?? "", savedAt = DateTime.Now.ToString("s") }, true));
        }
        catch (Exception e) { Debug.LogWarning("[finisher] rotation file not written: " + e.Message); }
    }

    public static void Clear()
    {
        try { string p = FilePath; if (p != null && File.Exists(p)) File.Delete(p); } catch (Exception e) { Debug.LogWarning("[finisher] rotation file not deleted: " + e.Message); }
    }
}
