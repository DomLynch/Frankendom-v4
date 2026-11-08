using System;
using System.Collections.Generic;

// STALL v1 (lane laneSTALL): the PURE logic of the camp trader stall. No UnityEngine in this file, so the self-test compiles it with plain dotnet.
//   * MarketRules / MarketRow / MarketPrice  : the price rule (sell = round(buyValue * sellRatio), never above the buy value, never below the minimum)
//   * IMarketBackend                         : the seam. Stock() + Trade(op, item, qty, requestId). LocalMarket is the single-player backend; a later server backend only has to implement this interface
//   * MarketLedger                           : coins + the request-id ledger (dedupe), applied TOGETHER with the removal of the sold item, so a sale is one change
//   * ShopController                         : the panel state machine (open, select, arm, sell, close), driven by commands so it can be tested without a screen
//   * MarketLayout / MarketArea              : panel rectangles at any resolution; the camp rectangle test with hysteresis
// Every number the logic uses comes in through MarketRules / MarketRow / the layout arguments, which MarketCfg.cs fills from market.json.

public static class MarketCode
{
    public const string Ok = "ok", NotForSale = "not-for-sale", NoRequestId = "request-id-required", Conflict = "request-conflict", BadQty = "bad-qty",
        NotSellable = "not-sellable", NotOwned = "not-owned", Equipped = "equipped", WalletFull = "wallet-full", SaveFailed = "save-failed", Disabled = "disabled", Busy = "busy", UnknownOp = "unknown-op",
        AlreadyOwned = "already-owned", NoCoins = "no-coins";   // laneNPCSPAWN
}

public sealed class MarketRules
{
    public double sellRatio = 0.33; public int minSell = 1, walletCap = 999999, keepRequests = 200;
    public string[] ops = new string[] { "sell" }; public bool buyEnabled = false, confirmSell = true, listEquipped = true;
    public bool Allows(string op) { if (string.IsNullOrEmpty(op) || ops == null) return false; if (op == "buy" && !buyEnabled) return false; for (int i = 0; i < ops.Length; i++) if (ops[i] == op) return true; return false; }
}

public sealed class MarketRow { public string item = ""; public int buyValue; public bool sellable = true; public int sellOverride; }

public static class MarketPrice
{
    /// <summary>Coins the trader pays for one copy. 0 = not bought. Never above the buy value (a sale can not make money from a buy), never below minSell.</summary>
    public static int Sell(MarketRow row, MarketRules r)
    {
        if (row == null || r == null || !row.sellable || row.buyValue <= 0) return 0;
        int p = row.sellOverride > 0 ? row.sellOverride : (int)Math.Round((double)row.buyValue * r.sellRatio + 1e-9, MidpointRounding.AwayFromZero);
        if (p < r.minSell) p = r.minSell;
        if (p > row.buyValue) p = row.buyValue;
        return p;
    }
    public static MarketRow Find(IList<MarketRow> rows, string item)
    {
        if (rows != null && !string.IsNullOrEmpty(item)) for (int i = 0; i < rows.Count; i++) if (rows[i] != null && rows[i].item == item) return rows[i];
        return null;
    }
}

public sealed class MarketLine { public string id = "", name = ""; public int price; public bool canSell; public bool worn; public string why = ""; public string tag = ""; /* // laneNPCSPAWN: row tag text */ public bool stack; public int qty; /* POTIONS v1: a stackable line (potion): qty = carried, tag = "x3" */ }
public sealed class MarketStock { public int coins; public List<MarketLine> lines = new List<MarketLine>(); }
public sealed class MarketResult
{
    public bool ok, replayed; public string code = MarketCode.Ok, item = ""; public int qty, coinsDelta, coins;
    public static MarketResult Fail(string code, int coins) { return new MarketResult { ok = false, code = code, coins = coins }; }
}

/// <summary>The seam between the panel and whoever owns the coins. Single player: LocalMarket. Later: a server backend with the same two calls (request ids make a retry safe).</summary>
public interface IMarketBackend
{
    MarketStock Stock();
    MarketResult Trade(string op, string itemId, int qty, string requestId);
}

[Serializable] public class MarketSale { public string id = "", item = ""; public int coins; }

/// <summary>Coins + the sales ledger. Lives inside the loot save file (LootSave hunk), so the coins and the removal of the item are written by ONE file write.</summary>
public sealed class MarketLedger
{
    public int Coins, Seq; public readonly List<MarketSale> Sales = new List<MarketSale>();
    public sealed class State { public int coins, seq; public MarketSale[] sales = new MarketSale[0]; }

