using System;
using System.Collections.Generic;
using UnityEngine;

// V64F: interpenetration probe of the two fighters over the WHOLE finisher scene (-finisherauto, also cheap enough to leave on).
//  * hips: distance between the two Hips bones every frame. FAIL when it stays below hipsMinM for more than hipsFailS cumulative scene seconds (river_light: the thrown victim passes through the killer).
//  * capsules: both skeletons as bone-to-bone capsules (rt.pair.capsules; radius x the fighter's scale). Separation = segment distance - radii; < -capsulePenM counts as overlapping;
//    FAIL when the cumulative overlap time exceeds capsuleFailS. Weapons are not modelled (they pass through bodies by design).
// All numbers are data (finishers.json rt.pair). The component-free static design costs one 20 x 20 segment test per frame while a scene runs.
[Serializable] public class PairCapsuleRow { public string a = "", b = ""; public float radiusM = 0.1f; public string group = ""; }   // empty = derived from the bone name   // V64F2: group core (hips / spine / neck / head), leg, arm (arm / forearm / hand)

[Serializable]
public class FinisherPairCfg
{
    public bool enabled = true;
    public string hipsBone = "Hips";
    public float hipsMinM = 0.35f, hipsFailS = 0.3f;
    public float capsulePenM = 0.03f, capsuleFailS = 0.3f; public bool capsulesFail = true;
    // V64F2: capsules are shrunk (radiusK) so limb brushes do not count; only the group pairs in failPairs can FAIL (torso through torso / leg), every other overlap (arms, hands, the blade contact itself) is a WARN
    public float radiusK = 0.6f; public string[] failPairs = { "core-core", "core-leg" };
    public PairCapsuleRow[] capsules = Defaults();
    static PairCapsuleRow R(string a, string b, float r) { string g = a.Contains("Arm") || a.Contains("Hand") ? "arm" : a.Contains("Leg") ? "leg" : "core"; return new PairCapsuleRow { a = a, b = b, radiusM = r, group = g }; }
    public static PairCapsuleRow[] Defaults()
    {
        return new[] {
            R("Hips", "Spine", 0.17f), R("Spine", "Spine1", 0.17f), R("Spine1", "Spine2", 0.17f), R("Spine2", "Neck", 0.12f), R("Neck", "Head", 0.10f),
            R("LeftUpLeg", "LeftLeg", 0.10f), R("LeftLeg", "LeftFoot", 0.07f), R("RightUpLeg", "RightLeg", 0.10f), R("RightLeg", "RightFoot", 0.07f),
            R("LeftArm", "LeftForeArm", 0.06f), R("LeftForeArm", "LeftHand", 0.05f), R("RightArm", "RightForeArm", 0.06f), R("RightForeArm", "RightHand", 0.05f) };
    }
}

public struct PairResult
{
    public bool any; public int frames; public float hipsMin, hipsCloseS, capMinSep, capOverlapS, capMinAt, hipsMinAt, t, softMinSep, softOverlapS, softMinAt; public string capWho, softWho, killer, victim;
    public string Line(FinisherPairCfg C)
    {
        if (!any) return "pair: no samples";
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        return "hips min " + hipsMin.ToString("0.00", ic) + " m at +" + hipsMinAt.ToString("0.00", ic) + " s (closer than " + C.hipsMinM.ToString("0.00", ic) + " m for " + hipsCloseS.ToString("0.00", ic) + " s, limit " + C.hipsFailS.ToString("0.00", ic)
            + " s) | body capsules min separation " + capMinSep.ToString("+0.00;-0.00", ic) + " m at +" + capMinAt.ToString("0.00", ic) + " s [" + capWho + "] (overlap deeper than " + C.capsulePenM.ToString("0.00", ic) + " m for " + capOverlapS.ToString("0.00", ic) + " s, limit " + C.capsuleFailS.ToString("0.00", ic) + " s), " + frames + " frames";
    }
}

