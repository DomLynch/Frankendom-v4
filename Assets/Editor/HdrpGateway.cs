using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Builds the doorway GATEWAY (abyss <-> rift) from Assets/Data/gateway.json (data layer: every number lives in the JSON; see GatewayTable.cs).
// Entry points:  HdrpGateway.Run()               (static void, usable with -executeMethod; idempotent: deletes and rebuilds the GatewayRoot object, reuses assets)
//                HdrpGateway.RunWithFurnace()    (HdrpFurnace.RunDefault() then Run(): the furnace skips what the gateway owns, see gateway.json "furnace")
//                menu  Tools/Gateway/Build GatewayRoot
// Builds: portal quad (Furnace/GatewayRift material), abyss fog volume + cold threshold wisp (Texture3D noise asset), faint red depth light, realm spill lights
// (rect + spot, non-shadow), flash light, ShardOrbit (obsidian placeholder shards), boom AudioSource, and the GatewayRift runtime component wired to all of them.
// Never opens the Play mode, never touches HdrpBatch.cs.
public static class HdrpGateway
{
    [MenuItem("Tools/Gateway/Build GatewayRoot")]
    public static void Run() { Build(true); }

    public static void RunWithFurnace() { HdrpFurnace.RunDefault(); Build(true); }

    public static void Build(bool saveScene)
    {
        GatewayTable.Reload();
        var D = GatewayTable.Get();
        if (D == null || D.portal == null) throw new Exception("HDRPGATEWAY gateway.json missing or has no 'portal' row (" + GatewayTable.LoadedFrom + ")");

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != D.scenePath) scene = EditorSceneManager.OpenScene(D.scenePath);
        foreach (var g in scene.GetRootGameObjects()) if (g.name == D.root) UnityEngine.Object.DestroyImmediate(g);
        Directory.CreateDirectory(D.materialFolder);

        foreach (var path in new[] { D.portal.panorama, D.portal.clouds, D.portal.cubemap })
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

        var root = new GameObject(D.root);
        var gr = root.AddComponent<GatewayRift>();

        BuildPortal(D, root.transform, gr);
        BuildFog(D, root.transform, gr);
        BuildLights(D, root.transform, gr);
        BuildShards(D, root.transform, gr);

        var boom = new GameObject("Boom"); boom.transform.SetParent(root.transform, false);
        gr.boomSource = boom.AddComponent<AudioSource>(); gr.boomSource.playOnAwake = false; gr.boomSource.spatialBlend = D.boom.spatialBlend;

