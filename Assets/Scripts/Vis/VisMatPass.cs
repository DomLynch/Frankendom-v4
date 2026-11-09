using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// VIS3 v1 (laneVIS3, items 12 wetness-on-props, 13 grime on clean props, and a per-zone tint of the paving strips). NEW FILE, nothing else calls it.
// OFF by default: each data file (wetness.json, prop_grime.json) has enabled=false; with both off, or no file, Boot returns and nothing is allocated. Args: -vismat:1|0 (forces both files on / off).
// What it does: for each enabled file, for each row, the renderers under the zone root (GameObject.Find(zoneRoot), "" = whole scene) whose material name contains one of matchMaterial (and none of excludeMaterial) get a
// SHARED CLONE of that material (one clone per source material per row, assigned through sharedMaterials: no per-renderer instance, SRP-batcher friendly) with:
//   baseColorMul (multiplies the original _BaseColor, 1 = leave: this is the grime / wet-darkening colour multiply, no new texture, no new shader keyword, so no shader variant that the player does not already contain),
//   smoothnessMinFloor / smoothnessMaxFloor (the remap min / max are RAISED to at least this, never lowered; -1 = leave) and smoothnessMaxCap (lowers the max, for dry dirt),
//   metallicMul. Material property names come from data (props).
// Only HDRP/Lit-style materials are touched (the material must own every property the row writes). Scans: every rescanSeconds for rows whose root is found; a renderer is processed once. No per-frame work.
// Look rules: grime = colour multiply close to 1 (0.7-0.9), never a restyle; wet = a darker, glossier version of the same material, only under the root named in the row (z2).
// Data: StreamingAssets/<file> (then Assets/Data). UNPROVEN IN UNITY: compiled with Roslyn only. A detail-map grime (texture pattern) would need a Editor-time material edit so the _DETAIL_MAP variant ships: see MANIFEST item 13 route B.
public sealed class VisMatPass : MonoBehaviour
{
    [Serializable] public class Row
    {
        public string id, zoneRoot; public string[] matchMaterial, excludeMaterial; public float[] baseColorMul;
        public float smoothnessMinFloor = -1f, smoothnessMaxFloor = -1f, smoothnessMaxCap = -1f, metallicMul = -1f; public bool enabled = true;
    }
    [Serializable] public class Props { public string[] baseColor = { "_BaseColor" }, smoothMin = { "_SmoothnessRemapMin" }, smoothMax = { "_SmoothnessRemapMax" }, metallic = { "_Metallic" }, metallicMax = { "_MetallicRemapMax" }; }
    [Serializable] public class Cfg { public bool enabled = false, log = true; public float pollSeconds = 1f, rescanSeconds = 10f, minPollS = 0.25f; public int maxPerPoll = 40; public string cloneSuffix = " (vismat)", instanceSuffix = " (Instance)"; public Props props = new Props(); public Row[] rows; }

    static readonly string[] Files = { "wetness.json", "prop_grime.json" };
    const string ArgPrefix = "-vismat:", DataDir = "Data";
    class FileState { public string file; public Cfg c; public float nextScan; public readonly HashSet<(Renderer, Row)> done = new HashSet<(Renderer, Row)>(); public readonly Dictionary<(Row, Material), Material> clones = new Dictionary<(Row, Material), Material>(); public int touched; public bool logged; }

