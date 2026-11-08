using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// FOGSSGI v1 (laneFOGSSGI). Finer volumetric-fog and SSGI cost knobs than RuntimeQuality had, all DATA and all OFF by default.
//   1. Volume knobs: RuntimeQuality.Flags fields fog*/ssgi* (-1 = leave as authored; -qset:fogDepthExtent=40 works like every other flag).
//      RuntimeQuality.Apply calls FogSsgiTuning.ApplyVolume(profile, flags) once per Volume profile. With every key at -1 nothing changes.
//   2. Local fog volumes + light volumetric dimmers: StreamingAssets/fogssgi_tuning.json. "enabled": false and no -fogtune: argument = nothing happens.
//      -fogtune:<profileId> on the command line selects a profile and wins over the file (so an A/B arm needs no data edit).
// Why reflection for a few fields: Fog / GlobalIllumination keep their Balance-mode and ray-step fields private and the setters only write .value. A VolumeParameter whose
// overrideState is false is IGNORED by the volume stack (the default is used), so a bare setter would do nothing. .Override(x) sets both. A missing field logs once and is skipped.
public static class FogSsgiTuning
{
    [Serializable] public class VolRow { public string name = ""; public int enable = -1; public float fadeStart = -1f, fadeEnd = -1f, densityMul = 1f; }
    [Serializable] public class LightRow { public string name = ""; public float volumetricDimmer = -1f, fadeDistance = -1f; }
    [Serializable] public class Profile { public string id = ""; public string what = ""; public string look = ""; public VolRow[] volumes; public LightRow[] lights; }
    [Serializable] public class Cfg { public bool enabled = false; public string active = ""; public Profile[] profiles; }

    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    const float OptimalPct = 12.5f;   // Fog.optimalFogScreenResolutionPercentage is internal
    static bool warned;

    // set a (possibly private) VolumeParameter<T> field with overrideState = true
    static void Ov<T>(VolumeComponent c, string field, T v)
    {
        var f = c.GetType().GetField(field, BF);
        var p = f != null ? f.GetValue(c) as VolumeParameter<T> : null;
        if (p != null) p.Override(v);
        else if (!warned) { warned = true; Debug.LogWarning("FOGSSGI: field " + field + " not found on " + c.GetType().Name + " (HDRP version changed?), skipped"); }
    }

    static GlobalLightingQualitySettings Q()
    {
        var a = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
        return a != null ? a.currentPlatformRenderPipelineSettings.lightingQualitySettings : null;
    }

    public static void ApplyVolume(VolumeProfile p, RuntimeQuality.Flags F)
    {
        try
        {
            if (p.TryGet(out Fog fog)) ApplyFog(fog, F);
            if (p.TryGet(out GlobalIllumination gi)) ApplyGi(gi, F);
        }
        catch (Exception e) { Debug.LogWarning("FOGSSGI volume apply failed: " + e.Message); }
    }

    static void ApplyFog(Fog fog, RuntimeQuality.Flags F)
    {
        bool custom = F.fogManual >= 0 || F.fogDepthRatio >= 0f || F.fogBudget >= 0f;
        if (custom || F.fogDepthExtent > 0f || F.fogDenoise >= 0 || F.fogAniso > -1.5f || F.fogUniformity >= 0f || F.fogCutoff >= 0f || F.fogDirOnly >= 0)
            Debug.Log("FOGSSGI fog keys: manual=" + F.fogManual + " pct=" + F.fogScreenPct + " slices=" + F.fogSlices + " ratio=" + F.fogDepthRatio + " budget=" + F.fogBudget + " extent=" + F.fogDepthExtent + " denoise=" + F.fogDenoise + " aniso=" + F.fogAniso + " cutoff=" + F.fogCutoff + " dirOnly=" + F.fogDirOnly);
        if (custom)
        {
            // leave the quality LEVEL values (Balance, budget, depth ratio) as the starting point, then override only what the data asks for
            var q = Q(); int lv = Mathf.Clamp(fog.quality.value, 0, 2);
            float budget = q != null ? q.Fog_Budget[lv] : 0.5f, ratio = q != null ? q.Fog_DepthRatio[lv] : 0.5f;
            fog.quality.Override(ScalableSettingLevelParameter.LevelCount);
            Ov(fog, "m_FogControlMode", FogControl.Balance); Ov(fog, "m_VolumetricFogBudget", budget); fog.m_ResolutionDepthRatio.Override(ratio);
            if (F.fogBudget >= 0f) Ov(fog, "m_VolumetricFogBudget", F.fogBudget);   // also makes the older fogBudget flag real (see MANIFEST, finding F0)
            if (F.fogDepthRatio >= 0f) fog.m_ResolutionDepthRatio.Override(F.fogDepthRatio);
            if (F.fogManual == 1)
            {
                Ov(fog, "m_FogControlMode", FogControl.Manual);
                fog.screenResolutionPercentage.Override(F.fogScreenPct >= 0f ? F.fogScreenPct : OptimalPct);   // 12.5 = 8 px voxels = HDRP's scalarised VL_PRESET_OPTIMAL path
                fog.volumeSliceCount.Override(F.fogSlices >= 1 ? F.fogSlices : 64);
            }
        }
        if (F.fogDepthExtent > 0f) fog.depthExtent.Override(F.fogDepthExtent);
        if (F.fogDenoise >= 0) fog.denoisingMode.Override((FogDenoisingMode)Mathf.Clamp(F.fogDenoise, 0, 3));
        if (F.fogAniso > -1.5f) fog.anisotropy.Override(Mathf.Clamp(F.fogAniso, -1f, 1f));
        if (F.fogUniformity >= 0f) fog.sliceDistributionUniformity.Override(Mathf.Clamp01(F.fogUniformity));
        if (F.fogCutoff >= 0f) fog.volumetricLightingDensityCutoff.Override(F.fogCutoff);
        if (F.fogDirOnly >= 0) fog.directionalLightsOnly.Override(F.fogDirOnly == 1);
    }

