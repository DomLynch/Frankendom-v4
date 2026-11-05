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
        [NonSerialized] public float lastWritten = float.NaN;   // PERF3 v100 D1
    }

    [Serializable]
    public class EmissiveDrive
    {
        public Renderer[] renderers;  // all renderers sharing this material
        public Color baseEmissive;    // HDRP _EmissiveColor in nits
        public float amount;          // 0..1: how strongly the master noise moves this emissive
        [NonSerialized] public Material material;
        [NonSerialized] public float lastK = float.NaN;   // PERF3 v100 D1
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
    bool guardD1, mpbD1b; float epsD1, epsEmD1, lastFireK = float.NaN; MaterialPropertyBlock mpb;   // PERF3 v100 D1 / D1b (perf_fixes.json)
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
        guardD1 = PerfFixes.On("D1"); mpbD1b = PerfFixes.On("D1b"); epsD1 = FlickerGuard.Eps; epsEmD1 = FlickerGuard.EpsEmissive; if (mpbD1b) mpb = new MaterialPropertyBlock();
        if (emissives != null && !mpbD1b)   // PERF3 v100 D1b: with a property block per renderer the shared material is never written, so it is not cloned either
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
                float v = d.baseIntensity * Mathf.Lerp(own, m, d.correlation);
                if (guardD1 && !float.IsNaN(d.lastWritten) && !FlickerGuard.Moved(d.lastWritten, v, d.baseIntensity, epsD1)) continue;   // PERF3 v100 D1: write only on change
                d.light.intensity = v; d.lastWritten = v;
            }

        if (emissives != null)
            for (int i = 0; i < emissives.Length; i++)
            {
                var e = emissives[i];
                if (e == null || (e.material == null && !mpbD1b)) continue;
                float k = 1f + (m - master.baseValue) * e.amount;
                if (guardD1 && !float.IsNaN(e.lastK) && !FlickerGuard.Moved(e.lastK, k, 1f, epsEmD1)) continue;   // PERF3 v100 D1: write only on change
                e.lastK = k;
                if (mpbD1b)   // PERF3 v100 D1b: property block on slot 0 of each renderer (what the clone replaced), the shared material stays untouched
                {
                    if (e.renderers == null) continue;
                    mpb.Clear(); mpb.SetColor(EmissiveId, e.baseEmissive * (k < 0f ? 0f : k));
                    for (int r = 0; r < e.renderers.Length; r++) if (e.renderers[r] != null) e.renderers[r].SetPropertyBlock(mpb, 0);
                    continue;
                }
                e.material.SetColor(EmissiveId, e.baseEmissive * (k < 0f ? 0f : k));
            }

        if (fire != null && fireBaseRate != null)
            for (int i = 0; i < fire.Length && i < fireBaseRate.Length; i++)
            {
                if (fire[i] == null) continue;
                float k = 1f + (m - master.baseValue) * fireAmount;
                if (guardD1 && !float.IsNaN(lastFireK) && i == 0 && !FlickerGuard.Moved(lastFireK, k, 1f, epsEmD1)) break;   // PERF3 v100 D1: the fire layers share k; skip the whole loop when it did not move
                if (i == 0) lastFireK = k;
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
