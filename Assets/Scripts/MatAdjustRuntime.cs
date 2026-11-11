using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// MATADJUST v1 (laneDATA4, 2026-11-11). UNPROVEN until compiled and run by the Unity dev. PRESENTATION ONLY.
// What: the matAdjust rows of hdrp_fixes.json (rock facets, clutter_ tint, per-material colour / smoothness / metallic / AO / normal / tiling) are applied to the LOADED materials at runtime,
//   following the CharMatOverride pattern (hidden driver, json polled by write time). Today HdrpFixes bakes them into the .mat assets in the scene chain; this makes them HOT.
// Rows: the 'source' json's matAdjust (default hdrp_fixes.json, read through DataPath) then optional inline rows[] of matadjust_runtime.json (applied after, so they win). Same fields and same maths as Editor\HdrpFixes.cs.
// Cost / look: absolute values written IN PLACE on the shared material object, once per material (an id + rows-hash guard), never a per-renderer instance and never a MaterialPropertyBlock (an MPB breaks the SRP batcher).
//   Already baked values are detected (within epsilon) and not written, so a build whose chain already applied the rows keeps the same pixels and the same batches.
// Timing: on every scene load for fastSeconds at fastPollSeconds (zone roots stream in over frames), then every rescanSeconds; a changed json re-applies at once.
// Editor: off unless applyInEditor (the .mat assets would be dirtied); when on, scene renderers get a clone per material (cloneSuffix), like CharMatOverride.
// Writes ONLY the Material properties named in props{}. Touches no transform, collider, scene object or json. No existing script calls it. enabled:false or a missing file = nothing runs.
public static class MatAdjustRuntime
{
    [Serializable] public class Row { public string match = ""; public float[] color; public float normalScale = -1f, tiling = 0f, metallicMax = -1f, smoothnessMax = -1f, smoothnessMin = -1f, aoStrength = -1f, emissiveScale = -1f; }
    [Serializable] class Src { public Row[] matAdjust; }
    [Serializable] public class Props { public string[] baseColor = new string[0], normalScale = new string[0], textures = new string[0], metallicMax = new string[0], smoothnessMax = new string[0], smoothnessMin = new string[0], aoMin = new string[0], emissiveColor = new string[0], emissiveMap = new string[0]; }
    [Serializable] public class Cfg
    {
        public bool enabled = true, caseSensitive = true, applyInEditor = false, logScans = true;
        public string source = "", cloneSuffix = ""; public string[] stripSuffixes = new string[0];
        public float epsilon = 0.0015f, filePollSeconds = 1f, fastPollSeconds = 0.5f, fastSeconds = 8f, rescanSeconds = 5f;
        public Row[] rows = new Row[0]; public Props props = new Props();
    }

    const string FileName = "matadjust_runtime.json";
    static Cfg cfg; static Row[] all = new Row[0]; static int rowsHash; static bool loadedOnce; static long stampCfg = -1, stampSrc = -1;
    static readonly Dictionary<Material, int> done = new Dictionary<Material, int>();   // material object -> rows hash it was last processed with
    static readonly Dictionary<Material, Material> clones = new Dictionary<Material, Material>();
    public static string LastReport = "";

