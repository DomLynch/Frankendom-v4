using System;
using UnityEngine;

// CAMREFRAME v102 (approved V3 lock-cam reframe for far separations). Data: camera_reframe.json (StreamingAssets, then Assets/Data). enabled=false = today's pose exactly.
//  Look()  CameraRig, lock block: when the fighters are more than midpointFromSepM apart the look-at blends (over blendSpanM) to their exact midpoint (the V3 look-at has a lookAhead nudge toward the foe).
//  Pos()   CameraRig, after CamFraming.Fit: pulls the camera in toward maxDistM from the look-at as far as BOTH fighters stay inside CamFraming's safe area (never gives up framing: the -camproof steadyMin stays 0.9),
//          then tilts the orbit by pitchDeg (|pitch| <= pitchLimitDeg). Weight = clamp01((sep - midpointFromSepM) / blendSpanM). Finisher / death cameras are separate components and are untouched.
public static class CamReframe
{
    [Serializable]
    public class ProofRow { public float farSepM = 6f; public float playerScreenHMin = 0.22f; public float fullSepM = 7f; public float playerScreenHMinFar = 0.15f; public float farSepFullM = 10f; public string rowName = "joan_screen_h"; }   // INT103: need = playerScreenHMin up to fullSepM, linear to playerScreenHMinFar at farSepFullM
    [Serializable]
    public class Row
    {
        public bool enabled = true;
        public float maxDistM = 6.5f;            // camera distance from the look-at the pull-in aims for
        public float midpointFromSepM = 6f;      // fighters further apart than this: look-at goes to the midpoint
        public float blendSpanM = 1f;            // metres of separation over which the effect fades in
        public float pitchDeg = -4f;             // orbit about the look-at: negative = camera lower (flatter), positive = higher
        public float pitchLimitDeg = 4f;         // hard limit on |pitch change|
        public int bisectIters = 18;
        public ProofRow proof = new ProofRow();
    }
    static Row cfg; static Vector3 pa, pb; static float sa = 1f, sb = 1f; static int stamp = -1;
    public static Row Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/camera_reframe.json", Application.dataPath + "/Data/camera_reframe.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Row>(DataPath.ReadFile(p)); if (r != null) { cfg = r; if (cfg.proof == null) cfg.proof = new ProofRow(); } } catch (Exception e) { Debug.LogWarning("[CamReframe] " + p + ": " + e.Message); }
            break;
        }
        return cfg;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; stamp = -1; }

    static bool Live(float lockW) { var r = Cfg(); var f = CamFraming.Cfg(); return r.enabled && lockW >= f.minLockW && CameraDeath.Weight <= f.maxDeathWeight; }
    static float Sep() { return Vector2.Distance(new Vector2(pa.x, pa.z), new Vector2(pb.x, pb.z)); }
    /// <summary>0 at midpointFromSepM, 1 at midpointFromSepM + blendSpanM.</summary>
    public static float Weight(float sep) { var r = Cfg(); return Mathf.Clamp01((sep - r.midpointFromSepM) / Mathf.Max(0.01f, r.blendSpanM)); }

    /// <summary>Look-at for this frame (called before the pose smoothing). Also remembers the fighters for Pos().</summary>
    public static Vector3 Look(Vector3 lockLook, Vector3 posA, float scaleA, Vector3 posB, float scaleB, float lockW)
    {
        pa = posA; pb = posB; sa = scaleA; sb = scaleB; stamp = Time.frameCount;
        if (!Live(lockW)) return lockLook;
        float w = Weight(Sep()); if (w <= 0f) return lockLook;
        Vector3 mid = (pa + pb) * 0.5f;
        return new Vector3(Mathf.Lerp(lockLook.x, mid.x, w), lockLook.y, Mathf.Lerp(lockLook.z, mid.z, w));
    }

    static bool Fits(Vector3 c, Vector3 look, float lockW, float aspect)
    {
        var f = CamFraming.Cfg(); Vector3 d = look - c; if (d.sqrMagnitude < 1e-6f) return false;
        float fov = Mathf.Lerp(f.fovFree, f.fovLock, lockW); /* DATALAYER b7 */   // same lens model as CamFraming.Fit
        float tv = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), th = tv * Mathf.Max(0.5f, aspect);
        float xlo = 2f * (f.safeMinX + f.marginX) - 1f, xhi = 2f * (f.safeMaxX - f.marginX) - 1f, ylo = 2f * (f.safeMinY + f.marginY) - 1f, yhi = 2f * (f.safeMaxY - f.marginY) - 1f;
        Quaternion inv = Quaternion.Inverse(Quaternion.LookRotation(d.normalized, Vector3.up));
        for (int k = 0; k < 6; k++)
        {
            Vector3 w = k < 3 ? pa : pb; float s = k < 3 ? sa : sb; float h = (k % 3 == 0 ? f.feetH : (k % 3 == 1 ? f.waistH : f.headH)) * s;
            Vector3 v = inv * (w + Vector3.up * h - c);
            if (v.z < f.behindZ) return false; /* DATALAYER b7 */
            float nx = v.x / (v.z * th), ny = v.y / (v.z * tv);
            if (nx > xhi || nx < xlo || ny > yhi || ny < ylo) return false;
        }
        return true;
    }

    /// <summary>Drawn camera position after CamFraming.Fit; unchanged when off / not lock-on / close fighters.</summary>
    public static Vector3 Pos(Vector3 pos, Vector3 look, float lockW, float aspect)
    {
        if (stamp != Time.frameCount || !Live(lockW)) return pos;
        var r = Cfg(); float w = Weight(Sep()); if (w <= 0f) return pos;
        Vector3 dir = look - pos; float cur = dir.magnitude; if (cur < 1e-3f) return pos;
        Vector3 dn = dir / cur; Vector3 rgt = Vector3.Cross(Vector3.up, dn);
        float ang = Mathf.Clamp(r.pitchDeg, -Mathf.Abs(r.pitchLimitDeg), Mathf.Abs(r.pitchLimitDeg)) * w;
        Quaternion tilt = (rgt.sqrMagnitude > 1e-6f && Mathf.Abs(ang) > 1e-4f) ? Quaternion.AngleAxis(ang, rgt.normalized) : Quaternion.identity;
        Vector3 back = -dn;
        float tt = cur;
        if (cur > r.maxDistM)
        {
            if (Fits(look + tilt * (back * r.maxDistM), look, lockW, aspect)) tt = r.maxDistM;
            else if (Fits(look + tilt * (back * cur), look, lockW, aspect))
            {
                float lo = r.maxDistM, hi = cur;   // hi fits, lo does not: smallest distance that still frames both
                for (int i = 0; i < r.bisectIters; i++) { float m = 0.5f * (lo + hi); if (Fits(look + tilt * (back * m), look, lockW, aspect)) hi = m; else lo = m; }
                tt = hi;
            }
        }
        tt = Mathf.Lerp(cur, tt, w);
        return look + tilt * (back * tt);
    }

    /// <summary>INT103: the joan_screen_h minimum for a proof separation (0.22 up to fullSepM, linear to 0.15 at farSepFullM).</summary>
    public static float ProofMin(float sep)
    {
        var p = Cfg().proof; if (sep <= p.fullSepM) return p.playerScreenHMin;
        float k = Mathf.Clamp01((sep - p.fullSepM) / Mathf.Max(0.01f, p.farSepFullM - p.fullSepM)); return Mathf.Lerp(p.playerScreenHMin, p.playerScreenHMinFar, k);
    }

    /// <summary>Viewport height of a fighter (feet to head per camera_framing.json), for the -camproof row.</summary>
    public static float ScreenH(Camera c, Transform t)
    {
        var fr = CamFraming.Cfg(); float s = FighterScale.Eff(t.lossyScale.y, t);
        return Mathf.Abs(c.WorldToViewportPoint(t.position + Vector3.up * fr.headH * s).y - c.WorldToViewportPoint(t.position + Vector3.up * fr.feetH * s).y);
    }
}