        string fs = D.state != null ? D.state.forceState : "";
        gr.mode = fs == "rift" ? GatewayRift.Mode.Rift : fs == "abyss" ? GatewayRift.Mode.Abyss : GatewayRift.Mode.Auto;

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (saveScene) EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPGATEWAY built root=" + D.root + " data=" + GatewayTable.LoadedFrom + " forceState=" + fs + " shards=" + D.shards.count + " avgColor=" + gr.panoramaAverage);
    }

    // ---------------------------------------------------------------- portal
    static void BuildPortal(GatewayTable.Data D, Transform root, GatewayRift gr)
    {
        var P = D.portal;
        var shader = Shader.Find(P.shader);
        if (shader == null) throw new Exception("HDRPGATEWAY shader '" + P.shader + "' not found (Assets/VFX/GatewayRift.shader must compile first)");
        string matPath = D.materialFolder + "/Gateway_Rift.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, matPath); } else m.shader = shader;

        GatewayTable.ApplyMaterialRows(m, P);
        var pano = string.IsNullOrEmpty(P.panorama) ? null : AssetDatabase.LoadAssetAtPath<Texture>(P.panorama);
        var clouds = string.IsNullOrEmpty(P.clouds) ? null : AssetDatabase.LoadAssetAtPath<Texture>(P.clouds);
        var cube = string.IsNullOrEmpty(P.cubemap) ? null : AssetDatabase.LoadAssetAtPath<Cubemap>(P.cubemap);
        if (pano == null) Debug.LogWarning("HDRPGATEWAY panorama not found at '" + P.panorama + "' (run Tools/gen_gateway_panorama.py)");
        if (pano != null) m.SetTexture("_PanoTex", pano);
        if (clouds != null) m.SetTexture("_CloudTex", clouds);
        if (cube != null) m.SetTexture("_Cube", cube);
        bool useCube = P.useCube && cube != null;
        m.SetFloat("_UseCube", useCube ? 1f : 0f);
        if (useCube) m.EnableKeyword("_USE_CUBE"); else m.DisableKeyword("_USE_CUBE");

        float w = 2f * P.frameHalf[0] + 2f * P.margin, h = 2f * P.frameHalf[1] + 2f * P.margin;
        m.SetVector("_FrameHalf", new Vector4(P.frameHalf[0], P.frameHalf[1], 0f, 0f));
        m.SetVector("_QuadSize", new Vector4(w, h, 0f, 0f));
        m.SetFloat("_Open", 0f);
        m.renderQueue = 3000 + P.queueOffset;   // HDRP transparent base + offset: drawn before the ember particles (3003+) so embers in front of the portal stay on top
        EditorUtility.SetDirty(m);

        var q = GameObject.CreatePrimitive(PrimitiveType.Quad); q.name = P.name; q.transform.SetParent(root, false);
        q.transform.localPosition = GatewayTable.V3(P.pos); q.transform.localRotation = Quaternion.Euler(GatewayTable.V3(P.euler)); q.transform.localScale = new Vector3(w, h, 1f);
        UnityEngine.Object.DestroyImmediate(q.GetComponent<Collider>());
        var r = q.GetComponent<Renderer>(); r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        r.enabled = false;   // GatewayRift switches it on while the rift is (partly) open

        gr.portal = r; gr.panorama = pano;
        gr.panoramaAverage = AveragePng(P.panorama, gr.panoramaAverage);
    }

    // gamma-space average of the PNG (strided), used as the spill colour when the runtime texture is not readable
    static Color AveragePng(string path, Color fallback)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return fallback;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) { UnityEngine.Object.DestroyImmediate(tex); return fallback; }
            var px = tex.GetPixels32(); double r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < px.Length; i += 7) { r += px[i].r; g += px[i].g; b += px[i].b; n++; }
            UnityEngine.Object.DestroyImmediate(tex);
            return n == 0 ? fallback : new Color((float)(r / n / 255.0), (float)(g / n / 255.0), (float)(b / n / 255.0), 1f);
        }
        catch (Exception ex) { Debug.LogWarning("HDRPGATEWAY average colour failed: " + ex.Message); return fallback; }
    }

    // ---------------------------------------------------------------- fog
    static void BuildFog(GatewayTable.Data D, Transform root, GatewayRift gr)
    {
        var group = new GameObject("Fog").transform; group.SetParent(root, false);
        var tex = EnsureNoise(D.fog.noise);
        if (D.fog.abyss != null) gr.abyssFog = MakeFog(group, D.fog.abyss, tex);
        if (D.fog.wisp != null) gr.wispFog = MakeFog(group, D.fog.wisp, tex);
    }

    static LocalVolumetricFog MakeFog(Transform parent, GatewayTable.FogRow row, Texture3D tex)
    {
        var go = new GameObject(row.name); go.transform.SetParent(parent, false); go.transform.localPosition = GatewayTable.V3(row.pos);
        var lv = go.AddComponent<LocalVolumetricFog>();
        var p = lv.parameters;
        p.size = GatewayTable.V3(row.size); p.albedo = GatewayTable.Col(row.albedo); p.meanFreePath = row.meanFreePath; p.anisotropy = row.anisotropy;
        p.blendingMode = (LocalVolumetricFogBlendingMode)Enum.Parse(typeof(LocalVolumetricFogBlendingMode), row.blending); p.priority = row.priority;
        p.volumeMask = tex; p.textureTiling = GatewayTable.V3(row.tiling); p.textureScrollingSpeed = GatewayTable.V3(row.scroll);
        p.positiveFade = GatewayTable.V3(row.fadePos); p.negativeFade = GatewayTable.V3(row.fadeNeg);
        lv.parameters = p;
        return lv;
    }

    // periodic 3D value noise so the volume tiles and scrolls seamlessly (delete the asset to regenerate after changing the noise params)
    static Texture3D EnsureNoise(GatewayTable.NoiseRow n)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture3D>(n.path);
        if (existing != null) return existing;
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
            v = Mathf.Clamp01((v / norm - 0.22f) * 1.7f);
            byte b = (byte)Mathf.RoundToInt(v * 255f);
            px[x + y * s + z * s * s] = new Color32(b, b, b, b);
        }
        tex.SetPixels32(px); tex.wrapMode = TextureWrapMode.Repeat; tex.filterMode = FilterMode.Bilinear; tex.Apply(false);
        AssetDatabase.CreateAsset(tex, n.path);
        return tex;
    }

    static int Mod(int a, int m) { return ((a % m) + m) % m; }
    static float Sm(float f) { return f * f * (3f - 2f * f); }
    static float H3(int x, int y, int z, int seed)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + z * 1013904223 + seed * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177; h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }
    static float VNoise3(float x, float y, float z, int per, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
        float fx = Sm(x - x0), fy = Sm(y - y0), fz = Sm(z - z0);
        int xa = Mod(x0, per), xb = Mod(x0 + 1, per), ya = Mod(y0, per), yb = Mod(y0 + 1, per), za = Mod(z0, per), zb = Mod(z0 + 1, per);
        float c00 = Mathf.Lerp(H3(xa, ya, za, seed), H3(xb, ya, za, seed), fx), c10 = Mathf.Lerp(H3(xa, yb, za, seed), H3(xb, yb, za, seed), fx);
        float c01 = Mathf.Lerp(H3(xa, ya, zb, seed), H3(xb, ya, zb, seed), fx), c11 = Mathf.Lerp(H3(xa, yb, zb, seed), H3(xb, yb, zb, seed), fx);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
    }

    // ---------------------------------------------------------------- lights (physical units, no shadows)
    static void BuildLights(GatewayTable.Data D, Transform root, GatewayRift gr)
    {
        var group = new GameObject("Lights").transform; group.SetParent(root, false);
        gr.depthLight = MakeLight(group, D.lights.depth);
        gr.spillRect = MakeLight(group, D.lights.spillRect);
        gr.spillSpot = MakeLight(group, D.lights.spillSpot);
        gr.flashLight = MakeLight(group, D.lights.flash);
        // start dark: GatewayRift ramps them (depth stays on in the abyss)
        if (gr.spillRect != null) gr.spillRect.enabled = false;
        if (gr.spillSpot != null) gr.spillSpot.enabled = false;
        if (gr.flashLight != null) gr.flashLight.enabled = false;
    }

    static Light MakeLight(Transform parent, GatewayTable.LightRow row)
    {
        if (row == null) return null;
        var go = new GameObject(row.name); go.transform.SetParent(parent, false);
        go.transform.localPosition = GatewayTable.V3(row.pos); go.transform.localRotation = Quaternion.Euler(GatewayTable.V3(row.euler));
        var l = go.AddComponent<Light>(); var hd = go.AddComponent<HDAdditionalLightData>();
        l.type = (LightType)Enum.Parse(typeof(LightType), row.kind); l.lightUnit = (LightUnit)Enum.Parse(typeof(LightUnit), row.unit);
        l.intensity = row.value; l.color = GatewayTable.Col(row.color); l.range = row.range;
        if (l.type == LightType.Rectangle) l.areaSize = new Vector2(row.size[0], row.size[1]);
        if (l.type == LightType.Spot) { l.spotAngle = row.spotAngle; l.innerSpotAngle = row.spotAngle * row.innerPercent / 100f; }
        hd.EnableShadows(false); hd.volumetricDimmer = row.volumetricDimmer;
        return l;
    }

    // ---------------------------------------------------------------- shards
    static void BuildShards(GatewayTable.Data D, Transform root, GatewayRift gr)
    {
        var S = D.shards;
        var go = new GameObject("Shards"); go.transform.SetParent(root, false);
        go.transform.localPosition = GatewayTable.V3(D.portal.pos); go.transform.localRotation = Quaternion.Euler(GatewayTable.V3(D.portal.euler));
        var so = go.AddComponent<ShardOrbit>();

        // material: HDRP Lit, dark glossy obsidian
        string mp = D.materialFolder + "/Gateway_Shard.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(mp);
        var lit = Shader.Find("HDRP/Lit");
        if (m == null) { m = new Material(lit); AssetDatabase.CreateAsset(m, mp); } else m.shader = lit;
        m.SetColor("_BaseColor", GatewayTable.Col(S.baseColor)); m.SetFloat("_Metallic", S.metallic); m.SetFloat("_Smoothness", S.smoothness);
        var em = S.emissive != null && S.emissive.Length >= 3 ? new Color(S.emissive[0], S.emissive[1], S.emissive[2], 1f) : Color.black;
        m.SetFloat("_UseEmissiveIntensity", 0f); m.SetColor("_EmissiveColor", em);
        HDMaterial.ValidateMaterial(m);
        m.SetColor("_EmissiveColor", em);
        EditorUtility.SetDirty(m);

        // meshes: first Mesh of each GLB/FBX path in data (Hades env delivers later); otherwise the saved procedural placeholders
        var meshes = new List<Mesh>();
        if (S.meshPaths != null)
            foreach (var path in S.meshPaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Debug.LogWarning("HDRPGATEWAY shard mesh '" + path + "' not found, skipped"); continue; }
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path)) { var mm = o as Mesh; if (mm != null) { meshes.Add(mm); break; } }
            }
        if (meshes.Count == 0)
            for (int v = 1; v <= 3; v++)
            {
                string ap = "Assets/VFX/Gateway/shard_placeholder_" + v + ".asset";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                if (mesh == null) { mesh = ShardOrbit.MakeShardMesh(v); AssetDatabase.CreateAsset(mesh, ap); }
                meshes.Add(mesh);
            }

        so.meshes = meshes.ToArray(); so.material = m; so.count = S.count; so.seed = S.seed;
        so.frameHalf = new Vector2(D.portal.frameHalf[0], D.portal.frameHalf[1]); so.marginOut = S.marginOut;
        so.sizeRange = R2(S.sizeRange); so.speedRange = R2(S.speedRange); so.tumbleRange = R2(S.tumbleRange); so.zWobble = R2(S.zWobble); so.radialRange = R2(S.radialRange);
        so.introRadiusBoost = S.introRadiusBoost;
        gr.shards = so;
    }

    static Vector2 R2(float[] a) { return a == null || a.Length < 2 ? Vector2.zero : new Vector2(a[0], a[1]); }
}
