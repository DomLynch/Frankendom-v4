using System;
using System.IO;
using UnityEngine;

// STALL v1 (lane laneSTALL): data for the camp trader stall. Assets/Data/market.json (copy in StreamingAssets, read first). Every field has a default so a missing file keeps the stall OFF;
// "enabled": false (the shipped value) switches the whole feature off: no prompt, no panel, no combat rule.
// Sizes are px at the HUD reference height (hud.json) and scale with HudCfg.Scale() (the HUD's UI scale). Colours not listed here come from hud.json.
public static class MarketCfg
{
    [Serializable] public class CurrencyC { public string id = "coins", name = "Coins"; public int cap = 999999, startCoins = 0; }   // INT108 v1: coins of a new save (placeholder in market.json)
    [Serializable] public class RulesC { public float sellRatio = 0.33f; public int minSell = 1, keepRequests = 200; public string[] ops = { "sell" }; public bool buyEnabled = false, confirmSell = true, listEquipped = true; }
    [Serializable] public class RowC { public string item = ""; public int buyValue = 0; public bool sellable = true; public int sellOverride = 0; }
    [Serializable] public class SinkC { public string id = "", kind = "", needs = ""; public bool enabled = false; }
    [Serializable] public class AnchorC { public string zone = "", spot = ""; public float x = 0f, z = 0f; }
    [Serializable] public class AreaC { public float zMin = 0f, zMax = 0f, xHalf = 8f, enterPad = 0.5f, leavePad = 0.5f; }
    [Serializable] public class StallC { public string id = "", title = "Trader"; public AnchorC anchor = new AnchorC(); public float radius = 2.4f, promptRadius = 5f, promptHeight = 2.1f; public AreaC area = new AreaC(); }
    [Serializable] public class SafeC { public bool noCombat = true; public string[] allowPress = { "roll", "backstep", "sidestep" }; public float refuseSeconds = 1.4f, tagTop = 26f, coinDy = 8f, tagTracking = 3.2f; public int tagBig = 20, tagSmall = 15, subSize = 13, coinSize = 17; }
    [Serializable]
    public class KeysC
    {
        public string[] interact = { "E", "JoystickButton2" }, confirm = { "E", "JoystickButton0", "Return" }, close = { "Escape", "JoystickButton1", "Tab", "JoystickButton7" }, up = { "UpArrow", "W" }, down = { "DownArrow", "S" };
        public string axisY = "Vertical"; public float stickThreshold = 0.6f, repeatDelayS = 0.35f, repeatS = 0.11f;
    }
    [Serializable] public class ColorsC { public float[] panel = { 0.018f, 0.018f, 0.022f, 0.95f }, rowSelected = { 0.11f, 0.075f, 0.075f, 0.95f }, rowArmed = { 0.20f, 0.10f, 0.07f, 0.97f }, dimmer = { 0f, 0f, 0f, 0.58f }, good = { 0.45f, 0.78f, 0.38f, 1f }, bad = { 0.82f, 0.22f, 0.20f, 1f }; }
    [Serializable]
    public class LayoutC
    {
        public float panelW = 880f, panelH = 660f, margin = 24f, pad = 26f, headerH = 98f, footerH = 34f, detailH = 92f, rowH = 50f, rowGap = 5f, rowPadX = 16f, frameW = 2f, accentBar = 3f, ruleH = 2f, diamond = 9f;
        public int titleBig = 34, titleSmall = 26; public float titleTracking = 3.4f;
        public int subSize = 14, coinSize = 22, rowNameSize = 20, rowPriceSize = 20, rowTagSize = 12, detailNameSize = 20, detailSize = 15, hintSize = 14, messageSize = 17;
        public int labelNameBig = 22, labelNameSmall = 17; public float labelTracking = 3f; public int promptSize = 15; public float promptGap = 8f, toastBottom = 150f; public int toastSize = 22; public float openFadeS = 0.16f;
    }
    [Serializable] public class CodeC { public string code = "", text = ""; }
    [Serializable]
    public class TextC
    {
        public string title = "Trader", subtitle = "Sell what you carry. What you wear is kept.", coins = "{0} coins", price = "{0}", worn = "Worn", nothing = "You carry nothing the trader will take.", prompt = "Sell gear", promptKeys = "E / X",
            hintMove = "Up / Down  Select", hintSell = "E / A  Sell", hintClose = "Esc / B  Close", safeTag = "SAFE", safeSub = "No fighting in the camp.", noFight = "No fighting in the camp.", sold = "Sold {0} for {1}.", armed = "Press again to sell {0} for {1}.",
            detailPays = "The trader pays {0}", detailWorn = "Worn: take it off to sell it";
        public CodeC[] codes = new CodeC[0];
    }
    [Serializable]
    public class Data
    {
        public int version = 1; public bool enabled = false, debugLog = false; public int guiDepth = -1090;
        public CurrencyC currency = new CurrencyC(); public RulesC rules = new RulesC(); public RowC[] rows = new RowC[0]; public SinkC[] sinks = new SinkC[0]; public StallC stall = new StallC(); public SafeC safe = new SafeC();
        public KeysC keys = new KeysC(); public ColorsC colors = new ColorsC(); public LayoutC layout = new LayoutC(); public TextC text = new TextC();
    }

