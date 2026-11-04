using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ALPHA lane B, Unity layer: data loader, per-fighter wound runtime (location roll, severity, effects), accumulating blood decals attached to bones, blood sprays.
// Presentation only. Never touches DuelFighter / Fighter / RagdollDeath state; it reads bones from the fighter's model and adds child quads.
// Data: duel_rules.json "wounds" block (lookup order Resources, StreamingAssets, Assets/Data like DodgeTable). If that file or block is missing,
// Assets/Data/alpha_wounds.json (same shape, {"wounds": {...}}) is used, then the class defaults.

public static class ADWoundsData
{
    [Serializable] class Root { public ADWoundsCfg wounds; }
    static ADWoundsCfg cfg;
    public static ADWoundsCfg Get() { if (cfg == null) Load(); return cfg; }
    public static void Reload() { cfg = null; Load(); }

    static string Read(string file)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(file);
            var ta = Resources.Load<TextAsset>(name);
            if (ta != null) return ta.text;
            string p = Path.Combine(Application.streamingAssetsPath, file);
            if (File.Exists(p)) return File.ReadAllText(p);
            p = Path.Combine(Application.dataPath, "Data/" + file);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        catch (Exception ex) { Debug.LogWarning("ADWounds: read " + file + " failed: " + ex.Message); }
        return null;
    }

    static void Load()
    {
        foreach (var file in new[] { "duel_rules.json", "alpha_wounds.json" })
        {
            string json = Read(file); if (string.IsNullOrEmpty(json)) continue;
            try
            {
                json = json.TrimStart((char)0xFEFF);
                var r = JsonUtility.FromJson<Root>(json);
                if (r != null && r.wounds != null) { Fix(r.wounds); cfg = r.wounds; return; }
            }
            catch (Exception ex) { Debug.LogError("ADWounds: parse " + file + " failed: " + ex.Message); }
        }
        cfg = new ADWoundsCfg();
    }

    static void Fix(ADWoundsCfg c)
    {
        if (c.hitWeights == null) c.hitWeights = new ADWoundWeightRow[0];
        if (c.locations == null || c.locations.Length < ADWoundRules.Count) c.locations = new ADWoundsCfg().locations;
        if (c.effects == null) c.effects = new ADWoundEffects();
        if (c.gore == null) c.gore = new ADGoreCfg();
        if (c.gore.locs == null) c.gore.locs = new ADGoreLoc[0];
        if (c.gore.woundSpray == null) c.gore.woundSpray = new ADSprayCfg();
        if (c.gore.neckSpray == null) c.gore.neckSpray = new ADSprayCfg();
        if (c.cuts == null) c.cuts = new ADCutsCfg();
        if (c.cuts.finishers == null) c.cuts.finishers = new ADCutFinisher[0];
        if (c.cuts.bindings == null) c.cuts.bindings = new ADCutBinding[0];
        if (c.cuts.units == null) c.cuts.units = new ADCutUnit[0];
    }
}

/// <summary>Shared procedural blood art: splat textures, the decal material, the spray particle system.</summary>
public static class ADGoreFx
{
    static Texture2D[] splats; static Material decalMat, sprayMat; static Mesh quad; static bool warned;

    public static Mesh Quad()
    {
        if (quad != null) return quad;
        quad = new Mesh { name = "ADBloodQuad" };
        quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
        quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        quad.RecalculateBounds();
        return quad;
    }

