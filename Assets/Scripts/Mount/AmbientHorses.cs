using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// hades-env AMBIENT HORSES v1 (staged DARK, UNPROVEN in Unity). Decorative horses (the plain Tripo horse and the war horse, both Dom's 80k-tri LODs, no rig) that stand still and walk to random
// points inside rectangles listed in ambient_horses.json. Nothing here is read by the sim: no collider, no rigidbody, no DuelFighter, no weapon, no gate or play-area change; the player walks through
// them. Everything is data (ambient_horses.json, enabled=false and no file = nothing runs, no behaviour change). One new file, no hunks in existing code.
// Spawn: a herd loads (one body at a time) when the player is within spawnMarginM of its rectangle and the zones are live; sleeps (SetActive false) past despawnMarginM, or while the player is in a fight.
// Walk: pick a random point in the rectangle (not within avoidPlayerM of the player, not inside an avoid circle, not closer than minSpacingM to a herdmate), turn, walk at walkMps with a procedural gait
// (bob / pitch / roll locked to the ground speed, the same recipe as MountRider.Gait), stand pauseSMin..pauseSMax s. Ground = a down ray (first non-player collider); no ground = stand still.
[Serializable] public class AhBreed { public string id = ""; public string glb = ""; public float heightM = 1.75f, glbHeightM = 1.75f; public string midGlb = ""; }   // FPSX_D2: midGlb = mesh-only twin (~10k tris), "" = none
[Serializable] public class AhRect { public float xMin, xMax, zMin, zMax; }
[Serializable] public class AhCircle { public float x, z, r; }
[Serializable] public class AhHerd
{
    public string id = ""; public bool enabled = true; public AhRect area = new AhRect(); public string[] breeds = new string[0]; public int count = 1; public int seed = 1; public AhCircle[] avoid = new AhCircle[0];
}
[Serializable] public class AhGait
{
    public float walkMps = 1.1f, turnDegPerS = 90f, pauseSMin = 3f, pauseSMax = 9f, arriveM = 0.4f, cyclesPerM = 0.55f, bobM = 0.02f, pitchDeg = 1.5f, rollDeg = 1.5f, breathM = 0.004f, breathCyclesPerS = 0.3f, blendS = 0.15f, alignDeg = 45f, standMps = 0.05f, minBlendS = 0.001f;
}
[Serializable] public class AhCfg
{
    public bool enabled = false, debugLog = false, convertMaterials = true, shadows = false, holdWhileEngaged = true;
    public float pollSeconds = 0.5f, spawnMarginM = 14f, despawnMarginM = 22f, avoidPlayerM = 4f, minSpacingM = 3f, groundRayUpM = 3f, groundRayDownM = 10f, sinkM = 0.03f, fallbackY = 0f;
    public float freeMarginM = 0f, engagedMaxM = 40f;   // laneHERDS fix2: free a sleeping herd's meshes/textures when the player is this far from its area (0 = never, today)
    public int groundMask = -1, framesBetweenLoads = 2, maxTargetTries = 12, seedStride = 7919, hitBuffer = 8;
    public float lodMidEnterM = 0f, lodMidExitM = 0f, lodMidBoundsTolM = 0.15f;   // FPSX_D2: camera distance (m) beyond which the mid mesh is shown / back to full inside exit; 0 = never load or use a mid mesh (today)
    public bool midFirst = false;   // laneHERDS load: true = the mid mesh (untextured, ~0.4 MB) is shown first, the full textured glb is imported ONCE per breed and shared (false = old behaviour)
    public float midFirstFullMaxM = 0f;   // midFirst: >0 = the full glb of a breed is only imported while the player is within this many m of the herd area (0 = always)
    public float[] midFirstTint = new float[0];   // midFirst: optional r,g,b placeholder colour of the untextured mid mesh (empty = the glb colour)
    public AhGait gait = new AhGait(); public AhBreed[] breeds = new AhBreed[0]; public AhHerd[] herds = new AhHerd[0];
}

