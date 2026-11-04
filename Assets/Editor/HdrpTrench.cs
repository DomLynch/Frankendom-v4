using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Bridge trench (zone 5) scene setup, data in Assets/Data/trench_setup.json (editor section). Idempotent: deletes and rebuilds the scene root "HadesTrench".
//  - invisible deck-edge blockers on both sides of the bridge (BoxColliders, no renderer) so the player cannot fall into the 12 m slot
//  - crimson lava point lights along the trench floor (HDRP lumens, no shadows)
//  - one Local Volumetric Fog box (near-black albedo) that darkens the upper trench and lets the lava glow read at the bottom
//  - triplanar copy of the terrain material (M_WorldTerrainTriplanar) on the inner terrain chunks (names ending _b0) because the steep trench walls smear the planar UVs
// Batch entry: HdrpTrench.Run() (call after HdrpWorldMats.Run(), which creates WorldTerrain and M_WorldTerrain). Log tag: HDRPTRENCH.
public static class HdrpTrench
{
    [System.Serializable] public class TLight { public string type; public float[] pos, color; public float intensityLumens, range; public bool shadows; }
    [System.Serializable] public class Blockers { public float x = 3.3f, z0 = 8f, z1 = 22f, yMin = -1f, yMax = 3.5f, thickness = 0.3f; }
    [System.Serializable] public class FogCfg { public float[] center, size, albedo; public float meanFreePath = 8f, blendDistance = 2f; }
    [System.Serializable] public class TriCfg { public string sourceMaterial, newMaterial, chunkSuffix = "_b0"; public float worldTileM = 64f; }
    [System.Serializable] public class Cfg { public TLight[] lights; public Blockers deckBlockers; public FogCfg fog; public TriCfg triplanar; }

    const string DataPath = "Assets/Data/trench_setup.json";
    const string RootName = "HadesTrench";

    [MenuItem("Hades/World/Apply trench setup")]
    public static void Run()
    {
        if (!File.Exists(DataPath)) { Debug.LogWarning("HDRPTRENCH missing " + DataPath); return; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath));
        if (cfg == null) { Debug.LogWarning("HDRPTRENCH cannot parse " + DataPath); return; }
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/HadesSlice.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        foreach (var rg in scene.GetRootGameObjects()) if (rg.name == RootName) Object.DestroyImmediate(rg);
        var root = new GameObject(RootName);

        int cols = 0, lights = 0, fogs = 0, tri = 0;
        if (cfg.deckBlockers != null)
        {
            var b = cfg.deckBlockers; float len = b.z1 - b.z0, h = b.yMax - b.yMin;
            for (int side = -1; side <= 1; side += 2)
            {
                var g = new GameObject("DeckEdgeBlocker_" + (side < 0 ? "L" : "R")); g.transform.SetParent(root.transform);
                g.transform.position = new Vector3(side * b.x, (b.yMin + b.yMax) * 0.5f, (b.z0 + b.z1) * 0.5f);
                g.AddComponent<BoxCollider>().size = new Vector3(b.thickness, h, len); cols++;
            }
        }
        if (cfg.lights != null)
        {
            foreach (var t in cfg.lights)
            {
                if (t == null || t.pos == null || t.pos.Length < 3) continue;
                var g = new GameObject("TrenchLava_" + lights); g.transform.SetParent(root.transform); g.transform.position = new Vector3(t.pos[0], t.pos[1], t.pos[2]);
                var l = g.AddComponent<Light>(); l.type = LightType.Point; l.range = t.range;
                l.color = t.color != null && t.color.Length >= 3 ? new Color(t.color[0], t.color[1], t.color[2]) : new Color(1f, 0.25f, 0.08f);
                l.shadows = t.shadows ? LightShadows.Soft : LightShadows.None;
                if (g.GetComponent<HDAdditionalLightData>() == null) g.AddComponent<HDAdditionalLightData>();
                l.lightUnit = LightUnit.Lumen; l.intensity = t.intensityLumens; lights++;
            }
        }
        if (cfg.fog != null && cfg.fog.center != null && cfg.fog.size != null)
        {
            var g = new GameObject("TrenchDarkFog"); g.transform.SetParent(root.transform);
            g.transform.position = new Vector3(cfg.fog.center[0], cfg.fog.center[1], cfg.fog.center[2]);
            var f = g.AddComponent<LocalVolumetricFog>(); var p = f.parameters;
            p.size = new Vector3(cfg.fog.size[0], cfg.fog.size[1], cfg.fog.size[2]);
            p.albedo = cfg.fog.albedo != null && cfg.fog.albedo.Length >= 3 ? new Color(cfg.fog.albedo[0], cfg.fog.albedo[1], cfg.fog.albedo[2]) : new Color(0.04f, 0.04f, 0.04f);
            p.meanFreePath = cfg.fog.meanFreePath; f.parameters = p; fogs++;
        }
        if (cfg.triplanar != null && !string.IsNullOrEmpty(cfg.triplanar.sourceMaterial)) tri = ApplyTriplanar(cfg.triplanar);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPTRENCH done blockers=" + cols + " lights=" + lights + " fog=" + fogs + " triplanarChunks=" + tri);
    }

    static int ApplyTriplanar(TriCfg c)
    {
        var src = AssetDatabase.LoadAssetAtPath<Material>(c.sourceMaterial);
        if (src == null) { Debug.LogWarning("HDRPTRENCH source material not found " + c.sourceMaterial + " (run HdrpWorldMats.Run first)"); return 0; }
        string np = string.IsNullOrEmpty(c.newMaterial) ? Path.Combine(Path.GetDirectoryName(c.sourceMaterial), Path.GetFileNameWithoutExtension(c.sourceMaterial) + "Triplanar.mat").Replace('\\', '/') : c.newMaterial;
        var m = AssetDatabase.LoadAssetAtPath<Material>(np);
        if (m == null) { m = new Material(src) { name = Path.GetFileNameWithoutExtension(np) }; AssetDatabase.CreateAsset(m, np); }
        else m.CopyPropertiesFromMaterial(src);
        m.SetFloat("_UVBase", 5f);                                   // HDRP Lit: 5 = Triplanar
        m.SetFloat("_TexWorldScale", 1f / Mathf.Max(0.01f, c.worldTileM));
        m.SetFloat("_UVMappingPlanar", 1f);
        HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m);
        int n = 0;
        var terrain = GameObject.Find("WorldTerrain");
        if (terrain == null) { Debug.LogWarning("HDRPTRENCH WorldTerrain not in scene"); return 0; }
        foreach (var r in terrain.GetComponentsInChildren<Renderer>())
        {
            if (!r.gameObject.name.EndsWith(c.chunkSuffix)) continue;
            var ms = new Material[r.sharedMaterials.Length]; for (int i = 0; i < ms.Length; i++) ms[i] = m;
            r.sharedMaterials = ms; n++;
        }
        return n;
    }
}
