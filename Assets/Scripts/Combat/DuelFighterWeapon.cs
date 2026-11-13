using UnityEngine;

// WEAPONSWAP v88: the equipped weapon (gear.json items[].weapon, WeaponSwap.cs) as seen by the fight sim. DuelFighter is partial; this file holds the two seams so the hunks in the other files stay one-token edits:
//   ReachScale  = scale x the weapon's reach multiplier: replaces `.scale` in every "move.reach * fighter.scale" distance check (Duel.Resolve hit detection, the AI's reach reads of a fighter).
//   Tk(ticks)   = a move phase length (windup / active / recovery) scaled by 1 / weapon speed multiplier. The sim, the attack-clip plan (BeginAttackClip derives the clip speeds from the tick counts) and the
//                 queries (TicksLeft, WindupLeft, RecoveryLen) all go through it, so a slow weapon is slower in the sim AND in the animation. Identity (same int) for a neutral weapon.
// Both are exactly the old values for a fighter without a WeaponSwap (Pitborn, Dracula): WeaponHooks returns 1.
public partial class DuelFighter
{
    public float ReachScale { get { return scale * WeaponHooks.ReachMul(this); } }
    // WEAPONMOVES v1: the move this fighter's weapon makes of base move `id`: the base object itself when no row applies (identity), else a cached derived copy (id unchanged).
    public MoveDef WeaponMove(string id)
    {
        var b = moves != null ? moves.Get(id) : null; if (b == null) return null;
        var ws = WeaponHooks.Of(this); var rows = ws.moves; if (rows == null || rows.Length == 0) return b;
        if (ws.cache == null) ws.cache = new System.Collections.Generic.Dictionary<string, MoveDef>();
        MoveDef d; if (ws.cache.TryGetValue(id, out d)) return d;
        GearTable.MoveOverride r = null;
        for (int i = 0; i < rows.Length && r == null; i++) if (rows[i] != null && rows[i].ids != null && System.Array.IndexOf(rows[i].ids, id) >= 0) r = rows[i];
        for (int i = 0; i < rows.Length && r == null; i++) if (rows[i] != null && rows[i].ids != null && System.Array.IndexOf(rows[i].ids, "*") >= 0) r = rows[i];
        if (r == null) { ws.cache[id] = b; return b; }
        d = b.Copy();
        WmPhase(ref d.windup, ref d.chainedWindup, r.windup, r.windupMul); WmPhase(ref d.active, ref d.chainedActive, r.active, r.activeMul); WmPhase(ref d.recovery, ref d.chainedRecovery, r.recovery, r.recoveryMul);
        d.reach = r.reach > 0f ? r.reach : b.reach * r.reachMul;
        d.damage *= r.damageMul; d.posture *= r.postureMul; d.stagger *= r.staggerMul; d.staminaDamage *= r.staminaDamageMul; d.knockback *= r.knockbackMul; d.weaponStaminaMul = r.staminaMul;
        Debug.Log("[WeaponMoves] " + name + " " + ws.id + " " + id + ": ticks " + d.windup + "/" + d.active + "/" + d.recovery + " (base " + b.windup + "/" + b.active + "/" + b.recovery + ") reach " + d.reach.ToString("F2") + " dmg " + d.damage.ToString("F1") + " stamina x" + r.staminaMul.ToString("F2"));
        ws.cache[id] = d; return d;
    }
    static void WmPhase(ref int ticks, ref int chainedTicks, int abs, float mul)
    {
        float k = abs > 0 && ticks > 0 ? (float)abs / ticks : mul; if (k == 1f) return;
        ticks = Mathf.Max(1, Mathf.RoundToInt(ticks * k)); if (chainedTicks > 0) chainedTicks = Mathf.Max(1, Mathf.RoundToInt(chainedTicks * k));
    }
    public int WeaponTicks(int ticks) { return Tk(ticks); }   // the same for code outside the class (AI reads of THIS fighter's remaining recovery)
    int Tk(int ticks)
    {
        if (ticks <= 0) return ticks;
        float k = WeaponHooks.TimeMul(this); if (k == 1f) return ticks;
        return Mathf.Max(1, Mathf.RoundToInt(ticks * k));
    }
}
