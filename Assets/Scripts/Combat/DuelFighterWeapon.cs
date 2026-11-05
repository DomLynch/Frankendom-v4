using UnityEngine;

// WEAPONSWAP v88: the equipped weapon (gear.json items[].weapon, WeaponSwap.cs) as seen by the fight sim. DuelFighter is partial; this file holds the two seams so the hunks in the other files stay one-token edits:
//   ReachScale  = scale x the weapon's reach multiplier: replaces `.scale` in every "move.reach * fighter.scale" distance check (Duel.Resolve hit detection, the AI's reach reads of a fighter).
//   Tk(ticks)   = a move phase length (windup / active / recovery) scaled by 1 / weapon speed multiplier. The sim, the attack-clip plan (BeginAttackClip derives the clip speeds from the tick counts) and the
//                 queries (TicksLeft, WindupLeft, RecoveryLen) all go through it, so a slow weapon is slower in the sim AND in the animation. Identity (same int) for a neutral weapon.
// Both are exactly the old values for a fighter without a WeaponSwap (Pitborn, Dracula): WeaponHooks returns 1.
public partial class DuelFighter
{
    public float ReachScale { get { return scale * WeaponHooks.ReachMul(this); } }
    public int WeaponTicks(int ticks) { return Tk(ticks); }   // the same for code outside the class (AI reads of THIS fighter's remaining recovery)
    int Tk(int ticks)
    {
        if (ticks <= 0) return ticks;
        float k = WeaponHooks.TimeMul(this); if (k == 1f) return ticks;
        return Mathf.Max(1, Mathf.RoundToInt(ticks * k));
    }
}
