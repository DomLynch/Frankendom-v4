using System.Collections.Generic;
using UnityEngine;

// S2 THE GATE ASSASSIN (STAGED DARK): the second half of ZoneRuntime (the class is made `partial` by apply_s2.py, one word). Lets GateAssassin.cs read the zone state and add ONE extra fighter to the live table
// the way SpawnOne does, without touching any other ZoneRuntime line. Nothing here runs unless GateAssassin calls it (gate_assassin.json enabled / -s2:1).
public sealed partial class ZoneRuntime
{
    internal static bool SurpriseState(out string zoneId, out string spot, out DuelFighter pl, out bool play)
    {
        var z = Inst; zoneId = ""; spot = ""; pl = null; play = false;
        if (z == null || !z.on || z.zone == null || z.player == null) return false;
        zoneId = z.zone.id; spot = z.lastSpot ?? ""; pl = z.player; play = z.phase == Phase.Play; return true;
    }
    internal static bool SurpriseSpotXZ(string spotId, out Vector3 pos)
    {
        pos = Vector3.zero; var z = Inst; if (z == null || z.zone == null) return false;
        var sp = ZoneData.Spot(z.zone, spotId); if (sp == null || sp.pos == null || sp.pos.Length < 2) return false;
        pos = SpotPos(sp, z.player != null ? z.player.transform.position : Vector3.zero); return true;
    }
    internal static int SurpriseEngagedCount() { var z = Inst; return z == null ? 0 : z.EngagedCount(); }
    /// <summary>Template row: the first zones.json spawn whose first pick is this creature (hp / damage / difficulty / aggro of the existing bandit rows). Null when none.</summary>
    internal static ZSpawn SurpriseRowOf(string creature)
    {
        var t = ZoneData.Get(); if (t == null || t.zones == null) return null;
        foreach (var q in t.zones) { if (q == null || q.spawns == null) continue; foreach (var s in q.spawns) if (s != null && s.picks != null && s.picks.Length > 0 && s.picks[0] != null && s.picks[0].creature == creature) return s; }
        return null;
    }
    /// <summary>Clone the creature at pos (dormant, brain off) and register it in the live table under row.id: the SpawnOne path minus the ZoneSave look. Null on any refusal.</summary>
    internal static DuelFighter SurpriseSpawn(ZSpawn row, string creature, Vector3 pos, float yaw)
    {
        var z = Inst; if (z == null || !z.on || z.zone == null || row == null) return null;
        if (z.live.ContainsKey(row.id)) return null;
        var tpl = z.Template(creature); if (tpl == null) { z.Warn("s2." + creature, "[S2] no template for " + creature); return null; }
        var go = Instantiate(tpl, pos, Quaternion.Euler(0f, yaw, 0f)); go.name = Roster.Get(creature).objectName + "#" + row.id;
        var df = go.GetComponent<DuelFighter>(); var br = go.GetComponent<AIBrain>();
        if (df == null) { Destroy(go); return null; }
        if (br != null) br.enabled = false;
        var zc = ZoneData.Creature(creature); if (zc != null && zc.source == "resources") AttachRuntime(go);
        ApplyRow(df, br, row);
        if (go.GetComponent<ArenaLimits>() == null && go.GetComponent<CharacterController>() != null) go.AddComponent<ArenaLimits>();
        go.SetActive(true); RagdollDeath.PrebuildOne(go);
        var lv = new Live { row = row, creature = creature, gen = 0, go = go, df = df, brain = br, home = pos, homeYaw = yaw, zoneId = z.zone.id };
        var cr = ChaseTable.Enabled ? ChaseTable.Row(creature) : null; if (cr != null) { lv.chase = go.AddComponent<ChaseAgent>(); lv.chase.Init(df, z.player, pos, row.leash, cr, false); }
        z.live[row.id] = lv; return df;
    }
    internal static bool SurpriseEngage(DuelFighter df)
    {
        var z = Inst; if (z == null || df == null) return false; var l = z.Find(df); if (l == null || l.killed) return false;
        z.Engage(l); return true;
    }
    internal static bool SurpriseLive(DuelFighter df) { var z = Inst; if (z == null || df == null) return false; var l = z.Find(df); return l != null && !l.killed && l.go != null; }
}
