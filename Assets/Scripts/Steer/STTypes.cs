using System;
using System.Collections.Generic;
using UnityEngine;

// STEER layer shared contract (lane S1). Everything is inert unless steer mode is on (-steer / -steerauto / runtime_flags "steer": true).
public enum STStance { Aggressive, Defensive, Neutral, Trickster }
public enum STTactic { Press, BackOff, Circle, GuardUp, Dodge, Feint, GoForKill }

public static class STSteer
{
    // ---- mode ----
    public static bool Active;                      // steer mode on (set by STController.Boot)
    public static bool AutoLog;                     // -steerauto: Log() appends to steer_auto_log.txt
    public static STStance Stance { get; private set; }

    // ---- events (UI / tells / auto subscribe) ----
    public static event Action<STStance> OnStance;
    public static event Action<STTactic, bool> OnTactic;      // (tactic, accepted)
    public static event Action<string> OnFeed;                // one-liners for the UI feed
    public static event Action<STTactic> OnTacticStart;       // accepted tactic began running
    public static event Action<STTactic> OnTacticEnd;         // tactic finished (resolved or timed out)
    public static event Action OnFightReset;                  // new fight (the enemy revived / scene restarted)

    // ---- implementation providers (set by STController; null-safe defaults so lanes compile and run without it) ----
    public static Func<STTactic, bool> TacticUser;            // asks the controller to start a tactic
    public static Func<STTactic, float> CooldownProvider;     // 0 ready .. 1 just used
    public static Func<STTactic, bool> EnabledProvider;
    public static Func<STTactic?> RunningProvider;            // tactic currently overriding the AI, or null
    public static Func<STTactic, float> TimeLeftProvider;     // seconds of the running tactic left (0 if not running)
    public static Func<DuelFighter, DuelFighter, float> DamageMulProvider;

    // hook target: AIBrain.Update calls STSteer.PreBrain(this) every frame before Decide (no-op unless steer is active)
    public static Action<AIBrain> PreBrainHandler;
    public static void PreBrain(AIBrain b) { if (Active && PreBrainHandler != null) PreBrainHandler(b); }
    // new fight: reloads the active scene (the controller survives it); UI "Next opponent" calls this
    public static void RestartFight()
    {
        var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        UnityEngine.SceneManagement.SceneManager.LoadScene(sc.buildIndex);
    }

    // ---- fighters (set by STController once found) ----
    public static DuelFighter Player, Enemy;      // Roster.Player / Roster.Enemy fighters

    public static void SetStance(STStance s)
    {
        bool changed = s != Stance; Stance = s;
        if (OnStance != null) OnStance(s);
        if (changed) Feed("Stance: " + s);
        Log("stance:" + s, "", "");
    }
    // used by the controller at reset (no event spam)
    public static void ResetStance(STStance s) { Stance = s; }

    public static bool UseTactic(STTactic t)
    {
        bool ok = Active && TacticUser != null && TacticUser(t);
        if (OnTactic != null) OnTactic(t, ok);
        return ok;
    }
    public static float Cooldown01(STTactic t) { return CooldownProvider != null ? Mathf.Clamp01(CooldownProvider(t)) : 0f; }
    public static bool Enabled(STTactic t) { return Active && EnabledProvider != null && EnabledProvider(t); }
    public static STTactic? Running { get { return RunningProvider != null ? RunningProvider() : null; } }
    public static bool IsRunning(STTactic t) { var r = Running; return r.HasValue && r.Value == t; }
    public static float TimeLeft(STTactic t) { return TimeLeftProvider != null ? TimeLeftProvider(t) : 0f; }

    public static void Feed(string line) { if (OnFeed != null && !string.IsNullOrEmpty(line)) OnFeed(line); }
    public static void RaiseTacticStart(STTactic t) { if (OnTacticStart != null) OnTacticStart(t); }
    public static void RaiseTacticEnd(STTactic t) { if (OnTacticEnd != null) OnTacticEnd(t); }
    public static void RaiseFightReset() { if (OnFightReset != null) OnFightReset(); }

