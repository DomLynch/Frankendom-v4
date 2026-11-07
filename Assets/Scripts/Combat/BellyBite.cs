using System;
using UnityEngine;

// laneBELLYBITE v1 (NEW). Pitborn's belly mouth: a telegraphed grab that cannot be blocked (only a perfect parry or a dodge beats it), the mouth morph driver and the AI use.
// Data: belly_bite.json. MASTER SWITCH enabled=false (and a missing file) = today: every hook below returns on its first line and touches no sim state, no RNG, no object.
// Hooks (the only edits elsewhere): Duel.Resolve (1 line, after the dodge i-frame test), DuelFighter.Press (accepts the move id), AIBrain (1 line before the specials block).
// The sim effect (damage, knock-down, throw) is applied here in ticks, identical with or without a presentation scene, so a headless -balanceauto run measures the same fight.
public static class BellyBite
{
    [Serializable] public class Ai { public bool enabled = false; public float minGap = 1.2f, maxGap = 2.6f; public int cooldownTicks = 420, firstTick = 600; public float chance = 0.25f; public float maxPlayerHpFrac = 1.0f; }
    [Serializable] public class Mouth { public bool enabled = true; public float openAtWindupFrac = 0.35f, openFull = 1f, clench = 1f, clenchTicks = 8, relaxTicks = 20; public string openShape = "mouth_open", clenchShape = "mouth_clench"; }
    // BELLYGRAB v1: the paired grab scene (finishers.json entry belly_bite, survive = true). enabled=false (or no block) = the headless grab of BELLYBITE exactly.
    [Serializable] public class MouthKey { public float t, open, clench; }
    [Serializable] public class SceneCfg { public bool enabled = false; public string entryId = "belly_bite"; public bool forceEnable = false, reRoot = true, getUpPose = true; public float endKnock = 0f; public bool requireClips; /* BELLYLETHAL v1: data key required (true) */ public MouthKey[] mouthKeys; }
    [Serializable] public class Data
    {
        public bool enabled = false;               // MASTER SWITCH
        public string character = "Pitborn";       // roster id of the grabber
        public string moveId = "belly_grab";       // row in move-defs.json
        public float damage = 22f, throwKnock = 5f; public bool nonLethal = true;   // nonLethal: the victim keeps at least 1 HP
        public bool knockDown = true;
        public bool blockable = false, parryable = true, dodgeable = true;           // parryable: only a perfect parry window beats it
        public Ai ai = new Ai(); public Mouth mouth = new Mouth(); public SceneCfg scene = new SceneCfg();   // BELLYGRAB v1
    }
    static Data d; static bool loaded;
    public static Data Cfg
    {
        get
        {
            if (!loaded) { loaded = true; try { var t = DataPath.ReadText("belly_bite.json"); if (!string.IsNullOrEmpty(t)) d = JsonUtility.FromJson<Data>(t); } catch (Exception e) { Debug.LogWarning("[bellybite] read failed: " + e.Message); } }
            return d;
        }
    }
    public static void Reload() { loaded = false; d = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { loaded = false; d = null; nextAi.Clear(); }

    public static bool On { get { var c = Cfg; return c != null && c.enabled; } }
    public static bool Handles(DuelFighter a, MoveDef m)
    {
        var c = Cfg; if (c == null || !c.enabled || m == null || a == null) return false;
        return m.id == c.moveId && a.CharacterId == c.character;
    }

    public static bool IsPress(string what) { var c = Cfg; return c != null && c.enabled && what == c.moveId; }

    public enum Result { Landed, Parried, Scene }   // BELLYGRAB v1: Scene = the paired finisher scene took over (Duel.Resolve returns true, no Hit fired here)
    // Called by Duel.Resolve (the dodge i-frame test has already passed: a roll / sidestep beats it). Landed = the caller fires the Hit event and returns true.
    public static Result Resolve(DuelFighter a, DuelFighter v)
    {
        var c = Cfg;
        bool defGuard = v.IsGuarding;
        if (c.parryable && defGuard && v.InParryWindow) return Result.Parried;   // the normal parry branch of Duel.Resolve takes over
        if (TryScene(a, v)) return Result.Scene;   // BELLYGRAB v1: scene.enabled + finishers on + not a balance run; any refusal falls back to the headless path below
        Land(a, v);
        return Result.Landed;
    }

    // the damage / knock-down / throw, shared by the headless path (Resolve) and a presentation scene that ends with entry.survive (FinisherPlayer: the sim is frozen during the scene, then this runs)
    // ---- BELLYGRAB v1: paired scene ---------------------------------------------------------------------------------------------------------------------------------------------
    public static bool SceneOn; public static float SceneOpen, SceneClench; static float sceneT;
    static bool TryScene(DuelFighter a, DuelFighter v)
    {
        var c = Cfg; var sc = c != null ? c.scene : null; if (sc == null || !sc.enabled || string.IsNullOrEmpty(sc.entryId)) return false;
        if (RTFinisher.Busy) return false;
        var ent = FinisherPlayer.ById(sc.entryId); if (ent == null) return false;
        if (sc.requireClips && !ClipsReady(ent, a, v)) return false;   // BELLYLETHAL v1: without the baked clips the scene is two frozen idles for 5 s: the headless grab runs instead
        string wasId = RTFinisher.ForceId; bool wasF = RTFinisher.ForceEnable; RTFinisher.ForceId = sc.entryId; RTFinisher.ForceEnable = sc.forceEnable || ProofFlag;   // BELLYLETHAL v1: -bellyscene = proof flag (scene also inside -balanceauto)
        bool ok = false; try { ok = RTFinisher.TryBegin(a, v, a.CurrentMove); } catch (Exception e) { Debug.LogWarning("[bellygrab] scene refused: " + e.Message); } finally { RTFinisher.ForceId = wasId; RTFinisher.ForceEnable = wasF; }
        return ok;
    }
    static readonly bool ProofFlag = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-bellyscene") >= 0;   // BELLYLETHAL v1: off without the flag
    static bool warnedClips;
    static bool ClipsReady(FinisherEntry ent, DuelFighter a, DuelFighter v)
    {
        bool ok = (string.IsNullOrEmpty(ent.attackerClip) || FinisherPlayer.HasState(a.GetComponent<Fighter>(), ent.attackerClip)) && (string.IsNullOrEmpty(ent.victimClip) || FinisherPlayer.HasState(v.GetComponent<Fighter>(), ent.victimClip));
        if (!ok && !warnedClips) { warnedClips = true; Debug.LogWarning("[bellygrab] scene skipped: animator state " + ent.attackerClip + " / " + ent.victimClip + " missing (run Tools/Combat/Add Finisher Clips); headless grab used"); }
        return ok;
    }
    public static void SceneBegin(DuelFighter a)
    {
        SceneOn = true; SceneOpen = 0f; SceneClench = 0f; sceneT = 0f;
        if (a != null && a.GetComponentInChildren<BellyMouthDriver>() == null) a.gameObject.AddComponent<BellyMouthDriver>();
    }
    public static void SceneTick(float T)
    {
        var c = Cfg; if (!SceneOn || c == null || c.scene == null || c.scene.mouthKeys == null || c.scene.mouthKeys.Length == 0) return;
        sceneT = T; var k = c.scene.mouthKeys; float o = k[0].open, cl = k[0].clench;
        if (T >= k[k.Length - 1].t) { o = k[k.Length - 1].open; cl = k[k.Length - 1].clench; }
        else for (int i = 0; i < k.Length - 1; i++) if (T >= k[i].t && T <= k[i + 1].t) { float u = (T - k[i].t) / Mathf.Max(1e-5f, k[i + 1].t - k[i].t); o = Mathf.Lerp(k[i].open, k[i + 1].open, u); cl = Mathf.Lerp(k[i].clench, k[i + 1].clench, u); break; }
        SceneOpen = o * c.mouth.openFull; SceneClench = cl * c.mouth.clench;
    }
    static Transform FindHips(Transform root) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name.EndsWith("Hips")) return t; return null; }
    // the same damage / knock-down as the headless path; with scene.getUpPose the victim is re-rooted where the thrown body lies and goes straight to the get_up clip (the baked throw ends on its first frame)
    public static void EndScene(DuelFighter a, DuelFighter v)
    {
        SceneOn = false; SceneOpen = 0f; SceneClench = 0f;
        if (!On || a == null || v == null) return;
        var sc = Cfg.scene; bool pose = sc != null && sc.enabled && sc.getUpPose;
        if (pose && sc.reRoot) { var hp = FindHips(v.transform); if (hp != null) { Vector3 q = hp.position; q.y = v.transform.position.y; FinisherPlayer.Teleport(v, q, v.transform.forward); } }
        float hp0 = v.health;   // BELLYLETHAL v1: proof line
        Land(a, v, pose, sc != null ? sc.endKnock : -1f);
        Debug.Log("[bellygrab] scene ended: " + v.name + " hp " + hp0.ToString("0.0") + " -> " + v.health.ToString("0.0") + " dead=" + v.Dead + " state=" + v.state);   // BELLYLETHAL v1
    }
    static void Land(DuelFighter a, DuelFighter v, bool lying = false, float endKnock = -1f)
    {   // BELLYGRAB v1: lying = the scene already threw her (animation), endKnock >= 0 replaces throwKnock
        var c = Cfg;
        float dmg = c.damage * a.DamageScaleNow;
        v.health -= dmg;
        if (c.nonLethal && v.health < 1f) v.health = 1f;
        Vector3 away = v.transform.position - a.transform.position; away.y = 0f; away = away.sqrMagnitude > 1e-6f ? away.normalized : a.transform.forward;
        v.SetKnock(away * (endKnock >= 0f ? endKnock : c.throwKnock) * a.scale);
        if (c.knockDown) { v.KnockDown(); if (lying) { var fv = v.GetComponent<Fighter>(); if (fv != null) fv.ForceExact("get_up", 0f, 0f); v.t = 53; } } else v.Stagger(30, "hit_heavy");   // BELLYGRAB v1: t = 53 -> the next tick starts get_up (DuelFighter Down state)
        if (v.health <= 0f) v.Die();
    }

    // ---- AI: deterministic (tick hash, no UnityEngine.Random, nothing drawn from the brain's RNG) ----
    static readonly System.Collections.Generic.Dictionary<int, int> nextAi = new System.Collections.Generic.Dictionary<int, int>();
    public static bool AiWants(DuelFighter df, DuelFighter o, float gap, int tick)
    {
        var c = Cfg; if (c == null || !c.enabled || !c.ai.enabled || df == null || o == null) return false;
        if (df.CharacterId != c.character || df.moves == null || df.moves.Get(c.moveId) == null) return false;
        if (gap < c.ai.minGap || gap > c.ai.maxGap) return false;
        if (o.maxHealth > 0f && o.health / o.maxHealth > c.ai.maxPlayerHpFrac) return false;
        int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(df); int next; if (!nextAi.TryGetValue(id, out next)) { next = c.ai.firstTick; nextAi[id] = next; if (df.GetComponent<BellyMouthDriver>() == null) df.gameObject.AddComponent<BellyMouthDriver>(); }
        if (tick < next) return false;
        uint h = (uint)(tick * 2654435761u); if ((h >> 8) % 1000u >= (uint)(c.ai.chance * 1000f)) return false;
        nextAi[id] = tick + c.ai.cooldownTicks; return true;
    }
}

