using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// HDRP Water System on the Styx: replaces the old plane with a WaterSurface (sea/lake) at the water level; numbers from Assets/Data/hdrp_water.json
public static class HdrpWater
{
    [System.Serializable] public class W { public float level, windSpeed, choppiness, repetition, timeMul, foam, smoothness, absorption; public float[] scatter; public string type; }
    public static void Run()
    {
        var scene = EditorSceneManager.GetActiveScene(); if (scene.path != "Assets/Scenes/HadesSlice.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        var P = JsonUtility.FromJson<W>(System.IO.File.ReadAllText("Assets/Data/hdrp_water.json"));
        var old = GameObject.Find("Styx_Water"); if (old != null) old.SetActive(false);
        var g = GameObject.Find("Styx_Water_HDRP") ?? new GameObject("Styx_Water_HDRP"); g.transform.position = new Vector3(0, P.level, 0);
        var ws = g.GetComponent<WaterSurface>() ?? g.AddComponent<WaterSurface>();
        ws.surfaceType = (WaterSurfaceType)System.Enum.Parse(typeof(WaterSurfaceType), P.type);
        ws.timeMultiplier = P.timeMul; ws.repetitionSize = P.repetition;
        ws.largeWindSpeed = P.windSpeed; ws.largeChaos = P.choppiness; ws.ripplesWindSpeed = P.windSpeed * 0.4f;
        ws.scatteringColor = new Color(P.scatter[0], P.scatter[1], P.scatter[2]); ws.absorptionDistance = P.absorption;
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPWATER done");
    }
}