#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// RESGATE v1: resolution gate capture, DARK. Runs ONLY with the command-line flag -resgate AND "enabled": true in resgate.json (shipped false; the runner passes a copy with true via -resgatedata:<json>).
//   HadesSlice.exe -resgate -resgatesize:<id> [-resgateoff] [-resgateout:<dir>] [-resgatedata:<json>] [-resgatestates:a,b] [-resgatetag:<t>] [-lootdemo] -screen-width W -screen-height H -screen-fullscreen 0 -popupwindow
// Why: the game must work at 1920x1080, 2560x1440, 2560x1600, 3440x1440 and 3840x2160, but a window on the 2560x1600 laptop display clamps (COORDINATION 11:47), so 3440x1440 and 3840x2160 cannot be opened at size.
//   HUD (IMGUI) layout is a pure function of (vw, vh, s) = (Screen.width / s, Screen.height / s, Screen.height / 1080), so an ASPECT-EQUIVALENT proxy window gives the identical virtual layout:
//   3440x1440 -> 2560x1072 (vw 2578 vs 2580), 3840x2160 -> 1920x1080 (vw 1920, s 1 instead of 2). resgate.json sizes[].proxy names that window; the runner opens it, this class records both names.
//   IMGUI is NOT drawn into a camera RenderTexture, so the offscreen pass (-resgateoff, once per run) renders the 3D scene only, at the true size of every sizes[] row (camera.targetTexture): framing, FOV, aspect.
// Per window state (fight, hints, lock, guard, victory, defeat, menu_pause, menu_settings, loot_labels, loot_screen) two PNGs: shot_<size>_<state>.png (the real frame) and hud_<size>_<state>.png (the same frame with the 3D
//   layer culled to a flat black background = HUD ink only, for resgate_check.py), plus probe_<size>_<state>.json (every HudDraw/LootDraw text and shape rect in screen px, from ResGateProbe).
// The sim is frozen (Duel.Frozen) after the fight is ready; nothing on disk is changed except <out>. Exit codes: 0 done, 2 setup failure / exception, 3 timeout. Every size, state and wait is resgate.json (StreamingAssets copy).
public static class ResGateProbe
{
    public static bool On;
    public struct Item { public string kind, text, anchor; public float x, y, w, h, needW; public int size; }
    public static readonly List<Item> Items = new List<Item>();

    static void Rect2(Rect r, out float x0, out float y0, out float x1, out float y1)
    {
        var m = GUI.matrix;   // all four corners: the guard arcs draw under a rotation matrix
        Vector3 a = m.MultiplyPoint3x4(new Vector3(r.x, r.y, 0f)), b = m.MultiplyPoint3x4(new Vector3(r.xMax, r.y, 0f)), c = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0f)), d = m.MultiplyPoint3x4(new Vector3(r.x, r.yMax, 0f));
        x0 = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)); y0 = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)); x1 = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x)); y1 = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
    }
    // a single label: rect in the caller's (scaled) GUI space -> screen px; needW = the text's own width (CalcSize) in screen px
    public static void Text(Rect r, string s, string kind, int size, TextAnchor a, bool bold, bool wrap = false)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint || string.IsNullOrEmpty(s)) return;
        float x0, y0, x1, y1; Rect2(r, out x0, out y0, out x1, out y1);
        float k = GUI.matrix.m00; float need = HudDraw.Style(kind, size, TextAnchor.UpperLeft, bold, false).CalcSize(new GUIContent(s)).x * k;
        if (wrap) need = Mathf.Min(need, x1 - x0);   // a wrapped paragraph breaks inside its box
        Items.Add(new Item { kind = "text", text = s, anchor = a.ToString(), x = x0, y = y0, w = x1 - x0, h = y1 - y0, needW = need, size = size });
    }
    // a tracked small-caps line (HudDraw.Caps): x = left edge of the line in GUI space, width = its laid-out width
    public static void Caps(float x, float y, float width, float height, string s, int size)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint || string.IsNullOrEmpty(s)) return;
        float x0, y0, x1, y1; Rect2(new Rect(x, y, width, height), out x0, out y0, out x1, out y1);
        Items.Add(new Item { kind = "caps", text = s, anchor = "UpperLeft", x = x0, y = y0, w = x1 - x0, h = y1 - y0, needW = x1 - x0, size = size });
    }
    // a texture shape (bars, discs, glyphs, arcs); big layers (vignette, dim) are skipped
    public static void Box(Rect r)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        float x0, y0, x1, y1; Rect2(r, out x0, out y0, out x1, out y1);
        if ((x1 - x0) * (y1 - y0) >= 0.25f * Screen.width * Screen.height) return;
        Items.Add(new Item { kind = "tex", text = "", anchor = "", x = x0, y = y0, w = x1 - x0, h = y1 - y0, needW = x1 - x0, size = 0 });
    }
}

