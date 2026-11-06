using System;
using System.IO;
using UnityEngine;

// DEATHFIX v92: data layer for the plain death (DuelFighter.Die): Assets/Data/death.json (copy in StreamingAssets for the player).
// Lookup order: Resources/death, (Editor) Assets/Data, StreamingAssets, Application.dataPath/Data. A missing / unparsable file or "enabled": false = the old behaviour
// (clip "death" from 0 at speed 1, 0.08 s fade, no zero-HP safety net). Rows are keyed by roster id (Roster.IdOf); character "" = the default row. Every field is stated in the shipped JSON.
public static class DeathTable
{
    [Serializable]
    public class Row
    {
        public string character;                 // roster id, "" = any
        public string clip;                      // Animator state of the death clip ("" / missing in the controller = "death")
        public float startSeconds, speed, fade;  // start second inside the clip (skips the upright lead-in), Animator speed multiplier, crossfade seconds
        public bool downEnabled;                 // killed while knocked down (FState.Down): start at the floor pose instead
        public float downStartSeconds, downFade;
        public string note;
    }
    [Serializable]
    public class Data
    {
        public bool enabled;
        public bool enforceZeroHp;               // Duel.Update: a fighter at 0 HP that is not Dead is killed (finisher scenes excluded)
        public bool debugLog;
        public Row[] rows = new Row[0];
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    // exact character match wins over the default row; null = table off (caller keeps the old behaviour)
    public static Row Find(string character)
    {
        var d = Get();
        if (!d.enabled || d.rows == null) return null;
        Row any = null;
        for (int i = 0; i < d.rows.Length; i++)
        {
            var r = d.rows[i]; if (r == null) continue;
            if (!string.IsNullOrEmpty(character) && r.character == character) return r;
            if (string.IsNullOrEmpty(r.character)) any = r;
        }
        return any;
    }

    static void Load()
    {
        data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("death");
            if (ta != null) json = ta.text;
#if UNITY_EDITOR
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/death.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "death.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/death.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("DeathTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("DeathTable: death.json not found; old plain death in use."); return; }
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.rows == null) d.rows = new Row[0];
            data = d;
        }
        catch (Exception ex) { Debug.LogError("DeathTable: parse failed: " + ex.Message); data = new Data(); }
    }
}
