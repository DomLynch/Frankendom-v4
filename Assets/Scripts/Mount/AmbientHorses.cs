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
[Serializable] public class AhBreed { public string id = ""; public string glb = ""; public float heightM = 1.75f, glbHeightM = 1.75f; }
[Serializable] public class AhRect { public float xMin, xMax, zMin, zMax; }
[Serializable] public class AhCircle { public float x, z, r; }
[Serializable] public class AhHerd
{
    public string id = ""; public bool enabled = true; public AhRect area = new AhRect(); public string[] breeds = new string[0]; public int count = 1; public int seed = 1; public AhCircle[] avoid = new AhCircle[0];
}
[Serializable] public class AhGait
{
    public float walkMps = 1.1f, turnDegPerS = 90f, pauseSMin = 3f, pauseSMax = 9f, arriveM = 0.4f, cyclesPerM = 0.55f, bobM = 0.02f, pitchDeg = 1.5f, rollDeg = 1.5f, breathM = 0.004f, breathCyclesPerS = 0.3f, blendS = 0.15f, alignFullDeg = 45f;
}
[Serializable] public class AhCfg
{
    public bool enabled = false, debugLog = false, convertMaterials = true, shadows = false, holdWhileEngaged = true;
    public float pollSeconds = 0.5f, spawnMarginM = 14f, despawnMarginM = 22f, avoidPlayerM = 4f, minSpacingM = 3f, groundRayUpM = 3f, groundRayDownM = 10f, sinkM = 0.03f, fallbackY = 0f;
    public int groundMask = -1, framesBetweenLoads = 2, maxTargetTries = 12;
    public AhGait gait = new AhGait(); public AhBreed[] breeds = new AhBreed[0]; public AhHerd[] herds = new AhHerd[0];
}

