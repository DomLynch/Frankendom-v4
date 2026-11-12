using UnityEngine;

// BANDITGROUND v1: (a) capsule: a controller whose height is below 2 x radius is a ball that hangs below the root, so the root stands 0.12-0.22 m high: the centre is raised so the bottom is at the root.
// (b) unstack: a live fighter whose root is more than maxAboveM above the nearest NON-fighter surface for 'confirmChecks' checks is put on that surface, beside the fighter it stood on,
// and its fall-recovery memory is cleared (DuelFighter restored it to the height of the head it stood on). Data: code2.json "banditGround". Block missing or off = nothing happens.
public sealed partial class ZoneRuntime
{
    float nextBanditGround; readonly System.Collections.Generic.Dictionary<DuelFighter, int> bgCount = new System.Collections.Generic.Dictionary<DuelFighter, int>();
    static bool BgWanted(Code2Cfg.BanditGroundBlock B, Live l) { return l != null && l.row != null && (B.idPrefix.Length == 0 || l.row.id.StartsWith(B.idPrefix, System.StringComparison.Ordinal)); }

    void BanditGroundStep()
    {
        var C = Code2Cfg.Get(); if (C == null || !C.banditGround.enabled) return; var B = C.banditGround;
        if (Time.unscaledTime < nextBanditGround) return; nextBanditGround = Time.unscaledTime + Mathf.Max(0.05f, B.checkEveryS);
        foreach (var l in live.Values)
        {
            if (l == null || l.df == null || l.killed || l.df.Dead || l.go == null || !l.go.activeInHierarchy || !BgWanted(B, l)) continue;
            var cc = l.go.GetComponent<CharacterController>(); if (cc == null || !cc.enabled) continue;
            if (B.capsuleFix && cc.height < 2f * cc.radius) { var c = cc.center; c.y = cc.radius; cc.center = c; cc.height = 2f * cc.radius; }   // once: afterwards height == 2 x radius
            if (B.maxAboveM <= 0f) continue;
            Vector3 p = l.df.transform.position; float gy = float.NaN, best = 1e9f; DuelFighter under = null; float underD = 1e9f;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.3f, Vector3.down, B.rayDownM, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || h.collider.transform.IsChildOf(l.go.transform)) continue;
                var of = h.collider.GetComponentInParent<DuelFighter>();
                if (of != null) { if (h.distance < underD) { underD = h.distance; under = of; } continue; }   // a fighter is not ground
                if (h.distance < best) { best = h.distance; gy = h.point.y; }
            }
            int n; bgCount.TryGetValue(l.df, out n);
            if (float.IsNaN(gy) || p.y - gy <= B.maxAboveM) { if (n != 0) bgCount[l.df] = 0; continue; }
            if (++n < Mathf.Max(1, B.confirmChecks)) { bgCount[l.df] = n; continue; }
            bgCount[l.df] = 0; Vector3 to = new Vector3(p.x, gy + B.returnUpM, p.z);
            if (under != null)   // step off sideways so it does not land inside the one below
            {
                Vector3 d = p - under.transform.position; d.y = 0f; if (d.sqrMagnitude < 0.0001f) d = -l.df.transform.forward; d.Normalize();
                Vector3 q = under.transform.position + d * (cc.radius * l.go.transform.lossyScale.x + 0.7f + B.sideGapM); float qy = gy;
                foreach (var h in Physics.RaycastAll(new Vector3(q.x, p.y + 0.3f, q.z), Vector3.down, B.rayDownM, ~0, QueryTriggerInteraction.Ignore))
                    if (h.collider != null && h.collider.GetComponentInParent<DuelFighter>() == null && h.point.y > qy - 0.001f && h.point.y <= gy + B.maxAboveM) qy = h.point.y;
                to = new Vector3(q.x, qy + B.returnUpM, q.z);
            }
            Place(l.df, to, l.df.transform.eulerAngles.y); l.df.GroundReset();
            if (B.log) Debug.Log("[code2] banditGround: " + l.go.name + " was " + (p.y - gy).ToString("F2") + " m above the ground" + (under != null ? " on " + under.name : "") + ": put down at (" + to.x.ToString("F1") + ", " + to.y.ToString("F2") + ", " + to.z.ToString("F1") + ")");
        }
    }
}

public partial class DuelFighter
{
    /// <summary>BANDITGROUND v1: forget the fall-recovery spot after an outside move (it may be the head of another fighter).</summary>
    public void GroundReset() { lastGroundedPos = transform.position; hasGrounded = false; vy = -1f; knock = Vector3.zero; snapView = true; }
}
