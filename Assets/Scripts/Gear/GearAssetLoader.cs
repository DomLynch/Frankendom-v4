using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

// Finds and instantiates the visual of a gear item. NEVER throws and never blocks: a missing asset ends in done(null, null, reason) and the caller equips the item without a visual.
// Order: 1. Resources prefab (item.resource)   2. Editor: the imported asset at item.glb (AssetDatabase)   3. player / editor fallback: the .glb FILE through glTFast,
// looked up in StreamingAssets/Gear/<file name>, StreamingAssets/<item.glb>, <project>/<item.glb>, or item.glb as an absolute path.
// (A glb under Assets/ is NOT in a player build unless a scene or Resources references it, so the build step copies gear glbs to StreamingAssets/Gear/. See GEAR_DESIGN.md.)
public static class GearAssetLoader
{
    // the item's primary visual (resource prefab first, then its glb)
    public static IEnumerator Load(GearTable.Item it, Transform parent, Action<GameObject, IDisposable, string> done)
    {
        return Load(it, it.glb, true, parent, done);
    }

    // GEAR2: an explicit glb path (the mirror glb of a pair: useResource false, so a Resources prefab is only ever the primary piece)
    public static IEnumerator Load(GearTable.Item it, string glb, bool useResource, Transform parent, Action<GameObject, IDisposable, string> done)
    {
        if (glb == null) glb = "";
        if (useResource && it.resource.Length > 0)
        {
            GameObject p = null; try { p = Resources.Load<GameObject>(it.resource); } catch (Exception) { }
            if (p != null) { done(UnityEngine.Object.Instantiate(p, parent), null, "resources:" + it.resource); yield break; }
        }
#if UNITY_EDITOR
        if (glb.Length > 0)
        {
            var a = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(glb);
            if (a != null) { done(UnityEngine.Object.Instantiate(a, parent), null, "assetdb:" + glb); yield break; }
        }
#endif
        string file = FindFile(glb);
        if (file == null) { done(null, null, glb.Length == 0 && (!useResource || it.resource.Length == 0) ? "item has no glb / resource" : "asset not found (" + (glb.Length > 0 ? glb : it.resource) + ")"); yield break; }

        var task = LoadGlb(file, parent);
        while (!task.IsCompleted) yield return null;
        var r = task.Result;   // LoadGlb catches everything, it never faults
        if (r.root == null) { done(null, null, "glTFast could not load " + file); yield break; }
        done(r.root, r.res, "gltf:" + file);
    }

    struct Loaded { public GameObject root; public IDisposable res; }

    static async Task<Loaded> LoadGlb(string file, Transform parent)
    {
        var holder = new GameObject("GearGlb");
        try
        {
            holder.transform.SetParent(parent, false);
            var gltf = new GLTFast.GltfImport();
            bool ok = await gltf.LoadFile(file);
            if (ok) ok = await gltf.InstantiateMainSceneAsync(holder.transform);
            if (!ok) { gltf.Dispose(); UnityEngine.Object.Destroy(holder); return new Loaded(); }
            return new Loaded { root = holder, res = gltf };
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Gear] glb load failed (" + file + "): " + e.Message);
            if (holder != null) UnityEngine.Object.Destroy(holder);
            return new Loaded();
        }
    }

    static string FindFile(string glb)
    {
        if (string.IsNullOrEmpty(glb)) return null;
        try
        {
            string name = Path.GetFileName(glb);
            string[] c =
            {
                Path.Combine(Application.streamingAssetsPath, "Gear/" + name),
                Path.Combine(Application.streamingAssetsPath, glb),
                Path.GetFullPath(Path.Combine(Application.dataPath, "../" + glb)),
                glb
            };
            for (int i = 0; i < c.Length; i++) if (Path.IsPathRooted(c[i]) && File.Exists(c[i])) return c[i];
        }
        catch (Exception) { }
        return null;
    }
}