// Drives the two morph targets of the belly mouth from the grab move's own phase (no clip channel needed). Added lazily to a fighter whose mesh carries the shapes.
public sealed class BellyMouthDriver : MonoBehaviour
{
    DuelFighter df; SkinnedMeshRenderer smr; int iOpen = -1, iClench = -1; bool searched; float lodHoldT;   // BELLYLOOK v1
    public static float Override = -1f;   // presentation scene (stage 2): >= 0 forces mouth_open to this 0..1 and clench 0
    void Awake() { df = GetComponentInParent<DuelFighter>(); }
    void Find()
    {
        searched = true; var c = BellyBite.Cfg; if (c == null) return;
        foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var m = r.sharedMesh; if (m == null || m.blendShapeCount == 0) continue;
            int a = m.GetBlendShapeIndex(c.mouth.openShape), b = m.GetBlendShapeIndex(c.mouth.clenchShape);
            if (a >= 0) { smr = r; iOpen = a; iClench = b; return; }
        }
    }
    void LateUpdate()
    {
        var c = BellyBite.Cfg; if (c == null || !c.enabled || !c.mouth.enabled) { if (smr != null) Zero(); return; }
        if (!searched) Find(); if (smr == null) return;
        float o = 0f, k = 0f; var m = df != null ? df.CurrentMove : null;
        if (m != null && m.id == c.moveId)
        {
            int wt = Mathf.Max(1, Mathf.RoundToInt(m.windup * 1f)), t = df.t;
            if (df.state == FState.Windup) o = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(c.mouth.openAtWindupFrac * wt, wt, t));
            else if (df.state == FState.Active) o = 1f;
            else if (df.state == FState.Recovery) { float u = Mathf.Clamp01(t / Mathf.Max(1f, c.mouth.clenchTicks)); o = 1f - u; k = u * (1f - Mathf.Clamp01((t - c.mouth.clenchTicks) / Mathf.Max(1f, c.mouth.relaxTicks))); }
        }
        if (Override >= 0f) { o = Override; k = 0f; }
        if (BellyBite.SceneOn && !RTFinisher.Busy) BellyBite.SceneOn = false;   // BELLYGRAB v1: a scene that died without EndScene cannot hold the mouth
        if (BellyBite.SceneOn) { o = BellyBite.SceneOpen; k = BellyBite.SceneClench; }   // BELLYGRAB v1: the scene keys (belly_bite.json scene.mouthKeys)
        smr.SetBlendShapeWeight(iOpen, 100f * o * c.mouth.openFull); if (iClench >= 0) smr.SetBlendShapeWeight(iClench, 100f * k * c.mouth.clench);
        if ((o > 0f || k > 0f) && Time.unscaledTime >= lodHoldT) { lodHoldT = Time.unscaledTime + 1f; FighterLod.ForceHigh(smr); }   // BELLYLOOK v1: the LOD1 proxy has no mouth morph, so hold the full mesh while the mouth moves (no-op while FighterLod is off)
    }
    void Zero() { if (iOpen >= 0) smr.SetBlendShapeWeight(iOpen, 0f); if (iClench >= 0) smr.SetBlendShapeWeight(iClench, 0f); }
}