public sealed class AmbientHorses : MonoBehaviour
{
    static AhCfg K; static AmbientHorses Inst; static readonly RaycastHit[] hits = new RaycastHit[8];
    class Horse { public GameObject go; public Transform body; public AhHerd herd; public Vector3 pos, target; public float yaw, wait, phase, breath, speed, amp, bob, pitch, roll, spawnY; public bool walking, placed; public System.Random rnd; }
    class Loaded { public GltfHold hold; }
    class GltfHold { public GLTFast.GltfImport imp; }
    class HerdState { public List<Horse> horses = new List<Horse>(); public bool loading, tried, asleep; public List<Material> mats = new List<Material>(); public List<GltfHold> holds = new List<GltfHold>(); public int gen; }
    readonly Dictionary<AhHerd, HerdState> st = new Dictionary<AhHerd, HerdState>(); float nextPoll;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; K = null; }
    public static AhCfg Get()
    {
        if (K != null) return K; K = new AhCfg(); string txt = DataPath.ReadText("ambient_horses.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<AhCfg>(txt); if (k != null) K = k; } catch (Exception e) { Debug.LogWarning("[AmbientHorses] ambient_horses.json unreadable, off: " + e.Message); K = new AhCfg(); } }
        if (K.gait == null) K.gait = new AhGait(); if (K.breeds == null) K.breeds = new AhBreed[0]; if (K.herds == null) K.herds = new AhHerd[0];
        return K;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return; var k = Get(); if (!k.enabled || k.herds.Length == 0) return; if (PerfFlags.HarnessRun()) return;
        var go = new GameObject("AmbientHorses"); DontDestroyOnLoad(go); Inst = go.AddComponent<AmbientHorses>();
    }
    void OnDestroy() { foreach (var kv in st) Free(kv.Value); st.Clear(); if (Inst == this) Inst = null; }

    static AhBreed Breed(string id) { foreach (var b in K.breeds) if (b != null && b.id == id) return b; return null; }
    static string File(AhBreed b) { try { var p = System.IO.Path.Combine(Application.streamingAssetsPath, b.glb); return System.IO.File.Exists(p) ? p : null; } catch (Exception) { return null; } }

    void Update()
    {
        float now = Time.unscaledTime; var p = GameLoop.Player; bool zones = ZoneRuntime.Owns && ZoneSpan.Live && p != null && !ZoneGroundHold.Active;
        if (now >= nextPoll)
        {
            nextPoll = now + K.pollSeconds;
            foreach (var h in K.herds)
            {
                if (h == null || !h.enabled || h.count <= 0 || h.breeds == null || h.breeds.Length == 0) continue; HerdState s; if (!st.TryGetValue(h, out s)) { s = new HerdState(); st[h] = s; }
                bool near = zones && Near(p.transform.position, h.area, s.asleep || s.horses.Count > 0 ? K.despawnMarginM : K.spawnMarginM);
                bool engaged = K.holdWhileEngaged && p != null && p.opponent != null && !p.opponent.Dead;
                if (near && !engaged) { if (s.asleep) Wake(s); else if (!s.loading && !s.tried && s.horses.Count == 0) { s.tried = true; var t = SpawnHerd(h, s, ++s.gen); } }
                else if (!near || engaged) { if (s.horses.Count > 0 && !s.asleep) Sleep(s); else if (s.loading) { s.gen++; s.tried = false; } if (!near) s.tried = false; }
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
            var rnd = new System.Random(h.seed);
            for (int i = 0; i < h.count; i++)
            {
                if (gen != s.gen) { Free(s); return; }
                var b = Breed(h.breeds[rnd.Next(h.breeds.Length)]); if (b == null) { Debug.LogWarning("[AmbientHorses] herd " + h.id + ": unknown breed"); continue; }
                string file = File(b); if (file == null) { Debug.LogWarning("[AmbientHorses] breed " + b.id + ": glb not found under StreamingAssets: " + b.glb); continue; }
                var go = new GameObject("AmbientHorse_" + h.id + "_" + i); DontDestroyOnLoad(go); go.SetActive(false);
                var body = new GameObject("Body"); body.transform.SetParent(go.transform, false);
                var hold = new GltfHold { imp = new GLTFast.GltfImport() }; s.holds.Add(hold);
                if (!await hold.imp.LoadFile(file) || !await hold.imp.InstantiateMainSceneAsync(body.transform)) { Debug.LogWarning("[AmbientHorses] glb failed: " + file); Destroy(go); continue; }
                if (gen != s.gen) { Destroy(go); Free(s); return; }
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
                var rl = new List<Renderer>(go.GetComponentsInChildren<Renderer>(true));
                foreach (var r in rl) r.shadowCastingMode = K.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                if (K.convertMaterials) s.mats.AddRange(GearMaterials.Convert(rl));
                body.transform.localScale = Vector3.one * (b.heightM > 0f && b.glbHeightM > 0f ? b.heightM / b.glbHeightM : 1f);
                var hr = new Horse { go = go, body = body.transform, herd = h, rnd = new System.Random(h.seed * 7919 + i), wait = 0f };
                hr.pos = RandomPoint(h, hr, s, null); hr.yaw = (float)(hr.rnd.NextDouble() * 360.0); hr.target = hr.pos; hr.wait = Pause(hr);
                s.horses.Add(hr);
                for (int f = 0; f < K.framesBetweenLoads; f++) await Task.Yield();
            }
            if (K.debugLog) Debug.Log("[AmbientHorses] herd " + h.id + ": " + s.horses.Count + " horses loaded");
        }
        catch (Exception e) { Debug.LogWarning("[AmbientHorses] herd " + h.id + " error: " + e.Message); }
        finally { s.loading = false; }
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
                float align = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(h.yaw, want)) / Mathf.Max(1f, G.alignFullDeg)); spd = G.walkMps * align;   // turn on the spot first, then walk
                var step = Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward * (spd * dt); var np = h.pos + step; float y;
                if (Ground(np.x, np.z, h.pos.y, out y)) { h.pos = new Vector3(np.x, y, np.z); } else { h.walking = false; h.wait = Pause(h); spd = 0f; }   // no ground ahead: stop
            }
        }
        else { h.wait -= dt; if (h.wait <= 0f) { h.target = RandomPoint(herd, h, s, p); h.walking = true; } }
        h.speed = Mathf.Lerp(h.speed, spd, Mathf.Clamp01(dt / Mathf.Max(1e-3f, G.blendS)));
        h.amp = Mathf.MoveTowards(h.amp, h.speed > 0.05f ? 1f : 0f, dt / Mathf.Max(1e-3f, G.blendS));   // hc:ok 0.05 m/s = standing threshold
        h.phase = Mathf.Repeat(h.phase + G.cyclesPerM * h.speed * dt * 2f * Mathf.PI, 2f * Mathf.PI); h.breath = Mathf.Repeat(h.breath + dt * G.breathCyclesPerS * 2f * Mathf.PI, 2f * Mathf.PI);
        h.bob = Mathf.Abs(Mathf.Sin(h.phase)) * G.bobM * h.amp + Mathf.Sin(h.breath) * G.breathM * (1f - h.amp); h.pitch = Mathf.Sin(h.phase * 2f) * G.pitchDeg * h.amp; h.roll = Mathf.Sin(h.phase) * G.rollDeg * h.amp;
        h.go.transform.SetPositionAndRotation(h.pos, Quaternion.Euler(0f, h.yaw, 0f)); h.body.localPosition = Vector3.up * h.bob; h.body.localRotation = Quaternion.Euler(h.pitch, 0f, h.roll);
    }
}
