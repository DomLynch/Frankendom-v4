using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Arena variant "ground" (Dom: ONE ground level at y 0 from the start to the gate; no bridge deck, no trench, no ramp, no platform step).
// Data: Assets/Data/ground.json. Run AFTER Look()/HdrpDoorway and BEFORE PropsLayout.RebuildAndSave (it swaps props_layout.json for the ground layout).
//   - layout: Assets/Data/props_layout_ground.json is copied over props_layout.json (the old file is kept once as Tools/props_layout_pre_ground.json)
//   - scene: the old deck/landing/ramp/trench/stair objects are deleted (deleteRoots + deleteNames + deletePrefixes), the gate/door FX/furnace/gateway/platform groups are lowered to platformTop
//     (per-object idempotent: a lowered object carries a child marker "ApproachTop_<y>", the legacy marker "ApproachLowered" means 0.3, unmarked = oldPlatformTop 2.0),
//     ONE walk collider (the ground plane, top y 0) + the optional threshold lip collider are added, the fighters are put on y = spawnY.
public static class HdrpGround
{
    [System.Serializable] class Box { public string name; public float[] center, size; }
    [System.Serializable] class Row
    {
        public bool enabled = true; public float platformTop = 0f, oldPlatformTop = 2f, legacyLoweredTop = 0.3f, spawnY = 0.02f, platformVisualExtraDrop = 0.03f;
        public string layoutSource = "Assets/Data/props_layout_ground.json";
        public string[] deleteRoots, deleteNames, deletePrefixes, moveNames, movePrefixes, disableNames;
        public Box[] addColliders;
    }

    static float AppliedTop(Transform t, Row F)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            string n = t.GetChild(i).name;
            if (n == "ApproachLowered") return F.legacyLoweredTop;
            if (n.StartsWith("ApproachTop_")) { float v; if (float.TryParse(n.Substring(12), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v; }
        }
        return F.oldPlatformTop;
    }

