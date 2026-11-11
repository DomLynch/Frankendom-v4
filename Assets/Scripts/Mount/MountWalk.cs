using System;
using UnityEngine;

// laneMOUNTWALK v1 (bandits-dev2, staged, NOT applied, UNPROVEN in Unity). Two jobs, both reading horse_behaviour.json (the same gait table and rigMap as the ambient horses):
// (1) AmbientHorses partial: a fleeing horse that finds no clear heading no longer calms down on the spot (log: 'boxed in' x4 in four hits, so it stood still and died); it takes the
//     best heading that still has ground ahead. A horse inside skit.spaceM of the player walks away from him (they used to stroll into him: log 'target ... dist 0.5 m').
// (2) MountLegs: the RIDDEN mount (MountRider) drives the same 9-joint rig (HB_*) with the same stance/swing IK, so its legs walk, trot and gallop with planted hooves. No rig in the glb = no-op (old behaviour).
// No collider, no DuelFighter, no damage, no weapon: nothing here can ever hurt the player.
public sealed partial class AmbientHorses
{
    // the strict HbSteer found no heading clear of the avoid circles: best heading by metres of ground ahead (circles ignored), nearest the away direction on a tie
    bool HbSteerRelax(Horse h, HbHorse b, AhHerd herd, HbCfg C, out float best)
    {
        var k = C.skit; best = b.fleeBase; float step = Mathf.Max(1f, k.fanStepDeg), score = 0f; int n = Mathf.RoundToInt(k.fanMaxDeg / step), steps = Mathf.Max(1, Mathf.CeilToInt(k.lookAheadM / Mathf.Max(0.5f, k.groundCheckStepM)));   // hc:ok guards
        for (int i = 0; i <= n * 2; i++)
        {
            float off = ((i + 1) / 2) * step * ((i & 1) == 1 ? 1f : -1f); if (i == 0) off = 0f; float yaw = b.fleeBase + off; var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward; int ok = 0; float gy;
            for (int q = 1; q <= steps; q++) { var pt = h.pos + dir * (k.lookAheadM * q / steps); if (Ground(pt.x, pt.z, h.pos.y, out gy)) ok++; else break; }
            if (ok > score) { score = ok; best = yaw; }
        }
        return score > 0f;
    }

    // player closer than skit.spaceM: walk straight away from him (stroll only; a flee already runs away from the hit)
    bool HbSpace(Horse h, DuelFighter p, HbCfg C, ref float desired, ref float wantYaw)
    {
        var k = C.skit; if (p == null || k.spaceM <= 0f) return false;
        var d = h.pos - p.transform.position; d.y = 0f; if (d.magnitude >= k.spaceM) return false;
        if (d.sqrMagnitude < HbEps) d = Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward;
        wantYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; float align = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(h.yaw, wantYaw)) / Mathf.Max(1f, k.alignDeg)); desired = k.spaceMps * align; h.walking = false; h.wait = Mathf.Max(h.wait, 1f);   // hc:ok guards
        return true;
    }
}

public static class MountLegs
{
    class R { public Transform body; public Transform[] U = new Transform[4], L = new Transform[4], F = new Transform[4]; public Vector3[] pivot = new Vector3[4], foot = new Vector3[4]; public float[] l1 = new float[4], l2 = new float[4], r1 = new float[4], r2 = new float[4]; }
    static R rig; static Transform bound; static float phase, bodyY, strideC, dutyC, liftC; static int gait = -1; static readonly float[] off = new float[4];
    static readonly string[] Legs = { "FL", "FR", "HL", "HR" };

    /// <summary>MountRider.LoadHorse: the rigged copy of the glb when horse_behaviour.json maps one and it exists, else the same path.</summary>
    public static string RigFile(string full)
    {
        try
        {
            var C = AmbientHorses.HbGet(); if (C == null || !C.enabled || C.rigMap == null || string.IsNullOrEmpty(full)) return full;
            string sa = Application.streamingAssetsPath.Replace('\\', '/'), f = full.Replace('\\', '/'); if (!f.StartsWith(sa, StringComparison.OrdinalIgnoreCase)) return full;
            string rel = f.Substring(sa.Length).TrimStart('/');
            foreach (var m in C.rigMap) if (m != null && m.from == rel && !string.IsNullOrEmpty(m.to)) { var p = System.IO.Path.Combine(Application.streamingAssetsPath, m.to); if (System.IO.File.Exists(p)) return p; }
        }
        catch (Exception) { }
        return full;
    }

    static Transform Find(Transform root, string name) { if (root == null) return null; foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t; return null; }

    static R Make(Transform body)
    {
        Transform hb = Find(body, "HB_Body"); if (hb == null) return null; var r = new R { body = hb };
        for (int i = 0; i < 4; i++) { r.U[i] = Find(hb.parent, "HB_U_" + Legs[i]); r.L[i] = Find(hb.parent, "HB_L_" + Legs[i]); r.F[i] = Find(hb.parent, "HB_F_" + Legs[i]); if (r.U[i] == null || r.L[i] == null || r.F[i] == null) return null; }
        for (int i = 0; i < 4; i++)
        {
            r.pivot[i] = body.InverseTransformPoint(r.U[i].position); var kn = body.InverseTransformPoint(r.L[i].position); r.foot[i] = body.InverseTransformPoint(r.F[i].position);
            var u = kn - r.pivot[i]; var l = r.foot[i] - kn; r.l1[i] = new Vector2(u.z, u.y).magnitude; r.l2[i] = new Vector2(l.z, l.y).magnitude; r.r1[i] = Mathf.Atan2(u.z, -u.y); r.r2[i] = Mathf.Atan2(l.z, -l.y);
        }
        var smr = body.GetComponentInChildren<SkinnedMeshRenderer>(true); if (smr != null) { var bb = smr.localBounds; bb.Expand(C0().move.boundsPadM); smr.localBounds = bb; smr.updateWhenOffscreen = false; }
        Debug.Log("[MountLegs] rig bound on " + body.parent.name + " (9 joints, legs L1/L2 FL " + r.l1[0].ToString("F2") + "/" + r.l2[0].ToString("F2") + ")");
        return r;
    }
    static HbCfg C0() { return AmbientHorses.HbGet(); }

