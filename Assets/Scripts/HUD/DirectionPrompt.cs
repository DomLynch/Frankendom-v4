using System;
using System.Collections.Generic;
using UnityEngine;

// CLOSE6 (alpha item 6, gaps G2 + G6): the one-time onboarding prompt of a fresh save. Teaches overhead, thrust, slash left, slash right and the guard sides, one line at a time.
// Data: direction_prompt.json (StreamingAssets first in the player, Assets/Data in the Editor: DataPath). Missing file or "enabled": false = this class does nothing.
// Wording per device: a connected gamepad (Input.GetJoystickNames, old Input Manager like the rest of the project) shows the "pad" lines, else the "kbm" lines.
// Words in braces are replaced with the LIVE bindings so the text stays true after a rebind: {strike} {guard} {thrust} {guardLeft} {guardRight} {guardUp} (any InputMap action id, current keys)
// and {padStrike} {padGuard} (controls.json pad.attackKeys / pad.guardKeys through menu.json padNames). An unknown word is left as written.
// Once per save: PlayerPrefs int <prefsKey> = 1 after the last line (or the dismiss key). Pause > New game (SaveSlot.OnErase) clears the flag, so a new save is taught again.
// Never runs in harness runs (RunMode.IsHarness / MenuCfg.Harness) or with -camscene / -dirproof / -camtrace (skipArgs, prefix match): those use sandbox saves.
// Log: "[DirectionPrompt] ..." (boot decision, shown, done). Presentation only: reads input for the dismiss key, changes no game state. Roll-back = delete this file and the two json files.
public class DirectionPrompt : MonoBehaviour
{
    [Serializable] public class Cfg
    {
        public bool enabled = true, debugLog;
        public string prefsKey = "frankendom.directionPrompt.v1.shown";
        public string[] skipArgs = { "-camscene", "-dirproof", "-camtrace" };
        public float showDelayS = 4f, secondsPerLine = 6f, fadeS = 0.35f;
        public bool pauseWhileMenuOpen = true, showCounter = true;
        public string[] dismissKeys = { "Return", "KeypadEnter", "JoystickButton6" };
        public string title = "HOW TO FIGHT", dismissKbm = "Enter  Skip", dismissPad = "Back  Skip", keyJoin = " / ";
        public string[] kbm = new string[0], pad = new string[0];
        public float x = 0.5f, y = 0.24f, width = 980f, padPx = 18f;       // x / y = panel centre as a screen fraction (y from the top); width / padPx in reference-1080 px (HudCfg.Scale)
        public int fontSize = 30, titleSize = 20, footSize = 16;
        public string font = "serif", titleFont = "title";
        public float[] textColor = { 0.96f, 0.93f, 0.85f, 1f }, titleColor = { 0.96f, 0.78f, 0.30f, 1f }, backColor = { 0f, 0f, 0f, 0.55f };
    }

    static Cfg cfg; static DirectionPrompt inst;
    string[] lines; bool padMode; int idx = -1; float wait, lineT; KeyCode[] dismiss; GUIStyle sBody, sTitle, sFoot; int builtFor = -1;

    static Cfg Load()
    {
        string t = DataPath.ReadText("direction_prompt.json"); if (string.IsNullOrEmpty(t)) return null;
        try { return JsonUtility.FromJson<Cfg>(t); } catch (Exception e) { Debug.LogWarning("[DirectionPrompt] direction_prompt.json could not be parsed: " + e.Message); return null; }
    }

