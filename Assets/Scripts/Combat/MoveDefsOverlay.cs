using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// laneGUARD2 v82 (NEW). Adds the move rows that exist in Assets/Data/move-defs.json (StreamingAssets copy in the player) but not in the baked Moves.asset
// (heavy_left / heavy_right / heavy_thrust / light_overhead: the asset was imported from the V3 file once and nobody re-imports it, so the v70 JSON rows never reached the game
// and controls.json attack.map fell back to heavy_overhead). Rows already in the asset are NEVER touched (the asset stays authoritative for every shipped move, so no balance drift).
// Called once by MoveTable.Get when it builds its lookup; the asset's own list is not modified (nothing to save / dirty).
public static class MoveDefsOverlay
{
    [Serializable] class Chained { public int windup, active, recovery; }
    [Serializable] class Chain { public int window; public string[] follow; }
    [Serializable] class VsGuard { public float stagger, staminaDamage; }
    [Serializable] class MoveJ
    {
        public string id, direction, path;
        public int windup, active, recovery;
        public Chained chained; public Chain chain; public VsGuard vsGuard;
        public float damage, stamina, staminaDamage, stagger, poise, poiseFrom, chip, knockback, stepIn, reach, posture, chamber, arcDeg;   // ABILITIES v1: arcDeg
        public bool breaksGuard, parryable, charges;
    }
    [Serializable] class MovesJ { public MoveJ[] moves; }

    public static void Merge(Dictionary<string, MoveDef> map)
    {
        try
        {
            foreach (var p in new[] { Application.streamingAssetsPath + "/move-defs.json", Application.dataPath + "/Data/move-defs.json" })
            {
                if (!File.Exists(p)) continue;
                var mj = JsonUtility.FromJson<MovesJ>(DataPath.ReadFile(p));
                if (mj == null || mj.moves == null) return;
                string added = "";
                foreach (var m in mj.moves)
                {
                    if (m == null || string.IsNullOrEmpty(m.id) || map.ContainsKey(m.id)) continue;
                    if (m.windup <= 0 && m.active <= 0) continue;   // not a full row (no timings): never add a half-filled move
                    var d = new MoveDef
                    {
                        id = m.id, direction = m.direction, path = m.path, windup = m.windup, active = m.active, recovery = m.recovery,
                        damage = m.damage, stamina = m.stamina, staminaDamage = m.staminaDamage, stagger = m.stagger, poise = m.poise, poiseFrom = m.poiseFrom,
                        chip = m.chip, knockback = m.knockback, stepIn = m.stepIn, reach = m.reach, posture = m.posture, chamber = m.chamber, arcDeg = m.arcDeg,
                        breaksGuard = m.breaksGuard, parryable = m.parryable, charges = m.charges
                    };
                    if (m.chained != null && m.chained.windup > 0) { d.hasChained = true; d.chainedWindup = m.chained.windup; d.chainedActive = m.chained.active; d.chainedRecovery = m.chained.recovery; }
                    if (m.chain != null && m.chain.follow != null && m.chain.follow.Length > 0) { d.chainWindow = m.chain.window; d.chainFollow = m.chain.follow; }
                    if (m.vsGuard != null && m.vsGuard.stagger > 0) { d.hasVsGuard = true; d.vsGuardStagger = m.vsGuard.stagger; d.vsGuardStaminaDamage = m.vsGuard.staminaDamage; }
                    map[d.id] = d; added += (added.Length > 0 ? ", " : "") + d.id;
                }
                if (added.Length > 0) Debug.Log("[MoveDefsOverlay] rows added from " + Path.GetFileName(p) + " (not in Moves.asset): " + added);
                return;
            }
        }
        catch (Exception ex) { Debug.LogWarning("[MoveDefsOverlay] skipped: " + ex.Message); }
    }
}
