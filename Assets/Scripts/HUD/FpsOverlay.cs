using System;
using UnityEngine;

// FPSOVERLAY v1: a small FPS counter in a screen corner (default top right), like most games have. Own OnGUI host (like PotionHud / BowHud): GameHUD, hud.json and the F3 debug label are untouched.
// Everything comes from fps_overlay.json (DataPath: Assets/Data in the editor, StreamingAssets in a player): corner, size and margins as fractions of the screen height (so it scales with the resolution),
// colours, smoothing window, whether the frame time shows, the toggle key and the launch argument that hides it. No file = no object at all (default off in code).
//   FPS = frames / unscaled seconds over the smoothing window; the text is rebuilt once per window (twice a second at 0.5 s) and cached, OnGUI allocates nothing per frame.
//   Never created in batch mode; hidden while a test harness flag is on (GameLoop.HarnessOn) when the json says hideInHarness; "-nofps" on the command line = not created.
public sealed class FpsOverlay : MonoBehaviour
{
    [Serializable]
    public class Cfg
    {
        public bool enabled = false;                       // start state; the toggle key flips it at runtime
        public string corner = "topRight";                 // topRight | topLeft | bottomRight | bottomLeft
        public float fontHeightFrac = 0.016f;              // font px = this x Screen.height (rounded, at least minFontPx)
        public int minFontPx = 10;
        public float lineHeightMul = 1.4f;                 // label rect height = font px x this
        public float marginXFrac = 0.012f, marginYFrac = 0.012f;   // distance from the corner, x Screen.height
        public float boxWidthFrac = 0.22f;                 // label rect width, x Screen.height (text is anchored to the corner inside it)
        public float[] color = { 0.92f, 0.92f, 0.88f, 0.62f };
        public float smoothSeconds = 0.5f;                 // averaging window = text refresh period
        public bool showMs = true;
        public float msPerSecond = 1000f;                  // unit factor seconds -> ms
        public int fpsDigits = 0, msDigits = 1;
        public string format = "{0} FPS", formatMs = "{0} FPS  {1} ms";
        public string font = "ui";                         // HudDraw font kind: ui | serif | title
        public bool bold = false;
        public int guiDepth = -1000;                       // lower = drawn later (on top of the other OnGUI hosts)
        public string[] toggleKeys = { "F2" };
        public string hideArg = "-nofps";
        public bool hideInBatchMode = true, hideInHarness = true, hideInFinisher = false;
    }

    static Cfg cfg; static bool cfgLoaded;
    public static Cfg Get()
    {
        if (cfgLoaded) return cfg;
        cfgLoaded = true; cfg = null;
        string txt = null;
        try { txt = DataPath.ReadText("fps_overlay.json"); } catch (Exception) { txt = null; }
        if (string.IsNullOrEmpty(txt)) return null;   // file missing = no overlay
        try
        {
            var d = JsonUtility.FromJson<Cfg>(txt.TrimStart((char)0xFEFF)); if (d == null) return null;
            var def = new Cfg();
            if (d.color == null || d.color.Length < 3) d.color = def.color;
            if (d.toggleKeys == null) d.toggleKeys = def.toggleKeys;
            if (string.IsNullOrEmpty(d.format)) d.format = def.format;
            if (string.IsNullOrEmpty(d.formatMs)) d.formatMs = def.formatMs;
            cfg = d;
        }
        catch (Exception e) { Debug.LogWarning("[FpsOverlay] fps_overlay.json parse failed, overlay off: " + e.Message); cfg = null; }
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            var C = Get(); if (C == null) return;
            if (C.hideInBatchMode && Application.isBatchMode) return;
            if (C.hideArg != null && C.hideArg.Length > 0) foreach (var a in Environment.GetCommandLineArgs()) if (string.Equals(a, C.hideArg, StringComparison.OrdinalIgnoreCase)) return;
            if (FindFirstObjectByType<FpsOverlay>() != null) return;
            var go = new GameObject("FpsOverlay"); DontDestroyOnLoad(go); go.AddComponent<FpsOverlay>();
        }
        catch (Exception e) { Debug.LogWarning("[FpsOverlay] boot failed: " + e.Message); }
    }

    Cfg C; KeyCode[] keys; bool on, right, bottom; TextAnchor anchor;
    float acc; int frames; string text = "", fpsFmt, msFmt;
    int fontPx, lastH = -1; Rect box; Color col;

    void Awake()
    {
        C = Get(); if (C == null) { enabled = false; return; }
        keys = HudCfg.Keys(C.toggleKeys, new KeyCode[0]); on = C.enabled;
        right = C.corner != null && C.corner.EndsWith("Right", StringComparison.OrdinalIgnoreCase);
        bottom = C.corner != null && C.corner.StartsWith("bottom", StringComparison.OrdinalIgnoreCase);
        anchor = bottom ? (right ? TextAnchor.LowerRight : TextAnchor.LowerLeft) : (right ? TextAnchor.UpperRight : TextAnchor.UpperLeft);
        fpsFmt = "F" + Mathf.Max(0, C.fpsDigits); msFmt = "F" + Mathf.Max(0, C.msDigits);
        col = HudCfg.Col(C.color);
    }

    void Update()
    {
        if (HudCfg.AnyDown(keys)) { on = !on; acc = 0f; frames = 0; }
        if (!on) return;
        acc += Time.unscaledDeltaTime; frames++;
        if (acc >= Mathf.Max(Time.unscaledDeltaTime, C.smoothSeconds) && frames > 0)
        {
            float fps = frames / acc, ms = C.msPerSecond * acc / frames;
            text = C.showMs ? string.Format(C.formatMs, fps.ToString(fpsFmt), ms.ToString(msFmt)) : string.Format(C.format, fps.ToString(fpsFmt));   // twice a second, not per frame
            acc = 0f; frames = 0;
        }
    }

    void OnGUI()
    {
        if (!on || text.Length == 0 || Event.current.type != EventType.Repaint) return;
        if (C.hideInHarness && GameLoop.HarnessOn) return;
        if (C.hideInFinisher && RTFinisher.Busy) return;
        int h = Screen.height;
        if (h != lastH)   // layout only when the resolution changes
        {
            lastH = h; fontPx = Mathf.Max(C.minFontPx, Mathf.RoundToInt(C.fontHeightFrac * h));
            float w = C.boxWidthFrac * h, mx = C.marginXFrac * h, my = C.marginYFrac * h, bh = fontPx * C.lineHeightMul;
            box = new Rect(right ? Screen.width - mx - w : mx, bottom ? h - my - bh : my, w, bh);
        }
        GUI.depth = C.guiDepth;
        float a = HudDraw.Alpha; HudDraw.Alpha = 1f;
        HudDraw.Text(box, text, C.font, fontPx, anchor, C.bold, col);
        HudDraw.Alpha = a;
    }
}
