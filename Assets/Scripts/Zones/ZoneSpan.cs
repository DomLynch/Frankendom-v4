using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// ZONESPAN v1 (on ZONE2DUP v1): zones are a DATA LIST. zone_span.json "zones" holds one row per physical zone (offset, ring groups, gate row); any number of rows, so a third zone, a tenth zone or a moved safe zone is only data (gen_zone2.py + Zone2Build).
// Zone 0 = the loaded scene (Arena); zone i>=1 = the inactive root of row i. The wake / retire rule is SpanChain (pure, ZoneSpanCore.cs), identical to the old two-zone SpanCore for 2 zones.
// New data flag gate.destSafe: the zone behind that gate is a safe zone: followers stop at the door (clamped gate.followStopBehind m before the door face), the gate is NOT shut by an engaged creature, no soft wall.
// DARK: only created when zone_span.json enabled=true AND laneZONES' zones.json is on (ZoneSave.Active). Never touches the sim, Duel.cs, AIBrain or chase code. UNPROVEN IN UNITY. Checkpoints: MANIFEST.md.

[Serializable] public class SpWake { public float ahead = 14f, behind = 14f, hysteresis = 2f; }
[Serializable] public class SpGate { public string id = "", backId = ""; public float frontLocal = 27f, boundaryLocal = -4f, followStopBehind = 1.5f; public bool forceOpen, destSafe; }
[Serializable] public class SpTunnel { public string zone = ""; public string[] hideNames = new string[0]; }
[Serializable] public class SpBase { public string arenaRoot = "Arena"; public float laneZMin = 8f, laneZMax = 22f; public string[] retireGroups = new string[0]; }
[Serializable] public class SpRing { public string group = ""; public int rows; }
[Serializable] public class SpTerrain { public string mode = "crop"; public float xHalf = 40f, zFromLocal = -1.5f, zToLocal = 100f, yBias = -0.02f; }
[Serializable] public class SpZone
{
    public string id = "", root = "", layout = ""; public float offsetZ = 36f, laneZMin = 8f, laneZMax = 22f, colliderCenterLocal = 25f, colliderSizeZ = 70f; public bool safe, gateSet;
    public SpGate gate = new SpGate(); public SpTerrain terrain = new SpTerrain(); public SpRing[] rings = new SpRing[0]; public string[] hideRows = new string[0];
}
[Serializable] public class SpSky { public string arenaRoot = "Arena", group = "WorldFar"; public float follow = 1f, fromZ = 27f; }
[Serializable] public class SpKeep { public float monsterMargin = 4f; }
[Serializable] public class SpBuild { public string terrainName = "WorldTerrain", colliderName = "GroundCollider"; public string[] cloneNames = new string[0]; public bool cloneProbes = true; }
[Serializable] public class SpArrive { public bool enabled = true; public int holdFrames = 240, noSpanFrames = 900; public float rayUpM = 3f, rayDownM = 10f; public int groundMask = -1; }   // CAMPGROUND v1
[Serializable] public class SpTest { public float walkSpeed = 3f, startZ = 14f, turnZ = 46f, dwellSeconds = 1.5f, settleSeconds = 3f, baselineSeconds = 8f, worstFrameExtraMs = 12f, meanFactor = 1.15f, softHitchExtraMs = 6f, windowPadSeconds = 0.5f; public int maxSoftHitches = 2; }
[Serializable] public class SpanTable
{
    public bool enabled; public int version = 2; public float pollSeconds = 0.25f; public int ringsPerFrame = 1;
    public SpWake wake = new SpWake(); public SpTunnel tunnel = new SpTunnel(); public SpBase zone1 = new SpBase(); public SpZone[] zones = new SpZone[0];
    public SpSky skyline = new SpSky(); public SpKeep keep = new SpKeep(); public SpBuild build = new SpBuild(); public SpTest test = new SpTest(); public SpArrive arrive = new SpArrive();   // CAMPGROUND v1: arrive
    /// <summary>Offset of the first duplicate (the crossing test's route is zone 0 to zone 1).</summary>
    public float FirstOffset { get { return zones.Length > 0 ? zones[0].offsetZ : 0f; } }
    public float MaxOffset { get { float m = 0f; foreach (var z in zones) if (z != null && z.offsetZ > m) m = z.offsetZ; return m; } }
    public bool AnyForced { get { foreach (var z in zones) if (z != null && z.gate.forceOpen) return true; return false; } }
    public void ForceOpenAll() { foreach (var z in zones) if (z != null) z.gate.forceOpen = true; }
}

