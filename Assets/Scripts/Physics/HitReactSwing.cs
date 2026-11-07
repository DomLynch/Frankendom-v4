using System;
using System.Collections.Generic;
using UnityEngine;

// PHYSSWING v1: physics on swings and locomotion follow-through, guard hold and block recoil, for BOTH fighters. PRESENTATION ONLY (same contract as HitReact.cs):
//  nothing here writes DuelFighter / Fighter / CharacterController state or an Animator parameter, uses no UnityEngine.Random, creates no collider; the only things read from the sim are
//  DuelFighter.state (to pick the situation row) and the clip's own bone motion. -physproof:simhash must stay identical ON/OFF at 30 / 60 / 144 fps.
//  Sub-switch: physics.json "swing".enabled (default FALSE). Off = HitReact behaves exactly as before this file existed (every hook below returns its input unchanged).
//
//  WHAT IT ADDS to the collider-less powered proxy rig of HitReact (one rig per fighter, already built by PHYS2):
//   1. SITUATION ROWS: one persistent low-priority reaction per fighter whose spring strength (dips), damper multiplier (damps) and shown share (blend) per part follow what the fighter does:
//      loco (walk / run / strafe: weight shift, torso + head + arm lag), windup, strike (Active), recover (Recovery / ParryRecover: follow-through settle), guard (Guard: guard hold), roll.
//      Rows crossfade over swing.easeS. A hit / block reaction is a second reaction on top: per part the lowest strength and highest share win (existing HitReact rule).
//   2. LIMIT-AWARE SHARE ("reach"): the cause of the v101 swing-track FAIL (hit.track.*.attack 118.9 / 94.8 deg). The clips contain forearm / shin flexions OUTSIDE the proxy joint window
//      (Joan / Dracula attacks: forearmR -116.8 / -109 deg against a hyper-extension floor of -30; see tools/swing_reach.json). No spring can reach them, the joint limit holds the proxy and the
//      shown pose is off by the whole excess. Now a part whose CLIP pose is outside its proxy limit fades its share to 0 (the clip shows) and relaxes its spring: no limit fight, no deviation.
//      The proof prints how many ticks faded (hit.track.*.reach), so a gate cannot pass by fading everything.
//   3. LEAD: the slerp drive lags a moving target by damper/spring (+ the tick the target waits for the next Physics.Simulate). The joint TARGET is the clip pose extrapolated by that time, from the clip's own
//      angular velocity sampled per sim step. Same joint-space conversion as before, only the extrapolated clip rotation goes in. Bounded by swing.lead.maxDeg.
//   4. Optional display interpolation (swing.interpolate): the proxy pose is the Duel.Alpha interpolation between the last two steps (smooth at 144 fps); lead.latencyTicks should be 2 then.
// Data: Assets/Data/physics.json "swing" (copy in StreamingAssets). Proof: -physproof:swing (PhysProofSwing.cs) and the existing -physproof:hit track rows.