    static string SkipArg(Cfg c)
    {
        if (c.skipArgs == null) return null;
        foreach (var a in Environment.GetCommandLineArgs()) foreach (var s in c.skipArgs) if (!string.IsNullOrEmpty(s) && a.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        inst = null; cfg = Load();
        if (cfg == null || !cfg.enabled) return;   // no file / switched off by data = nothing, not even a log line
        if (string.IsNullOrEmpty(cfg.prefsKey)) cfg.prefsKey = new Cfg().prefsKey;
        string skip = SkipArg(cfg);
        if (skip != null || RunMode.IsHarness || MenuCfg.Harness) { Debug.Log("[DirectionPrompt] off: harness run" + (skip != null ? " (" + skip + ")" : "")); return; }
        SaveSlot.OnErase -= OnErase; SaveSlot.OnErase += OnErase;   // Pause > New game = a fresh save = teach again
        if (PlayerPrefs.GetInt(cfg.prefsKey, 0) != 0) { Debug.Log("[DirectionPrompt] off: already shown for this save (" + cfg.prefsKey + ")"); return; }
        Arm();
    }

    static void Arm()
    {
        if (inst != null || cfg == null || !cfg.enabled) return;
        var go = new GameObject("DirectionPrompt"); DontDestroyOnLoad(go); inst = go.AddComponent<DirectionPrompt>();
        Debug.Log("[DirectionPrompt] armed: shows in " + cfg.showDelayS + " s, " + cfg.secondsPerLine + " s per line");
    }

    static void OnErase()
    {
        if (cfg == null || !cfg.enabled) return;
        PlayerPrefs.DeleteKey(cfg.prefsKey); PlayerPrefs.Save(); Debug.Log("[DirectionPrompt] save erased: flag cleared");
        if (inst != null) { inst.idx = -1; inst.wait = 0f; } else Arm();
    }

    static bool PadConnected()
    {
        try { var n = Input.GetJoystickNames(); if (n != null) for (int i = 0; i < n.Length; i++) if (!string.IsNullOrEmpty(n[i])) return true; } catch (Exception) { }
        return false;
    }

    // ---------------- words in braces ----------------
    static string PadName(string key)
    {
        const string pre = "JoystickButton"; int n; var names = MenuCfg.Get().controls.padNames;
        if (key != null && names != null && key.StartsWith(pre, StringComparison.Ordinal) && int.TryParse(key.Substring(pre.Length), out n) && n >= 0 && n < names.Length) return names[n];
        return key ?? "";
    }
    static string PadList(string[] keys) { if (keys == null || keys.Length == 0) return null; var s = new List<string>(); foreach (var k in keys) if (!string.IsNullOrEmpty(k)) s.Add(PadName(k)); return s.Count > 0 ? string.Join(cfg.keyJoin, s.ToArray()) : null; }
    static string Word(string id)
    {
        try
        {
            if (id == "padStrike") return PadList(ControlsTable.Get().pad.attackKeys);
            if (id == "padGuard") return PadList(ControlsTable.Get().pad.guardKeys);
            var a = InputMap.Find(id); if (a == null || a.cur == null) return null;
            var s = new List<string>(); foreach (var k in a.cur) if (k != KeyCode.None && !InputMap.IsPad(k)) s.Add(InputMap.Name(k));
            return s.Count > 0 ? string.Join(cfg.keyJoin, s.ToArray()) : InputMap.Ui.unbound;
        }
        catch (Exception) { return null; }
    }
    static string Fill(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        int from = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            int a = line.IndexOf('{', from); if (a < 0) break; int b = line.IndexOf('}', a + 1); if (b < 0) break;
            string v = Word(line.Substring(a + 1, b - a - 1));
            if (v == null) { from = b + 1; continue; }
            line = line.Substring(0, a) + v + line.Substring(b + 1); from = a + v.Length;
        }
        return line;
    }

    void Begin()
    {
        padMode = PadConnected() && cfg.pad != null && cfg.pad.Length > 0;
        var src = padMode ? cfg.pad : cfg.kbm; if (src == null) src = new string[0];
        lines = new string[src.Length]; for (int i = 0; i < src.Length; i++) lines[i] = Fill(src[i]);
        dismiss = ControlsTable.Keys(cfg.dismissKeys);
        if (lines.Length == 0) { Debug.LogWarning("[DirectionPrompt] no lines in direction_prompt.json (" + (padMode ? "pad" : "kbm") + "): nothing shown, not marked"); inst = null; Destroy(gameObject); return; }
        idx = 0; lineT = 0f;
        Debug.Log("[DirectionPrompt] shown: device=" + (padMode ? "pad" : "kbm") + " lines=" + lines.Length + " first=\"" + lines[0] + "\"");
    }

    void Finish(string why)
    {
        PlayerPrefs.SetInt(cfg.prefsKey, 1); PlayerPrefs.Save();
        Debug.Log("[DirectionPrompt] done (" + why + "): " + cfg.prefsKey + " = 1, will not show again on this save");
        inst = null; Destroy(gameObject);
    }

