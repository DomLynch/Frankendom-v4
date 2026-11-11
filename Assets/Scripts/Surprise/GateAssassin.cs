using System;
using System.IO;
using UnityEngine;

// S2 THE GATE ASSASSIN (pool-laneS2, STAGED DARK, UNPROVEN IN UNITY). NEW FILE Assets/Scripts/Surprise/GateAssassin.cs, with ZoneRuntime.Surprise.cs (ZoneRuntime made partial) and SurpriseGate.cs.
// Everything numeric / named is Assets/Data/gate_assassin.json (StreamingAssets copy read first). Missing file or "enabled": false = no component is ever created: the game is exactly as before.
// Flow: Joan arrives through a gate row of gates[] (a zone change whose arrive spot matches). Pass 1 of the run is never hit (minPassIndex). On a later pass a seeded roll (ZoneCore.Hash of zone / pass / run seed) decides.
// When Joan has walked trigger.minWalkM from the gate spot (and the doorway side is off screen) the shared SurpriseGate is claimed, the boot-scrape tell plays (CueBus, existing cue), tell.leadS later the existing
// BanditAssassin is cloned in the doorway BEHIND her (gate -> Joan line) and engaged (the normal AIBrain does the fighting). A parry of its first blow ends in the existing finisher kill-cam:
// RTFinisher.TryBegin(Joan, assassin, move, deathblow) with the victim's health at zero, the same call Duel.Resolve makes on a lethal blow.
// Harness (compiled out under FRANKEN_NO_HARNESS): -s2:1 forces it at harness.force.zone / arriveSpot on the FIRST observation (no pass rule, chance harness.chance) and simulates the parry on the assassin's first attack
// (a proof of the kill-cam path, NOT of the parry detection); -s2:0 hard off.
public sealed class GateAssassin : MonoBehaviour
{
    [Serializable] public class SeedRow { public string salt = "", directorFile = "", directorDataRel = ""; public bool useDirectorSeed = true; }
    [Serializable] public class GateRow { public string zone = "", arriveSpot = "", gateSpot = ""; public bool enabled = true; }
    [Serializable] public class CapRow { public int perRun = 1; public bool persistAcrossLaunches = false; }
    [Serializable] public class TriggerRow { public float minWalkM = 7f, maxWalkM = 30f, timeoutS = 25f, pollS = 0.2f, viewMargin = 0.1f; public bool waitOffscreen = true, skipWhileEngaged = true, skipWhileFinisher = true, skipWhileDown = true; }
    [Serializable] public class TellRow { public string cue = ""; public float gain = 1f, rate = 1f, repeatGapS = 0.1f, leadS = 0.5f; public int repeat = 1; }
    [Serializable] public class SpawnRow { public float behindM = 6f, minBehindM = 3.5f, insideM = 1.5f, groundRayUpM = 2f, groundRayDownM = 8f; }
    [Serializable] public class AssassinRow { public string creature = "", rowId = ""; public bool copyRowFromCreature = true; public float hpMul = -1f, damageMul = -1f, aggro = -1f, leash = 60f; public int difficulty = -1; }
    [Serializable] public class ParryRow { public bool finisher = true, forceEnable = true, setHealthZero = true, restoreIfRefused = true, onlyFirstBlow = true; public float delayS = 0.1f, firstBlowWindowS = 6f; public string forceId = ""; }
    [Serializable] public class ForceRow { public string zone = "", arriveSpot = ""; }
    [Serializable] public class HarnessRow { public string arg = "-s2:", off = "0", on = "1"; public ForceRow force = new ForceRow(); public float chance = 1f, simulateParryAfterS = 0.35f; public bool ignorePassRule = true, ignoreEnabled = true, ignoreCaps = true, ignoreDirector = true, simulateParry = true; }
    [Serializable] public class Cfg
    {
        public bool enabled = false, log = true; public int minPassIndex = 2; public float chance = 0f; public string gateKey = "";
        public SeedRow seed = new SeedRow(); public GateRow[] gates = new GateRow[0]; public CapRow caps = new CapRow(); public TriggerRow trigger = new TriggerRow(); public TellRow tell = new TellRow();
        public SpawnRow spawn = new SpawnRow(); public AssassinRow assassin = new AssassinRow(); public ParryRow parry = new ParryRow(); public HarnessRow harness = new HarnessRow();
    }
    [Serializable] class DirectorSeed { public int seed = 0; }

