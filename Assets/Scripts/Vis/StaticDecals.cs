using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// VIS3 v1 (laneVIS3, items 10 + 11 + 12-ground + 13-base grime): STATIC HDRP DecalProjectors from data. NEW FILE, nothing else calls it.
// OFF by default: each of the 4 data files (ground_patches / static_decals / wet_decals / prop_grime_decals .json) has enabled=false and no -staticdecals:1 = Boot returns, nothing is allocated, no file is read twice.
// What it does: rows (zone, world x/z, yaw, size, texture id, opacity) become ONE DecalProjector each, created once per zone the first time the player comes within wakeMarginM of the zone's z range, a few per frame
// (spawnPerFrame), parented under one container per zone; the container is switched off again beyond despawnMarginM (hysteresis), so a far zone costs nothing and no projector is ever created or destroyed per frame.
// Ground height per row = one down ray at spawn time (rayUpM / rayDownM); a ray that hits nothing or lands more than yTolM from the zone's yExpect is a prop or a missing collider: the row is retried on the next polls
// (retryPolls) and then dropped (counted in the log), never placed floating. Culling = DecalProjector.drawDistance (cap in data) + HDRP's own frustum culling.
// Material: clone of Resources/WoundDecalRef (the same reference asset WoundDecals uses, so the HDRP/Decal variants it keeps are the ones in the player) plus the row texture set loaded once from StreamingAssets/<texDir>/<id>.png (_n normal, _m mask).
// Textures are loaded with Texture2D.LoadImage (RGBA32); the kit's PNGs are 256 / 512 px (about 17 MB on disk, about 25 MB of RAM once all are loaded, lazily per texture id).
// Data: StreamingAssets/static_decals.json (then Assets/Data). Args: -staticdecals:1|0. UNPROVEN IN UNITY: nothing here has been run; compiled only with Roslyn (see kit\tools\compile_check.ps1).
public sealed class StaticDecals : MonoBehaviour
{
    [Serializable] public class TexCfg { public string id, name; public float smoothness = 0.5f; public bool affectNormal, affectSmoothness = true, affectAO; public float[] tint; }
    [Serializable] public class ZoneCfg { public string id; public float zMin, zMax, yExpect, yTolM = 0.5f; public bool enabled = true; public int maxRows = 100; }
    [Serializable] public class Row { public string id, zone, tex; public float x, z, yaw, w, h, depth, opacity = 1f, yOffset, dd; }
    [Serializable] public class Cfg
    {
        public bool enabled = false, log = true; public string texDir = "StaticDecals", refMaterial = "WoundDecalRef";
        public float pollSeconds = 0.5f, minPollS = 0.1f, wakeMarginM = 45f, despawnMarginM = 70f, drawDistance = 35f, fadeScale = 0.8f, startAngleFade = 70f, endAngleFade = 100f, depthM = 0.5f, liftM = 0.25f, rayUpM = 3f, rayDownM = 6f, anisoLevel = 4f;
        public int spawnPerFrame = 6, retryPolls = 6, maxTotal = 700; public int decalLayer = -1;
        public TexCfg[] textures; public ZoneCfg[] zones; public Row[] rows;
    }

    static readonly string[] Files = { "ground_patches.json", "static_decals.json", "wet_decals.json", "prop_grime_decals.json" };
    const string ArgPrefix = "-staticdecals:", DataDir = "Data"; string file;
    class ZoneState { public ZoneCfg cfg; public GameObject root; public List<Row> rows = new List<Row>(); public int next, spawned, dropped; public List<Row> retry = new List<Row>(); public int retries; public bool done, logged; }

