using System;
using System.Collections.Generic;
using UnityEngine;

// FOOTFX v1 (laneFOOTFX). NEW FILE, runtime only, UNPROVEN IN UNITY (not compiled here: read-only lane). Footfall puffs by ground surface for humans, monsters and the ridden mount.
// DARK by default: foot_fx.json enabled=false (or no file) => Boot returns, nothing is allocated. Arg -footfx:1|0 forces it on / off. Nothing is hard-coded: sizes, counts, colours, cooldowns, rate caps,
// foot-bone names, plant thresholds and the water detection all come from StreamingAssets\foot_fx.json; the surface of each zone is the optional field "surface" on zone_ground_mat.json rows
// (same zMin / zMax gating as ZoneGroundMat; JsonUtility ignores unknown fields, so ZoneGroundMat itself is untouched). Live reload: DataWatcher calls FootFx.Reload / FootFx.ReloadZones (rows in PATCH.md).
// Humans + monsters: every DuelFighter is scanned (rescanSeconds); its foot bones (names ending with cfg.footBones, e.g. mixamorig:LeftFoot) are sampled each frame: a foot that was lifted above liftY then drops under
// plantY (metres above the fighter root, at scale 1, scaled by the fighter's lossyScale) while the fighter moves >= minSpeedMps = one footfall. Mount: MountLegs.Apply calls FootFx.MountTick once per frame, a
// foot-plant is the stance start of a leg (its gait phase wraps past 1). Output: ONE pooled world-space ParticleSystem per surface (maxParticles = poolCapPerSurface, EmitParams, no per-frame allocation),
// the shared Furnace/Particle material of FxMaterials ("footFx" role, soft dot texture, vertex colour), no shadows, no lights. Characters are never touched (Joan's look unchanged).
public sealed class FootFx : MonoBehaviour
{
    [Serializable] public class Surf
    {
        public string id = ""; public float sizeMin = 0.10f, sizeMax = 0.22f; public int count = 4; public float lifetime = 0.9f; public float[] colour = { 0.5f, 0.45f, 0.4f };
        public float alpha = 0.3f, upSpeed = 0.5f, spread = 0.4f, gravity = 0f, grow = 2f, cooldown = 0.16f; public int maxPerSecond = 10;
    }
    [Serializable] public class Cfg
    {
        public bool enabled = false, log = true; public string[] footBones = { "LeftFoot", "RightFoot" }; public float plantY = 0.12f, liftY = 0.20f, minSpeedMps = 0.5f, maxSpeedMps = 30f, speedRef = 5f, speedBoost = 0.5f, sizeMul = 1f;
        public float maxDistM = 40f, rescanSeconds = 1.5f, waterRayM = 0.5f, mountSizeMul = 1f, mountMinSpeedMps = 0.5f; public int maxEmitPerFrame = 8, poolCapPerSurface = 96, maxFeetPerFighter = 6;
        public string defaultSurface = "stone", waterSurface = "shallowwater", zSource = "creature", materialRole = "footFx"; public string[] waterNames = { };
        public bool testBurst = false; public float testEverySec = 0.6f; public Surf[] surfaces;
    }
    [Serializable] public class ZRow { public string id = "", targetRoot = "", surface = ""; public float zMin = -1e9f, zMax = 1e9f, waterY = -1e9f; }
    [Serializable] class ZCfg { public ZRow[] rows; }

    sealed class SS { public Surf s; public ParticleSystem ps; public float winStart; public int winCount; public Color col; }
    sealed class Rig { public Transform root; public Transform[] feet; public float[] lastPlant; public bool[] armed; public Vector3 prev; public bool hasPrev; }

