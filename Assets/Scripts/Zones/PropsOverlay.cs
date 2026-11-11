using System;
using System.Collections.Generic;
using UnityEngine;

// PROPSOVERLAY v1 (laneDATA3). Runtime overlay of added / moved / disabled prop rows on top of the BAKED props_layout_*.json scene objects, so ordinary prop edits are HOT (no scene chain).
// Data: props_overlay.json (StreamingAssets, then Assets/Data). enabled=false, a missing file or an empty rows list = Boot returns or applies nothing, nothing touched. Args: -propsoverlay:1|0.
// When: after a scene load, once at least one baked wrapper (a grandchild of a scene root) exists plus apply.delayFrames, then once per load. ZoneSpan ring groups are not changed: clones join a ring group, so the ring wake/sleep carries them.
// Baked naming (PropsLayout.PlaceOne): wrapper GameObject named by the row id, under a group (Arena/<Group> in the main scene, <ZoneRoot>/<ZnRingN> in zones), children LOD0..LOD2 + LODGroup, world coordinates, group transforms at identity.
// add = Instantiate of a baked wrapper (src), so materials, LODGroup and shadow flags are the baked ones (no GLB load, no HDRP material step); move = set the baked wrapper's transform; disable = SetActive(false).
// Ground: pos[1] is the bottom of the prop (as PropsLayout "bottom") or, with ground=snap, an offset above the raycast ground hit (ZoneRowGround.Snap algorithm; run when the ring is awake, its colliders are off before).
// Clearance: rows inside a play_areas.json area/lane (plus clearM) are rejected. Hot reload: the file time is watched (apply.watchFileS, 0 = off); a change reverts the overlay and applies the new one.
// Baked-only things stay baked: LOD swaps of a baked row, lod_tex_map, colliders, baked occlusion data. UNPROVEN in Unity.
public class PropsOverlay : MonoBehaviour
{
    [Serializable] public class Row
    {
        public string id, op = "add", src, zone, group, ground = "bottom", note;
        public int ring = 1, lod = -1; public bool enabled = true;
        public float[] pos = new float[0], rotEuler = new float[0]; public float scale = 0f, scaleYMul = 0f;
    }
    [Serializable] public class ApplyCfg { public int delayFrames = 3; public float waitRootsS = 20f, pollSeconds = 0.5f, minPollS = 0.1f, watchFileS = 2f; }
    [Serializable] public class SnapCfg { public float rayUpM = 0f, rayDownM = 0f, sinkM = -1f; }
    [Serializable] public class ClearCfg { public bool enabled = true, lane = true; public float clearM = 1.5f; }
    [Serializable] public class Cfg
    {
        public bool enabled = false; public int version = 1; public string mainRoot = "Arena", scaleMode = "relative"; public string[] layouts = new string[0];
        public ApplyCfg apply = new ApplyCfg(); public SnapCfg snap = new SnapCfg(); public ClearCfg clearance = new ClearCfg(); public Row[] rows = new Row[0];
    }
    [Serializable] class SpRingJ { public string group; }
    [Serializable] class SpZoneJ { public string id, root; public SpRingJ[] rings; }
    [Serializable] class SpFileJ { public SpZoneJ[] zones; }
    [Serializable] class ZgJ { public float rayUpM = 3f, rayDownM = 12f, sinkM = 0.03f; }
    [Serializable] class LRow { public string id; public float scale = 1f, scaleYMul = 1f; }
    [Serializable] class LFile { public LRow[] props; }
    class Saved { public Transform t; public Vector3 p, s; public Quaternion r; public bool active; }

    const string ArgPrefix = "-propsoverlay:", FileName = "props_overlay.json";
    Cfg c; string srcPath; DateTime srcTime; float next, nextWatch, waitStart; int frames; bool applied, waiting;
    readonly Dictionary<string, Transform> index = new Dictionary<string, Transform>();
    readonly HashSet<Transform> groups = new HashSet<Transform>();
    readonly List<Transform> clones = new List<Transform>();
    readonly Dictionary<Transform, Saved> saved = new Dictionary<Transform, Saved>();
    readonly List<Row> snapRows = new List<Row>(); readonly List<Transform> snapObjs = new List<Transform>();
    SpFileJ span; ZgJ zg; Dictionary<string, LRow> layoutRows;
    public static string LastReport = ""; public static int LastAdded, LastMoved, LastDisabled, LastRejected, LastSkipped;