    public void Clear() { Coins = 0; Seq = 0; Sales.Clear(); }
    /// <summary>Read a saved record. Hand-edited or damaged data is repaired: negative coins become 0, coins over the cap are cut, a request id seen twice keeps its first row, the list is cut to the newest `keep`.</summary>
    public void Load(int coins, int seq, MarketSale[] rows, int cap, int keep)
    {
        Clear(); Coins = Math.Max(0, Math.Min(coins, cap)); Seq = Math.Max(0, seq);
        if (rows != null) for (int i = 0; i < rows.Length; i++) { var s = rows[i]; if (s == null || string.IsNullOrEmpty(s.id) || Find(s.id) != null) continue; Sales.Add(new MarketSale { id = s.id, item = s.item ?? "", coins = Math.Max(0, s.coins) }); }
        Trim(keep);
    }
    public MarketSale Find(string requestId) { if (!string.IsNullOrEmpty(requestId)) for (int i = 0; i < Sales.Count; i++) if (Sales[i].id == requestId) return Sales[i]; return null; }
    void Trim(int keep) { keep = Math.Max(1, keep); while (Sales.Count > keep) Sales.RemoveAt(0); }
    /// <summary>A fresh request id. The sequence is saved with the sale it belongs to, so an id is never reused.</summary>
    public string NextId(string item) { string id; do { Seq++; id = "sell-" + Seq + "-" + item; } while (Find(id) != null); return id; }

    /// <summary>The sale: remove one copy of the item from the carried list, add the coins, write the ledger row. All or nothing. False = nothing changed.</summary>
    public bool Apply(IList<string> owned, string requestId, string item, int price, int keep, int cap)
    {
        if (owned == null || price <= 0 || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(item) || Find(requestId) != null) return false;
        int at = owned.IndexOf(item); if (at < 0) return false;
        if ((long)Coins + price > cap) return false;
        owned.RemoveAt(at); Coins += price; Sales.Add(new MarketSale { id = requestId, item = item, coins = price }); Trim(keep); return true;
    }
    /// <summary>laneNPCSPAWN: a purchase. Add one copy to the carried list, take the coins, write the ledger row (price recorded negative). All or nothing. False = nothing changed.</summary>
    public bool ApplyBuy(IList<string> owned, string requestId, string item, int price, int keep)
    {
        if (owned == null || price <= 0 || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(item) || Find(requestId) != null || owned.Contains(item) || Coins < price) return false;
        owned.Add(item); Coins -= price; Sales.Add(new MarketSale { id = requestId, item = item, coins = -price }); Trim(keep); return true;
    }
    /// <summary>Future coin sink (a buy row, a camp upgrade, a toll): take coins, all or nothing. A repeated request id takes nothing the second time.</summary>
    public bool TrySpend(int amount, string requestId, int keep)
    {
        if (amount <= 0 || string.IsNullOrEmpty(requestId) || Find(requestId) != null || Coins < amount) return false;
        Coins -= amount; Sales.Add(new MarketSale { id = requestId, item = "", coins = -amount }); Trim(keep); return true;
    }
    /// <summary>BOSSSILVER v1: a reward. Add coins (clamped to the cap), write the ledger row (positive). A repeated request id pays nothing the second time. False = nothing changed.</summary>
    public bool Grant(int amount, string requestId, int keep, int cap)
    {
        if (amount <= 0 || string.IsNullOrEmpty(requestId) || Find(requestId) != null || Coins >= cap) return false;
        Coins = (int)Math.Min((long)cap, (long)Coins + amount); Sales.Add(new MarketSale { id = requestId, item = "", coins = amount }); Trim(keep); return true;
    }
    public State Snapshot() { return new State { coins = Coins, seq = Seq, sales = Sales.ToArray() }; }
    public void Restore(State s) { Coins = s.coins; Seq = s.seq; Sales.Clear(); Sales.AddRange(s.sales); }
}

/// <summary>What LocalMarket needs from the game: the carried list, what is worn, names, the ledger. The Unity adapter (MarketAdapters.cs) sits on LootSave and LootBridge.</summary>
public interface IMarketStore
{
    int Coins { get; }
    IList<string> Carried { get; }
    bool IsWorn(string item);
    string NameOf(string item);
    MarketSale FindSale(string requestId);
    string NextRequestId(string item);
    /// <summary>Atomic: remove one copy, add `price` coins, record the request id. Returns false and changes nothing when the write fails.</summary>
    bool CommitSale(string requestId, string item, int price, int keep, int cap);
    /// <summary>// laneNPCSPAWN: atomic: add one copy, take `price` coins, record the request id.</summary>
    bool CommitBuy(string requestId, string item, int price, int keep);
}

