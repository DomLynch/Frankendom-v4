using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// DOORGLOW v1 (laneFOGLOOK). OFF by default: door_glow_card.json enabled=false and no -doorglow:1 / -qset:doorGlow=1 = Boot returns, nothing allocated.
// Why: the doorway glow is the volumetric scatter lobe of the doorway point light (doorway.json light, volumetricDimmer 0.12). HDRP shades ONE stochastic
// sample per froxel and the lobe is 1/r^2 from the light, so at q1 voxel size the lobe shows the voxel grid as stepped rings (MANIFEST, section 1).
// While on: that light's volumetricDimmer = lightVolumetricDimmer (0 = out of the froxel grid) and a camera-facing additive soft card (procedural half-float
// lobe, no 8-bit step) sits at the light and follows its flicker. Cost: one transparent quad, no voxels.
// Data: StreamingAssets/door_glow_card.json. Args: -doorglow:1|0, or -qset:...,doorGlow=1|0 (JsonUtility ignores the unknown Flags key).
public class DoorGlowCard : MonoBehaviour
{
    [Serializable] public class Cfg
    {
        public bool enabled = false;
        public string lightName = "DoorwayFlickerLight";
        public float lightVolumetricDimmer = 0f;      // -1 = leave the light's dimmer alone
        public float sizeM = 4.4f, coreRadiusM = 0.37f, falloffPower = 2f, windowStart = 0.55f;
        public float[] tint = { 1f, 0.30f, 0.07f };
        public float intensity = 0.06f;               // display-referred (Furnace/Particle, _ExposureWeight 0). ESTIMATE: tune with the Visual QC pair
        public float refLightIntensity = 20000f;      // light.intensity at which the card shows 'intensity' (the flicker scales it)
        public float softDistanceM = 0.5f;
        public int textureSize = 128;
        public string shader = "Furnace/Particle", fallbackShader = "HDRP/Unlit";
    }
    Light lit; Material mat; Transform cam; float refI;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null, qs = null;
            foreach (var a in Environment.GetCommandLineArgs()) { if (a.StartsWith("-doorglow:")) a1 = a.Substring(10); else if (a.StartsWith("-qset:")) qs = a.Substring(6); }
            bool qOn = false, qOff = false;
            if (qs != null) foreach (var kv in qs.Split(',')) { if (kv == "doorGlow=1") qOn = true; else if (kv == "doorGlow=0") qOff = true; }
            string path = Application.streamingAssetsPath + "/door_glow_card.json";
            if (!System.IO.File.Exists(path)) return;
            var c = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(path)); if (c == null) return;
            bool on = a1 != null ? a1 == "1" : (qOn ? true : (qOff ? false : (c.enabled || RuntimeQuality.FogLook /* INTFPS v1: tier flag */)));
            if (!on) return;
            Light L = null;
            foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (l.name == c.lightName) { L = l; break; }
            if (L == null) { Debug.LogWarning("DOORGLOW: light '" + c.lightName + "' not found, card skipped"); return; }
            if (c.lightVolumetricDimmer >= 0f) { var hd = L.GetComponent<HDAdditionalLightData>(); if (hd != null) hd.volumetricDimmer = c.lightVolumetricDimmer; }
            var sh = Shader.Find(c.shader); bool particle = sh != null; if (sh == null) sh = Shader.Find(c.fallbackShader);
            if (sh == null) { Debug.LogWarning("DOORGLOW: no shader, card skipped"); return; }
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad); go.name = "DoorGlowCardSoft";
            Destroy(go.GetComponent<Collider>());
            var mf = go.GetComponent<MeshFilter>(); var qm = mf.mesh; var white = new Color[qm.vertexCount]; for (int i = 0; i < white.Length; i++) white[i] = Color.white; qm.colors = white;   // the shader multiplies by vertex colour
            var g = go.AddComponent<DoorGlowCard>(); g.lit = L; g.refI = Mathf.Max(1f, c.refLightIntensity);
            var tex = MakeTex(c);
            var m = new Material(sh);
            if (particle)
            {
                m.SetTexture("_MainTex", tex); m.SetColor("_Tint", new Color(c.tint[0], c.tint[1], c.tint[2], 1f)); m.SetFloat("_Intensity", c.intensity);
                m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", c.softDistanceM); m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f);
            }
            else
            {
                m.SetFloat("_SurfaceType", 1f); m.SetFloat("_BlendMode", 1f); m.SetFloat("_ZWrite", 0f); m.SetFloat("_DoubleSidedEnable", 1f);
                m.SetTexture("_UnlitColorMap", tex); m.SetColor("_UnlitColor", new Color(c.intensity * c.tint[0], c.intensity * c.tint[1], c.intensity * c.tint[2], 1f));
            }
            var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            g.mat = particle ? m : null; go.transform.localScale = new Vector3(c.sizeM, c.sizeM, 1f); go.transform.position = L.transform.position;
            Debug.Log("DOORGLOW on: card at light '" + L.name + "', size " + c.sizeM + " m, light dimmer " + c.lightVolumetricDimmer + ", intensity " + c.intensity + " (" + (particle ? c.shader : c.fallbackShader) + ")");
        }
        catch (Exception e) { Debug.LogWarning("DOORGLOW boot failed: " + e.Message); }
    }

    // lobe 1 / (1 + (r/r0)^p), faded to exactly 0 at the card edge, stored half-float. Furnace/Particle outputs rgb*alpha, so rgb = 1 and alpha = lobe (one power of the lobe)
    static Texture2D MakeTex(Cfg c)
    {
        int n = Mathf.Clamp(c.textureSize, 16, 512);
        var t = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true); t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
        var px = new Color[n * n]; float half = c.sizeM * 0.5f, r0 = Mathf.Max(0.01f, c.coreRadiusM), w0 = Mathf.Clamp01(c.windowStart);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = ((x + 0.5f) / n * 2f - 1f) * half, dy = ((y + 0.5f) / n * 2f - 1f) * half, r = Mathf.Sqrt(dx * dx + dy * dy);
            float v = 1f / (1f + Mathf.Pow(r / r0, c.falloffPower)), k = Mathf.Clamp01((r / half - w0) / Mathf.Max(1e-3f, 1f - w0));
            v *= 1f - k * k * (3f - 2f * k);
            px[y * n + x] = new Color(1f, 1f, 1f, v);
        }
        t.SetPixels(px); t.Apply(false, true); return t;
    }

    void LateUpdate()
    {
        if (lit == null) return;
        if (cam == null) { var cm = Camera.main; if (cm == null) return; cam = cm.transform; }
        transform.position = lit.transform.position; transform.rotation = cam.rotation;
        if (mat != null) { var tc = mat.GetColor("_Tint"); mat.SetColor("_Tint", new Color(tc.r, tc.g, tc.b, lit.enabled ? lit.intensity / refI : 0f)); }   // flicker rides the tint alpha
    }
}
