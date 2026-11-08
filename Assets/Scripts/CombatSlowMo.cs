using System;
using System.Collections.Generic;
using UnityEngine;

// V65CAM: brief gameplay slow-mo on a parry, a crit (counter / rear / "critical" blow by the player) and a deathblow that is NOT taken over by a finisher. Data: slow_mo.json (StreamingAssets, then Assets/Data).
//  Rules: ONE dip at a time (a second request merges: the lower scale wins, the hold is extended but never past maxSeconds in total: dips never multiply or stack); all timers on unscaled time;
//  a cooldown between dips; never while a finisher owns the clock (RTFinisher.Busy / FinisherTime.Slowed / FinisherCamera.Active) and a running dip is dropped the frame a finisher starts;
//  Time.timeScale is only ever written by this class between a start and its end, then set back to the value it had (1); fixedDeltaTime follows.
//  WATCHDOG: if the timeScale is below 1 (and above 0 = paused) and nobody owns it (not this class, not FinisherTime / a finisher) for watchdogS seconds, it is forced back to 1 and logged.
//  Hit-stop (PresentationClock) is on unscaled time and is unaffected; UI that runs on unscaled time (GameHUD, CombatHudBars) is too. The finisher slow-mo curves stay in finishers.json (FinisherTime).
public static class CombatSlowMo
{
    [Serializable] public class ClassRow { public string id; public float scale = 0.4f, holdS = 0.12f, easeInS = 0.03f, easeOutS = 0.2f, delayS = 0f; public bool attackerPlayerOnly; }
    [Serializable]
    public class Row
    {
        public bool enabled = true;
        public float minScale = 0.2f, maxSeconds = 0.9f, cooldownS = 1.2f, watchdogS = 2f;
        public ClassRow[] classes = new ClassRow[0];
    }