    internal static Cfg Current { get { return cfg; } }
    internal static Row[] Rows { get { return all; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (UnityEngine.Object.FindFirstObjectByType<MatAdjustDriver>() != null) return;
        var go = new GameObject(typeof(MatAdjustDriver).Name); go.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<MatAdjustDriver>();
#if !FRANKEN_NO_HARNESS
        MatAdjustProof.Boot();
#endif
    }

    static long Stamp(string p) { return p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0L; }

    // true when either json changed since the last call (or loaded for the first time)
    internal static bool Reload()
    {
        string pc = DataPath.Find(FileName);
        if (pc == null) { if (cfg != null) { cfg = null; all = new Row[0]; rowsHash = 0; done.Clear(); return true; } return false; }
        string ps = null; if (cfg != null && !string.IsNullOrEmpty(cfg.source)) ps = DataPath.Find(cfg.source);
        long tc = Stamp(pc), ts = Stamp(ps);
        if (loadedOnce && tc == stampCfg && ts == stampSrc) return false;
        if (!(loadedOnce && tc == stampCfg))   // the config itself changed (or first load); a changed source alone keeps the parsed config
        {
            try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(pc)); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[MatAdjust] bad " + FileName + ": " + e.Message); cfg = null; }
            if (cfg != null && !string.IsNullOrEmpty(cfg.source)) { ps = DataPath.Find(cfg.source); ts = Stamp(ps); }
        }
        loadedOnce = true; stampCfg = tc; stampSrc = ts;
        var list = new List<Row>(); var sb = new StringBuilder();
        if (cfg != null)
        {
            if (ps != null)
            {
                try { var s = JsonUtility.FromJson<Src>(DataPath.ReadFile(ps)); if (s != null && s.matAdjust != null) foreach (var r in s.matAdjust) if (r != null && !string.IsNullOrEmpty(r.match)) { list.Add(r); sb.Append(JsonUtility.ToJson(r)); } }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[MatAdjust] bad " + cfg.source + ": " + e.Message); }
            }
            if (cfg.rows != null) foreach (var r in cfg.rows) if (r != null && !string.IsNullOrEmpty(r.match)) { list.Add(r); sb.Append(JsonUtility.ToJson(r)); }
            sb.Append(cfg.epsilon).Append(cfg.caseSensitive).Append(JsonUtility.ToJson(cfg.props));
        }
        all = list.ToArray(); rowsHash = sb.ToString().GetHashCode(); done.Clear();
        return true;
    }

    static string BaseName(string n)
    {
        if (n == null) return "";
        if (cfg.stripSuffixes != null) foreach (var suf in cfg.stripSuffixes) if (!string.IsNullOrEmpty(suf) && n.EndsWith(suf, StringComparison.Ordinal)) n = n.Substring(0, n.Length - suf.Length);
        if (!string.IsNullOrEmpty(cfg.cloneSuffix) && n.EndsWith(cfg.cloneSuffix, StringComparison.Ordinal)) n = n.Substring(0, n.Length - cfg.cloneSuffix.Length);
        return n;
    }

    static bool Near(float a, float b, float e) { return Mathf.Abs(a - b) <= e; }
    static bool Near(Color a, Color b, float e) { return Near(a.r, b.r, e) && Near(a.g, b.g, e) && Near(a.b, b.b, e) && Near(a.a, b.a, e); }

    // one float property group: returns the number of properties that differed (and, when write, were set)
    static int SetF(Material m, string[] props, float v, bool write)
    {
        int n = 0; if (props == null) return 0;
        foreach (var p in props) { if (string.IsNullOrEmpty(p) || !m.HasProperty(p)) continue; if (Near(m.GetFloat(p), v, cfg.epsilon)) continue; n++; if (write) m.SetFloat(p, v); }
        return n;
    }

    // the HdrpFixes.matAdjust maths for one row on one material. write=false only counts the differences (proof harness).
    public static int Adjust(Material m, Row a, bool write)
    {
        var P = cfg.props; int n = 0; float eps = cfg.epsilon;
        if (a.color != null && a.color.Length >= 3 && P.baseColor != null)
        {
            var t = new Color(a.color[0], a.color[1], a.color[2], 1f);
            foreach (var p in P.baseColor) { if (string.IsNullOrEmpty(p) || !m.HasProperty(p)) continue; if (Near(m.GetColor(p), t, eps)) continue; n++; if (write) m.SetColor(p, t); }
        }
        if (a.normalScale >= 0f) n += SetF(m, P.normalScale, a.normalScale, write);
        if (a.tiling > 0f && P.textures != null)
        {
            var t = new Vector2(a.tiling, a.tiling);
            foreach (var p in P.textures) { if (string.IsNullOrEmpty(p) || !m.HasProperty(p)) continue; var cur = m.GetTextureScale(p); if (Near(cur.x, t.x, eps) && Near(cur.y, t.y, eps)) continue; n++; if (write) m.SetTextureScale(p, t); }
        }
        if (a.metallicMax >= 0f) n += SetF(m, P.metallicMax, a.metallicMax, write);
        if (a.smoothnessMax >= 0f) n += SetF(m, P.smoothnessMax, a.smoothnessMax, write);
        if (a.smoothnessMin >= 0f) n += SetF(m, P.smoothnessMin, a.smoothnessMin, write);
        if (a.aoStrength >= 0f) n += SetF(m, P.aoMin, 1f - Mathf.Clamp01(a.aoStrength), write);
        if (a.emissiveScale >= 0f && P.emissiveColor != null)
        {
            bool mapped = false; if (P.emissiveMap != null) foreach (var p in P.emissiveMap) if (!string.IsNullOrEmpty(p) && m.HasProperty(p) && m.GetTexture(p) != null) mapped = true;
            if (!mapped)   // absolute, only when there is no emissive map (same rule as HdrpFixes)
            {
                var t = new Color(a.emissiveScale, a.emissiveScale, a.emissiveScale, 1f);
                foreach (var p in P.emissiveColor) { if (string.IsNullOrEmpty(p) || !m.HasProperty(p)) continue; if (Near(m.GetColor(p), t, eps)) continue; n++; if (write) m.SetColor(p, t); }
            }
        }
        return n;
    }

