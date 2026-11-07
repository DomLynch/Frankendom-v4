using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// TERRAINSTAMP v2 (env-dev). EDITOR ONLY. Three calls from Zone2Build.Build (hunks, see apply_terrainstamp.py):
//   bool stampOk = Zone2TerrainStamp.Apply(K);   // BEFORE any zone is built: Prepare (validate everything, build the new meshes in memory, no scene change) then Commit (assign them). Any failure = Revert() = pristine terrain, false.
//   per row: a "stamp" row with stampOk == false is switched to the old crop path in memory (the crop stays intact as the fallback), so a failed stamp can never leave a zone without terrain.
// Zone2Build saves the scene before each HdrpGltfMats.Refresh() re-open, so a stamp committed before the loop is saved and survives the re-opens.
// v1 failed in Unity because the terrain root also holds scene-only meshes (TerrainCentrePlug, no prefab source) and the stamp aborted AFTER Zone2Build had skipped the crop. v2: scene-only meshes are skipped (logged), nothing is
// touched until validation has passed (chunks found, numbers sane, every window finds >= 99.5% of its source heights, max vertex move < 5 m), and a failed Prepare leaves the crop path to do what it always did.
// What the stamp does (zone_span.json zone row terrain { mode:"stamp", xHalf, zFromLocal, zToLocal, blendM, yBias }): the ORIGINAL terrain vertices inside each zone window are moved to the shifted zone-1 height with a smoothstep falloff
// (TerrainStampCore, pure C#, also run by the standalone test on the real GLB). No new triangles, no new draw calls, no clone, no runtime code.
// Saved-mesh path (decided): the new meshes are EMBEDDED in the scene (assigned to the MeshFilter, serialized with HadesSlice.unity), exactly like the Zone2_TerrainCrop mesh that CropTerrain already saves (found in the saved scene, 12,952 tris).
// No .asset files, nothing in the GLB import. Pristine meshes: the mesh asset itself, else the prefab source of the MeshFilter, else the GLB sub-asset of the same name (path from world_mats.json terrain.glb).
public static class Zone2TerrainStamp
{
    [Serializable] class TB { public string mode = ""; public float xHalf = 32f, zFromLocal = -4f, zToLocal = 44f, blendM = 6f, yBias = 0f; }
    [Serializable] class ZB { public string id = ""; public float offsetZ; public TB terrain = new TB(); }
    [Serializable] class SB { public ZB[] zones = new ZB[0]; public SBuild build = new SBuild(); }
    [Serializable] class SBuild { public string terrainName = "WorldTerrain"; }
    [Serializable] class WM { public WMT terrain = new WMT(); }
    [Serializable] class WMT { public string glb = "Assets/Env/World/Terrain/terrain_ground.glb"; }

    class Item { public MeshFilter mf; public Mesh pristine; public Mesh stamped; }
    static List<Item> all = new List<Item>();   // every terrain chunk with a pristine mesh (scene-only meshes are not in here)

