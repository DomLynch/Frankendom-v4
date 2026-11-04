using UnityEngine;

// Fire light flicker: layered noise on intensity, small positional wobble.
public class Flicker : MonoBehaviour
{
    public float baseIntensity = 300f, amount = 0.25f, speed = 7f;
    Light l; Vector3 p0; float seed;
    void Start() { l = GetComponent<Light>(); p0 = transform.localPosition; seed = Random.value * 100f; }
    void Update()
    {
        float n = Mathf.PerlinNoise(seed, Time.time * speed) * 0.7f + Mathf.PerlinNoise(seed + 9f, Time.time * speed * 2.7f) * 0.3f;
        l.intensity = baseIntensity * (1f - amount + n * amount * 2f);
        transform.localPosition = p0 + new Vector3(n - 0.5f, (n - 0.5f) * 0.5f, 0f) * 0.15f;
    }
}
