using System.Collections.Generic;
using UnityEngine;

// BOSSHOLD v1 (laneBOSSHOLD): a per-zone DATA flag, default OFF. zones.json zones[].bossHold { enabled, boss, waveGroups, wavePrefix, maxHoldS } (class ZBossHold in ZoneData.cs).
// While ANY enabled pre-boss wave row of that zone (spawn id = wavePrefix + group + "_" + n, e.g. bandit_z4_p_1) is alive (spawned and not dead, or alive on its save record and about to appear),
// the zone's boss spawn row `boss` (e.g. z4_grendel) neither ENGAGES (ZoneRuntime.Engagement skips it, so its AIBrain stays off and df.opponent stays null: no boss bar, BOSSBAR v1)
// nor AUTO-WALKS to Joan (ChaseAgent.Update returns while HoldsBoss, so no Chase mode: the boss stands idle at its spot). When the last wave member dies (or after maxHoldS > 0 seconds) the boss is
// released for good (that body only) and behaves exactly as before. Flag absent / enabled false / no matching boss row = nothing in this file changes anything.
public sealed partial class ZoneRuntime
{
    readonly Dictionary<DuelFighter, bool> bhHeld = new Dictionary<DuelFighter, bool>();
    readonly Dictionary<DuelFighter, float> bhSince = new Dictionary<DuelFighter, float>();
    readonly HashSet<DuelFighter> bhDone = new HashSet<DuelFighter>();
    float bhNext; const float BhEveryS = 0.1f; const int BhPruneAbove = 32;

    /// <summary>True while this fighter is a held boss (ChaseAgent reads it every frame: a hold must also stop the auto-walk during the arrival cover, when Engagement does not run).</summary>
    public static bool HoldsBoss(DuelFighter f) { bool h; return Inst != null && f != null && Inst.bhHeld.TryGetValue(f, out h) && h; }
    bool IsHeld(Live l) { bool h; return l != null && l.df != null && bhHeld.TryGetValue(l.df, out h) && h; }
    static bool BhIsWave(ZSpawn r, ZBossHold bh)
    {
        foreach (var g in bh.waveGroups) if (!string.IsNullOrEmpty(g) && r.id.StartsWith(bh.wavePrefix + g + "_", System.StringComparison.Ordinal)) return true;
        return false;
    }
    bool BhWaveAlive(ZZone z, ZBossHold bh)
    {
        long now = ZoneCore.NowMs();
        foreach (var row in z.spawns)
        {
            if (row == null || !row.enabled || !BhIsWave(row, bh)) continue;
            Live q; if (live.TryGetValue(row.id, out q)) { if (!q.killed && q.df != null && !q.df.Dead) return true; continue; }
            if (!ZoneCore.Look(ZoneSave.Rec(z.id, row.id), now).alive) continue;   // killed and still waiting for its respawn clock
            if (row.picks.Length > 0 && row.picks[0] != null && Template(row.picks[0].creature) != null) return true;   // alive on its record, not spawned yet (CanAppear): it counts, a creature with no template can never appear so it does not
        }
        return false;
    }
    void BossHoldTick()
    {
        if (Time.unscaledTime < bhNext) return; bhNext = Time.unscaledTime + BhEveryS; bhHeld.Clear();
        foreach (var l in live.Values)
        {
            if (l.df == null || l.row == null || l.killed || l.engaged || l.df.Dead) continue;
            var z = ZoneData.Zone(l.zoneId); var bh = z != null ? z.bossHold : null;
            if (bh == null || !bh.enabled || bh.boss != l.row.id || bhDone.Contains(l.df)) continue;
            float t0; if (!bhSince.TryGetValue(l.df, out t0)) { t0 = Time.unscaledTime; bhSince[l.df] = t0; Debug.Log("[BossHold] " + l.row.id + " waits for wave " + string.Join(",", bh.waveGroups)); }
            bool timeout = bh.maxHoldS > 0f && Time.unscaledTime - t0 > bh.maxHoldS;
            if (timeout || !BhWaveAlive(z, bh)) { bhDone.Add(l.df); Debug.Log("[BossHold] " + l.row.id + " released (" + (timeout ? "maxHoldS " + bh.maxHoldS.ToString("F0") + " s" : "wave dead") + ", held " + (Time.unscaledTime - t0).ToString("F1") + " s)"); continue; }
            bhHeld[l.df] = true;
        }
        if (bhDone.Count > BhPruneAbove || bhSince.Count > BhPruneAbove)   // bodies of earlier zone lives are destroyed: drop their keys
        {
            var gone = new List<DuelFighter>(); foreach (var k in bhSince.Keys) if (k == null) gone.Add(k); foreach (var k in bhDone) if (k == null) gone.Add(k);
            foreach (var k in gone) { bhSince.Remove(k); bhDone.Remove(k); }
        }
    }
}