    static void ApplyGi(GlobalIllumination gi, RuntimeQuality.Flags F)
    {
        if (F.ssgiThickness >= 0f) gi.depthBufferThickness.Override(Mathf.Clamp(F.ssgiThickness, 0f, 0.5f));
        if (F.ssgiRayMiss >= 0) gi.rayMiss.Override((RayMarchingFallbackHierarchy)Mathf.Clamp(F.ssgiRayMiss, 0, 3));   // 0 none, 1 sky, 2 probes, 3 probes+sky (authored)
        bool custom = F.ssgiSteps >= 0 || F.ssgiDenoise >= 0 || F.ssgiHalfDenoise >= 0 || F.ssgiSecondDenoise >= 0 || F.ssgiRadius >= 0f;
        if (F.ssgiThickness >= 0f || F.ssgiRayMiss >= 0 || custom) Debug.Log("FOGSSGI gi keys: steps=" + F.ssgiSteps + " denoise=" + F.ssgiDenoise + " halfDenoise=" + F.ssgiHalfDenoise + " second=" + F.ssgiSecondDenoise + " radius=" + F.ssgiRadius + " thickness=" + F.ssgiThickness + " rayMiss=" + F.ssgiRayMiss);
        if (!custom) return;
        var q = Q(); if (q == null) { Debug.LogWarning("FOGSSGI: no HDRP asset, SSGI custom keys skipped"); return; }
        int lv = Mathf.Clamp(F.ssgiQuality >= 0 ? F.ssgiQuality : gi.quality.value, 0, 2);   // start from the quality level's own numbers
        gi.quality.Override(ScalableSettingLevelParameter.LevelCount);
        Ov(gi, "m_MaxRaySteps", q.SSGIRaySteps[lv]); Ov(gi, "m_DenoiseSS", q.SSGIDenoise[lv]); Ov(gi, "m_HalfResolutionDenoiserSS", q.SSGIHalfResDenoise[lv]);
        Ov(gi, "m_DenoiserRadiusSS", q.SSGIDenoiserRadius[lv]); Ov(gi, "m_SecondDenoiserPassSS", q.SSGISecondDenoise[lv]);
        if (F.ssgiSteps >= 0) Ov(gi, "m_MaxRaySteps", Mathf.Max(1, F.ssgiSteps));
        if (F.ssgiDenoise >= 0) Ov(gi, "m_DenoiseSS", F.ssgiDenoise == 1);
        if (F.ssgiHalfDenoise >= 0) Ov(gi, "m_HalfResolutionDenoiserSS", F.ssgiHalfDenoise == 1);
        if (F.ssgiSecondDenoise >= 0) Ov(gi, "m_SecondDenoiserPassSS", F.ssgiSecondDenoise == 1);
        if (F.ssgiRadius >= 0f) Ov(gi, "m_DenoiserRadiusSS", Mathf.Clamp(F.ssgiRadius, 0.001f, 1f));
    }

    // ---- local fog volumes and light volumetric dimmers (scene objects, found by name) ----
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string arg = null;
            foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-fogtune:")) arg = a.Substring(9);
            string path = Application.streamingAssetsPath + "/fogssgi_tuning.json";
            if (!System.IO.File.Exists(path)) return;
            var cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(path));
            if (cfg == null || cfg.profiles == null) return;
            string id = !string.IsNullOrEmpty(arg) ? arg : (cfg.enabled ? cfg.active : "");
            if (string.IsNullOrEmpty(id)) return;
            Profile pr = null; foreach (var x in cfg.profiles) if (x != null && x.id == id) pr = x;
            if (pr == null) { Debug.LogWarning("FOGSSGI: profile '" + id + "' not found in fogssgi_tuning.json"); return; }
            int nv = 0, nl = 0;
            if (pr.volumes != null)
            {
                var fogs = UnityEngine.Object.FindObjectsByType<LocalVolumetricFog>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var r in pr.volumes) foreach (var f in fogs)
                {
                    if (f == null || r == null || f.name != r.name) continue;
                    if (r.enable == 0) f.enabled = false;
                    var P = f.parameters;
                    if (r.fadeStart >= 0f) P.distanceFadeStart = r.fadeStart;
                    if (r.fadeEnd >= 0f) P.distanceFadeEnd = r.fadeEnd;
                    if (r.densityMul > 0f && Mathf.Abs(r.densityMul - 1f) > 1e-4f) P.meanFreePath = P.meanFreePath / r.densityMul;
                    f.parameters = P; nv++;
                }
            }
            if (pr.lights != null)
            {
                var ls = UnityEngine.Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var r in pr.lights) foreach (var l in ls)
                {
                    if (l == null || r == null || l.name != r.name) continue;
                    if (r.volumetricDimmer >= 0f) l.volumetricDimmer = r.volumetricDimmer;
                    if (r.fadeDistance >= 0f) l.volumetricFadeDistance = r.fadeDistance;
                    nl++;
                }
            }
            Debug.Log("FOGSSGI profile " + id + ": " + nv + " fog volume row(s), " + nl + " light row(s) applied (" + pr.what + ")");
        }
        catch (Exception e) { Debug.LogWarning("FOGSSGI boot failed: " + e.Message); }
    }
}
