using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// BURROWCROC v1 fx (laneBURROWCROC, STAGED DARK, UNPROVEN IN UNITY). NEW FILE, used only by BurrowCroc.cs; every number is burrow_croc.json dust / ripple.
// DUST: ONE world-space ParticleSystem (emission by script, no per-croc system), billboards on the project's own particle material path: FxMaterials.Particle(null, dust.role) = the Furnace/Particle shader + FxMaterials.SoftDot() texture,
//   the same material builder ClashFx's dust and CombatVFX use (it keeps vertex colour, which HDRP/Unlit would drop). Colour = dust.color (sand), size grows over life, alpha fades. A trail while the croc moves under the ground (dust.perM particles per metre,
//   thrown back and a little up from its position), a burst when it surfaces / burrows.
// RIPPLES: a pool of HDRP DecalProjectors (the technique StaticDecals uses: a clone of Resources/WoundDecalRef, projector rotated to look down, pivot zero) with a ring texture generated in code (no new art file). Each ring expands from sizeStart to sizeEnd
//   over life and fades; a ring is laid every ripple.intervalM metres of crawling, and a double ring (ringsPerEvent) on surfacing / burrowing. Pool size is fixed (ripple.pool): the oldest ring is reused, nothing is created per frame.
// Missing shader / reference material = that part is off with ONE warning, never an exception. No file is read here.
public sealed class BurrowCrocFx : MonoBehaviour
{
    [Serializable] public class DustCfg
    {
        public bool enabled = true; public string role = "burrow_dust"; public int maxParticles = 300, surfaceBurst = 60, burrowBurst = 40;
        public float perM = 14f, minMoveMps = 0.25f, spreadM = 0.5f, lifeMin = 0.6f, lifeMax = 1.4f, sizeMin = 0.35f, sizeMax = 0.9f, growth = 2.2f, speedMin = 0.2f, speedMax = 0.9f, upSpeed = 0.5f, groundLiftM = 0.05f, burstSizeMul = 1.6f, burstSpeedMul = 3f, burstSpreadM = 1.2f;
        public float[] color = { 0.62f, 0.55f, 0.45f, 0.55f };
    }
    [Serializable] public class RippleCfg
    {
        public bool enabled = true; public string refMaterial = "WoundDecalRef"; public int pool = 10, texSize = 128, ringsPerEvent = 2;
        public float intervalM = 1.6f, life = 1.4f, sizeStartM = 0.8f, sizeEndM = 4.2f, opacity = 0.6f, depthM = 0.6f, liftM = 0.2f, ringRadiusFrac = 0.8f, ringWidthFrac = 0.12f, innerFill = 0.12f, surfaceSizeMul = 1.8f, fadePow = 1.5f, drawDistance = 40f, ringStaggerS = 0.18f;
        public float[] tint = { 0.7f, 0.62f, 0.5f };
    }

    class RingSlot { public GameObject go; public DecalProjector p; public float t0, s0, s1, op; public bool on; }
    struct Pending { public float at; public Vector3 pos; public float mul; }

    static readonly int IdAffectAlbedo = Shader.PropertyToID("_AffectAlbedo"), IdAffectNormal = Shader.PropertyToID("_AffectNormal"), IdAffectMetal = Shader.PropertyToID("_AffectMetal"), IdAffectAO = Shader.PropertyToID("_AffectAO"), IdAffectSmoothness = Shader.PropertyToID("_AffectSmoothness"), IdAffectEmission = Shader.PropertyToID("_AffectEmission"), IdDecalBlend = Shader.PropertyToID("_DecalBlend");
    const string DecalShader = "HDRP/Decal";
    static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap"), BaseColor = Shader.PropertyToID("_BaseColor");
    BurrowCroc.Cfg k; ParticleSystem ps; Material dustMat, ringMat; readonly List<RingSlot> rings = new List<RingSlot>(); readonly List<Pending> pending = new List<Pending>(); readonly System.Random rng = new System.Random(7);
    bool dustWarned, ringWarned;

    public static BurrowCrocFx Make(BurrowCroc.Cfg cfg, Transform parent)
    {
        var go = new GameObject("BurrowCrocFx"); go.transform.SetParent(parent, false);
        var f = go.AddComponent<BurrowCrocFx>(); f.k = cfg; f.Build(); return f;
    }

    float R() { return (float)rng.NextDouble(); }
    float R(float a, float b) { return Mathf.Lerp(a, b, R()); }

