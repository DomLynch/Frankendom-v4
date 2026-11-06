using System;
using UnityEngine;

// Steer bootstrap. Exists only with -steer / -steerauto / runtime_flags.json "steer": true (otherwise the real-time game is untouched).
// What it does: finds the player + the enemy, switches the player's AIBrain ON (AIBrain.cs disables it without -autoplay) and PlayerBrain OFF, loads steer.json,
// wires the STSteer API, re-asserts the setup after a scene reload, tracks fight resets, and turns contact events into feed lines / log lines.
// Survives scene reloads (DontDestroyOnLoad). Nothing here touches rendering.
[DefaultExecutionOrder(500)]
public class STController : MonoBehaviour
{
    public static STController Instance;
    public STCfg Cfg;
    public STTactics Tactics = new STTactics();
    AIBrain brain; PlayerBrain pb;
    float nextCheck, pitDeadAt = -1f;
    bool pitWasDead;
    float lastHpD, lastHpP;
    string foeMoveForFeed = "";

    [Serializable] class FlagProbe { public bool steer; }
    static bool HasArg(string a) { return Array.IndexOf(Environment.GetCommandLineArgs(), a) >= 0; }
    public static bool Wanted()
    {
        if (HasArg("-steer") || HasArg("-steerauto")) return true;
        try
        {
            string p = Application.streamingAssetsPath + "/runtime_flags.json";
            if (System.IO.File.Exists(p)) return JsonUtility.FromJson<FlagProbe>(System.IO.File.ReadAllText(p)).steer;
        }
        catch (Exception e) { Debug.LogWarning("[steer] runtime_flags.json unreadable: " + e.Message); }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; STSteer.ResetStatics(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (Instance != null || !Wanted()) return;
            STSteer.Active = true; STSteer.AutoLog = HasArg("-steerauto");   // as early as possible: S2/S3/S4 bootstraps poll STSteer.Active
            var go = new GameObject("STController");
            DontDestroyOnLoad(go);
            go.AddComponent<STController>();
        }
        catch (Exception e) { Debug.LogError("[steer] boot failed: " + e); }
    }

    void Awake()
    {
        Instance = this;
        try
        {
            // no ResetStatics here: other lanes (S2/S3/S4) may already have subscribed to STSteer events from their own bootstraps
            STSteer.ReloadConfig();
            Cfg = STCfg.Load();
            STSteer.Active = true;
            STSteer.AutoLog = HasArg("-steerauto");
            STStanceMods.Cfg = Cfg; STStanceMods.Seed(Cfg.general.seed); Tactics.cfg = Cfg; Tactics.Seed(Cfg.general.seed);
            STStance def; if (!Enum.TryParse(Cfg.general.defaultStance, true, out def)) def = STStance.Neutral;
            STSteer.ResetStance(def);
            STSteer.TacticUser = t => Tactics.Start(t);
            STSteer.CooldownProvider = t => Tactics.Cooldown01(t);
            STSteer.EnabledProvider = t => Tactics.Enabled(t);
            STSteer.RunningProvider = () => Tactics.Running >= 0 ? (STTactic?)(STTactic)Tactics.Running : null;
            STSteer.TimeLeftProvider = t => Tactics.TimeLeft(t);
            STSteer.DamageMulProvider = STStanceMods.Mul;
            STSteer.PreBrainHandler = OnPreBrain;
            STSteer.OnStance += OnStance;
            ContactRelay.OnEvent += OnContact;
            ContactRelay.OnDodge += OnDodge;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            Debug.Log("[steer] controller up: stances=" + Cfg.stances.list.Length + " tactics=" + Cfg.tactics.list.Length + " seed=" + Cfg.general.seed + (STSteer.AutoLog ? " AUTOLOG " + STSteer.LogPath : ""));
        }
        catch (Exception e) { Debug.LogError("[steer] Awake failed, steer off: " + e); STSteer.Active = false; enabled = false; }
    }

