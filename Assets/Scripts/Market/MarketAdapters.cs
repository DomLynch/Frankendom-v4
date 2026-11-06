using System;
using System.Collections.Generic;
using UnityEngine;

// STALL v1 (lane laneSTALL): the Unity side of the stall's small seams.
//   LootMarketStore  : IMarketStore on LootSave (carried list + coins + ledger, one file) and LootBridge (worn items, names)
//   MarketSafe       : the no-combat flag of the camp and the refusal of attack / guard presses (hunks in DuelFighter and PlayerBrain read it)
//   MarketWorld      : where the stall is (data x / z, or the zones.json spot when MarketZoneBridge is installed)
//   MarketHooks      : events other systems can listen to (a future shop, a HUD, audio)

public sealed class LootMarketStore : IMarketStore
{
    public int Coins { get { return LootSave.Coins; } }
    public IList<string> Carried { get { return LootSave.Owned; } }
    public bool IsWorn(string item)
    {
        if (string.IsNullOrEmpty(item)) return false;
        var defs = LootCfg.Get().slots; var slots = LootBridge.Slots;
        for (int i = 0; i < defs.Length; i++) if (defs[i] != null && defs[i].id.Length > 0 && slots.GetEquipped(defs[i].id) == item) return true;
        return false;
    }
    public string NameOf(string item) { GearItem it; return LootBridge.Catalog.TryGet(item, out it) && !string.IsNullOrEmpty(it.name) ? it.name : LootCfg.Pretty(item); }
    public MarketSale FindSale(string requestId) { return LootSave.FindSale(requestId); }
    public string NextRequestId(string item) { return LootSave.NextRequestId(item); }
    public bool CommitSale(string requestId, string item, int price, int keep, int cap) { return LootSave.CommitSale(requestId, item, price, keep, cap); }
    public bool CommitBuy(string requestId, string item, int price, int keep) { return LootSave.CommitBuy(requestId, item, price, keep); }   // laneNPCSPAWN
}

public static class MarketHooks
{
    /// <summary>(itemId, coins paid): raised once per completed sale (not for a replayed order).</summary>
    public static event Action<string, int> OnSold;
    public static void RaiseSold(string item, int coins) { if (OnSold != null) OnSold(item, coins); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { OnSold = null; }
}

public static class MarketSafe
{
    /// <summary>True while the player stands inside the camp rectangle and market.json is enabled.</summary>
    public static bool NoCombat;
    public static float RefusedAt = -99f;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { NoCombat = false; RefusedAt = -99f; }

    /// <summary>The player's press `what` (light, heavy, thrust, kick, special1, ...) is refused in the camp. Dodging (safe.allowPress) stays on.</summary>
    public static bool Refuses(string what)
    {
        if (HubSafe.Refuses(what)) { RefusedAt = Time.unscaledTime; return true; }   // laneNPCSPAWN: the camp rule of hub.json (works with the market switched off)
        if (!NoCombat || MarketCfg.AllowsPress(what)) return false;
        RefusedAt = Time.unscaledTime; return true;
    }
}

public static class MarketWorld
{
    /// <summary>Optional: resolve a zones.json spot (zone id, spot id) to world x / z. MarketZoneBridge sets it when the zone data exists.</summary>
    public delegate bool SpotFn(string zone, string spot, out float x, out float z);
    public static SpotFn ResolveSpot;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { ResolveSpot = null; }

    public static void StallXZ(MarketCfg.StallC s, out float x, out float z)
    {
        x = s.anchor.x; z = s.anchor.z; float rx, rz;
        if (ResolveSpot != null && !string.IsNullOrEmpty(s.anchor.zone) && !string.IsNullOrEmpty(s.anchor.spot) && ResolveSpot(s.anchor.zone, s.anchor.spot, out rx, out rz)) { x = rx; z = rz; }
    }
}
