using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// V61A: the ONLY system that tints a fighter's materials on a hit: a short warm/white flash (hit_flash.json: ~0.1 s) that always restores the original values.
// How: a per-slot MaterialPropertyBlock (renderer x material index) with a lerped _BaseColor (+ optional additive _EmissiveColor). The Material assets are never written, so
// "restore" = put back the slot's previous block (usually none = SetPropertyBlock(null)) and the material shows exactly its own values again.
// Restore happens: when the timer ends, when a second flash replaces the first (values are always recomputed from the untouched material colours, never compounded),
// when the fighter dies / is disabled / destroyed, on scene load, on disable of this runner, and by a failsafe (maxSeconds). Data lookup like PostureTable
// (Resources/hit_flash, StreamingAssets/hit_flash.json, Assets/Data/hit_flash.json). Missing file = built-in defaults below; "enabled": false = never touches a material.
public static class HitFlashTable
{
    [Serializable] public class Row
    {
        public string match = ""; public bool enabled = true, onlyDeathblow = true, onlyHeavy = false, skipOnKill = true;
        public float seconds = 0.1f, riseSeconds = 0.02f, strength = 0.45f, emissiveNits = 0f; public float[] color = { 1f, 0.82f, 0.6f };
    }
    [Serializable] public class Data
    {
        public bool enabled = true, debugLog = false; public string[] baseColorProps = { "_BaseColor", "_Color" }; public string emissiveProp = "_EmissiveColor"; public float maxSeconds = 0.5f;
        public Row[] rows = null;   // absent in the file = one default row on the roster enemy (filled in Get)
    }
    static Data d;
    public static Data Get() { if (d == null) { d = Load() ?? new Data(); if (d.rows == null) d.rows = new[] { new Row { match = Roster.EnemyId } }; } return d; }
    public static void Reload() { d = null; }
    static Data Load()
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("hit_flash"); if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "hit_flash.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/hit_flash.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("HitFlashTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonUtility.FromJson<Data>(json); } catch (Exception ex) { Debug.LogError("HitFlashTable: parse failed: " + ex.Message); return null; }
    }
    public static Row RowFor(string key)
    {
        var rows = Get().rows; if (rows == null || string.IsNullOrEmpty(key)) return null;
        for (int i = 0; i < rows.Length; i++) if (rows[i] != null && !string.IsNullOrEmpty(rows[i].match) && key.IndexOf(rows[i].match, StringComparison.OrdinalIgnoreCase) >= 0) return rows[i];
        return null;
    }
}

public class HitFlash : MonoBehaviour
{
    class Slot { public Renderer r; public int idx; public int baseId = -1, emId = -1; public Color baseC, emC; public MaterialPropertyBlock saved; public bool dirty; }
    class State { public List<Slot> slots; public float t0, seconds, rise, strength, nits; public Color col; public bool active; public float k; }