public sealed class LocalMarket : IMarketBackend
{
    readonly IMarketStore store; readonly MarketRules rules; readonly IList<MarketRow> rows; public bool Enabled = true;
    public LocalMarket(IMarketStore store, MarketRules rules, IList<MarketRow> rows) { this.store = store; this.rules = rules; this.rows = rows; }

    public MarketStock Stock()
    {
        var st = new MarketStock { coins = store.Coins }; var own = store.Carried; var seen = new HashSet<string>();
        for (int i = 0; i < own.Count; i++)
        {
            string id = own[i]; if (!seen.Add(id)) continue;   // a doubled id in a hand-edited save is shown once
            var row = MarketPrice.Find(rows, id); int p = MarketPrice.Sell(row, rules); bool worn = store.IsWorn(id);
            if (p <= 0) continue;   // the trader does not take it: not listed
            if (worn && !rules.listEquipped) continue;
            st.lines.Add(new MarketLine { id = id, name = store.NameOf(id), price = p, worn = worn, canSell = !worn, why = worn ? MarketCode.Equipped : "" });
        }
        PotionTrade.AddSellLines(st, rules);   // POTIONS v1: carried stacks (nothing while consumables.json is off)
        return st;
    }

    public MarketResult Trade(string op, string itemId, int qty, string requestId)
    {
        int c = store.Coins;
        if (!Enabled) return MarketResult.Fail(MarketCode.Disabled, c);
        if (op != "sell" && op != "buy") return MarketResult.Fail(MarketCode.UnknownOp, c);
        if (!rules.Allows(op) && !(op == "buy" && MarketBuyRules.Cur.enabled)) return MarketResult.Fail(MarketCode.NotForSale, c);   // laneNPCSPAWN: the buy side is hub.json data
        if (string.IsNullOrEmpty(requestId)) return MarketResult.Fail(MarketCode.NoRequestId, c);
        var prev = store.FindSale(requestId);
        if (prev != null)   // the same order again: the first answer, nothing moves
        {
            if (prev.item != itemId || (qty != 1 && !PotionStore.IsStack(itemId) /* POTIONS v1 */)) return MarketResult.Fail(MarketCode.Conflict, c);
            return new MarketResult { ok = true, replayed = true, item = prev.item, qty = 1, coinsDelta = prev.coins, coins = c };
        }
        if (PotionStore.IsStack(itemId)) return PotionTrade.Run(store, rules, op, itemId, qty, requestId, c);   // POTIONS v1: stackable items (potions) trade by quantity
        if (op == "buy") return Buy(itemId, qty, requestId, c);   // laneNPCSPAWN
        if (op != "sell") return MarketResult.Fail(MarketCode.NotForSale, c);
        if (qty != 1) return MarketResult.Fail(MarketCode.BadQty, c);   // gear is one copy per id in the save
        int price = MarketPrice.Sell(MarketPrice.Find(rows, itemId), rules);
        if (price <= 0) return MarketResult.Fail(MarketCode.NotSellable, c);
        if (!Contains(store.Carried, itemId)) return MarketResult.Fail(MarketCode.NotOwned, c);
        if (store.IsWorn(itemId)) return MarketResult.Fail(MarketCode.Equipped, c);
        if ((long)c + price > rules.walletCap) return MarketResult.Fail(MarketCode.WalletFull, c);
        if (!store.CommitSale(requestId, itemId, price, rules.keepRequests, rules.walletCap)) return MarketResult.Fail(MarketCode.SaveFailed, store.Coins);
        return new MarketResult { ok = true, item = itemId, qty = 1, coinsDelta = price, coins = store.Coins };
    }
    // laneNPCSPAWN: the buy side (hub.json stall.buy -> MarketBuyRules.Cur). One copy per id; the coins and the new item are written by ONE save (LootSave.CommitBuy).
    MarketResult Buy(string itemId, int qty, string requestId, int c)
    {
        var B = MarketBuyRules.Cur;
        if (!B.enabled) return MarketResult.Fail(MarketCode.NotForSale, c);
        if (qty != 1) return MarketResult.Fail(MarketCode.BadQty, c);
        int price = B.Sells(itemId) ? MarketBuyRules.Price(MarketPrice.Find(rows, itemId), B) : 0;
        if (price <= 0) return MarketResult.Fail(MarketCode.NotForSale, c);
        if (Contains(store.Carried, itemId) || store.IsWorn(itemId)) return MarketResult.Fail(MarketCode.AlreadyOwned, c);
        if (c < price) return MarketResult.Fail(MarketCode.NoCoins, c);
        if (!store.CommitBuy(requestId, itemId, price, rules.keepRequests)) return MarketResult.Fail(MarketCode.SaveFailed, store.Coins);
        return new MarketResult { ok = true, item = itemId, qty = 1, coinsDelta = -price, coins = store.Coins };
    }
    /// <summary>What the trader sells, as panel lines. canSell = "can be bought now"; an owned or too-dear line is greyed with a tag.</summary>
    public MarketStock BuyStock()
    {
        var B = MarketBuyRules.Cur; var st = new MarketStock { coins = store.Coins }; if (!B.enabled) return st;
        for (int i = 0; i < B.items.Length; i++)
        {
            string id = B.items[i]; if (PotionStore.IsStack(id)) { PotionTrade.AddBuyLine(st, id, B, store.Coins); continue; } /* POTIONS v1 */ int p = MarketBuyRules.Price(MarketPrice.Find(rows, id), B); if (p <= 0) continue;
            bool own = Contains(store.Carried, id) || store.IsWorn(id), poor = !own && store.Coins < p;
            st.lines.Add(new MarketLine { id = id, name = store.NameOf(id), price = p, worn = own || poor, canSell = !(own || poor), why = own ? MarketCode.AlreadyOwned : poor ? MarketCode.NoCoins : "", tag = own ? B.tagOwned : poor ? B.tagPoor : "" });
        }
        return st;
    }
    static bool Contains(IList<string> l, string id) { for (int i = 0; i < l.Count; i++) if (l[i] == id) return true; return false; }
}

