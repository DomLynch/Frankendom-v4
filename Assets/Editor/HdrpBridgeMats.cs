using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Bridge / platform / road stone: assigns the tileable bridge_stone_v4 PBR set to existing HDRP/Lit scene materials (M_PlatformCourses, M_HillRoad).
// Data: Assets/Data/bridge_mats.json (material path, texture folder + file prefix, tiling, normal strength, smoothness mapping).
// Packs <prefix>_orm.png (R AO, G roughness, B 0) into <prefix>_mask.png (HDRP mask map: R metal 0, G AO, B detail 0, A smoothness = 1 - roughness).
// Batch entry: HdrpBridgeMats.Run(). Idempotent. Edits only the material assets (no scene). Call it AFTER HdrpGltfMats.Refresh() and HdrpFixes.Run()
// (and after HdrpConvert.Run(), which would re-apply the old URP-converted textures to Assets/Env/M_*.mat).
public static class HdrpBridgeMats
{
    [System.Serializable] public class Row
    {
        public string material, folder, prefix;
        public float[] tiling, offset, baseColor;
        public float normalScale = 1f, roughnessScale = 1f, smoothnessRemapMin = 0f, smoothnessRemapMax = 1f, aoStrength = 1f, metallic = 0f;
    }
    [System.Serializable] public class Cfg { public int textureMaxSize = 4096, aniso = 16; public Row[] rows; }

    const string DataPath = "Assets/Data/bridge_mats.json";

