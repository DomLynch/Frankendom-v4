using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// FOLIAGE v1 (laneFOLIAGE, STAGED DARK, UNPROVEN IN UNITY). NEW FILE, nothing else calls it (S1 may call Foliage.FindTell / Foliage.Twitch).
// What: crossed-card clumps (N alpha-CLIP quads, pivot at the base) of reeds / dead grass from data. One shared runtime-built mesh and ONE shared HDRP/Lit material per variant (alpha clip, double sided,
// instancing on, no shadow casting by default): the SRP Batcher / instancing gives about one draw per variant. Rows (zone, x, z, yaw, scale, phase, tell) come from foliage.json (gen_foliage.py), are created once per zone the first
// time the player is within wakeMarginM of the zone (spawnPerFrame per frame) under one container per zone, switched off again beyond despawnMarginM. y = one down ray (zone yExpect +- yTolM, else retried then dropped), or a fixed y (row fixY).
// Wind: NO vertex shader (stock HDRP Lit has no vertex wind and a ShaderGraph would cost more): a tiny per-clump transform tilt around the base, ONLY for clumps within sway.nearM of the camera (nearest maxMovers), rescanned every scanS.
// S1 hook: rows with tell=true are the tell clumps. Foliage.FindTell(pos, radius, out id, out at) finds the nearest spawned tell clump; Foliage.Twitch(id) / Foliage.TwitchNear(pos, r) shake it (and its group within twitch.groupRadiusM).
// Data: foliage.json (StreamingAssets, then Assets/Data via DataPath), textures StreamingAssets/<texDir>/<tex>.png. Args: -foliage:0|1 (+ ",t" = twitch demo of every tell clump). enabled=false and no arg = nothing is allocated.
public sealed class Foliage : MonoBehaviour
{
    [Serializable] public class Variant { public string id, tex; public float widthM = 1f, heightM = 1f, alphaCutoff = 0.4f, smoothness = 0.1f; public int cards = 3; public float[] tint; }
    [Serializable] public class ZoneCfg { public string id; public float zMin, zMax, yExpect, yTolM = 0.45f; public bool enabled = true; public int maxClumps = 100; }
    [Serializable] public class Row { public string id, zone, v; public float x, z, yaw, s = 1f, ph, y; public bool tell, fixY; }
    [Serializable] public class CardsCfg { public float normalUpBlend = 0.65f, baseSinkM = 0.04f, cardYawStepDeg = 60f; }
    [Serializable] public class MatCfg { public bool alphaClipOn = true, doubleSided = true, enableInstancing = true, castShadows, receiveShadows = true; public float metallic; public int zTestForOpaque = 4, anisoLevel = 4; }
    [Serializable] public class SwayCfg { public bool enabled = true; public float secondPeriodMul = 0.61f, secondPhaseMul = 3f, nearM = 25f, amplitudeDeg = 2f, periodS = 3f, periodJitterFrac = 0.25f, windYawDeg, secondAxisFrac = 0.45f, scanS = 0.4f; public int maxMovers = 96; }
    [Serializable] public class TwitchCfg { public float durationS = 0.9f, amplitudeDeg = 9f, freqHz = 11f, decayPower = 1.5f, groupRadiusM = 0.9f, demoEveryS = 3f, secondAxisFrac = 0.6f; }
    [Serializable] public class TellCfg { public float searchRadiusM = 8f; }
    [Serializable] public class GroundCfg { public float rayUpM = 3f, rayDownM = 6f, liftM; public int retryPolls = 6; }
    [Serializable] public class StreamCfg { public float pollSeconds = 0.5f, wakeMarginM = 45f, despawnMarginM = 70f, drawDistanceM = 55f, minIntervalS = 0.05f, minScale = 0.05f; public int spawnPerFrame = 8, maxTotal = 560; }
    [Serializable] public class ProofCfg { public string argPrefix = "-foliage:", argOn = "1", argOff = "0", argTwitch = "t"; }
    [Serializable] public class Cfg
    {
        public bool enabled, log = true; public int version = 1; public string texDir = "Foliage", refMaterial = "", shader = "HDRP/Lit";
        public CardsCfg cards = new CardsCfg(); public MatCfg material = new MatCfg(); public SwayCfg sway = new SwayCfg(); public TwitchCfg twitch = new TwitchCfg(); public TellCfg tell = new TellCfg();
        public GroundCfg ground = new GroundCfg(); public StreamCfg stream = new StreamCfg(); public ProofCfg proof = new ProofCfg(); public float avoidPadM = 5f;
        public Variant[] variants; public ZoneCfg[] zones; public Row[] clumps;
    }

