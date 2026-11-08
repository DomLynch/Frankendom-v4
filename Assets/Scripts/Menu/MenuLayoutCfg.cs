using System;
using System.Collections.Generic;
using UnityEngine;

// UIDATAB v1 (laneUIDATA_B): the layout / style numbers of the pause + settings menu, the loot pickup labels + equip screen, the stall (market) UI and the boss-silver toast, moved out of the C# files.
// Data: menu_layout.json (Assets/Data, same bytes in StreamingAssets, read through DataPath). EVERY default below is EXACTLY the literal that was in the code, so a missing file / key = the old look, bit for bit.
// Loaded once (cached); read through the small ML properties of each UI class. Groups: menu (MenuUI + MenuControls), lootWorld (LootUI), lootScreen (LootScreen + LootDraw), market (MarketUI), bossSilver (toast depth).
// Not here on purpose: MenuCfg (already the menu layout/strings), HudCfg (shared), LootCfg / MarketCfg / PotionCfg / boss_silver.json (already data), and math constants (0.5f centring, 2f doubling, 0f/1f).
public static class MenuLayoutCfg
{
    [Serializable] public class MenuC_settings
    {
        public float titlePadK = 0.7f;
        public float capsLine = 1.45f;
        public float tabTopGap = 6f;
        public float tabsRowK = 1.7f;
        public float footerPadK = 0.6f;
        public float footerBoxK = 1.6f;
        public float hintLineK = 1.45f;
        public float headRowK = 0.9f;
        public float listTopGap = 14f;
        public float tabPadX = 8f;
        public float tabExtraW = 16f;
        public float sliderHitPad = 6f;
        public float sliderHitExtra = 12f;
        public float footerAlphaK = 0.9f;
        public float frameInnerK = 0.7f;
        public float diamondExtra = 3f;
        public float ruleDiamondExtra = 2f;
        public float tabIdleAlpha = 0.6f;
        public float tabLabelDy = 2f;
        public float tabUnderOff = 3f;
        public float tabUnderH = 2f;
        public float tabHintTrim = 4f;
        public string tabHintSep = "   |   ";
        public float tabRuleH = 1f;
        public float scrollGap = 8f;
        public float trackAlpha = 0.5f;
        public float thumbMin = 24f;
        public float thumbAlpha = 0.8f;
        public string emptyText = "-";
        public float hintShadowOff = 1f;
    }
    [Serializable] public class MenuC_confirm
    {
        public float optWK = 0.3f;
        public float optOffK = 0.25f;
        public float optTopK = 0.1f;
        public float optHK = 0.8f;
        public float titleY = 0.18f;
        public float capsLine = 1.45f;
        public float ruleGap = 12f;
        public float ruleK = 0.6f;
        public float itemIdleAlpha = 0.72f;
        public float footerOff = 40f;
        public float footerH = 24f;
    }
    [Serializable] public class MenuC_pause
    {
        public float capsLine = 1.45f;
        public float ruleGap = 14f;
        public float diamondExtra = 3f;
        public float selDiamondGap = 30f;
        public float itemIdleAlpha = 0.72f;
        public float footerBottom = 70f;
        public float footerH = 30f;
        public float crashBottom = 112f;
        public float crashH = 30f;
        public int crashFontAdd = 2;
        public float crashAlpha = 0.85f;
    }
    [Serializable] public class MenuC_controls
    {
        public float colHeadW1 = 300f;
        public int colHeadFontAdd = 2;
        public float colHeadAlpha = 0.8f;
        public float colHeadW2 = 400f;
        public float colHeadW3 = 300f;
        public float labelColK = 0.34f;
        public int sizeSub = 2;
        public float keysTrim = 12f;
        public float padTrim = 8f;
        public float bindCellDx = 2f;
        public float bindCellDy = 3f;
        public float bindCellWTrim = 6f;
        public float bindCellHTrim = 6f;
        public float bindCellAlphaSel = 0.35f;
        public float bindCellAlpha = 0.18f;
        public float bindOutlineSel = 1.6f;
        public float bindOutline = 1f;
        public string bindWaiting = "...";
    }
    [Serializable] public class MenuC_row
    {
        public float selDiamondX = 4f;
        public float selShiftK = 0.6f;
        public float idleLabelAlpha = 0.74f;
        public float idleValueAlpha = 0.82f;
        public float sliderValueGap = 8f;
        public float sliderIdleAlpha = 0.7f;
    }
    [Serializable] public class MenuC_capture
    {
        public float dimAlpha = 0.45f;
        public float titleY = 0.12f;
        public string titleSep = "   ";
        public int titleSizeSub = 4;
        public int smallSizeSub = 3;
        public float capsLine = 1.45f;
        public float ruleGap = 10f;
        public float marginX = 20f;
        public float lineY = 0.42f;
        public float marginW = 40f;
        public float lineK = 1.6f;
        public float warnK = 1.4f;
    }
    [Serializable] public class MenuC
    {
        public MenuC_settings settings = new MenuC_settings();
        public MenuC_confirm confirm = new MenuC_confirm();
        public MenuC_pause pause = new MenuC_pause();
        public MenuC_controls controls = new MenuC_controls();
        public MenuC_row row = new MenuC_row();
        public MenuC_capture capture = new MenuC_capture();
    }
    [Serializable] public class LootWorldC_label
    {
        public float nameLineK = 1.5f;
        public float bottomPad = 90f;
        public float ruleDy = 2f;
        public float subLineK = 1.5f;
        public float edgeX = 300f;
        public float halfW = 300f;
        public float boxW = 600f;
        public float ruleH = 1f;
        public float ruleAlpha = 0.75f;
        public string promptOpen = "[ ";
        public string promptClose = " ]   ";
    }
    [Serializable] public class LootWorldC_toast
    {
        public float equipHintK = 1.6f;
        public float fadeInS = 0.2f;
        public float subK = 1.6f;
        public float gapK = 1.7f;
        public float nameK = 1.5f;
    }
    [Serializable] public class LootWorldC
    {
        public LootWorldC_label label = new LootWorldC_label();
        public LootWorldC_toast toast = new LootWorldC_toast();
    }
    [Serializable] public class LootScreenC_screen
    {
        public float closeFrameW = 1f;
        public float rowFrameW = 1f;
        public float candPadX = 14f;
        public float candTrimW = 20f;
        public float candPadY = 5f;
        public float candNameTrim = 100f;
        public float candNameK = 1.4f;
        public float candSubK = 1.35f;
        public float candSubBoxK = 1.5f;
        public float badgeDx = 92f;
        public float badgeW = 80f;
        public float nothingDx = 6f;
        public float nothingDy = 8f;
        public string hintSep = "     ";
        public float msgOffK = 2.2f;
        public float msgBoxK = 1.8f;
        public int msgSizeAdd = 2;
    }
    [Serializable] public class LootScreenC_detail
    {
        public int emptySizeAdd = 2;
        public float removeK = 1.7f;
        public float nameBigK = 1.35f;
        public float nameBoxK = 1.4f;
        public int lineSizeAdd = 1;
        public float lineGapAdd = 6f;
        public float ruleH = 1f;
        public float ruleGapK = 0.6f;
        public float compareK = 1.9f;
        public float statTrim = 24f;
        public float statPadX = 8f;
        public float dashInset = 2f;
        public float dashHalf = 1f;
        public float dashTrim = 4f;
        public float dashH = 2f;
        public string plus = "+";
        public int deltaSizeSub = 2;
    }
    [Serializable] public class LootScreenC_draw
    {
        public int fontMin = 8;
        public float ruleH = 1f;
        public float shadowDiv = 13f;
        public float triRefSize = 32f;
    }
    [Serializable] public class LootScreenC
    {
        public LootScreenC_screen screen = new LootScreenC_screen();
        public LootScreenC_detail detail = new LootScreenC_detail();
        public LootScreenC_draw draw = new LootScreenC_draw();
    }
    [Serializable] public class MarketC_panel
    {
        public int fallbackRows = 8;
        public float insetPx = 6f;
        public float insetTrim = 12f;
        public float innerFrameW = 1f;
        public float subDx = 2f;
        public float titleK = 1.35f;
        public float subW = 0.7f;
        public float subK = 1.5f;
        public float coinDy = 4f;
        public float coinK = 1.5f;
        public float ruleBottom = 4f;
        public float nothingDy = 20f;
        public float nothingK = 1.6f;
        public float accentW = 3f;
        public float rowFrameW = 1f;
        public float nameW = 0.6f;
        public float tagX = 0.55f;
        public float tagW = 0.2f;
        public float thumbMin = 18f;
        public float trackDx = 6f;
        public float trackW = 2f;
    }
    [Serializable] public class MarketC_tag
    {
        public float bigK = 1.5f;
        public float subK = 1.5f;
        public float coinK = 1.5f;
    }
    [Serializable] public class MarketC_prompt
    {
        public float minSpan = 0.1f;
        public float fadeK = 0.5f;
        public float liftK = 1.6f;
        public float ruleHalf = 60f;
        public float ruleDy = 2f;
        public float ruleW = 120f;
        public float ruleH = 1f;
        public float ruleAlpha = 0.75f;
        public float halfW = 300f;
        public float boxW = 600f;
        public float boxK = 1.6f;
        public string open = "[ ";
        public string close = " ]   ";
    }
    [Serializable] public class MarketC_refusal
    {
        public float fadeInS = 0.12f;
        public float fadeOutS = 0.35f;
        public float boxK = 1.5f;
    }
    [Serializable] public class MarketC_detail
    {
        public float ruleH = 1f;
        public float dy = 6f;
        public float nameW = 0.55f;
        public float nameK = 1.5f;
        public float textK = 1.5f;
        public float msgK = 1.6f;
        public string statSep = "    ";
        public string statFmt = "0.#";
    }
    [Serializable] public class MarketC
    {
        public MarketC_panel panel = new MarketC_panel();
        public MarketC_tag tag = new MarketC_tag();
        public MarketC_prompt prompt = new MarketC_prompt();
        public MarketC_refusal refusal = new MarketC_refusal();
        public MarketC_detail detail = new MarketC_detail();
    }
    [Serializable] public class SilverC_toast
    {
        public int depth = -9000;
    }
    [Serializable] public class SilverC
    {
        public SilverC_toast toast = new SilverC_toast();
    }
    [Serializable] public class Data
    {
        public MenuC menu = new MenuC();
        public LootWorldC lootWorld = new LootWorldC();
        public LootScreenC lootScreen = new LootScreenC();
        public MarketC market = new MarketC();
        public SilverC bossSilver = new SilverC();
    }

