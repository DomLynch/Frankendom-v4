using System;
using System.IO;
using UnityEngine;

// FAKEDEATH v1 (pool-laneS7, NEW file, UNPROVEN IN UNITY). Two surprise mechanics, both DARK (fake_deaths.json enabled=false; no file = today's game):
//  (a) CORPSE GRAB: when Joan picks up loot beside a dead bandit, rarely (seeded chance per run, data cap) the corpse comes back for her ankle: revive of the ragdoll (RequestRevive), a lying pose, a reach clip,
//      Joan held for holdS (a scripted Down, DuelFighterFake.cs), ONE kick window (the existing "kick" action); a kick frees her and the body goes limp again, a miss costs hp (non lethal by data), then it goes limp.
//  (b) BOSS RISES ONCE: one boss per run (seeded pick among pickIds, persisted), in its first lethal blow: it falls with the existing death clip, pauses, rises with riseHpFrac (existing get_up clip) and the fight resumes.
//      The fall never calls Die(): no OnKilled, no drop / silver / kill record / ragdoll until the SECOND (real) death. A deathblow finisher is a real kill (never faked).
// Every number / clip / cue / id lives in fake_deaths.json. One surprise per zone visit by default through SurpriseGate.TryClaim. Randomness: System.Random(seed ^ runId), runId + counters persisted in
// fake_deaths_state.json (Application.persistentDataPath), reset by a new game (SaveSlot.OnErase). Harness runs (GameLoop.HarnessOn) never fire it unless FakeDeathProof forces it (-s7:1).
[Serializable] public class FdStep { public string clip = ""; public float startS = 0f, speed = 1f, seconds = 0f, fade = 0f; public bool hold = false, deathRow = false; public string cue = ""; public float cueGain = 0f, cueRate = 1f, cueDelay = 0f; public string shake = ""; public float shakeMul = 1f; }
[Serializable] public class FdCue { public string cue = ""; public float gain = 0f, rate = 1f, delay = 0f; public string shake = ""; public float shakeMul = 1f; }
[Serializable] public class FdBoss
{
    public bool enabled = false; public string[] pickIds = new string[0]; public int maxPerRun = 1; public float chance = 1f, riseHpFrac = 0.3f, holdHp = 1f, damageMulAfter = 1f;
    public string gateKey = ""; public string idleClip = ""; public float idleFade = 0f; public FdStep[] steps = new FdStep[0];
}
[Serializable] public class FdCorpse
{
    public bool enabled = false; public float chancePerLoot = 0f; public int maxPerRun = 1; public string gateKey = ""; public string[] matchPrefixes = new string[0], zones = new string[0];
    public float lootRadiusM = 0f, anklePlaceM = 0f, reviveWaitS = 0f, reviveHpFrac = 0.1f, holdS = 0f, kickFromS = 0f, kickToS = 0f, kickKnock = 0f, damage = 0f, minHp = 1f;
    public string kickAction = "", idleClip = "", hurtClip = ""; public float idleFade = 0f; public int hurtStaggerTicks = 0; public bool freeKicks = true, nonLethal = true;
    public FdStep[] corpseSteps = new FdStep[0], playerSteps = new FdStep[0]; public FdCue grabCue = new FdCue(), freeCue = new FdCue(), hurtCue = new FdCue();
}
[Serializable] public class FdProof { public float autoAfterS = 0f, deadWaitS = 0f, scanEveryS = 1f; }   // harness only: -s7corpse:auto kills the nearest living bandit after autoAfterS and grabs deadWaitS later
[Serializable] public class FdFile { public bool enabled = false, log = true; public int seed = 0; public string stateFile = ""; public FdBoss boss = new FdBoss(); public FdCorpse corpse = new FdCorpse(); public FdProof proof = new FdProof(); }
[Serializable] public class FdState { public int runId = 0, bossRisen = 0, corpseGrabs = 0, rolls = 0; public string bossId = ""; public bool has = false; }

public sealed class FakeDeath : MonoBehaviour
{
    // ---- switches (FakeDeathProof sets the force flags from the command line; they stay at their defaults in a release build) ----
    public static bool ForceOff, ForceAll, ForceCorpse, ForceBoss, AutoCorpse; public static string ForceBossId = "";
    public static int BossRises, CorpseGrabs, CorpseFreed, CorpseHurt;   // proof counters (log / harness)

