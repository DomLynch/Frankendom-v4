using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// PROPSRUNTIME v1 (Lead, 17 Nov, "everything on data"): props without a scene bake. Rows in props_runtime.json (StreamingAssets twin) place a runtime-loaded GLB
// (StreamingAssets/PropsRuntime/<glb>) in a zone when that zone becomes active, and remove it again when the zone changes: no props_layout bake, no player build.
// Hooked by ONE call in ZoneRuntime.Poll (PropsRuntime.Tick(zone id)); no RuntimeInitializeOnLoadMethod (a compile-only take never runs a new one), no serialized fields on any scene component.
// enabled=false (shipped) = the file is read once and nothing else ever runs. Materials: the same glTFast shader-graph -> HDRP Lit conversion the camp NPCs and gear use (GearMaterials.Convert).
// Row: { id, zone, glb, pos [x,z] (ground by raycast) or [x,y,z], yaw, scale, castShadows, collider none|box|mesh, enabled }. Log: [PropsRuntime] zone X: applied N rows, failed M, skipped K.
public static class PropsRuntime
{
    [Serializable] public class PRow { public string id = "", zone = "", glb = "", collider = "none"; public float[] pos = new float[0]; public float yaw = 0f, scale = 1f; public bool castShadows = true, enabled = true; }
    [Serializable] public class PRoot { public bool enabled = false; public string folder = "PropsRuntime"; public float reloadCheckSeconds = 3f; public PRow[] rows = new PRow[0]; }

    static PRoot data; static bool loaded; static string path; static DateTime stamp; static float nextCheck;
    static string curZone = ""; static GameObject root; static readonly List<GLTFast.GltfImport> imports = new List<GLTFast.GltfImport>(); static int gen;

    static void Load()
    {
        loaded = true; data = new PRoot(); path = DataPath.Find("props_runtime.json"); if (path == null) return;
        try { var r = JsonUtility.FromJson<PRoot>(DataPath.ReadFile(path)); if (r != null) data = r; stamp = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) { Debug.LogWarning("[PropsRuntime] json unreadable: " + e.Message); }
    }

    /// <summary>Called every zone poll with the current zone id ("" = none). Cheap: a string compare when nothing changed.</summary>
    public static void Tick(string zoneId)
    {
        if (!loaded) Load();
        if (data == null || !data.enabled) { if (root != null) Clear(); curZone = zoneId ?? ""; return; }
        zoneId = zoneId ?? "";
        if (zoneId != curZone) { curZone = zoneId; Clear(); Build(zoneId); return; }
        if (path != null && Time.unscaledTime >= nextCheck)   // hot reload: the file changed while the zone is up
        {
            nextCheck = Time.unscaledTime + Mathf.Max(1f, data.reloadCheckSeconds);
            try { var w = File.GetLastWriteTimeUtc(path); if (w != stamp) { Load(); Clear(); if (data.enabled) Build(zoneId); } } catch (Exception) { }
        }
    }

    static void Clear()
    {
        gen++;   // a load still in flight for the old zone drops its result
        if (root != null) UnityEngine.Object.Destroy(root); root = null;
        foreach (var g in imports) { try { if (g != null) g.Dispose(); } catch (Exception) { } }
        imports.Clear();
    }

    static void Build(string zoneId)
    {
        if (zoneId.Length == 0 || data.rows == null) return;
        var rows = new List<PRow>(); int skipped = 0;
        foreach (var r in data.rows) { if (r == null) continue; if (!r.enabled || r.zone != zoneId || string.IsNullOrEmpty(r.glb)) { skipped++; continue; } rows.Add(r); }
        if (rows.Count == 0) { Debug.Log("[PropsRuntime] zone " + zoneId + ": applied 0 rows, failed 0, skipped " + skipped); return; }
        root = new GameObject("PropsRuntime_" + zoneId); RunAll(zoneId, rows, skipped, root, gen);
    }

