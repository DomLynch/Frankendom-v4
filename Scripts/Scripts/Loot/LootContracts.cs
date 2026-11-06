using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// LOOTUI v85: the seam between the loot / equip UI (this lane) and the gear system (lane GEAR: GearSlots / GearDrop / gear.json).
// The UI never names GearSlots. It talks to IGearSlots (what is worn) and IGearCatalog (what an item id is), both reachable through LootBridge.
// With no gear system present the bridge falls back to LootLocalSlots (in memory + the save file) and LootDataCatalog (gear.json, else loot_ui.json "fallbackItems"),
// so the pickup marker and the equip screen work and persist on their own; they just have no visual effect on the body.
//
// MERGE (two lines on the GEAR side, nothing else):
//   GearSlots (the player's component): implements IGearSlots (or IGearSlotsTo); in Awake / OnEnable:  LootBridge.Slots = this;   in OnDestroy:  if (LootBridge.Slots == (IGearSlots)this) LootBridge.Slots = null;
//   GearDrop (rolls the kill drop):      after it decides an item:                                       LootBridge.RaiseLootDropped(itemId, worldPos);   and once at boot:  LootBridge.HasDropSource = true;
// If GEAR already has its own drop event, forward it:  GearDrop.OnLootDropped += LootBridge.RaiseLootDropped;  (same signature: string itemId, Vector3 worldPos).

/// <summary>What the wearer has equipped. Slot ids are the gear.json slot ids (head, chest, arms, legs, mainHand, offHand, remnant1, remnant2).</summary>
public interface IGearSlots
{
    /// <summary>Wear the item in the slot its definition says (a slot "group" picks a free slot of the group). False = refused (unknown id, no matching slot).</summary>
    bool Equip(string itemId);
    /// <summary>Take off whatever is in the slot. No-op for an empty / unknown slot.</summary>
    void Unequip(string slot);
    /// <summary>Item id in the slot, "" when empty.</summary>
    string GetEquipped(string slot);
}

/// <summary>Optional: equip into a named slot (needed to choose remnant1 vs remnant2, or main vs off hand). The UI uses it when the implementation has it, else Equip(id) after Unequip(slot) and reads the result back.</summary>
public interface IGearSlotsTo : IGearSlots
{
    bool EquipTo(string itemId, string slot);
}

[Serializable] public class GearStat { public string stat = ""; public float value = 0f; }
/// <summary>The fields the UI needs about an item (name, slot or slot group, rarity, flavour text, stats as a list so JsonUtility can read it).</summary>
[Serializable]
public class GearItem
{
    public string id = "", name = "", slot = "", rarity = "", desc = "";
    public GearStat[] stats = new GearStat[0];
    public float Stat(string key) { if (stats != null) for (int i = 0; i < stats.Length; i++) if (stats[i] != null && stats[i].stat == key) return stats[i].value; return 0f; }
    public bool Has(string key) { if (stats != null) for (int i = 0; i < stats.Length; i++) if (stats[i] != null && stats[i].stat == key) return true; return false; }
}

public interface IGearCatalog
{
    bool TryGet(string itemId, out GearItem item);
}

public static class LootBridge
{
    static IGearSlots slots; static IGearCatalog catalog;
    /// <summary>The live gear component when GearSlots registered itself, else the local fallback (never null).</summary>
    public static IGearSlots Slots
    {
        get { if (slots != null) return slots; return LootLocalSlots.Instance; }
        set { slots = value; }
    }
    public static bool SlotsAreLocal { get { return slots == null; } }
    public static IGearCatalog Catalog
    {
        get { if (catalog != null) return catalog; return LootDataCatalog.Instance; }
        set { catalog = value; }
    }

    /// <summary>True once a real drop roller exists (GearDrop sets it). While false the UI may spawn loot_ui.json fallbackDrops on a victory so the flow can be seen and tested.</summary>
    public static bool HasDropSource;
    /// <summary>(itemId, worldPos): an item should appear on the ground. The marker is shown after the fight on victory (queued until then).</summary>
    public static event Action<string, Vector3> OnLootDropped;
    public static void RaiseLootDropped(string itemId, Vector3 worldPos)
    {
        HasDropSource = true;
        if (OnLootDropped != null) OnLootDropped(itemId, worldPos);
    }
    /// <summary>Raised by the equip screen after a change (HUD / audio can listen). slot, itemId ("" = emptied).</summary>
    public static event Action<string, string> OnEquippedChanged;
    public static void RaiseEquippedChanged(string slot, string itemId) { if (OnEquippedChanged != null) OnEquippedChanged(slot, itemId ?? ""); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { slots = null; catalog = null; HasDropSource = false; OnLootDropped = null; OnEquippedChanged = null; LootLocalSlots.Reset(); LootDataCatalog.Reset(); }
}

/// <summary>In-memory slots used when no GearSlots is registered. State is only what LootSave puts in and the UI changes.</summary>
public sealed class LootLocalSlots : IGearSlotsTo
{
    static LootLocalSlots inst;
    public static LootLocalSlots Instance { get { if (inst == null) inst = new LootLocalSlots(); return inst; } }
    public static void Reset() { inst = null; }
    readonly Dictionary<string, string> worn = new Dictionary<string, string>();

