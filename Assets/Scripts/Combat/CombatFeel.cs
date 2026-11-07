using System;
using System.IO;
using UnityEngine;

// Combat FEEL data layer (lane P1): input fire mode, press queue, cancel windows and the input-to-motion latency probe. Every number lives in Assets/Data/cancel.json
// (copy in StreamingAssets for the player). Lookup order: Resources/cancel, StreamingAssets, Application.dataPath/Data (same as AttackClipTable).
// A missing / unparsable file or "enabled": false leaves DuelFighter and PlayerBrain on the old behaviour exactly.
public static class CombatFeel
{
    [Serializable] public class InputCfg { public string strikeFire = "down"; public float heavyHoldSeconds = 0.22f, holdToHeavyMs = 180f; public int holdToHeavyUntilTick = 16; public float holdToHeavyGiveUpS = 0.6f, stickLight = 0.5f; /* HCAUDIT1 v104 */ }   // strikeFire: "release" (V3: a tap fires on key-up) | "down" (light fires on key-down, heavy only by G / T)
    [Serializable] public class QueueCfg { public bool enabled = true; public int extraTicks = 11; public int maxTicks = 40; public string[] states = new string[0]; }
    [Serializable] public class Row { public string move = "*"; public string to; public int earliest = -1; public bool onHitOnly; }   // earliest = Recovery tick t (1 = first tick after the active window); -1 = never
    [Serializable]
    public class Data
    {
        public bool enabled = true;
        public string scope = "player";        // "player" (PlayerBrain fighters only; the AI keeps its tuned timings) | "all"
        public bool latencyProbe; public float latencyTargetMs = 100f; public string[] latencyTargetMoves = new string[] { "light_left", "light_right", "thrust" };
        public InputCfg input = new InputCfg();
        public QueueCfg queue = new QueueCfg();
        public Row[] rows = new Row[0];
    }

    static Data data; static bool loaded;
    public static Data Get() { if (!loaded) Load(); return data; }
    public static void Reload() { loaded = false; Load(); }

    // true when this fighter takes the feel layer (cancel table + queue)
    public static bool Applies(DuelFighter f)
    {
        var d = Get();
        return d.enabled && f != null && (d.scope == "all" || f.IsPlayer);
    }

    // earliest Recovery tick for move -> to; a move row wins over "*"; -1 = no row / never
    public static int Earliest(string move, string to)
    {
        var rows = Get().rows; int any = -1;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; if (r == null || r.to != to) continue;
            if (r.move == move) return r.earliest;
            if (r.move == "*") any = r.earliest;
        }
        return any;
    }
    public static bool OnHitOnly(string move, string to)
    {
        var rows = Get().rows; bool any = false;
        for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r == null || r.to != to) continue; if (r.move == move) return r.onHitOnly; if (r.move == "*") any = r.onHitOnly; }
        return any;
    }

    // press queue: while a move / dodge / stagger is running, a press stays alive until the move can take it (remaining ticks + the normal buffer, capped)
    public static int QueueTtl(DuelFighter f, int baseTtl)
    {
        var d = Get();
        if (!Applies(f) || !d.queue.enabled) return baseTtl;
        if (!StateListed(d.queue.states, f.state)) return baseTtl;
        int left = f.TicksLeft();
        return Mathf.Clamp(left + d.queue.extraTicks, baseTtl, Mathf.Max(baseTtl, d.queue.maxTicks));
    }
    static bool StateListed(string[] s, FState st)
    {
        if (s == null) return false; string n = st.ToString();
        for (int i = 0; i < s.Length; i++) if (s[i] == n) return true;
        return false;
    }

    static void Load()
    {
        loaded = true; data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("cancel");
            if (ta != null) json = ta.text;
#if UNITY_EDITOR
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/cancel.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "cancel.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/cancel.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("CombatFeel: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { data.enabled = false; return; }   // no file: old behaviour
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) { data.enabled = false; return; }
            if (d.input == null) d.input = new InputCfg();
            if (d.queue == null) d.queue = new QueueCfg();
            if (d.rows == null) d.rows = new Row[0];
            if (d.input.heavyHoldSeconds <= 0f) d.input.heavyHoldSeconds = 0.22f;
            if (string.IsNullOrEmpty(d.input.strikeFire)) d.input.strikeFire = "down";
            if (d.input.holdToHeavyMs <= 0f) d.input.holdToHeavyMs = 180f;
            if (d.input.holdToHeavyUntilTick <= 0) d.input.holdToHeavyUntilTick = 16;
            if (d.latencyTargetMs <= 0f) d.latencyTargetMs = 100f;
            if (d.latencyTargetMoves == null || d.latencyTargetMoves.Length == 0) d.latencyTargetMoves = new string[] { "light_left", "light_right", "thrust" };
            data = d;
        }
        catch (Exception ex) { Debug.LogError("CombatFeel: parse failed: " + ex.Message); data = new Data { enabled = false }; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded = false; data = null; LatencyProbe.ResetStatics(); }
}

