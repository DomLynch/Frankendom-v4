#if !FRANKEN_NO_HARNESS
using System;
using UnityEngine;
using UnityEngine.Rendering;

// LODBIASARM (laneFPSLEVERS lodlight; file name carries "Probe" so Toolshc_gate.py classes it as harness): harness-only, same-build LOD bias arm for the night benchmark.
//   -lodbias:<float>   the lodBias every Game camera CULLS with (e.g. -lodbias:0.5, -lodbias:1.0, -lodbias:2.0)
//   -lodbias:probe     change nothing, only log what the cameras really cull with
// No argument = this class does nothing (no event, no object). Compiled out with FRANKEN_NO_HARNESS.
// WHY a new arg: crack_flicker.json "lodBias" (2.0) only reaches QualitySettings.lodBias (CrackFlicker.cs, QualityTiers.ApplyLod). HDRP then calls
//   QualitySettings.SetLODSettings(frameSettings.GetResolvedLODBias(asset)) for every camera right before renderContext.Cull (HDRenderPipeline.cs, try/finally restore).
//   Camera frame settings here are lodBiasMode FromQualitySettings = the HDRP asset's own lodBias scalable setting (HDRP_Max: 1, 1, 1), so the data value 2.0 never reaches the cull:
//   the crack_flicker.json lodBias lever is expected to be INERT (the probe line below is the proof from a real run).
// HOW: RenderPipelineManager.beginCameraRendering runs INSIDE that try-block, after SetLODSettings and before Cull (HDRenderPipeline.cs: BeginCameraRendering(renderContext, camera) precedes
//   renderContext.Cull). Setting the bias there changes this camera's LOD selection (and its shadow-caster LOD selection, same cull) and HDRP's finally restores the previous value.
//   No asset, no material, no sim, no data file is touched.
// Log (first 6 cameras only), integers are bias x 100 so the runner's noopIfAllSame regex can read them:
//   [LodBiasArm] set x100 <HDRP resolved>-><override> camera <name>/<type>     (override == resolved => the runner reports the arm as NOOP)
//   [LodBiasArm] probe x100 app <QualitySettings value outside rendering> hdrp <value the camera culls with> camera <name>/<type>
public static class LodBiasArmProbe
{
    const string Arg = "-lodbias:";
    static bool probeOnly, active; static float value; static int appX100 = -1, logged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset() { probeOnly = false; active = false; value = 0f; appX100 = -1; logged = 0; RenderPipelineManager.beginContextRendering -= OnContext; RenderPipelineManager.beginCameraRendering -= OnCamera; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        string s = null;
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(Arg, StringComparison.OrdinalIgnoreCase)) s = a.Substring(Arg.Length).Trim();
        if (s == null) return;
        if (s.Equals("probe", StringComparison.OrdinalIgnoreCase)) probeOnly = true;
        else if (!float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) || value <= 0f) { Debug.LogError("[LodBiasArm] FAILED: bad value '" + s + "', arm ignored"); return; }
        active = true;
        RenderPipelineManager.beginContextRendering += OnContext;
        RenderPipelineManager.beginCameraRendering += OnCamera;
        Debug.Log("[LodBiasArm] arm " + (probeOnly ? "PROBE" : "SET " + value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)) + " (cameras cull with this value; crack_flicker.json lodBias untouched)");
    }

    // before HDRP resolves anything for a camera, QualitySettings.lodBias is still the application value (CrackFlicker data x tier multiplier)
    static void OnContext(ScriptableRenderContext ctx, System.Collections.Generic.List<Camera> cams) { if (active) appX100 = Mathf.RoundToInt(QualitySettings.lodBias * 100f); }

    static void OnCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (!active || cam == null) return;
        int hdrp = Mathf.RoundToInt(QualitySettings.lodBias * 100f);          // what HDRP just resolved for this camera
        if (probeOnly)
        {
            if (logged < 6) { logged++; Debug.Log("[LodBiasArm] probe x100 app " + appX100 + " hdrp " + hdrp + " camera " + cam.name + "/" + cam.cameraType); }
            return;
        }
        if (cam.cameraType != CameraType.Game) return;                         // scene view / preview / reflection cameras keep HDRP's value
        QualitySettings.SetLODSettings(value, QualitySettings.maximumLODLevel, false);   // HDRP's finally block restores its own previous value
        if (logged < 6) { logged++; Debug.Log("[LodBiasArm] set x100 " + hdrp + "->" + Mathf.RoundToInt(value * 100f) + " camera " + cam.name + "/" + cam.cameraType + " (app " + appX100 + ")"); }
    }
}
#endif