/// <summary>The panel as a state machine. Commands in, a note out; the drawing code reads Lines / Index / Armed / Note. No timing and no keys in here.</summary>
public sealed class ShopController
{
    public bool IsOpen; public int Index, Scroll; public string Armed = "";
    public string NoteKey = ""; public string NoteItem = ""; public int NoteCoins; public bool NoteGood;
    public MarketStock Stock = new MarketStock();
    readonly IMarketBackend backend; readonly MarketRules rules; readonly IMarketStore store;
    string pendingId = "", pendingItem = "";
    public ShopController(IMarketBackend backend, IMarketStore store, MarketRules rules) { this.backend = backend; this.store = store; this.rules = rules; }

    public MarketLine Selected { get { return Index >= 0 && Index < Stock.lines.Count ? Stock.lines[Index] : null; } }
    public bool BuyTab;   // laneNPCSPAWN: the Buy tab (hub.json stall.buy), off until the data turns it on
    public void ToggleTab() { if (!MarketBuyRules.Cur.enabled) return; BuyTab = !BuyTab; Armed = ""; NoteKey = ""; Index = 0; Scroll = 0; Refresh(); Index = FirstSellable(); }
    public void Open() { IsOpen = true; BuyTab = MarketBuyRules.Cur.enabled && MarketBuyRules.Cur.items != null && MarketBuyRules.Cur.items.Length > 0; /* STALLS vend v2: open on Buy when the stall sells something */ Armed = ""; NoteKey = ""; Refresh(); Index = FirstSellable(); Scroll = 0; }
    public void Close() { IsOpen = false; Armed = ""; }
    void Refresh() { var lm = backend as LocalMarket; Stock = BuyTab && lm != null ? lm.BuyStock() : backend.Stock();   // laneNPCSPAWN
        if (Index >= Stock.lines.Count) Index = Math.Max(0, Stock.lines.Count - 1); }
    int FirstSellable() { for (int i = 0; i < Stock.lines.Count; i++) if (Stock.lines[i].canSell) return i; return 0; }

    public void Move(int d, int visibleRows)
    {
        if (Stock.lines.Count == 0) return; Index = Math.Max(0, Math.Min(Stock.lines.Count - 1, Index + d)); Armed = "";
        if (visibleRows < 1) visibleRows = 1;
        if (Index < Scroll) Scroll = Index; if (Index >= Scroll + visibleRows) Scroll = Index - visibleRows + 1; Scroll = Math.Max(0, Scroll);
    }
    /// <summary>Back: disarm first, then close. True when the panel closed.</summary>
    public bool Back() { if (Armed.Length > 0) { Armed = ""; return false; } Close(); return true; }

