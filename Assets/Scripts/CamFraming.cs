using System;
using UnityEngine;

// V65CAM: keep BOTH fighters inside the safe area of the lock-on camera. Data: camera_framing.json (StreamingAssets, then Assets/Data). enabled=false = the V3 pose exactly as before.
//  Fit()       called by CameraRig after the smoothed pose: moves the camera straight back along its view ray (so rotation and look-at do not change) by exactly the distance that puts the feet / waist / head
//              points of both fighters inside the safe viewport (closed form: ndc' = ndc * z / (z + add)), at most maxExtraDist. Applied to the DRAWN pose only, so the smoothed state is not fed back.
//              It runs before the camera collision spherecast; whatever collision pulls back in is caught by WidenFov.
//  WidenFov()  called by CameraRig after the final pose: if a point is still outside (collision, floor, clamp), the vertical FOV opens up to fovMax; it closes again at fovReleaseDegPerS.
// Both are no-ops outside lock-on (lockW < minLockW) and while the death camera owns the pose (CameraDeath.Weight > maxDeathWeight).
public static class CamFraming
{
    [Serializable]
    public class ProofRow
    {
        public float steadyMin = 0.9f;        // VisibleFraction of BOTH fighters, steady state (-camproof)
        public float playerScreenHMin = 0.22f, reframeSteadyMin = 0.5f;   // GOOSEBACKLOG2 v102: player viewport height gate and relaxed steadyMin, both used ONLY while look_options.json camReframe.on
        public float transientMin = 0.66f;    // while a fighter is being knocked across the arena
        public float lumaMin = 0.02f, lumaMax = 0.95f;   // black / white frame guard (mean luma of the picture's middle band)
        public float[] seps = { 1.0f, 2.0f, 4.0f, 7.0f, 10.0f };
        public int settleFrames = 90;
        public float knockFromM = 2f, knockToM = 9f; public int knockFrames = 24;
    }
    [Serializable]
    public class Row
    {
        public bool enabled = true;
        public float safeMinX = 0.05f, safeMaxX = 0.95f, safeMinY = 0.03f, safeMaxY = 0.97f;   // viewport rectangle (the same one CameraRig.VisibleFraction tests)
        public float marginX = 0.03f, marginY = 0.02f;   // extra inside margin (viewport fraction) so the pose has headroom
        public float feetH = 0.1f, waistH = 0.9f, headH = 1.7f;   // metres x max(1, fighter scale)
        public float maxExtraDist = 14f;
        public float fovFree = 50f, fovLock = 38.6f; /* DATALAYER b7 */ // lens model: vertical FOV with no lock (lockW 0) and at full lock (lockW 1); CamReframe.Fit reads the same two numbers
        public float behindZ = 0.2f, behindRatio = 4f, behindPad = 0.5f; /* DATALAYER b7 */ // AddFor: a point nearer than behindZ (or behind the lens) pushes the camera back by behindZ - z + behindPad and counts as ratio behindRatio
        public float minLockW = 0.5f, maxDeathWeight = 0.01f;
        public bool fovWiden = true; public float fovMax = 60f, fovReleaseDegPerS = 6f;
        public ProofRow proof = new ProofRow();
    }
    static Row cfg; static bool has; static Vector3 pa, pb; static float sa = 1f, sb = 1f, widen;
    public static Row Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/camera_framing.json", Application.dataPath + "/Data/camera_framing.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Row>(System.IO.File.ReadAllText(p)); if (r != null) { cfg = r; if (cfg.proof == null) cfg.proof = new ProofRow(); } } catch (Exception e) { Debug.LogWarning("[CamFraming] " + p + ": " + e.Message); }
            break;
        }
        return cfg;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; has = false; widen = 0f; }

    public static float Widen { get { return widen; } }
    public static void Clear() { has = false; }
    public static void Begin(Vector3 posA, float scaleA, Vector3 posB, float scaleB) { has = true; pa = posA; pb = posB; sa = scaleA; sb = scaleB; /*HCKIT1 v93 K2: the caller passes FighterScale.Eff (per-character floor) */ }

    static bool Live(float lockW) { var r = Cfg(); return r.enabled && has && lockW >= r.minLockW && CameraDeath.Weight <= r.maxDeathWeight; }

    // ndc bounds of the safe area (with margin), lo <= n <= hi
    static void Bounds(Row r, out float xlo, out float xhi, out float ylo, out float yhi)
    {
        xlo = 2f * (r.safeMinX + r.marginX) - 1f; xhi = 2f * (r.safeMaxX - r.marginX) - 1f;
        ylo = 2f * (r.safeMinY + r.marginY) - 1f; yhi = 2f * (r.safeMaxY - r.marginY) - 1f;
    }

    // distance the camera must still move back so that point v (camera space) is inside; ratio = how much the FOV tangent would have to grow instead
    static float AddFor(Vector3 v, float th, float tv, float xlo, float xhi, float ylo, float yhi, out float ratio)
    {
        ratio = 1f;
        { var bc = Cfg(); if (v.z < bc.behindZ) { ratio = bc.behindRatio; return bc.behindZ - v.z + bc.behindPad; } } /* DATALAYER b7 */
        float nx = v.x / (v.z * th), ny = v.y / (v.z * tv), add = 0f;
        if (nx > xhi) { float k = nx / xhi; ratio = Mathf.Max(ratio, k); add = Mathf.Max(add, v.z * (k - 1f)); }
        else if (nx < xlo) { float k = nx / xlo; ratio = Mathf.Max(ratio, k); add = Mathf.Max(add, v.z * (k - 1f)); }
        if (ny > yhi) { float k = ny / yhi; ratio = Mathf.Max(ratio, k); add = Mathf.Max(add, v.z * (k - 1f)); }
        else if (ny < ylo) { float k = ny / ylo; ratio = Mathf.Max(ratio, k); add = Mathf.Max(add, v.z * (k - 1f)); }
        return add;
    }

    /// <summary>Camera position pushed back along its own view ray until both fighters fit; unchanged when not live.</summary>
    public static Vector3 Fit(Vector3 pos, Vector3 look, float lockW, float aspect)
    {
        if (!Live(lockW)) return pos;
        var r = Cfg(); Vector3 dir = look - pos; if (dir.sqrMagnitude < 1e-6f) return pos;
        Vector3 dn = dir.normalized; float fov = Mathf.Lerp(r.fovFree, r.fovLock, lockW); /* DATALAYER b7 */
        float tv = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), th = tv * Mathf.Max(0.5f, aspect);
        float xlo, xhi, ylo, yhi; Bounds(r, out xlo, out xhi, out ylo, out yhi);
        Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(dn, Vector3.up));
        float total = 0f;
        for (int it = 0; it < 3; it++)
        {
            Vector3 p = pos - dn * total; float add = 0f, rt;
            for (int k = 0; k < 6; k++)
            {
                Vector3 w = (k < 3 ? pa : pb); float s = k < 3 ? sa : sb; float h = (k % 3 == 0 ? r.feetH : (k % 3 == 1 ? r.waistH : r.headH)) * s;
                add = Mathf.Max(add, AddFor(inv * (w + Vector3.up * h - p), th, tv, xlo, xhi, ylo, yhi, out rt));
            }
            if (add < 0.005f) break;
            total = Mathf.Min(r.maxExtraDist, total + add);
        }
        float cap = LookOptions.CamCapM();   // LOOKOPT v95: combatCamCap switch (look_options.json); 0 = off = today's pull-back
        var rf = LookOptions.Reframe(); float rw = 0f;   // GOOSEBACKLOG2 v102: camReframe switch (Goose 08:30): cap the distance, raise the camera a few degrees instead of pulling back
        if (rf.on) { if (rf.maxDistM > 0f && (cap <= 0f || rf.maxDistM < cap)) cap = rf.maxDistM; rw = Mathf.Clamp01((Vector2.Distance(new Vector2(pa.x, pa.z), new Vector2(pb.x, pb.z)) - rf.startSepM) / Mathf.Max(0.01f, rf.spanSepM)); }
        if (cap > 0f) total = Mathf.Min(total, Mathf.Max(0f, cap - dir.magnitude));   // camera at most cap m from the look-at, never closer than the base pose
        Vector3 res = pos - dn * total;
        if (rw > 0f && rf.maxPitchDeg > 0f) { Vector3 rgt = Vector3.Cross(Vector3.up, dn); if (rgt.sqrMagnitude > 1e-6f) res = look + Quaternion.AngleAxis(rw * rf.maxPitchDeg, rgt.normalized) * (res - look); }   // GOOSEBACKLOG2 v102: orbit up about the look-at
        return res;
    }

    /// <summary>Vertical FOV for this frame: the base FOV, opened up (smoothly released) when a fighter is still out of the safe area at the final pose.</summary>
    public static float WidenFov(Transform camT, float baseFov, float lockW, float aspect)
    {
        var r = Cfg(); float dt = Time.unscaledDeltaTime;
        if (!Live(lockW) || !r.fovWiden) { widen = Mathf.MoveTowards(widen, 0f, r.fovReleaseDegPerS * dt); return baseFov + widen; }
        float tv = Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad), th = tv * Mathf.Max(0.5f, aspect);
        float xlo, xhi, ylo, yhi; Bounds(r, out xlo, out xhi, out ylo, out yhi);
        float ratio = 1f, rt;
        for (int k = 0; k < 6; k++)
        {
            Vector3 w = (k < 3 ? pa : pb); float s = k < 3 ? sa : sb; float h = (k % 3 == 0 ? r.feetH : (k % 3 == 1 ? r.waistH : r.headH)) * s;
            AddFor(camT.InverseTransformPoint(w + Vector3.up * h), th, tv, xlo, xhi, ylo, yhi, out rt); ratio = Mathf.Max(ratio, rt);
        }
        float need = ratio > 1.001f ? Mathf.Min(r.fovMax, 2f * Mathf.Atan(tv * ratio) * Mathf.Rad2Deg) - baseFov : 0f;
        need = Mathf.Max(0f, need);
        widen = need > widen ? need : Mathf.MoveTowards(widen, need, r.fovReleaseDegPerS * dt);
        var rfo = LookOptions.Reframe(); float fmax = rfo.on && rfo.fovMaxDeg > 0f ? Mathf.Min(r.fovMax, rfo.fovMaxDeg) : r.fovMax;   // GOOSEBACKLOG2 v102: no FOV widening beyond fovMaxDeg while camReframe is on
        return Mathf.Min(fmax, baseFov + widen);
    }
}