    readonly List<FileState> fs = new List<FileState>(); float nextPoll;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null;
            foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(ArgPrefix)) a1 = a.Substring(ArgPrefix.Length);
            var list = new List<FileState>();
            foreach (var f in Files)
            {
                Cfg cfg = null;
                foreach (var p in DataPath.Candidates(f))
                {
                    if (!File.Exists(p)) continue;
                    try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); } catch (Exception e) { Debug.LogWarning("[VisMatPass] " + p + ": " + e.Message); }
                    break;
                }
                if (cfg == null || cfg.rows == null || cfg.rows.Length == 0) continue;
                bool on = a1 != null ? a1 == "1" : cfg.enabled; if (!on) continue;
                list.Add(new FileState { file = f, c = cfg });
            }
            if (list.Count == 0 || FindFirstObjectByType<VisMatPass>() != null) return;
            var go = new GameObject("VisMatPass"); DontDestroyOnLoad(go); var v = go.AddComponent<VisMatPass>(); v.fs.AddRange(list);
        }
        catch (Exception e) { Debug.LogWarning("VISMAT boot failed: " + e.Message); }
    }

    void Start() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => { foreach (var f in fs) { f.done.Clear(); f.clones.Clear(); f.nextScan = 0f; f.touched = 0; f.logged = false; } }; }

    void Update()
    {
        float mp = 1f; foreach (var f in fs) mp = Mathf.Min(mp, Mathf.Max(f.c.minPollS, f.c.pollSeconds));
        if (Time.unscaledTime < nextPoll) return; nextPoll = Time.unscaledTime + mp;
        try { foreach (var f in fs) if (Time.unscaledTime >= f.nextScan) { f.nextScan = Time.unscaledTime + Mathf.Max(f.c.minPollS, f.c.rescanSeconds); Scan(f); } }
        catch (Exception e) { Debug.LogWarning("VISMAT scan failed: " + e.Message); }
    }

    void Scan(FileState f)
    {
        int budget = Mathf.Max(1, f.c.maxPerPoll);
        foreach (var row in f.c.rows)
        {
            if (row == null || !row.enabled) continue;
            Transform root = null; if (!string.IsNullOrEmpty(row.zoneRoot)) { var g = GameObject.Find(row.zoneRoot); if (g == null) continue; root = g.transform; }
            var rs = root != null ? root.GetComponentsInChildren<Renderer>(true) : FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var r in rs)
            {
                if (r == null || budget <= 0) continue; var key = (r, row); if (f.done.Contains(key)) continue;
                var arr = r.sharedMaterials; bool ch = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i]; if (m == null || !Match(row, m.name, f.c.instanceSuffix)) continue;
                    var ck = (row, m); Material cl;
                    if (!f.clones.TryGetValue(ck, out cl) || cl == null) { cl = Clone(f, row, m); f.clones[ck] = cl; }
                    if (cl != null && cl != m) { arr[i] = cl; ch = true; }
                }
                f.done.Add(key); if (ch) { r.sharedMaterials = arr; f.touched++; budget--; }
            }
        }
        if (f.c.log && !f.logged && f.touched > 0) { f.logged = true; Debug.Log("VISMAT " + f.file + ": " + f.touched + " renderers, " + f.clones.Count + " material clones"); }
    }

    static bool Match(Row r, string name, string inst)
    {
        string n = !string.IsNullOrEmpty(inst) && name.EndsWith(inst) ? name.Substring(0, name.Length - inst.Length) : name;
        if (r.excludeMaterial != null) foreach (var x in r.excludeMaterial) if (!string.IsNullOrEmpty(x) && n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (r.matchMaterial == null || r.matchMaterial.Length == 0) return true;
        foreach (var x in r.matchMaterial) if (string.IsNullOrEmpty(x) || n.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static string Pick(Material m, string[] names) { if (names == null) return null; foreach (var n in names) if (!string.IsNullOrEmpty(n) && m.HasProperty(n)) return n; return null; }

    Material Clone(FileState f, Row row, Material src)
    {
        var P = f.c.props; string bc = Pick(src, P.baseColor), smin = Pick(src, P.smoothMin), smax = Pick(src, P.smoothMax), met = Pick(src, P.metallic), metMax = Pick(src, P.metallicMax);
        bool wantsColor = row.baseColorMul != null && row.baseColorMul.Length >= 3, wantsSmooth = row.smoothnessMinFloor >= 0f || row.smoothnessMaxFloor >= 0f || row.smoothnessMaxCap >= 0f, wantsMetal = row.metallicMul >= 0f;
        if ((wantsColor && bc == null) || (wantsSmooth && smax == null)) return null;   // not an HDRP/Lit-style material: left alone
        var m = new Material(src) { name = src.name + f.c.cloneSuffix, hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (wantsColor) { var o = src.GetColor(bc); m.SetColor(bc, new Color(o.r * row.baseColorMul[0], o.g * row.baseColorMul[1], o.b * row.baseColorMul[2], o.a)); }
        if (wantsSmooth)
        {
            if (smin != null && row.smoothnessMinFloor >= 0f) m.SetFloat(smin, Mathf.Max(src.GetFloat(smin), row.smoothnessMinFloor));
            if (row.smoothnessMaxFloor >= 0f) m.SetFloat(smax, Mathf.Max(src.GetFloat(smax), row.smoothnessMaxFloor));
            if (row.smoothnessMaxCap >= 0f) m.SetFloat(smax, Mathf.Min(m.GetFloat(smax), row.smoothnessMaxCap));
        }
        if (wantsMetal) { if (met != null) m.SetFloat(met, src.GetFloat(met) * row.metallicMul); if (metMax != null) m.SetFloat(metMax, src.GetFloat(metMax) * row.metallicMul); }
        return m;
    }
}


