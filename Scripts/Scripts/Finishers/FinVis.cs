using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// FINVIS v1: finisher blood v2 (Dom: "impactful with nice graphics"; Goose rated the old blood 4.5/10: flat starburst card, red X, weak read of the cut). ALL of it is behind
// finishers.json gore.finisherVfx.v2 (default false = today's look, every hook is `if (FinVis.On)`). Textures are V1's own blood art on disk (StreamingAssets/finvis, built by make_textures.py).
//   1. FLOOR POOLS   V1 src/finisher-blood.ts as HDRP decals: up to 80 stains that GROW (target' = sqrt(target^2 + amount), radius eases to target at 2.2 /s), merge within 0.2 m, a wound seeps
//                    a pool under itself when it is below 0.65 m, each landed droplet adds a little. Lit HDRP/Decal: colour + normal map (meniscus) + wet smoothness.
//   2. DROPLETS      lit HDRP/Lit mesh particles (small dark-red spheres stretched along the velocity), gravity, short life, world collision; the landing feeds the pools (OnParticleCollision).
//   3. NO RED X      FinisherCleave's flat quad line + patches + drips (the X on the crown) are replaced by 4.
//   4. CUT REVEAL    a thin wet blood line that draws itself along the cut edge (the crown arc, or a ring round the stump) then thickens, then a few drips run down. Lit decals on the body.
// Presentation only, no sim, no RNG of the sim. Falls back silently to the old look when HDRP/Decal, HDRP/Lit or the textures are missing.
[Serializable] public class FinVisPoolCfg
{
    public bool enabled = true;
    public int max = 80;                         // V1 stains
    public float startRadiusM = 0.035f, startTargetM = 0.055f, maxRadiusM = 0.85f, growPerS = 2.2f;
    public float seepPerS = 0.08f, seepBelowM = 0.65f, seepMinSeconds = 0.6f, seepRefRate = 78f;   // seep amount per second x strength; strength = the spray's peak rate / seepRefRate (V1 burst 78)
    public float landAmount = 0.006f, mergeM = 0.2f;
    public float squashBase = 0.7f, squashSin = 0.2f;   // V1: scale y = r * (0.7 + 0.2 sin^2(angle))
    public float lateSeedS = 7f, lateSeedAmount = 0.3f, sessionGapS = 12f;
    public float lifeS = 90f, fadeS = 4f, depthM = 0.08f, liftM = 0.02f, drawDistanceM = 40f;
    public float smoothness = 0.85f, minNormalY = 0.6f, rayUpM = 0.3f, rayDownM = 0.3f;
    public int maxLandPerFrame = 12;
    public float[] tint = { 1f, 1f, 1f, 1f };
    public string[] poolFiles = { "floor-pool", "floor-pool-b" };
    public string[] splashFiles = { "floor-splash", "floor-splash-b", "floor-splash-c", "floor-splash-d" };
}
[Serializable] public class FinVisDropCfg
{
    public bool enabled = true, keepStrands = true;   // keepStrands: the old unlit stretched strands stay as the motion read; false = droplets only
    public float rateMul = 0.35f, speedMul = 1f, coneMul = 1f, gravityMs2 = 9.81f, stretch = 2.2f;
    public float[] size = { 0.012f, 0.03f }, life = { 0.7f, 1.3f };
    public float[] color = { 0.20f, 0.012f, 0.016f, 1f };
    public float smoothness = 0.92f, metallic = 0f;
    public int maxParticles = 160, maxCollisionShapes = 256;
    public string collisionQuality = "Medium";   // High | Medium | Low
    public string[] collisionLayers = new string[0];   // layer names the droplets collide with; empty = everything (the victim's own colliders included: then a droplet dies on the body). Set e.g. ["Default"] once the floor layer is known
}
[Serializable] public class FinVisBodyCfg
{
    public bool enabled = true;                  // Splash(): the starburst quads become lit wound decals (V1 wound art + normal map)
    public string[] files = { "blood-wound-b", "blood-wound-c", "blood-wound-d" };
    public float smoothness = 0.8f, depthM = 0.12f, sizeMul = 1f, fade = 1f;
}
[Serializable] public class FinVisRevealCfg
{
    public bool enabled = true;
    public float revealS = 0.28f, thickenS = 0.9f, startWidthMul = 0.35f, endWidthMul = 1.0f, lengthPad = 1.35f, depthM = 0.09f;
    public float lineWidthM = 0.022f;            // arc / ring line width (the crown row's cleave.widthM is used for the arc when > 0)
    public int ringSegments = 10; public float ringRadiusMul = 1f;
    public float[] lineColor = { 0.11f, 0.0f, 0.008f, 1f };
    public float lineSmoothness = 0.9f;
    public int drips = 5; public float dripDelayS = 0.35f, dripSpreadS = 0.5f, dripGrowS = 1.4f, dripWidthM = 0.035f, dripLengthM = 0.2f;
    public string[] dripFiles = { "blood-wound-b", "blood-wound-c" };
    public int seed = 17;
}
[Serializable] public class FinVisCfg
{
    public bool v2 = false;                      // THE switch. Off = old finisher blood exactly.
    public string dir = "finvis";                // StreamingAssets subfolder with the png files
    public FinVisPoolCfg pools = new FinVisPoolCfg();
    public FinVisDropCfg droplets = new FinVisDropCfg();
    public FinVisBodyCfg body = new FinVisBodyCfg();
    public FinVisRevealCfg reveal = new FinVisRevealCfg();
}

