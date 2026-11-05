using System;
using System.Collections.Generic;
using UnityEngine;

// RCHYGIENE v100 (lane laneRCHYGIENE): the data-driven audio mix. Categories (master / sfx / ambience / ui / music), their defaults, labels and menu rows, the cue -> category map and the
// pause fade all live in Assets/Data/audio_mix.json. Nothing here knows a number: every level is a category in that file.
//
// HOW CODE ROUTES SOUND THROUGH IT (grep for AudioSource: CueBus and GatewayRift are the only runtime creators today):
//   one-shot / scheduled sources  ->  volume = base * AudioMix.Gain("sfx")  (CueBus: AudioMix.CueGain(cueName), which maps the cue to its category through audio_mix.json cues[])
//   looping / long-lived sources  ->  AudioMix.Bind(source, "ambience", baseVolume): the driver keeps source.volume = baseVolume * Gain(category) as the slider moves
//   master                        ->  AudioListener.volume, written by the driver (master x the pause fade), never by game code
// PAUSE: AudioMix.SetPaused(true) fades the listener to silence over pause.fadeOutS (unscaled time), THEN sets AudioListener.pause so scheduled voices freeze exactly where they are;
//   SetPaused(false) un-pauses at once and fades back in over pause.fadeInS. The pause menu calls this instead of touching AudioListener.pause.
// HARNESS: the driver only writes AudioListener.volume while AudioMix.Managed is true (MenuSettings sets it outside harness runs), so RuntimeQuality's silent proof runs stay silent.
// Levels are the player's saved sliders (settings.json mix[]); the category min / max are the slider limits, defaultLevel the restore value. No audio assets are added.

public static class AudioMix
{
    // ---------------- data (audio_mix.json) ----------------
    [Serializable] public class Category { public string id = "", label = "", hint = ""; public float defaultLevel = 1f, min = 0f, max = 1f; public bool menu = true; }
    [Serializable] public class CueMap { public string cue = "", category = ""; }   // cue = an exact cue name, or a prefix ending in '*'
    [Serializable] public class PauseC { public bool fade = true; public float fadeOutS = 0.25f, fadeInS = 0.3f, silentBelow = 0.002f; }
    [Serializable] public class DisplayC { public float step = 0.05f, scale = 100f; public string fmt = "0", suffix = "%"; }
    [Serializable] public class Entry { public string id = ""; public float v = 1f; }
    [Serializable]
    public class Data
    {
        public bool enabled = true; public string masterId = "master", defaultCategory = "sfx";
        public Category[] categories = Defaults(); public CueMap[] cues = new CueMap[0]; public PauseC pause = new PauseC(); public DisplayC display = new DisplayC();
    }
    static Category[] Defaults()
    {
        return new[] { new Category { id = "master", label = "Master volume" }, new Category { id = "sfx", label = "Effects" }, new Category { id = "ambience", label = "Ambience" },
                       new Category { id = "ui", label = "Interface", menu = false }, new Category { id = "music", label = "Music", menu = false } };
    }

    // category ids the code itself names (the ids are data; these are the five the shipped file defines)
    public const string MasterId = "master", SfxId = "sfx", AmbienceId = "ambience", UiId = "ui", MusicId = "music";

    static Data data; static bool loaded;
    static readonly Dictionary<string, float> levels = new Dictionary<string, float>();
    static readonly Dictionary<string, string> cueCache = new Dictionary<string, string>();
    static readonly List<Bound> bound = new List<Bound>();
    class Bound { public AudioSource src; public string cat; public float baseVol; }

    /// <summary>True when the driver may write AudioListener.volume (set by MenuSettings outside harness runs).</summary>
    public static bool Managed;
    public static bool Paused { get; private set; }
    /// <summary>0..1, the pause fade (1 = full sound).</summary>
    public static float PauseFade { get { return fade; } }
    public static event Action Changed;