    /// <summary>MountRider.PlaceHorse, after the body bob / pitch / roll: stance/swing hooves for the ground speed `speed` (m/s).</summary>
    public static void Apply(Transform body, float speed, float dt)
    {
        try
        {
            var C = C0(); if (C == null || !C.enabled || C.gaits == null || C.gaits.Length == 0 || body == null || dt <= 0f) return;
            if (bound != body) { bound = body; rig = Make(body); phase = 0f; gait = -1; bodyY = 0f; }
            if (rig == null) return; var go = body.parent; float sc = body.localScale.y, sink = C.skit.mountSinkM;
            int gi = 0; for (int i = 0; i < C.gaits.Length; i++) { float need = C.gaits[i].minMps; if (gait >= 0) { if (i > gait) need += C.move.hysteresisMps; else if (i == gait) need -= C.move.hysteresisMps; } if (speed >= need) gi = i; }
            var g = C.gaits[gi]; if (gait < 0) { strideC = g.strideM; dutyC = g.duty; liftC = g.liftM; for (int i = 0; i < 4; i++) off[i] = g.offsets[i]; }
            gait = gi; float k = Mathf.Clamp01(dt / Mathf.Max(0.01f, C.move.gaitBlendS));   // hc:ok guard
            strideC = Mathf.Lerp(strideC, g.strideM, k); dutyC = Mathf.Lerp(dutyC, g.duty, k); liftC = Mathf.Lerp(liftC, g.liftM, k);
            for (int i = 0; i < 4; i++) { float d = Mathf.Repeat(g.offsets[i] - off[i] + 0.5f, 1f) - 0.5f; off[i] += d * k; }   // hc:ok circular lerp
            float move = Mathf.Clamp01(speed / Mathf.Max(0.01f, C.move.settleMps)); phase = Mathf.Repeat(phase + speed / Mathf.Max(0.01f, strideC) * dt, 1f);   // hc:ok guards
            var tgt = new Vector3[4]; float S = dutyC * strideC * move, byMax = 0f;
            for (int i = 0; i < 4; i++)
            {
                float ph = Mathf.Repeat(phase + off[i], 1f); var slot = rig.foot[i] * sc; float z, y;
                if (ph < dutyC) { float u = ph / Mathf.Max(0.01f, dutyC); z = slot.z + S * 0.5f - S * u; y = 0f; }   // hc:ok stance sweep
                else { float w = (ph - dutyC) / Mathf.Max(0.01f, 1f - dutyC), e = w * w * (3f - 2f * w); z = slot.z - S * 0.5f + S * e; y = liftC * move * Mathf.Sin(w * Mathf.PI); }   // hc:ok smoothstep swing
                tgt[i] = new Vector3(slot.x, sink + y, z);   // sink: hoof marker under the collider plane = the visible floor
                float lr = (rig.l1[i] + rig.l2[i]) * sc * C.move.reach, dz = z - rig.pivot[i].z * sc; if (lr * lr > dz * dz) { float cap = tgt[i].y - rig.pivot[i].y * sc + Mathf.Sqrt(lr * lr - dz * dz); if (cap < byMax) byMax = cap; }
            }
            FootFx.MountTick(go, phase, off, dutyC, tgt, sc, speed);   // FOOTFX v1: hoof plant = stance start of a leg
            byMax = Mathf.Max(byMax, -C.move.maxDropM); bodyY = Mathf.MoveTowards(bodyY, byMax, C.move.bodyDropRateMps * dt); body.localPosition += Vector3.up * bodyY;
            for (int i = 0; i < 4; i++)
            {
                var tb = body.InverseTransformPoint(go.TransformPoint(tgt[i])); var pv = rig.pivot[i]; float tz = tb.z - pv.z, ty = tb.y - pv.y, l1 = rig.l1[i], l2 = rig.l2[i], d = Mathf.Sqrt(tz * tz + ty * ty);
                d = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 0.001f, l1 + l2 - 0.001f); float phi = Mathf.Atan2(tz, -ty), cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f), a = Mathf.Acos(cosA), sgn = i < 2 ? 1f : -1f;   // hc:ok IK epsilons, fore/hind sign
                float a1 = phi + sgn * a, kz = l1 * Mathf.Sin(a1), ky = -l1 * Mathf.Cos(a1), a2 = Mathf.Atan2(tz - kz, -(ty - ky)), th1 = -(a1 - rig.r1[i]), th2 = -((a2 - rig.r2[i]) - (a1 - rig.r1[i]));
                rig.U[i].localRotation = Quaternion.Euler(th1 * Mathf.Rad2Deg, 0f, 0f); rig.L[i].localRotation = Quaternion.Euler(th2 * Mathf.Rad2Deg, 0f, 0f);
            }
        }
        catch (Exception e) { Debug.LogWarning("[MountLegs] " + e.Message); }
    }
}
