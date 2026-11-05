using System;
using UnityEngine;

// V1FIN v94: the two SCRIPTED severed pieces of Frankendom V1 (no Rigidbody physics; they replace MeshCut.Launch's Rigidbody when rt.v1.enabled).
//   SeveredHeadArc  = src/severed-head.ts (launchSeveredHead + stepSeveredHead), every number from CutUnit.arc (finishers.json): popped sideways + up, gravity, a bounce or two, then a roll without slipping until
//                     friction stops it, then it STAYS. Spin about the world axis, rotated about the head's own centre (V1 centres the baked group on its bounding box).
//   OpenedHalf      = src/opened.ts place(): the upper half slides sideways and tips over on a schedule (smoothstep windows), floored on the ground. The legs are the victim's own (animated) body.
// The component takes its time from FinisherTime.Step (real time, clamped), never from Time.timeScale.
static class V1Ground
{
    static readonly RaycastHit[] hits = new RaycastHit[16];
    public static float Y(Vector3 p, Transform ignore, float fallback)
    {
        int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.5f, Vector3.down, hits, 14f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore); float best = float.MaxValue, y = fallback;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || (ignore != null && h.collider.transform.IsChildOf(ignore))) continue;
            if (h.collider.GetComponentInParent<FinisherPiece>() != null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (h.distance < best) { best = h.distance; y = h.point.y; }
        }
        return y;
    }
    public static bool Bounds(Transform root, out Bounds b)
    {
        b = new Bounds(root.position, Vector3.zero); bool any = false;
        foreach (var r in root.GetComponentsInChildren<Renderer>()) { if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || !r.enabled) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
        return any;
    }
}

public sealed class SeveredHeadArc : MonoBehaviour
{
    ArcCfg a; Vector3 c, vel, spin, centerLocal, spawn; Quaternion q; float radius, groundDefault; bool rest;

    /// <summary>MeshCut.Launch hook: the piece (root, already placed and visible) becomes kinematic and is stepped by this script.</summary>
    public static void Begin(GameObject root, Transform victim, ArcCfg cfg, Vector3 away)
    {
        var rb = root.GetComponent<Rigidbody>(); if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
        var s = root.AddComponent<SeveredHeadArc>(); s.Init(cfg, away, victim != null ? victim.position.y : root.transform.position.y - 1.5f);
    }

    void Init(ArcCfg cfg, Vector3 away, float floorFallbackY)
    {
        a = cfg; Bounds b; if (!V1Ground.Bounds(transform, out b)) b = new Bounds(transform.position, Vector3.one * 0.2f);
        c = b.center; spawn = c; q = transform.rotation;
        centerLocal = Quaternion.Inverse(q) * (c - transform.position);
        radius = Mathf.Clamp(Mathf.Max(b.size.x, b.size.z) * a.radiusK, a.radiusMinM, a.radiusMaxM);   // characters.ts sever(): clamp(max(size.x, size.z) * .38, .07, .24)
        away.y = 0f; if (away.sqrMagnitude < 1e-4f) away = Vector3.forward; away.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, away);   // the right of the killer -> victim axis (the side the V1 camera slides to)
        vel = right * a.sideSign * a.sideMps + Vector3.up * a.upMps;   // launchSeveredHead: (axis-perpendicular 1.1, up 1.8)
        spin = right * a.spinSign * a.spinRadS;                        // spin about the blow's heading axis
        groundDefault = floorFallbackY; Debug.Log("[finisher] head arc: radius " + radius.ToString("0.000") + " m, v " + vel.ToString("0.00") + ", spin " + a.spinRadS.ToString("0.0") + " rad/s, centre " + c.ToString("0.00"));
    }

    void Update()
    {
        if (a == null || rest) return;
        float rem = FinisherTime.Step(Time.unscaledDeltaTime), sub = Mathf.Max(1e-3f, a.maxSubstepS);
        while (rem > 1e-6f && !rest) { float h = Mathf.Min(rem, sub); StepOnce(h); rem -= h; }
        transform.SetPositionAndRotation(c + q * -centerLocal, q);
    }

    // stepSeveredHead, line for line
    void StepOnce(float dt)
    {
        vel.y -= a.gravity * dt;
        c += vel * dt;
        float rate = spin.magnitude;
        if (rate > 0f) q = Quaternion.AngleAxis(rate * dt * Mathf.Rad2Deg, spin / rate) * q;   // rotateOnWorldAxis
        float gy = V1Ground.Y(c, transform, groundDefault);
        if (c.y < gy + radius)
        {
            c.y = gy + radius;
            float speed = Mathf.Sqrt(vel.x * vel.x + vel.z * vel.z);
            if (vel.y < -a.bounceMinVy) { vel.y = -vel.y * a.bounceVy; vel.x *= a.bounceXz; vel.z *= a.bounceXz; }   // a real bounce
            else
            {
                vel.y = 0f; float decay = Mathf.Max(0f, 1f - a.rollDecayPerS * dt); vel.x *= decay; vel.z *= decay;   // rolling friction
                if (speed > a.restSpeed) spin = new Vector3(vel.z / radius, 0f, -vel.x / radius);
                else { rest = true; Rested(gy); }
            }
        }
    }

    void Rested(float gy)
    {
        // V1 floors the sphere radius, not the mesh: if the visible mesh ended up under the floor, lift it out (once, <= restLiftMaxM)
        transform.SetPositionAndRotation(c + q * -centerLocal, q); Bounds b; float lift = 0f;
        if (V1Ground.Bounds(transform, out b)) { float under = gy - b.min.y; if (under > a.restLiftTolM) { lift = Mathf.Min(under + 0.005f, a.restLiftMaxM); c.y += lift; transform.position += Vector3.up * lift; } }
        var p = GetComponent<FinisherPiece>(); Vector3 ro = c - spawn; ro.y = 0f;
        if (p != null) { p.Settled = true; p.RestM = ro.magnitude; }
        Debug.Log("[finisher] piece '" + name + "' (head arc) REST " + ro.magnitude.ToString("0.00") + " m from the cut point (V1 scripted arc), lifted " + lift.ToString("0.000") + " m");
    }
    public bool AtRest { get { return rest; } }
}

