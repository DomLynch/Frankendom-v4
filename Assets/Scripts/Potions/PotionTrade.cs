using System;
using System.Collections.Generic;

// POTIONS v1: the buy / sell of stackable items for the camp stall. LocalMarket (MarketCore.cs) hands over here when PotionStore.IsStack(item). One unit per order from the panel (press again to buy another);
// the API takes qty so a later "x5" key is a UI change only. Prices are consumables.json buyValue (silver); sell = the MarketPrice.Sell rule of market.json (sellRatio, minSell). The silver, the quantity and the
// request id are written by ONE save (LootSave.CommitStackBuy / CommitStackSell, all or nothing). Nothing here runs while the master switch is off (IsStack false).
public static class PotionTrade
{
    public const string CarryFull = "carry-full";

    static int BuyUnit(PotionCfg.PotionC p, MarketBuyRules b) { return MarketBuyRules.Price(new MarketRow { item = p.id, buyValue = p.buyValue }, b); }
    static int SellUnit(PotionCfg.PotionC p, MarketRules r) { return MarketPrice.Sell(new MarketRow { item = p.id, buyValue = p.buyValue }, r); }

    public static MarketResult Run(IMarketStore store, MarketRules rules, string op, string item, int qty, string requestId, int coins)
    {
        var p = PotionCfg.Find(item); if (p == null) return MarketResult.Fail(MarketCode.NotForSale, coins);
        if (qty < 1 || qty > Math.Max(1, p.maxCarry)) return MarketResult.Fail(MarketCode.BadQty, coins);
        if (op == "buy")
        {
            var B = MarketBuyRules.Cur; if (!B.enabled || !B.Sells(item)) return MarketResult.Fail(MarketCode.NotForSale, coins);
            int unit = BuyUnit(p, B); if (unit <= 0) return MarketResult.Fail(MarketCode.NotForSale, coins);
            if (!PotionStore.CanAdd(item, qty, p.maxCarry)) return MarketResult.Fail(CarryFull, coins);
            long total = (long)unit * qty; if (coins < total) return MarketResult.Fail(MarketCode.NoCoins, coins);
            if (!LootSave.CommitStackBuy(requestId, item, qty, unit, rules.keepRequests, p.maxCarry)) return MarketResult.Fail(MarketCode.SaveFailed, store.Coins);
            return new MarketResult { ok = true, item = item, qty = qty, coinsDelta = -(int)total, coins = store.Coins };
        }
        else
        {
            int unit = SellUnit(p, rules); if (unit <= 0) return MarketResult.Fail(MarketCode.NotSellable, coins);
            if (PotionStore.Qty(item) < qty) return MarketResult.Fail(MarketCode.NotOwned, coins);
            long total = (long)unit * qty; if ((long)coins + total > rules.walletCap) return MarketResult.Fail(MarketCode.WalletFull, coins);
            if (!LootSave.CommitStackSell(requestId, item, qty, unit, rules.keepRequests, rules.walletCap)) return MarketResult.Fail(MarketCode.SaveFailed, store.Coins);
            return new MarketResult { ok = true, item = item, qty = qty, coinsDelta = (int)total, coins = store.Coins };
        }
    }

    /// <summary>Sell tab: one line per carried stack (after the gear lines).</summary>
    public static void AddSellLines(MarketStock st, MarketRules rules)
    {
        if (!PotionCfg.On) return; var P = PotionCfg.Get().potions; var T = PotionCfg.Get().text;
        for (int i = 0; i < P.Length; i++)
        {
            var p = P[i]; int n = PotionStore.Qty(p.id); if (n < 1 || !PotionStore.IsStack(p.id)) continue;
            int unit = SellUnit(p, rules); if (unit <= 0) continue;
            st.lines.Add(new MarketLine { id = p.id, name = p.name.Length > 0 ? p.name : p.id, price = unit, canSell = true, worn = false, stack = true, qty = n, tag = string.Format(T.tagQty, n) });
        }
    }

    /// <summary>Buy tab: the line of one stocked stack. Greyed (canSell false) when the carry limit is reached or the purse is short.</summary>
    public static void AddBuyLine(MarketStock st, string id, MarketBuyRules B, int coins)
    {
        var p = PotionCfg.Find(id); if (p == null) return; var T = PotionCfg.Get().text;
        int unit = BuyUnit(p, B); if (unit <= 0) return;
        int have = PotionStore.Qty(id); bool full = have >= Math.Max(1, p.maxCarry), poor = !full && coins < unit;
        string tag = have > 0 ? string.Format(T.tagQty, have) : ""; if (full) tag += (tag.Length > 0 ? "  " : "") + T.tagFull; else if (poor) tag += (tag.Length > 0 ? "  " : "") + B.tagPoor;
        st.lines.Add(new MarketLine { id = id, name = p.name.Length > 0 ? p.name : id, price = unit, canSell = !(full || poor), worn = full || poor, stack = true, qty = have, why = full ? CarryFull : poor ? MarketCode.NoCoins : "", tag = tag });
    }
}
