using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// BANDITS v1 (bandits-dev kit, STAGED DARK, UNPROVEN IN UNITY). NEW FILE, no existing file is edited. Everything numeric is Assets/Data/bandits.json (copy in StreamingAssets, read first).
// A missing file / "enabled": false = nothing here ever runs (no director, no component): the game is exactly as before.
// Three jobs, all behind the one master key:
//   1. ARCHER: a DuelFighter whose roster id is a types[] row with role "archer" gets this component. While its AIBrain is awake (= engaged by ZoneRuntime) it shoots with the PUBLIC BowShot.Launch on its
//      own cooldown (ranged.cooldownS), a data missChance (seeded hash, no UnityEngine.Random in the sim path), kites away below ranged.kiteBelowM, and leaves the melee brain alone otherwise.
//   2. BowShot.Step: the Director calls it itself once per frame (BowHooks.SimTick returns early while bow.json master is off). A second call from BowHooks is harmless: a landed arrow is removed.
//   3. AFTER-BOSS GATE: groups with needsGate keep the (small, capped) aggro that zones.json holds while the zone boss lives; once no live boss of that zone exists the spawn rows get the full type aggro.
//      Also hpAssert: FightBalance.ApplyNew can overwrite maxHealth after ZoneRuntime.ApplyRow multiplied it by the ladder hpMul, so a bandit's maxHealth is set once to
//      fight_balance maxHealth x the spawn row's hpMul (fresh fighters only).
// Execution order is AFTER AIBrain (DefaultExecutionOrder 100): the override of moveDir wins for the frame. The exact ordering against ChaseAgent / FoeSep / Duel.SimBrains is NOT proven: prove it in Unity (first test).
[DefaultExecutionOrder(100)]
public sealed class BanditArcher : MonoBehaviour
{
    [Serializable] public class Ranged
    {
        public float minM = 5f, maxM = 15f, cooldownS = 2.6f, windupS = 0.7f, missChance = 0.15f, kiteBelowM = 4f, arrowDamageMul = 1f, losHeightM = 1.3f, turnDegPerS = 360f;
        public int speedCmS = 700; public bool needLos = true; public string moveId = "thrust", tint = "#e8d9a8";
    }
    [Serializable] public class TypeRow { public string id = "", role = ""; public bool off = false; public float aggro = 7f, leashExtra = 12f; public Ranged ranged = new Ranged(); }
    [Serializable] public class MemberRow { public string type = ""; }
    [Serializable] public class GroupRow { public string id = "", zone = ""; public bool needsGate = false, skip = false; public MemberRow[] members = new MemberRow[0]; }
    [Serializable] public class BossRow { public string spawn = "", creature = ""; }
    [Serializable] public class ZoneRow { public string id = ""; public BossRow boss = new BossRow(); }
    [Serializable] public class GateCfg { public bool enabled = true; public float pollS = 0.5f; }
    [Serializable] public class RunCfg { public float pollS = 0.5f; public bool hpAssert = true; public int hpAssertAfterFrames = 90, tickHz = 60; }
    [Serializable] public class Cfg
    {
        public bool enabled = false; public string spawnIdFormat = "bandit_{group}_{n}";
        public TypeRow[] types = new TypeRow[0]; public GroupRow[] groups = new GroupRow[0]; public ZoneRow[] zones = new ZoneRow[0];
        public GateCfg afterGate = new GateCfg(); public RunCfg runtime = new RunCfg();
    }