    static Data data; static bool loaded;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; loaded = false; rules = null; rows = null; }

    public static Data Get()
    {
        if (loaded && data != null) return data;
        loaded = true; rules = null; rows = null; data = new Data();
        string txt = ReadData("market.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) data = d; }
            catch (Exception e) { Debug.LogWarning("[Market] market.json parse failed, stall off: " + e.Message); data = new Data(); }
        }
        Fix(data); return data;
    }
    public static void Reload() { loaded = false; }
    public static bool On { get { return Get().enabled; } }

    static void Fix(Data d)
    {
        if (d.currency == null) d.currency = new CurrencyC(); if (d.rules == null) d.rules = new RulesC(); if (d.rows == null) d.rows = new RowC[0]; if (d.sinks == null) d.sinks = new SinkC[0];
        if (d.stall == null) d.stall = new StallC(); if (d.stall.anchor == null) d.stall.anchor = new AnchorC(); if (d.stall.area == null) d.stall.area = new AreaC(); if (d.safe == null) d.safe = new SafeC();
        if (d.keys == null) d.keys = new KeysC(); if (d.colors == null) d.colors = new ColorsC(); if (d.layout == null) d.layout = new LayoutC(); if (d.text == null) d.text = new TextC();
        if (d.text.codes == null) d.text.codes = new CodeC[0]; if (d.rules.ops == null) d.rules.ops = new string[0]; if (d.safe.allowPress == null) d.safe.allowPress = new string[0];
        d.currency.cap = Mathf.Max(1, d.currency.cap); d.currency.startCoins = Mathf.Clamp(d.currency.startCoins, 0, d.currency.cap);
        if (d.rows.Length > 0) d.enabled = ZoneFlag.Allowed(d.enabled);   // INT108 v1: -zones turns the trader on, -nozones off, no flag = the file d.rules.keepRequests = Mathf.Max(1, d.rules.keepRequests); d.rules.minSell = Mathf.Max(1, d.rules.minSell);
    }

    // ---- the pure rule objects MarketCore.cs works with ----
    static MarketRules rules; static System.Collections.Generic.List<MarketRow> rows;
    public static MarketRules Rules()
    {
        var d = Get(); if (rules != null) return rules;
        rules = new MarketRules { sellRatio = d.rules.sellRatio, minSell = d.rules.minSell, walletCap = d.currency.cap, keepRequests = d.rules.keepRequests, ops = d.rules.ops, buyEnabled = d.rules.buyEnabled, confirmSell = d.rules.confirmSell, listEquipped = d.rules.listEquipped };
        return rules;
    }
    public static System.Collections.Generic.IList<MarketRow> Rows()
    {
        var d = Get(); if (rows != null) return rows;
        rows = new System.Collections.Generic.List<MarketRow>();
        for (int i = 0; i < d.rows.Length; i++) { var r = d.rows[i]; if (r == null || string.IsNullOrEmpty(r.item)) continue; rows.Add(new MarketRow { item = r.item, buyValue = r.buyValue, sellable = r.sellable, sellOverride = r.sellOverride }); }
        return rows;
    }
    public static string Code(string code)
    {
        var c = Get().text.codes; for (int i = 0; i < c.Length; i++) if (c[i] != null && c[i].code == code) return c[i].text;
        return code;
    }
    public static bool AllowsPress(string what) { var a = Get().safe.allowPress; for (int i = 0; i < a.Length; i++) if (a[i] == what) return true; return false; }

    // ---- io (same lookup as LootCfg / HudCfg) ----
    public static string ReadData(string name)
    {
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, name), Path.Combine(Application.dataPath, "Data/" + name) })
        {
            try { if (File.Exists(p)) return File.ReadAllText(p).TrimStart((char)0xFEFF); } catch { }
        }
        return null;
    }
    public static KeyCode[] Keys(string[] names)
    {
        var list = new System.Collections.Generic.List<KeyCode>();
        if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (!string.IsNullOrEmpty(names[i]) && Enum.TryParse(names[i], true, out k)) list.Add(k); }
        return list.ToArray();
    }
    public static bool AnyDown(KeyCode[] ks) { if (ks != null) for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }
    public static Color Col(float[] a, float alpha = -1f) { if (a == null || a.Length < 3) return Color.white; return new Color(a[0], a[1], a[2], alpha >= 0f ? alpha : (a.Length > 3 ? a[3] : 1f)); }
}