    // entries tagged for this variant must be enabled (another tool set disabled:true on the paving entry: v53 had no paving)
    static void ForceEnableTag(string lp, string tag)
    {
        var lines = File.ReadAllLines(lp); int changed = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("\"" + tag + "\"")) continue;
            for (int j = i; j < System.Math.Min(lines.Length, i + 12); j++)
            {
                if (!lines[j].Contains("\"disabled\"")) continue;
                if (lines[j].Contains("true")) { lines[j] = lines[j].Replace("true", "false"); changed++; }
                break;
            }
        }
        if (changed > 0) File.WriteAllLines(lp, lines);
        Debug.Log("HDRPGROUND force-enabled " + changed + " entries tagged " + tag);
    }

    public static void Run()
    {
        string path = "Assets/Data/ground.json";
        if (!File.Exists(path)) { Debug.Log("HDRPGROUND no data file"); return; }
        var F = JsonUtility.FromJson<Row>(File.ReadAllText(path));
        if (!F.enabled) { Debug.Log("HDRPGROUND disabled"); return; }

        // layout swap
        string lp = "Assets/Data/props_layout.json";
        if (File.Exists(F.layoutSource))
        {
            if (!File.Exists("Tools/props_layout_pre_ground.json") && File.Exists(lp)) File.Copy(lp, "Tools/props_layout_pre_ground.json", false);
            File.Copy(F.layoutSource, lp, true);
            if (File.Exists("Assets/StreamingAssets/props_layout.json")) File.Copy(F.layoutSource, "Assets/StreamingAssets/props_layout.json", true);
            ForceEnableTag(lp, "variant_gateApproach_ground");
            Debug.Log("HDRPGROUND layout = " + F.layoutSource);
        }
        else Debug.Log("HDRPGROUND WARNING layout source missing: " + F.layoutSource);

        var sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        int removed = 0, moved = 0, disabled = 0, added = 0;

        // delete (roots by name, exact names, prefixes); collect first, destroy after
        var kill = new List<GameObject>();
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null) continue; bool del = false;
            if (F.deleteRoots != null && System.Array.IndexOf(F.deleteRoots, t.name) >= 0 && (t.parent == null || t.parent.name == "Arena")) del = true;
            if (!del && F.deleteNames != null && System.Array.IndexOf(F.deleteNames, t.name) >= 0) del = true;
            if (!del && F.deletePrefixes != null) foreach (var p in F.deletePrefixes) if (t.name.StartsWith(p)) del = true;
            if (del) kill.Add(t.gameObject);
        }
        foreach (var g in kill) if (g != null) { Object.DestroyImmediate(g); removed++; }

        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t != null && F.disableNames != null && System.Array.IndexOf(F.disableNames, t.name) >= 0 && t.gameObject.activeSelf) { t.gameObject.SetActive(false); disabled++; }

        // lower the gate group (top-most matches only: a moved parent carries its children)
        var movedSet = new HashSet<Transform>();
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null) continue;
            bool mv = F.moveNames != null && System.Array.IndexOf(F.moveNames, t.name) >= 0;
            if (!mv && F.movePrefixes != null) foreach (var p in F.movePrefixes) if (t.name.StartsWith(p)) mv = true;
            if (!mv) continue;
            bool under = false; for (var q = t.parent; q != null; q = q.parent) if (movedSet.Contains(q)) under = true;
            if (under) continue; movedSet.Add(t);
            float applied = AppliedTop(t, F); float target = F.platformTop - (t.name == "GatePlatform" ? F.platformVisualExtraDrop : 0f);
            float delta = target - (applied - (t.name == "GatePlatform" ? 0f : 0f));
            if (Mathf.Abs(delta) > 0.0001f) t.position += new Vector3(0f, delta, 0f);
            for (int i = t.childCount - 1; i >= 0; i--) { string n = t.GetChild(i).name; if (n == "ApproachLowered" || n.StartsWith("ApproachTop_")) Object.DestroyImmediate(t.GetChild(i).gameObject); }
            var mk = new GameObject("ApproachTop_" + F.platformTop.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); mk.transform.SetParent(t, false); moved++;
        }

        // colliders
        var root = GameObject.Find("Arena");
        if (F.addColliders != null) foreach (var b in F.addColliders)
        {
            var old = GameObject.Find(b.name); if (old != null) Object.DestroyImmediate(old);
            var go = new GameObject(b.name); if (root != null) go.transform.SetParent(root.transform, true);
            go.transform.position = new Vector3(b.center[0], b.center[1], b.center[2]); go.AddComponent<BoxCollider>().size = new Vector3(b.size[0], b.size[1], b.size[2]); added++;
        }
        // fighters on the ground
        foreach (var n in RosterEditor.FighterObjects())
        {
            var g = GameObject.Find(n); if (g == null) continue; var cc = g.GetComponent<CharacterController>(); bool was = cc != null && cc.enabled; if (cc != null) cc.enabled = false;
            var p = g.transform.position; g.transform.position = new Vector3(p.x, F.spawnY, p.z); if (cc != null) cc.enabled = was;
        }
        EnsureTerrainCentrePlug();
        Debug.Log("HDRPGROUND removed=" + removed + " moved=" + moved + " disabled=" + disabled + " collidersAdded=" + added + " platformTop=" + F.platformTop);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
    }

    static void EnsureTerrainCentrePlug()
    {
        var existing = GameObject.Find("TerrainCentrePlug");
        if (existing != null) Object.DestroyImmediate(existing);
        var plug = new GameObject("TerrainCentrePlug");
        var root = GameObject.Find("WorldTerrain");
        if (root != null) plug.transform.SetParent(root.transform, false);
        plug.transform.position = new Vector3(0f, 0f, 15f);

        var mf = plug.AddComponent<MeshFilter>();
        var mr = plug.AddComponent<MeshRenderer>();
        var mesh = new Mesh { name = "TerrainCentrePlugMesh" };
        int segs = 16;
        var verts = new Vector3[segs + 1];
        var uvs = new Vector2[segs + 1];
        var tris = new int[segs * 3];
        verts[0] = Vector3.zero;
        uvs[0] = new Vector2(0f / 64f, 15f / 64f);
        float r = 1.6f;
        for (int i = 0; i < segs; i++)
        {
            float ang = i * Mathf.PI * 2f / segs;
            float vx = Mathf.Cos(ang) * r;
            float vz = Mathf.Sin(ang) * r;
            verts[i + 1] = new Vector3(vx, 0f, vz);
            uvs[i + 1] = new Vector2(vx / 64f, (15f + vz) / 64f);
            tris[i * 3] = 0;
            tris[i * 3 + 1] = (i + 1 == segs) ? 1 : i + 2;
            tris[i * 3 + 2] = i + 1;
        }
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;
        var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/HDRP/World/M_WorldTerrain.mat");
        if (mat != null) mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }
}


