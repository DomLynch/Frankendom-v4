using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Applies a lighting/volume preset (Assets/Data/hdrp_lighting_<tier>.json) to the open HDRP scene: global Volume (exposure, HDRI sky, volumetric fog, ACES, bloom, SSAO/SSGI/SSR, motion blur),
// physical light units, HDRP camera data. All numbers live in the JSON (data layer); this class only maps them onto Unity components. Idempotent.
public static class HdrpScene
{
    [System.Serializable] public class LightRow { public string name, unit; public float value; public float[] color; }
    [System.Serializable] public class FogRow { public float meanFreePath, baseHeight, maxHeight, anisotropy, budget; public float[] albedo; }
    [System.Serializable] public class PostRow { public float bloomIntensity, bloomThreshold, ssaoIntensity, motionBlur, vignette, saturation, contrast, grain; }
    [System.Serializable] public class ExtraRow { public string name, kind, unit; public float value; public float[] color, pos, euler, size; public bool shadows; }
    [System.Serializable] public class FogVol { public string name; public float[] pos, size, color; public float meanFreePath, blend; }
    [System.Serializable] public class CloudRow { public bool enable; public string preset; public float opacity, shapeFactor, sunDimmer, ambientDimmer, lowestAltitude, thickness; public float[] tint; }
    [System.Serializable] public class ColorRow { public string material, prop; public float[] color; }
    [System.Serializable] public class TintRow { public string match; public float value; public bool doubleSided; }
    [System.Serializable] public class ShadowRow { public string light; public int resolution, cascades; public float split0, split1, split2, maxDistance, normalBias, slopeBias; public bool contact, alwaysDrawDynamic; public string updateMode; }
    [System.Serializable] public class IndirectRow { public float diffuse = 1f, reflection = 1f; }
    [System.Serializable] public class SkyRow { public string hdriPath; public float hdriExposure, hdriRotation; public bool enable; public float[] top, mid, bottom; public float diffusion, exposure; }
    [System.Serializable] public class Preset { public IndirectRow indirect; public SkyRow sky; public ShadowRow shadow; public float exposureEV, hdriExposure; public FogRow fog; public PostRow post; public LightRow[] lights; public ExtraRow[] extra; public FogVol[] fogVolumes; public CloudRow clouds; public TintRow[] materialTints; public ColorRow[] materialColors; }
    public static Preset Load(string tier) { return JsonUtility.FromJson<Preset>(File.ReadAllText("Assets/Data/hdrp_lighting_" + tier + ".json")); }
    static Color Col(float[] c) { return new Color(c[0], c[1], c[2]); }

    public static void Run() { Apply("ultra", true); }

