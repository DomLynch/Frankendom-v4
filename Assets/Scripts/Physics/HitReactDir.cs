using System;
using UnityEngine;

// HITDIR v1 (lane HITDIR): the blow's DIRECTION in the victim's own frame (left / right / up / down / thrust) shapes a small PARTIAL reaction: torso twist + lean, head snap, near-arm flinch,
// shoulder sway, as a joint-target OFFSET (degrees, data) on top of the clip pose that the HitReact proxy chases, then returns to the animation. PRESENTATION ONLY (same contract as HitReact.cs):
// reads CombatEvent (struct copy), the attacker's Transform (to turn the move's local blow vector into world), DuelFighter.CharacterId; writes only HitReact's own Reaction / proxy targets. No sim state, no Random, no collider.
// Data: hit_physics.json "hitDir" (StreamingAssets + Assets/Data, identical). enabled=false (default) / block missing = HitReact exactly as before (HitDirStart returns at the first line, HitDirRel returns its input).
// Blow vector convention (attacker-local): x = to the attacker's RIGHT, y = up, z = forward. A light_right cut (clip: hand R-to-L) travels x = -1. It is turned into the victim frame: +x = toward the victim's right.

[Serializable] public class HdMove { public string id = "", contains = "", character = ""; public float x, y, z; }
[Serializable] public class HdPart { public string part = "", side = ""; public float yawLat, rollLat, pitchVert, pitchDepth, blend = 1f; }   // degrees per unit of lateral / vertical / depth; side: "" | "toward" | "away" (the side the blow travels toward)
[Serializable] public class HdClass { public string name = ""; public float mul = 1f, timeMul = 1f; }
[Serializable] public class HdFighter { public string id = ""; public float mul = 1f; }
[Serializable]
public class HdCfg
{
    public bool enabled, log; public float easeS = 0.06f, holdS = 0.14f, backS = 0.22f, maxDeg = 25f, refMassKg = 66.7f, massExponent = 0.25f, minMassMul = 0.35f, maxMassMul = 1f, maxPunchMul = 1.5f, flipLateral = 1f, minComponent = 0.02f;
    public Vector3 leftBlow = new Vector3(1f, 0f, 0.2f), rightBlow = new Vector3(-1f, 0f, 0.2f); public HdMove[] moves = new HdMove[0]; public HdPart[] parts = new HdPart[0]; public HdClass[] classes = new HdClass[0]; public HdFighter[] fighters = new HdFighter[0];
    public static void Fix(HdCfg c)
    {
        if (c.moves == null) c.moves = new HdMove[0]; if (c.parts == null) c.parts = new HdPart[0]; if (c.classes == null) c.classes = new HdClass[0]; if (c.fighters == null) c.fighters = new HdFighter[0];
    }
    /// <summary>Attacker-local blow vector: exact id (with the attacker's character first), then a substring row, then the event's own left / right word, then a plain thrust.</summary>
    public Vector3 Blow(CombatEvent e, string attackerId)
    {
        string id = e.moveId ?? "";
        for (int i = 0; i < moves.Length; i++) { var m = moves[i]; if (m != null && m.id.Length > 0 && m.id == id && m.character.Length > 0 && m.character == attackerId) return new Vector3(m.x, m.y, m.z); }
        for (int i = 0; i < moves.Length; i++) { var m = moves[i]; if (m != null && m.id.Length > 0 && m.id == id && m.character.Length == 0) return new Vector3(m.x, m.y, m.z); }
        for (int i = 0; i < moves.Length; i++) { var m = moves[i]; if (m != null && m.contains.Length > 0 && id.IndexOf(m.contains, StringComparison.Ordinal) >= 0) return new Vector3(m.x, m.y, m.z); }
        if (e.direction == "left") return leftBlow;
        if (e.direction == "right") return rightBlow;
        return new Vector3(0f, 0f, 1f);
    }
    public HdClass Class(string name) { for (int i = 0; i < classes.Length; i++) if (classes[i] != null && classes[i].name == name) return classes[i]; return null; }
    public float FighterMul(string id)
    {
        for (int i = 0; i < fighters.Length; i++) if (fighters[i] != null && fighters[i].id == id) return fighters[i].mul;
        for (int i = 0; i < fighters.Length; i++) if (fighters[i] != null && fighters[i].id == "*") return fighters[i].mul;
        return 1f;
    }
}

public partial class HitReact
{
    Reaction hdR; float[] hdYaw, hdRoll, hdPitch;
    public string HdLast = ""; public float HdLatLast, HdVertLast, HdScaleLast;   // proof / debug: the last blow in the victim frame

