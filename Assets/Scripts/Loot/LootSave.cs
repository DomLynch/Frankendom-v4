using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// LOOTUI v85: what the player owns and what is worn, saved as JSON in Application.persistentDataPath (loot_ui.json save.file, default loot_save.json).
//   { "version": 1, "owned": ["pitborn_pauldron", ...], "equipped": [ { "slot": "chest", "item": "..." }, ... ] }
// Saved after every pickup / equip / unequip (write to a temp file, then replace, so a crash never leaves half a file). A missing or broken file = nothing owned, nothing worn.
// The slot list is the truth of "what is worn" while the game runs (IGearSlots); this file only restores it at the next start and after every scene load.
public static class LootSave
{
    [Serializable] public class SlotRow { public string slot = "", item = ""; }
    [Serializable] public class FileData { public int version = 2; public string[] owned = new string[0]; public SlotRow[] equipped = new SlotRow[0]; public int coins; public int seq; public MarketSale[] sales = new MarketSale[0]; public bool startPaid; }   // STALL v1: version 2 adds coins + the sales ledger (a version 1 file loads with 0 coins)

    static readonly List<string> owned = new List<string>();
    static readonly List<SlotRow> wanted = new List<SlotRow>();   // as loaded: what to put back on the wearer
    static bool loaded;
    static bool startPaid;   // CAMPPLAY v1: the start coins were paid once for this save
    static readonly MarketLedger ledger = new MarketLedger();   // STALL v1: coins + request ids (MarketCore.cs)
    public static bool Applied;   // the saved selection has been put on a wearer at least once; before that Save() keeps the loaded rows instead of reading an empty slot list
    public static string LastError = "";
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)] static void HookErase() { SaveSlot.OnErase += Erase; }   // REBIND v100: New game forgets the carried items
    static void Erase() { owned.Clear(); ledger.Clear(); wanted.Clear(); loaded = true; Applied = false; startPaid = MarketCfg.Get().enabled; ledger.Coins = StartCoins(); try { if (File.Exists(Path_)) File.Delete(Path_); } catch (Exception e) { LastError = e.Message; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { owned.Clear(); ledger.Clear(); wanted.Clear(); loaded = false; Applied = false; LastError = ""; startPaid = false; }

    public static string Path_ { get { return Path.Combine(SavePaths.Dir, LootCfg.Get().save.file); } }
    public static IList<string> Owned { get { EnsureLoaded(); return owned; } }
    public static IList<SlotRow> Wanted { get { EnsureLoaded(); return wanted; } }
    /// <summary>Mark an item as carried without writing the file (the caller saves). True when it was new.</summary>
    public static bool Own(string id) { EnsureLoaded(); if (string.IsNullOrEmpty(id) || owned.Contains(id)) return false; owned.Add(id); return true; }
    public static bool Owns(string id) { EnsureLoaded(); return owned.Contains(id); }

    /// <summary>Add to the carried list (once per id; a second copy only refreshes the save). True when it was new.</summary>
    public static bool Add(string id)
    {
        EnsureLoaded(); if (string.IsNullOrEmpty(id)) return false;
        bool fresh = !owned.Contains(id); if (fresh) owned.Add(id);
        Save(); return fresh;
    }

    // STALL v1: coins + the sales ledger live in this file, so a sale (item removed + coins added + request id recorded) is ONE write.
    static int StartCoins() { var m = MarketCfg.Get(); return m.enabled ? Mathf.Clamp(m.currency.startCoins, 0, m.currency.cap) : 0; }   // INT108 v1: the coins of a new save (data), 0 while the trader is off
    public static int Coins { get { EnsureLoaded(); return ledger.Coins; } }
    public static MarketSale FindSale(string requestId) { EnsureLoaded(); return ledger.Find(requestId); }
    public static string NextRequestId(string item) { EnsureLoaded(); return ledger.NextId(item); }
    /// <summary>Sell one carried item: remove it, add `price` coins, record the request id, write the file. All or nothing (a failed write rolls the memory back).</summary>
    public static bool CommitSale(string requestId, string item, int price, int keep, int cap)
    {
        EnsureLoaded(); var snap = ledger.Snapshot(); int at = owned.IndexOf(item);
        if (!ledger.Apply(owned, requestId, item, price, keep, cap)) return false;
        LastError = ""; Save();
        if (LastError.Length > 0) { ledger.Restore(snap); if (at >= 0 && !owned.Contains(item)) owned.Insert(Mathf.Min(at, owned.Count), item); return false; }
        return true;
    }
    /// <summary>laneNPCSPAWN: buy one item: add it to the carried list, take the coins, record the request id, write the file. All or nothing (a failed write rolls the memory back).</summary>
    public static bool CommitBuy(string requestId, string item, int price, int keep)
    {
        EnsureLoaded(); var snap = ledger.Snapshot(); if (!ledger.ApplyBuy(owned, requestId, item, price, keep)) return false;
        LastError = ""; Save();
        if (LastError.Length > 0) { ledger.Restore(snap); owned.Remove(item); return false; }
        return true;
    }
    /// <summary>A future coin sink (a buy row, a camp upgrade): take coins, all or nothing; a repeated request id takes nothing.</summary>
    public static bool TrySpend(int amount, string requestId, int keep)
    {
        EnsureLoaded(); var snap = ledger.Snapshot(); if (!ledger.TrySpend(amount, requestId, keep)) return false;
        LastError = ""; Save(); if (LastError.Length > 0) { ledger.Restore(snap); return false; }
        return true;
    }

    public static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true; owned.Clear(); wanted.Clear(); ledger.Clear(); ledger.Coins = StartCoins(); startPaid = MarketCfg.Get().enabled;   // INT108 v1: new save
        if (!LootCfg.Get().save.enabled) return;
        try
        {
            string p = Path_; if (!File.Exists(p)) return;
            var f = JsonUtility.FromJson<FileData>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (f == null) return;
            ledger.Load(f.coins, f.seq, f.sales, MarketCfg.Get().currency.cap, MarketCfg.Get().rules.keepRequests);   // STALL v1
            if (f.version < 2) { ledger.Coins = StartCoins(); startPaid = MarketCfg.Get().enabled; } else startPaid = f.startPaid;   // INT108 v1: a version 1 save has no coins yet   // CAMPPLAY v1
            if (f.owned != null) for (int i = 0; i < f.owned.Length; i++) if (!string.IsNullOrEmpty(f.owned[i]) && !owned.Contains(f.owned[i])) owned.Add(f.owned[i]);
            if (f.equipped != null) for (int i = 0; i < f.equipped.Length; i++) { var r = f.equipped[i]; if (r != null && !string.IsNullOrEmpty(r.slot)) { if (r.item == null) r.item = ""; wanted.Add(r); if (r.item.Length > 0 && !owned.Contains(r.item)) owned.Add(r.item); } }
            if (!startPaid && MarketCfg.Get().enabled && ledger.Coins == 0 && ledger.Sales.Count == 0) { ledger.Coins = StartCoins(); startPaid = true; }   // CAMPPLAY v1: a save made with the trader off still gets the start coins once (written at the next Save)
            if (LootCfg.Get().debugLog) Debug.Log("[LootUI] save loaded: " + owned.Count + " owned, " + wanted.Count + " worn (" + p + ")");
        }
        catch (Exception e) { LastError = e.Message; Debug.LogWarning("[LootUI] save read failed, starting empty: " + e.Message); owned.Clear(); wanted.Clear(); ledger.Clear(); }
    }

    /// <summary>Write owned + the slots' current contents.</summary>
    public static void Save()
    {
        EnsureLoaded(); if (!LootCfg.Get().save.enabled) return;
        try
        {
            var slots = LootBridge.Slots; var rows = new List<SlotRow>(); var defs = LootCfg.Get().slots;
            if (!Applied) rows.AddRange(wanted);
            else
            {
                for (int i = 0; i < defs.Length; i++)
                {
                    if (defs[i] == null || defs[i].id.Length == 0) continue;
                    rows.Add(new SlotRow { slot = defs[i].id, item = slots.GetEquipped(defs[i].id) ?? "" });   // empty slots are saved too ("" = was taken off), so a starting loadout does not come back
                }
                wanted.Clear(); wanted.AddRange(rows);
            }
            var f = new FileData { owned = owned.ToArray(), equipped = rows.ToArray(), coins = ledger.Coins, seq = ledger.Seq, sales = ledger.Sales.ToArray(), startPaid = startPaid };   // STALL v1   // CAMPPLAY v1
            string p = Path_, dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = p + ".tmp"; File.WriteAllText(tmp, JsonUtility.ToJson(f, true));
            if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);   // STALL v1: one atomic swap, the coins are in this file (was Delete + Move: a crash between left no file)
             LastError = ""; SaveSlot.LootChanged();   // REBIND v100: the save slot keeps a pointer + counts
            if (LootCfg.Get().debugLog) Debug.Log("[LootUI] saved " + p);
        }
        catch (Exception e) { LastError = e.Message; Debug.LogWarning("[LootUI] save write failed: " + e.Message); }
    }

    /// <summary>Put the saved selection back on the wearer (after a scene load / when a new IGearSlots registers). Items the catalog no longer knows are skipped.</summary>
    public static int Apply()
    {
        EnsureLoaded(); var slots = LootBridge.Slots; var to = slots as IGearSlotsTo; int n = 0; Applied = true; bool newlyOwned = false;
        for (int i = 0; i < wanted.Count; i++)
        {
            var r = wanted[i];
            if (r.item.Length == 0)   // taken off last time ...
            {
                string cur = slots.GetEquipped(r.slot);
                if (cur.Length > 0)
                {
                    // v109c SHIELDFIX: ... unless the worn item is a roster loadout item this save never knew (Dom's save is from before the shield existed: every slot '' = nothing was ever offered). Keep it on and own it;
                    // an item the save DOES own that is now off was really taken off: respected.
                    if (!owned.Contains(cur)) { owned.Add(cur); newlyOwned = true; Debug.Log("[LootUI] save row '" + r.slot + "' is empty but '" + cur + "' is a loadout item the save never knew: kept on and now owned"); }
                    else slots.Unequip(r.slot);
                }
                continue;
            }
            GearItem it; if (!LootBridge.Catalog.TryGet(r.item, out it)) continue;
            if (slots.GetEquipped(r.slot) == r.item) { n++; continue; }
            bool ok = to != null ? to.EquipTo(r.item, r.slot) : slots.Equip(r.item);
            if (ok) n++;
        }
        if (newlyOwned) Save();   // v109c SHIELDFIX: write the new owned item + the real rows, once, after the whole selection is back on
        return n;
    }
}
