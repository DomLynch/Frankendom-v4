using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Data-driven Hades prop placement. Replaces the hard-coded rows of BuildSlice.AddProps (BuildSliceProps.cs) and
// BuildSlice.AddBoulders (BuildSliceSmoke.cs). All numbers live in Assets/Data/props_layout.json.
// Pipeline-neutral: AssetDatabase + LODGroup + Renderer only (no HDRP/URP API), so it compiles in both projects.
// Place in an Editor folder (Assets/Editor/PropsLayout.cs).
public static class PropsLayout
{
    public const string DataPath = "Data/props_layout.json";   // relative to Application.dataPath (Assets/)

    [System.Serializable] public class Constants { public float waterY; public float platformTop; }
    [System.Serializable] public class Library { public string name; public string pattern; }
    [System.Serializable] public class LodProfile { public string name; public float[] screenHeights; }
    [System.Serializable] public class Prop
    {
        public string id, group, library, asset, lodProfile, ground, yBase, note;
        public float[] pos, rotEuler;
        public float scale = 1f, sinkFraction, baseColorMul;
        public bool shadowsOff, disabled;
        public string[] tags;
    }
    [System.Serializable] public class Scatter { public string id; public bool disabled; }
    [System.Serializable] public class Layout
    {
        public int version;
        public string coordinateSpace;
        public Constants constants;
        public Library[] libraries;
        public LodProfile[] lodProfiles;
        public string[] groups;
        public Prop[] props;
        public Scatter[] scatter;
    }

    public static int LastPlaced, LastEnabled, LastMissing;

    public static Layout Load(string path = null)
    {
        path = path ?? Path.Combine(Application.dataPath, DataPath);
        if (!File.Exists(path)) { Debug.LogError("[PropsLayout] missing " + path); return null; }
        return JsonUtility.FromJson<Layout>(File.ReadAllText(path));
    }

    // Builds one child per group under 'arena' and places every enabled prop. Returns the number placed.
    public static int Build(GameObject arena, string path = null)
    {
        var L = Load(path);
        LastPlaced = LastEnabled = LastMissing = 0;
        if (L == null || L.props == null) return 0;
        var groupRoots = new Dictionary<string, Transform>();
        foreach (var g in L.groups ?? new string[0]) groupRoots[g] = NewGroup(arena, g);
        foreach (var p in L.props)
        {
            if (p.disabled) continue;
            LastEnabled++;
            string gname = string.IsNullOrEmpty(p.group) ? "Props" : p.group;
            if (!groupRoots.TryGetValue(gname, out var gr)) { gr = NewGroup(arena, gname); groupRoots[gname] = gr; }
            if (PlaceOne(L, p, gr) != null) LastPlaced++; else LastMissing++;
        }
        int scat = 0; if (L.scatter != null) foreach (var s in L.scatter) if (!s.disabled) scat++;
        if (scat > 0) Debug.LogWarning("[PropsLayout] " + scat + " enabled scatter rule(s) ignored (scatter placement is not implemented in v1)");
        Debug.Log("[PropsLayout] placed " + LastPlaced + "/" + LastEnabled + " enabled props (" + LastMissing + " missing assets) from " + DataPath);
        return LastPlaced;
    }

    static Transform NewGroup(GameObject arena, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(arena.transform);
        return go.transform;
    }

    static string AssetPath(Layout L, Prop p, int lod)
    {
        foreach (var lib in L.libraries) if (lib.name == p.library) return lib.pattern.Replace("{asset}", p.asset).Replace("{lod}", lod.ToString());
        return null;
    }

    static float[] Heights(Layout L, string profile)
    {
        foreach (var lp in L.lodProfiles) if (lp.name == profile && lp.screenHeights != null && lp.screenHeights.Length > 0) return lp.screenHeights;
        Debug.LogWarning("[PropsLayout] unknown lodProfile '" + profile + "', using 0.25/0.08/0.02");
        return new[] { 0.25f, 0.08f, 0.02f };
    }

    static float BaseY(Layout L, Prop p)
    {
        float b = p.yBase == "waterY" ? L.constants.waterY : p.yBase == "platformTop" ? L.constants.platformTop : 0f;
        return b + (p.pos != null && p.pos.Length > 1 ? p.pos[1] : 0f);
    }

    static Bounds Encapsulated(Renderer[] rs)
    {
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
    }

