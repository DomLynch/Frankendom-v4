using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// TIERS v96 (rebased from PERF-GPU), NOT COMPILED. Quality tier selection for the player: Low / Medium / High / Ultra. Data: StreamingAssets/quality_tiers.json.
//   tier name   : -quality:<low|medium|high|ultra>  >  PlayerPrefs[prefsKey] (ignored in harness runs)  >  first run suggestion (game_settings.json, off by default)  >  defaultTier  >  "ultra"
//   HDRP asset  : the QualitySettings level whose name equals the tier (Low/Medium/High/Ultra), each level -> its own HDRP asset
//   Volume/camera overrides : the tier's "flags" object, overlaid on runtime_flags.json by RuntimeQuality.Boot
//   -qset:key=value,key=value : ad-hoc overrides of any Flags field (A/B tests), e.g. -qset:asyncCompute=1,ssgiQuality=1
//   -renderscale:NN           : shortcut for -qset:renderScale=NN
// The menu / UI lane does NOT call this class: it goes through GameSettings (Scripts/GameSettings.cs).
// Tier switching at runtime: SetTier() changes the QualitySettings level (new HDRP asset) and re-applies the flags onto fresh private Volume.profile clones
// (the shared profile asset is never edited). The clone path is UNTESTED; if it misbehaves set game_settings.json graphics.applyAtRuntime=false (applies at the next launch).
public static class QualityTiers
{
    [System.Serializable] public class TierInfo { public string name = "", label = "", note = ""; public int minVramMb = 0, targetFps = 0; public string level = ""; public bool suggest = true; }   // 2KPROFILE v01: level = QualitySettings level (empty = the tier name capitalised), suggest = SuggestedTier() may pick it
    [System.Serializable] class Head { public string defaultTier = "ultra", baseTier = "ultra", prefsKey = "gfx.tier", harnessTier = ""; public TierInfo[] tiers = new TierInfo[0]; }
    [System.Serializable] class Mul { public float lodBiasMul = 1f; }
    struct PBase { public int max; public float rate, dist; }

    public static string Current = "ultra";
    public static string FlagsJson;          // the selected tier's "flags" object (raw json), null when no tier file
    public static float LodBiasMul = 1f;     // tier flag lodBiasMul: CrackFlicker multiplies crack_flicker.json lodBias by it (1 = the approved look)
    static string raw; static Head head = new Head();
    static readonly HashSet<Volume> tracked = new HashSet<Volume>();
    static readonly Dictionary<ParticleSystem, PBase> pbase = new Dictionary<ParticleSystem, PBase>();

    public static TierInfo[] Tiers { get { return head.tiers ?? new TierInfo[0]; } }
    public static string DefaultTier { get { return string.IsNullOrEmpty(head.defaultTier) ? "ultra" : head.defaultTier.ToLowerInvariant(); } }
    public static string HarnessTier { get { return string.IsNullOrEmpty(head.harnessTier) ? DefaultTier : head.harnessTier.ToLowerInvariant(); } }   // GO100 v1: harness runs (gates / benches / proofs) keep this tier when defaultTier moves to the shipped native tier

