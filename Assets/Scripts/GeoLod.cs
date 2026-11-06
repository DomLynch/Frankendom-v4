using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// laneGEOLOD v104 (GEOLOD): LOD TRANSITION HEIGHTS BY DATA, DARK (geo_lod.json "enabled": false).
// Why: every placed prop already owns a 3-level LODGroup (PropsLayout.cs, heights from props_layout.json lodProfiles), but those heights are hand-picked per profile, not per asset.
//      Each asset has a measured geometric error per LOD (_agents\laneGEOLOD\lod_error.py). This switchboard replaces the transition heights of the LODGroups under the Arena
//      groups with heights DERIVED FROM THAT ERROR: LOD n is shown only while its worst geometric error projects to <= tolPx pixels at H px output height
//      (relHeight <= T_n = tolPx * size / (err_n * H)). Row t[0] = T_1 (LOD0 -> LOD1), t[1] = T_2 (LOD1 -> LOD2); the cull height (last LOD) is never touched.
// Modes (geo_lod.json "mode"): save_only = a group never switches LATER than today (new height = max(old, T x bias)); strict = the data heights exactly (can add detail where today's switch is too early).
// Bias: Unity switches when relHeight x lodBias < height. HDRP overwrites QualitySettings.lodBias per camera with the HDRP asset value (HDRP_Max lodBias 1, FromQualitySettings), so the effective
//       bias is "effectiveBias" (data, default 1.0) and height = T x effectiveBias. The runner logs the bias a camera really renders with (beginCameraRendering) so the data value can be checked.
// Data: geo_lod.json (StreamingAssets in the player, Assets/Data in the editor). Missing file = OFF. The sim never reads any of this; render-only; revert on destroy.
// Command line (same exe, same data, for A/B arms):  -geolod:off | -geolod:on | -geolod:<setId> (turns it on with that threshold set, e.g. tol1, tol05) | -geolodmode:save_only|strict | -geolodreport
public static class GeoLod
{
    [Serializable] public class Row { public string match = ""; public float[] t = new float[0]; }
    [Serializable] public class RowSet { public string id = ""; public string note = ""; public Row[] rows = new Row[0]; }
    [Serializable] public class Cfg
    {
        public string note = "";
        public bool enabled = false;
        public string mode = "save_only";          // save_only | strict
        public string active = "tol1";             // RowSet id
        public float effectiveBias = 1.0f;         // the lodBias the cameras really cull with (HDRP asset lodBias, see header)
        public string arenaRoot = "Arena";
        public string[] groups = new string[0];                 // children of arenaRoot whose children are the prop roots (LODGroup parents)
        public string[] excludeNameContains = new string[0];    // prop root names with one of these are never touched (the ground is LOCKED)
        public int startDelayFrames = 90;
        public float minGapHeight = 0.0005f;                    // transitions stay strictly decreasing by at least this
        public bool log = true, probeBias = true;
        public string[] disableWhenArgs = new string[0];
        public RowSet[] sets = new RowSet[0];
    }

