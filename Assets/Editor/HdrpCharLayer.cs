using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Character light layer (Strategy/Dom: head and both fighters must not be tinted orange by the crimson door lights; do not touch albedo).
// Data: Assets/Data/hdrp_char_layer.json. Fighters (and the MetaHuman head) drop the Default light layer and take the character bit; every non-door light gets the
// character bit added, lights whose name contains one of doorLightContains lose it (and lose the Default bit's effect on characters because characters no longer have it).
public static class HdrpCharLayer
{
    [System.Serializable] class Row { public bool enabled = true; public int charBit = 7; public string[] characters; public string[] doorLightContains; public string[] keepLights, restore; }

    public static void Run()
    {
        string path = "Assets/Data/hdrp_char_layer.json";
        if (!File.Exists(path)) { Debug.Log("HDRPCHARLAYER no data file"); return; }
        var F = JsonUtility.FromJson<Row>(File.ReadAllText(path));
        if (!F.enabled) { Debug.Log("HDRPCHARLAYER disabled"); return; }
        var sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        uint cb = 1u << F.charBit; int rends = 0, door = 0, other = 0;
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool inc = false; foreach (var c in F.characters) if (r.transform.root.name == c || r.transform.root.name.StartsWith(c)) inc = true;
            if (!inc) { var p = r.transform; while (p != null && !inc) { foreach (var c in F.characters) if (p.name == c) inc = true; p = p.parent; } }
            if (!inc)
            {
                bool back = false; if (F.restore != null) foreach (var c in F.restore) if (r.transform.root.name.StartsWith(c)) back = true;
                if (back) r.renderingLayerMask = (r.renderingLayerMask | 1u) & ~cb;   // back on the Default light layer: keeps the door lights (the enemy's olive look)
                continue;
            }
            r.renderingLayerMask = (r.renderingLayerMask & ~1u) | cb; rends++;
        }
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var hd = l.GetComponent<HDAdditionalLightData>(); if (hd == null) continue;
            bool skip = false; if (F.keepLights != null) foreach (var k in F.keepLights) if (l.name == k) skip = true;
            if (skip) continue;
            bool isDoor = false; foreach (var d in F.doorLightContains) if (l.name.Contains(d)) isDoor = true;
            uint m = (uint)hd.lightlayersMask;
            if (isDoor) { m &= ~cb; door++; } else { m |= cb; other++; }
            hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)m;
        }
        Debug.Log("HDRPCHARLAYER renderers=" + rends + " doorLights=" + door + " otherLights=" + other);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
    }
}