    static async void RunAll(string zoneId, List<PRow> rows, int skipped, GameObject holder, int myGen)
    {
        int ok = 0, fail = 0;
        foreach (var r in rows)
        {
            if (myGen != gen || holder == null) return;   // zone changed meanwhile
            try { if (await PlaceOne(r, holder, myGen)) ok++; else fail++; }
            catch (Exception e) { fail++; Debug.LogWarning("[PropsRuntime] row " + r.id + ": " + e.GetType().Name + " " + e.Message); }
        }
        if (myGen == gen) Debug.Log("[PropsRuntime] zone " + zoneId + ": applied " + ok + " rows, failed " + fail + ", skipped " + skipped);
    }

    static async System.Threading.Tasks.Task<bool> PlaceOne(PRow r, GameObject holder, int myGen)
    {
        string file = Path.Combine(Path.Combine(Application.streamingAssetsPath, string.IsNullOrEmpty(data.folder) ? "PropsRuntime" : data.folder), r.glb.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file)) { Debug.LogWarning("[PropsRuntime] row " + r.id + ": missing " + file); return false; }
        var imp = new GLTFast.GltfImport();
        if (!await imp.LoadFile(file)) { Debug.LogWarning("[PropsRuntime] row " + r.id + ": glTFast could not load " + r.glb); imp.Dispose(); return false; }
        if (myGen != gen || holder == null) { imp.Dispose(); return false; }
        imports.Add(imp);
        var go = new GameObject("prop_" + r.id); go.transform.SetParent(holder.transform, false); go.SetActive(false);
        if (!await imp.InstantiateMainSceneAsync(go.transform)) { Debug.LogWarning("[PropsRuntime] row " + r.id + ": glTFast could not instantiate " + r.glb); UnityEngine.Object.Destroy(go); return false; }
        if (myGen != gen || holder == null) { UnityEngine.Object.Destroy(go); return false; }
        var rl = new List<Renderer>(go.GetComponentsInChildren<Renderer>(true));
        GearMaterials.Convert(rl);
        foreach (var rd in rl) { rd.shadowCastingMode = r.castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off; rd.receiveShadows = true; }
        go.transform.position = Spot(r); go.transform.rotation = Quaternion.Euler(0f, r.yaw, 0f); go.transform.localScale = Vector3.one * (r.scale > 0f ? r.scale : 1f);
        string col = (r.collider ?? "none").ToLowerInvariant();
        if (col == "box") { var b = new Bounds(); bool first = true; foreach (var rd in rl) { if (first) { b = rd.bounds; first = false; } else b.Encapsulate(rd.bounds); } if (!first) { var bc = go.AddComponent<BoxCollider>(); bc.center = go.transform.InverseTransformPoint(b.center); var ls = go.transform.lossyScale; bc.size = new Vector3(b.size.x / Mathf.Max(1e-4f, ls.x), b.size.y / Mathf.Max(1e-4f, ls.y), b.size.z / Mathf.Max(1e-4f, ls.z)); } }
        else if (col == "mesh") { foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh != null && mf.GetComponent<Collider>() == null) mf.gameObject.AddComponent<MeshCollider>(); }
        go.SetActive(true);
        return true;
    }

    /// <summary>World point of a row: [x,y,z] as given; [x,z] = ground below by a raycast (lowest surface that is not a fighter, within 12 m under the player's height), else the player's height.</summary>
    static Vector3 Spot(PRow r)
    {
        var p = r.pos ?? new float[0];
        if (p.Length >= 3) return new Vector3(p[0], p[1], p[2]);
        float x = p.Length > 0 ? p[0] : 0f, z = p.Length > 1 ? p[1] : 0f; float refY = 0f;
        foreach (var df in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (df != null && df.IsPlayer) { refY = df.transform.position.y; break; }
        float low = float.NaN;
        foreach (var h in Physics.RaycastAll(new Vector3(x, refY + 3f, z), Vector3.down, 12f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.collider == null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (root != null && h.collider.transform.IsChildOf(root.transform)) continue;
            if (float.IsNaN(low) || h.point.y < low) low = h.point.y;
        }
        return new Vector3(x, float.IsNaN(low) ? refY : low, z);
    }
}
