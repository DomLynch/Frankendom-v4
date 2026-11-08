using System;
using UnityEngine;

// laneHITFX2 v1 (HITFX2): per-hit-type impact FEEDBACK, one data table (hit_fx.json), presentation only. No sim state is read or written, no sim RNG, no allocation per event or per frame
// (rows, paths and particle pools are built once; one event = a few array scans and Emit calls on pre-made systems).
//  Hook: ContactRelay.OnEvent (the SAME event HitFeel, CamShake, CameraImpact, BloodBurst and the body-physics lane use; nothing new is raised).
//  A row = contact type x weapon x move direction x weight x surface x kill. Most specific row wins (moves 16, weapons 8, dirs 4, kill 2, heavy 1, metal 1; later row wins a tie).
//  What a row drives:
//   stopMs    hit-stop ms (clamped stopMinMs..stopMaxMs, 40..90). HitFeel.cs asks HitFx.StopMs(...) on the line it already had; 0 / no row = HitFeel's own tier value.
//             hitStopMode "presentation" (default): the sim is NOT paused. Only the hitter's and the victim's Animator.speed dips to animDipMul for that long (Fighter.ApplyPresDip applies HitFx.PresMul),
//             then plays at catchUpMul until the lost animation time is recovered, so the visual does not drift. "sim" = the old global PresentationClock stop.
//   shakeMul  x the CamShake pulse (CamShake.OnEvent asks HitFx.ShakeMul), on top of camera_impact.json classes and ImpactTable.MoveMul.
//   sparkMul  x HitFeel's HDR spark burst strength; flashMul x its flash light (HitFeel.OnEvent asks HitFx.SparkMul / FlashMul). Sparks stay on steel / guard contact only (hit_feel.json rows).
//   blood     directional dark blood on a landed hit on FLESH: "spray" = droplets along the blade path (slashes, chops, mauls, claws), "puff" = a short forward puff (stabs, thrusts).
//             Count x blood.json (weight x location x victim). Colour = hit_feel.json blood.startColor / endColor (dark, non glowing). Skipped on metal victims and while a finisher owns the scene.
//   trail     a very short pale streak along the blade path at the contact. DARK (trail.on false): weapon trails were removed by Dom (AGENTS.md), needs his LOOK OK.
//  Sound: not played here. cues.json already picks the clip by weapon x surface x weight x move; the kit adds the missing stab / maul-on-armour / claws-on-armour rows there (note HITFX2).
public static class HitFxTable
{
    [Serializable] public class Switches { public int stop = 1, shake = 1, spark = 1, blood = 1, trail = 0; }
    [Serializable] public class Path { public string dir = ""; public float side = 0f, down = 0f, fwd = 0f; }   // blade travel in the attacker frame: side +1 = attacker's right, down +1 = straight down, fwd +1 = toward the victim
    [Serializable] public class Pool
    {
        public int sprayMax = 160, puffMax = 80, trailMax = 48; public float minIntervalS = 0.03f;
        public float sprayLengthScale = 1.6f, sprayVelocityScale = 0.02f, sprayMaxSize = 0.5f, gravity = 8f;   // gravity m/s^2 (gravityModifier = gravity / 9.81)
        public float puffGravity = 5f, puffMaxSize = 0.08f;
    }
    [Serializable] public class TrailCfg { public float[] color = { 0.72f, 0.74f, 0.78f }; public float lengthScale = 6f, velocityScale = 0.03f, maxSize = 0.3f, startFrac = 0.2f, jitterM = 0.03f, alphaMid = 0.6f; }
    [Serializable] public class Score { public int moves = 16, weapons = 8, dirs = 4, kill = 2, heavy = 1, metal = 1; }   // row specificity weights
    [Serializable] public class Fade { public float midAt = 0.6f, sprayAlphaMid = 0.9f, puffAlphaMid = 0.9f; }   // colour/alpha over life
    [Serializable] public class Rng { public int mul = 1664525, add = 1013904223, seed = 90210; public float scale = 4294967296f; }   // presentation-only LCG
    [Serializable] public class Roles { public string spray = "bloodStrands", puff = "blood", trail = "dust", softDistanceProp = "_SoftDistance"; }   // fx_materials.json roles + shader property
    [Serializable]
    public class Blood
    {
        public string mode = "none";            // none | spray | puff
        public int count = 0;
        public float speedMin = 2f, speedMax = 4f, coneDeg = 12f, lifeMin = 0.3f, lifeMax = 0.55f, sizeMin = 0.02f, sizeMax = 0.03f;
        public float pathMix = 1f, fwdMix = 0.3f, upBias = 0.1f;
    }
    [Serializable] public class Trail { public int count = 0; public float lengthM = 0.35f, speed = 4f, life = 0.07f, width = 0.02f, alpha = 0.25f; }
    [Serializable]
    public class Row
    {
        public string id = "", type = "Hit";
        public string[] weapons = new string[0], dirs = new string[0], moves = new string[0];   // empty = any
        public int heavy = -1, metal = -1, kill = -1;                                              // -1 any
        public float stopMs = 0f, shakeMul = 1f, sparkMul = 1f, flashMul = 1f;
        public Blood blood = new Blood(); public Trail trail = new Trail();
    }
    [Serializable]
    public class Data
    {
        public int version = 1, enabled = 1, suppressInFinisher = 1; public float stopMinMs = 40f, stopMaxMs = 90f;
        public string hitStopMode = "presentation";   // presentation = only the hitter's and victim's Animator dips, the sim keeps ticking; sim = the old global PresentationClock stop
        public float animDipMul = 0.1f, catchUpMul = 1.5f, maxCatchUpS = 0.3f;   // Animator speed x during the dip; x while catching the lost animation time up; cap on the catch-up time
        public float minCatchUpMul = 1.01f, minFwdMix = 0.01f, gravityRef = 9.81f;
        public Score score = new Score(); public Fade fade = new Fade(); public Rng rng = new Rng(); public Roles roles = new Roles();
        public Switches on = new Switches(); public Pool pool = new Pool(); public TrailCfg trailCfg = new TrailCfg();
        public Path[] paths = new Path[0]; public Row[] rows = new Row[0];
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    static void Load()
    {
        var d = new Data(); d.enabled = 0;   // no file = off (the old behaviour)
        try
        {
            string s = DataPath.ReadText("hit_fx.json");
            if (!string.IsNullOrEmpty(s))
            {
                var r = JsonUtility.FromJson<Data>(s);
                if (r != null)
                {
                    if (r.score == null) r.score = new Score(); if (r.fade == null) r.fade = new Fade(); if (r.rng == null) r.rng = new Rng(); if (r.roles == null) r.roles = new Roles(); if (r.on == null) r.on = new Switches(); if (r.pool == null) r.pool = new Pool(); if (r.trailCfg == null) r.trailCfg = new TrailCfg(); if (r.paths == null) r.paths = new Path[0]; if (r.rows == null) r.rows = new Row[0];
                    if (r.trailCfg.color == null || r.trailCfg.color.Length < 3) r.trailCfg.color = new TrailCfg().color;
                    for (int i = 0; i < r.rows.Length; i++)
                    {
                        var w = r.rows[i]; if (w == null) continue;
                        if (w.weapons == null) w.weapons = new string[0]; if (w.dirs == null) w.dirs = new string[0]; if (w.moves == null) w.moves = new string[0];
                        if (w.blood == null) w.blood = new Blood(); if (w.trail == null) w.trail = new Trail();
                    }
                    d = r;
                }
            }
        }
        catch (Exception ex) { Debug.LogWarning("HitFxTable: hit_fx.json read failed: " + ex.Message); d = new Data(); d.enabled = 0; }
        data = d;
    }
}

public class HitFx : MonoBehaviour
{
    static HitFx instance;
    public static int Resolved, BloodBursts, TrailBursts; public static string LastRow = "";   // proof counters

