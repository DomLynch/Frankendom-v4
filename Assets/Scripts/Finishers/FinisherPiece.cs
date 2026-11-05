using System;
using System.Collections.Generic;
using UnityEngine;

// V62B: a severed piece (the head of the decapitation) is a physics prop that STAYS (Rigidbody + collider, settles on the ground, remains until the scene is reloaded).
// V62C: the v62 proof showed "rescued x41": the old keeper compared the piece's PIVOT (the neck bone, below the head's centre) with the floor every physics step, so a head resting correctly on the
//  ground counted as "tunnelled" and was teleported 41 times. Now:
//  * the piece gets a PROPER collider fitted to its renderer bounds (finishers.json gore.piece: shape sphere | capsule | box), a low-friction / low-bounce physics material, a Rigidbody with
//    collisionDetectionMode ContinuousSpeculative (or Continuous), a sleep threshold and a depenetration limit; the victim's colliders are ignored as MeshCut did;
//  * at spawn the lowest point is clamped to the ground (spawnClampM); no per-frame rescue: ONE safety rescue (maxRescues) only when the COLLIDER bottom is more than rescueBelowM under the floor
//    (or the piece fell out of the world); every rescue is counted and logged, -finisherauto fails above rt.auto.pieceMaxRescues (3).
public sealed class FinisherPiece : MonoBehaviour
{
    public static readonly List<FinisherPiece> All = new List<FinisherPiece>();
    public static int Destroyed;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { All.Clear(); Destroyed = 0; }

    public string unit = ""; public Transform victim; public int Rescues; public bool Settled; public float Born; public float SpawnLift; public string ColliderInfo = "";
    public int RestClamps; public float RestM = -1f; bool restLogged;   // FINFIX v93: how often the rest-distance clamp pulled the piece back; horizontal distance from the cut point once it rests (-1 = still moving)
    Rigidbody rb; Collider col; float still; Vector3 spawn; int mask; PieceCfg cfg; static readonly RaycastHit[] hits = new RaycastHit[16];

    /// <summary>Called by FinisherPlayer.Gore right after MeshCut.Cut: every piece with a Rigidbody (the halves of a split stay attached and are skipped) gets a keeper.</summary>
    public static void Adopt(CutResult res, Transform victimRoot, string unitId)
    {
        if (res == null || !res.ok) return;
        foreach (var go in res.pieces)
        {
            if (go == null || go.GetComponent<Rigidbody>() == null || go.GetComponent<FinisherPiece>() != null) continue;
            var p = go.AddComponent<FinisherPiece>(); p.unit = unitId; p.victim = victimRoot;
            try { p.Setup(); } catch (Exception e) { Debug.LogWarning("[finisher] piece setup: " + e.Message); }
        }
    }

    void Awake() { All.Add(this); Born = Time.unscaledTime; spawn = transform.position; mask = ~((1 << 8) | (1 << 9)); var f = FinisherConfig.Get(); cfg = f != null && f.gore != null && f.gore.piece != null ? f.gore.piece : new PieceCfg(); }
    void OnDestroy() { All.Remove(this); Destroyed++; if (Application.isPlaying) Debug.Log("[finisher] piece '" + name + "' (" + unit + ") destroyed after " + (Time.unscaledTime - Born).ToString("0.0") + " s"); }

    static CollisionDetectionMode Mode(string s)
    {
        switch (s) { case "Continuous": return CollisionDetectionMode.Continuous; case "ContinuousDynamic": return CollisionDetectionMode.ContinuousDynamic; case "Discrete": return CollisionDetectionMode.Discrete; default: return CollisionDetectionMode.ContinuousSpeculative; }
    }

