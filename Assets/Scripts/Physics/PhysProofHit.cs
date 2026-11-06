using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// PHYS2 v1: -physproof:hit (PH3 P2 proof). Part of PhysProof (partial class, PhysProof.cs declares it partial and calls HitProof from Main).
//   Synthetic Hit / Blocked / Parried events are handed STRAIGHT to the victim's HitReact (no hit-stop, blood, camera or sim involvement), the victim idles, and the numbers are the deviation in degrees
//   between the SHOWN bone rotation and the animated one (HitReact.DevNowDeg, world rotation, so a part carried by its parent counts too).
//   track  : calibration of the joint-target conversion: row 'track' (every limb at full strength, no blow, shown 100 percent) while the victim idles and while it swings a heavy: deviation < hit proof track.maxDeg.
//   hit    : victim (enemy, player) x class (light, heavy, counter) x zone (head, torso, legs, arms): triggered, peak deviation inside the class band, back to the clip (< recoverDeg) within
//            the reaction's duration + recoverExtraS, hips never move (the proxies never touch the root), feet stay where they were (FootGround runs after the blend), the sim state of both fighters
//            (hp, posture, stamina, state) is unchanged, no NaN, physics ms per step. Then per victim and zone: heavy peak >= heavyOverLightMin x light peak (the 'numeric sway amplitude per blow class').
//   clash  : Blocked / perfect / Parried / GuardBroken recoil on the arms, as attacker and as defender: peak inside clashMin..clashMax.
//   Output: physproof_hit_table.txt (every case: peak deg, part, time of the peak, time back to the clip, hips, feet), physproof_hit_sheet.png (rows = light / heavy / counter / clash cases on the sheet zones,
//   columns = sim ticks after the blow, cropped around the victim), and the usual physproof_log.txt / summary.
public partial class PhysProof
{
    class HitRes { public string id, victim, cls, zone, part = ""; public float peak, peakAtS, recS = -1f, dur, hips, foot; public bool clash; }
    readonly List<HitRes> hitRes = new List<HitRes>();
    readonly List<Color32[]> hsheet = new List<Color32[]>(); readonly List<string> hsheetRows = new List<string>(); int hsheetCols;

