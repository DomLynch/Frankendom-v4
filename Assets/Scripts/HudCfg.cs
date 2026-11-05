using System;
using System.IO;
using UnityEngine;

// V66HUD: data for the player HUD and the game loop screens. Assets/Data/hud.json (StreamingAssets first). Every field has a default, so a missing file or "enabled": false keeps a sane HUD.
[Serializable] public class HudRowKA { public string k = ""; public string a = ""; }

public static class HudCfg
{
    [Serializable] public class ColorsC
    {
        public float[] ink = { 0.93f, 0.90f, 0.84f, 1f }, dim = { 0.62f, 0.60f, 0.56f, 0.9f }, charcoal = { 0.035f, 0.035f, 0.04f, 0.78f }, crimson = { 0.62f, 0.07f, 0.09f, 1f }, gold = { 0.86f, 0.69f, 0.30f, 1f };
        public float[] hpPlayer = { 0.66f, 0.06f, 0.09f, 1f }, hpEnemy = { 0.72f, 0.36f, 0.09f, 1f }, hpLag = { 0.90f, 0.80f, 0.62f, 0.55f }, hpBack = { 0f, 0f, 0f, 0.7f };
    }
    [Serializable] public class PlateC
    {
        public float margin = 28f, top = 14f, width = 404f, height = 92f, pad = 12f; public int nameSize = 15; public float nameY = 7f, hpY = 30f, hpHeight = 16f, accentAlpha = 0.85f;
        public float[] tickMarks = { 0.25f, 0.5f, 0.75f }; public float lagDelay = 0.35f, lagRate = 0.45f;
        public string playerName = "DRACULA", enemyName = "PITBORN"; public bool fightLabel = true; public int fightLabelSize = 12; public string fightLabelText = "FIGHT {0}"; public float fightLabelY = 12f;
    }
    [Serializable] public class DebugC { public bool camLine = false; public int fpsLabelSize = 13; }
    [Serializable] public class HintC
    {
        public bool enabled = true; public string toggleKey = "F1"; public float delayS = 0.9f, showSeconds = 8f, fadeSeconds = 1.2f;
        public int columns = 2; public float colWidth = 232f, rowHeight = 19f; public int fontSize = 13; public float keyWidth = 92f, margin = 28f, pad = 10f, panelAlpha = 0.62f;
        public string title = "CONTROLS", footer = "F1 hide / show"; public HudRowKA[] rows = new HudRowKA[0];
    }
    [Serializable] public class VignetteC { public bool enabled = true; public float[] color = { 0.55f, 0.02f, 0.04f }; public float minAlpha = 0.16f, maxAlpha = 0.62f, gain = 3.2f, seconds = 0.5f, edgeFrac = 0.30f, power = 2f; }
    [Serializable] public class LowHpC { public bool enabled = true; public float threshold = 0.30f, maxAlpha = 0.17f, baseHz = 1.1f, maxHz = 1.8f; public float[] color = { 0.45f, 0.02f, 0.04f }; }
    [Serializable] public class HitTickC
    {
        public bool enabled = true; public float seconds = 0.17f, gap = 9f, length = 10f, thickness = 2f, heavyScale = 1.45f, blockedAlpha = 0.45f, chestHeight = 1.25f;
        public float[] color = { 0.96f, 0.93f, 0.85f, 1f }, heavyColor = { 0.95f, 0.76f, 0.30f, 1f };
    }
    [Serializable] public class EndC { public string title = "", subtitle = ""; public float[] color = { 1f, 1f, 1f, 1f }; }
    [Serializable] public class LabelsC { public string time = "Time", landed = "Hits landed", taken = "Hits taken", parries = "Parries", finisher = "Finisher"; }
    [Serializable] public class ScreensC
    {
        public float delayS = 0.9f, fadeInS = 0.7f, dimAlpha = 0.35f, bandHeight = 340f, bandLift = 20f, bandAlpha = 0.74f;
        public int titleSize = 78, subtitleSize = 22, statsSize = 16; public float buttonW = 236f, buttonH = 50f, buttonGap = 26f; public int buttonSize = 20, keyHintSize = 12, nextLabelSize = 13;
        public string statSep = "     "; public bool nextOnDefeat = false;
        public EndC victory = new EndC { title = "VICTORY", subtitle = "The {0} falls. The gate stands open.", color = new[] { 0.88f, 0.71f, 0.31f, 1f } };
        public EndC defeat = new EndC { title = "YOU DIED", subtitle = "The dark takes you. Rise again.", color = new[] { 0.74f, 0.08f, 0.10f, 1f } };
        public LabelsC labels = new LabelsC();
        public string retryText = "Retry", nextText = "Next opponent", nextPreview = "Next: {0}", fightText = "FIGHT {0}  -  {1}";
        public string retryHint = "Backspace / B", confirmHint = "Enter / A", retryHintDefeat = "Enter / Backspace / A";
    }
    [Serializable] public class FadeC { public bool enabled = true; public float inS = 0.7f, outS = 0.45f; public float[] color = { 0f, 0f, 0f }; }
    [Serializable] public class InputC
    {
        public string[] confirmKeys = { "Return", "KeypadEnter", "JoystickButton0" }, retryKeys = { "Backspace", "JoystickButton1" }, midFightRetryKeys = { "Backspace" };
    }
    [Serializable] public class Data
    {
        public bool enabled = true; public float referenceHeight = 1080f, minScale = 0.6f, maxScale = 3f;
        public ColorsC colors = new ColorsC(); public PlateC plate = new PlateC(); public DebugC debug = new DebugC(); public HintC hint = new HintC();
        public VignetteC vignette = new VignetteC(); public LowHpC lowHp = new LowHpC(); public HitTickC hitTick = new HitTickC(); public ScreensC screens = new ScreensC(); public FadeC fade = new FadeC(); public InputC input = new InputC();
    }