    bool RendererBounds(out Bounds b)
    {
        b = new Bounds(transform.position, Vector3.zero); bool any = false;
        foreach (var r in GetComponentsInChildren<Renderer>()) { if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
        return any;
    }

    void Setup()
    {
        rb = GetComponent<Rigidbody>(); if (rb == null) return;
        if (cfg == null) { var f = FinisherConfig.Get(); cfg = f != null && f.gore != null && f.gore.piece != null ? f.gore.piece : new PieceCfg(); }
        rb.collisionDetectionMode = Mode(cfg.collisionMode); rb.interpolation = cfg.interpolate ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
        rb.sleepThreshold = cfg.sleepThreshold; rb.maxDepenetrationVelocity = cfg.maxDepenetrationVelocity;
        if (cfg.linearDamping >= 0f) rb.linearDamping = cfg.linearDamping;
        FitCollider();
        ClampToGround();
    }

    // one collider fitted to the visible mesh (world bounds -> local shape); replaces the MeshCut one (a small sphere that could sit off the mesh centre)
    void FitCollider()
    {
        Bounds b; if (!RendererBounds(out b)) { col = GetComponent<Collider>(); return; }
        var old = GetComponents<Collider>(); foreach (var o in old) if (o != null) { o.enabled = false; Destroy(o); }
        Vector3 ext = b.extents, ls = transform.lossyScale; float sc = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z))));
        float mn = Mathf.Min(ext.x, Mathf.Min(ext.y, ext.z)), mx = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z));
        float rad = Mathf.Max(cfg.minRadiusM, (mn + (mx - mn) * Mathf.Clamp01(cfg.fitK)) * cfg.radiusScale);
        Vector3 c = transform.InverseTransformPoint(b.center);
        if (cfg.shape == "box")
        {
            var bc = gameObject.AddComponent<BoxCollider>(); bc.center = c; Vector3 sz = transform.InverseTransformVector(b.size); bc.size = new Vector3(Mathf.Abs(sz.x), Mathf.Abs(sz.y), Mathf.Abs(sz.z)) * cfg.radiusScale; col = bc; ColliderInfo = "box " + bc.size.ToString("0.00");
        }
        else if (cfg.shape == "capsule")
        {
            var cc = gameObject.AddComponent<CapsuleCollider>(); cc.center = c;
            Vector3 axisW = ext.x >= ext.y && ext.x >= ext.z ? Vector3.right : ext.y >= ext.z ? Vector3.up : Vector3.forward;
            float dx = Mathf.Abs(Vector3.Dot(transform.right, axisW)), dy = Mathf.Abs(Vector3.Dot(transform.up, axisW)), dz = Mathf.Abs(Vector3.Dot(transform.forward, axisW));
            cc.direction = dx >= dy && dx >= dz ? 0 : dy >= dz ? 1 : 2;
            cc.radius = rad / sc; cc.height = Mathf.Max(2f * rad, 2f * mx * cfg.radiusScale) / sc; col = cc; ColliderInfo = "capsule r " + rad.ToString("0.00") + " h " + cc.height.ToString("0.00");
        }
        else
        {
            var sp = gameObject.AddComponent<SphereCollider>(); sp.center = c; sp.radius = rad / sc; col = sp; ColliderInfo = "sphere r " + rad.ToString("0.00");
        }
        var pm = new PhysicsMaterial("FinisherPiece") { dynamicFriction = cfg.dynamicFriction, staticFriction = cfg.staticFriction, bounciness = cfg.bounciness, frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Minimum };
        col.sharedMaterial = pm;
        if (victim != null)
            foreach (var vc in victim.GetComponentsInChildren<Collider>(true))
            {
                if (vc == null || vc == col || vc.transform.IsChildOf(transform)) continue;
                if (!vc.enabled || !vc.gameObject.activeInHierarchy) continue;   // Physics.IgnoreCollision logs an error for a disabled collider (Duel.Die() switches the victim's off)
                try { Physics.IgnoreCollision(vc, col, true); } catch (Exception) { }
            }
        rb.ResetCenterOfMass(); rb.ResetInertiaTensor();
    }

    // at spawn: the lowest point of the mesh must not start under the floor
    void ClampToGround()
    {
        Bounds b; if (!RendererBounds(out b)) return;
        bool found; float gy = SurfaceY(new Vector3(b.center.x, b.max.y, b.center.z), out found);
        if (!found) return;
        float need = gy + cfg.spawnClampM - b.min.y;
        if (need > 0f) { SpawnLift = need; transform.position += Vector3.up * need; rb.position = transform.position; }
    }

    float SurfaceY(Vector3 p, out bool found)
    {
        found = false; float best = float.MaxValue, y = 0f;
        int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.5f, Vector3.down, hits, 14f, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || h.collider.transform.IsChildOf(transform)) continue;
            if (h.collider.GetComponentInParent<FinisherPiece>() != null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (h.distance < best) { best = h.distance; y = h.point.y; found = true; }
        }
        return y;
    }

    void FixedUpdate()
    {
        if (rb == null || col == null) return;
        // FINFIX v93: gore.piece.restMaxM - the head never travels (or rests) farther than this from the neck: pulled back onto the circle, the outward speed removed, the rest damped
        if (cfg.restMaxM > 0f && !rb.isKinematic)
        {
            Vector3 off = rb.position - spawn; off.y = 0f; float dm = off.magnitude;
            if (dm > cfg.restMaxM)
            {
                Vector3 dir = off / dm; Vector3 tp = rb.position; tp.x = spawn.x + dir.x * cfg.restMaxM; tp.z = spawn.z + dir.z * cfg.restMaxM;
                rb.position = tp; transform.position = tp; RestClamps++;
                Vector3 v = rb.linearVelocity; float outv = Mathf.Max(0f, v.x * dir.x + v.z * dir.z);
                v.x = (v.x - dir.x * outv) * cfg.restMaxDamp; v.z = (v.z - dir.z * outv) * cfg.restMaxDamp; rb.linearVelocity = v;
            }
        }
        // safety only (data gore.piece.rescueBelowM / maxRescues): the COLLIDER bottom (not the pivot) is far under the floor, or the piece left the world
        if (Rescues < cfg.maxRescues)
        {
            Bounds cb = col.bounds; bool found; float gy = SurfaceY(new Vector3(cb.center.x, cb.max.y, cb.center.z), out found);
            if (found && cb.min.y < gy - cfg.rescueBelowM) Rescue(Vector3.up * (gy + cfg.spawnClampM + 0.02f - cb.min.y), "collider bottom " + (gy - cb.min.y).ToString("0.00") + " m under the ground");
            else if (!found && rb.position.y < spawn.y - 6f) { Vector3 b = victim != null ? victim.position : spawn; Rescue(new Vector3(b.x, b.y + 0.5f, b.z) - rb.position, "fell out of the world"); }
        }
        if (rb.linearVelocity.sqrMagnitude < 0.01f && rb.angularVelocity.sqrMagnitude < 0.04f) still += Time.fixedDeltaTime; else still = 0f;
        Settled = still > cfg.settleSeconds;
        if (Settled && !restLogged)   // FINFIX v93: the head-rest distance (VERIFY.md greps this line)
        {
            restLogged = true; Vector3 ro = rb.position - spawn; ro.y = 0f; RestM = ro.magnitude;
            Debug.Log("[finisher] piece '" + name + "' (" + unit + ") REST " + RestM.ToString("0.00") + " m from the cut point, " + (Time.unscaledTime - Born).ToString("0.0") + " s after the cut, rest clamps " + RestClamps + (cfg.restMaxM > 0f ? " (limit " + cfg.restMaxM.ToString("0.00") + " m)" : ""));
        }
        if (cfg.settleLift && !rb.isKinematic && still > cfg.settleStillS && SettleLifts < 3)
        {
            Bounds vb; if (Visible(out vb))
            {
                bool f2; float gy2 = SurfaceY(new Vector3(vb.center.x, vb.max.y, vb.center.z), out f2);
                float gap = f2 ? vb.min.y - gy2 : 0f;
                if (f2 && gap < -cfg.settleTolM)
                {
                    SettleLifts++; float up = Mathf.Min(-gap + cfg.spawnClampM, cfg.settleMaxLiftM); var to = rb.position + Vector3.up * up; rb.position = to; transform.position = to;
                    if (cfg.settleFreeze) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; }
                    Debug.Log("[finisher] piece '" + name + "' (" + unit + ") SettleLift x" + SettleLifts + ": visible mesh " + (-gap).ToString("0.00") + " m under the ground, lifted " + up.ToString("0.00") + " m" + (cfg.settleFreeze ? " and frozen" : ""));
                }
            }
        }
    }

    public int SettleLifts;   // V64F2
    void Rescue(Vector3 move, string why)
    {
        Rescues++; var to = rb.position + move; rb.position = to; transform.position = to; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
        Debug.LogWarning("[finisher] piece '" + name + "' (" + unit + ") safety rescue x" + Rescues + ": " + why);
    }

    /// <summary>World bounds of the piece's renderers (empty bounds when it has none).</summary>
    public bool Visible(out Bounds b)
    {
        b = new Bounds(transform.position, Vector3.zero); bool any = false;
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
        }
        return any;
    }
}

