using System;
using UnityEngine;

// LOOTUI v85 -> GEAR bridge. MERGE THIS FILE ONLY AFTER lane GEAR (GearSlots.cs / GearDrop.cs / GearTable.cs) is in Assets: it is the only loot file that names GEAR types.
// Without it the loot UI runs on LootLocalSlots (no body effect). With it:
//   1. the player's GearSlots is wrapped as the UI's IGearSlotsTo (Equip(id, slot) / Unequip / ItemIn), re-bound every 0.5 s when the scene (and its GearSlots) is rebuilt;
//   2. GearDrop.OnDropped (a data-only GearPickup marker, no art) becomes LootBridge.RaiseLootDropped(itemId, pos): the UI shows its own pillar + name after the victory and
//      the GEAR marker object is destroyed. Collecting happens in the UI (carry list + equip screen), so gear.json drop.autoCollect must stay false.
// No GEAR file is edited.
public static class LootGearAdapter
{
    sealed class Wrap : IGearSlotsTo
    {
        public readonly GearSlots g;
        public Wrap(GearSlots g) { this.g = g; }
        public bool Equip(string itemId) { return g != null && g.Equip(itemId); }
        public bool EquipTo(string itemId, string slot) { return g != null && g.Equip(itemId, slot); }
        public void Unequip(string slot) { if (g != null) g.Unequip(slot); }
        public string GetEquipped(string slot) { return g != null ? g.ItemIn(slot) : ""; }
    }

    sealed class Binder : MonoBehaviour
    {
        float next;
        void Update()
        {
            if (Time.unscaledTime < next) return; next = Time.unscaledTime + 0.5f;
            GearSlots best = null;
            foreach (var g in FindObjectsByType<GearSlots>()) if (g != null && Roster.IsPlayer(g.CharacterId)) { best = g; break; }
            if (best == null) { if (wrapped != null && wrapped.g == null) { wrapped = null; LootBridge.Slots = null; } return; }   // wearer gone (scene reload): back to the local slots until the next one appears
            if (wrapped == null || wrapped.g != best) { wrapped = new Wrap(best); LootBridge.Slots = wrapped; }
        }
    }

    static Wrap wrapped; static Binder binder;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { wrapped = null; binder = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            var G = GearTable.Get(); if (!G.enabled) return;
            GearDrop.OnDropped -= OnDropped; GearDrop.OnDropped += OnDropped;
            if (G.drop.enabled) LootBridge.HasDropSource = true;
            if (binder == null) { var go = new GameObject("LootGearBinder"); UnityEngine.Object.DontDestroyOnLoad(go); binder = go.AddComponent<Binder>(); }
        }
        catch (Exception e) { Debug.LogWarning("[LootUI] gear adapter boot failed: " + e.Message); }
    }

    static void OnDropped(GearPickup p, DuelFighter killer)
    {
        if (p == null) return;
        LootBridge.RaiseLootDropped(p.itemId, p.transform.position);
        UnityEngine.Object.Destroy(p.gameObject);   // the UI owns the visible marker
    }
}
