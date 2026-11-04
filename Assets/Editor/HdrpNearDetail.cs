using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Near-detail pass (Hades env): HDRP DecalProjectors (cracked paving, soot, moss in cracks, chips) from Assets/Data/decals.json and low-lying ground fog (Local Volumetric Fog boxes) from Assets/Data/ground_fog_wisps.json.
// Toggle/data: Assets/Data/near_detail.json  { "decals": true, "wisps": true, "wispCount": 16 }  (missing file = both on). Variant: Assets/Data/approach.json mode (flat|stairs) decides whether rows tagged
// 'variant_gateApproach_flat' are spawned; rows with yBase "platformTop" use the layout constants.platformTop (0.3 flat / 2.0 stairs).
// Idempotent: deletes and rebuilds the scene root "HadesNearDetail". Batch entry: HdrpNearDetail.Run(). Log tag HDRPDETAIL. Run AFTER HdrpApproach.Run() and PropsLayout.RebuildAndSave().
public static class HdrpNearDetail
{
    [System.Serializable] public class Decal { public string id, texture, normal, yBase, zone; public float[] pos, eulerDeg, size; public float opacity = 1f; public string[] tags; }
    [System.Serializable] public class DecalFile { public Decal[] decals; }
    [System.Serializable] public class Vol { public string name; public float[] center, size, albedoSrgb; public float rotY, meanFreePath = 15f, blendDistance = 7f; }
    [System.Serializable] public class WispFile { public Vol[] volumes; }
    [System.Serializable] class Toggle { public bool decals = true, wisps = true; public int wispCount = 64; }
    [System.Serializable] class Approach { public string mode = "flat"; }

    const string RootName = "HadesNearDetail";

