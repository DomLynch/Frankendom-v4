using System;
using UnityEngine;

// OCCLUDE v105: fighter-over-fighter occlusion, measured (-camproof row) and, dark, fixed (camera data). Data: camera_occlusion.json (StreamingAssets, then Assets/Data). enabled=false = no row.
//  Hidden()/Measure()  CamProof only: samples the fighter's body (feet..head x a few lateral offsets), casts the camera->sample segment against the OTHER fighter's vertical capsule (analytic, no physics layers, no GPU)
//                      and returns the fraction of samples that segment crosses = how much of that fighter the other one hides. 0 = fully visible. CameraRig.VisibleFraction only tests the viewport, never occlusion.
//  Shift()             CameraRig, lock block: when fix.enabled, below fix.nearSepM the lock camera slides sideways (and optionally up) so the player's body stops covering the foe. fix.enabled=false = today's pose exactly.
public static class CamOcclusion
{
    [Serializable]
    public class ProofRow
    {
        public float[] seps = { 2f, 4f, 6f, 7f, 10f };   // separations (m) of the extra -camproof row, arena middle
        public int settleFrames = 60;                    // frames to settle at each separation (own count: camproof's 150 s budget has little headroom; the camera eases at ~8/s so 60 is ~7 time constants)
        public int sampleFrames = 6;                     // frames sampled after settling; the worst (largest) hidden fraction is reported
        public bool enforce = false;                     // false = log only (row + csv), never fails; true = Fail() when a fighter is hidden more than hiddenMax
        public float hiddenMax = 0.5f;                   // used only when enforce
        public string csvName = "camproof_occlusion.csv";
    }
    [Serializable]
    public class FixRow
    {
        public bool enabled = false;       // DARK: the camera is untouched until Dom turns this on after a LOOK OK
        public float nearSepM = 4f;        // full effect at 0 separation, none from nearSepM up
        public float blendSpanM = 2f;      // separation range (m) over which the effect fades in, ending at nearSepM
        public float shoulderAddM = 1f;    // extra sideways offset (m, positive = to the camera's right, same side as camera_pose.json shoulder)
        public float heightAddM = 0f;      // extra camera height (m)
    }
    [Serializable]
    public class Row
    {
        public bool enabled = true;
        public float[] heights = { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f, 1f };   // sample heights, fraction of feetH..headH (camera_framing.json) x fighter scale
        public float[] lateral = { -1f, 0f, 1f };                         // sample offsets across the body, in body radii (camera-right axis)
        public bool useController = true;                                // body radius from the fighter's CharacterController (x scale)
        public float radiusOfHeight = 0.2f;                              // fallback radius = this x body height, also the floor
        public float problemHidden = 0.25f;                              // tools verdict: a fighter hidden more than this at a separation is a real problem
        public ProofRow proof = new ProofRow();
        public FixRow fix = new FixRow();
    }
    static Row cfg;
    public static Row Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/camera_occlusion.json", Application.dataPath + "/Data/camera_occlusion.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try
            {
                var r = JsonUtility.FromJson<Row>(System.IO.File.ReadAllText(p));
                if (r != null)
                {
                    cfg = r; if (cfg.proof == null) cfg.proof = new ProofRow(); if (cfg.fix == null) cfg.fix = new FixRow();
                    if (cfg.heights == null || cfg.heights.Length == 0) cfg.heights = new[] { 0.5f };
                    if (cfg.lateral == null || cfg.lateral.Length == 0) cfg.lateral = new[] { 0f };
                    if (cfg.proof.seps == null) cfg.proof.seps = new float[0];
                }
            }
            catch (Exception e) { Debug.LogWarning("[CamOcclusion] " + p + ": " + e.Message); }
            break;
        }
        return cfg;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; }

    // ---- the fix (camera) -------------------------------------------------------------------------------------------------
    /// <summary>Lock-camera position with the near-range slide applied. right = the camera-right axis (yaw), sep = flat fighter gap (m).</summary>
    public static Vector3 Shift(Vector3 lockPos, Vector3 right, float sep, float lockW)
    {
        var r = Cfg(); if (!r.enabled || !r.fix.enabled) return lockPos;
        var f = CamFraming.Cfg(); if (lockW < f.minLockW || CameraDeath.Weight > f.maxDeathWeight) return lockPos;
        float w = Mathf.Clamp01((r.fix.nearSepM - sep) / Mathf.Max(0.01f, r.fix.blendSpanM)); if (w <= 0f) return lockPos;
        return lockPos + right * (r.fix.shoulderAddM * w) + Vector3.up * (r.fix.heightAddM * w);
    }

    // ---- the measurement (camproof) ----------------------------------------------------------------------------------------
    static float SegSegDist(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)   // closest distance between two segments (Ericson, Real-Time Collision Detection 5.1.9)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2; float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r), s, t;
        if (a <= 1e-9f && e <= 1e-9f) return r.magnitude;
        if (a <= 1e-9f) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-9f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                s = den > 1e-9f ? Mathf.Clamp01((b * f - c * e) / den) : 0f; t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); } else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        return ((p1 + d1 * s) - (p2 + d2 * t)).magnitude;
    }

    static float Radius(Transform t, float height)
    {
        var r = Cfg(); float fall = Mathf.Max(0.01f, r.radiusOfHeight) * height;
        if (!r.useController) return fall;
        var cc = t.GetComponent<CharacterController>(); if (!cc) return fall;
        var ls = t.lossyScale; return Mathf.Max(fall, cc.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z)));
    }

    /// <summary>Fraction (0..1) of fighter `a` hidden by fighter `b` seen from `camPos`; radiusB = the capsule radius used for b.</summary>
    public static float Hidden(Vector3 camPos, Transform a, Transform b, out float radiusB)
    {
        var r = Cfg(); var fr = CamFraming.Cfg();
        float sa = FighterScale.Eff(a.lossyScale.y, a), sb = FighterScale.Eff(b.lossyScale.y, b);
        float aFeet = fr.feetH * sa, aHead = fr.headH * sa, bFeet = fr.feetH * sb, bHead = fr.headH * sb;
        float ra = Radius(a, aHead - aFeet); radiusB = Radius(b, bHead - bFeet);
        Vector3 toA = a.position - camPos; toA.y = 0f; if (toA.sqrMagnitude < 1e-6f) return 0f;
        Vector3 right = Vector3.Cross(Vector3.up, toA.normalized);
        Vector3 b0 = b.position + Vector3.up * bFeet, b1 = b.position + Vector3.up * bHead; int n = 0, hid = 0;
        foreach (float hk in r.heights)
            foreach (float lt in r.lateral)
            {
                Vector3 pt = a.position + Vector3.up * Mathf.Lerp(aFeet, aHead, hk) + right * (lt * ra); n++;
                if (SegSegDist(camPos, pt, b0, b1) < radiusB) hid++;
            }
        return n > 0 ? (float)hid / n : 0f;
    }

    /// <summary>Both directions: playerHidden = how much of `player` the foe hides, foeHidden = how much of `foe` the player hides; radii = the capsule radii used.</summary>
    public static void Measure(Vector3 camPos, Transform player, Transform foe, out float playerHidden, out float foeHidden, out float playerRadius, out float foeRadius)
    {
        playerHidden = Hidden(camPos, player, foe, out foeRadius); foeHidden = Hidden(camPos, foe, player, out playerRadius);
    }
}