    /// <summary>Confirm on the selected line: arm (when rules.confirmSell), then sell. Returns the trade result when a trade was sent, else null.</summary>
    public MarketResult Confirm()
    {
        var l = Selected; if (l == null) return null;
        if (!l.canSell) { Note(l.why.Length > 0 ? l.why : MarketCode.Equipped, l, 0, false); return null; }   // laneNPCSPAWN
        if (rules.confirmSell && !l.stack /* POTIONS v1: 1-5 silver, no second press */ && Armed != l.id) { Armed = l.id; NoteKey = "armed"; NoteItem = l.name; NoteGood = true; NoteCoins = l.price; return null; }
        if (pendingItem != l.id || pendingId.Length == 0) { pendingItem = l.id; pendingId = store.NextRequestId(l.id); }   // a retry of the same item reuses the id
        var r = backend.Trade(BuyTab ? "buy" : "sell", l.id, 1, pendingId);   // laneNPCSPAWN
        Armed = "";
        bool retry = r.code == MarketCode.SaveFailed;   // keep the id: pressing again can not charge twice
        if (!retry) { pendingId = ""; pendingItem = ""; }
        if (r.ok) Note(l.stack ? (BuyTab ? "boughtStack" : "soldStack") : (BuyTab ? "bought" : "sold") /* POTIONS v1 */, l, Math.Abs(r.coinsDelta), true);   // laneNPCSPAWN
        else Note(r.code, l, 0, false);
        Refresh();
        return r;
    }
    void Note(string key, MarketLine l, int coins, bool good) { NoteKey = key; NoteItem = l != null ? l.name : ""; NoteCoins = coins; NoteGood = good; }
}

/// <summary>Panel rectangles for any screen. Virtual units = reference pixels (1080 high), screen px = virtual * Scale. Pure so a CPU test can check 1080p and 2560x1600.</summary>
public struct MRect { public float x, y, w, h; public MRect(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; } public float xMax { get { return x + w; } } public float yMax { get { return y + h; } } }

public sealed class MarketLayout
{
    public float scale, vw, vh; public MRect panel, header, list, detail, footer; public int visibleRows; public float rowH, rowGap;
    public static float ScaleFor(int screenH, float referenceHeight, float minScale, float maxScale) { float s = screenH / Math.Max(1f, referenceHeight); return Math.Max(minScale, Math.Min(maxScale, s)); }

    public static MarketLayout Compute(int sw, int sh, float refH, float minScale, float maxScale, float panelW, float panelH, float margin, float pad, float headerH, float footerH, float detailH, float rowH, float rowGap)
    {
        var L = new MarketLayout(); L.scale = ScaleFor(sh, refH, minScale, maxScale); L.vw = sw / L.scale; L.vh = sh / L.scale; L.rowH = rowH; L.rowGap = rowGap;
        float w = Math.Min(panelW, L.vw - 2f * margin), h = Math.Min(panelH, L.vh - 2f * margin);
        L.panel = new MRect((L.vw - w) * 0.5f, (L.vh - h) * 0.5f, w, h);
        L.header = new MRect(L.panel.x + pad, L.panel.y + pad, w - 2f * pad, headerH);
        L.footer = new MRect(L.panel.x + pad, L.panel.yMax - pad - footerH, w - 2f * pad, footerH);
        L.detail = new MRect(L.panel.x + pad, L.footer.y - detailH, w - 2f * pad, detailH);
        float top = L.header.yMax, bottom = L.detail.y - rowGap;
        L.list = new MRect(L.panel.x + pad, top, w - 2f * pad, Math.Max(0f, bottom - top));
        L.visibleRows = Math.Max(1, (int)Math.Floor((L.list.h + rowGap) / (rowH + rowGap)));
        return L;
    }
    public MRect Row(int visibleIndex) { return new MRect(list.x, list.y + visibleIndex * (rowH + rowGap), list.w, rowH); }
}

/// <summary>The camp as a rectangle in world x/z (data: market.json stall.area). Inside/outside with a margin each way so the flag does not flicker on the line.</summary>
public sealed class MarketArea
{
    public float zMin, zMax, xHalf, enterPad, leavePad; public bool Inside;
    public bool Update(float x, float z)
    {
        // enter: inside the rectangle shrunk by enterPad; leave: outside the rectangle grown by leavePad (so the flag does not flicker on the line)
        if (Inside) { if (Math.Abs(x) > xHalf + leavePad || z < zMin - leavePad || z > zMax + leavePad) Inside = false; }
        else if (Math.Abs(x) <= xHalf - enterPad && z >= zMin + enterPad && z <= zMax - enterPad) Inside = true;
        return Inside;
    }
    public static float Dist(float ax, float az, float bx, float bz) { float dx = ax - bx, dz = az - bz; return (float)Math.Sqrt(dx * dx + dz * dz); }
}
