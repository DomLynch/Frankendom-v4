#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// FINVIS v1: makes Assets/Resources/FinVisDecalRef.mat, the same trick as WoundDecalRef (QC1 v95): a HDRP/Decal material asset with the keyword set FinVis needs (colour map, normal map, affects albedo / normal / mask map),
// so the build keeps those shader variants and the runtime materials (FinVis.DecalMat copies it) draw. Run once after merging: menu Hades > FinVis > Create decal reference material. Idempotent.
public static class FinVisDecalRef
{
    const string Path = "Assets/Resources/FinVisDecalRef.mat";

    [MenuItem("Hades/FinVis/Create decal reference material")]
    public static void Create()
    {
        var sh = Shader.Find("HDRP/Decal"); if (sh == null) { Debug.LogError("[FinVis] HDRP/Decal shader not found"); return; }
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        var m = AssetDatabase.LoadAssetAtPath<Material>(Path); bool isNew = m == null;
        if (isNew) m = new Material(sh) { name = "FinVisDecalRef" };
        foreach (var k in new[] { "_COLORMAP", "_NORMALMAP", "_MASKMAP", "_MATERIAL_AFFECTS_ALBEDO", "_MATERIAL_AFFECTS_NORMAL", "_MATERIAL_AFFECTS_MASKMAP" }) m.EnableKeyword(k);
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectNormal", 1f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", 0f); m.SetFloat("_AffectSmoothness", 1f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", 1f); m.SetFloat("_NormalBlendSrc", 0f); m.SetFloat("_MaskBlendSrc", 0f);
        if (isNew) AssetDatabase.CreateAsset(m, Path); else EditorUtility.SetDirty(m);
        AssetDatabase.SaveAssets(); Debug.Log("[FinVis] " + Path + (isNew ? " created" : " updated"));
    }
}
#endif
