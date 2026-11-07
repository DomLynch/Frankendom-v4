using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// PHYSSWING v1: -physproof:swing (also part of -physproof:all). Part of PhysProof (partial class). Runs only when physics.json swing.enabled=true (sub-switch, default off), on both fighters.
//   track  : the v101 swing-track gate, again, with the swing fixes ON (limit-aware share + lead): the 'track' row (every limb at full strength) while the victim swings swing.proof.trackMove:
//            max shown-vs-clip deviation <= hit proof track.maxDeg (6 deg, NOT relaxed). Also: hit.track.<v>.reach = share of frames in which any part faded because its clip pose is outside the proxy limit
//            must stay <= swing.proof.maxFadedPct, so the gate cannot pass by fading everything. Info lines list the deviation for each lead.latencyTicks in swing.proof.leadSweepTicks (calibration).
//   rows   : one case per swing.proof.situations entry (loco = walk, swing_heavy / swing_light = Press, guard = guard held, roll): the situation row must be SELECTED (expect), the peak shown-vs-clip
//            deviation inside minPeakDeg..maxPeakDeg (min > 0 catches 'nothing happens'), hips never move, feet stay planted, no NaN, physics ms per step. The sim is not compared here (a swing spends stamina
//            by itself): -physproof:simhash (ON vs OFF at 30 / 60 / 144 fps) is the sim gate.
public partial class PhysProof
{
    IEnumerator SwingProof()
    {
        var hc = HitReact.Cfg; var sc = hc.swing; var sp = sc.proof; var pc = hc.proof.hit;
        if (!sc.enabled) { Log("swing proof skipped: physics.json swing.enabled=false (sub-switch, default off)"); yield break; }
        if (!RagdollDeath.Cfg.enabled || !RagdollDeath.Cfg.rig.prebuild) { Log("swing proof skipped: ragdoll.json enabled/prebuild off (no rig, no proxy)"); yield break; }
        yield return Reload();
        ScriptedFighters();
        var hD = D.GetComponent<HitReact>(); var hP = P.GetComponent<HitReact>();
        int guard = 0; while (((hD == null || !hD.Ready) || (hP == null || !hP.Ready)) && guard++ < 300) { yield return null; hD = D.GetComponent<HitReact>(); hP = P.GetComponent<HitReact>(); }
        Check("swing.attached", hD != null && hD.Ready && hP != null && hP.Ready, "HitReact (swing) on " + D.name + " ready=" + (hD != null && hD.Ready) + ", on " + P.name + " ready=" + (hP != null && hP.Ready));
        if (hD == null || !hD.Ready || hP == null || !hP.Ready) yield break;
        foreach (var vname in new[] { "enemy", "player" })
        {
            if (exiting) yield break;
            bool enemy = vname == "enemy";
            DuelFighter V = enemy ? P : D, A = enemy ? D : P; var hr = enemy ? hP : hD;
            Vector3 vp = V.transform.position; Vector3 dirA = Vector3.forward;
            Place(V, vp, dirA); Place(A, vp + dirA * sp.separationM, -dirA);
            yield return Ticks(pc.settleTicks);
            // 1) the v101 gate with the fixes on, plus the lead calibration sweep
            float keep = sc.lead.latencyTicks;
            foreach (float lt in sp.leadSweepTicks)
            {
                sc.lead.latencyTicks = lt; float dev = 0f, fp = 0f; string part = "";
                yield return SwingTrackRun(vname, V, A, hr, sp, pc, v => dev = v, v => fp = v, v => part = v);
                Log("swing.track." + vname + ".sweep latencyTicks " + F(lt) + ": max deviation " + F(dev) + " deg (part " + part + "), faded frames " + F(fp) + " %");
            }
            sc.lead.latencyTicks = keep;
            {
                float dev = 0f, fp = 0f; string part = "";
                yield return SwingTrackRun(vname, V, A, hr, sp, pc, v => dev = v, v => fp = v, v => part = v);
                Check("hit.track." + vname + ".attack.swing", dev <= pc.track.maxDeg, "while '" + sp.trackMove + "' plays with limit-aware share + lead (latencyTicks " + F(keep) + "): max deviation " + F(dev) + " deg (part " + part + "), limit " + F(pc.track.maxDeg) + " deg (v101: 118.9 / 94.8)");
                Check("hit.track." + vname + ".reach", fp <= sp.maxFadedPct, "frames in which a part faded because its clip pose is outside the proxy joint limit: " + F(fp) + " % (limit " + F(sp.maxFadedPct) + " %), worst excess " + F(hr.ReachWorstOverDeg) + " deg");
            }
            // 2) situation rows
            Place(V, vp, dirA); Place(A, vp + dirA * sp.separationM, -dirA);
            yield return Ticks(pc.settleTicks);
            foreach (var ps in sp.situations)
            {
                if (exiting) yield break;
                yield return SwingCase(vname, ps, V, A, hr, sp, pc);
                Place(V, vp, dirA); Place(A, vp + dirA * sp.separationM, -dirA);
                yield return Ticks(Mathf.CeilToInt(sp.settleSeconds * 60f));
            }
        }
        Log("swing proof avg step " + F(PhysicsClock.AvgMs) + " ms");
    }

