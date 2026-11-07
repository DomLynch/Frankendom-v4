using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// TERRAINSTAMP v1 (env-dev). EDITOR ONLY, called once by Zone2Build.Build after all zone rows are built (one hunk line: Zone2TerrainStamp.Apply(K);).
// Problem it fixes: Zone2Build "crop" copied ONE terrain chunk (the first MeshFilter), so zone 2 / camp stood on the original terrain with a one-sided patch crossing it.
// What it does instead (zone_span.json zone row, terrain.mode = "stamp"): the ORIGINAL terrain vertices inside the zone window are moved to the shifted zone-1 surface
//   y_new = y + w * (zone1Height(x, z - offsetZ) + yBias - y),  w = smoothstep falloff 1 in the core -> 0 at the window edge over blendM.
// No new triangles, no new draw calls, no clone object, no runtime code. One world surface: no two terrains crossing, no seam at the window edge (w = 0 there) or at the doors.
// Idempotent: every run first puts all terrain chunk meshes back to the pristine GLB meshes (prefab source), then applies every "stamp" row from the PRISTINE heights, so re-running or changing numbers never accumulates.
// The stamped chunks get an embedded copy of the mesh (vertex y + normals of the touched vertices; UVs, tangents, triangles, bounds unchanged). Run order: HdrpWorldMats (places WorldTerrain) -> Zone2Build.
// Rows: zone_span.json zones[].terrain { mode:"stamp", xHalf, zFromLocal, zToLocal, blendM, yBias } (read here with its own small parser; ZoneSpan.cs and Zone2Build.cs parse are untouched, they ignore blendM).
public static class Zone2TerrainStamp
{
    [Serializable] class TB { public string mode = ""; public float xHalf = 32f, zFromLocal = -4f, zToLocal = 44f, blendM = 6f, yBias = 0f; }
    [Serializable] class ZB { public string id = ""; public float offsetZ; public TB terrain = new TB(); }
    [Serializable] class SB { public ZB[] zones = new ZB[0]; public SBuild build = new SBuild(); }
    [Serializable] class SBuild { public string terrainName = "WorldTerrain"; }

