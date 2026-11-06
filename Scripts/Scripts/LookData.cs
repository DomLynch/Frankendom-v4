using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Look data layer (lane LOOK): Assets/Data/look.json (copy in StreamingAssets) and Assets/Data/atmosphere.json.
// look.json is the ONE home of the scene exposure (gameplay and finisher cameras share it) and of the look values that used to be hard-coded
// (tonemap, bloom scatter, grain type, fog denoise, local fog fade, camera AA, dynamic-res limits, render-scale clamp, glTF smoothness cap, sheen default).
// Every default below equals the value that was hard-coded before, so a missing file / key changes nothing.
public static class LookData
{
    [Serializable] public class ExposureRow { public string mode = "Fixed"; public float ev = 9.4f; public bool lockFinisher = true; }
    [Serializable] public class ContactRow { public bool enable = true; public float length = 0.15f; public float opacity = 1f; }
    [Serializable] public class PostRow { public string tonemap = "ACES"; public float bloomScatter = 0.7f; public string grain = "Medium1"; public string antialiasing = "TemporalAntialiasing"; }
    [Serializable] public class FogRow { public string denoise = "Gaussian"; public float[] localFade = { 0.5f, 0.5f, 0.5f }; }
    [Serializable] public class DynResRow { public float minPercentage = 60f, maxPercentage = 100f; public int maxShadowRequests = 256; }
    [Serializable] public class RuntimeRow { public float renderScaleMin = 25f, renderScaleMax = 100f; }
    [Serializable] public class GltfRow { public string smoothCapMatch = "boulder"; public float smoothCap = 0.40f; public int maskMaxSize = 2048; public float alphaCutoffDefault = 0.5f; }
    [Serializable] public class CharRow { public float[] sheenDefault = { 0.35f, 0.02f, 0.04f }; }
    // ground material pass (HdrpGroundLook, editor): material-only, never geometry
    [Serializable] public class GroundMat { public string path = ""; public float normalScale = -1f, detailTiling = 0f, detailAlbedo = 1f, detailSmoothness = 1f, smoothnessMin = -1f, smoothnessMax = -1f; }
    [Serializable] public class GroundRow { public bool enabled = true; public string detailTexture = "Assets/HDRP/World/ground_macro_detail.png"; public int size = 512, cells = 4, octaves = 4, seed = 11; public float albedoVar = 0.35f, roughVar = 0.5f; public GroundMat[] materials = new GroundMat[0]; }
    [Serializable] public class Cfg
    {
        public ExposureRow exposure = new ExposureRow(); public ContactRow contactShadows = new ContactRow(); public PostRow post = new PostRow(); public FogRow fog = new FogRow();
        public DynResRow dynamicRes = new DynResRow(); public RuntimeRow runtime = new RuntimeRow(); public GltfRow gltfMats = new GltfRow(); public CharRow charMats = new CharRow(); public GroundRow ground = new GroundRow();
    }

    // atmosphere.json: local fog boxes + non-shadow flicker lights (built by HdrpScene under HDRP_Atmosphere; AtmosFlicker animates the lights in the player)
    [Serializable] public class FogBox { public string name = ""; public float[] pos, size, albedo, fadePos, fadeNeg; public float meanFreePath = 25f; }
    [Serializable] public class FlickerLight { public string name = ""; public string kind = "Point"; public float[] pos; public float candela = 0f, kelvin = 2200f, range = 10f, volumetricDimmer = 1f; public bool shadows = false; public FurnaceFlicker.Noise flicker; }
    [Serializable] public class Atmos { public FogBox[] fogBoxes = new FogBox[0]; public FlickerLight[] flickerLights = new FlickerLight[0]; }

    static Cfg c; static Atmos a;
    public static string Source = "(defaults)";
    public static Cfg Get() { if (c == null) c = Load<Cfg>("look.json", out Source) ?? new Cfg(); return c; }
    public static Atmos GetAtmos() { if (a == null) { string s; a = Load<Atmos>("atmosphere.json", out s) ?? new Atmos(); } return a; }
    public static void Reload() { c = null; a = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; a = null; }

    static T Load<T>(string file, out string src) where T : class
    {
        src = "(defaults)";
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, file), Path.Combine(Application.dataPath, "Data/" + file) })
        {
            if (!File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<T>(File.ReadAllText(p)); if (r != null) { src = p; return r; } }
            catch (Exception e) { Debug.LogWarning("[LookData] " + p + ": " + e.Message); }
        }
        return null;
    }

    public static TE Enum<TE>(string s, TE fallback) where TE : struct
    {
        TE v; if (!string.IsNullOrEmpty(s) && System.Enum.TryParse(s, true, out v)) return v;
        if (!string.IsNullOrEmpty(s)) Debug.LogWarning("[LookData] unknown " + typeof(TE).Name + " '" + s + "', using " + fallback);
        return fallback;
    }
    public static Vector3 V3(float[] f, Vector3 fallback) { return f != null && f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : fallback; }
    public static ExposureMode ExposureModeValue => Enum(Get().exposure.mode, ExposureMode.Fixed);

    /// <summary>Write look.json exposure into a profile's Exposure override (mode + fixed EV). Used by HdrpScene (bake) and RuntimeQuality (player boot).</summary>
    public static void ApplyExposure(Exposure ex)
    {
        if (ex == null) return; var E = Get().exposure;
        ex.mode.Override(ExposureModeValue); ex.fixedExposure.Override(E.ev);
    }
    /// <summary>look.json contactShadows into a profile (adds the override when missing).</summary>
    public static void ApplyContactShadows(VolumeProfile p)
    {
        if (p == null) return; var C = Get().contactShadows;
        ContactShadows cs; if (!p.TryGet(out cs)) cs = p.Add<ContactShadows>(true);
        cs.enable.Override(C.enable); cs.length.Override(C.length); cs.opacity.Override(C.opacity);
    }
}
