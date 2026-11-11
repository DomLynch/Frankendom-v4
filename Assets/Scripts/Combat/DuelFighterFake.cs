using System;
using UnityEngine;

// FAKEDEATH v1 (pool-laneS7, NEW file, UNPROVEN IN UNITY). Partial of DuelFighter: a scripted "down" that is NOT a death.
// A fighter in a fake sequence is state Down (the sim already knows it: no input, no AI action, ticks like a knock-down) and plays a list of data steps (fake_deaths.json: clip, start second, speed, seconds, fade, hold last frame).
// The sequence never calls Die(): no OnKilled event (no ragdoll, no drop, no silver, no kill record, no death camera) and Dead stays false, so GameLoop / ZoneRuntime keep the fighter alive.
// Used twice: the boss that gets up once from zero health (the fall is the existing death clip, then a pause, then get_up), and the bandit corpse that grabs Joan's ankle (lying pose, reach), see FakeDeath.cs.
// Hooks in existing files (apply_s7.py): DuelFighter.Tick (Down case calls FakeTick), DuelFighter.Die (FakeDeathTry first), Duel.Resolve (immune + the lethal-blow gate). Switch-off = FakeDeath.On false: every hook returns on its first line.
public partial class DuelFighter
{
    FdStep[] fakeSteps; int fakeIdx, fakeTicks, fakeLen; bool fakeOn, fakeImmune, fakeUsed; Action<DuelFighter, bool> fakeDone; string fakeIdleClip = ""; float fakeIdleFade;
    public bool FakeOn { get { return fakeOn; } }
    public bool FakeImmune { get { return fakeOn && fakeImmune; } }
    public bool FakeUsed { get { return fakeUsed; } }
    public int FakeStepIndex { get { return fakeIdx; } }

    /// <summary>Called when the killing blow lands (Duel.Resolve before any finisher, and DuelFighter.Die). True = the boss falls instead of dying (the caller returns, nothing else happens).</summary>
    public bool FakeDeathTry()
    {
        if (fakeOn || fakeUsed || state == FState.Dead) return false;
        var B = FakeDeath.WantRise(this); if (B == null) return false;
        fakeUsed = true; health = Mathf.Max(B.holdHp, 0f); if (hp != null) hp.current = health;   // above zero while down: the Duel.Update zero-hp safety net must not kill it
        FakeStart(B.steps, true, B.idleClip, B.idleFade, FakeDeath.BossFinished);
        return true;
    }

    public void FakeSetHealth(float h) { health = Mathf.Clamp(h, 0f, maxHealth); if (hp != null) hp.current = health; }

    /// <summary>Start a scripted down. immune = no strike resolves on it while it runs. done(v, completed) runs after the last step (completed true) or on FakeAbort (false); the fighter stands in Loco / idle by then.</summary>
    public void FakeStart(FdStep[] steps, bool immune, string idleClip, float idleFade, Action<DuelFighter, bool> done)
    {
        fakeSteps = steps; fakeImmune = immune; fakeIdleClip = idleClip ?? ""; fakeIdleFade = idleFade; fakeDone = done; fakeOn = true; fakeIdx = -1;
        atkOn = false; fadeOutLen = 0f; state = FState.Down; t = 0; knock = Vector3.zero; staggerTicks = 0; paused = false;
        pLight = pHeavy = pThrust = pKick = pRoll = pBack = pSide = 0; pMove = null; pMoveTtl = 0; moveDir = Vector3.zero; guardHeld = false; heavyHeld = false;
        FakeNext();
    }

    void FakeNext()
    {
        fakeIdx++;
        if (fakeSteps == null || fakeIdx >= fakeSteps.Length) { FakeFinish(true); return; }
        var s = fakeSteps[fakeIdx]; fakeTicks = 0; fakeLen = Mathf.Max(1, Mathf.RoundToInt(s.seconds * 60f));   // sim ticks (60 Hz): the same length at any frame rate
        if (f != null && f.anim != null)
        {
            if (s.deathRow) { DeathPlay(FState.Loco); f.holdLast = s.hold; }   // the existing death.json clip / start second / speed / fade
            else if (HasState(s.clip)) { f.holdLast = false; paused = false; f.ForceExact(s.clip, Mathf.Max(0f, s.fade), Mathf.Max(0f, s.startS)); f.anim.speed = Mathf.Max(s.speed, 0f) * f.animWeight; f.holdLast = s.hold; }
            else if (!string.IsNullOrEmpty(s.clip)) FakeDeath.Warn("clip '" + s.clip + "' is not a state of " + name + ": the step waits on the previous pose");
        }
        FakeDeath.StepStarted(this, fakeIdx, s);
    }

    // called by DuelFighter.Tick, state Down
    void FakeTick() { fakeTicks++; if (fakeTicks >= fakeLen) FakeNext(); }

    void FakeFinish(bool completed)
    {
        fakeOn = false; fakeImmune = false; var cb = fakeDone; fakeDone = null; fakeSteps = null;
        state = FState.Loco; t = 0; staggerTicks = 0; posture = 0f; postureBroken = false; deathWindow = 0;
        pLight = pHeavy = pThrust = pKick = pRoll = pBack = pSide = 0; pMove = null; pMoveTtl = 0; knock = Vector3.zero;
        if (f != null)
        {
            f.holdLast = false;
            if (f.anim != null) { f.anim.enabled = true; f.anim.speed = f.animWeight; }
            if (HasState(fakeIdleClip)) PlayClip(fakeIdleClip, 0, fakeIdleFade);
        }
        if (cb != null) { try { cb(this, completed); } catch (Exception e) { Debug.LogWarning("[FakeDeath] done callback threw: " + e.Message); } }
    }

    /// <summary>Stop a running fake sequence now (the fighter stands in Loco / idle). No-op when none runs.</summary>
    public void FakeAbort() { if (fakeOn) FakeFinish(false); }

    // a real death arrives while a fake sequence runs (health forced to 0 by something that skipped Resolve): the real death wins
    void FakeCancelForDie() { fakeOn = false; fakeImmune = false; fakeDone = null; fakeSteps = null; }
}
