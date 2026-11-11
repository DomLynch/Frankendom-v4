using System;
using System.Collections.Generic;
using UnityEngine;

// S1 THE GROUND BITE v1 (pool lane S1, STAGED DARK, UNPROVEN IN UNITY). NEW FILE (Assets\Scripts\Surprise\). All numbers / names: Assets\Data\ground_bite.json (copy in StreamingAssets, via DataPath).
// enabled false (or a missing file) = no director object, nothing runs. Needs SurpriseGate (surprise_director.json) to let it fire: one surprise per zone visit.
// Idea: in zone 2 (Sobek lane) one of several candidate spots is picked per visit (seeded: differs per run). A faint ripple + dust puffs hint at it when Joan nears. When she walks over it the BURROW CROC (existing fighter, existing
// BurrowCroc / BurrowCrocFx) is placed on her shield side and forced up (BurrowCroc.forceUp), jaws erupt with a dust burst + rings, Joan is HELD (DuelFighter.Stagger) for bite.holdS. ONE beat: a press of strike / dodge
// (existing InputMap actions) inside the window wrenches her free (short stagger, croc keeps its place); no press = missDamage (the belly-bite damage pattern, non-lethal) and the same stagger. Then the croc fights (it is the normal engaged
// Sobek: 96 hp, claws only, killable through the usual Health / DuelFighter paths) or dives (sinks, ZoneRuntime.ForceDisengage sends it home burrowed). The croc brain is switched off during the clamp so it adds no bites of its own.
// Hooks in existing files (apply_s1.py patches, reversible): BurrowCroc.cs (public forceUp + 1 line in Engaged + 1 in Update), ZoneRuntime.cs (public static ForceDisengage). Nothing else.
// UNPROVEN: the whole thing in Unity (Stagger as a hold, croc placed next to Joan, forceUp surface look, ZoneRuntime engage racing the brain switch-off, ForceDisengage after a dive, InputMap ids "strike" / "dodge").
[DefaultExecutionOrder(130)]
public sealed class GroundBite : MonoBehaviour
{
    [Serializable] public class Spot { public float x, z; }
    [Serializable] public class ZoneCfg { public string zoneKey = "sobek_reach"; public float enterZFrom = 36f, enterZTo = 45f, enterXHalf = 3f; }
    [Serializable] public class RollCfg { public float chance = 0.7f, armDelayS = 1.5f; public int maxPerRun = 3; public string salt = "s1_ground_bite"; }
    [Serializable] public class CrocCfg { public string spawnId = "z2_burrow_croc"; public float rightM = -0.7f, forwardM = 0.5f; }
    [Serializable] public class TriggerCfg { public float radiusM = 0.9f; public bool skipIFrames = true; }
    [Serializable] public class TellCfg { public bool enabled = true; public float radiusM = 7f, intervalS = 1.1f, jitterFrac = 0.4f, ringSizeMul = 0.5f, dustSizeMul = 0.6f, dustSpeedMul = 0.5f, dustSpreadM = 0.3f; public int dustCount = 5; }
    [Serializable] public class BiteCfg
    {
        public float holdS = 1.3f, missDamage = 18f, missKnock = 2f, eruptSizeMul = 1.8f, freeKnock = 1.5f; public int ticksPerSecond = 60;
        public string holdClip = "hit_heavy", freeClip = "hit_light_front", missClip = "hit_heavy"; public bool nonLethal = true, knockDownOnMiss;
    }
    [Serializable] public class WrenchCfg { public float graceS = 0.15f; public string[] actions = { "strike", "dodge" }; public string[] fallbackKeys = { "Mouse0", "Space" }; }
    [Serializable] public class AfterCfg { public string mode = "random"; public float diveChance = 0.4f, diveDelayS = 0.9f; }
    [Serializable] public class CueCfg { public string tell = "", erupt = "", clamp = "", free = "", miss = ""; public float gain = 0.3f; }
    [Serializable] public class ProofCfg { public string argPrefix = "-s1:", argOff = "off", flagWrench = "w", flagDive = "d", flagFight = "f"; public float holdS = 4f, autoWrenchS = 2f; }
    [Serializable] public class Cfg
    {
        public bool enabled = false, debugLog = false; public int version = 1;
        public ZoneCfg zone = new ZoneCfg(); public RollCfg roll = new RollCfg(); public Spot[] spots = new Spot[0]; public CrocCfg croc = new CrocCfg(); public TriggerCfg trigger = new TriggerCfg();
        public TellCfg tell = new TellCfg(); public BiteCfg bite = new BiteCfg(); public WrenchCfg wrench = new WrenchCfg(); public AfterCfg after = new AfterCfg(); public CueCfg cues = new CueCfg(); public ProofCfg proof = new ProofCfg();
    }

