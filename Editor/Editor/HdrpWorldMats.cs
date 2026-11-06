using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Hades world build: places the midground terrain GLB and gives it one native HDRP/Lit material (4 PNGs, ORM -> mask map, crimson emissive).
// Data: Assets/Data/world_mats.json. Batch entry: HdrpWorldMats.Run(). Idempotent. Call it AFTER HdrpGltfMats.Refresh() (that pass reverts renderers that use
// Assets/HDRP/Mats/* materials; this material lives in Assets/HDRP/World/ so it is never reverted) and BEFORE HdrpFixes.Run() (layers, hide list).
public static class HdrpWorldMats
{
    [System.Serializable] public class MatCfg
    {
        public string path, albedo, orm, normal, emissive, maskOut;
        public int maxSize = 4096, emissiveMaxSize = 2048, aniso = 16;
        public float[] baseColor, emissiveColor;
        public float normalScale = 1f, metallicScale, roughnessScale = 1f, emissiveNits = 40f;
        public bool normalFlipG;
    }
    [System.Serializable] public class TerrainCfg { public string glb, rootName; public float[] position; public bool shadowsOff; public MatCfg material; }
    [System.Serializable] public class KitEm { public string namePrefix; public float emissive; }
    [System.Serializable] public class Cfg { public TerrainCfg terrain; public KitEm[] kitEmissive; }

    const string DataPath = "Assets/Data/world_mats.json";

    [MenuItem("Hades/World/Apply world materials")]
    public static void Run()
    {
        if (!File.Exists(DataPath)) { Debug.LogWarning("HDRPWORLD missing " + DataPath); return; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath));
        AssetDatabase.Refresh();
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/HadesSlice.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");