    static float fade = 1f; static bool listenerHeld, prevListenerPause, dirty = true; static float lastWritten = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { data = null; loaded = false; levels.Clear(); cueCache.Clear(); bound.Clear(); Managed = false; Paused = false; fade = 1f; listenerHeld = false; dirty = true; lastWritten = -1f; Changed = null; driver = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { EnsureDriver(); }

    public static Data Cfg()
    {
        if (loaded && data != null) return data;
        loaded = true; data = new Data();
        string txt = HudCfg.ReadData("audio_mix.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) { data = d; Fix(data); } }
            catch (Exception e) { Debug.LogWarning("[AudioMix] audio_mix.json parse failed, defaults kept: " + e.Message); data = new Data(); }
        }
        foreach (var c in data.categories) if (!levels.ContainsKey(c.id)) levels[c.id] = Mathf.Clamp(c.defaultLevel, c.min, c.max);
        return data;
    }
    public static void Reload() { loaded = false; cueCache.Clear(); }

    static void Fix(Data d)
    {
        if (d.categories == null || d.categories.Length == 0) d.categories = Defaults();
        if (d.cues == null) d.cues = new CueMap[0]; if (d.pause == null) d.pause = new PauseC(); if (d.display == null) d.display = new DisplayC();
        if (string.IsNullOrEmpty(d.masterId)) d.masterId = "master"; if (string.IsNullOrEmpty(d.defaultCategory)) d.defaultCategory = "sfx";
        var list = new List<Category>(); bool hasMaster = false;
        foreach (var c in d.categories) { if (c == null || string.IsNullOrEmpty(c.id)) continue; if (c.max < c.min) c.max = c.min; list.Add(c); if (c.id == d.masterId) hasMaster = true; }
        if (!hasMaster) list.Insert(0, new Category { id = d.masterId, label = "Master volume" });
        d.categories = list.ToArray();
    }

    // ---------------- levels ----------------
    public static Category Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var c in Cfg().categories) if (c.id == id) return c;
        return null;
    }
    /// <summary>The player's slider value of a category (its default until set; 1 for an unknown id).</summary>
    public static float Level(string id)
    {
        Cfg(); float v; return levels.TryGetValue(id ?? "", out v) ? v : 1f;
    }
    public static float Default(string id) { var c = Find(id); return c != null ? Mathf.Clamp(c.defaultLevel, c.min, c.max) : 1f; }
    public static void Set(string id, float v)
    {
        var c = Find(id); if (c == null) return;
        v = Mathf.Clamp(v, c.min, c.max); float old; if (levels.TryGetValue(id, out old) && Mathf.Approximately(old, v)) return;
        levels[id] = v; dirty = true; if (Changed != null) Changed();
    }
    /// <summary>Back to the defaultLevel of every category (the menu's "Restore defaults").</summary>
    public static void ResetAll() { foreach (var c in Cfg().categories) Set(c.id, c.defaultLevel); }

    /// <summary>The multiplier for code that creates its own source volume: the category slider (master is NOT included, it rides on AudioListener.volume).</summary>
    public static float Gain(string category) { return category == Cfg().masterId ? 1f : Level(category); }
    public static float Master { get { return Level(Cfg().masterId); } }
    public static float Sfx { get { return Level(SfxId); } }
    public static float Ambience { get { return Level(AmbienceId); } }
    public static float Ui { get { return Level(UiId); } }
    public static float Music { get { return Level(MusicId); } }

    // ---------------- cues ----------------
    /// <summary>The category a CueBus cue plays in (audio_mix.json cues[], exact name first, then the longest "prefix*"; else defaultCategory).</summary>
    public static string CueCategory(string cue)
    {
        if (string.IsNullOrEmpty(cue)) return Cfg().defaultCategory;
        string c; if (cueCache.TryGetValue(cue, out c)) return c;
        var D = Cfg(); c = D.defaultCategory; int best = -1;
        foreach (var m in D.cues)
        {
            if (m == null || string.IsNullOrEmpty(m.cue)) continue;
            if (m.cue == cue) { c = m.category; best = int.MaxValue; break; }
            if (m.cue[m.cue.Length - 1] == '*' && m.cue.Length - 1 > best && cue.StartsWith(m.cue.Substring(0, m.cue.Length - 1), StringComparison.Ordinal)) { c = m.category; best = m.cue.Length - 1; }
        }
        if (Find(c) == null) c = D.defaultCategory;
        cueCache[cue] = c; return c;
    }
    public static float CueGain(string cue) { return Gain(CueCategory(cue)); }

    // ---------------- looping sources ----------------
    /// <summary>Keeps source.volume = baseVolume * Gain(category) from now on (sliders move it live). Call Unbind when the source is destroyed early (destroyed sources are dropped on their own).</summary>
    public static void Bind(AudioSource src, string category, float baseVolume = 1f)
    {
        if (src == null) return;
        foreach (var b in bound) if (b.src == src) { b.cat = category; b.baseVol = baseVolume; dirty = true; EnsureDriver(); return; }
        bound.Add(new Bound { src = src, cat = category, baseVol = baseVolume }); dirty = true; EnsureDriver();
    }
    public static void Unbind(AudioSource src) { bound.RemoveAll(b => b.src == src); }

    // ---------------- save / load (settings.json mix[]) ----------------
    public static List<Entry> Export()
    {
        var l = new List<Entry>(); foreach (var c in Cfg().categories) l.Add(new Entry { id = c.id, v = Level(c.id) }); return l;
    }
    public static void Import(List<Entry> saved)
    {
        Cfg(); foreach (var c in data.categories) levels[c.id] = Mathf.Clamp(c.defaultLevel, c.min, c.max);   // anything the file lacks keeps its default
        if (saved != null) foreach (var s in saved) { if (s == null) continue; var c = Find(s.id); if (c != null) levels[c.id] = Mathf.Clamp(s.v, c.min, c.max); }
        dirty = true; if (Changed != null) Changed();
    }

    // ---------------- pause ----------------
    /// <summary>Pause menu on: fade to silence, then AudioListener.pause. Off: un-pause now, fade back in. Safe to call twice.</summary>
    public static void SetPaused(bool on)
    {
        if (on == Paused) return;
        Paused = on; EnsureDriver();
        if (on)
        {
            prevListenerPause = AudioListener.pause;
            if (!Cfg().pause.fade || Cfg().pause.fadeOutS <= 0f) { fade = 0f; HoldListener(); }
        }
        else
        {
            ReleaseListener();
            if (!Cfg().pause.fade || Cfg().pause.fadeInS <= 0f) fade = 1f;
        }
        dirty = true;
    }
    static void HoldListener() { if (listenerHeld) return; listenerHeld = true; AudioListener.pause = true; }
    static void ReleaseListener() { if (!listenerHeld) return; listenerHeld = false; AudioListener.pause = prevListenerPause; }

    // ---------------- driver ----------------
    static AudioMixDriver driver;
    static void EnsureDriver()
    {
        if (driver != null) return;
        try { var go = new GameObject("AudioMixDriver"); go.hideFlags = HideFlags.HideInHierarchy; UnityEngine.Object.DontDestroyOnLoad(go); driver = go.AddComponent<AudioMixDriver>(); }
        catch (Exception e) { Debug.LogWarning("[AudioMix] driver not created: " + e.Message); }
    }

    internal static void DriverGone() { driver = null; Paused = false; ReleaseListener(); }

    /// <summary>Called by the driver every frame (unscaled time, so it runs while the menu holds timeScale 0).</summary>
    internal static void Tick(float dt)
    {
        var P = Cfg().pause; float target = Paused ? 0f : 1f;
        if (!Mathf.Approximately(fade, target))
        {
            float dur = Paused ? P.fadeOutS : P.fadeInS;
            fade = !P.fade || dur <= 0f ? target : Mathf.MoveTowards(fade, target, dt / dur); dirty = true;
        }
        if (Paused && fade <= P.silentBelow) { fade = 0f; HoldListener(); }
        if (Managed)
        {
            float v = Master * fade;
            if (!Mathf.Approximately(v, lastWritten)) { AudioListener.volume = v; lastWritten = v; }
        }
        if (dirty || bound.Count > 0)
        {
            for (int i = bound.Count - 1; i >= 0; i--)
            {
                var b = bound[i]; if (b.src == null) { bound.RemoveAt(i); continue; }
                float want = Mathf.Clamp01(b.baseVol * Gain(b.cat)); if (!Mathf.Approximately(b.src.volume, want)) b.src.volume = want;
            }
            dirty = false;
        }
    }
}

/// <summary>Hidden carrier for AudioMix.Tick (pause fade, master volume, bound sources).</summary>
public class AudioMixDriver : MonoBehaviour
{
    void Update() { AudioMix.Tick(Time.unscaledDeltaTime); }
    void OnDestroy() { AudioMix.DriverGone(); }
}
