using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Applies Assets/Data/runtime_flags.json (StreamingAssets in the player) on startup: per-feature switches + render scale, so presets and fps sweeps are data, not rebuilds.
// -flags:<path> on the command line overrides the file. Missing file = scene defaults.
public static class RuntimeQuality
{
    [System.Serializable] public class Flags { public bool clouds = true, volumetricFog = true, ssgi = true, ssr = true, contactShadows = true, shadows = true; public float renderScale = 100f; public int shadowResolution = 0;
        // extra switches (-1 = leave scene/asset default). quality levels: 0 low, 1 medium, 2 high.
        public bool ssao = true, ssgiFullRes = true; public int ssgiQuality = -1, ssrQuality = -1, fogQuality = -1, cloudMode = -1, cascades = 0; public float fogBudget = -1f, shadowDistance = 0f;
        // look overrides applied in the player so tuning needs no rebuild (-1 = leave as authored)
        public bool muteInHarness = true; public float exposureEV = -1f, skyExposure = -1f, indirectDiffuse = -1f, indirectSpecular = -1f, hdriExposure = -999f, hdriRotation = -999f; public LightMul[] lightMul; public float[] skyMid; public MatOv[] matOverrides; public bool hideMHHead;
        // TIERS v96: tier keys (quality_tiers.json). -1 = leave as authored. lodBiasMul is read by QualityTiers (CrackFlicker lod), kept here so -qset: and the json round-trip.
        public int ssaoQuality = -1, bloomQuality = -1, asyncCompute = -1; public float motionBlur = -1f, particleScale = 1f, lodBiasMul = 1f;
        // 2KPROFILE v01: companion switches (laneAPV ArenaBounceFill, laneFOGLOOK), tier keys, false = off
        public bool bounceFill, fogLook;
        // FOGMERGE v1 (FPSA + FOGSSGI merged, ONE fogSlices): finer fog / SSGI cost knobs, all off by default: -1 leave as authored (fogAniso -2, fogGridWidth 0). Applied by FogSsgiTuning.ApplyVolume and FogGrid below.
        // fogGridWidth > 0: fog froxel grid held at that many internal px at every output size (FPSA; 268 = the approved 1080p look). Skipped when fogManual / fogBudget / fogDepthRatio is set (those win). fogSlices: 170 when only the grid lock is on; in fogManual 1 the slice count.
        // fogManual 1 = Manual fog control; fogDenoise 0 none 1 reprojection 2 gaussian 3 both; ssgiRayMiss 0 none 1 sky 2 probes 3 both; fogBudget (existing key) is real again when set (FOGSSGI finding F0).
        public int fogGridWidth = 0, fogManual = -1, fogSlices = -1, fogDenoise = -1, fogDirOnly = -1, ssgiSteps = -1, ssgiDenoise = -1, ssgiHalfDenoise = -1, ssgiSecondDenoise = -1, ssgiRayMiss = -1;
        public float fogScreenPct = -1f, fogDepthExtent = -1f, fogDepthRatio = -1f, fogUniformity = -1f, fogCutoff = -1f, fogAniso = -2f, ssgiRadius = -1f, ssgiThickness = -1f; }
    [System.Serializable] public class MatOv { public string match; public float[] color; public float smoothness = -1f, metallic = -1f; }
    [System.Serializable] public class LightMul { public string name; public float mul = 1f; }
    static Flags F; static float scale = 100f;
    // 2KPROFILE v01: what the active tier says about the companion lanes; their components poll these (one line each, see MANIFEST)
    public static bool BounceFill { get { return F != null && F.bounceFill; } }
    public static bool FogLook { get { return F != null && F.fogLook; } }
    public static string FlagsPath = ""; public static bool Fast;

    // TIERS v96: harness = any gate / proof / bench run. One list for the V68 mute below, QualityTiers (ignores the saved tier) and GameSettings (ignores saved options).
    static int harnessCache = -1;
    public static bool IsHarness()
    {
        if (harnessCache < 0)
        {
            harnessCache = 0;
            foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-balanceauto" || a == "-verifyGround" || a == "-finisherauto" || a == "-postureproof" || a == "-flowproof" || a == "-camproof" || a == "-looptest" || a == "-latencyprobe" || a == "-perfbench") harnessCache = 1;
        }
        return harnessCache == 1;
    }

