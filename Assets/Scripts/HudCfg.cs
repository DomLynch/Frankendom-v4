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
        // HUD2 roles
        public float[] frame = { 0.62f, 0.49f, 0.26f, 0.95f }, frameDim = { 0.34f, 0.29f, 0.22f, 0.85f }, shadow = { 0f, 0f, 0f, 0.85f };
        public float[] stamina = { 0.36f, 0.55f, 0.32f, 1f }, staminaLow = { 0.80f, 0.56f, 0.16f, 1f }, staminaExhausted = { 0.62f, 0.10f, 0.08f, 1f };
        public float[] posture = { 0.82f, 0.74f, 0.34f, 1f }, postureHigh = { 0.95f, 0.50f, 0.12f, 1f }, postureBreak = { 0.95f, 0.18f, 0.12f, 1f }, deathblow = { 1f, 0.92f, 0.85f, 1f };
        public float[] parry = { 0.98f, 0.82f, 0.32f, 1f }, guard = { 0.66f, 0.74f, 0.88f, 1f }, ready = { 0.90f, 0.74f, 0.34f, 1f }, cooling = { 0.42f, 0.40f, 0.40f, 1f };
    }
    [Serializable] public class FontsC
    {
        // Unity built-in fonts only (no font asset in the project, no TextMeshPro / uGUI package): OS serif faces through Font.CreateDynamicFontFromOSFont, first installed name wins; none installed = Unity default font
        public string[] serif = { "Palatino Linotype", "Book Antiqua", "Georgia", "Times New Roman" }, title = { "Palatino Linotype", "Book Antiqua", "Georgia", "Times New Roman" }, ui = { "Palatino Linotype", "Georgia", "Times New Roman" };
        public int osSize = 40;
    }
    [Serializable] public class BarC { public float w = 400f, h = 8f, taper = 10f, gap = 8f; }
    [Serializable] public class BarsC
    {
        public float texScale = 2f;                       // procedural bar texture resolution (texels per HUD px)
        public float framePad = 3.5f, frameLine = 1.3f, capSize = 7f, capLead = 30f;   // ornate frame: outline gap, line, end diamonds, thin lead-in lines beyond the tips
        public float backPad = 1.2f, gloss = 0.10f, tickAlpha = 0.38f, tickHeight = 0.62f; public float[] ticks = { 0.25f, 0.5f, 0.75f };
        // HUDFIX v92: damage trail = hold, then ease. lagHold s after the LAST hit (each new hit re-arms it), then the trail closes lagEase * gap per s (exponential, no more than the gap), never slower than lagMinRate (bar fractions per s), and is gone lagMaxS s after the hold ends at the latest
        public float lagHold = 0.55f, lagEase = 5f, lagMinRate = 0.25f, lagMaxS = 0.9f;
        public float lowFraction = 0.30f, exhaustedPulseHz = 3f, highFrom = 0.6f, pulseHz = 4f, flashAlpha = 0.85f;
    }
    [Serializable] public class PlayerC
    {
        public float x = 48f, y = 34f; public int nameSize = 22, nameSmall = 16, cueSize = 15; public float nameTracking = 3f, nameGap = 9f, cueDy = 3f;
        public bool showStamina = true, showPosture = true;
        public BarC hp = new BarC { w = 440f, h = 12f, taper = 18f }, stamina = new BarC { w = 330f, h = 6f, taper = 9f, gap = 9f }, posture = new BarC { w = 250f, h = 4f, taper = 7f, gap = 8f };
    }
    [Serializable] public class BossC
    {
        public bool enabled = true; public float w = 720f, top = 30f, sideMargin = 30f; public int nameSize = 24, nameSmall = 18, titleSize = 15, cueSize = 17; public float[] titleColor = null; /*HUDPOLISH: null = colors.dim*/ public float nameTracking = 3.2f, nameGap = 9f, titleGap = 3f, cueGap = 8f;
        public bool showTitle = true, showPosture = true, showStamina = false; public SecondC second = new SecondC(); public SubtitleC subtitle = new SubtitleC(); public AlignC align = new AlignC();   // TWOFIGHT v1; HUDALIGN v1
        public BarC hp = new BarC { h = 14f, taper = 20f }, stamina = new BarC { w = 420f, h = 5f, taper = 8f, gap = 9f }, posture = new BarC { w = 420f, h = 4f, taper = 7f, gap = 9f };
    }
    // TWOFIGHT v1: hud.json boss.second = the small bar of the second enemy (enabled false = nothing is drawn)
    [Serializable] public class SecondC { public bool enabled = false, ticks = false; public float gap = 10f, widthScale = 0.55f, heightScale = 0.6f, nameScale = 0.7f; }
    // HUDALIGN v1: hud.json boss.subtitle / boss.align (defaults = the old layout: title on its own line, bar from boss.top)
    [Serializable] public class SubtitleC { public bool inline = false, italic = true; public float gap = 14f, descent = 0.2f, dy = 0f; public int size = 0; }
    [Serializable] public class AlignC { public bool enabled = false, nameBaseline = true; public float barPivot = 0.5f, barDy = 0f; }
    [Serializable] public class StanceC
    {
        public bool enabled = true; public float x = 48f, bottom = 58f; public int captionSize = 11, nameSize = 17, nameSmall = 13, hintSize = 11, toastSize = 24, toastSmall = 18; public float tracking = 2.4f, diamond = 9f, toastY = 0.70f;
        public string caption = "STANCE", hint = "3 / 4";
    }
    [Serializable] public class SpecialItemC { public string key = "1", glyph = "slash", label = ""; }
    [Serializable] public class SpecialsC
    {
        public bool enabled = true; public float right = 52f, bottom = 56f, size = 62f, gap = 20f, ringPx = 2.2f, glyphScale = 0.62f, ticksPerSecond = 60f, cooldownTicks = 540f, readyFlashS = 0.55f;
        public int keySize = 13, cdSize = 21; public float keyDy = 5f; public int pieSteps = 48;
        public SpecialItemC[] items = { new SpecialItemC { key = "1", glyph = "slash" }, new SpecialItemC { key = "2", glyph = "crescent" } };
    }
    [Serializable] public class GuardDirC
    {
        public bool enabled = true; public float radius = 64f, arcDeg = 74f, thickness = 0.16f, fadeInS = 0.07f, fadeOutS = 0.2f, idleAlpha = 0.26f, dotSize = 8f, ringAlpha = 0.18f, parryPulseHz = 8f, parryGrow = 0.12f;
        // STEERUI v93: arcs anchored to the player's projected body (fractions of the player's on-screen height); radius is only used by the pre-v93 layout
        public bool anchorPlayer = true; public float centreFrac = 0.55f, sideDistFrac = 0.38f, topDistFrac = 0.57f, arcRadiusFrac = 0.40f, ringFrac = 0f, fallbackHeightFrac = 0.30f, defaultHeightM = 1.7f, minHeightM = 1.2f, maxHeightM = 2.6f, heightRefreshS = 1f;
    }
    [Serializable] public class DebugC { public bool camLine = false; public int fpsLabelSize = 13; }
    [Serializable] public class HintC
    {
        public bool enabled = true; public string toggleKey = "F1"; public float delayS = 0.9f, showSeconds = 0f, fadeSeconds = 0.25f;
        public int columns = 2; public float colWidth = 232f, rowHeight = 19f; public int fontSize = 13; public float keyWidth = 92f, margin = 28f, pad = 10f, panelAlpha = 0.62f;
        public string title = "CONTROLS", footer = "F1 hide / show", cornerText = "F1  Controls"; public int titleSize = 17, footerSize = 12, cornerSize = 11; public float bottom = 96f, cornerSeconds = 12f, cornerAlpha = 0.5f; public HudRowKA[] rows = new HudRowKA[0];
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
        public float delayS = 0.9f, fadeInS = 0.7f, dimAlpha = 0.35f, bandHeight = 420f, bandLift = 20f, bandAlpha = 0.74f;
        public int titleSize = 78, subtitleSize = 22, statsSize = 16; public float buttonW = 236f, buttonH = 50f, buttonGap = 26f; public int buttonSize = 20, keyHintSize = 12, nextLabelSize = 13;
        public string statSep = "     "; public bool nextOnDefeat = false;
        // HUD2: fade + serif title + small prompts
        public bool showStats = true, subtitleItalic = true; public float titleTracking = 12f, titleTrackingExtra = 20f, ruleW = 380f, promptGap = 70f, promptY = 34f, hudFade = 0.85f, titleLift = 10f; public int promptSize = 19, promptHintSize = 12;
        public float[] victoryTint = { 0.02f, 0.015f, 0f, 1f }, defeatTint = { 0.16f, 0f, 0.015f, 1f };
        // HUDFIX v92: end-screen legibility: secondary text colour (caption, stats, hints, next preview), glyph outline (px at 1080, 0 = off) in the shadow colour
        public float[] secondary = { 0.84f, 0.81f, 0.75f, 1f }; public float outlinePx = 2f, outlineAlpha = 0.9f;
        public float plateW = 820f, plateFeather = 140f, plateAlpha = 0.6f;   // HUDFIX v92: dark backing plate behind the end-screen text (width, soft side edges, alpha)
        public EndC victory = new EndC { title = "VICTORY", subtitle = "The {0} falls. The gate stands open.", color = new[] { 0.88f, 0.71f, 0.31f, 1f } };
        public EndC defeat = new EndC { title = "YOU DIED", subtitle = "The dark takes you. Rise again.", color = new[] { 0.74f, 0.08f, 0.10f, 1f } };
        public LabelsC labels = new LabelsC();
        public string retryText = "Retry", nextText = "Next opponent", nextPreview = "Next: {0}", fightText = "FIGHT {0}  -  {1}";
        public string retryHint = "Backspace / B", confirmHint = "Enter / A", retryHintDefeat = "Enter / Backspace / A";
        public bool zonesNoEndScreen = false;   // SEAMLESS v1: true = in a zones run (GameLoop.ZonesNoEnd) the VICTORY / YOU DIED card, its Next / Retry prompts and the end fade never exist
    }
    [Serializable] public class FadeC { public bool enabled = true; public float inS = 0.7f, outS = 0.45f; public float[] color = { 0f, 0f, 0f }; }
    [Serializable] public class InputC
    {
        public string[] confirmKeys = { "Return", "KeypadEnter", "JoystickButton0" }, retryKeys = { "Backspace", "JoystickButton1" }, midFightRetryKeys = { "Backspace" };
    }
    // HCKIT6 v100 (HC3 K13): the layout factors GameHUD / HudDraw held as literals (hud.json "layout"). Defaults = the old literals.
    [Serializable] public class LayoutC
    {
        public float nameLine = 1.35f, bossMinW = 300f, titleBox = 1.5f, titleAdvance = 1.4f, cueLift = 2f, cueSmallDelta = 3f, cueTracking = 3f;
        public float captionLift = 1.5f, captionW = 200f, captionH = 1.6f, diamondLine = 1.1f, diamondDx = 1f, diamondGap = 8f, hintGap = 16f, hintW = 160f, hintH = 1.6f, hintAlpha = 0.8f, hintDy = 0.2f, toastTracking = 1.5f;
        public float cornerFadeS = 1.5f, cornerDy = 1.5f, cornerW = 260f, cornerH = 1.6f;
        public float[] keysPanel = { 0.02f, 0.02f, 0.025f }; public float keysLinePx = 1f, keysTitleTracking = 3f, keysTitleDy = 0.6f, keysTitleLine = 1.7f, keysTitleSmall = 3f, keysRowInk = 0.9f, keysFooterLine = 1.8f, keysFooterBox = 1.6f, keysFooterLift = 2f, keysFooterAdvance = 1.7f;
        public float[] iconBack = { 0.025f, 0.025f, 0.03f, 0.86f }, pieShade = { 0f, 0f, 0f, 0.62f }; public float iconInk = 0.95f, iconCoolAlpha = 0.75f, cdInk = 0.95f, flashBase = 1.1f, flashGrow = 0.35f, keyLabelPad = 20f, keyLabelBox = 1.6f, keyLabelInk = 0.85f;
        public float guardDotCentre = 1.25f, guardRingPx = 0.5f;
        public float spaceW = 0.55f, lineH = 1.45f, shadowDiv = 14f, runPad = 6f; public int capsCache = 400;
    }
    [Serializable] public class Data
    {
        public bool enabled = true; public float referenceHeight = 1080f, minScale = 0.6f, maxScale = 3f;
        public ColorsC colors = new ColorsC(); public FontsC fonts = new FontsC(); public BarsC bars = new BarsC(); public PlayerC player = new PlayerC(); public BossC boss = new BossC(); public StanceC stance = new StanceC(); public SpecialsC specials = new SpecialsC(); public GuardDirC guardDir = new GuardDirC(); public DebugC debug = new DebugC(); public HintC hint = new HintC();
        public VignetteC vignette = new VignetteC(); public LowHpC lowHp = new LowHpC(); public HitTickC hitTick = new HitTickC(); public ScreensC screens = new ScreensC(); public FadeC fade = new FadeC(); public InputC input = new InputC();
        public LayoutC layout = new LayoutC();   // HCKIT6 v100
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
        if (d.colors == null) d.colors = new ColorsC(); if (d.fonts == null) d.fonts = new FontsC(); if (d.bars == null) d.bars = new BarsC(); if (d.player == null) d.player = new PlayerC(); if (d.boss == null) d.boss = new BossC(); if (d.stance == null) d.stance = new StanceC(); if (d.specials == null) d.specials = new SpecialsC(); if (d.guardDir == null) d.guardDir = new GuardDirC(); if (d.debug == null) d.debug = new DebugC(); if (d.hint == null) d.hint = new HintC();
        if (d.vignette == null) d.vignette = new VignetteC(); if (d.lowHp == null) d.lowHp = new LowHpC(); if (d.hitTick == null) d.hitTick = new HitTickC(); if (d.screens == null) d.screens = new ScreensC();
        if (d.fade == null) d.fade = new FadeC(); if (d.input == null) d.input = new InputC();
        if (d.hint.rows == null) d.hint.rows = new HudRowKA[0]; if (d.bars.ticks == null) d.bars.ticks = new float[0];
        if (d.fonts.serif == null || d.fonts.serif.Length == 0) d.fonts.serif = new FontsC().serif; if (d.fonts.title == null || d.fonts.title.Length == 0) d.fonts.title = new FontsC().title; if (d.fonts.ui == null || d.fonts.ui.Length == 0) d.fonts.ui = new FontsC().ui;
        if (d.player.hp == null) d.player.hp = new PlayerC().hp; if (d.player.stamina == null) d.player.stamina = new PlayerC().stamina; if (d.player.posture == null) d.player.posture = new PlayerC().posture;
        if (d.boss.hp == null) d.boss.hp = new BossC().hp; if (d.boss.stamina == null) d.boss.stamina = new BossC().stamina; if (d.boss.posture == null) d.boss.posture = new BossC().posture;
        if (d.specials.items == null) d.specials.items = new SpecialsC().items; d.specials.pieSteps = Mathf.Clamp(d.specials.pieSteps, 8, 128); d.bars.texScale = Mathf.Clamp(d.bars.texScale, 1f, 4f);
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
