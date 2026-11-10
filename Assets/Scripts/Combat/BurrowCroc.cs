using System;
using System.Collections.Generic;
using UnityEngine;

// BURROWCROC v1 (laneBURROWCROC, STAGED DARK, UNPROVEN IN UNITY). NEW FILE (with BurrowCrocFx.cs); NO existing file is edited. Everything numeric is Assets/Data/burrow_croc.json (copy in StreamingAssets, read via DataPath).
// A missing file / "enabled": false = no director, no component, no fx object: the game is exactly as before.
// What it is: a BURROWING CROC (Sand King / underground caterpillar feel) in the Sobek zone (zones.json sobek_reach). It is NOT a new character and NOT a new system: apply_burrow_croc.py adds ONE spawn row
//   (z2_burrow_croc, creature Sobek = the existing rigged croc with his own claw / bite moves, small hpMul, damageMul) to zones.json, so ZoneRuntime gives it everything an enemy has: aggro radius, engage, leash reset, target / lock-on,
//   hp, hit resolution (Duel.Resolve), kill record, loot, and the REAL-TIME RESPAWN (respawnMinS = respawnMaxS = 180). This file only adds the burrowing behaviour on top, like BanditPatrol does for bandits (same director pattern).
// Dormant (its AIBrain is disabled = not engaged yet): it lives on a SIDE LANE box (burrow_croc.json lane.box, away from the fight lane x +-2.45 and from the boss stand), half submerged: the visual pivot ("Visual", the same pivot Flinch uses) is
//   moved DOWN by visual.sinkM and tipped forward by visual.leanDeg, so the terrain hides the legs / belly and only the back, head and tail show. It roams the box slowly (pause -> seeded waypoint -> walk at lane.speedMps, moveDir like BanditPatrol),
//   leaves a DUST trail (BurrowCrocFx particles) and ripple rings (BurrowCrocFx decals) at its position. It stops and turns to face Joan within sense.faceWithinM, and stands still (stays under) while Joan fights anything else (holdWhileEngaged).
// Engaged (ZoneRuntime brain enabled when Joan walks within spawn.aggro of it): it SURFACES (visual sink -> chaseSinkFrac over visual.surfaceS, dust burst + ripple, a short lunge toward Joan for visual.lungeS), then the normal Sobek AI bites / claws
//   and chases her. ZoneRuntime's leash (spawn.leash from home) makes it forget her, reset to full hp and go home (teleport) BEFORE she reaches the boss ring; it BURROWS again (sink back, burst, ripple). If the Sobek boss is engaged it is released
//   (ChaseAgent.GiveUp when chase.json is on). Dead: it rises to the surface so the corpse is not half in the ground; the kill sets the 180 s respawn.
// Hits flash: burrow_croc.json flash (the existing hit_flash.json Sobek row is enabled:false, so the boss does not flash; this croc has its own short per-material colour flash with the HitFlash technique: MaterialPropertyBlock, always restored).
// Execution order 110 (after AIBrain 100, same as BanditPatrol). Presentation only except moveDir (roam, lunge). No UnityEngine.Random in the roam (seeded hash of the fighter name).
// Unproven in Unity: that moveDir written from Update reaches a dormant fighter's Loco tick (same assumption as BanditPatrol), the sunk look of the real Sobek mesh (sinkM / leanDeg are first guesses), the DecalProjector look on the real ground,
//   the Furnace/Particle dust look, that ZoneRuntime engages / respawns a second Sobek spawn row without side effects (boss rows keyed by the name prefix "Sobek": HUD name, finisher rules), the play_areas.json pocket row.
[DefaultExecutionOrder(110)]
public sealed class BurrowCroc : MonoBehaviour
{
    [Serializable] public class Box { public float xMin = -10.5f, xMax = -6.5f, zMin = 38f, zMax = 44f; }
    [Serializable] public class Circle { public float x = 0f, z = 0f, r = 0f; }
    [Serializable] public class SpawnRow { public string id = "z2_burrow_croc", zone = "sobek_reach", spot = "burrow_croc_home", creature = "Sobek"; }
    [Serializable] public class LaneCfg
    {
        public Box box = new Box(); public Circle[] avoid = new Circle[0];
        public float speedMps = 1.1f, pauseMinS = 1.5f, pauseMaxS = 5f, turnDegPerS = 140f, arriveM = 0.5f, walkTimeoutS = 25f, stuckS = 2f, stuckM = 0.05f, bodyClearM = 0.8f, pathStepM = 0.5f, minLegM = 1.5f, turnBeforeWalkDeg = 50f,
            groundRayUpM = 3f, groundRayDownM = 8f, groundMaxDeltaYM = 0.7f, outsideBoxPullM = 1f;
        public int waypointTries = 14, seed = 7, groundMask = -1;
    }
    [Serializable] public class SenseCfg { public float faceWithinM = 11f, faceTurnDegPerS = 120f, engagedMaxM = 40f, pollS = 0.5f; public bool holdWhileEngaged = true, releaseWhenBossEngaged = true; public string bossSpawnId = "z2_sobek"; }
    [Serializable] public class VisualCfg
    {
        public string visualChild = "Visual";
        public float sinkM = 1f, leanDeg = 20f, chaseSinkFrac = 0.1f, surfaceS = 0.4f, burrowS = 0.9f, deathRiseS = 0.3f, popM = 0.15f, popS = 0.15f, lungeS = 0.35f, lungeSpeedMps = 6.5f, minDelta = 0.002f;
    }
    [Serializable] public class FlashCfg { public bool enabled = true; public float seconds = 0.12f, riseS = 0.02f, strength = 0.5f; public float[] color = { 1f, 0.82f, 0.6f }; public string[] baseColorProps = { "_BaseColor", "_Color" }; }
    [Serializable] public class Cfg
    {
        public bool enabled = false, debugLog = false, logHits = true; public int version = 1;
        public SpawnRow spawn = new SpawnRow(); public LaneCfg lane = new LaneCfg(); public SenseCfg sense = new SenseCfg(); public VisualCfg visual = new VisualCfg(); public FlashCfg flash = new FlashCfg();
        public BurrowCrocFx.DustCfg dust = new BurrowCrocFx.DustCfg(); public BurrowCrocFx.RippleCfg ripple = new BurrowCrocFx.RippleCfg();
    }