    // soft irregular splat: a few random discs plus satellite drops, premultiplied alpha (Furnace/Particle blend One, OneMinusSrcAlpha)
    public static Texture2D[] Splats(ADGoreCfg g)
    {
        if (splats != null) return splats;
        int n = Mathf.Clamp(g.texSize, 16, 256);
        splats = new Texture2D[4];
        for (int v = 0; v < splats.Length; v++)
        {
            var rnd = new System.Random(g.seed * 31 + v * 7919);
            var a = new float[n * n];
            int blobs = 5 + rnd.Next(4);
            for (int b = 0; b < blobs; b++)
            {
                float cx = 0.5f + (float)(rnd.NextDouble() - 0.5) * 0.5f, cy = 0.5f + (float)(rnd.NextDouble() - 0.5) * 0.5f;
                float r = 0.07f + (float)rnd.NextDouble() * (b == 0 ? 0.22f : 0.12f);
                AddBlob(a, n, cx, cy, r);
            }
            int drops = 6 + rnd.Next(8);
            for (int d = 0; d < drops; d++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2, dist = 0.25 + rnd.NextDouble() * 0.2;
                AddBlob(a, n, 0.5f + (float)(Math.Cos(ang) * dist), 0.5f + (float)(Math.Sin(ang) * dist), 0.012f + (float)rnd.NextDouble() * 0.03f);
            }
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                float al = Mathf.Clamp01(a[i] * 1.6f);
                byte al8 = (byte)Mathf.RoundToInt(al * 255f);
                px[i] = new Color32(al8, al8, al8, al8);   // white premultiplied; tint colour comes from the material
            }
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "ADSplat" + v, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(px); t.Apply(false, true);
            splats[v] = t;
        }
        return splats;
    }

    static void AddBlob(float[] a, int n, float cx, float cy, float r)
    {
        int x0 = Mathf.Max(0, (int)((cx - r) * n)), x1 = Mathf.Min(n - 1, (int)((cx + r) * n) + 1), y0 = Mathf.Max(0, (int)((cy - r) * n)), y1 = Mathf.Min(n - 1, (int)((cy + r) * n) + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f) / n - cx, dy = (y + 0.5f) / n - cy;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                if (d >= 1f) continue;
                float v = Mathf.Clamp01((1f - d) * 3f);
                if (v > a[y * n + x]) a[y * n + x] = v;
            }
    }

    /// <summary>Decal material (Furnace/Particle, premultiplied alpha, tinted dark red). Null when the shader is missing.</summary>
    public static Material DecalMaterial(ADGoreCfg g, int variant)
    {
        var sh = FxMaterials.FindParticleShader();
        if (sh == null) { if (!warned) { warned = true; Debug.LogWarning("ADGore: Furnace/Particle shader not found; blood decals disabled."); } return null; }
        var tex = Splats(g)[variant % 4];
        var m = new Material(sh) { name = "ADBloodDecal" + variant };
        m.SetTexture("_MainTex", tex);
        var c = g.color; m.SetColor("_Tint", new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f));
        m.SetFloat("_Intensity", 1f); m.SetFloat("_ExposureWeight", 0f); m.SetFloat("_SoftDistance", 0f);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 10f);
        m.renderQueue = 3000;
        return m;
    }

    static Material[] decalMats;
    public static Material SharedDecalMaterial(ADGoreCfg g, int variant)
    {
        if (decalMats == null) decalMats = new Material[4];
        int i = variant & 3;
        if (decalMats[i] == null) decalMats[i] = DecalMaterial(g, i);
        return decalMats[i];
    }

    /// <summary>Directional blood spray (stretched strands like BloodBurst). The emitter is a child of `parent` so it follows the body. Returns null if no material.</summary>
    public static ParticleSystem Spray(Transform parent, Vector3 worldPos, Vector3 worldDir, ADSprayCfg c, float secondsOverride = 0f)
    {
        if (sprayMat == null) sprayMat = FxMaterials.Particle(null, "bloodStrands");
        if (sprayMat == null) return null;
        float secs = secondsOverride > 0f ? secondsOverride : c.seconds;
        var go = new GameObject("ADBloodSpray");
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        go.transform.rotation = Quaternion.LookRotation(worldDir.sqrMagnitude > 1e-6f ? worldDir.normalized : Vector3.up);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.duration = secs; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = c.gravity / 9.81f; main.maxParticles = 600;
        main.startLifetime = new ParticleSystem.MinMaxCurve(c.life[0], c.life[1]);
        main.startSpeed = new ParticleSystem.MinMaxCurve(c.speed[0], c.speed[1]);
        main.startSize = new ParticleSystem.MinMaxCurve(c.size[0], c.size[1]);
        var em = ps.emission; em.enabled = true;
        var decay = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.05f));   // jet tapers to drips
        em.rateOverTime = new ParticleSystem.MinMaxCurve(c.rate, decay);
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = c.coneDeg; sh.radius = 0.01f;
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(new Color(0.412f, 0.059f, 0.051f), 0f), new GradientColorKey(new Color(0.125f, 0.02f, 0.016f), 1f) },
                  new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = sprayMat; r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = 3.2f; r.velocityScale = 0f; r.cameraVelocityScale = 0f; r.maxParticleSize = 0.5f;
        ps.Play();
        UnityEngine.Object.Destroy(go, secs + c.life[1] + 2f);
        return ps;
    }
}

