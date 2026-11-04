using System;
using System.IO;
using UnityEngine;

// Runtime flicker of the doorway light (shadowed, 2700-3000 K). Data: Assets/Data/doorway.json "light.flicker" (same Noise schema as FurnaceFlicker, Perlin fBm).
// Base intensity is whatever the builder (HdrpDoorway) put on the light, so the number lives in doorway.json only.
public class DoorwayFlicker : MonoBehaviour
{
    [Serializable] class Root { public Row light; }
    [Serializable] class Row { public FurnaceFlicker.Noise flicker; }

    public Light target;
    float baseIntensity;
    FurnaceFlicker.Noise noise;
    bool ok;

    void Awake()
    {
        if (target == null) target = GetComponent<Light>();
        if (target == null) { enabled = false; return; }
        baseIntensity = target.intensity;
        foreach (var p in new[] { Application.streamingAssetsPath + "/doorway.json", Application.dataPath + "/Data/doorway.json" })
        {
            if (!File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(File.ReadAllText(p)); if (r != null && r.light != null) { noise = r.light.flicker; ok = true; break; } }
            catch (Exception e) { Debug.LogWarning("DoorwayFlicker: " + e.Message); }
        }
        if (!ok) { Debug.LogError("DoorwayFlicker: doorway.json (light.flicker) not found; light stays steady"); enabled = false; }
    }

    void Update()
    {
        if (!ok) return;
        target.intensity = baseIntensity * FurnaceFlicker.Eval(noise, Time.time);
    }
}