    void Update()
    {
        if (cfg == null) return;
        if (cfg.pauseWhileMenuOpen && MenuUI.Open) return;
        float dt = Time.unscaledDeltaTime; if (dt > 0.25f) dt = 0.25f;   // a load hitch does not eat the prompt
        if (idx < 0) { if (!LoadingCard.Cleared) return; wait += dt; if (wait >= cfg.showDelayS) Begin(); return; }   // dev2: the delay counts from the moment the loading card is gone (Cleared is true without a card)
        if (dismiss != null) for (int i = 0; i < dismiss.Length; i++) if (dismiss[i] != KeyCode.None && Input.GetKeyDown(dismiss[i])) { Finish("dismissed at line " + (idx + 1)); return; }
        lineT += dt;
        if (lineT >= Mathf.Max(0.5f, cfg.secondsPerLine)) { idx++; lineT = 0f; if (idx >= lines.Length) Finish("all lines"); else if (cfg.debugLog) Debug.Log("[DirectionPrompt] line " + (idx + 1) + ": " + lines[idx]); }
    }

    static Color Col(float[] c, float alpha) { return c != null && c.Length >= 4 ? new Color(c[0], c[1], c[2], c[3] * alpha) : new Color(1f, 1f, 1f, alpha); }

    void Styles()
    {
        if (sBody != null && builtFor == cfg.fontSize) return; builtFor = cfg.fontSize;
        sBody = new GUIStyle(HudDraw.Style(cfg.font, cfg.fontSize, TextAnchor.MiddleCenter, false, false)) { wordWrap = true, richText = false };
        sTitle = new GUIStyle(HudDraw.Style(cfg.titleFont, cfg.titleSize, TextAnchor.MiddleCenter, false, false)) { wordWrap = false };
        sFoot = new GUIStyle(HudDraw.Style(cfg.font, cfg.footSize, TextAnchor.MiddleCenter, false, true)) { wordWrap = false };
    }

    void OnGUI()
    {
        if (cfg == null || idx < 0 || lines == null || idx >= lines.Length || Event.current.type != EventType.Repaint) return;
        if (cfg.pauseWhileMenuOpen && MenuUI.Open) return;
        Styles();
        float per = Mathf.Max(0.5f, cfg.secondsPerLine), fade = Mathf.Clamp(cfg.fadeS, 0.01f, per * 0.5f);
        float alpha = Mathf.Clamp01(Mathf.Min(lineT / fade, (per - lineT) / fade));
        float s = HudCfg.Scale(), vw = Screen.width / s, vh = Screen.height / s;
        var m = GUI.matrix; var col = GUI.color; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        try
        {
            float w = Mathf.Min(cfg.width, vw - 40f), pad = cfg.padPx, inner = w - pad * 2f;
            var body = new GUIContent(lines[idx]);
            float titleH = string.IsNullOrEmpty(cfg.title) ? 0f : cfg.titleSize * 1.6f, bodyH = sBody.CalcHeight(body, inner);
            string foot = padMode ? cfg.dismissPad : cfg.dismissKbm; if (foot == null) foot = "";
            if (cfg.showCounter) foot = (idx + 1) + " / " + lines.Length + (foot.Length > 0 ? "      " + foot : "");
            float footH = foot.Length > 0 ? cfg.footSize * 1.7f : 0f, h = pad * 2f + titleH + bodyH + footH;
            float px = vw * cfg.x - w * 0.5f, py = vh * cfg.y - h * 0.5f;
            GUI.color = Col(cfg.backColor, alpha); GUI.DrawTexture(new Rect(px, py, w, h), Texture2D.whiteTexture);
            float yy = py + pad;
            if (titleH > 0f) { GUI.color = Col(cfg.titleColor, alpha); GUI.Label(new Rect(px + pad, yy, inner, titleH), cfg.title, sTitle); yy += titleH; }
            GUI.color = Col(cfg.textColor, alpha); GUI.Label(new Rect(px + pad, yy, inner, bodyH), body, sBody); yy += bodyH;
            if (footH > 0f) { GUI.color = Col(cfg.textColor, alpha * 0.7f); GUI.Label(new Rect(px + pad, yy, inner, footH), foot, sFoot); }
        }
        finally { GUI.color = col; GUI.matrix = m; }
    }
}
