using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// laneHITFX v81: AAA hit feel. Presentation only (no sim state, no sim RNG). Every number lives in Assets/Data/hit_feel.json (HitFeelTable below); the code holds no tuning.
//  1. Hit sparks on steel contact (Parried, Blocked, GuardBroken, and a landed Hit on a metal victim): an HDR emissive streak burst (Furnace/Particle, additive, _Intensity ~8, colour from a blackbody
//     temperature, 2200 K) plus a white-hot glint, sized up with camera distance so it reads at gameplay range, plus a 0.05 s warm point-light flash (HDRP lumens, unscaled time).
//  2. Hit-stop: PresentationClock.RequestStop(ms) with ms = lerp(minMs, maxMs, damage position) per contact tier (clean hit 60-80, heavy ~100, parry ~100, guard break 100, kill 120).
//     PresentationClock keeps the LONGEST request, so hit_stop.json (the old tier floor) and this never stack. Damage = victim health lost this contact (health snapshot taken at the start of the frame,
//     before Duel.Update resolves the blow), so no Duel.cs / CombatEvent edit is needed. Not requested while a finisher owns the clock (RTFinisher.Busy / FinisherTime.Slowed / Duel.Frozen);
//     shortened while CombatSlowMo is running; a parry / crit / deathblow slow-mo dip is delayed by slow_mo.json delayS so the freeze plays first and the dip is not eaten by it.
//  3. Blood: HitFeelTable.Get().blood feeds BloodBurst (lengthScale, wet colours, soft distance, size) and CombatVFX (drop / mist colours). Defaults in code = the v69 look.
// Self-installing: a RuntimeInitializeOnLoadMethod(AfterSceneLoad) bootstrap creates one DontDestroyOnLoad object. Hook: ContactRelay.OnEvent.