    const string File_ = "foot_fx.json", ZFile = "zone_ground_mat.json", ArgPrefix = "-footfx:";
    static FootFx inst;
    Cfg c; ZRow[] zrows = new ZRow[0];
    readonly Dictionary<string, SS> surf = new Dictionary<string, SS>();
    readonly List<Rig> rigs = new List<Rig>(); readonly HashSet<Transform> known = new HashSet<Transform>();
    readonly RaycastHit[] hits = new RaycastHit[6];
    ParticleSystem.EmitParams ep; Material mat; float nextScan, nextTest, now; int budget; bool forced; string lastTestSurface = "";
    // mount state (static call from MountLegs.Apply)
    Transform mountGo; readonly float[] mountPrev = new float[4]; readonly float[] mountLast = new float[4]; bool mountHas;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (inst != null) return;
            string a1 = null; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(ArgPrefix)) a1 = a.Substring(ArgPrefix.Length);
            var cfg = ReadCfg(); if (cfg == null) return;
            bool on = a1 != null ? a1 == "1" : cfg.enabled; if (!on) return;
            var go = new GameObject("FootFx"); DontDestroyOnLoad(go); inst = go.AddComponent<FootFx>(); inst.forced = a1 == "1"; inst.Apply(cfg); inst.LoadZones();
        }
        catch (Exception e) { Debug.LogWarning("[FootFx] boot failed: " + e.Message); }
    }

    static Cfg ReadCfg()
    {
        string t = DataPath.ReadText(File_); if (string.IsNullOrEmpty(t)) return null;
        try { var c = JsonUtility.FromJson<Cfg>(t.TrimStart((char)0xFEFF)); if (c != null && c.surfaces == null) c.surfaces = new Surf[0]; if (c != null && c.footBones == null) c.footBones = new string[0]; if (c != null && c.waterNames == null) c.waterNames = new string[0]; return c; }
        catch (Exception e) { Debug.LogWarning("[FootFx] " + File_ + " unreadable, keeping the last config: " + e.Message); return null; }
    }

    /// <summary>DataWatcher: foot_fx.json changed.</summary>
    public static void Reload() { var cfg = ReadCfg(); if (cfg == null) return; if (inst == null) { if (cfg.enabled) Boot(); return; } if (inst.forced) cfg.enabled = true; inst.Apply(cfg); }
    /// <summary>DataWatcher: zone_ground_mat.json changed (surface rows).</summary>
    public static void ReloadZones() { if (inst != null) inst.LoadZones(); }

    void LoadZones()
    {
        try
        {
            string t = DataPath.ReadText(ZFile); if (string.IsNullOrEmpty(t)) return;
            var z = JsonUtility.FromJson<ZCfg>(t.TrimStart((char)0xFEFF)); if (z == null || z.rows == null) return;
            var l = new List<ZRow>(); foreach (var r in z.rows) if (r != null && !string.IsNullOrEmpty(r.surface) && (r.zMin > -1e8f || r.zMax < 1e8f)) l.Add(r);   // only z-gated rows: the legacy ungated rows would match everywhere
            zrows = l.ToArray(); if (c != null && c.log) Debug.Log("[FootFx] " + zrows.Length + " zone surface rows from " + ZFile);
        }
        catch (Exception e) { Debug.LogWarning("[FootFx] " + ZFile + ": " + e.Message); }
    }

    void Apply(Cfg cfg)
    {
        c = cfg;
        foreach (var kv in surf) if (kv.Value.ps != null) Destroy(kv.Value.ps.gameObject);
        surf.Clear();
        if (!c.enabled) return;
        mat = FxMaterials.Particle(null, c.materialRole);
        if (mat == null) { Debug.LogWarning("[FootFx] no particle material (FxMaterials.Particle returned null: fx_materials.json off or Furnace/Particle missing): off"); c.enabled = false; return; }
        foreach (var s in c.surfaces) if (s != null && !string.IsNullOrEmpty(s.id)) surf[s.id] = Make(s);
        if (c.log) Debug.Log("[FootFx] armed: " + surf.Count + " surfaces, pool " + c.poolCapPerSurface + " each, default '" + c.defaultSurface + "'");
    }

    SS Make(Surf s)
    {
        var go = new GameObject("FootFx_" + s.id); go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = Mathf.Max(8, c.poolCapPerSurface);
        main.gravityModifier = s.gravity; main.startLifetime = s.lifetime; main.startSize = 0.1f; main.startSpeed = 0f; main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        var em = ps.emission; em.enabled = false; var sh = ps.shape; sh.enabled = false;
        var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, Mathf.Max(0.5f, s.grow * 0.5f))));
        var col = ps.colorOverLifetime; col.enabled = true; var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0f, 1f) }); col.color = g;
        var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = mat; r.renderMode = ParticleSystemRenderMode.Billboard; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        ps.Play();
        float cr = s.colour != null && s.colour.Length > 0 ? s.colour[0] : 0.5f, cg = s.colour != null && s.colour.Length > 1 ? s.colour[1] : cr, cb = s.colour != null && s.colour.Length > 2 ? s.colour[2] : cr;
        return new SS { s = s, ps = ps, col = new Color(cr, cg, cb, Mathf.Clamp01(s.alpha)) };
    }

    // ---------------------------------------------------------------- per frame
    void Update()
    {
        if (c == null || !c.enabled) return;
        float dt = Time.deltaTime; if (dt <= 0f) return; now = Time.time; budget = Mathf.Max(1, c.maxEmitPerFrame);
        var cam = Camera.main; Vector3 cp = cam != null ? cam.transform.position : Vector3.zero; float md2 = c.maxDistM * c.maxDistM;
        if (now >= nextScan) { nextScan = now + Mathf.Max(0.25f, c.rescanSeconds); Scan(); }
        for (int i = rigs.Count - 1; i >= 0; i--)
        {
            var rg = rigs[i]; if (rg.root == null) { rigs.RemoveAt(i); continue; }
            if (!rg.root.gameObject.activeInHierarchy) { rg.hasPrev = false; continue; }
            Vector3 p = rg.root.position; float speed = rg.hasPrev ? new Vector2(p.x - rg.prev.x, p.z - rg.prev.z).magnitude / dt : 0f; rg.prev = p; rg.hasPrev = true;
            if (speed < c.minSpeedMps || speed > c.maxSpeedMps) { ArmOnly(rg, p); continue; }   // standing, or a teleport / finisher snap
            if (cam != null && (p - cp).sqrMagnitude > md2) continue;
            float sc = Mathf.Max(0.1f, rg.root.lossyScale.y), plant = c.plantY * sc, lift = c.liftY * sc;
            for (int j = 0; j < rg.feet.Length; j++)
            {
                var f = rg.feet[j]; if (f == null) continue; float rel = f.position.y - p.y;
                if (rel >= lift) rg.armed[j] = true;
                else if (rg.armed[j] && rel <= plant)
                {
                    rg.armed[j] = false; if (budget <= 0) continue;
                    var fp = f.position; fp.y = p.y; Footfall(fp, ref rg.lastPlant[j], sc, speed, rg.root.position.z);
                }
            }
        }
        if (c.testBurst && now >= nextTest) { nextTest = now + Mathf.Max(0.1f, c.testEverySec); TestBurst(); }
    }

    void ArmOnly(Rig rg, Vector3 p) { float lift = c.liftY * Mathf.Max(0.1f, rg.root.lossyScale.y); for (int j = 0; j < rg.feet.Length; j++) { var f = rg.feet[j]; if (f != null && f.position.y - p.y >= lift) rg.armed[j] = true; } }

    void Scan()
    {
        known.RemoveWhere(t => t == null); var all = FindObjectsByType<DuelFighter>(FindObjectsSortMode.None);   // one small array per rescanSeconds, never per frame
        for (int i = 0; i < all.Length; i++)
        {
            var f = all[i]; if (f == null || known.Contains(f.transform)) continue;
            var list = new List<Transform>(); foreach (var t in f.GetComponentsInChildren<Transform>(true)) { if (list.Count >= c.maxFeetPerFighter) break; foreach (var s in c.footBones) if (!string.IsNullOrEmpty(s) && t.name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { list.Add(t); break; } }
            known.Add(f.transform);
            if (list.Count == 0) { if (c.log) Debug.Log("[FootFx] " + f.name + ": no foot bone matches footBones (add this rig's foot bone name suffixes to foot_fx.json)"); continue; }
            rigs.Add(new Rig { root = f.transform, feet = list.ToArray(), lastPlant = new float[list.Count], armed = new bool[list.Count] });
            if (c.log) { var sb = new System.Text.StringBuilder(); foreach (var t in list) sb.Append(t.name).Append(' '); Debug.Log("[FootFx] bound " + f.name + ": " + list.Count + " feet: " + sb); }
        }
    }

    // ---------------------------------------------------------------- mount hook
    /// <summary>MountLegs.Apply, right after the 4 hoof targets `tgt` (local to `go`) are computed: a leg whose phase wrapped this frame just started its stance = hoof plant.</summary>
    public static void MountTick(Transform go, float phase, float[] offs, float duty, Vector3[] tgt, float scale, float speed)
    {
        var s = inst; if (s == null || s.c == null || !s.c.enabled || go == null || offs == null || tgt == null) return;
        try
        {
            if (s.mountGo != go) { s.mountGo = go; s.mountHas = false; }
            float now = Time.time; s.now = now; s.budget = Mathf.Max(1, s.c.maxEmitPerFrame); bool move = speed >= s.c.mountMinSpeedMps; int n = Mathf.Min(4, Mathf.Min(offs.Length, tgt.Length));
            for (int i = 0; i < n; i++)
            {
                float ph = Mathf.Repeat(phase + offs[i], 1f);
                if (s.mountHas && move && ph < s.mountPrev[i] - 0.5f && s.budget > 0)
                {
                    var w = go.TransformPoint(tgt[i]); w.y = go.position.y; float sc = Mathf.Max(0.1f, scale) * s.c.mountSizeMul; s.Footfall(w, ref s.mountLast[i], sc, speed, go.position.z);
                }
                s.mountPrev[i] = ph;
            }
            s.mountHas = true;
        }
        catch (Exception e) { Debug.LogWarning("[FootFx] mount tick: " + e.Message); }
    }

    // ---------------------------------------------------------------- surface + emit
    void Footfall(Vector3 pos, ref float last, float scale, float speed, float creatureZ)
    {
        string sid = SurfaceAt(pos, creatureZ); SS ss; if (!surf.TryGetValue(sid, out ss)) return;
        if (now - last < ss.s.cooldown) return;
        if (now - ss.winStart >= 1f) { ss.winStart = now; ss.winCount = 0; }
        if (ss.winCount >= ss.s.maxPerSecond) return;
        last = now; ss.winCount++; budget--;
        float k = scale * c.sizeMul * (1f + c.speedBoost * Mathf.Clamp01(speed / Mathf.Max(0.1f, c.speedRef)));
        var s = ss.s; ep.applyShapeToPosition = false; ep.startLifetime = s.lifetime; ep.startColor = ss.col;
        for (int i = 0; i < s.count; i++)
        {
            Vector2 d = UnityEngine.Random.insideUnitCircle;
            ep.position = pos + new Vector3(d.x, 0.03f, d.y) * (0.08f * scale);
            ep.velocity = new Vector3(d.x * s.spread, s.upSpeed * UnityEngine.Random.Range(0.6f, 1f), d.y * s.spread) * Mathf.Sqrt(scale);
            ep.startSize = UnityEngine.Random.Range(s.sizeMin, s.sizeMax) * k;
            ss.ps.Emit(ep, 1);
        }
    }

    string SurfaceAt(Vector3 pos, float creatureZ)
    {
        float z = creatureZ; if (string.Equals(c.zSource, "camera", StringComparison.OrdinalIgnoreCase)) { var cam = Camera.main; if (cam != null) z = cam.transform.position.z; }
        string sid = c.defaultSurface; ZRow hit = null;
        for (int i = 0; i < zrows.Length; i++) { var r = zrows[i]; if (z >= r.zMin && z <= r.zMax) { hit = r; sid = r.surface; break; } }   // same inclusive zMin/zMax test as ZoneGroundMat.Gate
        if (!string.IsNullOrEmpty(c.waterSurface))
        {
            if (hit != null && hit.waterY > -1e8f && pos.y < hit.waterY) return c.waterSurface;
            if (c.waterNames.Length > 0 && IsWater(pos)) return c.waterSurface;
        }
        return sid;
    }

    bool IsWater(Vector3 pos)
    {
        int n = Physics.RaycastNonAlloc(pos + Vector3.up * 0.3f, Vector3.down, hits, c.waterRayM + 0.3f, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++) { var col = hits[i].collider; if (col == null) continue; foreach (var w in c.waterNames) if (!string.IsNullOrEmpty(w) && col.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true; }
        return false;
    }

    // harness: cfg.testBurst (or any caller) puffs at the player's feet whether or not he walks, so one still shot shows the effect in the zone he stands in
    void TestBurst()
    {
        foreach (var rg in rigs)
        {
            if (rg.root == null || rg.feet.Length == 0 || rg.feet[0] == null || rg.root.name != Roster.PlayerObject) continue;
            var fp = rg.feet[0].position; fp.y = rg.root.position.y; float sc = Mathf.Max(0.1f, rg.root.lossyScale.y); string sid = SurfaceAt(fp, rg.root.position.z);
            if (sid != lastTestSurface) { lastTestSurface = sid; Debug.Log("[FootFx] test burst surface=" + sid + " z=" + rg.root.position.z.ToString("F1")); }
            float dummy = -999f; budget = c.maxEmitPerFrame; Footfall(fp, ref dummy, sc, c.speedRef, rg.root.position.z); return;
        }
    }
}