public static class SpanData
{
    static SpanTable t;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { t = null; }
    public static SpanTable Get()
    {
        if (t != null) return t; t = new SpanTable();
        string txt = HudCfg.ReadData("zone_span.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<SpanTable>(txt); if (k != null) t = k; } catch (Exception e) { Debug.LogWarning("[ZoneSpan] zone_span.json unreadable, span off: " + e.Message); t = new SpanTable(); } }
        Fix(t); return t;
    }
    public static void Reload() { t = null; }
    static void Fix(SpanTable k)
    {
        if (k.wake == null) k.wake = new SpWake(); if (k.tunnel == null) k.tunnel = new SpTunnel(); if (k.zone1 == null) k.zone1 = new SpBase(); if (k.zones == null) k.zones = new SpZone[0];
        if (k.arrive == null) k.arrive = new SpArrive(); if (k.skyline == null) k.skyline = new SpSky(); if (k.keep == null) k.keep = new SpKeep(); if (k.build == null) k.build = new SpBuild(); if (k.test == null) k.test = new SpTest();
        if (k.tunnel.hideNames == null) k.tunnel.hideNames = new string[0]; if (k.zone1.retireGroups == null) k.zone1.retireGroups = new string[0]; if (k.build.cloneNames == null) k.build.cloneNames = new string[0];
        foreach (var z in k.zones) if (z != null) { if (z.gate == null) z.gate = new SpGate(); if (z.terrain == null) z.terrain = new SpTerrain(); if (z.rings == null) z.rings = new SpRing[0]; if (z.hideRows == null) z.hideRows = new string[0]; }
        k.pollSeconds = Mathf.Max(0.05f, k.pollSeconds); k.ringsPerFrame = Mathf.Max(1, k.ringsPerFrame);
    }
}

[DefaultExecutionOrder(650)]   // after ArenaLimits (600): the soft wall and the follower stop are applied last
public sealed class ZoneSpan : MonoBehaviour
{
    public static ZoneSpan Inst;
    /// <summary>Raised on every switch ("wake&lt;n&gt;" with n = zone number from 1, "on:&lt;group&gt;", "off:&lt;group&gt;", "enter:&lt;zone&gt;", "tunnel:open"), for the crossing test marker column.</summary>
    public static event Action<string> OnEvent;
    /// <summary>ZoneRuntime skips the flash for the gates the span walks through. False until the span is bound (so a missing build falls back to the flash).</summary>
    public static bool Handles(string gateId)
    {
        if (Inst == null || !Inst.live || gateId.Length == 0) return false;
        foreach (var z in Inst.K.zones) if (z != null && (z.gate.id == gateId || z.gate.backId == gateId)) return true;
        return false;
    }
    public static bool Live { get { return Inst != null && Inst.live; } }
    public static SpanChain Core { get { return Inst != null ? Inst.core : null; } }

    // per-zone runtime state, index = zone number (0 = the loaded scene)
    sealed class RZ
    {
        public SpZone row; public string zoneId = "", spot = "", backSpot = ""; public float lo, hi, boundary, front; public bool safe, wasWant; public readonly List<Transform> units = new List<Transform>();
    }