    // one-entry cache so StopMs, ShakeMul, SparkMul and FlashMul of ONE contact resolve the row once
    static int cTick = -1; static DuelFighter cActor, cTarget; static string cType; static HitFxTable.Row cRow; static bool cMetal;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; cTick = -1; cType = null; cRow = null; cActor = cTarget = null; System.Array.Clear(slots, 0, slots.Length); Stops = 0; Resolved = BloodBursts = TrailBursts = 0; LastRow = ""; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-hitfx:off") >= 0) return;
        var go = new GameObject("HitFx"); DontDestroyOnLoad(go); instance = go.AddComponent<HitFx>();
    }

    // ---- row lookup ----
    static bool In(string[] a, string v) { if (a.Length == 0) return true; for (int i = 0; i < a.Length; i++) if (a[i] == v) return true; return false; }

    static HitFxTable.Row Resolve(CombatEvent e, out bool metal)
    {
        metal = false;
        var d = HitFxTable.Get(); if (d.enabled == 0 || e.actor == null || e.target == null) return null;
                if (cTick == e.tick && ReferenceEquals(cActor, e.actor) && ReferenceEquals(cTarget, e.target) && cType == e.type && cRow != null) { metal = cMetal; return cRow; }
        try { metal = BloodTable.For(e.target, e.heavy, e.target.Dead).sparks; } catch (Exception) { }
        string weapon = CueTable.WeaponOf(HitReactionTable.CharOf(e.actor)), dir = e.actor.CurrentMove != null && e.actor.CurrentMove.direction != null ? e.actor.CurrentMove.direction : "";
        string mid = e.moveId ?? ""; bool dead = e.target.Dead;
        HitFxTable.Row best = null; int bestScore = -1;
        for (int i = 0; i < d.rows.Length; i++)
        {
            var r = d.rows[i]; if (r == null || r.type != e.type) continue;
            if (r.heavy >= 0 && (r.heavy == 1) != e.heavy) continue;
            if (r.metal >= 0 && (r.metal == 1) != metal) continue;
            if (r.kill >= 0 && (r.kill == 1) != dead) continue;
            if (!In(r.weapons, weapon) || !In(r.dirs, dir) || !In(r.moves, mid)) continue;
            var sc = d.score; int s = (r.moves.Length > 0 ? sc.moves : 0) + (r.weapons.Length > 0 ? sc.weapons : 0) + (r.dirs.Length > 0 ? sc.dirs : 0) + (r.kill >= 0 ? sc.kill : 0) + (r.heavy >= 0 ? sc.heavy : 0) + (r.metal >= 0 ? sc.metal : 0);
            if (s >= bestScore) { bestScore = s; best = r; }
        }
        cTick = e.tick; cActor = e.actor; cTarget = e.target; cType = e.type; cRow = best; cMetal = metal;
        if (best != null) { Resolved++; LastRow = best.id; }
        return best;
    }

