#if !FRANKEN_NO_HARNESS
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// GRDARM (laneFPSLEVERS): harness-only same-build A/B switch for the HDRP GPU Resident Drawer and GPU occlusion culling.
//   -grd:0   drawer forced OFF            (explicit baseline arm, same as no argument for the shipped assets, which have mode 0)
//   -grd:1   drawer ON  (Instanced Drawing), GPU occlusion culling OFF
//   -grd:2   drawer ON + GPU occlusion culling ON   (same as -grd:1 -grdocc:1)
//   -grdocc:0|1  overrides the occlusion half only; it has no effect while the drawer is off.
// No argument = this component is never created and nothing changes. Compiled out with FRANKEN_NO_HARNESS.
// How it works: it sets HDRenderPipelineAsset.currentPlatformRenderPipelineSettings.gpuResidentDrawerSettings on the CURRENT pipeline asset (in memory; a player never saves it).
//   The setter runs the asset's OnValidate, which rebuilds the render pipeline once and re-initialises the drawer (one hitch, then steady). It is re-checked every 30 frames so a
//   quality-tier asset swap (HDRP_Low/Medium/High/Max) keeps the arm. In the Editor the original values are restored on destroy so no asset on disk is dirtied.
// REQUIRES the build to ship the BatchRendererGroup DOTS variants (ProjectSettings Graphics > BatchRendererGroup Variants = Keep All; see GrdSetup.cs). Without them the drawer starts
//   but draws pink/error or nothing: the arm then logs "[GrdArm] NOT ACTIVE" and the night runner must discard the arm.
// Log lines: "[GrdArm] arm=<n> requested ..." , "[GrdArm] STATE drawer=<bool> occlusion=<bool> asset=<name>" (2 s after each apply), "[GrdArm] NOT ACTIVE ...".
public sealed class GrdArm : MonoBehaviour
{
    const string ArgDrawer = "-grd:", ArgOcc = "-grdocc:";
    const int RecheckFrames = 30, MaxArm = 2;   // data-like constants (a quality-tier swap is noticed within RecheckFrames; -grd:2 = drawer + occlusion)
    const float ReportDelay = 2f;
    int arm = -1, occOverride = -1;
    readonly Dictionary<HDRenderPipelineAsset, (GPUResidentDrawerMode mode, bool occ)> orig = new Dictionary<HDRenderPipelineAsset, (GPUResidentDrawerMode, bool)>();
    HDRenderPipelineAsset lastAsset; float checkAt = -1f; int frame; int applyCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        int a = -1, o = -1;
        foreach (var s in System.Environment.GetCommandLineArgs())
        {
            if (s.StartsWith(ArgDrawer, System.StringComparison.OrdinalIgnoreCase)) int.TryParse(s.Substring(ArgDrawer.Length), out a);
            else if (s.StartsWith(ArgOcc, System.StringComparison.OrdinalIgnoreCase)) int.TryParse(s.Substring(ArgOcc.Length), out o);
        }
        if (a < 0 && o < 0) return;
        if (a < 0) { Debug.Log("[GrdArm] -grdocc given without -grd: ignored"); return; }
        var go = new GameObject("GrdArm"); DontDestroyOnLoad(go);
        var c = go.AddComponent<GrdArm>(); c.arm = Mathf.Clamp(a, 0, MaxArm); c.occOverride = o < 0 ? -1 : (o > 0 ? 1 : 0);
    }

    void Update()
    {
        frame++;
        var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        if (asset != null && (asset != lastAsset || frame % RecheckFrames == 0)) { Apply(asset); lastAsset = asset; }
        if (checkAt > 0f && Time.realtimeSinceStartup >= checkAt) { checkAt = -1f; Report(asset); }
    }

    void Apply(HDRenderPipelineAsset a)
    {
        var s = a.currentPlatformRenderPipelineSettings; var g = s.gpuResidentDrawerSettings;
        if (!orig.ContainsKey(a)) orig[a] = (g.mode, g.enableOcclusionCullingInCameras);
        var wantMode = arm > 0 ? GPUResidentDrawerMode.InstancedDrawing : GPUResidentDrawerMode.Disabled;
        bool wantOcc = arm > 0 && (occOverride >= 0 ? occOverride == 1 : arm >= MaxArm);
        if (g.mode == wantMode && g.enableOcclusionCullingInCameras == wantOcc) return;
        g.mode = wantMode; g.enableOcclusionCullingInCameras = wantOcc; s.gpuResidentDrawerSettings = g;
        Debug.Log("[GrdArm] arm=" + arm + " requested drawer=" + wantMode + " occlusion=" + wantOcc + " on asset " + a.name + " (was " + orig[a].mode + "/" + orig[a].occ + "); pipeline rebuilds once");
        a.currentPlatformRenderPipelineSettings = s;   // OnValidate -> new HDRenderPipeline -> GPUResidentDrawer.Reinitialize
        applyCount++; checkAt = Time.realtimeSinceStartup + ReportDelay;
    }

    void Report(HDRenderPipelineAsset a)
    {
        bool on = IGPUResidentRenderPipeline.IsGPUResidentDrawerEnabled();
        bool occ = GPUResidentDrawer.IsInstanceOcclusionCullingEnabled();
        string nm = a != null ? a.name : "none";
        Debug.Log("[GrdArm] STATE drawer=" + on + " occlusion=" + occ + " asset=" + nm + " arm=" + arm);
        if (arm > 0 && !on)
        {
            IGPUResidentRenderPipeline.IsGPUResidentDrawerSupportedByProjectConfiguration(true);
            Debug.LogWarning("[GrdArm] NOT ACTIVE: drawer requested but not running (platform, resources or BRG variants). Discard this arm.");
        }
        else if (arm == 0 && on) Debug.LogWarning("[GrdArm] NOT ACTIVE: drawer still running with arm 0.");
    }

    void OnDestroy()
    {
        if (!Application.isEditor || lastAsset == null) return;
        if (!orig.TryGetValue(lastAsset, out var o)) return;
        var s = lastAsset.currentPlatformRenderPipelineSettings; var g = s.gpuResidentDrawerSettings;
        if (g.mode == o.mode && g.enableOcclusionCullingInCameras == o.occ) return;
        g.mode = o.mode; g.enableOcclusionCullingInCameras = o.occ; s.gpuResidentDrawerSettings = g; lastAsset.currentPlatformRenderPipelineSettings = s;
    }
}
#endif


