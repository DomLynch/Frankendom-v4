using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Fighter materials, driven by Assets/Data/hdrp_char_mats.json.
// The scene's fighters use Assets/Characters/<Name>/Mats/*.mat (per-part jpgs), not the GLB materials, so HdrpGltfMats never touches them.
// This assigns the baked mask map (R metal, G AO, A smoothness), sets the remaps, and for sheen "fabric" entries switches the material to the
// Fabric Shader Graph (fabric.shader) and feeds it the sheen mask + tint. If that shader does not exist the material stays HDRP/Lit.
public static class HdrpCharMats
{
    // lane LOOK: optional absolute look tweaks per material (baseColor rgb, smoothness remap min / max; missing or -1 = leave)
    [System.Serializable] public class Entry { public string mat, mask, sheenMask, sheen = "off"; public float sheenIntensity = 1f; public float[] baseColor; public float smoothnessMin = -1f, smoothnessMax = -1f; }
    [System.Serializable] public class Lin { public string path; public int maxSize = 2048; }
    [System.Serializable] public class Fab { public string shader; public float[] sheenColor; public float sheenIntensity = 1f; }
    [System.Serializable] public class Cfg { public Entry[] materials; public Lin[] linearTextures; public Fab fabric; }

    const string DataPath = "Assets/Data/hdrp_char_mats.json";

    [MenuItem("Hades/Characters/Apply HDRP character mats")]
    public static void Run()
    {
        if (!File.Exists(DataPath)) { Debug.LogWarning("HDRPCHAR missing " + DataPath); return; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath));
        AssetDatabase.Refresh();

        // linear (non sRGB) data textures: mask maps and sheen masks
        if (cfg.linearTextures != null)
            foreach (var l in cfg.linearTextures)
            {
                if (l == null || string.IsNullOrEmpty(l.path) || !File.Exists(l.path)) { if (l != null) Debug.LogWarning("HDRPCHAR texture missing " + l.path); continue; }
                AssetDatabase.ImportAsset(l.path, ImportAssetOptions.ForceSynchronousImport);
                var ti = AssetImporter.GetAtPath(l.path) as TextureImporter;
                if (ti == null) continue;
                if (ti.sRGBTexture || ti.maxTextureSize != l.maxSize || ti.textureType != TextureImporterType.Default)
                {
                    ti.textureType = TextureImporterType.Default; ti.sRGBTexture = false; ti.maxTextureSize = l.maxSize;
                    ti.textureCompression = TextureImporterCompression.Compressed; ti.mipmapEnabled = true;
                    ti.SaveAndReimport();
                }
            }

        var lit = Shader.Find("HDRP/Lit");
        Shader fabric = (cfg.fabric != null && !string.IsNullOrEmpty(cfg.fabric.shader)) ? Shader.Find(cfg.fabric.shader) : null;
        if (cfg.fabric != null && fabric == null) Debug.LogWarning("HDRPCHAR fabric shader '" + cfg.fabric.shader + "' not found: coat/cloak stay HDRP/Lit (create the Fabric Shader Graph, see _agents/look_apply_notes.md)");
        int n = 0;
        if (cfg.materials != null)
            foreach (var e in cfg.materials)
            {
                if (e == null || string.IsNullOrEmpty(e.mat)) continue;
                var m = AssetDatabase.LoadAssetAtPath<Material>(e.mat);
                if (m == null) { Debug.LogWarning("HDRPCHAR material not found " + e.mat); continue; }
                bool wantFabric = e.sheen == "fabric" && fabric != null && !string.IsNullOrEmpty(e.sheenMask);
                var target = wantFabric ? fabric : lit;
                if (target != null && m.shader != target) m.shader = target;

                var mask = string.IsNullOrEmpty(e.mask) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(e.mask);
                if (mask != null && m.HasProperty("_MaskMap"))
                {
                    m.SetTexture("_MaskMap", mask);
                    SetF(m, "_Metallic", 1f); SetF(m, "_MetallicRemapMin", 0f); SetF(m, "_MetallicRemapMax", 1f);
                    SetF(m, "_Smoothness", 1f); SetF(m, "_SmoothnessRemapMin", 0f); SetF(m, "_SmoothnessRemapMax", 1f);
                    SetF(m, "_AORemapMin", 0f); SetF(m, "_AORemapMax", 1f);
                }
                if (wantFabric)
                {
                    var sm = AssetDatabase.LoadAssetAtPath<Texture2D>(e.sheenMask);
                    if (sm != null && m.HasProperty("_SheenMask")) m.SetTexture("_SheenMask", sm);
                    float[] c = cfg.fabric.sheenColor; if (c == null || c.Length < 3) c = LookData.Get().charMats.sheenDefault;   // lane LOOK: default from look.json
                    float k = cfg.fabric.sheenIntensity * e.sheenIntensity;
                    if (m.HasProperty("_SheenColor")) m.SetColor("_SheenColor", new Color(c[0] * k, c[1] * k, c[2] * k, 1f));
                }
                if (e.baseColor != null && e.baseColor.Length >= 3 && m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(e.baseColor[0], e.baseColor[1], e.baseColor[2], 1f));
                if (e.smoothnessMin >= 0f) SetF(m, "_SmoothnessRemapMin", e.smoothnessMin);
                if (e.smoothnessMax >= 0f) SetF(m, "_SmoothnessRemapMax", e.smoothnessMax);
                if (target == lit) HDMaterial.ValidateMaterial(m);
                EditorUtility.SetDirty(m); n++;
                Debug.Log("HDRPCHAR " + m.name + " shader=" + m.shader.name + " mask=" + (mask != null) + " sheen=" + wantFabric);
            }
        AssetDatabase.SaveAssets();
        Debug.Log("HDRPCHAR materials updated=" + n);
    }

    static void SetF(Material m, string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
}