    static Data data;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; }

    public static Data Get()
    {
        if (data != null) return data;
        var d = new Data();
        try
        {
            string txt = DataPath.ReadText("menu_layout.json");
            if (!string.IsNullOrEmpty(txt)) { var k = JsonUtility.FromJson<Data>(txt.TrimStart((char)0xFEFF)); if (k != null) d = k; }
        }
        catch (Exception e) { Debug.LogWarning("[MenuLayoutCfg] menu_layout.json parse failed, defaults kept: " + e.Message); d = new Data(); }
        Fix(d); data = d; return d;
    }
    public static void Reload() { data = null; }

    static void Fix(Data d)   // a JSON null for a whole group keeps the defaults
    {
        if (d.menu == null) d.menu = new MenuC();
        if (d.menu.settings == null) d.menu.settings = new MenuC_settings();
        if (d.menu.confirm == null) d.menu.confirm = new MenuC_confirm();
        if (d.menu.pause == null) d.menu.pause = new MenuC_pause();
        if (d.menu.controls == null) d.menu.controls = new MenuC_controls();
        if (d.menu.row == null) d.menu.row = new MenuC_row();
        if (d.menu.capture == null) d.menu.capture = new MenuC_capture();
        if (d.lootWorld == null) d.lootWorld = new LootWorldC();
        if (d.lootWorld.label == null) d.lootWorld.label = new LootWorldC_label();
        if (d.lootWorld.toast == null) d.lootWorld.toast = new LootWorldC_toast();
        if (d.lootScreen == null) d.lootScreen = new LootScreenC();
        if (d.lootScreen.screen == null) d.lootScreen.screen = new LootScreenC_screen();
        if (d.lootScreen.detail == null) d.lootScreen.detail = new LootScreenC_detail();
        if (d.lootScreen.draw == null) d.lootScreen.draw = new LootScreenC_draw();
        if (d.market == null) d.market = new MarketC();
        if (d.market.panel == null) d.market.panel = new MarketC_panel();
        if (d.market.tag == null) d.market.tag = new MarketC_tag();
        if (d.market.prompt == null) d.market.prompt = new MarketC_prompt();
        if (d.market.refusal == null) d.market.refusal = new MarketC_refusal();
        if (d.market.detail == null) d.market.detail = new MarketC_detail();
        if (d.bossSilver == null) d.bossSilver = new SilverC();
        if (d.bossSilver.toast == null) d.bossSilver.toast = new SilverC_toast();
    }

    public static MenuC M { get { return Get().menu; } }
    public static LootWorldC LW { get { return Get().lootWorld; } }
    public static LootScreenC LS { get { return Get().lootScreen; } }
    public static MarketC MK { get { return Get().market; } }
    public static SilverC BS { get { return Get().bossSilver; } }
}