    static HitFlash inst;
    readonly Dictionary<DuelFighter, State> states = new Dictionary<DuelFighter, State>();
    readonly Dictionary<DuelFighter, int> seenDeathblows = new Dictionary<DuelFighter, int>();
    MaterialPropertyBlock mpb;   // created in Awake: a MonoBehaviour field initialiser must not construct a MaterialPropertyBlock (Unity throws CreateImpl error)
    void Awake() { if (mpb == null) mpb = new MaterialPropertyBlock(); }
    public static int FlashCount, RestoreCount;   // diagnostics for the harness

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || FindFirstObjectByType<HitFlash>() != null) return;
        var go = new GameObject("HitFlash"); DontDestroyOnLoad(go); go.AddComponent<HitFlash>();
    }

    void OnEnable() { inst = this; ContactRelay.OnEvent += OnEvent; SceneManager.sceneLoaded += OnScene; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; SceneManager.sceneLoaded -= OnScene; RestoreAll(); if (inst == this) inst = null; }
    void OnScene(Scene s, LoadSceneMode m) { RestoreAll(); states.Clear(); seenDeathblows.Clear(); }

    // ---- diagnostics (PostureProof): is this fighter flashing right now, and how strongly (0..1)
    public static bool IsFlashing(DuelFighter f) { State s; return inst != null && f != null && inst.states.TryGetValue(f, out s) && s.active; }
    public static float Amount(DuelFighter f) { State s; return inst != null && f != null && inst.states.TryGetValue(f, out s) && s.active ? s.k : 0f; }

    void OnEvent(CombatEvent e)
    {
        var D = HitFlashTable.Get(); if (!D.enabled || e.type != "Hit" || e.target == null) return;
        bool db = false;
        if (e.actor != null) { int seen; seenDeathblows.TryGetValue(e.actor, out seen); db = e.actor.statDeathblows > seen; seenDeathblows[e.actor] = e.actor.statDeathblows; }
        var row = HitFlashTable.RowFor(e.target.PostureKey); if (row == null || !row.enabled) return;
        if (row.onlyDeathblow && !db) return;
        if (row.onlyHeavy && !e.heavy && !db) return;
        if (row.skipOnKill && e.target.Dead) return;
        Begin(e.target, row);
    }

    void Begin(DuelFighter f, HitFlashTable.Row row)
    {
        State s; if (!states.TryGetValue(f, out s)) { s = new State(); states[f] = s; }
        if (s.slots == null || SlotsStale(s)) { if (s.slots != null) RestoreSlots(s); s.slots = BuildSlots(f); }
        s.t0 = Time.unscaledTime; s.seconds = Mathf.Max(0.02f, Mathf.Min(row.seconds, HitFlashTable.Get().maxSeconds)); s.rise = Mathf.Clamp(row.riseSeconds, 0.001f, s.seconds);
        s.strength = Mathf.Clamp01(row.strength); s.nits = Mathf.Max(0f, row.emissiveNits);
        s.col = row.color != null && row.color.Length >= 3 ? new Color(row.color[0], row.color[1], row.color[2], 1f) : new Color(1f, 0.82f, 0.6f, 1f);
        s.active = true; FlashCount++;
        if (HitFlashTable.Get().debugLog) Debug.Log("[HitFlash] " + f.name + " flash " + s.seconds.ToString("F2") + " s tick=" + Duel.TickCount);
        Apply(s);   // same frame: the contact frame already shows it
    }

    static bool SlotsStale(State s) { foreach (var sl in s.slots) if (sl.r == null) return true; return false; }

    List<Slot> BuildSlots(DuelFighter f)
    {
        var D = HitFlashTable.Get(); var list = new List<Slot>();
        foreach (var r in f.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is SpriteRenderer) continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue;
                var sl = new Slot { r = r, idx = i };
                if (D.baseColorProps != null) foreach (var p in D.baseColorProps) if (!string.IsNullOrEmpty(p) && m.HasProperty(p)) { sl.baseId = Shader.PropertyToID(p); sl.baseC = m.GetColor(sl.baseId); break; }
                if (!string.IsNullOrEmpty(D.emissiveProp) && m.HasProperty(D.emissiveProp)) { sl.emId = Shader.PropertyToID(D.emissiveProp); sl.emC = m.GetColor(sl.emId); }
                if (sl.baseId < 0 && sl.emId < 0) continue;
                r.GetPropertyBlock(mpb, i);
                if (!mpb.isEmpty) { sl.saved = new MaterialPropertyBlock(); r.GetPropertyBlock(sl.saved, i); }   // someone else's block: put it back on restore
                list.Add(sl);
            }
        }
        return list;
    }

    static float Envelope(State s, float age)
    {
        if (age >= s.seconds) return 0f;
        if (age < s.rise) return age / s.rise;
        return 1f - (age - s.rise) / Mathf.Max(1e-4f, s.seconds - s.rise);
    }

    void Apply(State s)
    {
        float k = Envelope(s, Time.unscaledTime - s.t0); s.k = k;
        foreach (var sl in s.slots)
        {
            if (sl.r == null) continue;
            if (sl.saved != null) sl.r.GetPropertyBlock(mpb, sl.idx); else mpb.Clear();   // keep another system's values and add ours on top
            if (sl.baseId >= 0) mpb.SetColor(sl.baseId, Color.Lerp(sl.baseC, new Color(s.col.r, s.col.g, s.col.b, sl.baseC.a), s.strength * k));
            if (sl.emId >= 0 && s.nits > 0f) mpb.SetColor(sl.emId, sl.emC + s.col * (s.nits * k));
            sl.r.SetPropertyBlock(mpb, sl.idx); sl.dirty = true;
        }
    }

    void RestoreSlots(State s)
    {
        if (s.slots == null) return;
        foreach (var sl in s.slots) if (sl.r != null && sl.dirty) { sl.r.SetPropertyBlock(sl.saved, sl.idx); sl.dirty = false; }   // saved == null clears the block: the material's own values
        if (s.active) RestoreCount++;
        s.active = false; s.k = 0f;
    }

    void RestoreAll() { foreach (var kv in states) RestoreSlots(kv.Value); }

    void LateUpdate()
    {
        if (states.Count == 0) return;
        bool anyGone = false;
        foreach (var kv in states)
        {
            var f = kv.Key; var s = kv.Value;
            if (f == null) { anyGone = true; continue; }
            if (!s.active) continue;
            float age = Time.unscaledTime - s.t0;
            if (age >= s.seconds || age >= HitFlashTable.Get().maxSeconds || f.Dead || !f.isActiveAndEnabled || !HitFlashTable.Get().enabled) { RestoreSlots(s); continue; }
            Apply(s);
        }
        if (anyGone) { var keep = new Dictionary<DuelFighter, State>(); foreach (var kv in states) if (kv.Key != null) keep[kv.Key] = kv.Value; states.Clear(); foreach (var kv in keep) states[kv.Key] = kv.Value; }
    }
}
