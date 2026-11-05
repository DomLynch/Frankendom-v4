using System;
using System.IO;
using UnityEngine;

// Player control scheme data (lane CTRL, v68): Assets/Data/controls.json (+ identical StreamingAssets copy). No file or "enabled": false = the old classic controls, guard covers every side.
// Read by DirectionalInput (swipe attacks, guard side, marker, combat camera), DirectionalGuard (side-match in Duel.Resolve), CameraRig (lock ease), AIBrain / FlowBot (guard side).
public static class ControlsTable
{
    [Serializable] public class MouseCfg { public float axisToPx = 10f, flickWindowMs = 100f, deadZonePx = 18f, guardClampPx = 60f; }
    [Serializable] public class PadCfg
    {
        public bool enabled = true; public string stickX = "RightStickX", stickY = "RightStickY"; public bool invertY = false; public float deadZone = 0.35f; public bool guardLatch = true;
        public string[] attackKeys = { "JoystickButton5" }, guardKeys = { "JoystickButton4" }, special1Keys = { "JoystickButton3" }, special2Keys = { "JoystickButton2" };
    }
    [Serializable] public class Sector { public string name = ""; public float deg = 0f, halfWidthDeg = 45f; }
    [Serializable] public class DirMap { public string dir = ""; public string[] light = new string[0], heavy = new string[0]; }
    [Serializable] public class AttackCfg
    {
        public string strikeFire = "release"; public float holdHeavyMs = 220f, heavyGraceMs = 100f, heavyHoldMsInCounterWindow = 80f; public bool useCounterHeavyHold = false;
        public string noSlideDir = "stab"; public Sector[] sectors = new Sector[0]; public DirMap[] map = new DirMap[0];
    }
    [Serializable] public class GuardSide { public string name = ""; public int id = 0; public float deg = 0f, halfWidthDeg = 45f; public string[] aliases = new string[0]; }
    [Serializable] public class GuardMatch { public string attack = ""; public string[] guards = new string[0]; }
    [Serializable] public class EaseRow { public string cls = "light"; public float bonusMs = 0f; }
    [Serializable] public class GuardCfg
    {
        public bool guardRequiresSide = true, classicRequiresSide = false; public string snap = "latch"; public float deadBandDeg = 15f;
        public string centreName = "centre"; public string[] centreAliases = new string[0];
        public GuardSide[] sides = new GuardSide[0]; public GuardMatch[] match = new GuardMatch[0];
        public float sideGraceMs = 0f, heavyBlockWindowBonusMs = 0f; public EaseRow[] blockEase = new EaseRow[0];
    }
    [Serializable] public class CameraCfg { public string combatCamera = "free"; public bool lockAtDuelStart = true; public float enterRange = 4f, stickyRange = 6f, exitRange = 12f, exitSeconds = 3f, easeIn = 3f, easeOut = 3f; }
    [Serializable] public class MarkerCfg
    {
        public bool enabled = true; public string anchor = "crosshair"; public float yOffset = 2f, sizePx = 8f, distPx = 24f, barLengthPx = 22f, barThickPx = 4f; public bool showIdle = true, showLabel = false; public int fontSize = 11;
        public float[] attackColor = { 0.96f, 0.78f, 0.3f, 0.95f }, heavyColor = { 0.95f, 0.35f, 0.2f, 0.95f }, guardColor = { 0.55f, 0.8f, 1f, 0.95f }, idleColor = { 0.96f, 0.93f, 0.85f, 0.35f };
    }
    [Serializable] public class BotCfg { public float sideReadChance = 0.8f; }

    [Serializable]
    public class Data
    {
        public bool enabled = true, debugLog; public string scheme = "swipe";
        public MouseCfg mouse = new MouseCfg(); public PadCfg pad = new PadCfg(); public AttackCfg attack = new AttackCfg(); public GuardCfg guard = new GuardCfg();
        public CameraCfg camera = new CameraCfg(); public MarkerCfg marker = new MarkerCfg(); public BotCfg bot = new BotCfg();
    }

    static Data data; static bool loaded;
    public static Data Get() { if (!loaded) Load(); return data; }
    public static void Reload() { loaded = false; Load(); }
    public static bool Swipe { get { var d = Get(); return d.enabled && d.scheme == "swipe"; } }
    public static bool SideRequired { get { var d = Get(); return d.enabled && d.guard.guardRequiresSide; } }

    // default guard sides when the file is missing: the old AIBrain mapping (left 1, right 2, overhead 3)
    static GuardSide[] DefaultSides()
    {
        return new[] {
            new GuardSide { name = "top", id = 3, deg = 0f, halfWidthDeg = 50f, aliases = new[] { "overhead", "up" } },
            new GuardSide { name = "left", id = 1, deg = -90f, halfWidthDeg = 90f },
            new GuardSide { name = "right", id = 2, deg = 90f, halfWidthDeg = 90f } };
    }

    static void Load()
    {
        loaded = true; data = new Data { enabled = false }; data.guard.sides = DefaultSides();
        string json = null;
        try
        {
#if UNITY_EDITOR
            { string p = Path.Combine(Application.dataPath, "Data/controls.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "controls.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("ControlsTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) return;   // no file: classic controls, any guard side blocks
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.mouse == null) d.mouse = new MouseCfg(); if (d.pad == null) d.pad = new PadCfg(); if (d.attack == null) d.attack = new AttackCfg(); if (d.guard == null) d.guard = new GuardCfg();
            if (d.camera == null) d.camera = new CameraCfg(); if (d.marker == null) d.marker = new MarkerCfg(); if (d.bot == null) d.bot = new BotCfg();
            if (d.attack.sectors == null) d.attack.sectors = new Sector[0]; if (d.attack.map == null) d.attack.map = new DirMap[0];
            if (d.guard.sides == null || d.guard.sides.Length == 0) d.guard.sides = DefaultSides(); if (d.guard.match == null) d.guard.match = new GuardMatch[0];
            if (d.guard.blockEase == null) d.guard.blockEase = new EaseRow[0]; if (d.guard.centreAliases == null) d.guard.centreAliases = new string[0];
            if (string.IsNullOrEmpty(d.scheme)) d.scheme = "swipe";
            data = d;
        }
        catch (Exception ex) { Debug.LogError("ControlsTable: parse failed: " + ex.Message); }
    }

    // ---------------- shared helpers ----------------
    // screen angle of a slide: 0 = up, +90 = right, -90 = left, 180 = down
    public static float AngleOf(Vector2 v) { return Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg; }
    public static float Diff(float a, float b) { return Mathf.Abs(Mathf.DeltaAngle(a, b)); }

    public static Color Col(float[] c, Color fallback) { return c != null && c.Length >= 4 ? new Color(c[0], c[1], c[2], c[3]) : fallback; }

    public static KeyCode[] Keys(string[] names)
    {
        var list = new System.Collections.Generic.List<KeyCode>();
        if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (!string.IsNullOrEmpty(names[i]) && Enum.TryParse(names[i], true, out k)) list.Add(k); }
        return list.ToArray();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded = false; data = null; }
}