    IEnumerator HitProof()
    {
        var hc = HitReact.Cfg; var pc = hc.proof.hit;
        if (!hc.hit.enabled) { Log("hit proof skipped: physics.json hit.enabled=false"); yield break; }
        if (!RagdollDeath.Cfg.enabled || !RagdollDeath.Cfg.rig.prebuild) { Log("hit proof skipped: ragdoll.json enabled/prebuild off (no rig, no hit reaction)"); yield break; }
        yield return Reload();
        ScriptedFighters();
        hsheetCols = Mathf.Max(1, pc.sheetTicks.Length);
        var hD = D.GetComponent<HitReact>(); var hP = P.GetComponent<HitReact>();
        int guard = 0; while (((hD == null || !hD.Ready) || (hP == null || !hP.Ready)) && guard++ < 300) { yield return null; hD = D.GetComponent<HitReact>(); hP = P.GetComponent<HitReact>(); }
        Check("hit.attached", hD != null && hD.Ready && hP != null && hP.Ready, "HitReact on " + D.name + " ready=" + (hD != null && hD.Ready) + (hD != null ? " (build " + F(hD.BuildMs) + " ms, " + hD.PartCount + " parts)" : "") + ", on " + P.name + " ready=" + (hP != null && hP.Ready) + (hP != null ? " (build " + F(hP.BuildMs) + " ms, " + hP.PartCount + " parts)" : "") + ", mode " + hc.hit.mode);
        if (hD == null || !hD.Ready || hP == null || !hP.Ready) yield break;

        foreach (var vname in pc.victims)
        {
            if (exiting) yield break;
            bool enemy = vname == "enemy";
            DuelFighter V = enemy ? P : D, A = enemy ? D : P; var hr = enemy ? hP : hD;
            Vector3 vp = V.transform.position; Vector3 dirA = Vector3.forward;
            // 1) calibration
            if (pc.track != null && !string.IsNullOrEmpty(pc.track.row))
            {
                Place(V, vp, dirA); Place(A, vp + dirA * pc.track.separationM, -dirA);
                yield return Ticks(pc.settleTicks);
                yield return TrackCase(vname, V, A, hr, pc);
            }
            Place(V, vp, dirA); Place(A, vp + dirA * pc.separationM, -dirA);
            yield return Ticks(pc.settleTicks);
            // 2) blows
            foreach (var cl in pc.classes)
                foreach (var zone in pc.zones)
                {
                    if (exiting) yield break;
                    var ev = new CombatEvent { type = "Hit", actor = A, target = V, moveId = cl.moveId, location = zone, direction = "front", heavy = cl.heavy, counter = cl.counter };
                    bool onSheet = Array.IndexOf(pc.sheetZones, zone) >= 0;
                    yield return HitCase("hit_" + vname + "_" + cl.name + "_" + zone, vname, cl.name, zone, V, A, hr, ev, false, Array.IndexOf(pc.minPeakZones, zone) >= 0 ? cl.minPeakDeg : 0f, cl.maxPeakDeg, onSheet, pc);
                }
            // 3) block / parry recoil
            foreach (var ck in pc.clash)
                foreach (string role in new[] { "attacker", "defender" })
                {
                    if (exiting) yield break;
                    var ev = new CombatEvent { type = ck.type, actor = role == "attacker" ? V : A, target = role == "attacker" ? A : V, moveId = "light_right", location = "torso", direction = "front", heavy = ck.heavy, perfect = ck.perfect };
                    yield return HitCase("clash_" + vname + "_" + ck.kind + "_" + role, vname, "clash_" + ck.kind, role, V, A, hr, ev, true, pc.clashMinPeakDeg, pc.clashMaxPeakDeg, true, pc);
                }
            // 4) class ordering per zone
            foreach (var zone in pc.zones)
            {
                float lp = -1f, hp = -1f;
                foreach (var r in hitRes) if (r.victim == vname && r.zone == zone && !r.clash) { if (r.cls == "light") lp = r.peak; else if (r.cls == "heavy") hp = r.peak; }
                if (lp >= 0f && hp >= 0f) Check("hit." + vname + ".heavyOverLight." + zone, hp >= lp * pc.heavyOverLightMin, "heavy peak " + F(hp) + " deg vs light " + F(lp) + " deg (need heavy >= " + F(pc.heavyOverLightMin) + " x light)");
            }
        }
        // table + class summary
        var sb = new StringBuilder("id\tpeak_deg\tpart\tpeak_at_s\tback_to_clip_s\treaction_s\thips_delta_m\tfoot_drift_m\n");
        foreach (var r in hitRes) sb.Append(r.id).Append('\t').Append(F(r.peak)).Append('\t').Append(r.part).Append('\t').Append(F(r.peakAtS)).Append('\t').Append(r.recS < 0f ? "never" : F(r.recS)).Append('\t').Append(F(r.dur)).Append('\t').Append(r.hips.ToString("F5", IC)).Append('\t').Append(F(r.foot)).Append('\n');
        try { File.WriteAllText(Path.Combine(dir, "physproof_hit_table.txt"), sb.ToString()); } catch (Exception e) { Log("table failed: " + e.Message); }
        foreach (var cl in pc.classes)
        {
            float mx = 0f, sum = 0f; int cnt = 0; foreach (var r in hitRes) if (!r.clash && r.cls == cl.name) { mx = Mathf.Max(mx, r.peak); sum += r.peak; cnt++; }
            Log("class " + cl.name + ": peak deviation max " + F(mx) + " deg, mean " + F(cnt > 0 ? sum / cnt : 0f) + " deg over " + cnt + " blows (band " + F(cl.minPeakDeg) + ".." + F(cl.maxPeakDeg) + ")");
        }
        try { WriteHitSheet(pc); } catch (Exception e) { Log("hit sheet failed: " + e.Message); }
        Log("hit proof avg step " + F(PhysicsClock.AvgMs) + " ms");
    }

