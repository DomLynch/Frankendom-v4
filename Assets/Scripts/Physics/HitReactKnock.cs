using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// PHYSKNOCK v1: knockdown and get-up as a PRESENTATION-ONLY ragdoll blend on the existing HitReact proxy rig (partial class, no new bodies, no colliders).
//   The 60 Hz sim owns everything: DuelFighter.state Down, its tick counter t, the length of the stun (rules "posture.stun"), the knock slide and the hips. This file only READS them
//   (state, t, Rule(), KnockVelocity, Fighter.Current) and never writes a sim field, an Animator parameter or a transform other than bone ROTATIONS in HitReact.Blend.
//   While the fighter is Down, one extra reaction ("knock") is held on the proxy: depth 0 -> 1 over timeline.riseTicks (smoothstep), 1 while he lies, 1 -> 0 (smoothstep) from the tick the
//   get-up clip starts (or timeline.releaseFromTickDefault) to (stun - releaseEndMarginTicks): the motors of the limbs go slack (row dips) so gravity and the fall impulse swing them, and the
//   shown pose is the clip's pose blended toward the proxy by that depth. Depth is a pure function of the sim tick (plus a sub-tick ramp so it is smooth at any fps), so physics never decides
//   WHEN anything happens. The blend back to the get-up clip is the same smoothstep, so the shown pose meets the clip exactly when the sim leaves Down (depth 0 at the end).
//   Pop guards (all data, knock.limits / knock.floor): a deviation cap (maxDevDeg), a rate limit on the deviation of every part (slewDegPerS, both directions), and a floor guard that fades
//   a part toward the clip where the collider-less proxy would sink below the ground.
//   Dark: physics.json knock.enabled=false (default) = Tick returns at once, Blend hook returns its input, nothing is attached. Needs master + clock on (same gate as every HitReact).
[Serializable] public class KnockTimeline { public int riseTicks = 12, releaseFromTickDefault = 54, releaseEndMarginTicks = 3, exitTicks = 12; public string getUpClip = "get_up", stunRule = "posture.stun"; public float stunDefault = 90f; }
[Serializable] public class KnockLimits { public float maxDevDeg = 75f, slewDegPerS = 540f, minSlewAngDeg = 0.05f; }
[Serializable] public class KnockFloorCfg { public float clearM = 0.02f, maxSagM = 0.25f, rampM = 0.1f, groundOffsetM = 0f; }
[Serializable] public class KnockProofCfg
{
    public float separationM = 1.7f, knockSpeedMps = 2.5f, observeExtraS = 0.35f, settleSeconds = 0.8f;
    public float maxStartDepth = 0.1f, maxPopDegPerS = 1800f, slewTolDegPerS = 1f, exitMaxDeg = 1f, minPeakDeg = 1f, maxPeakDeg = 90f, maxHipsDeltaM = 0.001f, maxAvgStepMs = 1.25f, maxP95StepMs = 2f;
    public int[] fps = new int[] { 60, 30, 144 }; public string[] victims = new string[] { "enemy", "player" }; public int exitFrames = 3;
}
[Serializable] public class KnockCfg
{
    public bool enabled; public string zonePart = "spine2"; public float minDirSpeedMps = 0.05f;
    public HitRow row = new HitRow(); public HitClassRow style = new HitClassRow();
    public KnockTimeline timeline = new KnockTimeline(); public KnockLimits limits = new KnockLimits(); public KnockFloorCfg floor = new KnockFloorCfg(); public KnockProofCfg proof = new KnockProofCfg();
}
[Serializable] public class KnockFile { public KnockCfg knock = new KnockCfg(); }

public partial class HitReact
{
    // ---------------- config (own parse of physics.json "knock": HitFile / PhysicsCfg stay as they are) ----------------
    static KnockCfg knockCfg;
    public static KnockCfg KnockSettings { get { if (knockCfg == null) knockCfg = KnockLoad(); return knockCfg; } }
    public static void KnockReload() { knockCfg = null; }
    /// <summary>Kill-switch / proof control run: true = no knock reaction is made (the sim cannot tell). Also set by the command line -knock:off.</summary>
    public static bool KnockSuppress;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void KnockResetStatics() { knockCfg = null; KnockSuppress = false; }

