using UnityEngine;

// REVIVE v1 (laneSEAMLESS): the player's in-world respawn. Joan dies, the zone keeps running, she stands up again: no scene reload, no fade, no card.
// Partial of DuelFighter. The request is queued by whoever owns the respawn rule (ZoneRuntime) and executed at the TOP OF THE NEXT SIM TICK (DuelFighter.Tick, state Dead branch), so the state change is
// tick-aligned like every other state change and does not depend on frame timing. Every number (hp share, delays, spot) is zones.json hp.respawn; nothing is decided here.
// What stands her up: hp / stamina / posture and every buffer of the state machine back to a fresh fighter, collider on, ragdoll put back (RagdollRig.Restore), Animator on and sent to idle.
// If the ragdoll cannot be put back (a finisher cut the body, a bone is gone) ReviveFailed is set, she stays dead and the caller falls back to the old reload. Never throws.
public partial class DuelFighter
{
    bool reviveReq; float reviveFrac = 1f;
    public bool ReviveFailed { get; private set; }
    public int Revives { get; private set; }
    public bool RevivePending { get { return reviveReq; } }

    /// <summary>Queue a revive for the next sim tick. Ignored unless the fighter is dead. hpFrac = share of max hp she stands up with.</summary>
    public void RequestRevive(float hpFrac)
    {
        if (state != FState.Dead) return;
        reviveReq = true; reviveFrac = Mathf.Clamp01(hpFrac); ReviveFailed = false;
    }

    void ReviveTick()
    {
        reviveReq = false;
        try
        {
            var rr = GetComponent<RagdollRig>();
            if (rr != null && rr.Converted && !rr.Restore()) { ReviveFailed = true; Debug.LogWarning("[Revive] " + name + ": the ragdoll cannot be put back (" + rr.RestoreWhy + "), she stays down"); return; }
            var rd = GetComponent<RagdollDeath>(); if (rd != null) { rd.enabled = false; Destroy(rd); }   // a later death must start a fresh RagdollDeath (OnKilledGlobal skips a fighter that has one)
            health = Mathf.Max(1f, maxHealth * reviveFrac); hp.current = health; stamina = maxStamina; stLast = stamina; posture = 0f; exhausted = false; postureBroken = false; deathWindow = 0;
            regenWait = 0; postureHold = 0; guardAge = 0; parryCd = 0; riposteTicks = 0; blockFlash = 0; parryWindow = false; guardBreakFlash = 0; parryOkFlash = 0; dodgeCd = 0; staggerTicks = 0; charge = 0;
            pLight = pHeavy = pThrust = pKick = pRoll = pBack = pSide = 0; pMove = null; pMoveTtl = 0; chained = false; hitDone = false; knock = Vector3.zero; vy = 0f; paused = false;
            moveDir = Vector3.zero; run = false; guardHeld = false; heavyHeld = false; atkOn = false; fadeOutLen = 0f; lastGroundedPos = transform.position;
            var col = GetComponent<Collider>(); if (col) col.enabled = true;   // Die() switched it off (the CharacterController is the first Collider)
            if (f != null)
            {
                f.holdLast = false;
                if (f.anim != null) { f.anim.enabled = true; f.anim.speed = 1f; }
            }
            state = FState.Loco; t = 0;
            if (f != null) PlayClip("idle", 0, R("blend.toIdleS", 0.1f));   // hc:ok same rules key and default as the feint path (DuelFighter.cs:42)
            Revives++; ReviveFailed = false;
            Debug.Log("[Revive] " + name + " stands up at sim tick " + Duel.TickCount + " with hp " + health.ToString("F0") + "/" + maxHealth.ToString("F0"));
        }
        catch (System.Exception ex) { ReviveFailed = true; Debug.LogWarning("[Revive] " + name + " failed: " + ex.Message); }
    }
}
