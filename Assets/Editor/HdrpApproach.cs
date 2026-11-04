using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Gate approach variant (Dom: try the gate without the big staircase). ONE data file: Assets/Data/approach.json
//   mode "flat": the gate platform is LOWERED from oldPlatformTop (2.0) to platformTop (0.3); the deck stays y 0 (flat paving to the threshold, 0.15 step + 0.15 lip come from the layout plate).
//     - layout: constants.platformTop = platformTop, entries tagged flatTag enabled, entries tagged stairsTag disabled
//     - scene: moveNames (gate, door FX, rift/shards) move down by (oldPlatformTop - platformTop); PlatformBody top follows; deleteNames removed (stairs visuals, ramp, treads, the z~22 landing collider);
//       addColliders add the flat boxes. Idempotent through the marker object "ApproachState" (stores the applied platformTop): if Look()/HdrpGateway re-ran and put things back, set "reapply": true.
//   mode "stairs": layout entries swapped back (flatTag disabled, stairsTag enabled); the stair visuals need the scene backup (Tools/_backup_*) or the BuildSlice rebuild.
public static class HdrpApproach
{
    [System.Serializable] class Box { public string name; public float[] center, size; public string layerNote; }
    [System.Serializable] class ZDel { public string[] names; public float zMin = 16.9f, zMax = 22.6f; public string parentName = "Causeway"; }
    [System.Serializable] class Row
    {
        public string mode = "flat"; public float platformTop = 0.3f, oldPlatformTop = 2.0f; public bool reapply;
        public string flatTag = "variant_gateApproach_flat", stairsTag = "variant_gateApproach_stairs";
        public string[] deleteNames, moveNames, movePrefixes, disableNames; public ZDel zDelete; public Box[] addColliders;
        public string platformBodyName = "PlatformBody";
    }