// Input-to-motion latency probe (flag: cancel.json "latencyProbe": true, or the launch arg -latencyprobe). Logs, per player action, the frames and milliseconds
// from the physical key-down to (1) the sim starting the move (Duel tick) and (2) the first frame the Animator is crossfading into it, and keeps a per-move summary.
// Summary for BalanceAuto / the build summary: LatencyProbe.SummaryJson() (also printed as one "[LatencySummary] {json}" line and written to latency_summary.json
// at quit: path from the launch arg -latencyout:<file>, else Application.persistentDataPath). A harness without keys can call KeyDown(label) before its Press and Record() itself.
// Off = no driver object is created and every call returns at once.
public static class LatencyProbe
{
    class Agg { public string key, label, move; public int n; public double simSum, animSum, simMax, animMax; }
    static readonly System.Collections.Generic.Dictionary<string, Agg> agg = new System.Collections.Generic.Dictionary<string, Agg>();
    static bool cli, cliRead; static string outArg;
    public static bool On
    {
        get
        {
            if (!cliRead)
            {
                cliRead = true;
                foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-latencyprobe") cli = true; else if (a.StartsWith("-latencyout:")) outArg = a.Substring(12); }
            }
            return cli || CombatFeel.Get().latencyProbe;
        }
    }
    static string label; static int downFrame, startFrame; static float downReal, startReal; static bool started; static DuelFighter who; static string what, startMove;
    static GameObject driver;

    internal static void ResetStatics() { label = null; started = false; who = null; driver = null; cliRead = false; agg.Clear(); }

    public static void KeyDown(string l)
    {
        if (!On) return;
        if (driver == null) { driver = new GameObject("LatencyProbe"); UnityEngine.Object.DontDestroyOnLoad(driver); driver.AddComponent<LatencyProbeDriver>(); }
        label = l; downFrame = Time.frameCount; downReal = Time.realtimeSinceStartup; started = false; who = null;
    }

    static string Category(string l) { return l == "dodge" ? "dodge" : l == "move" ? "move" : "attack"; }

    // called by DuelFighter when the sim starts something (category "attack" | "dodge" | "move"; moveId = the move / dodge kind)
    public static void Started(DuelFighter f, string category, string moveId = "")
    {
        if (label == null || started || !f.IsPlayer || Category(label) != category) return;
        started = true; who = f; what = category; startMove = moveId; startFrame = Time.frameCount; startReal = Time.realtimeSinceStartup;
    }

    public static void Record(string lbl, string move, float simMs, float animMs)
    {
        string key = lbl + ">" + move; Agg a;
        if (!agg.TryGetValue(key, out a)) { a = new Agg { key = key, label = lbl, move = move }; agg[key] = a; }
        a.n++; a.simSum += simMs; a.animSum += animMs; a.simMax = Math.Max(a.simMax, simMs); a.animMax = Math.Max(a.animMax, animMs);
    }

    // driver LateUpdate (after the Animator evaluated): report once
    internal static void Poll()
    {
        if (!started || who == null) return;
        var fi = who.GetComponent<Fighter>(); var a = fi != null ? fi.anim : null;
        bool tr = a != null && a.IsInTransition(0);
        if (!tr && Time.frameCount - startFrame < 3) return;   // wait up to 3 frames for the crossfade to show up
        float ms1 = (startReal - downReal) * 1000f, ms2 = (Time.realtimeSinceStartup - downReal) * 1000f;
        Debug.Log("[Latency] " + label + " (" + what + " " + startMove + "): key->sim start " + (startFrame - downFrame) + " frames / " + ms1.ToString("0.0") + " ms; key->animator " + (Time.frameCount - downFrame) + " frames / " + ms2.ToString("0.0") + " ms"
            + " (crossfade " + (tr ? "yes" : "NO") + ", state " + (fi != null ? fi.Current : "?") + ", frame dt " + (Time.unscaledDeltaTime * 1000f).ToString("0.0") + " ms)");
        Record(label, string.IsNullOrEmpty(startMove) ? what : startMove, ms1, ms2);
        started = false; label = null; who = null;
    }

    static bool IsTarget(string move) { var t = CombatFeel.Get().latencyTargetMoves; for (int i = 0; i < t.Length; i++) if (t[i] == move) return true; return false; }
    static string F(double v) { return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }

    // {"targetMs":100,"pass":true,"moves":[{"key":"strike>light_right","n":3,"simMeanMs":9.1,"simMaxMs":16.7,"motionMeanMs":24.0,"motionMaxMs":33.1,"target":true,"pass":true}]}
    // motion = key-down to the first animator crossfade (what the player sees); target moves (light, stab) must have motionMaxMs <= targetMs; "pass" is true with no samples too
    public static string SummaryJson()
    {
        float tgt = CombatFeel.Get().latencyTargetMs; bool all = true;
        var sb = new System.Text.StringBuilder(); bool first = true;
        foreach (var a in agg.Values)
        {
            bool t = IsTarget(a.move), ok = !t || a.animMax <= tgt; if (!ok) all = false;
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"key\":\"" + a.key + "\",\"n\":" + a.n + ",\"simMeanMs\":" + F(a.simSum / a.n) + ",\"simMaxMs\":" + F(a.simMax) + ",\"motionMeanMs\":" + F(a.animSum / a.n) + ",\"motionMaxMs\":" + F(a.animMax)
                + ",\"target\":" + (t ? "true" : "false") + ",\"pass\":" + (ok ? "true" : "false") + "}");
        }
        return "{\"targetMs\":" + tgt.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ",\"pass\":" + (all ? "true" : "false") + ",\"moves\":[" + sb + "]}";
    }

    internal static void Flush()
    {
        if (agg.Count == 0) return;
        string j = SummaryJson(); Debug.Log("[LatencySummary] " + j);
        try { string p = string.IsNullOrEmpty(outArg) ? Path.Combine(Application.persistentDataPath, "latency_summary.json") : outArg; File.WriteAllText(p, j); }
        catch (Exception ex) { Debug.LogWarning("LatencyProbe: could not write the summary: " + ex.Message); }
    }
}

[DefaultExecutionOrder(600)]
public class LatencyProbeDriver : MonoBehaviour { void LateUpdate() { LatencyProbe.Poll(); } void OnApplicationQuit() { LatencyProbe.Flush(); } }
