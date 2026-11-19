using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// DITHERKIT v1 (laneLG, 18 Nov; COO/Strategy order after the fk_banding measurement: 35 of 40 smooth sky patches banded, 1-level plateaus, likely no dithering).
// ONE data row, look_options.json "dither", read through LookOptions.Cfg (StreamingAssets copy first, then Assets/Data). Default = enabled false = today's look: the camera value is only READ and logged.
//   enabled       false = no change (diagnostic log only). true = apply `mode`.
//   mode          "on"   HDAdditionalCameraData.dithering = true on every gameplay camera (Game camera, no target texture)
//                 "off"  force false (A/B arm)      "asis" leave the baked value, only log
//   applyToFinisher  v1 = always consistent: FinisherLook.Capture copies the gameplay camera value at Begin, so the finisher shots carry whatever this class set (the key is reserved, false is not implemented).
//   diagLog       true (default) = log the line below at boot and after every scene load even when nothing is changed, so ONE run answers "was the baked value already true".
//   recheckS      when a change is wanted, re-assert every N s (silent unless something had reset it: FinisherLook.Apply restoring an old snapshot, a camera rebuilt by a tier switch)
//   grain         OPTIONAL diagnostic arm (default off): a runtime global Volume with FilmGrain, to see whether MORE noise amplitude helps. HDRP has no amplitude knob for the dither itself.
// QA flag (no data edit): -qa_dither:on | off | grain  forces the row for that player (grain = on + grain.enabled with grain.intensity).
// Log (exact core, the A/B reader greps it):  [Look] hd.dithering = <true|false> cam=<name> (was <x>, set <y|unchanged>)  followed by  why= src= fsDither= fsPost= post= custom= grain= dyn= colBuf= postBuf=
//   and  [Look] hd.dithering MOOT cam=<name>: <reason>  when a frame setting / post-processing switch makes the camera flag irrelevant.
// Wiring: NO new RuntimeInitializeOnLoadMethod and NO serialized MonoBehaviour field. LookOptions.Boot (existing hook) creates its existing Applier component, which calls DitherKit.Apply at frame 5 and 2 s after every scene load
// (existing schedule) and DitherKit.Recheck every Update. Cfg / GrainCfg are JSON-only [Serializable] classes held by the static LookOptions.Root (not by a MonoBehaviour).
public static class DitherKit
{
    [Serializable] public class GrainCfg { public bool enabled = false; public string type = "Thin1"; public float intensity = 0.1f, response = 0.8f; }
    [Serializable] public class Cfg
    {
        public bool enabled = false; public string mode = "on"; public bool applyToFinisher = true; public bool diagLog = true; public float recheckS = 5f;
        public GrainCfg grain = new GrainCfg();
    }

    static string qa; static bool qaRead; static GameObject grainGo; static float nextCheck;

    static string Qa()
    {
        if (qaRead) return qa; qaRead = true;
        try { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-qa_dither:", StringComparison.Ordinal)) qa = a.Substring(11).Trim().ToLowerInvariant(); } catch (Exception) { }
        if (qa != null) Debug.Log("[Look] dither QA flag -qa_dither:" + qa);
        return qa;
    }

    /// <summary>true = set dithering true, false = set false, null = leave it. src names where the decision came from.</summary>
    static bool? Want(Cfg c, out string src, out bool grain)
    {
        grain = false; var q = Qa();
        if (q == "on" || q == "grain") { src = "qa:" + q; grain = q == "grain"; return true; }
        if (q == "off") { src = "qa:off"; return false; }
        if (c == null || !c.enabled) { src = "data:disabled"; return null; }
        grain = c.grain != null && c.grain.enabled;
        string m = (c.mode ?? "on").Trim().ToLowerInvariant();
        if (m == "on") { src = "data:on"; return true; }
        if (m == "off") { src = "data:off"; return false; }
        src = "data:asis"; return null;
    }

    /// <summary>True while a change is wanted (the Applier then re-asserts it).</summary>
    public static bool Active(Cfg c) { string s; bool g; return Want(c, out s, out g).HasValue; }

    static bool Gameplay(Camera cam) { return cam != null && cam.isActiveAndEnabled && cam.cameraType == CameraType.Game && cam.targetTexture == null; }

    /// <summary>Boot / scene-load pass: set (when wanted) and log. quiet = true for the periodic re-assert (logs only when it had to change something).</summary>
    public static void Apply(Cfg c, string why, bool quiet = false)
    {
        try
        {
            if (c == null) c = new Cfg();
            string src; bool grain; var want = Want(c, out src, out grain);
            if (quiet && !want.HasValue) return;
            if (!quiet && !c.diagLog && !want.HasValue) return;
            var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None); int n = 0;
            for (int i = 0; i < cams.Length; i++)
            {
                var cam = cams[i]; if (!Gameplay(cam)) continue;
                var hd = cam.GetComponent<HDAdditionalCameraData>();
                if (hd == null) { if (!quiet) Debug.Log("[Look] hd.dithering = n/a cam=" + cam.name + " (no HDAdditionalCameraData) why=" + why); continue; }
                n++; bool was = hd.dithering; bool changed = false;
                if (want.HasValue && was != want.Value) { hd.dithering = want.Value; changed = true; }
                if (quiet && !changed) continue;
                var sb = new StringBuilder(220);
                sb.Append("[Look] hd.dithering = ").Append(hd.dithering ? "true" : "false").Append(" cam=").Append(cam.name).Append(" (was ").Append(was ? "true" : "false").Append(", set ").Append(changed ? (want.Value ? "true" : "false") : "unchanged").Append(")");
                sb.Append(" why=").Append(quiet ? "recheck-reset" : why).Append(" src=").Append(src);
                Detail(sb, cam, hd);
                Debug.Log(sb.ToString());
                if (hd.dithering) Moot(cam, hd);
            }
            if (!quiet && n == 0) Debug.Log("[Look] hd.dithering = n/a (no gameplay camera with HDAdditionalCameraData found) why=" + why);
            if (!quiet) Grain(c, grain);
        }
        catch (Exception e) { Debug.LogWarning("[Look] hd.dithering failed: " + e.Message); }
    }

