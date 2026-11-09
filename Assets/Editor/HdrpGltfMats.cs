using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Replaces every glTFast GLB material used by the scene's renderers with an HDRP/Lit material (base colour, normal, packed mask map: R metallic, G AO, A smoothness; emissive; alpha clip),
// so nothing depends on glTFast's URP shader graph. Scene overrides only: the GLBs are untouched.
public static class HdrpGltfMats
{
    static Texture2D Readable(Texture src, bool linear)
    {
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt); var prev = RenderTexture.active; RenderTexture.active = rt;
        var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, linear); t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0); t.Apply();
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); return t;
    }

    // after GLB textures change: drop the scene's material overrides (back to the GLB's own materials), then rebuild every HDRP material from the fresh textures
    public static void Refresh()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); int n = 0;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(r); if (src == null) continue;
            bool ours = false; foreach (var m in r.sharedMaterials) if (m != null && AssetDatabase.GetAssetPath(m).StartsWith("Assets/HDRP/Mats/")) ours = true;
            if (!ours) continue;
            PrefabUtility.RevertObjectOverride(r, InteractionMode.AutomatedAction); n++;
        }
        Debug.Log("HDRPGLTF reverted renderers=" + n);
        Run();
    }
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        Directory.CreateDirectory("Assets/HDRP/Mats"); Directory.CreateDirectory("Assets/HDRP/Masks");
        var hd = Shader.Find("HDRP/Lit"); var map = new Dictionary<Material, Material>(); int n = 0;
        LookData.Reload(); var GLM = LookData.Get().gltfMats; string capKey = (GLM.smoothCapMatch ?? "").ToLowerInvariant(); bool capOn = capKey.Length > 0;   // lane LOOK: smoothness cap / mask size / cutoff from look.json
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var ms = r.sharedMaterials; bool ch = false;
            for (int i = 0; i < ms.Length; i++)
            {
                var m = ms[i]; if (m == null) continue; string mp = AssetDatabase.GetAssetPath(m);
                bool glb = mp.EndsWith(".glb"); bool sg = m.shader != null && m.shader.name.StartsWith("Shader Graphs/glTF");
                if (!glb && !sg) continue;
                if (!map.TryGetValue(m, out var nm))
                {
                    string safe = (Path.GetFileNameWithoutExtension(mp) + "_" + m.name).Replace('/', '_').Replace(':', '_'); if (string.IsNullOrEmpty(mp)) safe = "scene_" + m.name;
                    string path = "Assets/HDRP/Mats/" + safe + ".mat";
                    nm = AssetDatabase.LoadAssetAtPath<Material>(path); if (nm == null) { nm = new Material(hd) { name = safe }; AssetDatabase.CreateAsset(nm, path); }
                    nm.shader = hd;
                    Texture T(string k) => m.HasProperty(k) ? m.GetTexture(k) : null;
                    Color bc = m.HasProperty("baseColorFactor") ? m.GetColor("baseColorFactor") : Color.white;
                    nm.SetColor("_BaseColor", bc); nm.SetTexture("_BaseColorMap", T("baseColorTexture"));
                    var nt = T("normalTexture"); if (nt) { nm.SetTexture("_NormalMap", nt); nm.SetFloat("_NormalScale", m.HasProperty("normalScale") ? m.GetFloat("normalScale") : 1f); }
                    float metal = m.HasProperty("metallicFactor") ? m.GetFloat("metallicFactor") : 0f, rough = m.HasProperty("roughnessFactor") ? m.GetFloat("roughnessFactor") : 0.8f;
                    // old Hades kits ship ORM with metal ~255 on stone: only weapons, braziers and characters keep metal; everything else in Assets/Env is non-metal
                    string lp = mp.ToLowerInvariant(); bool keepMetal = lp.Contains("weapons") || lp.Contains("brazier") || lp.Contains("characters"); if (!keepMetal && lp.Contains("assets/env")) metal = 0f;
                    var mrT = T("metallicRoughnessTexture"); var aoT = T("occlusionTexture");
                    if (mrT != null)
                    {
                        var mr = Readable(mrT, true); var px = mr.GetPixels32(); Color32[] ao = null;
                        if (aoT != null) { var a = Readable(aoT, true); if (a.width == mr.width && a.height == mr.height) ao = a.GetPixels32(); }
                        var mask = new Texture2D(mr.width, mr.height, TextureFormat.RGBA32, false, true); var o = new Color32[px.Length];
                        for (int k = 0; k < px.Length; k++) { byte met = (byte)Mathf.Clamp(px[k].b * metal, 0, 255), sm = (byte)Mathf.Clamp(255 - px[k].g * rough, 0, 255); byte aoc = ao != null ? ao[k].r : px[k].r; o[k] = new Color32(met, aoc, 0, sm); }
                        mask.SetPixels32(o); mask.Apply(); string mpth = "Assets/HDRP/Masks/" + safe + "_mask.png"; SafeIO.WriteAllBytes(mpth, mask.EncodeToPNG()); /* LOCKFIX v103 */ AssetDatabase.ImportAsset(mpth);
                        var ti = AssetImporter.GetAtPath(mpth) as TextureImporter; if (ti != null) { ti.sRGBTexture = false; ti.maxTextureSize = GLM.maskMaxSize; ti.textureCompression = TextureImporterCompression.Compressed; ti.SaveAndReimport(); }
                        float smMax = capOn && (safe.ToLowerInvariant().Contains(capKey) || lp.Contains(capKey)) ? GLM.smoothCap : 1.0f;
                        nm.SetTexture("_MaskMap", AssetDatabase.LoadAssetAtPath<Texture2D>(mpth)); nm.SetFloat("_Metallic", 1f); nm.SetFloat("_Smoothness", 1f); nm.SetFloat("_SmoothnessRemapMin", 0f); nm.SetFloat("_SmoothnessRemapMax", smMax); nm.SetFloat("_AORemapMin", 0f); nm.SetFloat("_AORemapMax", 1f);
                        Object.DestroyImmediate(mr); Object.DestroyImmediate(mask);
                    }
                    else { nm.SetFloat("_Metallic", metal); float sm = 1f - rough; if (capOn && (safe.ToLowerInvariant().Contains(capKey) || lp.Contains(capKey))) sm = Mathf.Min(sm, GLM.smoothCap); nm.SetFloat("_Smoothness", sm); }
                    var et = T("emissiveTexture"); Color ef = m.HasProperty("emissiveFactor") ? m.GetColor("emissiveFactor") : Color.black;
                    if (et != null || ef.maxColorComponent > 0.001f) { nm.SetColor("_EmissiveColor", ef); nm.SetTexture("_EmissiveColorMap", et); }
                    bool clip = m.IsKeywordEnabled("_ALPHATEST_ON") || (m.HasProperty("alphaCutoff") && m.GetFloat("alphaCutoff") > 0f && m.HasProperty("_Mode") && m.GetFloat("_Mode") == 1f);
                    if (clip && !(m.HasProperty("alphaCutoff") && m.GetFloat("alphaCutoff") <= 0f)) { /* AV-W8C: cutoff 0 clips nothing, do not re-enable the alpha-clip flag */ nm.SetFloat("_AlphaCutoffEnable", 1f); nm.SetFloat("_AlphaCutoff", m.HasProperty("alphaCutoff") ? m.GetFloat("alphaCutoff") : GLM.alphaCutoffDefault); }
                    bool keepDouble = lp.Contains("cloak") || lp.Contains("tree") || lp.Contains("hair") || lp.Contains("foliage") || lp.Contains("card"); nm.SetFloat("_DoubleSidedEnable", keepDouble ? 1f : 0f);
                    HDMaterial.ValidateMaterial(nm); EditorUtility.SetDirty(nm); map[m] = nm; n++;
                }
                ms[i] = nm; ch = true;
            }
            if (ch) r.sharedMaterials = ms;
        }
        HdrpGroundLook.Run();   // lane LOOK fix 3: ground detail / roughness breakup (after the paving materials were rebuilt)
        HdrpCharMats.Run(); // fighter materials (Characters/*/Mats): mask maps + Fabric sheen, data: Assets/Data/hdrp_char_mats.json
        AssetDatabase.SaveAssets(); EditorSceneManager.MarkAllScenesDirty(); EditorSceneManager.SaveOpenScenes();
        LodTexRepoint.Run();   // DEDUPE v2: re-point the LOD material slots at LOD0 textures (must follow every Run)
        Debug.Log("HDRPGLTF replaced materials=" + n);
    }
}