    const string FileName = "bandits.json", DataRel = "Data/bandits.json";   // file names, same pair as BossSilver
    const float Eps = 0.0001f, HpEps = 0.01f, HpTol = 0.5f, MinPollS = 0.1f; const uint MissGrain = 10000u; const int PruneAbove = 64;
    static Cfg c;
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, FileName); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, DataRel);
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[Bandits] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.types == null) c.types = new TypeRow[0]; if (c.groups == null) c.groups = new GroupRow[0]; if (c.zones == null) c.zones = new ZoneRow[0];
        if (c.afterGate == null) c.afterGate = new GateCfg(); if (c.runtime == null) c.runtime = new RunCfg();
        return c;
    }
    public static TypeRow TypeOf(string id) { var k = Get(); if (string.IsNullOrEmpty(id)) return null; foreach (var t in k.types) if (t != null && !t.off && t.id == id) return t; return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Get().enabled) return;   // master key off: no director, nothing attached, nothing logged
        if (FindFirstObjectByType<Director>() != null) return;
        var go = new GameObject("BanditDirector"); go.hideFlags = HideFlags.HideInHierarchy; DontDestroyOnLoad(go); go.AddComponent<Director>();
        Debug.Log("[Bandits] on: " + Get().types.Length + " types, " + Get().groups.Length + " groups");
    }

    // ------------------------------------------------------------------ the archer
    DuelFighter df; AIBrain brain; TypeRow type; int lastTick = -1, windupEnd = -1, cooldownUntil, shots; bool kiting;

    void Awake() { df = GetComponent<DuelFighter>(); brain = GetComponent<AIBrain>(); type = df != null ? TypeOf(df.CharacterId) : null; }

    void Update()
    {
        if (!Get().enabled || df == null || type == null || type.role != "archer") return;
        if (brain == null) brain = GetComponent<AIBrain>();
        if (df.Dead || brain == null || !brain.enabled) { windupEnd = -1; return; }   // dormant (not engaged yet) or dead
        var foe = df.opponent; if (foe == null || foe.Dead || Duel.FrozenFor(df)) { windupEnd = -1; return; }
        int tick = Duel.TickCount; if (tick == lastTick) return; lastTick = tick;   // one decision per sim tick
        var R = type.ranged; var d = foe.transform.position - transform.position; d.y = 0f; float gap = d.magnitude; var dir = gap > Eps ? d / gap : transform.forward;
        if (df.state != FState.Loco) { windupEnd = -1; return; }   // mid swing / stagger / roll: the normal machine owns it

        if (windupEnd >= 0)   // aiming: stand still, turn to the target, loose on time
        {
            df.moveDir = Vector3.zero; Face(dir, R);
            if (gap < R.kiteBelowM) { windupEnd = -1; return; }   // he got in my face: abort, kite
            if (tick >= windupEnd) Loose(foe, R);
            return;
        }
        if (gap < R.kiteBelowM) kiting = true; else if (gap >= R.minM) kiting = false;
        if (kiting) { df.moveDir = -dir; df.run = true; return; }   // back away until there is room to shoot again (AIBrain's own move is overridden for this tick)
        if (gap >= R.minM && gap <= R.maxM && tick >= cooldownUntil && (!R.needLos || LineOfSight(foe, R))) windupEnd = tick + Mathf.Max(1, Mathf.RoundToInt(R.windupS * Get().runtime.tickHz));
    }

    void Face(Vector3 dir, Ranged R)
    {
        if (dir.sqrMagnitude < Eps) return;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir, Vector3.up), R.turnDegPerS * Time.deltaTime);
    }

    bool LineOfSight(DuelFighter foe, Ranged R)
    {
        var a = transform.position + Vector3.up * (R.losHeightM * df.scale); var b = foe.transform.position + Vector3.up * (R.losHeightM * foe.scale);
        RaycastHit h; if (!Physics.Linecast(a, b, out h, ~0, QueryTriggerInteraction.Ignore)) return true;
        var r = h.collider != null ? h.collider.transform.root : null; return r != null && (r == transform.root || r == foe.transform.root);   // only a fighter in the line is fine
    }

    void Loose(DuelFighter foe, Ranged R)
    {
        windupEnd = -1; cooldownUntil = Duel.TickCount + Mathf.Max(1, Mathf.RoundToInt(R.cooldownS * Get().runtime.tickHz)); shots++;
        var mv = df.moves != null ? df.moves.Get(R.moveId) : null; if (mv == null) { Debug.LogWarning("[Bandits] " + name + ": move '" + R.moveId + "' missing, no shot"); return; }
        uint hsh = ZoneCore.Hash(name, "bandit_shot", shots, "miss"); bool miss = (hsh % MissGrain) < (uint)Mathf.RoundToInt(R.missChance * MissGrain);   // seeded: same run = same misses
        df.Press(R.moveId);   // plays the thrust state as the shooting pose; the foe is out of its reach, so it cannot melee-hit
        if (!miss) BowShot.Launch(df, foe, mv, new[] { 0 }, false, R.arrowDamageMul, R.speedCmS, 0, R.tint);
        Debug.Log("[Bandits] " + name + (miss ? " shot MISSED (missChance)" : " loosed") + " tick " + Duel.TickCount);
    }

    // ------------------------------------------------------------------ the director (one per game, only while the master key is on)
    public sealed class Director : MonoBehaviour
    {
        readonly Dictionary<DuelFighter, int> firstSeen = new Dictionary<DuelFighter, int>();
        readonly HashSet<DuelFighter> asserted = new HashSet<DuelFighter>();
        readonly Dictionary<string, float[]> orig = new Dictionary<string, float[]>();   // spawn id -> {aggro, leash} as zones.json holds them
        float nextPoll; int lastStep = -1;

        void Update()
        {
            var k = Get(); if (!k.enabled) return;
            int tick = Duel.TickCount; if (tick != lastStep) { lastStep = tick; BowShot.Step(tick); }   // own BowShot.Step: arrows land with bow.json master off
            if (Time.unscaledTime < nextPoll) return; nextPoll = Time.unscaledTime + Mathf.Max(MinPollS, Mathf.Min(k.runtime.pollS, k.afterGate.pollS));
            var all = FindObjectsByType<DuelFighter>(FindObjectsSortMode.None);
            foreach (var f in all)
            {
                if (f == null) continue; var t = TypeOf(f.CharacterId); if (t == null) continue;
                if (t.role == "archer" && f.GetComponent<BanditArcher>() == null) f.gameObject.AddComponent<BanditArcher>();
                if (k.runtime.hpAssert) HpAssert(f);
            }
            if (k.afterGate.enabled) Gate(k, all);
            if (firstSeen.Count > PruneAbove) { var dead = new List<DuelFighter>(); foreach (var kv in firstSeen) if (kv.Key == null) dead.Add(kv.Key); foreach (var x in dead) { firstSeen.Remove(x); asserted.Remove(x); } }
        }

        void HpAssert(DuelFighter f)
        {
            if (asserted.Contains(f) || f.Dead) return;
            int fr; if (!firstSeen.TryGetValue(f, out fr)) { firstSeen[f] = Time.frameCount; return; }
            if (Time.frameCount - fr < Get().runtime.hpAssertAfterFrames) return;
            asserted.Add(f);
            int hash = f.name.IndexOf('#'); if (hash < 0) return; string sid = f.name.Substring(hash + 1); int at = sid.IndexOf('@'); if (at >= 0) sid = sid.Substring(0, at);
            ZZone z; var row = ZoneData.FindSpawn(sid, out z); var fb = FightBalance.Active ? FightBalance.For(f) : null; if (row == null || fb == null || fb.maxHealth <= 0f) return;
            if (f.health < f.maxHealth - HpEps) return;   // already hurt: leave it
            float want = fb.maxHealth * row.hpMul; if (Mathf.Abs(want - f.maxHealth) < HpTol) return;
            f.maxHealth = want; f.health = want; var hp = f.GetComponent<Health>(); if (hp != null) { hp.max = want; hp.current = want; }
            Debug.Log("[Bandits] hpAssert " + f.name + " maxHealth " + want.ToString("F0"));
        }

        void Gate(Cfg k, DuelFighter[] all)
        {
            foreach (var g in k.groups)
            {
                if (g == null || g.skip || !g.needsGate) continue;
                string bossId = null; foreach (var zr in k.zones) if (zr != null && zr.id == g.zone && zr.boss != null) bossId = zr.boss.creature;
                if (string.IsNullOrEmpty(bossId)) continue;
                bool bossAlive = false; foreach (var f in all) if (f != null && !f.Dead && f.CharacterId == bossId) { bossAlive = true; break; }
                var zone = ZoneData.Zone(g.zone); if (zone == null) continue;
                for (int i = 0; i < g.members.Length; i++)
                {
                    string sid = k.spawnIdFormat.Replace("{group}", g.id).Replace("{n}", (i + 1).ToString());
                    var row = ZoneData.Spawn(zone, sid); var t = TypeOf(g.members[i].type); if (row == null || t == null) continue;
                    float[] o; if (!orig.TryGetValue(sid, out o)) { o = new[] { row.aggro, row.leash }; orig[sid] = o; }
                    float aggro = bossAlive ? o[0] : Mathf.Max(o[0], t.aggro);
                    if (Mathf.Abs(row.aggro - aggro) > Eps) { row.leash = o[1] + (aggro - o[0]); row.aggro = aggro; Debug.Log("[Bandits] gate " + sid + (bossAlive ? " held (boss alive) aggro " : " open (boss gone) aggro ") + aggro.ToString("F1")); }
                }
            }
        }
    }
}
