using System;
using System.Collections.Generic;

// laneNPCSPAWN v1: the BUY side of the camp stall, as DATA and pure logic (no UnityEngine, so dotnet runs it headless). laneSTALL ships the stall sell-only with the buy hooks off
// (rules.buyEnabled false, no Buy tab, MarketLedger.TrySpend unused). This lane fills that gap with small hunks marked '// laneNPCSPAWN' in MarketCore / MarketAdapters / MarketUI / LootSave
// (apply_npcspawn.py), and everything those hunks read lives here, filled from hub.json stall.buy by HubTrade at boot:
//   * Which gear the trader sells (items), the price rule (buyValue from market.json rows x mul, at least minPrice), every word on screen, the tab keys.
//   * Cur.enabled is false until hub.json is on, so a stock STALL panel behaves exactly as shipped (sell only).
// Placeholder balance numbers (Dom has not set them): see MANIFEST. UNPROVEN IN UNITY.
public sealed class MarketBuyRules
{
    public bool enabled; public double mul = 1.0; public int minPrice = 1;
    public string[] items = new string[0], tabKeys = new string[0];
    public string tabSell = "Sell", tabBuy = "Buy", subtitleBuy = "", hintMoveTab = "", hintBuy = "", armedBuy = "", bought = "", detailAsks = "", tagOwned = "", tagPoor = "";

    public static MarketBuyRules Cur = new MarketBuyRules();

    public bool Sells(string item) { if (items != null && !string.IsNullOrEmpty(item)) for (int i = 0; i < items.Length; i++) if (items[i] == item) return true; return false; }

    /// <summary>Coins one copy costs. 0 = not for sale. Never below minPrice.</summary>
    public static int Price(MarketRow row, MarketBuyRules b)
    {
        if (row == null || b == null || row.buyValue <= 0) return 0;
        int p = (int)Math.Round((double)row.buyValue * b.mul + 1e-9, MidpointRounding.AwayFromZero);
        return p < b.minPrice ? b.minPrice : p;
    }

    // wording helpers (the UI hunks call these; with the buy side off they return the stock STALL text)
    public string Subtitle(bool buyTab, string stock) { return enabled && buyTab && subtitleBuy.Length > 0 ? subtitleBuy : stock; }
    public string HintMove(string stock) { return enabled && hintMoveTab.Length > 0 ? stock + "    " + hintMoveTab : stock; }
    public string HintConfirm(bool buyTab, string stock) { return enabled && buyTab && hintBuy.Length > 0 ? hintBuy : stock; }
}