/// <summary>Per-fighter wound state + gore. Add (or ADWoundsRuntime.For) on Dracula and Pitborn. Lane A calls RegisterHit per landed hit and reads HeavyDamageMul / SpeedTierPenalty.</summary>
public class ADWoundsRuntime : MonoBehaviour
{
    public struct DecalRec { public int loc; public string bone; public Vector3 localPos; public float size; }

    public ADWoundSet wounds;
    public readonly List<DecalRec> decals = new List<DecalRec>();   // the per-location blood decal list (for replay / save)
    readonly List<GameObject>[] decalObjs = { new List<GameObject>(), new List<GameObject>(), new List<GameObject>(), new List<GameObject>() };
    Dictionary<string, Transform> lookup; Transform modelRoot; System.Random rng; ADWoundsCfg cfg;
    public ADWoundsCfg Cfg { get { return cfg; } }
    public Transform ModelRoot { get { EnsureInit(); return modelRoot; } }

    public static ADWoundsRuntime For(GameObject fighter) { if (!ADGoreGate.Gore || fighter == null) return null; var r = fighter.GetComponent<ADWoundsRuntime>(); return r != null ? r : fighter.AddComponent<ADWoundsRuntime>(); }

    void Awake() { EnsureInit(); }
    void EnsureInit()
    {
        if (cfg != null) return;
        cfg = ADWoundsData.Get();
        wounds = new ADWoundSet(cfg, this);
        rng = new System.Random(cfg.seed + name.GetHashCode());
        var f = GetComponent<Fighter>();
        modelRoot = f != null && f.model != null ? f.model : transform;
    }

    public static string Key(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int c = n.LastIndexOf(':'); if (c >= 0) n = n.Substring(c + 1);
        return n.ToLowerInvariant();
    }