    static bool NameHas(string name, string match) { return name.IndexOf(match, cfg.caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0; }

    // every row that matches this material name, in file order (later rows win, as in HdrpFixes)
    public static List<Row> RowsFor(string name)
    {
        var l = new List<Row>(); string nm = BaseName(name);
        foreach (var r in all) if (NameHas(nm, r.match)) l.Add(r);
        return l;
    }

    // one pass over the loaded materials. force = ignore the processed guard.
    internal static void Scan(bool force)
    {
        if (cfg == null || !cfg.enabled || all.Length == 0) return;
        var sw = Stopwatch.StartNew(); int matched = 0, baked = 0, changed = 0, props = 0;
        if (force || done.Count > 20000) done.Clear();
        if (Application.isEditor)
        {
            if (!cfg.applyInEditor) return;
            if (string.IsNullOrEmpty(cfg.cloneSuffix)) { SafeWarn("applyInEditor needs a cloneSuffix (else the .mat assets would be dirtied)"); return; }
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mats = r.sharedMaterials; bool replaced = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i]; if (m == null) continue; var rs = RowsFor(m.name); if (rs.Count == 0) continue;
                    if (!m.name.EndsWith(cfg.cloneSuffix, StringComparison.Ordinal)) { Material c; if (!clones.TryGetValue(m, out c) || c == null) { c = new Material(m); c.name = m.name + cfg.cloneSuffix; clones[m] = c; } mats[i] = c; replaced = true; m = c; }
                    Process(m, rs, ref matched, ref baked, ref changed, ref props);
                }
                if (replaced) r.sharedMaterials = mats;
            }
        }
        else
        {
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue; var rs = RowsFor(m.name); if (rs.Count == 0) continue;
                Process(m, rs, ref matched, ref baked, ref changed, ref props);
            }
        }
        LastReport = "rows=" + all.Length + " matched=" + matched + " alreadyBaked=" + baked + " changed=" + changed + " props=" + props + " ms=" + sw.ElapsedMilliseconds;
        if (cfg.logScans && matched > 0) UnityEngine.Debug.Log("[MatAdjust] " + LastReport);
    }

    static void Process(Material m, List<Row> rs, ref int matched, ref int baked, ref int changed, ref int props)
    {
        int h; if (done.TryGetValue(m, out h) && h == rowsHash) return;
        done[m] = rowsHash; matched++; int n = 0;
        foreach (var r in rs) n += Adjust(m, r, true);
        if (n == 0) baked++; else { changed++; props += n; }
    }

    static bool warned; static void SafeWarn(string s) { if (warned) return; warned = true; UnityEngine.Debug.LogWarning("[MatAdjust] " + s); }
}

// survives scene loads. Polls the two json write times, rescans on a scene load (fast for fastSeconds) and then every rescanSeconds.
public class MatAdjustDriver : MonoBehaviour
{
    float nextFile, nextScan, fastUntil; bool pending;
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void OnLoaded(Scene s, LoadSceneMode m) { var c = MatAdjustRuntime.Current; fastUntil = Time.unscaledTime + (c != null ? c.fastSeconds : 0f); nextScan = 0f; pending = true; }

