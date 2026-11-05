using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

// V66HUD: -looptest. Drives N consecutive REAL-TIME fights (default 3) through the same code the buttons use (GameLoop.Retry / GameLoop.Next) and asserts the loop is clean:
//   per fight start : HP / stamina / posture of both fighters back to full, timeScale 1, no finisher / hit-stop / slow-mo left over, CameraDeath weight 0, a sane lock camera, the fade is done,
//                     the fight counters moved the way the previous outcome says (Victory -> Next: FightNumber + 1, OpponentIndex + 1, Attempt 1; Defeat -> Retry: Attempt + 1), the Pitborn carries the ladder row's hp
//   per fight end   : the end screen reaches full alpha, a screenshot of it (loop_fN_victory.png / loop_fN_defeat.png), the stats line, no exception anywhere
// Dracula is driven like -autoplay / -balanceauto (his AIBrain in the playerBot profile + the flow bot). Fight 1 is natural; the next fights are forced (Pitborn or Dracula dies after -loopforce seconds)
// until BOTH screens have been seen, so a 3-fight run always shows a victory and a defeat screen. Run:
//   HadesSlice.exe -looptest [-loopfights:3] [-loopforce:12] [-loopmax:120] [-looptimeout:600] [-loopout:<dir>] -logFile looptest.log
// Result: <out>\looptest_result.txt (PASS / FAIL + one line per fight), exit code 0 / 2 (fail or exception) / 3 (timeout). Nothing is written outside <out>.
public class LoopTest : MonoBehaviour
{
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static float ArgF(string prefix, float def) { var s = Arg(prefix); float v; return s != null && float.TryParse(s, NumberStyles.Float, IC, out v) ? v : def; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-looptest")) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<LoopTest>() != null) return;
            var go = new GameObject("LoopTest"); DontDestroyOnLoad(go); go.AddComponent<LoopTest>();
        }
        catch (Exception e) { Debug.LogError("[looptest] boot failed: " + e); }
    }

    string outDir, logPath; int fights; float forceAfter, maxFight, timeout, startReal;
    bool exiting; int exitCode; float exitAt;
    readonly List<string> fails = new List<string>(), rows = new List<string>();
    int exceptions; bool seenWin, seenLose;
    float firstFadeAlpha = -1f;

    void Log(string s)
    {
        string line = "[looptest] " + s; Debug.Log(line);
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff", IC) + " " + s + "\n"); } catch { }
    }
    void Fail(string s) { fails.Add(s); Log("FAIL: " + s); }
    void Check(bool ok, string what) { if (!ok) Fail(what); }

    void OnEnable() { Application.logMessageReceived += OnLogMsg; }
    void OnDisable() { Application.logMessageReceived -= OnLogMsg; }
    void OnLogMsg(string msg, string stack, LogType t)
    {
        if (exiting) return;
        if (t == LogType.Exception) { exceptions++; Fail("EXCEPTION: " + msg + " | " + (stack ?? "").Replace("\n", " / ")); Exit(2, "exception"); }
        else if (t == LogType.Error && msg.StartsWith("[GameLoop]", StringComparison.Ordinal)) { Fail("game loop error: " + msg); }
    }

    void Start()
    {
        fights = Mathf.Max(1, (int)ArgF("-loopfights:", 3)); forceAfter = ArgF("-loopforce:", 12f); maxFight = ArgF("-loopmax:", 120f); timeout = ArgF("-looptimeout:", 600f);
        outDir = Arg("-loopout:") ?? Path.Combine(Application.dataPath, "..", "Shots", "looptest"); Directory.CreateDirectory(outDir); logPath = Path.Combine(outDir, "looptest.log");
        try { File.WriteAllText(logPath, ""); } catch { }
        startReal = Time.realtimeSinceStartup;
        Log("start: fights " + fights + ", force after " + forceAfter.ToString("F0", IC) + " s, max fight " + maxFight.ToString("F0", IC) + " s, timeout " + timeout.ToString("F0", IC) + " s, out " + outDir
            + ", development build " + Debug.isDebugBuild + ", " + Screen.width + "x" + Screen.height + ", HUD scale " + HudCfg.Scale().ToString("F2", IC));
        StartCoroutine(Main());
    }

    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - startReal > timeout) { Fail("timeout " + timeout.ToString("F0", IC) + " s"); Exit(3, "timeout"); }
        if (firstFadeAlpha < 0f && GameLoop.SceneLoads > 0) firstFadeAlpha = GameLoop.FadeAlpha;
    }

    void Exit(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = fails.Count > 0 && code == 0 ? 2 : code; exitAt = Time.unscaledTime;
        var sb = new StringBuilder();
        sb.AppendLine(fails.Count == 0 && exitCode == 0 ? "PASS" : "FAIL");
        sb.AppendLine("why: " + why + " | fights run " + rows.Count + " | exceptions " + exceptions + " | development build " + Debug.isDebugBuild);
        foreach (var r in rows) sb.AppendLine(r);
        foreach (var f in fails) sb.AppendLine("FAIL: " + f);
        try { File.WriteAllText(Path.Combine(outDir, "looptest_result.txt"), sb.ToString()); } catch { }
        Log("RESULT " + (fails.Count == 0 && exitCode == 0 ? "PASS" : "FAIL") + " (" + why + ") exit " + exitCode);
        Time.timeScale = 1f; Application.Quit(exitCode);
    }

    // ------------------------------------------------------------------ main flow
    IEnumerator Main()
    {
        int expectFight = 1, expectAttempt = 1, expectOpp = 0, expectStarted = 1, prevLoads = -1; float firstBase = -1f;
        GameLoop.Result lastOutcome = GameLoop.Result.None;
        for (int i = 0; i <= fights && !exiting; i++)
        {
            // ---- wait for the fight to be ready (fighters found, ladder applied, fade-in done)
            float w0 = Time.realtimeSinceStartup;
            while (!exiting && (!GameLoop.Ready || GameLoop.Player == null || GameLoop.Enemy == null || GameLoop.SceneLoads == prevLoads))
            {
                if (Time.realtimeSinceStartup - w0 > 60f) { Fail("fight " + (i + 1) + ": not ready after 60 s (ready " + GameLoop.Ready + ", loads " + GameLoop.SceneLoads + ")"); Exit(2, "not ready"); yield break; }
                yield return null;
            }
            if (exiting) yield break;
            prevLoads = GameLoop.SceneLoads;
            var D = GameLoop.Player; var P = GameLoop.Enemy;
            for (int k = 0; k < 20; k++) yield return null;
            string tag = "fight " + (i + 1) + (i == fights ? " (post-run check)" : "") + ": ";

            // ---- 1) clean start
            Check(Mathf.Abs(Time.timeScale - 1f) < 1e-3f, tag + "timeScale " + Time.timeScale.ToString("F3", IC));
            Check(!Duel.Frozen, tag + "Duel.Frozen left on"); Check(!RTFinisher.Busy, tag + "RTFinisher.Busy left on"); Check(!PresentationClock.Frozen, tag + "hit-stop left on"); Check(!CombatSlowMo.Active, tag + "slow-mo left on");
            Check(CameraDeath.Weight < 0.001f, tag + "CameraDeath weight " + CameraDeath.Weight.ToString("F3", IC));
            Check(!D.Dead && !P.Dead, tag + "a fighter starts dead");
            Check(D.health >= D.maxHealth - 0.01f && P.health >= P.maxHealth - 0.01f, tag + "HP not full (D " + D.health.ToString("F0", IC) + "/" + D.maxHealth.ToString("F0", IC) + ", P " + P.health.ToString("F0", IC) + "/" + P.maxHealth.ToString("F0", IC) + ")");
            Check(D.stamina >= D.maxStamina - 0.01f && P.stamina >= P.maxStamina - 0.01f, tag + "stamina not full (D " + D.stamina.ToString("F0", IC) + "/" + D.maxStamina.ToString("F0", IC) + ", P " + P.stamina.ToString("F0", IC) + "/" + P.maxStamina.ToString("F0", IC) + ")");
            Check(D.posture <= 0.01f && P.posture <= 0.01f, tag + "posture not reset (D " + D.posture.ToString("F1", IC) + ", P " + P.posture.ToString("F1", IC) + ")");
            Check(!D.PostureBroken && !P.PostureBroken && !D.Exhausted && !P.Exhausted, tag + "posture-broken / exhausted flag left over");
            Check(Duel.TickCount < 900, tag + "sim tick " + Duel.TickCount + " (not a fresh Duel)");
            Check(GameLoop.FadeAlpha < 0.05f, tag + "fade alpha " + GameLoop.FadeAlpha.ToString("F2", IC));
            Check(GameLoop.ScreenAlpha < 0.001f && GameLoop.Outcome == GameLoop.Result.None, tag + "end screen left over");
            Check(GameLoop.Landed == 0 && GameLoop.Taken == 0 && GameLoop.Parries == 0, tag + "stats not reset");
            { var hpD = D.GetComponent<Health>(); var hpP = P.GetComponent<Health>(); Check(hpD != null && hpP != null && hpD.current >= hpD.max - 0.01f && hpP.current >= hpP.max - 0.01f && !hpD.Dead && !hpP.Dead, tag + "Health components not full"); }
            CameraOk(tag, D);
            Check(GameLoop.FightNumber == expectFight && GameLoop.Attempt == expectAttempt && GameLoop.OpponentIndex == expectOpp, tag + "counters fight " + GameLoop.FightNumber + "/" + expectFight + " attempt " + GameLoop.Attempt + "/" + expectAttempt + " opponent " + GameLoop.OpponentIndex + "/" + expectOpp);
            Check(GameLoop.FightsStarted == expectStarted, tag + "FightsStarted " + GameLoop.FightsStarted + "/" + expectStarted);
            if (firstBase < 0f) firstBase = GameLoop.BaseEnemyHp; else Check(Mathf.Abs(GameLoop.BaseEnemyHp - firstBase) < 0.5f, tag + "scene base Pitborn hp changed (" + GameLoop.BaseEnemyHp.ToString("F0", IC) + " vs " + firstBase.ToString("F0", IC) + ")");
            if (GameLoop.LadderOn) { float hm, dm; var rw = GameLoop.RowAt(GameLoop.OpponentIndex, out hm, out dm); Check(Mathf.Abs(P.maxHealth - GameLoop.BaseEnemyHp * hm) < 0.6f, tag + "Pitborn hp " + P.maxHealth.ToString("F0", IC) + " != base x " + hm.ToString("F2", IC) + " (row " + rw.id + ")"); }
            Log(tag + "start OK-check done | fight " + GameLoop.FightNumber + " attempt " + GameLoop.Attempt + " opponent " + GameLoop.CurrentRow().id + " | hp D" + D.maxHealth.ToString("F0", IC) + " P" + P.maxHealth.ToString("F0", IC) + " | dmg x" + GameLoop.AppliedDmgMul.ToString("F2", IC) + " | ai " + GameLoop.AppliedDifficulty + " | first fade alpha " + firstFadeAlpha.ToString("F2", IC));
            if (i == fights) break;

            // ---- 2) the fight
            ForceAutoplay(D, P, 1000 + i);
            for (int k = 0; k < 10; k++) yield return null;
            float t0 = Time.realtimeSinceStartup; bool forced = false, hudShot = false, dmgShot = false, hitShot = false; string forcedWhat = "";
            int lastTaken = 0, lastLanded = 0; float minTs = 1f;
            while (!exiting && D != null && P != null && !(D.Dead || P.Dead))
            {
                float t = Time.realtimeSinceStartup - t0; minTs = Mathf.Min(minTs, Time.timeScale);
                if (!hudShot && i == 0 && t > 6f) { hudShot = true; yield return Shot("loop_f1_hud"); }
                if (!dmgShot && i == 0 && GameLoop.Taken > lastTaken) { dmgShot = true; yield return new WaitForSecondsRealtime(0.07f); yield return Shot("loop_f1_damage_vignette"); }
                if (!hitShot && i == 0 && GameLoop.Landed > lastLanded) { hitShot = true; yield return new WaitForSecondsRealtime(0.03f); yield return Shot("loop_f1_hit_tick"); }
                bool wantForce = (i > 0 && t > forceAfter && (!seenWin || !seenLose)) || t > maxFight;
                if (wantForce && !forced)
                {
                    forced = true;
                    if (!seenWin) { forcedWhat = "Pitborn"; P.Die(); } else if (!seenLose) { forcedWhat = "Dracula"; D.Die(); } else { forcedWhat = "Pitborn (time limit)"; P.Die(); }
                    Log(tag + "forced outcome: " + forcedWhat + " killed at " + t.ToString("F1", IC) + " s");
                }
                yield return null;
            }
            if (exiting) yield break;
            float dur = Time.realtimeSinceStartup - t0;

            // ---- 3) the end screen
            float s0 = Time.realtimeSinceStartup;
            while (!exiting && GameLoop.ScreenAlpha < 0.99f) { if (Time.realtimeSinceStartup - s0 > 25f) { Fail(tag + "end screen never reached full alpha (" + GameLoop.ScreenAlpha.ToString("F2", IC) + ")"); break; } yield return null; }
            for (int k = 0; k < 5; k++) yield return null;
            var res = GameLoop.Outcome; string resName = res == GameLoop.Result.Victory ? "victory" : res == GameLoop.Result.Defeat ? "defeat" : "none";
            Check(res != GameLoop.Result.None, tag + "no outcome");
            Check((P.Dead && !D.Dead) ? res == GameLoop.Result.Victory : (D.Dead ? res == GameLoop.Result.Defeat : true), tag + "outcome " + resName + " does not match who died");
            Check(GameLoop.Seconds > 0.5f, tag + "stats time " + GameLoop.Seconds.ToString("F1", IC));
            yield return Shot("loop_f" + (i + 1) + "_" + resName);
            string stats = GameLoop.StatsLine();
            Check(Mathf.Abs(Time.timeScale - 1f) < 0.02f, tag + "timeScale after the end " + Time.timeScale.ToString("F3", IC) + " (the end screen is full, the slow-mo must be over)");
            rows.Add("fight " + (i + 1) + " | " + resName + (forced ? " (forced: " + forcedWhat + ")" : " (natural)") + " | " + dur.ToString("F1", IC) + " s real | row " + GameLoop.CurrentRow().id + " | fightNo " + GameLoop.FightNumber + " attempt " + GameLoop.Attempt + " | " + stats.Replace("     ", " | ") + " | min timeScale " + minTs.ToString("F2", IC));
            Log(rows[rows.Count - 1]);
            if (res == GameLoop.Result.Victory) seenWin = true; else if (res == GameLoop.Result.Defeat) seenLose = true;
            lastOutcome = res;

            // ---- 4) the transition: exactly what the buttons call
            expectStarted++;
            if (res == GameLoop.Result.Victory) { expectFight++; expectOpp++; expectAttempt = 1; GameLoop.Next(); }
            else { expectAttempt++; GameLoop.Retry(); }
            float tr0 = Time.realtimeSinceStartup; float maxFade = 0f;
            while (!exiting && GameLoop.SceneLoads == prevLoads) { maxFade = Mathf.Max(maxFade, GameLoop.FadeAlpha); if (Time.realtimeSinceStartup - tr0 > 30f) { Fail(tag + "scene did not reload within 30 s"); Exit(2, "no reload"); yield break; } yield return null; }
            Check(!GameLoop.FadeOn || maxFade > 0.95f, tag + "fade-out never reached black (max " + maxFade.ToString("F2", IC) + ")");
        }
        if (exiting) yield break;
        Check(seenWin || seenLose, "no end screen was ever captured");
        if (fights >= 2) Check(seenWin && seenLose, "victory screen seen " + seenWin + ", defeat screen seen " + seenLose);
        Check(exceptions == 0, "exceptions: " + exceptions);
        Check(GameLoop.FightsStarted == fights + 1, "FightsStarted " + GameLoop.FightsStarted + " != " + (fights + 1));
        Check(lastOutcome != GameLoop.Result.None, "no outcome recorded");
        Exit(0, fights + " fights + post-run check");
    }

    void CameraOk(string tag, DuelFighter D)
    {
        var cam = Camera.main; Check(cam != null, tag + "no main camera"); if (cam == null) return;
        var rig = FindFirstObjectByType<CameraRig>(); Check(rig != null && rig.enabled, tag + "camera rig missing / disabled");
        var p = cam.transform.position; Check(!float.IsNaN(p.x + p.y + p.z) && !float.IsInfinity(p.x + p.y + p.z), tag + "camera position NaN");
        Check(cam.fieldOfView > 10f && cam.fieldOfView < 100f, tag + "camera fov " + cam.fieldOfView.ToString("F1", IC));
        float dist = Vector3.Distance(p, D.transform.position); Check(dist < 40f, tag + "camera " + dist.ToString("F1", IC) + " m from Dracula");
        var sp = cam.WorldToViewportPoint(D.transform.position + Vector3.up); Check(sp.z > 0f && sp.x > -0.1f && sp.x < 1.1f, tag + "Dracula not in front of the camera (viewport " + sp.x.ToString("F2", IC) + ", z " + sp.z.ToString("F1", IC) + ")");
    }

    // Dracula as in -autoplay / -balanceauto: PlayerBrain off, AIBrain on with the private "auto" flag (playerBot profile), flow bot on, lock range wide; both brains re-seeded
    void ForceAutoplay(DuelFighter D, DuelFighter P, int seed)
    {
        try
        {
            AIBrain.GlobalSeed = seed; UnityEngine.Random.InitState(seed);
            var fld = typeof(AIBrain).GetField("auto", BindingFlags.NonPublic | BindingFlags.Instance);
            var pb = D.GetComponent<PlayerBrain>(); if (pb != null) pb.enabled = false;
            D.moveDir = Vector3.zero; D.guardHeld = false;
            var dBrain = D.GetComponent<AIBrain>(); if (dBrain == null) dBrain = D.gameObject.AddComponent<AIBrain>();
            if (fld != null) fld.SetValue(dBrain, true); else Fail("AIBrain.auto field not found");
            var pBrain = P.GetComponent<AIBrain>();
            dBrain.enabled = false; dBrain.enabled = true;
            if (pBrain != null) { pBrain.enabled = false; pBrain.enabled = true; }
            if (D.opponent == null) D.opponent = P; if (P.opponent == null) P.opponent = D;
            var rig = FindAnyObjectByType<CameraRig>(); if (rig != null) { rig.lockRange = 80f; rig.enabled = true; }
            var fc = FlowTable.Get(); bool on = fc.enabled && fc.applyToBot;
            D.flowBotOn = on; P.flowBotOn = false;
            var fb = D.GetComponent<FlowBot>();
            if (on) { if (fb == null) fb = D.gameObject.AddComponent<FlowBot>(); fb.enabled = true; fb.Seed(); } else if (fb != null) fb.enabled = false;
            Time.timeScale = 1f;
        }
        catch (Exception e) { Fail("ForceAutoplay: " + e); }
    }

    IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        try
        {
            CaptureUtil.HideConsole(); var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex != null)
            {
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
                var px = tex.GetPixels32(); long sum = 0; int step = Mathf.Max(1, px.Length / 4000); int n = 0; for (int q = 0; q < px.Length; q += step) { sum += px[q].r + px[q].g + px[q].b; n++; }
                bool black = n > 0 && sum / (double)n < 6.0; Destroy(tex);
                Log("  SHOT " + name + ".png" + (black ? " (BLACK)" : "")); if (black) Fail("screenshot " + name + " is black");
            }
            else Fail("screenshot " + name + ": null texture");
        }
        catch (Exception e) { Fail("screenshot " + name + ": " + e.Message); }
    }
}