// -finisherauto proof for cut entries: the piece(s) of the unit are alive, visible and lying on the ground, and the victim's body no longer has the unit's triangles (headless end frame).
public static class FinisherPieceCheck
{
    public static void Pieces(string unitId, float maxGapM, out int alive, out float worstGap, out int rescues)
    {
        alive = 0; worstGap = 0f; rescues = 0;
        foreach (var p in FinisherPiece.All)
        {
            if (p == null || p.unit != unitId || !p.gameObject.activeInHierarchy) continue;
            Bounds b; if (!p.Visible(out b)) continue;
            var ray = new Ray(new Vector3(b.center.x, b.center.y + 1.5f, b.center.z), Vector3.down); float gy = b.min.y - 0.05f; bool found = false;
            foreach (var h in Physics.RaycastAll(ray, 14f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(p.transform) || h.collider.GetComponentInParent<FinisherPiece>() != null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
                if (!found || h.point.y > gy) { gy = h.point.y; found = true; }
            }
            float gap = found ? b.min.y - gy : 0f; if (Mathf.Abs(gap) > Mathf.Abs(worstGap)) worstGap = gap;
            alive++; rescues += p.Rescues;
        }
    }

    /// <summary>Triangles of the victim's skinned meshes whose vertices are (>= 2 of 3, weight >= cuts.weightMin) skinned to the unit's bone subtree, cap submeshes excluded. 0 = the part is gone from the body; -1 = cannot tell.</summary>
    public static int TrianglesLeft(Transform victim, string unitId)
    {
        var cfg = FinisherConfig.Get(); if (victim == null || cfg.cuts == null || cfg.cuts.units == null) return -1;
        CutUnit u = null; foreach (var x in cfg.cuts.units) if (x != null && x.id == unitId) u = x;
        if (u == null || u.bones == null) return -1;
        Transform root = null; var all = victim.GetComponentsInChildren<Transform>(true);
        foreach (var bn in u.bones) { string k = GoreFx.Key(bn); foreach (var t in all) if (GoreFx.Key(t.name) == k) { root = t; break; } if (root != null) break; }
        if (root == null) return -1;
        var set = new HashSet<Transform>(root.GetComponentsInChildren<Transform>(true)); int left = 0; bool any = false;
        foreach (var smr in victim.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            var mesh = smr.sharedMesh; if (mesh == null || !mesh.isReadable) continue;
            var bw = mesh.boneWeights; var bones = smr.bones; int vc = mesh.vertexCount; if (bw.Length != vc || bones == null) continue;
            any = true; var inA = new bool[vc];
            for (int v = 0; v < vc; v++)
            {
                float w = 0f; var b = bw[v];
                w += In(bones, b.boneIndex0, set) ? b.weight0 : 0f; w += In(bones, b.boneIndex1, set) ? b.weight1 : 0f; w += In(bones, b.boneIndex2, set) ? b.weight2 : 0f; w += In(bones, b.boneIndex3, set) ? b.weight3 : 0f;
                inA[v] = w >= cfg.cuts.weightMin;
            }
            var mats = smr.sharedMaterials;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (s < mats.Length && mats[s] != null && mats[s].name.StartsWith("FinisherCap", StringComparison.Ordinal)) continue;   // the flesh cap closing the neck
                var idx = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < idx.Length; t += 3) if ((inA[idx[t]] ? 1 : 0) + (inA[idx[t + 1]] ? 1 : 0) + (inA[idx[t + 2]] ? 1 : 0) >= 2) left++;
            }
        }
        return any ? left : -1;
    }
    static bool In(Transform[] bones, int i, HashSet<Transform> set) { return i >= 0 && i < bones.Length && bones[i] != null && set.Contains(bones[i]); }
}