    [MenuItem("Hades/Zones/Stamp zone terrain (re-apply)")]
    public static void Run() { var K = SpanData.Get(); Apply(K); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene()); }

    /// <summary>All-or-nothing. True = the terrain is stamped (or, with no stamp rows, pristine). False = the terrain is the pristine GLB and the stamp rows must use the crop path.</summary>
    public static bool Apply(SpanTable K)
    {
        if (!Prepare(K)) { Revert(); return false; }
        if (!Commit()) { Revert(); return false; }
        return true;
    }

    static Mesh Pristine(MeshFilter mf, Dictionary<string, Mesh> glbByName)
    {
        var m = mf.sharedMesh; if (m != null && AssetDatabase.Contains(m)) return m;
        var src = PrefabUtility.GetCorrespondingObjectFromSource(mf); if (src != null && src.sharedMesh != null && AssetDatabase.Contains(src.sharedMesh)) return src.sharedMesh;
        if (m != null && m.name.EndsWith("_stamp")) { Mesh g; if (glbByName.TryGetValue(m.name.Substring(0, m.name.Length - 6), out g)) return g; }
        return null;
    }

    static void Assign(MeshFilter mf, Mesh m)
    {
        var cur = mf.sharedMesh; if (cur == m) return;
        mf.sharedMesh = m;
        if (cur != null && !AssetDatabase.Contains(cur)) UnityEngine.Object.DestroyImmediate(cur);   // an old embedded stamped copy
    }

    public static bool Prepare(SpanTable K)
    {
        foreach (var it in all) if (it.stamped != null) UnityEngine.Object.DestroyImmediate(it.stamped);
        all = new List<Item>();
        try
        {
            string path = Path.Combine(Application.dataPath, "Data/zone_span.json"); if (!File.Exists(path)) { Debug.LogWarning("[TerrainStamp] no zone_span.json: nothing prepared"); return false; }
            var S = JsonUtility.FromJson<SB>(File.ReadAllText(path).TrimStart('﻿'));
            var rows = new List<ZB>(); foreach (var z in S.zones) if (z != null && z.terrain != null && z.terrain.mode == "stamp") rows.Add(z);
            if (rows.Count == 0) { Debug.Log("[TerrainStamp] no stamp rows: terrain will be the pristine GLB"); }
            var terr = GameObject.Find(S.build.terrainName != null && S.build.terrainName.Length > 0 ? S.build.terrainName : "WorldTerrain");
            if (terr == null) { Debug.LogError("[TerrainStamp] no WorldTerrain object: stamp NOT done, crop fallback"); return rows.Count == 0; }
            // GLB sub-assets by name (fallback to find the pristine mesh of an already stamped chunk)
            var byName = new Dictionary<string, Mesh>(); string glb = "Assets/Env/World/Terrain/terrain_ground.glb";
            try { string t = Path.Combine(Application.dataPath, "Data/world_mats.json"); if (File.Exists(t)) { var w = JsonUtility.FromJson<WM>(File.ReadAllText(t).TrimStart('﻿')); if (w != null && w.terrain != null && !string.IsNullOrEmpty(w.terrain.glb)) glb = w.terrain.glb; } } catch (Exception) { }
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(glb)) { var m = o as Mesh; if (m != null && !byName.ContainsKey(m.name)) byName[m.name] = m; }
            var chunks = new List<TerrainStampCore.Chunk>(); int skipped = 0;
            foreach (var mf in terr.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var pr = Pristine(mf, byName);
                if (pr == null) { skipped++; Debug.Log("[TerrainStamp] '" + mf.name + "' is a scene-only mesh (no prefab source): left untouched"); continue; }
                if (pr.subMeshCount < 1) continue;
                var v = pr.vertices; var T = mf.transform; var c = new TerrainStampCore.Chunk { name = mf.name, x = new float[v.Length], y = new float[v.Length], z = new float[v.Length] };
                for (int i = 0; i < v.Length; i++) { var w = T.TransformPoint(v[i]); c.x[i] = w.x; c.y[i] = w.y; c.z[i] = w.z; }
                var sub = new List<int[]>(); for (int s = 0; s < pr.subMeshCount; s++) sub.Add(pr.GetTriangles(s)); c.tris = sub.ToArray();
                chunks.Add(c); all.Add(new Item { mf = mf, pristine = pr });
            }
            if (rows.Count == 0) return true;   // Commit() will just put the pristine meshes back
            if (chunks.Count == 0) { Debug.LogError("[TerrainStamp] no terrain chunk with a pristine mesh (" + skipped + " scene-only skipped): stamp NOT done, crop fallback"); all.Clear(); return false; }
            var ws = new List<TerrainStampCore.Win>();
            foreach (var r in rows) ws.Add(new TerrainStampCore.Win { id = r.id, offsetZ = r.offsetZ, xHalf = r.terrain.xHalf, zFrom = r.terrain.zFromLocal, zTo = r.terrain.zToLocal, blend = r.terrain.blendM, yBias = r.terrain.yBias });
            var R = TerrainStampCore.Compute(chunks, ws, 0.995f, 5f);
            if (!R.ok) { Debug.LogError("[TerrainStamp] validation failed (" + R.error + "): stamp NOT done, crop fallback, scene untouched"); all.Clear(); return false; }
            // build the new meshes in memory (nothing assigned yet)
            for (int ci = 0; ci < all.Count; ci++)
            {
                bool any = false; foreach (bool m in R.moved[ci]) if (m) { any = true; break; } if (!any) continue;
                var it = all[ci]; var mesh = UnityEngine.Object.Instantiate(it.pristine); mesh.name = it.pristine.name + "_stamp";
                var v = it.pristine.vertices; var nr = it.pristine.normals; bool hasN = nr != null && nr.Length == v.Length; var T = it.mf.transform;
                for (int i = 0; i < v.Length; i++)
                {
                    if (!R.moved[ci][i] && !R.touched[ci][i]) continue;
                    v[i] = T.InverseTransformPoint(new Vector3(chunks[ci].x[i], R.newY[ci][i], chunks[ci].z[i]));
                    if (hasN && (R.nx[ci][i] != 0f || R.ny[ci][i] != 0f || R.nz[ci][i] != 0f)) nr[i] = T.InverseTransformDirection(new Vector3(R.nx[ci][i], R.ny[ci][i], R.nz[ci][i]));
                }
                mesh.vertices = v; if (hasN) mesh.normals = nr; mesh.RecalculateBounds(); it.stamped = mesh;
            }
            foreach (var kv in R.movedPerWin) Debug.Log("[TerrainStamp] prepared zone '" + kv.Key + "': " + kv.Value + " original terrain vertices moved (source coverage " + (R.coverage[kv.Key] * 100f).ToString("0.0") + "%)");
            Debug.Log("[TerrainStamp] prepared: " + R.chunksChanged + " of " + all.Count + " terrain chunks (" + skipped + " scene-only skipped), " + R.vertsRewritten + " vertices rewritten, max move " + R.maxDelta.ToString("0.00") + " m. Triangles and draw calls added: 0.");
            return true;
        }
        catch (Exception e) { Debug.LogError("[TerrainStamp] Prepare failed (" + e.Message + "): stamp NOT done, crop fallback, scene untouched"); foreach (var it in all) if (it.stamped != null) UnityEngine.Object.DestroyImmediate(it.stamped); all.Clear(); return false; }
    }

    /// <summary>Assign the prepared meshes (a chunk without a stamped mesh gets its pristine GLB mesh back). Never throws; false = nothing trustworthy was done, caller reverts.</summary>
    static bool Commit()
    {
        try
        {
            foreach (var it in all) if (it.mf == null) { Debug.LogError("[TerrainStamp] a terrain MeshFilter vanished before commit"); return false; }
            foreach (var it in all) Assign(it.mf, it.stamped != null ? it.stamped : it.pristine);
            int n = 0; foreach (var it in all) if (it.stamped != null) n++; Debug.Log("[TerrainStamp] committed: " + n + " chunk meshes stamped"); return true;
        }
        catch (Exception e) { Debug.LogError("[TerrainStamp] Commit failed (" + e.Message + ")"); return false; }
    }

    /// <summary>Pristine GLB meshes back on every terrain chunk that has one (also removes an older stamp).</summary>
    public static void Revert()
    {
        try
        {
            if (all.Count == 0)   // Prepare did not get that far: find the chunks again
            {
                var terr = GameObject.Find("WorldTerrain"); if (terr == null) return;
                var byName = new Dictionary<string, Mesh>(); foreach (var o in AssetDatabase.LoadAllAssetsAtPath("Assets/Env/World/Terrain/terrain_ground.glb")) { var m = o as Mesh; if (m != null && !byName.ContainsKey(m.name)) byName[m.name] = m; }
                foreach (var mf in terr.GetComponentsInChildren<MeshFilter>(true)) { if (mf.sharedMesh == null) continue; var pr = Pristine(mf, byName); if (pr != null) all.Add(new Item { mf = mf, pristine = pr }); }
            }
            foreach (var it in all) { Assign(it.mf, it.pristine); if (it.stamped != null) { UnityEngine.Object.DestroyImmediate(it.stamped); it.stamped = null; } }
            Debug.Log("[TerrainStamp] terrain is the pristine GLB (" + all.Count + " chunks)");
        }
        catch (Exception e) { Debug.LogError("[TerrainStamp] Revert failed: " + e.Message); }
    }
}