    static string Arg(string prefix) { foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, System.StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { raw = null; head = new Head(); FlagsJson = null; LodBiasMul = 1f; Current = "ultra"; tracked.Clear(); pbase.Clear(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Early()
    {
        try
        {
            string p = Application.streamingAssetsPath + "/quality_tiers.json";
            if (File.Exists(p)) { raw = DataPath.ReadFile(p); var h = JsonUtility.FromJson<Head>(raw); if (h != null) head = h; if (string.IsNullOrEmpty(head.prefsKey)) head.prefsKey = "gfx.tier"; }
            string t = Arg("-quality:");
            if (t == null) t = RuntimeQuality.IsHarness() ? HarnessTier : PlayerPrefs.GetString(head.prefsKey, "");   // gates / benches never depend on a saved player choice
            if (string.IsNullOrEmpty(t)) t = GameSettings.FirstRunTier(DefaultTier);
            Select(t);
        }
        catch (System.Exception e) { Debug.LogWarning("QualityTiers early failed: " + e.Message); }
    }

    static bool Known(string tier) { foreach (var ti in Tiers) if (ti != null && string.Equals(ti.name, tier, System.StringComparison.OrdinalIgnoreCase)) return true; return Tiers.Length == 0; }

    public static void Select(string tier)
    {
        tier = (tier ?? DefaultTier).ToLowerInvariant();
        if (!Known(tier)) { Debug.LogWarning("QualityTiers: unknown tier '" + tier + "', using " + DefaultTier); tier = DefaultTier; }
        Current = tier;
        string level = LevelOf(tier);   // 2KPROFILE v01: was char.ToUpperInvariant(tier[0]) + tier.Substring(1)
        int idx = System.Array.IndexOf(QualitySettings.names, level);
        if (idx >= 0) { if (idx != QualitySettings.GetQualityLevel()) QualitySettings.SetQualityLevel(idx, true); }
        else Debug.LogWarning("QualityTiers: no QualitySettings level named " + level + " (apply the staged QualitySettings.asset)");
        FlagsJson = raw != null ? ExtractFlags(raw, tier) : null;
        LodBiasMul = 1f;
        if (FlagsJson != null) { var m = JsonUtility.FromJson<Mul>(FlagsJson); if (m != null && m.lodBiasMul > 0f) LodBiasMul = m.lodBiasMul; }
        ApplyLod();
        Debug.Log("QUALITYTIER tier=" + tier + " level=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + " asset=" + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none")
            + " gpu=" + SystemInfo.graphicsDeviceName + " api=" + SystemInfo.graphicsDeviceType + " vram=" + SystemInfo.graphicsMemorySize + "MB");
    }

    // 2KPROFILE v01: the QualitySettings level of a tier: its json "level", else the tier name capitalised (low -> Low)
    static string LevelOf(string tier)
    {
        foreach (var ti in Tiers) if (ti != null && string.Equals(ti.name, tier, System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ti.level)) return ti.level;
        return char.ToUpperInvariant(tier[0]) + tier.Substring(1);
    }

    // highest tier (list order is low -> ultra) whose minVramMb fits this GPU; used for the 'recommended' label and the optional first-run pick
    public static string Suggested()
    {
        string best = null; int vram = SystemInfo.graphicsMemorySize;
        foreach (var ti in Tiers) if (ti != null && ti.suggest && !string.IsNullOrEmpty(ti.name) && vram >= ti.minVramMb) best = ti.name.ToLowerInvariant();   // 2KPROFILE v01: opt-in tiers (suggest false) are never suggested
        return best ?? DefaultTier;
    }

    // crack_flicker.json lodBias (the approved look, 2.0 today) x the tier multiplier. SetQualityLevel resets QualitySettings.lodBias, so this runs after every tier change.
    public static void ApplyLod() { if (CrackFlicker.BaseLodBias > 0f) QualitySettings.lodBias = CrackFlicker.BaseLodBias * LodBiasMul; }

    // Applies tier + -qset onto the flags object (JsonUtility.FromJsonOverwrite keeps every field the json does not mention).
    // The baseTier (ultra) is NOT overlaid: runtime_flags.json IS its definition (the v69 look; QC keeps tuning that one file), and every switch starts again from the authored
    // shared profile, so switching back to it restores the authored look. Its flags in the json are documentation, checked equal to runtime_flags.json by check_tiers.py.
    public static void Overlay(object flags)
    {
        if (FlagsJson != null && !string.Equals(Current, head.baseTier, System.StringComparison.OrdinalIgnoreCase)) JsonUtility.FromJsonOverwrite(FlagsJson, flags);
        string qs = Arg("-qset:"), rs = Arg("-renderscale:");
        var sb = new System.Text.StringBuilder("{");
        if (qs != null) foreach (var kv in qs.Split(',')) { var s = kv.Split('='); if (s.Length == 2) sb.Append("\"" + s[0] + "\":" + s[1] + ","); }
        if (rs != null) sb.Append("\"renderScale\":" + rs + ",");
        if (sb.Length > 1) { sb.Length--; sb.Append("}"); JsonUtility.FromJsonOverwrite(sb.ToString(), flags); }
    }

    // Returns a private clone of the Volume's shared profile so tier edits never touch the shared Global.asset (and a later SetTier starts again from the authored profile).
    // first = RuntimeQuality.Boot: every Volume present is cloned and remembered. Later (tier switch): only the remembered ones, so Volumes other scripts drive through
    // their own sharedProfile (finisher camera, vignette) are left alone.
    public static VolumeProfile Fresh(Volume v, bool first)
    {
        if (v == null || v.sharedProfile == null) return null;
        if (first) tracked.Add(v); else if (!tracked.Contains(v)) return null;
        v.profile = null;     // forget the previous clone ...
        return v.profile;     // ... the getter instantiates a new private copy of sharedProfile and its components
    }

    public static void Save(string tier) { PlayerPrefs.SetString(head.prefsKey, (tier ?? DefaultTier).ToLowerInvariant()); PlayerPrefs.Save(); }

    public static void SetTier(string tier, bool persist = true)
    {
        Select(tier);
        if (persist) Save(Current);
        RuntimeQuality.Reapply();
    }

    // ---- camera: async compute family via the public HDRP API (FrameSettings.SetEnabled + override mask). asyncCompute -1 = leave, 0 = off, 1 = on ----
    static readonly FrameSettingsField[] Async = { FrameSettingsField.AsyncCompute, FrameSettingsField.LightListAsync, FrameSettingsField.SSRAsync, FrameSettingsField.SSAOAsync, FrameSettingsField.ContactShadowsAsync, FrameSettingsField.VolumeVoxelizationsAsync };

    public static void ApplyCamera(int asyncCompute)
    {
        if (asyncCompute < 0) return;
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            var hd = cam.GetComponent<HDAdditionalCameraData>(); if (hd == null) continue;
            hd.customRenderingSettings = true;
            ref FrameSettings fs = ref hd.renderingPathCustomFrameSettings;
            foreach (var f in Async) { fs.SetEnabled(f, asyncCompute == 1); hd.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)f] = true; }
        }
    }

    // scales the maxParticles / emission of the ParticleSystems that exist now, always from their first-seen values (a tier switch never compounds). Systems spawned later keep their prefab values.
    public static void ApplyParticles(float scale)
    {
        if (Mathf.Approximately(scale, 1f) && pbase.Count == 0) return;
        foreach (var ps in Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var m = ps.main; var e = ps.emission; PBase b;
            if (!pbase.TryGetValue(ps, out b)) { b = new PBase { max = m.maxParticles, rate = e.rateOverTimeMultiplier, dist = e.rateOverDistanceMultiplier }; pbase[ps] = b; }
            m.maxParticles = Mathf.Max(1, Mathf.RoundToInt(b.max * scale)); e.rateOverTimeMultiplier = b.rate * scale; e.rateOverDistanceMultiplier = b.dist * scale;
        }
    }

    // finds {"name":"<tier>", ... "flags": { ... } } and returns the braces of "flags" (json has no braces inside strings)
    static string ExtractFlags(string text, string tier)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, "\"name\"\\s*:\\s*\"" + System.Text.RegularExpressions.Regex.Escape(tier) + "\"");
        if (!m.Success) return null;
        int f = text.IndexOf("\"flags\"", m.Index, System.StringComparison.Ordinal); if (f < 0) return null;
        int open = text.IndexOf('{', f); if (open < 0) return null;
        int depth = 0;
        for (int i = open; i < text.Length; i++) { if (text[i] == '{') depth++; else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1); }
        return null;
    }
}
