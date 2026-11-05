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
        m.EnableKeyword("_COLORMAP"); m.EnableKeyword("_MASKMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO"); m.EnableKeyword("_MATERIAL_AFFECTS_MASKMAP");
        m.DisableKeyword("_NORMALMAP"); m.DisableKeyword("_EMISSIVEMAP"); m.DisableKeyword("_MATERIAL_AFFECTS_NORMAL");
        if (created) AssetDatabase.CreateAsset(m, Path); else EditorUtility.SetDirty(m);
        AssetDatabase.SaveAssets();
        Debug.Log("WoundDecalRef: " + (created ? "created " : "refreshed ") + Path + " keywords: " + string.Join(",", m.shaderKeywords));
    }
}
