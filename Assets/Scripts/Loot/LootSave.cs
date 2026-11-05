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
    [Serializable] public class FileData { public int version = 1; public string[] owned = new string[0]; public SlotRow[] equipped = new SlotRow[0]; }

    static readonly List<string> owned = new List<string>();
    static readonly List<SlotRow> wanted = new List<SlotRow>();   // as loaded: what to put back on the wearer
    static bool loaded;
    public static bool Applied;   // the saved selection has been put on a wearer at least once; before that Save() keeps the loaded rows instead of reading an empty slot list
    public static string LastError = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { owned.Clear(); wanted.Clear(); loaded = false; Applied = false; LastError = ""; }

    public static string Path_ { get { return Path.Combine(Application.persistentDataPath, LootCfg.Get().save.file); } }
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

    public static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true; owned.Clear(); wanted.Clear();
        if (!LootCfg.Get().save.enabled) return;
        try
        {
            string p = Path_; if (!File.Exists(p)) return;
            var f = JsonUtility.FromJson<FileData>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (f == null) return;
            if (f.owned != null) for (int i = 0; i < f.owned.Length; i++) if (!string.IsNullOrEmpty(f.owned[i]) && !owned.Contains(f.owned[i])) owned.Add(f.owned[i]);
            if (f.equipped != null) for (int i = 0; i < f.equipped.Length; i++) { var r = f.equipped[i]; if (r != null && !string.IsNullOrEmpty(r.slot)) { if (r.item == null) r.item = ""; wanted.Add(r); if (r.item.Length > 0 && !owned.Contains(r.item)) owned.Add(r.item); } }
            if (LootCfg.Get().debugLog) Debug.Log("[LootUI] save loaded: " + owned.Count + " owned, " + wanted.Count + " worn (" + p + ")");
        }
        catch (Exception e) { LastError = e.Message; Debug.LogWarning("[LootUI] save read failed, starting empty: " + e.Message); owned.Clear(); wanted.Clear(); }
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
            var f = new FileData { owned = owned.ToArray(), equipped = rows.ToArray() };
            string p = Path_, dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = p + ".tmp"; File.WriteAllText(tmp, JsonUtility.ToJson(f, true));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p); LastError = "";
            if (LootCfg.Get().debugLog) Debug.Log("[LootUI] saved " + p);
        }
        catch (Exception e) { LastError = e.Message; Debug.LogWarning("[LootUI] save write failed: " + e.Message); }
    }

    /// <summary>Put the saved selection back on the wearer (after a scene load / when a new IGearSlots registers). Items the catalog no longer knows are skipped.</summary>
    public static int Apply()
    {
        EnsureLoaded(); var slots = LootBridge.Slots; var to = slots as IGearSlotsTo; int n = 0; Applied = true;
        for (int i = 0; i < wanted.Count; i++)
        {
            var r = wanted[i];
            if (r.item.Length == 0) { if (slots.GetEquipped(r.slot).Length > 0) slots.Unequip(r.slot); continue; }   // taken off last time
            GearItem it; if (!LootBridge.Catalog.TryGet(r.item, out it)) continue;
            if (slots.GetEquipped(r.slot) == r.item) { n++; continue; }
            bool ok = to != null ? to.EquipTo(r.item, r.slot) : slots.Equip(r.item);
            if (ok) n++;
        }
        return n;
    }
}