    class Clump
    {
        public Row row; public Transform t; public Renderer r; public Quaternion baseRot; public float period, ph, twitchT = -1f, twitchPh; public bool mover, shown = true; public ZoneState zone;
    }
    class ZoneState { public ZoneCfg cfg; public GameObject root; public List<Row> rows = new List<Row>(); public int next, spawned, dropped, retries; public List<Row> retry = new List<Row>(); public bool done, logged; public List<Clump> clumps = new List<Clump>(); }
    class VarState { public Variant v; public Mesh mesh; public Material mat; }

    const string FileName = "foliage.json", DataDir = "Data"; static readonly Vector4 MirrorConstants = new Vector4(1f, 1f, -1f, 0f); const int HitBuf = 16, QuadVerts = 4; const char Bom = (char)0xFEFF; static readonly int[] QuadTri = { 0, 2, 1, 2, 3, 1 };
    static readonly List<Clump> tells = new List<Clump>();
    static Foliage inst;
    Cfg c; bool demoTwitch; float nextPoll, nextScan, nextDemo; Transform who; Camera cam;
    readonly List<ZoneState> zs = new List<ZoneState>(); readonly Dictionary<string, VarState> vars = new Dictionary<string, VarState>(); readonly List<Clump> movers = new List<Clump>(); readonly List<Clump> twitching = new List<Clump>();
    int total, matLogged; static readonly RaycastHit[] hits = new RaycastHit[HitBuf];
    static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap"), BaseColor = Shader.PropertyToID("_BaseColor");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { tells.Clear(); inst = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            Cfg cfg = null; string txt = DataPath.ReadText(FileName);
            if (txt != null) { try { cfg = JsonUtility.FromJson<Cfg>(txt.TrimStart(Bom)); } catch (Exception e) { Debug.LogWarning("[Foliage] read failed, off: " + e.Message); } }
            if (cfg == null || cfg.clumps == null || cfg.clumps.Length == 0 || cfg.variants == null || cfg.zones == null) return;
            bool on = cfg.enabled, demo = false;
#if !FRANKEN_NO_HARNESS
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (string.IsNullOrEmpty(cfg.proof.argPrefix) || !a.StartsWith(cfg.proof.argPrefix, StringComparison.Ordinal)) continue;
                foreach (var tok in a.Substring(cfg.proof.argPrefix.Length).Split(','))
                {
                    if (tok == cfg.proof.argOn) on = true; else if (tok == cfg.proof.argOff) on = false; else if (tok == cfg.proof.argTwitch) demo = true;
                }
            }
