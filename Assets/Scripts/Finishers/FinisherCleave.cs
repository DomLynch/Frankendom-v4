using System;
using System.Collections.Generic;
using UnityEngine;

// V63G: the crown split must READ. Two data-driven additions on top of MeshCut's split (finishers.json: cuts.units[].cleave / .burst -> gore.cleaves[] / gore.bursts[]):
//  1. CLEAVE WOUND: a dark red wound line along the cleave plane (the victim's sagittal plane, over the crown from the brow to the back of the skull) with a soft red halo, plus side patches and
//     drips running down both sides of the head (the profile camera looks at the SIDE of the head, where the plane itself is only a silhouette line). Decals are quads on the Furnace/Particle
//     shader, premultiplied alpha, DARK red, no emission (no glow). Parented to the head bone so they follow the death clip.
//  2. BLOOD BURST: several GoreFx.Spray strands (bigger count / size than the neck jet) from the crown, each with its own direction (up / away / toward / left / right).
// Used on success (MeshCut.Blood: the head's size comes from the two cut halves' bounds) and as the FALLBACK when the cut could not run (FinisherPlayer.Gore: the head's size comes from the head bone
// + the row's fallback radii). Nothing here is hard-coded art: every number is in gore.cleaves / gore.bursts.
[Serializable]
public class CleaveRow
{
    public string id = "crown";
    // head ellipsoid (metres, x victim scale). From the cut halves' bounds when the cut ran (clamped by the two ratios), else from the head bone + these
    public float fallbackCenterUpM = 0.12f; public float[] fallbackRadiiM = { 0.11f, 0.09f, 0.12f };   // forward, side, up
    public float sideMaxRatio = 0.9f, heightMaxRatio = 1.3f;     // horns widen the bounds: side radius <= forward x this, up radius <= forward x this
    // the wound line over the crown: arc in the sagittal plane, angle from the victim's forward (0 = brow level front, 90 = top of the skull, 180 = back)
    public float arcFromDeg = -20f, arcToDeg = 165f; public int segments = 9;
    public float widthM = 0.035f, haloMul = 3.0f, lengthPad = 1.25f, lift = 0.006f;
    public float[] coreColor = { 0.09f, 0.0f, 0.0f, 1f };       // near-black red wound
    public float[] haloColor = { 0.30f, 0.018f, 0.018f, 1f };   // dark blood around it
    // patches and drips on both sides of the head (profile cameras)
    public int patches = 2;                                      // wide soft patches per side
    public float[] patchSizeM = { 0.16f, 0.24f };
    public int drips = 6;                                        // per side
    public float[] dripLengthM = { 0.10f, 0.26f }, dripWidthM = { 0.025f, 0.05f };
    public float[] dripElevDeg = { 15f, 75f }, dripAzimuthDeg = { 25f, 105f };   // from the forward axis toward the side
    public int seed = 7;
}
[Serializable] public class BurstSpray { public string dir = "up"; public float tiltDeg = 0f; public SprayCfg spray = new SprayCfg(); }   // dir: up | away | toward | left | right
[Serializable] public class BurstRow { public string id = "crown"; public BurstSpray[] sprays = new BurstSpray[0]; }

public static class FinisherCleave
{
    static Texture2D lineTex; static Material coreMat, haloMat; static bool warned;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { lineTex = null; coreMat = haloMat = null; warned = false; }

    static CleaveRow FindCleave(GoreCfg g, string id) { if (g == null || g.cleaves == null || string.IsNullOrEmpty(id)) return null; foreach (var c in g.cleaves) if (c != null && c.id == id) return c; return null; }
    static BurstRow FindBurst(GoreCfg g, string id) { if (g == null || g.bursts == null || string.IsNullOrEmpty(id)) return null; foreach (var b in g.bursts) if (b != null && b.id == id) return b; return null; }
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    /// <summary>The cut could not run (FinisherPlayer.Gore, handled == false): wound + burst from the head bone alone.</summary>
    public static void Fallback(Transform victim, Vector3 away, string unitId)
    {
        try
        {
            var f = FinisherConfig.Get(); if (f == null || f.cuts == null || f.cuts.units == null || victim == null) return;
            CutUnit u = null; foreach (var x in f.cuts.units) if (x != null && x.id == unitId) u = x;
            if (u == null || (string.IsNullOrEmpty(u.cleave) && string.IsNullOrEmpty(u.burst))) return;
            Apply(victim, u, Vector3.zero, Vector3.up, away, null);
        }
        catch (Exception e) { Debug.LogWarning("[finisher] cleave fallback: " + e.Message); }
    }

