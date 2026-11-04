using UnityEngine;

// Keeps both fighters inside the arena lane (Strategy audit: fighters drifted to / past the gate threshold and the camera ended up inside the gate geometry).
// Data: Assets/Data/arena_limits.json (StreamingAssets in the player) { enabled, xMin, xMax, zMin, zMax }. Runs after DuelFighter (LateUpdate, order 600) and moves the
// CharacterController back inside the rectangle. Auto-attaches to every DuelFighter at scene load. No effect when the file is missing or enabled=false.
[DefaultExecutionOrder(600)]
public class ArenaLimits : MonoBehaviour
{
    [System.Serializable] class Row { public bool enabled = true; public float xMin = -3.8f, xMax = 3.8f, zMin = 8f, zMax = 22f; }
    static Row row; static bool loaded;
    CharacterController cc;

    static Row Load()
    {
        if (loaded) return row; loaded = true;
        foreach (var p in new[] { Application.streamingAssetsPath + "/arena_limits.json", Application.dataPath + "/Data/arena_limits.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { row = JsonUtility.FromJson<Row>(System.IO.File.ReadAllText(p)); } catch { }
            break;
        }
        return row;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var r = Load(); if (r == null || !r.enabled) return;
        foreach (var f in Object.FindObjectsByType<CharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if ((f.name == "Dracula" || f.name == "Pitborn") && f.GetComponent<ArenaLimits>() == null) { f.gameObject.AddComponent<ArenaLimits>(); Debug.Log("[ArenaLimits] active on " + f.name + " x " + r.xMin + ".." + r.xMax + " z " + r.zMin + ".." + r.zMax); }
    }

    void Awake() { cc = GetComponent<CharacterController>(); }

    void LateUpdate()
    {
        var r = row; if (r == null || !r.enabled || cc == null || !cc.enabled) return;
        var p = transform.position;
        float x = Mathf.Clamp(p.x, r.xMin, r.xMax), z = Mathf.Clamp(p.z, r.zMin, r.zMax);
        if (x != p.x || z != p.z) cc.Move(new Vector3(x - p.x, 0f, z - p.z));
    }
}
