using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ZONEGROUNDMAT v1 (laneVIS_ground). NEW FILE, nothing else calls it. CODE kit (about 90 s build), then every look change is HOT data (zone_ground_mat.json + StreamingAssets\groundmat\*.png).
// OFF by default: zone_ground_mat.json enabled=false (or no file) => Boot returns, nothing is allocated. Arg -zgm:1|0 forces it on / off.
// Why: the ground (about 60 percent of every frame) is ONE material family (M_WorldTerrain / ground_paving plate_top, HDRP/Lit) with ONE neutral grey albedo (sRGB 42-45, R=G=B, luminance std 6 of 255)
// and a strong crack normal map, in all 7 zones. Decals (ENV2D / GROUNDID / LOOK10) sit on top at sRGB 47-65 = +5..+20 over the slab, so they do not read. This pass gives each zone its own ground MATERIAL.
// What it does: under each zoneRoot (inactive roots included, cached by name) the renderers whose material name contains matchMaterial (and not excludeMaterial; " (vismat)" / " (lookopt)" / " (Instance)" suffixes ignored)
// get ONE shared clone per (row, source material) with: ABSOLUTE _BaseColor = tint (mix blends from 1 in log space), smoothness remap min/max, _NormalScale, _DetailAlbedoScale / _DetailNormalScale / _DetailSmoothnessScale
// (the _DETAIL_MAP keyword is already on both ground materials, so no new shader variant), tiling multipliers (relative to the material's own _BaseColorMap_ST / _DetailMap_ST: UV layouts differ per zone) and optional
// texture swaps (StreamingAssets\<texDir>\<file>: albedo sRGB, normal + mask linear; mask = R metal, G AO, B detail mask, A smoothness, HDRP layout). Renderers are processed once; no per-frame work.
// Log: "ZONEGROUNDMAT <file>: <n> renderers, <m> clones" and one "ZONEGROUNDMAT scan <zoneRoot>: <material names>" line per root (diagnostic: shows the real material names the first time, a row with 0 matches logs a WARN).
// UNPROVEN IN UNITY: compiled with Roslyn only.
public sealed class ZoneGroundMat : MonoBehaviour
{
    [Serializable] public class Row
    {
        public string id, zoneRoot, targetRoot = ""; public string[] unlessActive; public float zMin = -1e9f, zMax = 1e9f; public string[] matchMaterial, excludeMaterial; public bool enabled = true; public float mix = 1f;
        public float[] tint; public float smoothMin = -1f, smoothMax = -1f, normalScale = -1f, detailAlbedo = -1f, detailNormal = -1f, detailSmooth = -1f, detailTilingMul = 1f, tilingMul = 1f;
        public string baseColorMap = "", normalMap = "", maskMap = "";
    }
    [Serializable] public class Cfg { public bool enabled = false, log = true; public float rescanSeconds = 3f, settledRescanSeconds = 20f; public int maxPerPoll = 60; public string texDir = "groundmat", cloneSuffix = " (zgm)"; public Row[] rows; }