    /// <summary>pos = where the cut opened (world; zero = the head centre), jetDir = the jet direction (up the neck), away = from the killer to the victim (zero = the victim's back), res = the cut result (null = fallback).</summary>
    public static void Apply(Transform victim, CutUnit u, Vector3 pos, Vector3 jetDir, Vector3 away, CutResult res)
    {
        try
        {
            var g = FinisherConfig.Get() != null ? FinisherConfig.Get().gore : null; if (g == null || u == null || victim == null) return;
            var cl = FindCleave(g, u.cleave); var bu = FindBurst(g, u.burst); if (cl == null && bu == null) return;
            Transform bone = HeadBone(victim, u); if (bone == null) { Debug.LogWarning("[finisher] cleave: no head bone on " + victim.name); return; }
            away = Flat(away); if (away.sqrMagnitude < 1e-4f) away = -Flat(victim.forward); if (away.sqrMagnitude < 1e-4f) away = Vector3.back; away.Normalize();
            Vector3 fwd = -away, right = Vector3.Cross(Vector3.up, fwd);
            float sc = Mathf.Max(0.5f, victim.lossyScale.y);
            Vector3 c; float rF, rS, rH; string how;
            if (cl != null) Ellipsoid(victim, bone, cl, res, fwd, right, sc, out c, out rF, out rS, out rH, out how); else { c = bone.position + Vector3.up * 0.12f * sc; rF = 0.11f * sc; rS = 0.09f * sc; rH = 0.12f * sc; how = "bone"; }
            int quads = 0, drips = 0, sprays = 0;
            if (cl != null) quads = Wound(bone, cl, c, fwd, right, rF, rS, rH, victim.name.GetHashCode(), out drips);
            if (bu != null) sprays = Burst(bone, bu, pos.sqrMagnitude < 1e-8f ? c : pos, jetDir, away, right);
            Debug.Log("[finisher] cleave '" + (cl != null ? cl.id : "-") + "' burst '" + (bu != null ? bu.id : "-") + "' on " + victim.name + "/" + bone.name + ": head ellipsoid from " + how + " centre " + c.ToString("0.00") + " radii F/S/U "
                + rF.ToString("0.000") + "/" + rS.ToString("0.000") + "/" + rH.ToString("0.000") + " m; wound quads " + quads + ", drips+patches " + drips + ", spray systems " + sprays);
        }
        catch (Exception e) { Debug.LogWarning("[finisher] cleave: " + e.Message + " " + e.StackTrace); }
    }

    // the deepest transform named like the unit's bone (a model can have a same-named container node near the root; the real bone sits deeper in the hierarchy)
    static Transform HeadBone(Transform victim, CutUnit u)
    {
        if (u.bones == null || u.bones.Length == 0) return null;
        Transform best = null; int bestDepth = -1; var all = victim.GetComponentsInChildren<Transform>(true);
        for (int n = 0; n < u.bones.Length && best == null; n++)
        {
            string k = GoreFx.Key(u.bones[n]);
            foreach (var t in all)
            {
                if (GoreFx.Key(t.name) != k) continue;
                int d = 0; for (var p = t; p != null && p != victim; p = p.parent) d++;
                if (d > bestDepth) { bestDepth = d; best = t; }
            }
        }
        return best;
    }

    static void Ellipsoid(Transform victim, Transform bone, CleaveRow cl, CutResult res, Vector3 fwd, Vector3 right, float sc, out Vector3 c, out float rF, out float rS, out float rH, out string how)
    {
        Bounds b = default(Bounds); bool have = false;
        if (res != null)
            foreach (var go in res.pieces) { if (go == null) continue; foreach (var r in go.GetComponentsInChildren<MeshRenderer>()) { if (!have) { b = r.bounds; have = true; } else b.Encapsulate(r.bounds); } }
        if (have && b.size.sqrMagnitude > 1e-6f)
        {
            Vector3 e = b.extents; c = b.center;
            rF = Mathf.Abs(fwd.x) * e.x + Mathf.Abs(fwd.z) * e.z; rS = Mathf.Abs(right.x) * e.x + Mathf.Abs(right.z) * e.z; rH = e.y;
            rF = Mathf.Clamp(rF, 0.05f * sc, 0.30f * sc); rS = Mathf.Min(rS, rF * cl.sideMaxRatio); rH = Mathf.Min(rH, rF * cl.heightMaxRatio);
            how = "cut bounds"; return;
        }
        c = bone.position + Vector3.up * cl.fallbackCenterUpM * sc;
        float[] r3 = cl.fallbackRadiiM != null && cl.fallbackRadiiM.Length >= 3 ? cl.fallbackRadiiM : new[] { 0.11f, 0.09f, 0.12f };
        rF = r3[0] * sc; rS = r3[1] * sc; rH = r3[2] * sc; how = "head bone";
    }

