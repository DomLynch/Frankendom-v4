using System;
using UnityEngine;

// CAMPGROUND v1 (laneCAMPGROUND): ground for an ARRIVAL that is not a walk. A direct start (play_zone_at.ps1 -> zones_save.json -> Rebind) puts the player at a spot whose ground ring (the GroundCollider is a child of
// the zone's first ring group, inactive until ZoneSpan wakes it) is still switched off; ZoneSpan binds some frames later and Pace wakes one ring per frame, after load frames of seconds. The player free-fell meanwhile
// (DuelFighter: vy -= 20 * dt) and ended below the 1 m collider box for good; HubNpcs then found no ground and used the player's y. Here: ZoneRuntime.Rebind calls Begin(); the player is pinned (gravity off through
// ZoneGroundHold.Active, a 1-line gate in DuelFighter) until a down ray finds ground at that x / z; ZoneSpan.WakeNow() switches the collider rings of the wanted zones on at once. Data: zone_span.json "arrive".
// Never runs in a harness run (ZoneRuntime does not exist there). Logs ONE line after the arrival: player y and the ray-down distance. UNPROVEN IN UNITY.
public static class ZoneGroundHold { public static bool Active; }

[DefaultExecutionOrder(700)]
public sealed class ZoneGround : MonoBehaviour
{
    static ZoneGround inst; static readonly RaycastHit[] hits = new RaycastHit[16];
    DuelFighter p; Vector3 pin; int start, liveFrames; bool woke;
    public static bool Holding { get { return ZoneGroundHold.Active; } }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; ZoneGroundHold.Active = false; }

    /// <summary>Called by ZoneRuntime.Rebind (and ZoneShot for each teleport) right after the player was placed at 'pos'.</summary>
    public static void Begin(DuelFighter player, Vector3 pos)
    {
        var A = SpanData.Get().arrive; if (!A.enabled || player == null) return;
        if (inst == null) { var go = new GameObject("ZoneGround"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneGround>(); }
        inst.p = player; inst.pin = pos; inst.start = Time.frameCount; inst.liveFrames = 0; inst.woke = false; ZoneGroundHold.Active = true;
    }

    /// <summary>Nearest ground below (x, z) from at.y + up, ignoring triggers and fighters. False = none; 'dist' = metres from 'at' down to the hit.</summary>
    public static bool Probe(Vector3 at, DuelFighter self, out float groundY, out float dist)
    {
        var A = SpanData.Get().arrive; groundY = at.y; dist = -1f;
        int n = Physics.RaycastNonAlloc(new Vector3(at.x, at.y + A.rayUpM, at.z), Vector3.down, hits, A.rayUpM + A.rayDownM, A.groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider; if (c == null || (self != null && c.transform.IsChildOf(self.transform))) continue;
            if (c.GetComponentInParent<DuelFighter>() != null) continue;   // another fighter is not ground
            if (hits[i].distance < best) { best = hits[i].distance; groundY = hits[i].point.y; dist = hits[i].distance - A.rayUpM; }
        }
        return best < float.MaxValue;
    }

    static void Put(DuelFighter f, Vector3 v)
    {
        var cc = f.GetComponent<CharacterController>(); bool en = cc != null && cc.enabled; if (en) cc.enabled = false;
        f.transform.position = v; if (en) cc.enabled = true; Physics.SyncTransforms();
    }

    void LateUpdate()
    {
        if (!ZoneGroundHold.Active) return;
        var A = SpanData.Get().arrive;
        if (p == null) { ZoneGroundHold.Active = false; return; }
        if (ZoneSpan.Live) { liveFrames++; if (!woke && ZoneSpan.Inst != null) { woke = true; ZoneSpan.Inst.WakeNow(); } }
        float gy, d;
        if (Probe(pin, p, out gy, out d))
        {
            pin.y = gy; Put(p, pin); ZoneGroundHold.Active = false;
            Debug.Log("[Zones] arrive: player y " + gy.ToString("F2") + ", ground " + d.ToString("F2") + " m below the start point (hold " + (Time.frameCount - start) + " frames)");
            return;
        }
        Put(p, pin);   // the pin: whatever DuelFighter moved this frame (v.y is -1 while held) is undone
        bool spanGone = !ZoneSpan.Live && Time.frameCount - start > A.noSpanFrames;
        if (liveFrames > A.holdFrames || spanGone)
        {
            ZoneGroundHold.Active = false;
            Debug.LogWarning("[Zones] arrive: NO ground below y " + pin.y.ToString("F2") + " after " + (Time.frameCount - start) + " frames (span " + (ZoneSpan.Live ? "live" : "not live") + "), hold released: the player will fall");
        }
    }
}