    // Same construction as the old PlaceLod: wrapper object, one child per LOD GLB, LODGroup over their renderers.
    public static GameObject PlaceOne(Layout L, Prop p, Transform parent)
    {
        var root = new GameObject(p.id); root.transform.SetParent(parent);
        var heights = Heights(L, p.lodProfile);
        var lods = new List<LOD>(); Renderer[] lod0 = null;
        for (int i = 0; i < 3; i++)
        {
            string ap = AssetPath(L, p, i); if (ap == null) break;
            var pf = AssetDatabase.LoadAssetAtPath<GameObject>(ap);
            if (pf == null) continue;
            var g = (GameObject)PrefabUtility.InstantiatePrefab(pf); g.name = "LOD" + i; g.transform.SetParent(root.transform, false);
            var rs = g.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) continue;
            if (i == 0) lod0 = rs;
            lods.Add(new LOD(heights[Mathf.Min(i, heights.Length - 1)], rs));
        }
        if (lod0 == null) { Debug.LogWarning("[PropsLayout] missing props " + p.id + " (" + AssetPath(L, p, 0) + ")"); Object.DestroyImmediate(root); return null; }
        var lg = root.AddComponent<LODGroup>(); lg.SetLODs(lods.ToArray()); lg.fadeMode = LODFadeMode.None; lg.RecalculateBounds();
        var all = root.GetComponentsInChildren<Renderer>();
        if (p.shadowsOff) foreach (var r in all) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        if (p.baseColorMul > 0f) TintBaseColor(all, p.baseColorMul);

        Vector3 pos = new Vector3(p.pos[0], BaseY(L, p), p.pos[2]);
        Quaternion rot = Quaternion.Euler(p.rotEuler != null && p.rotEuler.Length == 3 ? new Vector3(p.rotEuler[0], p.rotEuler[1], p.rotEuler[2]) : Vector3.zero);
        float s = p.scale > 0f ? p.scale : 1f;
        if (p.ground == "boundsCentreSink")
        {
            // old AddBoulders: LOD0 bounds centre on (x, z) at scale 1, scale about the pivot, then sink the bottom below the waterline
            root.transform.rotation = rot; root.transform.localScale = Vector3.one; root.transform.position = Vector3.zero;
            var c = Encapsulated(lod0).center;
            root.transform.position = new Vector3(pos.x - c.x, 0f, pos.z - c.z);
            root.transform.localScale = Vector3.one * s;
            var bb = Encapsulated(lod0);
            root.transform.position += new Vector3(0f, (pos.y - bb.size.y * p.sinkFraction) - bb.min.y, 0f);
        }
        else
        {
            // old PlaceLod: LOD0 bounds bottom sits on pos.y (props have a bottom-centre origin); the bounds are read at identity
            var b0 = Encapsulated(lod0);
            root.transform.localScale = Vector3.one * s; root.transform.rotation = rot;
            root.transform.position = new Vector3(pos.x, pos.y - b0.min.y * s, pos.z);
        }
        return root;
    }

    static void TintBaseColor(Renderer[] rs, float mul)
    {
        foreach (var r in rs)
        {
            var ms = r.sharedMaterials;
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i] == null) continue;
                var mc = new Material(ms[i]);
                foreach (var pn in new[] { "baseColorFactor", "_BaseColor", "_Color" })
                    if (mc.HasProperty(pn)) { var c = mc.GetColor(pn); mc.SetColor(pn, new Color(c.r * mul, c.g * mul, c.b * mul, c.a)); }
                ms[i] = mc;
            }
            r.sharedMaterials = ms;
        }
    }

    // Rebuild the groups of the open scene from the JSON (finds the object named "Arena").
    [MenuItem("Hades/Props/Rebuild From props_layout.json")]
    public static void RebuildInScene()
    {
        var arena = GameObject.Find("Arena");
        if (arena == null) { Debug.LogError("[PropsLayout] no 'Arena' object in the open scene"); return; }
        var L = Load(); if (L == null) return;
        foreach (var g in L.groups ?? new string[0])
        {
            var old = arena.transform.Find(g);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        }
        Build(arena);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(arena.scene);
    }

    // batch / CLI entry: open the slice, rebuild Props + Boulders from the JSON, save. Run HdrpGltfMats.Refresh() afterwards (new GLB instances need HDRP materials).
    public static void RebuildAndSave()
    {
        var sc = UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        RebuildInScene();
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(sc);
    }
}
