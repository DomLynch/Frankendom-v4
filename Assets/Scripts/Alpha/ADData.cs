using System;
using System.IO;
using UnityEngine;

// Loader for Assets/Data/duel_rules.json. Order (same as Combat\DodgeTable.cs): Resources/duel_rules, StreamingAssets/duel_rules.json, Application.dataPath/Data/duel_rules.json.
// Lanes B-E: ADJson.Block(ADData.LoadText(), "wounds") gives their block's text for JsonUtility.FromJson<TheirClass>.
// A2: LoadRules never returns null and never throws: a missing / broken file or block falls back to code defaults (ADRulesData.Fill) and logs one line.
public static class ADData
{
    public const string File = "duel_rules.json";
    static string cached;

    public static string LoadText(bool force = false)
    {
        if (cached != null && !force) return cached;
        string t = null;
        try
        {
            var ta = Resources.Load<TextAsset>("duel_rules");
            if (ta != null) t = ta.text;
            if (t == null) { string p = Application.streamingAssetsPath + "/" + File; if (System.IO.File.Exists(p)) t = System.IO.File.ReadAllText(p); }
            if (t == null) { string p = Application.dataPath + "/Data/" + File; if (System.IO.File.Exists(p)) t = System.IO.File.ReadAllText(p); }
        }
        catch (Exception e) { Debug.LogError("ADData: read failed: " + e.Message); }
        if (t == null) Debug.LogError("ADData: duel_rules.json not found (Resources, StreamingAssets, Assets/Data): code defaults are used");
        else cached = t;
        return t;
    }

    // Fresh object every call (the self-test mutates it). Never null.
    public static ADRulesData LoadRules()
    {
        ADRulesData d = null;
        string t = LoadText();
        if (t != null)
        {
            try { d = JsonUtility.FromJson<ADRulesData>(t); }
            catch (Exception e) { Debug.LogError("ADData: duel_rules.json parse failed (" + e.Message + "): code defaults are used"); }
        }
        if (d == null) d = new ADRulesData();
        d.Fill();
        return d;
    }
}

// Read-only feature flags for every Alpha script. Safe before the rules exist (falls back to the file, then to false).
public static class ADFlags
{
    static ADRulesCfg fallback;
    static ADRulesCfg Cfg
    {
        get
        {
            try { if (ADDuelController.Rules != null && ADDuelController.Rules.Data != null && ADDuelController.Rules.Data.rules != null) return ADDuelController.Rules.Data.rules; } catch { }
            if (fallback == null) { try { fallback = ADData.LoadRules().rules; } catch { } }
            return fallback ?? new ADRulesCfg();
        }
    }
    public static bool Wounds { get { return Cfg.enableWounds; } }
    public static bool Gore { get { return Cfg.enableGore; } }
    public static bool CritCam { get { return Cfg.enableCritCam; } }
    public static bool Specials { get { return Cfg.enableSpecials; } }
    public static bool Finishers { get { return Cfg.enableFinishers; } }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { fallback = null; }
}
