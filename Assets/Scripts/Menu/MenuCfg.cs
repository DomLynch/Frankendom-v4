using System;
using UnityEngine;

// MENU v100 (lane laneMENU): data for the pause + settings menu. Assets/Data/menu.json (StreamingAssets first, read through HudCfg.ReadData). Every field has a default,
// so a missing file keeps a working (English) menu; "enabled": false removes the menu completely. Colours of the gothic style come from hud.json (HudCfg.Get().colors); this file only holds
// menu-specific layout numbers, strings, key names, the tab / row lists and the defaults of the saved settings.
public static class MenuCfg
{
    [Serializable] public class InputC
    {
        // KeyCode names. openKeys opens the pause menu; backKeys = one level back (on the pause list = resume); closeKeys = resume from anywhere (pad Start).
        public string[] openKeys = { "Escape", "JoystickButton7" }, closeKeys = { "JoystickButton7" }, backKeys = { "Escape", "JoystickButton1" }, confirmKeys = { "Return", "KeypadEnter", "JoystickButton0" };
        public string[] upKeys = { "UpArrow", "W" }, downKeys = { "DownArrow", "S" }, leftKeys = { "LeftArrow", "A" }, rightKeys = { "RightArrow", "D" }, tabPrevKeys = { "Q", "JoystickButton4" }, tabNextKeys = { "E", "JoystickButton5" };
        public string axisX = "Horizontal", axisY = "Vertical";   // Input Manager axes (left stick); empty = no stick navigation
        public string dpadAxisX = "", dpadAxisY = "";   // F5 DPADNAV: Input Manager axes of the D-pad (DPadX / DPadY in InputManager.asset); empty = the D-pad does not navigate
        public float axisThreshold = 0.6f, repeatDelayS = 0.4f, repeatS = 0.11f, mouseMovePx = 3f; public bool mouse = true;
    }
    [Serializable] public class RowC
    {
        // type: gs (a GameSettings.MenuRows entry, id), resolution, fullscreen, vsync, framecap, slider (key = an audio_mix.json category id | camSens | deadZone, min / max / step, scale, fmt, suffix), mix (one slider per audio_mix.json category with menu=true), toggle (key hints), reset (scope audio|gameplay|display)
        public string type = "", key = "", id = "", label = "", hint = "", fmt = "0", suffix = ""; public float min = 0f, max = 1f, step = 0.05f, scale = 1f;
    }
    [Serializable] public class TabC { public string id = "", label = "", type = "list"; public RowC[] rows = new RowC[0]; }
    [Serializable] public class ItemC { public string id = "", label = "", confirm = ""; }
    [Serializable] public class ChoiceC { public int value = 0; public string id = "", label = ""; }
    [Serializable] public class KeyRowC { public string action = "", keys = "", pad = "", padFrom = ""; }
    [Serializable] public class ControlsC
    {
        public bool fromControlsJson = true;   // append the slide / scheme / dead-zone rows read live from controls.json
        public string[] padNames = { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "L3", "R3" };   // JoystickButton0..9
        public string slideAction = "Slide {0} + attack", slideKeys = "Light: {0}   Heavy: {1}", slidePad = "Right stick + RB", schemeAction = "Control scheme", deadZoneAction = "Swipe dead zone", padDeadZoneFmt = "stick {0}%";
        public string colAction = "Action", colKeys = "Keyboard / mouse", colPad = "Gamepad";
        public KeyRowC[] rows = new KeyRowC[0];
    }
    [Serializable]
    public class BootC
    {
        // F5 BOOTMENU: enabled false = nothing changes. title / resumeLabel / footer replace the PAUSED title, the "Resume" label and the footer line on that first open only.
        // afterCardS = wait after the loading card and the StartupWarm cover are gone; maxWaitS = give up (never opens late). skipArgs = proof / capture flags that must never meet a title.
        public bool enabled = false; public string title = "", resumeLabel = "", footer = ""; public float afterCardS = 0.3f, maxWaitS = 60f;
        public string[] skipArgs = { "-camscene", "-camtrace", "-dirproof", "-zoneshot", "-routebot", "-lightdump", "-lightoff", "-resgate", "-record", "-flickrec" };
    }
    [Serializable] public class DefaultsC { public float camSens = 1f;   /* RCHYGIENE v100: audio defaults live in audio_mix.json */ public int vsync = 1, frameCap = 0; public bool hints = true; }
    [Serializable] public class StringsC
    {
        public string pausedTitle = "PAUSED", settingsTitle = "SETTINGS", on = "On", off = "Off", unlimited = "Unlimited", arrowL = "<", arrowR = ">", yes = "Yes", no = "No";
        public string footerPause = "Up / Down  Select      Enter / A  Confirm      Esc / Start  Resume", footerSettings = "Up / Down  Select      Left / Right  Change      Q / E  Tab      Esc / B  Back", footerControls = "Up / Down  Scroll      Q / E  Tab      Esc / B  Back", footerConfirm = "Left / Right  Choose      Enter / A  Confirm      Esc / B  Cancel";
        public string tabPrevHint = "Q", tabNextHint = "E", resetLabel = "Restore defaults", resolutionLabel = "Resolution", fullscreenLabel = "Window mode", vsyncLabel = "Vertical sync", frameCapLabel = "Frame cap";
        public string crashedNotice = "The last session ended unexpectedly. A log was saved.";   // RCHYGIENE v100: shown once on the first pause after a crash (CrashLog.ShowNotice)
        public string resolutionHint = "The size of the game window. Applied at once.", fullscreenHint = "Exclusive fullscreen is the fastest; borderless window switches to other programs more easily.", vsyncHint = "Locks the frame rate to the screen's refresh rate. Turn it off to use the frame cap.", frameCapHint = "Only used while vertical sync is off.";
    }
    [Serializable] public class LayoutC
    {
        public float dimAlpha = 0.74f, vignetteAlpha = 0.55f, fadeInS = 0.12f;
        public int titleSize = 64, titleSmall = 46, itemSize = 30, itemSmall = 22, tabSize = 22, tabSmall = 17, labelSize = 22, valueSize = 22, hintSize = 17, footerSize = 14, panelTitleSize = 40, panelTitleSmall = 30, keySize = 13, confirmSize = 30, confirmSmall = 22;
        public float titleTracking = 14f, itemTracking = 5f, tabTracking = 3.5f, panelTitleTracking = 6f, titleY = 0.17f, ruleW = 420f, itemsY = 0.36f, itemGap = 64f, itemW = 560f;
        public float panelW = 1040f, panelH = 760f, colKeys = 0.34f, colPad = 0.72f, panelPad = 40f, panelMargin = 40f, tabGap = 46f, rowH = 46f, controlsRowH = 32f, rowPad = 18f, valueW = 420f, sliderW = 250f, sliderH = 8f, sliderTaper = 8f, sliderValueW = 90f, arrowW = 28f, scrollW = 5f, hintGap = 10f, hintLines = 2f, footerGap = 16f, selectAlpha = 0.22f, diamond = 9f, frameLine = 1.4f, framePad = 3f, confirmW = 640f, confirmH = 240f;
        public float[] panelColor = { 0.035f, 0.032f, 0.04f, 0.93f }, dimColor = { 0.01f, 0.008f, 0.012f, 1f };
    }
    [Serializable]
    public class Data
    {
        public bool enabled = true, allowOnEndScreen = true; public float maxFadeToOpen = 0.5f, unfreezeMaxS = 0.5f, saveDelayS = 0.6f, openWaitS = 0.8f;   // openWaitS: an Esc press that cannot open yet (slow-mo dip, fade) is remembered this long
        public string[] harnessArgs = { "-balanceauto", "-autoplay", "-finisherauto", "-finisherrotonly", "-postureproof", "-camproof", "-flowproof", "-verifyGround", "-looptest", "-latencyprobe", "-perfbench", "-steerauto", "-steerpreview", "-gaps", "-props", "-record", "-sheet:", "-headsheet", "-cmp", "-stand", "-isolate", "-facecam" };
        public BootC boot = new BootC(); public InputC input = new InputC(); public LayoutC layout = new LayoutC(); public StringsC strings = new StringsC(); public DefaultsC defaults = new DefaultsC(); public ControlsC controls = new ControlsC();
        public ItemC[] items = Items(); public TabC[] tabs = new TabC[0];
        public ChoiceC[] vsyncModes = { new ChoiceC { value = 0, label = "Off" }, new ChoiceC { value = 1, label = "On" } };
        public ChoiceC[] fullscreenModes = { new ChoiceC { id = "FullScreenWindow", label = "Borderless window" }, new ChoiceC { id = "ExclusiveFullScreen", label = "Exclusive fullscreen" }, new ChoiceC { id = "Windowed", label = "Windowed" } };
        public int[] frameCaps = { 0, 60, 120, 144, 240 };   // 0 = unlimited
    }
    static ItemC[] Items()
    {
        return new[] { new ItemC { id = "resume", label = "Resume" }, new ItemC { id = "settings", label = "Settings" }, new ItemC { id = "controls", label = "Controls" },
                       new ItemC { id = "restart", label = "Restart fight", confirm = "Restart this fight?" }, new ItemC { id = "quit", label = "Quit to desktop", confirm = "Quit to desktop?" } };
    }

