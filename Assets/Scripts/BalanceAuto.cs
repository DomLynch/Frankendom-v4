using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

// REAL-TIME BALANCE HARNESS (lane S4): -balanceauto [-seeds:N (default 5)] [-balancetimeout:<s> (default 900)] [-balanceseed:<n>] [-balanceout:<dir>] [-balancestrict] [-balancedata:<json>]
// N consecutive real-time fights: Dracula is driven by his AIBrain in the playerBot profile exactly like -autoplay (PlayerBrain off), the Pitborn by its AI.
// Between fights the scene is reloaded (the same call as GameHUD's Retry / Backspace); this object is DontDestroyOnLoad so the flags survive.
// No dependency on the Steer or Alpha code (both are optional and may be absent). Output in <exe dir> (or -balanceout):
//   balance_log.txt       one line per event, written immediately; HP/stamina/distance sample every cfg.sampleEvery (5 s)
//   balance_summary.txt   per fight: seed, duration, winner, damage dealt/taken by each side, hits by each side, moves by each side (light/slash/stab/heavy/kick/special), parries/blocks/dodges; then averages; VERDICT
//   balance_shots\balance_f<i>_start.png / _end.png  (only these, to save time)
// VERDICT PASS = average duration 60-120 s AND Dracula wins 40-70% AND both sides land hits in every fight AND the Pitborn uses >= 4 different moves (all thresholds in balance_auto.json).
// Exit codes: 0 finished (verdict in the file), 2 exception (stack in the log), 3 timeout.
// Seeding: AIBrain seeds its LCG from AiDial startSeed ^ hash(name) ^ AIBrain.GlobalSeed ^ seedOffset when it initialises (OnDisable resets it), so per fight the harness sets
// AIBrain.GlobalSeed = seed, then toggles both brains so they re-init with it. UnityEngine.Random.InitState(seed) is set as well (other systems). Timing (frame rate) is not seeded, so fights are not bit-reproducible.
[DefaultExecutionOrder(-70)]
public class BalanceAuto : MonoBehaviour
{
    [Serializable]
    public class Cfg
    {
        public int seed = 1234;
        public float maxFightSeconds = 180f, sampleEvery = 5f, endDelay = 2.5f, bootWaitSeconds = 20f, reloadWaitSeconds = 20f, timeoutSeconds = 900f;
        public int settleFrames = 30;
        public float startShotAt = 1f;
        public float minAvgDuration = 60f, maxAvgDuration = 120f, minWinRate = 0.4f, maxWinRate = 0.7f;
        public int minPitbornMoves = 4;
        public float maxRiposteShare = 0.3f, maxMoveShare = 0.4f;   // V63 WARN thresholds (do not change the verdict)
        public string shotMode = "auto";          // auto = game screen, camera render texture if black | screen | camera
        public string draculaName = "Dracula", pitbornName = "Pitborn";
    }