    public static void Apply(string tier, bool saveScene)
    {
        var P = Load(tier);
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/HadesSlice.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None)) if (v.gameObject.name != "HDRP_Global") Object.DestroyImmediate(v.gameObject);
        var go = GameObject.Find("HDRP_Global") ?? new GameObject("HDRP_Global"); var vol = go.GetComponent<Volume>() ?? go.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 10;
        Directory.CreateDirectory("Assets/HDRP");
        var prof = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.DeleteAsset("Assets/HDRP/Global.asset"); AssetDatabase.CreateAsset(prof, "Assets/HDRP/Global.asset");

        var ex = prof.Add<Exposure>(); ex.mode.Override(ExposureMode.Fixed); ex.fixedExposure.Override(P.exposureEV);
        var env = prof.Add<VisualEnvironment>(); env.skyType.Override((int)SkyType.HDRI); env.cloudType.Override(0); env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
        var hdri = prof.Add<HDRISky>();
        string cubePath = (P.sky != null && !string.IsNullOrEmpty(P.sky.hdriPath)) ? P.sky.hdriPath : "Assets/Env/skybox_styx.png"; var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(cubePath);
        if (cube == null)
        {
            var ti = AssetImporter.GetAtPath(cubePath) as TextureImporter;
            if (ti != null) { ti.textureShape = TextureImporterShape.TextureCube; ti.generateCubemap = TextureImporterGenerateCubemap.Cylindrical; ti.sRGBTexture = true; ti.mipmapEnabled = true; ti.SaveAndReimport(); cube = AssetDatabase.LoadAssetAtPath<Cubemap>(cubePath); }
        }
        if (cube != null) hdri.hdriSky.Override(cube);
        hdri.skyIntensityMode.Override(SkyIntensityMode.Exposure); hdri.exposure.Override(P.sky != null && !string.IsNullOrEmpty(P.sky.hdriPath) ? P.sky.hdriExposure : P.hdriExposure); if (P.sky != null && !string.IsNullOrEmpty(P.sky.hdriPath)) hdri.rotation.Override(P.sky.hdriRotation); hdri.multiplier.Override(1f);
        // optional dark red-brown horizon gradient sky (data: sky block); the HDRI stays as the fallback when absent
        if (P.sky != null && P.sky.enable && string.IsNullOrEmpty(P.sky.hdriPath))
        {
            env.skyType.Override((int)SkyType.Gradient);
            var gs = prof.Add<GradientSky>(); gs.top.Override(Col(P.sky.top)); gs.middle.Override(Col(P.sky.mid)); gs.bottom.Override(Col(P.sky.bottom)); gs.gradientDiffusion.Override(P.sky.diffusion); gs.exposure.Override(P.sky.exposure);
        }
        if (P.indirect != null) { var ilc = prof.Add<IndirectLightingController>(); ilc.indirectDiffuseLightingMultiplier.Override(P.indirect.diffuse); ilc.reflectionLightingMultiplier.Override(P.indirect.reflection); }
        var fog = prof.Add<Fog>(); fog.enabled.Override(true); fog.enableVolumetricFog.Override(true);
        fog.meanFreePath.Override(P.fog.meanFreePath); fog.baseHeight.Override(P.fog.baseHeight); fog.maximumHeight.Override(P.fog.maxHeight); fog.albedo.Override(Col(P.fog.albedo));
        fog.volumetricFogBudget = P.fog.budget; fog.denoisingMode.Override(FogDenoisingMode.Gaussian); fog.anisotropy.Override(P.fog.anisotropy);
        var tm = prof.Add<Tonemapping>(); tm.mode.Override(TonemappingMode.ACES);
        var bl = prof.Add<Bloom>(); bl.intensity.Override(P.post.bloomIntensity); bl.threshold.Override(P.post.bloomThreshold); bl.scatter.Override(0.7f);
        var ao = prof.Add<ScreenSpaceAmbientOcclusion>(); ao.intensity.Override(P.post.ssaoIntensity);
        var gi = prof.Add<GlobalIllumination>(); gi.enable.Override(true);
        var ssr = prof.Add<ScreenSpaceReflection>(); ssr.enabled.Override(true);
        var mb = prof.Add<MotionBlur>(); mb.intensity.Override(P.post.motionBlur);
        var vg = prof.Add<Vignette>(); vg.intensity.Override(P.post.vignette);
        var ca = prof.Add<ColorAdjustments>(); ca.saturation.Override(P.post.saturation); ca.contrast.Override(P.post.contrast);
        var fgr = prof.Add<FilmGrain>(); fgr.type.Override(FilmGrainLookup.Medium1); fgr.intensity.Override(P.post.grain);
        // volumetric clouds (data: preset name + opacity)
        if (P.clouds != null && P.clouds.enable)
        {
            env.cloudType.Override(0);
            var vc = prof.Add<VolumetricClouds>(); vc.enable.Override(true); vc.cloudControl.Override(VolumetricClouds.CloudControl.Simple);
            vc.cloudPreset = (VolumetricClouds.CloudPresets)System.Enum.Parse(typeof(VolumetricClouds.CloudPresets), P.clouds.preset); vc.densityMultiplier.Override(P.clouds.opacity); vc.shapeFactor.Override(P.clouds.shapeFactor); vc.sunLightDimmer.Override(P.clouds.sunDimmer); vc.ambientLightProbeDimmer.Override(P.clouds.ambientDimmer); vc.bottomAltitude.Override(P.clouds.lowestAltitude); vc.altitudeRange.Override(P.clouds.thickness); if (P.clouds.tint != null) vc.scatteringTint.Override(Col(P.clouds.tint));
        }        // key-light shadow quality (data): cascade count/splits live on the Volume's Shadows override, resolution/bias on the light
        if (P.shadow != null && P.shadow.cascades > 0)
        {
            var sh = prof.Add<HDShadowSettings>(); sh.cascadeShadowSplitCount.Override(P.shadow.cascades); sh.maxShadowDistance.Override(P.shadow.maxDistance);
            sh.cascadeShadowSplit0.Override(P.shadow.split0); sh.cascadeShadowSplit1.Override(P.shadow.split1); sh.cascadeShadowSplit2.Override(P.shadow.split2);
        }
        foreach (var comp in prof.components) AssetDatabase.AddObjectToAsset(comp, prof);
        vol.sharedProfile = prof; EditorUtility.SetDirty(prof); AssetDatabase.SaveAssets();

        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            var hd = l.GetComponent<HDAdditionalLightData>() ?? l.gameObject.AddComponent<HDAdditionalLightData>();
            bool found = false;
            foreach (var row in P.lights) if (row.name == l.name)
            {
                found = true; var unit = (LightUnit)System.Enum.Parse(typeof(LightUnit), row.unit);
                l.lightUnit = unit; l.intensity = row.value; if (row.color != null && row.color.Length >= 3) l.color = Col(row.color);
            }
            if (!found) Debug.Log("HDRPSCENE light not in preset: " + l.name + " " + l.type);
            hd.EnableShadows(l.shadows != LightShadows.None); hd.useContactShadow.@override = false; hd.useContactShadow.useOverride = false;
            if (P.shadow != null && l.name == P.shadow.light)
            {
                hd.SetShadowResolution(P.shadow.resolution); hd.shadowUpdateMode = P.shadow.updateMode == "OnEnable" ? ShadowUpdateMode.OnEnable : P.shadow.updateMode == "OnDemand" ? ShadowUpdateMode.OnDemand : ShadowUpdateMode.EveryFrame; hd.alwaysDrawDynamicShadows = P.shadow.alwaysDrawDynamic;
                l.shadowNormalBias = P.shadow.normalBias; l.shadowBias = P.shadow.slopeBias;
                hd.useContactShadow.@override = P.shadow.contact; hd.useContactShadow.useOverride = P.shadow.contact;
            }
        }
        // extra lights (fill, doorway rect area) and local volumetric fog volumes, created from data and named so a re-run replaces them
        foreach (var old in new[] { "HDRP_Extra" }) { var og = GameObject.Find(old); if (og != null) Object.DestroyImmediate(og); }
        var exRoot = new GameObject("HDRP_Extra");
        if (P.extra != null) foreach (var e in P.extra)
        {
            var lg = new GameObject(e.name); lg.transform.SetParent(exRoot.transform); lg.transform.position = new Vector3(e.pos[0], e.pos[1], e.pos[2]); lg.transform.rotation = Quaternion.Euler(e.euler[0], e.euler[1], e.euler[2]);
            var l = lg.AddComponent<Light>(); var hd = lg.AddComponent<HDAdditionalLightData>();
            l.type = (LightType)System.Enum.Parse(typeof(LightType), e.kind); l.lightUnit = (LightUnit)System.Enum.Parse(typeof(LightUnit), e.unit); l.intensity = e.value; l.color = Col(e.color);
            if (e.size != null && e.size.Length >= 2 && l.type == LightType.Rectangle) hd.SetAreaLightSize(new Vector2(e.size[0], e.size[1]));
            hd.EnableShadows(e.shadows);
        }
        if (P.fogVolumes != null) foreach (var fv in P.fogVolumes)
        {
            var fg = new GameObject(fv.name); fg.transform.SetParent(exRoot.transform); fg.transform.position = new Vector3(fv.pos[0], fv.pos[1], fv.pos[2]);
            var lv = fg.AddComponent<LocalVolumetricFog>(); lv.parameters.size = new Vector3(fv.size[0], fv.size[1], fv.size[2]); lv.parameters.albedo = Col(fv.color); lv.parameters.meanFreePath = fv.meanFreePath; lv.parameters.positiveFade = new Vector3(0.5f, 0.5f, 0.5f); lv.parameters.negativeFade = new Vector3(0.5f, 0.5f, 0.5f);
        }        if (P.materialTints != null) foreach (var t in P.materialTints)
            foreach (var g in AssetDatabase.FindAssets("t:Material " + t.match)) { var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)); if (m != null && m.name.Contains(t.match) && m.HasProperty("_BaseColor")) { m.SetColor("_BaseColor", new Color(t.value, t.value, t.value, 1f)); if (t.doubleSided) { m.SetFloat("_DoubleSidedEnable", 1f); HDMaterial.ValidateMaterial(m); } EditorUtility.SetDirty(m); } }        if (P.materialColors != null) foreach (var cr in P.materialColors)
            foreach (var g in AssetDatabase.FindAssets("t:Material " + cr.material)) { var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)); if (m != null && m.name == cr.material && m.HasProperty(cr.prop)) { m.SetColor(cr.prop, new Color(cr.color[0], cr.color[1], cr.color[2], cr.color[3])); EditorUtility.SetDirty(m); } }        var cam = Camera.main; if (cam != null) { var cd = cam.GetComponent<HDAdditionalCameraData>() ?? cam.gameObject.AddComponent<HDAdditionalCameraData>(); cd.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing; cd.allowDynamicResolution = true; }
        EditorSceneManager.MarkSceneDirty(scene); if (saveScene) EditorSceneManager.SaveScene(scene);
        Debug.Log("HDRPSCENE applied " + tier + " ev=" + P.exposureEV);
    }
}


