using System;
using System.IO;
using UnityEngine;

// Gateway (Abyss <-> Rift) numbers as DATA: Assets/Data/gateway.json (the editor reads it in place; BuildPlayer copies it to StreamingAssets for the player).
// Loader pattern as ImpactTable: Resources/gateway (TextAsset) -> editor: Assets/Data -> StreamingAssets -> Application.dataPath/Data. No numeric fallbacks:
// if the file is missing the gateway stays a plain abyss and an error is logged once. Used by GatewayRift (runtime), HdrpGateway (editor builder) and HdrpFurnace (skip flags).
public static class GatewayTable
{
    [Serializable] public class FloatRow { public string n; public float v; }
    [Serializable] public class ColorRow { public string n; public float[] c; }
    [Serializable] public class VectorRow { public string n; public float[] v; }
    [Serializable] public class NamedScale { public string name; public float abyss, rift; }

    [Serializable] public class StateRow { public string forceState; public string cmdlineArg; public bool instantWhenForced, debugKeys; }
    [Serializable] public class TriggerRow
    {
        public bool hpEnabled; public string hpTarget; public float hpFraction, closeHysteresis, pollSeconds;
        public bool closeOnTargetDeath; public float closeDelay;
        public bool specialEnabled; public string specialActor, specialMovePrefix;
    }
    [Serializable] public class TransitionRow
    {
        public float openSeconds, closeSeconds;
        public float dissolveStart, dissolveEnd;   // progress window in which _Open runs 0..1
        public float fogPartEnd;                    // fog thins/parts over progress 0..fogPartEnd
        public float shockStart;                    // progress at which the shock ring leaves the frame (runs to 1)
        public float flashAt, flashSeconds;         // progress at which the flash peaks, and its total length in seconds
        public float boomAt;                        // progress at which the boom/shake kick fires on opening
        public float spillRampStart;                // progress at which the realm light starts to ramp
        public float shardsStart;                   // progress at which shards start to appear
    }
    [Serializable] public class FurnaceRow
    {
        public bool skipFog, skipShimmer;           // HdrpFurnace builder: the gateway owns the interior fog / the old heat-shimmer card
        public float chamberEmissiveAbyss, chamberEmissiveRift;   // multiplier on the chamber materials' emissive (hdrp_furnace.json values stay as the dev set them)
        public float lightScaleAbyss, lightScaleRift;             // multiplier on the FurnaceRoot lights
        public float emberAbyssScale, emberRiftScale;             // multiplier on the ember emission rates
        public string[] emberNames;
    }
    [Serializable] public class PortalRow
    {
        public string name, shader, panorama, clouds, cubemap; public bool useCube;
        public float[] pos, euler, frameHalf; public float margin; public int queueOffset;
        public FloatRow[] floats; public ColorRow[] colors; public VectorRow[] vectors;
    }
    [Serializable] public class FogRow
    {
        public string name, blending; public float[] pos, size, albedo, tiling, scroll, fadePos, fadeNeg, riftSizeScale, riftAlbedo;
        public float meanFreePath, riftMeanFreePath, anisotropy; public int priority;
    }
    [Serializable] public class NoiseRow { public string path; public int size, frequency, octaves, seed; }
    [Serializable] public class FogSet { public FogRow abyss, wisp; public NoiseRow noise; public NamedScale[] external; }
    [Serializable] public class LightRow
    {
        public string name, kind, unit; public float[] pos, euler, color, size;
        public float value, range, spotAngle, innerPercent, volumetricDimmer, pulseAmp, pulseHz;
    }
    [Serializable] public class LightSet { public LightRow depth, spillRect, spillSpot, flash; public NamedScale[] external; }
    [Serializable] public class SpillRow { public string colorSource; public float[] color; public float saturation, minValue; }
    [Serializable]
    public class ShardRow
    {
        public int count, seed; public string[] meshPaths; public float[] sizeRange, speedRange, tumbleRange, zWobble, radialRange;
        public float marginOut, introSeconds, introRadiusBoost;
        public float[] baseColor; public float smoothness, metallic; public float[] emissive;
    }
    [Serializable] public class ShakeRow { public float kickAmp, kickRollDeg, kickSeconds, kickHz, rumbleAmp, rumbleRollDeg; }
    [Serializable] public class BoomRow { public string cue; public float cueGain; public bool procedural; public float gain, seconds, f0, f1, noiseGain, spatialBlend; }

