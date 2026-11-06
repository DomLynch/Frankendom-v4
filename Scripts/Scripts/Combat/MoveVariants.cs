using System;
using System.IO;
using UnityEngine;

// v99 (QC5): Pitborn's 4 new attack rows (heavy_left / heavy_right / heavy_thrust / light_overhead) never fired: an AI fighter always started heavy_overhead / the alternating light_left|right
// (DuelFighterDir.DirPick fallback). Data: move_variants.json rows { character (roster id, @enemy / @player / *), heavy[], light[] } of { id, weight }; id "$fallback" = the old default move.
// Picked with a deterministic hash of (sim tick, fighter name, per-fighter counter, AIBrain.GlobalSeed): no UnityEngine.Random, the same seed = the same fight. A row only applies when the move exists
// in the fighter's move table. Missing file / enabled=false / no row = exactly the old behaviour. Only used when there is no live directed press (so the player's directional input is untouched).
public static class MoveVariants
{
    [Serializable] class W { public string id = ""; public float weight = 1f; }
    [Serializable] class Row { public string character = ""; public W[] heavy, light; }
    [Serializable] class Data { public bool enabled = true; public Row[] rows; }
    static Data d; static bool loaded;
    static readonly System.Collections.Generic.Dictionary<DuelFighter, int> counters = new System.Collections.Generic.Dictionary<DuelFighter, int>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { d = null; loaded = false; counters.Clear(); }

    static Data Get()
    {
        if (loaded) return d; loaded = true; d = new Data { enabled = false };
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "move_variants.json"), Path.Combine(Application.dataPath, "Data/move_variants.json") })
        {
            try { if (File.Exists(p)) { var r = JsonUtility.FromJson<Data>(File.ReadAllText(p)); if (r != null) d = r; break; } } catch (Exception) { }
        }
        return d;
    }

    public static string Pick(DuelFighter df, string kind, string fallback)
    {
        var D = Get(); if (D == null || !D.enabled || D.rows == null || df == null) return fallback;
        string id = RosterResolver.IdOfName(df.name); Row row = null;
        for (int i = 0; i < D.rows.Length; i++) { var r = D.rows[i]; if (r != null && RosterResolver.KeyIs(r.character, id)) { row = r; break; } }
        if (row == null) return fallback;
        var list = kind == "heavy" ? row.heavy : kind == "light" ? row.light : null;
        if (list == null || list.Length == 0) return fallback;
        float total = 0f; for (int i = 0; i < list.Length; i++) if (list[i] != null && list[i].weight > 0f) total += list[i].weight;
        if (total <= 0f) return fallback;
        int n; counters.TryGetValue(df, out n); counters[df] = n + 1;
        unchecked
        {
            uint h = (uint)Duel.TickCount * 2654435761u ^ (uint)df.name.GetHashCode() ^ (uint)n * 40503u ^ (uint)AIBrain.GlobalSeed * 2246822519u;
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            float x = (h % 10000u) / 10000f * total;
            for (int i = 0; i < list.Length; i++)
            {
                var w = list[i]; if (w == null || w.weight <= 0f) continue;
                if (x < w.weight) { string pick = w.id == "$fallback" ? fallback : w.id; return pick; }
                x -= w.weight;
            }
        }
        return fallback;
    }
}