    const string FileName = "ground_bite.json";
    const float Eps = 0.0001f;
    static Cfg c; static int bitesThisRun;
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try { string txt = DataPath.ReadText(FileName); if (txt != null) { var k = JsonUtility.FromJson<Cfg>(txt.TrimStart((char)0xFEFF)); if (k != null) c = k; } }
        catch (Exception e) { Debug.LogWarning("[GroundBite] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.zone == null) c.zone = new ZoneCfg(); if (c.roll == null) c.roll = new RollCfg(); if (c.spots == null) c.spots = new Spot[0]; if (c.croc == null) c.croc = new CrocCfg(); if (c.trigger == null) c.trigger = new TriggerCfg();   // hc:ok null guards
        if (c.tell == null) c.tell = new TellCfg(); if (c.bite == null) c.bite = new BiteCfg(); if (c.wrench == null) c.wrench = new WrenchCfg(); if (c.after == null) c.after = new AfterCfg(); if (c.cues == null) c.cues = new CueCfg(); if (c.proof == null) c.proof = new ProofCfg();   // hc:ok null guards
        return c;
    }

    // proof arg state (harness only)
    static int proofSpot = -1; static bool proofWrench, proofDive, proofFight, proofOff;
    static void ParseProof(Cfg k)
    {
#if !FRANKEN_NO_HARNESS
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (string.IsNullOrEmpty(k.proof.argPrefix) || !a.StartsWith(k.proof.argPrefix, StringComparison.Ordinal)) continue;
            string v = a.Substring(k.proof.argPrefix.Length);
            if (v == k.proof.argOff) { proofOff = true; continue; }
            int i = 0; while (i < v.Length && char.IsDigit(v[i])) i++;
            int n; if (i == 0 || !int.TryParse(v.Substring(0, i), out n)) continue;
            proofSpot = n; string fl = v.Substring(i);
            proofWrench = !string.IsNullOrEmpty(k.proof.flagWrench) && fl.Contains(k.proof.flagWrench); proofDive = !string.IsNullOrEmpty(k.proof.flagDive) && fl.Contains(k.proof.flagDive); proofFight = !string.IsNullOrEmpty(k.proof.flagFight) && fl.Contains(k.proof.flagFight);
            SurpriseGate.ForceEnable(true);
        }
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; bitesThisRun = 0; proofSpot = -1; proofWrench = proofDive = proofFight = proofOff = false; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var k = Get(); ParseProof(k);
        if (proofOff || (!k.enabled && proofSpot < 0)) return;
        if (FindFirstObjectByType<GroundBite>() != null) return;
        var go = new GameObject("GroundBite"); go.hideFlags = HideFlags.HideInHierarchy; DontDestroyOnLoad(go); go.AddComponent<GroundBite>();
        Debug.Log("[GroundBite] on: " + k.spots.Length + " candidate spots, zone " + k.zone.zoneKey + (proofSpot >= 0 ? " PROOF spot " + proofSpot + " wrench " + proofWrench + " dive " + proofDive + " fight " + proofFight : ""));
    }

    enum Phase { Out, Armed, Hold, Dive, Spent }
    Phase phase = Phase.Out; bool inZone, armed, crocBrainWasOn, willDive; int visit, spotIdx = -1, draws; float armAt, nextTell, holdStart, holdLen, diveAt;
    BurrowCrocFx fx; BurrowCroc croc; DuelFighter crocDf; AIBrain crocBrain; CueBus bus; KeyCode[][] legacy;

    float Rand(int salt)   // seeded per run + visit + draw, no UnityEngine.Random
    {
        var K = Get(); uint h = SurpriseGate.Seed(K.roll.salt); h = (h ^ (uint)visit) * 16777619u; h = (h ^ (uint)(++draws)) * 16777619u; h = (h ^ (uint)salt) * 16777619u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;   // hc:ok FNV-1a constants
        return (h & 0xFFFFFFu) / 16777216f;   // hc:ok 24-bit fraction
    }

