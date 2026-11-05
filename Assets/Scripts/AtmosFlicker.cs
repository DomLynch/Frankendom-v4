using UnityEngine;

// Flicker for an atmosphere.json light (lane LOOK, fix 4). HdrpScene builds the light and copies the row's noise here; the base intensity is the built light's.
// Same Perlin fBm schema as FurnaceFlicker / DoorwayFlicker. A row with no flicker block (all zero) stays steady.
public class AtmosFlicker : MonoBehaviour
{
    public Light target;
    public FurnaceFlicker.Noise noise;
    float baseIntensity; bool steady;
    bool guard; float eps, lastI = float.NaN;   // PERF3 v100 D1

    void Awake()
    {
        if (target == null) target = GetComponent<Light>();
        if (target == null) { enabled = false; return; }
        baseIntensity = target.intensity;
        steady = noise.baseValue == 0f && noise.amplitude == 0f;
        guard = PerfFixes.On("D1"); eps = FlickerGuard.Eps;
    }

    void Update()
    {
        if (steady) return;
        float v = baseIntensity * Mathf.Max(0f, FurnaceFlicker.Eval(noise, Time.time));
        if (guard && !float.IsNaN(lastI) && !FlickerGuard.Moved(lastI, v, baseIntensity, eps)) return;   // PERF3 v100 D1: write only on change
        target.intensity = v; lastI = v;
    }
}
