using UnityEngine;

// KITHIT v1: the hit point (blood burst, VFX anchor, finisher contact) = the fighter's root axis at hitHeightM x scale, moved hitFwdM metres toward the attacker.
// Rows: roster.json characters[].hitHeightM (0 = the caller's default, 1.1 m = hit_vfx.json hitHeightM) and hitFwdM (0 = on the axis = today). Visual only: no tick, RNG or sim input reads it.
public static class HitAnchor
{
    public static Vector3 Point(DuelFighter a, DuelFighter d, float defaultHeightM = 1.1f)
    {
        float h = defaultHeightM, fwd = 0f;
        var r = Roster.Get(Roster.IdOf(d));
        if (r != null) { if (r.hitHeightM > 0f) h = r.hitHeightM; fwd = r.hitFwdM; }
        Vector3 p = d.transform.position + Vector3.up * (h * FighterScale.Of(d));
        if (fwd != 0f && a != null) { Vector3 t = a.transform.position - d.transform.position; t.y = 0f; if (t.sqrMagnitude > 1e-4f) p += t.normalized * fwd; }
        return p;
    }
}
