using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// BANDITPATROL v1 (bandits-dev2, STAGED DARK, UNPROVEN IN UNITY). NEW FILE, no existing file is edited. Everything numeric is Assets/Data/patrol.json (copy in StreamingAssets, read first).
// A missing file / "enabled": false / bandits.json master off = nothing here ever runs (no director, no component): the game is exactly as before.
// What it does: a DORMANT bandit (its AIBrain exists but is disabled = not engaged yet, see ZoneRuntime) is no mannequin any more. It paces inside its group's patrol BOX (patrol.json groups[]):
//   pause (pauseMinS..pauseMaxS, in the combat-ready GUARD stance = the guard_idle pose, a small seeded turn) -> pick a seeded random waypoint in the box (not in an avoid circle / rect, not on top of a group mate, ground under it
//   by a ray, the straight path clear of avoid shapes) -> walk there at the member's own speed (members[].speedMps 0.8-1.4 m/s) FACING the way it walks (guard-walk = legs walk, upper body guard pose via the GuardUpper layer; guardWalk false = plain walk).
//   Player within faceWithinM and no fight going on: stop and turn to face the player (guard stance). Player in a fight (any live opponent within engagedMaxM, a boss included; holdWhileEngaged like AmbientHorses): stand still, stance held.
//   The moment ZoneRuntime engages the bandit (AIBrain enabled) or it is dead / staggered / frozen the component lets go and never touches moveDir / guardHeld again until it is dormant again (leash reset puts it home, then it paces again).
// Movement is the NORMAL sim: this only writes DuelFighter.moveDir (speed = moveDir x walkSpeed x guard speedMul, the sim's own Loco / Guard formula), run = false and guardHeld; the sim keeps the capsule on the ground and ArenaLimits clamps the position (never off the zone / into the
// arena: the box is also computed outside the arena). Timers are sim TICKS (Duel.TickCount) and the draws are a seeded hash of the fighter name (no UnityEngine.Random): same run = same paths. Execution order 110: after AIBrain (100, BanditArcher).
// Unproven in Unity: that moveDir written from Update reaches a dormant (brain disabled) fighter's Loco / Guard tick, that guardHeld works without an opponent, speeds > the guard walk cap, the ground ray against the real colliders, the look of the guard walk.
[DefaultExecutionOrder(110)]
public sealed class BanditPatrol : MonoBehaviour
{
    [Serializable] public class Circle { public float x = 0f, z = 0f, r = 0f; }
    [Serializable] public class RectR { public float xMin = 0f, xMax = 0f, zMin = 0f, zMax = 0f; }
    [Serializable] public class Box { public float xMin = 0f, xMax = 0f, zMin = 0f, zMax = 0f; }
    [Serializable] public class Member { public string spawn = ""; public float speedMps = 1f; }
    [Serializable] public class Group { public string id = "", zone = ""; public bool enabled = true; public Box box = new Box(); public Circle[] avoid = new Circle[0]; public RectR[] rects = new RectR[0]; public Member[] members = new Member[0]; }
    [Serializable] public class Cfg
    {
        public bool enabled = false, holdWhileEngaged = true, guardStanceOnPause = true, guardWalk = true, debugLog = false;
        public float pollS = 0.5f, engagedMaxM = 40f, faceWithinM = 12f, faceTurnDegPerS = 200f, turnDegPerS = 240f, arriveM = 0.45f, walkTimeoutS = 20f, stuckS = 1.5f, stuckM = 0.05f, pauseMinS = 2f, pauseMaxS = 8f, pauseTurnMaxDeg = 70f,   // hc:ok algorithm constant / index / unit guard (reviewed)
            guardSpeedMulFallback = 0.5f, bodyClearM = 0.6f, minSpacingM = 1.6f, groundRayUpM = 3f, groundRayDownM = 8f, groundMaxDeltaYM = 0.6f, outsideBoxPullM = 1f, turnBeforeWalkDeg = 60f, pathStepM = 0.5f, minLegM = 1f;   // hc:ok algorithm constant / index / unit guard (reviewed)
        public int waypointTries = 14, groundMask = -1, seed = 1;   // hc:ok algorithm constant / index / unit guard (reviewed)
        public Group[] groups = new Group[0];   // hc:ok algorithm constant / index / unit guard (reviewed)
    }

