using System;
using System.Collections.Generic;
using UnityEngine;

// S6 THE ERLKING'S HUNT v1 (pool-laneS6, staged DARK, UNPROVEN in Unity). One new file. Data: surprise_hunt.json (Assets\Data + StreamingAssets). enabled=false and no file = nothing here runs.
// Flow: Joan lingers in the zone (timer seeded per visit, starts on entering, resets on leaving, paused while she fights or stands in the boss arena) -> SurpriseGate.TryClaim -> horn (CueBus, placeholder
// slice) -> leadS later the pack appears at a seeded horizon entry point on the zone edge. The riders are INJECTED as ZSpot + ZSpawn rows into the in-memory zones.json table of that zone: the existing
// ZoneRuntime then spawns / aggros / chases / kills / drops them (nothing new in the sim, DuelFighter, AIBrain or ZoneRuntime). A body that cannot spawn (NPC prefab missing or useNpcBodies=false) uses
// the data fallback creature. The ghost hound = an AmbientHorses herd row injected for the strip between the entry and Joan (decor, no attack). Once per run, one pack. Never in a harness run unless -s6:1.
[Serializable] public class ShArgs { public string enable = "-s6:", timer = "-s6t:"; }
[Serializable] public class ShTimer { public float secondsMin = 150f, secondsMax = 300f; public bool resetOnLeave = true; }
[Serializable] public class ShPause { public float engagedMaxM = 40f, pauseLocalZMin = 23f, pauseLocalZMax = 53f; public bool pauseWhileGroundHold = true; }
[Serializable] public class ShHorn { public string cue = ""; public float gain = 1f, rate = 1f, gapS = 1f, leadS = 3f; public int repeat = 1; }
[Serializable] public class ShPoint { public string id = "", edge = "far"; public float along; }
[Serializable] public class ShEntry { public float halfWidthX = 32f, insetM = 4f, minDistFromPlayerM = 34f; public ShPoint[] points = new ShPoint[0]; }
[Serializable] public class ShCreature { public string id = "", path = ""; }
[Serializable] public class ShRow { public float aggroM = 140f, leashExtraM = 30f, verifyS = 6f; public int respawnMinS = 3600, respawnMaxS = 3600, difficulty; }
[Serializable] public class ShMember
{
    public string id = "", kind = "rider", npcCreature = "", fallbackCreature = "", ladderRowNpc = "", ladderRowFallback = "", breed = "";
    public float lateralM, backM, speedMps, stripWidthM = 6f, stopShortM = 6f; public int count = 1;
}
[Serializable] public class ShCfg
{
    public bool enabled = false, useNpcBodies = false; public int version = 1, seedSalt = 1, maxPerRun = 1, seedFallback = 1; public string zone = "", gateKey = "";
    public float pollSeconds = 0.25f, walkOfRun = 0.4f;
    public ShArgs args = new ShArgs(); public ShTimer timer = new ShTimer(); public ShPause pause = new ShPause(); public ShHorn horn = new ShHorn(); public ShEntry entry = new ShEntry(); public ShRow spawnRow = new ShRow();
    public ShCreature[] creatures = new ShCreature[0]; public ShMember[] pack = new ShMember[0];
}

public sealed class SurpriseHunt : MonoBehaviour
{
    static ShCfg K; static SurpriseHunt Inst; static int runsDone;
    enum Phase { Idle, Armed, Horn, Pack, Done }
    class Member { public ShMember m; public string rowId, creature; public bool seen, warned; }