    [MenuItem("Hades/World/Apply near detail (decals + ground fog)")]
    public static void Run()
    {
        var tg = File.Exists("Assets/Data/near_detail.json") ? JsonUtility.FromJson<Toggle>(File.ReadAllText("Assets/Data/near_detail.json")) : new Toggle();
        var ap = File.Exists("Assets/Data/approach.json") ? JsonUtility.FromJson<Approach>(File.ReadAllText("Assets/Data/approach.json")) : new Approach();
        bool flat = ap.mode == "flat";
        float platformTop = 2f;
        try { var L = PropsLayout.Load(); if (L != null && L.constants != null) platformTop = L.constants.platformTop; } catch (System.Exception e) { Debug.LogWarning("HDRPDETAIL platformTop read failed: " + e.Message); }
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/HadesSlice.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        foreach (var rg in scene.GetRootGameObjects()) if (rg.name == RootName) Object.DestroyImmediate(rg);
        var root = new GameObject(RootName);
        int nd = 0, nw = 0, skipped = 0;
        if (tg.decals && File.Exists("Assets/Data/decals.json"))
        {
            var df = JsonUtility.FromJson<DecalFile>(File.ReadAllText("Assets/Data/decals.json"));
            var mats = new Dictionary<string, Material>();
            if (df != null && df.decals != null)
                foreach (var d in df.decals)
                {
                    if (d == null || d.pos == null || d.size == null || d.eulerDeg == null) continue;
                    bool flatOnly = d.tags != null && System.Array.IndexOf(d.tags, "variant_gateApproach_flat") >= 0;
                    if (flatOnly && !flat) { skipped++; continue; }
                    var m = GetMat(d, mats); if (m == null) continue;
                    float y = d.pos[1] + (d.yBase == "platformTop" ? platformTop : 0f) + 0.3f;
                    var g = new GameObject(d.id); g.transform.SetParent(root.transform);
                    g.transform.position = new Vector3(d.pos[0], y, d.pos[2]); g.transform.rotation = Quaternion.Euler(d.eulerDeg[0], d.eulerDeg[1], d.eulerDeg[2]);
                    var dp = g.AddComponent<DecalProjector>(); dp.material = m; dp.size = new Vector3(d.size[0], d.size[1], d.size[2]); dp.fadeFactor = Mathf.Clamp01(d.opacity);
                    dp.drawDistance = 40f; dp.fadeScale = 0.8f; dp.startAngleFade = 80f; dp.endAngleFade = 100f; nd++;
                }
        }
        if (tg.wisps && File.Exists("Assets/Data/ground_fog_wisps.json"))
        {
            var wf = JsonUtility.FromJson<WispFile>(File.ReadAllText("Assets/Data/ground_fog_wisps.json"));
            if (wf != null && wf.volumes != null)
                foreach (var v in wf.volumes)
                {
                    if (nw >= tg.wispCount) break;
                    if (v == null || v.center == null || v.size == null) continue;
                    var g = new GameObject(v.name); g.transform.SetParent(root.transform);
                    g.transform.position = new Vector3(v.center[0], v.center[1], v.center[2]); g.transform.rotation = Quaternion.Euler(0f, v.rotY, 0f);
                    var f = g.AddComponent<LocalVolumetricFog>(); var p = f.parameters;
                    p.size = new Vector3(v.size[0], v.size[1], v.size[2]);
                    var a = v.albedoSrgb != null && v.albedoSrgb.Length >= 3 ? new Color(v.albedoSrgb[0] / 255f, v.albedoSrgb[1] / 255f, v.albedoSrgb[2] / 255f) : new Color(0.25f, 0.24f, 0.24f);
                    p.albedo = a; p.meanFreePath = v.meanFreePath;
                    float fx = Mathf.Clamp01(v.blendDistance / Mathf.Max(0.1f, v.size[0] * 0.5f)), fy = Mathf.Clamp01(v.blendDistance / Mathf.Max(0.1f, v.size[1] * 0.5f)), fz = Mathf.Clamp01(v.blendDistance / Mathf.Max(0.1f, v.size[2] * 0.5f));
                    p.positiveFade = new Vector3(fx, Mathf.Min(fy, 0.5f), fz); p.negativeFade = new Vector3(fx, Mathf.Min(fy, 0.5f), fz);
                    f.parameters = p; nw++;
                }
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPDETAIL done decals=" + nd + " skippedFlatOnly=" + skipped + " wisps=" + nw + " mode=" + ap.mode + " platformTop=" + platformTop);
    }

    static Material GetMat(Decal d, Dictionary<string, Material> cache)
    {
        if (cache.TryGetValue(d.texture, out var c)) return c;
        string mp = "Assets/HDRP/World/Decals/M_" + Path.GetFileNameWithoutExtension(d.texture) + ".mat";
        Directory.CreateDirectory(Path.GetDirectoryName(mp));
        AssetDatabase.ImportAsset(d.texture, ImportAssetOptions.ForceSynchronousImport);
        if (!string.IsNullOrEmpty(d.normal)) { AssetDatabase.ImportAsset(d.normal, ImportAssetOptions.ForceSynchronousImport); SetNormalImport(d.normal); }
        var ti = AssetImporter.GetAtPath(d.texture) as TextureImporter;
        if (ti != null && (!ti.alphaIsTransparency || ti.wrapMode != TextureWrapMode.Clamp)) { ti.alphaIsTransparency = true; ti.wrapMode = TextureWrapMode.Clamp; ti.SaveAndReimport(); }
        var sh = Shader.Find("HDRP/Decal");
        if (sh == null) { Debug.LogWarning("HDRPDETAIL shader HDRP/Decal not found"); return null; }
        var m = AssetDatabase.LoadAssetAtPath<Material>(mp);
        if (m == null) { m = new Material(sh) { name = Path.GetFileNameWithoutExtension(mp) }; AssetDatabase.CreateAsset(m, mp); }
        m.shader = sh;
        m.SetTexture("_BaseColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(d.texture));
        if (!string.IsNullOrEmpty(d.normal)) m.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>(d.normal));
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectNormal", string.IsNullOrEmpty(d.normal) ? 0f : 1f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", 0f); m.SetFloat("_AffectSmoothness", 0f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", 1f); m.SetFloat("_NormalBlendSrc", 0f);
        HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); cache[d.texture] = m; return m;
    }

    static void SetNormalImport(string path)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter; if (ti == null) return;
        if (ti.textureType != TextureImporterType.NormalMap || ti.wrapMode != TextureWrapMode.Clamp) { ti.textureType = TextureImporterType.NormalMap; ti.wrapMode = TextureWrapMode.Clamp; ti.SaveAndReimport(); }
    }
}
