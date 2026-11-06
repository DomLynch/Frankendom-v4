using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// FLOORHUNT v1 (laneFLOORHUNT). MEASURING HARNESS ONLY, never ships. Splits the remaining frame by hiding things / toggling HDRP frame settings, one -hide: token set per run.
// OFF unless the player is started with -hide:<tok>,<tok>... (e.g. -hide:water  -hide:props,fighters  -hide:sss  -hide:aa_none  -hide:drs_off). Without the flag nothing is created, scanned or logged.
// All token -> target mappings are DATA (floorhunt.json, DataPath). Frame settings use the HDRP public API of 17.6.0: HDAdditionalCameraData.customRenderingSettings,
// renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField, bool) and renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] (BitArray128). The effective (sanitized)
// value is read back from HDCamera.frameSettings.IsEnabled and logged as proof ("FLOORHUNT proof ..."), and written to floorhunt_<tag>.txt in -benchout: when given.
// Fighters/decals are re-scanned every rescanSec because they spawn late; the sim is never touched (renderers/projectors only).
public class FloorHunt : MonoBehaviour
{
    class Cfg
    {
        public float applyDelaySec = 2f, rescanSec = 1f;
        public string[] waterNames = new string[0];
        public string woundDecalPrefix = "WoundDecal_";
        public Dictionary<string, string> frameToggles = new Dictionary<string, string>();
        public Dictionary<string, string> aaModes = new Dictionary<string, string>();
    }

    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.Ordinal)) return a.Substring(p.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { string h = Arg("-hide:"); if (!string.IsNullOrEmpty(h)) { var fh = new GameObject("FloorHunt").AddComponent<FloorHunt>(); fh.Init(h); } }

    Cfg cfg = new Cfg();
    readonly HashSet<string> want = new HashSet<string>();
    readonly List<string> propGroups = new List<string>();
    readonly HashSet<GameObject> done = new HashSet<GameObject>();      // already hidden
    readonly List<FrameSettingsField> fsFields = new List<FrameSettingsField>();
    readonly List<string> fsNames = new List<string>();
    readonly HashSet<Camera> camDone = new HashSet<Camera>();
    readonly Dictionary<string, int> counts = new Dictionary<string, int>();
    HDAdditionalCameraData.AntialiasingMode aaMode; bool wantAa;
    string camProof = "";
    int scans, lastTotal = -1;

    void Count(string k, int n = 1) { int c; counts.TryGetValue(k, out c); counts[k] = c + n; }

    void Init(string tokens)
    {
        DontDestroyOnLoad(gameObject);
        ParseCfg();
        foreach (var t in tokens.Split(new[] { ',', ';', '+' }, StringSplitOptions.RemoveEmptyEntries)) want.Add(t.Trim().ToLowerInvariant());
        if (want.Contains("list")) { Debug.Log("FLOORHUNT tokens: water props props:<Group> fighters wounddecals decalsall drs_off | frameToggles: " + string.Join(" ", new List<string>(cfg.frameToggles.Keys).ToArray()) + " | aa: " + string.Join(" ", new List<string>(cfg.aaModes.Keys).ToArray())); want.Remove("list"); }
        foreach (var w in want)
        {
            string fn;
            if (cfg.frameToggles.TryGetValue(w, out fn))
            {
                FrameSettingsField f;
                if (Enum.TryParse(fn, out f)) { fsFields.Add(f); fsNames.Add(w + "=" + fn); } else Debug.LogWarning("FLOORHUNT: FrameSettingsField '" + fn + "' does not exist, token " + w + " skipped");
            }
            string am;
            if (cfg.aaModes.TryGetValue(w, out am))
            {
                HDAdditionalCameraData.AntialiasingMode m;
                if (Enum.TryParse(am, out m)) { aaMode = m; wantAa = true; } else Debug.LogWarning("FLOORHUNT: AntialiasingMode '" + am + "' does not exist, token " + w + " skipped");
            }
        }
        LoadPropGroups();
        Debug.Log("FLOORHUNT ON tokens=" + string.Join(",", new List<string>(want).ToArray()) + " frameFields=" + string.Join(" ", fsNames.ToArray()) + " aa=" + (wantAa ? aaMode.ToString() : "-") + " propGroups=" + propGroups.Count);
        StartCoroutine(Loop());
    }

    // JsonUtility cannot read string maps, so scalars/arrays go through a shadow class and the two flat string maps through a small regex.
    [Serializable] class Shadow { public float applyDelaySec = 2f, rescanSec = 1f; public string[] waterNames; public string woundDecalPrefix = "WoundDecal_"; }
    void ParseCfg()
    {
        string txt = DataPath.ReadText("floorhunt.json");
        if (string.IsNullOrEmpty(txt)) { Debug.LogWarning("FLOORHUNT: floorhunt.json not found, no token maps"); return; }
        try
        {
            var s = JsonUtility.FromJson<Shadow>(txt);
            cfg.applyDelaySec = s.applyDelaySec; cfg.rescanSec = Mathf.Max(0.25f, s.rescanSec); cfg.waterNames = s.waterNames ?? new string[0]; cfg.woundDecalPrefix = s.woundDecalPrefix;
            cfg.frameToggles = StringMap(txt, "frameToggles"); cfg.aaModes = StringMap(txt, "aaModes");
        }
        catch (Exception e) { Debug.LogWarning("FLOORHUNT: floorhunt.json parse failed: " + e.Message); }
    }

    // {"key": "value", ...} object under a top-level key; flat string->string only. Keys are lower-cased.
    static Dictionary<string, string> StringMap(string txt, string key)
    {
        var d = new Dictionary<string, string>();
        int i = txt.IndexOf("\"" + key + "\"", StringComparison.Ordinal); if (i < 0) return d;
        int b = txt.IndexOf('{', i), e = b >= 0 ? txt.IndexOf('}', b) : -1; if (e < 0) return d;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(txt.Substring(b, e - b), "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\"")) d[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
        return d;
    }

    [Serializable] class PropGroups { public string[] groups; }
    void LoadPropGroups()
    {
        string txt = DataPath.ReadText("props_layout.json"); if (string.IsNullOrEmpty(txt)) return;
        try { var g = JsonUtility.FromJson<PropGroups>(txt); if (g != null && g.groups != null) propGroups.AddRange(g.groups); } catch (Exception) { }
    }

    IEnumerator Loop()
    {
        yield return new WaitForSecondsRealtime(cfg.applyDelaySec);
        while (true)
        {
            Scan();
            scans++;
            if (scans == 4) ReadBack();
            if (scans >= 4) { int tot = 0; foreach (var kv in counts) tot += kv.Value; if (tot != lastTotal || scans == 4) { lastTotal = tot; WriteProof(); } }
            yield return new WaitForSecondsRealtime(cfg.rescanSec);
        }
    }

    bool AnyPrefix(string p) { foreach (var w in want) if (w.StartsWith(p, StringComparison.Ordinal)) return true; return false; }
    void Hide(GameObject g, string what) { if (g == null || !done.Add(g)) return; g.SetActive(false); Count(what); }

    void Scan()
    {
        if (want.Contains("water"))
        {
            foreach (var w in FindObjectsByType<WaterSurface>(FindObjectsSortMode.None)) Hide(w.gameObject, "water");
            foreach (var n in cfg.waterNames) { var g = GameObject.Find(n); if (g != null) Hide(g, "water"); }
        }
        foreach (var w in want)   // laneWHIRLFIX: obj:<ExactName> hides scene objects by name (case-insensitive), e.g. -hide:obj:TerrainCentrePlug
            if (w.StartsWith("obj:", StringComparison.Ordinal))
                foreach (var ot in Resources.FindObjectsOfTypeAll<Transform>())
                    if (ot != null && ot.gameObject.scene.IsValid() && string.Equals(ot.name, w.Substring(4), StringComparison.OrdinalIgnoreCase)) Hide(ot.gameObject, w);
        bool allProps = want.Contains("props");
        if (allProps || AnyPrefix("props:"))
        {
            foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
                foreach (var gname in propGroups)
                    if (t != null && t.name == gname && (allProps || want.Contains("props:" + gname.ToLowerInvariant()))) { Hide(t.gameObject, "props:" + gname); break; }
        }
        if (want.Contains("fighters"))
            foreach (var f in FindObjectsByType<Fighter>(FindObjectsSortMode.None))
                foreach (var r in f.GetComponentsInChildren<Renderer>(false)) if (r != null && r.enabled && !(r is ParticleSystemRenderer)) { r.enabled = false; Count("fighters"); }
        bool dAll = want.Contains("decalsall"), dW = want.Contains("wounddecals");
        if (dAll || dW)
            foreach (var d in FindObjectsByType<DecalProjector>(FindObjectsSortMode.None))
            {
                if (d == null || !d.enabled) continue;
                bool wound = d.name.StartsWith(cfg.woundDecalPrefix, StringComparison.Ordinal);
                if (dAll || wound) { d.enabled = false; Count(wound ? "wounddecals" : "decals_other"); }
            }
        bool drs = want.Contains("drs_off");
        if (fsFields.Count > 0 || wantAa || drs)
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || cam.cameraType != CameraType.Game || camDone.Contains(cam)) continue;
                var hd = cam.GetComponent<HDAdditionalCameraData>(); if (hd == null) continue;
                camDone.Add(cam);
                if (fsFields.Count > 0)
                {
                    hd.customRenderingSettings = true;
                    var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
                    foreach (var f in fsFields) { hd.renderingPathCustomFrameSettings.SetEnabled(f, false); mask.mask[(uint)f] = true; }
                    hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                }
                if (wantAa) hd.antialiasing = aaMode;
                if (drs) { cam.allowDynamicResolution = false; hd.allowDynamicResolution = false; }
                Count("camera");
            }
    }

    // effective values after HDRP sanitises them (a field the asset does not support stays as the asset says regardless of the request).
    void ReadBack()
    {
        var sb = new StringBuilder();
        foreach (var cam in Camera.allCameras)
        {
            if (cam == null || cam.cameraType != CameraType.Game) continue;
            var hd = cam.GetComponent<HDAdditionalCameraData>(); var hc = HDCamera.GetOrCreate(cam);
            if (hd == null || hc == null) continue;
            sb.Append("cam=" + cam.name + " aaMode=" + hd.antialiasing + " customFS=" + hd.customRenderingSettings + " allowDRS(cam/hd)=" + cam.allowDynamicResolution + "/" + hd.allowDynamicResolution);
            foreach (var f in fsFields) sb.Append(" " + f + "(effective)=" + hc.frameSettings.IsEnabled(f));
            sb.Append(" AAfs(effective)=" + hc.frameSettings.IsEnabled(FrameSettingsField.Antialiasing));
            var dr = DynamicResolutionHandler.instance;
            sb.Append(" drsEnabled=" + (dr != null && dr.DynamicResolutionEnabled()) + " drsScale=" + (dr != null ? dr.GetCurrentScale().ToString("F2") : "n/a") + " actual=" + hc.actualWidth + "x" + hc.actualHeight + ";");
        }
        camProof = sb.ToString();
    }

    void WriteProof()
    {
        var sb = new StringBuilder();
        foreach (var kv in counts) sb.Append(kv.Key + "=" + kv.Value + " ");
        string line = "FLOORHUNT proof tokens=" + string.Join(",", new List<string>(want).ToArray()) + " changed: " + (sb.Length > 0 ? sb.ToString().Trim() : "NOTHING MATCHED") + " | " + camProof;
        Debug.Log(line);
        string dir = Arg("-benchout:"), tag = Arg("-benchtag:") ?? "run";
        if (string.IsNullOrEmpty(dir)) return;
        try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "floorhunt_" + tag + ".txt"), line + "\n"); } catch (Exception) { }
    }
}
