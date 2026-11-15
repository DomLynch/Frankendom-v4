using System;
using System.IO;
using UnityEngine;

// k11 (bandits dev): permanent silver upgrades. upgrade_ranks.json (StreamingAssets first, then Assets/Data): one row per rank item {item, field, mul, label}.
// The rank is an ordinary gear item (gear.json, slot "") bought once with silver at the Bric-a-brac stall through the existing Buy tab (MarketLedger / LootSave.CommitBuy takes the coins and
// writes the item to loot_save.json owned[] in ONE save). Owning the item multiplies Joan's Progression number: field hp | stamina | damage (Progression.Total kinds 0..2) or a perk field
// (blockCost blockChip attackCost postureTaken parryWindow moveSpeed). Ranks of one field multiply.
// No file, "enabled": false, or no rank owned = every Mul returns 1 (old behaviour byte for byte).
// Hooks (one line each, Progression.cs): Total(kind) and PerkMul(f, field) multiply their result by UpgradeRanks.Mul(...). Both sit after Progression's own enabled / Joan-only checks.
public static class UpgradeRanks
{
    [Serializable] public class Row { public string item = "", field = "", label = ""; public float mul = 1f; }
    [Serializable] public class Cfg { public bool enabled = false; public Row[] ranks = new Row[0]; }

    static Cfg c;
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "upgrade_ranks.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/upgrade_ranks.json");
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[UpgradeRanks] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.ranks == null) c.ranks = new Row[0];
        return c;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; }

    /// <summary>Product of the multipliers of the owned ranks of this field. 1 when off, no rows or nothing owned.</summary>
    public static float Mul(string field)
    {
        var k = Get(); if (!k.enabled || string.IsNullOrEmpty(field)) return 1f; float m = 1f;
        for (int i = 0; i < k.ranks.Length; i++) { var r = k.ranks[i]; if (r != null && r.field == field && r.item.Length > 0 && LootSave.Owns(r.item)) m *= r.mul; }
        return m;
    }
}