    // ---------------- the wound ----------------
    static int Wound(Transform bone, CleaveRow cl, Vector3 c, Vector3 fwd, Vector3 right, float rF, float rS, float rH, int seedHash, out int drips)
    {
        var halo = Mat(ref haloMat, cl.haloColor, "FinisherCleaveHalo"); var core = Mat(ref coreMat, cl.coreColor, "FinisherCleaveCore");
        drips = 0; if (halo == null || core == null) return 0;
        int n = Mathf.Max(2, cl.segments), made = 0;
        // 1. the line over the crown (sagittal plane)
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.Lerp(cl.arcFromDeg, cl.arcToDeg, i / (float)n) * Mathf.Deg2Rad, a1 = Mathf.Lerp(cl.arcFromDeg, cl.arcToDeg, (i + 1) / (float)n) * Mathf.Deg2Rad, am = (a0 + a1) * 0.5f;
            Vector3 p0 = c + fwd * rF * Mathf.Cos(a0) + Vector3.up * rH * Mathf.Sin(a0), p1 = c + fwd * rF * Mathf.Cos(a1) + Vector3.up * rH * Mathf.Sin(a1);
            Vector3 pm = c + fwd * rF * Mathf.Cos(am) + Vector3.up * rH * Mathf.Sin(am);
            Vector3 nrm = (fwd * Mathf.Cos(am) / Mathf.Max(1e-3f, rF) + Vector3.up * Mathf.Sin(am) / Mathf.Max(1e-3f, rH)).normalized;
            Vector3 along = p1 - p0; float mag = along.magnitude; if (mag < 1e-5f) continue; float len = mag * cl.lengthPad; along /= mag;
            Place(bone, halo, pm + nrm * cl.lift, nrm, along, cl.widthM * cl.haloMul, len);
            Place(bone, core, pm + nrm * (cl.lift + 0.002f), nrm, along, cl.widthM, len);
            made += 2;
        }
        // 2. patches and drips on both sides
        var rng = new System.Random(cl.seed * 7919 + seedHash);
        Func<float, float, float> R = (lo, hi) => lo + (hi - lo) * (float)rng.NextDouble();
        for (int side = -1; side <= 1; side += 2)
        {
            for (int k = 0; k < cl.patches; k++)
            {
                float az = R(cl.dripAzimuthDeg[0], cl.dripAzimuthDeg[1]) * Mathf.Deg2Rad, el = R(30f, 80f) * Mathf.Deg2Rad;
                Vector3 p, nrm; Surf(c, fwd, right * side, rF, rS, rH, az, el, out p, out nrm);
                float s = R(cl.patchSizeM[0], cl.patchSizeM[1]);
                Vector3 down = Vector3.ProjectOnPlane(Vector3.down, nrm);
                Place(bone, halo, p + nrm * cl.lift, nrm, Quaternion.AngleAxis(R(-35f, 35f), nrm) * (down.sqrMagnitude > 1e-6f ? down : Vector3.forward), s, s * R(1.0f, 1.5f)); drips++; made++;
            }
            for (int k = 0; k < cl.drips; k++)
            {
                float az = R(cl.dripAzimuthDeg[0], cl.dripAzimuthDeg[1]) * Mathf.Deg2Rad, el = R(cl.dripElevDeg[0], cl.dripElevDeg[1]) * Mathf.Deg2Rad;
                Vector3 p, nrm; Surf(c, fwd, right * side, rF, rS, rH, az, el, out p, out nrm);
                float len = R(cl.dripLengthM[0], cl.dripLengthM[1]), w = R(cl.dripWidthM[0], cl.dripWidthM[1]);
                Vector3 down = Vector3.ProjectOnPlane(Vector3.down, nrm); if (down.sqrMagnitude < 1e-6f) down = Vector3.forward;
                Place(bone, core, p + nrm * (cl.lift + 0.001f) + down.normalized * (len * 0.4f), nrm, down, w, len); drips++; made++;
            }
        }
        return made;
    }

    // point + outward normal on the head ellipsoid: azimuth from the forward axis toward `side`, elevation above the horizontal
    static void Surf(Vector3 c, Vector3 fwd, Vector3 side, float rF, float rS, float rH, float az, float el, out Vector3 p, out Vector3 nrm)
    {
        float ce = Mathf.Cos(el), se = Mathf.Sin(el), ca = Mathf.Cos(az), sa = Mathf.Sin(az);
        p = c + fwd * rF * ce * ca + side * rS * ce * sa + Vector3.up * rH * se;
        nrm = (fwd * ce * ca / Mathf.Max(1e-3f, rF) + side * ce * sa / Mathf.Max(1e-3f, rS) + Vector3.up * se / Mathf.Max(1e-3f, rH)).normalized;
    }

    // a quad whose local y runs along `along`, facing out along `nrm`, parented to the bone (keeps its world pose)
    static void Place(Transform bone, Material m, Vector3 pos, Vector3 nrm, Vector3 along, float width, float length)
    {
        Vector3 up = Vector3.ProjectOnPlane(along, nrm); if (up.sqrMagnitude < 1e-8f) up = Vector3.up; up.Normalize();
        var go = new GameObject("FinisherCleave");
        go.AddComponent<MeshFilter>().sharedMesh = GoreFx.Quad();
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = m; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
        go.transform.position = pos; go.transform.rotation = Quaternion.LookRotation(nrm, up); go.transform.localScale = new Vector3(Mathf.Max(0.005f, width), Mathf.Max(0.005f, length), 1f);
        go.transform.SetParent(bone, true);
    }

    // ---------------- the burst ----------------
    static int Burst(Transform bone, BurstRow row, Vector3 pos, Vector3 jetDir, Vector3 away, Vector3 right)
    {
        int n = 0; if (row.sprays == null) return 0;
        foreach (var s in row.sprays)
        {
            if (s == null || s.spray == null) continue;
            Vector3 d;
            switch (s.dir)
            {
                case "away": d = away + Vector3.up * 0.35f; break;
                case "toward": d = -away + Vector3.up * 0.35f; break;
                case "left": d = -right + Vector3.up * 0.4f; break;
                case "right": d = right + Vector3.up * 0.4f; break;
                default: d = jetDir.sqrMagnitude > 1e-6f ? jetDir : Vector3.up; break;
            }
            if (Mathf.Abs(s.tiltDeg) > 0.01f) d = Quaternion.AngleAxis(s.tiltDeg, right) * d;
            if (GoreFx.Spray(bone, pos, d, s.spray, s.spray.seconds) != null) n++;
        }
        return n;
    }

    // ---------------- art: a pointed, slightly ragged streak (alpha only; the colour is the material's tint) ----------------
    static float H(int i) { float s = Mathf.Sin(i * 127.1f) * 43758.5453f; return s - Mathf.Floor(s); }
    static float N(float x) { int i = Mathf.FloorToInt(x); float f = x - i; f = f * f * (3f - 2f * f); return Mathf.Lerp(H(i), H(i + 1), f); }
    static Texture2D Line()
    {
        if (lineTex != null) return lineTex;
        const int W = 32, Hh = 128; var px = new Color32[W * Hh];
        for (int y = 0; y < Hh; y++)
        {
            float v = (y + 0.5f) / Hh, taper = Mathf.Pow(Mathf.Sin(Mathf.PI * v), 0.4f), w = Mathf.Clamp01(taper * (0.72f + 0.28f * N(y * 0.23f)));
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W * 2f - 1f, d = Mathf.Abs(u + (N(y * 0.11f + 5f) - 0.5f) * 0.25f) / Mathf.Max(0.05f, w);
                float a = Mathf.Clamp01((1f - d) * 2.6f); a = a * a * (3f - 2f * a);
                byte b = (byte)Mathf.RoundToInt(a * 255f); px[y * W + x] = new Color32(b, b, b, b);
            }
        }
        lineTex = new Texture2D(W, Hh, TextureFormat.RGBA32, false) { name = "FinisherCleaveLine", wrapMode = TextureWrapMode.Clamp };
        lineTex.SetPixels32(px); lineTex.Apply(false, true);
        return lineTex;
    }
    // PREWARM v92: the halo + core cleave materials (and the generated line texture) exist before the first head-split; Prewarm92 draws them sub-pixel. Same Mat() calls as Wound(), so the cached statics are the real ones.
    internal static void CollectPrewarm(List<Material> into)
    {
        var g = FinisherConfig.Get().gore; if (g == null || g.cleaves == null) return;
        foreach (var cl in g.cleaves)
        {
            if (cl == null) continue;
            var h = Mat(ref haloMat, cl.haloColor, "FinisherCleaveHalo"); var c = Mat(ref coreMat, cl.coreColor, "FinisherCleaveCore");
            if (h != null && !into.Contains(h)) into.Add(h); if (c != null && !into.Contains(c)) into.Add(c);
        }
    }
    // PREWARM v92 end
    static Material Mat(ref Material m, float[] col, string name)
    {
        if (m != null) return m;
        var sh = FxMaterials.FindParticleShader();
        if (sh == null) { if (!warned) { warned = true; Debug.LogWarning("FinisherCleave: Furnace/Particle shader not found; the cleave wound is disabled."); } return null; }
        m = new Material(sh) { name = name };
        m.SetTexture("_MainTex", Line());
        m.SetColor("_Tint", new Color(col[0], col[1], col[2], col.Length > 3 ? col[3] : 1f));   // dark red, no emission
        m.SetFloat("_Intensity", 1f); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", 0f);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 10f);   // premultiplied alpha, never additive
        m.renderQueue = 3000;
        return m;
    }
}
