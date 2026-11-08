using System;
using System.IO;
using UnityEngine;

// Data layer for attack animation timing: Assets/Data/attack_clips.json (copy in StreamingAssets for the player).
// Lookup order: Resources/attack_clips, then (Editor) Assets/Data, StreamingAssets, Application.dataPath/Data (player: StreamingAssets first).
// A missing or unparsable file, or "enabled": false, leaves DuelFighter on the old behaviour (ClipFor + whole clip squashed into the move).
// All fields default to 0 / "" / false on purpose (JsonUtility array elements do not reliably run field initialisers); the shipped JSON states every field.
public static class AttackClipTable
{
    [Serializable]
    public class Row
    {
        public string character, move;          // character "" = any; move = MoveDef id
        public bool enabled;
        public string weapon;                   // SOBEKCLAWS: "" = any; set = only while this character's roster weapon is this one, and then it wins over the weapon-less row of the same character
        public string clip;                     // Animator state (new baked clip), seconds below are in that clip
        public float startTime, contactTime, endTime, holdClipTime;
        public string fallbackClip;             // old state used when `clip` is not in the controller
        public float fbStartTime, fbContactTime, fbEndTime, fbHoldClipTime;
        public float windupSpeed, recoverySpeed;   // > 0 overrides the automatic segment speed
        public float blendIn, blendOut;            // seconds; 0 = defaults
        public string rootMotion;                  // "inplace" | "limited" (decided at bake time, see Tools/bake_attack_clips.py)
        public float maxStep;                      // metres: cap on the code lunge (inplace) / on baked hips travel (limited)
        public bool useStepIn; public float stepIn;   // metres of code travel during the wind-up; false = the move's own stepIn
        public string source, note;
    }
    [Serializable]
    public class Defaults
    {
        public float contactActiveFrac, minWindupSpeed, maxWindupSpeed, maxRecoverySpeed, blendIn, blendOut, stepStopDist;
    }
    [Serializable]
    public class Data
    {
        public bool enabled;
        public bool debugLog;
        public Defaults defaults = new Defaults();
        public Row[] rows = new Row[0];
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    // exact character match wins over an "any" row
    public static Row Find(string character, string move)
    {
        var d = Get();
        if (!d.enabled || d.rows == null || string.IsNullOrEmpty(move)) return null;
        Row any = null, exact = null; string wk = ""; if (!string.IsNullOrEmpty(character)) { var rc = Roster.Get(character); if (rc != null && rc.weapon != null) wk = rc.weapon; } { string bowKey = BowHooks.WeaponKey(character); if (bowKey.Length > 0) wk = bowKey; } /* BOW v2 */   // SOBEKCLAWS
        for (int i = 0; i < d.rows.Length; i++)
        {
            var r = d.rows[i];
            if (r == null || !r.enabled || r.move != move) continue;
            if (!string.IsNullOrEmpty(r.weapon) && r.weapon != wk) continue;   // SOBEKCLAWS: a weapon row only applies to that weapon
            if (!string.IsNullOrEmpty(character) && r.character == character) { if (!string.IsNullOrEmpty(r.weapon)) return r; if (exact == null) exact = r; continue; }
            if (string.IsNullOrEmpty(r.character) || r.character == "*") any = r;
        }
        return exact ?? any;
    }

    static void Load()
    {
        data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("attack_clips");
            if (ta != null) json = ta.text;
#if UNITY_EDITOR
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/attack_clips.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "attack_clips.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/attack_clips.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("AttackClipTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("AttackClipTable: attack_clips.json not found; old clip squash in use."); return; }
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.defaults == null) d.defaults = new Defaults();
            if (d.rows == null) d.rows = new Row[0];
            // zero in the JSON = unset: fall back to the working values (the shipped file states them all)
            var df = d.defaults;
            if (df.contactActiveFrac <= 0f) df.contactActiveFrac = 0.15f;
            if (df.minWindupSpeed <= 0f) df.minWindupSpeed = 0.6f;
            if (df.maxWindupSpeed <= 0f) df.maxWindupSpeed = 2.6f;
            if (df.maxRecoverySpeed <= 0f) df.maxRecoverySpeed = 2.4f;
            if (df.blendIn <= 0f) df.blendIn = 0.06f;
            if (df.blendOut <= 0f) df.blendOut = 0.12f;
            data = d;
        }
        catch (Exception ex) { Debug.LogError("AttackClipTable: parse failed: " + ex.Message); data = new Data(); }
    }
}
