using System;
using System.IO;
using UnityEngine;

// Alpha, lane C2: the beat -> Animator state table (Assets/Data/alpha_playback.json) and the derived timing, read by ADPlayback / ADFinisherPlayer.
// Lookup order like the other tables: StreamingAssets, then Application.dataPath/Data. Everything has a code fallback (the old ADPlaybackCfg names),
// so a missing/broken file only loses the data, never the game. The names are validated against the controllers by Tools/validate_alpha_playback.py.
[Serializable] public class ADPlayRow { public string character = "", group = "", key = "", state = "", fallback = ""; public string[] press = new string[0], states = new string[0], fallbackStates = new string[0]; public bool alternate, deferred, pending; }
[Serializable] public class ADMoveTiming { public string move = "", id = ""; public int windup, active, recovery; public float contactTick, contactS, totalS, beatTimeoutS, slowFromS, slowToS; }
[Serializable] public class ADDefTiming { public string kind = ""; public int ticks, iframeStart, iframeEnd; public float seconds; }
[Serializable] public class ADRoundTiming { public int beats = 3; public float typicalBeatS = 2.5f, maxBeatS = 7f, roundMaxS = 21f, roundTypicalS = 7.5f, replayStartDelayS = 0.4f; }
[Serializable] public class ADTimingBlock { public int tickHz = 60; public float slowWindowS = 0.1f; public ADMoveTiming[] moves = new ADMoveTiming[0]; public ADDefTiming[] defence = new ADDefTiming[0]; public ADRoundTiming round = new ADRoundTiming(); }
[Serializable] public class ADPlayFile { public int version = 1; public ADPlayRow[] rows = new ADPlayRow[0]; public ADTimingBlock timing = new ADTimingBlock(); }
[Serializable] class ADArenaRow { public bool enabled = true; public float xMin = -3.8f, xMax = 3.8f, zMin = 8f, zMax = 22f; }

public static class ADPlayTable
{
    static ADPlayFile data; static ADArenaRow arena;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; arena = null; }

    public static ADPlayFile Data
    {
        get
        {
            if (data != null) return data;
            foreach (var p in new[] { Application.streamingAssetsPath + "/alpha_playback.json", Application.dataPath + "/Data/alpha_playback.json" })
            {
                try { if (!File.Exists(p)) continue; var d = JsonUtility.FromJson<ADPlayFile>(File.ReadAllText(p)); if (d != null && d.rows != null) { data = d; break; } }
                catch (Exception e) { Debug.LogWarning("[ADPlayTable] " + p + ": " + e.Message); }
            }
            if (data == null) { data = new ADPlayFile(); Debug.LogWarning("[ADPlayTable] alpha_playback.json not found: code fallbacks"); }
            if (data.timing == null) data.timing = new ADTimingBlock();
            return data;
        }
    }

    // feature flags live in duel_rules.json "rules" (ADFlags, lane A2); one source of truth
    public static bool CritCam { get { return ADFlags.CritCam; } }
    public static bool Finishers { get { return ADFlags.Finishers; } }
    public static bool Specials { get { return ADFlags.Specials; } }

    public static string CharKey(DuelFighter f)
    {
        if (f == null) return "";
        return f.name.Contains("Pitborn") || f.transform.root.name.Contains("Pitborn") ? "Pitborn" : "Dracula";
    }

    public static ADPlayRow Row(string ch, string group, string key)
    {
        var rows = Data.rows; if (rows == null) return null;
        for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r != null && r.group == group && r.key == key && r.character == ch) return r; }
        return null;
    }

    static bool HasState(DuelFighter f, string n)
    {
        if (f == null || string.IsNullOrEmpty(n)) return false;
        var fi = f.GetComponent<Fighter>(); return fi != null && fi.anim != null && fi.anim.HasState(0, Animator.StringToHash(n));
    }

    // state of a table row for this fighter (state, else the row's fallback, else `code`), checked against the live Animator; null-safe
    public static string State(DuelFighter f, string group, string key, string code)
    {
        var r = Row(CharKey(f), group, key);
        if (r != null)
        {
            if (HasState(f, r.state)) return r.state;
            if (HasState(f, r.fallback)) return r.fallback;
        }
        return code;
    }

    // hit reaction of the VICTIM by hit location; back = hit from behind
    public static string Hit(DuelFighter victim, ADLoc loc, bool heavy, bool back, string code)
    {
        string l = loc == ADLoc.Head ? "head" : loc == ADLoc.Arm ? "arm" : loc == ADLoc.Leg ? "leg" : "torso";
        return State(victim, "hit", l + (heavy ? ".heavy" : back ? ".lightBack" : ".light"), code);
    }
    public static string Reaction(DuelFighter f, string key, string code) { return State(f, "reaction", key, code); }

    // press ids of a move for this fighter (empty = use the code fallback)
    public static string[] Press(DuelFighter f, ADMove m)
    {
        var r = Row(CharKey(f), "move", m.ToString());
        return r != null && r.press != null && r.press.Length > 0 ? r.press : null;
    }
    public static bool Alternates(DuelFighter f, ADMove m) { var r = Row(CharKey(f), "move", m.ToString()); return r != null && r.alternate; }

    // ---- timing (derived by Tools/derive_alpha_timing.py) ----
    public static ADMoveTiming Timing(ADMove m)
    {
        var mv = Data.timing.moves; if (mv == null) return null;
        string n = m.ToString(); for (int i = 0; i < mv.Length; i++) if (mv[i] != null && mv[i].move == n) return mv[i];
        return null;
    }
    // safety timeout (real seconds) of one beat: the slowest move of the two plus the reaction allowance already inside beatTimeoutS; never above `cap`
    public static float BeatTimeout(ADBeat b, float cap)
    {
        float t = 0f; var ta = b != null ? Timing(b.a) : null; var tb = b != null ? Timing(b.b) : null;
        if (ta != null) t = Mathf.Max(t, ta.beatTimeoutS); if (tb != null) t = Mathf.Max(t, tb.beatTimeoutS);
        if (t <= 0f) t = cap;
        return Mathf.Min(Mathf.Max(t, 2f), cap > 0f ? cap : 7f);
    }

    // ---- arena (arena_limits.json): staging goals are clamped inside it ----
    public static Vector3 ClampToArena(Vector3 p, float margin = 0.3f)
    {
        if (arena == null)
        {
            arena = new ADArenaRow();
            foreach (var path in new[] { Application.streamingAssetsPath + "/arena_limits.json", Application.dataPath + "/Data/arena_limits.json" })
            {
                try { if (!File.Exists(path)) continue; var a = JsonUtility.FromJson<ADArenaRow>(File.ReadAllText(path)); if (a != null) arena = a; break; } catch { }
            }
        }
        if (!arena.enabled) return p;
        p.x = Mathf.Clamp(p.x, arena.xMin + margin, arena.xMax - margin); p.z = Mathf.Clamp(p.z, arena.zMin + margin, arena.zMax - margin);
        return p;
    }
}
