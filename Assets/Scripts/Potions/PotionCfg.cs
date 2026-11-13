using System;
using System.Globalization;
using UnityEngine;

// POTIONS v1 (lanePOTIONS): data for the potions / consumables. Assets/Data/consumables.json (copy in StreamingAssets, read first). Every field has a default, a missing or broken file = OFF.
// "enabled": false (the shipped value) = PotionUse / PotionHud are never created and PotionStore.IsStack is false, so the market, the save and the fight behave exactly as before.
// Effects (kind): "heal" and "stamina". Other kinds in the file (the poise tonic: "buff") are kept as data but are not stackable / not usable by this code.
public static class PotionCfg
{
    [Serializable]
    public class PotionC
    {
        public string id = "", name = "", kind = "", description = "", stat = "";
        public int healFlat = 0, buyValue = 0, maxCarry = 9;
        public float healFracOfMax = 0f, restoreFracOfMax = 0f, overSeconds = 0f, cooldownS = 8f, add = 0f, forSeconds = 0f;
    }
    [Serializable]
    public class RulesC
    {
        public bool refuseWhenFull = true, blockInFinisher = true, blockInMenus = true;
        public string[] blockStates = { "Dead", "Down", "Stagger", "Roll", "Windup", "Active" };   // FState names the player can not drink in
    }
    [Serializable] public class HudC { public bool enabled = true; public float leftPx = 36f, bottomPx = 170f, widthPx = 380f, barH = 3f, toastSeconds = 1.6f, toastGapPx = 6f, lineH = 1.5f, barFrac = 0.6f, barGapPx = 2f, toastFadeInS = 0.1f, toastFadeOutS = 0.4f; public int nameSize = 18, hintSize = 12, toastSize = 17; }
    [Serializable]
    public class TextC
    {
        public string chip = "{0}   {1}   x{2}", chipNone = "{0}   No potions", cycleHint = "{0} swap", tagQty = "x{0}", tagFull = "Full", healed = "+{0} health", stamina = "Stamina restored",
            bought = "Bought {0} for {1} silver.", sold = "Sold {0} for {1} silver.", refuseNone = "No potions left.", refuseCooldown = "Not ready yet.", refuseFull = "Already full.", refuseBusy = "Not now.",
            descHealFlat = "Restores {0} health", descHealFrac = "Restores {0}% of your health", descOver = " over {0} s", descStamina = "Restores {0}% of your stamina", descCooldown = "    Ready again after {0} s";
    }
    [Serializable] public class StartStackC { public string id = ""; public int qty; }   // STARTSTACKS v1: potions a NEW save starts with (consumables.json startStacks); empty = none (old behaviour)
    [Serializable]
    public class Data
    {
        public int version = 2; public bool enabled = false, debugLog = false;
        public string[] useKeys = { "H", "JoystickButton6" }, cycleKeys = { "J" };
        public RulesC rules = new RulesC(); public HudC hud = new HudC(); public TextC text = new TextC(); public PotionC[] potions = new PotionC[0]; public StartStackC[] startStacks = new StartStackC[0];
    }

    static Data data; static bool loaded; static KeyCode[] useK, cycleK; static FState[] blockSt;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; loaded = false; useK = cycleK = null; blockSt = null; }

    public static Data Get()
    {
        if (loaded && data != null) return data;
        loaded = true; data = new Data(); useK = cycleK = null; blockSt = null;
        string txt = MarketCfg.ReadData("consumables.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) data = d; }
            catch (Exception e) { Debug.LogWarning("[Potions] consumables.json parse failed, potions off: " + e.Message); data = new Data(); }
        }
        if (data.rules == null) data.rules = new RulesC(); if (data.hud == null) data.hud = new HudC(); if (data.text == null) data.text = new TextC(); if (data.potions == null) data.potions = new PotionC[0];
        if (data.useKeys == null) data.useKeys = new string[0]; if (data.cycleKeys == null) data.cycleKeys = new string[0]; if (data.rules.blockStates == null) data.rules.blockStates = new string[0];
        for (int i = 0; i < data.potions.Length; i++) if (data.potions[i] == null) data.potions[i] = new PotionC();
        return data;
    }
    public static void Reload() { loaded = false; }
    public static bool On { get { return Get().enabled; } }

    /// <summary>The row of an id (null when unknown). Does not look at the master switch.</summary>
    public static PotionC Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null; var p = Get().potions;
        for (int i = 0; i < p.Length; i++) if (p[i].id == id) return p[i];
        return null;
    }
    /// <summary>Only the effects this code implements are stackable: heal and stamina.</summary>
    public static bool Implemented(PotionC p) { return p != null && p.id.Length > 0 && (p.kind == "heal" || p.kind == "stamina"); }

    public static KeyCode[] UseKeys() { if (useK == null) useK = MarketCfg.Keys(Get().useKeys); return useK; }
    public static KeyCode[] CycleKeys() { if (cycleK == null) cycleK = MarketCfg.Keys(Get().cycleKeys); return cycleK; }
    public static bool Blocks(FState s)
    {
        if (blockSt == null)
        {
            var l = new System.Collections.Generic.List<FState>(); var n = Get().rules.blockStates;
            for (int i = 0; i < n.Length; i++) { FState st; if (Enum.TryParse(n[i], true, out st)) l.Add(st); }
            blockSt = l.ToArray();
        }
        for (int i = 0; i < blockSt.Length; i++) if (blockSt[i] == s) return true;
        return false;
    }

    static string N(float v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
    /// <summary>One line for the market detail strip, built from the row and the text templates.</summary>
    public static string Describe(string id)
    {
        var p = Find(id); if (p == null) return ""; var T = Get().text;
        if (p.description.Length > 0) return p.description;
        string s = "";
        if (p.kind == "heal") { s = p.healFracOfMax > 0f ? string.Format(CultureInfo.InvariantCulture, T.descHealFrac, N(p.healFracOfMax * 100f)) : string.Format(CultureInfo.InvariantCulture, T.descHealFlat, p.healFlat); }
        else if (p.kind == "stamina") s = string.Format(CultureInfo.InvariantCulture, T.descStamina, N(p.restoreFracOfMax * 100f));
        if (p.overSeconds > 0f) s += string.Format(CultureInfo.InvariantCulture, T.descOver, N(p.overSeconds));
        return s + string.Format(CultureInfo.InvariantCulture, T.descCooldown, N(p.cooldownS));
    }
    /// <summary>Market note line for a stack trade ("boughtStack" / "soldStack"): item name + silver.</summary>
    public static string NoteText(string key, string item, int silver)
    {
        var T = Get().text; return string.Format(CultureInfo.InvariantCulture, key == "boughtStack" ? T.bought : T.sold, item, silver);
    }
}
