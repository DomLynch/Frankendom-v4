using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// ZONESPAN v1 (on ZONE2DUP v1): builds EVERY zone row of zone_span.json into HadesSlice.unity as an inactive root per row (any number of zones; a row = offset + layout file + ring groups). Idempotent (a second run replaces the first).
// EDITOR ONLY, run by the Unity dev at a build boundary: menu Hades > Zones > Build zone 2 (duplicate), or -executeMethod Zone2Build.RunAndSave. Layout files are made by gen_zone2.py.
// Everything is shared: PropsLayout places the SAME GLB assets as zone 1; fog objects / probes are Instantiate-d (same mesh / material / baked-cube assets). The TERRAIN is not cloned whole: per row terrain.mode "crop" builds a new mesh from only the
// triangles inside the row's window (terrain.xHalf, zFromLocal..zToLocal, shifted by offsetZ) and starts it past the original's coplanar band (no z-fight, no second draw of the 880 m mesh); "none" = no terrain visual; "clone" = the old whole clone.
// All ring groups are saved INACTIVE: with the span off, a zone costs nothing in the frame.
public static class Zone2Build
{
    [MenuItem("Hades/Zones/Build zone 2 (duplicate)")]
    public static void Run() { Build(false); }
    public static void RunAndSave() { Build(true); }

    static void Build(bool save)
    {
        var sc = EditorSceneManager.GetActiveScene();
        if (!sc.path.EndsWith("HadesSlice.unity")) sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        SpanData.Reload(); var K = SpanData.Get();
        foreach (var row in K.zones)
        {
            if (row == null) continue; float D = row.offsetZ;
            string lay = Path.Combine(Application.dataPath, "Data/" + row.layout);
            if (!File.Exists(lay)) { Debug.LogError("[Zone2Build] zone '" + row.id + "': missing " + lay + " (run gen_zone2.py / apply_zonespan.py --apply)"); continue; }
            var old = GameObject.Find(row.root); if (old != null) Object.DestroyImmediate(old);
            var root = new GameObject(row.root); root.transform.position = Vector3.zero;
            int placed = PropsLayout.Build(root, lay); int missing = PropsLayout.LastMissing;
            { UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene()); HdrpGltfMats.Refresh(); root = GameObject.Find(row.root); }   // new GLB instances need their HDRP materials (same as after PropsLayout.RebuildInScene); v107 unity-dev: Refresh re-opens the scene in batch mode, so save first and re-find the root
            Transform ring0 = row.rings.Length > 0 ? root.transform.Find(row.rings[0].group) : null; if (ring0 == null) ring0 = root.transform;
            int cloned = 0; string tn = row.root + "_";
            var terr = GameObject.Find(K.build.terrainName);
            if (terr != null && row.terrain.mode == "crop") { if (CropTerrain(terr, ring0, row, tn)) cloned++; else { Clone(terr, ring0, D, tn); cloned++; } }
            else if (terr != null && row.terrain.mode == "clone") { Clone(terr, ring0, D, tn); cloned++; }
            else if (terr == null && row.terrain.mode != "none") Debug.LogWarning("[Zone2Build] no '" + K.build.terrainName + "' object: zone '" + row.id + "' has no terrain visual");
            foreach (var n in K.build.cloneNames) { var g = GameObject.Find(n); if (g != null) { Clone(g, ring0, D, tn); cloned++; } else Debug.LogWarning("[Zone2Build] clone name '" + n + "' not found (skipped)"); }
            if (K.build.cloneProbes)
                foreach (var p in Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                { if (IsInZoneRoot(p.transform, K)) continue; Clone(p.gameObject, ring0, D, tn); cloned++; }
            var col = new GameObject(K.build.colliderName + "_" + row.root); col.transform.SetParent(ring0, false); col.transform.position = new Vector3(0f, -0.5f, D + row.colliderCenterLocal);
            col.AddComponent<BoxCollider>().size = new Vector3(80f, 1f, row.colliderSizeZ);
            foreach (var r in row.rings) { var g = root.transform.Find(r.group); if (g != null) g.gameObject.SetActive(false); }
            Debug.Log("[Zone2Build] " + row.root + " (" + row.id + ") built: " + placed + " rows placed (" + missing + " missing assets), " + cloned + " objects cloned, rings inactive. Offset z " + D);
        }
        sc = EditorSceneManager.GetActiveScene(); /* v108b: Refresh() re-opens the scene, the old handle is stale and the last zone's work was never saved */ EditorSceneManager.MarkSceneDirty(sc); if (save) EditorSceneManager.SaveScene(sc);
    }