    // one 'track' run: row 'track' at full strength while the victim plays trackMove; reports max deviation, the part, and the share of frames with a faded part
    IEnumerator SwingTrackRun(string vname, DuelFighter V, DuelFighter A, HitReact hr, SwingProofCfg sp, HitProofCfg pc, Action<float> devOut, Action<float> fadedPctOut, Action<string> partOut)
    {
        var tk = pc.track;
        hr.ClearAll(); hr.ResetPeaks(); hr.SwingResetStats();
        bool ok = hr.TriggerRow(tk.row, "heavy", -A.transform.forward, "spine2");
        Check("swing.track." + vname + ".started", ok && hr.ActiveCount > 0, "TriggerRow('" + tk.row + "') = " + ok + ", reactions " + hr.ActiveCount);
        if (!ok) { devOut(999f); fadedPctOut(100f); partOut("none"); yield break; }
        yield return Ticks(Mathf.CeilToInt(tk.idleSeconds * 60f));
        hr.ResetPeaks(); hr.SwingResetStats();
        V.Press(sp.trackMove);
        int t0 = Duel.TickCount, ticks = Mathf.CeilToInt(sp.trackSeconds * 60f), fr = 0; float mx = 0f; string mp = "";
        while (Duel.TickCount - t0 < ticks && fr++ < ticks * 4 + 300)
        {
            yield return null;
            for (int i = 0; i < hr.PartCount; i++) if (hr.DevNowDeg[i] > mx) { mx = hr.DevNowDeg[i]; mp = hr.PartName(i); }
        }
        devOut(mx); partOut(mp); fadedPctOut(hr.SwingFrames > 0 ? 100f * hr.SwingFadedFrames / hr.SwingFrames : 0f);
        hr.ClearAll();
        yield return WaitFor(() => V.state == FState.Loco, 240);
        yield return Ticks(pc.settleTicks);
    }

    IEnumerator SwingCase(string vname, SwingProofSit ps, DuelFighter V, DuelFighter A, HitReact hr, SwingProofCfg sp, HitProofCfg pc)
    {
        string id = "swing." + vname + "." + ps.id;
        hr.ClearAll(); hr.ResetPeaks(); hr.SwingResetStats();
        int fl = hr.PartIndex("footL"), fr2 = hr.PartIndex("footR");
        Vector3 fL0 = fl >= 0 ? hr.PartBone(fl).position - hr.PartAnimPos(fl) : Vector3.zero, fR0 = fr2 >= 0 ? hr.PartBone(fr2).position - hr.PartAnimPos(fr2) : Vector3.zero;
        PhysicsClock.ResetStats();
        Vector3 toward = (A.transform.position - V.transform.position); toward.y = 0f; toward.Normalize();
        if (ps.driver == "move") { V.moveDir = toward; V.run = false; }
        else if (ps.driver == "guard") V.guardHeld = true;
        else if (ps.driver == "press" && !string.IsNullOrEmpty(ps.move)) V.Press(ps.move);
        int t0 = Duel.TickCount, ticks = Mathf.CeilToInt(ps.seconds * 60f), fr = 0; float peak = 0f, foot = 0f, hips = 0f; string peakPart = ""; bool nan = false;
        var seen = new HashSet<string>();
        while (Duel.TickCount - t0 < ticks && fr++ < ticks * 4 + 300)
        {
            yield return null;
            float n = hr.NowMaxDeg; if (float.IsNaN(n)) nan = true;
            if (n > peak) { peak = n; peakPart = hr.PeakPartName; }
            if (!string.IsNullOrEmpty(hr.SwingSituationId)) seen.Add(hr.SwingSituationId);
            hips = Mathf.Max(hips, hr.HipsDeltaPeakM);
            if (fl >= 0) foot = Mathf.Max(foot, ((hr.PartBone(fl).position - hr.PartAnimPos(fl)) - fL0).magnitude);
            if (fr2 >= 0) foot = Mathf.Max(foot, ((hr.PartBone(fr2).position - hr.PartAnimPos(fr2)) - fR0).magnitude);
        }
        V.moveDir = Vector3.zero; V.guardHeld = false; V.run = false;
        var sb = new StringBuilder(); foreach (var s in seen) sb.Append(s).Append(' ');
        bool selected = ps.driver == "hold" || ps.expect == null || ps.expect.Length == 0;
        if (!selected) foreach (var e in ps.expect) if (seen.Contains(e)) selected = true;
        Check(id + ".selected", selected, "situation rows seen: " + (sb.Length > 0 ? sb.ToString().Trim() : "none") + "; expected one of: " + (ps.expect != null ? string.Join(" ", ps.expect) : ""));
        Check(id + ".peak", !nan && peak >= ps.minPeakDeg && peak <= ps.maxPeakDeg, "peak shown-vs-clip deviation " + F(peak) + " deg (part " + peakPart + "), band " + F(ps.minPeakDeg) + ".." + F(ps.maxPeakDeg) + " deg");
        Check(id + ".hipsFixed", hips <= sp.maxHipsDeltaM, "hips moved by the blend: peak " + hips.ToString("F5", IC) + " m (limit " + sp.maxHipsDeltaM.ToString("F5", IC) + ")");
        Check(id + ".feetPlanted", foot <= sp.maxFootDriftM, "foot bones drifted at most " + F(foot) + " m from the clip (limit " + F(sp.maxFootDriftM) + ", FootGround runs after the blend)");
        float avg = PhysicsClock.AvgMs, p95 = PhysicsClock.P95Ms;
        Check(id + ".stepCost", avg <= sp.maxAvgStepMs && p95 <= sp.maxP95StepMs, "physics ms per step avg " + F(avg) + " (max " + F(sp.maxAvgStepMs) + "), p95 " + F(p95) + " (max " + F(sp.maxP95StepMs) + ")  [empty scene alone ~0.47 avg]");
        hr.ClearAll();
        yield return WaitFor(() => V.state == FState.Loco, 240);
    }
}
