using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// FPSNEXT v1 (laneFPSNEXT). Look-neutral-by-intent HDRP asset levers, ALL OFF by default; every number is a row in fps_next.json (Assets/Data == StreamingAssets). Missing file = everything off.
// NEW file, no hunk in any existing file. Render state only, never the sim. Nothing is written to HDRP_Max.asset: the live HDRenderPipelineAsset is CLONED in memory and the clone is assigned with
// QualitySettings.renderPipeline (the original is restored on quit, so an Editor play session never dirties the asset). The clone is rebuilt if a tier switch changes the asset.
//   N1 colour buffers : RenderPipelineSettings.colorBufferFormat (asset line 251) and postProcessSettings.bufferFormat (line 367) from RGBA16F to the named format (R11G11B10 = HDRP's own default,
//                       half the bytes per pixel). LOOK RISK: banding in dark gradients / fog and no alpha in the colour buffer; the pixel gate decides.
//   N3 shadow memory  : hdShadowInitParams.directionalShadowsDepthBits (line 321) 32 -> 16, punctual atlas resolution (line 326) 4096 -> 2048 (a 512 cube needs 1536x1024), optionally the decal atlas
//                       and decal draw distance (lines 356-358; 0 = leave). The 512 cube and the 2048 sun map are not resized, so nothing is rescaled while the atlas holds the requests.
//   N2 (fog temporal) is NOT here: it is two existing data keys (quality_tiers.json ultra2k fogQuality, runtime_flags.json fogDenoise) that apply_fpsnext.py edits, or -qset:fogQuality=0,fogDenoise=3.
// Command line: -fpsnext:N1,N3 (only these) | -fpsnext:none | -nofpsnext:N3 | -fnset:N3.directionalShadowsDepthBits=24,N1.colorBufferFormat=R11G11B10 (data override for A/B arms)
// Log lines the A/B runner checks: "[FpsNext] applied ..." (switch landed) or "[FpsNext] off ...".
public static class FpsNext
{
    [Serializable] public class N1Cfg { public bool enabled = false; public string colorBufferFormat = ""; public string postBufferFormat = ""; }
    [Serializable] public class N3Cfg { public bool enabled = false; public int directionalShadowsDepthBits = 0; public int punctualAtlasResolution = 0; public int decalAtlasSize = 0; public int decalDrawDistance = 0; }
    [Serializable] public class Cfg
    {
        public bool enabled = false;
        public int applyAfterFrames = 120;
        public int recheckEveryFrames = 60;
        public bool log = true;
        public string[] disableWhenArgs = new string[0];
        public N1Cfg N1 = new N1Cfg(); public N3Cfg N3 = new N3Cfg();
    }

    static Cfg cfg; static System.Collections.Generic.HashSet<string> only, off; static bool none;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; only = null; off = null; none = false; }
    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return a.Substring(p.Length); return null; }
    static System.Collections.Generic.HashSet<string> Split(string s) { var h = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var p in s.Split(',')) if (p.Trim().Length > 0) h.Add(p.Trim()); return h; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            var c = new Cfg();
            try { var txt = HudCfg.ReadData("fps_next.json"); if (!string.IsNullOrEmpty(txt)) { var r = JsonUtility.FromJson<Cfg>(txt); if (r != null) c = r; } else c.enabled = false; }
            catch (Exception e) { Debug.LogWarning("[FpsNext] fps_next.json: " + e.Message); c.enabled = false; }
            if (c.N1 == null) c.N1 = new N1Cfg(); if (c.N3 == null) c.N3 = new N3Cfg(); if (c.disableWhenArgs == null) c.disableWhenArgs = new string[0];
            cfg = c;
            var o = Arg("-fpsnext:"); if (o != null) { if (o.Equals("none", StringComparison.OrdinalIgnoreCase)) none = true; else only = Split(o); }
            var n = Arg("-nofpsnext:"); if (n != null) off = Split(n);
            var kv = Arg("-fnset:"); if (kv != null) foreach (var p in kv.Split(',')) Override(p);
            return cfg;
        }
    }

    // data override for A/B arms: keys are json field names, "N3.directionalShadowsDepthBits=24". Unknown key = warning, no change.
    static void Override(string pair)
    {
        int i = pair.IndexOf('='); if (i <= 0) return;
        string k = pair.Substring(0, i).Trim(), v = pair.Substring(i + 1).Trim(); int iv; int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv);
        switch (k)
        {
            case "N1.colorBufferFormat": cfg.N1.colorBufferFormat = v; break;
            case "N1.postBufferFormat": cfg.N1.postBufferFormat = v; break;
            case "N3.directionalShadowsDepthBits": cfg.N3.directionalShadowsDepthBits = iv; break;
            case "N3.punctualAtlasResolution": cfg.N3.punctualAtlasResolution = iv; break;
            case "N3.decalAtlasSize": cfg.N3.decalAtlasSize = iv; break;
            case "N3.decalDrawDistance": cfg.N3.decalDrawDistance = iv; break;
            case "applyAfterFrames": cfg.applyAfterFrames = Mathf.Max(1, iv); break;
            default: Debug.LogWarning("[FpsNext] -fnset: unknown key " + k); break;
        }
    }

    /// <summary>True when switch `id` (N1, N3) is on: command line first, then fps_next.json (master and the switch's own "enabled").</summary>
    public static bool On(string id)
    {
        var c = C; if (none) return false;
        if (only != null) return only.Contains(id);
        if (off != null && off.Contains(id)) return false;
        if (!c.enabled) return false;
        switch (id) { case "N1": return c.N1.enabled; case "N3": return c.N3.enabled; }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = C;
        foreach (var a in c.disableWhenArgs) if (!string.IsNullOrEmpty(a) && Array.IndexOf(Environment.GetCommandLineArgs(), a) >= 0) { Debug.Log("[FpsNext] off (" + a + ")"); return; }
        if (!On("N1") && !On("N3")) { Debug.Log("[FpsNext] off (fps_next.json: all switches false)"); return; }
        var go = new GameObject("FpsNext"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<FpsNextRunner>();
    }
}