    const string FileName = "burrow_croc.json";
    const float Eps = 0.0001f, MinPollS = 0.1f;
    static Cfg c;
    static readonly RaycastHit[] hits = new RaycastHit[8];
    public static readonly List<BurrowCroc> All = new List<BurrowCroc>();
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string txt = DataPath.ReadText(FileName);
            if (txt != null) { var k = JsonUtility.FromJson<Cfg>(txt.TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[BurrowCroc] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.spawn == null) c.spawn = new SpawnRow(); if (c.lane == null) c.lane = new LaneCfg(); if (c.sense == null) c.sense = new SenseCfg(); if (c.visual == null) c.visual = new VisualCfg(); if (c.flash == null) c.flash = new FlashCfg();
        if (c.dust == null) c.dust = new BurrowCrocFx.DustCfg(); if (c.ripple == null) c.ripple = new BurrowCrocFx.RippleCfg(); if (c.lane.box == null) c.lane.box = new Box(); if (c.lane.avoid == null) c.lane.avoid = new Circle[0];   // hc:ok null guards
        return c;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; All.Clear(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var k = Get(); if (!k.enabled) return;
        if (FindFirstObjectByType<Director>() != null) return;
        var go = new GameObject("BurrowCrocDirector"); go.hideFlags = HideFlags.HideInHierarchy; DontDestroyOnLoad(go); go.AddComponent<Director>();
        var b = k.lane.box;
        Debug.Log("[BurrowCroc] on: spawn " + k.spawn.id + " (" + k.spawn.creature + ") lane box x " + b.xMin.ToString("F1") + ".." + b.xMax.ToString("F1") + " z " + b.zMin.ToString("F1") + ".." + b.zMax.ToString("F1") + " sink " + k.visual.sinkM.ToString("F2") + " m");
    }

    // ------------------------------------------------------------------ director: attaches the component to the spawn row's fighter ("Sobek#z2_burrow_croc", as ZoneRuntime names it), owns the fx object
    public sealed class Director : MonoBehaviour
    {
        float nextPoll; BurrowCrocFx fx;
        void Update()
        {
            var k = Get(); if (!k.enabled) return;
            if (fx == null) fx = BurrowCrocFx.Make(k, transform);
            if (Time.unscaledTime < nextPoll) return; nextPoll = Time.unscaledTime + Mathf.Max(MinPollS, k.sense.pollS);
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
            {
                if (f == null || f.Dead || f.GetComponent<BurrowCroc>() != null || SpawnId(f.name) != k.spawn.id) continue;
                var bc = f.gameObject.AddComponent<BurrowCroc>(); bc.fx = fx;
                Debug.Log("[BurrowCroc] attached " + f.name + " at " + f.transform.position.ToString("F1") + " hp " + f.health.ToString("F0") + "/" + f.maxHealth.ToString("F0"));
            }
        }
    }

    public static string SpawnId(string n)   // "<Character>#<spawnId>[@...]" as ZoneRuntime names its fighters
    {
        int h = n.IndexOf('#'); if (h < 0) return ""; string s = n.Substring(h + 1); int at = s.IndexOf('@'); return at >= 0 ? s.Substring(0, at) : s;   // hc:ok algorithm constant / index / unit guard
    }

    // ------------------------------------------------------------------ one croc
    enum Mode { Pause, Walk }
    class Slot { public Renderer r; public int idx, baseId = -1; public Color baseC; public MaterialPropertyBlock saved; }
    DuelFighter df; AIBrain brain; ChaseAgent chase; Transform pivot; BurrowCrocFx fx;
    Mode mode = Mode.Pause; float until, walkStart, stuckAt, surf, lungeUntil, nextBossPoll, flashT0 = -1f, flashSeconds, targetYaw, rippleAcc, dustAcc, bossLogAt = -999f; int draws, hitCount;
    Vector3 wp, stuckPos, lastPos; bool control, haveYaw, wasEngaged, wasDead, pivotLooked, pivotTouched, haveLast, bossEngaged, flashSlotsBuilt, flashOn;
    readonly List<Slot> slots = new List<Slot>(); MaterialPropertyBlock mpb;

    void Awake() { df = GetComponent<DuelFighter>(); brain = GetComponent<AIBrain>(); chase = GetComponent<ChaseAgent>(); mpb = new MaterialPropertyBlock(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); ContactRelay.OnEvent += OnContact; }
    void OnDisable() { All.Remove(this); ContactRelay.OnEvent -= OnContact; EndFlash(); }
    void OnDestroy() { All.Remove(this); }

    bool Engaged { get { return df != null && !df.Dead && brain != null && brain.enabled; } }

    void Update()
    {
        var K = Get(); if (!K.enabled || df == null) return;
        if (brain == null) brain = GetComponent<AIBrain>(); if (chase == null) chase = GetComponent<ChaseAgent>();
        float dt = Time.deltaTime; Vector3 pos = transform.position; bool dead = df.Dead, engaged = Engaged;
        var player = GameLoop.Player;

        if (engaged != wasEngaged)   // edge: surface / burrow
        {
            wasEngaged = engaged;
            if (engaged) { lungeUntil = Time.time + K.visual.lungeS; Release(); if (fx != null) { fx.Burst(pos, transform.forward, true); fx.Ring(pos, K.ripple.surfaceSizeMul); } Debug.Log("[BurrowCroc] SURFACE " + name + " dist " + FlatDist(player, pos).ToString("F1") + " m hp " + df.health.ToString("F0") + "/" + df.maxHealth.ToString("F0")); }
            else if (!dead) { if (fx != null) { fx.Burst(pos, transform.forward, false); fx.Ring(pos, K.ripple.surfaceSizeMul); } Debug.Log("[BurrowCroc] BURROW " + name + " (disengaged: leash / gave up) hp " + df.health.ToString("F0")); }
        }
        if (dead != wasDead) { wasDead = dead; if (dead) { Release(); Debug.Log("[BurrowCroc] DEAD " + name + " after " + hitCount + " hits: rises, respawn by the zone clock"); } }

        // visual factor 0 = fully sunk, 1 = up. Targets: dead = up, engaged = up minus chaseSinkFrac, dormant = sunk.
        float target = dead ? 1f : engaged ? 1f - Mathf.Clamp01(K.visual.chaseSinkFrac) : 0f;
        float secs = dead ? K.visual.deathRiseS : (target > surf ? K.visual.surfaceS : K.visual.burrowS);
        surf = Mathf.MoveTowards(surf, target, dt / Mathf.Max(MinPollS * 0.1f, secs));   // hc:ok divide-by-zero floor

        // movement / fx bookkeeping (flat distance moved this frame)
        float moved = 0f; if (haveLast) { var d = pos - lastPos; d.y = 0f; moved = d.magnitude; } lastPos = pos; haveLast = true;   // hc:ok flat
        if (!dead && dt > Eps && moved / dt >= K.dust.minMoveMps && fx != null) FxMove(K, pos, moved);

        BossWatch(K);

        if (dead) { Release(); return; }
        if (engaged) { Lunge(K, player); return; }
        if (df.state != FState.Loco && df.state != FState.Guard) { Release(); return; }   // hit / stagger / an attack: the machine owns it
        if (Duel.FrozenFor(df)) { Release(); return; }
        Roam(K, player, pos, dt);
    }

    static float FlatDist(DuelFighter p, Vector3 q) { if (p == null) return -1f; var d = p.transform.position - q; d.y = 0f; return d.magnitude; }   // hc:ok flat

    // ------------------------------------------------------------------ fx while it moves
    void FxMove(Cfg K, Vector3 pos, float moved)
    {
        bool under = surf < 0.99f || Engaged;   // hc:ok algorithm constant / guard (reviewed)
        if (!under) return;
        var back = -transform.forward;
        dustAcc += moved * K.dust.perM; int n = Mathf.FloorToInt(dustAcc); if (n > 0) { dustAcc -= n; fx.Dust(pos, back, n, 1f, 1f); }
        rippleAcc += moved; if (rippleAcc >= Mathf.Max(0.1f, K.ripple.intervalM)) { rippleAcc = 0f; fx.Ring(pos, 1f); }   // hc:ok algorithm constant / guard (reviewed)
    }

    // ------------------------------------------------------------------ boss guard: the Sobek boss engaged = this croc lets go (the ring is the boss's)
    void BossWatch(Cfg K)
    {
        if (Time.unscaledTime < nextBossPoll) return; nextBossPoll = Time.unscaledTime + Mathf.Max(MinPollS, K.sense.pollS);
        bossEngaged = false; if (string.IsNullOrEmpty(K.sense.bossSpawnId)) return;
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (f == null || f == df || f.Dead || SpawnId(f.name) != K.sense.bossSpawnId) continue;
            var b = f.GetComponent<AIBrain>(); if (b != null && b.enabled) { bossEngaged = true; break; }
        }
        if (bossEngaged && K.sense.releaseWhenBossEngaged && Engaged && chase != null && chase.GiveUp() && Time.unscaledTime - bossLogAt > 5f) { bossLogAt = Time.unscaledTime; Debug.Log("[BurrowCroc] boss " + K.sense.bossSpawnId + " engaged: " + name + " gives up the chase"); }   // hc:ok log throttle
    }