public sealed class OpenedHalf : MonoBehaviour
{
    ScriptCfg s; Vector3 p0, right, along; Quaternion r0, tip; float age, scale = 1f, gy0; bool logged; Vector3 lastGood;

    public static void Begin(GameObject root, Transform victim, ScriptCfg cfg, Vector3 away)
    {
        var rb = root.GetComponent<Rigidbody>(); if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
        var h = root.AddComponent<OpenedHalf>(); h.Init(cfg, away, victim);
    }
    void Init(ScriptCfg cfg, Vector3 away, Transform victim)
    {
        s = cfg; p0 = transform.position; r0 = transform.rotation; away.y = 0f; if (away.sqrMagnitude < 1e-4f) away = Vector3.forward; along = away.normalized;
        right = Vector3.Cross(Vector3.up, along) * s.sideSign; scale = victim != null ? Mathf.Max(0.5f, Mathf.Abs(victim.lossyScale.y)) : 1f;
        tip = Quaternion.AngleAxis(-s.sideSign * s.fallDeg, along); gy0 = V1Ground.Y(p0, transform, victim != null ? victim.position.y : p0.y - 1.5f); lastGood = p0;
    }
    static float Smooth(float t, float a, float b) { float x = Mathf.Clamp01((t - a) / Mathf.Max(1e-4f, b - a)); return x * x * (3f - 2f * x); }
    void Update()
    {
        if (s == null) return;
        age += FinisherTime.Step(Time.unscaledDeltaTime);
        float slide = Smooth(age, s.slideStartS, s.slideEndS), fall = Smooth(age, s.fallStartS, s.fallEndS);   // opened.ts place(): .5 x scale sideways, .12 along; tips over .2-.66 of the clock
        Vector3 pos = p0 + right * (s.slideSideM * scale * slide) + along * (s.slideAlongM * scale * slide);
        Quaternion rot = Quaternion.Slerp(Quaternion.identity, tip, fall) * r0;
        transform.SetPositionAndRotation(pos, rot);
        Bounds b;
        if (V1Ground.Bounds(transform, out b)) { float gy = V1Ground.Y(b.center, transform, gy0); float under = gy + s.floorM - b.min.y; if (under > 0f) transform.position += Vector3.up * Mathf.Min(under, 0.5f); }   // floors[] table: .008 above the sand
        if (!logged && age > s.fallEndS) { logged = true; Debug.Log("[finisher] piece '" + name + "' (opened upper half) down at " + transform.position.ToString("0.00")); var p = GetComponent<FinisherPiece>(); if (p != null) p.Settled = true; }
    }
}
