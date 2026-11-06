using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Builds the furnace-into-hell doorway behind the Hades gate arch from Assets/Data/hdrp_furnace.json (data layer: every number lives in the JSON).
// Entry points: HdrpFurnace.Run("ultra"|"high"|"medium")  (idempotent: deletes and rebuilds the FurnaceRoot object, reuses assets)
//               HdrpFurnace.RunDefault()                  (static void, usable with -executeMethod; tier = quality.defaultTier)
// Builds: recessed lava chamber, Shuriken fire / smoke / ember layers (Furnace/Particle shader), heat-shimmer distortion quad, local volumetric fog
// with a generated Texture3D, physical-unit lights, and a FurnaceFlicker component that drives light/emissive/fire from one noise.
public static class HdrpFurnace
{
    const string DataPath = "Assets/Data/hdrp_furnace.json";
    const int TransparentQueue = 3000; // HDRP transparent render-queue base (RenderQueue.Transparent)

    // ---------------------------------------------------------------- data rows (mirror hdrp_furnace.json)
    [Serializable] public class MatRow { public string name; public float[] color, emissive; public float smoothness, metallic, emissiveFlicker; }
    [Serializable] public class PieceRow { public string name, mat; public float[] pos, size, euler; public bool castShadows; }
    [Serializable] public class ChamberRow { public MatRow[] materials; public PieceRow[] pieces; }
    [Serializable] public class TierRow { public string name; public int emberCount, smokeLayerCount, flipbookRes, fogVolumeCount, lightCount; public float fireScale, smokeScale; public bool fog, shimmer; }
    [Serializable] public class QualityRow { public string defaultTier; public TierRow[] tiers; }
    [Serializable] public class FlickerRow { public FurnaceFlicker.Noise master; public float fireRateAmount; public float[] shimmerScroll; }
    [Serializable] public class FogNoiseRow { public string path; public int size, frequency, octaves, seed; }
    [Serializable] public class FogRow { public string name, blending; public float[] pos, size, albedo, tiling, scroll, fadePos, fadeNeg; public float meanFreePath, anisotropy; public int priority; }
    [Serializable] public class LightRow { public string name, kind, unit; public float[] pos, euler, color, size; public float value, spotAngle, innerPercent, range, volumetricDimmer, correlation; public bool shadows; public FurnaceFlicker.Noise flicker; }
    [Serializable] public class ShimmerRow { public string name, texture; public float[] pos, size, euler, tiling, noiseFreq; public int textureSize, seed; public float distortionScale, vectorScale, vectorBias, blurScale, edgeFade; }
    [Serializable] public class PlaneRow { public string name; public float[] pos, euler; }
    [Serializable] public class CollRow { public float bounce, dampen, lifetimeLoss, minKillSpeed, radiusScale; }
    [Serializable] public class CKey { public float t; public float[] c; }
    [Serializable] public class AKey { public float t, a; }
    [Serializable]
    public class LayerRow
    {
        public string name, kind, blend, shapeType, flipbook;
        public int queueOffset, tiles, cycles, maxParticles;
        public float[] pos, euler, shapeScale, lifetime, startSize, startSpeed, startRotation, rotationSpeed, velocityMin, velocityMax, sizeOverLife, tint;
        public float shapeAngle, shapeRadius, gravity, rate, countShare, noiseStrength, noiseFrequency, noiseScroll, intensity, softDistance, sortingFudge, speedScale, lengthScale;
        public bool noiseDamping, stretch, collision;
        public bool burst; // one-shot baked burst sheet (ue_ember_burst): the sheet plays from frame 0 over the lifetime, rate/maxParticles are used as written (not tier emberCount); may use the motion-vector shader
        public string motionVectorMap; public float[] mvScale, mvBias; public float mvStrength; // Furnace/FlipbookMV (Unreal-baked motion vectors)
        public CKey[] colorKeys; public AKey[] alphaKeys;
    }
    [Serializable]
    public class Data
    {
        public string root, scenePath, materialFolder, particleShader, fallbackShader, flipbookShader;
        public QualityRow quality; public FlickerRow flicker; public ChamberRow chamber; public FogNoiseRow fogNoise3D;
        public FogRow[] fogVolumes; public LightRow[] lights; public ShimmerRow shimmer; public PlaneRow[] collisionPlanes; public CollRow collision;
        public LayerRow[] fire, smoke, embers;
    }

    // ---------------------------------------------------------------- entry points
    public static void RunDefault() { Run(null); }
    public static void Run(string tier) { Build(tier, true); }

