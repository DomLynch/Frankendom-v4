using System;
using UnityEngine;

// KNOCKBACK v1 (lane KNOCKBACK): a short decaying push of the VICTIM'S ROOT along the hit direction, like Frankendom v3. SIM state: it only calls DuelFighter.SetKnock (the existing 60 Hz
// knock slide, consumed in Stagger/Down by DuelFighter.MoveTick through the CharacterController, so zone colliders already stop it) and never touches the pose.
// Data: hit_physics.json "knockback" (HpCfg.knockback). enabled=false (the default, also with the block or file missing) = Apply returns at its first line, Decay is the old 0.88 literal: byte-identical.
// distance = classes[class].dist x (move.knockback / refKnockbackMps) x attacker scale x massScale(victim) x resist(victim) x poise knockMul, capped at maxM, then cut to the room left (arena rectangle, play area, stair width, wall, ground).
[Serializable] public class KbClass { public string name = ""; public float dist = 0f; }
[Serializable] public class KbResist { public string id = ""; public float mul = 1f; }
[Serializable]
public class KbCfg
{
    public bool enabled = false, skipShove = true, clampArena = true, clampWalls = true, clampGround = true, log = false;
    public float refKnockbackMps = 4f, decay = 0.88f, maxM = 1.2f, refMassKg = 66.7f, massExponent = 0.5f, minMassScale = 0.3f, maxMassScale = 1.6f;
    public float probeSkinM = 0.05f, probeStepM = 0.1f, probeUpM = 0.5f, probeDropM = 0.6f, probeChestM = 0.9f, minPushM = 0.02f;
    public int minSlideTicks = 6;
    public string defaultClass = "light", defaultResist = "*";
    public KbClass[] classes = new KbClass[0]; public KbResist[] resist = new KbResist[0];
}

public static class HitKnockback
{
    static KbCfg Cfg { get { var h = HitPhysics.Cfg; if (h.knockback == null) h.knockback = new KbCfg(); if (h.knockback.classes == null) h.knockback.classes = new KbClass[0]; if (h.knockback.resist == null) h.knockback.resist = new KbResist[0]; return h.knockback; } }
    /// <summary>Per-tick factor of the knock slide (DuelFighter.MoveTick used the literal 0.88).</summary>
    public static float Decay { get { return Mathf.Clamp(Cfg.decay, 0.05f, 0.995f); } }   // hc:ok sanity bounds on a data value (0 or 1 would break the slide maths)

    static float ClassDist(KbCfg c, string name)
    {
        float dflt = 0f;
        for (int i = 0; i < c.classes.Length; i++) { var k = c.classes[i]; if (k == null) continue; if (k.name == name) return k.dist; if (k.name == c.defaultClass) dflt = k.dist; }
        return dflt;
    }
    static float Resist(KbCfg c, string id)
    {
        float dflt = 1f;
        for (int i = 0; i < c.resist.Length; i++) { var r = c.resist[i]; if (r == null) continue; if (!string.IsNullOrEmpty(id) && r.id == id) return r.mul; if (r.id == c.defaultResist) dflt = r.mul; }
        return dflt;
    }
    static float MassScale(KbCfg c, string id)
    {
        float m = HitPhysics.Fighter(id).massKg; if (m <= 0f) m = c.refMassKg;
        return Mathf.Clamp(Mathf.Pow(c.refMassKg / Mathf.Max(Mathf.Epsilon, m), c.massExponent), c.minMassScale, c.maxMassScale);
    }

    /// <summary>Called from Duel.Resolve right after the legacy d.SetKnock on a clean hit (stg = stagger ticks before the clip scale). Replaces the victim's knock velocity so the slide covers `want` metres.</summary>
    public static void Apply(DuelFighter a, DuelFighter d, MoveDef m, Vector3 away, bool heavy, bool counter, float stg, float staggerScale, float knockMul)
    {
        var c = Cfg; if (!c.enabled || a == null || d == null || m == null || knockMul <= 0f) return;
        if (c.skipShove && ShoveTable.IsMove(m.id)) return;                   // a shove has its own pushToM maths
        away.y = 0f; float len = away.magnitude; if (len < 0.001f) return; Vector3 dir = away / len;   // hc:ok degenerate-direction guard
        string id = d.CharacterId;
        float want = ClassDist(c, counter ? "counter" : (heavy ? "heavy" : "light")) * (m.knockback / Mathf.Max(0.001f, c.refKnockbackMps)) * a.scale * MassScale(c, id) * Resist(c, id) * knockMul;   // hc:ok divide guard
        want = Mathf.Min(want, c.maxM);
        float room = Room(c, d, dir, want); if (room < want) want = room;
        if (want < c.minPushM) { d.SetKnock(Vector3.zero); return; }
        int n = Mathf.Max(c.minSlideTicks, (int)(stg * staggerScale)); float k = Decay;
        float v0 = want * 60f * (1f - k) / (1f - Mathf.Pow(k, n));              // same slide maths as ShoveTable.ApplyKnock: sum over n ticks of v0*k^i/60 = want
        d.SetKnock(dir * v0);
        if (c.log) Debug.Log("[knockback] " + d.name + " pushed " + want.ToString("0.00") + " m (room " + room.ToString("0.00") + ", v0 " + v0.ToString("0.0") + " m/s, " + n + " ticks)");
    }

    /// <summary>How far the victim can slide along dir before the arena rectangle / play area / stair width, a wall, or missing ground. Probes run once per landed hit.</summary>
    static float Room(KbCfg c, DuelFighter d, Vector3 dir, float want)
    {
        float room = want; Vector3 p = d.transform.position;
        if (c.clampArena) room = Mathf.Min(room, ArenaLimits.RoomAlong(p, dir, want));
        var cc = d.GetComponent<CharacterController>();
        if (c.clampWalls && cc != null && room > 0f)
        {
            RaycastHit h;
            if (Physics.SphereCast(p + Vector3.up * c.probeChestM, cc.radius, dir, out h, room + c.probeSkinM, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) room = Mathf.Min(room, Mathf.Max(0f, h.distance - c.probeSkinM));
        }
        if (c.clampGround && room > 0f)
            for (float s = c.probeStepM; s <= room + 0.0001f; s += c.probeStepM)   // hc:ok epsilon
                if (!Physics.Raycast(p + dir * s + Vector3.up * c.probeUpM, Vector3.down, c.probeUpM + c.probeDropM, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) { room = Mathf.Max(0f, s - c.probeStepM); break; }   // no floor there (gap / edge): stop one step short
        return room;
    }
}
