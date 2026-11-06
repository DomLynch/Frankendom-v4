using System.IO;
using UnityEditor;
using UnityEngine;

// v95: reference material for the runtime wound decals (WoundDecals.DecalMat).
// Root cause of "no wound decal draws in the player" (v93 woundshot: 4 live decals per fighter, nothing visible):
//  * HDRP/Decal keeps _COLORMAP / _MASKMAP / _MATERIAL_AFFECTS_ALBEDO / _MATERIAL_AFFECTS_MASKMAP as shader_feature variants. A shader_feature variant is only built when a material that is IN the build enables it.
//    The runtime materials (new Material(Shader.Find("HDRP/Decal")) + SetTexture) never enabled any keyword and no asset in the project does, so the player has none of those variants (and even with them the runtime material would not select them).
//  * Fix: this asset (Assets/Resources/WoundDecalRef.mat, shipped because it is in Resources) enables exactly the keywords the wounds use; WoundDecals clones it (new Material(ref) keeps the keywords) and sets its textures.
// Run: batch  -executeMethod WoundDecalRef.Run  (HdrpBatch.V51 calls it) or menu Tools > Combat > Build Wound Decal Reference Material. Idempotent.
public static class WoundDecalRef
{
    public const string Path = "Assets/Resources/WoundDecalRef.mat";

    [MenuItem("Tools/Combat/Build Wound Decal Reference Material")]
    public static void Run()
    {
        var sh = Shader.Find("HDRP/Decal");
        if (sh == null) { Debug.LogError("WoundDecalRef: HDRP/Decal shader not found"); return; }
        Directory.CreateDirectory("Assets/Resources");
        var m = AssetDatabase.LoadAssetAtPath<Material>(Path);
        bool created = m == null;
        if (created) m = new Material(sh); else m.shader = sh;
        m.name = "WoundDecalRef";
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectNormal", 0f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", 0f); m.SetFloat("_AffectSmoothness", 1f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", 1f);
        m.SetColor("_BaseColor", Color.white);
        // v95b: HDRP's material post-processor re-derives _COLORMAP / _MASKMAP from the texture slots (no texture = keyword off), so v95's EnableKeyword calls were cleared on save (the v95 log listed only
        // _MATERIAL_AFFECTS_ALBEDO,_MATERIAL_AFFECTS_MASKMAP) and the player still had no colour/mask variants. Give the reference material real textures so the keywords survive.
        m.SetTexture("_BaseColorMap", MakeTex("WoundDecalRef_base", true));
        m.SetTexture("_MaskMap", MakeTex("WoundDecalRef_mask", false));
        // WOUNDLOOK v96: the runtime decals also use _NORMALMAP / _MATERIAL_AFFECTS_NORMAL; give the ref a flat normal map and AffectNormal so those variants ship in the player
        m.SetFloat("_AffectNormal", 1f); m.SetTexture("_NormalMap", MakeNormalTex("WoundDecalRef_normal"));
        m.SetColor("_BaseColor", new Color(0.3f, 0.015f, 0.015f, 1f));
        m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_NORMAL");
        m.EnableKeyword("_COLORMAP"); m.EnableKeyword("_MASKMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO"); m.EnableKeyword("_MATERIAL_AFFECTS_MASKMAP");
        m.DisableKeyword("_EMISSIVEMAP");
        if (created) AssetDatabase.CreateAsset(m, Path); else EditorUtility.SetDirty(m);
        AssetDatabase.SaveAssets();
        Debug.Log("WoundDecalRef: " + (created ? "created " : "refreshed ") + Path + " keywords: " + string.Join(",", m.shaderKeywords)); if (!m.IsKeywordEnabled("_NORMALMAP") || !m.IsKeywordEnabled("_COLORMAP") || !m.IsKeywordEnabled("_MASKMAP")) Debug.LogError("WoundDecalRef: _COLORMAP / _MASKMAP are NOT enabled after save: the decal variants will be missing from the player");
    }

    static Texture2D MakeNormalTex(string name)   // WOUNDLOOK v96
    {
        string path = "Assets/Resources/" + name + ".png"; var t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (t == null)
        {
            var src = new Texture2D(4, 4, TextureFormat.RGBA32, false, true); var px = new Color32[16]; for (int i = 0; i < 16; i++) px[i] = new Color32(128, 128, 255, 255); src.SetPixels32(px); src.Apply();
            File.WriteAllBytes(path, src.EncodeToPNG()); Object.DestroyImmediate(src); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter; if (ti != null) { ti.textureType = TextureImporterType.NormalMap; ti.mipmapEnabled = false; ti.SaveAndReimport(); }
            t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        return t;
    }

    static Texture2D MakeTex(string name, bool srgb)
    {
        string path = "Assets/Resources/" + name + ".png";
        var t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (t == null)
        {
            var src = new Texture2D(4, 4, TextureFormat.RGBA32, false, !srgb); var px = new Color32[16]; for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255); src.SetPixels32(px); src.Apply();
            File.WriteAllBytes(path, src.EncodeToPNG()); Object.DestroyImmediate(src);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter; if (ti != null) { ti.sRGBTexture = srgb; ti.mipmapEnabled = false; ti.SaveAndReimport(); }
            t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        return t;
    }}