public sealed class AmbientHorses : MonoBehaviour
{
    static AhCfg K; static AmbientHorses Inst; static RaycastHit[] hits = new RaycastHit[8];   // resized to hitBuffer when the config loads
    class Horse { public Renderer[] fullR, midR; public bool midOn; public GameObject go; public Transform body; public AhHerd herd; public Vector3 pos, target; public float yaw, wait, phase, breath, speed, amp, bob, pitch, roll, spawnY; public bool walking, placed; public System.Random rnd; public Renderer midMain; public Transform midNode; }
    class Loaded { public GltfHold hold; }
    class GltfHold { public GLTFast.GltfImport imp; }
    class HerdState { public List<Horse> horses = new List<Horse>(); public bool loading, tried, asleep, traced; public List<Material> mats = new List<Material>(); public List<GltfHold> holds = new List<GltfHold>(); public int gen; public List<KeyValuePair<Horse, AhBreed>> up = new List<KeyValuePair<Horse, AhBreed>>(); }
    class BreedCache { public Task<GLTFast.GltfImport> mid, full; public Material[] midMats, fullMats; public List<Material> made = new List<Material>(); }   // laneHERDS load: one import per breed, shared by every animal
    static readonly Dictionary<string, BreedCache> cache = new Dictionary<string, BreedCache>();
    readonly Dictionary<AhHerd, HerdState> st = new Dictionary<AhHerd, HerdState>(); float nextPoll, nextGateLog;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; K = null; cache.Clear(); }
    public static AhCfg Get()
    {
        if (K != null) return K; K = new AhCfg(); string txt = DataPath.ReadText("ambient_horses.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<AhCfg>(txt); if (k != null) K = k; } catch (Exception e) { Debug.LogWarning("[AmbientHorses] ambient_horses.json unreadable, off: " + e.Message); K = new AhCfg(); } }
        if (K.gait == null) K.gait = new AhGait(); if (K.hitBuffer > 0 && K.hitBuffer != hits.Length) hits = new RaycastHit[K.hitBuffer]; if (K.breeds == null) K.breeds = new AhBreed[0]; if (K.herds == null) K.herds = new AhHerd[0];
        return K;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return; var k = Get();
        bool argOn = false; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-ambienthorses", StringComparison.OrdinalIgnoreCase)) argOn = true;   // laneHERDS proof: bypass the harness return (default behaviour unchanged)
        if (k.debugLog || argOn) Debug.Log("[AmbientHorses] Boot enabled=" + k.enabled + " herds=" + k.herds.Length + " harness=" + PerfFlags.HarnessRun() + " arg=" + argOn);
        if (!k.enabled || k.herds.Length == 0) return; if (PerfFlags.HarnessRun() && !argOn) return;
        var go = new GameObject("AmbientHorses"); DontDestroyOnLoad(go); Inst = go.AddComponent<AmbientHorses>();
    }
    void OnDestroy()
    {
        foreach (var kv in st) Free(kv.Value); st.Clear(); if (Inst == this) Inst = null;
        foreach (var kv in cache) { foreach (var m in kv.Value.made) if (m != null) Destroy(m); foreach (var t in new[] { kv.Value.mid, kv.Value.full }) if (t != null && t.Status == TaskStatus.RanToCompletion && t.Result != null) { try { t.Result.Dispose(); } catch (Exception) { } } }
        cache.Clear();
    }

    static AhBreed Breed(string id) { foreach (var b in K.breeds) if (b != null && b.id == id) return b; return null; }
    static string File(AhBreed b) { try { var p = System.IO.Path.Combine(Application.streamingAssetsPath, b.glb); return System.IO.File.Exists(p) ? p : null; } catch (Exception) { return null; } }

    void Update()
    {
        float now = Time.unscaledTime; var p = GameLoop.Player; bool zones = ZoneRuntime.Owns && ZoneSpan.Live && p != null && !ZoneGroundHold.Active;
        if (now >= nextPoll)
        {
            nextPoll = now + K.pollSeconds;
            if (K.debugLog && now >= nextGateLog) { nextGateLog = now + 5f; Debug.Log("[AmbientHorses] gate zones=" + zones + " Owns=" + ZoneRuntime.Owns + " Live=" + ZoneSpan.Live + " player=" + (p != null) + " groundHold=" + ZoneGroundHold.Active + " pos=" + (p != null ? p.transform.position.ToString("F1") : "-")); }   // laneHERDS debug
            foreach (var h in K.herds)
            {
                if (h == null || !h.enabled || h.count <= 0 || h.breeds == null || h.breeds.Length == 0) continue; HerdState s; if (!st.TryGetValue(h, out s)) { s = new HerdState(); st[h] = s; }
                bool near = zones && Near(p.transform.position, h.area, s.asleep || s.horses.Count > 0 ? K.despawnMarginM : K.spawnMarginM);
                bool engaged = K.holdWhileEngaged && p != null && p.opponent != null && !p.opponent.Dead && (K.engagedMaxM <= 0f || Vector3.Distance(p.transform.position, p.opponent.transform.position) <= K.engagedMaxM);   // laneHERDS fix3: a stale/far opponent (player.opponent is never cleared on leaving a zone) is not a fight
                if (K.debugLog && !s.traced) { s.traced = true; Debug.Log("[AmbientHorses] decide " + h.id + " near=" + near + " engaged=" + engaged + " opp=" + (p != null && p.opponent != null ? p.opponent.name : "-") + " loading=" + s.loading + " tried=" + s.tried + " asleep=" + s.asleep + " horses=" + s.horses.Count + " pos=" + (p != null ? p.transform.position.ToString("F1") : "-") + " area=" + h.area.xMin + ".." + h.area.xMax + " / " + h.area.zMin + ".." + h.area.zMax); }   // laneHERDS trace
                if (near && !engaged) { if (s.asleep) Wake(s); else if (!s.loading && !s.tried && s.horses.Count == 0) { s.tried = true; var t = SpawnHerd(h, s, ++s.gen); } }
                else if (!near || engaged) { if (s.horses.Count > 0 && !s.asleep) Sleep(s); else if (s.loading) { s.gen++; s.tried = false; } if (!near) s.tried = false;
                    if (!near && p != null && K.freeMarginM > 0f && s.asleep && s.horses.Count > 0 && !Near(p.transform.position, h.area, K.freeMarginM)) { Free(s); s.asleep = false; s.tried = false; } }   // laneHERDS fix2
            }
        }
        float dt = Time.deltaTime; foreach (var kv in st) { var s = kv.Value; if (s.asleep) continue; foreach (var hr in s.horses) if (hr.go != null && hr.go.activeSelf) Step(hr, kv.Key, s, dt, p); }
    }

    static bool Near(Vector3 q, AhRect a, float m) { return q.x >= a.xMin - m && q.x <= a.xMax + m && q.z >= a.zMin - m && q.z <= a.zMax + m; }
    static void Sleep(HerdState s) { foreach (var h in s.horses) if (h.go != null) h.go.SetActive(false); s.asleep = true; }
    static void Wake(HerdState s) { s.asleep = false; foreach (var h in s.horses) if (h.go != null) { h.go.SetActive(true); h.placed = false; } }
    static void Free(HerdState s)
    {
        foreach (var h in s.horses) if (h.go != null) Destroy(h.go); s.horses.Clear(); foreach (var m in s.mats) if (m != null) Destroy(m); s.mats.Clear();
        foreach (var g in s.holds) if (g != null && g.imp != null) { try { g.imp.Dispose(); } catch (Exception) { } } s.holds.Clear();
    }

    async Task SpawnHerd(AhHerd h, HerdState s, int gen)
    {
        s.loading = true;
        try
        {
            float t0 = Time.realtimeSinceStartup; if (K.debugLog) Debug.Log("[AmbientHorses] SpawnHerd start " + h.id + " count=" + h.count); var rnd = new System.Random(h.seed);
            for (int i = 0; i < h.count; i++)
            {
                if (gen != s.gen) { Free(s); return; }
                var b = Breed(h.breeds[rnd.Next(h.breeds.Length)]); if (b == null) { Debug.LogWarning("[AmbientHorses] herd " + h.id + ": unknown breed"); continue; }
                if (K.midFirst && !string.IsNullOrEmpty(b.midGlb) && File(new AhBreed { glb = b.midGlb }) != null) { await SpawnMidFirst(h, s, gen, i, b, t0); if (gen != s.gen) { Free(s); return; } for (int f = 0; f < K.framesBetweenLoads; f++) await Task.Yield(); continue; }   // laneHERDS load
                string file = File(b); if (K.debugLog) Debug.Log("[AmbientHorses] " + h.id + "[" + i + "] breed=" + b.id + " file=" + (file ?? "NULL")); if (file == null) { Debug.LogWarning("[AmbientHorses] breed " + b.id + ": glb not found under StreamingAssets: " + b.glb); continue; }
                var go = new GameObject("AmbientHorse_" + h.id + "_" + i); DontDestroyOnLoad(go); go.SetActive(false);
                var body = new GameObject("Body"); body.transform.SetParent(go.transform, false);
                var hold = new GltfHold { imp = new GLTFast.GltfImport() }; s.holds.Add(hold);
                bool okLoad = await hold.imp.LoadFile(file); if (K.debugLog) Debug.Log("[AmbientHorses] " + h.id + "[" + i + "] LoadFile ok=" + okLoad); if (!okLoad || !await hold.imp.InstantiateMainSceneAsync(body.transform)) { Debug.LogWarning("[AmbientHorses] glb failed: " + file); Destroy(go); continue; }
                if (gen != s.gen) { Destroy(go); Free(s); return; }
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
                var rl = new List<Renderer>(go.GetComponentsInChildren<Renderer>(true));
                foreach (var r in rl) r.shadowCastingMode = K.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                if (K.convertMaterials) s.mats.AddRange(GearMaterials.Convert(rl));
                body.transform.localScale = Vector3.one * (b.heightM > 0f && b.glbHeightM > 0f ? b.heightM / b.glbHeightM : 1f);
                var hr = new Horse { go = go, body = body.transform, herd = h, rnd = new System.Random(h.seed * K.seedStride + i), wait = 0f };
                hr.pos = RandomPoint(h, hr, s, null); hr.yaw = (float)(hr.rnd.NextDouble() * 360.0); hr.target = hr.pos; hr.wait = Pause(hr);   // hc:ok 360 = a full turn (random start heading)
                s.horses.Add(hr);
                go.transform.position = hr.pos;            // laneHERDS fix1: not at the world origin while Step() waits for ground
                if (!s.asleep) go.SetActive(true);         // laneHERDS fix1: the horse was created inactive and nothing ever activated it
                if (K.lodMidEnterM > 0f && !string.IsNullOrEmpty(b.midGlb) && rl.Count == 1) await AddMid(b, body.transform, hr, s, rl[0]);   // FPSX_D2
                for (int f = 0; f < K.framesBetweenLoads; f++) await Task.Yield();
            }
            if (K.debugLog) Debug.Log("[AmbientHorses] herd " + h.id + ": " + s.horses.Count + " horses loaded");
            if (s.up.Count > 0 && gen == s.gen) { var upg = UpgradeHerd(h, s, gen, t0); }   // laneHERDS load: textured full meshes follow in the background
        }
        catch (Exception e) { Debug.LogWarning("[AmbientHorses] herd " + h.id + " error: " + e.Message); }
        finally { s.loading = false; }
    }

    // laneHERDS load (midFirst): one import per breed, cached, shared by every animal of every herd (instantiate copies). Mid first, full later.
    static BreedCache BC(string id) { BreedCache c; if (!cache.TryGetValue(id, out c)) { c = new BreedCache(); cache[id] = c; } return c; }
    static async Task<GLTFast.GltfImport> Import(string file) { var imp = new GLTFast.GltfImport(); bool ok = await imp.LoadFile(file); return ok ? imp : null; }

    async Task SpawnMidFirst(AhHerd h, HerdState s, int gen, int i, AhBreed b, float t0)
    {
        var bc = BC(b.id); if (bc.mid == null) bc.mid = Import(File(new AhBreed { glb = b.midGlb }));
        var imp = await bc.mid; if (imp == null || gen != s.gen) return;
        var go = new GameObject("AmbientHorse_" + h.id + "_" + i); DontDestroyOnLoad(go); go.SetActive(false);
        var body = new GameObject("Body"); body.transform.SetParent(go.transform, false);
        var node = new GameObject("MidLod"); node.transform.SetParent(body.transform, false);
        if (!await imp.InstantiateMainSceneAsync(node.transform)) { Destroy(go); return; }
        if (gen != s.gen) { Destroy(go); return; }
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var rl = new List<Renderer>(go.GetComponentsInChildren<Renderer>(true));
        foreach (var r in rl) r.shadowCastingMode = K.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        if (rl.Count == 1 && bc.midMats != null) rl[0].sharedMaterials = bc.midMats;
        else
        {
            if (K.convertMaterials) { var made = GearMaterials.Convert(rl); bc.made.AddRange(made); if (K.midFirstTint != null && K.midFirstTint.Length >= 3) { var tc = new Color(K.midFirstTint[0], K.midFirstTint[1], K.midFirstTint[2], 1f); foreach (var m in made) { if (m == null) continue; if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tc); if (m.HasProperty("_Color")) m.SetColor("_Color", tc); } } }
            if (rl.Count == 1) bc.midMats = rl[0].sharedMaterials;
        }
        body.transform.localScale = Vector3.one * (b.heightM > 0f && b.glbHeightM > 0f ? b.heightM / b.glbHeightM : 1f);
        var hr = new Horse { go = go, body = body.transform, herd = h, rnd = new System.Random(h.seed * K.seedStride + i), wait = 0f, midMain = rl.Count == 1 ? rl[0] : null, midNode = node.transform };
        hr.pos = RandomPoint(h, hr, s, null); hr.yaw = (float)(hr.rnd.NextDouble() * 360.0); hr.target = hr.pos; hr.wait = Pause(hr);   // hc:ok 360 = a full turn (random start heading)
        s.horses.Add(hr); go.transform.position = hr.pos; if (!s.asleep) go.SetActive(true);
        s.up.Add(new KeyValuePair<Horse, AhBreed>(hr, b));
        Debug.Log("[AmbientHorses] mid shown in " + (Time.realtimeSinceStartup - t0).ToString("F2") + " s (" + h.id + "[" + i + "] " + b.id + ")");
    }

    async Task UpgradeHerd(AhHerd h, HerdState s, int gen, float t0)
    {
        try
        {
            var todo = new List<KeyValuePair<Horse, AhBreed>>(s.up); s.up.Clear();
            foreach (var kv in todo)
            {
                var hr = kv.Key; var b = kv.Value; if (gen != s.gen) return; if (hr.go == null) continue;
                while (K.midFirstFullMaxM > 0f && hr.go != null && gen == s.gen && (GameLoop.Player == null || !Near(GameLoop.Player.transform.position, h.area, K.midFirstFullMaxM))) await Task.Yield();   // far: stay on the mid mesh
                if (hr.go == null || gen != s.gen) continue;
                var bc = BC(b.id); string file = File(b); if (file == null) continue;
                bool first = bc.full == null; if (first) bc.full = Import(file);
                var imp = await bc.full; if (first) Debug.Log("[AmbientHorses] full " + b.id + " imported in " + (Time.realtimeSinceStartup - t0).ToString("F2") + " s (shared, once per breed)");
                if (imp == null || hr.go == null || gen != s.gen) continue;
                var node = new GameObject("FullLod"); node.transform.SetParent(hr.body, false);
                if (!await imp.InstantiateMainSceneAsync(node.transform)) { Destroy(node); continue; }
                if (hr.go == null) return;
                foreach (var c in node.GetComponentsInChildren<Collider>(true)) Destroy(c);
                var rl = new List<Renderer>(node.GetComponentsInChildren<Renderer>(true));
                foreach (var r in rl) r.shadowCastingMode = K.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                if (rl.Count == 1 && bc.fullMats != null) rl[0].sharedMaterials = bc.fullMats;
                else { if (K.convertMaterials) bc.made.AddRange(GearMaterials.Convert(rl)); if (rl.Count == 1) bc.fullMats = rl[0].sharedMaterials; }
                bool lod = K.lodMidEnterM > 0f && hr.midMain != null && rl.Count == 1; Bounds bf, bm;
                if (lod && (!LocalBounds(hr.body, rl[0], out bf) || !LocalBounds(hr.body, hr.midMain, out bm) || (bf.center - bm.center).magnitude > K.lodMidBoundsTolM || (bf.size - bm.size).magnitude > K.lodMidBoundsTolM * 2f)) lod = false;   // hc:ok size tolerance = 2 x the centre tolerance
                if (lod) { hr.midMain.sharedMaterials = rl[0].sharedMaterials; hr.fullR = new[] { rl[0] }; hr.midR = new[] { hr.midMain }; hr.midOn = true; rl[0].enabled = false; }
                else if (hr.midNode != null) Destroy(hr.midNode.gameObject);
                Debug.Log("[AmbientHorses] textured shown in " + (Time.realtimeSinceStartup - t0).ToString("F2") + " s (" + h.id + " " + b.id + " lod=" + lod + ")");
                await Task.Yield();
            }
        }
        catch (Exception e) { Debug.LogWarning("[AmbientHorses] herd " + h.id + " upgrade error: " + e.Message); }
    }

    // FPSX_D2: mid mesh twin. A mesh-only glb (no textures) is loaded beside the full body and shares the full body's material, so the 4K textures exist once. It is used only if its local bounds match the full mesh
    // (lodMidBoundsTolM) and it has one renderer; otherwise it is dropped and the horse stays full. Switch = camera distance with hysteresis. lodMidEnterM 0 = this code never runs.
    async Task AddMid(AhBreed b, Transform body, Horse hr, HerdState s, Renderer full)
    {
        string file = File(new AhBreed { glb = b.midGlb }); if (file == null) return;
        var node = new GameObject("MidLod"); node.transform.SetParent(body, false);
        var hold = new GltfHold { imp = new GLTFast.GltfImport() }; s.holds.Add(hold);
        bool ok = await hold.imp.LoadFile(file) && await hold.imp.InstantiateMainSceneAsync(node.transform);
        if (hr.go == null) return;
        if (!ok) { Destroy(node); return; }
        foreach (var c in node.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var mr = new List<Renderer>(node.GetComponentsInChildren<Renderer>(true)); Bounds bf, bm;
        if (mr.Count != 1 || !LocalBounds(body, full, out bf) || !LocalBounds(body, mr[0], out bm) || (bf.center - bm.center).magnitude > K.lodMidBoundsTolM || (bf.size - bm.size).magnitude > K.lodMidBoundsTolM * 2f)   // hc:ok size tolerance = 2 x the centre tolerance
        { if (K.debugLog) Debug.Log("[AmbientHorses] mid mesh of " + b.id + " dropped (renderer count or bounds differ)"); Destroy(node); return; }
        mr[0].sharedMaterials = full.sharedMaterials; mr[0].shadowCastingMode = full.shadowCastingMode; mr[0].enabled = false;
        hr.fullR = new[] { full }; hr.midR = new[] { mr[0] };
    }

    static bool LocalBounds(Transform space, Renderer r, out Bounds b)
    {
        b = default(Bounds); var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) return false;
        var m = space.worldToLocalMatrix * mf.transform.localToWorldMatrix; var mb = mf.sharedMesh.bounds; b = new Bounds(m.MultiplyPoint3x4(mb.center), Vector3.zero);
        for (int i = 0; i < 8; i++) b.Encapsulate(m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));   // hc:ok box corner signs
        return true;
    }

    void LodStep(Horse h, DuelFighter p)
    {
        var cam = Camera.main; if (cam == null && p == null) return;
        Vector3 q = cam != null ? cam.transform.position : p.transform.position; float d = Vector3.Distance(q, h.pos);
        bool want = h.midOn ? d > Mathf.Min(K.lodMidExitM, K.lodMidEnterM) : d > K.lodMidEnterM; if (want == h.midOn) return;
        h.midOn = want; foreach (var r in h.fullR) if (r != null) r.enabled = !want; foreach (var r in h.midR) if (r != null) r.enabled = want;
    }

    static float Pause(Horse h) { var g = K.gait; return g.pauseSMin + (float)h.rnd.NextDouble() * Mathf.Max(0f, g.pauseSMax - g.pauseSMin); }

    Vector3 RandomPoint(AhHerd h, Horse self, HerdState s, DuelFighter p)
    {
        Vector3 best = new Vector3((h.area.xMin + h.area.xMax) * 0.5f, 0f, (h.area.zMin + h.area.zMax) * 0.5f);
        for (int t = 0; t < Mathf.Max(1, K.maxTargetTries); t++)
        {
            float x = Mathf.Lerp(h.area.xMin, h.area.xMax, (float)self.rnd.NextDouble()), z = Mathf.Lerp(h.area.zMin, h.area.zMax, (float)self.rnd.NextDouble()); bool ok = true;
            if (h.avoid != null) foreach (var c in h.avoid) if (c != null && (x - c.x) * (x - c.x) + (z - c.z) * (z - c.z) < c.r * c.r) { ok = false; break; }
            if (ok && p != null) { var q = p.transform.position; if ((x - q.x) * (x - q.x) + (z - q.z) * (z - q.z) < K.avoidPlayerM * K.avoidPlayerM) ok = false; }
            if (ok) foreach (var o in s.horses) if (o != self && o.go != null && (x - o.pos.x) * (x - o.pos.x) + (z - o.pos.z) * (z - o.pos.z) < K.minSpacingM * K.minSpacingM) { ok = false; break; }
            best = new Vector3(x, 0f, z); if (ok) return best;
        }
        return best;
    }

    bool Ground(float x, float z, float refY, out float y)
    {
        var p = GameLoop.Player; y = 0f; float best = float.MaxValue; bool found = false;
        int n = Physics.RaycastNonAlloc(new Vector3(x, refY + K.groundRayUpM, z), Vector3.down, hits, K.groundRayUpM + K.groundRayDownM, K.groundMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++) { var c = hits[i].collider; if (c == null || (p != null && c.transform.IsChildOf(p.transform))) continue; if (c.GetComponentInParent<DuelFighter>() != null) continue; if (hits[i].distance < best) { best = hits[i].distance; y = hits[i].point.y - K.sinkM; found = true; } }
        return found;
    }

    void Step(Horse h, AhHerd herd, HerdState s, float dt, DuelFighter p)
    {
        var G = K.gait; var pp = p != null ? p.transform.position : Vector3.zero;
        if (!h.placed) { float y; if (Ground(h.pos.x, h.pos.z, p != null ? pp.y : K.fallbackY, out y)) { h.pos.y = y; h.placed = true; } else return; }   // ring still off: wait for ground
        float spd = 0f;
        if (h.walking)
        {
            var d = h.target - h.pos; d.y = 0f; float dist = d.magnitude;
            if (dist <= G.arriveM) { h.walking = false; h.wait = Pause(h); }
            else
            {
                float want = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; h.yaw = Mathf.MoveTowardsAngle(h.yaw, want, G.turnDegPerS * dt);
                float align = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(h.yaw, want)) / Mathf.Max(1f, K.gait.alignDeg)); spd = G.walkMps * align;   // turn on the spot first, then walk
                var step = Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward * (spd * dt); var np = h.pos + step; float y;
                if (Ground(np.x, np.z, h.pos.y, out y)) { h.pos = new Vector3(np.x, y, np.z); } else { h.walking = false; h.wait = Pause(h); spd = 0f; }   // no ground ahead: stop
            }
        }
        else { h.wait -= dt; if (h.wait <= 0f) { h.target = RandomPoint(herd, h, s, p); h.walking = true; } }
        h.speed = Mathf.Lerp(h.speed, spd, Mathf.Clamp01(dt / Mathf.Max(G.minBlendS, G.blendS)));
        h.amp = Mathf.MoveTowards(h.amp, h.speed > G.standMps ? 1f : 0f, dt / Mathf.Max(G.minBlendS, G.blendS));
        h.phase = Mathf.Repeat(h.phase + G.cyclesPerM * h.speed * dt * 2f * Mathf.PI, 2f * Mathf.PI); h.breath = Mathf.Repeat(h.breath + dt * G.breathCyclesPerS * 2f * Mathf.PI, 2f * Mathf.PI);
        h.bob = Mathf.Abs(Mathf.Sin(h.phase)) * G.bobM * h.amp + Mathf.Sin(h.breath) * G.breathM * (1f - h.amp); h.pitch = Mathf.Sin(h.phase * 2f) * G.pitchDeg * h.amp; h.roll = Mathf.Sin(h.phase) * G.rollDeg * h.amp;
        if (h.midR != null) LodStep(h, p);   // FPSX_D2
        h.go.transform.SetPositionAndRotation(h.pos, Quaternion.Euler(0f, h.yaw, 0f)); h.body.localPosition = Vector3.up * h.bob; h.body.localRotation = Quaternion.Euler(h.pitch, 0f, h.roll);
    }
}
