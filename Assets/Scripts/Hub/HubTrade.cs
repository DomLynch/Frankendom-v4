using System;
using System.Collections.Generic;
using UnityEngine;

// laneNPCSPAWN v1: the Unity side of the buy tab. Install() copies hub.json stall.buy into MarketBuyRules.Cur (read by the '// laneNPCSPAWN' hunks in MarketCore / MarketUI); TabPressed() is the
// tab key (data: stall.buy.tabKeys, KeyCode names). Called once from HubNpcs.Boot, only when hub.json is enabled. UNPROVEN IN UNITY.
public static class HubTrade
{
    static KeyCode[] tab = new KeyCode[0];
    static string[] baseItems = new string[0];   // MARKET2 v1: the main stall's stock (hub.json stall.buy.items)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { MarketBuyRules.Cur = new MarketBuyRules(); tab = new KeyCode[0]; baseItems = new string[0]; }

    public static void Install(HubData k)
    {
        var b = k.stall.buy; var r = new MarketBuyRules
        {
            enabled = b.enabled, mul = b.mul, minPrice = b.minPrice, items = b.items, tabKeys = b.tabKeys, tabSell = b.tabSell, tabBuy = b.tabBuy, subtitleBuy = b.subtitleBuy, hintMoveTab = b.hintMoveTab,
            hintBuy = b.hintBuy, armedBuy = b.armedBuy, bought = b.bought, detailAsks = b.detailAsks, tagOwned = b.tagOwned, tagPoor = b.tagPoor
        };
        MarketBuyRules.Cur = r; baseItems = b.items; var l = new List<KeyCode>();
        foreach (var n in b.tabKeys) { KeyCode c; if (Enum.TryParse(n, true, out c)) l.Add(c); }
        tab = l.ToArray();
    }

    /// <summary>MARKET2 v1: the stall the player stands at changed. items = that stall's own stock (market.json stalls[].items); null = the main stall's (hub.json stall.buy.items). Only called when stalls[] has rows.</summary>
    public static void UseStock(string[] items) { MarketBuyRules.Cur.items = items != null ? items : baseItems; }

    /// <summary>A Buy / Sell tab key went down this frame (false while the buy side is off).</summary>
    public static bool TabPressed()
    {
        if (!MarketBuyRules.Cur.enabled) return false;
        for (int i = 0; i < tab.Length; i++) if (Input.GetKeyDown(tab[i])) return true;
        return false;
    }
}
