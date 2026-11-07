using System;
using UnityEngine;

// FOESEP v1 (laneFOESEP, UNPROVEN in Unity): two enemies on Joan keep apart from each other and take different sides of her.
// Everything is a row of foe_sep.json (read once through HudCfg.ReadData); body widths are rows per creature id (bodies[]), so a new, wider or thinner body is a data edit.
// Zones only: the peer comes from ZoneRuntime.PeerOf (null without zones or with one fighting enemy), so a single enemy and the zones-off duel never reach the maths below.
// Never allocates (Vector3 structs, a 2-3 entry loop). Never changes the distance to Joan to first order: the separation moves the REAR enemy along the tangent round Joan, the spread orbits tangentially.
[Serializable] public class FsBody { public string id = "*"; public float halfWidthM; }          // halfWidthM 0 = the fighter's own DuelFighter.radius
[Serializable] public class FsSeparate { public float padM = 0.5f, minM = 1.6f, maxStepM = 0.03f, yieldMarginM = 0.3f, radialShare = 0f; }
[Serializable] public class FsSpread { public bool aiSide = true, inChase = true, inBrain = true; public float minAngleDeg = 50f, rampDeg = 20f, orbitGain = 0.45f, rangeM = 8f; }
[Serializable] public class FsFile { public bool enabled = true; public FsSeparate separate = new FsSeparate(); public FsSpread spread = new FsSpread(); public FsBody[] bodies = new FsBody[0]; }

public static class FoeSep
{
    static FsFile f; static bool loaded;
    static void Load()
    {
        if (loaded) return; loaded = true;
        var txt = HudCfg.ReadData("foe_sep.json");
        if (txt != null) { try { f = JsonUtility.FromJson<FsFile>(txt); } catch (Exception e) { Debug.LogWarning("[FoeSep] foe_sep.json parse failed: " + e.Message); } }
        if (f != null) { if (f.separate == null) f.separate = new FsSeparate(); if (f.spread == null) f.spread = new FsSpread(); if (f.bodies == null) f.bodies = new FsBody[0]; }
    }
    /// <summary>True when foe_sep.json says on (and -zones / -nozones allow it). Costs one bool test per call after the first.</summary>
    public static bool On { get { if (!loaded) Load(); return f != null && ZoneFlag.Allowed(f.enabled); } }

    /// <summary>Half width of a fighter's body in metres: the row whose id its name starts with, else the "*" row, else DuelFighter.radius. Data, per creature.</summary>
    public static float HalfWidth(DuelFighter d)
    {
        float star = 0f; var b = f.bodies;
        for (int i = 0; i < b.Length; i++)
        {
            var r = b[i]; if (r == null) continue;
            if (r.id == "*") { star = r.halfWidthM; continue; }
            if (r.id.Length > 0 && d.name.StartsWith(r.id, StringComparison.Ordinal)) return r.halfWidthM > 0f ? r.halfWidthM : d.radius;
        }
        return star > 0f ? star : d.radius;
    }
    public static float MinSep(DuelFighter a, DuelFighter b) { return Mathf.Max(f.separate.minM, HalfWidth(a) + HalfWidth(b) + f.separate.padM); }

    static float Flat2(Vector3 v) { v.y = 0f; return v.magnitude; }
    // the one with the larger distance to Joan yields (the queued one); inside yieldMarginM the name order decides, so exactly one of the two moves
    static bool Yields(DuelFighter a, DuelFighter b, Vector3 pa, Vector3 pb, Vector3 pj)
    {
        float ga = Flat2(pa - pj), gb = Flat2(pb - pj), m = f.separate.yieldMarginM;
        if (ga > gb + m) return true; if (ga < gb - m) return false;
        return string.CompareOrdinal(a.name, b.name) > 0;
    }
    // lat = the tangent round Joan as AIBrain defines it (dir.z, -dir.x with dir = self -> Joan); side = +1/-1 = the way that moves self away from the peer
    static bool Tangent(DuelFighter a, DuelFighter b, Vector3 pa, Vector3 pb, Vector3 pj, out Vector3 lat, out float side)
    {
        lat = Vector3.zero; side = 1f; Vector3 dir = pj - pa; dir.y = 0f; float l = dir.magnitude; if (l < 0.01f) return false; dir /= l;
        lat = new Vector3(dir.z, 0f, -dir.x); Vector3 ab = pa - pb; ab.y = 0f; float dot = Vector3.Dot(ab, lat);
        side = Mathf.Abs(dot) > 0.05f ? Mathf.Sign(dot) : (string.CompareOrdinal(a.name, b.name) > 0 ? 1f : -1f);
        return true;
    }
    static DuelFighter Peer(DuelFighter df, DuelFighter j)
    {
        if (j == null || j.Dead) return null; var p = ZoneRuntime.PeerOf(df); return p != null && p.opponent == j ? p : null;
    }

