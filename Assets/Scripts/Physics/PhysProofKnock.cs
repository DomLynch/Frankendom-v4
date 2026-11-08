#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// PHYSKNOCK v1: -physproof:knock (also part of -physproof:all). Part of PhysProof (partial class). Runs only when physics.json knock.enabled=true (sub-switch, default off), on both fighters,
// at every fps in knock.proof.fps (a pop that depends on the display rate would show up as a different number at 30 / 60 / 144).
//   One case = the victim is put through the real sim API V.KnockDown() + SetKnock() (what Duel.Resolve calls) with the knock blend ON, and again with it suppressed (HitReact.KnockSuppress,
//   same code path, no knock reaction), and once more suppressed as the control. Rows (per victim and fps, id knock.<victim>.<fps>.*):
//     selected    the knock reaction showed on at least one Down frame
//     peak        peak shown-vs-clip deviation inside minPeakDeg..maxPeakDeg (min > 0 catches "nothing happens")
//     devCap      the deviation never exceeded limits.maxDevDeg
//     noPopIn     depth on the first Down frame <= maxStartDepth (the blend starts from the clip)
//     noPop       NO POP: the largest frame-to-frame change of the shown-vs-clip deviation of any part (world rotation, measured independently of the limiter), in deg/s, over the fall, the lie,
//                 the get-up AND the first frames after the sim leaves Down, <= maxPopDegPerS
//     slew        the limiter's own largest deviation change per second <= limits.slewDegPerS + tolerance (proves the limiter does what it says)
//     exitBlend   first frame after the sim leaves Down: shown pose equals the clip within exitMaxDeg (the blend back is finished, no jump to the get-up / loco clip)
//     hipsFixed   the hips (the sim's position) never move through the blend
//     stepCost    physics ms per step avg / p95
//     simTimeline Down lasted the same number of sim ticks ON and OFF (physics never decides timing)
//     simhash     per-tick hash of state, t, posture, health, stamina, x, z of BOTH fighters identical ON vs OFF; the control (OFF vs OFF) says whether the harness is deterministic on its own
public partial class PhysProof
{
    class KnockRes
    {
        public float peak, pop, codePop, codePeak, startDepth = -1f, exitDev, hips, avg, p95; public int downTicks, liveFrames, frames; public bool nan;
        public Dictionary<int, ulong> ticks = new Dictionary<int, ulong>(); public ulong final = 14695981039346656037UL;
    }

    IEnumerator KnockProof()
    {
        var kc = HitReact.KnockSettings; var pr = kc.proof;
        if (!kc.enabled) { Log("knock proof skipped: physics.json knock.enabled=false (sub-switch, default off)"); yield break; }
        if (!RagdollDeath.Cfg.enabled || !RagdollDeath.Cfg.rig.prebuild) { Log("knock proof skipped: ragdoll.json enabled/prebuild off (no rig, no proxy)"); yield break; }
        HitReact.KnockSuppress = false;
        yield return Reload();
        ScriptedFighters();
        var hD = D.GetComponent<HitReact>(); var hP = P.GetComponent<HitReact>();
        int guard = 0; while (((hD == null || !hD.Ready) || (hP == null || !hP.Ready)) && guard++ < 300) { yield return null; hD = D.GetComponent<HitReact>(); hP = P.GetComponent<HitReact>(); }
        Check("knock.attached", hD != null && hD.Ready && hP != null && hP.Ready, "HitReact (knock) on " + D.name + " ready=" + (hD != null && hD.Ready) + ", on " + P.name + " ready=" + (hP != null && hP.Ready));
        if (hD == null || !hD.Ready || hP == null || !hP.Ready) yield break;
        foreach (var vname in pr.victims)
        {
            if (exiting) yield break;
            bool enemy = vname == "enemy";
            DuelFighter V = enemy ? P : D, A = enemy ? D : P; var hr = enemy ? hP : hD;
            Vector3 vp = V.transform.position;
            foreach (int fps in pr.fps)
            {
                if (exiting) yield break;
                Time.captureFramerate = fps;
                var on = new KnockRes(); var off = new KnockRes(); var ctl = new KnockRes();
                yield return KnockRun(V, A, hr, kc, vp, fps, false, on);
                yield return KnockRun(V, A, hr, kc, vp, fps, true, off);
                yield return KnockRun(V, A, hr, kc, vp, fps, true, ctl);
                KnockChecks("knock." + vname + "." + fps, kc, on, off, ctl);
            }
            Time.captureFramerate = 60;
        }
        HitReact.KnockSuppress = false;
        Log("knock proof avg step " + F(PhysicsClock.AvgMs) + " ms");
    }