    SpanTable K; SpanChain core; bool live, needBind = true, warned; int bindTries; float nextPoll;
    float wallMinZ = float.NaN; CharacterController pcc;
    DuelFighter player; Transform arena, skyline; Vector3 skyBase; bool tunnelHidden; int tunnelZone = 1;
    RZ[] rz = new RZ[0]; bool[] open = new bool[0], openEff = new bool[0], keep = new bool[0];
    readonly List<GameObject> tunnelObjs = new List<GameObject>(); readonly List<DuelFighter> chasers = new List<DuelFighter>();
    int lastZone;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; OnEvent = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return; var k = SpanData.Get(); if (!ZoneFlag.Allowed(k.enabled) || k.zones.Length == 0 || !ZoneSave.Active) return;
        var go = new GameObject("ZoneSpan"); DontDestroyOnLoad(go); Inst = go.AddComponent<ZoneSpan>();
    }
    void Awake() { K = SpanData.Get(); }
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { live = false; needBind = true; bindTries = 0; tunnelHidden = false; } }

    void Fire(string e) { var h = OnEvent; if (h != null) h(e); }

    // ------------------------------------------------------------------ bind to a freshly loaded duel scene (never throws, never spams)
    void Bind()
    {
        bindTries++; if (bindTries > 240) { needBind = false; if (!warned) { warned = true; Debug.LogWarning("[ZoneSpan] gave up binding (no Arena / zone roots / player); the flash crossing stays in charge"); } return; }
        player = null; foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { player = f; pcc = f.GetComponent<CharacterController>(); break; }
        var ag = GameObject.Find(K.zone1.arenaRoot); if (player == null || ag == null) return;
        int n = K.zones.Length + 1; var r = new RZ[n]; r[0] = new RZ(); arena = ag.transform;
        foreach (var nm in K.zone1.retireGroups) { var c = arena.Find(nm); if (c != null) r[0].units.Add(c); }
        r[0].lo = K.zone1.laneZMin; r[0].hi = K.zone1.laneZMax;
        for (int i = 1; i < n; i++)
        {
            var row = K.zones[i - 1]; var q = new RZ { row = row, safe = row.gate.destSafe }; r[i] = q; float prevOff = i >= 2 ? K.zones[i - 2].offsetZ : 0f;
            q.lo = row.laneZMin + row.offsetZ; q.hi = row.laneZMax + row.offsetZ; q.boundary = row.offsetZ + row.gate.boundaryLocal; q.front = prevOff + row.gate.frontLocal;
            ZGate g1 = null, g2 = null; ZZone zo1 = null;
            foreach (var z in ZoneData.Get().zones) if (z != null) foreach (var g in z.gates) if (g != null) { if (g.id == row.gate.id) { g1 = g; zo1 = z; } if (g.id == row.gate.backId) g2 = g; }
            if (g1 == null || zo1 == null) { needBind = false; Debug.LogWarning("[ZoneSpan] gate '" + row.gate.id + "' (zone row " + row.id + ") is not in zones.json; the flash crossing stays in charge"); return; }
            q.zoneId = g1.toZone; q.spot = g1.toSpot; q.backSpot = g2 != null ? g2.toSpot : ""; if (i == 1) r[0].zoneId = zo1.id;
            var zr = GameObject.Find(row.root); if (zr == null) return;   // scene objects appear a frame late: try again
            int found = 0;
            foreach (var rg in row.rings) { var c = zr.transform.Find(rg.group); if (c == null) continue; q.units.Add(c); found++; if (rg.rows > 0 && c.childCount > 0 && c.childCount != rg.rows) Debug.LogWarning("[ZoneSpan] ring " + rg.group + " has " + c.childCount + " rows, zone_span.json says " + rg.rows + " (rebuild the zone or regenerate)"); }
            if (found != row.rings.Length) { needBind = false; Debug.LogWarning("[ZoneSpan] zone '" + row.id + "' is not built (" + found + "/" + row.rings.Length + " ring groups): run Hades > Zones > Build zone 2 (duplicate). The flash crossing stays in charge"); return; }
        }
        skyline = string.IsNullOrEmpty(K.skyline.group) ? null : arena.Find(K.skyline.group); if (skyline != null) skyBase = skyline.position;
        var names = new HashSet<string>(K.tunnel.hideNames); var tun = new HashSet<string>(K.tunnel.hideNames); int hid = 0; tunnelObjs.Clear();
        foreach (var row in K.zones) foreach (var h in row.hideRows) names.Add(h);
        foreach (var tr in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (tr == null || !names.Contains(tr.name)) continue;
            if (tun.Contains(tr.name)) tunnelObjs.Add(tr.gameObject); else { tr.gameObject.SetActive(false); hid++; }
        }
        tunnelZone = 1; for (int i = 1; i < n; i++) if (r[i].row.id == K.tunnel.zone) tunnelZone = i;
        core = new SpanChain(n) { ahead = K.wake.ahead, behind = K.wake.behind, hyst = K.wake.hysteresis };
        for (int i = 0; i < n; i++) { core.min[i] = r[i].lo; core.max[i] = r[i].hi; core.boundary[i] = r[i].boundary; r[i].wasWant = i == 0; }
        rz = r; open = new bool[n]; openEff = new bool[n]; keep = new bool[n]; lastZone = 0; live = true; needBind = false; nextPoll = 0f;
        Debug.Log("[ZoneSpan] bound: " + n + " zones (" + (n - 1) + " built), " + hid + " zone 1 rows hidden, " + tunnelObjs.Count + " tunnel pieces"); ZoneHitch.Bound(this, skyline, WarmUnits());  /* ZONEHITCH v1 */
    }

    /// <summary>CAMPGROUND v1: switch the FIRST ring (the one that carries the GroundCollider) of every wanted built zone on at once, ahead of Pace's one-ring-per-frame. Called by ZoneGround for an arrival that is not a walk.</summary>
    public void WakeNow()
    {
        if (!live || player == null) return; Poll();
        for (int i = 1; i < rz.Length; i++) { if (!core.want[i] || rz[i].units.Count == 0) continue; var u = rz[i].units[0]; if (u != null && !u.gameObject.activeSelf) { u.gameObject.SetActive(true); Fire("on:" + u.name); } }
    }

    // ZONEHITCH v1: the ring groups of the later zones that are switched off right now (the warm renders them once, then preps them for the staged wake)
    List<Transform> WarmUnits() { var l = new List<Transform>(); for (int i = 1; i < rz.Length; i++) foreach (var u in rz[i].units) if (u != null && !u.gameObject.activeSelf) l.Add(u); return l; }

    bool GateOpen(SpZone row)
    {
        if (row.gate.forceOpen) return true;
        foreach (var z in ZoneData.Get().zones) if (z != null) foreach (var g in z.gates) if (g != null && g.id == row.gate.id)
        {
            if (g.unlockKilledSpawn.Length == 0) return true;
            if (g.sticky) return ZoneSave.GateUnlocked(g.id);
            ZZone uz; var s = ZoneData.FindSpawn(g.unlockKilledSpawn, out uz); if (s == null) return false;
            return !ZoneCore.Look(ZoneSave.Rec(uz.id, s.id), ZoneCore.NowMs()).alive;
        }
        return false;
    }

    // ------------------------------------------------------------------ per frame
    void Update()
    {
        if (!ZoneRuntime.Owns) return;
        if (needBind) { long zb = ZoneHitch.Mark(); Bind(); ZoneHitch.Lap("span.bind", ref zb); return; }  /* ZONEHITCH v1 */
        if (!live || player == null) { if (live && player == null) { live = false; needBind = true; bindTries = 0; } return; }
        float now = Time.unscaledTime;
        long zh = ZoneHitch.Mark(); if (now >= nextPoll) { nextPoll = now + K.pollSeconds; Poll(); ZoneHitch.Lap("span.poll", ref zh); }  /* ZONEHITCH v1 */
        Pace(); ZoneHitch.Lap("span.pace", ref zh); Skyline(); ZoneHitch.Lap("span.skyline", ref zh);  /* ZONEHITCH v1 */
    }

    void Poll()
    {
        int n = rz.Length; float z = player.transform.position.z; var zr = ZoneRuntime.Inst; ZoneHitch.PlayerZ = z;  /* ZONEHITCH v1 */ float m = K.keep.monsterMargin;
        // laneCHASE rule, same as the flash gate: while a creature is engaged that cannot follow, the way through stays shut. NOT for a gate whose destination is safe (destSafe): there the followers stop at the door and the player may pass.
        bool blocked = zr != null && zr.SpanGateBlocked(); bool reach = true;
        open[0] = true; openEff[0] = true; keep[0] = false;
        for (int i = 1; i < n; i++) { open[i] = GateOpen(rz[i].row); openEff[i] = open[i] && !(blocked && !rz[i].safe && z < rz[i].front); keep[i] = false; }
        wallMinZ = float.NaN; int zn = core.Zone; if (blocked && zn >= 1 && !rz[zn].safe) wallMinZ = rz[zn].boundary;
        chasers.Clear();
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (f == player || f.Dead || f.opponent == null) continue;   // only a creature that is fighting (engaged) holds a zone open under it; a follower chasing through a doorway is one
            chasers.Add(f); float fz = f.transform.position.z; reach = true;
            for (int i = 0; i < n; i++) { if (i > 0) reach = reach && openEff[i]; if (reach && fz >= rz[i].lo - m && fz <= rz[i].hi + m) keep[i] = true; }
        }
        core.Step(z, openEff, keep);
        for (int i = 0; i < n; i++)
        {
            bool w = core.want[i];
            if (w && !rz[i].wasWant) { Fire("wake" + (i + 1)); if (zr != null) { long zw = ZoneHitch.Mark(); zr.SpanWarm(rz[i].zoneId); ZoneHitch.Lap("span.warm " + rz[i].zoneId, ref zw); } }  /* ZONEHITCH v1 */
            if (!w && rz[i].wasWant && zr != null) zr.SpanDespawn(rz[i].zoneId);
            rz[i].wasWant = w;
        }
        if (core.Zone != lastZone)
        {
            int prev = lastZone; lastZone = core.Zone; string id = rz[lastZone].zoneId; string sp = lastZone > prev ? rz[lastZone].spot : rz[prev].backSpot;
            if (zr != null) zr.SpanEnter(id, sp); Fire("enter:" + id);
        }
        bool tOpen = n > tunnelZone && openEff[tunnelZone];
        if (tOpen != tunnelHidden) { tunnelHidden = tOpen; foreach (var o in tunnelObjs) if (o != null) o.SetActive(!tOpen); Fire(tOpen ? "tunnel:open" : "tunnel:closed"); }
        float top = float.NaN; reach = true; for (int i = 1; i < n; i++) { reach = reach && openEff[i]; if (!reach) break; top = rz[i].hi; }
        if (!float.IsNaN(top)) ArenaLimits.ZoneZMax = Mathf.Max(top, float.IsNaN(ArenaLimits.ZoneZMax) ? 0f : ArenaLimits.ZoneZMax);   // both fighters may walk the whole reachable span
        else if (zr != null) zr.SpanRestoreLimits();
    }

    void LateUpdate()
    {
        if (!live || player == null) return;
        if (!float.IsNaN(wallMinZ) && pcc != null && pcc.enabled) { float z = player.transform.position.z; if (z < wallMinZ) pcc.Move(new Vector3(0f, 0f, wallMinZ - z)); }
        // destSafe gates: a follower stops at the door (the player may pass, nothing chases into the safe zone)
        for (int i = 1; i < rz.Length; i++)
        {
            if (!rz[i].safe) continue;
            foreach (var f in chasers)
            {
                if (f == null || f.Dead) continue; float fz = f.transform.position.z; float to = SpanChain.FollowerZ(fz, rz[i].front, rz[i].row.gate.followStopBehind, true);
                if (to == fz || fz > rz[i].hi + K.keep.monsterMargin) continue;
                var cc = f.GetComponent<CharacterController>(); if (cc != null && cc.enabled) cc.Move(new Vector3(0f, 0f, to - fz)); else f.transform.position += new Vector3(0f, 0f, to - fz);
            }
        }
    }

    /// <summary>Switch at most ringsPerFrame units on and as many off per frame (zone 0 first, nearest ring first), so no frame carries a whole zone.</summary>
    void Pace()   // ZONEHITCH v1: a ring group with rows left to switch is one unit of the per-frame budget; with spread.rowsPerFrame > 0 it is switched in row batches (ZoneHitch.Step), 0 = the whole ring at once as in v107
    {
        int on = 0, off = 0, per = K.ringsPerFrame; long zh = ZoneHitch.Mark();
        for (int i = 0; i < rz.Length; i++)
        {
            bool want = core.want[i];
            foreach (var u in rz[i].units)
            {
                if (u == null || !ZoneHitch.Pending(u, want)) continue;
                if (want) { if (on >= per) continue; on++; } else { if (off >= per) continue; off++; }
                bool was = u.gameObject.activeSelf; ZoneHitch.Step(u, want); ZoneHitch.Lap("ring " + (want ? "on " : "off ") + u.name, ref zh);
                if (u.gameObject.activeSelf != was) Fire((want ? "on:" : "off:") + u.name);
            }
        }
    }

    void Skyline()
    {
        if (skyline == null || K.skyline.follow <= 0f || !tunnelHidden) return;
        float off = SpanCore.SkylineOffset(player.transform.position.z, K.skyline.fromZ, K.MaxOffset, K.skyline.follow);
        var p = skyBase + new Vector3(0f, 0f, off); if ((skyline.position - p).sqrMagnitude > 1e-6f) skyline.position = p;
    }
}