public class FpsNextRunner : MonoBehaviour
{
    HDRenderPipelineAsset original, mine; int frame; bool logged;

    void Update()
    {
        frame++; var c = FpsNext.C;
        if (frame < Mathf.Max(1, c.applyAfterFrames)) return;
        if (frame % Mathf.Max(1, c.recheckEveryFrames) != 0 && mine != null) return;
        var cur = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        if (cur == null || cur == mine) return;
        Apply(cur, c);
    }

    void Apply(HDRenderPipelineAsset src, FpsNext.Cfg c)
    {
        var clone = Instantiate(src); clone.name = src.name + ".fpsnext";
        var s = clone.currentPlatformRenderPipelineSettings; string what = "";
        if (FpsNext.On("N1"))
        {
            RenderPipelineSettings.ColorBufferFormat cf; PostProcessBufferFormat pf;
            if (!string.IsNullOrEmpty(c.N1.colorBufferFormat) && Enum.TryParse(c.N1.colorBufferFormat, true, out cf)) { what += " colorBuffer " + s.colorBufferFormat + "->" + cf; s.colorBufferFormat = cf; }
            var pp = s.postProcessSettings;
            if (!string.IsNullOrEmpty(c.N1.postBufferFormat) && Enum.TryParse(c.N1.postBufferFormat, true, out pf)) { what += " postBuffer " + pp.bufferFormat + "->" + pf; pp.bufferFormat = pf; s.postProcessSettings = pp; }
        }
        if (FpsNext.On("N3"))
        {
            var sh = s.hdShadowInitParams;
            if (c.N3.directionalShadowsDepthBits == 16 || c.N3.directionalShadowsDepthBits == 24 || c.N3.directionalShadowsDepthBits == 32)
            { what += " sunDepthBits " + (int)sh.directionalShadowsDepthBits + "->" + c.N3.directionalShadowsDepthBits; sh.directionalShadowsDepthBits = (DepthBits)c.N3.directionalShadowsDepthBits; }
            if (c.N3.punctualAtlasResolution > 0)
            { var pa = sh.punctualLightShadowAtlas; what += " punctualAtlas " + pa.shadowAtlasResolution + "->" + c.N3.punctualAtlasResolution; pa.shadowAtlasResolution = c.N3.punctualAtlasResolution; sh.punctualLightShadowAtlas = pa; }
            s.hdShadowInitParams = sh;
            var d = s.decalSettings;
            if (c.N3.decalAtlasSize > 0) { what += " decalAtlas " + d.atlasWidth + "->" + c.N3.decalAtlasSize; d.atlasWidth = c.N3.decalAtlasSize; d.atlasHeight = c.N3.decalAtlasSize; }
            if (c.N3.decalDrawDistance > 0) { what += " decalDistance " + d.drawDistance + "->" + c.N3.decalDrawDistance; d.drawDistance = c.N3.decalDrawDistance; }
            s.decalSettings = d;
        }
        clone.currentPlatformRenderPipelineSettings = s;   // setter runs OnValidate: the pipeline is rebuilt from the clone
        if (original == null) original = src;
        mine = clone; QualitySettings.renderPipeline = clone;
        if (c.log) Debug.Log("[FpsNext] applied N1=" + FpsNext.On("N1") + " N3=" + FpsNext.On("N3") + " asset=" + clone.name + ":" + (what.Length == 0 ? " (no row changed anything)" : what));
        logged = true;
    }

    void OnApplicationQuit() { Restore(); }
    void OnDestroy() { Restore(); }
    void Restore()
    {
        if (original != null && mine != null && QualitySettings.renderPipeline == mine) QualitySettings.renderPipeline = original;
        if (mine != null) { Destroy(mine); mine = null; }
        if (logged && FpsNext.C.log) Debug.Log("[FpsNext] restored the original asset");
        logged = false;
    }
}
