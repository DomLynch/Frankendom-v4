using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

// MENU v100 (lane laneMENU): the PAUSE + SETTINGS menu, dark gothic style of the HUD2 (IMGUI, HudDraw kit, OS serif fonts, hud.json colours). All text / keys / tabs / rows / sizes: menu.json (MenuCfg).
//   Esc / pad Start opens PAUSE: Time.timeScale 0, AudioListener.pause, Duel.Frozen (no brain decides), cursor unlocked; everything is put back on close.
//   PAUSE: Resume, Settings, Controls, Restart fight (confirm), Quit to desktop (confirm).   SETTINGS tabs: Graphics, Audio, Gameplay, Controls (read-only key map from menu.json + controls.json).
//   Keyboard (arrows / WASD, Enter, Esc, Q / E tabs), gamepad (left stick, A, B, LB / RB, Start) and mouse (hover, click, wheel).
// It never opens during a finisher (RTFinisher.Busy), a fade, steer mode, or a harness run (MenuCfg.Harness); other full-screen UIs set MenuUI.Veto (e.g. the loot screen: MenuUI.Veto = () => LootScreen.Open).
// Others read MenuUI.Open / Blocking: GameLoopDriver (Enter / Backspace must not retry while the menu is up), CameraRig (mouse must not turn the camera), GameHUD (Esc no longer just frees the cursor).
[DefaultExecutionOrder(-450)]
public sealed partial class MenuUI : MonoBehaviour
{
    public static bool Open { get; private set; }
    public static int ClosedFrame = -1;
    /// <summary>True while the menu is up and on the frame it closed (input consumers skip that frame).</summary>
    public static bool Blocking { get { return Open || ClosedFrame == Time.frameCount; } }
    /// <summary>True when the menu exists in this run (data enabled, not a harness): GameHUD leaves Esc to it.</summary>
    public static bool Enabled { get { return MenuCfg.Get().enabled && !MenuCfg.Harness; } }
    public static Func<bool> Veto;   // another full-screen UI is up: do not open

    static MenuUI inst;
    enum Scr { Pause, Settings, Confirm }
    sealed class Row
    {
        public string kind = "info", label = "", hint = "", c1 = "", c2 = ""; public Func<string> val; public Action<int> step; public Action act; public Func<float> frac; public Action<float> setFrac; public string bindId = "";
    }