    // ---------------------------------------------------------------- calibration
    IEnumerator TrackCase(string vname, DuelFighter V, DuelFighter A, HitReact hr, HitProofCfg pc)
    {
        var tk = pc.track;
        hr.ClearAll(); hr.ResetPeaks();
        bool ok = hr.TriggerRow(tk.row, "heavy", -A.transform.forward, "spine2");
        Check("hit.track." + vname + ".started", ok && hr.ActiveCount > 0, "TriggerRow('" + tk.row + "') = " + ok + ", reactions " + hr.ActiveCount + " (physics.json hit.rows needs the 'track' row)");
        if (!ok) yield break;
        int t0 = Duel.TickCount; int idleTicks = Mathf.CeilToInt(tk.idleSeconds * 60f);
        float idleMax = 0f; string idlePart = ""; int fr = 0;
        while (Duel.TickCount - t0 < idleTicks && fr++ < idleTicks * 4 + 300) { yield return null; float n = hr.NowMaxDeg; if (n > idleMax) { idleMax = n; idlePart = hr.PeakPartName; } }
        Check("hit.track." + vname + ".idle", idleMax <= tk.maxDeg, "idle: shown pose vs clip at strength 1, no blow: max deviation " + F(idleMax) + " deg (part " + idlePart + "), limit " + F(tk.maxDeg) + " deg  [PH3: joint-space target conversion]");
        hr.ResetPeaks();
        V.Press(tk.move);
        t0 = Duel.TickCount; int atkTicks = Mathf.CeilToInt(tk.seconds * 60f); float atkMax = 0f; string atkPart = ""; bool nan = false; fr = 0;
        var worst = new float[hr.PartCount];
        while (Duel.TickCount - t0 < atkTicks && fr++ < atkTicks * 4 + 300)
        {
            yield return null;
            float n = hr.NowMaxDeg; if (float.IsNaN(n)) nan = true;
            for (int i = 0; i < worst.Length; i++) worst[i] = Mathf.Max(worst[i], hr.DevNowDeg[i]);
            if (n > atkMax) { atkMax = n; atkPart = hr.PeakPartName; }
        }
        var top = new StringBuilder(); for (int k = 0; k < 3; k++) { int bi = -1; float bv = -1f; for (int i = 0; i < worst.Length; i++) if (worst[i] > bv) { bv = worst[i]; bi = i; } if (bi < 0) break; top.Append(' ').Append(hr.PartName(bi)).Append('=').Append(bv.ToString("F1", IC)); worst[bi] = -2f; }
        Check("hit.track." + vname + ".attack", !nan && atkMax <= tk.maxDeg, "while '" + tk.move + "' plays: max deviation " + F(atkMax) + " deg (part " + atkPart + "), worst parts:" + top + ", limit " + F(tk.maxDeg) + " deg");
        Check("hit.track." + vname + ".hips", hr.HipsDeltaPeakM <= pc.maxHipsDeltaM, "hips moved by the blend: peak " + hr.HipsDeltaPeakM.ToString("F5", IC) + " m (limit " + pc.maxHipsDeltaM.ToString("F5", IC) + ")");
        hr.ClearAll();
        yield return WaitFor(() => V.state == FState.Loco, 240);
        yield return Ticks(pc.settleTicks);
    }