    /// <summary>DuelFighter.MoveTick (after the opponent separation): the rear enemy of a fighting pair side-steps round Joan until the two bodies are minSep apart. Moves only itself, at most maxStepM per tick.</summary>
    public static void Separate(DuelFighter df, CharacterController cc)
    {
        if (!On) return; var j = df.opponent; var peer = Peer(df, j); if (peer == null) return;
        Vector3 pa = df.transform.position, pb = peer.transform.position, pj = j.transform.position; var S = f.separate;
        Vector3 ab = pa - pb; ab.y = 0f; float d = ab.magnitude, minSep = MinSep(df, peer); if (d >= minSep) return;
        if (!Yields(df, peer, pa, pb, pj)) return;
        Vector3 lat; float side; if (!Tangent(df, peer, pa, pb, pj, out lat, out side)) return;
        Vector3 dir = lat * side; if (S.radialShare > 0f && d > 0.001f) { dir = (dir * (1f - S.radialShare) + ab / d * S.radialShare).normalized; }
        cc.Move(dir * Mathf.Min(minSep - d, S.maxStepM));
    }

    static float Angle(Vector3 pa, Vector3 pb, Vector3 pj) { Vector3 ra = pa - pj, rb = pb - pj; ra.y = 0f; rb.y = 0f; return Vector3.Angle(ra, rb); }

    /// <summary>Tangential move input (moveDir units) for the rear enemy while the two are closer than minAngleDeg as seen from Joan. Zero otherwise, and for a single enemy.</summary>
    public static Vector3 Orbit(DuelFighter df, DuelFighter j)
    {
        if (!On) return Vector3.zero; var peer = Peer(df, j); if (peer == null) return Vector3.zero; var S = f.spread;
        Vector3 pa = df.transform.position, pb = peer.transform.position, pj = j.transform.position;
        Vector3 ab = pa - pb; ab.y = 0f; if (ab.magnitude > S.rangeM || !Yields(df, peer, pa, pb, pj)) return Vector3.zero;
        float k = Mathf.Clamp01((S.minAngleDeg - Angle(pa, pb, pj)) / Mathf.Max(1f, S.rampDeg)); if (k <= 0f) return Vector3.zero;
        Vector3 lat; float side; if (!Tangent(df, peer, pa, pb, pj, out lat, out side)) return Vector3.zero;
        return lat * (side * S.orbitGain * k);
    }
    /// <summary>AIBrain.Apply: the Orbit vector when spread.inBrain.</summary>
    public static Vector3 BrainOrbit(DuelFighter df, DuelFighter j) { return On && f.spread.inBrain ? Orbit(df, j) : Vector3.zero; }
    /// <summary>ChaseAgent: bends the heading toward Joan sideways (spread.inChase); the gap rule that hands over to the AI is unchanged, so he still arrives.</summary>
    public static Vector3 ChaseHeading(DuelFighter df, DuelFighter j, Vector3 dir)
    {
        if (!On || !f.spread.inChase) return dir; Vector3 o = Orbit(df, j); if (o.sqrMagnitude <= 0f) return dir; Vector3 h = dir + o; return h.sqrMagnitude > 1e-6f ? h.normalized : dir;
    }
    /// <summary>AIBrain.Decide: the circle side for the rear enemy (+1 / -1 as in s.side), 0 = keep the seeded side.</summary>
    public static int CircleSide(DuelFighter df, DuelFighter j)
    {
        if (!On || !f.spread.aiSide) return 0; var peer = Peer(df, j); if (peer == null) return 0;
        Vector3 pa = df.transform.position, pb = peer.transform.position, pj = j.transform.position;
        if (!Yields(df, peer, pa, pb, pj) || Angle(pa, pb, pj) >= f.spread.minAngleDeg) return 0;
        Vector3 lat; float side; return Tangent(df, peer, pa, pb, pj, out lat, out side) ? (int)side : 0;
    }

    // ---- csv (ZoneCamTrace): distance between two enemies and the angle at Joan ----
    public static float PairGap(Vector3 a, Vector3 b) { return Flat2(a - b); }
    public static float PairAngle(Vector3 a, Vector3 b, Vector3 j) { return Angle(a, b, j); }
    public static void ResetStatics() { f = null; loaded = false; }
}