    [MenuItem("Hades/Zones/Stamp zone terrain (re-apply)")]
    public static void Run() { var K = SpanData.Get(); Apply(K); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene()); }

    static float Sm(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
    static float Weight(TB t, float x, float zl) { float b = Mathf.Max(0.01f, t.blendM); return Sm((t.xHalf - Mathf.Abs(x)) / b) * Sm((zl - t.zFromLocal) / b) * Sm((t.zToLocal - zl) / b); }

    class Chunk { public MeshFilter mf; public Mesh pristine; public Vector3[] w; public int[][] tris; public Vector3[] nrm; }

    static Mesh Pristine(MeshFilter mf)
    {
        var m = mf.sharedMesh; if (m != null && AssetDatabase.Contains(m)) return m;
        var src = PrefabUtility.GetCorrespondingObjectFromSource(mf); if (src != null && src.sharedMesh != null) return src.sharedMesh;
        return null;
    }

    static void Restore(MeshFilter mf, Mesh pristine)
    {
        var cur = mf.sharedMesh;
        if (cur != pristine)
        {
            var so = new SerializedObject(mf); var p = so.FindProperty("m_Mesh");
            if (PrefabUtility.IsPartOfPrefabInstance(mf)) PrefabUtility.RevertPropertyOverride(p, InteractionMode.AutomatedAction); else mf.sharedMesh = pristine;
            if (mf.sharedMesh != pristine) mf.sharedMesh = pristine;
            if (cur != null && !AssetDatabase.Contains(cur)) UnityEngine.Object.DestroyImmediate(cur);
        }
    }

    public static void Apply(SpanTable K)
    {
        try
        {
            string path = Path.Combine(Application.dataPath, "Data/zone_span.json"); if (!File.Exists(path)) { Debug.LogWarning("[TerrainStamp] no zone_span.json"); return; }
            var S = JsonUtility.FromJson<SB>(File.ReadAllText(path).TrimStart('﻿'));
            var terr = GameObject.Find(S.build.terrainName != null && S.build.terrainName.Length > 0 ? S.build.terrainName : "WorldTerrain");
            if (terr == null) { Debug.LogWarning("[TerrainStamp] no WorldTerrain object, nothing done"); return; }
            var cs = new List<Chunk>();
            foreach (var mf in terr.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue; var pr = Pristine(mf);
                if (pr == null) { Debug.LogError("[TerrainStamp] cannot find the pristine mesh of " + mf.name + ", aborting without changes"); return; }
                cs.Add(new Chunk { mf = mf, pristine = pr });
            }
            foreach (var c in cs) Restore(c.mf, c.pristine);   // back to the GLB meshes
            var rows = new List<ZB>(); foreach (var z in S.zones) if (z != null && z.terrain != null && z.terrain.mode == "stamp") rows.Add(z);
            if (rows.Count == 0) { Debug.Log("[TerrainStamp] no stamp rows: terrain is the pristine GLB"); return; }
            // pristine world triangles + a 4 m height grid over the SOURCE windows (local coordinates)
            var tri = new List<Vector3[]>(); const float G = 4f; var grid = new Dictionary<long, List<int>>();
            Func<int, int, long> key = (gx, gz) => ((long)gx << 32) ^ (uint)gz;
            foreach (var c in cs)
            {
                var v = c.pristine.vertices; var T = c.mf.transform; c.w = new Vector3[v.Length]; for (int i = 0; i < v.Length; i++) c.w[i] = T.TransformPoint(v[i]);
                var sub = new List<int[]>(); for (int s = 0; s < c.pristine.subMeshCount; s++) sub.Add(c.pristine.GetTriangles(s)); c.tris = sub.ToArray();
                foreach (var idx in c.tris) for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    var a = c.w[idx[k]]; var b = c.w[idx[k + 1]]; var d = c.w[idx[k + 2]];
                    float x0 = Mathf.Min(a.x, Mathf.Min(b.x, d.x)), x1 = Mathf.Max(a.x, Mathf.Max(b.x, d.x)), z0 = Mathf.Min(a.z, Mathf.Min(b.z, d.z)), z1 = Mathf.Max(a.z, Mathf.Max(b.z, d.z));
                    bool any = false; foreach (var r in rows) if (x1 >= -r.terrain.xHalf - G && x0 <= r.terrain.xHalf + G && z1 >= r.terrain.zFromLocal - G && z0 <= r.terrain.zToLocal + G) { any = true; break; }
                    if (!any) continue;
                    int id = tri.Count; tri.Add(new[] { a, b, d });
                    for (int gx = Mathf.FloorToInt(x0 / G); gx <= Mathf.FloorToInt(x1 / G); gx++) for (int gz = Mathf.FloorToInt(z0 / G); gz <= Mathf.FloorToInt(z1 / G); gz++)
                    { long kk = key(gx, gz); List<int> l; if (!grid.TryGetValue(kk, out l)) { l = new List<int>(); grid[kk] = l; } l.Add(id); }
                }
            }
            Func<float, float, float?> H = (x, z) =>
            {
                List<int> l; if (!grid.TryGetValue(key(Mathf.FloorToInt(x / G), Mathf.FloorToInt(z / G)), out l)) return null; float? best = null;
                foreach (int i in l)
                {
                    var t = tri[i]; float dd = (t[1].z - t[2].z) * (t[0].x - t[2].x) + (t[2].x - t[1].x) * (t[0].z - t[2].z); if (Mathf.Abs(dd) < 1e-12f) continue;
                    float l1 = ((t[1].z - t[2].z) * (x - t[2].x) + (t[2].x - t[1].x) * (z - t[2].z)) / dd, l2 = ((t[2].z - t[0].z) * (x - t[2].x) + (t[0].x - t[2].x) * (z - t[2].z)) / dd, l3 = 1f - l1 - l2;
                    if (l1 < -1e-5f || l2 < -1e-5f || l3 < -1e-5f) continue;
                    float h = l1 * t[0].y + l2 * t[1].y + l3 * t[2].y; if (best == null || h > best.Value) best = h;
                }
                return best;
            };
            var ny = new List<float[]>(); var moved = new List<bool[]>(); foreach (var c in cs) { var a = new float[c.w.Length]; for (int i = 0; i < a.Length; i++) a[i] = c.w[i].y; ny.Add(a); moved.Add(new bool[c.w.Length]); }
            var perRow = new Dictionary<string, int>();
            foreach (var r in rows)
            {
                int n = 0;
                for (int ci = 0; ci < cs.Count; ci++)
                {
                    var w = cs[ci].w;
                    for (int i = 0; i < w.Length; i++)
                    {
                        float zl = w[i].z - r.offsetZ; if (Mathf.Abs(w[i].x) >= r.terrain.xHalf || zl <= r.terrain.zFromLocal || zl >= r.terrain.zToLocal) continue;
                        float wt = Weight(r.terrain, w[i].x, zl); if (wt <= 0f) continue;
                        var tgt = H(w[i].x, zl); if (tgt == null) continue;
                        ny[ci][i] += wt * (tgt.Value + r.terrain.yBias - ny[ci][i]); moved[ci][i] = true; n++;
                    }
                }
                perRow[r.id] = n;
            }
            // normals: area-weighted, welded by 1 cm position over ALL chunks (chunk borders duplicate vertices), applied only to vertices of triangles that touch a moved vertex
            var acc = new Dictionary<Vector3Int, Vector3>(); Func<Vector3, Vector3Int> K3 = p => new Vector3Int(Mathf.RoundToInt(p.x * 100f), Mathf.RoundToInt(p.y * 100f), Mathf.RoundToInt(p.z * 100f));
            for (int ci = 0; ci < cs.Count; ci++)
                foreach (var idx in cs[ci].tris) for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    var a = new Vector3(cs[ci].w[idx[k]].x, ny[ci][idx[k]], cs[ci].w[idx[k]].z); var b = new Vector3(cs[ci].w[idx[k + 1]].x, ny[ci][idx[k + 1]], cs[ci].w[idx[k + 1]].z); var d = new Vector3(cs[ci].w[idx[k + 2]].x, ny[ci][idx[k + 2]], cs[ci].w[idx[k + 2]].z);
                    var fn = Vector3.Cross(b - a, d - a); if (fn.y < 0f) fn = -fn;
                    foreach (var p in new[] { a, b, d }) { var kk = K3(p); Vector3 o; acc.TryGetValue(kk, out o); acc[kk] = o + fn; }
                }
            int chunksChanged = 0, vertsChanged = 0;
            for (int ci = 0; ci < cs.Count; ci++)
            {
                var c = cs[ci]; bool any = false; foreach (bool m in moved[ci]) if (m) { any = true; break; } if (!any) continue;
                var touched = new bool[c.w.Length];
                foreach (var idx in c.tris) for (int k = 0; k + 2 < idx.Length; k += 3) if (moved[ci][idx[k]] || moved[ci][idx[k + 1]] || moved[ci][idx[k + 2]]) { touched[idx[k]] = touched[idx[k + 1]] = touched[idx[k + 2]] = true; }
                var mesh = UnityEngine.Object.Instantiate(c.pristine); mesh.name = c.pristine.name + "_stamp";
                var v = c.pristine.vertices; var nr = c.pristine.normals; bool hasN = nr != null && nr.Length == v.Length; var T = c.mf.transform;
                for (int i = 0; i < v.Length; i++)
                {
                    if (!moved[ci][i] && !touched[i]) continue;
                    var wp = new Vector3(c.w[i].x, ny[ci][i], c.w[i].z); v[i] = T.InverseTransformPoint(wp); vertsChanged++;
                    if (hasN) { Vector3 a; if (acc.TryGetValue(K3(wp), out a) && a.sqrMagnitude > 1e-12f) nr[i] = T.InverseTransformDirection(a.normalized); }
                }
                mesh.vertices = v; if (hasN) mesh.normals = nr; mesh.RecalculateBounds();
                c.mf.sharedMesh = mesh; chunksChanged++;
            }
            foreach (var kv in perRow) Debug.Log("[TerrainStamp] zone '" + kv.Key + "': " + kv.Value + " original terrain vertices moved to the shifted zone-1 surface");
            Debug.Log("[TerrainStamp] " + chunksChanged + " of " + cs.Count + " terrain chunks got a stamped mesh copy (" + vertsChanged + " vertices rewritten). Triangles and draw calls added: 0.");
        }
        catch (Exception e) { Debug.LogError("[TerrainStamp] failed, terrain left as it was: " + e); }
    }
}
