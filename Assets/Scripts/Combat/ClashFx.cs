using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Clash sparks + block dust + foot dust, ported from Frankendom V3 (src/fight/clash-sparks.ts, foot-dust.ts; plan rows 9 and 19).
// Presentation only. Every number lives in Assets/Data/clash_fx.json (ClashFxTable below); the code holds no tuning.
//  - Sparks: thin silver streaks (velocity-stretched particles) fanned across the blades and upward, plus one 85 ms glint. Steel contact only: which cue gets sparks is a "rows" entry.
//  - Block dust: sand puffed off the defender's rear foot (or both feet on a guard break), strength from the same rows.
//  - Foot dust: a small puff at foot plants, taken from a stride-distance heuristic on each fighter's horizontal velocity, plus roll/backstep via ContactRelay.OnAction.
// Self-installing: a RuntimeInitializeOnLoadMethod(AfterSceneLoad) bootstrap creates one DontDestroyOnLoad object; no scene edits.
// Hooks: ContactRelay.OnContact (Hit, Blocked, Parried, GuardBroken) and ContactRelay.OnAction (roll, backstep).

public static class ClashFxTable
{
    [Serializable] public class Row { public string type = ""; public int heavy = -1, perfect = -1; public string targetContains = ""; public string attackerWeapon = ""; /* SOBEKCLAWS */ public float spark, dust; public string feet = "rear"; }
    [Serializable] public class ActionRow { public string action = ""; public float dust; public string feet = "both"; }
    [Serializable]
    public class Sparks
    {
        public int poolSize; public float cooldown;
        public int countBase; public float countRandomBase, countRandomPerStrength, firstWaveMin, firstWaveRange, lateDelay;
        public float anchorHeight, anchorBack, anchorJitter;
        public float alongBias, alongScale, sideSpread, upMin, upRange;
        public float speedMin, speedRange, speedBase, speedPerStrength;
        public float lifeMin, lifeRange, lifePerStrength;
        public float sizeMin, sizeRange, lengthScale, velocityScale, maxParticleSize, gravity, opacity;
        public Color colorHot, colorCool; public float coolAt;
        public int textureSize; public float textureEdgePower;
        public int glintPool; public float glintLife, glintSize, glintSizePerStrength; public Color glintColor; public float colorAlphaMid = 0.9f, colorAlphaMidAt = 0.5f; /* DATALAYER b12 */
    }
    [Serializable] public class Block { public float countBase, countPerStrength, radius, speedBase, speedPerStrength, liftBase, liftPerStrength, sizeBase, sizePerStrength, footBack, footSide; }
    [Serializable] public class Plant { public int count; public float radius, speed, lift, size; }
    [Serializable] public class Heuristic { public int enabled; public float minSpeed, runSpeed, walkStride, runStride, plantCooldown, maxStepMeters, footSide, footForward; public int skipWhenDead; }
    [Serializable]
    public class Dust
    {
        public int poolSize; public float cooldown, lifetime, lifetimeJitter, fadeInFraction, sizeGrowth;
        public Color color; public float colorJitter, gravity, dragLimit, dragDampen, floorOffset;
        public int textureSize; public float textureEdgePower;
        public Block block = new Block(); public Plant plant = new Plant(); public Heuristic heuristic = new Heuristic();
    }
    // Unreal-baked flipbook sheets (development use), layered on the procedural FX; any missing texture or shader leaves the procedural path unchanged.
    // Sparks: 6x4 sheet, impact point at (impactU, impactV) of the cell (V from the top), spray points right (mirrored by a flipped renderer for a hit from the left).
    // Dust: 8x8 one-shot puff, ground line at groundV of the cell, optional motion-vector frame blend (frameBlend = 1).
    [Serializable]
    public class SparkSheet
    {
        public int enabled, tilesX, tilesY, poolSize;
        public string texture, textureHeavy, resource, resourceHeavy;
        public float lifetime, size, sizePerStrength, heavySizeScale, impactU, impactV, intensity, softDistance, proceduralCountScale;
        public Color tint;
    }
    [Serializable]
    public class DustSheet
    {
        public int enabled, tilesX, tilesY, poolSize, frameBlend;
        public string texture, motion, resource, resourceMotion;
        public float lifetime, size, sizePerStrength, groundV, intensity, softDistance, mvStrength, minStrength, proceduralCountScale;
        public Color tint;
    }
    [Serializable]
    public class Sheets
    {
        public int enabled;
        public string shader = "Furnace/ClashFlipbook";
        public SparkSheet sparks = new SparkSheet();
        public DustSheet dust = new DustSheet();
    }
    [Serializable]
    public class Data
    {
        public Sheets sheets = new Sheets();
        public int version, enabled; public int renderQueue = 3006; /* DATALAYER b12 */
        public string[] shaders = new string[0];
        public float fighterRefreshSeconds;
        public Sparks sparks = new Sparks();
        public Dust dust = new Dust();
        public Row[] rows = new Row[0];
        public ActionRow[] actions = new ActionRow[0];
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    // same lookup pattern as ImpactTable: Resources TextAsset, StreamingAssets, then Application.dataPath/Data (editor and dev runs)
    static void Load()
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("clash_fx");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "clash_fx.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/clash_fx.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("ClashFxTable: read failed: " + ex.Message); }