    static Data data; static bool loaded;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; loaded = false; }

    public static Data Get()
    {
        if (loaded && data != null) return data;
        loaded = true; data = new Data();
        string txt = ReadData("hud.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) { data = d.enabled ? d : new Data(); Fix(data); } }
            catch (Exception e) { Debug.LogWarning("[HudCfg] hud.json parse failed, defaults kept: " + e.Message); data = new Data(); }
        }
        return data;
    }
    public static void Reload() { loaded = false; }

    // JsonUtility leaves a missing nested object at its default but turns a missing array into null / empty: re-fill what code relies on
    static void Fix(Data d)
    {
        if (d.colors == null) d.colors = new ColorsC(); if (d.plate == null) d.plate = new PlateC(); if (d.debug == null) d.debug = new DebugC(); if (d.hint == null) d.hint = new HintC();
        if (d.vignette == null) d.vignette = new VignetteC(); if (d.lowHp == null) d.lowHp = new LowHpC(); if (d.hitTick == null) d.hitTick = new HitTickC(); if (d.screens == null) d.screens = new ScreensC();
        if (d.fade == null) d.fade = new FadeC(); if (d.input == null) d.input = new InputC();
        if (d.hint.rows == null) d.hint.rows = new HudRowKA[0]; if (d.plate.tickMarks == null) d.plate.tickMarks = new float[0];
        if (d.screens.victory == null) d.screens.victory = new EndC(); if (d.screens.defeat == null) d.screens.defeat = new EndC(); if (d.screens.labels == null) d.screens.labels = new LabelsC();
        d.referenceHeight = Mathf.Max(240f, d.referenceHeight); d.minScale = Mathf.Max(0.2f, d.minScale); d.maxScale = Mathf.Max(d.minScale, d.maxScale);
    }

    public static string ReadData(string name)
    {
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, name), Path.Combine(Application.dataPath, "Data/" + name) })
        {
            try { if (File.Exists(p)) return File.ReadAllText(p).TrimStart((char)0xFEFF); } catch { }
        }
        return null;
    }

    public static Color Col(float[] a, float alpha = -1f)
    {
        if (a == null || a.Length < 3) return Color.white;
        return new Color(a[0], a[1], a[2], alpha >= 0f ? alpha : (a.Length > 3 ? a[3] : 1f));
    }

    /// <summary>UI scale: Screen.height / referenceHeight, clamped. Everything drawn inside GUI.matrix = Scale(s) uses reference-1080 pixels.</summary>
    public static float Scale()
    {
        var d = Get(); return Mathf.Clamp(Screen.height / d.referenceHeight, d.minScale, d.maxScale);
    }

    public static KeyCode[] Keys(string[] names, KeyCode[] fallback)
    {
        var list = new System.Collections.Generic.List<KeyCode>();
        if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (Enum.TryParse(names[i], true, out k)) list.Add(k); }
        return list.Count > 0 ? list.ToArray() : fallback;
    }
    public static bool AnyDown(KeyCode[] ks) { if (ks != null) for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }
}
