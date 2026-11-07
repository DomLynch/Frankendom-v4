using UnityEngine;

// V3 blood (blood-style.ts spawnBlood + blood-rows.json + armfeel gravity): a thin spray with a few heavier drops, stretched along their flight into strands.
// Only a landed Hit bleeds (isFleshHit). The foe's burst is pulled toward the camera along the camera->contact ray and grown (foeBurstPull / bloodGrow).
// Numbers: hit 15 / kill 25 particles, the first 2 / 3 are the heavy drops, colour #690f0d -> #200504, gravity 8 m/s^2, width x THIN .4095, length x LONG 2.4615.
public class BloodBurst : MonoBehaviour
{
    public Material material;
    public float sceneScale = 1.5f;       // V3 sizes are for a 1.8 m man on a phone; ours is a 1080p desktop view
    public DuelFighter hero;              // the player: his burst is not pulled / grown
    BloodShapes.Burst bc { get { return BloodShapes.Get().burst; } }   // HCKIT6 v100: Thin / Long / Front / MinCam / GrowMax / Gravity and the spray ranges are blood_shapes.json burst{} (class defaults = the old literals)
    ParticleSystem ps; uint seed; bool seeded;   // HCKIT6 v100: seed = blood_shapes.json burst.seed (was 731), read on first use (no data access in a MonoBehaviour field initializer)

    float Rnd() { if (!seeded) { seeded = true; seed = (uint)bc.seed; } unchecked { seed += 0x6d2b79f5u; uint t = (seed ^ (seed >> 15)) * (1u | seed); t ^= t + (t ^ (t >> 7)) * (61u | t); return (t ^ (t >> 14)) / 4294967296f; } }
    float Between(float lo, float hi) { return lo + (hi - lo) * Rnd(); }
    float Between(float[] r) { return r[0] + (r[1] - r[0]) * Rnd(); }   // HCKIT6 v100

    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }

    void Start()
    {
        material = FxMaterials.Particle(material, "bloodStrands");   // HDRP/Unlit would ignore the colour-over-lifetime and show white strands (fx_materials.json)
        var hb = HitFeelTable.Get().blood;   // laneHITFX v81: B1 fixes as data (hit_feel.json blood{}); the code defaults equal the v69 look
        if (material != null && hb.softDistance >= 0f && material.HasProperty("_SoftDistance")) material.SetFloat("_SoftDistance", hb.softDistance);   // laneHITFX v81: soft particles
        var go = new GameObject("BloodStrands"); go.transform.SetParent(transform); ps = go.AddComponent<ParticleSystem>();
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World; main.gravityModifier = bc.gravity / bc.gravityRef; main.maxParticles = bc.maxParticles; main.startLifetime = bc.startLifetime;   // HCKIT6 v100
        var em = ps.emission; em.enabled = false; var sh = ps.shape; sh.enabled = false;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(hb.startColor, 0f), new GradientColorKey(hb.endColor, 1f) }, new[] { new GradientAlphaKey(bc.alphaKeys[0], bc.alphaKeys[1]), new GradientAlphaKey(bc.alphaKeys[2], bc.alphaKeys[3]), new GradientAlphaKey(bc.alphaKeys[4], bc.alphaKeys[5]) }); col.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = material; r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = hb.lengthScale; r.velocityScale = hb.velocityScale;   // laneHITFX v81: was 1.6 / 0 in code
         r.cameraVelocityScale = bc.cameraVelocityScale; r.maxParticleSize = bc.maxParticleSize;   // HCKIT6 v100
    }

    // PERF (FxPrewarm): one sub-pixel particle (uses no RNG, no sim state)
    public void PrewarmEmit(Vector3 pos, float size)
    {
        if (ps == null) return;
        var ep = new ParticleSystem.EmitParams(); ep.position = pos; ep.velocity = Vector3.zero; ep.startLifetime = HitVfxCfg.Get().prewarmLifeS; ep.startSize = size; /* DATALAYER b12 */ ep.startColor = Color.white;
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
            Vector3 cp = cam.transform.position; float far = Vector3.Distance(cp, pos), near = Vector3.Distance(cp, hero.transform.position + Vector3.up * bc.pullHeight);
            float pull = Mathf.Max(0f, Mathf.Min(Mathf.Max(0f, far - near) + bc.front, far - bc.minCam));
            pos = pos - (pos - cp).normalized * pull;   // toward the camera, along the camera->contact ray
            grow = Mathf.Min(bc.growMax, Mathf.Max(1f, far / Mathf.Max(bc.nearEps, near)));
        }
        var bs = BloodTable.For(e.target, e.heavy, kill);   // P2: blood.json weight x location x victim multipliers (all 1 = V3 numbers)
        var hf = HitFeelTable.Get().blood; // laneHITFX v81
        int count = Mathf.Max(1, Mathf.RoundToInt((kill ? bc.killCount : bc.hitCount) * bs.count * hf.countScale)), drops = Mathf.Max(1, Mathf.RoundToInt((kill ? bc.killDrops : bc.hitDrops) * bs.drops));   // HCKIT6 v100
        float Thin = bc.Thin(), Long = bc.Long(); var dr = bc.drop; var sp2 = bc.spray;
        var ep = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            float a = Rnd() * Mathf.PI * 2f; Vector3 v; float life, size, stretch;
            if (i < drops) { life = Between(dr.life); size = Between(dr.size) * Thin; stretch = Between(dr.stretch) * Long; v = new Vector3(Mathf.Cos(a) * Between(dr.radial) + dir.x * Between(dr.along), Between(dr.up), Mathf.Sin(a) * Between(dr.radial) + dir.z * Between(dr.along)); }   // HCKIT6 v100: ranges from blood_shapes.json (same RNG order)
            else { life = Between(sp2.life); size = Between(sp2.size) * Thin; stretch = Between(sp2.stretch) * Long; float sp = Between(sp2.speed); v = new Vector3(Mathf.Cos(a) * sp * sp2.radialMul + dir.x * Between(sp2.along), Between(sp2.up), Mathf.Sin(a) * sp * sp2.radialMul + dir.z * Between(sp2.along)); }
            ep.position = pos; ep.velocity = new Vector3(v.x * bs.speed, v.y * bs.speed * bs.up, v.z * bs.speed); ep.startLifetime = life; ep.startSize = size * grow * sceneScale * hf.sizeScale; ep.startColor = Color.white;
            ep.applyShapeToPosition = false; ps.Emit(ep, 1);
            // strand length = width x stretch: Stretch mode scales the length by the system's lengthScale, so average it per burst
        }
        var r = ps.GetComponent<ParticleSystemRenderer>(); r.lengthScale = hf.lengthScale;   // laneHITFX v81: was the literal 1.6
    }
}