        if (cfg.terrain != null && cfg.terrain.material != null)
        {
            var mat = BuildTerrainMaterial(cfg.terrain.material);
            if (mat != null) PlaceTerrain(cfg.terrain, mat);
        }
        int kit = ApplyKitEmissive(cfg.kitEmissive);
        HdrpGroundLook.Run();   // lane LOOK fix 3: re-apply the ground breakup after the terrain material / mask were rebuilt
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPWORLD done kitEmissiveMaterials=" + kit);
    }

    static Texture2D LoadRaw(string path)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!t.LoadImage(File.ReadAllBytes(path))) { Object.DestroyImmediate(t); return null; }
        return t;
    }

    static void WritePng(string path, Texture2D t) { File.WriteAllBytes(path, t.EncodeToPNG()); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport); }

    // texture import settings (idempotent: reimports only when something differs)
    static void Imp(string path, bool srgb, bool normal, int maxSize, int aniso)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var ti = AssetImporter.GetAtPath(path) as TextureImporter; if (ti == null) { Debug.LogWarning("HDRPWORLD no importer for " + path); return; }
        var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        bool diff = ti.textureType != type || ti.maxTextureSize != maxSize || ti.anisoLevel != aniso || ti.wrapMode != TextureWrapMode.Repeat || !ti.mipmapEnabled || (!normal && ti.sRGBTexture != srgb);
        if (!diff) return;
        ti.textureType = type; if (!normal) ti.sRGBTexture = srgb;
        ti.maxTextureSize = maxSize; ti.anisoLevel = aniso; ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true;
        ti.textureCompression = TextureImporterCompression.Compressed;
        ti.SaveAndReimport();
    }

    static Material BuildTerrainMaterial(MatCfg c)
    {
        foreach (var p in new[] { c.albedo, c.orm, c.normal, c.emissive })
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) { Debug.LogWarning("HDRPWORLD terrain texture missing: " + p + " (copy Assets/Env/World/Terrain/Tex first)"); return null; }

        // ORM -> mask map (R metallic, G AO, B 0, A smoothness), rebuilt only when the ORM is newer than the mask
        string maskPath = c.maskOut;
        if (!File.Exists(maskPath) || File.GetLastWriteTimeUtc(c.orm) > File.GetLastWriteTimeUtc(maskPath))
        {
            var orm = LoadRaw(c.orm); if (orm == null) { Debug.LogWarning("HDRPWORLD cannot decode " + c.orm); return null; }
            var px = orm.GetPixels32(); var o = new Color32[px.Length];
            for (int k = 0; k < px.Length; k++)
            {
                byte met = (byte)Mathf.Clamp(px[k].b * c.metallicScale, 0, 255), sm = (byte)Mathf.Clamp(255 - px[k].g * c.roughnessScale, 0, 255);
                o[k] = new Color32(met, px[k].r, 0, sm);
            }
            var mask = new Texture2D(orm.width, orm.height, TextureFormat.RGBA32, false, true); mask.SetPixels32(o); mask.Apply();
            WritePng(maskPath, mask); Object.DestroyImmediate(mask); Object.DestroyImmediate(orm);
            Debug.Log("HDRPWORLD mask map written " + maskPath);
        }

        // optional green-channel flip of the normal map (glTF +Y vs Unity): writes a sibling PNG, source stays untouched
        string normalPath = c.normal;
        if (c.normalFlipG)
        {
            string fp = Path.Combine(Path.GetDirectoryName(c.normal), Path.GetFileNameWithoutExtension(c.normal) + "_flipG.png").Replace('\\', '/');
            if (!File.Exists(fp) || File.GetLastWriteTimeUtc(c.normal) > File.GetLastWriteTimeUtc(fp))
            {
                var nt = LoadRaw(c.normal);
                if (nt != null)
                {
                    var np = nt.GetPixels32(); for (int k = 0; k < np.Length; k++) np[k].g = (byte)(255 - np[k].g);
                    nt.SetPixels32(np); nt.Apply(); WritePng(fp, nt); Object.DestroyImmediate(nt);
                }
            }
            if (File.Exists(fp)) normalPath = fp;
        }

        Imp(c.albedo, true, false, c.maxSize, c.aniso);
        Imp(normalPath, false, true, c.maxSize, c.aniso);
        Imp(maskPath, false, false, c.maxSize, c.aniso);
        Imp(c.emissive, true, false, c.emissiveMaxSize, c.aniso);

        Directory.CreateDirectory(Path.GetDirectoryName(c.path));
        var m = AssetDatabase.LoadAssetAtPath<Material>(c.path);
        if (m == null) { m = new Material(Shader.Find("HDRP/Lit")) { name = Path.GetFileNameWithoutExtension(c.path) }; AssetDatabase.CreateAsset(m, c.path); }
        m.shader = Shader.Find("HDRP/Lit");
        var bc = c.baseColor != null && c.baseColor.Length >= 3 ? new Color(c.baseColor[0], c.baseColor[1], c.baseColor[2], 1f) : Color.white;
        m.SetColor("_BaseColor", bc);
        m.SetTexture("_BaseColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(c.albedo));
        m.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath)); m.SetFloat("_NormalScale", c.normalScale);
        m.SetTexture("_MaskMap", AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath));
        m.SetFloat("_Metallic", 1f); m.SetFloat("_MetallicRemapMin", 0f); m.SetFloat("_MetallicRemapMax", 1f);
        m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessRemapMin", 0f); m.SetFloat("_SmoothnessRemapMax", 1f);
        m.SetFloat("_AORemapMin", 0f); m.SetFloat("_AORemapMax", 1f);
        m.SetFloat("_DoubleSidedEnable", 0f);
        var et = AssetDatabase.LoadAssetAtPath<Texture2D>(c.emissive);
        if (c.emissiveNits > 0f && et != null)
        {
            var ec = c.emissiveColor != null && c.emissiveColor.Length >= 3 ? new Color(c.emissiveColor[0], c.emissiveColor[1], c.emissiveColor[2], 1f) : Color.white;
            m.SetTexture("_EmissiveColorMap", et);
            m.SetFloat("_UseEmissiveIntensity", 0f);   // _EmissiveColor is used as the HDR value directly (nits), same as HdrpGltfMats
            m.SetColor("_EmissiveColor", new Color(ec.r * c.emissiveNits, ec.g * c.emissiveNits, ec.b * c.emissiveNits, 1f));
        }
        else { m.SetTexture("_EmissiveColorMap", null); m.SetColor("_EmissiveColor", Color.black); }
        HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m);
        Debug.Log("HDRPWORLD terrain material " + c.path + " emissiveNits=" + c.emissiveNits + " flipG=" + c.normalFlipG);
        return m;
    }

    static void PlaceTerrain(TerrainCfg t, Material mat)
    {
        var glb = AssetDatabase.LoadAssetAtPath<GameObject>(t.glb);
        if (glb == null) { Debug.LogWarning("HDRPWORLD terrain model not found " + t.glb); return; }
        string rn = string.IsNullOrEmpty(t.rootName) ? "WorldTerrain" : t.rootName;
        foreach (var rg in EditorSceneManager.GetActiveScene().GetRootGameObjects()) if (rg.name == rn) Object.DestroyImmediate(rg);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(glb); inst.name = rn;
        inst.transform.position = t.position != null && t.position.Length >= 3 ? new Vector3(t.position[0], t.position[1], t.position[2]) : Vector3.zero;
        int n = 0, tris = 0;
        foreach (var r in inst.GetComponentsInChildren<Renderer>())
        {
            var ms = new Material[r.sharedMaterials.Length]; for (int i = 0; i < ms.Length; i++) ms[i] = mat;
            r.sharedMaterials = ms; if (t.shadowsOff) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; n++;
            var mf = r.GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null) for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) tris += (int)(mf.sharedMesh.GetIndexCount(s) / 3);
        }
        Debug.Log("HDRPWORLD terrain placed root=" + rn + " renderers=" + n + " tris=" + tris);
    }

    // glTF-converted materials (Assets/HDRP/Mats/<glb file name>_<material>) of the kit pieces: absolute emissive value when they have an emissive map
    static int ApplyKitEmissive(KitEm[] rows)
    {
        if (rows == null) return 0;
        int n = 0;
        foreach (var g in AssetDatabase.FindAssets("t:Material", new[] { "Assets/HDRP/Mats" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)); if (m == null || !m.HasProperty("_EmissiveColorMap") || m.GetTexture("_EmissiveColorMap") == null) continue;
            foreach (var r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.namePrefix) || !m.name.StartsWith(r.namePrefix)) continue;
                m.SetColor("_EmissiveColor", new Color(r.emissive, r.emissive, r.emissive, 1f)); EditorUtility.SetDirty(m); n++; break;
            }
        }
        return n;
    }
}
