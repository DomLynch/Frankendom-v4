using UnityEngine;

// V3 blood (blood-style.ts spawnBlood + blood-rows.json + armfeel gravity): a thin spray with a few heavier drops, stretched along their flight into strands.
// Only a landed Hit bleeds (isFleshHit). The foe's burst is pulled toward the camera along the camera->contact ray and grown (foeBurstPull / bloodGrow).
// Numbers: hit 15 / kill 25 particles, the first 2 / 3 are the heavy drops, colour #690f0d -> #200504, gravity 8 m/s^2, width x THIN .4095, length x LONG 2.4615.
public class BloodBurst : MonoBehaviour
{
    public Material material;
    public float sceneScale = 1.5f;       // V3 sizes are for a 1.8 m man on a phone; ours is a 1080p desktop view
    public DuelFighter hero;              // the player: his burst is not pulled / grown
    const float Thin = 0.7f * 0.65f * 0.9f, Long = 1.2f * 1.2f / (0.65f * 0.9f), Front = 0.35f, MinCam = 1.5f, GrowMax = 3f, Gravity = 8f;
    ParticleSystem ps; uint seed = 731;

    float Rnd() { unchecked { seed += 0x6d2b79f5u; uint t = (seed ^ (seed >> 15)) * (1u | seed); t ^= t + (t ^ (t >> 7)) * (61u | t); return (t ^ (t >> 14)) / 4294967296f; } }
    float Between(float lo, float hi) { return lo + (hi - lo) * Rnd(); }

    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }

    void Start()
    {
        material = FxMaterials.Particle(material, "bloodStrands");   // HDRP/Unlit would ignore the colour-over-lifetime and show white strands (fx_materials.json)
        var hb = HitFeelTable.Get().blood;   // laneHITFX v81: B1 fixes as data (hit_feel.json blood{}); the code defaults equal the v69 look
        if (material != null && hb.softDistance >= 0f && material.HasProperty("_SoftDistance")) material.SetFloat("_SoftDistance", hb.softDistance);   // laneHITFX v81: soft particles
        var go = new GameObject("BloodStrands"); go.transform.SetParent(transform); ps = go.AddComponent<ParticleSystem>();
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World; main.gravityModifier = Gravity / 9.81f; main.maxParticles = 400; main.startLifetime = 0.6f;
        var em = ps.emission; em.enabled = false; var sh = ps.shape; sh.enabled = false;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(hb.startColor, 0f), new GradientColorKey(hb.endColor, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.7f), new GradientAlphaKey(0f, 1f) }); col.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = material; r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = hb.lengthScale; r.velocityScale = hb.velocityScale;   // laneHITFX v81: was 1.6 / 0 in code
         r.cameraVelocityScale = 0f; r.maxParticleSize = 0.5f;
    }

    // PERF (FxPrewarm): one sub-pixel particle (uses no RNG, no sim state)
    public void PrewarmEmit(Vector3 pos, float size)
    {
        if (ps == null) return;
        var ep = new ParticleSystem.EmitParams(); ep.position = pos; ep.velocity = Vector3.zero; ep.startLifetime = 0.1f; ep.startSize = size; ep.startColor = Color.white;
        ps.Emit(ep, 1);
    }

    void OnEvent(CombatEvent e)
    {
        if (e.type != "Hit" || e.target == null || ps == null) return;
        bool kill = e.target.Dead; Vector3 dir = e.target.transform.position - e.actor.transform.position; dir.y = 0f; dir.Normalize();
        Vector3 pos = e.point; float grow = 1f;
        var cam = Camera.main;
        if (cam != null && hero != null && e.target != hero)
        {
            Vector3 cp = cam.transform.position; float far = Vector3.Distance(cp, pos), near = Vector3.Distance(cp, hero.transform.position + Vector3.up * 1.2f);
            float pull = Mathf.Max(0f, Mathf.Min(Mathf.Max(0f, far - near) + Front, far - MinCam));
            pos = pos - (pos - cp).normalized * pull;   // toward the camera, along the camera->contact ray
            grow = Mathf.Min(GrowMax, Mathf.Max(1f, far / Mathf.Max(0.01f, near)));
        }
        var bs = BloodTable.For(e.target, e.heavy, kill);   // P2: blood.json weight x location x victim multipliers (all 1 = V3 numbers)
        var hf = HitFeelTable.Get().blood; // laneHITFX v81
        int count = Mathf.Max(1, Mathf.RoundToInt((kill ? 25 : 15) * bs.count * hf.countScale)), drops = Mathf.Max(1, Mathf.RoundToInt((kill ? 3 : 2) * bs.drops));
        var ep = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            float a = Rnd() * Mathf.PI * 2f; Vector3 v; float life, size, stretch;
            if (i < drops) { life = Between(0.5f, 0.72f); size = Between(0.07f, 0.12f) * Thin; stretch = Between(1.1f, 1.5f) * Long; v = new Vector3(Mathf.Cos(a) * Between(0.3f, 1.1f) + dir.x * Between(0.3f, 1.0f), Between(1.6f, 3.0f), Mathf.Sin(a) * Between(0.3f, 1.1f) + dir.z * Between(0.3f, 1.0f)); }
            else { life = Between(0.14f, 0.4f); size = Between(0.018f, 0.05f) * Thin; stretch = Between(1.3f, 2.4f) * Long; float sp = Between(1.2f, 3.6f); v = new Vector3(Mathf.Cos(a) * sp * 0.6f + dir.x * Between(0.5f, 2.4f), Between(0.2f, 2.6f), Mathf.Sin(a) * sp * 0.6f + dir.z * Between(0.5f, 2.4f)); }
            ep.position = pos; ep.velocity = new Vector3(v.x * bs.speed, v.y * bs.speed * bs.up, v.z * bs.speed); ep.startLifetime = life; ep.startSize = size * grow * sceneScale * hf.sizeScale; ep.startColor = Color.white;
            ep.applyShapeToPosition = false; ps.Emit(ep, 1);
            // strand length = width x stretch: Stretch mode scales the length by the system's lengthScale, so average it per burst
        }
        var r = ps.GetComponent<ParticleSystemRenderer>(); r.lengthScale = hf.lengthScale;   // laneHITFX v81: was the literal 1.6
    }
}