    // ------------------------------------------------------------ boot
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    public static BalanceAuto Instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-balanceauto")) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<BalanceAuto>() != null) return;
            var go = new GameObject("BalanceAuto");
            DontDestroyOnLoad(go);
            go.AddComponent<BalanceAuto>();
        }
        catch (Exception e) { Debug.LogError("[balance] boot failed: " + e); }
    }

    // ------------------------------------------------------------ state
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static readonly string[] Cats = { "light", "slash", "stab", "heavy", "kick", "special", "other" };
    Cfg C = new Cfg();
    string dir, logPath, shotDir, summaryPath, dataSrc = "(defaults)";
    float t0, fightStart = -1f, timeout, exitAt; bool exiting, started, sceneReloaded; int exitCode;
    int seedsN = 5, fightIdx; int baseSeed;
    DuelFighter D, P;
    float dHpPrev, pHpPrev, dHpStart, pHpStart, dStaminaMin = 999f;
    int tickStart;
    bool shotThisFrame;

    // exceptions / fps
    int myExceptions, foreignExceptions; readonly List<string> exceptionList = new List<string>(); bool strict;
    float fpsSum, fpsMin = 9999f; int fpsN;

    class Side
    {
        public float dmgDealt, dmgTaken; public int hits, parries, blocks, guardBreaks, dodges, attacks;
        public readonly int[] cat = new int[Cats.Length]; public readonly Dictionary<string, int> moves = new Dictionary<string, int>();
    }
    class Rec { public string dAdapt = "", pAdapt = ""; public int idx, seed; public float dur; public string winner, endReason; public Side d = new Side(), p = new Side(); public float dHp, pHp; public int[] dPost = new int[4], pPost = new int[4]; public float dGain, pGain; public float[] dF = new float[12], pF = new float[12]; public string dFlow = "", pFlow = ""; }   // V64P   // P7: posture counters {breaks suffered, deathblows dealt, deathblows taken, windows missed}
    Side sd = new Side(), sp = new Side();      // current fight: Dracula, Pitborn
    readonly List<Rec> recs = new List<Rec>();

    // ------------------------------------------------------------ lifecycle
    void Awake()
    {
        Instance = this;
        t0 = Time.realtimeSinceStartup;
        try
        {
            string o = Arg("-balanceout:");
            try { dir = !string.IsNullOrEmpty(o) ? o : Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)); } catch { dir = "."; }
            if (string.IsNullOrEmpty(dir)) dir = ".";
            try { Directory.CreateDirectory(dir); } catch { }
            logPath = Path.Combine(dir, "balance_log.txt"); shotDir = Path.Combine(dir, "balance_shots"); summaryPath = Path.Combine(dir, "balance_summary.txt");
            try { File.WriteAllText(logPath, ""); } catch (Exception e) { Debug.LogWarning("[balance] cannot write " + logPath + ": " + e.Message); }
            try { if (File.Exists(summaryPath)) File.Delete(summaryPath); } catch { }
            try { Directory.CreateDirectory(shotDir); } catch { }
            LoadCfg();
            int n; var s = Arg("-seeds:"); seedsN = s != null && int.TryParse(s, out n) && n > 0 ? n : 5;
            var bs = Arg("-balanceseed:"); if (bs != null && int.TryParse(bs, out n)) C.seed = n;
            baseSeed = C.seed;
            timeout = C.timeoutSeconds > 0f ? C.timeoutSeconds : 900f;
            float tv; var ts = Arg("-balancetimeout:");
            if (ts != null && float.TryParse(ts, System.Globalization.NumberStyles.Float, IC, out tv) && tv > 0f) timeout = tv;
            strict = Flag("-balancestrict");
            Application.logMessageReceived += OnUnityLog;
            ContactRelay.OnContact += OnContact; ContactRelay.OnDodge += OnDodge; ContactRelay.OnAttackStarted += OnAttack;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log("balanceauto start " + DateTime.Now.ToString("s") + " unity " + Application.unityVersion + " res " + Screen.width + "x" + Screen.height + " seeds " + seedsN + " baseSeed " + baseSeed
                + " timeout " + timeout.ToString("F0", IC) + "s maxFight " + C.maxFightSeconds.ToString("F0", IC) + "s data " + dataSrc + (Flag("-autoplay") ? " (-autoplay also given: AutoShot runs too, not needed)" : ""));
        }
        catch (Exception e) { Fail(e, "Awake"); }
    }

    void Start() { StartCoroutine(Guard(Main())); }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityLog; ContactRelay.OnContact -= OnContact; ContactRelay.OnDodge -= OnDodge; ContactRelay.OnAttackStarted -= OnAttack;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (Instance == this) Instance = null;
    }

    void OnSceneLoaded(Scene s, LoadSceneMode m) { sceneReloaded = true; }

    void LoadCfg()
    {
        var paths = new List<string>();
        string cli = Arg("-balancedata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "balance_auto.json"));
        paths.Add(Path.Combine(Application.dataPath, "Data/balance_auto.json"));
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart('﻿'));
                if (c != null) { C = c; dataSrc = p; return; }
            }
            catch (Exception ex) { Debug.LogWarning("[balance] cannot read " + p + ": " + ex.Message); dataSrc = p + " (unreadable: defaults)"; }
        }
    }

    IEnumerator Guard(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Fail(ex, "main loop"); yield break; }
            yield return cur;
        }
    }

    // ------------------------------------------------------------ logging / exit
    public void Log(string line)
    {
        string stamp = fightStart >= 0f ? "f" + (Time.realtimeSinceStartup - fightStart).ToString("F2", IC).PadLeft(7) : "b" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7);
        try { File.AppendAllText(logPath, stamp + "s " + line + "\n"); } catch { }
        Debug.Log("[balance] " + line);
    }

    void Fail(Exception e, string where) { myExceptions++; exceptionList.Add(where + ": " + e.GetType().Name + " " + e.Message); Log("EXCEPTION in " + where + ": " + e); Exit(2, "exception in " + where, "ERROR"); }
    void Fail(string msg) { myExceptions++; exceptionList.Add(msg); Log("ERROR: " + msg); Exit(2, msg, "ERROR"); }

    void Exit(int code, string why, string verdictOverride)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime;
        try { WriteSummary(code, why, verdictOverride); } catch (Exception e) { Log("summary failed: " + e); }
        Log("EXIT code " + code + " (" + why + ")" + (foreignExceptions > 0 ? " foreignExceptions=" + foreignExceptions : ""));
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    void OnUnityLog(string msg, string stack, LogType type)
    {
        if (type != LogType.Exception || exiting) return;
        bool mine = Regex.IsMatch(stack ?? "", @"BalanceAuto") || Regex.IsMatch(msg ?? "", @"BalanceAuto");
        string one = (msg ?? "") + " | " + FirstLine(stack);
        if (mine || strict) { myExceptions++; exceptionList.Add(one); Log("UNHANDLED EXCEPTION (balance): " + msg + "\n" + stack); Exit(2, "unhandled exception", "ERROR"); }
        else { foreignExceptions++; exceptionList.Add("(foreign) " + one); Log("exception outside the harness (counted, not fatal; -balancestrict makes it fatal): " + msg + "\n" + stack); }
    }
    static string FirstLine(string s) { if (string.IsNullOrEmpty(s)) return ""; int i = s.IndexOf('\n'); return i < 0 ? s : s.Substring(0, i); }

    void Update()
    {
        if (exiting)
        {
#if !UNITY_EDITOR
            if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode);   // Application.Quit(code) sometimes lingers
#endif
            return;
        }
        try
        {
            float dt = Time.unscaledDeltaTime;
            if (shotThisFrame) shotThisFrame = false;
            else if (dt > 0f && started) { float f = 1f / dt; fpsSum += f; fpsN++; if (f < fpsMin) fpsMin = f; }
            if (Time.realtimeSinceStartup - t0 > timeout)
            {
                Log("TIMEOUT after " + timeout.ToString("F0", IC) + "s (fight " + (fightIdx + 1) + "/" + seedsN + ")");
                Exit(3, "timeout", "TIMEOUT"); return;
            }
            if (started && D != null && P != null)
            {
                float dh = D.health, ph = P.health;
                if (dh < dHpPrev) { float x = dHpPrev - dh; sp.dmgDealt += x; sd.dmgTaken += x; }
                if (ph < pHpPrev) { float x = pHpPrev - ph; sd.dmgDealt += x; sp.dmgTaken += x; }
                dHpPrev = dh; pHpPrev = ph;
                if (D.stamina < dStaminaMin) dStaminaMin = D.stamina;
            }
        }
        catch (Exception e) { Fail(e, "Update"); }
    }

    // ------------------------------------------------------------ combat events
    static int CatOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return 6;
        string s = id.ToLowerInvariant();
        if (s.StartsWith("skill_") || s.StartsWith("special") || s == "critical") return 5;
        if (s.StartsWith("heavy")) return 3;
        if (s.StartsWith("slash")) return 1;
        if (s.StartsWith("thrust") || s.StartsWith("stab") || s == "riposte") return 2;
        if (s.StartsWith("light")) return 0;
        if (s.StartsWith("kick")) return 4;
        return 6;
    }

    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (!started || exiting || D == null || P == null) return;
        bool aD = a == D, aP = a == P; if (!aD && !aP) return;
        Side att = aD ? sd : sp, def = aD ? sp : sd;
        switch (type)
        {
            case "Hit": att.hits++; break;
            case "Blocked": def.blocks++; break;
            case "Parried": def.parries++; break;
            case "GuardBroken": def.guardBreaks++; break;
        }
        Log("  CONTACT " + (aD ? "D>P" : "P>D") + " " + type + (heavy ? " heavy" : "") + " move=" + (a.CurrentMove != null ? a.CurrentMove.id : "?") + " hp D" + D.health.ToString("F0", IC) + "/P" + P.health.ToString("F0", IC));
    }
    void OnDodge(DuelFighter f, string kind, Vector3 dir)
    {
        if (!started || exiting) return;
        if (f == D) sd.dodges++; else if (f == P) sp.dodges++;
    }
    void OnAttack(DuelFighter f, MoveDef m)
    {
        if (!started || exiting || m == null) return;
        Side s = f == D ? sd : f == P ? sp : null; if (s == null) return;
        s.attacks++; s.cat[CatOf(m.id)]++;
        int c; s.moves.TryGetValue(m.id, out c); s.moves[m.id] = c + 1;
    }

    // ------------------------------------------------------------ main flow
    bool FindFighters(out DuelFighter d, out DuelFighter p)
    {
        d = p = null;
        foreach (var f in FindObjectsByType<DuelFighter>()) { if (f.name == C.draculaName) d = f; else if (f.name == C.pitbornName) p = f; }
        return d != null && p != null;
    }

    IEnumerator Main()
    {
        // 1) the first scene: both fighters
        float w0 = Time.realtimeSinceStartup;
        while (!FindFighters(out D, out P))
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSeconds) { Fail("fighters not found (" + C.draculaName + "/" + C.pitbornName + ") after " + C.bootWaitSeconds.ToString("F0", IC) + " s"); yield break; }
            yield return null;
        }
        for (int fi = 0; fi < seedsN && !exiting; fi++)
        {
            fightIdx = fi;
            int seed = baseSeed + fi;
            if (fi > 0)
            {
                // the real-time game's own restart (GameHUD Retry / Backspace): reload the active scene; this object and the static seed survive
                Log("RESTART via SceneManager.LoadScene (as GameHUD Retry)");
                started = false; sceneReloaded = false; var oldD = D; var oldP = P;
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
                w0 = Time.realtimeSinceStartup;
                while (true)
                {
                    yield return null;
                    DuelFighter d, p;
                    if (sceneReloaded && FindFighters(out d, out p) && d != oldD && p != oldP) { D = d; P = p; break; }
                    if (Time.realtimeSinceStartup - w0 > C.reloadWaitSeconds) { Fail("scene reload: no fresh fighters after " + C.reloadWaitSeconds.ToString("F0", IC) + " s"); yield break; }
                }
            }
            for (int i = 0; i < C.settleFrames; i++) yield return null;   // Awake/Start of every system
            ForceAutoplay(seed);
            for (int i = 0; i < 10; i++) yield return null;

            // 2) fight
            sd = new Side(); sp = new Side(); dStaminaMin = 999f;
            dHpPrev = dHpStart = D.health; pHpPrev = pHpStart = P.health; tickStart = Duel.TickCount;
            fightStart = Time.realtimeSinceStartup; started = true;
            Log("FIGHT START " + (fi + 1) + "/" + seedsN + " seed " + seed + " hp D" + dHpStart.ToString("F0", IC) + "/P" + pHpStart.ToString("F0", IC) + " dist " + Dist().ToString("F2", IC));
            float nextSample = C.sampleEvery; bool startShot = false; string endReason = "";
            while (!exiting)
            {
                float t = Time.realtimeSinceStartup - fightStart;
                if (!startShot && t >= C.startShotAt) { startShot = true; yield return Shot("balance_f" + (fi + 1) + "_start"); }
                if (t >= nextSample) { nextSample += C.sampleEvery; Sample(t); }
                if (D == null || P == null) { Fail("a fighter object vanished during the fight"); yield break; }
                if (D.Dead || P.Dead) { endReason = (P.Dead ? "Pitborn" : "Dracula") + " died"; break; }
                if (t >= C.maxFightSeconds) { endReason = "time limit " + C.maxFightSeconds.ToString("F0", IC) + " s"; break; }
                yield return null;
            }
            if (exiting) yield break;
            float dur = Time.realtimeSinceStartup - fightStart;
            Log("FIGHT END " + (fi + 1) + "/" + seedsN + " (" + endReason + ") at " + dur.ToString("F1", IC) + " s");
            float until = Time.realtimeSinceStartup + (D.Dead || P.Dead ? C.endDelay : 0.2f);
            while (Time.realtimeSinceStartup < until && !exiting) yield return null;
            if (exiting) yield break;
            yield return Shot("balance_f" + (fi + 1) + "_end");
            started = false;
            AddRecord(seed, dur, endReason);
        }
        Exit(0, seedsN + " fights done", null);
    }

    // Dracula as in -autoplay: PlayerBrain off, AIBrain on with the private "auto" flag (playerBot profile), lock cam range 80; both brains re-seeded
    void ForceAutoplay(int seed)
    {
        AIBrain.GlobalSeed = seed;
        UnityEngine.Random.InitState(seed);
        var fld = typeof(AIBrain).GetField("auto", BindingFlags.NonPublic | BindingFlags.Instance);
        var pb = D.GetComponent<PlayerBrain>(); if (pb != null) pb.enabled = false;
        D.moveDir = Vector3.zero; D.guardHeld = false;
        var dBrain = D.GetComponent<AIBrain>(); if (dBrain == null) dBrain = D.gameObject.AddComponent<AIBrain>();
        if (fld != null) fld.SetValue(dBrain, true); else Log("WARNING: AIBrain.auto field not found; Dracula's AI may stay off");
        var pBrain = P.GetComponent<AIBrain>();
        // OnDisable resets the brain's init flag, so the toggle makes both re-read GlobalSeed
        dBrain.enabled = false; dBrain.enabled = true;
        if (pBrain != null) { pBrain.enabled = false; pBrain.enabled = true; }
        if (D.opponent == null) D.opponent = P;
        if (P.opponent == null) P.opponent = D;
        var rig = FindAnyObjectByType<CameraRig>(); if (rig != null) { rig.lockRange = 80f; rig.enabled = true; }
        FlowBotSetup();   // V64P
        Time.timeScale = 1f;
        Log("autoplay forced: Dracula AIBrain " + (dBrain.enabled ? "on" : "OFF") + " (playerBot), PlayerBrain " + (pb != null ? "off" : "absent") + ", Pitborn AIBrain " + (pBrain != null && pBrain.enabled ? "on" : "MISSING/OFF")
            + ", AIBrain.GlobalSeed " + seed);
    }

    float Dist() { if (D == null || P == null) return 0f; Vector3 a = D.transform.position - P.transform.position; a.y = 0f; return a.magnitude; }

    void Sample(float t)
    {
        Log("  HP D" + D.health.ToString("F0", IC) + "/" + D.maxHealth.ToString("F0", IC) + " P" + P.health.ToString("F0", IC) + "/" + P.maxHealth.ToString("F0", IC) + " stamina D" + D.stamina.ToString("F0", IC) + "/P" + P.stamina.ToString("F0", IC)
            + " dist " + Dist().ToString("F2", IC) + " state D" + D.state + "/P" + P.state + " hits D" + sd.hits + "/P" + sp.hits + " fps " + (fpsN > 0 ? (fpsSum / fpsN).ToString("F0", IC) : "-"));
    }

    void AddRecord(int seed, float dur, string endReason)
    {
        var r = new Rec { idx = fightIdx + 1, seed = seed, dur = dur, endReason = endReason, d = sd, p = sp, dHp = D.health, pHp = P.health };
        if (D != null) { r.dPost = new[] { D.statPostureBreaks, D.statDeathblows, D.statDeathblowsTaken, D.statDeathblowsMissed }; r.dGain = D.statPostureGained; }
        if (P != null) { r.pPost = new[] { P.statPostureBreaks, P.statDeathblows, P.statDeathblowsTaken, P.statDeathblowsMissed }; r.pGain = P.statPostureGained; }
        r.winner = P.Dead && !D.Dead ? "Dracula" : D.Dead && !P.Dead ? "Pitborn" : D.Dead && P.Dead ? "both" : "none (time limit)";
        try { var ad = AiAdapt.Of(D); r.dAdapt = ad != null ? ad.Report() : "(adapt off)"; ad = AiAdapt.Of(P); r.pAdapt = ad != null ? ad.Report() : "(adapt off)"; } catch (Exception) { }   // V63
        FlowStats(r); recs.Add(r);   // V64P
        Log("FIGHT RESULT " + r.idx + "/" + seedsN + " seed " + seed + " dur " + dur.ToString("F1", IC) + " winner " + r.winner + " dmg D->P " + sd.dmgDealt.ToString("F0", IC) + " P->D " + sp.dmgDealt.ToString("F0", IC)
            + " hits D" + sd.hits + "/P" + sp.hits + " pitborn moves " + sp.moves.Count + " posture " + PostureText(r) + " | flow D: " + r.dFlow + " | flow P: " + r.pFlow);
    }

    // ------------------------------------------------------------ screenshots (STAuto conventions: screen grab, camera render texture when black)
    IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        try { Log("  SHOT " + name + ".png (" + SaveShot(Path.Combine(shotDir, name + ".png")) + ")"); }
        catch (Exception e) { Log("  SHOT failed " + name + ": " + e.Message); }
        shotThisFrame = true;
    }

    string SaveShot(string path)
    {
        try
        {
            CaptureUtil.HideConsole(); var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex != null)
            {
                File.WriteAllBytes(path, tex.EncodeToPNG());
                bool black = IsBlack(tex);
                Destroy(tex);
                return black ? "screen, black" : "screen";
            }
        }
        catch (Exception ex) { Debug.LogWarning("[shot] CaptureScreenshotAsTexture: " + ex.Message); }
        try { ScreenCapture.CaptureScreenshot(path); } catch { }
        return "screen capture fallback";
    }

    static bool IsBlack(Texture2D tex)
    {
        float lum = 0f; int n = 0;
        for (int yy = 1; yy < 9; yy++) for (int xx = 1; xx < 16; xx++) { Color c = tex.GetPixel(tex.width * xx / 16, tex.height * yy / 9); lum += c.r + c.g + c.b; n++; }
        return lum / Mathf.Max(1, n) < 0.003f;
    }

    // ------------------------------------------------------------ summary
    // P7: posture counters of one fight: breaks suffered, deathblows dealt, deathblows taken, windows that ran out unused, posture gained per second
    static string PostureText(Rec r)
    {
        float sec = Mathf.Max(0.1f, r.dur);
        return "breaks D" + r.dPost[0] + "/P" + r.pPost[0] + " deathblows D" + r.dPost[1] + "/P" + r.pPost[1] + " taken D" + r.dPost[2] + "/P" + r.pPost[2] + " missed D" + r.dPost[3] + "/P" + r.pPost[3]
            + " gained/s D" + (r.dGain / sec).ToString("F1", IC) + "/P" + (r.pGain / sec).ToString("F1", IC);
    }

    static string CatText(Side s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Cats.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(Cats[i] + " " + s.cat[i]); }
        return sb.ToString();
    }
    static string MoveText(Side s)
    {
        var sb = new StringBuilder(); foreach (var kv in s.moves) { if (sb.Length > 0) sb.Append(", "); sb.Append(kv.Key + " x" + kv.Value); }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    // V63 balance helpers
    static int MoveN(Side s, string id) { int c; s.moves.TryGetValue(id, out c); return c; }
    static float RipShare(Side s) { return s.attacks > 0 ? (MoveN(s, "riposte") + MoveN(s, "slash_riposte") + MoveN(s, "heavy_riposte")) / (float)s.attacks : 0f; }
    static float TopShare(Side s, out string id) { id = "-"; int best = 0; foreach (var kv in s.moves) if (kv.Value > best) { best = kv.Value; id = kv.Key; } return s.attacks > 0 ? best / (float)s.attacks : 0f; }
    static string Pct(float f) { return (f * 100f).ToString("F0", IC) + "%"; }

    // V64P: the flow layer on the bot + poise / hyper-armour / combo logging
    void FlowBotSetup()
    {
        var fc = FlowTable.Get(); bool on = fc.enabled && fc.applyToBot;
        D.flowBotOn = on; if (P != null) P.flowBotOn = false;
        var fb = D.GetComponent<FlowBot>();
        if (on) { if (fb == null) fb = D.gameObject.AddComponent<FlowBot>(); fb.enabled = true; fb.Seed(); }
        else if (fb != null) fb.enabled = false;
        var pz = PoiseTable.Get();
        Log("V64P flow layer on the bot (flow.json applyToBot): " + (on ? "ON (chain " + fc.bot.chain + ", cancel " + fc.bot.cancel + ", riposte key " + fc.bot.riposte + ", dodge directions " + fc.bot.dodgeDir + ", magnetism cap " + fc.attack.magnet.botMaxDistance.ToString("F1", IC) + " m)" : "off (the bot plays the old unflowed way)")
            + "   poise.json " + (pz.enabled ? "ON (armour " + pz.armour.enabled + ", meter " + pz.meter.enabled + " " + pz.meter.hits + " hits, riposte x" + pz.riposte.damageMul.ToString("F2", IC) + ")" : "off"));
    }
    // F[]: 0 comboMax 1 comboStrings 2 comboSum 3 chains 4 cancelDodge 5 cancelGuard 6 cancelAtk 7 riposteKey 8 riposteMoves 9 magnetSnaps 10 poiseAbsorbs 11 armourHits
    static float[] FlowArr(DuelFighter f, Side s)
    {
        var a = new float[12]; if (f == null) return a;
        a[0] = f.statComboMax; a[1] = f.statComboCount; a[2] = f.statComboSum; a[3] = f.flowChains; a[4] = f.statCancelDodge; a[5] = f.statCancelGuard; a[6] = f.statCancelAtk;
        a[7] = f.flowRipostes; a[8] = MoveN(s, "riposte") + MoveN(s, "slash_riposte") + MoveN(s, "heavy_riposte"); a[9] = f.statMagnetSnaps; a[10] = f.statPoiseAbsorbs; a[11] = f.statArmourHits;
        return a;
    }
    static string FlowText(float[] a, DuelFighter f)
    {
        return "combo max " + a[0].ToString("F0", IC) + " avg " + (a[1] > 0 ? a[2] / a[1] : 0f).ToString("F2", IC) + " (" + a[1].ToString("F0", IC) + " strings, " + a[3].ToString("F0", IC) + " chain links)"
            + " cancels dodge " + a[4].ToString("F0", IC) + " guard " + a[5].ToString("F0", IC) + " attack " + a[6].ToString("F0", IC)
            + " ripostes key " + a[7].ToString("F0", IC) + " moves " + a[8].ToString("F0", IC) + " magnet snaps " + a[9].ToString("F0", IC)
            + " poise absorbs " + a[10].ToString("F0", IC) + " armour hits " + a[11].ToString("F0", IC) + " dir dodges " + (f != null ? f.statDirDodges : 0);
    }
    void FlowStats(Rec r)
    {
        try
        {
            if (D != null) D.FlowComboFlush(); if (P != null) P.FlowComboFlush();
            r.dF = FlowArr(D, r.d); r.pF = FlowArr(P, r.p); r.dFlow = FlowText(r.dF, D); r.pFlow = FlowText(r.pF, P);
            { var fbd = D != null ? D.GetComponent<FlowBot>() : null; if (fbd != null) r.dFlow += " | " + fbd.Diag(); }   // V65BOT: which gate stopped a chain / cancel / riposte
        }
        catch (Exception) { }
    }
    void AppendFlowAverages(StringBuilder sb, int n)
    {
        var sD = new float[12]; var sP = new float[12]; float maxD = 0f, maxP = 0f;
        foreach (var r in recs) { for (int k = 0; k < 12; k++) { sD[k] += r.dF[k]; sP[k] += r.pF[k]; } maxD = Mathf.Max(maxD, r.dF[0]); maxP = Mathf.Max(maxP, r.pF[0]); }
        var fc = FlowTable.Get();
        sb.AppendLine("  V64P flow layer on the bot " + (fc.enabled && fc.applyToBot ? "ON" : "OFF") + ": Dracula combo max " + maxD.ToString("F0", IC) + " avg " + (sD[1] > 0 ? sD[2] / sD[1] : 0f).ToString("F2", IC) + " (targets: avg >= 1.4, max >= 3)  chain links/fight " + (sD[3] / n).ToString("F1", IC)
            + "  cancels/fight dodge " + (sD[4] / n).ToString("F1", IC) + " guard " + (sD[5] / n).ToString("F1", IC) + " attack " + (sD[6] / n).ToString("F1", IC) + "  ripostes/fight key " + (sD[7] / n).ToString("F1", IC) + " (all riposte moves " + (sD[8] / n).ToString("F1", IC) + ")  magnet snaps/fight " + (sD[9] / n).ToString("F1", IC));
        sb.AppendLine("  V64P Pitborn (unflowed AI): combo max " + maxP.ToString("F0", IC) + " avg " + (sP[1] > 0 ? sP[2] / sP[1] : 0f).ToString("F2", IC) + "  cancels/fight dodge " + (sP[4] / n).ToString("F1", IC) + " guard " + (sP[5] / n).ToString("F1", IC) + "  magnet snaps/fight " + (sP[9] / n).ToString("F1", IC));
        sb.AppendLine("  V64P poise per fight (targets: absorbs 0.5-3 per side, Pitborn armour hits 1-6): absorbs D " + (sD[10] / n).ToString("F1", IC) + " P " + (sP[10] / n).ToString("F1", IC) + "  armour hits D " + (sD[11] / n).ToString("F1", IC) + " P " + (sP[11] / n).ToString("F1", IC));
        if (fc.enabled && fc.applyToBot && fc.bot.chain && sD[3] <= 0f) sb.AppendLine("  WARN: flow layer on the bot but no chain link in the whole run (flow.json chain rows / bot.chainOn* too low, or the bot never recovers in reach)");
        if (PoiseTable.Get().enabled && PoiseTable.Get().meter.enabled && (sD[10] / n > 4f || sP[10] / n > 4f)) sb.AppendLine("  WARN: poise absorbs above 4 per fight: the meter fires too often (raise meter.hits or shorten meter.windowTicks)");
        if (PoiseTable.Get().enabled && PoiseTable.Get().armour.enabled && sP[11] <= 0f) sb.AppendLine("  WARN: the Pitborn never took a hit on hyper-armour in the whole run (armour rows too narrow, or he rarely uses heavies / specials)");
    }

    // V65BOT: flow targets line per run (WARN only, the verdict rule is unchanged)
    void AppendFlowTargets(StringBuilder sb, int n)
    {
        var fc = FlowTable.Get(); if (n <= 0 || !(fc.enabled && fc.applyToBot)) return;
        float cStr = 0f, cSum = 0f, canc = 0f, chains = 0f, key = 0f, rmov = 0f, snaps = 0f, par = 0f;
        foreach (var r in recs) { cStr += r.dF[1]; cSum += r.dF[2]; chains += r.dF[3]; canc += r.dF[4] + r.dF[5] + r.dF[6]; key += r.dF[7]; rmov += r.dF[8]; snaps += r.dF[9]; par += r.d.parries; }
        float avg = cStr > 0f ? cSum / cStr : 0f;
        sb.AppendLine("  V65BOT flow targets (WARN only, verdict unchanged): combo avg " + avg.ToString("F2", IC) + " (>= 1.6)   cancels/fight " + (canc / n).ToString("F1", IC) + " (>= 3; dodge+guard+attack cancels, chain links not counted: " + (chains / n).ToString("F1", IC) + "/fight)"
            + "   ripostes by key/fight " + (key / n).ToString("F1", IC) + " vs parries/fight " + (par / n).ToString("F1", IC) + " (key == parries; all riposte moves " + (rmov / n).ToString("F1", IC) + ")   magnet snaps/fight " + (snaps / n).ToString("F1", IC) + " (> 0)");
        if (avg < 1.6f) sb.AppendLine("  WARN: flow target: Dracula combo avg " + avg.ToString("F2", IC) + " below 1.6 (see the 'bot chain decisions' gates in the per-fight flow D line)");
        if (canc / n < 3f) sb.AppendLine("  WARN: flow target: cancels/fight " + (canc / n).ToString("F1", IC) + " below 3 (bot.cancelChance / cancelHeavyChance / cancelHeavyLeadMax in flow.json)");
        if (par > 0f && key < par * 0.9f) sb.AppendLine("  WARN: flow target: riposte-key presses " + key.ToString("F0", IC) + " below parries " + par.ToString("F0", IC) + " (bot.riposteChance / riposteReactMax / riposteSlack)");
        if (snaps <= 0f) sb.AppendLine("  WARN: flow target: no magnetism snap on the bot in the whole run (attack.magnet.botMaxDistance too low, or the bot swings from outside it)");
    }

    void WriteSummary(int code, string why, string verdictOverride)
    {
        int n = recs.Count;
        float sDur = 0, sDD = 0, sDT = 0, sPD = 0, sPT = 0, sHD = 0, sHP = 0, sPM = 0; int dWins = 0, pWins = 0; bool bothHit = n > 0; int minPitMoves = int.MaxValue;
        foreach (var r in recs)
        {
            sDur += r.dur; sDD += r.d.dmgDealt; sDT += r.d.dmgTaken; sPD += r.p.dmgDealt; sPT += r.p.dmgTaken; sHD += r.d.hits; sHP += r.p.hits; sPM += r.p.moves.Count;
            if (r.winner == "Dracula") dWins++; else if (r.winner == "Pitborn") pWins++;
            if (r.d.hits <= 0 || r.p.hits <= 0) bothHit = false;
            if (r.p.moves.Count < minPitMoves) minPitMoves = r.p.moves.Count;
        }
        // distinct Pitborn moves over the whole run (a fight can be short); the per-fight minimum is reported too
        var allPit = new HashSet<string>(); foreach (var r in recs) foreach (var k in r.p.moves.Keys) allPit.Add(k);
        float avgDur = n > 0 ? sDur / n : 0f, winRate = n > 0 ? (float)dWins / n : 0f;
        var problems = new List<string>();
        if (n < seedsN) problems.Add("only " + n + " of " + seedsN + " fights completed");
        if (n > 0 && (avgDur < C.minAvgDuration || avgDur > C.maxAvgDuration)) problems.Add("average duration " + avgDur.ToString("F1", IC) + " s outside " + C.minAvgDuration.ToString("F0", IC) + "-" + C.maxAvgDuration.ToString("F0", IC) + " s");
        if (n > 0 && (winRate < C.minWinRate || winRate > C.maxWinRate)) problems.Add("Dracula win rate " + (winRate * 100f).ToString("F0", IC) + "% outside " + (C.minWinRate * 100f).ToString("F0", IC) + "-" + (C.maxWinRate * 100f).ToString("F0", IC) + "%");
        if (!bothHit) problems.Add("a side landed no hits in at least one fight");
        if (allPit.Count < C.minPitbornMoves) problems.Add("the Pitborn used only " + allPit.Count + " different moves (need " + C.minPitbornMoves + ")");
        string verdict = verdictOverride ?? (problems.Count == 0 ? "PASS" : "FAIL");

        var sb = new StringBuilder();
        sb.AppendLine("VERDICT: " + verdict + "   (exit code " + code + ", " + why + ")");
        sb.AppendLine("date " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  res " + Screen.width + "x" + Screen.height + "  seeds " + baseSeed + ".." + (baseSeed + seedsN - 1) + "  fights " + n + "/" + seedsN + "  data " + dataSrc);
        sb.AppendLine("pass rule: average duration " + C.minAvgDuration.ToString("F0", IC) + "-" + C.maxAvgDuration.ToString("F0", IC) + " s AND Dracula win rate " + (C.minWinRate * 100f).ToString("F0", IC) + "-" + (C.maxWinRate * 100f).ToString("F0", IC)
            + "% AND both sides land hits in every fight AND the Pitborn uses >= " + C.minPitbornMoves + " different moves");
        foreach (var p in problems) sb.AppendLine("PROBLEM: " + p);
        sb.AppendLine();
        sb.AppendLine("per fight (D = Dracula (playerBot AI), P = Pitborn; dmg D>P = damage Dracula dealt):");
        foreach (var r in recs)
        {
            sb.AppendLine("  #" + r.idx + " seed " + r.seed + "  " + r.dur.ToString("F1", IC) + " s  winner " + r.winner + " (" + r.endReason + ")  hp end D" + r.dHp.ToString("F0", IC) + "/P" + r.pHp.ToString("F0", IC));
            sb.AppendLine("       damage D>P " + r.d.dmgDealt.ToString("F0", IC) + "  P>D " + r.p.dmgDealt.ToString("F0", IC) + "   hits landed D " + r.d.hits + "  P " + r.p.hits + "   swings landed/attempted D " + r.d.hits + "/" + r.d.attacks + " P " + r.p.hits + "/" + r.p.attacks);
            sb.AppendLine("       parries (by) D " + r.d.parries + " P " + r.p.parries + "   blocks D " + r.d.blocks + " P " + r.p.blocks + "   guardbreaks on D " + r.d.guardBreaks + " P " + r.p.guardBreaks + "   dodges D " + r.d.dodges + " P " + r.p.dodges);
            sb.AppendLine("       posture " + PostureText(r)); sb.AppendLine("       V64P flow D: " + r.dFlow); sb.AppendLine("       V64P flow P: " + r.pFlow);
            sb.AppendLine("       moves D (" + r.d.attacks + "): " + CatText(r.d));
            sb.AppendLine("       moves P (" + r.p.attacks + "): " + CatText(r.p) + "   distinct " + r.p.moves.Count + ": " + MoveText(r.p));
            { string tdI, tpI; float tdS = TopShare(r.d, out tdI), tpS = TopShare(r.p, out tpI);
              sb.AppendLine("       V63 riposte share of swings D " + Pct(RipShare(r.d)) + " P " + Pct(RipShare(r.p)) + " (ripostes D " + (MoveN(r.d, "riposte") + MoveN(r.d, "slash_riposte") + MoveN(r.d, "heavy_riposte")) + " P " + (MoveN(r.p, "riposte") + MoveN(r.p, "slash_riposte") + MoveN(r.p, "heavy_riposte"))
                  + ", slash_riposte D " + MoveN(r.d, "slash_riposte") + " P " + MoveN(r.p, "slash_riposte") + ")   top move D " + tdI + " " + Pct(tdS) + " P " + tpI + " " + Pct(tpS) + "   kicks D " + MoveN(r.d, "kick") + " P " + MoveN(r.p, "kick"));
              sb.AppendLine("       V63 adapt D: " + r.dAdapt); sb.AppendLine("       V63 adapt P: " + r.pAdapt); }
        }
        sb.AppendLine();
        if (n > 0)
        {
            sb.AppendLine("averages over " + n + " fights:");
            sb.AppendLine("  duration " + avgDur.ToString("F1", IC) + " s   Dracula wins " + dWins + "/" + n + " (" + (winRate * 100f).ToString("F0", IC) + "%)   Pitborn wins " + pWins + "   no winner " + (n - dWins - pWins));
            sb.AppendLine("  damage dealt: Dracula " + (sDD / n).ToString("F1", IC) + "  Pitborn " + (sPD / n).ToString("F1", IC) + "   (taken: Dracula " + (sDT / n).ToString("F1", IC) + "  Pitborn " + (sPT / n).ToString("F1", IC) + ")");
            sb.AppendLine("  hits landed: Dracula " + (sHD / n).ToString("F1", IC) + "  Pitborn " + (sHP / n).ToString("F1", IC));
            int[] tD = new int[4], tP = new int[4]; foreach (var r in recs) for (int k = 0; k < 4; k++) { tD[k] += r.dPost[k]; tP[k] += r.pPost[k]; }
            sb.AppendLine("  posture per fight (P6 targets: 1-3 breaks, deathblows/breaks >= 50%, missed 0-1): breaks D " + (tD[0] / (float)n).ToString("F1", IC) + " P " + (tP[0] / (float)n).ToString("F1", IC)
                + "  deathblows dealt D " + (tD[1] / (float)n).ToString("F1", IC) + " P " + (tP[1] / (float)n).ToString("F1", IC) + "  taken D " + (tD[2] / (float)n).ToString("F1", IC) + " P " + (tP[2] / (float)n).ToString("F1", IC)
                + "  missed D " + (tD[3] / (float)n).ToString("F1", IC) + " P " + (tP[3] / (float)n).ToString("F1", IC)
                + "  | totals: breaks " + (tD[0] + tP[0]) + ", deathblows " + (tD[1] + tP[1]) + ", conversion " + ((tD[0] + tP[0]) > 0 ? ((tD[1] + tP[1]) * 100f / (tD[0] + tP[0])).ToString("F0", IC) + "%" : "-"));
            AppendFlowAverages(sb, n); AppendFlowTargets(sb, n); sb.AppendLine("  Pitborn distinct moves per fight " + (sPM / n).ToString("F1", IC) + " (min " + minPitMoves + "), over the run " + allPit.Count);
            {   // V63 balance targets: riposte share <= 30% of Pitborn swings, no single move > 40%, parries spread, enrage entered
                float ripP = 0, ripD = 0, topPmax = 0, topDmax = 0; int parD = 0, parP = 0, enr = 0, kickD = 0, kickP = 0; string idd;
                foreach (var r in recs) { ripP += RipShare(r.p); ripD += RipShare(r.d); topPmax = Mathf.Max(topPmax, TopShare(r.p, out idd)); topDmax = Mathf.Max(topDmax, TopShare(r.d, out idd)); parD += r.d.parries; parP += r.p.parries; kickD += MoveN(r.d, "kick"); kickP += MoveN(r.p, "kick"); if (r.pAdapt.Contains("enrage yes")) enr++; }
                sb.AppendLine("  V63 riposte share of swings: Pitborn " + Pct(ripP / n) + " (target <= " + Pct(C.maxRiposteShare) + ")  Dracula " + Pct(ripD / n) + "   worst single-move share per fight: Pitborn " + Pct(topPmax) + " Dracula " + Pct(topDmax) + " (target <= " + Pct(C.maxMoveShare) + ")");
                sb.AppendLine("  V63 parries (by) per fight: Dracula " + (parD / (float)n).ToString("F1", IC) + " Pitborn " + (parP / (float)n).ToString("F1", IC) + "   kicks per fight: Dracula " + (kickD / (float)n).ToString("F1", IC) + " Pitborn " + (kickP / (float)n).ToString("F1", IC) + "   Pitborn enrage entered in " + enr + "/" + n + " fights");
                if (ripP / n > C.maxRiposteShare) sb.AppendLine("  WARN: Pitborn riposte share " + Pct(ripP / n) + " above " + Pct(C.maxRiposteShare));
                if (topPmax > C.maxMoveShare || topDmax > C.maxMoveShare) sb.AppendLine("  WARN: a single move exceeds " + Pct(C.maxMoveShare) + " of swings in at least one fight");
                if (parD + parP >= 6 && (parD < (parD + parP) * 0.3f || parP < (parD + parP) * 0.3f)) sb.AppendLine("  WARN: parries lopsided (D " + parD + " / P " + parP + ")");
            }
        }
        sb.AppendLine("fps over all fights: avg " + (fpsN > 0 ? (fpsSum / fpsN).ToString("F1", IC) : "-") + "  worst frame " + (fpsN > 0 ? fpsMin.ToString("F1", IC) : "-") + "   (target >= 120 at 1080p)");
        sb.AppendLine("exceptions: harness " + myExceptions + ", foreign " + foreignExceptions);
        foreach (var e in exceptionList) sb.AppendLine("  " + e);
        File.WriteAllText(summaryPath, sb.ToString());
    }
}
