using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// APV v1 (laneAPV): cheap stand-in for SSGI's soft floor bounce when SSGI is off. A few NON-shadow point lights from data (Assets/Data/arena_bounce.json),
// following roster fighters or fixed. Self-installs after scene load, does nothing unless "enabled": true. No character names: Roster ids only.
public class ArenaBounceFill : MonoBehaviour
{
    [Serializable] class Row { public string name = "", follow = "fixed"; public float[] offset, pos, color; public float candela = 100f, range = 4f, volumetricDimmer = 0f; }
    [Serializable] class Root { public bool enabled; public float smooth = 10f; public Row[] rows; }

    class Item { public Row r; public Light l; public Transform t; public string id; }
    Item[] items; float smooth, nextFind;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        Root root = null;
        foreach (var p in new[] { Application.streamingAssetsPath + "/arena_bounce.json", Application.dataPath + "/Data/arena_bounce.json" })
        {
            if (!File.Exists(p)) continue;
            try { root = JsonUtility.FromJson<Root>(File.ReadAllText(p)); break; } catch (Exception e) { Debug.LogWarning("ArenaBounceFill: " + e.Message); }
        }
        if (root == null || !root.enabled || root.rows == null || root.rows.Length == 0) return;
        if (FindFirstObjectByType<ArenaBounceFill>() != null) return;
        var go = new GameObject("ArenaBounceFill"); var c = go.AddComponent<ArenaBounceFill>(); c.Build(root);
    }

    static Vector3 V3(float[] a, Vector3 d) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : d; }

    void Build(Root root)
    {
        smooth = root.smooth; items = new Item[root.rows.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var r = root.rows[i]; var g = new GameObject(r.name); g.transform.SetParent(transform);
            var l = g.AddComponent<Light>(); var hd = g.AddComponent<HDAdditionalLightData>();
            l.type = LightType.Point; l.lightUnit = UnityEngine.Rendering.LightUnit.Candela /* INTFPS v1: CS0103 fix, LightUnit is in UnityEngine.Rendering */; l.intensity = r.candela; l.range = r.range;
            if (r.color != null && r.color.Length >= 3) l.color = new Color(r.color[0], r.color[1], r.color[2]);
            hd.EnableShadows(false); hd.volumetricDimmer = r.volumetricDimmer;
            if (r.follow == "fixed") g.transform.position = V3(r.pos, Vector3.zero);
            items[i] = new Item { r = r, l = l, id = r.follow == "player" ? Roster.PlayerId : r.follow == "enemy" ? Roster.EnemyId : r.follow };
        }
    }

    void LateUpdate()
    {
        if (items == null) return;
        bool find = Time.unscaledTime >= nextFind; if (find) nextFind = Time.unscaledTime + 1f;
        float k = 1f - Mathf.Exp(-smooth * Time.unscaledDeltaTime);
        foreach (var it in items)
        {
            if (it.r.follow == "fixed") continue;
            if (it.t == null) { if (find) { var o = GameObject.Find(Roster.Get(it.id).objectName); if (o != null) it.t = o.transform; } if (it.t == null) continue; }
            var want = it.t.position + V3(it.r.offset, Vector3.zero);
            it.l.transform.position = Vector3.Lerp(it.l.transform.position, want, k);
        }
    }
}