[DefaultExecutionOrder(-57)]
public class ResGate : MonoBehaviour
{
    [Serializable] public class Proxy { public int w, h; }
    [Serializable] public class SizeRow { public string id = ""; public int w, h; public float steamPct; public string note = ""; public Proxy proxy; }
    [Serializable] public class StateRow { public string id = ""; public string needsArg = ""; }
    [Serializable] public class OffRow { public bool enabled = true; public int settleFrames = 90; public bool keepPng = true; public string[] sizeIds = new string[0]; }
    [Serializable]
    public class Cfg
    {
        public bool enabled = false, requireWindowSize = true, muteAudio = true, hudOnly = true;
        public float bootWaitSec = 60f, timeoutSec = 900f, exitGraceSec = 2f, stateSeconds = 1.0f;
        public int settleFrames = 90, stateFrames = 8, hudFrames = 4;
        public SizeRow[] sizes = new SizeRow[0];
        public StateRow[] states = new StateRow[0];
        public OffRow offscreen = new OffRow();
    }

    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static string F(float v) { return v.ToString("F3", IC); }
    static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-resgate")) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<ResGate>() != null) return;
            var go = new GameObject("ResGate"); DontDestroyOnLoad(go); go.AddComponent<ResGate>();
        }
        catch (Exception e) { Debug.LogError("[resgate] boot failed: " + e); }
    }

    Cfg C = new Cfg(); string dir, logPath, sizeId = "win", fileTag = ""; float t0; bool exiting; List<string> only;
    readonly List<string> rows = new List<string>(), offRows = new List<string>(), fails = new List<string>();
    Camera cam; CameraRig rig;

    void Log(string s) { string line = Time.realtimeSinceStartup.ToString("F1", IC) + " " + s; Debug.Log("[resgate] " + s); try { if (logPath != null) File.AppendAllText(logPath, line + "\n"); } catch { } }

    void Start() { t0 = Time.realtimeSinceStartup; StartCoroutine(Run()); }

    void Update()
    {
        if (!exiting && C.enabled && Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT"); Finish(3); }
    }

    bool LoadCfg()
    {
        string src = Arg("-resgatedata:"); var cands = new List<string>(); if (!string.IsNullOrEmpty(src)) cands.Add(src);
        cands.Add(Path.Combine(Application.streamingAssetsPath, "resgate.json")); cands.Add(Path.Combine(Application.dataPath, "Data/resgate.json"));
        foreach (var p in cands)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (c == null) continue;
                C = c; if (C.sizes == null) C.sizes = new SizeRow[0]; if (C.states == null) C.states = new StateRow[0]; if (C.offscreen == null) C.offscreen = new OffRow(); return true;
            }
            catch (Exception e) { Debug.LogWarning("[resgate] " + p + ": " + e.Message); }
        }
        return false;
    }

    IEnumerator Run()
    {
        yield return null;
        if (!LoadCfg() || !C.enabled) { Debug.Log("[resgate] -resgate given but resgate.json is missing or enabled=false: dark, nothing done."); Destroy(gameObject); yield break; }
        dir = Arg("-resgateout:"); if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "resgate");
        Directory.CreateDirectory(dir); logPath = Path.Combine(dir, "resgate_log.txt");
        sizeId = Arg("-resgatesize:") ?? (Screen.width + "x" + Screen.height); fileTag = Arg("-resgatetag:") ?? "";
        string stArg = Arg("-resgatestates:"); if (!string.IsNullOrEmpty(stArg)) only = new List<string>(stArg.Split(','));
        if (C.muteAudio) AudioListener.volume = 0f;
        Log("start size=" + sizeId + " window=" + Screen.width + "x" + Screen.height + " gpu=" + SystemInfo.graphicsDeviceName + " scale=" + F(HudCfg.Scale()) + " data=" + (Arg("-resgatedata:") ?? "(streaming)"));
        SizeRow row = null; foreach (var r in C.sizes) if (r != null && r.id == sizeId) row = r;
        int wantW = row != null ? (row.proxy != null && row.proxy.w > 0 ? row.proxy.w : row.w) : Screen.width, wantH = row != null ? (row.proxy != null && row.proxy.h > 0 ? row.proxy.h : row.h) : Screen.height;
        bool sizeOk = Screen.width == wantW && Screen.height == wantH;
        if (!sizeOk) { Log("WINDOW SIZE MISMATCH: asked " + wantW + "x" + wantH + ", got " + Screen.width + "x" + Screen.height + " (clamped by the display)"); if (C.requireWindowSize) fails.Add("window " + Screen.width + "x" + Screen.height + " != " + wantW + "x" + wantH); }

        float w0 = Time.realtimeSinceStartup;
        while (!GameLoop.Ready || GameLoop.Player == null || GameLoop.Enemy == null)
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSec) { Log("FAIL fight not ready after " + C.bootWaitSec + " s"); Finish(2); yield break; }
            yield return null;
        }
        cam = Camera.main; rig = FindFirstObjectByType<CameraRig>();
        if (cam == null) { Log("FAIL no Camera.main"); Finish(2); yield break; }
        Duel.Frozen = true;   // sim off at once (an idle player must not be killed while the camera settles); the camera rig and the IMGUI keep running
        for (int i = 0; i < C.settleFrames; i++) yield return null;

        bool hasLootArg = Flag("-lootdemo");
        foreach (var s in C.states)
        {
            if (s == null || string.IsNullOrEmpty(s.id) || (only != null && !only.Contains(s.id))) continue;
            if (!string.IsNullOrEmpty(s.needsArg) && !Flag(s.needsArg)) { Log("skip state " + s.id + " (needs " + s.needsArg + ")"); continue; }
            yield return DoState(s.id, hasLootArg);
            if (exiting) yield break;
        }
        if (Flag("-resgateoff") && C.offscreen.enabled && (only == null || only.Contains("offscreen"))) yield return OffscreenPass();
        WriteResult();
        Finish(0);   // informational: resgate_check.py decides PASS / FAIL
    }

    // ---------------- window states ----------------
    float ownHintSec, ownHintDelay, savedToggle; bool savedGuard, guardSaved; int savedSide;
    MenuUI menu; Func<bool> keep;

    IEnumerator WaitKeep(int frames, float secs)
    {
        float t = Time.realtimeSinceStartup;
        for (int i = 0; i < frames || Time.realtimeSinceStartup - t < secs; i++) { if (keep != null) keep(); yield return null; }
    }

    IEnumerator DoState(string id, bool lootArg)
    {
        var P = GameLoop.Player; keep = null;
        try
        {
            switch (id)
            {
                case "fight": break;
                case "hints": { var h = HudCfg.Get().hint; ownHintSec = h.showSeconds; ownHintDelay = h.delayS; h.showSeconds = 100000f; h.delayS = 0f; break; }
                case "lock": { FlowTable.ForceHud = true; if (rig != null) rig.lockEnabled = true; savedToggle = FlowTable.lastToggleTime; keep = () => { FlowTable.lastToggleTime = Time.unscaledTime + 3600f; FlowTable.lastToggleOn = true; return true; }; break; }
                case "guard": { if (P != null) { guardSaved = true; savedGuard = P.guardHeld; savedSide = P.guardSide; keep = () => { P.guardHeld = true; P.guardSide = 1; return true; }; } break; }
                case "victory": keep = () => { GameLoop.Outcome = GameLoop.Result.Victory; GameLoop.ScreenAlpha = 1f; return true; }; break;
                case "defeat": keep = () => { GameLoop.Outcome = GameLoop.Result.Defeat; GameLoop.ScreenAlpha = 1f; return true; }; break;
                case "menu_pause": case "menu_settings": if (!OpenMenu(id == "menu_settings")) { Log("skip state " + id + " (menu not reachable)"); return Empty(); } break;
                case "loot_labels": break;   // the -lootdemo drop sits in front of the player; its labels draw while the loot screen is closed
                case "loot_screen": if (!lootArg || !LootScreen.CanOpen(true)) { Log("skip state loot_screen (cannot open)"); return Empty(); } LootScreen.OpenScreen(); break;
                default: Log("skip unknown state " + id); return Empty();
            }
        }
        catch (Exception e) { Log("state " + id + " setup failed: " + e.Message); fails.Add("state " + id + ": " + e.Message); return Empty(); }
        return CaptureState(id);
    }
    static IEnumerator Empty() { yield break; }

    IEnumerator CaptureState(string id)
    {
        yield return WaitKeep(C.stateFrames, C.stateSeconds);
        string tag = sizeId + "_" + id; string full = "shot_" + tag + ".png", hud = "", probe = "";
        yield return new WaitForEndOfFrame();
        if (keep != null) keep();
        SavePng(ScreenCapture.CaptureScreenshotAsTexture(), Path.Combine(dir, full));
        if (C.hudOnly)
        {
            var hd = cam.GetComponent<HDAdditionalCameraData>(); int mask = cam.cullingMask;
            HDAdditionalCameraData.ClearColorMode mode = hd != null ? hd.clearColorMode : HDAdditionalCameraData.ClearColorMode.Sky; Color bg = hd != null ? hd.backgroundColorHDR : Color.black;
            cam.cullingMask = 0; if (hd != null) { hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; hd.backgroundColorHDR = Color.black; }
            for (int i = 0; i < C.hudFrames; i++) { if (keep != null) keep(); yield return null; }
            if (keep != null) keep();
            yield return null;   // the probe is armed in this frame, before its OnGUI pass
            ResGateProbe.Items.Clear(); ResGateProbe.On = true;
            if (keep != null) keep();
            yield return new WaitForEndOfFrame();
            ResGateProbe.On = false;
            hud = "hud_" + tag + ".png"; probe = "probe_" + tag + ".json";
            SavePng(ScreenCapture.CaptureScreenshotAsTexture(), Path.Combine(dir, hud));
            WriteProbe(Path.Combine(dir, probe));
            cam.cullingMask = mask; if (hd != null) { hd.clearColorMode = mode; hd.backgroundColorHDR = bg; }
        }
        float s = HudCfg.Scale();
        rows.Add("{\"size\":" + Q(sizeId) + ",\"state\":" + Q(id) + ",\"actualW\":" + Screen.width + ",\"actualH\":" + Screen.height + ",\"scale\":" + F(s) + ",\"vw\":" + F(Screen.width / s) + ",\"vh\":" + F(Screen.height / s)
            + ",\"full\":" + Q(full) + ",\"hud\":" + Q(hud) + ",\"probe\":" + Q(probe) + "}");
        Log("captured " + tag);
        keep = null;   // undo this state
        try
        {
            switch (id)
            {
                case "hints": { var h = HudCfg.Get().hint; h.showSeconds = ownHintSec; h.delayS = ownHintDelay; break; }
                case "lock": FlowTable.ForceHud = false; FlowTable.lastToggleTime = savedToggle; break;
                case "guard": if (guardSaved && GameLoop.Player != null) { GameLoop.Player.guardHeld = savedGuard; GameLoop.Player.guardSide = savedSide; } guardSaved = false; break;
                case "victory": case "defeat": GameLoop.Outcome = GameLoop.Result.None; GameLoop.ScreenAlpha = 0f; break;
                case "menu_pause": case "menu_settings": CloseMenu(); break;
                case "loot_screen": LootScreen.CloseScreen(); break;
            }
        }
        catch (Exception e) { Log("state " + id + " undo failed: " + e.Message); }
        for (int i = 0; i < 3; i++) yield return null;
    }

    // MenuUI has no public open: the private OpenMenu / OpenSettings / ForceClose are called by name (reflection); a rename makes the state skip, never breaks the run
    bool OpenMenu(bool settings)
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        menu = FindFirstObjectByType<MenuUI>(); if (menu == null) return false;
        var mo = typeof(MenuUI).GetMethod("OpenMenu", NP); if (mo == null) return false;
        mo.Invoke(menu, null);
        if (settings) { var ms = typeof(MenuUI).GetMethod("OpenSettings", NP); if (ms == null) return false; ms.Invoke(menu, new object[] { 0 }); }
        return MenuUI.Open;
    }
    void CloseMenu()
    {
        if (menu == null) return;
        var mc = typeof(MenuUI).GetMethod("ForceClose", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance); if (mc != null) mc.Invoke(menu, null);
        Time.timeScale = 1f;
    }

    // ---------------- offscreen 3D pass (true target size, HUD not included) ----------------
    IEnumerator OffscreenPass()
    {
        var ids = new List<string>(C.offscreen.sizeIds); var P = GameLoop.Player; var E = GameLoop.Enemy;
        foreach (var r in C.sizes)
        {
            if (r == null || (ids.Count > 0 && !ids.Contains(r.id))) continue;
            RenderTexture rt = null; Texture2D tex = null;
            try { rt = new RenderTexture(r.w, r.h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 }; rt.Create(); cam.targetTexture = rt; }
            catch (Exception e) { Log("offscreen " + r.id + " RT failed: " + e.Message); fails.Add("offscreen " + r.id + ": " + e.Message); continue; }
            for (int i = 0; i < C.offscreen.settleFrames; i++) yield return null;
            yield return new WaitForEndOfFrame();
            float asp = cam.aspect, fov = cam.fieldOfView, hfov = 2f * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * asp) * Mathf.Rad2Deg;
            float vp = rig != null && P != null ? rig.VisibleFraction(P.transform) : -1f, ve = rig != null && E != null ? rig.VisibleFraction(E.transform) : -1f;
            float hp = P != null ? CamReframe.ScreenH(cam, P.transform) : -1f, he = E != null ? CamReframe.ScreenH(cam, E.transform) : -1f;
            string png = "";
            try
            {
                RenderTexture.active = rt; tex = new Texture2D(r.w, r.h, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, r.w, r.h), 0, 0, false); tex.Apply(false); RenderTexture.active = null;
                png = "cam_" + r.id + ".png"; if (C.offscreen.keepPng) File.WriteAllBytes(Path.Combine(dir, png), tex.EncodeToPNG());
            }
            catch (Exception e) { Log("offscreen " + r.id + " read failed: " + e.Message); fails.Add("offscreen " + r.id + ": " + e.Message); }
            cam.targetTexture = null; cam.ResetAspect();
            if (tex != null) Destroy(tex);
            if (rt != null) { rt.Release(); Destroy(rt); }
            offRows.Add("{\"size\":" + Q(r.id) + ",\"w\":" + r.w + ",\"h\":" + r.h + ",\"aspect\":" + F(asp) + ",\"vfovDeg\":" + F(fov) + ",\"hfovDeg\":" + F(hfov) + ",\"visPlayer\":" + F(vp) + ",\"visEnemy\":" + F(ve)
                + ",\"screenHPlayer\":" + F(hp) + ",\"screenHEnemy\":" + F(he) + ",\"widen\":" + F(CamFraming.Widen) + ",\"png\":" + Q(png) + "}");
            Log("offscreen " + r.id + " " + r.w + "x" + r.h + " hfov " + F(hfov) + " vis " + F(vp) + "/" + F(ve) + " screenH " + F(hp));
            yield return null;
        }
    }

    // ---------------- output ----------------
    static void SavePng(Texture2D t, string path)
    {
        if (t == null) return;
        try { File.WriteAllBytes(path, t.EncodeToPNG()); } catch (Exception e) { Debug.LogWarning("[resgate] png " + path + ": " + e.Message); }
        Destroy(t);
    }

    void WriteProbe(string path)
    {
        var sb = new StringBuilder(); sb.Append("{\"screenW\":" + Screen.width + ",\"screenH\":" + Screen.height + ",\"items\":[");
        for (int i = 0; i < ResGateProbe.Items.Count; i++)
        {
            var it = ResGateProbe.Items[i]; if (i > 0) sb.Append(',');
            sb.Append("{\"kind\":" + Q(it.kind) + ",\"text\":" + Q(it.text) + ",\"anchor\":" + Q(it.anchor) + ",\"x\":" + F(it.x) + ",\"y\":" + F(it.y) + ",\"w\":" + F(it.w) + ",\"h\":" + F(it.h) + ",\"needW\":" + F(it.needW) + ",\"size\":" + it.size + "}");
        }
        sb.Append("]}"); try { File.WriteAllText(path, sb.ToString()); } catch { }
    }

    void WriteResult()
    {
        var sb = new StringBuilder();
        sb.Append("{\"sizeId\":" + Q(sizeId) + ",\"window\":[" + Screen.width + "," + Screen.height + "],\"gpu\":" + Q(SystemInfo.graphicsDeviceName) + ",\"unity\":" + Q(Application.unityVersion) + ",\"fails\":[");
        for (int i = 0; i < fails.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(fails[i])); }
        sb.Append("],\"states\":[" + string.Join(",", rows.ToArray()) + "],\"offscreen\":[" + string.Join(",", offRows.ToArray()) + "]}");
        try { File.WriteAllText(Path.Combine(dir, "resgate_result_" + sizeId + fileTag + ".json"), sb.ToString()); } catch (Exception e) { Debug.LogWarning("[resgate] result: " + e.Message); }
    }

    void Finish(int code)
    {
        if (exiting) return; exiting = true;
        try { if (dir != null) File.WriteAllText(Path.Combine(dir, "resgate_done_" + sizeId + fileTag + ".txt"), "exit " + code + "\n"); } catch { }
        Time.timeScale = 1f; StartCoroutine(Quit(code));
    }
    IEnumerator Quit(int code) { yield return new WaitForSecondsRealtime(C.exitGraceSec); Application.Quit(code); }
}
#else // FIXHARNESS v1 stub nl=0
using UnityEngine;
public static class ResGateProbe
{
    public static bool On;
    public static void Box(Rect r) { }
    public static void Text(Rect r, string s, string kind, int size, TextAnchor a, bool bold, bool wrap = false) { }
    public static void Caps(float x, float y, float width, float height, string s, int size) { }
}   // release stub: HudDraw/LootDraw call these behind On
#endif // FIXHARNESS v1