    Phase phase = Phase.Idle; bool inZone, forced; float nextPoll, timerLeft, forcedTimer = -1f, hornAt, spawnAt, packAt, lastTick; int visit, hornsPlayed; float nextHornAt;
    SpZone zr; readonly List<Member> pack = new List<Member>(); AhHerd houndHerd; System.Random rnd; Vector3 entryPos;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; K = null; runsDone = 0; }
    public static ShCfg Get()
    {
        if (K != null) return K; K = new ShCfg(); string txt = DataPath.ReadText("surprise_hunt.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<ShCfg>(txt); if (k != null) K = k; } catch (Exception e) { Debug.LogWarning("[S6] surprise_hunt.json unreadable, off: " + e.Message); K = new ShCfg(); } }
        if (K.args == null) K.args = new ShArgs(); if (K.timer == null) K.timer = new ShTimer(); if (K.pause == null) K.pause = new ShPause(); if (K.horn == null) K.horn = new ShHorn(); if (K.entry == null) K.entry = new ShEntry();
        if (K.spawnRow == null) K.spawnRow = new ShRow(); if (K.creatures == null) K.creatures = new ShCreature[0]; if (K.pack == null) K.pack = new ShMember[0]; if (K.entry.points == null) K.entry.points = new ShPoint[0];
        return K;
    }
    static string Arg(string prefix) { if (string.IsNullOrEmpty(prefix)) return null; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return; var k = Get(); string ea = Arg(k.args.enable); bool force = ea == "1", off = ea == "0";
        if (off || (!k.enabled && !force) || k.pack.Length == 0) return;
#if FRANKEN_NO_HARNESS
        force = false; if (!k.enabled) return;   // shipping build: the proof args do nothing
#else
        if (PerfFlags.HarnessRun() && !force) return; if (force) SurpriseGate.ForceEnable(true);   // -s6:1 proof: director file may stay dark
#endif
        var go = new GameObject("SurpriseHunt"); DontDestroyOnLoad(go); Inst = go.AddComponent<SurpriseHunt>(); Inst.forced = force;
#if !FRANKEN_NO_HARNESS
        string ts = Arg(k.args.timer); float tv; if (ts != null && float.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tv) && tv > 0f) Inst.forcedTimer = tv;
#endif
        Debug.Log("[S6] boot enabled=" + k.enabled + " forced=" + force + " timerOverride=" + Inst.forcedTimer + " useNpcBodies=" + k.useNpcBodies);
    }
    void OnDestroy() { if (Inst == this) Inst = null; }

    int RunSeed()
    {
        return (int)(SurpriseGate.Seed(K.gateKey) ^ (uint)K.seedSalt);   // S1 gate: per-run stream (seed 0) or fixed (seed set), one stream per surprise key
    }

    void Update()
    {
        float now = Time.unscaledTime; if (now < nextPoll) return; nextPoll = now + K.pollSeconds; float dt = lastTick > 0f ? now - lastTick : 0f; lastTick = now;
        var p = GameLoop.Player; if (p == null) return;
        if (zr == null) { foreach (var z in SpanData.Get().zones) if (z != null && z.id == K.zone) zr = z; if (zr == null) return; }
        float z0 = zr.offsetZ + zr.laneZMin, z1 = zr.offsetZ + zr.laneZMax; Vector3 pp = p.transform.position;
        bool now_in = pp.z >= z0 && pp.z <= z1 && ZoneRuntime.Owns;
        if (now_in && !inZone) EnterZone(); else if (!now_in && inZone) LeaveZone();
        inZone = now_in;
        if (!inZone) return;
        if (phase == Phase.Armed)
        {
            if (!Paused(p, pp)) timerLeft -= dt;
            if (timerLeft <= 0f) Trigger(p);
        }
        else if (phase == Phase.Horn)
        {
            if (hornsPlayed < K.horn.repeat && now >= nextHornAt) { PlayHorn(); }
            if (now >= spawnAt) SpawnPack(p);
        }
        else if (phase == Phase.Pack) Watch(p, now);
    }

    bool Paused(DuelFighter p, Vector3 pp)
    {
        if (K.pause.pauseWhileGroundHold && ZoneGroundHold.Active) return true;
        float lz = pp.z - zr.offsetZ; if (lz >= K.pause.pauseLocalZMin && lz <= K.pause.pauseLocalZMax) return true;
        var o = p.opponent; return o != null && !o.Dead && Vector3.Distance(pp, o.transform.position) <= K.pause.engagedMaxM;
    }

    void EnterZone()
    {
        visit++; SurpriseGate.Reset(K.zone); if (runsDone >= K.maxPerRun) { phase = Phase.Done; return; }
        if (phase == Phase.Pack || phase == Phase.Horn || phase == Phase.Done) return;
        rnd = new System.Random(RunSeed() + visit * 7919);   // hc:ok visit stride, any odd prime; the seed itself is data
        float t = forcedTimer > 0f ? forcedTimer : K.timer.secondsMin + (float)rnd.NextDouble() * Mathf.Max(0f, K.timer.secondsMax - K.timer.secondsMin);
        timerLeft = t; phase = Phase.Armed; Debug.Log("[S6] timer armed " + t.ToString("F1") + "s (visit " + visit + ")");
    }
    void LeaveZone()
    {
        if (phase == Phase.Armed && K.timer.resetOnLeave) { phase = Phase.Idle; Debug.Log("[S6] left the zone, timer reset"); }
        if (phase == Phase.Pack) EndPack("left the zone");
    }

    bool Claim() { return SurpriseGate.TryClaim(K.zone, K.gateKey); }
    void Trigger(DuelFighter p)
    {
        if (!Claim()) { phase = Phase.Done; Debug.Log("[S6] gate refused (director off or another surprise this visit)"); return; }
        runsDone++; phase = Phase.Horn; hornsPlayed = 0; nextHornAt = Time.unscaledTime; spawnAt = Time.unscaledTime + K.horn.leadS; Debug.Log("[S6] TRIGGER: horn, pack in " + K.horn.leadS.ToString("F1") + "s");
        PlayHorn();
    }
    void PlayHorn()
    {
        hornsPlayed++; nextHornAt = Time.unscaledTime + K.horn.gapS; var cb = FindFirstObjectByType<CueBus>();
        if (cb != null) cb.Play(K.horn.cue, K.horn.gain, 0f, K.horn.rate); else Debug.Log("[S6] horn: no CueBus (silent)");
        Debug.Log("[S6] horn " + hornsPlayed + "/" + K.horn.repeat + " cue=" + K.horn.cue);
    }

    Vector3 EntryPoint(Vector3 pp)
    {
        float zl = zr.offsetZ + zr.laneZMin + K.entry.insetM, zh = zr.offsetZ + zr.laneZMax - K.entry.insetM, hx = K.entry.halfWidthX - K.entry.insetM;
        var cand = new List<Vector3>(); var ids = new List<string>();
        foreach (var e in K.entry.points)
        {
            if (e == null) continue; float a = Mathf.Clamp(e.along, -1f, 1f), x, z;
            switch (e.edge) { case "far": z = zh; x = a * hx; break; case "near": z = zl; x = a * hx; break; case "west": x = -hx; z = Mathf.Lerp(zl, zh, (a + 1f) * 0.5f); break; default: x = hx; z = Mathf.Lerp(zl, zh, (a + 1f) * 0.5f); break; }
            cand.Add(new Vector3(x, pp.y, z)); ids.Add(e.id);
        }
        if (cand.Count == 0) return new Vector3(0f, pp.y, zr.offsetZ + zr.laneZMax);
        var ok = new List<int>(); int far = 0; float fd = -1f;
        for (int i = 0; i < cand.Count; i++) { float d = Flat(cand[i], pp); if (d >= K.entry.minDistFromPlayerM) ok.Add(i); if (d > fd) { fd = d; far = i; } }
        int pick = ok.Count > 0 ? ok[rnd.Next(ok.Count)] : far; Debug.Log("[S6] entry " + ids[pick] + " dist " + Flat(cand[pick], pp).ToString("F1") + " (" + ok.Count + " of " + cand.Count + " far enough)"); return cand[pick];
    }
    static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

    void SpawnPack(DuelFighter p)
    {
        phase = Phase.Pack; packAt = Time.unscaledTime; Vector3 pp = p.transform.position; entryPos = EntryPoint(pp);
        var zone = ZoneData.Zone(K.zone); if (zone == null) { Debug.LogWarning("[S6] zone " + K.zone + " not in zones.json: no pack"); phase = Phase.Done; return; }
        Vector3 dir = pp - entryPos; dir.y = 0f; if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward; dir.Normalize(); Vector3 side = new Vector3(dir.z, 0f, -dir.x); float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        var spots = new List<ZSpot>(zone.spots); var spawns = new List<ZSpawn>(zone.spawns); var table = ZoneData.Get(); pack.Clear();
        foreach (var m in K.pack)
        {
            if (m == null) continue; Vector3 at = entryPos + side * m.lateralM - dir * m.backM;
            if (m.kind == "ambient") { AddHound(m, entryPos, pp, dir, side); continue; }
            string cid = Creature(m, table); if (string.IsNullOrEmpty(cid)) { Debug.LogWarning("[S6] " + m.id + ": no creature available, skipped"); continue; }
            bool npc = cid == m.npcCreature; string lad = npc ? m.ladderRowNpc : m.ladderRowFallback; string rid = "hunt_" + m.id;
            spots.Add(new ZSpot { id = rid, pos = new[] { at.x, at.z }, yaw = yaw, note = "S6 hunt entry" });
            var row = new ZSpawn { id = rid, spot = rid, enabled = true, ladderRow = lad, picks = new[] { new ZPick { creature = cid, weight = 1 } }, respawnMinS = K.spawnRow.respawnMinS, respawnMaxS = K.spawnRow.respawnMaxS, difficulty = K.spawnRow.difficulty, aggro = K.spawnRow.aggroM, leash = K.spawnRow.aggroM + K.spawnRow.leashExtraM };
            spawns.RemoveAll(s => s != null && s.id == rid); spawns.Add(row); pack.Add(new Member { m = m, rowId = rid, creature = cid });
            Debug.Log("[S6] pack row " + rid + " creature " + cid + " ladder " + lad + " at " + at.ToString("F1") + " yaw " + yaw.ToString("F0"));
        }
        spots.RemoveAll(s => s == null); zone.spots = spots.ToArray(); zone.spawns = spawns.ToArray();   // ZoneRuntime reads zone.spots / zone.spawns each poll: the rows are picked up on its next look
    }

    string Creature(ShMember m, ZoneTable table)
    {
        string want = K.useNpcBodies ? m.npcCreature : "";
        if (!string.IsNullOrEmpty(want))
        {
            if (ZoneData.Creature(want) == null) { foreach (var c in K.creatures) if (c != null && c.id == want) { var l = new List<ZCreature>(table.creatures); l.Add(new ZCreature { id = c.id, source = "resources", path = c.path }); table.creatures = l.ToArray(); } }
            var zc = ZoneData.Creature(want); if (zc != null && zc.path.Length > 0 && Resources.Load<GameObject>(zc.path) != null) return want;
            Debug.Log("[S6] " + m.id + ": NPC body " + want + " not loadable, using " + m.fallbackCreature);
        }
        return ZoneData.Creature(m.fallbackCreature) != null ? m.fallbackCreature : "";
    }

    void AddHound(ShMember m, Vector3 e, Vector3 pp, Vector3 dir, Vector3 side)
    {
        var ah = AmbientHorses.Get(); if (!ah.enabled) { Debug.Log("[S6] hound skipped: ambient_horses.json is off"); return; }
        Vector3 a = e - dir * m.backM, b = pp - dir * m.stopShortM; float hw = m.stripWidthM * 0.5f;
        var area = new AhRect { xMin = Mathf.Min(a.x, b.x) - hw, xMax = Mathf.Max(a.x, b.x) + hw, zMin = Mathf.Min(a.z, b.z) - hw, zMax = Mathf.Max(a.z, b.z) + hw };
        houndHerd = new AhHerd { id = "hunt_hound", enabled = true, area = area, breeds = new[] { m.breed }, count = m.count, seed = RunSeed() };
        var l = new List<AhHerd>(ah.herds); l.RemoveAll(h => h != null && h.id == houndHerd.id); l.Add(houndHerd); ah.herds = l.ToArray(); Debug.Log("[S6] hound herd " + m.breed + " x" + m.count + " box x " + area.xMin.ToString("F0") + ".." + area.xMax.ToString("F0") + " z " + area.zMin.ToString("F0") + ".." + area.zMax.ToString("F0"));
    }

    void Watch(DuelFighter p, float now)
    {
        int live = 0, dead = 0, want = 0;
        foreach (var mb in pack)
        {
            want++; var df = Find(mb.rowId);
            if (df == null) { if (!mb.seen && !mb.warned && now - packAt > K.spawnRow.verifyS) { mb.warned = true; Debug.LogWarning("[S6] " + mb.rowId + " did not spawn within " + K.spawnRow.verifyS + "s (ZoneRuntime refused: prefab missing or not the current zone)"); } if (mb.seen) dead++; continue; }
            if (!mb.seen)
            {
                mb.seen = true; DisableRow(mb.rowId);
                if (mb.m.speedMps > 0f) { df.runSpeed = mb.m.speedMps; df.walkSpeed = mb.m.speedMps * K.walkOfRun; }
                Debug.Log("[S6] " + mb.rowId + " live (" + df.name + ") dist " + Flat(df.transform.position, p.transform.position).ToString("F1") + " run " + df.runSpeed.ToString("F1"));
            }
            if (df.Dead) dead++; else live++;
        }
        if (want > 0 && dead >= want) EndPack("pack dead");
    }
    static DuelFighter Find(string rowId)
    {
        string suffix = "#" + rowId; foreach (var d in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (d != null && d.name.EndsWith(suffix, StringComparison.Ordinal)) return d; return null;
    }
    void DisableRow(string rowId) { var z = ZoneData.Zone(K.zone); if (z != null) foreach (var s in z.spawns) if (s != null && s.id == rowId) s.enabled = false; }   // the body exists: no respawn this run
    void EndPack(string why)
    {
        foreach (var mb in pack) DisableRow(mb.rowId); if (houndHerd != null) houndHerd.enabled = false;
        phase = Phase.Done; Debug.Log("[S6] pack over: " + why);
    }
}
