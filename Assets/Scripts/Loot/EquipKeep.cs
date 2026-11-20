using System;
using System.IO;
using UnityEngine;

// EQUIPKEEP v1 (Dom 20 Nov, "when equipping the bow or a new weapon you lose the sword and shield: all weapons should stay in your inventory").
// Root cause: the carried list of the equipment screen is LootSave.Owned. An item only gets into it on pickup / buy, or when the screen is OPENED while it is worn (LootScreen.OpenScreen).
// (1) Joan's starting sword is not an item at all (the default sword model, WeaponSwap id ""): it was never carried and could not be listed. (2) A worn item that was never opened in the screen
// (the loadout shield, taken off by the two-hand rule when the bow goes on) was dropped from the worn slot without ever being carried.
// This file: (a) Seed() keeps the ids of equip_keep.json alwaysOwned (joan_sword) in the carried list on every load, (b) Hook() listens to GearSlots.OnUnequipped (every replace, two-hand take-off and take off
// goes through it) and makes sure the item that left a player slot is carried. No Save here (the event fires inside Equip, before the new item is in the slot): every UI path saves right after.
// Hooked from LootSave.HookErase (an existing AfterAssembliesLoaded method), no new RuntimeInitializeOnLoadMethod, no serialized field. Player only. Missing file or enabled false = old behaviour.
public static class EquipKeep
{
    [Serializable] public class Cfg { public bool enabled = false, log = true, keepDisplaced = true; public string mainSlot = "mainHand", offSlot = "offHand"; public string[] alwaysOwned = new string[0]; }
    static Cfg cfg;
    static Cfg Get()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, "equip_keep.json"); if (!File.Exists(path)) path = Path.Combine(Application.dataPath, "Data/equip_keep.json");
            if (File.Exists(path)) { var read = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(path).TrimStart((char)0xFEFF)); if (read != null) cfg = read; }
        }
        catch (Exception e) { Debug.LogWarning("[Equip] equip_keep.json read failed, off: " + e.Message); cfg = new Cfg(); }
        return cfg;
    }
    public static void Reset() { cfg = null; }
    public static void Hook() { GearSlots.OnUnequipped -= OnLeft; GearSlots.OnUnequipped += OnLeft; }

    /// <summary>Put the always-carried ids in the carried list (memory only; the next Save writes them). Returns how many were new.</summary>
    public static int Seed()
    {
        var k = Get(); int added = 0; if (!k.enabled || k.alwaysOwned == null) return 0;
        for (int i = 0; i < k.alwaysOwned.Length; i++)
        {
            string id = k.alwaysOwned[i]; if (string.IsNullOrEmpty(id) || GearTable.Find(id) == null) continue;
            if (LootSave.Own(id)) { added++; if (k.log) Debug.Log("[Equip] " + id + " -> carried (always carried)"); }
        }
        return added;
    }

    static void OnLeft(GearSlots wearer, GearTable.Item item, string slot)
    {
        var k = Get(); if (!k.enabled || !k.keepDisplaced || wearer == null || item == null || !Roster.IsPlayer(wearer.CharacterId)) return;
        bool fresh = LootSave.Own(item.id);
        if (!k.log) return;
        string why = "slot " + slot + " emptied";
        if (slot == k.offSlot) { var main = GearTable.Find(wearer.ItemIn(k.mainSlot)); if (main != null && main.weapon != null && main.weapon.twoHanded) why = "two-handed " + main.id; }
        Debug.Log("[Equip] " + item.id + " -> carried (" + why + (fresh ? ", was not carried" : "") + ")");
    }
}
