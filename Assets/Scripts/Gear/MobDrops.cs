using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// MOBDROPS v1 (FK1 loot, STAGED): a per-victim drop table on top of GearDrop. mob_drops.json (StreamingAssets first, then Assets/Data):
//   { "enabled": true, "rows": [ { "creature": "NpcBogHag", "chance": 0.2, "count": 1, "tiers": [ {"tier":"common","w":65}, ... ], "items": [ {"id":"joan_boots","tier":"common","w":1}, ... ] } ] }
// GearDrop.Roll calls MobDrops.Append(victimId, r, win) once per kill: roll chance, then `count` times pick a tier (weights of tiers that have items), then an item of that tier (weights).
// A missing file, enabled false, an unknown victim or an unknown item id = nothing added = the old behaviour. Uses the caller's System.Random (never UnityEngine.Random).
public static class MobDrops
{
    [Serializable] public class TierW { public string tier = ""; public float w = 0f; }
    [Serializable] public class ItemW { public string id = "", tier = ""; public float w = 1f; }
    [Serializable] public class Row { public string creature = "", zone = ""; public float chance = 0f; public int count = 1; public TierW[] tiers = new TierW[0]; public ItemW[] items = new ItemW[0]; }
    [Serializable] public class Cfg { public bool enabled = false; public Row[] rows = new Row[0]; }

    static Cfg c; public static int Wins;   // counter for proofs
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "mob_drops.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/mob_drops.json");
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[MobDrops] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.rows == null) c.rows = new Row[0];
        return c;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; Wins = 0; }

    // adds the table's items for victimId to win (GearDrop.Roll's list). Returns how many were added.
    public static int Append(string victimId, System.Random r, List<GearTable.Item> win)
    {
        var k = Get(); if (!k.enabled || r == null || win == null || string.IsNullOrEmpty(victimId)) return 0;
        Row row = null; for (int i = 0; i < k.rows.Length; i++) if (k.rows[i] != null && k.rows[i].creature == victimId) { row = k.rows[i]; break; }
        if (row == null || row.chance <= 0f || row.items == null || row.items.Length == 0) return 0;
        GearDrop.Rolls++;
        if (r.NextDouble() >= row.chance) return 0;
        int added = 0, n = Mathf.Max(1, row.count);
        for (int d = 0; d < n; d++)
        {
            // tier weights, only tiers that still have a usable (known, not yet dropped) item
            string tier = null; float tot = 0f;
            if (row.tiers != null) for (int t = 0; t < row.tiers.Length; t++) { var tw = row.tiers[t]; if (tw != null && tw.w > 0f && HasUsable(row, tw.tier, win)) tot += tw.w; }
            if (tot <= 0f) break;
            double x = r.NextDouble() * tot;
            for (int t = 0; t < row.tiers.Length; t++) { var tw = row.tiers[t]; if (tw == null || tw.w <= 0f || !HasUsable(row, tw.tier, win)) continue; x -= tw.w; tier = tw.tier; if (x < 0.0) break; }
            float it = 0f; for (int i = 0; i < row.items.Length; i++) if (Usable(row.items[i], tier, win)) it += Mathf.Max(0f, row.items[i].w);
            if (it <= 0f) break;
            double y = r.NextDouble() * it; GearTable.Item pick = null;
            for (int i = 0; i < row.items.Length; i++) { var iw = row.items[i]; if (!Usable(iw, tier, win) || iw.w <= 0f) continue; pick = GearTable.Find(iw.id); y -= iw.w; if (y < 0.0) break; }
            if (pick == null) break;
            win.Add(pick); added++; Wins++;
        }
        return added;
    }

    static bool HasUsable(Row row, string tier, List<GearTable.Item> win) { for (int i = 0; i < row.items.Length; i++) if (Usable(row.items[i], tier, win) && row.items[i].w > 0f) return true; return false; }
    static bool Usable(ItemW iw, string tier, List<GearTable.Item> win)
    {
        if (iw == null || iw.tier != tier || string.IsNullOrEmpty(iw.id)) return false;
        var it = GearTable.Find(iw.id); if (it == null) return false;
        for (int i = 0; i < win.Count; i++) if (win[i] != null && win[i].id == iw.id) return false;
        return true;
    }
}
