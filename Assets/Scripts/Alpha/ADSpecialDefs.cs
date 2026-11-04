using System;
using System.IO;
using UnityEngine;

// Alpha step 1, lane E: data-driven definitions of the two SPECIAL moves (one use per fight per fighter) and the loader for the
// "ai" and "special" blocks of Assets/Data/duel_rules.json. Lookup order like Combat\DodgeTable.cs: Resources/duel_rules,
// StreamingAssets/duel_rules.json, Application.dataPath/Data/duel_rules.json. Missing blocks keep empty/default values.
// Lane A (rules) reads: damage, tier, healFraction, knockdown (already in ADTypes) plus unblockable, guardMode, guardReduce, avoidedBy, critAllowed,
// hitLocation, knockdown, area, usesPerFight. Lane C (playback) reads: windupClip, strikeClip, victimClip, getUpClip, cue, cameraHint, slowMo.
[Serializable]
public class ADSpecialRow
{
    public string id = "";                  // "blood_drain" | "ground_slam"
    public string side = "";                // "Dracula" | "Pitborn"
    public int usesPerFight = 1;
    // rules
    public float damage = 20f;              // base damage (same field name as lane A's ADSpecialDef)
    public float tier = 5f;                 // speed tier number as in rules.moves (higher = slower; Heavy is 5)
    public float healFraction = 0f;         // attacker heals this fraction of damage dealt (blood drain)
    public bool unblockable = false;        // true: guard does not stop it (see guardMode)
    public string guardMode = "reduced";    // "full" (guard blocks), "reduced" (guard cuts damage by guardReduce), "none" (ignores guard)
    public float guardReduce = 0.5f;        // damage kept fraction when guardMode == "reduced" (0.5 = half damage)
    public string[] avoidedBy = new string[0]; // moves that dodge it completely, e.g. ["Roll"]
    public string speedTierName = "normal"; // informational: "fast" | "normal" | "slow" (the number in tier decides)
    public bool critAllowed = true;
    public string hitLocation = "any";      // "any" (wound table rolls) or a fixed ADLoc name: Arm | Leg | Torso | Head
    public bool knockdown = false;          // (lane A field) victim ends the beat on the ground (loses the next beat's first action unless data says otherwise)
    public bool area = false;               // wide: also catches a Sidestep (but not what avoidedBy lists)
    public int staggerBeats = 0;            // beats of stagger on a hit (knockdown overrides)
    // playback (Animator state names in Dracula.controller / Pitborn.controller)
    public string windupClip = "";
    public string strikeClip = "";
    public string victimClip = "";          // victim hit-react state; "" = default flinch
    public string getUpClip = "get_up";
    public string cue = "";                 // CueBus / audio action name
    public string cameraHint = "";          // "closeup" | "wide_low" ... free text for lane C
    public float slowMo = 0f;               // 0..1 timescale dip at the strike (0 = none), 0.25 = quarter speed
    public float slowMoSeconds = 0f;
}

[Serializable]
public class ADSpecialBlock
{
    public ADSpecialRow dracula = new ADSpecialRow();   // same keys as lane A's ADSpecialCfg
    public ADSpecialRow pitborn = new ADSpecialRow();
}

[Serializable]
public class ADRulesAiSpecialFile
{
    public ADAiBlock ai;
    public ADSpecialBlock special;
}

public static class ADSpecialDefs
{
    static ADRulesAiSpecialFile cache;
    public const string FileName = "duel_rules.json";

    public static ADRulesAiSpecialFile Load(bool force = false)
    {
        if (cache != null && !force) return cache;
        string json = null;
        try
        {
            TextAsset ta = Resources.Load<TextAsset>("duel_rules");
            if (ta != null) json = ta.text;
            if (string.IsNullOrEmpty(json))
            {
                string p = Path.Combine(Application.streamingAssetsPath, FileName);
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (string.IsNullOrEmpty(json))
            {
                string p = Path.Combine(Application.dataPath, "Data/" + FileName);
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
        }
        catch (Exception e) { Debug.LogWarning("[ADSpecialDefs] read failed: " + e.Message); }
        ADRulesAiSpecialFile f = null;
        if (!string.IsNullOrEmpty(json))
        {
            try { f = JsonUtility.FromJson<ADRulesAiSpecialFile>(json); }
            catch (Exception e) { Debug.LogWarning("[ADSpecialDefs] parse failed: " + e.Message); }
        }
        if (f == null) f = new ADRulesAiSpecialFile();
        if (f.ai == null) f.ai = new ADAiBlock();
        if (f.special == null) f.special = new ADSpecialBlock();
        cache = f;
        return f;
    }

    public static ADAiBlock Ai { get { return Load().ai; } }

    // side: "Dracula" or "Pitborn" (ADSide.ToString()). Returns the dracula/pitborn row (never null once loaded).
    public static ADSpecialRow For(string side)
    {
        ADSpecialBlock b = Load().special;
        if (string.Equals(side, "Dracula", StringComparison.OrdinalIgnoreCase)) return b.dracula;
        if (string.Equals(side, "Pitborn", StringComparison.OrdinalIgnoreCase)) return b.pitborn;
        return null;
    }
}

