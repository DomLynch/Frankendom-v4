#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// STEER lane S4: -steerauto verify harness (implies -steer; S1's controller must treat STAuto.Requested like the -steer flag).
// A scripted "player" steers the player for auto.seconds (steer.json "auto" block) and logs every command -> the AI decision that followed -> the outcome.
//   <exe dir>\steer_auto_log.txt      one line per event, written immediately (a crash still leaves it). STSteer.Log() lines from S1 land in the same file.
//   <exe dir>\steer_shots\steer_<sec>_<tactic>.png   every auto.shotEvery s (RenderTexture-safe, see ShotMode)
//   <exe dir>\steer_auto_summary.txt  verdict + counts + worst fps + exceptions
//   exit code: 0 = fight ended or auto.seconds elapsed without exceptions, 2 = exception (stack in the log) or steer layer missing, 3 = -steertimeout:<s> (default auto.timeoutSeconds = 200)
// Options: -steertimeout:<s>  -steerseed:<n>  -steerout:<dir> (default: the exe directory)  -steerstrict (any Unity exception = exit 2)  -steerdata:<steer.json path>
// The scripted player only ever calls STSteer.SetStance / UseTactic (through STAutoApi). Everything tunable is in steer.json "auto".
[DefaultExecutionOrder(-80)]
public class STAuto : MonoBehaviour
{
    // ------------------------------------------------------------ data (steer.json "auto" block; these defaults mirror the file)
    [Serializable]
    public class AutoCfg
    {
        public float seconds = 120f, commandEvery = 3f, firstCommandAt = 0.5f, shotEvery = 3f, sampleEvery = 0.25f, summaryEvery = 10f;
        public float timeoutSeconds = 200f, endDelay = 2.5f, bootWaitSeconds = 20f, decisionWindow = 1.5f;
        public float stanceChance = 0.6f;       // chance per command slot that a stance change is issued as well
        public int seed = 1234;
        public int goForKillTryEvery = 5;       // every n-th slot GoForKill is attempted even when disabled, so the log says why
        public string shotMode = "auto";        // auto = game screen (with the UI), camera render texture if that comes out black | screen | camera
        public int shotWidth = 0, shotHeight = 0;   // camera mode size (0 = Screen size)
        public bool logSamples = false;         // true: a sample line every sampleEvery s (default: only state changes)
        // v58: opening sequence (guarantees the tactics are tried early); after the last entry the random cycle continues every commandEvery
        public FirstCmd[] firstCommands = {
            new FirstCmd { t = 0.5f, tactic = "Press" }, new FirstCmd { t = 3.5f, tactic = "Circle" }, new FirstCmd { t = 6.5f, tactic = "GuardUp" },
            new FirstCmd { t = 9.5f, tactic = "Dodge" }, new FirstCmd { t = 12.5f, tactic = "Feint" }, new FirstCmd { t = 15.5f, tactic = "BackOff" }, new FirstCmd { t = 18.5f, tactic = "GoForKill" } };
        // v58: balance harness (-seeds:N)
        public float seedsTimeoutSeconds = 900f, balanceMinDuration = 60f, balanceMaxDuration = 120f, balanceMinWinRate = 0.4f, balanceMaxWinRate = 0.7f;
        public float restartWaitSeconds = 20f;
    }
    [Serializable] public class FirstCmd { public float t; public string tactic = ""; public string stance = ""; }
    [Serializable] class Root { public AutoCfg auto; }