    static bool IsInZoneRoot(Transform t, SpanTable K) { foreach (var z in K.zones) { var r = GameObject.Find(z.root); if (r != null && t.IsChildOf(r.transform)) return true; } return false; }

    static void Clone(GameObject src, Transform parent, float dz, string prefix)
    {
        var c = Object.Instantiate(src, src.transform.position, src.transform.rotation, parent); c.name = prefix + src.name;
        c.transform.position = src.transform.position + new Vector3(0f, 0f, dz); c.SetActive(src.activeSelf);
    }

    /// <summary>A new object with a mesh holding only the source triangles whose three vertices lie inside the window (world x within +-xHalf, world z within zFromLocal..zToLocal of the SOURCE), moved by offsetZ + yBias. Same material assets. False if the mesh is not readable (the caller falls back to a whole clone).</summary>
    static bool CropTerrain(GameObject src, Transform parent, SpZone row, string prefix)
    {
        var mf = src.GetComponentInChildren<MeshFilter>(); var mr = src.GetComponentInChildren<MeshRenderer>();
        if (mf == null || mr == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) { Debug.LogWarning("[Zone2Build] terrain mesh missing or not readable (enable Read/Write on the model): whole clone instead of a crop"); return false; }
        var m = mf.sharedMesh; var T = mf.transform; var v = m.vertices; var nrm = m.normals; var uv = m.uv; var tan = m.tangents; int vc = v.Length;
        var inside = new bool[vc]; float z0 = row.terrain.zFromLocal, z1 = row.terrain.zToLocal, xh = row.terrain.xHalf;
        for (int i = 0; i < vc; i++) { var w = T.TransformPoint(v[i]); inside[i] = Mathf.Abs(w.x) <= xh && w.z >= z0 && w.z <= z1; }
        var map = new int[vc]; for (int i = 0; i < vc; i++) map[i] = -1;
        var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nu = new List<Vector2>(); var nt = new List<Vector4>(); var subs = new List<List<int>>(); int kept = 0;
        for (int s = 0; s < m.subMeshCount; s++)
        {
            var idx = m.GetTriangles(s); var list = new List<int>();
            for (int k = 0; k + 2 < idx.Length; k += 3)
            {
                int a = idx[k], b = idx[k + 1], c = idx[k + 2]; if (!(inside[a] && inside[b] && inside[c])) continue; kept++;
                foreach (int q in new[] { a, b, c })
                {
                    if (map[q] < 0) { map[q] = nv.Count; nv.Add(v[q]); if (nrm.Length == vc) nn.Add(nrm[q]); if (uv.Length == vc) nu.Add(uv[q]); if (tan.Length == vc) nt.Add(tan[q]); }
                    list.Add(map[q]);
                }
            }
            subs.Add(list);
        }
        if (kept == 0) { Debug.LogWarning("[Zone2Build] terrain crop window of zone '" + row.id + "' holds no triangle; no terrain visual"); return true; }
        var cm = new Mesh { name = prefix + "TerrainCrop" }; if (nv.Count > 65000) cm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        cm.SetVertices(nv); if (nn.Count == nv.Count) cm.SetNormals(nn); if (nu.Count == nv.Count) cm.SetUVs(0, nu); if (nt.Count == nv.Count) cm.SetTangents(nt);
        cm.subMeshCount = subs.Count; for (int s = 0; s < subs.Count; s++) cm.SetTriangles(subs[s], s); cm.RecalculateBounds();
        var go = new GameObject(prefix + src.name); go.transform.SetParent(parent, false); go.transform.SetPositionAndRotation(T.position + new Vector3(0f, row.terrain.yBias, row.offsetZ), T.rotation); go.transform.localScale = T.lossyScale;
        go.AddComponent<MeshFilter>().sharedMesh = cm; var r2 = go.AddComponent<MeshRenderer>(); r2.sharedMaterials = mr.sharedMaterials; r2.shadowCastingMode = mr.shadowCastingMode; r2.receiveShadows = mr.receiveShadows;
        Debug.Log("[Zone2Build] terrain crop of zone '" + row.id + "': " + nv.Count + " of " + vc + " vertices, " + kept + " triangles");
        return true;
    }
}