    void Update()
    {
        var K = Get(); if (!K.enabled && proofSpot < 0) return;
        var P = GameLoop.Player; if (P == null || P.Dead || !ZoneRuntime.Owns) return;
        if (phase == Phase.Hold) { TickHold(K, P); return; }
        if (phase == Phase.Dive) { TickDive(K); return; }
        var pp = P.transform.position;
        bool now = pp.z >= K.zone.enterZFrom && pp.z <= K.zone.enterZTo && Mathf.Abs(pp.x) <= K.zone.enterXHalf;
        if (now && !inZone) OnEnter(K);
        else if (!now && inZone) { phase = Phase.Out; armed = false; }
        inZone = now;
        if (phase != Phase.Armed || !armed || spotIdx < 0 || spotIdx >= K.spots.Length) return;
        var sp = K.spots[spotIdx]; float dist = new Vector2(pp.x - sp.x, pp.z - sp.z).magnitude;
        if (K.tell.enabled && dist <= K.tell.radiusM && Time.time >= nextTell) Tell(K, sp, pp);
        if (dist <= K.trigger.radiusM && Time.time >= armAt && !(K.trigger.skipIFrames && P.IFrames)) TryBite(K, P, sp);
    }

    void OnEnter(Cfg K)
    {
        visit++; draws = 0; SurpriseGate.Reset(K.zone.zoneKey); phase = Phase.Armed;
        armAt = Time.time + (proofSpot >= 0 ? 0f : Mathf.Max(0f, K.roll.armDelayS)); nextTell = 0f;
        if (K.spots.Length == 0) { armed = false; return; }
        if (proofSpot >= 0) { spotIdx = Mathf.Clamp(proofSpot, 0, K.spots.Length - 1); armed = true; }
        else { spotIdx = Mathf.Min(K.spots.Length - 1, Mathf.FloorToInt(Rand(1) * K.spots.Length)); armed = Rand(2) < K.roll.chance && (K.roll.maxPerRun <= 0 || bitesThisRun < K.roll.maxPerRun); }
        Debug.Log("[GroundBite] visit " + visit + ": spot " + spotIdx + " (" + K.spots[spotIdx].x.ToString("F1") + ", " + K.spots[spotIdx].z.ToString("F1") + ") armed " + armed + " run bites " + bitesThisRun);
    }

    void EnsureFx() { if (fx == null) fx = BurrowCrocFx.Make(BurrowCroc.Get(), transform); }

    void Tell(Cfg K, Spot sp, Vector3 pp)
    {
        EnsureFx(); if (fx == null) return;
        float jit = 1f + (Rand(3) * 2f - 1f) * K.tell.jitterFrac; nextTell = Time.time + K.tell.intervalS * jit;
        var w = new Vector3(sp.x, pp.y, sp.z);
        fx.Ring(w, K.tell.ringSizeMul); fx.Dust(w, Vector3.back, K.tell.dustCount, K.tell.dustSizeMul, K.tell.dustSpeedMul, K.tell.dustSpreadM);
        Cue(K.cues.tell);
    }

    bool FindCroc(Cfg K)
    {
        croc = null; crocDf = null; crocBrain = null;
        foreach (var b in BurrowCroc.All)
        {
            if (b == null) continue; var f = b.GetComponent<DuelFighter>(); if (f == null || f.Dead || BurrowCroc.SpawnId(f.name) != K.croc.spawnId) continue;
            var br = b.GetComponent<AIBrain>(); if (br != null && br.enabled) continue;   // already engaged: not an ambush any more
            croc = b; crocDf = f; crocBrain = br; return true;
        }
        return false;
    }