// ---------------- data ----------------
[Serializable] public class SwingLead { public bool enabled = true; public float gain = 1f, latencyTicks = 1f, maxDeg = 40f, minRateDegS = 20f; }
[Serializable] public class SwingReachCfg { public bool enabled = true, offAxis = true; public float startInsideDeg = 4f, rampDeg = 20f, minStrength = 0.2f; }
[Serializable]
public class SwingSituation
{
    public string id = ""; public string[] states = new string[0]; public float minSpeed = 0f, maxSpeed = 99f, easeS = -1f;   // speed = horizontal speed of the clip's hips (m/s); states = FState names; first matching row wins
    public HitKV[] dips = new HitKV[0], blend = new HitKV[0], damps = new HitKV[0];   // dips: spring strength 0..1 (lower = looser, more lag); damps: damper multiplier (>= 0); blend: shown share 0..1. Keys: role, part name (no $near)
    [NonSerialized] public int mask; [NonSerialized] public float[] dipV, shareV, dampV;
}
[Serializable] public class SwingProofSit { public string id = "", driver = "hold", move = ""; public string[] expect = new string[0]; public float seconds = 1f, minPeakDeg, maxPeakDeg = 45f; }   // driver: hold | move | press | guard
[Serializable]
public class SwingProofCfg
{
    public float separationM = 6f, settleSeconds = 0.6f, maxFadedPct = 35f, maxHipsDeltaM = 0.001f, maxFootDriftM = 0.03f, maxAvgStepMs = 1.25f, maxP95StepMs = 2f, trackSeconds = 1.4f;
    public string trackMove = "heavy"; public float[] leadSweepTicks = new float[0]; public SwingProofSit[] situations = new SwingProofSit[0];
}
[Serializable]
public class SwingCfg
{
    public bool enabled, interpolate; public float easeS = 0.12f, speedSmoothS = 0.12f, maxDeviationDeg = 50f;
    public SwingLead lead = new SwingLead(); public SwingReachCfg reach = new SwingReachCfg(); public SwingSituation[] situations = new SwingSituation[0]; public SwingProofCfg proof = new SwingProofCfg();
    public static void Fix(SwingCfg c)
    {
        if (c.lead == null) c.lead = new SwingLead(); if (c.reach == null) c.reach = new SwingReachCfg(); if (c.proof == null) c.proof = new SwingProofCfg();
        if (c.situations == null) c.situations = new SwingSituation[0]; if (c.proof.situations == null) c.proof.situations = new SwingProofSit[0]; if (c.proof.leadSweepTicks == null) c.proof.leadSweepTicks = new float[0];
        for (int k = 0; k < c.situations.Length; k++)
        {
            var s = c.situations[k]; if (s == null) { c.situations[k] = new SwingSituation(); s = c.situations[k]; }
            if (s.states == null) s.states = new string[0]; if (s.dips == null) s.dips = new HitKV[0]; if (s.blend == null) s.blend = new HitKV[0]; if (s.damps == null) s.damps = new HitKV[0];
            s.mask = 0; s.dipV = null; s.shareV = null; s.dampV = null;
            for (int q = 0; q < s.states.Length; q++) { FState fs; if (Enum.TryParse<FState>(s.states[q], out fs)) s.mask |= 1 << (int)fs; }
        }
    }
}

public partial class HitReact
{
    // ---------------- swing state ----------------
    Reaction swingR; float swingSpeed; bool leadInit; int leadStep = -1;
    Quaternion[] leadPrevRel, physPrev, physCur; Vector3[] leadVel; int[] physStepOf;
    float[] reachFade, limLoF, limHiF, limTw, limY, limZ, swingDamp; bool[] hingeP;
    /// <summary>Proof: true = the situation row is held neutral (the 'track' calibration row also does this by itself).</summary>
    public bool SwingSuspended;
    public string SwingSituationId = ""; public int SwingFrames, SwingFadedFrames; public float ReachWorstOverDeg, LeadDegPeak;
    public float ReachFadeOf(int i) { return reachFade != null && i >= 0 && i < reachFade.Length ? reachFade[i] : 1f; }
    public float SwingSpeedNow { get { return swingSpeed; } }
    public void SwingResetStats() { SwingFrames = 0; SwingFadedFrames = 0; ReachWorstOverDeg = 0f; LeadDegPeak = 0f; }
    int SwingCounted { get { return swingR != null && reactions.Contains(swingR) ? 1 : 0; } }