    [Serializable]
    public class Data
    {
        public string root, scenePath, materialFolder;
        public StateRow state; public TriggerRow trigger; public TransitionRow transition; public FurnaceRow furnace;
        public PortalRow portal; public FogSet fog; public LightSet lights; public SpillRow spill; public ShardRow shards; public ShakeRow shake; public BoomRow boom;
    }

    static Data data;
    static bool errored;
    public static string LoadedFrom = "";

    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }
    public static bool Loaded { get { return Get() != null && Get().portal != null; } }

    static void Load()
    {
        string json = null;
        try
        {
#if UNITY_EDITOR
            string pe = Path.Combine(Application.dataPath, "Data/gateway.json");
            if (File.Exists(pe)) { json = File.ReadAllText(pe); LoadedFrom = pe; }
#endif
            if (json == null) { var ta = Resources.Load<TextAsset>("gateway"); if (ta != null) { json = ta.text; LoadedFrom = "Resources/gateway"; } }
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "gateway.json"); if (File.Exists(p)) { json = File.ReadAllText(p); LoadedFrom = p; } }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/gateway.json"); if (File.Exists(p)) { json = File.ReadAllText(p); LoadedFrom = p; } }
        }
        catch (Exception ex) { Debug.LogWarning("GatewayTable: read failed: " + ex.Message); }

        if (string.IsNullOrEmpty(json))
        {
            if (!errored) Debug.LogError("GatewayTable: gateway.json not found; gateway disabled.");
            errored = true; data = new Data(); Fill(); return;
        }
        try { data = JsonUtility.FromJson<Data>(json); if (data == null) data = new Data(); }
        catch (Exception ex) { Debug.LogError("GatewayTable: parse failed: " + ex.Message); data = new Data(); }
        Fill();
    }

    static void Fill()
    {
        if (data.state == null) data.state = new StateRow();
        if (data.trigger == null) data.trigger = new TriggerRow();
        if (data.transition == null) data.transition = new TransitionRow();
        if (data.furnace == null) data.furnace = new FurnaceRow();
        if (data.fog == null) data.fog = new FogSet();
        if (data.lights == null) data.lights = new LightSet();
        if (data.spill == null) data.spill = new SpillRow();
        if (data.shards == null) data.shards = new ShardRow();
        if (data.shake == null) data.shake = new ShakeRow();
        if (data.boom == null) data.boom = new BoomRow();
    }

    // ---- shared helpers (runtime + editor)
    public static Color Col(float[] c) { return c == null || c.Length < 3 ? Color.white : new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f); }
    public static Vector3 V3(float[] a) { return a == null || a.Length < 3 ? Vector3.zero : new Vector3(a[0], a[1], a[2]); }

    /// Applies the portal's float / colour / vector rows to a material (same rows at build time and at run time, so JSON retunes without a rebuild).
    public static void ApplyMaterialRows(Material m, PortalRow p)
    {
        if (m == null || p == null) return;
        if (p.floats != null) foreach (var f in p.floats) if (f != null && !string.IsNullOrEmpty(f.n) && m.HasProperty(f.n)) m.SetFloat(f.n, f.v);
        if (p.colors != null) foreach (var c in p.colors) if (c != null && !string.IsNullOrEmpty(c.n) && m.HasProperty(c.n)) m.SetColor(c.n, Col(c.c));
        if (p.vectors != null)
            foreach (var v in p.vectors)
                if (v != null && !string.IsNullOrEmpty(v.n) && m.HasProperty(v.n) && v.v != null && v.v.Length >= 2)
                    m.SetVector(v.n, new Vector4(v.v[0], v.v[1], v.v.Length > 2 ? v.v[2] : 0f, v.v.Length > 3 ? v.v[3] : 0f));
    }
}
