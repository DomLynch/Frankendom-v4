using UnityEngine;

// CODE2 v1 (laneCODE2): boss tether. In the 10 Nov full run an alive Erlking walked up the lane past the end of the z6 ground (z 298.6; ring centre z 282, disc 15 + lane |x| <= 6)
// while Joan was in z7, fell, was restored, fell again: 28 '[DuelFighter] Erlking fell out of the world' lines. Chase / leash do not cover it (a chase agent owns the body, Engagement() skips the leash for it).
// Data: code2.json "tether" { enabled, rows[ { id (spawn id) or creature, centre [x,z], radius, minY } ] }. A fighter whose flat distance to the centre exceeds the radius (or whose y < minY) is put back at its
// home spot (Live.home, the same Place() the leash reset uses). Health is NOT refilled unless healOnReturn. Block off / row off / no row = nothing happens.
public sealed partial class ZoneRuntime
{
    float nextTether, nextTetherLog;
    void TetherStep()
    {
        var C = Code2Cfg.Get(); if (C == null || !C.tether.enabled) return;
        if (Time.unscaledTime < nextTether) return; nextTether = Time.unscaledTime + Mathf.Max(0.05f, C.tether.checkEveryS);
        foreach (var l in live.Values)
        {
            if (l == null || l.df == null || l.killed || l.df.Dead || l.go == null || !l.go.activeInHierarchy) continue;
            var r = C.tether.Find(l.row != null ? l.row.id : "", l.creature); if (r == null || r.centre == null || r.centre.Length < 2) continue;
            Vector3 p = l.df.transform.position; float dx = p.x - r.centre[0], dz = p.z - r.centre[1];
            bool far = dx * dx + dz * dz > r.radius * r.radius, low = r.minY > -900f && p.y < r.minY;
            if (!far && !low) continue;
            Place(l.df, l.home + Vector3.up * C.tether.returnUpM, l.homeYaw);
            if (C.tether.healOnReturn) { l.df.health = l.df.maxHealth; var hp = l.df.GetComponent<Health>(); if (hp != null) hp.current = hp.max; }
            if (l.chase != null) l.chase.Restart();
            if (C.tether.log && Time.unscaledTime >= nextTetherLog) { nextTetherLog = Time.unscaledTime + Mathf.Max(0f, C.tether.logEveryS); Debug.Log("[code2] tether: " + l.go.name + " was at (" + p.x.ToString("F1") + ", " + p.y.ToString("F1") + ", " + p.z.ToString("F1") + "), " + (far ? "outside radius " + r.radius.ToString("F1") : "below minY " + r.minY.ToString("F1")) + ": returned to its home spot"); }
        }
    }
}