    // ---------------- build / activate hooks ----------------
    void SwingBuild()
    {
        leadPrevRel = new Quaternion[n]; leadVel = new Vector3[n]; physPrev = new Quaternion[n]; physCur = new Quaternion[n]; physStepOf = new int[n]; reachFade = new float[n]; swingDamp = new float[n];
        limLoF = new float[n]; limHiF = new float[n]; limTw = new float[n]; limY = new float[n]; limZ = new float[n]; hingeP = new bool[n];
        float mg = Mathf.Max(0f, Cfg.hit.limitMarginDeg); var parts = rig.parts;
        for (int i = 0; i < n; i++) { reachFade[i] = 1f; swingDamp[i] = 1f; physStepOf[i] = -1; physPrev[i] = Quaternion.identity; physCur[i] = Quaternion.identity; leadPrevRel[i] = Quaternion.identity; }
        for (int i = 1; i < n; i++)
        {
            var j = jt[i]; if (j == null) continue; var p = parts[i];
            limTw[i] = Mathf.Max(Mathf.Abs(j.lowAngularXLimit.limit), Mathf.Abs(j.highAngularXLimit.limit)); limY[i] = j.angularYLimit.limit; limZ[i] = j.angularZLimit.limit;   // already include hit.limitMarginDeg
            hingeP[i] = p.hinge && p.cfg != null && p.cfg.hinge != null && p.cfg.hinge.enabled;
            if (hingeP[i]) { bool flip = p.cfg.hinge.flip; limLoF[i] = (flip ? -p.highX : p.lowX) - mg; limHiF[i] = (flip ? -p.lowX : p.highX) + mg; }   // flexion window of the proxy (same sign rule as RagdollRig.Build)
        }
    }

    void SwingActivated() { if (physStepOf != null) for (int i = 0; i < n; i++) physStepOf[i] = -1; }

    // ---------------- per frame: situation row + lead sampling ----------------
    bool HasTag(string tag) { for (int k = 0; k < reactions.Count; k++) if (reactions[k].tag == tag) return true; return false; }

    void SwingTick(float dt)
    {
        var s = Cfg.swing;
        if (!s.enabled || leadVel == null) { if (swingR != null) { reactions.Remove(swingR); swingR = null; } return; }
        SwingLeadSample(dt);
        if (hadPrev && dt > 1e-5f)
        {
            Vector3 dv = animPos[0] - prevPos[0]; dv.y = 0f; float inst = dv.magnitude / dt;
            swingSpeed += (inst - swingSpeed) * Mathf.Clamp01(dt / Mathf.Max(1e-3f, s.speedSmoothS));
        }
        SwingSituation sit = null;
        if (!SwingSuspended && df != null && !HasTag("track"))
        {
            int bit = 1 << (int)df.state;
            for (int k = 0; k < s.situations.Length; k++)
            {
                var c = s.situations[k]; if (c == null || (c.mask & bit) == 0) continue;
                if (swingSpeed < c.minSpeed || swingSpeed >= c.maxSpeed) continue;
                sit = c; break;
            }
        }
        SwingSituationId = sit != null ? sit.id : "";
        if (sit != null) SwingResolve(sit);
        if (swingR == null)
        {
            if (sit == null) return;   // nothing to show and nothing running: no proxy cost
            swingR = new Reaction { t = 0f, ease = 0f, hold = 1e9f, back = 0f, dipMul = 1f, blendMul = 1f, tag = "swing", dip = new float[n], share = new float[n] };
            for (int i = 0; i < n; i++) swingR.dip[i] = 1f;
        }
        float ease = sit != null && sit.easeS >= 0f ? sit.easeS : s.easeS;
        float kf = ease <= 1e-4f ? 1f : Mathf.Clamp01(dt / ease);
        float maxShare = 0f;
        for (int i = 0; i < n; i++)
        {
            float td = sit != null ? sit.dipV[i] : 1f, ts = sit != null ? sit.shareV[i] : 0f, tm = sit != null ? sit.dampV[i] : 1f;
            swingR.dip[i] = Mathf.Lerp(swingR.dip[i], td, kf); swingR.share[i] = Mathf.Lerp(swingR.share[i], ts, kf); swingDamp[i] = Mathf.Lerp(swingDamp[i], tm, kf);
            if (swingR.share[i] > maxShare) maxShare = swingR.share[i];
        }
        swingR.t = 0f;   // never ages out
        if (sit == null && maxShare < 1e-3f) { reactions.Remove(swingR); for (int i = 0; i < n; i++) swingDamp[i] = 1f; return; }
        if (!reactions.Contains(swingR)) reactions.Add(swingR);
    }