    // ---- logging: command -> AI decision -> outcome; appended to <exe dir>\steer_auto_log.txt when -steerauto ----
    static string logPath; static bool logStarted;
    public static string LogPath
    {
        get
        {
            if (logPath == null)
            {
                string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
                logPath = System.IO.Path.Combine(dir, "steer_auto_log.txt");
            }
            return logPath;
        }
    }
    public static void Log(string command, string decision, string outcome)
    {
        if (!AutoLog) return;
        try
        {
            if (!logStarted) { logStarted = true; System.IO.File.WriteAllText(LogPath, "# steer_auto_log " + DateTime.Now.ToString("s") + "\n"); }
            string line = "t=" + Time.realtimeSinceStartup.ToString("0.00") + " tick=" + Duel.TickCount + " | cmd=" + (command ?? "") + " | decision=" + (decision ?? "") + " | outcome=" + (outcome ?? "") + "\n";
            System.IO.File.AppendAllText(LogPath, line);
        }
        catch (Exception e) { Debug.LogWarning("[steer] log failed: " + e.Message); }
    }

    // ---- data (steer.json, per-lane blocks) ----
    static string cfgText;
    public static string ConfigText
    {
        get
        {
            if (cfgText == null)
            {
                try
                {
                    string p = Application.streamingAssetsPath + "/steer.json";
                    if (System.IO.File.Exists(p)) cfgText = System.IO.File.ReadAllText(p);
                    else { p = Application.dataPath + "/Data/steer.json"; if (System.IO.File.Exists(p)) cfgText = System.IO.File.ReadAllText(p); }
                }
                catch (Exception e) { Debug.LogWarning("[steer] steer.json unreadable: " + e.Message); }
                if (cfgText == null) cfgText = "{}";
            }
            return cfgText;
        }
    }
    public static void ReloadConfig() { cfgText = null; }
    // raw JSON text of a top-level object block ("stances", "tells", "ui", "auto", ...) or "{}" when missing
    public static string Block(string key) { return STJson.Block(ConfigText, key); }
    // parse a block into T (JsonUtility); a default-constructed T when missing/broken
    public static T Load<T>(string key) where T : new()
    {
        try { string b = Block(key); if (b != "{}") return JsonUtility.FromJson<T>(b); }
        catch (Exception e) { Debug.LogWarning("[steer] block '" + key + "': " + e.Message); }
        return new T();
    }

    // damage multiplier for one resolved hit (hook in Duel.Resolve); 1 when steer is off
    public static float DamageMul(DuelFighter attacker, DuelFighter defender) { return Active && DamageMulProvider != null ? DamageMulProvider(attacker, defender) : 1f; }

    public static void ResetStatics()
    {
        Active = false; AutoLog = false; Stance = STStance.Neutral; Player = Enemy = null; cfgText = null; logPath = null; logStarted = false;
        OnStance = null; OnTactic = null; OnFeed = null; OnTacticStart = null; OnTacticEnd = null; OnFightReset = null;
        TacticUser = null; CooldownProvider = null; EnabledProvider = null; RunningProvider = null; TimeLeftProvider = null; DamageMulProvider = null; PreBrainHandler = null;
    }
}

// minimal JSON helper (JsonUtility cannot pull a named sub-object out of a larger file)
public static class STJson
{
    // text of the object value of a top-level key (balanced braces, string aware), "{}" when absent
    public static string Block(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "{}";
        int i = 0, depth = 0, n = json.Length;
        string needle = "\"" + key + "\"";
        while (i < n)
        {
            char c = json[i];
            if (c == '"')
            {
                int s = i; i++;
                while (i < n && json[i] != '"') { if (json[i] == '\\') i++; i++; }
                i++;
                if (depth == 1 && i - s == needle.Length && string.CompareOrdinal(json, s, needle, 0, needle.Length) == 0)
                {
                    int j = i; while (j < n && (char.IsWhiteSpace(json[j]) || json[j] == ':')) j++;
                    if (j < n && json[j] == '{') return Balanced(json, j);
                }
                continue;
            }
            if (c == '{' || c == '[') depth++; else if (c == '}' || c == ']') depth--;
            i++;
        }
        return "{}";
    }
    static string Balanced(string s, int start)
    {
        int depth = 0;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { i++; while (i < s.Length && s[i] != '"') { if (s[i] == '\\') i++; i++; } continue; }
            if (c == '{') depth++; else if (c == '}') { depth--; if (depth == 0) return s.Substring(start, i - start + 1); }
        }
        return "{}";
    }
}