    static void SetDisabled(string[] lines, string tag, bool disabled, ref int changed)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("\"" + tag + "\"")) continue;
            for (int j = i; j < System.Math.Min(lines.Length, i + 8); j++)
            {
                if (!lines[j].Contains("\"disabled\"")) continue;
                string nv = disabled ? "true" : "false"; string l = lines[j]; int k = l.IndexOf("\"disabled\""); int c = l.IndexOf(':', k);
                int e = c + 1; while (e < l.Length && l[e] == ' ') e++;
                int e2 = e; while (e2 < l.Length && char.IsLetter(l[e2])) e2++;
                if (l.Substring(e, e2 - e) != nv) { lines[j] = l.Substring(0, e) + nv + l.Substring(e2); changed++; }
                break;
            }
        }
    }

    static void SetPlatformTop(string lp, float v)
    {
        // constants.platformTop (string edit of the first "platformTop": number in the constants block)
        var lines = File.ReadAllLines(lp);
        for (int i = 0; i < lines.Length && i < 400; i++)
        {
            int k = lines[i].IndexOf("\"platformTop\"");
            if (k < 0) continue;
            int c = lines[i].IndexOf(':', k); if (c < 0) continue;
            int e = c + 1; while (e < lines[i].Length && lines[i][e] == ' ') e++;
            int e2 = e; while (e2 < lines[i].Length && (char.IsDigit(lines[i][e2]) || lines[i][e2] == '.' || lines[i][e2] == '-')) e2++;
            if (e2 > e) { lines[i] = lines[i].Substring(0, e) + v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + lines[i].Substring(e2); File.WriteAllLines(lp, lines); Debug.Log("HDRPAPPROACH constants.platformTop line " + (i + 1) + " = " + v); return; }
        }
        Debug.Log("HDRPAPPROACH WARNING constants.platformTop not found in the first 400 lines of " + lp);
    }

    public static void Run()
    {
        string path = "Assets/Data/approach.json";
        var F = File.Exists(path) ? JsonUtility.FromJson<Row>(File.ReadAllText(path)) : new Row();
        bool flat = F.mode == "flat";
        Debug.Log("HDRPAPPROACH mode=" + F.mode + " platformTop=" + F.platformTop);

        string lp = "Assets/Data/props_layout.json";
        if (File.Exists(lp))
        {
            var lines = File.ReadAllLines(lp); int changed = 0;
            SetDisabled(lines, F.flatTag, !flat, ref changed); SetDisabled(lines, F.stairsTag, flat, ref changed);
            if (changed > 0) File.WriteAllLines(lp, lines);
            Debug.Log("HDRPAPPROACH layout entries toggled=" + changed);
            SetPlatformTop(lp, flat ? F.platformTop : F.oldPlatformTop);
        }

        if (!flat) return;
        var sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        var marker = GameObject.Find("ApproachState");
        float applied = F.oldPlatformTop;
        if (marker != null && marker.transform.position.y < 100f) applied = marker.transform.position.y; // marker y stores the applied platformTop
        float delta = F.platformTop - F.oldPlatformTop;   // per-object idempotency: a moved object carries an empty child "ApproachLowered"; rebuilt objects (e.g. by Look/HdrpFurnace) lack it and get shifted again
        Debug.Log("HDRPAPPROACH appliedTop=" + applied + " delta=" + delta);

        int removed = 0, moved = 0;
        // delete first (stairs visuals, ramp, treads, the old z~22 landing collider)
        var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var t in all)
        {
            if (t == null) continue;
            bool del = false;
            if (F.deleteNames != null) foreach (var n in F.deleteNames) if (t.name == n) del = true;
            if (t.name == "LandingCollider" && t.position.z > 20f && t.position.z < 23f) del = true;
            if (!del && F.zDelete != null && F.zDelete.names != null && t.parent != null && t.parent.name == F.zDelete.parentName && System.Array.IndexOf(F.zDelete.names, t.name) >= 0 && t.position.z >= F.zDelete.zMin && t.position.z <= F.zDelete.zMax) del = true;   // raised Causeway pieces of the old stair zone (slabs, piers, cheek walls, posts)   // the upper landing (top 2.0), replaced by the flat boxes
            if (del) { Object.DestroyImmediate(t.gameObject); removed++; }
        }
        // disable (not delete): e.g. the Furnace ember emitters (Dom: orange sparks look cheap), one data flag to bring them back
        int disabled = 0; if (F.disableNames != null) foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t != null && System.Array.IndexOf(F.disableNames, t.name) >= 0 && t.gameObject.activeSelf) { t.gameObject.SetActive(false); disabled++; }
        Debug.Log("HDRPAPPROACH disabled=" + disabled);
        // move (only top-most matches: a moved parent carries its children)
        var movedSet = new HashSet<Transform>(); var prevLowered = new HashSet<Transform>();
        foreach (var t0 in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t0 != null && t0.Find("ApproachLowered") != null) prevLowered.Add(t0);
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null) continue;
            bool mv = F.moveNames != null && System.Array.IndexOf(F.moveNames, t.name) >= 0;
            if (!mv && F.movePrefixes != null) foreach (var p in F.movePrefixes) if (t.name.StartsWith(p)) mv = true;
            if (t.name == F.platformBodyName) mv = true;
            if (!mv) continue;
            bool underMoved = false; for (var q = t.parent; q != null; q = q.parent) if (movedSet.Contains(q)) underMoved = true;
            if (underMoved) continue;
            movedSet.Add(t);
            if (!F.reapply && t.Find("ApproachLowered") != null) continue;
            if (Mathf.Abs(delta) > 0.0001f) { t.position += new Vector3(0f, delta, 0f); foreach (var d in t.GetComponentsInChildren<Transform>(true)) if (d != t && prevLowered.Contains(d)) d.position -= new Vector3(0f, delta, 0f); }   // a child that was already lowered on its own (e.g. PlatformBody) must not move twice
            if (t.Find("ApproachLowered") == null) { var mk = new GameObject("ApproachLowered"); mk.transform.SetParent(t, false); }
            moved++;
        }
        // flat colliders (idempotent: removed first by name)
        int added = 0;
        if (F.addColliders != null) foreach (var b in F.addColliders)
        {
            var old = GameObject.Find(b.name); if (old != null) Object.DestroyImmediate(old);
            var go = new GameObject(b.name); var root = GameObject.Find("Arena"); if (root != null) go.transform.SetParent(root.transform, true);
            go.transform.position = new Vector3(b.center[0], b.center[1], b.center[2]); go.AddComponent<BoxCollider>().size = new Vector3(b.size[0], b.size[1], b.size[2]); added++;
        }
        if (marker == null) marker = new GameObject("ApproachState");
        marker.transform.position = new Vector3(0f, F.platformTop, -1000f);
        Debug.Log("HDRPAPPROACH removed=" + removed + " moved=" + moved + " collidersAdded=" + added);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
    }
}




