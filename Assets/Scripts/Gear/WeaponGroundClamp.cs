using System.Collections.Generic;
using UnityEngine;

// AXEGRIP v1 (lane FK axegrip, Dom 19 Nov: 'the axe dips beneath the ground, the ground is solid'): ground clamp for a held two-handed weapon (war axe, maul).
// Nothing stopped the weapon mesh from passing through the floor (no weapon collider: WeaponSwap destroys them; PhysWeapon is presentation-only and dark). This runs from TwoHandIK.LateUpdate
// (order 60: after the Animator, WeaponGrip -50 and the grip rows, BEFORE the left-hand solve reads the weapon's off-hand node, so the left hand follows the corrected weapon).
// Every frame: the weapon mesh's lowest world points (a vertex sample in the weapon's local space, plus the tip and butt nodes) are tested against the ground under them (ray down, PhysMasks.Ground,
// fighters / triggers skipped). A point below ground + epsilon makes the weapon TILT about the right palm (the socket origin) up to maxTiltDeg, then LIFT by at most maxLiftM; both are smoothed
// (attackS in, releaseS out) so there is no pop. The right hand keeps its grip (the pivot is the palm); only the head swings up.
// DATA: weapon_ground_clamp.json (StreamingAssets, enabled true). No serialized fields on any MonoBehaviour, no RuntimeInitializeOnLoadMethod: static class called from an existing component.
public static class WeaponGroundClamp
{
    [System.Serializable]
    public class Cfg
    {
        public bool enabled = false;   // AXEGRIP v1: default OFF (absent json = off); the json turns it on after the dip pair passes
        public float epsilonM = 0.02f;        // keep the lowest point this far above the ground
        public float probeUpM = 1.6f;         // ray starts this far above the fighter's root height (never from underground)
        public float probeLenM = 4f;
        public float maxTiltDeg = 55f;        // tilt about the palm, then lift
        public float maxLiftM = 0.2f;
        public float attackS = 0.03f, releaseS = 0.12f;
        public float quickRejectM = 1.3f;     // lowest point more than this above the fighter's root: no probe at all
        public int maxSamples = 2500;         // mesh vertices kept (stride sampled)
        public bool logCorrection = true;     // one line per 0.5 s while a correction is active
        public bool logClearance = false;     // proof only: one line per 0.5 s with the clearance of the lowest point even when nothing is corrected
        public string[] weapons = new string[0];   // weapon ids this applies to; empty = every two-handed weapon
    }

    sealed class St
    {
        public Vector3[] pts; public Vector3 tipL, buttL; public bool haveTipButt; public bool boundsOnly;
        public Quaternion baseRot = Quaternion.identity; public Vector3 basePos, lastPos; public bool haveBase;
        public float tilt, lift; public Vector3 axis = Vector3.right; public float nextLog;
    }

    static Cfg cfg; static readonly Dictionary<Transform, St> st = new Dictionary<Transform, St>(); static readonly RaycastHit[] hits = new RaycastHit[12];
    static int nCorrections;

