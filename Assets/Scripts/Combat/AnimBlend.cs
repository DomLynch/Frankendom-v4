using System;
using System.IO;
using UnityEngine;

// V3 anim-blend port (src/fight/anim-blend.ts + data/fight.json blendMs/settleMs) as DATA: Assets/Data/anim_blend.json.
// Lookup order for the file: Resources/anim_blend, StreamingAssets/anim_blend.json, Application.dataPath/Data/anim_blend.json.
// API: AnimBlend.Fade(fromState, toState, fallbackSeconds) returns the crossfade duration in SECONDS for CrossFadeInFixedTime.
//   1. an explicit pair row (from or "*", to) wins;
//   2. else if `to` is a combat clip (attack/special/hurt/roll): <kind>In ms (V3: the clip's weight rises over <kind>In ms);
//   3. else if `from` is a combat clip: settleMs.<kind> if present, else <kind>Out ms (V3: the clip falls over <kind>Out ms);
//   4. else locomotion ms. If the file is missing or "enabled" is false, the caller's fallback is returned unchanged.
public static class AnimBlend
{
    [Serializable] public class BlendMs { public float locomotion = 120f, attackIn = 75f, attackOut = 80f, specialIn = 75f, specialOut = 80f, hurtIn = 40f, hurtOut = 80f, rollIn = 60f, rollOut = 120f; }
    [Serializable] public class SettleMs { public float roll = 120f; public float attack = -1f, special = -1f, hurt = -1f; }
    [Serializable] public class StateKind { public string state, kind; }
    [Serializable] public class Pair { public string from, to; public float ms; }
    [Serializable] public class Data
    {
        public bool enabled = true;
        public float defaultMs = 150f;
        public BlendMs blendMs = new BlendMs();
        public SettleMs settleMs = new SettleMs();
        public StateKind[] stateKinds = new StateKind[0];
        public Pair[] pairs = new Pair[0];
    }

    static Data data;
    static bool loaded, ok;

    public static Data Get() { if (!loaded) Load(); return data; }
    public static bool On { get { if (!loaded) Load(); return ok && data.enabled; } }
    public static void Reload() { loaded = false; data = null; Load(); }

    /// <summary>Kind of a clip state ("attack", "special", "hurt", "roll") or null for locomotion/other.</summary>
    public static string KindOf(string state)
    {
        if (string.IsNullOrEmpty(state) || !On) return null;
        var k = data.stateKinds;
        for (int i = 0; i < k.Length; i++) if (k[i] != null && k[i].state == state) return k[i].kind;
        return null;
    }

    static float In(string kind)
    {
        var b = data.blendMs;
        switch (kind) { case "attack": return b.attackIn; case "special": return b.specialIn; case "hurt": return b.hurtIn; case "roll": return b.rollIn; }
        return b.locomotion;
    }

    static float Out(string kind)
    {
        var b = data.blendMs; var s = data.settleMs;
        switch (kind)
        {
            case "attack": return s.attack > 0f ? s.attack : b.attackOut;
            case "special": return s.special > 0f ? s.special : b.specialOut;
            case "hurt": return s.hurt > 0f ? s.hurt : b.hurtOut;
            case "roll": return s.roll > 0f ? s.roll : b.rollOut;
        }
        return b.locomotion;
    }

    /// <summary>Crossfade duration in seconds for from -> to (from may be null/empty). Returns fallbackSeconds when the table is off.</summary>
    public static float Fade(string from, string to, float fallbackSeconds)
    {
        if (!On) return fallbackSeconds;
        var p = data.pairs;
        for (int i = 0; i < p.Length; i++)
        {
            var r = p[i]; if (r == null || r.to != to) continue;
            if (r.from == "*" || r.from == from) return Mathf.Max(0f, r.ms) / 1000f;
        }
        string tk = KindOf(to);
        if (tk != null) return In(tk) / 1000f;
        string fk = KindOf(from);
        if (fk != null) return Out(fk) / 1000f;
        return data.blendMs.locomotion > 0f ? data.blendMs.locomotion / 1000f : data.defaultMs / 1000f;
    }

    /// <summary>Eased-weight rate per second (V3 weightEase: 3000 / locomotion ms closes ~95% of a gap in that time). 24 is V1's rate.</summary>
    public static float WeightRate() { return On && data.blendMs.locomotion > 0f ? 3000f / data.blendMs.locomotion : 24f; }

    static void Load()
    {
        loaded = true; ok = false; data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("anim_blend");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "anim_blend.json");
                if (File.Exists(p)) json = DataPath.ReadFile(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/anim_blend.json");
                if (File.Exists(p)) json = DataPath.ReadFile(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("AnimBlend: read failed: " + ex.Message); }

        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("AnimBlend: anim_blend.json not found; callers keep their own fades."); return; }
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.blendMs == null) d.blendMs = new BlendMs();
            if (d.settleMs == null) d.settleMs = new SettleMs();
            if (d.stateKinds == null) d.stateKinds = new StateKind[0];
            if (d.pairs == null) d.pairs = new Pair[0];
            data = d; ok = true;
        }
        catch (Exception ex) { Debug.LogError("AnimBlend: parse failed: " + ex.Message); data = new Data(); }
    }
}
