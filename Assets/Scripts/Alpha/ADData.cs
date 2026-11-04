using System;
using System.IO;
using UnityEngine;

// Loader for Assets/Data/duel_rules.json. Order (same as Combat\DodgeTable.cs): Resources/duel_rules, StreamingAssets/duel_rules.json, Application.dataPath/Data/duel_rules.json.
// Lanes B-E: ADJson.Block(ADData.LoadText(), "wounds") gives their block's text for JsonUtility.FromJson<TheirClass>.
public static class ADData
{
    public const string File = "duel_rules.json";
    static string cached;

    public static string LoadText(bool force = false)
    {
        if (cached != null && !force) return cached;
        string t = null;
        var ta = Resources.Load<TextAsset>("duel_rules");
        if (ta != null) t = ta.text;
        if (t == null) { string p = Application.streamingAssetsPath + "/" + File; if (System.IO.File.Exists(p)) t = System.IO.File.ReadAllText(p); }
        if (t == null) { string p = Application.dataPath + "/Data/" + File; if (System.IO.File.Exists(p)) t = System.IO.File.ReadAllText(p); }
        if (t == null) Debug.LogError("ADData: duel_rules.json not found (Resources, StreamingAssets, Assets/Data)");
        cached = t;
        return t;
    }

    // Fresh object every call (the self-test mutates it).
    public static ADRulesData LoadRules()
    {
        string t = LoadText();
        if (t == null) return null;
        return JsonUtility.FromJson<ADRulesData>(t);
    }
}
