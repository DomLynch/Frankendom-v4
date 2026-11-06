using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Doorway interior (Goose QC: the v44 doorway showed SKY instead of a furnace). ONE data file: Assets/Data/doorway.json. Idempotent: Run() opens HadesSlice, removes and rebuilds
// 'DoorwayRoot', saves the scene and writes the same objects as prefabs under Assets/Prefabs/Doorway/. Everything is placed from the HeroGate renderer bounds
// (front face = bounds.min.z, tunnel along +z behind it). No colliders anywhere. Contents:
//   DoorwayInterior   rough-hewn basalt tunnel (procedural noise-displaced mesh: floor, walls, ceiling, back wall = no sky sightline) + child DoorwayEmberGlow (additive low glow near the floor)
//   DoorwayFogLocal   two warm LocalVolumetricFog volumes (inside + just outside the door)
//   DoorwayFlickerLight one shadowed 2800 K light + DoorwayFlicker (Perlin flicker, data driven)
//   DoorwayEmbers     Shuriken ember system (OFF by data: embers.enabled=false)
//   DoorwayShimmer    HDRP/Unlit distortion quad (OFF by data: shimmer.enabled=false)
public static class HdrpDoorway
{
    [Serializable] class TunnelRow { public bool enabled = true; public float startInset, depth, halfWidth, height, endScale = 1f, superExp = 0.35f, noiseAmp, noiseFreq, floorNoiseScale, floorY, floorOffset, uvTile = 0.25f, smoothness; public int ringPoints = 56, depthRings = 36, backRings = 10, noiseOctaves = 3, seed; public bool useGateFloor = true, castShadows; public string baseMaterial; public float[] fallbackColor; }
    [Serializable] class GlowRow { public bool enabled = true; public float height, inset, crackWidth, uvTile, intensity; public string texture, particleShader; public int textureSize = 256, seed; public float[] tint; }
    [Serializable] class FogVol { public string name; public float offsetZ, centerHeight, meanFreePath, anisotropy; public float[] size, albedo, tiling, scroll, fadePos, fadeNeg; }
    [Serializable] class FogRow { public bool enabled = true; public string noiseTexture; public FogVol[] volumes; }
    [Serializable] class LightRow { public bool enabled = true; public string kind, unit; public float intensity, kelvin, range, offsetZ, centerHeight, volumetricDimmer, spotAngle; public bool shadows; public int shadowResolution = 512; public FurnaceFlicker.Noise flicker; }
    [Serializable] class CKey { public float t; public float[] c; }
    [Serializable] class AKey { public float t, a; }
    [Serializable] class EmberRow { public bool enabled; public float offsetZ, ratePerSecond, gravityModifier, noiseStrength, noiseFrequency, noiseScroll, bounce, dampen, lifetimeLoss, intensity, softDistance; public int maxParticles = 1500; public float[] lifetime, startSize, startSpeed, emitterSize, driftWorld, tint; public string texture, shader, fallbackShader; public CKey[] colorKeys; public AKey[] alphaKeys; }
    [Serializable] class ShimmerRow { public bool enabled; public float offsetZ, bottomOffset, width, height, distortionScale, vectorScale, vectorBias, blurScale; public string texture; public float[] tiling; }
    [Serializable] class Data { public string rootName, scenePath, gateName, prefabFolder, vfxFolder; public string[] disableNames; public TunnelRow tunnel; public GlowRow glow; public FogRow fog; public LightRow light; public EmberRow embers; public ShimmerRow shimmer; }

    const string DataPath = "Assets/Data/doorway.json";
    const int TransparentQueue = 3000;

    public static void Run() { RunPaths(); }

    // returns the prefab asset paths written
    public static string[] RunPaths()
    {
        var paths = new List<string>();
        if (!File.Exists(DataPath)) { Debug.LogError("HDRPDOORWAY missing " + DataPath); return paths.ToArray(); }
        var D = JsonUtility.FromJson<Data>(File.ReadAllText(DataPath));
        var sc = EditorSceneManager.OpenScene(D.scenePath);

        Transform gate = null;
        foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t != null && t.name == D.gateName) { gate = t; break; }
        if (gate == null) { Debug.LogError("HDRPDOORWAY gate '" + D.gateName + "' not found in scene; nothing built"); return paths.ToArray(); }
        bool have = false; Bounds gb = new Bounds();
        foreach (var r in gate.GetComponentsInChildren<Renderer>(true)) { if (!have) { gb = r.bounds; have = true; } else gb.Encapsulate(r.bounds); }
        if (!have) { Debug.LogError("HDRPDOORWAY gate has no renderers"); return paths.ToArray(); }
        float cx = gb.center.x, frontZ = gb.min.z;
        float floorY = (D.tunnel.useGateFloor ? gb.min.y : D.tunnel.floorY) + D.tunnel.floorOffset;
        Debug.Log("HDRPDOORWAY gate bounds center=" + gb.center + " size=" + gb.size + " frontZ=" + frontZ + " floorY=" + floorY);

        foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t != null && t.parent == null && t.name == D.rootName) UnityEngine.Object.DestroyImmediate(t.gameObject);
        if (D.disableNames != null) foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t != null && Array.IndexOf(D.disableNames, t.name) >= 0) t.gameObject.SetActive(false);

        EnsureFolder(D.prefabFolder); EnsureFolder(D.vfxFolder); EnsureFolder(D.vfxFolder + "/Materials");
        var root = new GameObject(D.rootName).transform;

        if (D.tunnel.enabled) paths.Add(BuildInterior(D, root, cx, frontZ, floorY));
        if (D.fog.enabled) paths.Add(BuildFog(D, root, cx, frontZ, floorY));
        if (D.light.enabled) paths.Add(BuildLight(D, root, cx, frontZ, floorY));
        paths.Add(BuildEmbers(D, root, cx, frontZ, floorY));
        paths.Add(BuildShimmer(D, root, cx, frontZ, floorY));

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
        foreach (var p in paths) if (p != null) Debug.Log("HDRPDOORWAY prefab " + p);
        Debug.Log("HDRPDOORWAY done embers=" + D.embers.enabled + " shimmer=" + D.shimmer.enabled);
        return paths.ToArray();
    }

    // ---------------------------------------------------------------- helpers
    static void EnsureFolder(string path)
    {
        var parts = path.Split('/'); string cur = parts[0];
        for (int i = 1; i < parts.Length; i++) { string next = cur + "/" + parts[i]; if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]); cur = next; }
    }
    static Color Col(float[] c) { return new Color(c[0], c[1], c[2], 1f); }
    static Vector3 V3(float[] a) { return new Vector3(a[0], a[1], a[2]); }

    static Material GetMat(string path, Shader sh)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(sh); AssetDatabase.CreateAsset(m, path); } else m.shader = sh;
        return m;
    }

    static string SavePrefab(GameObject go, string folder, string name)
    {
        string path = folder + "/" + name + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(go, path);
        return path;
    }

    static void SetMesh(string path, Mesh src, out Mesh asset)
    {
        var ex = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (ex == null) { AssetDatabase.CreateAsset(src, path); asset = src; return; }
        ex.Clear(); ex.indexFormat = src.indexFormat;
        ex.vertices = src.vertices; ex.normals = src.normals; ex.uv = src.uv; ex.colors = src.colors; ex.triangles = src.triangles; ex.RecalculateBounds();
        EditorUtility.SetDirty(ex); asset = ex;
    }

    // ---------------------------------------------------------------- noise
    static float Fbm(float x, float y, float z, int oct, int seed)
    {
        float sum = 0f, norm = 0f, amp = 1f, f = 1f;
        for (int o = 0; o < oct; o++)
        {
            float sx = x * f + seed * 7.13f + o * 31.7f, sy = y * f + seed * 3.71f, sz = z * f + seed * 5.17f;
            float n = (Mathf.PerlinNoise(sx, sy) + Mathf.PerlinNoise(sy + 17.3f, sz) + Mathf.PerlinNoise(sz + 41.9f, sx)) / 3f;
            sum += amp * n; norm += amp; amp *= 0.5f; f *= 2.03f;
        }
        return sum / norm;
    }

    // ---------------------------------------------------------------- interior tunnel
    static string BuildInterior(Data D, Transform root, float cx, float frontZ, float floorY)
    {
        var T = D.tunnel;
        int N = Mathf.Max(8, T.ringPoints), R = Mathf.Max(2, T.depthRings), M = Mathf.Max(2, T.backRings);
        float z0 = frontZ + T.startInset, z1 = z0 + T.depth, hh = T.height * 0.5f, hw = T.halfWidth;
        int rows = R + 1 + M, stride = N + 1;
        var verts = new Vector3[rows * stride]; var uvs = new Vector2[rows * stride];
        for (int r = 0; r < rows; r++)
        {
            float k = 1f, z, sc;
            if (r <= R) { float t = (float)r / R; z = z0 + t * T.depth; sc = Mathf.Lerp(1f, T.endScale, t); }
            else { int j = r - R; k = 1f - (float)j / M; z = z1; sc = T.endScale; }
            for (int p = 0; p < stride; p++)
            {
                float th = 2f * Mathf.PI * p / N, c = Mathf.Cos(th), s = Mathf.Sin(th);
                float px = hw * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), T.superExp) * sc;
                float py = hh * Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), T.superExp) * sc;
                float cy = floorY + hh * sc;
                float x = cx + k * px, y = cy + k * py, zz = z;
                float len = Mathf.Sqrt(px * px + py * py);
                float f = T.noiseFreq;
                float nz = Fbm(x * f, y * f, zz * f, T.noiseOctaves, T.seed);
                float w = Mathf.Lerp(T.floorNoiseScale, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((y - floorY) / 0.8f)));
                float d = T.noiseAmp * nz * w * k;           // inward only: the tunnel never gets narrower than halfWidth - noiseAmp
                if (len > 1e-4f) { x -= px / len * d; y -= py / len * d; }
                if (r > R) zz -= T.noiseAmp * 1.5f * nz * (1f - k);
                int i = r * stride + p;
                verts[i] = new Vector3(x, y, zz);
                uvs[i] = new Vector2((float)p / N * 3f, zz * T.uvTile);
            }
        }
        var tris = new List<int>();
        for (int r = 0; r < rows - 1; r++) for (int p = 0; p < N; p++)
        {
            int a = r * stride + p, b = a + 1, c = a + stride, d = c + 1;
            tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(b); tris.Add(c); tris.Add(d);   // inward-facing (viewer inside the tunnel)
        }
        var mesh = new Mesh { name = "DoorwayInterior_mesh", indexFormat = IndexFormat.UInt32 };
        mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris.ToArray(); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        Mesh meshAsset; SetMesh(D.prefabFolder + "/DoorwayInterior_mesh.asset", mesh, out meshAsset);

        // material: copy of the existing furnace basalt (HDRP/Lit, dark), double sided as a winding hedge
        string mp = D.vfxFolder + "/Materials/Doorway_Basalt.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
        if (mat == null)
        {
            var baseMat = AssetDatabase.LoadAssetAtPath<Material>(T.baseMaterial);
            if (baseMat != null) mat = new Material(baseMat);
            else { Debug.LogWarning("HDRPDOORWAY base material missing: " + T.baseMaterial + " (fallback HDRP/Lit)"); mat = new Material(Shader.Find("HDRP/Lit")); mat.SetColor("_BaseColor", Col(T.fallbackColor)); mat.SetFloat("_Smoothness", T.smoothness); mat.SetFloat("_Metallic", 0f); }
            AssetDatabase.CreateAsset(mat, mp);
        }
        mat.SetFloat("_DoubleSidedEnable", 1f); HDMaterial.ValidateMaterial(mat); EditorUtility.SetDirty(mat);

        var go = new GameObject("DoorwayInterior"); go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = meshAsset;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = T.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        if (D.glow.enabled) BuildGlow(D, go.transform, meshAsset, floorY);
        var path = SavePrefab(go, D.prefabFolder, "DoorwayInterior");
        Debug.Log("HDRPDOORWAY tunnel verts=" + verts.Length + " tris=" + tris.Count / 3 + " z " + z0 + ".." + z1);
        return path;
    }

    // low hot glow near the floor only: triangles of the tunnel below glow.height, offset 2 cm inward, additive Furnace/Particle material, vertex colour = height fade, crack texture
    static void BuildGlow(Data D, Transform parent, Mesh tunnel, float floorY)
    {
        var G = D.glow;
        if (!File.Exists(G.texture)) { GenCracks(G); }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(G.texture);
        var v = tunnel.vertices; var tr = tunnel.triangles;
        var gv = new List<Vector3>(); var gu = new List<Vector2>(); var gc = new List<Color>(); var gt = new List<int>();
        for (int i = 0; i < tr.Length; i += 3)
        {
            Vector3 a = v[tr[i]], b = v[tr[i + 1]], c = v[tr[i + 2]];
            if (a.y - floorY > G.height || b.y - floorY > G.height || c.y - floorY > G.height) continue;
            var n = Vector3.Cross(b - a, c - a); if (n.sqrMagnitude < 1e-12f) continue; n.Normalize();
            foreach (var p in new[] { a, b, c })
            {
                float h = Mathf.Clamp01(1f - (p.y - floorY) / G.height); h *= h;
                var q = p + n * G.inset;
                Vector2 uv = (Mathf.Abs(n.y) >= Mathf.Abs(n.x) && Mathf.Abs(n.y) >= Mathf.Abs(n.z)) ? new Vector2(q.x, q.z) : (Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? new Vector2(q.z, q.y) : new Vector2(q.x, q.y));
                gt.Add(gv.Count); gv.Add(q); gu.Add(uv * G.uvTile); gc.Add(new Color(1f, 1f, 1f, h));
            }
        }
        var m = new Mesh { name = "DoorwayEmberGlow_mesh", indexFormat = IndexFormat.UInt32 };
        m.SetVertices(gv); m.SetUVs(0, gu); m.SetColors(gc); m.SetTriangles(gt, 0); m.RecalculateNormals(); m.RecalculateBounds();
        Mesh asset; SetMesh(D.prefabFolder + "/DoorwayEmberGlow_mesh.asset", m, out asset);

        var sh = Shader.Find(G.particleShader); if (sh == null) { Debug.LogWarning("HDRPDOORWAY glow shader " + G.particleShader + " missing; glow skipped"); return; }
        var mat = GetMat(D.vfxFolder + "/Materials/Doorway_EmberGlow.mat", sh);
        mat.SetTexture("_MainTex", tex); mat.SetColor("_Tint", Col(G.tint)); mat.SetFloat("_Intensity", G.intensity);
        mat.SetFloat("_ExposureWeight", 0f); mat.SetFloat("_SoftDistance", 0f); mat.SetFloat("_SrcBlend", 1f); mat.SetFloat("_DstBlend", 1f);
        mat.renderQueue = TransparentQueue; EditorUtility.SetDirty(mat);
        var go = new GameObject("DoorwayEmberGlow"); go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = asset;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        Debug.Log("HDRPDOORWAY glow tris=" + gt.Count / 3);
    }

    // tileable crack texture (rgb = a = crack strength), editor-time only
    static float Hash(int x, int y, int seed) { unchecked { uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177); h = (h ^ (h >> 13)) * 1274126177u; return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF; } }
    static float VNoise(float x, float y, int per, int seed)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y); float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        int x0 = ((xi % per) + per) % per, x1 = (x0 + 1) % per, y0 = ((yi % per) + per) % per, y1 = (y0 + 1) % per;
        return Mathf.Lerp(Mathf.Lerp(Hash(x0, y0, seed), Hash(x1, y0, seed), fx), Mathf.Lerp(Hash(x0, y1, seed), Hash(x1, y1, seed), fx), fy);
    }
    static void GenCracks(GlowRow G)
    {
        int S = G.textureSize; var px = new Color32[S * S];
        for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
        {
            float u = (float)x / S, v = (float)y / S;
            float n = VNoise(u * 6f, v * 6f, 6, G.seed) * 0.65f + VNoise(u * 12f, v * 12f, 12, G.seed + 3) * 0.35f;
            float ridge = Mathf.Abs(n - 0.5f);
            float crack = Mathf.Clamp01(1f - ridge / G.crackWidth); crack *= crack;
            float pool = Mathf.Clamp01((VNoise(u * 3f, v * 3f, 3, G.seed + 9) - 0.62f) * 4f) * 0.5f;
            byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(crack + pool) * 255f);
            px[y * S + x] = new Color32(b, b, b, b);
        }
        var t = new Texture2D(S, S, TextureFormat.RGBA32, false); t.SetPixels32(px);
        SafeIO.WriteAllBytes(G.texture, t.EncodeToPNG()); /* LOCKFIX v103 */ UnityEngine.Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(G.texture, ImportAssetOptions.ForceUpdate);
        var imp = AssetImporter.GetAtPath(G.texture) as TextureImporter;
        if (imp != null) { imp.sRGBTexture = true; imp.mipmapEnabled = true; imp.wrapMode = TextureWrapMode.Repeat; imp.alphaIsTransparency = false; imp.SaveAndReimport(); }
    }

    // ---------------------------------------------------------------- local volumetric fog
    static string BuildFog(Data D, Transform root, float cx, float frontZ, float floorY)
    {
        var group = new GameObject("DoorwayFogLocal"); group.transform.SetParent(root, false);
        var tex = string.IsNullOrEmpty(D.fog.noiseTexture) ? null : AssetDatabase.LoadAssetAtPath<Texture3D>(D.fog.noiseTexture);
        foreach (var row in D.fog.volumes)
        {
            var go = new GameObject(row.name); go.transform.SetParent(group.transform, false);
            go.transform.position = new Vector3(cx, floorY + row.centerHeight, frontZ + row.offsetZ);
            var lv = go.AddComponent<LocalVolumetricFog>(); var p = lv.parameters;
            p.size = V3(row.size); p.albedo = Col(row.albedo); p.meanFreePath = row.meanFreePath; p.anisotropy = row.anisotropy;
            p.blendingMode = LocalVolumetricFogBlendingMode.Additive; p.priority = 0;
            p.volumeMask = tex; p.textureTiling = V3(row.tiling); p.textureScrollingSpeed = V3(row.scroll);
            p.positiveFade = V3(row.fadePos); p.negativeFade = V3(row.fadeNeg);
            lv.parameters = p;
        }
        return SavePrefab(group, D.prefabFolder, "DoorwayFogLocal");
    }

    // ---------------------------------------------------------------- shadowed flicker light
    static string BuildLight(Data D, Transform root, float cx, float frontZ, float floorY)
    {
        var L = D.light;
        var go = new GameObject("DoorwayFlickerLight"); go.transform.SetParent(root, false);
        go.transform.position = new Vector3(cx, floorY + L.centerHeight, frontZ + L.offsetZ);
        var l = go.AddComponent<Light>(); var hd = go.AddComponent<HDAdditionalLightData>();
        l.type = (LightType)Enum.Parse(typeof(LightType), L.kind); l.lightUnit = (LightUnit)Enum.Parse(typeof(LightUnit), L.unit);
        l.intensity = L.intensity; l.range = L.range; l.color = Color.white; l.useColorTemperature = true; l.colorTemperature = L.kelvin;
        if (l.type == LightType.Spot) { l.spotAngle = L.spotAngle; go.transform.rotation = Quaternion.Euler(8f, 180f, 0f); }
        hd.EnableShadows(L.shadows); hd.volumetricDimmer = L.volumetricDimmer;
        if (L.shadows) hd.SetShadowResolution(L.shadowResolution);
        var fl = go.AddComponent<DoorwayFlicker>(); fl.target = l;
        return SavePrefab(go, D.prefabFolder, "DoorwayFlickerLight");
    }

    // ---------------------------------------------------------------- embers (Shuriken; OFF by data)
    static string BuildEmbers(Data D, Transform root, float cx, float frontZ, float floorY)
    {
        var E = D.embers;
        var go = new GameObject("DoorwayEmbers"); go.transform.SetParent(root, false);
        go.transform.position = new Vector3(cx, floorY + 0.05f, frontZ + E.offsetZ); go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);   // shape z = up
        var plane = new GameObject("Plane_DoorwayFloor"); plane.transform.SetParent(go.transform, false); plane.transform.position = new Vector3(cx, floorY, frontZ + E.offsetZ); plane.transform.rotation = Quaternion.identity;
        var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.duration = 5f; main.loop = true; main.prewarm = true; main.playOnAwake = true; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = E.maxParticles; main.gravityModifier = E.gravityModifier;
        main.startLifetime = new ParticleSystem.MinMaxCurve(E.lifetime[0], E.lifetime[1]);
        main.startSize = new ParticleSystem.MinMaxCurve(E.startSize[0], E.startSize[1]);
        main.startSpeed = new ParticleSystem.MinMaxCurve(E.startSpeed[0], E.startSpeed[1]);
        main.startColor = Color.white;
        var em = ps.emission; em.enabled = true; em.rateOverTime = E.ratePerSecond;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = V3(E.emitterSize);
        var vo = ps.velocityOverLifetime; vo.enabled = true; vo.space = ParticleSystemSimulationSpace.World;
        vo.x = new ParticleSystem.MinMaxCurve(E.driftWorld[0]); vo.y = new ParticleSystem.MinMaxCurve(E.driftWorld[1]); vo.z = new ParticleSystem.MinMaxCurve(E.driftWorld[2]);
        var nz = ps.noise; nz.enabled = true; nz.strength = E.noiseStrength; nz.frequency = E.noiseFrequency; nz.scrollSpeed = E.noiseScroll; nz.damping = true; nz.octaveCount = 2; nz.quality = ParticleSystemNoiseQuality.Medium;
        var co = ps.collision; co.enabled = true; co.type = ParticleSystemCollisionType.Planes; co.mode = ParticleSystemCollisionMode.Collision3D;
        co.SetPlane(0, plane.transform); co.bounce = new ParticleSystem.MinMaxCurve(E.bounce); co.dampen = new ParticleSystem.MinMaxCurve(E.dampen); co.lifetimeLoss = new ParticleSystem.MinMaxCurve(E.lifetimeLoss);
        var cl = ps.colorOverLifetime; cl.enabled = true;
        var ck = new List<GradientColorKey>(); foreach (var k in E.colorKeys) ck.Add(new GradientColorKey(Col(k.c), k.t));
        var ak = new List<GradientAlphaKey>(); foreach (var k in E.alphaKeys) ak.Add(new GradientAlphaKey(k.a, k.t));
        var g = new Gradient(); g.SetKeys(ck.ToArray(), ak.ToArray()); cl.color = new ParticleSystem.MinMaxGradient(g);

        var rend = go.GetComponent<ParticleSystemRenderer>(); rend.renderMode = ParticleSystemRenderMode.Billboard; rend.shadowCastingMode = ShadowCastingMode.Off; rend.receiveShadows = false;
        var shader = Shader.Find(E.shader); bool custom = shader != null; if (!custom) { Debug.LogWarning("HDRPDOORWAY ember shader " + E.shader + " missing, fallback " + E.fallbackShader); shader = Shader.Find(E.fallbackShader); }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(E.texture);
        var m = GetMat(D.vfxFolder + "/Materials/Doorway_Ember.mat", shader);
        if (custom)
        {
            m.SetTexture("_MainTex", tex); m.SetColor("_Tint", Col(E.tint)); m.SetFloat("_Intensity", E.intensity); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", E.softDistance);
            m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f);
        }
        else
        {
            m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", 1f); m.SetFloat("_ZWrite", 0f); m.SetFloat("_DoubleSidedEnable", 1f);
            m.SetTexture("_UnlitColorMap", tex); m.SetColor("_UnlitColor", new Color(E.intensity, E.intensity, E.intensity, 1f)); HDMaterial.ValidateMaterial(m);
        }
        m.renderQueue = TransparentQueue; EditorUtility.SetDirty(m); rend.sharedMaterial = m;
        go.SetActive(E.enabled);
        var path = SavePrefab(go, D.prefabFolder, "DoorwayEmbers");
        return path;
    }

    // ---------------------------------------------------------------- heat shimmer (existing HDRP/Unlit distortion approach from HdrpFurnace; OFF by data)
    static string BuildShimmer(Data D, Transform root, float cx, float frontZ, float floorY)
    {
        var S = D.shimmer;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad); q.name = "DoorwayShimmer"; q.transform.SetParent(root, false);
        q.transform.position = new Vector3(cx, floorY + S.bottomOffset + S.height * 0.5f, frontZ + S.offsetZ); q.transform.localScale = new Vector3(S.width, S.height, 1f);
        UnityEngine.Object.DestroyImmediate(q.GetComponent<Collider>());
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(S.texture);
        if (tex == null) { Debug.LogWarning("HDRPDOORWAY shimmer vector map missing (" + S.texture + "): shimmer stays off"); S.enabled = false; }
        var m = GetMat(D.vfxFolder + "/Materials/Doorway_Shimmer.mat", Shader.Find("HDRP/Unlit"));
        m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", 0f); m.SetFloat("_ZWrite", 0f); m.SetFloat("_DoubleSidedEnable", 1f);
        m.SetFloat("_DistortionEnable", 1f); m.SetFloat("_DistortionOnly", 1f); m.SetFloat("_DistortionDepthTest", 1f); m.SetFloat("_DistortionBlendMode", 0f);
        m.SetFloat("_DistortionScale", S.distortionScale); m.SetFloat("_DistortionVectorScale", S.vectorScale); m.SetFloat("_DistortionVectorBias", S.vectorBias); m.SetFloat("_DistortionBlurScale", S.blurScale);
        if (tex != null) { m.SetTexture("_DistortionVectorMap", tex); m.SetTextureScale("_DistortionVectorMap", new Vector2(S.tiling[0], S.tiling[1])); }
        HDMaterial.ValidateMaterial(m); m.renderQueue = TransparentQueue + 10; EditorUtility.SetDirty(m);
        var r = q.GetComponent<Renderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        q.SetActive(S.enabled);
        return SavePrefab(q, D.prefabFolder, "DoorwayShimmer");
    }
}