        if (string.IsNullOrEmpty(json)) { Debug.LogError("ClashFxTable: clash_fx.json not found; clash/dust FX disabled."); data = new Data(); return; }
        try
        {
            data = JsonUtility.FromJson<Data>(json);
            if (data == null) data = new Data();
            if (data.shaders == null) data.shaders = new string[0];
            if (data.rows == null) data.rows = new Row[0];
            if (data.actions == null) data.actions = new ActionRow[0];
            if (data.sheets == null) data.sheets = new Sheets();
            if (data.sheets.sparks == null) data.sheets.sparks = new SparkSheet();
            if (data.sheets.dust == null) data.sheets.dust = new DustSheet();
            if (data.sparks == null) data.sparks = new Sparks();
            if (data.dust == null) data.dust = new Dust();
            if (data.dust.block == null) data.dust.block = new Block();
            if (data.dust.plant == null) data.dust.plant = new Plant();
            if (data.dust.heuristic == null) data.dust.heuristic = new Heuristic();
        }
        catch (Exception ex) { Debug.LogError("ClashFxTable: parse failed: " + ex.Message); data = new Data(); }
    }
}

public class ClashFx : MonoBehaviour
{
    static ClashFx instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("ClashFx");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ClashFx>();
    }

    // PERF (FxPrewarm): one sub-pixel particle through every pooled system so their pipeline states / buffers exist before the first clash. Uses no RNG and no sim state.
    public static void PrewarmEmit(Vector3 pos, float size)
    {
        if (instance == null || !instance.ready) return;
        instance.EmitTiny(instance.sparkPS, pos, size); instance.EmitTiny(instance.glintPS, pos, size); instance.EmitTiny(instance.dustPS, pos, size);
        instance.EmitTiny(instance.sheetSparkR, pos, size); instance.EmitTiny(instance.sheetSparkL, pos, size);
        instance.EmitTiny(instance.sheetHeavyR, pos, size); instance.EmitTiny(instance.sheetHeavyL, pos, size); instance.EmitTiny(instance.sheetDust, pos, size);
    }
    public static void CollectPrewarm(List<Material> into)
    {
        if (instance == null || !instance.ready || into == null) return;
        if (instance.sparkMat != null) into.Add(instance.sparkMat);
        if (instance.dustMat != null) into.Add(instance.dustMat);
        if (instance.sheetSparkMat != null) into.Add(instance.sheetSparkMat);
        if (instance.sheetHeavyMat != null) into.Add(instance.sheetHeavyMat);
        if (instance.sheetDustMat != null) into.Add(instance.sheetDustMat);
    }
    void EmitTiny(ParticleSystem ps, Vector3 pos, float size)
    {
        if (ps == null) return;
        var ep = new ParticleSystem.EmitParams();
        ep.position = pos; ep.velocity = Vector3.zero; ep.startLifetime = HitVfxCfg.Get().prewarmLifeS; ep.startSize = size; ep.startColor = (Color32)Color.white; /* DATALAYER b12 */
        ps.Emit(ep, 1);
    }

    class Foot { public DuelFighter f; public Vector3 prev; public bool has; public float stride, cool; public int side = 1; }
    struct Late { public float at; public Vector3 anchor, along, side; public float strength; public int n; }

    ClashFxTable.Data cfg;
    bool ready;
    ParticleSystem sparkPS, glintPS, dustPS;
    Material sparkMat, glintMat, dustMat;
    Texture2D sparkTex, dustTex;
    // flipbook sheets (null = not available, procedural only)
    ParticleSystem sheetSparkR, sheetSparkL, sheetHeavyR, sheetHeavyL, sheetDust;
    Material sheetSparkMat, sheetHeavyMat, sheetDustMat;
    readonly List<Late> late = new List<Late>();
    readonly List<Foot> feet = new List<Foot>();
    float refreshAt, sparkReadyAt, dustReadyAt;
    uint seed = 1;

    float Rand() { unchecked { seed = seed * 1664525u + 1013904223u; } return seed / 4294967296f; }

    void Awake()
    {
        cfg = ClashFxTable.Get();
        if (cfg.enabled == 0 || cfg.sparks.poolSize <= 0 || cfg.dust.poolSize <= 0) return;
        Shader sh = null;
        for (int i = 0; i < cfg.shaders.Length && sh == null; i++) sh = Shader.Find(cfg.shaders[i]);
        if (sh == null) { Debug.LogWarning("ClashFx: no usable particle shader found from clash_fx.json 'shaders'; disabled."); return; }

        sparkTex = MakeTexture(cfg.sparks.textureSize, cfg.sparks.textureEdgePower, false);
        dustTex = MakeTexture(cfg.dust.textureSize, cfg.dust.textureEdgePower, true);
        sparkMat = MakeMaterial(sh, sparkTex); glintMat = MakeMaterial(sh, sparkTex); dustMat = MakeMaterial(sh, dustTex);

        sparkPS = MakeSystem("ClashSparks", sparkMat, cfg.sparks.poolSize, cfg.sparks.gravity);
        var sr = sparkPS.GetComponent<ParticleSystemRenderer>();
        sr.renderMode = ParticleSystemRenderMode.Stretch; sr.lengthScale = cfg.sparks.lengthScale; sr.velocityScale = cfg.sparks.velocityScale; sr.cameraVelocityScale = 0f; sr.maxParticleSize = cfg.sparks.maxParticleSize;
        SetColorOverLife(sparkPS, ArcFx.SparkHot(cfg.sparks.colorHot), ArcFx.SparkCool(cfg.sparks.colorCool), cfg.sparks.coolAt, 0f);   // ARCS v105

        glintPS = MakeSystem("ClashGlint", glintMat, cfg.sparks.glintPool, 0f);
        var gr = glintPS.GetComponent<ParticleSystemRenderer>();
        gr.renderMode = ParticleSystemRenderMode.Billboard; gr.maxParticleSize = cfg.sparks.maxParticleSize;
        SetColorOverLife(glintPS, Color.white, Color.white, 0.5f, 0f);

        dustPS = MakeSystem("ClashDust", dustMat, cfg.dust.poolSize, cfg.dust.gravity);
        var dr = dustPS.GetComponent<ParticleSystemRenderer>();
        dr.renderMode = ParticleSystemRenderMode.Billboard; dr.maxParticleSize = 1f;
        var so = dustPS.sizeOverLifetime; so.enabled = true;
        so.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, Mathf.Max(0.01f, cfg.dust.sizeGrowth)));
        var lv = dustPS.limitVelocityOverLifetime; lv.enabled = true; lv.limit = new ParticleSystem.MinMaxCurve(cfg.dust.dragLimit); lv.dampen = Mathf.Clamp01(cfg.dust.dragDampen);
        SetColorOverLife(dustPS, Color.white, Color.white, 0.5f, Mathf.Clamp(cfg.dust.fadeInFraction, 0.001f, 0.5f));

        ready = true;
        try { BuildSheets(); } catch (Exception ex) { Debug.LogWarning("ClashFx: flipbook sheets disabled: " + ex.Message); }
    }

    // ---- flipbook sheets ----
    static Texture2D Pick(Texture2D held, string assetPath, string resourceName)
    {
        if (held != null) return held;
#if UNITY_EDITOR
        if (!string.IsNullOrEmpty(assetPath)) { var t = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath); if (t != null) return t; }