    static Data data; static bool loaded; static int harness = -1;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; loaded = false; harness = -1; }

    public static Data Get()
    {
        if (loaded && data != null) return data;
        loaded = true; data = new Data();
        string txt = HudCfg.ReadData("menu.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) { data = d; Fix(data); } }
            catch (Exception e) { Debug.LogWarning("[MenuCfg] menu.json parse failed, defaults kept: " + e.Message); data = new Data(); }
        }
        return data;
    }
    public static void Reload() { loaded = false; }

    static void Fix(Data d)
    {
        if (d.boot == null) d.boot = new BootC(); if (d.boot.skipArgs == null) d.boot.skipArgs = new string[0]; if (d.input == null) d.input = new InputC(); if (d.layout == null) d.layout = new LayoutC(); if (d.strings == null) d.strings = new StringsC(); if (d.defaults == null) d.defaults = new DefaultsC(); if (d.controls == null) d.controls = new ControlsC();
        if (d.items == null || d.items.Length == 0) d.items = Items(); if (d.tabs == null) d.tabs = new TabC[0]; if (d.harnessArgs == null) d.harnessArgs = new string[0];
        if (d.vsyncModes == null || d.vsyncModes.Length == 0) d.vsyncModes = new Data().vsyncModes; if (d.fullscreenModes == null || d.fullscreenModes.Length == 0) d.fullscreenModes = new Data().fullscreenModes; if (d.frameCaps == null || d.frameCaps.Length == 0) d.frameCaps = new Data().frameCaps;
        if (d.controls.rows == null) d.controls.rows = new KeyRowC[0]; if (d.controls.padNames == null) d.controls.padNames = new ControlsC().padNames;
        foreach (var t in d.tabs) if (t != null && t.rows == null) t.rows = new RowC[0];
    }

    /// <summary>True when a proof / capture harness drives the game: the menu stays off and the saved settings are not applied (gates stay deterministic).</summary>
    public static bool Harness
    {
        get
        {
            if (harness >= 0) return harness == 1;
            bool h = Application.isBatchMode || GameLoop.HarnessOn;
            if (!h)
            {
                var args = Environment.GetCommandLineArgs(); var list = Get().harnessArgs;
                for (int i = 0; i < args.Length && !h; i++)
                    for (int k = 0; k < list.Length; k++)
                    {
                        string p = list[k]; if (string.IsNullOrEmpty(p)) continue;
                        if (p[p.Length - 1] == ':' ? args[i].StartsWith(p, StringComparison.Ordinal) : args[i] == p) { h = true; break; }
                    }
            }
            harness = h ? 1 : 0; return h;
        }
    }

    public static RowC FindRow(string key)
    {
        foreach (var t in Get().tabs) if (t != null) foreach (var r in t.rows) if (r != null && r.key == key && r.type == "slider") return r;
        return null;
    }

    public static KeyCode[] Keys(string[] names, KeyCode[] fallback) { return HudCfg.Keys(names, fallback); }
}
