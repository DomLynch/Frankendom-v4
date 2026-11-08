using System.Collections.Generic;
using UnityEngine;

// BOW v2 (laneBOW, STAGED DARK, UNPROVEN). The arrows. Sim: a shot is launched by BowHooks.Release on the swing's first Active tick, flies speedCmS for ceil(range*100*60/speed) ticks (V3 missileTicks) and is judged on ARRIVAL by the
// ordinary Duel.Resolve (so a roll i-frame dodges it, a guard that covers the lane's direction blocks it, a clean hit staggers / adds posture / starts HitPhysics exactly like a melee hit) with Duel.RangedNow set: reach and arc
// are skipped (the arrow homes, V3 missileHoming 1), the finisher is skipped (RTFinisher.TryBegin), and events carry the shooter's swing (Duel.RangedMove). The arrow cannot be parried (parryable false).
// Presentation: BowArrowView interpolates the shot between ticks with Duel.Alpha. Everything numeric is bow.json; the view is the only thing that touches Unity objects.
public static class BowShot
{
    public class Shot { public DuelFighter a, d; public MoveDef mv; public int launched, arrive, pinTicks; public Vector3 from; public BowArrowView view; }
    static readonly List<Shot> live = new List<Shot>(8);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { live.Clear(); }

    static MoveDef Clone(MoveDef m)
    {
        return new MoveDef { id = m.id, direction = m.direction, path = m.path, windup = m.windup, active = m.active, recovery = m.recovery, hasChained = false, chainedWindup = m.chainedWindup, chainedActive = m.chainedActive, chainedRecovery = m.chainedRecovery,
            chainWindow = 0, chainFollow = null, damage = m.damage, stamina = 0f, staminaDamage = m.staminaDamage, stagger = m.stagger, poise = m.poise, poiseFrom = m.poiseFrom, chip = m.chip, knockback = m.knockback, stepIn = 0f, reach = 99f, posture = m.posture, chamber = 0f,
            breaksGuard = false, parryable = false, charges = false, hasVsGuard = false, vsGuardStagger = 0f, vsGuardStaminaDamage = 0f };
    }

    /// <summary>Throw one arrow per lane at the foe. special = the move row's damage is already per arrow and its direction comes from the lane; an ordinary shot keeps the swing's own direction. mul scales the row's damage.</summary>
    public static void Launch(DuelFighter a, DuelFighter d, MoveDef m, int[] lanes, bool special, float mul, int speedCmS, int pinTicks, string tint)
    {
        var C = BowCfg.Get(); float gap = Mathf.Max(0.01f, (d.transform.position - a.transform.position).magnitude);
        int ticks = Mathf.Max(1, Mathf.CeilToInt(gap * 100f * 60f / Mathf.Max(1, speedCmS)));   // V3 missileTicks
        Vector3 from = a.transform.position + Vector3.up * (C.arrow.releaseHeightM * a.scale) + a.transform.forward * C.arrow.releaseForwardM;
        for (int i = 0; i < lanes.Length; i++)
        {
            var mv = Clone(m); mv.damage = m.damage * mul;
            if (special || lanes.Length > 1) mv.direction = BowCfg.DirectionOf(lanes[i]);
            var s = new Shot { a = a, d = d, mv = mv, launched = Duel.TickCount, arrive = Duel.TickCount + ticks, pinTicks = i == 0 ? pinTicks : 0, from = from };
            var go = new GameObject("BowArrow"); s.view = go.AddComponent<BowArrowView>(); s.view.Init(s, BowCfg.Col(tint, Color.white), lanes[i], lanes.Length);
            live.Add(s);
        }
    }

    /// <summary>One 60 Hz tick (BowHooks.SimTick): land every shot whose time has come.</summary>
    public static void Step(int tick)
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var s = live[i];
            if (s.a == null || s.d == null || s.d.Dead) { End(s); live.RemoveAt(i); continue; }
            if (tick < s.arrive) continue;
            if (Duel.FrozenFor(s.d) || Duel.FrozenFor(s.a)) { s.arrive++; continue; }   // a finisher / menu holds the sim: the arrow hangs until it ends
            live.RemoveAt(i); Land(s); End(s);
        }
    }

    static void End(Shot s) { if (s.view != null) Object.Destroy(s.view.gameObject); }

    static void Land(Shot s)
    {
        bool hit = false;
        Duel.RangedNow = true; Duel.RangedMove = s.mv;
        try { hit = Duel.Resolve(s.a, s.d, s.mv, 1f); }
        catch (System.Exception ex) { Debug.LogWarning("[bow] Resolve threw on landing: " + ex); }
        finally { Duel.RangedNow = false; Duel.RangedMove = null; }
        // a clean hit (HitReactionTable.Last is set by Resolve only for one) of a pinning arrow holds the foe: V3 pinShot crowd control; pinTicks is 0 for foes that take the posture break only (bow.json foes[].pinMul)
        if (hit && s.pinTicks > 0 && HitReactionTable.Last != null && !s.d.Dead) s.d.Stagger(s.pinTicks, "stagger_loop");
        if (BowCfg.Log) Debug.Log("[bow] arrow " + s.mv.id + " landed tick " + Duel.TickCount + (hit ? " contact" : " MISS (i-frames)") + (hit && HitReactionTable.Last != null ? " clean hit" : ""));
    }
}

// One arrow in the air: a thin shaft, a head and fins (V1 arrow model), moved between sim ticks by Duel.Alpha. Pure presentation.
public class BowArrowView : MonoBehaviour
{
    BowShot.Shot s; int lane, count; Transform shaft; Vector3 laneOff;

    public void Init(BowShot.Shot shot, Color tint, int laneIndex, int laneCount)
    {
        s = shot; lane = laneIndex; count = laneCount; var C = BowCfg.Get().arrow;
        Shader sh = null; for (int i = 0; i < C.shaders.Length && sh == null; i++) sh = Shader.Find(C.shaders[i]);
        Material mat = sh != null ? new Material(sh) : null; if (mat != null) { mat.color = Color.Lerp(BowCfg.Col(C.shaft, Color.gray), tint, 0.35f); }
        var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder); Destroy(g.GetComponent<Collider>()); g.name = "Shaft"; shaft = g.transform; shaft.SetParent(transform, false);
        shaft.localRotation = Quaternion.Euler(90f, 0f, 0f); shaft.localScale = new Vector3(C.radiusM * 2f, C.lengthM * 0.5f, C.radiusM * 2f);   // cylinder is 2 m tall on Y, turned onto Z
        var r = g.GetComponent<Renderer>(); if (mat != null) r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        laneOff = count > 1 ? new Vector3(lane * C.volleySpreadM, 0f, 0f) : Vector3.zero;   // a volley fans a little so three shafts read as three
        Place();
    }

    void LateUpdate() { if (s == null || s.a == null || s.d == null) return; Place(); }

    void Place()
    {
        var C = BowCfg.Get().arrow; float span = Mathf.Max(1f, s.arrive - s.launched);
        float p = Mathf.Clamp01((Duel.TickCount - s.launched + Duel.Alpha) / span);
        Vector3 to = s.d.transform.position + Vector3.up * (C.aimHeightM * s.d.scale);   // homing: the arrow aims at where the foe stands NOW, as the sim judges it
        Vector3 pos = Vector3.Lerp(s.from, to, p), dir = (to - s.from); if (dir.sqrMagnitude < 1e-6f) dir = transform.forward;
        transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.position = pos + transform.TransformVector(laneOff) * Mathf.Sin(p * Mathf.PI);
    }
}