    static KnockCfg KnockLoad()
    {
        var d = new KnockCfg();   // class defaults = disabled
        string json = null;
        string[] paths = { Path.Combine(Application.streamingAssetsPath, "physics.json"), Path.Combine(Application.dataPath, "Data/physics.json") };
        for (int i = 0; i < paths.Length && json == null; i++)
        {
            try { if (File.Exists(paths[i])) json = File.ReadAllText(paths[i]).TrimStart('﻿'); }
            catch (Exception ex) { Debug.LogWarning("[HitReact.knock] cannot read " + paths[i] + ": " + ex.Message); }
        }
        if (string.IsNullOrEmpty(json)) return d;
        try
        {
            var r = JsonUtility.FromJson<KnockFile>(json);
            if (r == null || r.knock == null) return d;
            var k = r.knock;
            if (k.row == null) k.row = new HitRow(); if (k.style == null) k.style = new HitClassRow(); if (k.timeline == null) k.timeline = new KnockTimeline(); if (k.limits == null) k.limits = new KnockLimits();
            if (k.floor == null) k.floor = new KnockFloorCfg(); if (k.proof == null) k.proof = new KnockProofCfg();
            var w = k.row; if (w.timing == null) w.timing = new HitTiming(); if (w.dips == null) w.dips = new HitKV[0]; if (w.blend == null) w.blend = new HitKV[0]; if (w.split == null) w.split = new HitSplit[0]; if (w.dirs == null) w.dirs = new HitDirRow[0];
            if (k.proof.fps == null || k.proof.fps.Length == 0) k.proof.fps = new int[] { Mathf.RoundToInt(1f / PhysicsClock.StepDt) }; if (k.proof.victims == null) k.proof.victims = new string[0];
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-knock:off") >= 0) KnockSuppress = true;
            return k;
        }
        catch (Exception ex) { Debug.LogError("[HitReact.knock] parse failed: " + ex.Message); return d; }
    }