#endif
            if (!on || inst != null) return;
            var go = new GameObject("Foliage"); DontDestroyOnLoad(go); var f = go.AddComponent<Foliage>(); f.c = cfg; f.demoTwitch = demo; inst = f;
        }
        catch (Exception e) { Debug.LogWarning("FOLIAGE boot failed: " + e.Message); }
    }

    void Start()
    {
        foreach (var v in c.variants) if (v != null && !string.IsNullOrEmpty(v.id)) vars[v.id] = new VarState { v = v };
        foreach (var z in c.zones) { if (z == null || !z.enabled) continue; zs.Add(new ZoneState { cfg = z }); }
        foreach (var r in c.clumps)
        {
            if (r == null || total >= c.stream.maxTotal || !vars.ContainsKey(r.v)) continue;
            foreach (var s in zs) if (s.cfg.id == r.zone && s.rows.Count < s.cfg.maxClumps) { s.rows.Add(r); total++; break; }
        }
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnScene;
        if (c.log) Debug.Log("FOLIAGE armed: " + total + " clumps, " + vars.Count + " variants, " + zs.Count + " zones, sway " + (c.sway.enabled ? "on" : "off") + (demoTwitch ? ", twitch demo" : ""));
    }
    void OnDestroy() { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnScene; if (inst == this) inst = null; tells.Clear(); }
    void OnScene(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m)
    {
        foreach (var z in zs) { if (z.root != null) Destroy(z.root); z.root = null; z.next = 0; z.spawned = 0; z.dropped = 0; z.retries = 0; z.retry.Clear(); z.clumps.Clear(); z.done = false; z.logged = false; }
        movers.Clear(); twitching.Clear(); tells.Clear();
    }

    Transform Who()
    {
        if (who != null) return who;
        var f = FindFirstObjectByType<DuelFighter>(); if (f != null) { who = f.transform; return who; }
        return null;
    }
    Vector3 CamPos()
    {
        if (cam == null) cam = Camera.main;
        if (cam != null) return cam.transform.position;
        var w = Who(); return w != null ? w.position : Vector3.zero;
    }

    void Update()
    {
        var w = Who(); if (w == null && Camera.main == null) return;
        float now = Time.unscaledTime; float pz = w != null ? w.position.z : CamPos().z;
        bool busy = false; foreach (var z in zs) if (z.root != null && z.root.activeSelf && !z.done) { busy = true; break; }
        if (busy || now >= nextPoll)
        {
            if (now >= nextPoll)
            {
                nextPoll = now + Mathf.Max(c.stream.pollSeconds, c.stream.minIntervalS);
                foreach (var z in zs)
                {
                    bool near = pz > z.cfg.zMin - c.stream.wakeMarginM && pz < z.cfg.zMax + c.stream.wakeMarginM, far = pz < z.cfg.zMin - c.stream.despawnMarginM || pz > z.cfg.zMax + c.stream.despawnMarginM;
                    if (z.root == null) { if (!near || z.rows.Count == 0) continue; z.root = new GameObject("Foliage_" + z.cfg.id); z.root.transform.SetParent(transform, false); }
                    else if (z.root.activeSelf && far) z.root.SetActive(false);
                    else if (!z.root.activeSelf && near) z.root.SetActive(true);
                }
            }
            SpawnBudget();
        }
        if (c.sway.enabled || demoTwitch) { if (now >= nextScan) { nextScan = now + Mathf.Max(c.sway.scanS, c.stream.minIntervalS); Scan(); } }
        if (demoTwitch && now >= nextDemo) { nextDemo = now + Mathf.Max(c.twitch.demoEveryS, c.stream.minIntervalS); foreach (var t in tells) StartTwitch(t); }
        Animate(Time.time);
    }

    void SpawnBudget()
    {
        int budget = Mathf.Max(1, c.stream.spawnPerFrame);
        foreach (var z in zs)
        {
            if (z.root == null || !z.root.activeSelf || z.done) continue;
            while (budget > 0 && z.next < z.rows.Count) { budget--; var r = z.rows[z.next++]; if (!Place(z, r)) z.retry.Add(r); }
            if (z.next >= z.rows.Count && budget > 0 && z.retry.Count > 0)
            {
                if (z.retries >= c.ground.retryPolls) { z.dropped += z.retry.Count; z.retry.Clear(); }
                else { var again = new List<Row>(z.retry); z.retry.Clear(); z.retries++; foreach (var r in again) { budget--; if (!Place(z, r)) z.retry.Add(r); if (budget <= 0) break; } }
            }
            if (z.next >= z.rows.Count && z.retry.Count == 0 && !z.done)
            {
                z.done = true;
                if (c.log && !z.logged) { z.logged = true; Debug.Log("FOLIAGE " + z.cfg.id + ": " + z.spawned + " placed, " + z.dropped + " dropped (no ground within " + z.cfg.yTolM.ToString("F2") + " m of y " + z.cfg.yExpect.ToString("F2") + "), of " + z.rows.Count); }
            }
            if (budget <= 0) break;
        }
    }

    bool Place(ZoneState z, Row r)
    {
        try
        {
            var vs = Var(r.v); if (vs == null || vs.mesh == null || vs.mat == null) { z.dropped++; return true; }
            float gy = r.y; if (!r.fixY && !Ground(r.x, r.z, z.cfg, out gy)) return false;
            var go = new GameObject(string.IsNullOrEmpty(r.id) ? "fol" : r.id); go.transform.SetParent(z.root.transform, false);
            go.transform.position = new Vector3(r.x, gy + c.ground.liftM, r.z); var rot = Quaternion.Euler(0f, r.yaw, 0f); go.transform.rotation = rot; go.transform.localScale = Vector3.one * Mathf.Max(r.s, c.stream.minScale);
            go.AddComponent<MeshFilter>().sharedMesh = vs.mesh; var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = vs.mat;
            mr.shadowCastingMode = c.material.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off; mr.receiveShadows = c.material.receiveShadows;
            mr.lightProbeUsage = LightProbeUsage.Off; mr.reflectionProbeUsage = ReflectionProbeUsage.Off; mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            var cl = new Clump { row = r, t = go.transform, r = mr, baseRot = rot, ph = r.ph, zone = z };
            cl.period = c.sway.periodS * (1f + (r.ph * 2f - 1f) * c.sway.periodJitterFrac);
            z.clumps.Add(cl); if (r.tell) tells.Add(cl);
            z.spawned++; return true;
        }
        catch (Exception e) { z.dropped++; if (c.log) Debug.LogWarning("FOLIAGE place failed " + r.id + ": " + e.Message); return true; }
    }

    bool Ground(float x, float zz, ZoneCfg zc, out float gy)
    {
        gy = zc.yExpect; int n = Physics.RaycastNonAlloc(new Vector3(x, zc.yExpect + c.ground.rayUpM, zz), Vector3.down, hits, c.ground.rayUpM + c.ground.rayDownM, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < n; i++)
        {
            var col = hits[i].collider; if (col == null || col.GetComponentInParent<DuelFighter>() != null) continue;
            if (hits[i].distance < best) { best = hits[i].distance; gy = hits[i].point.y; found = true; }
        }
        return found && Mathf.Abs(gy - zc.yExpect) <= zc.yTolM;
    }

    // ---- wind: nearest clumps only; everything else stays at its base rotation
    void Scan()
    {
        var cp = CamPos(); float nearSq = c.sway.nearM * c.sway.nearM, drawSq = c.stream.drawDistanceM * c.stream.drawDistanceM;
        foreach (var m in movers) { m.mover = false; }
        var cand = new List<KeyValuePair<float, Clump>>();
        foreach (var z in zs)
        {
            if (z.root == null || !z.root.activeSelf) continue;
            foreach (var k in z.clumps)
            {
                float d = (k.t.position - cp).sqrMagnitude; bool show = d <= drawSq;
                if (show != k.shown) { k.shown = show; k.r.enabled = show; }
                if (show && c.sway.enabled && d <= nearSq) cand.Add(new KeyValuePair<float, Clump>(d, k));
            }
        }
        if (cand.Count > c.sway.maxMovers) cand.Sort((a, b) => a.Key.CompareTo(b.Key));
        int take = Mathf.Min(cand.Count, c.sway.maxMovers); var old = new List<Clump>(movers); movers.Clear();
        for (int i = 0; i < take; i++) { var k = cand[i].Value; k.mover = true; movers.Add(k); }
        foreach (var o in old) if (!o.mover && o.twitchT < 0f && o.t != null) o.t.rotation = o.baseRot;
    }

    void Animate(float t)
    {
        float tau = Mathf.PI * 2f, wy = c.sway.windYawDeg * Mathf.Deg2Rad, cx = Mathf.Cos(wy), sx = Mathf.Sin(wy);
        for (int i = 0; i < movers.Count; i++)
        {
            var k = movers[i]; if (k.t == null) continue;
            float a = c.sway.amplitudeDeg * Mathf.Sin(tau * (t / k.period + k.ph)), b = c.sway.amplitudeDeg * c.sway.secondAxisFrac * Mathf.Sin(tau * (t / (k.period * c.sway.secondPeriodMul) + k.ph * c.sway.secondPhaseMul));
            float tx = a * cx - b * sx, tz = a * sx + b * cx;
            if (k.twitchT >= 0f) { Twitching(k, ref tx, ref tz); }
            k.t.rotation = Quaternion.Euler(tx, 0f, tz) * k.baseRot;
        }
        for (int i = twitching.Count - 1; i >= 0; i--)
        {
            var k = twitching[i]; if (k.t == null) { twitching.RemoveAt(i); continue; }
            if (k.twitchT < 0f) { twitching.RemoveAt(i); if (!k.mover) k.t.rotation = k.baseRot; continue; }
            if (k.mover) { continue; }   // already animated above (twitch term included)
            float tx = 0f, tz = 0f; Twitching(k, ref tx, ref tz); k.t.rotation = Quaternion.Euler(tx, 0f, tz) * k.baseRot;
        }
    }
    void Twitching(Clump k, ref float tx, ref float tz)
    {
        k.twitchT += Time.deltaTime; float u = k.twitchT / Mathf.Max(c.twitch.durationS, c.stream.minIntervalS);
        if (u >= 1f) { k.twitchT = -1f; return; }
        float env = Mathf.Pow(1f - u, c.twitch.decayPower), s = c.twitch.amplitudeDeg * env * Mathf.Sin(Mathf.PI * 2f * c.twitch.freqHz * k.twitchT + k.twitchPh);
        tx += s; tz += s * c.twitch.secondAxisFrac;
    }
    void StartTwitch(Clump k) { if (k == null || k.t == null) return; k.twitchT = 0f; k.twitchPh = k.ph * Mathf.PI * 2f; if (!twitching.Contains(k)) twitching.Add(k); }

    // ---- S1 hooks
    /// <summary>Nearest spawned tell clump (tell:true row) within radius of pos (radius <= 0 = tell.searchRadiusM). False when foliage is off or no tell clump is spawned yet.</summary>
    public static bool FindTell(Vector3 pos, float radius, out string id, out Vector3 at)
    {
        id = null; at = Vector3.zero; if (inst == null) return false; float rr = radius > 0f ? radius : inst.c.tell.searchRadiusM, best = rr * rr;
        foreach (var k in tells) { if (k.t == null) continue; float d = (k.t.position - pos).sqrMagnitude; if (d <= best) { best = d; id = k.row.id; at = k.t.position; } }
        return id != null;
    }
    /// <summary>Twitch the clump with this row id and the clumps of its group (within twitch.groupRadiusM). Returns false when off / unknown id.</summary>
    public static bool Twitch(string id)
    {
        if (inst == null || string.IsNullOrEmpty(id)) return false; Clump hit = null;
        foreach (var z in inst.zs) foreach (var k in z.clumps) if (k.row.id == id) { hit = k; break; }
        if (hit == null) return false; inst.TwitchGroup(hit); return true;
    }
    /// <summary>Twitch the nearest tell clump within radius of pos (see FindTell). Returns its id or null.</summary>
    public static string TwitchNear(Vector3 pos, float radius)
    {
        string id; Vector3 at; if (!FindTell(pos, radius, out id, out at)) return null; return Twitch(id) ? id : null;
    }
    void TwitchGroup(Clump hit)
    {
        float rr = c.twitch.groupRadiusM * c.twitch.groupRadiusM; StartTwitch(hit);
        foreach (var k in hit.zone.clumps) if (k != hit && k.t != null && (k.t.position - hit.t.position).sqrMagnitude <= rr) StartTwitch(k);
    }

    // ---- mesh + material (one per variant)
    VarState Var(string id)
    {
        VarState vs; if (!vars.TryGetValue(id, out vs)) return null; if (vs.mesh != null && vs.mat != null) return vs;
        vs.mesh = BuildMesh(vs.v); vs.mat = BuildMat(vs.v); return vs;
    }
    Mesh BuildMesh(Variant v)
    {
        int n = Mathf.Max(1, v.cards); var vert = new Vector3[n * QuadVerts]; var nor = new Vector3[n * QuadVerts]; var uv = new Vector2[n * QuadVerts]; var tan = new Vector4[n * QuadVerts]; var tri = new int[n * QuadTri.Length];
        float hw = v.widthM * 0.5f, y0 = -c.cards.baseSinkM, y1 = v.heightM - c.cards.baseSinkM;
        for (int i = 0; i < n; i++)
        {
            var q = Quaternion.Euler(0f, i * c.cards.cardYawStepDeg, 0f); Vector3 right = q * Vector3.right, fwd = q * Vector3.forward; int b = i * QuadVerts;
            vert[b] = right * -hw + Vector3.up * y0; vert[b + 1] = right * hw + Vector3.up * y0; vert[b + 2] = right * -hw + Vector3.up * y1; vert[b + 3] = right * hw + Vector3.up * y1;
            uv[b] = new Vector2(0f, 0f); uv[b + 1] = new Vector2(1f, 0f); uv[b + 2] = new Vector2(0f, 1f); uv[b + 3] = new Vector2(1f, 1f);
            var nn = Vector3.Slerp(fwd, Vector3.up, Mathf.Clamp01(c.cards.normalUpBlend)).normalized;
            for (int k = 0; k < QuadVerts; k++) { nor[b + k] = nn; tan[b + k] = new Vector4(right.x, right.y, right.z, 1f); }
            int t = i * QuadTri.Length; for (int k = 0; k < QuadTri.Length; k++) tri[t + k] = b + QuadTri[k];
        }
        var m = new Mesh { name = "fol_" + v.id }; m.vertices = vert; m.normals = nor; m.uv = uv; m.tangents = tan; m.triangles = tri; m.RecalculateBounds(); m.UploadMeshData(true); return m;
    }
    Material BuildMat(Variant v)
    {
        Material m = null; var refMat = string.IsNullOrEmpty(c.refMaterial) ? null : Resources.Load<Material>(c.refMaterial);
        if (refMat != null) m = new Material(refMat);
        else
        {
            var sh = Shader.Find(c.shader); if (sh == null) { Debug.LogWarning("FOLIAGE: shader " + c.shader + " missing from the player and no refMaterial: no foliage"); return null; }
            m = new Material(sh);
            if (c.material.alphaClipOn) { m.EnableKeyword("_ALPHATEST_ON"); SetF(m, "_AlphaCutoffEnable", 1f); }
            if (c.material.doubleSided) { m.EnableKeyword("_DOUBLESIDED_ON"); SetF(m, "_DoubleSidedEnable", 1f); SetF(m, "_CullMode", 0f); SetF(m, "_CullModeForward", 0f); SetF(m, "_DoubleSidedNormalMode", 1f); if (m.HasProperty("_DoubleSidedConstants")) m.SetVector("_DoubleSidedConstants", MirrorConstants); }
            SetF(m, "_ZTestDepthEqualForOpaque", c.material.zTestForOpaque); SetF(m, "_ZTestGBuffer", c.material.zTestForOpaque);
        }
        m.name = "fol_" + v.id; m.hideFlags = HideFlags.DontUnloadUnusedAsset; m.enableInstancing = c.material.enableInstancing;
        SetF(m, "_AlphaCutoff", v.alphaCutoff); SetF(m, "_Smoothness", v.smoothness); SetF(m, "_Metallic", c.material.metallic);
        var tex = Load(v.tex + ".png"); if (tex == null) { Debug.LogWarning("FOLIAGE: texture " + v.tex + ".png not found under " + c.texDir); return null; }
        m.SetTexture(BaseColorMap, tex); if (v.tint != null && v.tint.Length >= 3) m.SetColor(BaseColor, new Color(v.tint[0], v.tint[1], v.tint[2], 1f));
        if (c.log && matLogged++ < vars.Count) Debug.Log("FOLIAGE material " + v.id + ": shader " + m.shader.name + " clip " + m.IsKeywordEnabled("_ALPHATEST_ON") + " doubleSided " + m.IsKeywordEnabled("_DOUBLESIDED_ON") + " tex " + tex.width + "x" + tex.height);
        return m;
    }
    static void SetF(Material m, string p, float val) { if (m.HasProperty(p)) m.SetFloat(p, val); }
    Texture2D Load(string file)
    {
        string p = Path.Combine(Path.Combine(Application.streamingAssetsPath, c.texDir), file);
        if (!File.Exists(p)) p = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, DataDir), c.texDir), file);
        if (!File.Exists(p)) return null;
        try
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = "fol_" + file, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = c.material.anisoLevel };
            if (!t.LoadImage(File.ReadAllBytes(p), true)) { Destroy(t); return null; }
            return t;
        }
        catch (Exception e) { Debug.LogWarning("FOLIAGE texture " + p + ": " + e.Message); return null; }
    }
}
