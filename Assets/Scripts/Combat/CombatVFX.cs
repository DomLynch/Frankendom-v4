using UnityEngine;

// F3 hit VFX: impact sparks, dust puff, a subtle blood mist, and a short slash arc for heavy blows. Driven by ContactRelay (no gameplay logic).
public class CombatVFX : MonoBehaviour
{
    public Material spark, dust, blood;
    ParticleSystem sparks, puff, mist, guardSparks, flash, drops;

    void OnEnable() { ContactRelay.OnContact += OnContact; }
    void OnDisable() { ContactRelay.OnContact -= OnContact; }

    void Start()
    {
        // HDRP/Unlit ignores particle vertex colour/alpha (grey-white cards that never fade): rebuild the three materials on Furnace/Particle (fx_materials.json roles spark/dust/blood)
        spark = FxMaterials.Particle(spark, "spark"); dust = FxMaterials.Particle(dust, "dust"); blood = FxMaterials.Particle(blood, "blood");
        // streaky velocity-stretched spark lines (white-yellow core to orange, short life, gravity), a brief small flash, dark-red blood droplets and a small dust puff
        sparks = Make("ImpactSparks", spark, 0.015f, 0.03f, 0.1f, 0.25f, 4f, 10f, new Color(1f, 0.9f, 0.55f), -1.0f);
        guardSparks = Make("GuardSparks", spark, 0.015f, 0.03f, 0.1f, 0.25f, 4f, 9f, new Color(1f, 0.97f, 0.8f), -1.0f);
        Stretch(sparks, new Color(1f, 0.95f, 0.7f), new Color(1f, 0.45f, 0.1f)); Stretch(guardSparks, new Color(1f, 1f, 0.9f), new Color(1f, 0.55f, 0.15f));
        flash = Make("HitFlash", spark, 0.25f, 0.35f, 0.05f, 0.08f, 0f, 0f, new Color(1f, 0.85f, 0.6f, 0.8f), 0f);
        drops = Make("BloodDrops", blood, 0.02f, 0.04f, 0.4f, 0.8f, 2f, 5f, new Color(0.145f, 0.008f, 0.008f, 1f), -1.2f);   // #250202, alpha-blended billboards (no stretch needles)
        { var dr = drops.GetComponent<ParticleSystemRenderer>(); dr.renderMode = ParticleSystemRenderMode.Billboard; dr.maxParticleSize = 0.06f; }
        puff = Make("DustPuff", dust, 0.3f, 0.6f, 0.6f, 1.0f, 0.3f, 0.9f, new Color(0.4f, 0.4f, 0.42f, 0.28f), -0.05f);
        mist = Make("BloodMist", blood, 0.03f, 0.07f, 0.35f, 0.6f, 1.5f, 3.5f, new Color(0.22f, 0.015f, 0.015f, 0.95f), -1.2f);
    }

    void Stretch(ParticleSystem ps, Color a, Color b, float len = 5.2f)
    {
        var r = ps.GetComponent<ParticleSystemRenderer>(); r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.06f; r.lengthScale = len; r.cameraVelocityScale = 0f; r.maxParticleSize = 0.5f;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 0.6f), new GradientColorKey(b, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.5f), new GradientAlphaKey(0f, 1f) }); col.color = g;
    }
    ParticleSystem Make(string n, Material m, float s0, float s1, float l0, float l1, float v0, float v1, Color c, float grav)
    {
        var go = new GameObject(n); go.transform.SetParent(transform);
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.startLifetime = new ParticleSystem.MinMaxCurve(l0, l1);
        main.startSpeed = new ParticleSystem.MinMaxCurve(v0, v1); main.startSize = new ParticleSystem.MinMaxCurve(s0, s1); main.startColor = c;
        main.gravityModifier = grav < 0 ? -grav : -grav; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 200;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.08f;
        var fade = ps.colorOverLifetime; fade.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0, 1) }); fade.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m; r.maxParticleSize = 0.25f;
        return ps;
    }

    void Burst(ParticleSystem ps, Vector3 pos, Vector3 dir, int count, float cone)
    {
        if (ps == null) return;
        ps.transform.position = pos; ps.transform.rotation = Quaternion.LookRotation(dir == Vector3.zero ? Vector3.up : dir);
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = cone; sh.radius = 0.05f;
        ps.Emit(count);
    }

    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        Vector3 pos = d.transform.position + Vector3.up * (1.1f * Mathf.Max(1f, d.scale));
        Vector3 dir = (d.transform.position - a.transform.position); dir.y = 0.15f; dir.Normalize();
        switch (type)
        {
            case "Hit":
                if (d.name.Contains("Pitborn")) Burst(flash, pos, dir, 1, 5f);
                Burst(drops, pos, dir, heavy ? 16 : 7, 40f);
                if (d.name.Contains("Pitborn")) Burst(sparks, pos, dir, heavy ? 9 : 4, 35f);   // bright sparks only on metal/armour
                Burst(puff, pos - Vector3.up * 0.4f, Vector3.up + dir * 0.3f, heavy ? 2 : 1, 60f);
                Burst(mist, pos, dir, heavy ? 5 : 2, 50f);
                break;
            case "Blocked": Burst(flash, pos, -dir, 1, 5f); Burst(guardSparks, pos, -dir + Vector3.up * 0.3f, perfect ? 22 : 12, 50f); break;
            case "Parried": Burst(guardSparks, pos, Vector3.up + dir * 0.4f, 30, 70f); break;
            case "GuardBroken": Burst(guardSparks, pos, dir, 36, 80f); Burst(puff, pos, dir, 6, 80f); break;
        }
    }
}




