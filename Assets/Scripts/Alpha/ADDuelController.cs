using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ALPHA bootstrap + round loop. Created automatically ONLY when the command line has -alpha or runtime_flags.json has "alpha": true
// (default off: the real-time build is untouched). Disables PlayerBrain / AIBrain / TouchControls, owns the ADRules instance and drives
//   Plan (timer) -> Resolve (beats played by the playback hook) -> Replay -> Result -> next Plan, and Restart() after a kill.
// Lane hooks (all optional, set them from your own RuntimeInitializeOnLoad or Awake; the controller works alone with placeholders):
//   ADDuelController.Rules                  the live ADRules (UI submits Dracula's plan: Rules.Submit(ADSide.Dracula, plan); reads Phase, PlanTimeLeft, LastResult, fighters)
//   ADDuelController.PitbornPlanProvider   Func<ADRules, ADPlan> lane E; default = uniform random with seeded rng
//   ADDuelController.BeatPlayer            Func<ADBeat, IEnumerator> lane C; default = wait rules.fallbackBeatSeconds
//   ADDuelController.FinisherPlayer        Func<ADBeat, IEnumerator> lane C; run after the killing beat (instead of waiting)
//   ADDuelController.ReplayPlayer          Func<ADRoundResult, IEnumerator> lane D; default = wait rules.replaySeconds (UI draws the strip while Phase == Replay)
//   ADDuelController.OnRestart             event raised by Restart() so playback can reset health / positions / ragdolls
// Lane MonoBehaviours found by type name are added when they exist and are not already in the scene: ADCameraRig, ADUI, ADPlayback.
public class ADDuelController : MonoBehaviour
{
    public static ADDuelController Instance;
    public static ADRules Rules;
    public static Func<ADRules, ADPlan> PitbornPlanProvider;
    public static Func<ADBeat, IEnumerator> BeatPlayer, FinisherPlayer;
    public static Func<ADRoundResult, IEnumerator> ReplayPlayer;
    public static event Action OnRestart;
    public static event Action<ADRules> OnRulesCreated;

    [Serializable] class FlagProbe { public bool alpha; }
    static readonly string[] laneTypes = { "ADCameraRig", "ADPlayback", "ADUI" };

    public static bool Enabled()
    {
        foreach (var a in Environment.GetCommandLineArgs()) if (a == "-alpha") return true;
        try
        {
            string p = Application.streamingAssetsPath + "/runtime_flags.json";
            if (System.IO.File.Exists(p)) return JsonUtility.FromJson<FlagProbe>(System.IO.File.ReadAllText(p)).alpha;
        }
        catch (Exception e) { Debug.LogWarning("ADDuelController: runtime_flags.json unreadable: " + e.Message); }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null || !Enabled()) return;
        var go = new GameObject("ADDuelController");
        DontDestroyOnLoad(go);
        go.AddComponent<ADDuelController>();
    }

    ADRng pick;
    bool running, autoDracula;
    float disableUntil;

    void Awake()
    {
        Instance = this;
        var data = ADData.LoadRules();
        if (data == null) { Debug.LogError("ADDuelController: no rules data, alpha mode off"); enabled = false; return; }
        Rules = new ADRules(data);
        pick = new ADRng(data.seed ^ 0x5151);
        foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-alphaauto") autoDracula = true; if (a == "-alphaselftest") ADRulesSelfTest.Run(); }
        autoDracula |= data.rules.autoPlayDracula;
        if (OnRulesCreated != null) OnRulesCreated(Rules);
    }

    void Start()
    {
        DisableOldInput();
        disableUntil = Time.unscaledTime + 3f;       // brains may be (re)enabled by scene setup for a moment; keep them off
        foreach (var n in laneTypes)
        {
            var t = Type.GetType(n);
            if (t == null || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
            if (FindFirstObjectByType(t) == null) gameObject.AddComponent(t);
        }
        Rules.OnPlan += OnPlan;
        Rules.StartDuel();
        Debug.Log("[alpha] duel started, hp " + Rules.Dracula.hp + " vs " + Rules.Pitborn.hp);
    }

    void OnDestroy() { if (Instance == this) { Instance = null; Rules = null; } }

    static void DisableOldInput()
    {
        foreach (var b in FindObjectsByType<PlayerBrain>(FindObjectsSortMode.None)) b.enabled = false;
        foreach (var b in FindObjectsByType<AIBrain>(FindObjectsSortMode.None)) b.enabled = false;
        foreach (var b in FindObjectsByType<TouchControls>(FindObjectsSortMode.None)) b.enabled = false;
    }

    void Update()
    {
        if (Time.unscaledTime < disableUntil) DisableOldInput();
        if (Rules == null) return;
        Rules.Tick(Time.unscaledDeltaTime);          // plan timer runs in real time even during slow-mo
        if (Rules.Phase == ADPhase.Resolve && !running) StartCoroutine(RunRound());
    }

    void OnPlan(int round)
    {
        var plan = PitbornPlanProvider != null ? PitbornPlanProvider(Rules) : RandomPlan();
        Rules.Submit(ADSide.Pitborn, plan);
        if (autoDracula && Rules.Phase == ADPhase.Plan) Rules.Submit(ADSide.Dracula, RandomPlan());
    }

    // Placeholder AI (lane E replaces it through PitbornPlanProvider): uniform random stance and moves, Special at most once.
    ADPlan RandomPlan()
    {
        var p = new ADPlan { stance = (ADStance)(int)(pick.Next01() * 4), moves = new ADMove[Rules.Cfg.beats] };
        for (int i = 0; i < p.moves.Length; i++) p.moves[i] = (ADMove)(int)(pick.Next01() * 9);
        return p;
    }

    IEnumerator RunRound()
    {
        running = true;
        var rr = Rules.LastResult;
        foreach (var beat in rr.beats)
        {
            if (BeatPlayer != null) yield return StartCoroutine(BeatPlayer(beat));
            else yield return new WaitForSecondsRealtime(Rules.Cfg.fallbackBeatSeconds);
            if (beat.killed && FinisherPlayer != null) yield return StartCoroutine(FinisherPlayer(beat));
        }
        Rules.BeginReplay();
        if (ReplayPlayer != null) yield return StartCoroutine(ReplayPlayer(rr));
        else yield return new WaitForSecondsRealtime(Rules.Cfg.replaySeconds);
        Rules.EndRound();
        if (!Rules.Over)
        {
            yield return new WaitForSecondsRealtime(Rules.Cfg.resultSeconds);
            Rules.NextRound();
        }
        else Debug.Log("[alpha] duel over, winner " + Rules.Winner);
        running = false;
    }

    // Win screen button ("Next opponent": the Pitborn again in the alpha).
    public static void Restart()
    {
        if (Rules == null) return;
        if (Instance != null) Instance.StopAllCoroutines();
        if (Instance != null) Instance.running = false;
        if (OnRestart != null) OnRestart();
        Rules.StartDuel();
    }
}