    const string FileName = "patrol.json", DataRel = "Data/patrol.json";
    const float Eps = 0.0001f, MinPollS = 0.1f;
    static Cfg c;
    static readonly List<BanditPatrol> All = new List<BanditPatrol>();
    static readonly RaycastHit[] hits = new RaycastHit[8];
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, FileName); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, DataRel);
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[BanditPatrol] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.groups == null) c.groups = new Group[0];   // hc:ok algorithm constant / index / unit guard (reviewed)
        return c;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; All.Clear(); }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Get().enabled || !BanditArcher.Get().enabled) return;   // both master keys: patrol.json AND bandits.json
        if (FindFirstObjectByType<Director>() != null) return;
        var go = new GameObject("BanditPatrolDirector"); go.hideFlags = HideFlags.HideInHierarchy; DontDestroyOnLoad(go); go.AddComponent<Director>();
        Debug.Log("[BanditPatrol] on: " + Get().groups.Length + " groups");
    }

    public sealed class Director : MonoBehaviour
    {
        float nextPoll;
        void Update()
        {
            var k = Get(); if (!k.enabled || !BanditArcher.Get().enabled) return;
            if (Time.unscaledTime < nextPoll) return; nextPoll = Time.unscaledTime + Mathf.Max(MinPollS, k.pollS);
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
            {
                if (f == null || f.Dead || BanditArcher.TypeOf(f.CharacterId) == null || f.GetComponent<BanditPatrol>() != null) continue;
                string sid = SpawnId(f.name); if (sid.Length == 0) continue;   // hc:ok algorithm constant / index / unit guard (reviewed)
                foreach (var g in k.groups)
                {
                    if (g == null || !g.enabled) continue;
                    foreach (var m in g.members) if (m != null && m.spawn == sid) { var bp = f.gameObject.AddComponent<BanditPatrol>(); bp.Init(g, m, sid); if (k.debugLog) Debug.Log("[BanditPatrol] attached " + f.name + " group " + g.id + " speed " + m.speedMps.ToString("F2")); goto next; }
                }
                next:;
            }
        }
    }

    static string SpawnId(string n)   // "<Character>#<spawnId>[@...]" as ZoneRuntime names its fighters
    {
        int h = n.IndexOf('#'); if (h < 0) return ""; string s = n.Substring(h + 1); int at = s.IndexOf('@'); return at >= 0 ? s.Substring(0, at) : s;   // hc:ok algorithm constant / index / unit guard (reviewed)
    }

    // ------------------------------------------------------------------ one bandit
    enum Mode { Pause, Walk }
    DuelFighter df; AIBrain brain; Group g; Member m; string sid = "";
    Mode mode = Mode.Pause; int until, walkStart, draws, stuckTick; Vector3 wp, stuckPos; float targetYaw; bool control, haveYaw;

    public void Init(Group grp, Member mem, string spawn) { g = grp; m = mem; sid = spawn; }
    void Awake() { df = GetComponent<DuelFighter>(); brain = GetComponent<AIBrain>(); }
    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }
    void OnDestroy() { All.Remove(this); }

    void Update()
    {
        var K = Get(); if (!K.enabled || df == null || g == null || m == null || !BanditArcher.Get().enabled) return;
        if (brain == null) brain = GetComponent<AIBrain>();
        bool engaged = brain != null && brain.enabled;
        if (df.Dead || engaged || Duel.FrozenFor(df)) { Release(); return; }
        if (df.state != FState.Loco && df.state != FState.Guard) { Release(); return; }   // hit, stagger, roll, an attack: the machine owns it
        int tick = Duel.TickCount; int hz = Mathf.Max(1, BanditArcher.Get().runtime.tickHz); float dt = Time.deltaTime;   // hc:ok algorithm constant / index / unit guard (reviewed)
        var player = GameLoop.Player; Vector3 pos = transform.position;

        bool fight = K.holdWhileEngaged && player != null && player.opponent != null && !player.opponent.Dead && player.opponent != df
                     && (K.engagedMaxM <= 0f || Vector3.Distance(player.transform.position, player.opponent.transform.position) <= K.engagedMaxM);   // hc:ok algorithm constant / index / unit guard (reviewed)
        if (fight) { Stand(K.guardStanceOnPause); return; }   // hold while Joan is in a fight: no pacing, no turning

        if (player != null && !player.Dead)
        {
            var d = player.transform.position - pos; d.y = 0f;   // hc:ok algorithm constant / index / unit guard (reviewed)
            if (d.magnitude <= K.faceWithinM) { Stand(K.guardStanceOnPause); Turn(d, K.faceTurnDegPerS, dt); mode = Mode.Pause; until = tick + 1; return; }   // look at the traveller (the pause timer restarts when he walks off)   // hc:ok algorithm constant / index / unit guard (reviewed)
        }

        if (mode == Mode.Pause)
        {
            Stand(K.guardStanceOnPause);
            if (!haveYaw) { targetYaw = transform.eulerAngles.y + (Rand(tick, 1) * 2f - 1f) * K.pauseTurnMaxDeg; haveYaw = true; }   // hc:ok algorithm constant / index / unit guard (reviewed)
            Turn(Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward, K.turnDegPerS * 0.5f, dt);   // hc:ok algorithm constant / index / unit guard (reviewed)
            if (tick >= until) { if (PickWaypoint(K, pos, tick)) { mode = Mode.Walk; walkStart = tick; stuckTick = tick; stuckPos = pos; } else StartPause(K, tick, hz); }
            return;
        }

        // Walk
        var to = wp - pos; to.y = 0f; float dist = to.magnitude;   // hc:ok algorithm constant / index / unit guard (reviewed)
        if (dist <= K.arriveM || tick - walkStart > Mathf.RoundToInt(K.walkTimeoutS * hz)) { StartPause(K, tick, hz); return; }
        if (tick - stuckTick >= Mathf.RoundToInt(K.stuckS * hz))
        {
            var mv = pos - stuckPos; mv.y = 0f; if (mv.magnitude < K.stuckM) { if (K.debugLog) Debug.Log("[BanditPatrol] " + name + " stuck at " + pos.ToString("F1") + ": new pause"); StartPause(K, tick, hz); return; }   // hc:ok algorithm constant / index / unit guard (reviewed)
            stuckTick = tick; stuckPos = pos;
        }
        var dir = to / Mathf.Max(Eps, dist);
        float ang = Vector3.Angle(transform.forward, dir);
        Turn(dir, K.turnDegPerS, dt);
        bool guard = K.guardWalk; float mul = guard ? GuardMul(K) : 1f;   // hc:ok algorithm constant / index / unit guard (reviewed)
        float mag = ang > K.turnBeforeWalkDeg ? 0f : m.speedMps / Mathf.Max(0.1f, df.walkSpeed * mul);   // turn on the spot first, then walk (no sliding sideways)   // hc:ok algorithm constant / index / unit guard (reviewed)
        Drive(dir * mag, guard);
    }

    float GuardMul(Cfg K) { var t = DodgeTable.Get(); return t != null && t.enabled ? Mathf.Max(0.05f, t.guard.speedMul) : K.guardSpeedMulFallback; }   // hc:ok minimum multiplier guard

    void Drive(Vector3 move, bool guard) { control = true; df.run = false; df.guardHeld = guard; df.moveDir = move; }
    void Stand(bool guard) { control = true; df.run = false; df.guardHeld = guard; df.moveDir = Vector3.zero; }
    void Release() { if (!control) return; control = false; if (df != null) { df.moveDir = Vector3.zero; df.guardHeld = false; } mode = Mode.Pause; until = 0; haveYaw = false; }

    void Turn(Vector3 dir, float degPerS, float dt)
    {
        dir.y = 0f; if (dir.sqrMagnitude < Eps) return;   // hc:ok algorithm constant / index / unit guard (reviewed)
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir, Vector3.up), degPerS * dt);
    }

    void StartPause(Cfg K, int tick, int hz)
    {
        mode = Mode.Pause; haveYaw = false;
        float u = Rand(tick, 2); until = tick + Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(K.pauseMinS, K.pauseMaxS, u) * hz));   // hc:ok algorithm constant / index / unit guard (reviewed)
        Stand(K.guardStanceOnPause);
    }

    // ------------------------------------------------------------------ waypoints
    float Rand(int tick, int salt)   // seeded: fighter name + counter, no UnityEngine.Random
    {
        uint h = 2166136261u; foreach (char ch in name) h = (h ^ ch) * 16777619u;   // hc:ok algorithm constant / index / unit guard (reviewed)
        h = (h ^ (uint)Get().seed) * 16777619u; h = (h ^ (uint)(++draws)) * 16777619u; h = (h ^ (uint)salt) * 16777619u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;   // hc:ok algorithm constant / index / unit guard (reviewed)
        return (h & 0xFFFFFFu) / 16777216f;   // hc:ok algorithm constant / index / unit guard (reviewed)
    }

    bool InAvoid(Cfg K, float x, float z)
    {
        foreach (var a in g.avoid) { if (a == null) continue; float rr = a.r + K.bodyClearM; if ((x - a.x) * (x - a.x) + (z - a.z) * (z - a.z) < rr * rr) return true; }
        foreach (var r in g.rects) if (r != null && x > r.xMin - K.bodyClearM && x < r.xMax + K.bodyClearM && z > r.zMin - K.bodyClearM && z < r.zMax + K.bodyClearM) return true;
        return false;
    }

    bool PathClear(Cfg K, Vector3 a, Vector3 b)
    {
        float len = Vector3.Distance(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z)); int n = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.1f, K.pathStepM)));   // hc:ok algorithm constant / index / unit guard (reviewed)
        for (int i = 1; i < n; i++) { float t = (float)i / n; if (InAvoid(K, Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.z, b.z, t))) return false; }   // hc:ok algorithm constant / index / unit guard (reviewed)
        return true;
    }

    bool GroundOk(Cfg K, float x, float z, float refY)
    {
        int n = Physics.RaycastNonAlloc(new Vector3(x, refY + K.groundRayUpM, z), Vector3.down, hits, K.groundRayUpM + K.groundRayDownM, K.groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue, y = 0f;   // hc:ok algorithm constant / index / unit guard (reviewed)
        for (int i = 0; i < n; i++) { var h = hits[i]; if (h.collider == null || h.collider.GetComponentInParent<DuelFighter>() != null) continue; if (h.distance < best) { best = h.distance; y = h.point.y; } }   // hc:ok algorithm constant / index / unit guard (reviewed)
        return best < float.MaxValue && Mathf.Abs(y - refY) <= K.groundMaxDeltaYM;
    }

    bool PickWaypoint(Cfg K, Vector3 pos, int tick)
    {
        var b = g.box; float inset = K.bodyClearM;
        float x0 = b.xMin + inset, x1 = b.xMax - inset, z0 = b.zMin + inset, z1 = b.zMax - inset;
        if (x1 <= x0 || z1 <= z0) return false;
        bool outside = pos.x < b.xMin - K.outsideBoxPullM || pos.x > b.xMax + K.outsideBoxPullM || pos.z < b.zMin - K.outsideBoxPullM || pos.z > b.zMax + K.outsideBoxPullM;
        if (outside) { wp = new Vector3(Mathf.Clamp(pos.x, x0, x1), pos.y, Mathf.Clamp(pos.z, z0, z1)); return true; }   // pushed out of the box: walk back in
        for (int i = 0; i < Mathf.Max(1, K.waypointTries); i++)   // hc:ok algorithm constant / index / unit guard (reviewed)
        {
            float x = Mathf.Lerp(x0, x1, Rand(tick, 3)), z = Mathf.Lerp(z0, z1, Rand(tick, 4));   // hc:ok algorithm constant / index / unit guard (reviewed)
            if (Mathf.Abs(x - pos.x) + Mathf.Abs(z - pos.z) < K.minLegM || InAvoid(K, x, z)) continue;
            bool crowded = false;
            foreach (var o in All) { if (o == null || o == this || o.g != g) continue; var q = o.mode == Mode.Walk ? o.wp : o.transform.position; if ((q.x - x) * (q.x - x) + (q.z - z) * (q.z - z) < K.minSpacingM * K.minSpacingM) { crowded = true; break; } }
            if (crowded || !GroundOk(K, x, z, pos.y) || !PathClear(K, pos, new Vector3(x, pos.y, z))) continue;
            wp = new Vector3(x, pos.y, z); if (K.debugLog) Debug.Log("[BanditPatrol] " + name + " -> (" + x.ToString("F1") + ", " + z.ToString("F1") + ") " + m.speedMps.ToString("F2") + " m/s"); return true;
        }
        return false;
    }
}