    static Row cfg; static CameraRig rig; static bool owns; static float baseFixed;   // PERF3 v100: was 0.02 (stale vs the 60 Hz project); always read from Time.fixedDeltaTime (ResetStatics / Boot)
    static string pending; static float pendingAt; static float lastStart = -999f;
    static float scale = 1f, age, holdEnd, easeIn, easeOut; static float orphan; static int lastFrame = -1;
    static readonly Dictionary<DuelFighter, int> seenDb = new Dictionary<DuelFighter, int>();
    public static int Started, Merged, Skipped, WatchdogFires; public static float MinSeen = 1f; public static string LastClass = "";   // proof counters
    public static bool Active { get { return owns; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; rig = null; owns = false; pending = null; lastStart = -999f; orphan = 0f; lastFrame = -1; seenDb.Clear(); Started = Merged = Skipped = WatchdogFires = 0; MinSeen = 1f; baseFixed = Time.fixedDeltaTime; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        baseFixed = Time.fixedDeltaTime;
        if (UnityEngine.Object.FindFirstObjectByType<CombatSlowMoDriver>() != null) return;
        var go = new GameObject("CombatSlowMo"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<CombatSlowMoDriver>();
    }

    public static Row Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/slow_mo.json", Application.dataPath + "/Data/slow_mo.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Row>(DataPath.ReadFile(p)); if (r != null) { cfg = r; if (cfg.classes == null) cfg.classes = new ClassRow[0]; } } catch (Exception e) { Debug.LogWarning("[CombatSlowMo] " + p + ": " + e.Message); }
            break;
        }
        return cfg;
    }
    static ClassRow Find(string id) { var c = Cfg().classes; for (int i = 0; i < c.Length; i++) if (c[i] != null && c[i].id == id) return c[i]; return null; }
    static bool IsPlayer(DuelFighter f)
    {
        if (rig == null) rig = UnityEngine.Object.FindFirstObjectByType<CameraRig>();
        if (f == null || rig == null || rig.target == null) return false;
        return f.transform == rig.target || f.transform.IsChildOf(rig.target);
    }
    static bool FinisherOwns() { return RTFinisher.Busy || FinisherTime.Slowed || (FinisherCamera.Instance != null && FinisherCamera.Instance.Active); }

    public static void OnEvent(CombatEvent e)
    {
        var r = Cfg(); if (!r.enabled) return;
        string cls = null;
        if (e.type == CombatClass.Parried) cls = CombatClass.Parry;   // HCKIT4 v95
        else if (e.type == "Hit")
        {
            if (e.actor != null)
            {
                int seen; seenDb.TryGetValue(e.actor, out seen); int now = e.actor.statDeathblows; seenDb[e.actor] = now;
                if (now > seen) cls = "deathblow";
            }
            if (cls == null && CombatClass.IsCrit(e)) cls = CombatClass.Crit;   // HCKIT4 v95
        }
        if (cls == null) return;
        var c = Find(cls); if (c == null) return;
        if (c.attackerPlayerOnly && !IsPlayer(e.actor)) return;
        pending = cls; pendingAt = Time.unscaledTime + Mathf.Max(0f, c.delayS);   // starts from Tick (next frame): by then a finisher that took over the deathblow is visible
    }

    /// <summary>Request a dip now (no event needed). Returns false when refused.</summary>
    public static bool Request(string cls)
    {
        var r = Cfg(); var c = Find(cls); if (!r.enabled || c == null) { Skipped++; return false; }
        if (FinisherOwns()) { Skipped++; return false; }
        float sc = Mathf.Clamp(c.scale, r.minScale, 1f);
        if (owns)
        {
            // merge, never stack: the lower scale wins, the hold may only grow up to the total cap
            if (sc < scale) scale = sc;
            float cap = Mathf.Max(0f, r.maxSeconds - easeOut);
            holdEnd = Mathf.Min(cap, Mathf.Max(holdEnd, age + c.holdS));
            Merged++; return true;
        }
        if (Time.unscaledTime - lastStart < r.cooldownS) { Skipped++; return false; }
        if (Time.timeScale < 0.999f) { Skipped++; return false; }   // somebody else (pause, harness) owns the clock
        owns = true; scale = sc; age = 0f; easeIn = Mathf.Max(0.001f, c.easeInS); easeOut = Mathf.Max(0.001f, c.easeOutS);
        holdEnd = Mathf.Min(Mathf.Max(0f, r.maxSeconds - easeOut), easeIn + Mathf.Max(0f, c.holdS));
        lastStart = Time.unscaledTime; Started++; LastClass = cls; return true;
    }

    static void End()
    {
        if (owns) { Time.timeScale = 1f; if (!PhysicsClock.Scripted) Time.fixedDeltaTime = baseFixed; }   // PHYS1 v1: PhysX runs on the sim clock now
        owns = false; pending = null;
    }
    public static void Abort() { End(); }

    /// <summary>Once per rendered frame, before the sim (driver order -1000).</summary>
    public static void Tick()
    {
        if (lastFrame == Time.frameCount) return; lastFrame = Time.frameCount;
        var r = Cfg(); float dt = Time.unscaledDeltaTime;
        bool fin = FinisherOwns();
        if (pending != null && Time.unscaledTime >= pendingAt) { string p = pending; pending = null; if (!fin) Request(p); }
        if (owns)
        {
            if (fin || !r.enabled) { End(); }
            else if (Time.timeScale <= 0.0001f) { owns = false; pending = null; }   // paused by someone else: hand the clock back untouched
            else
            {
                age += dt;
                float total = holdEnd + easeOut, w;
                if (age < easeIn) { float u = Mathf.Clamp01(age / easeIn); w = u * u * (3f - 2f * u); }
                else if (age <= holdEnd) w = 1f;
                else if (age < total) { float u = Mathf.Clamp01((age - holdEnd) / easeOut); w = 1f - u * u * (3f - 2f * u); }
                else { End(); w = 0f; }
                if (owns)
                {
                    float ts = Mathf.Lerp(1f, scale, w); Time.timeScale = ts; if (!PhysicsClock.Scripted) Time.fixedDeltaTime = baseFixed * ts; MinSeen = Mathf.Min(MinSeen, ts);
                }
            }
            orphan = 0f; return;
        }
        // watchdog: a stuck sub-1 timeScale nobody owns
        float cur = Time.timeScale;
        if (cur > 0.0001f && cur < 0.999f && !fin) { orphan += dt; if (orphan > Mathf.Max(0.5f, r.watchdogS)) { Debug.LogWarning("[CombatSlowMo] watchdog: timeScale " + cur.ToString("0.00") + " stuck for " + orphan.ToString("0.0") + " s, forced to 1"); Time.timeScale = 1f; if (!PhysicsClock.Scripted) Time.fixedDeltaTime = baseFixed; WatchdogFires++; orphan = 0f; } }
        else orphan = 0f;
    }
}

[DefaultExecutionOrder(-1000)]
public class CombatSlowMoDriver : MonoBehaviour
{
    void OnEnable() { ContactRelay.OnEvent += CombatSlowMo.OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= CombatSlowMo.OnEvent; CombatSlowMo.Abort(); }
    void Update() { CombatSlowMo.Tick(); }
}