    void SwingResolve(SwingSituation sit)
    {
        if (sit.dipV != null && sit.dipV.Length == n) return;
        sit.dipV = new float[n]; sit.shareV = new float[n]; sit.dampV = new float[n];
        for (int i = 0; i < n; i++) { sit.dipV[i] = 1f; sit.dampV[i] = 1f; }
        for (int k = 0; k < sit.dips.Length; k++) { var kv = sit.dips[k]; if (kv == null) continue; for (int i = 0; i < n; i++) if (Matches(kv.key, i, 'R')) sit.dipV[i] = Mathf.Min(sit.dipV[i], Mathf.Clamp01(kv.value)); }
        for (int k = 0; k < sit.blend.Length; k++) { var kv = sit.blend[k]; if (kv == null) continue; for (int i = 0; i < n; i++) if (Matches(kv.key, i, 'R')) sit.shareV[i] = Mathf.Max(sit.shareV[i], Mathf.Clamp01(kv.value)); }
        for (int k = 0; k < sit.damps.Length; k++) { var kv = sit.damps[k]; if (kv == null) continue; for (int i = 0; i < n; i++) if (Matches(kv.key, i, 'R')) sit.dampV[i] = Mathf.Max(0.01f, kv.value); }   // hc:ok min damper multiplier guard
    }

    /// <summary>Clip angular velocity of every part, sampled once per Physics step (the rate the proxy sees), local to the part's parent. Zero while the sim is frozen.</summary>
    void SwingLeadSample(float dt)
    {
        int sc = PhysicsClock.StepCount;
        if (!leadInit) { for (int i = 1; i < n; i++) leadPrevRel[i] = animRel[i]; leadStep = sc; leadInit = true; return; }
        if (dt <= 1e-6f) { for (int i = 1; i < n; i++) leadVel[i] = Vector3.zero; return; }
        if (sc == leadStep) return;
        float sdt = Mathf.Max(1, sc - leadStep) * Mathf.Max(1e-4f, PhysicsClock.StepDt);
        for (int i = 1; i < n; i++)
        {
            Quaternion d = Quaternion.Inverse(leadPrevRel[i]) * animRel[i]; float ang; Vector3 ax; d.ToAngleAxis(out ang, out ax);
            if (ang > 180f) ang -= 360f;
            leadVel[i] = (float.IsNaN(ax.x) || float.IsInfinity(ax.x) || ax.sqrMagnitude < 1e-8f) ? Vector3.zero : ax.normalized * (ang * Mathf.Deg2Rad / sdt);
            leadPrevRel[i] = animRel[i];
        }
        leadStep = sc;
    }

    // ---------------- Feed hook: the extrapolated clip rotation (identical to animRel[i] when the switch or the lead is off) ----------------
    Quaternion SwingRel(int i)
    {
        var s = Cfg.swing; if (!s.enabled || !s.lead.enabled || leadVel == null || !leadInit) return animRel[i];
        Vector3 w = leadVel[i]; float rate = w.magnitude * Mathf.Rad2Deg; if (rate < s.lead.minRateDegS) return animRel[i];
        float tau = springBase[i] > 1e-4f ? damperBase[i] / springBase[i] : 0f;   // the drive's own time constant at full strength (a dip loosens it on purpose: that lag is the effect)
        float lead = (tau + Mathf.Max(0f, s.lead.latencyTicks) * PhysicsClock.StepDt) * Mathf.Max(0f, s.lead.gain);
        float ang = Mathf.Min(rate * lead, Mathf.Max(0f, s.lead.maxDeg)); if (ang > LeadDegPeak) LeadDegPeak = ang;
        return animRel[i] * Quaternion.AngleAxis(ang, w.normalized);
    }