    const string File_ = "zone_ground_mat.json", ArgPrefix = "-zgm:";
    static readonly string[] Suffixes = { " (Instance)", " (vismat)", " (lookopt)", " (zgm)" };
    Cfg c; float nextScan; int settled;
    readonly HashSet<(Renderer, Row)> done = new HashSet<(Renderer, Row)>();
    readonly Dictionary<(Row, string), Material> clones = new Dictionary<(Row, string), Material>();
    readonly Dictionary<string, Texture2D> tex = new Dictionary<string, Texture2D>();
    readonly Dictionary<string, GameObject> roots = new Dictionary<string, GameObject>();
    readonly Dictionary<Row, int> matches = new Dictionary<Row, int>();
    readonly HashSet<string> scanLogged = new HashSet<string>();
    readonly Dictionary<Renderer, Material[]> orig = new Dictionary<Renderer, Material[]>();
    readonly HashSet<(Renderer, Row)> changed = new HashSet<(Renderer, Row)>();
    int touched; bool logged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(ArgPrefix)) a1 = a.Substring(ArgPrefix.Length);
            Cfg cfg = null;
            foreach (var p in DataPath.Candidates(File_))
            {
                if (!File.Exists(p)) continue;
                try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); } catch (Exception e) { Debug.LogWarning("[ZoneGroundMat] " + p + ": " + e.Message); }
                break;
            }
            if (cfg == null || cfg.rows == null || cfg.rows.Length == 0) return;
            bool on = a1 != null ? a1 == "1" : cfg.enabled; if (!on) return;
            if (FindFirstObjectByType<ZoneGroundMat>() != null) return;
            var go = new GameObject("ZoneGroundMat"); DontDestroyOnLoad(go); go.AddComponent<ZoneGroundMat>().c = cfg;
        }
        catch (Exception e) { Debug.LogWarning("ZONEGROUNDMAT boot failed: " + e.Message); }
    }

    void Start() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => { done.Clear(); changed.Clear(); orig.Clear(); clones.Clear(); roots.Clear(); matches.Clear(); scanLogged.Clear(); settled = 0; touched = 0; logged = false; nextScan = 0f; }; }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + Mathf.Max(0.25f, settled >= 3 ? c.settledRescanSeconds : c.rescanSeconds);
        try { int before = touched; Scan(); settled = touched == before ? settled + 1 : 0; }
        catch (Exception e) { Debug.LogWarning("ZONEGROUNDMAT scan failed: " + e.Message); }
    }

    GameObject Root(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        GameObject g; if (roots.TryGetValue(name, out g) && g != null) return g;
        var all = Resources.FindObjectsOfTypeAll<GameObject>();   // includes inactive zone roots (GameObject.Find skips them); scene objects only
        for (int i = 0; i < all.Length; i++) { var o = all[i]; if (o != null && o.name == name && o.scene.IsValid() && o.transform.parent == null) { roots[name] = o; return o; } }
        return null;
    }

    bool RootActive(string n) { var g = Root(n); return g != null && g.activeInHierarchy; }
    // gated row (targetRoot set, e.g. the shared WorldTerrain): applies only while its zone root is active (and none of unlessActive); otherwise the original materials are restored
    bool Gate(Row row)
    {
        if (string.IsNullOrEmpty(row.targetRoot)) return true;
        if (row.zMin > -1e8f || row.zMax < 1e8f) { var cam = Camera.main; if (cam == null) return false; float cz = cam.transform.position.z; return cz >= row.zMin && cz <= row.zMax; }   // all zone roots are always active in the scene: gate by the camera z range instead
        if (!RootActive(row.zoneRoot)) return false;
        if (row.unlessActive != null) foreach (var u in row.unlessActive) if (!string.IsNullOrEmpty(u) && RootActive(u)) return false;
        return true;
    }
    void Revert(Row row)
    {
        List<(Renderer, Row)> mine = null;
        foreach (var k in changed) if (k.Item2 == row) { if (mine == null) mine = new List<(Renderer, Row)>(); mine.Add(k); }
        if (mine == null) return;
        foreach (var k in mine) { Material[] o; if (k.Item1 != null && orig.TryGetValue(k.Item1, out o)) k.Item1.sharedMaterials = o; changed.Remove(k); done.Remove(k); }
        done.RemoveWhere(k => k.Item2 == row); settled = 0;
    }

    static string Base(string n) { bool ch = true; while (ch) { ch = false; foreach (var s in Suffixes) if (n.EndsWith(s)) { n = n.Substring(0, n.Length - s.Length); ch = true; } } return n; }

    static bool Match(Row r, string name)
    {
        string n = Base(name);
        if (r.excludeMaterial != null) foreach (var x in r.excludeMaterial) if (!string.IsNullOrEmpty(x) && n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (r.matchMaterial == null || r.matchMaterial.Length == 0) return true;
        foreach (var x in r.matchMaterial) if (!string.IsNullOrEmpty(x) && n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    void Scan()
    {
        int budget = Mathf.Max(1, c.maxPerPoll);
        foreach (var row in c.rows) if (row != null && row.enabled && !string.IsNullOrEmpty(row.targetRoot) && !Gate(row)) Revert(row);   // reverts first, so a new zone row never gets overwritten
        foreach (var row in c.rows)
        {
            if (row == null || !row.enabled) continue;
            if (!Gate(row)) continue;
            var g = Root(string.IsNullOrEmpty(row.targetRoot) ? row.zoneRoot : row.targetRoot); if (g == null) continue;
            var rs = g.GetComponentsInChildren<Renderer>(true);
            var seen = new HashSet<string>();
            foreach (var r in rs)
            {
                if (r == null) continue; var key = (r, row);
                var arr = r.sharedMaterials; bool ch = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i]; if (m == null) continue; string bn = Base(m.name);
                    if (c.log && (r.gameObject.name.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0 || bn.IndexOf("terrain", StringComparison.OrdinalIgnoreCase) >= 0 || bn.IndexOf("paving", StringComparison.OrdinalIgnoreCase) >= 0)) seen.Add(bn + "[" + (m.shader != null ? m.shader.name : "?") + "]");
                    if (done.Contains(key) || budget <= 0 || !Match(row, m.name)) continue;
                    if (!m.HasProperty("_BaseColor")) continue;   // not an HDRP/Lit-style material: left alone
                    Material cl; var ck = (row, bn);
                    if (!clones.TryGetValue(ck, out cl) || cl == null) { cl = Make(row, m); clones[ck] = cl; }
                    if (cl != null && cl != m) { arr[i] = cl; ch = true; int n; matches.TryGetValue(row, out n); matches[row] = n + 1; }
                }
                if (!done.Contains(key) && budget > 0) { done.Add(key); if (ch) { if (!orig.ContainsKey(r)) orig[r] = r.sharedMaterials; r.sharedMaterials = arr; changed.Add(key); touched++; budget--; } }
            }
            if (c.log && scanLogged.Add(row.id)) Debug.Log("ZONEGROUNDMAT scan " + row.id + " (" + (string.IsNullOrEmpty(row.targetRoot) ? row.zoneRoot : row.targetRoot) + "): " + rs.Length + " renderers; ground-like materials: " + string.Join(", ", new List<string>(seen).ToArray()));
        }
        if (c.log && !logged && touched > 0) { logged = true; Debug.Log("ZONEGROUNDMAT " + File_ + ": " + touched + " renderers, " + clones.Count + " material clones"); }
        if (c.log) foreach (var row in c.rows) if (row != null && row.enabled && Root(row.zoneRoot) != null && !matches.ContainsKey(row) && settled == 2) Debug.LogWarning("ZONEGROUNDMAT WARN row " + row.id + ": root " + row.zoneRoot + " found but 0 material slots matched (see the scan line for the real names)");
    }

    Material Make(Row row, Material src)
    {
        var m = new Material(src) { name = Base(src.name) + c.cloneSuffix, hideFlags = HideFlags.DontUnloadUnusedAsset };
        float mix = Mathf.Clamp01(row.mix);
        if (row.tint != null && row.tint.Length >= 3)
        {
            var o = Color.white; if (row.tint.Length >= 3) o = new Color(Mathf.Pow(Mathf.Max(1e-4f, row.tint[0]), mix), Mathf.Pow(Mathf.Max(1e-4f, row.tint[1]), mix), Mathf.Pow(Mathf.Max(1e-4f, row.tint[2]), mix), 1f);
            m.SetColor("_BaseColor", o);   // ABSOLUTE: the ground assets are _BaseColor 1,1,1; this also replaces any earlier multiply on the clone we started from
            if (m.HasProperty("_Color")) m.SetColor("_Color", o);
        }
        if (m.HasProperty("_SmoothnessRemapMin") && row.smoothMin >= 0f) m.SetFloat("_SmoothnessRemapMin", row.smoothMin);
        if (m.HasProperty("_SmoothnessRemapMax") && row.smoothMax >= 0f) m.SetFloat("_SmoothnessRemapMax", row.smoothMax);
        if (m.HasProperty("_NormalScale") && row.normalScale >= 0f) m.SetFloat("_NormalScale", row.normalScale);
        if (m.HasProperty("_DetailAlbedoScale") && row.detailAlbedo >= 0f) m.SetFloat("_DetailAlbedoScale", row.detailAlbedo);
        if (m.HasProperty("_DetailNormalScale") && row.detailNormal >= 0f) m.SetFloat("_DetailNormalScale", row.detailNormal);
        if (m.HasProperty("_DetailSmoothnessScale") && row.detailSmooth >= 0f) m.SetFloat("_DetailSmoothnessScale", row.detailSmooth);
        if (m.HasProperty("_BaseColorMap") && Mathf.Abs(row.tilingMul - 1f) > 1e-4f) { float k = Mathf.Pow(Mathf.Max(0.01f, row.tilingMul), mix); var s = m.GetTextureScale("_BaseColorMap"); m.SetTextureScale("_BaseColorMap", s * k); }
        if (m.HasProperty("_DetailMap") && Mathf.Abs(row.detailTilingMul - 1f) > 1e-4f) { var s = m.GetTextureScale("_DetailMap"); m.SetTextureScale("_DetailMap", s * Mathf.Max(0.01f, row.detailTilingMul)); }
        Swap(m, "_BaseColorMap", row.baseColorMap, false, ""); Swap(m, "_NormalMap", row.normalMap, true, "_NORMALMAP"); Swap(m, "_MaskMap", row.maskMap, true, "_MASKMAP");
        return m;
    }

    void Swap(Material m, string prop, string file, bool linear, string keyword)
    {
        if (string.IsNullOrEmpty(file) || !m.HasProperty(prop)) return;
        string k = file + (linear ? "|lin" : "|srgb"); Texture2D t;
        if (!tex.TryGetValue(k, out t) || t == null)
        {
            string p = Path.Combine(Path.Combine(Application.streamingAssetsPath, c.texDir), file);
            if (!File.Exists(p)) { Debug.LogWarning("ZONEGROUNDMAT missing texture " + p); return; }
            try
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "zgm_" + file, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
                if (!t.LoadImage(File.ReadAllBytes(p), false)) { Destroy(t); return; }
                tex[k] = t;
            }
            catch (Exception e) { Debug.LogWarning("ZONEGROUNDMAT texture " + p + ": " + e.Message); return; }
        }
        m.SetTexture(prop, t);
        if (keyword == "_NORMALMAP" || keyword == "_MASKMAP") m.EnableKeyword(keyword);   // both are already on for the ground materials; harmless when so
    }
}