    void Update()
    {
        float now = Time.unscaledTime;
        if (now >= nextFile)
        {
            bool changed = MatAdjustRuntime.Reload(); var c0 = MatAdjustRuntime.Current;
            nextFile = now + (c0 != null ? Mathf.Max(0.1f, c0.filePollSeconds) : 2f);
            if (changed) { pending = true; nextScan = 0f; }
        }
        var c = MatAdjustRuntime.Current; if (c == null || !c.enabled) return;
        if (now < nextScan) return;
        bool fast = now < fastUntil;
        if (!fast && !pending && c.rescanSeconds <= 0f) { nextScan = now + 1f; return; }
        nextScan = now + (fast ? Mathf.Max(0.05f, c.fastPollSeconds) : (c.rescanSeconds > 0f ? c.rescanSeconds : 1f));
        pending = false;
        try { MatAdjustRuntime.Scan(false); } catch (Exception e) { UnityEngine.Debug.LogWarning("[MatAdjust] scan failed: " + e.Message); }
    }
}

#if !FRANKEN_NO_HARNESS
// proof harness: <exe> -matadjproof [-mapsettle:<s>] [-mapout:<file>] [-makeep]. After the settle time it re-checks every matched material (read only: counts props that still differ) and writes RESULT PASS / FAIL.
public class MatAdjustProof : MonoBehaviour
{
    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.Ordinal)) return a.Substring(p.Length); return null; }
    static bool Flag(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a == p) return true; return false; }
    internal static void Boot() { if (!Flag("-matadjproof")) return; var go = new GameObject("MatAdjustProof"); go.hideFlags = HideFlags.HideAndDontSave; DontDestroyOnLoad(go); go.AddComponent<MatAdjustProof>(); }

    System.Collections.IEnumerator Start()
    {
        float settle; if (!float.TryParse(Arg("-mapsettle:"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out settle) || settle <= 0f) settle = 8f;
        yield return new WaitForSecondsRealtime(settle);
        var sb = new StringBuilder(); int fails = 0, rowsHit = 0; var c = MatAdjustRuntime.Current;
        if (c == null || !c.enabled) { sb.AppendLine("matadjust off (file missing or enabled:false): nothing applied, as designed"); sb.AppendLine("RESULT PASS fails=0 (off)"); }
        else
        {
            sb.AppendLine("last scan: " + MatAdjustRuntime.LastReport);
            foreach (var r in MatAdjustRuntime.Rows)
            {
                int mats = 0, diff = 0; string sample = "";
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null) continue; var rs = MatAdjustRuntime.RowsFor(m.name); if (!rs.Contains(r)) continue;
                    mats++; int d = 0; foreach (var rr in rs) d += MatAdjustRuntime.Adjust(m, rr, false); diff += d;
                    if (sample.Length == 0 && m.HasProperty("_BaseColor")) { var col = m.GetColor("_BaseColor"); sample = m.name + " _BaseColor=" + col.r.ToString("F3") + "," + col.g.ToString("F3") + "," + col.b.ToString("F3"); }
                }
                if (mats > 0) rowsHit++; if (diff > 0) fails++;
                sb.AppendLine("row '" + r.match + "': materials=" + mats + " propsStillDiffering=" + diff + (sample.Length > 0 ? " sample " + sample : "") + (mats == 0 ? " (no loaded material matches: not in this scene)" : "") + (diff > 0 ? " FAIL" : ""));
            }
            sb.AppendLine("rows with materials: " + rowsHit + " of " + MatAdjustRuntime.Rows.Length);
            sb.AppendLine("RESULT " + (fails == 0 ? "PASS" : "FAIL") + " fails=" + fails);
        }
        string o = Arg("-mapout:"); if (!string.IsNullOrEmpty(o)) { try { File.WriteAllText(o, sb.ToString()); } catch (Exception e) { UnityEngine.Debug.LogWarning("[MatAdjust] proof out: " + e.Message); } }
        UnityEngine.Debug.Log("[MatAdjust] proof\n" + sb);
        if (!Flag("-makeep")) Application.Quit();
    }
}
#endif