public static class FinVis
{
    public static FinVisCfg Cfg() { var f = FinisherConfig.Get(); return f != null && f.gore != null ? f.gore.finisherVfx : null; }
    public static bool On { get { var c = Cfg(); return c != null && c.v2; } }

    static readonly Dictionary<string, Texture2D> tex = new Dictionary<string, Texture2D>();
    static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
    static Shader decalShader, litShader; static bool shaderLooked, warned; static Material dropMat; static Mesh dropMesh;
    static readonly List<PoolStain> stains = new List<PoolStain>();
    static readonly List<Seep> seeps = new List<Seep>();
    static readonly List<Beat> beats = new List<Beat>();
    static readonly RaycastHit[] hits = new RaycastHit[8];
    static FinVisDriver driver; static int stainCursor, serial, landThisFrame, sessionStains; static float lastRegister = -999f, sessionStart; static bool lateSeeded;
    static readonly List<Vector3> lateSources = new List<Vector3>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { tex.Clear(); mats.Clear(); decalShader = litShader = null; shaderLooked = warned = false; dropMat = null; dropMesh = null; stains.Clear(); seeps.Clear(); beats.Clear(); driver = null; stainCursor = serial = landThisFrame = sessionStains = 0; lastRegister = -999f; lateSeeded = false; lateSources.Clear(); }
    // INT100b: the reload of the duel scene (rematch / balance fights) also drops the pools, seeps, beats and the driver (their scene objects are gone); shader / texture caches are kept (no regeneration hitch)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void HookSceneReset() { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneReset; UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneReset; }
    static void OnSceneReset(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        for (int i = 0; i < stains.Count; i++) if (stains[i] != null && stains[i].go != null) UnityEngine.Object.Destroy(stains[i].go);
        stains.Clear(); seeps.Clear(); beats.Clear(); driver = null; stainCursor = serial = landThisFrame = sessionStains = 0; lastRegister = -999f; lateSeeded = false; lateSources.Clear();
        WoundDecals.SceneReset();
    }

    class PoolStain { public GameObject go; public DecalProjector p; public Vector3 pos, normal; public float radius, target, angle, squash, born; public bool live; }
    class Seep { public Transform tr; public float strength, until; public Vector3 lastFloor; public bool has; }
    class Beat { public DecalProjector p; public Transform t; public float t0, dur, w0, w1, len, h0, h1; public bool grow; public bool started; }