    // ---------------- attach (the knock sub-switch attaches the proxy by itself; hit.enabled is not needed) ----------------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void KnockBoot() { SceneManager.sceneLoaded -= KnockSceneLoaded; SceneManager.sceneLoaded += KnockSceneLoaded; KnockAttach(); }
    static void KnockSceneLoaded(Scene s, LoadSceneMode m) { KnockAttach(); }
    static void KnockAttach()
    {
        var k = KnockSettings;
        if (k == null || !k.enabled || !PhysicsClock.PhysicsOn || !PhysicsClock.Scripted) return;   // master / clock off = no proxies, same as AttachAll
        var all = FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].GetComponent<HitReact>() == null) all[i].gameObject.AddComponent<HitReact>();
    }

    // ---------------- state ----------------
    const float KnockUnit = 1000f;   // structural: the knock Reaction's three segments are each KnockUnit long so its own clock can never run out; its depth is set through t
    Reaction knockR; bool knockOn, knockLive, knockDone, knockExit, knockGotUp, knockFresh;
    float kx, kBeginX, kRelFrom, kExitX, kExitDepth, kdep, kdt;
    float[] kDev; bool[] kTouched;
    public float KnockDepth { get { return kdep; } }
    public bool KnockActive { get { return knockOn; } }
    public bool KnockShowing { get { return knockLive; } }
    public float KnockPeakDevDeg, KnockPopPeakDegPerS; public int KnockFrames;
    public float KnockReleaseFromTick { get { return kRelFrom; } }

    static float KnockSmooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    /// <summary>Proof / tools: forget the knock state and stats (between cases). Does not touch other reactions.</summary>
    public void KnockResetAll() { KnockEnd(); KnockPeakDevDeg = 0f; KnockPopPeakDegPerS = 0f; KnockFrames = 0; }

    void KnockEnd()
    {
        if (knockR != null) reactions.Remove(knockR);
        knockR = null; knockOn = false; knockLive = false; knockDone = false; knockExit = false; knockGotUp = false; kdep = 0f;
        if (kDev != null) { Array.Clear(kDev, 0, kDev.Length); Array.Clear(kTouched, 0, kTouched.Length); }
    }

    void KnockBegin(KnockCfg kc)
    {
        if (!ready || n <= 0) return;
        if (kDev == null || kDev.Length != n) { kDev = new float[n]; kTouched = new bool[n]; }
        Vector3 dir = df.KnockVelocity; dir.y = 0f;
        if (dir.sqrMagnitude < kc.minDirSpeedMps * kc.minDirSpeedMps) dir = BlowDir(df.opponent); else dir.Normalize();   // the slide of this blow is the direction he is driven; presentation only
        StartRow(kc.row, kc.style, dir, kc.zonePart);
        if (reactions.Count == 0) return;
        knockR = reactions[reactions.Count - 1]; knockR.tag = "knock";
        knockR.ease = KnockUnit; knockR.hold = KnockUnit; knockR.back = KnockUnit; knockR.t = 0f;
        knockOn = true; knockDone = false; knockExit = false; knockGotUp = false; knockFresh = true; kdep = 0f;   // knockFresh: the begin frame does not advance the clock, so the first shown depth is exactly 0
        kx = df.t; kBeginX = kx; kRelFrom = kc.timeline.releaseFromTickDefault;
        LastRow = kc.row.id;
    }

    // called every live frame from LateUpdate (hook), after Record() and the ageing of the reactions, before the "no reactions" early-out
    void KnockTick(float dt)
    {
        var kc = KnockSettings;
        if (!kc.enabled || KnockSuppress || df == null) { if (knockOn) KnockEnd(); knockLive = false; return; }
        if (knockOn && kDev != null) for (int i = 0; i < n; i++) { if (!kTouched[i]) kDev[i] = 0f; kTouched[i] = false; }   // a part that was not blended last frame shows the clip: nothing to slew from
        bool down = df.state == FState.Down;
        kdt = dt;
        if (!knockOn)
        {
            if (!down) { knockLive = false; return; }
            KnockBegin(kc); if (!knockOn) { knockLive = false; return; }
        }
        var tl = kc.timeline; float hz = 1f / Mathf.Max(1e-4f, PhysicsClock.StepDt);
        float d;
        int phase;   // 0 rising, 1 lying, 2 releasing
        if (down && !knockExit)
        {
            kx = Mathf.Clamp(kx + (knockFresh ? 0f : dt * hz), df.t, df.t + 1f); knockFresh = false;   // follows the sim tick: never behind it, never more than one tick ahead, never backwards (hit-stop: dt 0, t frozen)
            if (!knockGotUp && rig != null && rig.fighter != null && rig.fighter.Current == tl.getUpClip) { knockGotUp = true; if (kx < kRelFrom) kRelFrom = kx; }
            float stun = df.Rule(tl.stunRule, tl.stunDefault);
            float relTo = Mathf.Max(kRelFrom + 1f, stun - tl.releaseEndMarginTicks);
            float rise = KnockSmooth((kx - kBeginX) / Mathf.Max(1f, tl.riseTicks));
            float rel = kx >= kRelFrom ? 1f - KnockSmooth((kx - kRelFrom) / (relTo - kRelFrom)) : 1f;
            d = Mathf.Min(rise, rel);
            phase = kx >= kRelFrom ? 2 : (rise < 1f ? 0 : 1);
        }
        else
        {
            if (!knockExit) { knockExit = true; kExitX = kx; kExitDepth = kdep; }   // the sim left Down earlier than the envelope expected (never in normal play): finish from where we are, no jump
            kx += dt * hz;
            d = kExitDepth * (1f - KnockSmooth((kx - kExitX) / Mathf.Max(1f, tl.exitTicks)));
            phase = 2;
        }
        kdep = d;
        if (d <= 0f && phase == 2)
        {
            if (knockExit) { KnockEnd(); return; }
            knockDone = true;   // fully released while the sim still counts out the last ticks of Down: nothing is shown until the state ends
        }
        if (knockDone && down) { if (knockR != null) reactions.Remove(knockR); knockLive = false; kdep = 0f; return; }
        if (knockDone && !down) { KnockEnd(); return; }
        knockR.t = phase == 0 ? d * KnockUnit : (phase == 1 ? 1.5f * KnockUnit : (3f - d) * KnockUnit);   // Reaction.Depth() is linear in t: this makes it return exactly d   // hc:ok smoothstep segment structure (3 segments, middle of the held one)
        if (!reactions.Contains(knockR)) reactions.Add(knockR);   // evicted by the maxReactions cap or aged out: put it back (never counted twice)
        knockLive = d > 0f; if (knockLive) KnockFrames++;
    }

    // called by Blend for every blended part just before the bone is written: floor guard, deviation cap, rate limit. Returns rel unchanged when no knock is showing.
    Quaternion KnockRel(int i, Quaternion rel)
    {
        if (!knockLive) return rel;
        var kc = KnockSettings; var lm = kc.limits; var fl = kc.floor;
        Quaternion a = animRel[i];
        if (rb[i] != null && df != null)
        {
            float gy = df.transform.position.y + fl.groundOffsetM, ramp = Mathf.Max(1e-3f, fl.rampM);
            float over = (gy + fl.clearM) - rb[i].worldCenterOfMass.y;   // > 0: the limb centre would be under the floor
            float sag = animPos[i].y - rb[i].position.y;                 // > 0: the proxy joint hangs below where the clip has it
            float f1 = over > 0f ? 1f - Mathf.Clamp01(over / ramp) : 1f, f2 = sag > fl.maxSagM ? 1f - Mathf.Clamp01((sag - fl.maxSagM) / ramp) : 1f;
            float f = Mathf.Min(f1, f2);
            if (f < 1f) rel = Quaternion.Slerp(a, rel, f);
        }
        float ang = Quaternion.Angle(a, rel);
        if (ang > lm.maxDevDeg && ang > 1e-4f) { rel = Quaternion.Slerp(a, rel, lm.maxDevDeg / ang); ang = lm.maxDevDeg; }
        float prev = kDev[i], lim = lm.slewDegPerS * kdt;
        if (ang > prev + lim) { rel = Quaternion.Slerp(a, rel, (prev + lim) / ang); ang = prev + lim; }
        else if (ang < prev - lim && ang > lm.minSlewAngDeg) { rel = Quaternion.SlerpUnclamped(a, rel, (prev - lim) / ang); ang = prev - lim; }   // the deviation may not collapse faster than it grew either
        if (kdt > 1e-5f) { float rate = Mathf.Abs(ang - prev) / kdt; if (rate > KnockPopPeakDegPerS) KnockPopPeakDegPerS = rate; }
        if (ang > KnockPeakDevDeg) KnockPeakDevDeg = ang;
        kDev[i] = ang; kTouched[i] = true;
        return rel;
    }
}
