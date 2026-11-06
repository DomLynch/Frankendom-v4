using UnityEngine;

// lanePARRY: parry / riposte feel on DuelFighter. All numbers: Assets/Data/parry.json (StaminaTable.Parry()) and posture.json parry{}.
//  - perfect parry   : guard raised <= windowTicks before the blow (Duel.Resolve). Attacker: stagger (attackerStagger[Heavy]Ticks), posture (posture.json parry.attacker,
//                      x attackerPostureHeavyMul on a heavy). Parrier: selfRecoveryTicks, posture relief, stamina - staminaCost + staminaGain, riposte window riposteWindowTicks.
//                      Presentation is event driven ("Parried"): slow_mo.json parry, hit_stop.json parry, cues.json parry, clash_fx.json, camera_impact.json parry.
//  - late block      : any blow on a raised guard outside the window = plain block (stamina.json block, posture.json block, chip).
//  - whiffed parry   : a guard tap (released at guard age <= whiff.releaseTicks) that opened a window and met no blow -> whiff.recoveryTicks of ParryRecover (no guard / attack / dodge).
//                      The re-raise cooldown (cooldownTicks, counted from the last raise) still applies: a re-raise inside it is a plain block.
//  - sidestep        : StaminaTable.SidestepHitsThrough(m) blows ignore the sidestep i-frames (parry.json sidestep).
// Hooks in DuelFighter.cs: TickFree guard raise -> ParryGuardRaised, guard drop -> ParryWhiffOnDrop, ParryRecover length -> ParryRecoverLen,
// ParrySuccess / NoteBlock -> parryGuardContact; Duel.Resolve -> ParryAttackerPosture, SidestepIgnoresIFrames.
public partial class DuelFighter
{
    bool parryArmed, parryGuardContact; int parryWhiffLen;
    public int statParryWhiffs;   // balance counter

    // the guard just went up (TickFree, after parryWindow / parryCd were set)
    void ParryGuardRaised() { parryArmed = parryWindow; parryGuardContact = false; }

    bool ParryWhiffApplies()
    {
        var P = StaminaTable.Parry(); var W = P.whiff;
        if (!P.enabled || W == null || !W.enabled) return false;
        return W.scope != "player" || IsPlayer;
    }

    // the guard just dropped (TickFree set Loco). true = a whiff recovery started (the caller returns)
    bool ParryWhiffOnDrop()
    {
        bool armed = parryArmed, contact = parryGuardContact; parryArmed = false; parryGuardContact = false;
        if (!armed || contact || !ParryWhiffApplies()) return false;
        var W = StaminaTable.Parry().whiff;
        if (guardAge > W.releaseTicks || W.recoveryTicks <= 0) return false;
        atkOn = false; fadeOutLen = 0f; state = FState.ParryRecover; t = 0; parryWhiffLen = W.recoveryTicks; statParryWhiffs++;
        if (!string.IsNullOrEmpty(W.clip) && HasState(W.clip)) PlayClip(W.clip, Mathf.Max(1, W.clipTicks), 0.05f);
        if (StaminaTable.Get().debugLog) Debug.Log("[Parry] " + name + " WHIFF guardAge=" + guardAge + " recovery=" + parryWhiffLen + " tick=" + Duel.TickCount);
        return true;
    }

    // ParryRecover length: the whiff recovery when one runs, else the parry's own recovery
    int ParryRecoverLen() { return parryWhiffLen > 0 ? parryWhiffLen : (int)ParrySelfRecovery(); }
    public bool InParryWhiff => state == FState.ParryRecover && parryWhiffLen > 0;

    // posture the parried attacker (this) takes
    public float ParryAttackerPosture(bool heavy)
    {
        var P = StaminaTable.Parry();
        float g = PostureTable.ParryAttackerGain(P.enabled ? P.attackerPosture : R("posture.parry", 25));
        return P.enabled && heavy ? g * P.attackerPostureHeavyMul : g;
    }

    // true = this fighter's dodge i-frames do NOT cover the blow (sidestep vs an overhead)
    public bool SidestepIgnoresIFrames(MoveDef m)
    {
        if (dodgeKind != 2) return false;
        var S = StaminaTable.Parry().sidestep;
        if (S != null && !S.includeBack && sideDir == 0) return false;
        return StaminaTable.SidestepHitsThrough(m);
    }
}
