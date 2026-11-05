using System;
using System.Collections.Generic;
using UnityEngine;

// LOOT DROP (lane GEAR v79/v80). On a kill (ContactRelay.OnKilled) the victim's character id is looked up in gear.json: every item whose dropFrom lists that id (or "*") rolls dropChance;
// up to drop.maxPerKill winners spawn a pickup MARKER at the victim (an empty GameObject with a GearPickup, no art) and GearDrop.OnDropped is raised. The pickup / equip UI is another lane:
// it listens to OnDropped / OnCollected and calls GearPickup.TryCollect(wearer). drop.autoCollect = true lets the killer's GearSlots take it at once (v80 "worn by Joan" without a UI).
// The roll uses its OWN System.Random (drop.seed 0 = time based), never UnityEngine.Random, so it can not disturb the seeded fight sim / balance bots.
public class GearPickup : MonoBehaviour
{
    public string itemId = "", victimId = ""; public int tick; public bool collected;
    public GearTable.Item Item { get { return GearTable.Find(itemId); } }
    // equip it on the wearer and remove the marker. False = nothing happened (already collected, no wearer, unknown item).
    public bool TryCollect(GearSlots wearer)
    {
        if (collected || wearer == null || !wearer.Equip(itemId)) return false;
        collected = true; GearDrop.RaiseCollected(this, wearer); Destroy(gameObject); return true;
    }
}

public static class GearDrop
{
    public static event Action<GearPickup, DuelFighter> OnDropped;      // marker spawned; killer may be null
    public static event Action<GearPickup, GearSlots> OnCollected;
    static System.Random rng;
    static readonly HashSet<DuelFighter> rolled = new HashSet<DuelFighter>();
    public static int Rolls, Drops;   // counters for proofs

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { rng = null; rolled.Clear(); Rolls = Drops = 0; OnDropped = null; OnCollected = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        ContactRelay.OnKilled -= OnKilled;   // no domain reload in the editor: never double-subscribe
        var G = GearTable.Get(); if (!G.enabled || !G.drop.enabled) return;
        ContactRelay.OnKilled += OnKilled;
        rolled.Clear();
    }

    internal static void RaiseCollected(GearPickup p, GearSlots w) { if (OnCollected != null) OnCollected(p, w); }

    // items that drop from victimId this time (also the unit-test entry: pass your own Random)
    public static List<GearTable.Item> Roll(string victimId, System.Random r)
    {
        var G = GearTable.Get(); var win = new List<GearTable.Item>();
        if (!G.enabled || string.IsNullOrEmpty(victimId)) return win;
        int max = Mathf.Max(0, G.drop.maxPerKill);
        for (int i = 0; i < G.items.Length && win.Count < max; i++)
        {
            var it = G.items[i]; if (it == null || it.dropChance <= 0f || it.dropFrom == null) continue;
            bool from = false; for (int k = 0; k < it.dropFrom.Length; k++) if (it.dropFrom[k] == victimId || it.dropFrom[k] == "*") { from = true; break; }
            if (!from) continue;
            Rolls++;
            if (r.NextDouble() < it.dropChance) win.Add(it);
        }
        return win;
    }

    static void OnKilled(DuelFighter victim, MoveDef m)
    {
        var G = GearTable.Get(); if (!G.enabled || !G.drop.enabled || victim == null) return;
        if (!rolled.Add(victim)) return;   // a kill is rolled once (the finisher path and Die() can both fire)
        if (rng == null) rng = G.drop.seed != 0 ? new System.Random(G.drop.seed) : new System.Random();
        var killer = victim.opponent; GearSlots kg = killer != null ? killer.GetComponent<GearSlots>() : null;
        foreach (var it in Roll(victim.CharacterId, rng))
        {
            if (G.drop.skipIfOwned && kg != null && kg.Has(it.id)) continue;
            Drops++;
            var go = new GameObject("GearPickup_" + it.id);
            go.transform.position = victim.transform.position + Vector3.up * G.drop.spawnHeight;
            var p = go.AddComponent<GearPickup>(); p.itemId = it.id; p.victimId = victim.CharacterId; p.tick = Duel.TickCount;
            if (G.drop.lifetimeSeconds > 0f) UnityEngine.Object.Destroy(go, G.drop.lifetimeSeconds);
            GearTable.Log("DROP " + it.id + " from " + p.victimId + " at tick " + p.tick);
            if (OnDropped != null) OnDropped(p, killer);
            if (G.drop.autoCollect && kg != null) p.TryCollect(kg);
        }
    }
}
