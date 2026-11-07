using UnityEngine;

// F3 hit VFX: impact sparks, dust puff, a subtle blood mist, and a short slash arc for heavy blows. Driven by ContactRelay (no gameplay logic).
public class CombatVFX : MonoBehaviour
{
    public Material spark, dust, blood;
    ParticleSystem sparks, puff, mist, guardSparks, flash, drops; HitVfxCfg.Data vx;   // HCKIT4 v95

    void OnEnable() { ContactRelay.OnContact += OnContact; }
    void OnDisable() { ContactRelay.OnContact -= OnContact; }

    void Start()
    {
        // HDRP/Unlit ignores particle vertex colour/alpha (grey-white cards that never fade): rebuild the three materials on Furnace/Particle (fx_materials.json roles spark/dust/blood)
        spark = FxMaterials.Particle(spark, "spark"); dust = FxMaterials.Particle(dust, "dust"); blood = FxMaterials.Particle(blood, "blood");
        // HCKIT4 v95 K10a: every number below is hit_vfx.json (HitVfxCfg); defaults = the old literals
        vx = HitVfxCfg.Get();
        sparks = Make("ImpactSparks", vx.sparks); guardSparks = Make("GuardSparks", vx.guardSparks); flash = Make("HitFlash", vx.flash);
        drops = Make("BloodDrops", vx.drops); puff = Make("DustPuff", vx.puff); mist = Make("BloodMist", vx.mist);
    }

    // PERF (FxPrewarm): one sub-pixel particle through each system (uses no RNG, no sim state)
    public void PrewarmEmit(Vector3 pos, float size)
    {
        var all = new[] { sparks, guardSparks, flash, drops, puff, mist };
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            var ep = new ParticleSystem.EmitParams(); ep.position = pos; ep.velocity = Vector3.zero; ep.startLifetime = HitVfxCfg.Get().prewarmLifeS; ep.startSize = size; ep.startColor = Color.white;   // HCKIT4 v95
            all[i].Emit(ep, 1);
        }
    }

    void Stretch(ParticleSystem ps, Color a, Color b)   // HCKIT4 v95 K10a
    {
        var sc = vx.stretchCfg;
        var r = ps.GetComponent<ParticleSystemRenderer>(); r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = sc.velocityScale; r.lengthScale = sc.lengthScale; r.cameraVelocityScale = sc.cameraVelocityScale; r.maxParticleSize = sc.maxParticleSize;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, sc.midAt), new GradientColorKey(b, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(sc.midAlpha, sc.midAlphaAt), new GradientAlphaKey(0f, 1f) }); col.color = g;
    }
    ParticleSystem Make(string n, HitVfxCfg.Sys s)
    {
        var cm = vx.common;
        Material m = s.material == "dust" ? dust : s.material == "blood" ? blood : spark;
        Color c = s.colorFrom == "dropColor" ? HitFeelTable.Get().blood.dropColor : s.colorFrom == "mistColor" ? HitFeelTable.Get().blood.mistColor : HitVfxCfg.C(s.color, Color.white);
        var go = new GameObject(n); go.transform.SetParent(transform);
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.startLifetime = new ParticleSystem.MinMaxCurve(s.lifeMin, s.lifeMax);
        main.startSpeed = new ParticleSystem.MinMaxCurve(s.speedMin, s.speedMax); main.startSize = new ParticleSystem.MinMaxCurve(s.sizeMin, s.sizeMax); main.startColor = c;
        main.gravityModifier = -s.gravity; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = cm.maxParticles;
        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = cm.shapeRadius;
        var fade = ps.colorOverLifetime; fade.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(cm.fadeMidAlpha, cm.fadeMidAt), new GradientAlphaKey(0, 1) }); fade.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = m; r.maxParticleSize = cm.rendererMaxSize;
        if (s.stretch) Stretch(ps, HitVfxCfg.C(s.stretchFrom, Color.white), HitVfxCfg.C(s.stretchTo, Color.white));
        if (s.billboard) { r.renderMode = ParticleSystemRenderMode.Billboard; r.maxParticleSize = s.billboardMaxSize; }
        return ps;
    }

    void Burst(ParticleSystem ps, Vector3 pos, Vector3 dir, int count, float cone)
    {
        if (ps == null || count <= 0) return;
        ps.transform.position = pos; ps.transform.rotation = Quaternion.LookRotation(dir == Vector3.zero ? Vector3.up : dir);
        var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = cone; sh.radius = vx.burstShapeRadius;   // HCKIT4 v95
        ps.Emit(count);
    }

    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        var x = HitVfxCfg.Get();   // HCKIT4 v95 K10a: heights, counts and cones from hit_vfx.json
        Vector3 pos = HitAnchor.Point(a, d, x.hitHeightM);   /* KITHIT v1 */
        Vector3 dir = (d.transform.position - a.transform.position); dir.y = x.hitDirY; dir.Normalize();
        switch (type)
        {
            case CombatClass.Hit:
                {
                    var h = x.hit; var bs = BloodTable.For(d, heavy, d.Dead);   // P2: blood.json (weight x location x victim); sparks / flash only where the victim row says metal
                    if (bs.flash) Burst(flash, pos, dir, h.flashCount, h.flashCone);
                    Burst(drops, pos, dir, Mathf.RoundToInt((heavy ? h.dropsHeavy : h.dropsLight) * bs.drops), h.dropsCone);
                    if (bs.sparks) Burst(sparks, pos, dir, heavy ? h.sparksHeavy : h.sparksLight, h.sparksCone);   // bright sparks only on metal/armour
                    Burst(puff, pos - Vector3.up * x.puffDropM, Vector3.up + dir * h.puffDirMix, heavy ? h.puffHeavy : h.puffLight, h.puffCone);
                    Burst(mist, pos, dir, Mathf.RoundToInt((heavy ? h.mistHeavy : h.mistLight) * bs.mist), h.mistCone);
                    break;
                }
            case CombatClass.Blocked: { var b = x.block; Burst(flash, pos, -dir, b.flashCount, b.flashCone); Burst(guardSparks, pos, -dir + Vector3.up * b.upMix, Mathf.RoundToInt((perfect ? b.sparksPerfect : b.sparks) * HitFeel.GuardSparkScale), b.sparksCone); break; }   // laneHITFX v81: HitFeel's HDR burst replaces these (guardSparkScale 0); 1 if HitFeel is off
            case CombatClass.Parried: { var p = x.parried; Burst(guardSparks, pos, Vector3.up + dir * p.dirMix, Mathf.RoundToInt(p.sparks * HitFeel.GuardSparkScale), p.cone); break; }
            case CombatClass.GuardBroken: { var g = x.guardBroken; Burst(guardSparks, pos, dir, Mathf.RoundToInt(g.sparks * HitFeel.GuardSparkScale), g.sparksCone); Burst(puff, pos, dir, g.puff, g.puffCone); break; }
        }
    }
}




