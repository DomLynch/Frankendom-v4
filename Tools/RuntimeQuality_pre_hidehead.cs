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
        public float exposureEV = -1f, skyExposure = -1f, indirectDiffuse = -1f, indirectSpecular = -1f, hdriExposure = -999f, hdriRotation = -999f; public LightMul[] lightMul; public float[] skyMid; public MatOv[] matOverrides; }
    [System.Serializable] public class MatOv { public string match; public float[] color; public float smoothness = -1f, metallic = -1f; }
    [System.Serializable] public class LightMul { public string name; public float mul = 1f; }
    static Flags F; static float scale = 100f;

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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Diagnose();
        string path = Application.streamingAssetsPath + "/runtime_flags.json";
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-flags:")) path = a.Substring(7);
        if (!System.IO.File.Exists(path)) return;
        F = JsonUtility.FromJson<Flags>(System.IO.File.ReadAllText(path));
        foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
        {
            var p = v.sharedProfile; if (p == null) continue;
            if (p.TryGet(out VolumetricClouds vc)) vc.active = F.clouds;
            if (p.TryGet(out Fog fog) && !F.volumetricFog) fog.enableVolumetricFog.Override(false);
            if (p.TryGet(out GlobalIllumination gi)) gi.active = F.ssgi;
            if (p.TryGet(out ScreenSpaceReflection sr)) sr.active = F.ssr;
            if (p.TryGet(out ContactShadows cs)) cs.active = F.contactShadows;
            if (p.TryGet(out ScreenSpaceAmbientOcclusion ao)) ao.active = F.ssao;
            if (p.TryGet(out GlobalIllumination g2)) { g2.fullResolutionSS.Override(F.ssgiFullRes); if (F.ssgiQuality >= 0) g2.quality.Override(F.ssgiQuality); }
            if (p.TryGet(out ScreenSpaceReflection s2) && F.ssrQuality >= 0) s2.quality.Override(F.ssrQuality);
            if (p.TryGet(out Fog f2)) { if (F.fogQuality >= 0) f2.quality.Override(F.fogQuality); if (F.fogBudget >= 0f) { f2.quality.Override(ScalableSettingLevelParameter.LevelCount); f2.volumetricFogBudget = F.fogBudget; } }
            if (p.TryGet(out VolumetricClouds c2) && F.cloudMode >= 0) c2.cloudSimpleMode.Override((VolumetricClouds.CloudSimpleMode)F.cloudMode);
            if (F.exposureEV >= 0f && p.TryGet(out Exposure exo)) exo.fixedExposure.Override(F.exposureEV);
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
        }
        if (F.matOverrides != null) foreach (var rr in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) foreach (var mm in rr.materials) foreach (var mo in F.matOverrides) if (mm != null && !string.IsNullOrEmpty(mo.match) && mm.name.Contains(mo.match))
        {
            if (mo.color != null && mo.color.Length >= 3 && mm.HasProperty("_BaseColor")) mm.SetColor("_BaseColor", new Color(mo.color[0], mo.color[1], mo.color[2], 1f));
            if (mo.smoothness >= 0f && mm.HasProperty("_Smoothness")) mm.SetFloat("_Smoothness", mo.smoothness);
            if (mo.metallic >= 0f && mm.HasProperty("_Metallic")) mm.SetFloat("_Metallic", mo.metallic);
        }
        if (F.lightMul != null) foreach (var m in F.lightMul) foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.name == m.name) l.intensity *= m.mul;
        if (!F.shadows) foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) l.shadows = LightShadows.None;
        scale = Mathf.Clamp(F.renderScale, 25f, 100f);
        if (scale < 99.5f) DynamicResolutionHandler.SetDynamicResScaler(() => scale, DynamicResScalePolicyType.ReturnsPercentage);
        Debug.Log("RuntimeQuality applied from " + path + " scale=" + scale);
    }
}