    /// <summary>Called every Update by the LookOptions Applier: cheap, re-asserts the wanted value every recheckS seconds.</summary>
    public static void Recheck(Cfg c)
    {
        if (c == null || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + Mathf.Max(1f, c.recheckS);
        string s; bool g; if (!Want(c, out s, out g).HasValue) return;
        Apply(c, "recheck", true);
    }

    static void Detail(StringBuilder sb, Camera cam, HDAdditionalCameraData hd)
    {
        string fsd = "?", fsp = "?";
        try
        {
            if (Time.frameCount > 3) { var fs = HDCamera.GetOrCreate(cam).frameSettings; fsd = fs.IsEnabled(FrameSettingsField.Dithering) ? "on" : "off"; fsp = fs.IsEnabled(FrameSettingsField.Postprocess) ? "on" : "off"; }
        }
        catch (Exception) { }
        string post = "?"; try { post = CoreUtils.ArePostProcessesEnabled(cam) ? "on" : "off"; } catch (Exception) { }
        string gr = "none"; try { var st = VolumeManager.instance.stack; var fg = st != null ? st.GetComponent<FilmGrain>() : null; if (fg != null) gr = fg.intensity.value.ToString("F2") + (fg.IsActive() ? "(active)" : "(inactive)"); } catch (Exception) { }
        string cf = "?", pf = "?";
        try { var a = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset; if (a != null) { var s = a.currentPlatformRenderPipelineSettings; cf = s.colorBufferFormat.ToString(); pf = s.postProcessSettings.bufferFormat.ToString(); } } catch (Exception) { }
        sb.Append(" fsDither=").Append(fsd).Append(" fsPost=").Append(fsp).Append(" post=").Append(post).Append(" custom=").Append(hd.customRenderingSettings ? "true" : "false")
          .Append(" grain=").Append(gr).Append(" dyn=").Append(hd.allowDynamicResolution ? "true" : "false").Append(" aa=").Append(hd.antialiasing).Append(" colBuf=").Append(cf).Append(" postBuf=").Append(pf).Append(" res=").Append(Screen.width).Append("x").Append(Screen.height);
    }

    // the camera flag is moot when the frame setting or post-processing is off: the final pass never runs the DITHER keyword (HDRenderPipeline.PostProcess.cs: ditheringEnabled = hdCamera.dithering && frameSettings Dithering && PostProcess on)
    static void Moot(Camera cam, HDAdditionalCameraData hd)
    {
        try
        {
            if (Time.frameCount <= 3) return;
            var fs = HDCamera.GetOrCreate(cam).frameSettings; string why = null;
            if (!fs.IsEnabled(FrameSettingsField.Postprocess)) why = "frame setting Postprocess is off";
            else if (!fs.IsEnabled(FrameSettingsField.Dithering)) why = "frame setting Dithering is off";
            else if (!CoreUtils.ArePostProcessesEnabled(cam)) why = "post-processing disabled for this camera (CoreUtils.ArePostProcessesEnabled false)";
            if (why != null) Debug.LogWarning("[Look] hd.dithering MOOT cam=" + cam.name + ": " + why);
        }
        catch (Exception) { }
    }

    static void Grain(Cfg c, bool want)
    {
        if (!want) { if (grainGo != null) { UnityEngine.Object.Destroy(grainGo); grainGo = null; Debug.Log("[Look] dither grain volume removed"); } return; }
        if (grainGo != null) return;
        grainGo = new GameObject("DitherKitGrain"); UnityEngine.Object.DontDestroyOnLoad(grainGo);
        var v = grainGo.AddComponent<Volume>(); v.isGlobal = true; v.priority = 1000f; v.weight = 1f;
        var p = ScriptableObject.CreateInstance<VolumeProfile>(); var fg = p.Add<FilmGrain>(true);
        FilmGrainLookup t; if (!Enum.TryParse(c.grain != null ? c.grain.type : "Thin1", true, out t)) t = FilmGrainLookup.Thin1;
        fg.type.Override(t); fg.intensity.Override(Mathf.Clamp01(c.grain != null ? c.grain.intensity : 0.1f)); fg.response.Override(Mathf.Clamp01(c.grain != null ? c.grain.response : 0.8f));
        v.sharedProfile = p;
        Debug.Log("[Look] dither grain volume on: FilmGrain " + t + " intensity " + fg.intensity.value.ToString("F2") + " response " + fg.response.value.ToString("F2") + " (diagnostic arm, multiplicative: weak in the darkest pixels)");
    }
}