    // ------------------------------------------------------------------ surface lunge: a short run at Joan right after it comes up (the bite is the normal Sobek move)
    void Lunge(Cfg K, DuelFighter player)
    {
        if (player == null || player.Dead || Time.time >= lungeUntil || df.state != FState.Loco) { if (control && Time.time >= lungeUntil) Release(); return; }
        var to = player.transform.position - transform.position; to.y = 0f; if (to.sqrMagnitude < Eps) return;   // hc:ok flat
        var dir = to.normalized; Turn(dir, K.lane.turnDegPerS * 4f, Time.deltaTime);   // hc:ok quick turn on the lunge
        control = true; df.run = true; df.guardHeld = false; df.moveDir = dir * Mathf.Clamp01(K.visual.lungeSpeedMps / Mathf.Max(0.1f, df.runSpeed));   // hc:ok algorithm constant / guard (reviewed)
    }

    // ------------------------------------------------------------------ roam in the lane box (dormant): same structure as BanditPatrol, one croc, no guard stance
    void Roam(Cfg K, DuelFighter player, Vector3 pos, float dt)
    {
        var L = K.lane; var S = K.sense;
        bool fight = S.holdWhileEngaged && player != null && player.opponent != null && !player.opponent.Dead && player.opponent != df
                     && (S.engagedMaxM <= 0f || Vector3.Distance(player.transform.position, player.opponent.transform.position) <= S.engagedMaxM);
        if (fight || bossEngaged) { Stand(); return; }   // Joan is in a fight: no roaming, stays under
        if (player != null && !player.Dead)
        {
            var d = player.transform.position - pos; d.y = 0f;   // hc:ok flat
            if (d.magnitude <= S.faceWithinM) { Stand(); Turn(d, S.faceTurnDegPerS, dt); mode = Mode.Pause; until = Time.time + 1f; return; }   // it feels her coming: stops, faces her
        }
        if (mode == Mode.Pause)
        {
            Stand();
            if (!haveYaw) { targetYaw = transform.eulerAngles.y + (Rand(1) * 2f - 1f) * 70f; haveYaw = true; }   // hc:ok seeded small turn while paused
            Turn(Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward, L.turnDegPerS * 0.5f, dt);
            if (Time.time >= until) { if (PickWaypoint(K, pos)) { mode = Mode.Walk; walkStart = Time.time; stuckAt = Time.time; stuckPos = pos; } else StartPause(L); }
            return;
        }
        var to = wp - pos; to.y = 0f; float dist = to.magnitude;   // hc:ok flat
        if (dist <= L.arriveM || Time.time - walkStart > L.walkTimeoutS) { StartPause(L); return; }
        if (Time.time - stuckAt >= L.stuckS)
        {
            var mv = pos - stuckPos; mv.y = 0f; if (mv.magnitude < L.stuckM) { if (K.debugLog) Debug.Log("[BurrowCroc] " + name + " stuck at " + pos.ToString("F1") + ": new pause"); StartPause(L); return; }   // hc:ok flat
            stuckAt = Time.time; stuckPos = pos;
        }
        var dir = to / Mathf.Max(Eps, dist); float ang = Vector3.Angle(transform.forward, dir);
        Turn(dir, L.turnDegPerS, dt);
        float mag = ang > L.turnBeforeWalkDeg ? 0f : Mathf.Clamp01(L.speedMps / Mathf.Max(0.1f, df.walkSpeed));   // turn on the spot first, then crawl   // hc:ok algorithm constant / guard (reviewed)
        Drive(dir * mag);
    }

