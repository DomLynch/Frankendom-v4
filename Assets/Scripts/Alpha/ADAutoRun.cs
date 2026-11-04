using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// ALPHA headless proof mode (-alphaauto), created by ADDuelController only when that flag is on.
//   <exe dir>\alpha_auto_log.txt     one line at a time, written immediately (a crash still leaves the log): per round plans, beats, outcomes, HP, wounds, finisher id, fps
//   <exe dir>\alpha_shots\*.png      plan screen per round, first contact of each beat, end of each beat, the kill, the end screen
//   exit code 0 = duel(s) finished and the end screen was shown for autoEndScreenSeconds; 2 = internal exception (stack in the log); 3 = -alphatimeout:<s> (default rules.alphaTimeoutSeconds = 300)
// Options: -alphatimeout:<s>  -alphafights:<n> (default 1; n > 1 also exercises the restart / revive path)  -alphaout:<dir> (default: the exe directory)  -alphastrict (any exception = exit 2)
public class ADAutoRun : MonoBehaviour
{
    public static ADAutoRun Instance;
    public static int FightsTarget = 1;
    public string Dir { get; private set; }
    public string LogPath { get; private set; }
    string shotDir; float timeout; float started; bool exiting; int exitCode; float exitAt;
    string tag = "boot"; string lastContactTag = "";
    bool strict; int foreignExceptions;
    float fpsSum; int fpsN; float fpsMin = 9999f; string finisherId = "";

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }

    public static ADAutoRun Create(GameObject host, float defaultTimeout)
    {
        if (Instance != null) return Instance;
        var r = host.AddComponent<ADAutoRun>();
        r.Init(defaultTimeout);
        return r;
    }

    void Init(float defaultTimeout)
    {
        Instance = this;
        started = Time.realtimeSinceStartup;
        string d = Arg("-alphaout:");
        try { Dir = !string.IsNullOrEmpty(d) ? d : Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)); } catch { Dir = "."; }
        if (string.IsNullOrEmpty(Dir)) Dir = ".";
        try { Directory.CreateDirectory(Dir); } catch { }
        LogPath = Path.Combine(Dir, "alpha_auto_log.txt");
        shotDir = Path.Combine(Dir, "alpha_shots");
        try { Directory.CreateDirectory(shotDir); } catch (Exception e) { Debug.LogWarning("[alphaauto] cannot create " + shotDir + ": " + e.Message); }
        try { File.WriteAllText(LogPath, ""); } catch (Exception e) { Debug.LogWarning("[alphaauto] cannot write " + LogPath + ": " + e.Message); }
        timeout = defaultTimeout > 0f ? defaultTimeout : 300f;
        float t; var ts = Arg("-alphatimeout:"); if (ts != null && float.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t) && t > 0f) timeout = t;
        int n; var fs = Arg("-alphafights:"); if (fs != null && int.TryParse(fs, out n) && n > 0) FightsTarget = n;
        strict = Flag("-alphastrict");
        Application.logMessageReceived += OnUnityLog;
        ContactRelay.OnContact += OnContact;
        ADFinisherPlayer.OnFinisherStart += OnFinisher;
        Log("alphaauto start " + DateTime.Now.ToString("s") + " unity " + Application.unityVersion + " res " + Screen.width + "x" + Screen.height + " timeout " + timeout + "s fights " + FightsTarget
            + " flags wounds=" + ADFlags.Wounds + " gore=" + ADFlags.Gore + " critCam=" + ADFlags.CritCam + " specials=" + ADFlags.Specials + " finishers=" + ADFlags.Finishers);
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityLog; ContactRelay.OnContact -= OnContact; ADFinisherPlayer.OnFinisherStart -= OnFinisher;
        if (Instance == this) Instance = null;
    }

    // ---- hooks from the controller ----
    public void Hook(ADRules r)
    {
        if (r == null) return;
        r.OnPlan += round => Log("PLAN round " + round + " hp D" + SafeHp(r.Dracula) + " P" + SafeHp(r.Pitborn));
        r.OnBeat += b => Log("  beat " + b.round + "." + b.beat + " " + b.a + " vs " + b.b + " -> " + b.outA + "/" + b.outB + " dmg D-" + b.dmgToA + " P-" + b.dmgToB + (b.crit ? " CRIT" : "") + (b.killed ? " KILL(" + b.victim + ")" : "") + " | " + b.reason);
        r.OnRoundEnd += rr => RoundEnd(r, rr);
        r.OnKill += (w, b) => Log("KILL winner " + w + " at beat " + b.round + "." + b.beat);
    }

    static int SafeHp(ADFighter f) { return f != null ? f.hp : -1; }

    void RoundEnd(ADRules r, ADRoundResult rr)
    {
        if (rr == null) return;
        float avg = fpsN > 0 ? fpsSum / fpsN : 0f;
        string w = "";
        try { w = " woundsD[" + string.Join(",", Array.ConvertAll(r.Dracula.wounds, x => x.ToString())) + "] woundsP[" + string.Join(",", Array.ConvertAll(r.Pitborn.wounds, x => x.ToString())) + "]"; } catch { }
        Log("ROUND " + rr.round + " plans D{" + rr.planA + "} P{" + rr.planB + "}" + (rr.timedOutA || rr.timedOutB ? " (timeout repeat)" : "") + " stance D:" + rr.stanceNoteA + " P:" + rr.stanceNoteB
            + " hp D" + SafeHp(r.Dracula) + " P" + SafeHp(r.Pitborn) + w + " finisher " + (string.IsNullOrEmpty(finisherId) ? "none" : finisherId) + " fps avg " + avg.ToString("F1") + " min " + (fpsN > 0 ? fpsMin.ToString("F1") : "-") + (rr.killed ? " KILLED winner " + rr.winner : ""));
        fpsSum = 0; fpsN = 0; fpsMin = 9999f; finisherId = "";
    }

    void OnFinisher(string id, ADBeat b) { finisherId = id; Log("  finisher " + id); }

    public void SetTag(string t) { tag = t; }
    public void Shot(string name)
    {
        if (exiting) return;
        try { ScreenCapture.CaptureScreenshot(Path.Combine(shotDir, name + ".png")); Log("  shot " + name); }
        catch (Exception e) { Log("  shot failed " + name + ": " + e.Message); }
    }
    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (tag == lastContactTag || exiting) return;
        lastContactTag = tag; Shot(tag + "_contact_" + type);
    }

    // ---- logging / exit ----
    public void Log(string line)
    {
        string s = (Time.realtimeSinceStartup - started).ToString("F1").PadLeft(6) + "s " + line;
        try { File.AppendAllText(LogPath, s + "\n"); } catch { }
        Debug.Log("[alphaauto] " + line);
    }

    public void Fail(Exception e, string where)
    {
        Log("EXCEPTION in " + where + ": " + e);   // full stack trace
        Exit(2, "exception in " + where);
    }
    public void Fail(string msg)
    {
        Log("ERROR: " + msg);
        Exit(2, msg);
    }

    public void Exit(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime;
        Log("EXIT code " + code + " (" + why + ")" + (foreignExceptions > 0 ? " foreignExceptions=" + foreignExceptions : ""));
        try { ADTime.Restore(); } catch { }
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
    public bool Exiting { get { return exiting; } }

    void OnUnityLog(string msg, string stack, LogType type)
    {
        if (type != LogType.Exception || exiting) return;
        bool mine = Regex.IsMatch(stack ?? "", @"(^|\s)AD[A-Z]\w*[:\.]") || Regex.IsMatch(msg ?? "", @"\bAD[A-Z]\w*\b");
        if (mine || strict) { Log("UNHANDLED EXCEPTION (alpha): " + msg + "\n" + stack); Exit(2, "unhandled exception"); }
        else { foreignExceptions++; Log("exception outside alpha (counted, not fatal; -alphastrict makes it fatal): " + msg + "\n" + stack); }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f && !exiting) { float f = 1f / dt; fpsSum += f; fpsN++; if (f < fpsMin) fpsMin = f; }
        if (exiting)
        {
#if !UNITY_EDITOR
            if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode);   // Application.Quit(code) sometimes lingers
#endif
            return;
        }
        if (Time.realtimeSinceStartup - started > timeout) { Log("TIMEOUT after " + timeout + "s, phase " + (ADDuelController.Rules != null ? ADDuelController.Rules.Phase.ToString() : "no rules")); Exit(3, "timeout"); }
    }
}