    // ---------------------------------------------------------------- one blow
    IEnumerator HitCase(string id, string vname, string cls, string zone, DuelFighter V, DuelFighter A, HitReact hr, CombatEvent ev, bool clash, float minPeak, float maxPeak, bool onSheet, HitProofCfg pc)
    {
        hr.ClearAll(); hr.ResetPeaks();
        yield return Ticks(pc.settleTicks);
        ev.tick = Duel.TickCount;
        int fl = hr.PartIndex("footL"), fr2 = hr.PartIndex("footR"), sp2 = hr.PartIndex("spine2");
        Vector3 fL0 = fl >= 0 ? hr.PartBone(fl).position - hr.PartAnimPos(fl) : Vector3.zero, fR0 = fr2 >= 0 ? hr.PartBone(fr2).position - hr.PartAnimPos(fr2) : Vector3.zero;   // INTPHYS v99: baseline = shown foot minus the clip's own foot
        float vh = V.health, vp = V.posture, vs = V.stamina, ah = A.health, ap = A.posture, asx = A.stamina; var vst = V.state; var ast = A.state;
        PhysicsClock.ResetStats();
        var res = new HitRes { id = id, victim = vname, cls = cls, zone = zone, clash = clash };
        if (clash) hr.TriggerClash(ev); else hr.TriggerHit(ev);
        yield return null;
        bool started = hr.ActiveCount > 0;
        Check(id + ".started", started, "reactions after the event: " + hr.ActiveCount + " (row " + hr.LastRow + ", class " + hr.LastClass + ", dir " + hr.LastDir + ")");
        if (!started) { hitRes.Add(res); yield break; }
        res.dur = hr.LastDuration;
        int t0 = Duel.TickCount, maxTicks = Mathf.CeilToInt((hr.LastDuration + pc.recoverExtraS + pc.observeExtraS) * 60f) + 6; int frames = 0, next = 0, recTick = -1;
        float foot = 0f, now, best = 0f, lastActive = 0f; bool nan = false;
        var cells = new Color32[hsheetCols][];
        while (Duel.TickCount - t0 < maxTicks && frames++ < maxTicks * 4 + 200)
        {
            int tk = Duel.TickCount - t0;
            now = hr.NowMaxDeg; if (float.IsNaN(now)) nan = true;
            if (now > best) { best = now; res.peakAtS = tk / 60f; }
            if (fl >= 0) foot = Mathf.Max(foot, ((hr.PartBone(fl).position - hr.PartAnimPos(fl)) - fL0).magnitude);
            if (fr2 >= 0) foot = Mathf.Max(foot, ((hr.PartBone(fr2).position - hr.PartAnimPos(fr2)) - fR0).magnitude);
            if (hr.ActiveCount > 0) lastActive = now;   // the deviation while the reaction still ran (Park zeroes the readout afterwards)
            if (recTick < 0 && hr.ActiveCount == 0) recTick = tk;
            if (onSheet && next < pc.sheetTicks.Length && tk >= pc.sheetTicks[next])
            {
                yield return new WaitForEndOfFrame();
                cells[next] = GrabCellAround(sp2 >= 0 ? hr.PartBone(sp2) : V.transform, pc.sheetCellWidth, pc.sheetCellHeight, pc.sheetCropFraction); next++;
            }
            yield return null;
        }
        res.peak = hr.PeakMaxDeg; res.part = hr.PeakPartName; res.hips = hr.HipsDeltaPeakM; res.foot = foot; res.recS = recTick >= 0 ? recTick / 60f : -1f;
        Check(id + ".peak", !nan && res.peak >= minPeak && res.peak <= maxPeak, "peak shown-vs-clip deviation " + F(res.peak) + " deg at " + F(res.peakAtS) + " s (part " + res.part + "), band " + F(minPeak) + ".." + F(maxPeak) + " deg");
        Check(id + ".recovered", recTick >= 0 && lastActive <= pc.recoverDeg && res.recS <= res.dur + pc.recoverExtraS + 2f / 60f, "back to the clip after " + (recTick >= 0 ? F(res.recS) + " s" : "never") + " (reaction " + F(res.dur) + " s + " + F(pc.recoverExtraS) + " s allowed), deviation on the last frame the reaction ran " + F(lastActive) + " deg (limit " + F(pc.recoverDeg) + ")");
        Check(id + ".hipsFixed", res.hips <= pc.maxHipsDeltaM, "hips moved by the blend: peak " + res.hips.ToString("F5", IC) + " m (limit " + pc.maxHipsDeltaM.ToString("F5", IC) + ")");
        Check(id + ".feetPlanted", res.foot <= pc.maxFootDriftM, "foot bones drifted at most " + F(res.foot) + " m from their pre-blow position (limit " + F(pc.maxFootDriftM) + ", FootGround runs after the blend)");
        bool simSame = Mathf.Approximately(vh, V.health) && Mathf.Approximately(vp, V.posture) && Mathf.Approximately(vs, V.stamina) && Mathf.Approximately(ah, A.health) && Mathf.Approximately(ap, A.posture) && Mathf.Approximately(asx, A.stamina) && vst == V.state && ast == A.state;
        Check(id + ".simUntouched", simSame, "victim hp/posture/stamina/state " + F(vh) + "/" + F(vp) + "/" + F(vs) + "/" + vst + " -> " + F(V.health) + "/" + F(V.posture) + "/" + F(V.stamina) + "/" + V.state + ", attacker " + F(ah) + "/" + F(ap) + "/" + F(asx) + "/" + ast + " -> " + F(A.health) + "/" + F(A.posture) + "/" + F(A.stamina) + "/" + A.state);
        float avg = PhysicsClock.AvgMs, p95 = PhysicsClock.P95Ms;
        Check(id + ".stepCost", avg <= pc.maxAvgStepMs && p95 <= pc.maxP95StepMs, "physics ms per step avg " + F(avg) + " (max " + F(pc.maxAvgStepMs) + "), p95 " + F(p95) + " (max " + F(pc.maxP95StepMs) + ")");
        hitRes.Add(res);
        if (onSheet) { for (int i = 0; i < hsheetCols; i++) hsheet.Add(cells[i] ?? new Color32[pc.sheetCellWidth * pc.sheetCellHeight]); hsheetRows.Add(id); }
    }