    Cfg c; float nextPoll; Transform who; readonly List<ZoneState> zs = new List<ZoneState>(); readonly Dictionary<string, Material> mats = new Dictionary<string, Material>(); readonly Dictionary<string, TexCfg> texById = new Dictionary<string, TexCfg>();
    Material refMat; bool refLooked, shaderMissingLogged; int total;
    static readonly RaycastHit[] hits = new RaycastHit[16];
    static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap"), MaskMap = Shader.PropertyToID("_MaskMap"), NormalMap = Shader.PropertyToID("_NormalMap"), BaseColor = Shader.PropertyToID("_BaseColor");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string a1 = null;
            foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(ArgPrefix)) a1 = a.Substring(ArgPrefix.Length);
            foreach (var fn in Files)   // one independent instance per data file = one switch per item (10 ground patches, 11 decals, 12 wet, 13 grime)
            {
                Cfg cfg = null;
                foreach (var p in DataPath.Candidates(fn))
                {
                    if (!File.Exists(p)) continue;
                    try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); } catch (Exception e) { Debug.LogWarning("[StaticDecals] " + p + ": " + e.Message); }
                    break;
                }
                if (cfg == null || cfg.rows == null || cfg.rows.Length == 0 || cfg.zones == null) continue;
                bool on = a1 != null ? a1 == "1" : cfg.enabled; if (!on) continue;
                if (GameObject.Find("StaticDecals:" + fn) != null) continue;
                var go = new GameObject("StaticDecals:" + fn); DontDestroyOnLoad(go); var sd = go.AddComponent<StaticDecals>(); sd.c = cfg; sd.file = fn;
            }
        }
        catch (Exception e) { Debug.LogWarning("STATICDECALS boot failed: " + e.Message); }
    }
    void Start()
    {
        foreach (var t in c.textures ?? new TexCfg[0]) if (t != null && !string.IsNullOrEmpty(t.id)) texById[t.id] = t;
        foreach (var z in c.zones) { if (z == null || !z.enabled) continue; var s = new ZoneState { cfg = z }; zs.Add(s); }
        int cap = Mathf.Max(0, c.maxTotal);
        foreach (var r in c.rows)
        {
            if (r == null || total >= cap) continue;
            foreach (var s in zs) if (s.cfg.id == r.zone && s.rows.Count < s.cfg.maxRows) { s.rows.Add(r); total++; break; }
        }
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnScene;
        if (c.log) Debug.Log("STATICDECALS " + file + " armed: " + total + " rows in " + zs.Count + " zones");
    }

    void OnScene(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m)
    {
        // containers live under this DontDestroyOnLoad object: a scene (re)load must remove them, ground and props are new objects; spawn again when the player is near
        foreach (var z in zs) { if (z.root != null) Destroy(z.root); z.root = null; z.next = 0; z.spawned = 0; z.dropped = 0; z.retry.Clear(); z.retries = 0; z.done = false; z.logged = false; }
    }

    Transform Who()
    {
        if (who != null) return who;
        var f = FindFirstObjectByType<DuelFighter>(); if (f != null) { who = f.transform; return who; }
        var cam = Camera.main; if (cam != null) who = cam.transform; return who;
    }

    void Update()
    {
        // budgeted spawn: runs every frame only while some awake zone still has rows to place; the idle poll is every pollSeconds
        bool busy = false; foreach (var z in zs) if (z.root != null && z.root.activeSelf && !z.done) { busy = true; break; }
        if (!busy && Time.unscaledTime < nextPoll) return;
        var w = Who(); if (w == null) { nextPoll = Time.unscaledTime + Mathf.Max(c.minPollS, c.pollSeconds); return; }
        float pz = w.position.z;
        if (Time.unscaledTime >= nextPoll)
        {
            nextPoll = Time.unscaledTime + Mathf.Max(c.minPollS, c.pollSeconds);
            foreach (var z in zs)
            {
                bool near = pz > z.cfg.zMin - c.wakeMarginM && pz < z.cfg.zMax + c.wakeMarginM, far = pz < z.cfg.zMin - c.despawnMarginM || pz > z.cfg.zMax + c.despawnMarginM;
                if (z.root == null) { if (!near || z.rows.Count == 0) continue; z.root = new GameObject("StaticDecals_" + z.cfg.id); z.root.transform.SetParent(transform, false); z.root.SetActive(true); }
                else if (z.root.activeSelf && far) z.root.SetActive(false);
                else if (!z.root.activeSelf && near) z.root.SetActive(true);
            }
        }
        int budget = Mathf.Max(1, c.spawnPerFrame);
        foreach (var z in zs)
        {
            if (z.root == null || !z.root.activeSelf || z.done) continue;
            while (budget > 0 && z.next < z.rows.Count) { budget--; var r = z.rows[z.next++]; if (!Place(z, r)) z.retry.Add(r); }
            if (z.next >= z.rows.Count && budget > 0 && z.retry.Count > 0)
            {
                if (z.retries >= c.retryPolls) { z.dropped += z.retry.Count; z.retry.Clear(); }
                else { var again = new List<Row>(z.retry); z.retry.Clear(); z.retries++; foreach (var r in again) { budget--; if (!Place(z, r)) z.retry.Add(r); if (budget <= 0) break; } }
            }
            if (z.next >= z.rows.Count && z.retry.Count == 0 && !z.done)
            {
                z.done = true;
                if (c.log && !z.logged) { z.logged = true; Debug.Log("STATICDECALS " + file + " " + z.cfg.id + ": " + z.spawned + " placed, " + z.dropped + " dropped (no ground / not at yExpect " + z.cfg.yExpect.ToString("F2") + " +-" + z.cfg.yTolM.ToString("F2") + "), of " + z.rows.Count); }
            }
            if (budget <= 0) break;
        }
    }

    bool Place(ZoneState z, Row r)
    {
        try
        {
            var m = MatFor(r.tex); if (m == null) { z.dropped++; return true; }
            float gy; bool ok = Ground(r.x, r.z, z.cfg, out gy);
            if (!ok) return false;
            var go = new GameObject(string.IsNullOrEmpty(r.id) ? "sd" : r.id); go.transform.SetParent(z.root.transform, false);
            go.transform.position = new Vector3(r.x, gy + c.liftM + r.yOffset, r.z); go.transform.rotation = Quaternion.Euler(90f, r.yaw, 0f);
            var p = go.AddComponent<DecalProjector>(); p.material = m;
            p.size = new Vector3(r.w, r.h, r.depth > 0f ? r.depth : c.depthM); p.pivot = Vector3.zero; p.fadeFactor = Mathf.Clamp01(r.opacity);
            p.drawDistance = r.dd > 0f ? r.dd : c.drawDistance; p.fadeScale = c.fadeScale; p.startAngleFade = c.startAngleFade; p.endAngleFade = c.endAngleFade;
            if (c.decalLayer >= 0) p.decalLayerMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)(1u << c.decalLayer);
            z.spawned++; return true;
        }
        catch (Exception e) { z.dropped++; if (c.log) Debug.LogWarning("STATICDECALS place failed " + r.id + ": " + e.Message); return true; }
    }

    // nearest non-trigger, non-fighter surface below (x, yExpect + rayUp); true only when within yTolM of yExpect (else a prop or a roof, or the ring collider is not awake yet)
    bool Ground(float x, float zz, ZoneCfg zc, out float gy)
    {
        gy = zc.yExpect; int n = Physics.RaycastNonAlloc(new Vector3(x, zc.yExpect + c.rayUpM, zz), Vector3.down, hits, c.rayUpM + c.rayDownM, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < n; i++)
        {
            var col = hits[i].collider; if (col == null || col.GetComponentInParent<DuelFighter>() != null) continue;
            if (hits[i].distance < best) { best = hits[i].distance; gy = hits[i].point.y; found = true; }
        }
        return found && Mathf.Abs(gy - zc.yExpect) <= zc.yTolM;
    }

    Material MatFor(string id)
    {
        if (string.IsNullOrEmpty(id)) return null; Material m;
        if (mats.TryGetValue(id, out m)) return m;
        TexCfg t; if (!texById.TryGetValue(id, out t)) { mats[id] = null; return null; }
        if (!refLooked) { refLooked = true; refMat = Resources.Load<Material>(c.refMaterial); }
        var sh = refMat != null ? null : Shader.Find("HDRP/Decal");
        if (refMat == null && sh == null) { if (!shaderMissingLogged) { shaderMissingLogged = true; Debug.LogWarning("STATICDECALS: no " + c.refMaterial + " and no HDRP/Decal shader: no decals"); } mats[id] = null; return null; }
        m = refMat != null ? new Material(refMat) : new Material(sh); m.name = "sd_" + id; m.hideFlags = HideFlags.DontUnloadUnusedAsset; m.enableInstancing = true;
        string file = string.IsNullOrEmpty(t.name) ? id : t.name;
        var col = Load(file + ".png", false); var msk = Load(file + "_m.png", true); var nrm = t.affectNormal ? Load(file + "_n.png", true) : null;
        if (col == null) { mats[id] = null; return null; }
        m.EnableKeyword("_COLORMAP"); m.SetTexture(BaseColorMap, col);
        if (msk != null) { m.EnableKeyword("_MASKMAP"); m.SetTexture(MaskMap, msk); }
        if (nrm != null) { m.EnableKeyword("_NORMALMAP"); m.EnableKeyword("_MATERIAL_AFFECTS_NORMAL"); m.SetTexture(NormalMap, nrm); }
        m.EnableKeyword("_MATERIAL_AFFECTS_ALBEDO"); m.EnableKeyword("_MATERIAL_AFFECTS_MASKMAP");
        var tn = t.tint != null && t.tint.Length >= 3 ? new Color(t.tint[0], t.tint[1], t.tint[2], 1f) : Color.white; m.SetColor(BaseColor, tn);
        m.SetFloat("_AffectAlbedo", 1f); m.SetFloat("_AffectNormal", nrm != null ? 1f : 0f); m.SetFloat("_AffectMetal", 0f); m.SetFloat("_AffectAO", t.affectAO ? 1f : 0f); m.SetFloat("_AffectSmoothness", t.affectSmoothness ? 1f : 0f); m.SetFloat("_AffectEmission", 0f);
        m.SetFloat("_DecalBlend", 1f); m.SetFloat("_DecalMaskMapBlueScale", 1f); m.SetFloat("_NormalBlendSrc", 0f); m.SetFloat("_MaskBlendSrc", 1f);
        if (m.HasProperty("_SmoothnessRemapMin")) m.SetFloat("_SmoothnessRemapMin", 0f);
        if (m.HasProperty("_SmoothnessRemapMax")) m.SetFloat("_SmoothnessRemapMax", t.smoothness > 0f ? t.smoothness : 1f);
        mats[id] = m; return m;
    }

    Texture2D Load(string file, bool linear)
    {
        string p = Path.Combine(Path.Combine(Application.streamingAssetsPath, c.texDir), file);
        if (!File.Exists(p)) p = Path.Combine(Path.Combine(Path.Combine(Application.dataPath, DataDir), c.texDir), file);
        if (!File.Exists(p)) return null;
        try
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "sd_" + file, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = Mathf.RoundToInt(c.anisoLevel) };
            if (!t.LoadImage(File.ReadAllBytes(p), true)) { Destroy(t); return null; }
            return t;
        }
        catch (Exception e) { Debug.LogWarning("STATICDECALS texture " + p + ": " + e.Message); return null; }
    }
}