    static string FindFile()
    {
        foreach (var p in new[] { System.IO.Path.Combine(Application.streamingAssetsPath, FileName), System.IO.Path.Combine(Application.dataPath, "Data/" + FileName) }) if (System.IO.File.Exists(p)) return p;
        return null;
    }
    static Cfg ReadCfg(string p)
    {
        try
        {
            var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p));
            if (k != null) { if (k.rows == null) k.rows = new Row[0]; if (k.apply == null) k.apply = new ApplyCfg(); if (k.snap == null) k.snap = new SnapCfg(); if (k.clearance == null) k.clearance = new ClearCfg(); }
            return k;
        }
        catch (Exception e) { Debug.LogWarning("[PropsOverlay] " + p + ": " + e.Message); return null; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(ArgPrefix, StringComparison.Ordinal)) a1 = a.Substring(ArgPrefix.Length);
            string p = FindFile(); if (p == null) return;
            var cfg = ReadCfg(p); if (cfg == null) return;
            bool on = a1 != null ? a1 == "1" : cfg.enabled; if (!on) return;
            if (FindFirstObjectByType<PropsOverlay>() != null) return;
            var go = new GameObject("PropsOverlay"); DontDestroyOnLoad(go); var po = go.AddComponent<PropsOverlay>(); po.c = cfg; po.srcPath = p; po.srcTime = System.IO.File.GetLastWriteTimeUtc(p);
        }
        catch (Exception e) { Debug.LogWarning("PROPSOVERLAY boot failed: " + e.Message); }
    }

    void Start()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => { applied = false; waiting = false; index.Clear(); groups.Clear(); clones.Clear(); saved.Clear(); snapRows.Clear(); snapObjs.Clear(); };
    }

    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(c.apply.minPollS, c.apply.pollSeconds);
        try
        {
            if (!applied)
            {
                if (!waiting) { waiting = true; waitStart = Time.unscaledTime; frames = 0; }
                frames++; BuildIndex();
                bool ready = index.Count > 0 && frames >= c.apply.delayFrames;
                if (!ready && Time.unscaledTime - waitStart > c.apply.waitRootsS) { Debug.LogWarning("[PropsOverlay] no baked props found after " + c.apply.waitRootsS + " s: overlay not applied"); applied = true; return; }
                if (!ready) return;
                ApplyAll(); applied = true; nextWatch = Time.unscaledTime + c.apply.watchFileS;
            }
            else
            {
                PollSnap();
                if (c.apply.watchFileS > 0f && Time.unscaledTime >= nextWatch) { nextWatch = Time.unscaledTime + c.apply.watchFileS; Watch(); }
            }
        }
        catch (Exception e) { Debug.LogWarning("PROPSOVERLAY failed: " + e.Message); applied = true; }
    }

    void Watch()
    {
        if (srcPath == null || !System.IO.File.Exists(srcPath)) return;
        var t = System.IO.File.GetLastWriteTimeUtc(srcPath); if (t == srcTime) return; srcTime = t;
        var n = ReadCfg(srcPath); if (n == null) return;
        Revert(); c = n; layoutRows = null; applied = false; waiting = false; Debug.Log("[PropsOverlay] file changed: reverted, re-applying");
    }

    void Revert()
    {
        foreach (var t in clones) if (t != null) Destroy(t.gameObject);
        foreach (var kv in saved) { var s = kv.Value; if (s.t == null) continue; s.t.position = s.p; s.t.rotation = s.r; s.t.localScale = s.s; s.t.gameObject.SetActive(s.active); }
        clones.Clear(); saved.Clear(); snapRows.Clear(); snapObjs.Clear();
    }

    void BuildIndex()
    {
        index.Clear(); groups.Clear();
        var roots = new List<GameObject>(); UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects(roots);
        foreach (var r in roots)
        {
            if (r == null || r.name == "PropsOverlay") continue;
            foreach (Transform g in r.transform) { groups.Add(g); foreach (Transform w in g) if (!index.ContainsKey(w.name)) index[w.name] = w; }
        }
    }

    static float[] Vec3(float[] a) { return a != null && a.Length >= 3 ? a : null; }

    void ApplyAll()
    {
        LastAdded = LastMoved = LastDisabled = LastRejected = LastSkipped = 0; var rep = new System.Text.StringBuilder();
        if (zg == null) LoadZg();
        foreach (var r in c.rows)
        {
            if (r == null || !r.enabled || string.IsNullOrEmpty(r.id)) { LastSkipped++; continue; }
            string res;
            try { res = ApplyRow(r); } catch (Exception e) { res = "ERROR " + e.Message; }
            rep.AppendLine(r.op + " " + r.id + ": " + res);
            if (res != "ok") Debug.LogWarning("[PropsOverlay] " + r.op + " " + r.id + ": " + res);
        }
        LastReport = rep.ToString();
        if (c.rows.Length > 0) Debug.Log("PROPSOVERLAY applied: " + LastAdded + " added, " + LastMoved + " moved, " + LastDisabled + " disabled, " + LastRejected + " rejected, " + LastSkipped + " off/empty, " + index.Count + " baked wrappers indexed");
#if !FRANKEN_NO_HARNESS
        PropsOverlayProof.Write(LastReport, LastAdded, LastMoved, LastDisabled, LastRejected);
#endif
    }

    string ApplyRow(Row r)
    {
        string op = (r.op ?? "add").ToLowerInvariant();
        if (op == "disable")
        {
            if (!index.TryGetValue(r.id, out var d)) return "skipped: no baked prop named " + r.id;
            Remember(d); d.gameObject.SetActive(false); LastDisabled++; return "ok";
        }
        float[] pos = Vec3(r.pos);
        if (pos != null && !ClearanceOk(pos[0], pos[2], out var why)) { LastRejected++; return "rejected: " + why; }
        Transform obj;
        if (op == "add")
        {
            if (string.IsNullOrEmpty(r.src) || !index.TryGetValue(r.src, out var src)) return "skipped: src '" + r.src + "' is not a baked prop of this build";
            if (index.ContainsKey(r.id)) return "skipped: id already exists (use op move)";
            if (pos == null) return "skipped: add needs pos";
            var parent = ParentFor(r, src); if (parent == null) return "skipped: zone/ring '" + r.zone + "' not found";
            var go = Instantiate(src.gameObject, parent, true); go.name = r.id; go.SetActive(true); obj = go.transform; clones.Add(obj); index[r.id] = obj;
            Place(obj, src, r, pos, true); LastAdded++;
        }
        else if (op == "move")
        {
            if (!index.TryGetValue(r.id, out obj)) return "skipped: no baked prop named " + r.id;
            Remember(obj); Place(obj, obj, r, pos, false); LastMoved++;
        }
        else return "skipped: unknown op " + r.op;
        if (r.lod >= 0) { var lg = obj.GetComponent<LODGroup>(); if (lg != null) lg.ForceLOD(Mathf.Min(r.lod, Mathf.Max(0, lg.lodCount - 1))); }
        return "ok";
    }

    void Remember(Transform t) { if (!saved.ContainsKey(t)) saved[t] = new Saved { t = t, p = t.position, r = t.rotation, s = t.localScale, active = t.gameObject.activeSelf }; }

    Transform ParentFor(Row r, Transform src)
    {
        if (string.IsNullOrEmpty(r.zone)) return src.parent;
        var roots = new List<GameObject>(); UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects(roots);
        if (r.zone == "main")
        {
            foreach (var g in roots) if (g.name == c.mainRoot) return string.IsNullOrEmpty(r.group) ? src.parent : g.transform.Find(r.group);
            return null;
        }
        if (span == null) { span = new SpFileJ(); var txt = DataPath.ReadText("zone_span.json"); if (txt != null) { try { span = JsonUtility.FromJson<SpFileJ>(txt) ?? span; } catch { } } }
        if (span.zones == null) return null;
        foreach (var z in span.zones)
        {
            if (z == null || z.id != r.zone || z.rings == null || z.rings.Length == 0) continue;
            string grp = z.rings[Mathf.Clamp(r.ring, 0, z.rings.Length - 1)].group;
            foreach (var g in roots) if (g.name == z.root) return g.transform.Find(grp);
        }
        return null;
    }

    void LoadZg()
    {
        zg = new ZgJ(); var txt = DataPath.ReadText("zone_ground.json"); if (txt == null) return;
        try { var k = JsonUtility.FromJson<ZgJ>(txt); if (k != null) zg = k; } catch { }
    }

    float LayoutScale(string id)
    {
        if (layoutRows == null)
        {
            layoutRows = new Dictionary<string, LRow>();
            foreach (var f in c.layouts ?? new string[0])
            {
                var txt = DataPath.ReadText(f); if (txt == null) continue;
                try { var lf = JsonUtility.FromJson<LFile>(txt); if (lf != null && lf.props != null) foreach (var p in lf.props) if (p != null && !string.IsNullOrEmpty(p.id) && !layoutRows.ContainsKey(p.id)) layoutRows[p.id] = p; } catch { }
            }
        }
        return layoutRows.TryGetValue(id, out var row) && row.scale > 0f ? row.scale : -1f;
    }

    void Place(Transform t, Transform src, Row r, float[] pos, bool isClone)
    {
        var rot = Vec3(r.rotEuler); if (rot != null) t.rotation = Quaternion.Euler(rot[0], rot[1], rot[2]);   // rotation and scale first: the bottom is measured with them
        Vector3 ls = t.localScale;
        if (r.scale > 0f)
        {
            float m = r.scale;
            if (c.scaleMode == "absolute") { float b = LayoutScale(src.name); if (b > 0f) m = r.scale / b; else Debug.LogWarning("[PropsOverlay] " + r.id + ": no layout row for " + src.name + ", scale taken as relative"); }
            ls = (isClone ? src.localScale : t.localScale) * m;
        }
        if (r.scaleYMul > 0f) ls.y *= r.scaleYMul;
        t.localScale = ls;
        if (pos == null) return;
        string g = string.IsNullOrEmpty(r.ground) ? "bottom" : r.ground;
        t.position = new Vector3(pos[0], pos[1], pos[2]);
        if (g == "pivot") return;
        if (g == "snap") { snapRows.Add(r); snapObjs.Add(t); return; }   // y is finished by PollSnap once the ring is awake
        if (BottomY(t, out float bot)) t.position += new Vector3(0f, pos[1] - bot, 0f);
    }

    // world y of the lowest LOD0 mesh bound corner (mesh bounds, not Renderer.bounds: valid under an inactive ring)
    static bool BottomY(Transform t, out float y)
    {
        y = 0f; var lg = t.GetComponent<LODGroup>(); Renderer[] rs = null;
        if (lg != null) { var l = lg.GetLODs(); if (l != null && l.Length > 0) rs = l[0].renderers; }
        if (rs == null || rs.Length == 0) rs = t.GetComponentsInChildren<Renderer>(true);
        bool any = false; float mn = float.MaxValue;
        foreach (var rd in rs)
        {
            if (rd == null) continue; Mesh m = null;
            var mf = rd.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; else { var sk = rd as SkinnedMeshRenderer; if (sk != null) m = sk.sharedMesh; }
            if (m == null) continue; var b = m.bounds; var M = rd.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                float wy = M.MultiplyPoint3x4(corner).y; if (wy < mn) mn = wy; any = true;
            }
        }
        if (any) y = mn; return any;
    }

    // play_areas.json: area (disc/rect) grown by clearM, and the lane band, in the zone whose z range holds the row
    bool ClearanceOk(float x, float z, out string why)
    {
        why = null; if (!c.clearance.enabled) return true;
        var row = PlayArea.Row(z); if (row == null) return true; float cm = c.clearance.clearM;
        if (c.clearance.lane && Mathf.Abs(x - row.laneX) <= row.laneHalfX + cm) { why = "inside the lane of play area " + row.zone + " (|x-" + row.laneX + "| <= " + (row.laneHalfX + cm) + ")"; return false; }
        if (row.centre != null && row.centre.Length >= 2)
        {
            if (row.shape == "rect") { if (row.half != null && row.half.Length >= 2 && Mathf.Abs(x - row.centre[0]) <= row.half[0] + cm && Mathf.Abs(z - row.centre[1]) <= row.half[1] + cm) { why = "inside the fight area (rect) of " + row.zone; return false; } }
            else { float dx = x - row.centre[0], dz = z - row.centre[1], rr = row.radius + cm; if (dx * dx + dz * dz <= rr * rr) { why = "inside the fight area (disc) of " + row.zone; return false; } }
        }
        return true;
    }

    void PollSnap()
    {
        for (int i = snapObjs.Count - 1; i >= 0; i--)
        {
            var t = snapObjs[i]; if (t == null) { snapObjs.RemoveAt(i); snapRows.RemoveAt(i); continue; }
            if (!t.gameObject.activeInHierarchy) continue;      // ring asleep: its colliders are off too
            var r = snapRows[i]; snapObjs.RemoveAt(i); snapRows.RemoveAt(i); Snap(t, r);
        }
    }

    void Snap(Transform t, Row r)
    {
        if (zg == null) LoadZg();
        float up = c.snap.rayUpM > 0f ? c.snap.rayUpM : (zg.rayUpM > 0f ? zg.rayUpM : 3f), down = c.snap.rayDownM > 0f ? c.snap.rayDownM : (zg.rayDownM > 0f ? zg.rayDownM : 12f), sink = c.snap.sinkM >= 0f ? c.snap.sinkM : zg.sinkM;
        var p = t.position; var o = new Vector3(p.x, p.y + up, p.z);
        var hits = Physics.RaycastAll(o, Vector3.down, up + down, ~0, QueryTriggerInteraction.Ignore); Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            var ht = h.collider.transform; if (ht == t || ht.IsChildOf(t) || IsPropWrapper(ht)) continue;
            if (h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            float off = r.pos != null && r.pos.Length >= 3 ? r.pos[1] : 0f;
            t.position = new Vector3(p.x, h.point.y, p.z);
            if (BottomY(t, out float bot)) t.position += new Vector3(0f, -sink + off - (bot - h.point.y), 0f); else t.position = new Vector3(p.x, h.point.y - sink + off, p.z);
            Debug.Log("PROPSOVERLAY snap " + t.name + " -> y " + t.position.y.ToString("F2") + " (" + ht.name + ")"); return;
        }
        Debug.LogWarning("[PropsOverlay] snap " + t.name + ": no ground under it, y stays");
    }

    bool IsPropWrapper(Transform h)
    {
        for (int i = 0; i < 6 && h != null; i++) { if (h.parent != null && groups.Contains(h.parent) && h.GetComponent<LODGroup>() != null) return true; h = h.parent; }
        return false;
    }
}

#if !FRANKEN_NO_HARNESS
// proof harness: -pooverlayout:<file> writes the apply report once per apply (no behaviour change)
public static class PropsOverlayProof
{
    public static void Write(string report, int added, int moved, int disabled, int rejected)
    {
        try
        {
            string path = null; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-pooverlayout:", StringComparison.Ordinal)) path = a.Substring("-pooverlayout:".Length);
            if (path == null) return;
            System.IO.File.WriteAllText(path, "added " + added + " moved " + moved + " disabled " + disabled + " rejected " + rejected + "\n" + report);
        }
        catch (Exception e) { Debug.LogWarning("[PropsOverlayProof] " + e.Message); }
    }
}
#endif
