using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// MENU v100 (lane laneMENU): the player's saved options + applying them. File: Application.persistentDataPath/settings.json. Loaded and applied at boot (BeforeSceneLoad), re-applied after a scene load
// and after a graphics tier switch (QualitySettings.SetQualityLevel resets vSync). Harness runs (MenuCfg.Harness) never read, apply or write it.
// NOT stored here: the graphics tier and the free-camera-in-combat option, which belong to GameSettings (laneTIERS, PlayerPrefs); the menu only shows them.
// Readers: CameraRig (CamSens), CueBus (AudioMix.Sfx), GatewayRift (AudioMix.Ambience), AudioListener.volume (Master), ControlsTable mouse.deadZonePx, HudCfg hint.enabled.

// RCHYGIENE v100: AudioMix moved to Assets/Scripts/Audio/AudioMix.cs (data-driven categories, audio_mix.json). settings.json keeps the player's levels in mix[].

public static class MenuSettings
{
    [Serializable]
    public class Saved
    {
        public int version = 1;
        public float camSens = 1f, deadZonePx = -1f;   // deadZonePx < 0 = the controls.json value
        public List<AudioMix.Entry> mix = new List<AudioMix.Entry>();   // RCHYGIENE v100: one entry per audio_mix.json category the player changed (id, v)
        public int vsync = 1, frameCap = 0, resW = 0, resH = 0;                              // resW 0 = keep the window as it is; frameCap 0 = unlimited
        public string fullscreen = "";                                                        // FullScreenMode name, "" = keep
        public bool hints = true;
        public InputMap.Bind[] keys = new InputMap.Bind[0];   // REBIND v100: only the key bindings the player changed (InputMap)
    }

    public static Saved S = new Saved();
    static bool loaded, dirty, subscribed, baseCaptured; static float dirtyAt, baseDeadZone; static bool baseHints;
    static List<Vector2Int> resList;