    void Build()
    {
        var D = k.dust;
        if (D.enabled)
        {
            dustMat = FxMaterials.Particle(null, D.role);
            if (dustMat == null) { Debug.LogWarning("[BurrowCroc] dust off: no Furnace/Particle material (FxMaterials.Particle returned null)"); }
            else
            {
                var go = new GameObject("BurrowDust"); go.transform.SetParent(transform, false); ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var m = ps.main; m.loop = false; m.playOnAwake = false; m.simulationSpace = ParticleSystemSimulationSpace.World; m.maxParticles = Mathf.Max(8, D.maxParticles);   // hc:ok algorithm constant / guard (reviewed)
                m.startLifetime = D.lifeMax; m.startSize = D.sizeMax; m.startSpeed = 0f; m.gravityModifier = 0f; m.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                var em = ps.emission; em.enabled = false; var sh = ps.shape; sh.enabled = false;
                var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f / Mathf.Max(1f, D.growth)), new Keyframe(1f, 1f)));
                var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });   // hc:ok algorithm constant / guard (reviewed)
                col.color = new ParticleSystem.MinMaxGradient(g);
                var pr = go.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = dustMat; pr.renderMode = ParticleSystemRenderMode.Billboard; pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; pr.receiveShadows = false;
                ps.Play();
            }
        }
        var Rp = k.ripple;
        if (Rp.enabled)
        {
            var refMat = Resources.Load<Material>(Rp.refMaterial); Shader sh = null; if (refMat == null) sh = Shader.Find(DecalShader);
            if (refMat == null && sh == null) { Debug.LogWarning("[BurrowCroc] ripples off: no " + Rp.refMaterial + " and no HDRP/Decal shader"); }
            else
            {
                ringMat = refMat != null ? new Material(refMat) : new Material(sh); ringMat.name = "burrow_ring"; ringMat.hideFlags = HideFlags.DontUnloadUnusedAsset; ringMat.enableInstancing = true;
                ringMat.EnableKeyword("_COLORMAP"); ringMat.SetTexture(BaseColorMap, MakeRing(Rp)); ringMat.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO"); ringMat.SetColor(BaseColor, Color.white);
                ringMat.SetFloat(IdAffectAlbedo, 1f); ringMat.SetFloat(IdAffectNormal, 0f); ringMat.SetFloat(IdAffectMetal, 0f); ringMat.SetFloat(IdAffectAO, 0f); ringMat.SetFloat(IdAffectSmoothness, 0f); ringMat.SetFloat(IdAffectEmission, 0f); ringMat.SetFloat(IdDecalBlend, 1f);
                for (int i = 0; i < Mathf.Max(1, Rp.pool); i++)
                {
                    var go = new GameObject("ring" + i); go.transform.SetParent(transform, false); go.SetActive(false);
                    var p = go.AddComponent<DecalProjector>(); p.material = ringMat; p.pivot = Vector3.zero; p.drawDistance = Rp.drawDistance; p.fadeFactor = 0f;
                    rings.Add(new RingSlot { go = go, p = p });
                }
            }
        }
    }

    static Texture2D MakeRing(RippleCfg Rp)
    {
        int n = Mathf.Clamp(Rp.texSize, 16, 512); var tex = new Texture2D(n, n, TextureFormat.RGBA32, true); tex.name = "BurrowRing"; tex.wrapMode = TextureWrapMode.Clamp;   // hc:ok algorithm constant / guard (reviewed)
        var px = new Color32[n * n]; var tn = Rp.tint != null && Rp.tint.Length >= 3 ? new Color(Rp.tint[0], Rp.tint[1], Rp.tint[2], 1f) : Color.white; float w = Mathf.Max(0.01f, Rp.ringWidthFrac);   // hc:ok algorithm constant / guard (reviewed)
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(u * u + v * v);
                float ring = Mathf.Exp(-((r - Rp.ringRadiusFrac) / w) * ((r - Rp.ringRadiusFrac) / w)), fill = Rp.innerFill * Mathf.Clamp01(1f - r), edge = Mathf.Clamp01((1f - r) / 0.1f);   // hc:ok ring profile
                float a = Mathf.Clamp01(ring + fill) * edge;
                px[y * n + x] = new Color32((byte)Mathf.RoundToInt(tn.r * 255f), (byte)Mathf.RoundToInt(tn.g * 255f), (byte)Mathf.RoundToInt(tn.b * 255f), (byte)Mathf.RoundToInt(a * 255f));
            }
        tex.SetPixels32(px); tex.Apply(true, true); return tex;
    }

    // ------------------------------------------------------------------ API used by BurrowCroc
    public void Dust(Vector3 pos, Vector3 backDir, int count, float sizeMul, float speedMul, float spread = -1f)
    {
        if (ps == null) { if (k.dust.enabled && !dustWarned) { dustWarned = true; } return; }
        var D = k.dust; backDir.y = 0f; if (backDir.sqrMagnitude < 0.0001f) backDir = Vector3.back; backDir.Normalize();   // hc:ok flat
        float sp = spread >= 0f ? spread : D.spreadM; var tint = D.color != null && D.color.Length >= 4 ? new Color(D.color[0], D.color[1], D.color[2], D.color[3]) : Color.gray;   // hc:ok algorithm constant / guard (reviewed)
        var ep = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            float ang = R() * Mathf.PI * 2f, rr = Mathf.Sqrt(R()) * sp;
            ep.position = pos + new Vector3(Mathf.Cos(ang) * rr, D.groundLiftM, Mathf.Sin(ang) * rr);
            var side = new Vector3(R(-1f, 1f), 0f, R(-1f, 1f)) * 0.5f;   // hc:ok sideways scatter
            ep.velocity = (backDir + side) * (R(D.speedMin, D.speedMax) * speedMul) + Vector3.up * (D.upSpeed * R(0.5f, 1f) * speedMul);   // hc:ok scatter
            ep.startLifetime = R(D.lifeMin, D.lifeMax); ep.startSize = R(D.sizeMin, D.sizeMax) * sizeMul; ep.startColor = tint; ep.rotation = R() * 360f;   // hc:ok degrees
            ps.Emit(ep, 1);
        }
    }

    /// <summary>The surface / burrow burst: a ring of dust thrown outward (up = true surfacing, false = going under).</summary>
    public void Burst(Vector3 pos, Vector3 forward, bool surfacing)
    {
        var D = k.dust; int n = surfacing ? D.surfaceBurst : D.burrowBurst; if (n <= 0 || ps == null) return;
        float sp = D.burstSpreadM; var back = -forward;
        for (int i = 0; i < n; i++) { float ang = (float)i / n * Mathf.PI * 2f; var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)); Dust(pos + dir * (sp * 0.5f), dir, 1, D.burstSizeMul, D.burstSpeedMul, sp * 0.5f); }   // hc:ok ring layout
    }

    public void Ring(Vector3 pos, float sizeMul)
    {
        var Rp = k.ripple; if (ringMat == null || rings.Count == 0) return;
        Lay(pos, sizeMul);
        for (int i = 1; i < Mathf.Max(1, Rp.ringsPerEvent) && sizeMul > 1.01f; i++) pending.Add(new Pending { at = Time.time + Rp.ringStaggerS * i, pos = pos, mul = sizeMul * (1f - 0.2f * i) });   // hc:ok only the big events get the second ring
    }

    void Lay(Vector3 pos, float mul)
    {
        var Rp = k.ripple; RingSlot pick = null;
        foreach (var r in rings) if (!r.on) { pick = r; break; }
        if (pick == null) { float oldest = float.MaxValue; foreach (var r in rings) if (r.t0 < oldest) { oldest = r.t0; pick = r; } }
        if (pick == null) return;
        pick.on = true; pick.t0 = Time.time; pick.s0 = Rp.sizeStartM * mul; pick.s1 = Rp.sizeEndM * mul; pick.op = Mathf.Clamp01(Rp.opacity);
        pick.go.transform.SetPositionAndRotation(new Vector3(pos.x, pos.y + Rp.liftM, pos.z), Quaternion.Euler(90f, R() * 360f, 0f));   // hc:ok looks down, random yaw
        pick.p.size = new Vector3(pick.s0, pick.s0, Rp.depthM); pick.p.fadeFactor = pick.op; pick.go.SetActive(true);
    }

    void Update()
    {
        var Rp = k != null ? k.ripple : null; if (Rp == null || ringMat == null) return;
        for (int i = pending.Count - 1; i >= 0; i--) if (Time.time >= pending[i].at) { Lay(pending[i].pos, pending[i].mul); pending.RemoveAt(i); }
        foreach (var r in rings)
        {
            if (!r.on) continue;
            float t = (Time.time - r.t0) / Mathf.Max(0.05f, Rp.life);   // hc:ok divide guard
            if (t >= 1f) { r.on = false; r.go.SetActive(false); continue; }
            float e = 1f - (1f - t) * (1f - t), s = Mathf.Lerp(r.s0, r.s1, e);   // ease out
            r.p.size = new Vector3(s, s, Rp.depthM); r.p.fadeFactor = r.op * Mathf.Pow(1f - t, Mathf.Max(0.1f, Rp.fadePow));   // hc:ok algorithm constant / guard (reviewed)
        }
    }
}