    // ---- hooks called from the existing consumers (one line each; identity when HitFx is off or has no row) ----
    public static float StopMs(CombatEvent e, float ms, float slowMoMul)
    {
        var d = HitFxTable.Get(); if (d.enabled == 0 || d.on.stop == 0) return ms;
        bool m; var r = Resolve(e, out m); if (r == null || r.stopMs <= 0f) return ms;
        return Mathf.Clamp(r.stopMs, d.stopMinMs, d.stopMaxMs) * slowMoMul;
    }
    public static float ShakeMul(CombatEvent e) { var d = HitFxTable.Get(); if (d.enabled == 0 || d.on.shake == 0) return 1f; bool m; var r = Resolve(e, out m); return r == null ? 1f : r.shakeMul; }
    public static float SparkMul(CombatEvent e) { var d = HitFxTable.Get(); if (d.enabled == 0 || d.on.spark == 0) return 1f; bool m; var r = Resolve(e, out m); return r == null ? 1f : r.sparkMul; }
    public static float FlashMul(CombatEvent e) { var d = HitFxTable.Get(); if (d.enabled == 0 || d.on.spark == 0) return 1f; bool m; var r = Resolve(e, out m); return r == null ? 1f : r.flashMul; }

    // ---- presentation hit-stop: per-fighter Animator dip + catch-up (8 slots, no allocation) ----
    struct Slot { public Fighter f; public float dip, lost, mul; }
    static readonly Slot[] slots = new Slot[8];
    public static int Stops;
    static bool PresMode(HitFxTable.Data d) { return d.enabled != 0 && d.on.stop != 0 && d.hitStopMode == "presentation"; }
    /// <summary>True when HitFx handled the stop as a per-fighter dip (the caller must NOT request the global stop).</summary>
    public static bool PresentationStop(CombatEvent e, float ms)
    {
        var d = HitFxTable.Get(); if (!PresMode(d)) return false;
        AddDip(e.actor, ms); AddDip(e.target, ms); Stops++; return true;
    }
    /// <summary>True while hit_stop.json's old global floor must not fire (presentation mode).</summary>
    public static bool SuppressSimStop() { return PresMode(HitFxTable.Get()); }
    static void AddDip(DuelFighter df, float ms)
    {
        if (df == null || ms <= 0f) return;
        var f = df.GetComponent<Fighter>(); if (f == null || f.anim == null) return;
        int free = -1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (ReferenceEquals(slots[i].f, f)) { slots[i].dip = Mathf.Max(slots[i].dip, ms / 1000f); return; }
            if (free < 0 && ReferenceEquals(slots[i].f, null)) free = i;
        }
        if (free >= 0) { slots[free].f = f; slots[free].dip = ms / 1000f; slots[free].lost = 0f; slots[free].mul = HitFxTable.Get().animDipMul; }
    }
    /// <summary>Animator speed multiplier for this fighter now (1 = untouched).</summary>
    public static float PresMul(Fighter f)
    {
        for (int i = 0; i < slots.Length; i++) if (ReferenceEquals(slots[i].f, f)) return slots[i].mul;
        return 1f;
    }
    void Update()
    {
        var d = HitFxTable.Get(); float dt = Time.unscaledDeltaTime, dip = d.animDipMul, up = Mathf.Max(d.minCatchUpMul, d.catchUpMul), maxLost = d.maxCatchUpS * (up - 1f);
        for (int i = 0; i < slots.Length; i++)
        {
            if (ReferenceEquals(slots[i].f, null)) continue;
            if (slots[i].f == null) { slots[i] = default(Slot); continue; }   // destroyed
            if (slots[i].dip > 0f) { float use = Mathf.Min(dt, slots[i].dip); slots[i].dip -= use; slots[i].lost = Mathf.Min(maxLost, slots[i].lost + use * (1f - dip)); slots[i].mul = dip; }
            else if (slots[i].lost > 0f) { slots[i].lost -= dt * (up - 1f); slots[i].mul = up; if (slots[i].lost <= 0f) slots[i] = default(Slot); }
            else slots[i] = default(Slot);
        }
    }

    // ---- particle pools ----
    HitFxTable.Data cfg; ParticleSystem sprayPS, puffPS, trailPS; float nextAt; uint seed;
    float Rand() { var g = cfg.rng; unchecked { seed = seed * (uint)g.mul + (uint)g.add; } return seed / g.scale; }
    float Between(float a, float b) { return a + (b - a) * Rand(); }

    void Awake()
    {
        cfg = HitFxTable.Get(); seed = (uint)cfg.rng.seed; if (cfg.enabled == 0) return;
        try { Build(); } catch (Exception ex) { Debug.LogWarning("HitFx: particles disabled: " + ex.Message); sprayPS = puffPS = trailPS = null; }
    }
    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }
    void OnDestroy() { if (instance == this) instance = null; }

    ParticleSystem MakeSystem(string n, Material m, int max, float gravityModifier, ParticleSystemRenderMode mode)
    {
        var go = new GameObject(n); go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = Mathf.Max(1, max); main.gravityModifier = gravityModifier; main.startSpeed = 0f;
        var em = ps.emission; em.enabled = false; var sh = ps.shape; sh.enabled = false;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m; r.renderMode = mode; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        ps.Play(); return ps;
    }

    static void Fade(ParticleSystem ps, Color a, Color b, float alphaMid)
    {
        float midAt = HitFxTable.Get().fade.midAt;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(alphaMid, midAt), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    void Build()
    {
        var p = cfg.pool; var hb = HitFeelTable.Get().blood;
        if (cfg.on.blood != 0)
        {
            var ms = FxMaterials.Particle(null, cfg.roles.spray); var mp = FxMaterials.Particle(null, cfg.roles.puff);
            if (ms != null && ms.HasProperty(cfg.roles.softDistanceProp) && hb.softDistance >= 0f) ms.SetFloat(cfg.roles.softDistanceProp, hb.softDistance);
            if (ms != null) { sprayPS = MakeSystem("HitFxSpray", ms, p.sprayMax, p.gravity / cfg.gravityRef, ParticleSystemRenderMode.Stretch); var r = sprayPS.GetComponent<ParticleSystemRenderer>(); r.lengthScale = p.sprayLengthScale; r.velocityScale = p.sprayVelocityScale; r.cameraVelocityScale = 0f; r.maxParticleSize = p.sprayMaxSize; Fade(sprayPS, hb.startColor, hb.endColor, cfg.fade.sprayAlphaMid); }
            if (mp != null) { puffPS = MakeSystem("HitFxPuff", mp, p.puffMax, p.puffGravity / cfg.gravityRef, ParticleSystemRenderMode.Billboard); puffPS.GetComponent<ParticleSystemRenderer>().maxParticleSize = p.puffMaxSize; Fade(puffPS, hb.dropColor, hb.endColor, cfg.fade.puffAlphaMid); }
        }
        if (cfg.on.trail != 0)
        {
            var mt = FxMaterials.Particle(null, cfg.roles.trail);   // alpha blend, never additive (ARCS note)
            if (mt != null)
            {
                trailPS = MakeSystem("HitFxTrail", mt, p.trailMax, 0f, ParticleSystemRenderMode.Stretch); var r = trailPS.GetComponent<ParticleSystemRenderer>(); r.lengthScale = cfg.trailCfg.lengthScale; r.velocityScale = cfg.trailCfg.velocityScale; r.cameraVelocityScale = 0f; r.maxParticleSize = cfg.trailCfg.maxSize;
                Color k = HitVfxCfg.C(cfg.trailCfg.color, Color.white); Fade(trailPS, k, k, cfg.trailCfg.alphaMid);
            }
        }
    }

    static bool PathOf(HitFxTable.Data d, string dir, out HitFxTable.Path path)
    {
        for (int i = 0; i < d.paths.Length; i++) if (d.paths[i] != null && d.paths[i].dir == dir) { path = d.paths[i]; return true; }
        path = null; return false;
    }

    void OnEvent(CombatEvent e)
    {
        if (cfg == null || cfg.enabled == 0 || e.actor == null || e.target == null) return;
        if (cfg.suppressInFinisher != 0 && RTFinisher.Busy) return;   // the finisher owns the gore (GoreFx) and the camera
        bool metal; var row = Resolve(e, out metal); if (row == null) return;
        bool bloodOk = e.type == "Hit" && !metal && row.blood.mode != "none" && row.blood.count > 0 && cfg.on.blood != 0;
        bool trailOk = cfg.on.trail != 0 && trailPS != null && row.trail.count > 0;
        if (!bloodOk && !trailOk) return;
        if (Time.unscaledTime < nextAt) return; nextAt = Time.unscaledTime + cfg.pool.minIntervalS;

        Vector3 blow = e.target.transform.position - e.actor.transform.position; blow.y = 0f;
        if (blow.sqrMagnitude < 1e-4f) { blow = e.actor.transform.forward; blow.y = 0f; }
        if (blow.sqrMagnitude < 1e-4f) blow = Vector3.forward;
        blow.Normalize();
        Vector3 right = new Vector3(blow.z, 0f, -blow.x);
        string dir = e.actor.CurrentMove != null && e.actor.CurrentMove.direction != null ? e.actor.CurrentMove.direction : "";
        HitFxTable.Path path; Vector3 pv = blow;
        if (PathOf(cfg, dir, out path)) { pv = right * path.side + Vector3.down * path.down + blow * path.fwd; if (pv.sqrMagnitude < 1e-6f) pv = blow; pv.Normalize(); }
        Vector3 at = e.point;

        if (bloodOk) EmitBlood(e, row.blood, at, pv, blow);
        if (trailOk) EmitTrail(row.trail, at, pv);
    }

    void EmitBlood(CombatEvent e, HitFxTable.Blood b, Vector3 at, Vector3 pv, Vector3 blow)
    {
        bool spray = b.mode == "spray"; var ps = spray ? sprayPS : puffPS; if (ps == null) return;
        var bs = BloodTable.For(e.target, e.heavy, e.target.Dead);
        int n = Mathf.RoundToInt(b.count * bs.count * HitFeelTable.Get().blood.countScale); if (n <= 0) return;
        Vector3 baseDir = spray ? pv * b.pathMix + blow * b.fwdMix + Vector3.up * b.upBias : blow * Mathf.Max(cfg.minFwdMix, b.fwdMix) + Vector3.up * b.upBias;
        if (baseDir.sqrMagnitude < 1e-6f) baseDir = blow; baseDir.Normalize();
        Vector3 a1 = Vector3.Cross(baseDir, Vector3.up); if (a1.sqrMagnitude < 1e-6f) a1 = Vector3.right; a1.Normalize();
        Vector3 a2 = Vector3.Cross(baseDir, a1);
        float cone = Mathf.Tan(b.coneDeg * Mathf.Deg2Rad);
        var ep = new ParticleSystem.EmitParams(); ep.position = at; ep.startColor = (Color32)Color.white; ep.applyShapeToPosition = false;
        for (int i = 0; i < n; i++)
        {
            Vector3 v = baseDir + a1 * ((Rand() - 0.5f) * 2f * cone) + a2 * ((Rand() - 0.5f) * 2f * cone); v.Normalize();
            ep.velocity = v * (Between(b.speedMin, b.speedMax) * bs.speed); ep.startLifetime = Between(b.lifeMin, b.lifeMax); ep.startSize = Between(b.sizeMin, b.sizeMax);
            ps.Emit(ep, 1);
        }
        BloodBursts++;
    }

    void EmitTrail(HitFxTable.Trail t, Vector3 at, Vector3 pv)
    {
        var ep = new ParticleSystem.EmitParams(); Color32 tc = Color.white; tc.a = (byte)Mathf.Clamp(Mathf.RoundToInt(t.alpha * 255f), 0, 255); ep.startColor = tc; ep.applyShapeToPosition = false;
        for (int i = 0; i < t.count; i++)
        {
            float k = t.count > 1 ? (float)i / (t.count - 1) : 0f;
            ep.position = at - pv * (t.lengthM * (cfg.trailCfg.startFrac + (1f - cfg.trailCfg.startFrac) * k)) + Vector3.up * ((Rand() - 0.5f) * cfg.trailCfg.jitterM);
            ep.velocity = pv * t.speed; ep.startLifetime = t.life; ep.startSize = t.width;
            trailPS.Emit(ep, 1);
        }
        TrailBursts++;
    }
}
