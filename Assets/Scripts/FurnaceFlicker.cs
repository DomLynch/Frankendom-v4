using System;
using UnityEngine;

// Runtime driver for the furnace doorway. HdrpFurnace (editor builder) fills every field from Assets/Data/hdrp_furnace.json; nothing here is tuned in code.
// One shared "master" fBm Perlin noise flickers the chamber emissive materials and the fire emission rate; each light also has its own noise,
// blended with the master by 'correlation' so light and fire breathe together but not in lock step. Deterministic (seeded), no per-frame allocations.
// Play mode only: materials are cloned in Awake so the .mat assets on disk are never modified by flicker.
public class FurnaceFlicker : MonoBehaviour
{
    [Serializable]
    public struct Noise
    {
        public float baseValue;     // centre of the multiplier (1 = unchanged)
        public float amplitude;     // +/- swing around baseValue
        public float frequency;     // Hz-ish: Perlin units per second
        public int octaves;         // fBm octaves
        public float seed;          // per-light offset into the noise field
        public float lacunarity;    // frequency multiplier per octave
        public float gain;          // amplitude multiplier per octave
    }

    [Serializable]
    public class LightDrive
    {
        public Light light;
        public float baseIntensity;   // in the light's own unit (lumen / candela), from the JSON
        public Noise noise;
        public float correlation;     // 0 = own noise only, 1 = master noise only
    }

    [Serializable]
    public class EmissiveDrive
    {
        public Renderer[] renderers;  // all renderers sharing this material
        public Color baseEmissive;    // HDRP _EmissiveColor in nits
        public float amount;          // 0..1: how strongly the master noise moves this emissive
        [NonSerialized] public Material material;
    }

    public Noise master;
    public LightDrive[] lights;
    public EmissiveDrive[] emissives;
    public ParticleSystem[] fire;     // layers whose rate follows the master noise
    public float[] fireBaseRate;
    public float fireAmount;
    public Renderer shimmerRenderer;  // heat shimmer quad (HDRP/Unlit distortion), vector map scrolled here
    public Vector2 shimmerScroll;     // UV units per second

    static readonly int EmissiveId = Shader.PropertyToID("_EmissiveColor");
    static readonly int DistortionMapId = Shader.PropertyToID("_DistortionVectorMap");
    const float PerlinSpread = 1.6f;  // Mathf.PerlinNoise*2-1 rarely leaves +/-0.6; this normalises fBm to about +/-1
    Material shimmerMaterial;
    Vector2 shimmerOffset;

    public static float Eval(in Noise n, float t)
    {
        int oct = n.octaves < 1 ? 1 : n.octaves;
        float sum = 0f, norm = 0f, amp = 1f, f = n.frequency;
        for (int o = 0; o < oct; o++)
        {
            sum += amp * (Mathf.PerlinNoise(n.seed + o * 17.31f, t * f) * 2f - 1f);
            norm += amp; amp *= n.gain; f *= n.lacunarity;
        }
        if (norm <= 0f) return n.baseValue;
        float s = Mathf.Clamp(sum / norm * PerlinSpread, -1f, 1f);
        float v = n.baseValue + n.amplitude * s;
        return v < 0f ? 0f : v;
    }

    void Awake()
    {
        if (emissives != null)
            for (int i = 0; i < emissives.Length; i++)
            {
                var e = emissives[i];
                if (e == null || e.renderers == null || e.renderers.Length == 0 || e.renderers[0] == null) continue;
                e.material = new Material(e.renderers[0].sharedMaterial);
                for (int r = 0; r < e.renderers.Length; r++) if (e.renderers[r] != null) e.renderers[r].sharedMaterial = e.material;
            }
        if (shimmerRenderer != null)
        {
            shimmerMaterial = new Material(shimmerRenderer.sharedMaterial);
            shimmerRenderer.sharedMaterial = shimmerMaterial;
        }
    }

    void OnDestroy()
    {
        if (emissives != null) for (int i = 0; i < emissives.Length; i++) if (emissives[i] != null && emissives[i].material != null) Destroy(emissives[i].material);
        if (shimmerMaterial != null) Destroy(shimmerMaterial);
    }

    void Update()
    {
        float t = Time.time;
        float m = Eval(master, t);

        if (lights != null)
            for (int i = 0; i < lights.Length; i++)
            {
                var d = lights[i];
                if (d == null || d.light == null) continue;
                float own = Eval(d.noise, t);
                d.light.intensity = d.baseIntensity * Mathf.Lerp(own, m, d.correlation);
            }

        if (emissives != null)
            for (int i = 0; i < emissives.Length; i++)
            {
                var e = emissives[i];
                if (e == null || e.material == null) continue;
                float k = 1f + (m - master.baseValue) * e.amount;
                e.material.SetColor(EmissiveId, e.baseEmissive * (k < 0f ? 0f : k));
            }

        if (fire != null && fireBaseRate != null)
            for (int i = 0; i < fire.Length && i < fireBaseRate.Length; i++)
            {
                if (fire[i] == null) continue;
                float k = 1f + (m - master.baseValue) * fireAmount;
                var em = fire[i].emission;
                em.rateOverTimeMultiplier = fireBaseRate[i] * (k < 0.05f ? 0.05f : k);
            }

        if (shimmerMaterial != null)
        {
            shimmerOffset += shimmerScroll * Time.deltaTime;
            shimmerOffset.x -= Mathf.Floor(shimmerOffset.x); shimmerOffset.y -= Mathf.Floor(shimmerOffset.y);
            shimmerMaterial.SetTextureOffset(DistortionMapId, shimmerOffset);
        }
    }
}
