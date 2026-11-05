using UnityEngine;

// Fire light flicker: layered noise on intensity, small positional wobble.
// lanePERF3 D1: with the D1 fix on, intensity and position are written only when they moved (perf_fixes.json D1.epsilonRel x the base); a light whose transform / intensity is written every frame costs CPU in HDRP's light update.
public class Flicker : MonoBehaviour
{
    public float baseIntensity = 300f, amount = 0.25f, speed = 7f;
    Light l; Vector3 p0; float seed;
    bool guard; float eps, lastI = float.NaN; Vector3 lastOff = new Vector3(float.NaN, 0f, 0f);   // PERF3 v100 D1
    const float Wobble = 0.15f;   // positional swing (m), as before
    void Start() { l = GetComponent<Light>(); p0 = transform.localPosition; seed = Random.value * 100f; guard = PerfFixes.On("D1"); eps = FlickerGuard.Eps; }
    void Update()
    {
        float n = Mathf.PerlinNoise(seed, Time.time * speed) * 0.7f + Mathf.PerlinNoise(seed + 9f, Time.time * speed * 2.7f) * 0.3f;
        float intensity = baseIntensity * (1f - amount + n * amount * 2f);
        Vector3 off = new Vector3(n - 0.5f, (n - 0.5f) * 0.5f, 0f) * Wobble;
        if (guard)   // PERF3 v100 D1: write only on change
        {
            if (float.IsNaN(lastI) || FlickerGuard.Moved(lastI, intensity, baseIntensity, eps)) { l.intensity = intensity; lastI = intensity; }
            if (float.IsNaN(lastOff.x) || (off - lastOff).sqrMagnitude > (eps * Wobble) * (eps * Wobble)) { transform.localPosition = p0 + off; lastOff = off; }
            return;
        }
        l.intensity = intensity;
        transform.localPosition = p0 + off;
    }
}