    public Transform FindBone(string[] names)
    {
        EnsureInit();
        if (lookup == null)
        {
            lookup = new Dictionary<string, Transform>();
            var all = modelRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++) { string k = Key(all[i].name); if (k.Length > 0 && !lookup.ContainsKey(k)) lookup[k] = all[i]; }
        }
        if (names == null) return null;
        for (int i = 0; i < names.Length; i++) { Transform t; if (lookup.TryGetValue(Key(names[i]), out t)) return t; }
        return null;
    }

    float Rnd() { return (float)rng.NextDouble(); }

    // ---------------- rules-facing API ----------------
    public float HeavyDamageMul() { EnsureInit(); return wounds.HeavyDamageMul(); }
    public float SpeedTierPenalty() { EnsureInit(); return wounds.SpeedTierPenalty(); }
    public int Severity(int loc) { EnsureInit(); return wounds.severity[loc]; }

    /// <summary>Roll the hit location for a landed hit of `move` ("light","slash","stab","heavy","kick","special"), register the wound, show the gore.
    /// Returns the location int (0 Arm, 1 Leg, 2 Torso, 3 Head); dmgOut is the damage after the location multiplier (lane A may use it or ignore it).</summary>
    public int RegisterHit(string move, float dmg, bool crit, out float dmgOut)
    {
        if (!ADGoreGate.Wounds) { dmgOut = dmg; return 2; }
        EnsureInit();
        int loc = ADWoundRules.RollLocation(cfg, move, Rnd);
        dmgOut = dmg * ADWoundRules.LocationDamageMul(cfg, loc);
        ApplyAt(loc, dmgOut, crit);
        return loc;
    }

    /// <summary>Register a hit at a location the rules already chose (ADBeat.locA / locB).</summary>
    public bool ApplyAt(int loc, float dmg, bool crit)
    {
        if (!ADGoreGate.Wounds) return false;
        EnsureInit();
        bool up = wounds.Apply(loc, dmg, crit);
        if (!cfg.gore.enabled || !ADGoreGate.Gore) return up;
        if (up)
        {
            int s = wounds.severity[loc];
            int n = cfg.gore.decalsBySeverity != null && s < cfg.gore.decalsBySeverity.Length ? cfg.gore.decalsBySeverity[s] : s * 3;
            AddDecals(loc, Mathf.RoundToInt(n * (crit ? cfg.gore.critMul : 1f)), s);
        }
        else AddDecals(loc, crit ? 2 : 1, wounds.severity[loc]);
        return up;
    }

    /// <summary>Splash `count` more decals on a location (finishers call this at the cut).</summary>
    public void Splash(int loc, int count) { if (!ADGoreGate.Gore) return; EnsureInit(); AddDecals(loc, count, 3); }

    ADGoreLoc GoreLoc(int loc)
    {
        string n = ADWoundRules.Names[loc];
        for (int i = 0; i < cfg.gore.locs.Length; i++) if (cfg.gore.locs[i] != null && cfg.gore.locs[i].name == n) return cfg.gore.locs[i];
        return null;
    }

    void AddDecals(int loc, int count, int severity)
    {
        var g = cfg.gore; var gl = GoreLoc(loc);
        if (gl == null || count <= 0) return;
        var list = decalObjs[loc];
        for (int i = 0; i < count; i++)
        {
            Transform bone = null;
            for (int tries = 0; tries < 4 && bone == null && gl.bones.Length > 0; tries++) bone = FindBone(new[] { gl.bones[(int)(Rnd() * gl.bones.Length) % gl.bones.Length] });
            if (bone == null) return;
            var mat = ADGoreFx.SharedDecalMaterial(g, (int)(Rnd() * 4f));
            if (mat == null) return;

            // point on the limb segment (bone -> first child), pushed out to the skin radius on the front arc
            Vector3 a = bone.position, b = bone.childCount > 0 ? bone.GetChild(0).position : a + bone.up * 0.2f;
            Vector3 axis = b - a; if (axis.sqrMagnitude < 1e-6f) axis = bone.up; axis.Normalize();
            float t = Mathf.Lerp(gl.along[0], gl.along[1], Rnd());
            Vector3 p = Vector3.Lerp(a, b, t);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, axis); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(transform.right, axis); fwd.Normalize();
            Vector3 side = Vector3.Cross(axis, fwd);
            float ang = (Rnd() - 0.5f) * Mathf.Min(360f, gl.frontArcDeg) * Mathf.Deg2Rad;
            Vector3 dir = (Mathf.Cos(ang) * fwd + Mathf.Sin(ang) * side).normalized;
            float size = Mathf.Lerp(g.sizeM[0], g.sizeM[1], Rnd()) * (1f + 0.25f * severity);

            var go = new GameObject("ADBlood_" + ADWoundRules.Names[loc]);
            go.AddComponent<MeshFilter>().sharedMesh = ADGoreFx.Quad();
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            go.transform.position = p + dir * (gl.radiusM + g.quadLift);
            go.transform.rotation = Quaternion.LookRotation(dir, axis) * Quaternion.Euler(0f, 0f, Rnd() * 360f);
            go.transform.localScale = Vector3.one * size;
            go.transform.SetParent(bone, true);   // keeps world pose and size; follows the bone from now on
            list.Add(go);
            decals.Add(new DecalRec { loc = loc, bone = bone.name, localPos = go.transform.localPosition, size = size });
            while (list.Count > g.maxDecalsPerLoc) { if (list[0] != null) Destroy(list[0]); list.RemoveAt(0); if (decals.Count > 0) decals.RemoveAt(0); }
        }
    }

    /// <summary>Clear all gore and wounds (rematch / next opponent).</summary>
    public void ResetGore()
    {
        if (!ADGoreGate.Wounds && !ADGoreGate.Gore) return;
        EnsureInit();
        for (int l = 0; l < decalObjs.Length; l++) { for (int i = 0; i < decalObjs[l].Count; i++) if (decalObjs[l][i] != null) Destroy(decalObjs[l][i]); decalObjs[l].Clear(); }
        decals.Clear(); wounds.Reset();
    }

    /// <summary>Spray a short wound burst at a location (e.g. on the big hit). Uses the woundSpray block.</summary>
    public void WoundSpray(int loc, Vector3 awayDir)
    {
        if (!ADGoreGate.Gore) return;
        EnsureInit();
        var gl = GoreLoc(loc); var bone = gl != null ? FindBone(gl.bones) : null; if (bone == null) return;
        ADGoreFx.Spray(bone, bone.position, awayDir + Vector3.up * 0.3f, cfg.gore.woundSpray);
    }
}