    // ---------------- Compose hooks ----------------
    float SwingDamp(int i) { return swingDamp != null && Cfg.swing.enabled ? swingDamp[i] : 1f; }

    static float TwistSwing(Quaternion q, out float swY, out float swZ)
    {
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        float tw = 2f * Mathf.Atan2(q.x, q.w) * Mathf.Rad2Deg;
        float m = Mathf.Sqrt(q.x * q.x + q.w * q.w); Quaternion t = m < 1e-6f ? Quaternion.identity : new Quaternion(q.x / m, 0f, 0f, q.w / m);
        Quaternion sq = q * Quaternion.Inverse(t); if (sq.w < 0f) sq = new Quaternion(-sq.x, -sq.y, -sq.z, -sq.w);
        float d = 1f + sq.w; swY = 4f * Mathf.Atan2(sq.y, d) * Mathf.Rad2Deg; swZ = 4f * Mathf.Atan2(sq.z, d) * Mathf.Rad2Deg;
        return tw;
    }

    /// <summary>Limit-aware share: a part whose CLIP pose is outside its proxy joint limit shows the clip (share -> 0) and relaxes its spring. Runs after the INTPHYS swing cap, before the drives are written.</summary>
    void SwingReach()
    {
        var s = Cfg.swing; if (!s.enabled || !s.reach.enabled || reachFade == null) return;
        var rc = s.reach; var parts = rig.parts; bool any = false; float worst = -999f;
        for (int i = 1; i < n; i++)
        {
            reachFade[i] = 1f;
            if (jt[i] == null || share[i] <= 0f) continue;
            float over;
            float swY, swZ; float tw = TwistSwing(Quaternion.Inverse(toJoint[i]) * Quaternion.Inverse(restLocal[i]) * animRel[i] * toJoint[i], out swY, out swZ);
            if (hingeP[i]) { float f = rig.HingeAngle(parts[i]); over = Mathf.Max(limLoF[i] - f, f - limHiF[i]); }
            else over = Mathf.Abs(tw) - limTw[i];
            if (rc.offAxis)
            {
                float yl = Mathf.Max(1f, limY[i]), zl = Mathf.Max(1f, limZ[i]); float e = Mathf.Sqrt((swY / yl) * (swY / yl) + (swZ / zl) * (swZ / zl));
                over = Mathf.Max(over, (e - 1f) * Mathf.Max(yl, zl));
            }
            if (over > worst) worst = over;
            float fade = Mathf.Clamp01(1f - (over + rc.startInsideDeg) / Mathf.Max(0.01f, rc.rampDeg));   // hc:ok divide guard
            if (fade < 1f) { reachFade[i] = fade; share[i] *= fade; strength[i] *= Mathf.Lerp(Mathf.Clamp01(rc.minStrength), 1f, fade); any = true; }
        }
        SwingFrames++; if (any) SwingFadedFrames++; if (worst > ReachWorstOverDeg) ReachWorstOverDeg = worst;
    }

    // ---------------- Blend hooks ----------------
    Quaternion PhysRel(int i)
    {
        Quaternion cur = Quaternion.Inverse(rb[pi[i]].rotation) * rb[i].rotation;
        var s = Cfg.swing; if (!s.enabled || !s.interpolate || physStepOf == null) return cur;
        int sc = PhysicsClock.StepCount;
        if (physStepOf[i] != sc) { physPrev[i] = (physStepOf[i] >= 0 && sc - physStepOf[i] <= 1) ? physCur[i] : cur; physCur[i] = cur; physStepOf[i] = sc; }
        return Quaternion.Slerp(physPrev[i], physCur[i], Duel.Alpha);
    }

    float SwingShare(int i, Quaternion phys, float sh)
    {
        var c = Cfg.swing; if (!c.enabled || c.maxDeviationDeg <= 0f) return sh;
        float a = Quaternion.Angle(animRel[i], phys); return a * sh > c.maxDeviationDeg ? c.maxDeviationDeg / a : sh;
    }
}