    const string FileName = "gate_assassin.json", DataRel = "Data/gate_assassin.json";
    static Cfg c; static int forced = -1;   // hc:ok -1 = no -s2: argument; 0 = hard off; 1 = forced
    public static Cfg Get() { if (c != null) return c; c = Read(); return c; }
    static Cfg Read()
    {
        var k = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, FileName); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, DataRel);
            if (File.Exists(p)) { var r = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (r != null) k = r; }
        }
        catch (Exception e) { Debug.LogWarning("[S2] read failed, off: " + e.Message); k = new Cfg(); }
        if (k.gates == null) k.gates = new GateRow[0];
        return k;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; forced = -1; }   // hc:ok -1 = no argument

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            var k = Get();
#if !FRANKEN_NO_HARNESS
            foreach (var a in Environment.GetCommandLineArgs())
                if (!string.IsNullOrEmpty(k.harness.arg) && a.StartsWith(k.harness.arg, StringComparison.Ordinal))
                { string v = a.Substring(k.harness.arg.Length); forced = v == k.harness.on ? 1 : (v == k.harness.off ? 0 : forced); }   // hc:ok sentinel, argument value is data
#endif
            if (forced == 0) return;   // hc:ok 0 = hard off
            bool on = k.enabled || (forced == 1 && k.harness.ignoreEnabled);   // hc:ok 1 = forced
            if (!on) return;
            if (FindFirstObjectByType<GateAssassin>() != null) return;
            var go = new GameObject("GateAssassin"); DontDestroyOnLoad(go); go.AddComponent<GateAssassin>();
        }
        catch (Exception e) { Debug.LogWarning("[S2] boot: " + e.Message); }
    }

    enum St { Idle, Armed, Tell, Fight }
    Cfg K; St st = St.Idle; bool Forced { get { return forced == 1; } }   // hc:ok 1 = forced
    string lastZone = ""; int pass; int used; uint runSeed; bool seeded;
    GateRow gate; Vector3 gatePos; float armedAt, nextPoll, strikeAt, parryDueAt, simParryAt; bool tellPlayed, parryDone, simDone;
    DuelFighter assassin; ZSpawn row; CueBus bus;

    void OnEnable() { ContactRelay.OnEvent += OnEvent; ContactRelay.OnAttackStarted += OnAttack; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; ContactRelay.OnAttackStarted -= OnAttack; }
    void Awake() { K = Get(); }
    void Log(string m) { if (K.log) Debug.Log("[S2] " + m); }

    void Update()
    {
        float now = Time.unscaledTime;
        if (parryDueAt > 0f && now >= parryDueAt) { parryDueAt = 0f; Finish(); }   // hc:ok 0 = not scheduled
        if (st == St.Fight && assassin != null && !simDone && Forced && K.harness.simulateParry && firstAttackAt >= 0f && now >= simParryAt) { simDone = true; parryDone = true; Log("harness: simulated parry of the first blow"); parryDueAt = now; }   // hc:ok
        if (st == St.Fight && (assassin == null || assassin.Dead || !ZoneRuntime.SurpriseLive(assassin)) && parryDueAt <= 0f && !RTFinisher.Busy) { Log("assassin gone, back to idle"); assassin = null; st = St.Idle; }   // hc:ok
        if (st == St.Tell && now >= strikeAt) Strike();
        if (now < nextPoll) return; nextPoll = now + Mathf.Max(K.trigger.pollS, Time.unscaledDeltaTime);
        string zone, spot; DuelFighter pl; bool play;
        if (!ZoneRuntime.SurpriseState(out zone, out spot, out pl, out play)) return;
        if (zone != lastZone)
        {
            bool first = lastZone.Length == 0; lastZone = zone; SurpriseGate.Reset(zone);   // hc:ok 0 = never observed
            if (st == St.Armed || st == St.Tell) { Log("zone changed while armed: disarmed"); st = St.Idle; }
            bool count = !first || (Forced && zone == K.harness.force.zone && spot == K.harness.force.arriveSpot);
            if (count) OnArrive(zone, spot);
        }
        if (st == St.Armed) TickArmed(pl, play, now);
    }

    GateRow RowFor(string zone, string spot) { foreach (var g in K.gates) if (g != null && g.enabled && g.zone == zone && g.arriveSpot == spot) return g; return null; }

    void OnArrive(string zone, string spot)
    {
        var g = RowFor(zone, spot); if (g == null) return;
        pass++; if (!seeded) { runSeed = RunSeed(); seeded = true; }
        bool force = Forced && zone == K.harness.force.zone && spot == K.harness.force.arriveSpot;
        if (st != St.Idle) return;
        if (!force)
        {
            if (!K.enabled) return;
            if (pass < K.minPassIndex) { Log("pass " + pass + " (" + zone + " / " + spot + "): first passes are never hit"); return; }
            if (used >= K.caps.perRun) { Log("run cap reached"); return; }
        }
        double u = (double)ZoneCore.Hash(zone, K.gateKey, pass, K.seed.salt + ":" + runSeed) / (double)uint.MaxValue;
        float ch = force ? K.harness.chance : K.chance;
        Log("pass " + pass + " " + zone + " / " + spot + ": roll " + u.ToString("0.000") + " vs chance " + ch.ToString("0.00") + (force ? " (harness)" : ""));
        if (u >= ch) return;
        Vector3 gp; if (!ZoneRuntime.SurpriseSpotXZ(g.gateSpot, out gp)) { Log("gate spot " + g.gateSpot + " has no pos in " + zone + ": skipped"); return; }
        gate = g; gatePos = gp; armedAt = Time.unscaledTime; st = St.Armed; tellPlayed = false;
    }

    uint RunSeed()
    {
        uint s = ZoneCore.Hash(K.seed.salt, "run", 0, Convert.ToString(DateTime.UtcNow.Ticks));   // hc:ok 0 = generation
        if (K.seed.useDirectorSeed)
        {
            try
            {
                string p = Path.Combine(Application.streamingAssetsPath, K.seed.directorFile); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, K.seed.directorDataRel);
                if (File.Exists(p)) { var d = JsonUtility.FromJson<DirectorSeed>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (d != null && d.seed != 0) s = ZoneCore.Hash(K.seed.salt, "run", d.seed, "director"); }   // hc:ok 0 = seed unset (random per run)
            }
            catch (Exception e) { Debug.LogWarning("[S2] director seed: " + e.Message); }
        }
        return s;
    }

    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }

    bool Offscreen(Vector3 at)
    {
        var cam = Camera.main; if (cam == null) return true; float m = K.trigger.viewMargin;
        Vector3 v = cam.WorldToViewportPoint(at + Vector3.up); return v.z <= 0f || v.x < -m || v.x > 1f + m || v.y < -m || v.y > 1f + m;   // hc:ok 1 = viewport extent, 0 = behind camera
    }

    Vector3 SpawnPos(DuelFighter pl)
    {
        Vector3 d = pl.transform.position - gatePos; d.y = 0f; float dist = d.magnitude;   // hc:ok
        if (dist < 0.01f) { d = -pl.transform.forward; d.y = 0f; dist = 0f; }   // hc:ok
        d.Normalize(); float back = Mathf.Clamp(K.spawn.behindM, K.spawn.minBehindM, dist + K.spawn.insideM);
        Vector3 p = pl.transform.position - d * back; RaycastHit h;
        if (Physics.Raycast(p + Vector3.up * K.spawn.groundRayUpM, Vector3.down, out h, K.spawn.groundRayDownM, ~0, QueryTriggerInteraction.Ignore)) p.y = h.point.y;   // hc:ok
        return p;
    }

    void TickArmed(DuelFighter pl, bool play, float now)
    {
        if (now - armedAt > K.trigger.timeoutS) { Log("timeout: disarmed"); st = St.Idle; return; }
        if (!play || pl == null || pl.Dead) { if (K.trigger.skipWhileDown) return; }
        if (K.trigger.skipWhileFinisher && RTFinisher.Busy) return;
        if (K.trigger.skipWhileEngaged && ZoneRuntime.SurpriseEngagedCount() > 0) return;   // hc:ok 0 = nobody engaged
        float walked = Flat(pl.transform.position, gatePos);
        if (walked > K.trigger.maxWalkM) { Log("walked past maxWalkM: disarmed"); st = St.Idle; return; }
        if (walked < K.trigger.minWalkM) return;
        if (K.trigger.waitOffscreen && !Offscreen(SpawnPos(pl))) return;
        if (!(Forced && K.harness.ignoreDirector) && !SurpriseGate.TryClaim(lastZone, K.gateKey)) { Log("SurpriseGate refused (one surprise per visit): disarmed"); st = St.Idle; return; }
        PlayTell(); st = St.Tell; strikeAt = now + K.tell.leadS; Log("tell played, strike in " + K.tell.leadS.ToString("0.00") + " s");
    }

    void PlayTell()
    {
        if (tellPlayed) return; tellPlayed = true;
        if (bus == null) bus = FindFirstObjectByType<CueBus>();
        if (bus == null || string.IsNullOrEmpty(K.tell.cue)) { Log("tell: no CueBus / cue, silent"); return; }
        for (int i = 0; i < Mathf.Max(1, K.tell.repeat); i++) bus.Play(K.tell.cue, K.tell.gain, i * K.tell.repeatGapS, K.tell.rate);
    }

    void Strike()
    {
        string zone, spot; DuelFighter pl; bool play;
        if (!ZoneRuntime.SurpriseState(out zone, out spot, out pl, out play) || zone != lastZone || pl == null) { st = St.Idle; return; }
        row = ZoneRuntime.SurpriseRowOf(K.assassin.creature);
        var r = new ZSpawn { id = K.assassin.rowId, spot = "", picks = new[] { new ZPick { creature = K.assassin.creature, weight = 1 } }, enabled = false };   // hc:ok weight 1 = the one pick
        if (K.assassin.copyRowFromCreature && row != null) { r.hpMul = row.hpMul; r.damageMul = row.damageMul; r.difficulty = row.difficulty; r.aggro = row.aggro; r.leash = row.leash; r.respawnMinS = row.respawnMinS; r.respawnMaxS = row.respawnMaxS; }
        if (K.assassin.hpMul >= 0f) r.hpMul = K.assassin.hpMul; if (K.assassin.damageMul >= 0f) r.damageMul = K.assassin.damageMul; if (K.assassin.difficulty >= 0) r.difficulty = K.assassin.difficulty;   // hc:ok -1 = unset
        if (K.assassin.aggro >= 0f) r.aggro = K.assassin.aggro; r.leash = Mathf.Max(r.leash, K.assassin.leash);   // hc:ok -1 = unset
        row = r;
        Vector3 pos = SpawnPos(pl); Vector3 to = pl.transform.position - pos; to.y = 0f; float yaw = to.sqrMagnitude > 0f ? Quaternion.LookRotation(to).eulerAngles.y : 0f;   // hc:ok
        var df = ZoneRuntime.SurpriseSpawn(row, K.assassin.creature, pos, yaw);
        if (df == null) { Log("spawn refused (no prefab / row already live): disarmed"); st = St.Idle; return; }
        ZoneRuntime.SurpriseEngage(df); assassin = df; used++; parryDone = false; simDone = false; simParryAt = Time.unscaledTime + K.harness.simulateParryAfterS;
        parryDueAt = 0f; firstAttackAt = -1f; st = St.Fight; Log("STRIKE at " + pos.ToString("F1") + " behind Joan (" + lastZone + ", pass " + pass + ", run use " + used + "/" + K.caps.perRun + ")");   // hc:ok
    }

    float firstAttackAt = -1f;   // hc:ok -1 = no attack yet
    void OnAttack(DuelFighter f, MoveDef m) { if (st == St.Fight && f == assassin && firstAttackAt < 0f) { firstAttackAt = Time.unscaledTime; simParryAt = firstAttackAt + K.harness.simulateParryAfterS; } }   // hc:ok

    void OnEvent(CombatEvent e)
    {
        if (st != St.Fight || assassin == null || !K.parry.finisher) return;
        if (e.type != "Parried" || e.actor != assassin || e.target == null || e.target != GameLoop.Player) return;
        if (parryDone && K.parry.onlyFirstBlow) return;
        if (K.parry.onlyFirstBlow && Time.unscaledTime - strikeAt > K.parry.firstBlowWindowS + K.tell.leadS) return;
        parryDone = true; parryDueAt = Time.unscaledTime + K.parry.delayS; Log("assassin's blow PARRIED: kill-cam in " + K.parry.delayS.ToString("0.00") + " s");
    }

    void Finish()
    {
        var pl = GameLoop.Player; var a = assassin; if (pl == null || a == null || a.Dead || RTFinisher.Busy) return;
        float was = a.health; if (K.parry.setHealthZero) a.health = 0f;   // hc:ok 0 = lethal for the finisher gate
        string wasId = RTFinisher.ForceId; bool wasF = RTFinisher.ForceEnable;
        if (!string.IsNullOrEmpty(K.parry.forceId)) RTFinisher.ForceId = K.parry.forceId; RTFinisher.ForceEnable = K.parry.forceEnable || wasF;
        bool ok = false; try { ok = RTFinisher.TryBegin(pl, a, pl.CurrentMove, true); } catch (Exception ex) { Debug.LogWarning("[S2] finisher threw: " + ex.Message); } finally { RTFinisher.ForceId = wasId; RTFinisher.ForceEnable = wasF; }
        if (!ok && K.parry.restoreIfRefused) a.health = was;
        Log("kill-cam " + (ok ? "STARTED" : "REFUSED (" + RTFinisher.LastRefusal + ")"));
    }
}
