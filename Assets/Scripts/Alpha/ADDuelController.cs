using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// ALPHA bootstrap + round loop (lane A, hardened by A2). Created automatically ONLY when the command line has -alpha / -alphaauto or runtime_flags.json has "alpha": true
// (default off: the real-time build is untouched).
//
// Startup order (nothing here depends on script execution order):
//   1. Boot()  [RuntimeInitializeOnLoadMethod AfterSceneLoad]  creates this object (DontDestroyOnLoad). Other AfterSceneLoad bootstraps (ArenaLimits, FootGround, FootYaw, HeadClamp,
//      GuardLayer, BloodEdge, ClashFx ...) run independently and are untouched.
//   2. Awake  loads the rules (ADData.LoadRules never returns null: a missing / broken json block falls back to code defaults).
//   3. Start  switches the real-time layer off (see DisableRealtimeLayer) and adds ADPlayback + ADUI. ADCameraRig is created on the camera by ADPlayback.Begin.
//   4. Setup  coroutine waits up to rules.bootWaitSeconds (10 s) for the two fighters (retry every 0.25 s, one log line per second), then ADPlayback.Begin(), then Rules.StartDuel().
//
// What the alpha switches OFF (restored by ExitAlpha): PlayerBrain, AIBrain, TouchControls, GameHUD (real-time HUD), CameraRig (by ADCameraRig.Begin/End), AutoShot when -autoplay is also given.
// What stays ON: Duel (the 60 Hz tick). DuelFighter.Tick (state machine, animation, movement, Press/guardHeld) only runs from Duel.Update, and ADPlayback needs it to play the clips.
//   Duel.Resolve (hit decisions) never fires because ADPlayback sets DuelFighter.opponent = null, so the dice in ADRules decide and the playback applies the outcome by hand.
// Lifecycle: Plan (timer) -> Resolve (beats) -> Replay -> Result -> next Plan ... -> kill -> (finisher if enabled) -> end screen -> 'Next opponent' -> Restart():
//   revive in place (ADPlayback.Revive: health, state, collider, animator, positions) or, when a ragdoll was built / the animator is gone, SceneManager.LoadScene with the alpha flag kept in a static.
// -alphaauto: headless proof, see ADAutoRun (log file, screenshots, exit codes 0 / 2 / 3).
// Hooks (all optional): Rules, PitbornPlanProvider, BeatPlayer, FinisherPlayer, ReplayPlayer, OnRestart, OnRulesCreated.
public class ADDuelController : MonoBehaviour
{
    public static ADDuelController Instance;
    public static ADRules Rules;
    public static Func<ADRules, ADPlan> PitbornPlanProvider;
    public static Func<ADBeat, IEnumerator> BeatPlayer, FinisherPlayer;
    public static Func<ADRoundResult, IEnumerator> ReplayPlayer;
    public static event Action OnRestart;
    public static event Action<ADRules> OnRulesCreated;
    public static bool ForceEnable;          // survives a scene reload (static): alpha stays on after SceneManager.LoadScene

    [Serializable] class FlagProbe { public bool alpha; }

    static bool HasArg(string a) { return Array.IndexOf(Environment.GetCommandLineArgs(), a) >= 0; }