    void TryBite(Cfg K, DuelFighter P, Spot sp)
    {
        if (!FindCroc(K)) { if (K.debugLog) Debug.Log("[GroundBite] no idle burrow croc: spot stays armed"); return; }
        if (!SurpriseGate.TryClaim(K.zone.zoneKey, "ground_bite")) { phase = Phase.Spent; return; }
        bitesThisRun++; EnsureFx();
        var fwd = P.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < Eps) fwd = Vector3.forward; fwd.Normalize(); var right = Vector3.Cross(Vector3.up, fwd);
        var at = P.transform.position + right * K.croc.rightM + fwd * K.croc.forwardM;
        FinisherPlayer.Teleport(crocDf, at, P.transform.position - at);
        croc.forceUp = true; crocBrainWasOn = false; if (crocBrain != null) crocBrain.enabled = false;
        if (fx != null) { fx.Burst(at, crocDf.transform.forward, true); fx.Ring(at, K.bite.eruptSizeMul); }
        holdLen = proofSpot >= 0 ? K.proof.holdS : K.bite.holdS; holdStart = Time.time;
        P.Stagger(Mathf.CeilToInt(holdLen * Mathf.Max(1, K.bite.ticksPerSecond)), K.bite.holdClip);
        if (legacy == null) BuildKeys(K);
        string m = proofSpot >= 0 && proofDive ? "dive" : proofSpot >= 0 && proofFight ? "fight" : K.after.mode;
        willDive = m == "dive" || (m == "random" && Rand(4) < K.after.diveChance);
        phase = Phase.Hold; Cue(K.cues.erupt); Cue(K.cues.clamp);
        Debug.Log("[GroundBite] BITE at spot " + spotIdx + ": " + crocDf.name + " up at " + at.ToString("F1") + ", Joan held " + holdLen.ToString("F1") + " s, then " + (willDive ? "DIVE" : "FIGHT"));
    }

    void BuildKeys(Cfg K)
    {
        var W = K.wrench; int n = W.actions != null ? W.actions.Length : 0; legacy = new KeyCode[n][];
        for (int i = 0; i < n; i++) { KeyCode kc; string nm = W.fallbackKeys != null && i < W.fallbackKeys.Length ? W.fallbackKeys[i] : ""; legacy[i] = new[] { Enum.TryParse(nm, out kc) ? kc : KeyCode.None }; }
    }

    bool Pressed(Cfg K)
    {
        var A = K.wrench.actions; if (A == null || legacy == null) return false;
        for (int i = 0; i < A.Length && i < legacy.Length; i++) if (InputMap.AnyDown(A[i], legacy[i])) return true;
        return false;
    }

    void TickHold(Cfg K, DuelFighter P)
    {
        if (crocBrain != null && crocBrain.enabled) { crocBrainWasOn = true; crocBrain.enabled = false; }   // ZoneRuntime engaged it: keep it quiet for the window
        float age = Time.time - holdStart;
        if (P.Dead) { EndHold(K, P, false, true); return; }
        bool auto = proofSpot >= 0 && proofWrench && age >= K.proof.autoWrenchS;
        if (age >= K.wrench.graceS && (Pressed(K) || auto)) { EndHold(K, P, true, false); return; }
        if (age >= holdLen) EndHold(K, P, false, false);
    }

    void EndHold(Cfg K, DuelFighter P, bool freed, bool playerDead)
    {
        var B = K.bite; float age = Time.time - holdStart;
        if (!playerDead)
        {
            var away = P.transform.position - (crocDf != null ? crocDf.transform.position : P.transform.position); away.y = 0f;
            if (freed) { P.Stagger(0, B.freeClip); if (away.sqrMagnitude > Eps) P.SetKnock(away.normalized * B.freeKnock); Cue(K.cues.free); }
            else
            {
                float dmg = B.missDamage * (crocDf != null ? crocDf.DamageScaleNow : 1f), hp0 = P.health; P.health -= dmg; if (B.nonLethal && P.health < 1f) P.health = 1f;
                if (away.sqrMagnitude > Eps) P.SetKnock(away.normalized * B.missKnock);
                if (B.knockDownOnMiss) P.KnockDown(); else P.Stagger(0, B.missClip);
                if (P.health <= 0f) P.Die();
                Cue(K.cues.miss); Debug.Log("[GroundBite] MISS: Joan hp " + hp0.ToString("F0") + " -> " + P.health.ToString("F0"));
            }
        }
        Debug.Log("[GroundBite] " + (freed ? "WRENCH FREE after " : "HOLD ended (no press) after ") + age.ToString("F2") + " s");
        if (croc != null) croc.forceUp = false;
        if (willDive && croc != null && !playerDead) { phase = Phase.Dive; diveAt = Time.time + K.after.diveDelayS; return; }
        if (crocDf != null && crocBrain != null && crocBrainWasOn && !crocDf.Dead) crocBrain.enabled = true;   // fight: its normal AI resumes
        phase = Phase.Spent; Debug.Log("[GroundBite] croc fights");
    }

    void TickDive(Cfg K)
    {
        if (Time.time < diveAt) return;
        if (crocDf != null && !crocDf.Dead) { bool ok = ZoneRuntime.ForceDisengage(K.croc.spawnId); Debug.Log("[GroundBite] croc dives: ForceDisengage " + ok); }
        phase = Phase.Spent;
    }

    void Cue(string name)
    {
        if (string.IsNullOrEmpty(name)) return; if (bus == null) bus = FindFirstObjectByType<CueBus>();
        if (bus != null) bus.Play(name, Get().cues.gain);
    }
}
