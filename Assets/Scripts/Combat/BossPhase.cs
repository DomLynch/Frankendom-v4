using System;
using System.Collections.Generic;
using UnityEngine;

// BOSSPHASE v1 (laneTOP20B, NEW file, UNPROVEN IN UNITY). The half-HP phase change of a boss as DATA: a readable "tell" and an optional damage step on the moment the boss changes phase.
// The behaviour change itself (faster tempo, shorter waits, more combos, more thrust / kick) already exists: AiAdapt.cs "enrage" (ai_adapt.json profiles[].enrage, hpFrac). This file does NOT duplicate it:
// it watches the same switch (AIBrain.adapt.enraged; when the fighter has no AiAdapt profile it falls back to health / maxHealth <= the row's hpFrac) and, once per phase entry, plays an EXISTING cue
// (CueBus.Play, e.g. "effort_voice" pitched down = a roar), fires an EXISTING camera shake class (camera_impact.json shake, CamShake.Trigger) and multiplies DuelFighter.damageScale by the row's damageMul.
// No new move, clip, weapon or art. Data: Assets/Data/boss_phase.json (StreamingAssets copy). The master "enabled" ships false, and no file = no object = today's behaviour exactly.
// Reads only public state; edits no existing file. A phase that ends (the boss heals past the enrage release) undoes its damage step. Keyed per DuelFighter, so a respawned clone starts fresh.
[Serializable] public class BpCue { public string cue = ""; public float gain = 0f, rate = 1f, delay = 0f; }
[Serializable]
public class BpRow
{
    public string id = ""; public bool enabled = true;
    public float hpFrac = 0.5f;     // only used when the fighter has no AiAdapt profile; otherwise the phase follows AiAdapt.enraged (threshold = ai_adapt.json enrage.hpFrac)
    public float damageMul = 1f;    // multiplies DuelFighter.damageScale while the phase is on (1 = no change)
    public BpCue cue = new BpCue();
    public string shake = ""; public float shakeMul = 1f;   // a camera_impact.json shake class id ("" = none)
}
[Serializable] public class BpFile { public bool enabled = false; public float pollSeconds = 0.25f, refreshSeconds = 2f; public bool log = true; public BpRow[] rows = new BpRow[0]; }

public class BossPhase : MonoBehaviour
{
    const float MinPoll = 0.02f, MinRefresh = 0.1f;   // floors so a bad json cannot make the poll run every frame
    static BpFile cfg; static bool loaded;
    public static int Fired; public static string LastId = "";   // proof counters (harness / log)
    class St { public bool on; public float applied = 1f; }
    readonly Dictionary<DuelFighter, St> st = new Dictionary<DuelFighter, St>();
    AIBrain[] brains = new AIBrain[0]; float nextPoll, nextRefresh; CueBus bus;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; loaded = false; Fired = 0; LastId = ""; }

    static BpFile Cfg()
    {
        if (loaded) return cfg;
        loaded = true; cfg = null;
        string txt = DataPath.ReadText("boss_phase.json"); if (txt == null) return null;
        try { var f = JsonUtility.FromJson<BpFile>(txt); if (f != null && f.enabled && f.rows != null) cfg = f; }
        catch (Exception e) { Debug.LogWarning("[BossPhase] boss_phase.json parse failed: " + e.Message); }
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Cfg() == null || FindFirstObjectByType<BossPhase>() != null) return;
        var go = new GameObject("BossPhase"); DontDestroyOnLoad(go); go.AddComponent<BossPhase>();
    }

    static BpRow RowFor(BpFile c, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < c.rows.Length; i++) { var r = c.rows[i]; if (r != null && r.enabled && RosterResolver.KeyIs(r.id, id)) return r; }
        return null;
    }

    void Update()
    {
        var c = Cfg(); if (c == null) return;
        float now = Time.unscaledTime; if (now < nextPoll) return;
        nextPoll = now + Mathf.Max(MinPoll, c.pollSeconds);
        if (now >= nextRefresh)
        {
            nextRefresh = now + Mathf.Max(MinRefresh, c.refreshSeconds);
            brains = FindObjectsByType<AIBrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            List<DuelFighter> gone = null;
            foreach (var k in st.Keys) if (k == null) { if (gone == null) gone = new List<DuelFighter>(); gone.Add(k); }
            if (gone != null) for (int i = 0; i < gone.Count; i++) st.Remove(gone[i]);
        }
        for (int i = 0; i < brains.Length; i++)
        {
            var b = brains[i]; if (b == null || !b.isActiveAndEnabled) continue;
            var df = b.df; if (df == null || df.IsPlayer || df.Dead) continue;
            var row = RowFor(c, df.CharacterId); if (row == null) continue;
            bool phase = b.adapt != null ? b.adapt.enraged : (df.maxHealth > 0f && df.health > 0f && df.health / df.maxHealth <= row.hpFrac);
            St s; if (!st.TryGetValue(df, out s)) { s = new St(); st[df] = s; }
            if (phase && !s.on) Enter(c, row, df, s); else if (!phase && s.on) Leave(df, s);
        }
    }

    void Enter(BpFile c, BpRow row, DuelFighter df, St s)
    {
        s.on = true; Fired++; LastId = row.id;
        if (row.damageMul > 0f && row.damageMul != 1f) { df.damageScale *= row.damageMul; s.applied = row.damageMul; }
        var q = row.cue;
        if (q != null && !string.IsNullOrEmpty(q.cue) && q.gain > 0f)
        {
            if (bus == null) bus = FindFirstObjectByType<CueBus>();
            if (bus != null) bus.Play(q.cue, q.gain, q.delay, q.rate <= 0f ? 1f : q.rate);
        }
        if (!string.IsNullOrEmpty(row.shake)) CamShake.Trigger(row.shake, row.shakeMul);
        if (c.log) Debug.Log("[BossPhase] " + row.id + " enters its phase (health " + df.health.ToString("F0") + "/" + df.maxHealth.ToString("F0") + ", damageScale " + df.damageScale.ToString("F2") + ")");
    }

    static void Leave(DuelFighter df, St s)
    {
        if (s.applied != 1f && s.applied > 0f) df.damageScale /= s.applied;
        s.applied = 1f; s.on = false;
    }
}