    // ---------------------------------------------------------------- contact sheet
    Color32[] GrabCellAround(Transform focus, int w, int h, float frac)
    {
        Texture2D tex = null;
        try { CaptureUtil.HideConsole(); tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("capture failed: " + e.Message); }
        if (tex == null) return new Color32[w * h];
        var src = tex.GetPixels32(); var o = new Color32[w * h];
        float ch = Mathf.Min(tex.height, tex.height * Mathf.Clamp(frac, 0.1f, 1f)), cw = Mathf.Min(tex.width, ch * w / h);
        Vector3 sp = new Vector3(tex.width * 0.5f, tex.height * 0.5f, 1f);
        var cam = Camera.main; if (cam != null && focus != null) { Vector3 s = cam.WorldToScreenPoint(focus.position); if (s.z > 0f) { float k = tex.width / Mathf.Max(1f, (float)Screen.width); sp = new Vector3(s.x * k, s.y * k, s.z); } }
        float x0 = Mathf.Clamp(sp.x - cw * 0.5f, 0f, tex.width - cw), y0 = Mathf.Clamp(sp.y - ch * 0.5f, 0f, tex.height - ch);
        for (int y = 0; y < h; y++) { int sy = Mathf.Clamp((int)(y0 + (y + 0.5f) * ch / h), 0, tex.height - 1); for (int x = 0; x < w; x++) { int sx = Mathf.Clamp((int)(x0 + (x + 0.5f) * cw / w), 0, tex.width - 1); o[y * w + x] = src[sy * tex.width + sx]; } }
        Destroy(tex); return o;
    }

    void WriteHitSheet(HitProofCfg pc)
    {
        if (hsheetRows.Count == 0 || hsheetCols <= 0) return;
        int w = pc.sheetCellWidth, h = pc.sheetCellHeight, rows = hsheetRows.Count;
        var t = new Texture2D(w * hsheetCols, h * rows, TextureFormat.RGBA32, false);
        var all = new Color32[w * hsheetCols * h * rows];
        for (int r = 0; r < rows; r++) for (int c = 0; c < hsheetCols; c++)
        {
            var cell = hsheet[r * hsheetCols + c]; int x0 = c * w, y0 = (rows - 1 - r) * h;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) all[(y0 + y) * (w * hsheetCols) + x0 + x] = cell[y * w + x];
        }
        t.SetPixels32(all); t.Apply(false);
        File.WriteAllBytes(Path.Combine(dir, "physproof_hit_sheet.png"), t.EncodeToPNG());
        var sb = new StringBuilder("hit sheet rows top to bottom: "); for (int i = 0; i < rows; i++) sb.Append(hsheetRows[i]).Append(i + 1 < rows ? ", " : ""); sb.Append("; columns = sim ticks after the blow:");
        foreach (var f in pc.sheetTicks) sb.Append(' ').Append(f);
        Log(sb.ToString());
    }
}