    public static string FilePath { get { return Path.Combine(Application.persistentDataPath, "settings.json"); } }
    public static float CamSens { get { return MenuCfg.Harness ? 1f : S.camSens; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { S = new Saved(); loaded = dirty = subscribed = baseCaptured = false; resList = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        if (MenuCfg.Harness || !MenuCfg.Get().enabled) return;
        Load(); ApplyAll();
        if (!subscribed) { subscribed = true; SceneManager.sceneLoaded += (s, m) => { ApplyGameplay(); ApplyDisplay(false); }; GameSettings.Changed += () => ApplyDisplay(false); }
    }

    // ---------------- load / save ----------------
    static void Defaults()
    {
        var D = MenuCfg.Get().defaults; S = new Saved { camSens = D.camSens, vsync = D.vsync, frameCap = D.frameCap, hints = D.hints };   // RCHYGIENE v100: audio defaults come from audio_mix.json
    }
    public static void Load()
    {
        loaded = true; Defaults();
        try { string p = FilePath; if (File.Exists(p)) JsonUtility.FromJsonOverwrite(File.ReadAllText(p), S); }
        catch (Exception e) { Debug.LogWarning("[MenuSettings] settings.json unreadable, defaults used: " + e.Message); Defaults(); }
        AudioMix.Import(S.mix); S.mix = AudioMix.Export();   // RCHYGIENE v100: clamps to the audio_mix.json limits, fills missing categories with their defaults
        var cs = MenuCfg.FindRow("camSens"); if (cs != null) S.camSens = Mathf.Clamp(S.camSens, cs.min, cs.max);
        var dz = MenuCfg.FindRow("deadZone"); if (dz != null && S.deadZonePx >= 0f) S.deadZonePx = Mathf.Clamp(S.deadZonePx, dz.min, dz.max);
        if (S.keys == null) S.keys = new InputMap.Bind[0]; InputMap.SettingsLoaded();   // REBIND v100
    }
    public static void MarkDirty() { dirty = true; dirtyAt = Time.unscaledTime; }
    public static bool DirtyDue(float delay) { return dirty && Time.unscaledTime - dirtyAt >= delay; }
    public static void Flush()
    {
        if (!dirty) return; dirty = false;
        if (MenuCfg.Harness) return;
        try
        {
            string p = FilePath, tmp = p + ".tmp"; Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(tmp, JsonUtility.ToJson(S, true));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }
        catch (Exception e) { Debug.LogWarning("[MenuSettings] settings.json not saved: " + e.Message); }
    }

    // ---------------- apply ----------------
    public static void ApplyAll() { ApplyAudio(); ApplyGameplay(); ApplyDisplay(true); if (!MenuCfg.Harness) Debug.Log("[MenuSettings] applied res " + Screen.width + "x" + Screen.height + " vsync " + QualitySettings.vSyncCount + " cap " + Application.targetFrameRate + " master " + AudioListener.volume.ToString("F2") + " (saved res " + S.resW + "x" + S.resH + ", saved vsync " + S.vsync + ", saved cap " + S.frameCap + ")"); }   // dev2: proof line for the settings check (item 6b), not released yet

    public static void ApplyAudio()
    {
        if (MenuCfg.Harness) return;
        AudioMix.Managed = true;   // RCHYGIENE v100: the AudioMix driver now writes AudioListener.volume (master x pause fade); the levels were set by AudioMix.Import / Set
    }

    static void CaptureBase()
    {
        if (baseCaptured) return; baseCaptured = true;
        baseDeadZone = ControlsTable.Get().mouse.deadZonePx; baseHints = HudCfg.Get().hint.enabled;
    }
    public static float BaseDeadZone { get { CaptureBase(); return baseDeadZone; } }
    public static void ApplyGameplay()
    {
        if (MenuCfg.Harness) return;
        CaptureBase();
        ControlsTable.Get().mouse.deadZonePx = S.deadZonePx >= 0f ? S.deadZonePx : baseDeadZone;
        HudCfg.Get().hint.enabled = baseHints && S.hints;   // the F1 panel and the corner prompt; a hud.json "enabled": false stays off
    }

    /// <summary>vSync, frame cap and (when withScreen) the saved window mode / size.</summary>
    public static void ApplyDisplay(bool withScreen)
    {
        if (MenuCfg.Harness) return;
        QualitySettings.vSyncCount = Mathf.Max(0, S.vsync); Application.targetFrameRate = S.frameCap > 0 ? S.frameCap : -1;
        if (withScreen) ApplyScreen();
    }
    public static void ApplyScreen()
    {
        if (MenuCfg.Harness) return;
        FullScreenMode mode = Screen.fullScreenMode; FullScreenMode m2;
        if (!string.IsNullOrEmpty(S.fullscreen) && Enum.TryParse(S.fullscreen, out m2)) mode = m2;
        int w = S.resW > 0 ? S.resW : Screen.width, h = S.resH > 0 ? S.resH : Screen.height;
        if (w != Screen.width || h != Screen.height || mode != Screen.fullScreenMode) Screen.SetResolution(w, h, mode);
    }

    // ---------------- sliders / toggles by key ----------------
    public static float GetF(string key)
    {
        if (AudioMix.Find(key) != null) return AudioMix.Level(key);   // RCHYGIENE v100: any audio_mix.json category
        switch (key)
        {
            case "camSens": return S.camSens;
            case "deadZone": return S.deadZonePx >= 0f ? S.deadZonePx : BaseDeadZone;
        }
        return 0f;
    }
    public static void SetF(string key, float v)
    {
        if (AudioMix.Find(key) != null) { AudioMix.Set(key, v); S.mix = AudioMix.Export(); ApplyAudio(); MarkDirty(); return; }   // RCHYGIENE v100
        var r = MenuCfg.FindRow(key); if (r != null) v = Mathf.Clamp(v, r.min, r.max);
        switch (key)
        {
            case "camSens": S.camSens = v; break;
            case "deadZone": S.deadZonePx = Mathf.Max(0f, v); ApplyGameplay(); break;
            default: return;
        }
        MarkDirty();
    }
    public static bool GetB(string key) { return key == "hints" ? S.hints : false; }
    public static void SetB(string key, bool v) { if (key == "hints") { S.hints = v; ApplyGameplay(); MarkDirty(); } }

    public static void Reset(string scope)
    {
        var D = MenuCfg.Get().defaults;
        if (scope == "audio") { AudioMix.ResetAll(); S.mix = AudioMix.Export(); ApplyAudio(); }   // RCHYGIENE v100
        else if (scope == "gameplay") { S.camSens = D.camSens; S.deadZonePx = -1f; S.hints = D.hints; ApplyGameplay(); }
        else if (scope == "display") { S.vsync = D.vsync; S.frameCap = D.frameCap; S.resW = S.resH = 0; S.fullscreen = ""; ApplyDisplay(false); }
        MarkDirty();
    }

    // ---------------- display lists ----------------
    public static List<Vector2Int> Resolutions()
    {
        if (resList != null && resList.Count > 0) return resList;
        var set = new SortedDictionary<long, Vector2Int>();
        foreach (var r in Screen.resolutions) set[(long)r.width * 100000L + r.height] = new Vector2Int(r.width, r.height);
        var cur = new Vector2Int(Screen.width, Screen.height); set[(long)cur.x * 100000L + cur.y] = cur;
        resList = new List<Vector2Int>(set.Values); return resList;
    }
    public static Vector2Int CurrentResolution() { return S.resW > 0 ? new Vector2Int(S.resW, S.resH) : new Vector2Int(Screen.width, Screen.height); }
    public static void StepResolution(int dir)
    {
        var l = Resolutions(); var c = CurrentResolution(); int i = l.IndexOf(c);
        if (i < 0) { i = 0; for (int k = 0; k < l.Count; k++) if (l[k].x * l[k].y <= c.x * c.y) i = k; }
        var n = l[((i + dir) % l.Count + l.Count) % l.Count]; S.resW = n.x; S.resH = n.y; ApplyScreen(); MarkDirty();
    }
    public static string CurrentFullscreenId() { return !string.IsNullOrEmpty(S.fullscreen) ? S.fullscreen : Screen.fullScreenMode.ToString(); }
    public static string FullscreenLabel()
    {
        string id = CurrentFullscreenId(); foreach (var c in MenuCfg.Get().fullscreenModes) if (c.id == id) return c.label; return id;
    }
    public static void StepFullscreen(int dir)
    {
        var l = MenuCfg.Get().fullscreenModes; string id = CurrentFullscreenId(); int i = 0; for (int k = 0; k < l.Length; k++) if (l[k].id == id) i = k;
        S.fullscreen = l[((i + dir) % l.Length + l.Length) % l.Length].id; ApplyScreen(); MarkDirty();
    }
    public static string VsyncLabel() { foreach (var c in MenuCfg.Get().vsyncModes) if (c.value == S.vsync) return c.label; return S.vsync.ToString(); }
    public static void StepVsync(int dir)
    {
        var l = MenuCfg.Get().vsyncModes; int i = 0; for (int k = 0; k < l.Length; k++) if (l[k].value == S.vsync) i = k;
        S.vsync = l[((i + dir) % l.Length + l.Length) % l.Length].value; ApplyDisplay(false); MarkDirty();
    }
    public static string FrameCapLabel() { return S.frameCap > 0 ? S.frameCap.ToString() + " fps" : MenuCfg.Get().strings.unlimited; }
    public static void StepFrameCap(int dir)
    {
        var l = MenuCfg.Get().frameCaps; int i = 0; for (int k = 0; k < l.Length; k++) if (l[k] == S.frameCap) i = k;
        S.frameCap = Mathf.Max(0, l[((i + dir) % l.Length + l.Length) % l.Length]); ApplyDisplay(false); MarkDirty();
    }
}