    IEnumerator KnockRun(DuelFighter V, DuelFighter A, HitReact hr, KnockCfg kc, Vector3 vp, int fps, bool suppress, KnockRes res)
    {
        var pr = kc.proof; float stepDt = PhysicsClock.StepDt;
        HitReact.KnockSuppress = suppress; hr.KnockResetAll(); hr.ClearAll(); hr.ResetPeaks();
        Vector3 dirA = Vector3.forward;
        V.health = V.maxHealth; V.stamina = V.maxStamina; V.posture = 0f; A.health = A.maxHealth; A.stamina = A.maxStamina; A.posture = 0f;
        Place(V, vp, dirA); Place(A, vp + dirA * pr.separationM, -dirA);
        yield return Ticks(Mathf.CeilToInt(pr.settleSeconds / stepDt));
        yield return WaitFor(() => V.state == FState.Loco, 240);
        hr.KnockResetAll(); hr.ResetPeaks(); PhysicsClock.ResetStats();
        V.KnockDown(); V.SetKnock(-dirA * pr.knockSpeedMps);   // the sim API, exactly what Duel.Resolve calls; the slide is the sim's
        var prev = new float[hr.PartCount]; int lastTick = Duel.TickCount, exitLeft = Mathf.Max(1, pr.exitFrames), guard = 0; bool left = false;
        int maxFrames = Mathf.CeilToInt((V.Rule(kc.timeline.stunRule, kc.timeline.stunDefault) * stepDt + pr.observeExtraS) * fps * 2f) + 300;
        while (guard++ < maxFrames)
        {
            yield return null;
            bool down = V.state == FState.Down;
            int dtk = Duel.TickCount - lastTick;
            if (dtk > 0)
            {
                lastTick = Duel.TickCount; if (down) res.downTicks += dtk;
                ulong h = 14695981039346656037UL; h = Mix(h, (ulong)lastTick);
                foreach (var f in new[] { V, A })
                {
                    var p = f.transform.position;
                    h = Mix(h, Bits(f.health)); h = Mix(h, Bits(f.posture)); h = Mix(h, Bits(f.stamina)); h = Mix(h, Bits(p.x)); h = Mix(h, Bits(p.z)); h = Mix(h, (ulong)(int)f.state); h = Mix(h, (ulong)f.t);
                }
                res.ticks[lastTick] = h; res.final = Mix(res.final, h);
            }
            float now = hr.NowMaxDeg; if (float.IsNaN(now)) res.nan = true;
            if (down)
            {
                res.frames++; if (res.startDepth < 0f) res.startDepth = hr.KnockDepth;
                if (now > res.peak) res.peak = now; if (hr.KnockShowing) res.liveFrames++;
            }
            for (int i = 0; i < hr.PartCount; i++) { float dv = hr.DevNowDeg[i]; float r = Mathf.Abs(dv - prev[i]) * fps; if (r > res.pop) res.pop = r; prev[i] = dv; }   // independent of the limiter
            if (hr.HipsDeltaPeakM > res.hips) res.hips = hr.HipsDeltaPeakM;
            if (!down) { if (!left) { left = true; res.exitDev = now; } if (--exitLeft <= 0) break; }
        }
        res.codePop = hr.KnockPopPeakDegPerS; res.codePeak = hr.KnockPeakDevDeg; res.avg = PhysicsClock.AvgMs; res.p95 = PhysicsClock.P95Ms;
        HitReact.KnockSuppress = false; hr.KnockResetAll(); hr.ClearAll();
        yield return WaitFor(() => V.state == FState.Loco, 240);
    }