    static Cfg cfg; static string argSet, argMode; static bool argOff, argOn, report;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; argSet = null; argMode = null; argOff = false; argOn = false; report = false; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new Cfg();
            var txt = HudCfg.ReadData("geo_lod.json");
            if (txt != null) { try { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } catch (Exception e) { Debug.LogWarning("[GeoLod] geo_lod.json parse failed, OFF: " + e.Message); cfg = new Cfg(); } }
            if (cfg.groups == null) cfg.groups = new string[0]; if (cfg.excludeNameContains == null) cfg.excludeNameContains = new string[0];
            if (cfg.disableWhenArgs == null) cfg.disableWhenArgs = new string[0]; if (cfg.sets == null) cfg.sets = new RowSet[0];
            ParseArgs();
            return cfg;
        }
    }
    static void ParseArgs()
    {
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("-geolodreport", StringComparison.OrdinalIgnoreCase)) report = true;
            else if (a.StartsWith("-geolodmode:", StringComparison.OrdinalIgnoreCase)) argMode = a.Substring(12);
            else if (a.StartsWith("-geolod:", StringComparison.OrdinalIgnoreCase))
            {
                string v = a.Substring(8);
                if (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("none", StringComparison.OrdinalIgnoreCase)) argOff = true;
                else if (v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("json", StringComparison.OrdinalIgnoreCase)) argOn = v.Equals("on", StringComparison.OrdinalIgnoreCase);
                else { argOn = true; argSet = v; }
            }
        }
    }
    public static bool On { get { var c = C; if (argOff) return false; if (argOn) return true; return c.enabled; } }
    public static string ActiveSet { get { return argSet ?? C.active; } }
    public static string Mode { get { return (argMode ?? C.mode ?? "save_only").ToLowerInvariant(); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Debug.Log("[GeoLod] " + (On ? "on set=" + ActiveSet + " mode=" + Mode + " bias=" + C.effectiveBias : "off"));
        if (!On) return;
        foreach (var a in Environment.GetCommandLineArgs()) foreach (var d in C.disableWhenArgs) if (!string.IsNullOrEmpty(d) && a.Equals(d, StringComparison.OrdinalIgnoreCase)) { Debug.Log("[GeoLod] disabled by argument " + d); return; }
        var go = new GameObject("GeoLod"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<GeoLodRunner>();
    }

    /// <summary>Pure rule, also mirrored by _agents\laneGEOLOD\geolod_model.py: new transition heights for a 3-LOD group. cur = today's heights, t = {T_1, T_2} (unbiased relHeight), bias = effective lodBias.</summary>
    public static float[] NewHeights(float[] cur, float[] t, float bias, bool saveOnly, float gap)
    {
        var h = (float[])cur.Clone();
        if (t == null || t.Length < 1 || cur.Length < 2) return h;
        float h0 = t[0] * bias; if (saveOnly) h0 = Mathf.Max(h0, cur[0]);
        if (cur.Length >= 3 && t.Length >= 2)
        {
            float h1 = t[1] * bias; if (saveOnly) h1 = Mathf.Max(h1, cur[1]);
            h1 = Mathf.Min(h1, h0 - gap); h1 = Mathf.Max(h1, cur[2] + gap);   // strictly between h0 and the cull height
            h0 = Mathf.Max(h0, h1 + gap); h[1] = h1;
        }
        else h0 = Mathf.Max(h0, cur[1] + gap);
        h[0] = h0;
        return h;
    }

    [DefaultExecutionOrder(1000)]
    public sealed class GeoLodRunner : MonoBehaviour
    {
        sealed class Saved { public LODGroup g; public float[] heights; }
        readonly List<Saved> saved = new List<Saved>();
        int frame, matched, changed, unmatched, skipped; bool applied;
        readonly HashSet<string> probed = new HashSet<string>();

        void OnEnable() { if (C.probeBias) RenderPipelineManager.beginCameraRendering += Probe; }
        void OnDisable() { RenderPipelineManager.beginCameraRendering -= Probe; }
        // beginCameraRendering runs inside HDRP's try-block after QualitySettings.SetLODSettings(frameSettings lodBias): the value read here is the one the camera culls with.
        void Probe(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || probed.Count >= 6) return;
            string k = cam.name + "/" + cam.cameraType;
            if (probed.Add(k)) Debug.Log("[GeoLod] probe camera " + k + " QualitySettings.lodBias during rendering = " + QualitySettings.lodBias + " (data effectiveBias " + C.effectiveBias + ")");
        }
        void Update()
        {
            frame++;
            if (!applied && frame >= C.startDelayFrames) { applied = true; Apply(); }
        }
        RowSet FindSet()
        {
            string id = ActiveSet;
            foreach (var s in C.sets) if (s != null && string.Equals(s.id, id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }
        void Apply()
        {
            var set = FindSet();
            if (set == null || set.rows == null) { Debug.LogWarning("[GeoLod] no row set '" + ActiveSet + "', nothing changed"); return; }
            var arena = GameObject.Find(C.arenaRoot); if (arena == null) { Debug.LogWarning("[GeoLod] no '" + C.arenaRoot + "' object, nothing changed"); return; }
            bool saveOnly = Mode != "strict"; float bias = Mathf.Max(0.01f, C.effectiveBias);
            foreach (var gname in C.groups)
            {
                var gt = arena.transform.Find(gname); if (gt == null) continue;
                for (int i = 0; i < gt.childCount; i++)
                {
                    var root = gt.GetChild(i); var lg = root.GetComponent<LODGroup>(); if (lg == null) continue;
                    if (Excluded(root.name)) { skipped++; continue; }
                    Row best = null; int bl = 0; string nm = root.name.ToLowerInvariant();
                    foreach (var r in set.rows) if (r != null && r.match.Length > bl && nm.Contains(r.match.ToLowerInvariant())) { best = r; bl = r.match.Length; }
                    if (best == null) { unmatched++; continue; }
                    matched++;
                    var lods = lg.GetLODs(); if (lods.Length < 2) continue;
                    var cur = new float[lods.Length]; for (int k = 0; k < lods.Length; k++) cur[k] = lods[k].screenRelativeTransitionHeight;
                    var nh = NewHeights(cur, best.t, bias, saveOnly, C.minGapHeight);
                    bool diff = false; for (int k = 0; k < cur.Length; k++) if (Mathf.Abs(nh[k] - cur[k]) > 1e-6f) diff = true;
                    if (!diff) continue;
                    saved.Add(new Saved { g = lg, heights = cur });
                    for (int k = 0; k < lods.Length; k++) lods[k].screenRelativeTransitionHeight = nh[k];
                    lg.SetLODs(lods); changed++;
                }
            }
            if (C.log) Debug.Log("[GeoLod] applied set=" + set.id + " mode=" + (saveOnly ? "save_only" : "strict") + " bias=" + bias + " groups matched=" + matched + " changed=" + changed + " unmatched=" + unmatched + " excluded=" + skipped);
        }
        bool Excluded(string n) { foreach (var e in C.excludeNameContains) if (!string.IsNullOrEmpty(e) && n.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        void Restore()
        {
            foreach (var s in saved)
            {
                if (s.g == null) continue;
                var lods = s.g.GetLODs(); int n = Mathf.Min(lods.Length, s.heights.Length);
                for (int k = 0; k < n; k++) lods[k].screenRelativeTransitionHeight = s.heights[k];
                s.g.SetLODs(lods);
            }
            saved.Clear();
        }
        void OnDestroy() { Restore(); }
        void OnApplicationQuit() { Restore(); }
    }
}