    static FdFile cfg; static bool loaded; static FdState st; static System.Random rng; static FakeDeath inst;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; loaded = false; st = null; rng = null; inst = null; BossRises = CorpseGrabs = CorpseFreed = CorpseHurt = 0; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)] static void HookErase() { SaveSlot.OnErase += OnNewGame; }

    static FdFile Cfg()
    {
        if (loaded) return cfg;
        loaded = true; cfg = null;
        string txt = DataPath.ReadText("fake_deaths.json"); if (txt == null) return null;
        try { cfg = JsonUtility.FromJson<FdFile>(txt); } catch (Exception e) { Debug.LogWarning("[FakeDeath] fake_deaths.json parse failed: " + e.Message); }
        return cfg;
    }

    /// <summary>The master switch every hook tests first. Off = the hooks return at once and touch nothing.</summary>
    public static bool On
    {
        get
        {
            if (ForceOff) return false;
            if (ForceAll || ForceCorpse || ForceBoss) return true;
            var c = Cfg(); return c != null && c.enabled && !GameLoop.HarnessOn;
        }
    }
    public static void Warn(string s) { Debug.LogWarning("[FakeDeath] " + s); }
    static void Log(string s) { var c = Cfg(); if (c == null || c.log) Debug.Log("[FakeDeath] " + s); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!On || inst != null) return;
        var go = new GameObject("FakeDeath"); DontDestroyOnLoad(go); inst = go.AddComponent<FakeDeath>();
    }

    // ---- run state (persisted) ----
    static string StatePath { get { var c = Cfg(); return c == null || string.IsNullOrEmpty(c.stateFile) ? null : Path.Combine(Application.persistentDataPath, c.stateFile); } }
    static FdState State()
    {
        if (st != null) return st;
        st = new FdState(); string p = StatePath;
        try { if (p != null && File.Exists(p)) { var k = JsonUtility.FromJson<FdState>(File.ReadAllText(p)); if (k != null && k.has) st = k; } } catch (Exception e) { Warn("state unreadable: " + e.Message); }
        if (!st.has) { st.has = true; st.runId = Environment.TickCount ^ Cfg().seed; Save(); }
        return st;
    }
    static void Save() { string p = StatePath; if (p == null || st == null) return; try { File.WriteAllText(p, JsonUtility.ToJson(st)); } catch (Exception e) { Warn("state not saved: " + e.Message); } }
    static void OnNewGame() { st = null; rng = null; string p = StatePath; try { if (p != null && File.Exists(p)) File.Delete(p); } catch (Exception) { } }   // a new game is a new run: new pick, caps full
    /// <summary>Seeded roll that differs per run and survives a restart (the draw counter is saved).</summary>
    static bool Roll(float p)
    {
        if (p >= 1f) return true; if (p <= 0f) return false;
        var S = State(); if (rng == null) { rng = new System.Random(Cfg().seed ^ S.runId); for (int i = 0; i < S.rolls; i++) rng.NextDouble(); }
        S.rolls++; Save(); return rng.NextDouble() < p;
    }
    static string Zone() { var z = ZoneRuntime.Inst; return z != null ? z.ZoneIdNow : ""; }
    static bool IdIs(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase); }
    static bool InList(string[] l, string id) { if (l == null) return false; for (int i = 0; i < l.Length; i++) if (IdIs(l[i], id)) return true; return false; }

    // ================= (b) the boss that rises once =================
    static string PickedBoss(FdBoss B)
    {
        if (!string.IsNullOrEmpty(ForceBossId)) return ForceBossId;
        var S = State();
        if (string.IsNullOrEmpty(S.bossId) && B.pickIds != null && B.pickIds.Length > 0) { var r = new System.Random(Cfg().seed ^ S.runId); S.bossId = B.pickIds[r.Next(B.pickIds.Length)]; Save(); Log("this run's boss that rises once: " + S.bossId); }
        return S.bossId;
    }

    /// <summary>Called from DuelFighter.FakeDeathTry at the killing blow. Returns the boss row when THIS fighter must fall and rise (quota spent), else null.</summary>
    public static FdBoss WantRise(DuelFighter v)
    {
        if (!On || v == null || v == GameLoop.Player || RTFinisher.Busy) return null;
        var c = Cfg(); if (c == null) return null; var B = c.boss; if (B == null || B.steps == null || B.steps.Length == 0) return null;
        bool forced = ForceAll || ForceBoss; if (!forced && !B.enabled) return null;
        string id = v.CharacterId; if (!InList(B.pickIds, id)) return null;
        if (forced) { if (!string.IsNullOrEmpty(ForceBossId) && !IdIs(ForceBossId, id)) return null; }
        else
        {
            if (!IdIs(PickedBoss(B), id) || State().bossRisen >= B.maxPerRun || !Roll(B.chance)) return null;
            if (!SurpriseGate.TryClaim(Zone(), B.gateKey)) return null;
        }
        State().bossRisen++; Save(); BossRises++;
        Log(id + " falls at zero health and will rise once (run rises " + State().bossRisen + "/" + B.maxPerRun + (forced ? ", forced" : "") + ")");
        return B;
    }

    static CueBus bus;
    static void PlayCue(string cue, float gain, float delay, float rate, string shake, float shakeMul)
    {
        if (!string.IsNullOrEmpty(cue) && gain > 0f) { if (bus == null) bus = FindFirstObjectByType<CueBus>(); if (bus != null) bus.Play(cue, gain, delay, rate <= 0f ? 1f : rate); }   // an existing cue id only (missing audio is listed in READY.md)
        if (!string.IsNullOrEmpty(shake)) CamShake.Trigger(shake, shakeMul);
    }
    /// <summary>DuelFighter.FakeNext: a step began (its optional cue / camera shake = the "rising" telegraph).</summary>
    public static void StepStarted(DuelFighter v, int idx, FdStep s) { if (s != null) PlayCue(s.cue, s.cueGain, s.cueDelay, s.cueRate, s.shake, s.shakeMul); }

    /// <summary>Boss sequence over: stands up with riseHpFrac (completed false = it was aborted, nothing changes).</summary>
    public static void BossFinished(DuelFighter v, bool completed)
    {
        var c = Cfg(); if (!completed || v == null || c == null) return; var B = c.boss;
        v.FakeSetHealth(Mathf.Max(B.holdHp, v.maxHealth * B.riseHpFrac)); v.stamina = v.maxStamina;
        if (B.damageMulAfter > 0f && B.damageMulAfter != 1f) v.damageScale *= B.damageMulAfter;
        Log(v.CharacterId + " rises with hp " + v.health.ToString("F0") + "/" + v.maxHealth.ToString("F0") + " at sim tick " + Duel.TickCount);
    }

    // ================= (a) the corpse that grabs the ankle =================
    enum Ph { None, Reviving, Held }
    Ph ph; DuelFighter corpse, joan; float phT, heldAt;

    /// <summary>LootUI.Collect hook: Joan just took an item at anchor.</summary>
    public static void OnLoot(Vector3 anchor) { if (On && inst != null) inst.TryGrab(anchor); }

    void TryGrab(Vector3 anchor)
    {
        var c = Cfg(); if (c == null) return; var C = c.corpse; bool forced = ForceAll || ForceCorpse;
        if (C == null || (!forced && !C.enabled) || ph != Ph.None) return;
        joan = GameLoop.Player; if (joan == null || joan.Dead || joan.FakeOn || RTFinisher.Busy) return;
        if (C.corpseSteps == null || C.corpseSteps.Length == 0 || C.playerSteps == null || C.playerSteps.Length == 0) return;
        string zone = Zone(); if (!forced && C.zones.Length > 0 && !InList(C.zones, zone)) return;
        if (!forced && State().corpseGrabs >= C.maxPerRun) return;
        DuelFighter best = null; float bd = C.lootRadiusM;
        foreach (var v in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (v == null || v == joan || !v.Dead) continue;
            bool match = false; for (int i = 0; i < C.matchPrefixes.Length && !match; i++) match = v.CharacterId.StartsWith(C.matchPrefixes[i], StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            Vector3 d = v.transform.position - anchor; d.y = 0f; float m = d.magnitude; if (m <= bd) { bd = m; best = v; }
        }
        if (best == null) return;
        if (!forced && (!Roll(C.chancePerLoot) || !SurpriseGate.TryClaim(zone, C.gateKey))) return;
        State().corpseGrabs++; Save(); CorpseGrabs++;
        corpse = best; ph = Ph.Reviving; phT = Time.time; corpse.RequestRevive(C.reviveHpFrac);
        Log(corpse.name + " (" + corpse.CharacterId + ") stirs as Joan loots in " + zone + (forced ? " (forced)" : "") + " (run grabs " + State().corpseGrabs + "/" + C.maxPerRun + ")");
    }

    float autoNext = -1f, autoKillAt = -1f; DuelFighter autoVictim;
    void Update()
    {
#if !FRANKEN_NO_HARNESS
        if (AutoCorpse && ph == Ph.None) ProofAuto();
#endif
        if (ph == Ph.None) return;
        var C = Cfg().corpse;
        if (corpse == null || joan == null || joan.Dead) { EndGrab(false); return; }
        if (ph == Ph.Reviving)
        {
            if (corpse.ReviveFailed || Time.time - phT > C.reviveWaitS) { Warn("corpse could not stand up (" + (corpse.ReviveFailed ? "ragdoll not restorable" : "timeout") + "): no grab"); EndGrab(false); return; }
            if (corpse.Dead) return;   // the revive runs at the top of the next sim tick
            var br = corpse.GetComponent<AIBrain>(); if (br != null) br.enabled = false;
            corpse.opponent = null; corpse.moveDir = Vector3.zero;
            Vector3 jp = joan.transform.position, away = corpse.transform.position - jp; away.y = 0f; away = away.sqrMagnitude > 1e-4f ? away.normalized : -joan.transform.forward;
            FinisherPlayer.Teleport(corpse, jp + away * C.anklePlaceM, -away);   // re-rooted at her ankle, facing her feet
            corpse.FakeStart(C.corpseSteps, true, C.idleClip, C.idleFade, null);
            joan.FakeStart(C.playerSteps, false, C.idleClip, C.idleFade, null);
            heldAt = Time.time; ph = Ph.Held; PlayCue(C.grabCue.cue, C.grabCue.gain, C.grabCue.delay, C.grabCue.rate, C.grabCue.shake, C.grabCue.shakeMul);
            Log("Joan is held by the corpse for " + C.holdS.ToString("F1") + " s, kick window " + C.kickFromS.ToString("F2") + " .. " + C.kickToS.ToString("F2") + " s");
            return;
        }
        // Held
        if (!joan.FakeOn) { EndGrab(false); return; }   // something else ended her hold (zone change, finisher): no damage
        float t = Time.time - heldAt;
        if (t >= C.kickFromS && t <= C.kickToS && InputMap.Down(C.kickAction, KeyCode.None)) { Free(C); return; }
        if (t >= C.holdS) Hurt(C);
    }

    void Free(FdCorpse C)
    {
        CorpseFreed++; Log("Joan kicks free after " + (Time.time - heldAt).ToString("F2") + " s");
        PlayCue(C.freeCue.cue, C.freeCue.gain, C.freeCue.delay, C.freeCue.rate, C.freeCue.shake, C.freeCue.shakeMul);
        Vector3 away = corpse.transform.position - joan.transform.position; away.y = 0f; if (away.sqrMagnitude > 1e-4f) corpse.SetKnock(away.normalized * C.kickKnock);
        var j = joan; EndGrab(true); if (C.freeKicks && j != null) j.Press(C.kickAction);
    }

    void Hurt(FdCorpse C)
    {
        CorpseHurt++; var j = joan; float h0 = j.health;
        float dmg = C.damage; float n = j.health - dmg; if (C.nonLethal && n < C.minHp) n = C.minHp;
        j.FakeAbort(); j.FakeSetHealth(n);
        if (j.health > 0f && C.hurtStaggerTicks > 0) j.Stagger(C.hurtStaggerTicks, C.hurtClip);
        PlayCue(C.hurtCue.cue, C.hurtCue.gain, C.hurtCue.delay, C.hurtCue.rate, C.hurtCue.shake, C.hurtCue.shakeMul);
        Log("the corpse holds on: Joan hp " + h0.ToString("F0") + " -> " + j.health.ToString("F0"));
        EndGrab(true); if (j.health <= 0f && !j.Dead) j.Die();
    }

    // the body goes limp again (a second Die: the one-shot guards of GearDrop / WeaponDrop / BossSilver / ZoneRuntime keep rewards and the kill record single) and Joan stands free
    void EndGrab(bool killCorpse)
    {
        var c = corpse; var j = joan; corpse = null; joan = null; ph = Ph.None;
        if (j != null && j.FakeOn) j.FakeAbort();
        if (c != null && c.FakeOn) c.FakeAbort();
        if (killCorpse && c != null && !c.Dead) c.Die();
    }

#if !FRANKEN_NO_HARNESS
    // -s7corpse:auto: no fight needed for a proof clip. After proof.autoAfterS the nearest living bandit is killed (a normal Die: ragdoll, drops), deadWaitS later the loot beside it is "taken" and the grab starts.
    void ProofAuto()
    {
        var P = Cfg().proof; var C = Cfg().corpse; float now = Time.time;
        if (autoVictim != null) { if (autoVictim.Dead && now >= autoKillAt) { var v = autoVictim; autoVictim = null; TryGrab(v.transform.position); } return; }
        if (now < P.autoAfterS || now < autoNext) return;
        autoNext = now + P.scanEveryS; joan = GameLoop.Player; if (joan == null || joan.Dead) return;
        DuelFighter best = null; float bd = float.MaxValue;
        foreach (var v in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (v == null || v == joan || v.Dead) continue;
            bool match = false; for (int i = 0; i < C.matchPrefixes.Length && !match; i++) match = v.CharacterId.StartsWith(C.matchPrefixes[i], StringComparison.OrdinalIgnoreCase);
            if (!match) continue; float d = (v.transform.position - joan.transform.position).sqrMagnitude; if (d < bd) { bd = d; best = v; }
        }
        if (best == null) return;
        best.health = 0f; best.Die(); autoVictim = best; autoKillAt = now + P.deadWaitS; Log("proof: killed " + best.name + ", the grab follows in " + P.deadWaitS.ToString("F1") + " s");
    }
#endif

    void OnDisable() { if (ph != Ph.None) EndGrab(false); if (inst == this) inst = null; }
}
