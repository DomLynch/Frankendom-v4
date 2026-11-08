using System;
using System.IO;
using UnityEngine;

// Palette guard (Dom: crimson/ember night glow, no cheap blue or purple): lights outside the whitelist whose colour is blue/purple dominant are shifted to the
// crimson colour from gateway.json 'palette' (intensity kept) and logged once. Runs after scene load and re-scans every few seconds for runtime-created lights.
public class PaletteGuard : MonoBehaviour
{
    [Serializable] class Row { public bool enabled; public string[] whitelist; public float[] crimson; }
    [Serializable] class Root { public Row palette; }
    static Row row; float next; static readonly System.Collections.Generic.HashSet<Light> done = new System.Collections.Generic.HashSet<Light>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string[] paths = { Application.streamingAssetsPath + "/gateway.json", Application.dataPath + "/Data/gateway.json" };
        foreach (var p in paths) { if (!File.Exists(p)) continue; try { row = JsonUtility.FromJson<Root>(DataPath.ReadFile(p)).palette; } catch { } break; }
        if (row == null || !row.enabled || row.crimson == null || row.crimson.Length < 3) return;
        var go = new GameObject("PaletteGuard"); DontDestroyOnLoad(go); go.AddComponent<PaletteGuard>();
    }

    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + 3f;
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (done.Contains(l)) continue; done.Add(l);
            bool white = false; if (row.whitelist != null) foreach (var w in row.whitelist) if (l.name == w) white = true;
            if (white) continue;
            var c = l.color; if (c.b > c.r && c.b >= c.g * 0.9f)
            {
                Debug.Log("[PaletteGuard] light '" + l.name + "' is blue/purple dominant, shifted to crimson");
                l.color = new Color(row.crimson[0], row.crimson[1], row.crimson[2]);
            }
        }
    }
}