#endif
        if (!string.IsNullOrEmpty(resourceName)) return Resources.Load<Texture2D>(resourceName);
        return null;
    }

    static Material MakeSheetMaterial(Shader sh, Texture2D tex, Texture2D motion, int tx, int ty, float mvStrength, float intensity, float soft, Color tint, bool additive)
    {
        var m = new Material(sh);
        m.SetTexture("_MainTex", tex);
        if (motion != null) m.SetTexture("_MotionTex", motion);
        m.SetVector("_TilesXY", new Vector4(tx, ty, 0f, 0f));
        m.SetFloat("_MVStrength", motion != null ? mvStrength : 0f);
        m.SetFloat("_Intensity", intensity); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", soft);
        m.SetColor("_Tint", tint);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", additive ? 1f : 10f); // BlendMode.One = 1, OneMinusSrcAlpha = 10
        m.renderQueue = ClashFxTable.Get().renderQueue; /* DATALAYER b12 */
        return m;
    }

    ParticleSystem MakeSheetSystem(string n, Material m, int max, int tx, int ty, bool flipX, bool blend, bool sorted)
    {
        var ps = MakeSystem(n, m, max, 0f);
        var ts = ps.textureSheetAnimation; ts.enabled = true; ts.mode = ParticleSystemAnimationMode.Grid;
        ts.numTilesX = tx; ts.numTilesY = ty; ts.animation = ParticleSystemAnimationType.WholeSheet;
        ts.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
        ts.startFrame = new ParticleSystem.MinMaxCurve(0f); ts.cycleCount = 1;
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard; r.maxParticleSize = 4f;
        r.flip = new Vector3(flipX ? 1f : 0f, 0f, 0f);
        r.sortMode = sorted ? ParticleSystemSortMode.Distance : ParticleSystemSortMode.None;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        if (blend) r.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { // TEXCOORD0.xy frame A, .zw frame B, TEXCOORD1.x blend (see ClashFlipbook.shader)
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.AnimBlend });
        return ps;
    }

    void BuildSheets()
    {
        var sc = cfg.sheets;
        if (sc == null || sc.enabled == 0) return;
        var sh = string.IsNullOrEmpty(sc.shader) ? null : Shader.Find(sc.shader);
        if (sh == null) { Debug.LogWarning("ClashFx: sheet shader '" + sc.shader + "' not found; flipbook sheets disabled (procedural FX only)."); return; }
        var refs = FindFirstObjectByType<ClashFxSheetRefs>();
        var sp = sc.sparks;
        if (sp.enabled != 0 && sp.tilesX > 0 && sp.tilesY > 0 && sp.poolSize > 0)
        {
            var tex = Pick(refs != null ? refs.sparks : null, sp.texture, sp.resource);
            var texH = Pick(refs != null ? refs.sparksHeavy : null, sp.textureHeavy, sp.resourceHeavy);
            if (tex == null) Debug.LogWarning("ClashFx: spark sheet texture missing; procedural sparks only.");
            else
            {
                sheetSparkMat = MakeSheetMaterial(sh, tex, null, sp.tilesX, sp.tilesY, 0f, sp.intensity, sp.softDistance, sp.tint, true);
                sheetSparkR = MakeSheetSystem("SheetSparksR", sheetSparkMat, sp.poolSize, sp.tilesX, sp.tilesY, false, false, false);
                sheetSparkL = MakeSheetSystem("SheetSparksL", sheetSparkMat, sp.poolSize, sp.tilesX, sp.tilesY, true, false, false);
                if (texH != null)
                {
                    sheetHeavyMat = MakeSheetMaterial(sh, texH, null, sp.tilesX, sp.tilesY, 0f, sp.intensity, sp.softDistance, sp.tint, true);
                    sheetHeavyR = MakeSheetSystem("SheetSparksHeavyR", sheetHeavyMat, sp.poolSize, sp.tilesX, sp.tilesY, false, false, false);
                    sheetHeavyL = MakeSheetSystem("SheetSparksHeavyL", sheetHeavyMat, sp.poolSize, sp.tilesX, sp.tilesY, true, false, false);
                }
            }
        }
        var du = sc.dust;
        if (du.enabled != 0 && du.tilesX > 0 && du.tilesY > 0 && du.poolSize > 0)
        {
            var tex = Pick(refs != null ? refs.dust : null, du.texture, du.resource);
            if (tex == null) Debug.LogWarning("ClashFx: dust sheet texture missing; procedural dust only.");
            else
            {
                var mv = du.frameBlend != 0 ? Pick(refs != null ? refs.dustMotion : null, du.motion, du.resourceMotion) : null;
                sheetDustMat = MakeSheetMaterial(sh, tex, mv, du.tilesX, du.tilesY, du.mvStrength, du.intensity, du.softDistance, Color.white, false);
                sheetDust = MakeSheetSystem("SheetDust", sheetDustMat, du.poolSize, du.tilesX, du.tilesY, false, du.frameBlend != 0 && mv != null, true);
            }
        }
    }

    // one baked spark spray; mirrored (left systems) when the force travels toward screen-left
    void SpawnSheetSparks(Vector3 anchor, Vector3 along, float strength, bool heavy)
    {
        var sp = cfg.sheets.sparks;
        bool useHeavy = heavy && sheetHeavyR != null;
        var psR = useHeavy ? sheetHeavyR : sheetSparkR; var psL = useHeavy ? sheetHeavyL : sheetSparkL;
        if (psR == null || psL == null) return;
        Vector3 right = Vector3.right, up = Vector3.up;
        var cam = Camera.main;
        if (cam != null) { right = cam.transform.right; up = cam.transform.up; }
        float sgn = Vector3.Dot(along, right) >= 0f ? 1f : -1f;
        float size = (sp.size + strength * sp.sizePerStrength) * (useHeavy ? Mathf.Max(0.1f, sp.heavySizeScale) : 1f);
        // the quad centre sits (0.5 - impactU) of a cell toward the spray side and (impactV - 0.5) above the impact point
        Vector3 centre = anchor + right * (sgn * (0.5f - sp.impactU) * size) + up * ((sp.impactV - 0.5f) * size);
        var ep = new ParticleSystem.EmitParams();
        ep.position = centre; ep.velocity = Vector3.zero; ep.startLifetime = Mathf.Max(0.05f, sp.lifetime); ep.startSize = size;
        ep.startColor = (Color32)Color.white;
        (sgn >= 0f ? psR : psL).Emit(ep, 1);
    }

    // one baked ground puff at a foot position (dust sheet pivot: ground line at groundV of the cell)
    void SpawnSheetDust(Vector3 at, float strength)
    {
        if (sheetDust == null || strength < cfg.sheets.dust.minStrength) return;
        var du = cfg.sheets.dust;
        float size = du.size + strength * du.sizePerStrength;
        var ep = new ParticleSystem.EmitParams();
        ep.position = new Vector3(at.x, at.y + cfg.dust.floorOffset + (du.groundV - 0.5f) * size, at.z);
        ep.velocity = Vector3.zero; ep.startLifetime = Mathf.Max(0.05f, du.lifetime); ep.startSize = size;
        ep.startColor = (Color32)du.tint;
        sheetDust.Emit(ep, 1);
    }

    void OnEnable() { ContactRelay.OnContact += OnContact; ContactRelay.OnAction += OnAction; }
    void OnDisable() { ContactRelay.OnContact -= OnContact; ContactRelay.OnAction -= OnAction; }
    void OnDestroy() { if (instance == this) instance = null; }

    // ---- construction helpers ----
    static Texture2D MakeTexture(int size, float edgePower, bool mottled)
    {
        size = Mathf.Max(4, size);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;
        var px = new Color32[size * size];
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x - half) / half, v = (y - half) / half;
                float edge = Mathf.Max(0f, 1f - u * u - v * v);
                float a = Mathf.Pow(edge, edgePower);
                if (mottled) { float s = Mathf.Sin(x * 0.9f + Mathf.Sin(y * 0.6f)); a *= 0.65f + 0.35f * s * s; }
                px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
            }
        tex.SetPixels32(px); tex.Apply(false, true);
        return tex;
    }

    static Material MakeMaterial(Shader sh, Texture2D tex)
    {
        var m = new Material(sh);
        m.mainTexture = tex;
        if (m.HasProperty("_UnlitColorMap")) m.SetTexture("_UnlitColorMap", tex);
        return m;
    }

    ParticleSystem MakeSystem(string n, Material m, int max, float gravity)
    {
        var go = new GameObject(n);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = Mathf.Max(1, max); main.gravityModifier = gravity; main.startSpeed = 0f;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = false;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        ps.Play();   // emission is off, so nothing spawns on its own; a playing system keeps simulating Emit() particles after the first burst dies out
        return ps;
    }

    // colour hot -> cool by coolAt, then held; alpha fades in over fadeIn (0 = full from birth) and out to 0 at end of life
    static void SetColorOverLife(ParticleSystem ps, Color hot, Color cool, float coolAt, float fadeIn)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        var ck = new[] { new GradientColorKey(hot, 0f), new GradientColorKey(cool, Mathf.Clamp(coolAt, 0.01f, 0.99f)), new GradientColorKey(cool, 1f) };
        GradientAlphaKey[] ak;
        if (fadeIn > 0f) ak = new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, fadeIn), new GradientAlphaKey(0f, 1f) };
        else ak = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(ClashFxTable.Get().sparks.colorAlphaMid, ClashFxTable.Get().sparks.colorAlphaMidAt), new GradientAlphaKey(0f, 1f) }; /* DATALAYER b12 */
        g.SetKeys(ck, ak);
        col.color = g;
    }

    // ---- hooks ----
    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (!ready || a == null || d == null) return;
        ClashFxTable.Row row = null;
        for (int i = 0; i < cfg.rows.Length; i++)
        {
            var r = cfg.rows[i];
            if (r == null || r.type != type) continue;
            if (r.heavy >= 0 && (r.heavy == 1) != heavy) continue;
            if (r.perfect >= 0 && (r.perfect == 1) != perfect) continue;
            if (!RosterResolver.NameFilter(r.targetContains, d.name)) continue;   // HCKIT2 v93
            if (!string.IsNullOrEmpty(r.attackerWeapon) && WeaponHooks.Of(a).id != r.attackerWeapon) continue;   // SOBEKCLAWS
            row = r; break;
        }
        if (row == null) return;

        Vector3 along = d.transform.position - a.transform.position; along.y = 0f;
        if (along.sqrMagnitude < 1e-4f) { along = a.transform.forward; along.y = 0f; }
        if (along.sqrMagnitude < 1e-4f) along = Vector3.forward;
        along.Normalize();
        Vector3 side = new Vector3(along.z, 0f, -along.x);

        if (row.spark > 0f && Time.time >= sparkReadyAt)
        {
            sparkReadyAt = Time.time + cfg.sparks.cooldown;
            var sp = cfg.sparks;
            Vector3 anchor = d.transform.position + Vector3.up * (sp.anchorHeight * FighterScale.Of(d)) - along * sp.anchorBack;
        anchor = PhysWeapon.SparkAnchor(a, d, type, false, anchor); /*PHYSWEAPON v106*/ anchor = ArcFx.ContactPoint(a, d, type, false, anchor);   // ARCS v105
            int n = sp.countBase + Mathf.RoundToInt(Rand() * (sp.countRandomBase + row.spark * sp.countRandomPerStrength)); n = Mathf.RoundToInt(n * ArcFx.CountMul());   // ARCS v105
            if (sheetSparkR != null)
            {
                var AS = ArcFx.SparksOn(); if (AS == null || AS.flipbook) SpawnSheetSparks(anchor, along, row.spark, heavy);   // ARCS v105
                n = Mathf.RoundToInt(n * Mathf.Max(0f, AS == null || AS.flipbook ? cfg.sheets.sparks.proceduralCountScale : AS.proceduralCountScale)); // 0 = the baked sheet replaces the procedural streaks
            }
            int now = n > 0 ? Mathf.Max(1, Mathf.RoundToInt(n * (sp.firstWaveMin + Rand() * sp.firstWaveRange))) : 0;
            if (now > 0) { SpawnSparks(anchor, along, side, row.spark, now); SpawnGlint(anchor, row.spark); }
            if (n > now) late.Add(new Late { at = Time.time + sp.lateDelay, anchor = anchor, along = along, side = side, strength = row.spark, n = n - now });
        }
        if (row.dust > 0f && Time.time >= dustReadyAt)
        {
            dustReadyAt = Time.time + cfg.dust.cooldown;
            BlockDust(d.transform.position, along, side, row.dust, row.feet);
        }
    }

    void OnAction(DuelFighter f, string action)
    {
        if (!ready || f == null) return;
        for (int i = 0; i < cfg.actions.Length; i++)
        {
            var r = cfg.actions[i];
            if (r == null || r.action != action || r.dust <= 0f) continue;
            Vector3 fwd = f.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            // for the rear-foot rule "along" points the way the force travels; a fighter's own back is behind his facing, so use -forward as the rear direction
            BlockDust(f.transform.position, -fwd, new Vector3(-fwd.z, 0f, fwd.x), r.dust, r.feet);
            break;
        }
    }

    // ---- emitters ----
    void SpawnSparks(Vector3 anchor, Vector3 along, Vector3 side, float strength, int n)
    {
        var sp = cfg.sparks;
        for (int j = 0; j < n; j++)
        {
            Vector3 at = anchor + side * ((Rand() - 0.5f) * 2f * sp.anchorJitter);
            Vector3 dir = along * ((Rand() - sp.alongBias) * sp.alongScale) + side * ((Rand() - 0.5f) * sp.sideSpread * ArcFx.SideMul()) + Vector3.up * (sp.upMin + Rand() * sp.upRange * ArcFx.UpMul());   // ARCS v105
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.up;
            dir.Normalize();
            float r1 = Rand(), speed = (sp.speedMin + r1 * Rand() * sp.speedRange) * (sp.speedBase + strength * sp.speedPerStrength);
            float life = sp.lifeMin + Rand() * Rand() * sp.lifeRange + strength * sp.lifePerStrength;
            float size = sp.sizeMin + Rand() * Rand() * sp.sizeRange;
            var ep = new ParticleSystem.EmitParams();
            ep.position = at; ep.velocity = dir * speed; ep.startLifetime = life; ep.startSize = size;
            ep.startColor = (Color32)new Color(1f, 1f, 1f, sp.opacity);
            sparkPS.Emit(ep, 1);
        }
    }

    void SpawnGlint(Vector3 at, float strength)
    {
        var sp = cfg.sparks;
        var ep = new ParticleSystem.EmitParams();
        ep.position = at; ep.velocity = Vector3.zero; ep.startLifetime = sp.glintLife; ep.startSize = sp.glintSize + strength * sp.glintSizePerStrength;
        ep.startColor = (Color32)sp.glintColor;
        glintPS.Emit(ep, 1);
    }

    // along = the way the force travels (attacker -> defender); the rear foot is the one further along it
    void BlockDust(Vector3 pos, Vector3 along, Vector3 side, float strength, string feetRule)
    {
        var b = cfg.dust.block;
        int n = Mathf.RoundToInt(b.countBase + strength * b.countPerStrength);
        if (sheetDust != null)
        {
            n = Mathf.RoundToInt(n * Mathf.Max(0f, cfg.sheets.dust.proceduralCountScale)); // 0 = the baked puff replaces the procedural grains
            if (feetRule == "both") { SpawnSheetDust(pos + side * b.footSide, strength); SpawnSheetDust(pos - side * b.footSide, strength); }
            else SpawnSheetDust(pos + along * b.footBack, strength);
        }
        float speed = b.speedBase + strength * b.speedPerStrength, lift = b.liftBase + strength * b.liftPerStrength, size = b.sizeBase + strength * b.sizePerStrength;
        if (feetRule == "both")
        {
            SpawnDust(pos + side * b.footSide, n, b.radius, speed, lift, size);
            SpawnDust(pos - side * b.footSide, n, b.radius, speed, lift, size);
        }
        else SpawnDust(pos + along * b.footBack, n, b.radius, speed, lift, size);
    }

    void SpawnDust(Vector3 at, int n, float radius, float speed, float lift, float size)
    {
        var dc = cfg.dust;
        for (int j = 0; j < n; j++)
        {
            float ang = Rand() * 6.2831853f, s = Mathf.Sin(ang), c = Mathf.Cos(ang);
            float tint = 1f + (Rand() - 0.5f) * 2f * dc.colorJitter;
            var ep = new ParticleSystem.EmitParams();
            ep.position = new Vector3(at.x + s * radius, at.y + dc.floorOffset, at.z + c * radius);
            ep.velocity = new Vector3(s * speed, lift, c * speed);
            ep.startLifetime = dc.lifetime * (1f - dc.lifetimeJitter * Rand());
            ep.startSize = size;
            ep.startColor = (Color32)new Color(Mathf.Clamp01(dc.color.r * tint), Mathf.Clamp01(dc.color.g * tint), Mathf.Clamp01(dc.color.b * tint), dc.color.a);
            dustPS.Emit(ep, 1);
        }
    }

    // ---- per-frame: late sparks + foot-plant heuristic ----
    void Update()
    {
        if (!ready) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        for (int i = late.Count - 1; i >= 0; i--)
        {
            var l = late[i];
            if (Time.time < l.at) continue;
            int k = Mathf.Max(1, Mathf.RoundToInt(l.n * 0.6f));
            SpawnSparks(l.anchor, l.along, l.side, l.strength, k);
            if (l.n > k) { l.n -= k; l.at = Time.time + cfg.sparks.lateDelay; late[i] = l; }
            else late.RemoveAt(i);
        }

        var h = cfg.dust.heuristic;
        if (h.enabled == 0) return;
        if (Time.time >= refreshAt)
        {
            refreshAt = Time.time + Mathf.Max(0.1f, cfg.fighterRefreshSeconds);
            RefreshFighters();
        }
        for (int i = 0; i < feet.Count; i++)
        {
            var ft = feet[i];
            if (ft.f == null) continue;
            Vector3 p = ft.f.transform.position;
            ft.cool = Mathf.Max(0f, ft.cool - dt);
            if (ft.has)
            {
                Vector3 delta = p - ft.prev; delta.y = 0f;
                float dist = delta.magnitude;
                float speed = dist / dt;
                bool dead = h.skipWhenDead != 0 && ft.f.Dead;
                if (dist < h.maxStepMeters && speed >= h.minSpeed && !dead)
                {
                    ft.stride += dist;
                    float need = speed >= h.runSpeed ? h.runStride : h.walkStride;
                    if (ft.stride >= need && ft.cool <= 0f)
                    {
                        ft.stride = 0f; ft.cool = h.plantCooldown; ft.side = -ft.side;
                        var t = ft.f.transform;
                        Vector3 fp = p + t.right * (h.footSide * ft.side) + t.forward * h.footForward;
                        var pl = cfg.dust.plant;
                        SpawnDust(fp, pl.count, pl.radius, pl.speed, pl.lift, pl.size);
                    }
                }
                else if (speed < h.minSpeed) ft.stride = 0f;
            }
            ft.prev = p; ft.has = true;
        }
    }

    void RefreshFighters()
    {
        var all = FindObjectsByType<DuelFighter>(FindObjectsSortMode.None);
        for (int i = feet.Count - 1; i >= 0; i--) if (feet[i].f == null) feet.RemoveAt(i);
        for (int i = 0; i < all.Length; i++)
        {
            bool known = false;
            for (int j = 0; j < feet.Count; j++) if (feet[j].f == all[i]) { known = true; break; }
            if (!known) feet.Add(new Foot { f = all[i] });
        }
    }
}
