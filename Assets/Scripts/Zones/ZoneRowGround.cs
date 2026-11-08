using System;
using System.Collections.Generic;
using UnityEngine;

// ZONE5 v1 (laneZONE5). OFF by default: zone_ground.json enabled=false and no -zonerowground:1 = Boot returns, nothing allocated.
// Per-zone ground option + raycast row snap for zones that own a hand-made layout (zone_ground.json zones[]: root, prefix, floor, skipLibraries).
// Rows are placed with a placeholder y; when a zone's ring group is switched on by ZoneSpan, every row wrapper of that zone (a child of a ring group whose name starts with prefix) is moved to the real ground
// height by a downward raycast: ray from (x, y + rayUpM, z), rayDownM long, against every collider; the first hit that is not one of the zone's own props and not a character is the ground, and only a hit within
// maxDeltaM of the row's own y counts (a wrong collider such as a roof is ignored). No hit = the row's y stays. The wrapper pivot is the bounds bottom (PropsLayout ground "bottom"), so wrapper.y = hit.y - sinkM + the row's own y (a slot yOffset).
// 'floor' and 'skipLibraries' document the zone's ground choice (earth = dark cracked ground, no flagstone row in its layout; the span checker waives the flagstone rule for libraries listed in skipLibraries); they are logged.
// Poll cost: the wrapper list of each root is cached once per scene load; the poll touches only wrappers not done yet (activeInHierarchy check), every pollSeconds, and stops when all are done.
// Data: StreamingAssets/zone_ground.json (then Assets/Data). Args: -zonerowground:1|0. Positions change in memory only. UNPROVEN in Unity.
public class ZoneRowGround : MonoBehaviour
{
    [Serializable] public class ZoneCfg { public string id, root, prefix, floor; public string[] skipLibraries; }
    [Serializable] public class Cfg
    {
        public bool enabled = false; public float pollSeconds = 0.5f, minPollS = 0.1f, rayUpM = 3f, rayDownM = 12f, sinkM = 0.03f, maxDeltaM = 1.5f;
        public ZoneCfg[] zones;
    }
    const string ArgPrefix = "-zonerowground:", FileName = "zone_ground.json", DataDir = "Data";
    Cfg c; float next; readonly List<Transform>[] wrappers = new List<Transform>[8]; readonly bool[][] done = new bool[8][]; readonly int[] snapped = new int[8]; readonly int[] total = new int[8];
    bool built; int sceneStamp = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null;
            foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(ArgPrefix)) a1 = a.Substring(ArgPrefix.Length);
            Cfg cfg = null;
            foreach (var p in new[] { System.IO.Path.Combine(Application.streamingAssetsPath, FileName), System.IO.Path.Combine(Application.dataPath, DataDir, FileName) })
            {
                if (!System.IO.File.Exists(p)) continue;
                try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); } catch (Exception e) { Debug.LogWarning("[ZoneRowGround] " + p + ": " + e.Message); }
                break;
            }
            if (cfg == null || cfg.zones == null || cfg.zones.Length == 0) return;
            bool on = a1 != null ? a1 == "1" : cfg.enabled; if (!on) return;
            if (FindFirstObjectByType<ZoneRowGround>() != null) return;
            var go = new GameObject("ZoneRowGround"); DontDestroyOnLoad(go); go.AddComponent<ZoneRowGround>().c = cfg;
        }
        catch (Exception e) { Debug.LogWarning("ZONEROWGROUND boot failed: " + e.Message); }
    }

    void Start() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => { built = false; }; }

    void Build()
    {
        built = true; int n = Mathf.Min(c.zones.Length, wrappers.Length);
        for (int i = 0; i < n; i++)
        {
            var z = c.zones[i]; wrappers[i] = new List<Transform>(); total[i] = 0; snapped[i] = 0;
            var root = string.IsNullOrEmpty(z.root) ? null : GameObject.Find(z.root);
            if (root != null)
                foreach (Transform ring in root.transform)       // ring groups (inactive until ZoneSpan wakes them)
                    foreach (Transform w in ring) if (w.name.StartsWith(z.prefix, StringComparison.Ordinal)) wrappers[i].Add(w);
            total[i] = wrappers[i].Count; done[i] = new bool[total[i]];
            Debug.Log("ZONEROWGROUND " + z.id + " root " + z.root + ": " + total[i] + " rows, floor " + z.floor + ", skipLibraries " + (z.skipLibraries == null ? "none" : string.Join(",", z.skipLibraries)));
        }
    }

    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(c.minPollS, c.pollSeconds);
        try
        {
            if (!built) Build();
            int n = Mathf.Min(c.zones.Length, wrappers.Length);
            for (int i = 0; i < n; i++)
            {
                var L = wrappers[i]; if (L == null || snapped[i] >= total[i]) continue; int before = snapped[i];
                for (int k = 0; k < L.Count; k++)
                {
                    var t = L[k]; if (done[i][k]) continue;
                    if (t == null) { done[i][k] = true; snapped[i]++; continue; }
                    if (!t.gameObject.activeInHierarchy) continue;      // ring still off: its colliders (GroundCollider is in ring 0) are off too
                    done[i][k] = true; snapped[i]++; Snap(t, c.zones[i].prefix);
                }
                if (snapped[i] != before && snapped[i] == total[i]) Debug.Log("ZONEROWGROUND " + c.zones[i].id + " all " + total[i] + " rows processed");
            }
        }
        catch (Exception e) { Debug.LogWarning("ZONEROWGROUND snap failed: " + e.Message); }
    }

    void Snap(Transform t, string prefix)
    {
        var p = t.position; var o = new Vector3(p.x, p.y + c.rayUpM, p.z);
        var hits = Physics.RaycastAll(o, Vector3.down, c.rayUpM + c.rayDownM, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            var ht = h.collider.transform;
            if (ht == t || ht.IsChildOf(t) || ht.name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (Mathf.Abs(h.point.y - p.y) > c.maxDeltaM) continue;
            t.position = new Vector3(p.x, h.point.y - c.sinkM + p.y, p.z);   /* + p.y keeps a slot yOffset (the row y is only that offset above the placeholder ground 0) */ Debug.Log("ZONEROWGROUND " + t.name + " y " + p.y.ToString("F2") + " -> " + t.position.y.ToString("F2") + " (" + ht.name + ")"); return;
        }
    }
}