    // no RuntimeInitializeOnLoadMethod here (compile-only keeps the old table): cfg is lazily loaded; a stale entry in `st` is keyed by a destroyed Transform and pruned in Apply
    static Cfg Get()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        string json = DataPath.ReadText("weapon_ground_clamp.json");
        if (!string.IsNullOrEmpty(json)) { try { var c = JsonUtility.FromJson<Cfg>(json.TrimStart('﻿')); if (c != null) { if (c.weapons == null) c.weapons = new string[0]; cfg = c; } } catch (System.Exception e) { Debug.LogWarning("[WeaponGround] weapon_ground_clamp.json parse failed, defaults: " + e.Message); } }
        Debug.Log("[WeaponGround] cfg enabled " + cfg.enabled + " eps " + cfg.epsilonM + " maxTilt " + cfg.maxTiltDeg + " maxLift " + cfg.maxLiftM);
        return cfg;
    }

    static Transform FindNode(Transform top, string n) { foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t; return null; }

    static St Build(Transform inst)
    {
        var s = new St(); var pts = new List<Vector3>(); var C = Get();
        var mfs = inst.GetComponentsInChildren<MeshFilter>(true); int total = 0;
        foreach (var mf in mfs) { if (mf.sharedMesh != null && mf.sharedMesh.isReadable) total += mf.sharedMesh.vertexCount; }
        int stride = Mathf.Max(1, total / Mathf.Max(100, C.maxSamples));
        foreach (var mf in mfs)
        {
            var me = mf.sharedMesh; if (me == null) continue;
            Matrix4x4 rel = inst.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            if (me.isReadable)
            {
                var vs = me.vertices;
                for (int i = 0; i < vs.Length; i += stride) pts.Add(rel.MultiplyPoint3x4(vs[i]));
                // always keep the extremes along the six axes (the stride can drop them)
                int[] ext = new int[6]; float[] best = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < vs.Length; i++) { var v = vs[i]; if (v.x < best[0]) { best[0] = v.x; ext[0] = i; } if (v.y < best[1]) { best[1] = v.y; ext[1] = i; } if (v.z < best[2]) { best[2] = v.z; ext[2] = i; } if (v.x > best[3]) { best[3] = v.x; ext[3] = i; } if (v.y > best[4]) { best[4] = v.y; ext[4] = i; } if (v.z > best[5]) { best[5] = v.z; ext[5] = i; } }
                for (int k = 0; k < 6; k++) pts.Add(rel.MultiplyPoint3x4(vs[ext[k]]));
            }
            else
            {
                s.boundsOnly = true; var b = me.bounds;
                for (int xi = 0; xi < 2; xi++) for (int yi = 0; yi < 2; yi++) for (int zi = 0; zi < 2; zi++) pts.Add(rel.MultiplyPoint3x4(new Vector3(xi == 0 ? b.min.x : b.max.x, yi == 0 ? b.min.y : b.max.y, zi == 0 ? b.min.z : b.max.z)));
            }
        }
        var tip = FindNode(inst, "tip"); var butt = FindNode(inst, "butt");
        if (tip != null && butt != null) { s.tipL = inst.InverseTransformPoint(tip.position); s.buttL = inst.InverseTransformPoint(butt.position); s.haveTipButt = true; pts.Add(s.tipL); pts.Add(s.buttL); }
        s.pts = pts.ToArray();
        Debug.Log("[WeaponGround] sampled " + inst.name + ": " + s.pts.Length + " points" + (s.boundsOnly ? " (mesh not readable: bounds corners only, conservative)" : "") + (s.haveTipButt ? ", tip+butt nodes" : ", no tip/butt node"));
        return s;
    }

    // y of the highest ground under (x, z), below fromY + probeUp; NaN when no ground is hit. Fighters, triggers and the weapon's own colliders are skipped.
    static float GroundY(Vector3 p, float fromY, Cfg C)
    {
        int n = Physics.RaycastNonAlloc(new Vector3(p.x, fromY + C.probeUpM, p.z), Vector3.down, hits, C.probeUpM + C.probeLenM, PhysMasks.Ground, QueryTriggerInteraction.Ignore);
        float gy = float.NaN;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider; if (c == null || c.isTrigger) continue;
            if (c.GetComponentInParent<DuelFighter>() != null) continue;
            if (hits[i].point.y > fromY + C.probeUpM - 0.05f) continue;   // the ray started inside / above a ceiling mesh
            if (float.IsNaN(gy) || hits[i].point.y > gy) gy = hits[i].point.y;
        }
        return gy;
    }

    public static void Apply(WeaponSwap owner, DuelFighter fighter)
    {
        var C = Get();
        Transform inst = owner != null ? owner.WeaponRoot : null;
        if (inst == null) return;
        St s; if (!st.TryGetValue(inst, out s)) { s = null; }
        // undo last frame's correction first (WeaponGrip re-writes the child position every frame, but never the rotation)
        if (s != null && s.haveBase)
        {
            if ((inst.localPosition - s.lastPos).sqrMagnitude < 1e-10f) inst.localPosition = s.basePos; else s.basePos = inst.localPosition;   // position untouched since I wrote it = restore; else WeaponGrip rewrote it
            inst.localRotation = s.baseRot;
        }
        bool on = C.enabled && owner.TwoHanded && (fighter == null || !fighter.Dead) && !Duel.Frozen;
        if (on && C.weapons != null && C.weapons.Length > 0) { on = false; string id = owner.WeaponId; for (int i = 0; i < C.weapons.Length; i++) if (C.weapons[i] == id) on = true; }
        if (!on) { if (s != null) { s.tilt = 0f; s.lift = 0f; } return; }
        if (s == null) { s = Build(inst); st[inst] = s; }
        if (!s.haveBase) { s.basePos = inst.localPosition; s.baseRot = inst.localRotation; s.haveBase = true; }
        if (st.Count > 8) { var dead = new List<Transform>(); foreach (var kv in st) if (kv.Key == null) dead.Add(kv.Key); foreach (var d in dead) st.Remove(d); }

        Matrix4x4 m = inst.localToWorldMatrix; float rootY = fighter != null ? fighter.transform.position.y : inst.position.y;
        // lowest sampled vertex
        int lo = -1; float loY = float.MaxValue;
        for (int i = 0; i < s.pts.Length; i++) { var q = s.pts[i]; float y = m.m10 * q.x + m.m11 * q.y + m.m12 * q.z + m.m13; if (y < loY) { loY = y; lo = i; } }
        float targetTilt = 0f, targetLift = 0f; Vector3 axisW = s.axis; float worstPen = 0f, clearance = float.NaN; Vector3 worstP = Vector3.zero;
        if (lo >= 0 && loY - rootY < C.quickRejectM)
        {
            // candidates: lowest vertex, butt, tip (the 3 that matter for a head-down, a butt-down and a swing-through)
            Vector3 pl = m.MultiplyPoint3x4(s.pts[lo]);
            for (int c = 0; c < 3; c++)
            {
                Vector3 p; if (c == 0) p = pl; else if (!s.haveTipButt) break; else p = m.MultiplyPoint3x4(c == 1 ? s.buttL : s.tipL);
                float gy = GroundY(p, rootY, C); if (float.IsNaN(gy)) continue;
                float clr = p.y - gy; if (float.IsNaN(clearance) || clr < clearance) clearance = clr;
                float pen = gy + C.epsilonM - p.y; if (pen > worstPen) { worstPen = pen; worstP = p; }
            }
        }
        Vector3 pivot = inst.parent != null ? inst.parent.position : inst.position;   // the socket origin = the right palm
        if (worstPen > 0f)
        {
            Vector3 r = worstP - pivot; Vector3 rh = new Vector3(r.x, 0f, r.z); float rhl = rh.magnitude;
            float lift = worstPen;
            if (rhl > 0.08f)
            {
                axisW = Vector3.Cross(rh / rhl, Vector3.up);   // horizontal axis that raises r when rotated by a positive angle
                float lo2 = 0f, hi = C.maxTiltDeg;
                float dyMax = (Quaternion.AngleAxis(hi, axisW) * r).y - r.y;
                if (dyMax < worstPen) { targetTilt = hi; lift = worstPen - Mathf.Max(0f, dyMax); }
                else { for (int it = 0; it < 14; it++) { float mid = 0.5f * (lo2 + hi); float dy = (Quaternion.AngleAxis(mid, axisW) * r).y - r.y; if (dy < worstPen) lo2 = mid; else hi = mid; } targetTilt = hi; lift = 0f; }
            }
            targetLift = Mathf.Min(C.maxLiftM, Mathf.Max(0f, lift));
        }
        float dt = Time.deltaTime; float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.005f, targetTilt > s.tilt ? C.attackS : C.releaseS));
        s.tilt = Mathf.Lerp(s.tilt, targetTilt, k); s.lift = Mathf.Lerp(s.lift, targetLift, k);
        if (targetTilt > 0f) s.axis = axisW;
        if (s.tilt < 0.05f) s.tilt = 0f; if (s.lift < 0.0005f) s.lift = 0f;
        if (s.tilt > 0f || s.lift > 0f)
        {
            Quaternion R = Quaternion.AngleAxis(s.tilt, s.axis);
            Vector3 np = pivot + R * (inst.position - pivot) + Vector3.up * s.lift; Quaternion nr = R * inst.rotation;
            inst.SetPositionAndRotation(np, nr);
        }
        s.lastPos = inst.localPosition;
        if (Time.time >= s.nextLog && ((C.logCorrection && (worstPen > 0f || s.tilt > 0f)) || C.logClearance))
        {
            s.nextLog = Time.time + 0.5f; nCorrections++;
            if (nCorrections < 400) Debug.Log("[WeaponGround] " + owner.WeaponId + " clearance " + (float.IsNaN(clearance) ? "n/a" : clearance.ToString("F3")) + " m, pen " + worstPen.ToString("F3") + ", tilt " + s.tilt.ToString("F1") + " deg, lift " + s.lift.ToString("F3") + " m, lowY " + loY.ToString("F2") + " rootY " + rootY.ToString("F2"));
        }
    }
}