    // ------------------------------------------------------------ boot
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    /// <summary>True when -steerauto is on the command line (S1: treat this as -steer).</summary>
    public static bool Requested { get { return Flag("-steerauto"); } }
    public static STAuto Instance;
    /// <summary>-seeds:N (default 1): N consecutive fights with seeds seed, seed+1, ... and steer_balance_summary.txt.</summary>
    public static int SeedsN { get { int n; var a = Arg("-seeds:"); return a != null && int.TryParse(a, out n) && n > 1 ? n : 1; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Requested) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<STAuto>() != null) return;
            var go = new GameObject("STAuto");
            DontDestroyOnLoad(go);
            go.AddComponent<STAuto>();
        }
        catch (Exception e) { Debug.LogError("[steerauto] boot failed: " + e); }
    }

    // ------------------------------------------------------------ state
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    AutoCfg C = new AutoCfg();
    string dir, logPath, shotDir, summaryPath, dataSrc = "(defaults)";
    float t0, fightStart = -1f, timeout;
    bool exiting, started, ended; int exitCode; float exitAt;
    string lastTactic = "none";
    bool logOpen; readonly StringBuilder pending = new StringBuilder();   // lines before the fight start wait here: STSteer.Log() truncates the file on its first call
    System.Random rng;
    DuelFighter D, P;                      // the player, the enemy
    string[] stances, tactics;
    int slot, shotN, cmdN, seedsN = 1, fightIdx, openIdx; float openEnd, fightDur, nextKill; string shotPrefix = "steer_";
    class Rec { public int idx, seed; public float dur, dmgDealt, dmgTaken, dHp, pHp; public string winner, endReason, tactics; public int hitsD, hitsP, parriesD, parriesP, dodgesD, dodgesP, acc, att; }
    readonly List<Rec> recs = new List<Rec>();
    readonly Dictionary<string, int> sumAtt = new Dictionary<string, int>(), sumAcc = new Dictionary<string, int>();
    float nextCmd, nextShot, nextSample, nextSummary;
    bool shotThisFrame;

    // fps (hitch frames caused by our own screenshot are skipped)
    readonly List<float> dts = new List<float>(16384);
    float winFpsSum, winFpsMin = 9999f; int winFpsN;
    float allFpsSum, allFpsMin = 9999f; int allFpsN;

    // exceptions
    int stExceptions, foreignExceptions; readonly List<string> exceptionList = new List<string>();
    bool strict;

    // counters
    class Stats
    {
        public float dmgDealt, dmgTaken;                       // by the player to the enemy / by the enemy to the player (health deltas)
        public int hitsD, hitsP, blockedByD, blockedByP, parriesD, parriesP, guardBreaksD, guardBreaksP, dodgesD, dodgesP, attacksD, attacksP;
        public Stats Clone() { return (Stats)MemberwiseClone(); }
    }
    Stats S = new Stats(), win = new Stats();
    float dHpPrev, pHpPrev, dHpMin, pHpMin, dHpStart, pHpStart;
    int contactEvents; int tickStart;
    FState dStatePrev, pStatePrev; string dMovePrev = "", pMovePrev = "";

    // per-tactic bookkeeping
    readonly Dictionary<string, int> attempted = new Dictionary<string, int>(), accepted = new Dictionary<string, int>(), refused = new Dictionary<string, int>();
    readonly Dictionary<string, string> lastRefusal = new Dictionary<string, string>();
    readonly Dictionary<string, int> stanceUsed = new Dictionary<string, int>();
    int acceptedAll, refusedAll, stanceChanges;

    // the command currently being observed
    class Cmd { public int n; public float t; public string text; public Stats snap; public string decision; public float dist0, distMin = 99f, distMax; public bool open = true; public string stance, tacticStart; }
    Cmd cur;

    // ------------------------------------------------------------ lifecycle
    void Awake()
    {
        Instance = this;
        t0 = Time.realtimeSinceStartup;
        try
        {
            string d = Arg("-steerout:");
            try { dir = !string.IsNullOrEmpty(d) ? d : Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)); } catch { dir = "."; }
            if (string.IsNullOrEmpty(dir)) dir = ".";
            try { Directory.CreateDirectory(dir); } catch { }
            logPath = Path.Combine(dir, "steer_auto_log.txt");
            shotDir = Path.Combine(dir, "steer_shots");
            summaryPath = Path.Combine(dir, "steer_auto_summary.txt");
            try { File.WriteAllText(logPath, ""); } catch (Exception e) { Debug.LogWarning("[steerauto] cannot write " + logPath + ": " + e.Message); }
            try { if (File.Exists(summaryPath)) File.Delete(summaryPath); } catch { }
            try { Directory.CreateDirectory(shotDir); } catch (Exception e) { Debug.LogWarning("[steerauto] cannot create " + shotDir + ": " + e.Message); }
            LoadCfg();
            seedsN = SeedsN;
            if (C.firstCommands == null) C.firstCommands = new FirstCmd[0];
            Array.Sort(C.firstCommands, (x, y) => x.t.CompareTo(y.t));
            openEnd = C.firstCommands.Length > 0 ? C.firstCommands[C.firstCommands.Length - 1].t : 0f;
            timeout = seedsN > 1 ? (C.seedsTimeoutSeconds > 0f ? C.seedsTimeoutSeconds : 900f) : (C.timeoutSeconds > 0f ? C.timeoutSeconds : 200f);
            float tv; var ts = Arg("-steertimeout:");
            if (ts != null && float.TryParse(ts, System.Globalization.NumberStyles.Float, IC, out tv) && tv > 0f) timeout = tv;
            int sv; var ss = Arg("-steerseed:"); if (ss != null && int.TryParse(ss, out sv)) C.seed = sv;
            strict = Flag("-steerstrict");
            rng = new System.Random(C.seed);
            foreach (var tn0 in STAutoApi.TacticNames) { sumAtt[tn0] = 0; sumAcc[tn0] = 0; }
            stances = STAutoApi.StanceNames; tactics = STAutoApi.TacticNames;
            foreach (var tn in tactics) { attempted[tn] = 0; accepted[tn] = 0; refused[tn] = 0; }
            Application.logMessageReceived += OnUnityLog;
            ContactRelay.OnContact += OnContact;
            ContactRelay.OnDodge += OnDodge;
            ContactRelay.OnAttackStarted += OnAttack;
            Log("steerauto start " + DateTime.Now.ToString("s") + " unity " + Application.unityVersion + " res " + Screen.width + "x" + Screen.height
                + " seeds " + seedsN + " seconds " + C.seconds + " opening " + C.firstCommands.Length + " cmds until " + openEnd.ToString("F1", IC) + "s" + " commandEvery " + C.commandEvery + " shotEvery " + C.shotEvery + " seed " + C.seed + " timeout " + timeout + "s shotMode " + C.shotMode + " data " + dataSrc);
        }
        catch (Exception e) { Fail(e, "Awake"); }
    }

    void Start() { StartCoroutine(Guard(Main())); }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityLog; ContactRelay.OnContact -= OnContact; ContactRelay.OnDodge -= OnDodge; ContactRelay.OnAttackStarted -= OnAttack;
        if (Instance == this) Instance = null;
    }

    void LoadCfg()
    {
        var paths = new List<string>();
        string cli = Arg("-steerdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        else
        {
            try { C = STSteer.Load<AutoCfg>("auto"); dataSrc = "steer.json auto block (STSteer.Load)"; } catch (Exception ex) { Debug.LogWarning("[steerauto] " + ex.Message); }
            return;
        }
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var r = JsonUtility.FromJson<Root>(File.ReadAllText(p).TrimStart('﻿'));
                if (r != null && r.auto != null) { C = r.auto; dataSrc = p; return; }
                dataSrc = p + " (no \"auto\" block: defaults)";
            }
            catch (Exception ex) { Debug.LogWarning("[steerauto] cannot read " + p + ": " + ex.Message); dataSrc = p + " (unreadable: defaults)"; }
        }
    }

    IEnumerator Guard(IEnumerator inner)
    {
        while (true)
        {
            object cur2;
            try { if (!inner.MoveNext()) yield break; cur2 = inner.Current; }
            catch (Exception ex) { Fail(ex, "main loop"); yield break; }
            yield return cur2;
        }
    }

    // ------------------------------------------------------------ logging / exit
    float Clock { get { return fightStart >= 0f ? Time.realtimeSinceStartup - fightStart : 0f; } }

    public void Log(string line)
    {
        string stamp = fightStart >= 0f ? "f" + (Time.realtimeSinceStartup - fightStart).ToString("F2", IC).PadLeft(7) : "b" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7);
        if (!logOpen) pending.AppendLine(stamp + "s " + line);
        else { try { File.AppendAllText(logPath, stamp + "s " + line + "\n"); } catch { } }
        Debug.Log("[steerauto] " + line);
    }

    void Fail(Exception e, string where) { stExceptions++; exceptionList.Add(where + ": " + e.GetType().Name + " " + e.Message); Log("EXCEPTION in " + where + ": " + e); Exit(2, "exception in " + where, "ERROR"); }
    void Fail(string msg) { stExceptions++; exceptionList.Add(msg); Log("ERROR: " + msg); Exit(2, msg, "ERROR"); }

    void Exit(int code, string why, string verdictOverride)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime;
        if (!logOpen) { logOpen = true; try { File.AppendAllText(logPath, pending.ToString()); } catch { } pending.Length = 0; }
        try { if (cur != null && cur.open) CloseCmd("exit"); } catch { }
        try { WriteSummary(code, why, verdictOverride); } catch (Exception e) { Log("summary failed: " + e); }
        if (seedsN > 1) { try { WriteBalance(code, why, verdictOverride); } catch (Exception e) { Log("balance summary failed: " + e); } }
        Log("EXIT code " + code + " (" + why + ")" + (foreignExceptions > 0 ? " foreignExceptions=" + foreignExceptions : ""));
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    void OnUnityLog(string msg, string stack, LogType type)
    {
        if (type != LogType.Exception || exiting) return;
        bool mine = Regex.IsMatch(stack ?? "", @"(^|\s)ST[A-Z]\w*[:\.]") || Regex.IsMatch(msg ?? "", @"\bST[A-Z]\w*\b");
        string one = (msg ?? "") + " | " + FirstLine(stack);
        if (mine || strict) { stExceptions++; exceptionList.Add(one); Log("UNHANDLED EXCEPTION (steer): " + msg + "\n" + stack); Exit(2, "unhandled exception", "ERROR"); }
        else { foreignExceptions++; exceptionList.Add("(foreign) " + one); Log("exception outside steer (counted, not fatal; -steerstrict makes it fatal): " + msg + "\n" + stack); }
    }
    static string FirstLine(string s) { if (string.IsNullOrEmpty(s)) return ""; int i = s.IndexOf('\n'); return i < 0 ? s : s.Substring(0, i); }

    // ------------------------------------------------------------ per-frame: fps, timeout, health deltas, state changes
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
            else if (dt > 0f && fightStart >= 0f && !ended)
            {
                float f = 1f / dt;
                dts.Add(dt); winFpsSum += f; winFpsN++; allFpsSum += f; allFpsN++;
                if (f < winFpsMin) winFpsMin = f; if (f < allFpsMin) allFpsMin = f;
            }
            if (Time.realtimeSinceStartup - t0 > timeout)
            {
                Log("TIMEOUT after " + timeout.ToString("F0", IC) + "s (fight clock " + (fightStart >= 0f ? (Time.realtimeSinceStartup - fightStart).ToString("F1", IC) : "not started") + ")");
                Exit(3, "timeout", "TIMEOUT"); return;
            }
            if (started && D != null && P != null) Track();
        }
        catch (Exception e) { Fail(e, "Update"); }
    }

    void Track()
    {
        float dh = D.health, ph = P.health;
        if (dh < dHpPrev) { float x = dHpPrev - dh; S.dmgTaken += x; win.dmgTaken += x; }
        if (ph < pHpPrev) { float x = pHpPrev - ph; S.dmgDealt += x; win.dmgDealt += x; }
        dHpPrev = dh; pHpPrev = ph; if (dh < dHpMin) dHpMin = dh; if (ph < pHpMin) pHpMin = ph;
        float dist = Dist();
        if (cur != null && cur.open) { if (dist < cur.distMin) cur.distMin = dist; if (dist > cur.distMax) cur.distMax = dist; }

        // state changes (these are the AI decisions made visible); the player lines name the decision when it follows a command
        string dm = D.CurrentMove != null ? D.CurrentMove.id : "", pm = P.CurrentMove != null ? P.CurrentMove.id : "";
        if (D.state != dStatePrev || (D.state != FState.Loco && dm != dMovePrev))
        {
            string what = "D " + dStatePrev + ">" + D.state + (dm.Length > 0 ? " move=" + dm : "") + " dist=" + dist.ToString("F2", IC) + " hp D" + dh.ToString("F0", IC) + "/P" + ph.ToString("F0", IC);
            Log("  STATE " + what);
            if (cur != null && cur.open && cur.decision == null && D.state != FState.Loco && Clock - cur.t <= C.decisionWindow)
            {
                cur.decision = "D " + D.state + (dm.Length > 0 ? " " + dm : "") + " +" + (Clock - cur.t).ToString("F2", IC) + "s";
                Log("  DECISION #" + cur.n + " after [" + cur.text + "]: " + cur.decision);
            }
            dStatePrev = D.state; dMovePrev = dm;
        }
        if (P.state != pStatePrev || (P.state != FState.Loco && pm != pMovePrev))
        {
            Log("  STATE P " + pStatePrev + ">" + P.state + (pm.Length > 0 ? " move=" + pm : "") + " dist=" + dist.ToString("F2", IC));
            pStatePrev = P.state; pMovePrev = pm;
        }
    }

    float Dist()
    {
        if (D == null || P == null) return 0f;
        Vector3 a = D.transform.position - P.transform.position; a.y = 0f; return a.magnitude;
    }

    // ------------------------------------------------------------ combat events
    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (!started || exiting) return;
        contactEvents++;
        bool aIsD = a == D, aIsP = a == P;
        if (!aIsD && !aIsP) return;
        switch (type)
        {
            case "Hit": if (aIsD) { S.hitsD++; win.hitsD++; } else { S.hitsP++; win.hitsP++; } break;
            case "Blocked": if (aIsD) { S.blockedByP++; win.blockedByP++; } else { S.blockedByD++; win.blockedByD++; } break;
            case "Parried": if (aIsD) { S.parriesP++; win.parriesP++; } else { S.parriesD++; win.parriesD++; } break;   // attacker was parried by the defender
            case "GuardBroken": if (aIsD) { S.guardBreaksP++; win.guardBreaksP++; } else { S.guardBreaksD++; win.guardBreaksD++; } break;
        }
        string who = aIsD ? "D>P" : "P>D";
        Log("  CONTACT " + who + " " + type + (heavy ? " heavy" : "") + (perfect ? " perfect" : "") + " move=" + (a.CurrentMove != null ? a.CurrentMove.id : "?")
            + " hp D" + D.health.ToString("F0", IC) + "/P" + P.health.ToString("F0", IC));
    }
    void OnDodge(DuelFighter f, string kind, Vector3 dir)
    {
        if (!started || exiting) return;
        if (f == D) { S.dodgesD++; win.dodgesD++; } else if (f == P) { S.dodgesP++; win.dodgesP++; } else return;
        Log("  DODGE " + (f == D ? "D" : "P") + " " + kind);
    }
    void OnAttack(DuelFighter f, MoveDef m)
    {
        if (!started || exiting) return;
        if (f == D) { S.attacksD++; win.attacksD++; } else if (f == P) { S.attacksP++; win.attacksP++; }
    }

    // events from S1's STSteer (hooked through STAutoApi): feed lines and accepted/stance confirmations
    void OnSteerEvent(string name, string text)
    {
        if (exiting) return;
        Log("  evt " + name + (text.Length > 0 ? " " + text : ""));
        if (name == "TacticStart" && cur != null && cur.open && cur.tacticStart == null) { cur.tacticStart = text + " +" + (Clock - cur.t).ToString("F2", IC) + "s"; }
    }

    // ------------------------------------------------------------ main flow
    IEnumerator Main()
    {
        // 1) the steer layer + both fighters
        float w0 = Time.realtimeSinceStartup; float lastNote = -1f; string why = "";
        while (true)
        {
            why = "";
            if (!STAutoApi.Available) why = STAutoApi.Missing();
            else if (!STAutoApi.Active) why = "STSteer.Active is false (the steer controller must start for -steerauto: S1 treats -steerauto as -steer)";
            DuelFighter d = null, p = null;
            d = STAutoApi.Player; p = STAutoApi.Enemy;
            if (d == null || p == null) foreach (var f in FindObjectsByType<DuelFighter>()) { if (f.name == Roster.PlayerObject) d = f; else if (f.name == Roster.EnemyObject) p = f; }
            if (why.Length == 0 && (d == null || p == null)) why = "fighters not found (" + Roster.PlayerName + " " + (d != null) + ", " + Roster.EnemyName + " " + (p != null) + ")";
            if (why.Length == 0) { D = d; P = p; break; }
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSeconds) { Fail("not ready after " + C.bootWaitSeconds.ToString("F0", IC) + " s: " + why); yield break; }
            if (Time.realtimeSinceStartup - lastNote >= 1f) { lastNote = Time.realtimeSinceStartup; Log("waiting: " + why); }
            yield return null;
        }
        for (int i = 0; i < 20; i++) yield return null;   // let Awake/Start of the steer layer settle
        int hooked = STAutoApi.Hook(OnSteerEvent);
        STAutoApi.ForceAutoLog();
        STAutoApi.Mark("steerauto", "harness attached", "seed " + C.seed + " seeds " + seedsN);   // S1's first Log() (re)creates the file; our buffered boot lines follow
        logOpen = true;
        try { File.AppendAllText(logPath, pending.ToString()); } catch { }
        pending.Length = 0;

        string endReason = "";
        for (fightIdx = 0; fightIdx < seedsN && !exiting; fightIdx++)
        {
            if (fightIdx > 0)
            {
                yield return RestartFight();
                if (exiting) yield break;
            }
            // 2) start (per-fight state is reset here)
            int seed = C.seed + fightIdx;
            ResetFight(seed);
            fightStart = Time.realtimeSinceStartup; started = true; ended = false;
            dHpStart = dHpPrev = dHpMin = D.health; pHpStart = pHpPrev = pHpMin = P.health;
            dStatePrev = D.state; pStatePrev = P.state; tickStart = Duel.TickCount;
            nextCmd = C.firstCommandAt; nextSample = C.sampleEvery; nextSummary = C.summaryEvery; nextKill = 0.5f; openIdx = 0;
            bool single = seedsN <= 1;
            nextShot = single ? C.shotEvery : 1f;   // seeds mode: one shot at the start (1 s) and one at the end of each fight
            shotPrefix = single ? "steer_" : "steer_f" + (fightIdx + 1) + "_";
            Log("FIGHT START " + (fightIdx + 1) + "/" + seedsN + " seed " + seed + " stance " + STAutoApi.Stance + " hooked steer events " + hooked + " hp D" + dHpStart.ToString("F0", IC) + "/P" + pHpStart.ToString("F0", IC)
                + " dist " + Dist().ToString("F2", IC) + " tactics [" + string.Join(",", tactics) + "] stances [" + string.Join(",", stances) + "]");

            // 3) the scripted player
            endReason = "";
            while (!exiting)
            {
                float t = Time.realtimeSinceStartup - fightStart;
                if (openIdx < C.firstCommands.Length)
                {
                    var fc = C.firstCommands[openIdx];
                    if (t >= fc.t) { openIdx++; Command(t, fc, null); if (openIdx >= C.firstCommands.Length) nextCmd = t + C.commandEvery; }
                }
                else if (t >= nextCmd) { nextCmd += C.commandEvery; Command(t, null, null); }
                if (t >= nextKill)   // GoForKill whenever it is enabled (once per fight)
                {
                    nextKill = t + 0.5f;
                    if (accepted.ContainsKey("GoForKill") && accepted["GoForKill"] == 0 && STAutoApi.Enabled("GoForKill") && STAutoApi.Cooldown01("GoForKill") <= 0f) Command(t, null, "GoForKill");
                }
                if (t >= nextSample) { nextSample += C.sampleEvery; Sample(t); }
                if (t >= nextSummary) { nextSummary += C.summaryEvery; Summary10(t); }
                if (t >= nextShot) { nextShot = single ? nextShot + C.shotEvery : float.MaxValue; yield return Shot(t); }
                if (D.Dead || P.Dead) { endReason = (P.Dead ? Roster.EnemyName : Roster.PlayerName) + " died"; break; }
                if (t >= C.seconds) { endReason = C.seconds.ToString("F0", IC) + " s elapsed"; break; }
                yield return null;
            }
            if (exiting) yield break;

            // 4) the end: let the death/last blow play, last shot, record
            fightDur = Time.realtimeSinceStartup - fightStart;
            Log("FIGHT END " + (fightIdx + 1) + "/" + seedsN + " (" + endReason + ") at " + fightDur.ToString("F1", IC) + " s");
            if (cur != null && cur.open) CloseCmd("fight end");
            float until = Time.realtimeSinceStartup + (D.Dead || P.Dead ? C.endDelay : 0.2f);
            while (Time.realtimeSinceStartup < until && !exiting) { Track(); yield return null; }
            if (exiting) yield break;
            lastTactic = D.Dead || P.Dead ? "end" : lastTactic;
            yield return Shot(Time.realtimeSinceStartup - fightStart);
            ended = true;
            Summary10(Time.realtimeSinceStartup - fightStart);
            AddRecord(seed, endReason);
        }
        Exit(0, seedsN > 1 ? seedsN + " fights done" : endReason, null);
    }

    void ResetFight(int seed)
    {
        S = new Stats(); win = new Stats(); cur = null; slot = 0; cmdN = 0; shotN = 0; contactEvents = 0; acceptedAll = refusedAll = stanceChanges = 0; lastTactic = "none";
        foreach (var tn in tactics) { attempted[tn] = 0; accepted[tn] = 0; refused[tn] = 0; }
        lastRefusal.Clear(); stanceUsed.Clear();
        rng = new System.Random(seed);
        UnityEngine.Random.InitState(seed);
        STStanceMods.Seed(seed);     // S1's per-hit Trickster roll
        winFpsSum = 0; winFpsN = 0; winFpsMin = 9999f;
        dStatePrev = D.state; pStatePrev = P.state; dMovePrev = ""; pMovePrev = "";
    }

    void AddRecord(int seed, string endReason)
    {
        var r = new Rec { idx = fightIdx + 1, seed = seed, dur = fightDur, endReason = endReason, dmgDealt = S.dmgDealt, dmgTaken = S.dmgTaken, dHp = D.health, pHp = P.health,
            hitsD = S.hitsD, hitsP = S.hitsP, parriesD = S.parriesD, parriesP = S.parriesP, dodgesD = S.dodgesD, dodgesP = S.dodgesP };
        r.winner = P.Dead && !D.Dead ? Roster.PlayerName : D.Dead && !P.Dead ? Roster.EnemyName : D.Dead && P.Dead ? "both" : "none (time limit)";
        var sb = new StringBuilder();
        foreach (var tn in tactics) { r.acc += accepted[tn]; r.att += attempted[tn]; sumAtt[tn] += attempted[tn]; sumAcc[tn] += accepted[tn]; sb.Append(tn + " " + accepted[tn] + "/" + attempted[tn] + "  "); }
        r.tactics = sb.ToString().TrimEnd();
        recs.Add(r);
        Log("FIGHT RESULT " + r.idx + "/" + seedsN + " seed " + r.seed + " dur " + r.dur.ToString("F1", IC) + " winner " + r.winner + " dmg dealt " + r.dmgDealt.ToString("F0", IC) + " taken " + r.dmgTaken.ToString("F0", IC) + " hits D" + r.hitsD + "/P" + r.hitsP + " | tactics acc/att: " + r.tactics);
    }

    // new fight for the next seed: S1's restart path (STSteer.RestartFight reloads the scene; STController, STAuto and the flags survive it) and wait for fresh, living fighters
    IEnumerator RestartFight()
    {
        var oldD = D; var oldP = P;
        started = false;
        Log("RESTART via STSteer.RestartFight (scene reload)");
        try { STSteer.RestartFight(); } catch (Exception e) { Fail(e, "RestartFight"); yield break; }
        float w0 = Time.realtimeSinceStartup; float lastNote = -1f;
        while (true)
        {
            yield return null;
            var d = STAutoApi.Player; var p = STAutoApi.Enemy;
            if (d != null && p != null && d != oldD && p != oldP && !d.Dead && !p.Dead) { D = d; P = p; break; }
            if (Time.realtimeSinceStartup - w0 > C.restartWaitSeconds) { Fail("restart: no fresh living fighters after " + C.restartWaitSeconds.ToString("F0", IC) + " s"); yield break; }
            if (Time.realtimeSinceStartup - lastNote >= 1f) { lastNote = Time.realtimeSinceStartup; Log("waiting for the restarted fight ..."); }
        }
        for (int i = 0; i < 30; i++) yield return null;
        if (D == null || P == null || D.Dead || P.Dead) { Fail("restart: fighters not usable after settle"); yield break; }
    }

    // ------------------------------------------------------------ the scripted player
    static string Canon(string[] arr, string v) { if (string.IsNullOrEmpty(v)) return null; foreach (var a in arr) if (string.Equals(a, v, StringComparison.OrdinalIgnoreCase)) return a; return null; }

    void Command(float t, FirstCmd fc, string forced)
    {
        try
        {
            if (cur != null && cur.open) CloseCmd("next command");
            slot++;
            var sb = new StringBuilder();
            // stance: change (to a different one, cycling through all four) with probability stanceChance; always on the first slot
            string stNow = STAutoApi.Stance, stNew = null;
            string fcStance = fc != null ? Canon(stances, fc.stance) : null;
            if (forced != null) { }                                              // forced GoForKill: no stance change
            else if (fcStance != null) { if (fcStance != stNow) stNew = fcStance; }
            else if (slot == 1 || rng.NextDouble() < C.stanceChance)
            {
                var opts = new List<string>(); foreach (var s in stances) if (s != stNow) opts.Add(s);
                // prefer the least used stance so all four get exercised, break ties with the seeded rng
                int best = int.MaxValue; var pick = new List<string>();
                foreach (var s in opts) { int u; stanceUsed.TryGetValue(s, out u); if (u < best) { best = u; pick.Clear(); } if (u == best) pick.Add(s); }
                if (pick.Count > 0) stNew = pick[rng.Next(pick.Count)];
            }
            if (stNew != null)
            {
                STAutoApi.SetStance(stNew); stanceChanges++;
                int u2; stanceUsed.TryGetValue(stNew, out u2); stanceUsed[stNew] = u2 + 1;
                sb.Append("stance " + stNow + "->" + stNew);
            }
            // tactic: never-accepted ones first, then the least accepted; ready (enabled, no cooldown) ones before others; GoForKill probe every n-th slot
            string tac = null;
            string fcTac = fc != null ? Canon(tactics, fc.tactic) : null;
            if (forced != null) tac = forced;
            else if (fcTac != null) tac = fcTac;
            else if (C.goForKillTryEvery > 0 && slot % C.goForKillTryEvery == 0 && Array.IndexOf(tactics, "GoForKill") >= 0) tac = "GoForKill";
            else
            {
                int bestScore = int.MaxValue; var pick = new List<string>();
                foreach (var tn in tactics)
                {
                    bool ready = STAutoApi.Enabled(tn) && STAutoApi.Cooldown01(tn) <= 0f;
                    int score = (ready ? 0 : 10000) + accepted[tn] * 10 + (tn == "GoForKill" ? 1 : 0);   // GoForKill rarely wins on count alone: it has its own probe + the Enabled shortcut below
                    if (tn == "GoForKill" && STAutoApi.Enabled(tn) && accepted[tn] == 0) score = -1;     // enabled and not yet used: do it now
                    if (score < bestScore) { bestScore = score; pick.Clear(); }
                    if (score == bestScore) pick.Add(tn);
                }
                if (pick.Count > 0) tac = pick[rng.Next(pick.Count)];
            }
            if (tac != null)
            {
                bool en = STAutoApi.Enabled(tac); float cd = STAutoApi.Cooldown01(tac);
                attempted[tac]++;
                bool ok = STAutoApi.UseTactic(tac);
                string res;
                if (ok) { accepted[tac]++; acceptedAll++; lastTactic = tac; res = "ACCEPTED"; }
                else
                {
                    refused[tac]++; refusedAll++;
                    string reason = !en ? "not enabled (disabled by its condition)" : cd > 0f ? "on cooldown " + cd.ToString("F2", IC) : "refused (enabled, no cooldown: busy / unknown)";
                    lastRefusal[tac] = reason; res = "REFUSED (" + reason + ")";
                }
                if (sb.Length > 0) sb.Append(" + ");
                sb.Append("tactic " + tac + " " + res);
            }
            cmdN++;
            cur = new Cmd { n = cmdN, t = t, text = sb.ToString(), snap = S.Clone(), dist0 = Dist(), distMin = Dist(), distMax = Dist(), stance = STAutoApi.Stance };
            Log("COMMAND #" + cmdN + " slot " + slot + (fc != null ? " (opening)" : forced != null ? " (GoForKill enabled)" : "") + ": " + cur.text + " | stance now " + cur.stance + " | D " + D.state + " hp D" + D.health.ToString("F0", IC) + "/P" + P.health.ToString("F0", IC)
                + " dist " + cur.dist0.ToString("F2", IC) + " cooldowns [" + CooldownText() + "]");
        }
        catch (Exception e) { Fail(e, "Command"); }
    }

    string CooldownText()
    {
        var sb = new StringBuilder();
        foreach (var tn in tactics) { if (sb.Length > 0) sb.Append(' '); sb.Append(tn + "=" + (STAutoApi.Enabled(tn) ? STAutoApi.Cooldown01(tn).ToString("F2", IC) : "off")); }
        return sb.ToString();
    }

    // the outcome of one command = what happened between it and the next one (or the end)
    void CloseCmd(string why)
    {
        var c = cur; if (c == null || !c.open) return;
        c.open = false;
        var a = c.snap;
        string dec = (c.tacticStart != null ? "steer ran " + c.tacticStart + "; " : "") + (c.decision ?? "no " + Roster.PlayerName + " action state observed (Loco: movement only)");
        Log("  OUTCOME #" + c.n + " [" + c.text + "] decision: " + dec + " | dist " + c.dist0.ToString("F2", IC) + "->" + Dist().ToString("F2", IC) + " (min " + c.distMin.ToString("F2", IC) + " max " + c.distMax.ToString("F2", IC) + ")"
            + " | dmg dealt " + (S.dmgDealt - a.dmgDealt).ToString("F1", IC) + " taken " + (S.dmgTaken - a.dmgTaken).ToString("F1", IC)
            + " | hits D" + (S.hitsD - a.hitsD) + " P" + (S.hitsP - a.hitsP)
            + " parries D" + (S.parriesD - a.parriesD) + " P" + (S.parriesP - a.parriesP)
            + " blocks D" + (S.blockedByD - a.blockedByD) + " P" + (S.blockedByP - a.blockedByP)
            + " guardbreaks D" + (S.guardBreaksD - a.guardBreaksD) + " P" + (S.guardBreaksP - a.guardBreaksP)
            + " dodges D" + (S.dodgesD - a.dodgesD) + " P" + (S.dodgesP - a.dodgesP)
            + " attacks D" + (S.attacksD - a.attacksD) + " P" + (S.attacksP - a.attacksP) + " (" + why + ")");
    }

    void Sample(float t)
    {
        if (!C.logSamples) return;
        Log("  SAMPLE D{" + D.state + " hp " + D.health.ToString("F0", IC) + " st " + D.stamina.ToString("F0", IC) + " pos " + V(D.transform.position) + "} P{" + P.state + " hp " + P.health.ToString("F0", IC)
            + " st " + P.stamina.ToString("F0", IC) + " pos " + V(P.transform.position) + "} dist " + Dist().ToString("F2", IC));
    }
    static string V(Vector3 v) { return v.x.ToString("F1", IC) + "," + v.z.ToString("F1", IC); }

    void Summary10(float t)
    {
        float avg = winFpsN > 0 ? winFpsSum / winFpsN : 0f;
        var sb = new StringBuilder();
        sb.Append("SUMMARY t=" + t.ToString("F0", IC) + "s fps avg " + avg.ToString("F1", IC) + " min " + (winFpsN > 0 ? winFpsMin.ToString("F1", IC) : "-"));
        sb.Append(" | hp D" + D.health.ToString("F0", IC) + "/" + D.maxHealth.ToString("F0", IC) + " P" + P.health.ToString("F0", IC) + "/" + P.maxHealth.ToString("F0", IC));
        sb.Append(" | stance " + STAutoApi.Stance + " | tactics acc/att");
        foreach (var tn in tactics) sb.Append(" " + tn + "=" + accepted[tn] + "/" + attempted[tn]);
        sb.Append(" | accepted " + acceptedAll + " refused " + refusedAll + " stanceChanges " + stanceChanges);
        sb.Append(" | last10s dmg dealt " + win.dmgDealt.ToString("F0", IC) + " taken " + win.dmgTaken.ToString("F0", IC) + " hits D" + win.hitsD + "/P" + win.hitsP + " parries D" + win.parriesD + "/P" + win.parriesP + " dodges D" + win.dodgesD + "/P" + win.dodgesP);
        Log(sb.ToString());
        win = new Stats(); winFpsSum = 0; winFpsN = 0; winFpsMin = 9999f;
    }

    // ------------------------------------------------------------ screenshots
    IEnumerator Shot(float t)
    {
        yield return new WaitForEndOfFrame();
        string name = shotPrefix + ((int)t).ToString("D3", IC) + "_" + lastTactic;
        string path = Path.Combine(shotDir, name + ".png");
        try
        {
            string how = SaveShot(path);
            shotN++;
            Log("  SHOT " + name + ".png (" + how + ")");
        }
        catch (Exception e) { Log("  SHOT failed " + name + ": " + e.Message); }
        shotThisFrame = true;   // the readback hitch does not count as a fps dip
    }

    string SaveShot(string path)
    {
        try
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
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

    // ------------------------------------------------------------ summary file
    void WriteSummary(int code, string why, string verdictOverride)
    {
        float dur = started ? Time.realtimeSinceStartup - fightStart : 0f;
        var problems = new List<string>(); var warns = new List<string>();
        if (stExceptions > 0) problems.Add(stExceptions + " steer exception(s)");
        bool haveFight = started && D != null && P != null;
        int ticks = haveFight ? Duel.TickCount - tickStart : 0;
        bool fightEnded = haveFight && (D.Dead || P.Dead);
        if (haveFight)
        {
            if (!(ticks >= 60 && (contactEvents > 0 || S.attacksD + S.attacksP > 0 || fightEnded))) problems.Add("the fight did not progress (ticks " + ticks + ", contacts " + contactEvents + ", attacks " + (S.attacksD + S.attacksP) + ")");
            if (!(dHpMin < dHpStart || pHpMin < pHpStart)) problems.Add("HP never changed");
            foreach (var tn in tactics)
            {
                if (accepted[tn] > 0) continue;
                if (attempted[tn] == 0)
                {
                    if (fightDur <= 0f) fightDur = dur;
                    if (fightDur <= openEnd + 0.5f) warns.Add("tactic " + tn + " not attempted: the fight ended at " + fightDur.ToString("F1", IC) + " s, inside the opening sequence (last opening command at " + openEnd.ToString("F1", IC) + " s)");
                    else problems.Add("tactic " + tn + " was never attempted");
                }
                else warns.Add("tactic " + tn + " never accepted: " + (lastRefusal.ContainsKey(tn) ? lastRefusal[tn] : "?") + " (" + attempted[tn] + " tries)");
            }
        }
        if (foreignExceptions > 0) warns.Add(foreignExceptions + " exception(s) outside steer code (see log)");
        string verdict = verdictOverride ?? (problems.Count > 0 ? "FAIL" : warns.Count > 0 ? "PASS_WITH_WARNINGS" : "PASS");
        if (verdictOverride == null && code == 0 && problems.Count > 0) { /* exit code stays 0: the fight ran, the verdict says what is wrong */ }

        dts.Sort();
        float p1 = 0f; if (dts.Count > 0) { int i = Mathf.Clamp((int)(dts.Count * 0.99f), 0, dts.Count - 1); p1 = 1f / dts[i]; }
        float worst = dts.Count > 0 ? 1f / dts[dts.Count - 1] : 0f;
        var sb = new StringBuilder();
        sb.AppendLine("VERDICT: " + verdict + "   (exit code " + code + ", " + why + ")");
        sb.AppendLine("date " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  res " + Screen.width + "x" + Screen.height + "  seed " + C.seed + "  data " + dataSrc);
        sb.AppendLine("fight: " + dur.ToString("F1", IC) + " s of " + C.seconds.ToString("F0", IC) + " s, ended by " + (fightEnded ? (P != null && P.Dead ? Roster.EnemyName + " death" : Roster.PlayerName + " death") : "time/other") + ", sim ticks " + ticks + ", contacts " + contactEvents);
        if (haveFight) sb.AppendLine("hp: " + Roster.PlayerName + " " + dHpStart.ToString("F0", IC) + " -> " + D.health.ToString("F0", IC) + " (min " + dHpMin.ToString("F0", IC) + ")   " + Roster.EnemyName + " " + pHpStart.ToString("F0", IC) + " -> " + P.health.ToString("F0", IC) + " (min " + pHpMin.ToString("F0", IC) + ")");
        sb.AppendLine("commands: " + cmdN + " (stance changes " + stanceChanges + ", tactics accepted " + acceptedAll + ", refused " + refusedAll + ")   shots " + shotN);
        sb.AppendLine("stances used: " + DictText(stanceUsed));
        sb.AppendLine("tactics (accepted/attempted/refused):");
        foreach (var tn in tactics) sb.AppendLine("  " + tn.PadRight(10) + accepted[tn] + "/" + attempted[tn] + "/" + refused[tn] + (accepted[tn] == 0 ? "   <- " + (lastRefusal.ContainsKey(tn) ? lastRefusal[tn] : "never attempted") : ""));
        sb.AppendLine("totals: damage dealt by " + Roster.PlayerName + " " + S.dmgDealt.ToString("F0", IC) + ", taken " + S.dmgTaken.ToString("F0", IC) + "; hits D" + S.hitsD + "/P" + S.hitsP + "; parries by D" + S.parriesD + "/by P" + S.parriesP
            + "; blocks by D" + S.blockedByD + "/by P" + S.blockedByP + "; guardbreaks on D" + S.guardBreaksD + "/on P" + S.guardBreaksP + "; dodges D" + S.dodgesD + "/P" + S.dodgesP + "; attacks D" + S.attacksD + "/P" + S.attacksP);
        sb.AppendLine("fps: avg " + (allFpsN > 0 ? (allFpsSum / allFpsN).ToString("F1", IC) : "-") + "  1%-low " + p1.ToString("F1", IC) + "  worst frame " + worst.ToString("F1", IC) + "  min(per-frame) " + (allFpsN > 0 ? allFpsMin.ToString("F1", IC) : "-") + "   (screenshot frames excluded; target >= 120 at 1080p)");
        sb.AppendLine("exceptions: steer " + stExceptions + ", foreign " + foreignExceptions);
        foreach (var e in exceptionList) sb.AppendLine("  " + e);
        foreach (var p in problems) sb.AppendLine("PROBLEM: " + p);
        foreach (var w in warns) sb.AppendLine("WARN: " + w);
        File.WriteAllText(summaryPath, sb.ToString());
    }

    // ------------------------------------------------------------ balance summary (-seeds:N)
    void WriteBalance(int code, string why, string verdictOverride)
    {
        string path = Path.Combine(dir, "steer_balance_summary.txt");
        int n = recs.Count;
        float sumDur = 0, sumDealt = 0, sumTaken = 0, sumHD = 0, sumHP = 0; int dWins = 0, pWins = 0; bool bothHit = n > 0;
        foreach (var r in recs)
        {
            sumDur += r.dur; sumDealt += r.dmgDealt; sumTaken += r.dmgTaken; sumHD += r.hitsD; sumHP += r.hitsP;
            if (r.winner == Roster.PlayerName) dWins++; else if (r.winner == Roster.EnemyName) pWins++;
            if (r.hitsD <= 0 || r.hitsP <= 0) bothHit = false;
        }
        float avgDur = n > 0 ? sumDur / n : 0f, winRate = n > 0 ? (float)dWins / n : 0f;
        var reasons = new List<string>();
        if (n < seedsN) reasons.Add("only " + n + " of " + seedsN + " fights completed");
        if (n > 0 && (avgDur < C.balanceMinDuration || avgDur > C.balanceMaxDuration)) reasons.Add("average duration " + avgDur.ToString("F1", IC) + " s outside " + C.balanceMinDuration.ToString("F0", IC) + "-" + C.balanceMaxDuration.ToString("F0", IC));
        if (n > 0 && (winRate < C.balanceMinWinRate || winRate > C.balanceMaxWinRate)) reasons.Add(Roster.PlayerName + " win rate " + (winRate * 100f).ToString("F0", IC) + "% outside " + (C.balanceMinWinRate * 100f).ToString("F0", IC) + "-" + (C.balanceMaxWinRate * 100f).ToString("F0", IC) + "%");
        if (!bothHit) reasons.Add("a side landed no hits in at least one fight");
        string verdict = verdictOverride ?? (reasons.Count == 0 ? "PASS" : "FAIL");
        var sb = new StringBuilder();
        sb.AppendLine("VERDICT: " + verdict + "   (exit code " + code + ", " + why + ")");
        sb.AppendLine("date " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  res " + Screen.width + "x" + Screen.height + "  seeds " + C.seed + ".." + (C.seed + seedsN - 1) + "  fights " + n + "/" + seedsN + "  data " + dataSrc);
        sb.AppendLine("pass rule: average duration " + C.balanceMinDuration.ToString("F0", IC) + "-" + C.balanceMaxDuration.ToString("F0", IC) + " s AND " + Roster.PlayerName + " win rate " + (C.balanceMinWinRate * 100f).ToString("F0", IC) + "-" + (C.balanceMaxWinRate * 100f).ToString("F0", IC) + "% AND both sides land hits in every fight");
        foreach (var r in reasons) sb.AppendLine("PROBLEM: " + r);
        sb.AppendLine();
        sb.AppendLine("per fight (damage dealt = by " + Roster.PlayerName + " to the " + Roster.EnemyName + ", taken = by " + Roster.PlayerName + " from the " + Roster.EnemyName + "; tactics = accepted/attempted):");
        foreach (var r in recs)
            sb.AppendLine("  #" + r.idx + " seed " + r.seed + "  " + r.dur.ToString("F1", IC) + " s  winner " + r.winner + " (" + r.endReason + ")  hp end D" + r.dHp.ToString("F0", IC) + "/P" + r.pHp.ToString("F0", IC)
                + "  dmg dealt " + r.dmgDealt.ToString("F0", IC) + " taken " + r.dmgTaken.ToString("F0", IC) + "  hits D" + r.hitsD + "/P" + r.hitsP + "  parries by D" + r.parriesD + "/P" + r.parriesP + "  dodges D" + r.dodgesD + "/P" + r.dodgesP
                + "\n       tactics: " + r.tactics);
        sb.AppendLine();
        if (n > 0)
        {
            sb.AppendLine("averages over " + n + " fights:");
            sb.AppendLine("  duration " + avgDur.ToString("F1", IC) + " s   " + Roster.PlayerName + " wins " + dWins + "/" + n + " (" + (winRate * 100f).ToString("F0", IC) + "%)   " + Roster.EnemyName + " wins " + pWins + "   no winner " + (n - dWins - pWins));
            sb.AppendLine("  damage dealt by " + Roster.PlayerName + " " + (sumDealt / n).ToString("F1", IC) + "   damage taken by " + Roster.PlayerName + " " + (sumTaken / n).ToString("F1", IC));
            sb.AppendLine("  hits landed: " + Roster.PlayerName + " " + (sumHD / n).ToString("F1", IC) + "   " + Roster.EnemyName + " " + (sumHP / n).ToString("F1", IC));
        }
        sb.AppendLine("tactics, all fights (accepted/attempted):");
        foreach (var tn in tactics) sb.AppendLine("  " + tn.PadRight(10) + sumAcc[tn] + "/" + sumAtt[tn] + (sumAtt[tn] == 0 ? "   <- never attempted" : ""));
        float fpsAvg = allFpsN > 0 ? allFpsSum / allFpsN : 0f;
        sb.AppendLine("fps over all fights: avg " + fpsAvg.ToString("F1", IC) + "  worst frame " + (allFpsN > 0 ? allFpsMin.ToString("F1", IC) : "-"));
        sb.AppendLine("exceptions: steer " + stExceptions + ", foreign " + foreignExceptions);
        foreach (var e in exceptionList) sb.AppendLine("  " + e);
        File.WriteAllText(path, sb.ToString());
    }


    static string DictText(Dictionary<string, int> d)
    {
        var sb = new StringBuilder(); foreach (var kv in d) { if (sb.Length > 0) sb.Append(", "); sb.Append(kv.Key + " x" + kv.Value); }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }
}
#else // FIXHARNESS v1 nl=0
#endif // FIXHARNESS v1
