using System.Collections.Generic;
using UnityEngine;

// K24 (laneLG, file-only, NOT applied): bow aim assist. While the bow is up and the fighter draws or looses (FState Windup / Active / Recovery) the body turns to the target at bow.json aim.turnSpeedDegS,
// and the release snaps the last few degrees so the arrow never leaves from the back. Target = the fighter's duel foe (inside aim.rangeM, inside the cone or aim.lockedAlwaysFaces), and with aim.retarget the nearest
// opposing fighter inside the cone measured from the camera heading. Master off / aim.enabled false = nothing here runs (BowHooks calls return at once). Runs on the 60 Hz sim tick AFTER MoveTick, so it wins over the
// default facing (which only follows the move direction when no lock is held). Nothing numeric lives here: bow.json aim.
public static class BowAim
{
    static bool On { get { var a = BowCfg.Get().aim; return a != null && a.enabled; } }

    static bool Aiming(DuelFighter f) { return f.state == FState.Windup || f.state == FState.Active || f.state == FState.Recovery; }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // the heading the player is looking: the camera when there is one, else the body
    static Vector3 Heading(DuelFighter f)
    {
        if (f.IsPlayer && Camera.main != null) { var c = Flat(Camera.main.transform.forward); if (c.sqrMagnitude > 1e-4f) return c.normalized; }
        var b = Flat(f.transform.forward); return b.sqrMagnitude > 1e-4f ? b.normalized : Vector3.forward;
    }

    static bool InCone(DuelFighter f, Vector3 to, float coneDeg)
    {
        if (coneDeg >= 180f) return true;
        return Vector3.Angle(Heading(f), to) <= coneDeg;
    }

    /// <summary>The fighter the bow faces / shoots, or null (no assist: the caller keeps the duel's own foe and facing).</summary>
    public static DuelFighter Pick(DuelFighter f, DuelFighter foe, List<DuelFighter> all)
    {
        if (!On || f == null) return null;
        var A = BowCfg.Get().aim; if (A.playerOnly && !f.IsPlayer) return null;
        DuelFighter best = null; float bestD = float.MaxValue;
        if (foe != null && !foe.Dead)
        {
            Vector3 d = Flat(foe.transform.position - f.transform.position); float g = d.magnitude;
            if (g <= A.rangeM && g > 0.01f && (A.lockedAlwaysFaces || InCone(f, d, A.coneDeg))) { best = foe; bestD = g; if (!A.retarget) return best; }
        }
        if (!A.retarget || all == null) return best;
        for (int i = 0; i < all.Count; i++)
        {
            var o = all[i]; if (o == null || o == f || o.Dead || o.IsPlayer == f.IsPlayer) continue;
            Vector3 d = Flat(o.transform.position - f.transform.position); float g = d.magnitude;
            if (g > A.rangeM || g < 0.01f || g >= bestD || !InCone(f, d, A.coneDeg)) continue;
            // an in-cone foe never loses to a locked foe that stands outside the cone: only closer wins over a cone-accepted lock
            best = o; bestD = g;
        }
        return best;
    }

    /// <summary>BowHooks.SimTick (after Evaluate): turn towards the target while drawing / loosing. Returns the target used (or null).</summary>
    public static DuelFighter Tick(DuelFighter f, DuelFighter foe, List<DuelFighter> all)
    {
        if (!On) return null;
        var A = BowCfg.Get().aim;
        bool aiming = Aiming(f); if (!aiming && !A.faceInLoco && !BowFree.FaceHeld(f)) return null;   // FREEAIM v1
        if (f.state == FState.Dead || f.state == FState.Stagger || f.state == FState.Down || f.state == FState.Roll) return null;
        if (BowFree.Wants(f) && (aiming || BowFree.FaceHeld(f)))   // FREEAIM v1: the body follows the camera heading (the crosshair), not a foe
        {
            Vector3 h = BowFree.Heading(f); if (h.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.RotateTowards(f.transform.rotation, Quaternion.LookRotation(h, Vector3.up), A.turnSpeedDegS / 60f);
            return null;
        }
        if (!aiming && !A.faceInLoco) return null;
        var t = Pick(f, foe, all); if (t == null) return null;
        Vector3 d = Flat(t.transform.position - f.transform.position); if (d.sqrMagnitude < 1e-4f) return t;
        Quaternion want = Quaternion.LookRotation(d.normalized, Vector3.up);
        f.transform.rotation = Quaternion.RotateTowards(f.transform.rotation, want, A.turnSpeedDegS / 60f);
        return t;
    }

    /// <summary>FREEAIM v1: a point shot leaves from the front too.</summary>
    public static void SnapPoint(DuelFighter a, Vector3 p)
    {
        if (!On || a == null) return;
        var A = BowCfg.Get().aim; if (!A.snapOnRelease || (A.playerOnly && !a.IsPlayer)) return;
        Vector3 d = Flat(p - a.transform.position); if (d.sqrMagnitude < 1e-4f) return;
        Quaternion want = Quaternion.LookRotation(d.normalized, Vector3.up);
        if (Quaternion.Angle(a.transform.rotation, want) > A.snapAboveDeg) a.transform.rotation = want;
    }

    /// <summary>BowHooks.Release: the arrow leaves from the front. Closes the remaining angle when it is above aim.snapAboveDeg.</summary>
    public static void Snap(DuelFighter a, DuelFighter target)
    {
        if (!On || a == null || target == null) return;
        var A = BowCfg.Get().aim; if (!A.snapOnRelease || (A.playerOnly && !a.IsPlayer)) return;
        Vector3 d = Flat(target.transform.position - a.transform.position); if (d.sqrMagnitude < 1e-4f) return;
        Quaternion want = Quaternion.LookRotation(d.normalized, Vector3.up);
        if (Quaternion.Angle(a.transform.rotation, want) > A.snapAboveDeg) a.transform.rotation = want;
    }
}