public static class HitFeelTable
{
    [Serializable] public class StopTier { public string id = ""; public float minMs, maxMs, dmgLo, dmgHi; }
    [Serializable]
    public class Stop
    {
        public int enabled = 1;
        public float maxMs = 140f;              // hard cap on one request
        public float unknownDamageT = 0.5f;     // position in [minMs, maxMs] when the health delta could not be read
        public float duringSlowMoMul = 0.5f;    // x ms while CombatSlowMo owns the clock
        public float minRequestMs = 8f;
        public StopTier[] tiers = new StopTier[0];
    }
    [Serializable] public class Row { public string type = ""; public int heavy = -1, perfect = -1, metal = -1; public float spark, flash; public string attackerWeapon = ""; /* SOBEKCLAWS: "" = any attacker weapon (WeaponHooks id); first match wins, so keyed rows go first */ }
    [Serializable]
    public class Sparks
    {
        public int enabled = 1, poolSize = 320, glintPool = 24; public float cooldown = 0.03f;
        public float intensity = 8f, kelvin = 2200f, coolKelvin = 1700f, coolAt = 0.55f, glintWhiteMix = 0.55f, opacity = 1f;
        public int suppressInFinisher = 1; /* v89: 1 = no spark burst / flash while a finisher owns the clock (spark storm in finishers) */ public int countBase = 14, countPerStrength = 14; public float countRandom = 0.25f, countScaleHeavy = 1.3f;
        public float anchorHeight = 1.1f, anchorBack = 0.4f, anchorJitter = 0.1f;
        public float alongBias = 0.3f, alongScale = 1.1f, sideSpread = 2.4f, upMin = 0.15f, upRange = 1.4f, towardCamera = 0.7f;
        public float speedMin = 3f, speedRange = 6f, speedPerStrength = 0.35f;
        public float lifeMin = 0.18f, lifeRange = 0.27f, gravity = 1.2f;
        public float sizeMin = 0.03f, sizeRange = 0.03f, lengthScale = 2.2f, velocityScale = 0.09f, maxParticleSize = 0.5f;
        public float glintLife = 0.07f, glintSize = 0.3f, glintSizePerStrength = 0.25f, glintIntensity = 8f;
        public float refDistance = 4f, maxGrow = 2.2f;      // readability: size x clamp(cameraDistance / refDistance, 1, maxGrow)
    }
    [Serializable]
    public class Flash
    {
        public int enabled = 1, pool = 2;
        public float seconds = 0.05f, lumen = 9000f, range = 4.5f, falloffPower = 1.5f, towardCamera = 0.3f, kelvin = 2200f;
    }
    [Serializable]
    public class Blood
    {
        public float lengthScale = 1.6f, velocityScale = 0f, softDistance = -1f, sizeScale = 1f, countScale = 1f;   // softDistance < 0 = leave the fx_materials.json role value
        public Color startColor = new Color(0.412f, 0.059f, 0.051f, 1f), endColor = new Color(0.125f, 0.02f, 0.016f, 1f);
        public Color dropColor = new Color(0.145f, 0.008f, 0.008f, 1f), mistColor = new Color(0.22f, 0.015f, 0.015f, 0.95f);
    }
    [Serializable]
    public class Data
    {
        public int version = 1, enabled = 1;
        public float guardSparkScale = 1f;      // x the old CombatVFX guard-spark counts (0 = replaced by the HDR burst)
        public Stop hitstop = new Stop();
        public Sparks sparks = new Sparks();
        public Flash flash = new Flash();
        public Row[] rows = new Row[0];
        public Blood blood = new Blood();
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    // same lookup pattern as ClashFxTable: Resources TextAsset, StreamingAssets, then Application.dataPath/Data
    static void Load()
    {
        data = new Data(); string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("hit_feel");
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "hit_feel.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/hit_feel.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("HitFeelTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { data.enabled = 0; return; }   // no file: HitFeel off, BloodBurst / CombatVFX keep the code defaults (the v69 look)
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) { data.enabled = 0; return; }
            if (d.hitstop == null) d.hitstop = new Stop(); if (d.hitstop.tiers == null) d.hitstop.tiers = new StopTier[0];
            if (d.sparks == null) d.sparks = new Sparks(); if (d.flash == null) d.flash = new Flash(); if (d.rows == null) d.rows = new Row[0]; if (d.blood == null) d.blood = new Blood();
            data = d;
        }
        catch (Exception ex) { Debug.LogError("HitFeelTable: parse failed: " + ex.Message); data = new Data(); data.enabled = 0; }
    }
}

public class HitFeel : MonoBehaviour
{
    static HitFeel instance;
    // proof counters (read by a harness / the -hitstoplog flag)
    public static float LastDamage, LastStopMs; public static string LastTier = ""; public static int Stops, Bursts, Flashes;
    static readonly bool logOn = Array.IndexOf(Environment.GetCommandLineArgs(), "-hitstoplog") >= 0;

    /// <summary>x the old CombatVFX guard-spark counts: 1 when HitFeel is not running (so a missing shader never leaves a block without sparks), else hit_feel.json guardSparkScale.</summary>
    public static float GuardSparkScale { get { return instance != null && instance.sparkReady ? Mathf.Max(0f, HitFeelTable.Get().guardSparkScale) : 1f; } }

    // PERF (FxPrewarm, optional 2-line hook, see MANIFEST): one sub-pixel particle through both systems / the two spark materials, so the first parry does not pay for pipeline-state creation. Uses no sim RNG.
    public static void PrewarmEmit(Vector3 pos, float size)
    {
        if (instance == null || !instance.sparkReady) return;
        var ep = new ParticleSystem.EmitParams(); ep.position = pos; ep.velocity = Vector3.zero; ep.startLifetime = 0.1f; ep.startSize = size; ep.startColor = (Color32)Color.white;
        instance.sparkPS.Emit(ep, 1); instance.glintPS.Emit(ep, 1);
    }
    public static void CollectPrewarm(List<Material> into)
    {
        if (instance == null || !instance.sparkReady || into == null) return;
        if (instance.sparkMat != null) into.Add(instance.sparkMat);
        if (instance.glintMat != null) into.Add(instance.glintMat);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; LastDamage = 0f; LastStopMs = 0f; LastTier = ""; Stops = Bursts = Flashes = 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("HitFeel");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<HitFeel>();
    }