    void OnDestroy()
    {
        ContactRelay.OnEvent -= OnContact; ContactRelay.OnDodge -= OnDodge; STSteer.OnStance -= OnStance;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) { Instance = null; STSteer.Active = false; }
    }

    void OnSceneLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) { brain = null; pb = null; nextCheck = 0f; NewFight("scene loaded"); }

    void NewFight(string why)
    {
        Tactics.Reset(); STStanceMods.Seed(Cfg.general.seed); pitWasDead = false; pitDeadAt = -1f;
        STSteer.RaiseFightReset();
        STSteer.Log("fight", why, "reset");
    }

    // ---- find fighters + keep the player AI-driven ----
    void Resolve()
    {
        var dr = STSteer.Player; var pi = STSteer.Enemy;
        if (dr == null) { var g = GameObject.Find(Roster.PlayerObject); if (g != null) dr = g.GetComponent<DuelFighter>(); }
        if (pi == null) { var g = GameObject.Find(Roster.EnemyObject); if (g != null) pi = g.GetComponent<DuelFighter>(); }
        STSteer.Player = dr; STSteer.Enemy = pi;
        Tactics.d = dr; Tactics.o = pi;
        if (dr == null) return;
        if (brain == null) { brain = dr.GetComponent<AIBrain>(); if (brain == null) brain = dr.gameObject.AddComponent<AIBrain>(); STAiBias.Bind(brain); Tactics.brain = brain; }
        if (brain != null && !brain.enabled) { brain.enabled = true; Debug.Log("[steer] " + Roster.PlayerName + " AIBrain enabled"); }
        if (Cfg.general.disablePlayerBrain)
        {
            if (pb == null) pb = dr.GetComponent<PlayerBrain>();
            if (pb != null && pb.enabled) { pb.enabled = false; dr.moveDir = Vector3.zero; dr.guardHeld = false; }
            var tc = dr.GetComponent<TouchControls>();
            if (tc != null && tc.enabled) tc.enabled = false;
            TouchControls.Active = false;
        }
        if (pi != null && dr.opponent == null) dr.opponent = pi;
    }

    void Update()
    {
        if (Cfg == null) return;
        if (Time.unscaledTime >= nextCheck || STSteer.Player == null || STSteer.Enemy == null || brain == null)
        {
            nextCheck = Time.unscaledTime + Mathf.Max(0.1f, Cfg.general.checkEverySeconds);
            Resolve();
        }
        var o = STSteer.Enemy;
        if (o != null)
        {
            // fight reset: the enemy dead for a while, then alive again (revive without a scene reload)
            if (o.Dead) { if (!pitWasDead) { pitWasDead = true; pitDeadAt = Time.unscaledTime; STSteer.Feed("The " + Roster.EnemyName + " falls"); STSteer.Log("fight", "", Roster.EnemyName.ToLowerInvariant() + " dead"); } }
            else if (pitWasDead && Time.unscaledTime - pitDeadAt >= Cfg.general.fightResetDeadSeconds) NewFight(Roster.EnemyName.ToLowerInvariant() + " revived");
        }
    }

    // ---- AIBrain pre-hook (every frame, before Decide) ----
    void OnPreBrain(AIBrain b)
    {
        if (b != brain || Cfg == null) return;
        STAiBias.Refresh(Cfg, Cfg.Stance(STSteer.Stance), Tactics.Def);
        if (STReactions.Instance != null) STReactions.Instance.ReapplyPlayer();
        Tactics.Pre();
    }

    void OnStance(STStance s)
    {
        STAiBias.Refresh(Cfg, Cfg.Stance(s), Tactics.Def);   // immediate; AIBrain reads the profile on its next decision
        STSteer.Log("stance-applied:" + s, Tactics.Describe(), "");
    }

    // ---- outcomes ----
    static string MoveName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "blow";
        if (id.StartsWith("heavy")) return "overhead"; if (id.StartsWith("light")) return "slash"; if (id == "thrust") return "thrust"; if (id == "kick") return "kick"; if (id.StartsWith("skill")) return "special";
        return id;
    }
    void OnContact(CombatEvent e)
    {
        var dr = STSteer.Player; if (dr == null || (e.actor != dr && e.target != dr)) return;
        bool byMe = e.actor == dr;
        float dmg = 0f;
        if (e.target != null)
        {
            float hp = e.target.health;
            if (e.target == dr) { dmg = Mathf.Max(0f, lastHpD - hp); lastHpD = hp; } else { dmg = Mathf.Max(0f, lastHpP - hp); lastHpP = hp; }
        }
        var T = Tactics;
        bool feed = Cfg.general.feedLines;
        switch (e.type)
        {
            case "Hit":
                if (byMe) { T.dmgDealt += dmg; T.hitsDealt++; } else { T.dmgTaken += dmg; T.hitsTaken++; }
                if (feed && Cfg.general.feedHits) STSteer.Feed(byMe ? "You hit him" : "He hits you");
                break;
            case "Parried":
                if (!byMe) { T.parries++; if (feed) STSteer.Feed("You parried his " + MoveName(e.moveId)); } else if (feed) STSteer.Feed("He parried your " + MoveName(e.moveId));
                break;
            case "Blocked":
                if (!byMe) T.blocks++;
                if (feed && e.heavy) STSteer.Feed(byMe ? "He blocked your " + MoveName(e.moveId) : "You blocked his " + MoveName(e.moveId));
                break;
            case "GuardBroken":
                if (feed) STSteer.Feed(byMe ? "You broke his guard" : "He broke your guard");
                break;
        }
        STSteer.Log("", Tactics.Describe(), e.type + (byMe ? " by " + Roster.PlayerName + " " : " by " + Roster.EnemyName + " ") + MoveName(e.moveId) + " dmg=" + dmg.ToString("0.0") + " mul=" + STStanceMods.LastMul.ToString("0.00") + (string.IsNullOrEmpty(STStanceMods.LastNote) ? "" : " (" + STStanceMods.LastNote + ")") + " hpD=" + (STSteer.Player != null ? STSteer.Player.health.ToString("0") : "?") + " hpP=" + (STSteer.Enemy != null ? STSteer.Enemy.health.ToString("0") : "?"));
        STStanceMods.LastNote = "";
    }
    void OnDodge(DuelFighter f, string kind, Vector3 dir)
    {
        if (f != STSteer.Player) return;
        Tactics.dodges++;
        if (Cfg.general.feedLines) STSteer.Feed("You " + (kind == "backstep" ? "step back" : kind == "sidestep" ? "sidestep" : "roll"));
        STSteer.Log("", Tactics.Describe(), "dodge " + kind);
    }

    void LateUpdate()
    {
        // keep health samples fresh for damage deltas
        if (STSteer.Player != null) { if (STSteer.Player.health > lastHpD) lastHpD = STSteer.Player.health; }
        if (STSteer.Enemy != null) { if (STSteer.Enemy.health > lastHpP) lastHpP = STSteer.Enemy.health; }
    }
}