    [MenuItem("Hades/World/Apply bridge stone materials")]
    public static void Run()
    {
        if (!File.Exists(DataPath)) { Debug.LogWarning("HDRPBRIDGE missing " + DataPath); return; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath));
        if (cfg == null || cfg.rows == null) { Debug.LogWarning("HDRPBRIDGE no rows in " + DataPath); return; }
        AssetDatabase.Refresh();
        int ok = 0;
        foreach (var r in cfg.rows) { if (r != null && Apply(r, cfg)) ok++; }
        AssetDatabase.SaveAssets();
        Debug.Log("HDRPBRIDGE done materials=" + ok + "/" + cfg.rows.Length);
    }

    static Texture2D LoadRaw(string path)
    {
        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!t.LoadImage(File.ReadAllBytes(path))) { Object.DestroyImmediate(t); return null; }
        return t;
    }

    // idempotent import settings: reimports only when something differs
    static void Imp(string path, bool srgb, bool normal, int maxSize, int aniso)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { Debug.LogWarning("HDRPBRIDGE no importer for " + path); return; }
        var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        bool diff = ti.textureType != type || ti.maxTextureSize != maxSize || ti.anisoLevel != aniso || ti.wrapMode != TextureWrapMode.Repeat || !ti.mipmapEnabled || (!normal && ti.sRGBTexture != srgb);
        if (!diff) return;
        ti.textureType = type; if (!normal) ti.sRGBTexture = srgb;
        ti.maxTextureSize = maxSize; ti.anisoLevel = aniso; ti.wrapMode = TextureWrapMode.Repeat; ti.mipmapEnabled = true;
        ti.textureCompression = TextureImporterCompression.Compressed;
        ti.SaveAndReimport();
    }

    static bool Apply(Row r, Cfg cfg)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(r.material);
        if (m == null) { Debug.LogWarning("HDRPBRIDGE material not found " + r.material); return false; }
        string a = r.folder + "/" + r.prefix + "_albedo.png", n = r.folder + "/" + r.prefix + "_normal.png", orm = r.folder + "/" + r.prefix + "_orm.png", mask = r.folder + "/" + r.prefix + "_mask.png";
        foreach (var p in new[] { a, n, orm })
            if (!File.Exists(p)) { Debug.LogWarning("HDRPBRIDGE texture missing " + p); return false; }

        // ORM -> mask map; rebuilt when missing, older than the ORM, or the baked knobs changed (a stamp file next to the mask stores roughnessScale|aoStrength)
        string stamp = mask + ".params.txt";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string want = r.roughnessScale.ToString("R", inv) + "|" + r.aoStrength.ToString("R", inv);
        bool stale = !File.Exists(mask) || File.GetLastWriteTimeUtc(orm) > File.GetLastWriteTimeUtc(mask) || !File.Exists(stamp) || File.ReadAllText(stamp) != want;
        if (stale)
        {
            var t = LoadRaw(orm);
            if (t == null) { Debug.LogWarning("HDRPBRIDGE cannot decode " + orm); return false; }
            var px = t.GetPixels32(); var o = new Color32[px.Length];
            for (int k = 0; k < px.Length; k++)
            {
                byte ao = (byte)Mathf.Clamp(255f - (255f - px[k].r) * r.aoStrength, 0f, 255f);
                byte sm = (byte)Mathf.Clamp(255f - px[k].g * r.roughnessScale, 0f, 255f);
                o[k] = new Color32(0, ao, 0, sm);
            }
            var mt = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false, true); mt.SetPixels32(o); mt.Apply();
            SafeIO.WriteAllBytes(mask, mt.EncodeToPNG()); SafeIO.WriteAllText(stamp, want); /* LOCKFIX v103 */
            Object.DestroyImmediate(mt); Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(mask, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("HDRPBRIDGE mask map written " + mask);
        }

        Imp(a, true, false, cfg.textureMaxSize, cfg.aniso);
        Imp(n, false, true, cfg.textureMaxSize, cfg.aniso);
        Imp(mask, false, false, cfg.textureMaxSize, cfg.aniso);

        var ta = AssetDatabase.LoadAssetAtPath<Texture2D>(a); var tn = AssetDatabase.LoadAssetAtPath<Texture2D>(n); var tm = AssetDatabase.LoadAssetAtPath<Texture2D>(mask);
        m.shader = Shader.Find("HDRP/Lit");
        Color bc = r.baseColor != null && r.baseColor.Length >= 3 ? new Color(r.baseColor[0], r.baseColor[1], r.baseColor[2], 1f) : Color.white;
        m.SetColor("_BaseColor", bc);
        m.SetTexture("_BaseColorMap", ta);
        m.SetTexture("_NormalMap", tn); m.SetFloat("_NormalScale", r.normalScale);
        m.SetTexture("_MaskMap", tm);
        m.SetFloat("_Metallic", r.metallic); m.SetFloat("_MetallicRemapMin", 0f); m.SetFloat("_MetallicRemapMax", 1f);
        m.SetFloat("_Smoothness", 1f); m.SetFloat("_SmoothnessRemapMin", r.smoothnessRemapMin); m.SetFloat("_SmoothnessRemapMax", r.smoothnessRemapMax);
        m.SetFloat("_AORemapMin", 0f); m.SetFloat("_AORemapMax", 1f);
        m.SetTexture("_DetailMap", null);   // no URP-leftover detail layer on top of the flagstone
        m.SetFloat("_DoubleSidedEnable", 0f);

        var sc = r.tiling != null && r.tiling.Length >= 2 ? new Vector2(r.tiling[0], r.tiling[1]) : Vector2.one;
        var of = r.offset != null && r.offset.Length >= 2 ? new Vector2(r.offset[0], r.offset[1]) : Vector2.zero;
        foreach (var prop in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap" }) { m.SetTextureScale(prop, sc); m.SetTextureOffset(prop, of); }
        // the URP-converted leftovers (_BaseMap/_BumpMap/_MainTex) are unused by HDRP/Lit; keep them in step with the new stone
        if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", ta); m.SetTextureScale("_BaseMap", sc); }
        if (m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", tn); m.SetTextureScale("_BumpMap", sc); }
        if (m.HasProperty("_MainTex")) { m.SetTexture("_MainTex", ta); m.SetTextureScale("_MainTex", sc); }

        HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m);
        Debug.Log("HDRPBRIDGE " + r.material + " <- " + r.prefix + " tiling=" + sc + " normal=" + r.normalScale + " smooth=[" + r.smoothnessRemapMin + "," + r.smoothnessRemapMax + "]");
        return true;
    }
}