/// <summary>UIDATAB v1: tiny string caches for OnGUI code (no per-frame concat / Format / ToUpperInvariant): same text as before, rebuilt only when an input changes.</summary>
public static class UiStr
{
    static readonly Dictionary<string, string> up = new Dictionary<string, string>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { up.Clear(); }
    /// <summary>s.ToUpperInvariant(), computed once per distinct string.</summary>
    public static string Up(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        string r; if (up.TryGetValue(s, out r)) return r;
        if (up.Count > 512) up.Clear();
        r = s.ToUpperInvariant(); up[s] = r; return r;
    }
    /// <summary>a + b + c + d (d upper-cased when upLast), rebuilt only when a part changes.</summary>
    public sealed class Cat4
    {
        string a, b, c, d, v; bool u, has;
        public string Get(string a0, string b0, string c0, string d0, bool upLast)
        {
            if (has && u == upLast && string.Equals(a, a0) && string.Equals(b, b0) && string.Equals(c, c0) && string.Equals(d, d0)) return v;
            a = a0; b = b0; c = c0; d = d0; u = upLast; has = true; v = a0 + b0 + c0 + (upLast ? Up(d0) : d0); return v;
        }
    }
    /// <summary>a + sep + b + sep + c + sep + d, rebuilt only when a part changes.</summary>
    public sealed class CatSep
    {
        string a, s, b, c, d, v; bool has;
        public string Get(string a0, string sep, string b0, string c0, string d0)
        {
            if (has && string.Equals(a, a0) && string.Equals(s, sep) && string.Equals(b, b0) && string.Equals(c, c0) && string.Equals(d, d0)) return v;
            a = a0; s = sep; b = b0; c = c0; d = d0; has = true; v = a0 + sep + b0 + sep + c0 + sep + d0; return v;
        }
    }
    /// <summary>string.Format(fmt, n) for an int, one result per distinct n (cleared when the format text changes).</summary>
    public sealed class IntFmt
    {
        string fmt; readonly Dictionary<int, string> d = new Dictionary<int, string>();
        public string Get(string f, int n)
        {
            if (!string.Equals(fmt, f)) { fmt = f; d.Clear(); }
            string s; if (d.TryGetValue(n, out s)) return s;
            if (d.Count > 256) d.Clear();
            s = string.Format(f, n); d[n] = s; return s;
        }
    }
}