    public string GetEquipped(string slot) { string id; return !string.IsNullOrEmpty(slot) && worn.TryGetValue(slot, out id) ? id : ""; }
    public void Unequip(string slot) { if (!string.IsNullOrEmpty(slot)) worn.Remove(slot); }
    public bool Equip(string itemId)
    {
        GearItem it; if (string.IsNullOrEmpty(itemId) || !LootBridge.Catalog.TryGet(itemId, out it)) return false;
        string first = null;
        var rows = LootCfg.Get().slots;
        for (int i = 0; i < rows.Length; i++)
        {
            var s = rows[i]; if (s == null || !LootCfg.Fits(it, s)) continue;
            if (GetEquipped(s.id).Length == 0) return EquipTo(itemId, s.id);
            if (first == null) first = s.id;
        }
        return first != null && EquipTo(itemId, first);
    }
    public bool EquipTo(string itemId, string slot)
    {
        GearItem it; if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(slot) || !LootBridge.Catalog.TryGet(itemId, out it)) return false;
        var s = LootCfg.Slot(slot); if (s == null || !LootCfg.Fits(it, s)) return false;
        var keys = new List<string>(worn.Keys); for (int i = 0; i < keys.Count; i++) if (worn[keys[i]] == itemId) worn.Remove(keys[i]);   // an item sits in one slot only
        worn[slot] = itemId; return true;
    }
}

/// <summary>Item definitions. Reads gear.json in the lane GEAR schema (items[]: id, name, slot, stats{armor, poise, weight}; slots[]: id, group), then loot_ui.json "fallbackItems" for any id it does not know.</summary>
public sealed class LootDataCatalog : IGearCatalog
{
    static LootDataCatalog inst;
    public static LootDataCatalog Instance { get { if (inst == null) { inst = new LootDataCatalog(); inst.Load(); } return inst; } }
    public static void Reset() { inst = null; }

    // gear.json shape (GearTable.Data / Item / Stats): only what the UI reads. Unknown fields are ignored by JsonUtility.
    [Serializable] class GStats { public float armor, poise, weight; }
    [Serializable] class GItem { public string id = "", name = "", slot = "", rarity = "", desc = ""; public GStats stats = new GStats(); public GearStat[] statList; }
    [Serializable] class GFile { public GItem[] items; }

    readonly Dictionary<string, GearItem> byId = new Dictionary<string, GearItem>();
    public int Count { get { return byId.Count; } }
    public IEnumerable<GearItem> All { get { return byId.Values; } }

    public bool TryGet(string itemId, out GearItem item)
    {
        item = null; if (string.IsNullOrEmpty(itemId)) return false;
        return byId.TryGetValue(itemId, out item);
    }

    void Load()
    {
        var C = LootCfg.Get();
        string json = LootCfg.ReadData(C.catalog.gearFile);
        if (json != null)
        {
            try
            {
                var f = JsonUtility.FromJson<GFile>(json);
                if (f != null && f.items != null)
                    for (int i = 0; i < f.items.Length; i++)
                    {
                        var g = f.items[i]; if (g == null || string.IsNullOrEmpty(g.id)) continue;
                        var it = new GearItem { id = g.id, name = string.IsNullOrEmpty(g.name) ? LootCfg.Pretty(g.id) : g.name, slot = g.slot ?? "", rarity = g.rarity ?? "", desc = g.desc ?? "" };
                        var list = new List<GearStat>();
                        if (g.stats != null)
                        {   // gear.json stats{armor, poise, weight}: key names from loot_ui.json catalog.gearStatKeys (same order), zero = not listed
                            var keys = C.catalog.gearStatKeys; float[] vals = { g.stats.armor, g.stats.poise, g.stats.weight };
                            for (int s = 0; s < vals.Length && s < keys.Length; s++) if (Mathf.Abs(vals[s]) > 0f) list.Add(new GearStat { stat = keys[s], value = vals[s] });
                        }
                        if (g.statList != null) for (int q = 0; q < g.statList.Length; q++) if (g.statList[q] != null && !string.IsNullOrEmpty(g.statList[q].stat)) list.Add(g.statList[q]);
                        it.stats = list.ToArray(); byId[it.id] = it;
                    }
            }
            catch (Exception e) { Debug.LogWarning("[LootUI] " + C.catalog.gearFile + " parse failed, fallback items only: " + e.Message); }
        }
        // loot_ui.json "fallbackItems": whole rows for ids gear.json does not have; for ids it has, they fill what gear.json leaves out (rarity, flavour text, stats)
        for (int i = 0; i < C.fallbackItems.Length; i++)
        {
            var f = C.fallbackItems[i]; if (f == null || string.IsNullOrEmpty(f.id)) continue;
            if (f.stats == null) f.stats = new GearStat[0];
            GearItem have;
            if (byId.TryGetValue(f.id, out have))
            {
                if (string.IsNullOrEmpty(have.rarity)) have.rarity = f.rarity ?? "";
                if (string.IsNullOrEmpty(have.desc)) have.desc = f.desc ?? "";
                if ((have.stats == null || have.stats.Length == 0) && f.stats.Length > 0) have.stats = f.stats;
                continue;
            }
            if (string.IsNullOrEmpty(f.name)) f.name = LootCfg.Pretty(f.id);
            byId[f.id] = f;
        }
    }
}