    class Lamp { public Light light; public HDAdditionalLightData hd; public float age = 99f, peak; }

    HitFeelTable.Data cfg;
    bool sparkReady;
    ParticleSystem sparkPS, glintPS;
    Material sparkMat, glintMat;
    Lamp[] lamps = new Lamp[0]; int lampNext;
    DuelFighter[] fighters = new DuelFighter[0]; readonly Dictionary<DuelFighter, float> hp = new Dictionary<DuelFighter, float>();
    float refreshAt, sparkReadyAt; uint seed = 4177;

    float Rand() { unchecked { seed = seed * 1664525u + 1013904223u; } return seed / 4294967296f; }

    void Awake()
    {
        cfg = HitFeelTable.Get();
        if (cfg.enabled == 0) return;
        try { BuildSparks(); } catch (Exception ex) { Debug.LogWarning("HitFeel: sparks disabled: " + ex.Message); sparkReady = false; }
        try { BuildLamps(); } catch (Exception ex) { Debug.LogWarning("HitFeel: flash light disabled: " + ex.Message); lamps = new Lamp[0]; }
    }

    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }
    void OnDestroy() { if (instance == this) instance = null; }

    // ---- colour helpers ----
    // blackbody colour (Tanner Helland fit, display-referred 0..1), 1000 K .. 40000 K
    public static Color Kelvin(float k)
    {
        float t = Mathf.Clamp(k, 1000f, 40000f) / 100f, r, g, b;
        r = t <= 66f ? 255f : 329.698727446f * Mathf.Pow(t - 60f, -0.1332047592f);
        g = t <= 66f ? 99.4708025861f * Mathf.Log(t) - 161.1195681661f : 288.1221695283f * Mathf.Pow(t - 60f, -0.0755148492f);
        b = t >= 66f ? 255f : (t <= 19f ? 0f : 138.5177312231f * Mathf.Log(t - 10f) - 305.0447927307f);
        return new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f), 1f);
    }

    // ---- construction ----
    Material MakeMat(float intensity)
    {
        var m = FxMaterials.Particle(null, "spark");   // Furnace/Particle additive on the soft dot (fx_materials.json role "spark"); null when the shader is missing
        if (m == null || !m.HasProperty("_Intensity")) return null;
        m.name = "FxMat_hitSpark";
        m.SetFloat("_Intensity", intensity); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", 0f);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f);   // additive
        return m;
    }

    ParticleSystem MakeSystem(string n, Material m, int max, float gravity)
    {
        var go = new GameObject(n); go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = Mathf.Max(1, max); main.gravityModifier = gravity; main.startSpeed = 0f;
        var em = ps.emission; em.enabled = false; var sh = ps.shape; sh.enabled = false;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        ps.Play();   // emission is off; a playing system keeps simulating Emit() particles
        return ps;
    }

    static void ColorOverLife(ParticleSystem ps, Color hot, Color cool, float coolAt)
    {
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(hot, 0f), new GradientColorKey(cool, Mathf.Clamp(coolAt, 0.01f, 0.99f)), new GradientColorKey(cool, 1f) },
                  new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.5f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    void BuildSparks()
    {
        var sp = cfg.sparks;
        if (sp.enabled == 0 || sp.poolSize <= 0) return;
        sparkMat = MakeMat(sp.intensity); glintMat = MakeMat(sp.glintIntensity);
        if (sparkMat == null || glintMat == null) { Debug.LogWarning("HitFeel: Furnace/Particle shader missing; HDR sparks off (old guard sparks stay)."); return; }
        sparkPS = MakeSystem("HitSparks", sparkMat, sp.poolSize, sp.gravity);
        var sr = sparkPS.GetComponent<ParticleSystemRenderer>();
        sr.renderMode = ParticleSystemRenderMode.Stretch; sr.lengthScale = sp.lengthScale; sr.velocityScale = sp.velocityScale; sr.cameraVelocityScale = 0f; sr.maxParticleSize = sp.maxParticleSize;
        ColorOverLife(sparkPS, Kelvin(sp.kelvin), Kelvin(sp.coolKelvin), sp.coolAt);
        glintPS = MakeSystem("HitGlint", glintMat, Mathf.Max(1, sp.glintPool), 0f);
        var gr = glintPS.GetComponent<ParticleSystemRenderer>();
        gr.renderMode = ParticleSystemRenderMode.Billboard; gr.maxParticleSize = Mathf.Max(sp.maxParticleSize, 2f);
        ColorOverLife(glintPS, Color.Lerp(Kelvin(sp.kelvin), Color.white, sp.glintWhiteMix), Kelvin(sp.kelvin), 0.5f);
        sparkReady = true;
    }

    void BuildLamps()
    {
        var f = cfg.flash;
        if (f.enabled == 0 || f.pool <= 0) return;
        lamps = new Lamp[f.pool];
        for (int i = 0; i < lamps.Length; i++)
        {
            var go = new GameObject("HitFlashLight" + i); go.transform.SetParent(transform, false);
            var l = go.AddComponent<Light>(); l.type = LightType.Point; l.range = f.range; l.shadows = LightShadows.None; l.color = Kelvin(f.kelvin);
            var hd = go.GetComponent<HDAdditionalLightData>(); if (hd == null) hd = go.AddComponent<HDAdditionalLightData>();
            hd.SetIntensity(f.lumen, UnityEngine.Rendering.LightUnit.Lumen);   // same HDRP call as MoveSheet's point lights
            l.enabled = false;
            lamps[i] = new Lamp { light = l, hd = hd };
        }
    }

    // ---- per frame: flash decay on real time; health snapshot at the END of every frame ----
    // LateUpdate runs after Duel.Update, so hp[] holds the health each fighter ended the previous frame with; a contact in the next sim tick compares against it (damage = loss).
    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        for (int i = 0; i < lamps.Length; i++)
        {
            var lp = lamps[i]; if (lp == null || lp.age >= 90f) continue;
            lp.age += dt;
            float life = Mathf.Max(0.005f, cfg.flash.seconds);
            if (lp.age >= life) { lp.light.enabled = false; lp.age = 99f; continue; }
            float k = Mathf.Pow(1f - lp.age / life, Mathf.Max(0.1f, cfg.flash.falloffPower));
            lp.hd.SetIntensity(lp.peak * k, UnityEngine.Rendering.LightUnit.Lumen);
        }
    }

    void LateUpdate()
    {
        if (Time.unscaledTime >= refreshAt) { refreshAt = Time.unscaledTime + 1f; fighters = FindObjectsByType<DuelFighter>(FindObjectsSortMode.None); }
        for (int i = 0; i < fighters.Length; i++) if (fighters[i] != null) hp[fighters[i]] = fighters[i].health;
    }

    // ---- contact ----
    HitFeelTable.Row FindRow(CombatEvent e, bool metal)
    {
        var rows = cfg.rows;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r == null || r.type != e.type) continue;
            if (r.heavy >= 0 && (r.heavy == 1) != e.heavy) continue;
            if (r.perfect >= 0 && (r.perfect == 1) != e.perfect) continue;
            if (r.metal >= 0 && (r.metal == 1) != metal) continue;
            if (!string.IsNullOrEmpty(r.attackerWeapon) && (e.actor == null || WeaponHooks.Of(e.actor).id != r.attackerWeapon)) continue;   // SOBEKCLAWS
            return r;
        }
        return null;
    }

    static HitFeelTable.StopTier Tier(HitFeelTable.StopTier[] tiers, string id)
    {
        for (int i = 0; i < tiers.Length; i++) if (tiers[i] != null && tiers[i].id == id) return tiers[i];
        return null;
    }

    string TierOf(CombatEvent e)
    {
        switch (e.type)
        {
            case "Hit":
                if (e.target != null && e.target.Dead) return "kill";
                string kick = PresentationClock.Table != null && !string.IsNullOrEmpty(PresentationClock.Table.kickMoveId) ? PresentationClock.Table.kickMoveId : CombatClass.KickMoveId;   // HCKIT4 v95
                if (!string.IsNullOrEmpty(e.moveId) && e.moveId == kick) return CombatClass.Kick;   // HCKIT4 v95
                return CombatClass.Of(e.type, e.heavy);   // HCKIT4 v95
            case CombatClass.Blocked: return CombatClass.Of(e.type, e.heavy);   // HCKIT4 v95
            case CombatClass.Parried: return CombatClass.Parry;   // HCKIT4 v95
            case CombatClass.GuardBroken: return CombatClass.GuardBreak;   // HCKIT4 v95
        }
        return null;
    }

    static bool ClockOwned() { return RTFinisher.Busy || FinisherTime.Slowed || Duel.Frozen; }

    void OnEvent(CombatEvent e)
    {
        if (cfg == null || cfg.enabled == 0 || e.actor == null || e.target == null) return;

        // damage = health the victim lost this contact (snapshot from the end of the previous frame); the baseline is refreshed so a second contact in the same frame reads its own loss
        float dmg = 0f; float prev;
        if (hp.TryGetValue(e.target, out prev)) dmg = Mathf.Max(0f, prev - e.target.health);
        hp[e.target] = e.target.health;
        LastDamage = dmg;

        RequestStop(e, dmg);
        if (cfg.sparks != null && cfg.sparks.suppressInFinisher != 0 && RTFinisher.Busy) return;   // v89: no sparks / flash during a finisher

        bool metal = false;
        try { metal = BloodTable.For(e.target, e.heavy, e.target.Dead).sparks; } catch (Exception) { }   // victims[].sparks (blood.json) = armour: a landed blow on metal also sparks
        var row = FindRow(e, metal);
        if (row == null) return;
        if (row.spark <= 0f && row.flash <= 0f) return;
        if (Time.unscaledTime < sparkReadyAt) return;               // one burst per cooldown (a Parried + GuardBroken pair on the same swing is one flash)
        sparkReadyAt = Time.unscaledTime + cfg.sparks.cooldown;
        Vector3 along = e.target.transform.position - e.actor.transform.position; along.y = 0f;
        if (along.sqrMagnitude < 1e-4f) { along = e.actor.transform.forward; along.y = 0f; }
        if (along.sqrMagnitude < 1e-4f) along = Vector3.forward;
        along.Normalize();
        var sp = cfg.sparks;
        Vector3 anchor = e.target.transform.position + Vector3.up * (sp.anchorHeight * FighterScale.Of(e.target)) - along * sp.anchorBack; anchor = ArcFx.ContactPoint(e.actor, e.target, e.type, metal, anchor);   // ARCS v105
        if (sparkReady && row.spark > 0f) Burst(anchor, along, row.spark, e.heavy);
        Flash(anchor, row.flash);
    }

    void RequestStop(CombatEvent e, float dmg)
    {
        var hs = cfg.hitstop;
        if (hs.enabled == 0) return;
        string id = TierOf(e); if (id == null) return;
        var t = Tier(hs.tiers, id); if (t == null) return;
        if (ClockOwned()) return;                                    // a finisher owns the clock: no extra freeze on top of its own timing
        float k = t.dmgHi > t.dmgLo ? (dmg > 0f ? Mathf.Clamp01((dmg - t.dmgLo) / (t.dmgHi - t.dmgLo)) : hs.unknownDamageT) : 1f;
        float ms = Mathf.Lerp(t.minMs, t.maxMs, k);
        if (CombatSlowMo.Active || Time.timeScale < 0.999f) ms *= hs.duringSlowMoMul;     // already slow: a full freeze on top reads as a stall
        ms = Mathf.Min(ms, hs.maxMs);
        LastTier = id; LastStopMs = ms;
        if (ms < hs.minRequestMs) return;
        PresentationClock.RequestStop(ms);                           // the clock keeps the longest request: this never adds to hit_stop.json
        Stops++;
        if (logOn) Debug.Log("[HitFeel] tick " + Duel.TickCount + " " + e.type + " move=" + e.moveId + " tier=" + id + " dmg=" + dmg.ToString("0.0") + " -> " + ms.ToString("0") + " ms (frame " + Time.frameCount + ")");
    }

    // ---- emitters ----
    void Burst(Vector3 anchor, Vector3 along, float strength, bool heavy)
    {
        var sp = cfg.sparks;
        Vector3 side = new Vector3(along.z, 0f, -along.x);
        Vector3 toCam = Vector3.zero; float grow = 1f;
        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 c = cam.transform.position - anchor; float dist = c.magnitude;
            if (dist > 1e-3f) toCam = c / dist;
            grow = Mathf.Clamp(dist / Mathf.Max(0.1f, sp.refDistance), 1f, Mathf.Max(1f, sp.maxGrow));
        }
        int n = Mathf.Max(1, Mathf.RoundToInt((sp.countBase + strength * sp.countPerStrength) * (heavy ? sp.countScaleHeavy : 1f) * (1f + (Rand() - 0.5f) * 2f * sp.countRandom)));
        var ep = new ParticleSystem.EmitParams();
        for (int j = 0; j < n; j++)
        {
            Vector3 at = anchor + side * ((Rand() - 0.5f) * 2f * sp.anchorJitter);
            Vector3 dir = along * ((Rand() - sp.alongBias) * sp.alongScale) + side * ((Rand() - 0.5f) * sp.sideSpread) + Vector3.up * (sp.upMin + Rand() * sp.upRange) + toCam * (sp.towardCamera * (0.4f + 0.6f * Rand()));
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.up;
            dir.Normalize();
            float speed = (sp.speedMin + Rand() * sp.speedRange) * (1f + strength * sp.speedPerStrength);
            ep.position = at; ep.velocity = dir * speed;
            ep.startLifetime = sp.lifeMin + Rand() * sp.lifeRange;
            ep.startSize = (sp.sizeMin + Rand() * sp.sizeRange) * grow;
            ep.startColor = (Color32)new Color(1f, 1f, 1f, sp.opacity);
            sparkPS.Emit(ep, 1);
        }
        var gp = new ParticleSystem.EmitParams();
        gp.position = anchor + toCam * 0.1f; gp.velocity = Vector3.zero; gp.startLifetime = sp.glintLife; gp.startSize = (sp.glintSize + strength * sp.glintSizePerStrength) * grow; gp.startColor = (Color32)Color.white;
        glintPS.Emit(gp, 1);
        Bursts++;
    }

    void Flash(Vector3 anchor, float strength)
    {
        if (lamps.Length == 0 || strength <= 0f) return;
        var f = cfg.flash;
        var cam = Camera.main;
        Vector3 at = anchor;
        if (cam != null) { Vector3 c = cam.transform.position - anchor; if (c.sqrMagnitude > 1e-6f) at += c.normalized * f.towardCamera; }   // in front of the contact so the light falls on the surfaces, not inside them
        var lp = lamps[lampNext]; lampNext = (lampNext + 1) % lamps.Length;
        if (lp == null) return;
        lp.light.transform.position = at; lp.light.range = f.range; lp.light.color = Kelvin(f.kelvin);
        lp.peak = f.lumen * strength; lp.age = 0f;
        lp.hd.SetIntensity(lp.peak, UnityEngine.Rendering.LightUnit.Lumen);
        lp.light.enabled = true;
        Flashes++;
    }
}