    void Drive(Vector3 move) { control = true; df.run = false; df.guardHeld = false; df.moveDir = move; }
    void Stand() { control = true; df.run = false; df.guardHeld = false; df.moveDir = Vector3.zero; }
    void Release() { if (!control) return; control = false; if (df != null) { df.moveDir = Vector3.zero; df.guardHeld = false; df.run = false; } mode = Mode.Pause; until = 0f; haveYaw = false; }

    void Turn(Vector3 dir, float degPerS, float dt)
    {
        dir.y = 0f; if (dir.sqrMagnitude < Eps) return;   // hc:ok flat
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir, Vector3.up), degPerS * dt);
    }

    void StartPause(LaneCfg L) { mode = Mode.Pause; haveYaw = false; until = Time.time + Mathf.Lerp(L.pauseMinS, L.pauseMaxS, Rand(2)); Stand(); }

    // ------------------------------------------------------------------ waypoints
    float Rand(int salt)   // seeded: fighter name + counter, no UnityEngine.Random
    {
        uint h = 2166136261u; foreach (char ch in name) h = (h ^ ch) * 16777619u;   // hc:ok FNV-1a constants
        h = (h ^ (uint)Get().lane.seed) * 16777619u; h = (h ^ (uint)(++draws)) * 16777619u; h = (h ^ (uint)salt) * 16777619u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;   // hc:ok FNV-1a constants
        return (h & 0xFFFFFFu) / 16777216f;   // hc:ok 24-bit fraction
    }

    bool InAvoid(LaneCfg L, float x, float z)
    {
        foreach (var a in L.avoid) { if (a == null) continue; float rr = a.r + L.bodyClearM; if ((x - a.x) * (x - a.x) + (z - a.z) * (z - a.z) < rr * rr) return true; }
        return false;
    }

    bool PathClear(LaneCfg L, Vector3 a, Vector3 b)
    {
        float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z)); int n = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.1f, L.pathStepM)));   // hc:ok flat
        for (int i = 1; i < n; i++) { float t = (float)i / n; if (InAvoid(L, Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.z, b.z, t))) return false; }
        return true;
    }

    bool GroundOk(LaneCfg L, float x, float z, float refY)
    {
        int n = Physics.RaycastNonAlloc(new Vector3(x, refY + L.groundRayUpM, z), Vector3.down, hits, L.groundRayUpM + L.groundRayDownM, L.groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue, y = 0f;   // hc:ok sentinel
        for (int i = 0; i < n; i++) { var h = hits[i]; if (h.collider == null || h.collider.GetComponentInParent<DuelFighter>() != null) continue; if (h.distance < best) { best = h.distance; y = h.point.y; } }
        return best < float.MaxValue && Mathf.Abs(y - refY) <= L.groundMaxDeltaYM;
    }

    bool PickWaypoint(Cfg K, Vector3 pos)
    {
        var L = K.lane; var b = L.box; float inset = L.bodyClearM;
        float x0 = b.xMin + inset, x1 = b.xMax - inset, z0 = b.zMin + inset, z1 = b.zMax - inset;
        if (x1 <= x0 || z1 <= z0) return false;
        bool outside = pos.x < b.xMin - L.outsideBoxPullM || pos.x > b.xMax + L.outsideBoxPullM || pos.z < b.zMin - L.outsideBoxPullM || pos.z > b.zMax + L.outsideBoxPullM;
        if (outside) { wp = new Vector3(Mathf.Clamp(pos.x, x0, x1), pos.y, Mathf.Clamp(pos.z, z0, z1)); return true; }   // pushed out of the box (leash reset puts it home): crawl back in
        for (int i = 0; i < Mathf.Max(1, L.waypointTries); i++)
        {
            float x = Mathf.Lerp(x0, x1, Rand(3)), z = Mathf.Lerp(z0, z1, Rand(4));
            if (Mathf.Abs(x - pos.x) + Mathf.Abs(z - pos.z) < L.minLegM || InAvoid(L, x, z)) continue;
            if (!GroundOk(L, x, z, pos.y) || !PathClear(L, pos, new Vector3(x, pos.y, z))) continue;
            wp = new Vector3(x, pos.y, z); if (K.debugLog) Debug.Log("[BurrowCroc] " + name + " -> (" + x.ToString("F1") + ", " + z.ToString("F1") + ") " + L.speedMps.ToString("F2") + " m/s"); return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the sunk look: the "Visual" pivot goes down and tips forward. Y is written ABSOLUTE (root y minus the offset; Flinch / FootGround never put the pivot off the root height),
    // the tip is absolute from the root rotation, and both are written only while the offset is non-zero (plus ONE reset write when it reaches zero), so nothing accumulates and a surfaced croc is untouched.
    void FindPivot(VisualCfg V)
    {
        pivotLooked = true;
        if (!string.IsNullOrEmpty(V.visualChild)) pivot = transform.Find(V.visualChild);
        if (pivot == null) { var smr = GetComponentInChildren<SkinnedMeshRenderer>(true); if (smr != null) { var t = smr.transform; while (t.parent != null && t.parent != transform) t = t.parent; if (t.parent == transform) pivot = t; } }
        if (pivot == null) Debug.LogWarning("[BurrowCroc] " + name + ": no visual pivot (child '" + V.visualChild + "' or the mesh root): it will not sink, only dust / ripples");
        else if (Get().debugLog) Debug.Log("[BurrowCroc] pivot " + pivot.name);
    }

    void LateUpdate()
    {
        var K = Get(); if (!K.enabled || df == null) return; var V = K.visual;
        if (!pivotLooked) FindPivot(V);
        TickFlash(K);
        if (pivot == null) return;
        float e = surf * surf * (3f - 2f * surf);   // smoothstep
        float off = (1f - e) * V.sinkM;
        float pop = surf > 0f && surf < 1f ? Mathf.Sin(surf * Mathf.PI) * V.popM : 0f;   // hc:ok half sine overshoot while it breaks the surface (only on the way up: sink is shrinking)
        if (Engaged || df.Dead) off -= pop;
        float lean = (1f - e) * V.leanDeg;
        if (off > V.minDelta || off < -V.minDelta || lean > V.minDelta)
        {
            var p = pivot.position; p.y = transform.position.y - off; pivot.position = p;
            if (lean > V.minDelta) pivot.rotation = Quaternion.AngleAxis(lean, transform.right) * transform.rotation;
            pivotTouched = true;
        }
        else if (pivotTouched)
        {
            pivotTouched = false; var p = pivot.position; p.y = transform.position.y; pivot.position = p; pivot.rotation = transform.rotation;
        }
    }

    // ------------------------------------------------------------------ hit flash (HitFlash technique, per material slot, always restored)
    void OnContact(CombatEvent e)
    {
        if (e.target != df || e.type != "Hit") return; var K = Get(); if (!K.enabled) return;
        hitCount++; if (K.logHits) Debug.Log("[BurrowCroc] HIT " + name + " #" + hitCount + " hp " + df.health.ToString("F0") + "/" + df.maxHealth.ToString("F0") + " tick " + Duel.TickCount);
        if (K.flash.enabled && !df.Dead) BeginFlash(K);
    }

    void BeginFlash(Cfg K)
    {
        if (flashOn) RestoreSlots();
        slots.Clear(); flashSlotsBuilt = true;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is SpriteRenderer) continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue; var sl = new Slot { r = r, idx = i };
                foreach (var pn in K.flash.baseColorProps) if (!string.IsNullOrEmpty(pn) && m.HasProperty(pn)) { sl.baseId = Shader.PropertyToID(pn); sl.baseC = m.GetColor(sl.baseId); break; }
                if (sl.baseId < 0) continue;
                r.GetPropertyBlock(mpb, i); if (!mpb.isEmpty) { sl.saved = new MaterialPropertyBlock(); r.GetPropertyBlock(sl.saved, i); }
                slots.Add(sl);
            }
        }
        flashT0 = Time.unscaledTime; flashSeconds = Mathf.Max(0.02f, K.flash.seconds); flashOn = true; ApplyFlash(K);   // hc:ok algorithm constant / guard (reviewed)
    }

    void TickFlash(Cfg K) { if (flashOn) ApplyFlash(K); }

    void ApplyFlash(Cfg K)
    {
        float age = Time.unscaledTime - flashT0, rise = Mathf.Clamp(K.flash.riseS, 0.001f, flashSeconds);
        if (age >= flashSeconds) { EndFlash(); return; }
        float k = age < rise ? age / rise : 1f - (age - rise) / Mathf.Max(0.0001f, flashSeconds - rise);   // hc:ok envelope
        var fc = K.flash.color != null && K.flash.color.Length >= 3 ? new Color(K.flash.color[0], K.flash.color[1], K.flash.color[2], 1f) : Color.white;
        foreach (var sl in slots)
        {
            if (sl.r == null) continue;
            if (sl.saved != null) sl.r.GetPropertyBlock(mpb, sl.idx); else mpb.Clear();
            mpb.SetColor(sl.baseId, Color.Lerp(sl.baseC, new Color(fc.r, fc.g, fc.b, sl.baseC.a), Mathf.Clamp01(K.flash.strength) * k));
            sl.r.SetPropertyBlock(mpb, sl.idx);
        }
    }

    void RestoreSlots() { foreach (var sl in slots) if (sl.r != null) sl.r.SetPropertyBlock(sl.saved, sl.idx); }
    void EndFlash() { if (!flashOn) return; flashOn = false; RestoreSlots(); slots.Clear(); }
}
