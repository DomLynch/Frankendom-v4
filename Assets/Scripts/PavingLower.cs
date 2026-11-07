using System;
using System.Collections;
using UnityEngine;

// PAVINGLOWER v1 (env-dev). SHIPS OFF: paving_lower.json enabled=false and no -pavinglower:1 => Boot returns after one small file read, nothing is allocated, nothing moves.
// Why: the ground_paving plate (gate_approach_flat, x +-4, z 0.4..24) has plate_top at y -0.069..+0.005, the terrain lane / plug / cap are the plane y 0 over it. 9-23% of the fight-zone plate area is within 10 mm of that plane
// (0.5-1.3% pokes above it), so the two floors contest the same pixels and crawl under TAA at the fighters' feet (laneFLICKERNEXT out\paving_heights.txt).
// ON: every scene object whose name contains nameContains (default "ground_paving"; a root only, not its LOD children) is moved DOWN by dropM (default 0.02 m): the plate then lies >= 15 mm under the plane everywhere,
// the terrain stays the only visible floor at the feet (y 0 unchanged, the pokes of at most 5 mm are the only visible difference). Zone clones (z2_/hub rows) match the same name. No lights, no per-frame work, no new draw calls.
// Limits: a statically batched plate would not follow the transform (the log line then still says moved); only transform.position.y changes.
// Args: -pavinglower:1|0 overrides enabled, -pavingdrop:<metres> overrides dropM. Data: paving_lower.json (Assets/Data and StreamingAssets, byte-identical).
public class PavingLower : MonoBehaviour
{
    [Serializable] public class Cfg { public bool enabled = false; public float dropM = 0.02f; public string nameContains = "ground_paving"; public int retryFrames = 600; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null, a2 = null;
            foreach (var a in Environment.GetCommandLineArgs()) { if (a.StartsWith("-pavinglower:")) a1 = a.Substring(13); else if (a.StartsWith("-pavingdrop:")) a2 = a.Substring(12); }
            string txt = DataPath.ReadText("paving_lower.json"); if (txt == null) return;
            var c = JsonUtility.FromJson<Cfg>(txt); if (c == null) return;
            bool on = a1 != null ? a1 == "1" : c.enabled; if (!on) return;
            float d; if (a2 != null && float.TryParse(a2, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) c.dropM = d;
            var go = new GameObject("PavingLowerHost"); var k = go.AddComponent<PavingLower>(); k.StartCoroutine(k.Run(c));
        }
        catch (Exception e) { Debug.LogWarning("PAVINGLOWER boot failed: " + e.Message); }
    }

    IEnumerator Run(Cfg c)
    {
        for (int f = 0; f < Mathf.Max(1, c.retryFrames); f++)
        {
            int n = 0; string names = "";
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.name.IndexOf(c.nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.parent != null && t.parent.name.IndexOf(c.nameContains, StringComparison.OrdinalIgnoreCase) >= 0) continue;   // roots only (LOD children follow)
                if (t.GetComponentInChildren<Renderer>(true) == null) continue;
                var p = t.position; t.position = new Vector3(p.x, p.y - c.dropM, p.z); n++; names += (names.Length > 0 ? "," : "") + t.name;
            }
            if (n > 0) { Debug.Log("PAVINGLOWER on: " + n + " object(s) moved down " + c.dropM.ToString("0.000") + " m (" + names + ")"); Destroy(gameObject); yield break; }
            yield return null;
        }
        Debug.LogWarning("PAVINGLOWER: no object named *" + c.nameContains + "* found, nothing moved"); Destroy(gameObject);
    }
}