    /// <summary>After StartRow in TriggerHit: add the directional offset reaction for this blow.</summary>
    void HitDirStart(CombatEvent e)
    {
        var c = HitPhysics.Cfg.hitDir; if (c == null || !c.enabled || e.actor == null || df == null) return;
        Vector3 w = e.actor.transform.TransformDirection(c.Blow(e, e.actor.CharacterId));
        Vector3 v = transform.InverseTransformDirection(w);
        if (v.sqrMagnitude < 1e-6f) return;
        v.Normalize();
        float lat = v.x * c.flipLateral;
        var af = HitPhysics.Fighter(e.actor.CharacterId); var vf = HitPhysics.Fighter(df.CharacterId);
        string cn = ClassName(e); var cl = c.Class(cn); float cm = cl != null ? cl.mul : 1f, tm = cl != null ? cl.timeMul : 1f;
        float mass = vf.massKg > 0f ? vf.massKg : c.refMassKg;
        float k = cm * c.FighterMul(df.CharacterId) * Mathf.Clamp(Mathf.Pow(c.refMassKg / Mathf.Max(1f, mass), c.massExponent), c.minMassMul, c.maxMassMul) * Mathf.Min(c.maxPunchMul, Mathf.Max(0f, af.punch));
        if (hdYaw == null || hdYaw.Length != n) { hdYaw = new float[n]; hdRoll = new float[n]; hdPitch = new float[n]; }
        Array.Clear(hdYaw, 0, n); Array.Clear(hdRoll, 0, n); Array.Clear(hdPitch, 0, n);
        var r = new Reaction { tag = "hitdir", ease = Mathf.Max(0f, c.easeS), hold = Mathf.Max(0f, c.holdS * tm), back = Mathf.Max(0f, c.backS * tm), dipMul = 1f, blendMul = 1f };
        r.dip = new float[n]; r.share = new float[n];
        for (int i = 0; i < n; i++) r.dip[i] = 1f;   // stiff: the offset is a TARGET, the spring chases it, share (= blend x depth) decides how much of it shows
        char toward = lat >= 0f ? 'R' : 'L'; bool any = false;
        for (int p = 0; p < c.parts.Length; p++)
        {
            var q = c.parts[p]; if (q == null || string.IsNullOrEmpty(q.part)) continue;
            string spec = q.side == "toward" ? q.part + "$near" : (q.side == "away" ? q.part + "$far" : q.part);
            int idx = ResolvePart(spec, toward); if (idx <= 0) continue;
            float yaw = Mathf.Clamp(q.yawLat * lat * k, -c.maxDeg, c.maxDeg), roll = Mathf.Clamp(q.rollLat * lat * k, -c.maxDeg, c.maxDeg);
            float pitch = Mathf.Clamp((q.pitchVert * -v.y + q.pitchDepth * v.z) * k, -c.maxDeg, c.maxDeg);
            if (Mathf.Abs(yaw) + Mathf.Abs(roll) + Mathf.Abs(pitch) < c.minComponent) continue;
            hdYaw[idx] += yaw; hdRoll[idx] += roll; hdPitch[idx] += pitch; r.share[idx] = Mathf.Max(r.share[idx], Mathf.Clamp01(q.blend)); any = true;
        }
        HdLast = e.moveId; HdLatLast = lat; HdVertLast = v.y; HdScaleLast = k;
        if (!any) return;
        AddReaction(r); hdR = r;
        if (c.log) Debug.Log("[hitdir] " + name + " <- " + e.moveId + " lat " + lat.ToString("F2") + " vert " + v.y.ToString("F2") + " depth " + v.z.ToString("F2") + " scale " + k.ToString("F2"));
    }

    /// <summary>Feed hook: the clip rotation of part i plus the directional offset (a rotation about the victim's up / forward / right axes, expressed in the part's parent frame). Input unchanged when off or finished.</summary>
    Quaternion HitDirRel(int i, Quaternion rel)
    {
        if (hdR == null || hdYaw == null || i <= 0 || i >= hdYaw.Length) return rel;
        float y = hdYaw[i], ro = hdRoll[i], pt = hdPitch[i];
        if (y == 0f && ro == 0f && pt == 0f) return rel;
        if (!reactions.Contains(hdR)) return rel;
        Quaternion R = Quaternion.AngleAxis(y, transform.up) * Quaternion.AngleAxis(ro, transform.forward) * Quaternion.AngleAxis(pt, transform.right);
        Quaternion par = animRot[pi[i]];
        return Quaternion.Inverse(par) * R * par * rel;
    }
}