    // first common sim tick whose hash differs, -1 = identical on every common tick (and at least 90 % of the ticks shared, else the runs did not cover the same thing: -2)
    static int KnockDiff(KnockRes a, KnockRes b)
    {
        int common = 0, first = int.MaxValue;
        foreach (var kv in a.ticks) { ulong hb; if (!b.ticks.TryGetValue(kv.Key, out hb)) continue; common++; if (hb != kv.Value && kv.Key < first) first = kv.Key; }
        if (first != int.MaxValue) return first;
        int shorter = Mathf.Min(a.ticks.Count, b.ticks.Count);
        return common >= (int)(0.9f * shorter) && shorter > 0 ? -1 : -2;
    }

    void KnockChecks(string id, KnockCfg kc, KnockRes on, KnockRes off, KnockRes ctl)
    {
        var pr = kc.proof; var lm = kc.limits;
        Check(id + ".selected", on.liveFrames > 0 && off.liveFrames == 0, "knock blend showed on " + on.liveFrames + " of " + on.frames + " Down frames; off run showed 0 of " + off.frames + " (peak " + F(off.peak) + " deg)");
        Check(id + ".peak", !on.nan && on.peak >= pr.minPeakDeg && on.peak <= pr.maxPeakDeg, "peak shown-vs-clip deviation " + F(on.peak) + " deg, band " + F(pr.minPeakDeg) + ".." + F(pr.maxPeakDeg) + " deg");
        Check(id + ".devCap", on.codePeak <= lm.maxDevDeg + 0.01f, "largest per-part deviation " + F(on.codePeak) + " deg (cap " + F(lm.maxDevDeg) + ")");
        Check(id + ".noPopIn", on.startDepth >= 0f && on.startDepth <= pr.maxStartDepth, "depth on the first Down frame " + F(on.startDepth) + " (limit " + F(pr.maxStartDepth) + ")");
        Check(id + ".noPop", on.pop <= pr.maxPopDegPerS, "largest frame-to-frame change of any part's deviation from the clip (fall, lie, get-up and 1st frames after Down) " + F(on.pop) + " deg/s (limit " + F(pr.maxPopDegPerS) + ")");
        Check(id + ".slew", on.codePop <= lm.slewDegPerS + pr.slewTolDegPerS, "limiter's largest deviation change " + F(on.codePop) + " deg/s (slew " + F(lm.slewDegPerS) + ")");
        Check(id + ".exitBlend", on.exitDev <= pr.exitMaxDeg, "first frame after Down: shown pose vs clip " + F(on.exitDev) + " deg (limit " + F(pr.exitMaxDeg) + ")");
        Check(id + ".hipsFixed", on.hips <= pr.maxHipsDeltaM, "hips moved by the blend: peak " + on.hips.ToString("F5", IC) + " m (limit " + pr.maxHipsDeltaM.ToString("F5", IC) + ")");
        Check(id + ".stepCost", on.avg <= pr.maxAvgStepMs && on.p95 <= pr.maxP95StepMs, "physics ms per step avg " + F(on.avg) + " (max " + F(pr.maxAvgStepMs) + "), p95 " + F(on.p95) + " (max " + F(pr.maxP95StepMs) + ")");
        Check(id + ".simTimeline", off.downTicks > 0 && on.downTicks == off.downTicks, "sim ticks in Down: knock ON " + on.downTicks + ", OFF " + off.downTicks + ", control " + ctl.downTicks);
        int dOn = KnockDiff(on, off), dCtl = KnockDiff(off, ctl);
        if (dOn == -1) Check(id + ".simhash", true, "knock ON vs OFF: " + on.ticks.Count + " ticks byte-identical, final " + on.final.ToString("X16") + (dCtl != -1 ? " (note: the control OFF vs OFF differs at " + dCtl + ": the harness is not fully deterministic on its own)" : ", control OFF vs OFF identical"));
        else if (dCtl == -1) Check(id + ".simhash", false, "knock ON vs OFF DIFFER (first at tick " + dOn + ") while the control OFF vs OFF is identical: THE BLEND LEAKED INTO THE SIM");
        else { Log("knock simhash INCONCLUSIVE (" + id + "): ON vs OFF " + dOn + ", control " + dCtl + ": the harness is not deterministic on its own"); checks.Add("INCONCLUSIVE " + id + ".simhash"); }
    }
}
#else // FIXHARNESS v1 nl=0
#endif // FIXHARNESS v1