    // one-line player-side dump of what the renderer actually uses (exposure, sky, fog, quality level, lights) so editor stills can be compared with the player
    static void Diagnose()
    {
        try
        {
            var sb = new System.Text.StringBuilder("RQDIAG quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + " asset=" + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none") + " ");
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                var p = v.sharedProfile; sb.Append("vol=" + v.name + " en=" + v.enabled + " w=" + v.weight + " prio=" + v.priority + " ");
                if (p == null) continue;
                if (p.TryGet(out Exposure ex)) sb.Append("expMode=" + ex.mode.value + " fixed=" + ex.fixedExposure.value + " ");
                if (p.TryGet(out VisualEnvironment ve)) sb.Append("sky=" + ve.skyType.value + " ");
                if (p.TryGet(out GradientSky gs)) sb.Append("gsky exp=" + gs.exposure.value + " ");
                if (p.TryGet(out Fog fg)) sb.Append("fogMfp=" + fg.meanFreePath.value + " ");
            }
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) sb.Append(l.name + "=" + l.intensity.ToString("F0") + (l.enabled ? "" : "(off)") + " ");
            Debug.Log(sb.ToString());
        }
        catch (System.Exception e) { Debug.Log("RQDIAG failed " + e.Message); }
    }

    // lane LOOK fix 1: one exposure for gameplay AND finisher cameras: every profile that carries an Exposure override gets look.json's mode + EV; the global profile gets contact shadows
    static void ApplyLook()
    {
        try
        {
            int n = 0;
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                var p = v.sharedProfile; if (p == null) continue;
                if (p.TryGet(out Exposure ex)) { LookData.ApplyExposure(ex); n++; if (v.isGlobal) LookData.ApplyContactShadows(p); }
            }
            Debug.Log("LOOK exposure " + LookData.Get().exposure.mode + " ev=" + LookData.Get().exposure.ev + " on " + n + " profile(s), source " + LookData.Source);
        }
        catch (System.Exception e) { Debug.LogWarning("LOOK apply failed: " + e.Message); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Diagnose();
        string path = Application.streamingAssetsPath + "/runtime_flags.json";
        // QC rule (Goose): runtime_flags.json is the only flags file unless -fast is given; -fast stamps FAST/NOT-QC on every capture (CaptureMeta)
        FlagsPath = path; Fast = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-fast") >= 0;
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-flags:")) { if (Fast) { path = a.Substring(7); FlagsPath = path; } else Debug.LogWarning("RuntimeQuality: -flags: ignored without -fast (runtime_flags.json is used)"); }
        CaptureMeta.Boot();
        // V68: harness runs (balance / verify / proofs ...) are silent; runtime_flags.json "muteInHarness": false turns the sound back on
        bool harness = IsHarness();   // TIERS v96: the list moved to IsHarness()
        if (harness) AudioListener.volume = 0f;
        ApplyLook();   // lane LOOK: look.json exposure (+ contact shadows) on every Volume BEFORE the flags, so the flags can still switch features off
        if (!System.IO.File.Exists(path) && QualityTiers.FlagsJson == null) return;   // TIERS v96: no runtime_flags.json is fine when a tier file supplies the flags
        F = System.IO.File.Exists(path) ? JsonUtility.FromJson<Flags>(System.IO.File.ReadAllText(path)) : new Flags();
        QualityTiers.Overlay(F);   // TIERS v96: tier flags from quality_tiers.json, then -qset: / -renderscale: overrides
        if (harness && !F.muteInHarness) AudioListener.volume = 1f;
        if (F.exposureEV >= 0f && Mathf.Abs(F.exposureEV - LookData.Get().exposure.ev) > 0.001f) Debug.LogWarning("RuntimeQuality: runtime_flags exposureEV " + F.exposureEV + " ignored; look.json exposure.ev " + LookData.Get().exposure.ev + " is the one exposure");
        Apply(path, true);
    }

    // TIERS v96: re-applies runtime_flags.json + the (new) tier onto fresh Volume.profile clones. Called by QualityTiers.SetTier; look.json exposure is already on the shared profiles.
    public static void Reapply() { if (!System.IO.File.Exists(FlagsPath) && QualityTiers.FlagsJson == null) return; F = System.IO.File.Exists(FlagsPath) ? JsonUtility.FromJson<Flags>(System.IO.File.ReadAllText(FlagsPath)) : new Flags(); QualityTiers.Overlay(F); Apply(FlagsPath, false); }

    // FOGMERGE v1 (from FPSA v1): Balance mode derives the fog grid from the DRS-scaled viewport, so 2560 wide has ~4.3x the voxels of the 1286 wide viewport the Ultra look was approved at (grid 268 x 151 x 170 = 6.9 M).
    // Manual mode with the screen fraction = gridWidth / internal width gives every output size the approved grid. Only called when fogGridWidth > 0.
    static void FogGrid(Fog fg, float internalWidth)
    {
        float pct = Mathf.Clamp(100f * F.fogGridWidth / Mathf.Max(1f, internalWidth), 100f / 16f, 50f);   // Fog min / max screenResolutionPercentage (6.25 .. 50)
        fg.quality.Override(ScalableSettingLevelParameter.LevelCount);   // Custom: otherwise the asset's level table (Fog_Budget) wins over the values below
        foreach (var prm in fg.parameters) if (prm is FogControlParameter fcp) fcp.Override(FogControl.Manual);   // the fogControlMode setter only writes .value; the volume stack ignores a parameter whose overrideState is false
        fg.screenResolutionPercentage.Override(pct);
        fg.volumeSliceCount.Override(F.fogSlices > 0 ? F.fogSlices : 170);
    }

    // the part of Boot that depends on the flags (TIERS v96: split out of Boot so a tier switch can run it again; the body is unchanged except where marked)
    static void Apply(string path, bool first)
    {
        float fogInternalW = 0f;   // FOGMERGE v1: only read when the grid lock is on
        if (F.fogGridWidth > 0) { var LR0 = LookData.Get().runtime; float sc0 = Mathf.Clamp(F.renderScale, LR0.renderScaleMin, LR0.renderScaleMax); fogInternalW = Screen.width * (sc0 < 99.5f ? sc0 : 100f) * 0.01f; }   // same clamp as the render scale below
        foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
        {
            var p = QualityTiers.Fresh(v, first); if (p == null) continue;   // TIERS v96: was v.sharedProfile (edits went into the shared asset)
            if (p.TryGet(out VolumetricClouds vc)) vc.active = F.clouds;
            if (p.TryGet(out Fog fog) && !F.volumetricFog) fog.enableVolumetricFog.Override(false);
            if (p.TryGet(out GlobalIllumination gi)) gi.active = F.ssgi;
            if (p.TryGet(out ScreenSpaceReflection sr)) sr.active = F.ssr;
            if (p.TryGet(out ContactShadows cs)) cs.active = F.contactShadows;
            if (p.TryGet(out ScreenSpaceAmbientOcclusion ao)) ao.active = F.ssao;
            if (p.TryGet(out GlobalIllumination g2)) { g2.fullResolutionSS.Override(F.ssgiFullRes); if (F.ssgiQuality >= 0) g2.quality.Override(F.ssgiQuality); }
            if (p.TryGet(out ScreenSpaceReflection s2) && F.ssrQuality >= 0) s2.quality.Override(F.ssrQuality);
            if (p.TryGet(out Fog f2)) { if (F.fogQuality >= 0) f2.quality.Override(F.fogQuality); if (F.fogBudget >= 0f) { f2.quality.Override(ScalableSettingLevelParameter.LevelCount); f2.volumetricFogBudget = F.fogBudget; } }
            if (F.fogGridWidth > 0 && F.fogManual < 0 && F.fogBudget < 0f && F.fogDepthRatio < 0f && p.TryGet(out Fog f3) && f3.enableVolumetricFog.value) FogGrid(f3, fogInternalW);   // FOGMERGE v1 grid lock (the FOGSSGI custom keys win over it)
            if (p.TryGet(out VolumetricClouds c2) && F.cloudMode >= 0) c2.cloudSimpleMode.Override((VolumetricClouds.CloudSimpleMode)F.cloudMode);
            if (F.skyExposure >= 0f && p.TryGet(out GradientSky gsk)) gsk.exposure.Override(F.skyExposure);
            if (F.skyMid != null && F.skyMid.Length >= 3 && p.TryGet(out GradientSky gsm)) { var cm = new Color(F.skyMid[0], F.skyMid[1], F.skyMid[2]); gsm.middle.Override(cm); gsm.bottom.Override(cm); }
            if (p.TryGet(out HDRISky hsk)) { if (F.hdriExposure > -900f) hsk.exposure.Override(F.hdriExposure); if (F.hdriRotation > -900f) hsk.rotation.Override(F.hdriRotation); }
            if (F.indirectDiffuse >= 0f || F.indirectSpecular >= 0f)
            {
                if (!p.TryGet(out IndirectLightingController ilc)) ilc = p.Add<IndirectLightingController>(true);
                if (F.indirectDiffuse >= 0f) ilc.indirectDiffuseLightingMultiplier.Override(F.indirectDiffuse);
                if (F.indirectSpecular >= 0f) ilc.reflectionLightingMultiplier.Override(F.indirectSpecular);
            }
            if (p.TryGet(out HDShadowSettings hs)) { if (F.cascades > 0) hs.cascadeShadowSplitCount.Override(F.cascades); if (F.shadowDistance > 0f) hs.maxShadowDistance.Override(F.shadowDistance); }
            // TIERS v96: tier-only keys (-1 = as authored)
            if (F.ssaoQuality >= 0 && p.TryGet(out ScreenSpaceAmbientOcclusion aoq)) aoq.quality.Override(F.ssaoQuality);
            if (F.bloomQuality >= 0 && p.TryGet(out Bloom blq)) blq.quality.Override(F.bloomQuality);
            if (F.motionBlur >= 0f && p.TryGet(out MotionBlur mbo)) mbo.intensity.Override(F.motionBlur);
            FogSsgiTuning.ApplyVolume(p, F);   // FOGMERGE v1: fog / SSGI detail keys (all -1 = no-op)
        }
        if (first && F.matOverrides != null) foreach (var rr in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) foreach (var mm in rr.materials) foreach (var mo in F.matOverrides) if (mm != null && !string.IsNullOrEmpty(mo.match) && mm.name.Contains(mo.match))
        {
            if (mo.color != null && mo.color.Length >= 3 && mm.HasProperty("_BaseColor")) mm.SetColor("_BaseColor", new Color(mo.color[0], mo.color[1], mo.color[2], 1f));
            if (mo.smoothness >= 0f && mm.HasProperty("_Smoothness")) mm.SetFloat("_Smoothness", mo.smoothness);
            if (mo.metallic >= 0f && mm.HasProperty("_Metallic")) mm.SetFloat("_Metallic", mo.metallic);
        }
        if (F.hideMHHead) { var mhc = MHAttachData.Load(); var mhh = GameObject.Find(mhc.objectName); if (mhh != null) mhh.SetActive(false); var drq = GameObject.Find(mhc.fighterName); if (drq != null) foreach (var smr in drq.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (smr.name == "head") smr.enabled = true; }
        if (first && F.lightMul != null) foreach (var m in F.lightMul) foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.name == m.name) l.intensity *= m.mul;
        if (!F.shadows) foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) l.shadows = LightShadows.None;
        if (F.shadowResolution > 0) foreach (var ls in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (ls.type == LightType.Directional && ls.shadows != LightShadows.None) { var hdls = ls.GetComponent<HDAdditionalLightData>(); if (hdls != null) { hdls.SetShadowResolutionOverride(true); hdls.SetShadowResolution(Mathf.Clamp(F.shadowResolution, 64, 8192)); } }   // FIXED22 v1: the flag was declared and never read; 0 = as authored
        var LR = LookData.Get().runtime; scale = Mathf.Clamp(F.renderScale, LR.renderScaleMin, LR.renderScaleMax);
        if (scale < 99.5f)
        {
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                cam.allowDynamicResolution = true;
                var hdCam = cam.GetComponent<HDAdditionalCameraData>();
                if (hdCam != null) hdCam.allowDynamicResolution = true;
            }
            DynamicResolutionHandler.SetDynamicResScaler(() => scale, DynamicResScalePolicyType.ReturnsPercentage);
        }
        QualityTiers.ApplyCamera(F.asyncCompute); QualityTiers.ApplyParticles(F.particleScale); QualityTiers.ApplyLod();   // TIERS v96
        Debug.Log("RuntimeQuality applied from " + path + " tier=" + QualityTiers.Current + " scale=" + scale);
    }
}