    // ---- state ----
    Scr scr = Scr.Pause; int pauseSel, tab, rowSel, scroll, confSel; string confText = "", confAction = "";
    List<Row> rows = new List<Row>(); Rect barRect;
    float prevTs, wantUntil; bool wantOpen; bool ownFreeze, cursorSaved, pendingUnfreeze, mouseActive; CursorLockMode prevLock; bool prevVis; float openedAt, unfreezeBy; Vector3 lastMouse;
    Vector2Int lastNav; float nextNav; bool axisBroken;
    object keyOwner; KeyCode[] kOpen, kClose, kBack, kConfirm, kUp, kDown, kLeft, kRight, kPrev, kNext;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly Dictionary<int, GUIStyle> wrapStyles = new Dictionary<int, GUIStyle>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Open = false; ClosedFrame = -1; Veto = null; inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (inst != null || !Enabled) return;
            var go = new GameObject("MenuUI"); DontDestroyOnLoad(go); inst = go.AddComponent<MenuUI>();
        }
        catch (Exception e) { Debug.LogWarning("[MenuUI] boot failed: " + e.Message); }
    }

    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; if (Open) ForceClose(); MenuSettings.Flush(); }
    void OnApplicationQuit() { MenuSettings.Flush(); }
    void OnApplicationFocus(bool f) { if (!f) MenuSettings.Flush(); }
    void OnLoaded(Scene s, LoadSceneMode m) { if (Open) ForceClose(); pendingUnfreeze = false; ownFreeze = false; }

    // ---------------- open / close ----------------
    /// <summary>Nothing forbids the menu right now (finisher, steer mode, harness, another UI owning the fight).</summary>
    static bool Allowed()
    {
        var C = MenuCfg.Get();
        if (!C.enabled || MenuCfg.Harness) return false;
        if (STSteer.Active || RTFinisher.Busy || GameLoopDriver.Transitioning) return false;
        if (GameLoop.Player == null || GameLoop.Enemy == null) return false;
        if (!C.allowOnEndScreen && GameLoop.Outcome != GameLoop.Result.None) return false;
        if (Duel.Frozen) return false;   // a finisher scene or another UI owns the fight
        if (Veto != null && Veto()) return false;
        return true;
    }
    /// <summary>The clock and the screen are quiet: no slow-mo dip (the menu never guesses its base fixed step) and no fade. An Esc press that is Allowed but not ClockReady waits openWaitS.</summary>
    static bool ClockReady() { return Time.timeScale >= 0.999f && GameLoop.FadeAlpha <= MenuCfg.Get().maxFadeToOpen; }

    void OpenMenu()
    {
        prevTs = Time.timeScale; Time.timeScale = 0f;
        AudioMix.SetPaused(true);   // RCHYGIENE v100: fades the sound out over audio_mix.json pause.fadeOutS, then pauses the listener
        if (!Duel.Frozen) { Duel.Frozen = true; ownFreeze = true; }
        if (!Application.isBatchMode) { prevLock = Cursor.lockState; prevVis = Cursor.visible; cursorSaved = true; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        pendingUnfreeze = false; Open = true; openedAt = Time.unscaledTime; scr = Scr.Pause; pauseSel = 0; mouseActive = false; lastMouse = Input.mousePosition; lastNav = Vector2Int.zero; nextNav = 0f;
    }

    /// <summary>Closes the menu and gives the clock, audio, cursor and the fight back. keepFrozen: a scene reload follows, leave Duel.Frozen set until it resets.</summary>
    void CloseMenu(bool keepFrozen = false)
    {
        if (!Open) return;
        Open = false; ClosedFrame = Time.frameCount;
        Time.timeScale = prevTs;   // CanOpen only lets the menu open at 1; fixedDeltaTime is never touched
        AudioMix.SetPaused(false);   // RCHYGIENE v100: un-pauses at once, fades back in
        CrashLog.AckNotice();   // RCHYGIENE v100: the 'last session crashed' line is shown for one pause only
        if (cursorSaved) { Cursor.lockState = prevLock; Cursor.visible = prevVis; cursorSaved = false; }
        if (ownFreeze && !keepFrozen) { pendingUnfreeze = true; unfreezeBy = Time.unscaledTime + MenuCfg.Get().unfreezeMaxS; }
        MenuSettings.Flush();
    }
    void ForceClose()
    {
        Open = false; ClosedFrame = Time.frameCount; Time.timeScale = 1f; AudioMix.SetPaused(false); ownFreeze = false; pendingUnfreeze = false;
        if (cursorSaved) { Cursor.lockState = prevLock; Cursor.visible = prevVis; cursorSaved = false; }
    }

    // ---------------- input ----------------
    void Keys(MenuCfg.Data C)
    {
        if (ReferenceEquals(keyOwner, C)) return; keyOwner = C; var I = C.input;
        kOpen = MenuCfg.Keys(I.openKeys, new[] { KeyCode.Escape }); kClose = MenuCfg.Keys(I.closeKeys, new KeyCode[0]); kBack = MenuCfg.Keys(I.backKeys, new[] { KeyCode.Escape }); kConfirm = MenuCfg.Keys(I.confirmKeys, new[] { KeyCode.Return });
        kUp = MenuCfg.Keys(I.upKeys, new[] { KeyCode.UpArrow }); kDown = MenuCfg.Keys(I.downKeys, new[] { KeyCode.DownArrow }); kLeft = MenuCfg.Keys(I.leftKeys, new[] { KeyCode.LeftArrow }); kRight = MenuCfg.Keys(I.rightKeys, new[] { KeyCode.RightArrow });
        kPrev = MenuCfg.Keys(I.tabPrevKeys, new[] { KeyCode.Q }); kNext = MenuCfg.Keys(I.tabNextKeys, new[] { KeyCode.E });
    }
    static bool Held(KeyCode[] ks) { if (ks != null) for (int i = 0; i < ks.Length; i++) if (Input.GetKey(ks[i])) return true; return false; }
    float Axis(string name)
    {
        if (axisBroken || string.IsNullOrEmpty(name)) return 0f;
        try { return Input.GetAxisRaw(name); } catch (Exception) { axisBroken = true; Debug.LogWarning("[MenuUI] input axis '" + name + "' not defined: stick navigation off"); return 0f; }
    }

    void Update()
    {
        var C = MenuCfg.Get(); Keys(C);
        if (MenuSettings.DirtyDue(C.saveDelayS)) MenuSettings.Flush();
        if (pendingUnfreeze)
        {
            if (RTFinisher.Busy || !ownFreeze) { pendingUnfreeze = false; ownFreeze = false; }   // a finisher took the freeze over
            else if (Time.frameCount > ClosedFrame && ((!Input.GetMouseButton(0) && !Input.GetMouseButton(1)) || Time.unscaledTime > unfreezeBy)) { Duel.Frozen = false; ownFreeze = false; pendingUnfreeze = false; }   // wait for the click that closed the menu to be released
        }
        if (!Open)
        {
            if (HudCfg.AnyDown(kOpen) && Allowed()) { wantOpen = true; wantUntil = Time.unscaledTime + C.openWaitS; }   // not remembered when a finisher / another UI is up: Esc that closes the loot screen must not pop the menu
            if (wantOpen)
            {
                if (Time.unscaledTime > wantUntil || !Allowed()) wantOpen = false;
                else if (ClockReady()) { wantOpen = false; OpenMenu(); }
            }
            return;
        }
        if (Time.timeScale != 0f) Time.timeScale = 0f;
        if (GameLoop.Player == null) { CloseMenu(); return; }
        var mp = Input.mousePosition; if ((mp - lastMouse).magnitude > C.input.mouseMovePx) { mouseActive = true; lastMouse = mp; }

        if (UpdateBind()) return;   // REBIND v100: a key capture owns every key while it waits
        if (HudCfg.AnyDown(kClose)) { CloseMenu(); return; }
        if (HudCfg.AnyDown(kBack)) { Back(); return; }
        if (HudCfg.AnyDown(kConfirm)) { mouseActive = false; Activate(); return; }
        if (scr == Scr.Settings)
        {
            if (HudCfg.AnyDown(kPrev)) { mouseActive = false; ChangeTab(-1); return; }
            if (HudCfg.AnyDown(kNext)) { mouseActive = false; ChangeTab(1); return; }
        }
        // direction with auto-repeat (keys, arrows, left stick)
        int dx = 0, dy = 0; var I = C.input;
        if (Held(kLeft)) dx--; if (Held(kRight)) dx++; if (Held(kUp)) dy--; if (Held(kDown)) dy++;
        float ax = Axis(I.axisX), ay = Axis(I.axisY);
        if (ax <= -I.axisThreshold) dx = -1; else if (ax >= I.axisThreshold) dx = 1;
        if (ay >= I.axisThreshold) dy = -1; else if (ay <= -I.axisThreshold) dy = 1;   // stick up = positive Vertical = previous row
        if (dy != 0) dx = 0;
        var dir = new Vector2Int(dx, dy);
        if (dir == Vector2Int.zero) { lastNav = dir; return; }
        float now = Time.unscaledTime; bool fire = false;
        if (dir != lastNav) { fire = true; nextNav = now + I.repeatDelayS; } else if (now >= nextNav) { fire = true; nextNav = now + I.repeatS; }
        lastNav = dir;
        if (fire) { mouseActive = false; Move(dx, dy); }
    }

    // ---------------- navigation ----------------
    void Back()
    {
        if (scr == Scr.Pause) CloseMenu();
        else { scr = Scr.Pause; MenuSettings.Flush(); }
    }
    static int Wrap(int i, int n) { return n <= 0 ? 0 : ((i % n) + n) % n; }
    void Move(int dx, int dy)
    {
        var C = MenuCfg.Get();
        if (scr == Scr.Pause) { if (dy != 0) pauseSel = Wrap(pauseSel + dy, C.items.Length); }
        else if (scr == Scr.Confirm) { if (dx != 0) confSel = 1 - confSel; }
        else
        {
            if (dy != 0) rowSel = Wrap(rowSel + dy, rows.Count);
            else if (dx != 0 && rowSel < rows.Count && rows[rowSel].step != null) rows[rowSel].step(dx);
        }
    }
    void Activate()
    {
        var C = MenuCfg.Get();
        if (scr == Scr.Pause) { if (pauseSel >= 0 && pauseSel < C.items.Length) DoItem(C.items[pauseSel]); }
        else if (scr == Scr.Confirm) { if (confSel == 1) Execute(confAction); else scr = Scr.Pause; }
        else if (rowSel >= 0 && rowSel < rows.Count)
        {
            var r = rows[rowSel];
            if (r.act != null) r.act(); else if (r.kind == "choice" || r.kind == "toggle") { if (r.step != null) r.step(1); }
        }
    }
    void DoItem(MenuCfg.ItemC it)
    {
        if (!string.IsNullOrEmpty(it.confirm)) { scr = Scr.Confirm; confText = it.confirm; confAction = it.id; confSel = 0; return; }
        Execute(it.id);
    }
    void Execute(string id)
    {
        var C = MenuCfg.Get();
        switch (id)
        {
            case "resume": CloseMenu(); break;
            case "settings": OpenSettings(FirstTab(false)); break;
            case "controls": OpenSettings(FirstTab(true)); break;
            case "newgame": NewGame(); break;   // REBIND v100
            case "restart":
                if (GameLoopDriver.Instance != null) { CloseMenu(true); GameLoop.Retry(); } else CloseMenu();
                break;
            case "quit":
                MenuSettings.Flush();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                break;
            default: Debug.LogWarning("[MenuUI] unknown menu item '" + id + "'"); scr = Scr.Pause; break;
        }
    }
    int FirstTab(bool controls)
    {
        var T = MenuCfg.Get().tabs; for (int i = 0; i < T.Length; i++) if (T[i] != null && (T[i].type == "controls") == controls) return i; return 0;
    }
    void OpenSettings(int t)
    {
        var T = MenuCfg.Get().tabs; if (T.Length == 0) { scr = Scr.Pause; return; }
        scr = Scr.Settings; tab = Mathf.Clamp(t, 0, T.Length - 1); rows = BuildRows(T[tab]); rowSel = 0; scroll = 0;
    }
    void ChangeTab(int d) { var T = MenuCfg.Get().tabs; if (T.Length > 0) OpenSettings(Wrap(tab + d, T.Length)); }

    // ---------------- rows ----------------
    static float Snap(float v, MenuCfg.RowC r)
    {
        float st = Mathf.Max(0.0001f, r.step); v = Mathf.Round((v - r.min) / st) * st + r.min; return Mathf.Clamp(v, r.min, r.max);
    }
    List<Row> BuildRows(MenuCfg.TabC t)
    {
        var C = MenuCfg.Get(); var St = C.strings; var list = new List<Row>();
        if (t.type == "controls") return BindRows(C);   // REBIND v100 (falls back to ControlRows while the key map is off)
        List<GameSettings.Row> gs = null;
        foreach (var rc in t.rows)
        {
            if (rc == null) continue; var r = rc;
            switch (r.type)
            {
                case "gs":
                    {
                        if (gs == null) gs = GameSettings.MenuRows();
                        GameSettings.Row g = null; foreach (var x in gs) if (x.Id == r.id) g = x;
                        if (g == null) break;   // an option this build does not have
                        list.Add(new Row { kind = "choice", label = string.IsNullOrEmpty(r.label) ? g.Label : r.label, hint = string.IsNullOrEmpty(r.hint) ? g.Hint : r.hint, val = () => g.ValueLabel, step = d => g.Cycle(d) });
                        break;
                    }
                case "resolution": list.Add(new Row { kind = "choice", label = Or(r.label, St.resolutionLabel), hint = Or(r.hint, St.resolutionHint), val = () => { var c = MenuSettings.CurrentResolution(); return c.x + " x " + c.y; }, step = d => MenuSettings.StepResolution(d) }); break;
                case "fullscreen": list.Add(new Row { kind = "choice", label = Or(r.label, St.fullscreenLabel), hint = Or(r.hint, St.fullscreenHint), val = () => MenuSettings.FullscreenLabel(), step = d => MenuSettings.StepFullscreen(d) }); break;
                case "vsync": list.Add(new Row { kind = "choice", label = Or(r.label, St.vsyncLabel), hint = Or(r.hint, St.vsyncHint), val = () => MenuSettings.VsyncLabel(), step = d => MenuSettings.StepVsync(d) }); break;
                case "framecap": list.Add(new Row { kind = "choice", label = Or(r.label, St.frameCapLabel), hint = Or(r.hint, St.frameCapHint), val = () => MenuSettings.FrameCapLabel(), step = d => MenuSettings.StepFrameCap(d) }); break;
                case "slider": list.Add(SliderRow(r)); break;   // RCHYGIENE v100: body moved to SliderRow
                case "mix":   // RCHYGIENE v100: one slider per audio_mix.json category with menu=true (label, hint, limits and display from that file)
                    {
                        var AM = AudioMix.Cfg(); var dp = AM.display;
                        foreach (var cat in AM.categories)
                            if (cat.menu) list.Add(SliderRow(new MenuCfg.RowC { type = "slider", key = cat.id, label = cat.label, hint = cat.hint, min = cat.min, max = cat.max, step = dp.step, scale = dp.scale, fmt = dp.fmt, suffix = dp.suffix }));
                        break;
                    }
                case "toggle":
                    {
                        string key = r.key;
                        list.Add(new Row { kind = "toggle", label = r.label, hint = r.hint, val = () => MenuSettings.GetB(key) ? St.on : St.off, step = d => MenuSettings.SetB(key, !MenuSettings.GetB(key)) });
                        break;
                    }
                case "reset": { string scope = r.key; list.Add(new Row { kind = "action", label = Or(r.label, St.resetLabel), hint = r.hint, act = () => MenuSettings.Reset(scope) }); break; }
                default: Debug.LogWarning("[MenuUI] unknown row type '" + r.type + "' in tab " + t.id); break;
            }
        }
        return list;
    }
    Row SliderRow(MenuCfg.RowC r)   // RCHYGIENE v100 (was inline in BuildRows)
    {
        string key = r.key;
        return new Row
        {
            kind = "slider", label = r.label, hint = r.hint,
            val = () => (MenuSettings.GetF(key) * r.scale).ToString(r.fmt, Inv) + r.suffix,
            frac = () => Mathf.InverseLerp(r.min, r.max, MenuSettings.GetF(key)),
            step = d => MenuSettings.SetF(key, Snap(MenuSettings.GetF(key) + d * r.step, r)),
            setFrac = f => MenuSettings.SetF(key, Snap(Mathf.Lerp(r.min, r.max, f), r))
        };
    }
    static string Or(string a, string b) { return string.IsNullOrEmpty(a) ? b : a; }

    // ---- the read-only key map: menu.json rows + the actions of controls.json (slide directions, scheme, dead zone) ----
    static string[] PadKeys(ControlsTable.Data C, string name)
    {
        switch (name)
        {
            case "pad.attackKeys": return C.pad.attackKeys; case "pad.guardKeys": return C.pad.guardKeys; case "pad.special1Keys": return C.pad.special1Keys; case "pad.special2Keys": return C.pad.special2Keys;
        }
        return new string[0];
    }
    static string PadName(MenuCfg.ControlsC K, string key)
    {
        const string pre = "JoystickButton"; int n;
        if (key != null && key.StartsWith(pre, StringComparison.Ordinal) && int.TryParse(key.Substring(pre.Length), out n) && n >= 0 && n < K.padNames.Length) return K.padNames[n];
        return key ?? "";
    }
    List<Row> ControlRows(MenuCfg.Data C)
    {
        var K = C.controls; var cd = ControlsTable.Get(); var list = new List<Row>();
        foreach (var kr in K.rows)
        {
            if (kr == null) continue; string pad = kr.pad;
            if (!string.IsNullOrEmpty(kr.padFrom))
            {
                var names = new List<string>();
                foreach (var src in kr.padFrom.Split(',')) foreach (var k in PadKeys(cd, src.Trim())) names.Add(PadName(K, k));
                if (names.Count > 0) pad = string.Join(" / ", names.ToArray());
            }
            list.Add(new Row { kind = "info", label = kr.action, c1 = kr.keys, c2 = pad });
        }
        if (K.fromControlsJson && cd.enabled)
        {
            foreach (var m in cd.attack.map)
            {
                if (m == null || string.IsNullOrEmpty(m.dir)) continue;
                string l = m.light != null && m.light.Length > 0 ? GameLoop.Pretty(m.light[0]) : "-", h = m.heavy != null && m.heavy.Length > 0 ? GameLoop.Pretty(m.heavy[0]) : "-";
                list.Add(new Row { kind = "info", label = string.Format(K.slideAction, GameLoop.Pretty(m.dir)), c1 = string.Format(K.slideKeys, l, h), c2 = K.slidePad });
            }
            list.Add(new Row { kind = "info", label = K.schemeAction, c1 = GameLoop.Pretty(cd.scheme), c2 = "" });
            list.Add(new Row { kind = "info", label = K.deadZoneAction, c1 = Mathf.Round(cd.mouse.deadZonePx).ToString("0", Inv) + " px", c2 = string.Format(K.padDeadZoneFmt, Mathf.Round(cd.pad.deadZone * 100f).ToString("0", Inv)) });
        }
        return list;
    }

    // ---------------- geometry (shared by drawing and the mouse) ----------------
    struct Geo { public Rect panel, title, tabsRow, list, hint, footer; public Rect[] tab; public float rowH, headH; public int visible; }
    Geo MakeGeo(float vw, float vh)
    {
        var L = MenuCfg.Get().layout; var T = MenuCfg.Get().tabs; var g = new Geo();
        float w = Mathf.Min(L.panelW, vw - 2f * L.panelMargin), h = Mathf.Min(L.panelH, vh - 2f * L.panelMargin);
        g.panel = new Rect((vw - w) * 0.5f, (vh - h) * 0.5f, w, h);
        float x0 = g.panel.x + L.panelPad, iw = w - 2f * L.panelPad, y = g.panel.y + L.panelPad * 0.7f;
        g.title = new Rect(x0, y, iw, L.panelTitleSize * 1.45f); y = g.title.yMax + 6f;
        g.tabsRow = new Rect(x0, y, iw, L.tabSize * 1.7f);
        g.footer = new Rect(x0, g.panel.yMax - L.panelPad * 0.6f - L.footerSize * 1.6f, iw, L.footerSize * 1.6f);
        float hintH = L.hintSize * 1.45f * Mathf.Max(1f, L.hintLines);
        g.hint = new Rect(x0, g.footer.y - L.footerGap - hintH, iw, hintH);
        bool controls = tab >= 0 && tab < T.Length && T[tab] != null && T[tab].type == "controls";
        g.rowH = controls ? L.controlsRowH : L.rowH; g.headH = controls ? g.rowH * 0.9f : 0f;   // the key map uses denser rows so more of it fits
        float ly = g.tabsRow.yMax + 14f + g.headH;
        g.list = new Rect(x0, ly, iw, Mathf.Max(g.rowH, g.hint.y - L.hintGap - ly)); g.visible = Mathf.Max(1, Mathf.FloorToInt(g.list.height / g.rowH));
        g.tab = new Rect[T.Length]; float tx = x0;
        for (int i = 0; i < T.Length; i++)
        {
            float tw = HudDraw.CapsWidth(T[i].label, "title", L.tabSize, L.tabSmall, L.tabTracking);
            g.tab[i] = new Rect(tx - 8f, g.tabsRow.y, tw + 16f, g.tabsRow.height); tx += tw + L.tabGap;
        }
        return g;
    }
    Rect PauseRect(int i, float vw, float vh) { var L = MenuCfg.Get().layout; return new Rect((vw - L.itemW) * 0.5f, vh * L.itemsY + i * L.itemGap, L.itemW, L.itemGap); }
    Rect[] ConfirmOpts(float vw, float vh)
    {
        var L = MenuCfg.Get().layout; float cx = vw * 0.5f, cy = vh * 0.5f; float ow = L.confirmW * 0.3f;
        return new[] { new Rect(cx - L.confirmW * 0.25f - ow * 0.5f, cy + L.confirmH * 0.1f, ow, L.itemGap * 0.8f), new Rect(cx + L.confirmW * 0.25f - ow * 0.5f, cy + L.confirmH * 0.1f, ow, L.itemGap * 0.8f) };
    }

    // ---------------- mouse ----------------
    void MouseEvent(Event ev, float vw, float vh)
    {
        var C = MenuCfg.Get(); if (!C.input.mouse) return;
        if (CaptureBusy) { if (ev.type == EventType.MouseDown) ev.Use(); return; }   // REBIND v100: the capture swallows the clicks
        if (ev.type == EventType.ScrollWheel && scr != Scr.Confirm) { int d = ev.delta.y > 0f ? 1 : -1; if (scr == Scr.Pause) pauseSel = Wrap(pauseSel + d, C.items.Length); else rowSel = Wrap(rowSel + d, rows.Count); ev.Use(); return; }
        if (ev.type != EventType.MouseDown || ev.button != 0) return;
        Vector2 p = ev.mousePosition;
        if (scr == Scr.Pause)
        {
            for (int i = 0; i < C.items.Length; i++) if (PauseRect(i, vw, vh).Contains(p)) { pauseSel = i; Activate(); ev.Use(); return; }
        }
        else if (scr == Scr.Confirm)
        {
            var o = ConfirmOpts(vw, vh); for (int i = 0; i < 2; i++) if (o[i].Contains(p)) { confSel = i; Activate(); ev.Use(); return; }
        }
        else
        {
            var g = MakeGeo(vw, vh);
            for (int i = 0; i < g.tab.Length; i++) if (g.tab[i].Contains(p)) { OpenSettings(i); ev.Use(); return; }
            for (int i = scroll; i < rows.Count && i < scroll + g.visible; i++)
            {
                var rr = new Rect(g.list.x, g.list.y + (i - scroll) * g.rowH, g.list.width, g.rowH);
                if (!rr.Contains(p)) continue;
                rowSel = i; var r = rows[i];
                if (r.kind == "slider" && r.setFrac != null)
                {
                    var br = SliderRect(rr, C.layout); var hit = new Rect(br.x - 6f, rr.y, br.width + 12f, rr.height);
                    if (hit.Contains(p)) r.setFrac(Mathf.Clamp01((p.x - br.x) / br.width));
                    else if (p.x < br.x && r.step != null) r.step(-1); else if (r.step != null && p.x > br.xMax) r.step(1);
                }
                else if (r.kind == "choice" || r.kind == "toggle")
                {
                    float vx = rr.xMax - C.layout.valueW - C.layout.rowPad;
                    if (p.x < vx) { /* label side: just select */ }
                    else if (r.step != null) r.step(p.x < vx + C.layout.valueW * 0.5f ? -1 : 1);
                }
                else if (r.bindId.Length > 0) BindClick(r, rr, p);   // REBIND v100
                else if (r.act != null) r.act();
                ev.Use(); return;
            }
        }
    }
    static Rect SliderRect(Rect rr, MenuCfg.LayoutC L) { return new Rect(rr.xMax - L.rowPad - L.valueW + L.arrowW, rr.center.y - L.sliderH * 0.5f, L.sliderW, L.sliderH); }

    // ---------------- drawing ----------------
    void OnGUI()
    {
        if (!Open) return;
        GUI.depth = -2000;
        var ev = Event.current; var C = MenuCfg.Get();
        float s = HudCfg.Scale(), vw = Screen.width / s, vh = Screen.height / s;
        var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        try
        {
            if (ev.type == EventType.Repaint)
            {
                HudDraw.Alpha = Mathf.Clamp01((Time.unscaledTime - openedAt) / Mathf.Max(0.01f, C.layout.fadeInS));
                DrawBackdrop(vw, vh);
                if (scr == Scr.Pause) DrawPause(vw, vh);
                else if (scr == Scr.Settings) DrawSettings(vw, vh);
                else DrawConfirm(vw, vh);
            }
            else MouseEvent(ev, vw, vh);
            // swallow keys / mouse so nothing underneath reacts to the menu's clicks
            if (ev.type == EventType.KeyDown || ev.type == EventType.KeyUp) ev.Use();
        }
        finally { HudDraw.Alpha = 1f; GUI.matrix = m; }
    }

    static Color Ink(float a = 1f) { return HudCfg.Col(HudCfg.Get().colors.ink, a); }
    static Color Gold(float a = 1f) { return HudCfg.Col(HudCfg.Get().colors.gold, a); }
    static Color Dim() { return HudCfg.Col(HudCfg.Get().colors.dim); }

    void DrawBackdrop(float vw, float vh)
    {
        var L = MenuCfg.Get().layout; var d = L.dimColor;
        HudDraw.Fill(new Rect(0f, 0f, vw, vh), new Color(d[0], d[1], d[2], L.dimAlpha));
        var cr = HudCfg.Get().colors.crimson; HudDraw.Tex(new Rect(0f, 0f, vw, vh), HudDraw.Vignette(), new Color(cr[0], cr[1], cr[2], L.vignetteAlpha));
    }
    void Rule(float cx, float y, float w)
    {
        var L = MenuCfg.Get().layout; var fr = HudCfg.Col(HudCfg.Get().colors.frame);
        HudDraw.Fill(new Rect(cx - w * 0.5f, y, w, L.frameLine), fr); float d = L.diamond + 2f; HudDraw.Tex(new Rect(cx - d * 0.5f, y + L.frameLine * 0.5f - d * 0.5f, d, d), HudDraw.Diamond(), fr);
    }
    void Footer(Rect r, string text)
    {
        var L = MenuCfg.Get().layout; HudDraw.Text(r, text, "serif", L.footerSize, TextAnchor.MiddleCenter, false, new Color(Dim().r, Dim().g, Dim().b, Dim().a * 0.9f));
    }

    void DrawPause(float vw, float vh)
    {
        var C = MenuCfg.Get(); var L = C.layout; float cx = vw * 0.5f;
        float ty = vh * L.titleY;
        HudDraw.Caps(cx, ty, 0.5f, C.strings.pausedTitle, "title", L.titleSize, L.titleSmall, L.titleTracking, Ink());
        Rule(cx, ty + L.titleSize * 1.45f + 14f, L.ruleW);
        var hover = mouseActive && C.input.mouse ? Event.current.mousePosition : new Vector2(-1f, -1f);
        for (int i = 0; i < C.items.Length; i++)
        {
            var r = PauseRect(i, vw, vh);
            if (mouseActive && r.Contains(hover)) pauseSel = i;
            bool sel = i == pauseSel; string lab = C.items[i].label;
            float w = HudDraw.CapsWidth(lab, "title", L.itemSize, L.itemSmall, L.itemTracking);
            if (sel)
            {
                HudDraw.Tex(r, HudDraw.BandV(), Gold(L.selectAlpha));
                float d = L.diamond + 3f, gx = w * 0.5f + 30f;
                HudDraw.Tex(new Rect(cx - gx - d, r.center.y - d * 0.5f, d, d), HudDraw.Diamond(), Gold()); HudDraw.Tex(new Rect(cx + gx, r.center.y - d * 0.5f, d, d), HudDraw.Diamond(), Gold());
            }
            HudDraw.Caps(cx, r.y + (r.height - L.itemSize * 1.45f) * 0.5f, 0.5f, lab, "title", L.itemSize, L.itemSmall, L.itemTracking, sel ? Gold() : Ink(0.72f));
        }
        Footer(new Rect(0f, vh - 70f, vw, 30f), C.strings.footerPause);
        if (CrashLog.ShowNotice) HudDraw.Text(new Rect(0f, vh - 112f, vw, 30f), C.strings.crashedNotice, "serif", L.footerSize + 2, TextAnchor.MiddleCenter, false, Gold(0.85f));   // RCHYGIENE v100
    }

    void DrawConfirm(float vw, float vh)
    {
        var C = MenuCfg.Get(); var L = C.layout; float cx = vw * 0.5f, cy = vh * 0.5f;
        var box = new Rect(cx - L.confirmW * 0.5f, cy - L.confirmH * 0.5f, L.confirmW, L.confirmH);
        Frame(box);
        HudDraw.Caps(cx, box.y + L.confirmH * 0.18f, 0.5f, confText, "title", L.confirmSize, L.confirmSmall, L.itemTracking, Ink());
        Rule(cx, box.y + L.confirmH * 0.18f + L.confirmSize * 1.45f + 12f, L.ruleW * 0.6f);
        var o = ConfirmOpts(vw, vh); string[] labs = { C.strings.no, C.strings.yes };
        var hover = mouseActive && C.input.mouse ? Event.current.mousePosition : new Vector2(-1f, -1f);
        for (int i = 0; i < 2; i++)
        {
            if (mouseActive && o[i].Contains(hover)) confSel = i;
            bool sel = i == confSel;
            if (sel) HudDraw.Tex(o[i], HudDraw.BandV(), Gold(L.selectAlpha));
            HudDraw.Caps(o[i].center.x, o[i].y + (o[i].height - L.itemSize * 1.45f) * 0.5f, 0.5f, labs[i], "title", L.itemSize, L.itemSmall, L.itemTracking, sel ? Gold() : Ink(0.72f));
        }
        Footer(new Rect(box.x, box.yMax - 40f, box.width, 24f), C.strings.footerConfirm);
    }

    // panel + double gold frame + corner diamonds
    void Frame(Rect p)
    {
        var L = MenuCfg.Get().layout; var H = HudCfg.Get(); var pc = L.panelColor;
        HudDraw.Fill(p, new Color(pc[0], pc[1], pc[2], pc[3]));
        var fr = HudCfg.Col(H.colors.frame); var fd = HudCfg.Col(H.colors.frameDim); float t = L.frameLine, q = L.framePad + t;
        Outline(p, t, fr); Outline(new Rect(p.x + q, p.y + q, p.width - 2f * q, p.height - 2f * q), t * 0.7f, fd);
        float d = L.diamond + 3f;
        foreach (var c in new[] { new Vector2(p.x, p.y), new Vector2(p.xMax, p.y), new Vector2(p.x, p.yMax), new Vector2(p.xMax, p.yMax) }) HudDraw.Tex(new Rect(c.x - d * 0.5f, c.y - d * 0.5f, d, d), HudDraw.Diamond(), fr);
    }
    static void Outline(Rect r, float t, Color c)
    {
        HudDraw.Fill(new Rect(r.x, r.y, r.width, t), c); HudDraw.Fill(new Rect(r.x, r.yMax - t, r.width, t), c); HudDraw.Fill(new Rect(r.x, r.y, t, r.height), c); HudDraw.Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
    }

    GUIStyle WrapStyle(int size)
    {
        GUIStyle s; if (wrapStyles.TryGetValue(size, out s) && s != null) return s;
        s = new GUIStyle(HudDraw.Style("serif", size, TextAnchor.UpperLeft, false, true)) { wordWrap = true, clipping = TextClipping.Clip }; wrapStyles[size] = s; return s;
    }

    void DrawSettings(float vw, float vh)
    {
        var C = MenuCfg.Get(); var L = C.layout; var T = C.tabs; var H = HudCfg.Get(); var g = MakeGeo(vw, vh);
        Frame(g.panel);
        HudDraw.Caps(g.title.x, g.title.y, 0f, C.strings.settingsTitle, "title", L.panelTitleSize, L.panelTitleSmall, L.panelTitleTracking, Ink());
        var hover = mouseActive && C.input.mouse ? Event.current.mousePosition : new Vector2(-1f, -1f);
        // tabs
        for (int i = 0; i < T.Length; i++)
        {
            bool cur = i == tab, hv = mouseActive && g.tab[i].Contains(hover);
            Color col = cur ? Gold() : hv ? Ink() : Ink(0.6f);
            HudDraw.Caps(g.tab[i].x + 8f, g.tab[i].y + 2f, 0f, T[i].label, "title", L.tabSize, L.tabSmall, L.tabTracking, col);
            if (cur) HudDraw.Fill(new Rect(g.tab[i].x, g.tab[i].yMax - 3f, g.tab[i].width, 2f), Gold());
        }
        HudDraw.Text(new Rect(g.tabsRow.x, g.tabsRow.y, g.tabsRow.width, g.tabsRow.height - 4f), C.strings.tabPrevHint + "   |   " + C.strings.tabNextHint, "serif", L.keySize, TextAnchor.MiddleRight, false, Dim());
        HudDraw.Fill(new Rect(g.tabsRow.x, g.tabsRow.yMax, g.tabsRow.width, 1f), HudCfg.Col(H.colors.frameDim));

        bool controls = T.Length > 0 && T[Mathf.Clamp(tab, 0, T.Length - 1)].type == "controls";
        if (controls)
        {
            var hr = new Rect(g.list.x, g.list.y - g.headH, g.list.width, g.headH);
            HudDraw.Text(new Rect(hr.x + L.rowPad, hr.y, 300f, hr.height), C.controls.colAction, "serif", L.keySize + 2, TextAnchor.MiddleLeft, false, Gold(0.8f), true);
            HudDraw.Text(new Rect(hr.x + hr.width * L.colKeys, hr.y, 400f, hr.height), C.controls.colKeys, "serif", L.keySize + 2, TextAnchor.MiddleLeft, false, Gold(0.8f), true);
            HudDraw.Text(new Rect(hr.x + hr.width * L.colPad, hr.y, 300f, hr.height), C.controls.colPad, "serif", L.keySize + 2, TextAnchor.MiddleLeft, false, Gold(0.8f), true);
        }

        // rows
        int n = rows.Count; rowSel = Mathf.Clamp(rowSel, 0, Mathf.Max(0, n - 1));
        scroll = Mathf.Clamp(scroll, rowSel - g.visible + 1, rowSel); scroll = Mathf.Clamp(scroll, 0, Mathf.Max(0, n - g.visible));
        float listW = g.list.width - (n > g.visible ? L.scrollW + 8f : 0f);
        for (int i = scroll; i < n && i < scroll + g.visible; i++)
        {
            var rr = new Rect(g.list.x, g.list.y + (i - scroll) * g.rowH, listW, g.rowH);
            if (mouseActive && new Rect(rr.x, rr.y, g.list.width, rr.height).Contains(hover)) rowSel = i;
            DrawRow(rows[i], rr, i == rowSel, controls, L, H);
        }
        if (n > g.visible)
        {
            var track = new Rect(g.list.xMax - L.scrollW, g.list.y, L.scrollW, g.visible * g.rowH);
            HudDraw.Fill(track, HudCfg.Col(H.colors.frameDim, 0.5f));
            float th = Mathf.Max(24f, track.height * g.visible / n), ty = track.y + (track.height - th) * (n - g.visible <= 0 ? 0f : scroll / (float)(n - g.visible));
            HudDraw.Fill(new Rect(track.x, ty, track.width, th), Gold(0.8f));
        }
        if (n == 0) HudDraw.Text(new Rect(g.list.x, g.list.y, g.list.width, g.rowH), "-", "serif", L.labelSize, TextAnchor.MiddleLeft, false, Dim());
        // hint of the selected row
        if (rowSel < n && !string.IsNullOrEmpty(rows[rowSel].hint))
        {
            var st = WrapStyle(L.hintSize); var dim = Dim(); var sh = HudCfg.Col(H.colors.shadow);
            st.normal.textColor = new Color(sh.r, sh.g, sh.b, sh.a * HudDraw.Alpha * dim.a); GUI.Label(new Rect(g.hint.x + 1f, g.hint.y + 1f, g.hint.width, g.hint.height), rows[rowSel].hint, st);
            st.normal.textColor = new Color(dim.r, dim.g, dim.b, dim.a * HudDraw.Alpha); GUI.Label(g.hint, rows[rowSel].hint, st);
        }
        Footer(g.footer, controls ? ControlsFooter(C) : C.strings.footerSettings);
        DrawCapture(vw, vh);   // REBIND v100: the "press a key" box
    }

    void DrawRow(Row r, Rect rr, bool sel, bool controls, MenuCfg.LayoutC L, HudCfg.Data H)
    {
        if (sel)
        {
            HudDraw.Tex(rr, HudDraw.GradH(), Gold(L.selectAlpha));
            float d = L.diamond; HudDraw.Tex(new Rect(rr.x + 4f, rr.center.y - d * 0.5f, d, d), HudDraw.Diamond(), Gold());
        }
        float lx = rr.x + L.rowPad + (sel ? L.diamond * 0.6f : 0f);
        Color lab = sel ? Ink() : Ink(0.74f), val = sel ? Gold() : Ink(0.82f);
        if (controls)
        {
            if (r.bindId.Length > 0) { DrawBindRow(r, rr, sel, lx, lab, val, L); return; }   // REBIND v100
            HudDraw.Text(new Rect(lx, rr.y, rr.width * 0.34f, rr.height), r.label, "serif", L.labelSize - 2, TextAnchor.MiddleLeft, false, lab);
            HudDraw.Text(new Rect(rr.x + rr.width * L.colKeys, rr.y, rr.width * (L.colPad - L.colKeys) - 12f, rr.height), r.c1, "serif", L.valueSize - 2, TextAnchor.MiddleLeft, false, val);
            HudDraw.Text(new Rect(rr.x + rr.width * L.colPad, rr.y, rr.width * (1f - L.colPad) - 8f, rr.height), r.c2, "serif", L.valueSize - 2, TextAnchor.MiddleLeft, false, val);
            return;
        }
        HudDraw.Text(new Rect(lx, rr.y, rr.width - L.valueW - L.rowPad * 2f, rr.height), r.label, "serif", L.labelSize, TextAnchor.MiddleLeft, false, r.kind == "action" ? (sel ? Gold() : Ink(0.74f)) : lab);
        float vx = rr.xMax - L.valueW - L.rowPad;
        if (r.kind == "slider")
        {
            var br = SliderRect(rr, L); var arrows = sel ? Gold() : Dim(); var S = MenuCfg.Get().strings;
            HudDraw.Text(new Rect(vx, rr.y, L.arrowW, rr.height), S.arrowL, "serif", L.valueSize, TextAnchor.MiddleCenter, true, arrows);
            float f = r.frac != null ? Mathf.Clamp01(r.frac()) : 0f; float ts = H.bars.texScale;
            var shape = HudDraw.BarShape(br.width, br.height, L.sliderTaper, 0f);
            HudDraw.Tex(br, shape, HudCfg.Col(H.colors.hpBack));
            if (f > 0.001f) HudDraw.TexUV(new Rect(br.x, br.y, br.width * f, br.height), shape, new Rect(0f, 0f, f, 1f), sel ? Gold() : Gold(0.7f));
            float p = L.framePad; HudDraw.Tex(new Rect(br.x - p, br.y - p, br.width + 2f * p, br.height + 2f * p), HudDraw.BarShape(br.width + 2f * p, br.height + 2f * p, L.sliderTaper + p, H.bars.frameLine * ts), HudCfg.Col(H.colors.frameDim));
            HudDraw.Text(new Rect(br.xMax + 8f, rr.y, L.sliderValueW, rr.height), r.val != null ? r.val() : "", "serif", L.valueSize, TextAnchor.MiddleLeft, false, val);
            HudDraw.Text(new Rect(rr.xMax - L.rowPad - L.arrowW, rr.y, L.arrowW, rr.height), S.arrowR, "serif", L.valueSize, TextAnchor.MiddleCenter, true, arrows);
        }
        else if (r.kind == "choice" || r.kind == "toggle")
        {
            var S = MenuCfg.Get().strings; var arrows = sel ? Gold() : Dim();
            HudDraw.Text(new Rect(vx, rr.y, L.arrowW, rr.height), S.arrowL, "serif", L.valueSize, TextAnchor.MiddleCenter, true, arrows);
            HudDraw.Text(new Rect(vx + L.arrowW, rr.y, L.valueW - 2f * L.arrowW, rr.height), r.val != null ? r.val() : "", "serif", L.valueSize, TextAnchor.MiddleCenter, false, val);
            HudDraw.Text(new Rect(rr.xMax - L.rowPad - L.arrowW, rr.y, L.arrowW, rr.height), S.arrowR, "serif", L.valueSize, TextAnchor.MiddleCenter, true, arrows);
        }
    }
}
