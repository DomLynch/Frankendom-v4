using System;
using System.Collections.Generic;
using UnityEngine;

// Data ported from Frankendom V3 (src/fight/move-defs.json, rules-defs.json). All timings are whole 60 Hz ticks.
[Serializable]
public class MoveDef
{
    public string id, direction, path;
    public int windup, active, recovery;
    public bool hasChained; public int chainedWindup, chainedActive, chainedRecovery;
    public int chainWindow; public string[] chainFollow;
    public float damage, stamina, staminaDamage, stagger, poise, poiseFrom, chip, knockback, stepIn, reach, posture, chamber;
    public bool breaksGuard, parryable, charges;
    public float weaponStaminaMul;   // WEAPONMOVES v1: 0 = 1 (not an initialiser, so Moves.asset rows without the field read as neutral)
    public float arcDeg;   // ABILITIES v1: per-move hit arc in degrees, 0 = the global a.HitArcDeg (Duel.Resolve)
    public MoveDef Copy() { return (MoveDef)MemberwiseClone(); }
    public bool hasVsGuard; public float vsGuardStagger, vsGuardStaminaDamage;
    public int Windup(bool chained) { return chained && hasChained ? chainedWindup : windup; }
    public int Active(bool chained) { return chained && hasChained ? chainedActive : active; }
    public int Recovery(bool chained) { return chained && hasChained ? chainedRecovery : recovery; }
}