public static class FinisherPairProbe
{
    public static PairResult Last;
    static FinisherPairCfg C; static DuelFighter A, V; static Transform hipsA, hipsV; static Transform[] aa, ab, va, vb; static float[] rad; static string[] nm, grp; static float t;

    static Transform Find(Transform root, string suffix)
    {
        if (root == null || string.IsNullOrEmpty(suffix)) return null;
        Transform best = null; int bd = int.MaxValue;
        foreach (var x in root.GetComponentsInChildren<Transform>(true))
        {
            if (!x.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            int d = 0; for (var p = x; p != null && p != root; p = p.parent) d++;
            if (d < bd) { bd = d; best = x; }
        }
        return best;
    }

    public static void Begin(DuelFighter killer, DuelFighter victim)
    {
        Last = default(PairResult); C = null; A = killer; V = victim; t = 0f;
        try
        {
            var fc = FinisherConfig.Get(); C = fc != null && fc.rt != null ? fc.rt.pair : null;
            if (C == null || !C.enabled || A == null || V == null) { C = null; return; }
            hipsA = Find(A.transform, C.hipsBone); hipsV = Find(V.transform, C.hipsBone);
            var rows = C.capsules ?? new PairCapsuleRow[0]; var la = new List<Transform>(); var lb = new List<Transform>(); var lva = new List<Transform>(); var lvb = new List<Transform>(); var lr = new List<float>(); var ln = new List<string>(); var lg = new List<string>();
            foreach (var r in rows)
            {
                if (r == null) continue;
                var a1 = Find(A.transform, r.a); var b1 = Find(A.transform, r.b); var a2 = Find(V.transform, r.a); var b2 = Find(V.transform, r.b);
                if (a1 == null || b1 == null || a2 == null || b2 == null) continue;
                la.Add(a1); lb.Add(b1); lva.Add(a2); lvb.Add(b2); lr.Add(r.radiusM); ln.Add(r.a + ">" + r.b); lg.Add(!string.IsNullOrEmpty(r.group) ? r.group : (r.a.Contains("Arm") || r.a.Contains("Hand")) ? "arm" : r.a.Contains("Leg") ? "leg" : "core");
            }
            aa = la.ToArray(); ab = lb.ToArray(); va = lva.ToArray(); vb = lvb.ToArray(); rad = lr.ToArray(); nm = ln.ToArray(); grp = lg.ToArray();
            Last.hipsMin = 99f; Last.capMinSep = 99f; Last.softMinSep = 99f; Last.killer = A.name; Last.victim = V.name;
        }
        catch (Exception ex) { Debug.LogWarning("[finisher] pair probe begin: " + ex.Message); C = null; }
    }

    // dt = scene seconds since the last call (scaled time during the scene, unscaled in the final hold where the scale is 1)
    public static void Sample(float dt)
    {
        if (C == null || A == null || V == null) return;
        try
        {
            t += dt; Last.any = true; Last.frames++; Last.t = t;
            if (hipsA != null && hipsV != null)
            {
                float d = Vector3.Distance(hipsA.position, hipsV.position);
                if (d < Last.hipsMin) { Last.hipsMin = d; Last.hipsMinAt = t; }
                if (d < C.hipsMinM) Last.hipsCloseS += dt;
            }
            float sa = Mathf.Max(0.3f, A.scale) * C.radiusK, sv = Mathf.Max(0.3f, V.scale) * C.radiusK, worst = 99f, soft = 99f; int wi = -1, si = -1;
            for (int i = 0; i < rad.Length; i++)
                for (int j = 0; j < rad.Length; j++)
                {
                    if (aa[i] == null || ab[i] == null || va[j] == null || vb[j] == null) continue;
                    float s = SegDist(aa[i].position, ab[i].position, va[j].position, vb[j].position) - rad[i] * sa - rad[j] * sv;
                    if (FailPair(grp[i], grp[j])) { if (s < worst) { worst = s; wi = i * 1000 + j; } }
                    else if (s < soft) { soft = s; si = i * 1000 + j; }
                }
            if (wi >= 0)
            {
                if (worst < Last.capMinSep) { Last.capMinSep = worst; Last.capMinAt = t; Last.capWho = "killer " + nm[wi / 1000] + " x victim " + nm[wi % 1000]; }
                if (worst < -C.capsulePenM) Last.capOverlapS += dt;
            }
            if (si >= 0)
            {
                if (soft < Last.softMinSep) { Last.softMinSep = soft; Last.softMinAt = t; Last.softWho = "killer " + nm[si / 1000] + " x victim " + nm[si % 1000]; }
                if (soft < -C.capsulePenM) Last.softOverlapS += dt;
            }
        }
        catch (Exception ex) { Debug.LogWarning("[finisher] pair probe: " + ex.Message); C = null; }
    }

    static bool FailPair(string a, string b)
    {
        var f = C != null ? C.failPairs : null; if (f == null) return false;
        foreach (var p in f)
        {
            if (string.IsNullOrEmpty(p)) continue; int k = p.IndexOf('-'); if (k < 0) continue; string x = p.Substring(0, k), y = p.Substring(k + 1);
            if ((a == x && b == y) || (a == y && b == x)) return true;
        }
        return false;
    }

    public static void Verify(string id, List<string> errors, List<string> warns, out string line)
    {
        line = "pair probe off";
        var cfg = FinisherConfig.Get() != null && FinisherConfig.Get().rt != null ? FinisherConfig.Get().rt.pair : null;
        if (cfg == null || !cfg.enabled) return;
        if (!Last.any) { line = "pair probe: no samples"; warns.Add("pair probe: no samples (hips / bones not found)"); return; }
        line = Last.Line(cfg);
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (Last.hipsCloseS > cfg.hipsFailS) errors.Add("interpenetration: the two Hips were closer than " + cfg.hipsMinM.ToString("0.00", ic) + " m for " + Last.hipsCloseS.ToString("0.00", ic) + " s (limit " + cfg.hipsFailS.ToString("0.00", ic) + " s), min " + Last.hipsMin.ToString("0.00", ic) + " m at +" + Last.hipsMinAt.ToString("0.00", ic) + " s: fix pairOffset / killerYawDeg / victimYawDeg");
        if (Last.softOverlapS > cfg.capsuleFailS) warns.Add("limb / hand overlap (not failing): " + Last.softMinSep.ToString("+0.00;-0.00", ic) + " m at +" + Last.softMinAt.ToString("0.00", ic) + " s [" + Last.softWho + "], deeper than " + cfg.capsulePenM.ToString("0.00", ic) + " m for " + Last.softOverlapS.ToString("0.00", ic) + " s (radii x " + cfg.radiusK.ToString("0.00", ic) + ")");
        if (Last.capOverlapS > cfg.capsuleFailS)
        {
            string m = "interpenetration: the torso / leg capsules (radii x " + cfg.radiusK.ToString("0.00", ic) + ") overlapped by more than " + cfg.capsulePenM.ToString("0.00", ic) + " m for " + Last.capOverlapS.ToString("0.00", ic) + " s (limit " + cfg.capsuleFailS.ToString("0.00", ic) + " s), worst " + Last.capMinSep.ToString("+0.00;-0.00", ic) + " m at +" + Last.capMinAt.ToString("0.00", ic) + " s [" + Last.capWho + "]";
            if (cfg.capsulesFail) errors.Add(m); else warns.Add(m);
        }
    }

    // closest distance between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision Detection)
    static float SegDist(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2; float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r), s, u;
        const float eps = 1e-8f;
        if (a <= eps && e <= eps) return r.magnitude;
        if (a <= eps) { s = 0f; u = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= eps) { u = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                s = den > eps ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                u = (b * s + f) / e;
                if (u < 0f) { u = 0f; s = Mathf.Clamp01(-c / a); } else if (u > 1f) { u = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        return ((p1 + d1 * s) - (p2 + d2 * u)).magnitude;
    }
}
