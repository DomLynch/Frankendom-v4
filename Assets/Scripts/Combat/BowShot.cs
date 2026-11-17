using System.Collections.Generic;
using UnityEngine;

// BOW v2 (laneBOW, STAGED DARK, UNPROVEN) + K24 (laneLG, file-only, NOT applied: ballistic look, solid arrow, stuck arrows, sound cues). The arrows. Sim: a shot is launched by BowHooks.Release on the swing's first Active tick, flies speedCmS for ceil(range*100*60/speed) ticks (V3 missileTicks) and is judged on ARRIVAL by the
// ordinary Duel.Resolve (so a roll i-frame dodges it, a guard that covers the lane's direction blocks it, a clean hit staggers / adds posture / starts HitPhysics exactly like a melee hit) with Duel.RangedNow set: reach and arc
// are skipped (the arrow homes, V3 missileHoming 1), the finisher is skipped (RTFinisher.TryBegin), and events carry the shooter's swing (Duel.RangedMove). The arrow cannot be parried (parryable false).
// Presentation: BowArrowView interpolates the shot between ticks with Duel.Alpha. K24: the path is a gravity arc with drag (bow.json arrow.gravityMS2 / arcMaxM / dragPerS; the sim time and the hit are unchanged, the arc starts and ends on the same two
// points the straight line did), the arrow is a solid shaft + head + fletching built from the arrow row, a landed arrow stays in the body (parented to the nearest bone) for arrow.stuckSeconds, a dodged one flies on into the ground.
// Everything numeric is bow.json; the view is the only thing that touches Unity objects.
public static class BowShot
{
    public class Shot { public DuelFighter a, d; public MoveDef mv; public int launched, arrive, pinTicks; public Vector3 from; public BowArrowView view; }
    static readonly List<Shot> live = new List<Shot>(8);
    static CueBus bus;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { live.Clear(); bus = null; BowArrowView.ResetStatics(); }

    static MoveDef Clone(MoveDef m)
    {
        return new MoveDef { id = m.id, direction = m.direction, path = m.path, windup = m.windup, active = m.active, recovery = m.recovery, hasChained = false, chainedWindup = m.chainedWindup, chainedActive = m.chainedActive, chainedRecovery = m.chainedRecovery,
            chainWindow = 0, chainFollow = null, damage = m.damage, stamina = 0f, staminaDamage = m.staminaDamage, stagger = m.stagger, poise = m.poise, poiseFrom = m.poiseFrom, chip = m.chip, knockback = m.knockback, stepIn = 0f, reach = 99f, posture = m.posture, chamber = 0f,
            breaksGuard = false, parryable = false, charges = false, hasVsGuard = false, vsGuardStagger = 0f, vsGuardStaminaDamage = 0f };
    }

    /// <summary>K24: play an existing cue id from the CueBus (arrow.thudCue / looseCue). Empty id or gain 0 = silent.</summary>
    public static void PlayCue(string cue, float gain, float rate)
    {
        if (string.IsNullOrEmpty(cue) || gain <= 0f) return;
        if (bus == null) bus = Object.FindFirstObjectByType<CueBus>();
        if (bus != null) bus.Play(cue, gain, 0f, rate <= 0f ? 1f : rate);
    }