    public static void Build(string tierName, bool saveScene)
    {
        var D = JsonUtility.FromJson<Data>(File.ReadAllText(DataPath));
        var T = FindTier(D, tierName);
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != D.scenePath) scene = EditorSceneManager.OpenScene(D.scenePath);

        foreach (var g in scene.GetRootGameObjects()) if (g.name == D.root) UnityEngine.Object.DestroyImmediate(g);
        var root = new GameObject(D.root);
        Directory.CreateDirectory(D.materialFolder);
        var ff = root.AddComponent<FurnaceFlicker>();
        ff.master = D.flicker.master; ff.fireAmount = D.flicker.fireRateAmount;

        BuildChamber(D, root.transform, ff);
        BuildLights(D, T, root.transform, ff);
        var gw = GatewayTable.Get().furnace; // gateway.json decides whether the gateway owns the interior fog / the old shimmer card
        if (T.fog && !gw.skipFog) BuildFog(D, T, root.transform);
        BuildParticles(D, T, root.transform, ff);
        if (T.shimmer && D.shimmer != null && !gw.skipShimmer) BuildShimmer(D, root.transform, ff);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (saveScene) EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPFURNACE built tier=" + T.name + " embers=" + T.emberCount + " fog=" + T.fog + " shimmer=" + T.shimmer);
    }

    static TierRow FindTier(Data D, string name)
    {
        if (string.IsNullOrEmpty(name)) name = D.quality.defaultTier;
        foreach (var t in D.quality.tiers) if (t.name == name) return t;
        throw new Exception("HDRPFURNACE unknown tier '" + name + "' (see quality.tiers in hdrp_furnace.json)");
    }

    // ---------------------------------------------------------------- small helpers
    static Vector3 V3(float[] a) { return new Vector3(a[0], a[1], a[2]); }
    static Color Col(float[] c) { return new Color(c[0], c[1], c[2], 1f); }
    static T Parse<T>(string s) { return (T)Enum.Parse(typeof(T), s); }
    static int Mod(int a, int m) { return ((a % m) + m) % m; }

    static Material GetMat(string path, Shader sh)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); } else m.shader = sh;
        return m;
    }

    // ---------------------------------------------------------------- chamber
    static void BuildChamber(Data D, Transform root, FurnaceFlicker ff)
    {
        var group = new GameObject("Chamber").transform; group.SetParent(root, false);
        var mats = new Dictionary<string, Material>();
        var rows = new Dictionary<string, MatRow>();
        var users = new Dictionary<string, List<Renderer>>();
        var lit = Shader.Find("HDRP/Lit");
        foreach (var r in D.chamber.materials)
        {
            var m = GetMat(D.materialFolder + "/Furnace_" + r.name + ".mat", lit);
            m.SetColor("_BaseColor", Col(r.color)); m.SetFloat("_Metallic", r.metallic); m.SetFloat("_Smoothness", r.smoothness);
            // _EmissiveColor is in nits (HDRP/Lit multiplies it by the exposure); _UseEmissiveIntensity 0 means this colour is used as is.
            var em = new Color(r.emissive[0], r.emissive[1], r.emissive[2], 1f);
            m.SetFloat("_UseEmissiveIntensity", 0f); m.SetColor("_EmissiveColor", em);
            HDMaterial.ValidateMaterial(m);
            m.SetColor("_EmissiveColor", em); // ValidateMaterial may re-derive it; the explicit nits value wins
            EditorUtility.SetDirty(m);
            mats[r.name] = m; rows[r.name] = r; users[r.name] = new List<Renderer>();
        }
        foreach (var p in D.chamber.pieces)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = p.name; go.transform.SetParent(group, false);
            go.transform.localPosition = V3(p.pos); go.transform.localRotation = Quaternion.Euler(V3(p.euler)); go.transform.localScale = V3(p.size);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            var rend = go.GetComponent<Renderer>(); rend.sharedMaterial = mats[p.mat];
            rend.shadowCastingMode = p.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            users[p.mat].Add(rend);
        }
        var drives = new List<FurnaceFlicker.EmissiveDrive>();
        foreach (var kv in rows)
        {
            var r = kv.Value;
            if (r.emissiveFlicker <= 0f || users[kv.Key].Count == 0) continue;
            drives.Add(new FurnaceFlicker.EmissiveDrive { renderers = users[kv.Key].ToArray(), baseEmissive = new Color(r.emissive[0], r.emissive[1], r.emissive[2], 1f), amount = r.emissiveFlicker });
        }
        ff.emissives = drives.ToArray();
    }

    // ---------------------------------------------------------------- lights (physical units, no shadows)
    static void BuildLights(Data D, TierRow T, Transform root, FurnaceFlicker ff)
    {
        var group = new GameObject("Lights").transform; group.SetParent(root, false);
        var drives = new List<FurnaceFlicker.LightDrive>();
        for (int i = 0; i < D.lights.Length && i < T.lightCount; i++)
        {
            var row = D.lights[i];
            var go = new GameObject(row.name); go.transform.SetParent(group, false);
            go.transform.localPosition = V3(row.pos); go.transform.localRotation = Quaternion.Euler(V3(row.euler));
            var l = go.AddComponent<Light>(); var hd = go.AddComponent<HDAdditionalLightData>();
            l.type = Parse<LightType>(row.kind); l.lightUnit = Parse<LightUnit>(row.unit); l.intensity = row.value; l.color = Col(row.color); l.range = row.range;
            if (l.type == LightType.Rectangle) hd.SetAreaLightSize(new Vector2(row.size[0], row.size[1]));
            if (l.type == LightType.Spot) { l.spotAngle = row.spotAngle; l.innerSpotAngle = row.spotAngle * row.innerPercent / 100f; }
            hd.EnableShadows(row.shadows); hd.volumetricDimmer = row.volumetricDimmer;
            drives.Add(new FurnaceFlicker.LightDrive { light = l, baseIntensity = row.value, noise = row.flicker, correlation = row.correlation });
        }
        ff.lights = drives.ToArray();
    }

    // ---------------------------------------------------------------- local volumetric fog
    static void BuildFog(Data D, TierRow T, Transform root)
    {
        var group = new GameObject("Fog").transform; group.SetParent(root, false);
        var tex = EnsureFogTexture(D.fogNoise3D);
        for (int i = 0; i < D.fogVolumes.Length && i < T.fogVolumeCount; i++)
        {
            var row = D.fogVolumes[i];
            var go = new GameObject(row.name); go.transform.SetParent(group, false); go.transform.localPosition = V3(row.pos);
            var lv = go.AddComponent<LocalVolumetricFog>();
            var p = lv.parameters;
            p.size = V3(row.size); p.albedo = Col(row.albedo); p.meanFreePath = row.meanFreePath; p.anisotropy = row.anisotropy;
            p.blendingMode = Parse<LocalVolumetricFogBlendingMode>(row.blending); p.priority = row.priority;
            p.volumeMask = tex; p.textureTiling = V3(row.tiling); p.textureScrollingSpeed = V3(row.scroll);
            p.positiveFade = V3(row.fadePos); p.negativeFade = V3(row.fadeNeg);
            lv.parameters = p;
        }
    }

    static Texture3D EnsureFogTexture(FogNoiseRow n)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture3D>(n.path);
        if (existing != null) return existing; // delete the asset to regenerate after changing the noise params
        Directory.CreateDirectory(Path.GetDirectoryName(n.path));
        int s = n.size;
        var tex = new Texture3D(s, s, s, TextureFormat.RGBA32, false);
        var px = new Color32[s * s * s];
        for (int z = 0; z < s; z++) for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
        {
            float v = 0f, amp = 0.5f, norm = 0f; int per = n.frequency;
            for (int o = 0; o < n.octaves; o++)
            {
                v += amp * VNoise3(x / (float)s * per, y / (float)s * per, z / (float)s * per, per, n.seed + o * 31);
                norm += amp; amp *= 0.5f; per *= 2;
            }
            v = Mathf.Clamp01((v / norm - 0.3f) * 1.8f); // contrast: wisps with gaps
            byte b = (byte)Mathf.RoundToInt(v * 255f);
            px[x + y * s + z * s * s] = new Color32(b, b, b, b); // all channels equal: works whichever channel the volume mask reads
        }
        tex.SetPixels32(px); tex.wrapMode = TextureWrapMode.Repeat; tex.filterMode = FilterMode.Bilinear; tex.Apply(false);
        AssetDatabase.CreateAsset(tex, n.path);
        return tex;
    }

    // ---------------------------------------------------------------- particles
    static void BuildParticles(Data D, TierRow T, Transform root, FurnaceFlicker ff)
    {
        var group = new GameObject("Particles").transform; group.SetParent(root, false);
        var planesRoot = new GameObject("CollisionPlanes").transform; planesRoot.SetParent(root, false);
        var planes = new List<Transform>();
        foreach (var pr in D.collisionPlanes)
        {
            var pt = new GameObject(pr.name).transform; pt.SetParent(planesRoot, false);
            pt.localPosition = V3(pr.pos); pt.localRotation = Quaternion.Euler(V3(pr.euler)); planes.Add(pt);
        }
        var shader = Shader.Find(D.particleShader);
        if (shader == null)
        {
            shader = Shader.Find(D.fallbackShader);
            Debug.LogWarning("HDRPFURNACE shader '" + D.particleShader + "' not found (move FurnaceParticle.shader into Assets). Falling back to " + D.fallbackShader + ": vertex colour / alpha-over-lifetime will NOT work.");
        }
        EnsureFlipbooks(D, T);
        var mvShader = string.IsNullOrEmpty(D.flipbookShader) ? null : Shader.Find(D.flipbookShader);
        if (mvShader == null && !string.IsNullOrEmpty(D.flipbookShader)) Debug.LogWarning("HDRPFURNACE flipbook shader '" + D.flipbookShader + "' not found: layers keep " + D.particleShader + " (no motion-vector blend).");

        var fireSys = new List<ParticleSystem>(); var fireRate = new List<float>();
        foreach (var L in D.fire)
        {
            float rate = L.rate * T.fireScale; int max = Mathf.CeilToInt(L.maxParticles * T.fireScale);
            var ps = BuildLayer(D, group, L, PickShader(L, shader, mvShader), rate, max, planes);
            fireSys.Add(ps); fireRate.Add(rate);
        }
        for (int i = 0; i < D.smoke.Length && i < T.smokeLayerCount; i++)
        {
            var L = D.smoke[i];
            BuildLayer(D, group, L, PickShader(L, shader, mvShader), L.rate * T.smokeScale, Mathf.CeilToInt(L.maxParticles * T.smokeScale), planes);
        }
        foreach (var L in D.embers)
        {
            if (L.burst) { BuildLayer(D, group, L, PickShader(L, shader, mvShader), L.rate, Mathf.Max(1, L.maxParticles), planes); continue; }
            int count = Mathf.Max(1, Mathf.RoundToInt(T.emberCount * L.countShare));
            float meanLife = 0.5f * (L.lifetime[0] + L.lifetime[1]);
            BuildLayer(D, group, L, shader, count / meanLife, count, planes);
        }
        ff.fire = fireSys.ToArray(); ff.fireBaseRate = fireRate.ToArray();
    }

    // Fire and smoke rows with a motion-vector sheet use the Furnace/FlipbookMV shader; embers and rows without the sheet keep the standard particle shader.
    static Shader PickShader(LayerRow L, Shader standard, Shader mv)
    {
        bool useMv = mv != null && (L.kind != "ember" || L.burst) && !string.IsNullOrEmpty(L.motionVectorMap) && File.Exists(L.motionVectorMap);
        return useMv ? mv : standard;
    }

    static ParticleSystem BuildLayer(Data D, Transform parent, LayerRow L, Shader shader, float rate, int max, List<Transform> planes)
    {
        var go = new GameObject(L.name); go.transform.SetParent(parent, false);
        go.transform.localPosition = V3(L.pos); go.transform.localRotation = Quaternion.Euler(V3(L.euler));
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ps.useAutoRandomSeed = false; ps.randomSeed = StableSeed(L.name); // deterministic captures

        var main = ps.main;
        main.loop = true; main.playOnAwake = true; main.prewarm = true; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = Range(L.lifetime, 1f); main.startSize = Range(L.startSize, 1f); main.startSpeed = Range(L.startSpeed, 1f);
        main.startRotation = Range(L.startRotation, Mathf.Deg2Rad); main.startColor = Color.white;
        main.gravityModifier = new ParticleSystem.MinMaxCurve(L.gravity); main.maxParticles = Mathf.Max(1, max);

        var em = ps.emission; em.enabled = true; em.rateOverTime = new ParticleSystem.MinMaxCurve(rate);

        var sh = ps.shape; sh.enabled = true; sh.shapeType = Parse<ParticleSystemShapeType>(L.shapeType);
        sh.scale = V3(L.shapeScale); sh.angle = L.shapeAngle; sh.radius = L.shapeRadius;

        if (L.velocityMin != null && L.velocityMax != null && (L.velocityMin[0] != 0f || L.velocityMin[1] != 0f || L.velocityMin[2] != 0f || L.velocityMax[0] != 0f || L.velocityMax[1] != 0f || L.velocityMax[2] != 0f))
        {
            var vo = ps.velocityOverLifetime; vo.enabled = true; vo.space = ParticleSystemSimulationSpace.World;
            vo.x = new ParticleSystem.MinMaxCurve(L.velocityMin[0], L.velocityMax[0]);
            vo.y = new ParticleSystem.MinMaxCurve(L.velocityMin[1], L.velocityMax[1]);
            vo.z = new ParticleSystem.MinMaxCurve(L.velocityMin[2], L.velocityMax[2]);
        }
        if (L.rotationSpeed != null && (L.rotationSpeed[0] != 0f || L.rotationSpeed[1] != 0f))
        {
            var ro = ps.rotationOverLifetime; ro.enabled = true; ro.z = Range(L.rotationSpeed, Mathf.Deg2Rad);
        }
        if (L.noiseStrength > 0f)
        {
            var nz = ps.noise; nz.enabled = true; nz.separateAxes = false; nz.strength = new ParticleSystem.MinMaxCurve(L.noiseStrength);
            nz.frequency = L.noiseFrequency; nz.damping = L.noiseDamping; nz.scrollSpeed = new ParticleSystem.MinMaxCurve(L.noiseScroll);
            nz.octaveCount = 2; nz.quality = ParticleSystemNoiseQuality.Medium;
        }
        if (L.colorKeys != null && L.colorKeys.Length > 0)
        {
            var ck = new GradientColorKey[L.colorKeys.Length]; for (int i = 0; i < ck.Length; i++) ck[i] = new GradientColorKey(Col(L.colorKeys[i].c), L.colorKeys[i].t);
            var ak = new GradientAlphaKey[L.alphaKeys.Length]; for (int i = 0; i < ak.Length; i++) ak[i] = new GradientAlphaKey(L.alphaKeys[i].a, L.alphaKeys[i].t);
            var grad = new Gradient(); grad.SetKeys(ck, ak);
            var co = ps.colorOverLifetime; co.enabled = true; co.color = new ParticleSystem.MinMaxGradient(grad);
        }
        if (L.sizeOverLife != null && L.sizeOverLife.Length >= 2)
        {
            var curve = new AnimationCurve(); int n = L.sizeOverLife.Length;
            for (int i = 0; i < n; i++) curve.AddKey(i / (float)(n - 1), L.sizeOverLife[i]);
            for (int i = 0; i < n; i++) curve.SmoothTangents(i, 0f);
            var so = ps.sizeOverLifetime; so.enabled = true; so.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }
        if (L.tiles > 1)
        {
            var ts = ps.textureSheetAnimation; ts.enabled = true; ts.mode = ParticleSystemAnimationMode.Grid;
            ts.numTilesX = L.tiles; ts.numTilesY = L.tiles; ts.animation = ParticleSystemAnimationType.WholeSheet;
            ts.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
            ts.startFrame = L.burst ? new ParticleSystem.MinMaxCurve(0f) : new ParticleSystem.MinMaxCurve(0f, 0.5f); // random start frame in the first half so layers do not play in lock step
            ts.cycleCount = Mathf.Max(1, L.cycles);
        }
        if (L.collision && planes.Count > 0)
        {
            var c = D.collision; var col = ps.collision; col.enabled = true; col.type = ParticleSystemCollisionType.Planes; col.mode = ParticleSystemCollisionMode.Collision3D;
            col.dampen = new ParticleSystem.MinMaxCurve(c.dampen); col.bounce = new ParticleSystem.MinMaxCurve(c.bounce); col.lifetimeLoss = new ParticleSystem.MinMaxCurve(c.lifetimeLoss);
            col.minKillSpeed = c.minKillSpeed; col.radiusScale = c.radiusScale; col.quality = ParticleSystemCollisionQuality.High; col.sendCollisionMessages = false;
            foreach (var pt in planes) col.AddPlane(pt);
        }

        var rend = go.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = L.stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (L.stretch) { rend.velocityScale = L.speedScale; rend.lengthScale = L.lengthScale; rend.cameraVelocityScale = 0f; }
        rend.sortMode = L.blend == "alpha" ? ParticleSystemSortMode.Distance : ParticleSystemSortMode.None;
        rend.sortingFudge = L.sortingFudge;
        rend.shadowCastingMode = ShadowCastingMode.Off; rend.receiveShadows = false;
        rend.lightProbeUsage = LightProbeUsage.Off; rend.reflectionProbeUsage = ReflectionProbeUsage.Off;
        rend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        rend.sharedMaterial = MakeParticleMaterial(D, L, shader);
        if (!string.IsNullOrEmpty(D.flipbookShader) && shader.name == D.flipbookShader && L.tiles > 1)
            rend.SetActiveVertexStreams(new List<ParticleSystemVertexStream> { // TEXCOORD0.xy frame A, .zw frame B, TEXCOORD1.x blend (see FurnaceFlipbook.shader)
                ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
                ParticleSystemVertexStream.UV2, ParticleSystemVertexStream.AnimBlend });
        return ps;
    }

    static Material MakeParticleMaterial(Data D, LayerRow L, Shader shader)
    {
        var m = GetMat(D.materialFolder + "/Furnace_" + L.name + ".mat", shader);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(L.flipbook);
        var tint = new Color(L.tint[0], L.tint[1], L.tint[2], 1f);
        bool add = L.blend == "add";
        bool isMv = !string.IsNullOrEmpty(D.flipbookShader) && shader.name == D.flipbookShader;
        if (shader.name == D.particleShader || isMv)
        {
            m.SetTexture("_MainTex", tex); m.SetColor("_Tint", tint); m.SetFloat("_Intensity", L.intensity);
            m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", L.softDistance);
            m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", add ? 1f : 10f); // UnityEngine.Rendering.BlendMode: One = 1, OneMinusSrcAlpha = 10
            if (isMv)
            {
                m.SetTexture("_MotionTex", AssetDatabase.LoadAssetAtPath<Texture2D>(L.motionVectorMap));
                m.SetFloat("_Tiles", L.tiles); m.SetFloat("_MVStrength", L.mvStrength);
                if (L.mvScale != null && L.mvScale.Length >= 2) m.SetVector("_MVScale", new Vector4(L.mvScale[0], L.mvScale[1], 0f, 0f));
                if (L.mvBias != null && L.mvBias.Length >= 2) m.SetVector("_MVBias", new Vector4(L.mvBias[0], L.mvBias[1], 0f, 0f));
            }
        }
        else
        {
            m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", add ? 1f : 0f); m.SetFloat("_ZWrite", 0f); m.SetFloat("_DoubleSidedEnable", 1f);
            m.SetTexture("_UnlitColorMap", tex); m.SetColor("_UnlitColor", new Color(tint.r * L.intensity, tint.g * L.intensity, tint.b * L.intensity, 1f));
            HDMaterial.ValidateMaterial(m);
        }
        m.renderQueue = TransparentQueue + L.queueOffset;
        EditorUtility.SetDirty(m);
        return m;
    }

    static ParticleSystem.MinMaxCurve Range(float[] a, float mul)
    {
        if (a == null || a.Length < 2) return new ParticleSystem.MinMaxCurve(0f);
        return a[0] == a[1] ? new ParticleSystem.MinMaxCurve(a[0] * mul) : new ParticleSystem.MinMaxCurve(a[0] * mul, a[1] * mul);
    }

    static uint StableSeed(string s) { uint h = 2166136261u; unchecked { foreach (char c in s) h = (h ^ c) * 16777619u; } return h; }

    // ---------------------------------------------------------------- heat shimmer
    // Screen-space distortion needs HDRP/Unlit with _DistortionEnable (supported: Unlit.shader has _DistortionVectorMap/_DistortionScale; the Distortion
    // pass is enabled by HDMaterial.ValidateMaterial for transparent surfaces). Requires "Distortion" enabled in the HDRP asset's Rendering settings.
    // Limitation: no per-pixel edge fade along the top/bottom of the quad (the map scrolls vertically); the fade is baked on the X edges only, so keep distortionScale small.
    static void BuildShimmer(Data D, Transform root, FurnaceFlicker ff)
    {
        var S = D.shimmer;
        if (!File.Exists(S.texture)) { GenHeatVector(S); ConfigureTexture(S.texture, false, false, true, TextureWrapMode.Repeat); }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(S.texture);
        var m = GetMat(D.materialFolder + "/Furnace_" + S.name + ".mat", Shader.Find("HDRP/Unlit"));
        m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", 0f); m.SetFloat("_ZWrite", 0f); m.SetFloat("_DoubleSidedEnable", 1f);
        m.SetFloat("_DistortionEnable", 1f); m.SetFloat("_DistortionOnly", 1f); m.SetFloat("_DistortionDepthTest", 1f); m.SetFloat("_DistortionBlendMode", 0f);
        m.SetFloat("_DistortionScale", S.distortionScale); m.SetFloat("_DistortionVectorScale", S.vectorScale); m.SetFloat("_DistortionVectorBias", S.vectorBias); m.SetFloat("_DistortionBlurScale", S.blurScale);
        m.SetTexture("_DistortionVectorMap", tex); m.SetTextureScale("_DistortionVectorMap", new Vector2(S.tiling[0], S.tiling[1]));
        HDMaterial.ValidateMaterial(m);
        m.renderQueue = TransparentQueue + 10;
        EditorUtility.SetDirty(m);

        var q = GameObject.CreatePrimitive(PrimitiveType.Quad); q.name = S.name; q.transform.SetParent(root, false);
        q.transform.localPosition = V3(S.pos); q.transform.localRotation = Quaternion.Euler(V3(S.euler)); q.transform.localScale = new Vector3(S.size[0], S.size[1], 1f);
        UnityEngine.Object.DestroyImmediate(q.GetComponent<Collider>());
        var r = q.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        ff.shimmerRenderer = r; ff.shimmerScroll = new Vector2(D.flicker.shimmerScroll[0], D.flicker.shimmerScroll[1]);
    }

    // ---------------------------------------------------------------- placeholder texture generation (editor time only)
    static void EnsureFlipbooks(Data D, TierRow T)
    {
        var done = new HashSet<string>();
        var all = new List<LayerRow>(); all.AddRange(D.fire); all.AddRange(D.smoke); all.AddRange(D.embers);
        foreach (var L in all)
        {
            if (done.Contains(L.flipbook)) continue; done.Add(L.flipbook);
            if (File.Exists(L.flipbook)) continue; // a real (Unreal-baked) flipbook has been dropped in: keep it
            int res = L.kind == "ember" ? 64 : T.flipbookRes;
            GenFlipbook(L.flipbook, L.kind, L.tiles, res);
            ConfigureTexture(L.flipbook, true, true, false, TextureWrapMode.Clamp);
            Debug.Log("HDRPFURNACE generated placeholder flipbook " + L.flipbook + " (" + res + "px, " + L.tiles + "x" + L.tiles + ")");
        }
    }

    static void ConfigureTexture(string path, bool srgb, bool alphaTransparency, bool mips, TextureWrapMode wrap)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var ti = AssetImporter.GetAtPath(path) as TextureImporter; if (ti == null) return;
        ti.textureType = TextureImporterType.Default; ti.sRGBTexture = srgb; ti.alphaIsTransparency = alphaTransparency;
        ti.mipmapEnabled = mips; ti.wrapMode = wrap; ti.filterMode = FilterMode.Bilinear; ti.maxTextureSize = 4096;
        ti.SaveAndReimport();
    }

    static void GenFlipbook(string path, string kind, int tiles, int res)
    {
        if (tiles < 1) tiles = 1;
        int F = Mathf.Max(8, res / tiles), W = F * tiles, frames = tiles * tiles;
        var px = new Color32[W * W];
        for (int f = 0; f < frames; f++)
        {
            float t = frames > 1 ? f / (float)(frames - 1) : 0f;
            int ox = (f % tiles) * F, oy = (tiles - 1 - f / tiles) * F; // frame 0 is the top-left cell, like Unity's texture sheet
            for (int y = 0; y < F; y++) for (int x = 0; x < F; x++)
            {
                float u = (x + 0.5f) / F, v = (y + 0.5f) / F;
                Color c = kind == "fire" ? FireTexel(u, v, t) : kind == "smoke" ? SmokeTexel(u, v, t) : EmberTexel(u, v);
                px[(oy + y) * W + ox + x] = c;
            }
        }
        WritePng(path, px, W, W);
    }

    static float EdgeFade(float u, float v)
    {
        float e = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
        return Mathf.SmoothStep(0f, 1f, e / 0.05f);
    }

    // v = 0 is the bottom of the cell. Colour is baked white-yellow core to orange edge; the particle gradient then tints it over lifetime.
    static Color FireTexel(float u, float v, float t)
    {
        float warp = (Mathf.PerlinNoise(u * 3f + 5f, v * 3f - t * 5f + 11f) - 0.5f) * 0.35f * (0.3f + v);
        float x = u - 0.5f + warp;
        float width = Mathf.Lerp(0.30f, 0.03f, Mathf.Pow(v, 0.8f));
        float body = Mathf.Clamp01(1f - Mathf.Abs(x) / width);
        float n2 = Mathf.PerlinNoise(u * 6f + 3f, v * 6f - t * 8f + 7f);
        float d = body * Mathf.Clamp01((1f - v) * 1.25f) * Mathf.Clamp01(v * 8f) * Mathf.Lerp(0.6f, 1.3f, n2);
        d = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d * 1.3f));
        float core = Mathf.Pow(d, 2.2f);
        Color c = Color.Lerp(new Color(1f, 0.38f, 0.06f), new Color(1f, 0.93f, 0.62f), core);
        c.a = d * EdgeFade(u, v);
        return c;
    }

    static Color SmokeTexel(float u, float v, float t)
    {
        float dx = u - 0.5f, dy = v - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
        float n = 0f, amp = 0.5f, f = 3f, norm = 0f;
        for (int o = 0; o < 3; o++)
        {
            n += amp * Mathf.PerlinNoise(u * f + 20f * o + t * 1.5f, v * f + 7f * o - t * 0.8f);
            norm += amp; amp *= 0.5f; f *= 2f;
        }
        n /= norm;
        float a = Mathf.Clamp01(1f - r) * 1.5f * (0.35f + 1.1f * n) - 0.1f;
        a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a)) * EdgeFade(u, v);
        float l = Mathf.Lerp(1f, 0.55f, v) * (0.6f + 0.4f * n); // lighter at the bottom: reads as lit from the furnace below
        return new Color(l, l, l, a);
    }

    static Color EmberTexel(float u, float v)
    {
        float dx = u - 0.5f, dy = v - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
        float a = Mathf.Pow(Mathf.Clamp01(1f - r), 2f);
        Color c = Color.Lerp(new Color(1f, 0.45f, 0.08f), new Color(1f, 0.95f, 0.8f), a);
        c.a = a * EdgeFade(u, v);
        return c;
    }

    // Vector map: r,g = distortion direction (0.5 = none), b = blur. Periodic value noise so scrolling wraps seamlessly; stretched vertically like rising heat.
    static void GenHeatVector(ShimmerRow S)
    {
        int n = S.textureSize; var px = new Color32[n * n];
        int fx = Mathf.Max(1, Mathf.RoundToInt(S.noiseFreq[0])), fy = Mathf.Max(1, Mathf.RoundToInt(S.noiseFreq[1]));
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
            float vx = 0f, vy = 0f, amp = 0.65f, norm = 0f; int px_ = fx, py_ = fy;
            for (int o = 0; o < 2; o++)
            {
                vx += amp * (VNoise2(u * px_, v * py_, px_, py_, S.seed + o * 7) * 2f - 1f);
                vy += amp * (VNoise2(u * px_, v * py_, px_, py_, S.seed + 101 + o * 7) * 2f - 1f);
                norm += amp; amp *= 0.5f; px_ *= 2; py_ *= 2;
            }
            float mask = Mathf.SmoothStep(0f, 1f, Mathf.Min(u, 1f - u) / Mathf.Max(0.001f, S.edgeFade)); // fades to "no distortion" at the quad's left/right edges
            float rr = 0.5f + 0.5f * Mathf.Clamp(vx / norm * 1.6f, -1f, 1f) * mask;
            float gg = 0.5f + 0.5f * Mathf.Clamp(vy / norm * 1.6f, -1f, 1f) * mask;
            px[y * n + x] = new Color(rr, gg, 0.35f, 1f);
        }
        WritePng(S.texture, px, n, n);
    }

    static void WritePng(string path, Color32[] px, int w, int h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        tex.SetPixels32(px); tex.Apply(false);
        SafeIO.WriteAllBytes(path, tex.EncodeToPNG()); /* LOCKFIX v103 */
        UnityEngine.Object.DestroyImmediate(tex);
    }

    // ---------------------------------------------------------------- periodic value noise
    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + seed * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177; h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }

    static float Smooth(float f) { return f * f * (3f - 2f * f); }

    static float VNoise2(float x, float y, int perX, int perY, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y); float fx = Smooth(x - x0), fy = Smooth(y - y0);
        int xa = Mod(x0, perX), xb = Mod(x0 + 1, perX), ya = Mod(y0, perY), yb = Mod(y0 + 1, perY);
        return Mathf.Lerp(Mathf.Lerp(Hash(xa, ya, seed), Hash(xb, ya, seed), fx), Mathf.Lerp(Hash(xa, yb, seed), Hash(xb, yb, seed), fx), fy);
    }

    static float VNoise3(float x, float y, float z, int per, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
        float fx = Smooth(x - x0), fy = Smooth(y - y0), fz = Smooth(z - z0);
        int xa = Mod(x0, per), xb = Mod(x0 + 1, per), ya = Mod(y0, per), yb = Mod(y0 + 1, per), za = Mod(z0, per), zb = Mod(z0 + 1, per);
        float c00 = Mathf.Lerp(H3(xa, ya, za, seed), H3(xb, ya, za, seed), fx), c10 = Mathf.Lerp(H3(xa, yb, za, seed), H3(xb, yb, za, seed), fx);
        float c01 = Mathf.Lerp(H3(xa, ya, zb, seed), H3(xb, ya, zb, seed), fx), c11 = Mathf.Lerp(H3(xa, yb, zb, seed), H3(xb, yb, zb, seed), fx);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
    }

    static float H3(int x, int y, int z, int seed) { return Hash(x + z * 1013, y + z * 517, seed); }
}