    public static bool Enabled()
    {
        if (ForceEnable || HasArg("-alpha") || HasArg("-alphaauto")) return true;
        try
        {
            string p = Application.streamingAssetsPath + "/runtime_flags.json";
            if (System.IO.File.Exists(p)) return JsonUtility.FromJson<FlagProbe>(System.IO.File.ReadAllText(p)).alpha;
        }
        catch (Exception e) { Debug.LogWarning("[alpha] runtime_flags.json unreadable: " + e.Message); }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Instance = null; Rules = null; PitbornPlanProvider = null; BeatPlayer = null; FinisherPlayer = null; ReplayPlayer = null; OnRestart = null; OnRulesCreated = null; ForceEnable = false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (Instance != null || !Enabled()) return;
            var go = new GameObject("ADDuelController");
            DontDestroyOnLoad(go);
            go.AddComponent<ADDuelController>();
        }
        catch (Exception e) { Debug.LogError("[alpha] boot failed: " + e); }
    }

    ADRng pick;
    ADPitbornAI ai;
    ADUI ui;
    ADAutoRun auto;
    int fights, fightsDone, errors, submittedRound = -1;
    bool running, autoDracula, reloading, aborted;
    float disableUntil;
    readonly List<Behaviour> disabledByAlpha = new List<Behaviour>();

    void Awake()
    {
        Instance = this;
        try
        {
            var data = ADData.LoadRules();                                   // never null
            Rules = new ADRules(data);
            var c = data.rules;
            if (c.enableWounds) Rules.Wounds = new ADWounds();               // lane B (flag, default off): location weights, severity; else ADBasicWounds without effects
            pick = new ADRng(data.seed ^ 0x5151);
            ai = new ADPitbornAI(ADSpecialDefs.Ai, data.seed ^ 0xA1);        // lane E weighted-random AI
            if (PitbornPlanProvider == null) PitbornPlanProvider = AiPlan;
            autoDracula = HasArg("-alphaauto") || c.autoPlayDracula;
            if (HasArg("-alphaselftest")) ADRulesSelfTest.Run();
            if (HasArg("-alphaauto")) { auto = ADAutoRun.Create(gameObject, c.alphaTimeoutSeconds); auto.Hook(Rules); }
            Debug.Log("[alpha] controller up. flags wounds=" + c.enableWounds + " gore=" + c.enableGore + " critCam=" + c.enableCritCam + " specials=" + c.enableSpecials + " finishers=" + c.enableFinishers + (autoDracula ? " AUTO" : ""));
            if (OnRulesCreated != null) OnRulesCreated(Rules);
        }
        catch (Exception e) { Debug.LogError("[alpha] Awake failed, alpha off: " + e); Rules = null; enabled = false; if (auto != null) auto.Fail(e, "Awake"); }
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; if (Instance == this) { Instance = null; Rules = null; } }

    void Start()
    {
        if (Rules == null) return;
        try
        {
            DisableRealtimeLayer();
            disableUntil = Time.unscaledTime + 3f;       // brains / HUD may be (re)enabled by scene setup for a moment; keep them off
            if (FindFirstObjectByType<ADPlayback>() == null) gameObject.AddComponent<ADPlayback>();
            ui = ADUI.Instance != null ? ADUI.Instance : FindFirstObjectByType<ADUI>();
            if (ui == null) ui = gameObject.AddComponent<ADUI>();
            ui.OnPlanConfirmed += plan => { try { SubmitDracula(plan); } catch (Exception e) { ReportError(e, "plan confirm"); } };
            ui.OnNextOpponent += Restart;
            ui.OnRestart += Restart;
            Rules.OnPlan += OnPlan;
        }
        catch (Exception e) { ReportError(e, "Start"); }
        StartCoroutine(Safe(() => Setup(), "setup"));
    }

    // ---------------- real-time layer off / on ----------------
    void DisableRealtimeLayer()
    {
        Off<PlayerBrain>(); Off<AIBrain>(); Off<TouchControls>();
        Off<GameHUD>();                                                       // the old health bars / help text / Retry button (ADUI replaces them)
        if (HasArg("-autoplay")) { Debug.LogWarning("[alpha] -autoplay must not run together with -alpha: AutoShot disabled"); Off<AutoShot>(); }
        // Duel and the fighters' Fighter / ArenaLimits / FootGround ... stay on: DuelFighter.Tick only runs from Duel.Update and ADPlayback plays the clips through it.
    }
    void Off<T>() where T : Behaviour
    {
        foreach (var b in FindObjectsByType<T>(FindObjectsSortMode.None))
        {
            if (b == null || !b.enabled) continue;
            b.enabled = false; if (!disabledByAlpha.Contains(b)) disabledByAlpha.Add(b);
        }
    }

    // Leaves alpha mode: playback / camera rig released, the real-time layer restored. (Not called by the normal flow; for a menu or tests.)
    public static void ExitAlpha()
    {
        var i = Instance; if (i == null) return;
        try { var pb = ADPlayback.Instance; if (pb != null) pb.End(); } catch (Exception e) { Debug.LogWarning("[alpha] ExitAlpha playback: " + e.Message); }
        foreach (var b in i.disabledByAlpha) if (b != null) b.enabled = true;
        i.disabledByAlpha.Clear();
        ADTime.Restore();
        Destroy(i.gameObject);
    }

    // ---------------- error handling ----------------
    void ReportError(Exception e, string where)
    {
        errors++;
        if (auto != null) { auto.Fail(e, where); return; }
        Debug.LogError("[alpha] exception in " + where + " (#" + errors + "), continuing: " + e);
    }

    // Runs a coroutine step by step; an exception is reported (and, in -alphaauto, ends the run with exit code 2) instead of killing the loop silently.
    IEnumerator Safe(Func<IEnumerator> make, string where, Action onError = null)
    {
        IEnumerator e = null;
        try { e = make(); } catch (Exception ex) { ReportError(ex, where); if (onError != null) onError(); yield break; }
        if (e == null) yield break;
        while (true)
        {
            object cur;
            try { if (!e.MoveNext()) yield break; cur = e.Current; }
            catch (Exception ex) { ReportError(ex, where); if (onError != null) onError(); yield break; }
            yield return cur;
        }
    }

    // ---------------- bootstrap ----------------
    static bool IsPit(DuelFighter df) { return df.name.Contains("Pitborn") || (df.transform.root != null && df.transform.root.name.Contains("Pitborn")); }

    // True when both fighters are present and usable. Logs what is missing.
    bool FindFighters(out DuelFighter dr, out DuelFighter pit, out string why)
    {
        dr = pit = null; why = "";
        foreach (var df in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (df == null) continue;
            if (IsPit(df)) { if (pit == null) pit = df; } else if (df.name.Contains("Dracula") || df.transform.root.name.Contains("Dracula") || dr == null) { dr = df; }
        }
        if (dr == null) why += "no Dracula DuelFighter; ";
        if (pit == null) why += "no Pitborn DuelFighter; ";
        foreach (var f in new[] { dr, pit })
        {
            if (f == null) continue;
            if (f.GetComponent<Health>() == null) why += f.name + " has no Health; ";
            if (f.GetComponent<CharacterController>() == null) why += f.name + " has no CharacterController; ";
            var fi = f.GetComponent<Fighter>(); if (fi == null || fi.anim == null) why += f.name + " has no Fighter/Animator yet; ";
        }
        return why.Length == 0;
    }

    IEnumerator Setup()
    {
        float wait = Rules.Cfg.bootWaitSeconds > 0f ? Rules.Cfg.bootWaitSeconds : 10f, t0 = Time.realtimeSinceStartup, lastLog = -9f;
        yield return null; yield return null;
        DuelFighter dr, pit; string why;
        while (!FindFighters(out dr, out pit, out why))
        {
            float el = Time.realtimeSinceStartup - t0;
            if (el - lastLog >= 1f) { lastLog = el; Debug.Log("[alpha] waiting for the fighters (" + el.ToString("F1") + "/" + wait + " s): " + why); }
            if (el > wait)
            {
                Debug.LogError("[alpha] fighters not found after " + wait + " s: " + why + " - alpha mode aborted, real-time layer restored");
                if (auto != null) auto.Fail("fighters not found: " + why);
                Abort();
                yield break;
            }
            DisableRealtimeLayer();
            yield return new WaitForSecondsRealtime(0.25f);
        }
        Debug.Log("[alpha] fighters found: Dracula '" + dr.name + "' Pitborn '" + pit.name + "' after " + (Time.realtimeSinceStartup - t0).ToString("F1") + " s");
        DisableRealtimeLayer();
        var pb = ADPlayback.Instance != null ? ADPlayback.Instance : FindFirstObjectByType<ADPlayback>();
        if (pb == null) { pb = gameObject.AddComponent<ADPlayback>(); }
        if (!pb.Ready && !pb.Begin())
        {
            Debug.LogError("[alpha] ADPlayback.Begin failed (fighters / camera): alpha aborted");
            if (auto != null) auto.Fail("ADPlayback.Begin failed");
            Abort(); yield break;
        }
        if (Rules.Cfg.enableGore)
        {
            foreach (var df in new[] { dr, pit })
            {
                try { ADWounds.Bind(IsPit(df) ? ADSide.Pitborn : ADSide.Dracula, ADWoundsRuntime.For(df.gameObject)); }
                catch (Exception e) { Debug.LogWarning("[alpha] gore bind failed for " + df.name + ": " + e.Message); }
            }
        }
        yield return new WaitForSecondsRealtime(0.3f);                      // let the camera cut and the first UI frame settle
        Rules.StartDuel();
        Debug.Log("[alpha] duel started, hp " + Rules.Dracula.hp + " vs " + Rules.Pitborn.hp + (autoDracula ? " (auto)" : ""));
    }

    void Abort()
    {
        aborted = true;
        foreach (var b in disabledByAlpha) if (b != null) b.enabled = true;
        disabledByAlpha.Clear();
        try { var pb = ADPlayback.Instance; if (pb != null) pb.End(); } catch { }
        Rules = null;
    }

    void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        if (!reloading || Rules == null) return;
        reloading = false; disabledByAlpha.Clear();
        Debug.Log("[alpha] scene reloaded, re-running setup");
        StartCoroutine(Safe(() => ReSetup(), "re-setup"));
    }
    IEnumerator ReSetup()
    {
        DisableRealtimeLayer(); disableUntil = Time.unscaledTime + 3f;
        yield return Setup();
    }

    // ---------------- per frame ----------------
    void Update()
    {
        if (Rules == null || aborted) return;
        try
        {
            if (Time.unscaledTime < disableUntil) DisableRealtimeLayer();
            if (reloading) return;
            Rules.Tick(Time.unscaledDeltaTime);          // plan timer runs in real time even during slow-mo
            if (Rules.Phase == ADPhase.Resolve && !running) StartCoroutine(Safe(() => RunRound(), "round", Recover));
        }
        catch (Exception e) { ReportError(e, "Update"); }
    }

    // The only way Dracula's plan reaches the rules: one submit per round (round id), only in the Plan phase. The UI confirm, the UI timeout, the auto-confirm
    // and ADRules.Tick's own timeout can all fire in the same frame; the first one wins, the others are ignored. A null plan (UI timeout with an incomplete queue) is never submitted:
    // ADRules.Tick repeats the last plan.
    bool SubmitDracula(ADPlan plan)
    {
        if (plan == null || Rules == null || Rules.Phase != ADPhase.Plan) return false;
        if (submittedRound == Rules.Round || Rules.HasSubmitted(ADSide.Dracula)) return false;
        submittedRound = Rules.Round;
        Rules.Submit(ADSide.Dracula, plan);
        return true;
    }

    void OnPlan(int round)
    {
        Rules.PlanTimeLeft += 0.4f;                  // the UI timer (same length) expires first, so a full queue is confirmed instead of dropped by ADRules.Tick
        try
        {
            if (ui != null) { ui.SetFighter(Rules.Dracula); ui.SetFighter(Rules.Pitborn); ui.BeginRound(round, Rules.Cfg.planSeconds); }
        }
        catch (Exception e) { ReportError(e, "ui BeginRound"); }
        ADPlan plan = null;
        try { if (PitbornPlanProvider != null) plan = PitbornPlanProvider(Rules); }
        catch (Exception e) { Debug.LogWarning("[alpha] Pitborn AI failed, random plan used: " + e.Message); }
        if (plan == null || plan.moves == null) plan = RandomPlan();
        Rules.Submit(ADSide.Pitborn, plan);
        if (autoDracula) StartCoroutine(Safe(() => AutoConfirm(round), "auto confirm"));
    }

    IEnumerator AutoConfirm(int round)
    {
        yield return new WaitForSecondsRealtime(Rules.Cfg.autoConfirmSeconds);
        if (Rules != null && Rules.Round == round && Rules.Phase == ADPhase.Plan && !Rules.HasSubmitted(ADSide.Dracula))
        {
            if (auto != null) auto.Shot("r" + round + "_plan");
            yield return null;                                   // let the screenshot grab the plan screen before the plan resolves
            if (Rules != null && Rules.Round == round && Rules.Phase == ADPhase.Plan && !Rules.HasSubmitted(ADSide.Dracula)) SubmitDracula(RandomPlan());
        }
    }

    // Lane E adapter: ADPitbornAI speaks AiStance/AiMove (same names, own enums); it needs hp and whether the Special is spent.
    ADPlan AiPlan(ADRules r)
    {
        var st = new ADAiState { hp = r.Pitborn.hp, hpMax = r.Pitborn.hpMax, specialUsed = r.Pitborn.specialUsed || !r.Cfg.enableSpecials };
        var ap = ai.ChoosePlan(st, r.Round, r.Cfg.aiDifficulty);
        var p = new ADPlan { stance = (ADStance)Enum.Parse(typeof(ADStance), ap.stance.ToString()), moves = new ADMove[r.Cfg.beats] };
        for (int i = 0; i < p.moves.Length; i++) p.moves[i] = ap.moves != null && i < ap.moves.Length ? (ADMove)Enum.Parse(typeof(ADMove), ap.moves[i].ToString()) : ADMove.Guard;
        return p;
    }

    // Uniform random plan (Dracula in -alphaauto; fallback if the AI fails). Special is never drawn unless rules.enableSpecials.
    ADPlan RandomPlan()
    {
        int n = Rules != null && Rules.Cfg.enableSpecials ? 9 : 8;
        int beats = Rules != null ? Rules.Cfg.beats : 3;
        var p = new ADPlan { stance = (ADStance)Mathf.Min(3, (int)(pick.Next01() * 4)), moves = new ADMove[beats] };
        for (int i = 0; i < p.moves.Length; i++) p.moves[i] = (ADMove)Mathf.Min(n - 1, (int)(pick.Next01() * n));
        return p;
    }

    // ---------------- the round ----------------
    // After an error inside a round: put the sim back to a sane state and carry on with the next round.
    void Recover()
    {
        running = false;
        try { var pb = ADPlayback.Instance; if (pb != null) pb.CleanupPublic(); } catch { }
        try { ADTime.Restore(); } catch { }
        if (Rules == null || auto != null) return;
        try
        {
            if (Rules.Phase == ADPhase.Resolve) Rules.BeginReplay();
            if (Rules.Phase == ADPhase.Replay) Rules.EndRound();
            if (Rules.Over) { if (ui != null) ui.ShowEnd(Rules.Winner == ADSide.Dracula); }
            else if (Rules.Phase == ADPhase.Result) Rules.NextRound();
        }
        catch (Exception e) { Debug.LogError("[alpha] recover failed: " + e); }
    }

    IEnumerator RunRound()
    {
        running = true;
        var rr = Rules.LastResult;
        if (rr == null || rr.beats == null) { Debug.LogError("[alpha] resolve phase without a result"); Recover(); yield break; }
        // HP bars follow the beats: rebuild the HP before the round (the rules already applied every beat)
        int shownA = Rules.Dracula.hp, shownB = Rules.Pitborn.hp;
        foreach (var bt in rr.beats) { shownA += bt.dmgToA - bt.healA; shownB += bt.dmgToB - bt.healB; }
        if (ui != null) { ui.SetHp(ADSide.Dracula, shownA, Rules.Dracula.hpMax); ui.SetHp(ADSide.Pitborn, shownB, Rules.Pitborn.hpMax); ui.BeginResolve(); }
        foreach (var beat in rr.beats)
        {
            if (beat == null) continue;
            if (auto != null) auto.SetTag("r" + beat.round + "_b" + beat.beat);
            if (ui != null) ui.ShowBeat(beat);
            if (BeatPlayer != null) yield return StartCoroutine(Safe(() => BeatPlayer(beat), "beat " + beat.round + "." + beat.beat));
            else yield return new WaitForSecondsRealtime(Rules.Cfg.fallbackBeatSeconds);
            if (auto != null) auto.Shot("r" + beat.round + "_b" + beat.beat + "_end");
            shownA += -beat.dmgToA + beat.healA; shownB += -beat.dmgToB + beat.healB;
            if (ui != null) { ui.SetHp(ADSide.Dracula, shownA, Rules.Dracula.hpMax); ui.SetHp(ADSide.Pitborn, shownB, Rules.Pitborn.hpMax); }
            if (beat.killed)
            {
                if (FinisherPlayer != null && ADFlags.Finishers) yield return StartCoroutine(Safe(() => FinisherPlayer(beat), "finisher"));
                if (auto != null) { yield return new WaitForSecondsRealtime(0.6f); auto.Shot("kill"); yield return null; }
            }
        }
        if (ui != null) { ui.SetFighter(Rules.Dracula); ui.SetFighter(Rules.Pitborn); }
        Rules.BeginReplay();
        bool killRound = false; foreach (var kb in rr.beats) if (kb != null && kb.killed) killRound = true;
        if (!killRound)   // no replay strip after a kill: straight from the death to the end screen
        {
            if (ui != null) ui.ShowReplay();
            if (ReplayPlayer != null) yield return StartCoroutine(Safe(() => ReplayPlayer(rr), "replay"));
            else yield return new WaitForSecondsRealtime(Rules.Cfg.replaySeconds);
        }
        Rules.EndRound();
        if (!Rules.Over)
        {
            yield return new WaitForSecondsRealtime(Rules.Cfg.resultSeconds);
            Rules.NextRound();
            running = false;
            yield break;
        }
        Debug.Log("[alpha] duel over, winner " + Rules.Winner + ", rounds " + Rules.Round);
        if (ui != null) ui.ShowEnd(Rules.Winner == ADSide.Dracula);           // 'Next opponent' (win) / restart (loss) button -> Restart()
        running = false;
        if (auto != null) { StartCoroutine(Safe(() => AutoEnd(), "auto end")); yield break; }
        if (autoDracula && Rules.Cfg.autoRestartSeconds > 0f) { yield return new WaitForSecondsRealtime(Rules.Cfg.autoRestartSeconds); Restart(); }
    }

    // -alphaauto: end screen for autoEndScreenSeconds (shot at 1 s), then the next fight (-alphafights:n) or exit 0.
    IEnumerator AutoEnd()
    {
        float total = Mathf.Max(1.5f, Rules.Cfg.autoEndScreenSeconds);
        yield return new WaitForSecondsRealtime(1f);
        auto.Shot("end_screen");
        yield return new WaitForSecondsRealtime(total - 1f);
        fightsDone++;
        auto.Log("FIGHT " + fightsDone + "/" + ADAutoRun.FightsTarget + " done, winner " + Rules.Winner + ", rounds " + Rules.Round);
        if (fightsDone >= ADAutoRun.FightsTarget) { auto.Exit(0, "all fights finished, end screen shown " + total + " s"); yield break; }
        Restart();
    }

    // Win screen button ("Next opponent": the Pitborn again in the alpha) and the loss screen's restart.
    public static void Restart()
    {
        if (Rules == null || Instance == null) return;
        try { Instance.DoRestart(); } catch (Exception e) { Instance.ReportError(e, "Restart"); }
    }

    void DoRestart()
    {
        if (reloading) return;
        StopAllCoroutines();
        running = false; fights++;
        if (ai != null) ai.Reseed(Rules.Data.seed ^ 0xA1 ^ (fights * 7919));
        ADTime.Restore(); ADFinisherPlayer.ResetFight();
        var pb = ADPlayback.Instance;
        bool reload = pb == null || pb.NeedsReload();
        if (reload)
        {
            Debug.Log("[alpha] restart: a body cannot be revived in place, reloading the scene (alpha flag kept)");
            if (auto != null) auto.Log("restart via scene reload");
            ForceEnable = true; reloading = true;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            return;
        }
        if (auto != null) auto.Log("restart: revive in place");
        if (OnRestart != null) OnRestart();                                  // ADPlayback: revive both, positions, plan camera
        StartCoroutine(Safe(() => AfterRevive(), "restart"));
    }

    IEnumerator AfterRevive()
    {
        yield return new WaitForSecondsRealtime(Rules.Cfg.reviveWaitSeconds);
        Rules.StartDuel();
    }
}