    /// <summary>Throw one arrow per lane at the foe. special = the move row's damage is already per arrow and its direction comes from the lane; an ordinary shot keeps the swing's own direction. mul scales the row's damage.</summary>
    public static void Launch(DuelFighter a, DuelFighter d, MoveDef m, int[] lanes, bool special, float mul, int speedCmS, int pinTicks, string tint)
    {
        var C = BowCfg.Get(); float gap = Mathf.Max(0.01f, (d.transform.position - a.transform.position).magnitude);
        int ticks = Mathf.Max(1, Mathf.CeilToInt(gap * 100f * 60f / Mathf.Max(1, speedCmS)));   // V3 missileTicks
        Vector3 from = a.transform.position + Vector3.up * (C.arrow.releaseHeightM * a.scale) + a.transform.forward * C.arrow.releaseForwardM;
        if (!a.IsPlayer) { var bv = a.GetComponent<BowVisual>(); Vector3 np; if (bv != null && bv.NockWorld(out np)) from = np; }   // NPCARCHER v2: the arrow leaves from the nock / right hand, not from a height over the body
        PlayCue(C.arrow.looseCue, C.arrow.looseGain, C.arrow.looseRate);
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
            live.RemoveAt(i); bool hit = Land(s);
            var A = BowCfg.Get().arrow;
            if (s.view == null) continue;
            if (hit && A.stuckSeconds > 0f && (A.stickToPlayer || !s.d.IsPlayer)) { PlayCue(A.thudCue, A.thudGain, A.thudRate); s.view.Embed(s.d); }   // K24: the arrow stays in the body
            else if (!hit && A.stuckSeconds > 0f) s.view.Overfly();                                          // K24: a dodged arrow flies on and sticks in the ground
            else End(s);
        }
    }

    static void End(Shot s) { if (s.view != null) Object.Destroy(s.view.gameObject); }

    static bool Land(Shot s)
    {
        bool hit = false;
        Duel.RangedNow = true; Duel.RangedMove = s.mv;
        try { hit = Duel.Resolve(s.a, s.d, s.mv, 1f); }
        catch (System.Exception ex) { Debug.LogWarning("[bow] Resolve threw on landing: " + ex); }
        finally { Duel.RangedNow = false; Duel.RangedMove = null; }
        // a clean hit (HitReactionTable.Last is set by Resolve only for one) of a pinning arrow holds the foe: V3 pinShot crowd control; pinTicks is 0 for foes that take the posture break only (bow.json foes[].pinMul)
        if (hit && s.pinTicks > 0 && HitReactionTable.Last != null && !s.d.Dead) s.d.Stagger(s.pinTicks, "stagger_loop");
        if (BowCfg.Log) Debug.Log("[bow] arrow " + s.mv.id + " landed tick " + Duel.TickCount + (hit ? " contact" : " MISS (i-frames)") + (hit && HitReactionTable.Last != null ? " clean hit" : ""));
        return hit;
    }
}

// One arrow: solid shaft, head and fletching built from bow.json arrow (sizes, colours, finCount), flown between sim ticks by Duel.Alpha along a gravity arc with drag, then (K24) stuck in the target for arrow.stuckSeconds
// or flown on into the ground. Pure presentation: the sim neither reads nor waits for it. Local +Z = flight direction, origin = shaft centre.
public class BowArrowView : MonoBehaviour
{
    BowShot.Shot s; int lane, count; Vector3 laneOff; float halfTotal; GameObject streakGo; static readonly Dictionary<DuelFighter, List<BowArrowView>> perBody = new Dictionary<DuelFighter, List<BowArrowView>>();
    bool stuck, flying; float until, fadeFrom; Vector3 vel, stuckScale; float flyEnd;

    // ---- shared meshes / materials (built once, cleared on domain reset) ----
    static Mesh shaftM, headM, finM; static string finKey = "";
    static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
    static readonly Queue<BowArrowView> stuckQ = new Queue<BowArrowView>();
    public static void ResetStatics() { shaftM = headM = finM = null; finKey = ""; finNockedM = null; finNockedKey = ""; mats.Clear(); stuckQ.Clear(); perBody.Clear(); }