    // ---------------- assets ----------------
    static Texture2D Tex(string name, bool linear)
    {
        string key = name + (linear ? "#lin" : "");
        Texture2D t; if (tex.TryGetValue(key, out t)) return t;
        t = null;
        try
        {
            var c = Cfg(); string p = Path.Combine(Application.streamingAssetsPath, c != null ? c.dir : "finvis"); p = Path.Combine(p, name + ".png");
            if (File.Exists(p))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "FinVis_" + key, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.DontUnloadUnusedAsset };
                if (!t.LoadImage(File.ReadAllBytes(p), true)) { UnityEngine.Object.Destroy(t); t = null; }
            }
        }
        catch (Exception e) { Debug.LogWarning("[FinVis] texture " + name + ": " + e.Message); t = null; }
        tex[key] = t; return t;
    }

    static bool Shaders()
    {
        if (!shaderLooked) { shaderLooked = true; decalShader = Shader.Find("HDRP/Decal"); litShader = Shader.Find("HDRP/Lit"); if (decalShader == null && !warned) { warned = true; Debug.LogWarning("[FinVis] HDRP/Decal missing from the player: finisher blood v2 falls back to the old look (add it to Always Included Shaders / Resources/FinVisDecalRef)."); } }
        return decalShader != null;
    }

    /// <summary>One lit HDRP/Decal material per key: colour map (+ optional normal map, wet smoothness). Resources/FinVisDecalRef (made by the editor menu) keeps the shader variants in a build; Shader.Find is the fallback. Null = not possible.</summary>
    static Material DecalMat(string key, Texture2D color, Texture2D normal, float smoothness, float[] tint)
    {
        Material m; if (mats.TryGetValue(key, out m) && m != null) return m;
        if (!Shaders() || color == null) return null;
        var refMat = Resources.Load<Material>("FinVisDecalRef");
        m = refMat != null ? new Material(refMat) : new Material(decalShader);
        m.name = "FinVis_" + key; m.hideFlags = HideFlags.DontUnloadUnusedAsset; m.enableInstancing = true;
        m.EnableKeyword("_COLORMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO"); m.EnableKeyword("_MATERIAL_AFFECTS_MASKMAP");   // same keyword set as WoundDecals (QC1 v95: a runtime material needs them or the decal draws nothing)
        m.SetTexture("_BaseColorMap", color);
        Color c = tint != null && tint.Length >= 3 ? new Color(tint[0], tint[1], tint[2], tint.Length > 3 ? tint[3] : 1f) : Color.white; m.SetColor("_BaseColor", c);
        if (normal != null) { m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_NORMAL"); m.SetTexture("_NormalMap", normal); m.SetFloat("_NormalBlendSrc", 0f); m.SetFloat("_AffectNormal", 1f); }   // normal blend = the normal map's alpha (= the colour alpha, see make_textures.py)
        else m.SetFloat("_AffectNormal", 0f);
        m.SetFloat("_MaskBlendSrc", 0f);   // smoothness / metal blend by the colour alpha
        m.SetFloat("_Smoothness", Mathf.Clamp01(smoothness)); m.SetFloat("_Metallic", 0f); m.SetFloat("_AO", 1f);
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", 0f); m.SetFloat("_AffectSmoothness", 1f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", 1f);
        mats[key] = m; return m;
    }

    static Material FileMat(string file, float smoothness, float[] tint, bool withNormal)
    {
        var color = Tex(file, false); var nrm = withNormal ? Tex(file + "-n", true) : null;
        return DecalMat(file + (withNormal && nrm != null ? "+n" : "") + "@" + smoothness.ToString("0.00"), color, nrm, smoothness, tint);
    }

    /// <summary>PERF (FxPrewarm / GoreFx.CollectPrewarm): textures, decal + droplet materials, mesh are loaded now (v2 only), so the first cut does not hitch. Nothing is drawn.</summary>
    public static void Warm()
    {
        var c = Cfg(); if (c == null || !c.v2) return;
        try
        {
            if (c.pools.enabled) { foreach (var f in c.pools.poolFiles) FileMat(f, c.pools.smoothness, c.pools.tint, true); foreach (var f in c.pools.splashFiles) FileMat(f, c.pools.smoothness, c.pools.tint, true); }
            if (c.body.enabled) foreach (var f in c.body.files) FileMat(f, c.body.smoothness, null, true);
            if (c.reveal.enabled) foreach (var f in c.reveal.dripFiles) FileMat(f, c.body.smoothness, null, true);
            if (c.droplets.enabled) { DropMat(); DropMesh(); }
        }
        catch (Exception e) { Debug.LogWarning("[FinVis] warm: " + e.Message); }
    }

    static Mesh DropMesh()
    {
        if (dropMesh != null) return dropMesh;
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var mf = go.GetComponent<MeshFilter>(); dropMesh = mf != null ? mf.sharedMesh : null;
        UnityEngine.Object.Destroy(go); return dropMesh;
    }

    static Material DropMat()
    {
        if (dropMat != null) return dropMat;
        Shaders(); if (litShader == null) return null;
        var d = Cfg().droplets;
        dropMat = new Material(litShader) { name = "FinVis_Droplet", hideFlags = HideFlags.DontUnloadUnusedAsset, enableInstancing = true };
        dropMat.SetColor("_BaseColor", new Color(d.color[0], d.color[1], d.color[2], d.color.Length > 3 ? d.color[3] : 1f));
        dropMat.SetFloat("_Smoothness", d.smoothness); dropMat.SetFloat("_Metallic", d.metallic);
        return dropMat;
    }

    // ---------------- driver ----------------
    static void EnsureDriver()
    {
        if (driver != null) return;
        stains.Clear(); seeps.Clear(); beats.Clear(); sessionStains = 0; lateSeeded = false; lateSources.Clear();   // the old driver died with its scene: its decals went with it
        var go = new GameObject("FinVisDriver"); driver = go.AddComponent<FinVisDriver>();
    }

    static void Session()
    {
        float now = Time.time;
        if (now - lastRegister > Cfg().pools.sessionGapS) { sessionStart = now; sessionStains = 0; lateSeeded = false; lateSources.Clear(); }
        lastRegister = now;
    }

    static bool Floor(Vector3 from, float up, float down, out Vector3 point, out Vector3 normal)
    {
        point = from; normal = Vector3.up;
        int n = Physics.RaycastNonAlloc(from + Vector3.up * up, Vector3.down, hits, up + down, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; bool found = false; float minN = Cfg().pools.minNormalY;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (h.normal.y < minN) continue;
            if (h.distance < best) { best = h.distance; point = h.point; normal = h.normal; found = true; }
        }
        return found;
    }

    // ---------------- 1. floor pools ----------------
    static void Stain(Vector3 floorPoint, Vector3 normal, float amount, bool splash)
    {
        var pc = Cfg().pools; if (!pc.enabled) return;
        float m2 = pc.mergeM * pc.mergeM;
        for (int i = 0; i < stains.Count; i++)
        {
            var s = stains[i]; if (!s.live) continue;
            float dx = s.pos.x - floorPoint.x, dz = s.pos.z - floorPoint.z;
            if (dx * dx + dz * dz < m2 && Mathf.Abs(s.pos.y - floorPoint.y) < 0.3f) { s.target = Mathf.Min(pc.maxRadiusM, Mathf.Sqrt(s.target * s.target + amount)); return; }
        }
        PoolStain st;
        if (stains.Count < Mathf.Max(1, pc.max)) { st = new PoolStain(); stains.Add(st); }
        else { st = stains[stainCursor % stains.Count]; }
        stainCursor++;
        int shape = splash && pc.splashFiles.Length > 0 ? pc.poolFiles.Length + (stainCursor % pc.splashFiles.Length) : (pc.poolFiles.Length > 0 ? stainCursor % pc.poolFiles.Length : 0);
        string file = shape < pc.poolFiles.Length ? pc.poolFiles[shape] : pc.splashFiles[(shape - pc.poolFiles.Length) % Mathf.Max(1, pc.splashFiles.Length)];
        var mat = FileMat(file, pc.smoothness, pc.tint, true); if (mat == null) return;
        if (st.go == null)
        {
            st.go = new GameObject("FinisherBloodPool"); st.go.SetActive(false);
            st.p = st.go.AddComponent<DecalProjector>(); st.p.scaleMode = DecalScaleMode.ScaleInvariant; st.p.pivot = Vector3.zero;
            st.p.drawDistance = pc.drawDistanceM; st.p.fadeScale = 0.8f; st.p.startAngleFade = 60f; st.p.endAngleFade = 90f;
        }
        st.p.material = mat; st.p.fadeFactor = 1f;
        st.pos = floorPoint; st.normal = normal; st.radius = pc.startRadiusM; st.target = pc.startTargetM; st.angle = ++serial * 2.4f; st.born = Time.time; st.live = true;
        st.squash = pc.squashBase + pc.squashSin * Mathf.Pow(Mathf.Sin(st.angle), 2f);
        var yaw = new Vector3(Mathf.Sin(st.angle), 0f, Mathf.Cos(st.angle)); yaw = Vector3.ProjectOnPlane(yaw, normal); if (yaw.sqrMagnitude < 1e-6f) yaw = Vector3.forward;
        st.go.transform.SetPositionAndRotation(floorPoint + normal * (pc.depthM * 0.5f - pc.liftM), Quaternion.LookRotation(-normal, yaw.normalized));
        st.p.size = new Vector3(st.radius * 2f, st.radius * 2f * st.squash, pc.depthM);
        st.go.SetActive(true); sessionStains++;
    }

    /// <summary>Droplet landed (FinVisDropSink): a splash stain where it hit a floor-like surface that is not a fighter.</summary>
    internal static void Landed(Vector3 point, Vector3 normal, Component hit)
    {
        var c = Cfg(); if (c == null || !c.v2 || landThisFrame >= c.pools.maxLandPerFrame) return;
        if (normal.y < c.pools.minNormalY) return;
        if (hit != null && hit.GetComponentInParent<DuelFighter>() != null) return;
        landThisFrame++; Stain(point, normal, c.pools.landAmount, true);
    }

    static void StartSeep(Transform tr, float strength, float seconds)
    {
        var c = Cfg(); if (!c.pools.enabled || tr == null) return;
        Session(); EnsureDriver();
        seeps.Add(new Seep { tr = tr, strength = strength, until = Time.time + seconds });
    }

    // ---------------- 2. droplets ----------------
    static float Scaled(float v, float f) { return v * f; }
    static ParticleSystem.MinMaxCurve Scale(ParticleSystem.MinMaxCurve c, float f)
    {
        switch (c.mode)
        {
            case ParticleSystemCurveMode.Constant: return new ParticleSystem.MinMaxCurve(c.constant * f);
            case ParticleSystemCurveMode.TwoConstants: return new ParticleSystem.MinMaxCurve(c.constantMin * f, c.constantMax * f);
            case ParticleSystemCurveMode.Curve: return new ParticleSystem.MinMaxCurve(c.curveMultiplier * f, c.curve);
            default: return new ParticleSystem.MinMaxCurve(c.curveMultiplier * f, c.curveMin, c.curveMax);
        }
    }

    /// <summary>GoreFx.Spray hook (v2 only): lit droplet companion system on the spray emitter (same cone, speeds and rate curve as the strands, scaled by the json) + the wound seep under it.</summary>
    public static void OnSpray(GameObject go, ParticleSystem src, SprayCfg c, float secs)
    {
        var cfg = Cfg(); if (cfg == null || !cfg.v2 || go == null || src == null) return;
        try
        {
            var d = cfg.droplets;
            if (d.enabled && DropMat() != null && DropMesh() != null)
            {
                Session(); EnsureDriver();
                var dgo = new GameObject("FinisherDroplets"); dgo.transform.SetParent(go.transform, false);
                var ps = dgo.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var sm = src.main; var m = ps.main; m.playOnAwake = false; m.loop = false; m.duration = sm.duration; m.simulationSpace = ParticleSystemSimulationSpace.World;
                m.gravityModifier = d.gravityMs2 / 9.81f; m.maxParticles = Mathf.Max(8, d.maxParticles);
                m.startLifetime = new ParticleSystem.MinMaxCurve(d.life[0], d.life[1]);
                m.startSpeed = Scale(sm.startSpeed, d.speedMul);
                m.startSize3D = true;
                m.startSizeX = new ParticleSystem.MinMaxCurve(d.size[0], d.size[1]); m.startSizeY = new ParticleSystem.MinMaxCurve(d.size[0], d.size[1]); m.startSizeZ = new ParticleSystem.MinMaxCurve(d.size[0] * d.stretch, d.size[1] * d.stretch);
                var em = ps.emission; em.enabled = true; em.rateOverTime = Scale(src.emission.rateOverTime, d.rateMul);
                var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = c.coneDeg * d.coneMul; sh.radius = 0.01f;
                var r = dgo.GetComponent<ParticleSystemRenderer>(); r.renderMode = ParticleSystemRenderMode.Mesh; r.mesh = DropMesh(); r.sharedMaterial = DropMat(); r.alignment = ParticleSystemRenderSpace.Velocity;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                var col = ps.collision; col.enabled = true; col.type = ParticleSystemCollisionType.World; col.mode = ParticleSystemCollisionMode.Collision3D; col.dampen = 0f; col.bounce = 0f; col.lifetimeLoss = 1f;
                col.quality = d.collisionQuality == "High" ? ParticleSystemCollisionQuality.High : d.collisionQuality == "Low" ? ParticleSystemCollisionQuality.Low : ParticleSystemCollisionQuality.Medium;
                col.maxCollisionShapes = Mathf.Max(1, d.maxCollisionShapes); col.collidesWith = d.collisionLayers != null && d.collisionLayers.Length > 0 ? (LayerMask)LayerMask.GetMask(d.collisionLayers) : (LayerMask)(~0); col.sendCollisionMessages = cfg.pools.enabled; col.radiusScale = 1f;
                if (cfg.pools.enabled) dgo.AddComponent<FinVisDropSink>();
                ps.Play();
                if (!d.keepStrands) { var sr = go.GetComponent<ParticleSystemRenderer>(); if (sr != null) sr.enabled = false; }
            }
            if (cfg.pools.enabled && secs >= cfg.pools.seepMinSeconds)
            {
                float peak = c.rate; if (c.phaseRate != null) for (int i = 0; i < c.phaseRate.Length; i++) peak = Mathf.Max(peak, c.phaseRate[i]);
                StartSeep(go.transform, Mathf.Clamp(peak / Mathf.Max(1f, cfg.pools.seepRefRate), 0.1f, 2f), secs + c.life[1]);
            }
        }
        catch (Exception e) { Debug.LogWarning("[FinVis] spray hook: " + e.Message); }
    }

    // ---------------- body splats (Splash replacement) ----------------
    /// <summary>GoreFx.Splash hook (v2 only): the splat as a lit wound decal (V1 art + normal map) on the bone. False = could not (the caller keeps the old quad).</summary>
    public static bool BodySplat(Transform bone, Vector3 surfacePos, Vector3 outDir, Vector3 axis, float size, int variant, string loc)
    {
        var cfg = Cfg(); if (cfg == null || !cfg.v2 || !cfg.body.enabled || bone == null || cfg.body.files.Length == 0) return false;
        var b = cfg.body; var mat = FileMat(b.files[variant % b.files.Length], b.smoothness, null, true); if (mat == null) return false;
        var go = new GameObject("FinisherBloodBody_" + loc); go.SetActive(false);
        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, outDir); if (up.sqrMagnitude < 1e-4f) up = axis; up.Normalize();   // texture top = world up on the skin, the drips run down
        go.transform.SetPositionAndRotation(surfacePos, Quaternion.LookRotation(-outDir, up));
        go.transform.SetParent(bone, true);
        var p = go.AddComponent<DecalProjector>(); p.scaleMode = DecalScaleMode.ScaleInvariant;
        p.material = mat; p.size = new Vector3(size * b.sizeMul, size * b.sizeMul * 1.25f, Mathf.Max(0.02f, b.depthM)); p.pivot = Vector3.zero; p.fadeFactor = Mathf.Clamp01(b.fade);
        p.drawDistance = 25f; p.fadeScale = 0.8f; p.startAngleFade = 60f; p.endAngleFade = 90f;
        go.SetActive(true); return true;
    }

    // ---------------- 4. cut reveal ----------------
    static Material LineMat(Texture2D lineTex)
    {
        var r = Cfg().reveal; var c = r.lineColor;
        return DecalMat("cutline@" + c[0].ToString("0.00") + c[1].ToString("0.00") + c[2].ToString("0.00"), lineTex, null, r.lineSmoothness, c);
    }

    static Beat Line(Transform bone, Material mat, Vector3 pos, Vector3 outNormal, Vector3 along, float w, float len, float t0, float dur, float w0, float w1, float depth)
    {
        var go = new GameObject("FinisherCutLine"); go.SetActive(false);
        Vector3 up = Vector3.ProjectOnPlane(along, outNormal); if (up.sqrMagnitude < 1e-8f) up = Vector3.up; up.Normalize();
        go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-outNormal, up));
        go.transform.SetParent(bone, true);
        var p = go.AddComponent<DecalProjector>(); p.scaleMode = DecalScaleMode.ScaleInvariant; p.material = mat; p.pivot = Vector3.zero; p.fadeFactor = 1f;
        p.size = new Vector3(Mathf.Max(0.002f, w * w0), len, depth); p.drawDistance = 25f; p.fadeScale = 0.8f; p.startAngleFade = 60f; p.endAngleFade = 90f;
        var b = new Beat { p = p, t = go.transform, t0 = t0, dur = dur, w0 = w * w0, w1 = w * w1, len = len, h0 = len, h1 = len, grow = false };
        beats.Add(b); return b;
    }

    static void Drips(Transform bone, Vector3[] pts, Vector3[] nrms, float t0, FinVisRevealCfg r, int seedMix)
    {
        if (r.drips <= 0 || r.dripFiles.Length == 0 || pts.Length == 0) return;
        var rng = new System.Random(r.seed * 7919 + seedMix);
        for (int k = 0; k < r.drips; k++)
        {
            int i = rng.Next(pts.Length); var mat = FileMat(r.dripFiles[k % r.dripFiles.Length], Cfg().body.smoothness, null, true); if (mat == null) return;
            float w = r.dripWidthM * (0.7f + 0.6f * (float)rng.NextDouble()), h = r.dripLengthM * (0.7f + 0.6f * (float)rng.NextDouble());
            var go = new GameObject("FinisherCutDrip"); go.SetActive(false);
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, nrms[i]); if (up.sqrMagnitude < 1e-6f) up = Vector3.up; up.Normalize();   // blob at the top of the texture = on the cut, the run goes down
            go.transform.SetPositionAndRotation(pts[i], Quaternion.LookRotation(-nrms[i], up));
            go.transform.SetParent(bone, true);
            var p = go.AddComponent<DecalProjector>(); p.scaleMode = DecalScaleMode.ScaleInvariant; p.material = mat; p.fadeFactor = 1f;
            p.size = new Vector3(w, h * 0.2f, r.depthM); p.pivot = new Vector3(0f, -h * 0.1f, 0f); p.drawDistance = 25f; p.fadeScale = 0.8f; p.startAngleFade = 60f; p.endAngleFade = 90f;
            beats.Add(new Beat { p = p, t = go.transform, t0 = t0 + r.dripDelayS + (float)rng.NextDouble() * r.dripSpreadS, dur = r.dripGrowS, w0 = w, w1 = w, h0 = h * 0.2f, h1 = h, grow = true });
        }
    }

    /// <summary>The crown cleave (FinisherCleave.Apply, v2 only): the line over the skull draws itself along the arc, thickens, drips run. Returns the number of decals (0 = could not, the caller keeps the old wound).</summary>
    public static int CutArc(Transform bone, CleaveRow cl, Vector3 c, Vector3 fwd, Vector3 rightUnused, float rF, float rH, Texture2D lineTex, int seedHash)
    {
        var cfg = Cfg(); if (cfg == null || !cfg.v2 || !cfg.reveal.enabled || bone == null) return 0;
        var r = cfg.reveal; var mat = LineMat(lineTex); if (mat == null) return 0;
        EnsureDriver(); float now = Time.time;
        int n = Mathf.Max(2, cl.segments); float w = cl.widthM > 0f ? cl.widthM : r.lineWidthM;
        var pts = new Vector3[n]; var nrms = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.Lerp(cl.arcFromDeg, cl.arcToDeg, i / (float)n) * Mathf.Deg2Rad, a1 = Mathf.Lerp(cl.arcFromDeg, cl.arcToDeg, (i + 1) / (float)n) * Mathf.Deg2Rad, am = (a0 + a1) * 0.5f;
            Vector3 p0 = c + fwd * rF * Mathf.Cos(a0) + Vector3.up * rH * Mathf.Sin(a0), p1 = c + fwd * rF * Mathf.Cos(a1) + Vector3.up * rH * Mathf.Sin(a1);
            Vector3 pm = c + fwd * rF * Mathf.Cos(am) + Vector3.up * rH * Mathf.Sin(am);
            Vector3 nrm = (fwd * Mathf.Cos(am) / Mathf.Max(1e-3f, rF) + Vector3.up * Mathf.Sin(am) / Mathf.Max(1e-3f, rH)).normalized;
            Vector3 along = p1 - p0; float mag = along.magnitude; if (mag < 1e-5f) continue; along /= mag;
            pts[i] = pm; nrms[i] = nrm;
            Line(bone, mat, pm + nrm * cl.lift, nrm, along, w, mag * r.lengthPad, now + r.revealS * i / n, r.thickenS, r.startWidthMul, r.endWidthMul, r.depthM);
        }
        Drips(bone, pts, nrms, now + r.revealS, r, seedHash);
        return n + r.drips;
    }

    /// <summary>A ring of line segments round the stump (decapitation, run through, opened ...; MeshCut.Blood, v2 only), drawn round the cut over revealS, then thickened, then drips.</summary>
    public static int CutRing(Transform victim, Transform bone, Vector3 pos, Vector3 dir, float radiusM, Texture2D lineTex)
    {
        var cfg = Cfg(); if (cfg == null || !cfg.v2 || !cfg.reveal.enabled || bone == null) return 0;
        var r = cfg.reveal; var mat = LineMat(lineTex); if (mat == null) return 0;
        EnsureDriver(); float now = Time.time;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.up;
        Vector3 u = Vector3.ProjectOnPlane(victim != null ? victim.forward : Vector3.forward, dir); if (u.sqrMagnitude < 1e-4f) u = Vector3.ProjectOnPlane(Vector3.right, dir); u.Normalize();
        Vector3 v = Vector3.Cross(dir, u);
        int n = Mathf.Max(4, r.ringSegments); float rad = Mathf.Max(0.02f, radiusM * r.ringRadiusMul); float seg = 2f * Mathf.PI * rad / n * r.lengthPad;
        var pts = new Vector3[n]; var nrms = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float th = 2f * Mathf.PI * i / n; Vector3 radial = Mathf.Cos(th) * u + Mathf.Sin(th) * v; Vector3 tangent = Vector3.Cross(dir, radial);
            Vector3 at = pos + radial * rad; pts[i] = at; nrms[i] = radial;
            Line(bone, mat, at, radial, tangent, r.lineWidthM, seg, now + r.revealS * i / n, r.thickenS, r.startWidthMul, r.endWidthMul, r.depthM);
        }
        Drips(bone, pts, nrms, now + r.revealS, r, victim != null ? victim.name.GetHashCode() : 0);
        return n + r.drips;
    }

    // ---------------- per-frame ----------------
    internal static void Tick()
    {
        var cfg = Cfg(); if (cfg == null) return;
        float dt = Time.deltaTime, now = Time.time; landThisFrame = 0;
        if (dt <= 0f) return;
        var pc = cfg.pools;
        // seeps: a wound below seepBelowM seeps a pool under itself
        for (int i = seeps.Count - 1; i >= 0; i--)
        {
            var s = seeps[i]; if (s.tr == null || now > s.until) { seeps.RemoveAt(i); continue; }
            Vector3 fp, fn; if (!Floor(s.tr.position, pc.rayUpM, pc.seepBelowM + pc.rayDownM, out fp, out fn)) continue;
            s.lastFloor = fp; s.has = true;
            if (s.tr.position.y - fp.y < pc.seepBelowM) Stain(fp, fn, dt * pc.seepPerS * s.strength, false);
        }
        // V1: if nothing ever stained by lateSeedS, the wounds stain under themselves once
        if (!lateSeeded && sessionStart > 0f && now - sessionStart >= pc.lateSeedS)
        {
            lateSeeded = true;
            if (sessionStains == 0) for (int i = 0; i < seeps.Count; i++) if (seeps[i].has) Stain(seeps[i].lastFloor, Vector3.up, pc.lateSeedAmount, false);
        }
        // growth + fade
        for (int i = 0; i < stains.Count; i++)
        {
            var s = stains[i]; if (!s.live) continue;
            float age = now - s.born;
            if (age > pc.lifeS)
            {
                float f = 1f - (age - pc.lifeS) / Mathf.Max(0.1f, pc.fadeS);
                if (f <= 0f) { s.live = false; s.go.SetActive(false); continue; }
                s.p.fadeFactor = f;
            }
            float nr = s.radius + (s.target - s.radius) * (1f - Mathf.Exp(-dt * pc.growPerS));
            if (Mathf.Abs(nr - s.radius) > 1e-4f) { s.radius = nr; s.p.size = new Vector3(nr * 2f, nr * 2f * s.squash, pc.depthM); }
        }
        // cut-reveal beats
        var rc = cfg.reveal;
        for (int i = beats.Count - 1; i >= 0; i--)
        {
            var b = beats[i]; if (b.p == null) { beats.RemoveAt(i); continue; }
            float a = (now - b.t0) / Mathf.Max(0.01f, b.dur);
            if (a < 0f) continue;
            if (!b.started) { b.started = true; b.p.gameObject.SetActive(true); }
            float k = Mathf.Clamp01(a); k = k * k * (3f - 2f * k);
            var sz = b.p.size; float w = Mathf.Lerp(b.w0, b.w1, k), h = Mathf.Lerp(b.h0, b.h1, k);
            if (b.grow) { b.p.size = new Vector3(w, h, sz.z); b.p.pivot = new Vector3(0f, -h * 0.5f, 0f); } else b.p.size = new Vector3(w, b.len, sz.z);
            if (a >= 1f) beats.RemoveAt(i);
        }
    }
}

/// <summary>Runs FinVis.Tick (no logic of its own); lives in the scene, so a scene load takes the decals and the driver away together.</summary>
public sealed class FinVisDriver : MonoBehaviour { void Update() { FinVis.Tick(); } }

/// <summary>On the droplet particle system: OnParticleCollision -> a splash stain on the floor.</summary>
public sealed class FinVisDropSink : MonoBehaviour
{
    static readonly List<ParticleCollisionEvent> ev = new List<ParticleCollisionEvent>(16);
    ParticleSystem ps;
    void Awake() { ps = GetComponent<ParticleSystem>(); }
    void OnParticleCollision(GameObject other)
    {
        if (ps == null) return;
        int n = ps.GetCollisionEvents(other, ev);
        for (int i = 0; i < n; i++) FinVis.Landed(ev[i].intersection, ev[i].normal, ev[i].colliderComponent);
    }
}