    static Mesh Cylinder(int seg, bool cone)   // unit radius, z 0..1 for the cone (tip at z 1), z -0.5..0.5 for the cylinder
    {
        var m = new Mesh(); var v = new List<Vector3>(); var t = new List<int>();
        float z0 = cone ? 0f : -0.5f, z1 = cone ? 1f : 0.5f;
        for (int i = 0; i < seg; i++) { float a = i * Mathf.PI * 2f / seg; v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), z0)); v.Add(new Vector3(cone ? 0f : Mathf.Cos(a), cone ? 0f : Mathf.Sin(a), z1)); }
        for (int i = 0; i < seg; i++) { int a = i * 2, b = a + 1, c = ((i + 1) % seg) * 2, d = c + 1; t.Add(a); t.Add(c); t.Add(b); t.Add(c); t.Add(d); t.Add(b); }
        int cb = v.Count; v.Add(new Vector3(0f, 0f, z0)); for (int i = 0; i < seg; i++) { t.Add(cb); t.Add(((i + 1) % seg) * 2); t.Add(i * 2); }   // tail / base cap
        if (!cone) { int ct = v.Count; v.Add(new Vector3(0f, 0f, z1)); for (int i = 0; i < seg; i++) { t.Add(ct); t.Add(i * 2 + 1); t.Add(((i + 1) % seg) * 2 + 1); } }
        m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static Mesh Fins(BowCfg.Arrow C)   // finCount double-sided trapezoids around the tail, taller at the nock end
    {
        var m = new Mesh(); var v = new List<Vector3>(); var t = new List<int>();
        float zBack = -C.lengthM * 0.5f + C.finInsetM, zFront = zBack + C.finLenM, r = C.radiusM, h = C.finHeightM;
        for (int i = 0; i < Mathf.Max(1, C.finCount); i++)
        {
            float a = i * Mathf.PI * 2f / Mathf.Max(1, C.finCount); Vector3 u = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f); int b = v.Count;
            v.Add(u * r + new Vector3(0f, 0f, zBack)); v.Add(u * r + new Vector3(0f, 0f, zFront)); v.Add(u * (r + h * 0.35f) + new Vector3(0f, 0f, zFront)); v.Add(u * (r + h) + new Vector3(0f, 0f, zBack));
            t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3); t.Add(b); t.Add(b + 2); t.Add(b + 1); t.Add(b); t.Add(b + 3); t.Add(b + 2);
        }
        m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static Material Mat(BowCfg.Arrow C, Color c, float smooth, float metal)
    {
        string key = ColorUtility.ToHtmlStringRGBA(c) + "|" + smooth + "|" + metal;
        Material m; if (mats.TryGetValue(key, out m) && m != null) return m;
        Shader sh = null; for (int i = 0; i < C.shaders.Length && sh == null; i++) sh = Shader.Find(C.shaders[i]);
        if (sh == null) return null;
        m = new Material(sh) { name = "BowArrow_" + key };
        if (sh.name.IndexOf("Unlit", System.StringComparison.OrdinalIgnoreCase) >= 0 && C.unlitMul > 0f) { c = new Color(c.r * C.unlitMul, c.g * C.unlitMul, c.b * C.unlitMul, c.a); }   // a lit shader was not found: an unlit one ignores the dark scene and reads as a glowing tracer, so scale the colour down (arrow.unlitMul)
        if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black); if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); if (m.HasProperty("_Color")) m.SetColor("_Color", c); if (m.HasProperty("_UnlitColor")) m.SetColor("_UnlitColor", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth); if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        mats[key] = m; return m;
    }

    GameObject Part(string n, Mesh mesh, Material mat, Vector3 pos, Vector3 scale, bool shadow)
    {
        var g = new GameObject(n); g.transform.SetParent(transform, false); g.transform.localPosition = pos; g.transform.localScale = scale;
        g.AddComponent<MeshFilter>().sharedMesh = mesh; var r = g.AddComponent<MeshRenderer>(); if (mat != null) r.sharedMaterial = mat;
        r.shadowCastingMode = shadow ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off; return g;
    }

    public void Init(BowShot.Shot shot, Color tint, int laneIndex, int laneCount)
    {
        s = shot; lane = laneIndex; count = laneCount; var C = BowCfg.Get().arrow;
        Build(tint);
        laneOff = count > 1 ? new Vector3(lane * C.volleySpreadM, 0f, 0f) : Vector3.zero;   // a volley fans a little so three shafts read as three
        Place();
    }

    /// <summary>Builds the shaft, head and fins (shared by the flying arrow and the arrow nocked on the string).</summary>
    static Mesh finNockedM; static string finNockedKey = "";
    public void Build(Color tint, bool flyingArrow = true, bool nockedView = false)
    {
        var C = BowCfg.Get().arrow;
        float sr = nockedView && C.nockedRadiusM > 0f ? C.nockedRadiusM : C.radiusM, hs = sr / Mathf.Max(1e-4f, C.radiusM);   // a nocked arrow is a slim shaft; its head and fins keep their proportions
        if (shaftM == null) shaftM = Cylinder(Mathf.Max(3, C.radialSegments), false); if (headM == null) headM = Cylinder(Mathf.Max(3, C.radialSegments), true);
        string fk = C.finCount + "|" + C.lengthM + "|" + C.radiusM + "|" + C.finLenM + "|" + C.finHeightM + "|" + C.finInsetM; if (finM == null || finKey != fk) { finM = Fins(C); finKey = fk; }
        Color shaftC = Color.Lerp(BowCfg.Col(C.shaft, Color.gray), tint, C.tintMix);
        halfTotal = C.lengthM * 0.5f + C.headLenM;
        Part("Shaft", shaftM, Mat(C, shaftC, C.shaftSmoothness, 0f), Vector3.zero, new Vector3(sr, sr, C.lengthM), C.castShadow);
        Part("Head", headM, Mat(C, BowCfg.Col(C.head, Color.gray), C.headSmoothness, C.headMetallic), new Vector3(0f, 0f, C.lengthM * 0.5f), new Vector3(C.headRadiusM * hs, C.headRadiusM * hs, C.headLenM), C.castShadow);
        Mesh finUse = finM;
        if (nockedView && C.nockedRadiusM > 0f)
        {   // real fletching on the nocked arrow: three vanes at the slim shaft's tail, height / length from nockedFinHeightM / nockedFinLenM (0 = the flight values)
            string nk = fk + "|n" + sr + "|" + C.nockedFinHeightM + "|" + C.nockedFinLenM;
            if (finNockedM == null || finNockedKey != nk) { var C2 = JsonUtility.FromJson<BowCfg.Arrow>(JsonUtility.ToJson(C)); C2.radiusM = sr; if (C.nockedFinHeightM > 0f) C2.finHeightM = C.nockedFinHeightM; if (C.nockedFinLenM > 0f) C2.finLenM = C.nockedFinLenM; finNockedM = Fins(C2); finNockedKey = nk; }
            finUse = finNockedM;
        }
        Part("Fins", finUse, Mat(C, BowCfg.Col(!string.IsNullOrEmpty(C.fletchColor) ? C.fletchColor : C.fin, Color.gray), C.shaftSmoothness, 0f), Vector3.zero, Vector3.one, C.castShadow);
        if (flyingArrow && C.streakLenM > 0f) streakGo = Part("Streak", shaftM, Mat(C, BowCfg.Col(C.streakColor, Color.white), 0.1f, 0f), new Vector3(0f, 0f, -(C.lengthM * 0.5f + C.streakLenM * 0.5f)), new Vector3(C.radiusM * 0.7f, C.radiusM * 0.7f, C.streakLenM), false);
    }

    /// <summary>The arrow on the string during a draw: a static view (no Shot) parented to the bow, local +Z = the way it will fly.</summary>
    public static GameObject MakeNocked(Transform parent)
    {
        var g = new GameObject("BowArrow_nocked"); g.transform.SetParent(parent, false);
        var v = g.AddComponent<BowArrowView>(); v.stuck = true; v.until = float.MaxValue; v.fadeFrom = float.MaxValue; v.Build(BowCfg.Col(BowCfg.Get().arrow.shaft, Color.gray), false, true); return g;
    }

    void LateUpdate() { if (stuck || flying || s == null || s.a == null || s.d == null) return; Place(); }

    // ---- flight: tip position at progress p (0..1 of the sim time) ----
    float Span { get { return Mathf.Max(1f, s.arrive - s.launched); } }
    Vector3 Aim() { return s.d.transform.position + Vector3.up * (BowCfg.Get().arrow.aimHeightM * s.d.scale); }   // homing: where the foe stands NOW, as the sim judges it
    Vector3 TipAt(float p)
    {
        var C = BowCfg.Get().arrow; float T = Span / 60f; Vector3 to = Aim();
        float k = C.dragPerS * T, w = k > 1e-4f ? (1f - Mathf.Exp(-k * p)) / (1f - Mathf.Exp(-k)) : p;   // drag: fast off the string, slower late; w(0)=0, w(1)=1
        Vector3 pos = Vector3.Lerp(s.from, to, w);
        float peak = Mathf.Min(C.gravityMS2 * T * T / 8f, C.arcMaxM); pos.y += 4f * peak * p * (1f - p);   // 0.5 g T^2 p (1-p), capped: same two end points as the straight line
        Vector3 dir = to - s.from; dir.y = 0f; if (dir.sqrMagnitude > 1e-6f) { Vector3 right = Vector3.Cross(Vector3.up, dir.normalized); pos += right * laneOff.x * Mathf.Sin(p * Mathf.PI); }
        return pos;
    }
    float P() { return Mathf.Clamp01((Duel.TickCount - s.launched + Duel.Alpha) / Span); }

    void Pose(Vector3 tip, Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-8f) dir = transform.forward; dir.Normalize();
        transform.rotation = Quaternion.LookRotation(dir, Vector3.up); transform.position = tip - dir * halfTotal;
    }

    void Place()
    {
        float p = P(), e = 0.02f; Vector3 tip = TipAt(p); Vector3 dir = TipAt(Mathf.Min(1f, p + e)) - TipAt(Mathf.Max(0f, p - e));
        Pose(tip, dir);
    }

    // ---- landed: stays in the body ----
    public void Embed(DuelFighter d)
    {
        var C = BowCfg.Get().arrow; Vector3 tip = TipAt(1f), dir = (TipAt(1f) - TipAt(0.96f)); if (dir.sqrMagnitude < 1e-8f) dir = transform.forward; dir.Normalize(); if (C.stuckLevel > 0f) { dir.y *= Mathf.Clamp01(1f - C.stuckLevel); if (dir.sqrMagnitude < 1e-8f) dir = transform.forward; dir.Normalize(); }   // stuckLevel: the arrow points back along the shot
        var rnd = new System.Random(lane * 7919 + Duel.TickCount); Vector3 side = Vector3.Cross(dir, Vector3.up); if (side.sqrMagnitude < 1e-6f) side = Vector3.right; side.Normalize();
        tip += side * ((float)rnd.NextDouble() * 2f - 1f) * C.stuckScatterM + Vector3.up * ((float)rnd.NextDouble() * 2f - 1f) * C.stuckScatterM + dir * C.embedM;
        Pose(tip, dir); stuck = true;
        Transform bone = NearestBone(d, transform.position + dir * halfTotal * 0.5f); transform.SetParent(bone != null ? bone : d.transform, true);
        stuckScale = transform.localScale; until = Time.time + C.stuckSeconds; fadeFrom = Mathf.Max(0f, C.stuckSeconds - C.stuckFadeS);
        while (stuckQ.Count >= Mathf.Max(1, C.maxStuck)) { var o = stuckQ.Dequeue(); if (o != null) Destroy(o.gameObject); }
        stuckQ.Enqueue(this); s = null;
        if (streakGo != null) { Destroy(streakGo); streakGo = null; }
        if (C.maxStuckPerBody > 0 && d != null)
        {
            List<BowArrowView> L; if (!perBody.TryGetValue(d, out L)) { L = new List<BowArrowView>(); perBody[d] = L; }
            L.RemoveAll(x => x == null); L.Add(this); while (L.Count > C.maxStuckPerBody) { var o = L[0]; L.RemoveAt(0); if (o != null) Destroy(o.gameObject); }
        }
    }

    static Transform NearestBone(DuelFighter d, Vector3 at)
    {
        Transform best = null; float bd = float.MaxValue;
        foreach (var smr in d.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            foreach (var b in smr.bones) { if (b == null) continue; float q = (b.position - at).sqrMagnitude; if (q < bd) { bd = q; best = b; } }
        return best;
    }

    // ---- dodged: flies on under gravity until the ground (or aim.stuckSeconds-bounded time) ----
    public void Overfly()
    {
        var C = BowCfg.Get().arrow; float T = Span / 60f; Vector3 a = TipAt(0.96f), b = TipAt(1f); vel = (b - a) / (0.04f * T);
        Vector3 dir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward; flying = true; flyEnd = Time.time + Mathf.Max(1f, C.stuckSeconds * 0.1f + 1.5f); s = null;
        Pose(b, dir);
    }

    void Update()
    {
        var C = BowCfg.Get().arrow;
        if (stuck)
        {
            if (Time.time >= until) { Destroy(gameObject); return; }
            if (Time.time > fadeFrom && C.stuckFadeS > 0.01f) transform.localScale = stuckScale * Mathf.Clamp01((until - Time.time) / C.stuckFadeS);   // shrinks away, no transparent pass
            return;
        }
        if (!flying) return;
        float dt = Time.deltaTime; vel.y -= C.gravityMS2 * dt; Vector3 step = vel * dt, tip = transform.position + transform.forward * halfTotal; float len = step.magnitude;
        RaycastHit h;
        if (len > 1e-5f && Physics.Raycast(tip, step / len, out h, len + C.embedM, ~0, QueryTriggerInteraction.Ignore) && h.collider.GetComponentInParent<DuelFighter>() == null)
        {
            Vector3 dir = step / len; Pose(h.point + dir * C.embedM, dir); flying = false; stuck = true; transform.SetParent(h.collider.transform, true);
            stuckScale = transform.localScale; until = Time.time + C.stuckSeconds; fadeFrom = Mathf.Max(0f, C.stuckSeconds - C.stuckFadeS);
            while (stuckQ.Count >= Mathf.Max(1, C.maxStuck)) { var o = stuckQ.Dequeue(); if (o != null) Destroy(o.gameObject); }
            stuckQ.Enqueue(this); return;
        }
        if (len > 1e-5f) Pose(tip + step, step);
        if (Time.time >= flyEnd) Destroy(gameObject);
    }
}
