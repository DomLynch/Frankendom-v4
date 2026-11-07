using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// ROUTEBOT v1 (laneROUTEBOT): -routebot:<dir>, used with -zones. A scripted player walks the whole zone route in a real build with no human and writes <dir>\routebot.json (per step PASS / FAIL, the checks and their evidence)
// plus upright PNG frames in <dir>\frames. Steps and every tunable are DATA (route_bot.json). Never boots without the flag; with the flag it only reads game state and presses keys, it changes no sim code, no Duel, no brain.
//   INPUT   : real key presses. Windows SendInput delivers WM_KEYDOWN to the focused game window, so the legacy Input class, InputMap, PlayerBrain (move, run, strike), MarketUI (E, Tab, Esc) see exactly what a keyboard sends.
//             Only while Application.isFocused; every key is released when focus is lost or a step ends. Direction keys are chosen the way PlayerBrain turns keys into a direction: camera-relative, 8 ways.
//   READS   : GameLoop.Player / Enemy / EnemyName, GameHUD.enemy (the Health the boss bar draws), CameraRig, ZoneRuntime / ZoneSpan state, LootSave coins, MarketUI. Nothing private except MarketUI.shop (reflection, evidence only).
//   ASSIST  : combat.assistHpFloorFrac puts the player's health back to a floor (counted and reported) so one bad exchange does not end the proof. 0 = none.
//   REUSES  : ZoneData / ZoneGround.Probe / ZoneGroundHold / ZoneSave / ZoneCore (the kill-record and respawn rules), SpanData, ChaseTable, ZoneSpan.Live, MarketCfg / MarketWorld / LootSave, InputMap bindings, GameLoop.Retry (the death path's reload).
// A step that runs past its timeout fails and the run moves on. The run quits by itself (data quit): exit code 0 when every step passed, else 1. UNPROVEN IN UNITY (SendInput delivery, screen capture, combat tuning).

// ------------------------------------------------------------------ data (route_bot.json)
[Serializable] public class ZrInput { public bool scanCodes = true, requireFocus = true; public float focusWaitS = 30f, tapMaxS = 0.12f, deadzone = 0.38f; public int tapFrames = 3, gapFrames = 4, selfTestFrames = 40; public string selfTestKey = "LeftShift", forward = "W", back = "S", left = "A", right = "D", run = "LeftShift", strike = "F", guard = "Q"; }
[Serializable] public class ZrCapture { public bool enabled = true, flipY; public string mode = "screen", folder = "frames"; public int width = 1920, height = 1080, settleFrames = 2; }
[Serializable] public class ZrNav { public float arriveM = 1f, runBeyondM = 7f, stuckSeconds = 5f, stuckMoveM = 0.25f, groundTolM = 0.35f; }
[Serializable] public class ZrGuard { public bool enabled; public float gapM = 3.4f; }
[Serializable] public class ZrCombat { public float attackGapM = 2.6f, approachGapM = 2.2f, strikeIntervalS = 0.55f, staminaMinFrac = 0.2f, assistHpFloorFrac = 0.45f, hudMaxDeviation = 0.03f, barEndMaxFrac = 0.12f, camMaxDistM = 22f, camSampleS = 0.2f, spawnWaitS = 25f; public ZrGuard guard = new ZrGuard(); }
[Serializable]
public class ZrStep
{
    public string id = "", name = "", kind = "", spawn = "", otherSpawn = "", expectName = "", doorGate = "", toZone = "", toSpot = "", zone = "", spot = "", pushZone = "", pushSpot = "";
    public string[] revive = new string[0];
    public float timeoutS, minBarDropFrac, settleS, finisherWaitS, camMinFollowFrac, dwellS, pushUntilGapM, followWaitS, throughMarginM;   // 0 = take the value from "stepDefaults" (so a row only lists what differs)
    public bool assist, slow, ground; public int tapSettleFrames, maxDownPresses, closeTries;   // slow = walk instead of run
}
[Serializable] public class ZrStepDef { public float timeoutS = 60f, minBarDropFrac = 0.5f, settleS = 4f, finisherWaitS = 15f, camMinFollowFrac = 0.6f, dwellS = 2f, pushUntilGapM = 7f, followWaitS = 20f, throughMarginM = 1f; public int tapSettleFrames = 6, maxDownPresses = 12, closeTries = 3; }
[Serializable]
public class ZrCfg
{
    public int version = 1; public bool quit = true; public float globalTimeoutS = 1500f, startWaitS = 90f; public int readyExtraFrames = 30;
    public ZrStepDef stepDefaults = new ZrStepDef(); public ZrInput input = new ZrInput(); public ZrCapture capture = new ZrCapture(); public ZrNav nav = new ZrNav(); public ZrCombat combat = new ZrCombat();
    public string[] failOnLogTypes = new string[] { "Exception" }, badLogs = new string[0], logPrefixes = new string[0]; public string flashLog = "[Zones] crossing ";
    public ZrStep[] steps = new ZrStep[0];
}

// ------------------------------------------------------------------ report (routebot.json)
[Serializable] public class ZrCheck { public string name = "", detail = ""; public bool ok; }
[Serializable] public class ZrFrame { public string label = "", file = ""; public int w, h; public float lumaTop, lumaBottom; }
[Serializable]
public class ZrStepRes
{
    public string id = "", name = "", kind = "", status = "SKIP", why = ""; public float seconds, timeoutS;
    public List<ZrCheck> checks = new List<ZrCheck>(); public List<string> evidence = new List<string>(), logs = new List<string>(), exceptions = new List<string>(); public List<ZrFrame> frames = new List<ZrFrame>();
}
[Serializable]
public class ZrReport
{
    public string tool = "ZoneRouteBot v1 (laneROUTEBOT)", result = "RUNNING", note = "", startedUtc = "", finishedUtc = "", unity = "", build = "", resolution = "", commandLine = "", dir = "", inputMode = "";
    public int pass, fail, skip, assists, errorsLogged, exceptionsLogged; public float seconds; public List<ZrStepRes> steps = new List<ZrStepRes>();
}

// ------------------------------------------------------------------ OS keyboard (SendInput). Windows player only; elsewhere every call reports "not available".
static class ZrOs
{
#if UNITY_STANDALONE_WIN
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static string FgNote = ""; static bool fgLogged;
    // v109 hard guard: a key goes out ONLY when the OS foreground window belongs to this process (Application.isFocused alone is not enough: keys must never land in another app).
    public static bool ForegroundIsUs()
    {
        try { uint pid; var h = GetForegroundWindow(); GetWindowThreadProcessId(h, out pid); bool ok = h != IntPtr.Zero && pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id; FgNote = "foreground hwnd " + h.ToInt64() + " pid " + pid + " ours " + System.Diagnostics.Process.GetCurrentProcess().Id + (ok ? " OK" : " NOT us"); if (!ok && !fgLogged) { fgLogged = true; UnityEngine.Debug.LogWarning("[RouteBot] key send blocked: " + FgNote); } return ok; }
        catch (Exception e) { FgNote = e.GetType().Name; return false; }
    }
    public static bool Available = true; public static string LastError = "";
    public static bool Send(ushort vk, bool down, bool scan)
    {
        if (!Available || vk == 0) return false;
        if (down && !ForegroundIsUs()) { LastError = FgNote; return false; }   // v109 guard
        try
        {
            uint flags = down ? 0u : 0x0002u; ushort sc = (ushort)MapVirtualKey(vk, 0);
            if (scan) flags |= 0x0008u;
            if (vk >= 0x21 && vk <= 0x28) flags |= 0x0001u;   // arrows, home, end, page keys: extended
            var inp = new INPUT[1]; inp[0].type = 1; inp[0].u.ki.wVk = scan ? (ushort)0 : vk; inp[0].u.ki.wScan = sc; inp[0].u.ki.dwFlags = flags;
            uint n = SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
            if (n != 1) { LastError = "SendInput returned " + n + " (error " + Marshal.GetLastWin32Error() + ")"; return false; }
            return true;
        }
        catch (Exception e) { Available = false; LastError = e.GetType().Name + ": " + e.Message; return false; }
    }
    public static void Front() { try { SetForegroundWindow(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle); } catch (Exception) { } }
#else
    public static bool Available = false; public static string LastError = "not a Windows player";
    public static bool Send(ushort vk, bool down, bool scan) { return false; }
    public static bool ForegroundIsUs() { return false; }
    public static string FgNote = "";
    public static void Front() { }
#endif
    public static ushort Vk(KeyCode k)
    {
        if (k >= KeyCode.A && k <= KeyCode.Z) return (ushort)(0x41 + (k - KeyCode.A));
        if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return (ushort)(0x30 + (k - KeyCode.Alpha0));
        switch (k)
        {
            case KeyCode.Space: return 0x20; case KeyCode.Return: return 0x0D; case KeyCode.Escape: return 0x1B; case KeyCode.Tab: return 0x09; case KeyCode.Backspace: return 0x08;
            case KeyCode.LeftShift: return 0xA0; case KeyCode.RightShift: return 0xA1; case KeyCode.LeftControl: return 0xA2; case KeyCode.LeftAlt: return 0xA4;
            case KeyCode.LeftArrow: return 0x25; case KeyCode.UpArrow: return 0x26; case KeyCode.RightArrow: return 0x27; case KeyCode.DownArrow: return 0x28;
        }
        return 0;
    }
}

public sealed class ZoneRouteBot : MonoBehaviour
{
    sealed class Tap { public KeyCode k; public int frame; public float t; }
    sealed class LogLine { public string text; }
    sealed class CamAcc { public int n, bad, nonFinite, far, wrongTarget, staleLock; public float maxDist, camZ0 = float.NaN, plZ0 = float.NaN, camZ1, plZ1, next; }

    static string dir = "";
    ZrCfg C; ZrReport R; ZrStepRes cur; ZrStep curStep; float runT0, stepT0; int frameNo, errStart; int logStart;
    readonly HashSet<KeyCode> held = new HashSet<KeyCode>(); readonly List<Tap> taps = new List<Tap>();
    readonly object logLock = new object(); readonly List<LogLine> lines = new List<LogLine>(); readonly List<string[]> errs = new List<string[]>();   // {type, text}
    bool assistOn, focused = true, aborted; int assists;
    GameHUD hud; CameraRig rig; bool walkOk; string walkWhy = ""; KeyCode kF, kB, kL, kR, kRun, kStrike, kGuard;
    int enFrame = -1; readonly List<DuelFighter> en = new List<DuelFighter>(); float nextStrike; bool guarding; static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    string[] onlyIds;

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { dir = ""; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        dir = Arg("-routebot:"); if (string.IsNullOrEmpty(dir)) return;
        if (GameObject.Find("ZoneRouteBot") != null) return;
        var go = new GameObject("ZoneRouteBot"); DontDestroyOnLoad(go); go.AddComponent<ZoneRouteBot>();
    }

    void Awake() { Application.logMessageReceivedThreaded += OnLog; }
    void OnDestroy() { Application.logMessageReceivedThreaded -= OnLog; ReleaseAll(); }
    void OnApplicationQuit() { ReleaseAll(); }
    void OnDisable() { ReleaseAll(); }

    void Start()
    {
        C = new ZrCfg(); var t = HudCfg.ReadData("route_bot.json");
        if (t != null) { try { var k = JsonUtility.FromJson<ZrCfg>(t); if (k != null) C = k; } catch (Exception e) { Debug.LogWarning("[RouteBot] route_bot.json unreadable, defaults (no steps): " + e.Message); } }
        Fix();
        var only = Arg("-routebot-only:"); onlyIds = string.IsNullOrEmpty(only) ? new string[0] : only.Split(',');
        StartCoroutine(Main());
    }
    void Fix()
    {
        if (C.input == null) C.input = new ZrInput(); if (C.capture == null) C.capture = new ZrCapture(); if (C.nav == null) C.nav = new ZrNav(); if (C.combat == null) C.combat = new ZrCombat(); if (C.combat.guard == null) C.combat.guard = new ZrGuard();
        if (C.steps == null) C.steps = new ZrStep[0]; if (C.failOnLogTypes == null) C.failOnLogTypes = new string[0]; if (C.badLogs == null) C.badLogs = new string[0]; if (C.logPrefixes == null) C.logPrefixes = new string[0];
        if (C.stepDefaults == null) C.stepDefaults = new ZrStepDef(); var D = C.stepDefaults;
        foreach (var s in C.steps)
        {
            if (s == null) continue; if (s.revive == null) s.revive = new string[0];
            if (s.timeoutS <= 0f) s.timeoutS = D.timeoutS; if (s.minBarDropFrac <= 0f) s.minBarDropFrac = D.minBarDropFrac; if (s.settleS <= 0f) s.settleS = D.settleS; if (s.finisherWaitS <= 0f) s.finisherWaitS = D.finisherWaitS;
            if (s.camMinFollowFrac <= 0f) s.camMinFollowFrac = D.camMinFollowFrac; if (s.dwellS <= 0f) s.dwellS = D.dwellS; if (s.pushUntilGapM <= 0f) s.pushUntilGapM = D.pushUntilGapM; if (s.followWaitS <= 0f) s.followWaitS = D.followWaitS;
            if (s.throughMarginM <= 0f) s.throughMarginM = D.throughMarginM; if (s.tapSettleFrames <= 0) s.tapSettleFrames = D.tapSettleFrames; if (s.maxDownPresses <= 0) s.maxDownPresses = D.maxDownPresses; if (s.closeTries <= 0) s.closeTries = D.closeTries;
        }
    }

    // ------------------------------------------------------------------ log capture
    void OnLog(string cond, string stack, LogType t)
    {
        lock (logLock)
        {
            if (t == LogType.Exception || t == LogType.Error || t == LogType.Assert) { if (errs.Count < 500) errs.Add(new[] { t.ToString(), cond + (string.IsNullOrEmpty(stack) ? "" : " | " + FirstLine(stack)) }); }
            if (C == null || cond == null) return;
            foreach (var p in C.logPrefixes) if (p.Length > 0 && cond.StartsWith(p, StringComparison.Ordinal)) { if (lines.Count < 6000) lines.Add(new LogLine { text = cond }); break; }
        }
    }
    static string FirstLine(string s) { int i = s.IndexOf('\n'); return i > 0 ? s.Substring(0, i).Trim() : s.Trim(); }
    int LogMark() { lock (logLock) return lines.Count; }
    bool LogHas(string needle, int from) { lock (logLock) { for (int i = Math.Max(0, from); i < lines.Count; i++) if (lines[i].text.Contains(needle)) return true; } return false; }
    string LogFirst(string needle, int from) { lock (logLock) { for (int i = Math.Max(0, from); i < lines.Count; i++) if (lines[i].text.Contains(needle)) return lines[i].text; } return ""; }

    // ------------------------------------------------------------------ keys
    bool CanSend() { return ZrOs.Available && (!C.input.requireFocus || Application.isFocused); }
    void SetHeld(KeyCode k, bool on)
    {
        if (k == KeyCode.None) return; bool is_ = held.Contains(k); if (on == is_) return;
        if (on && !CanSend()) return;
        if (ZrOs.Send(ZrOs.Vk(k), on, C.input.scanCodes)) { if (on) held.Add(k); else held.Remove(k); }
        else if (!on) held.Remove(k);
    }
    void TapKey(KeyCode k) { if (k == KeyCode.None || !CanSend()) return; if (held.Contains(k)) SetHeld(k, false); SetHeld(k, true); if (held.Contains(k)) taps.Add(new Tap { k = k, frame = Time.frameCount, t = Time.realtimeSinceStartup }); }
    void ReleaseAll() { if (held.Count == 0 && taps.Count == 0) return; var l = new List<KeyCode>(held); foreach (var k in l) SetHeld(k, false); held.Clear(); taps.Clear(); guarding = false; }
    void TapTick()
    {
        for (int i = taps.Count - 1; i >= 0; i--)
        {
            var t = taps[i]; int f = Time.frameCount - t.frame;
            if (f >= Mathf.Max(2, C.input.tapFrames) || (f >= 2 && Time.realtimeSinceStartup >= t.t + C.input.tapMaxS)) { SetHeld(t.k, false); taps.RemoveAt(i); }
        }
    }
    static KeyCode Parse(string n, KeyCode fb) { KeyCode k; return !string.IsNullOrEmpty(n) && Enum.TryParse(n, true, out k) ? k : fb; }
    static bool Kb(KeyCode k) { string s = k.ToString(); return k != KeyCode.None && !s.StartsWith("Mouse", StringComparison.Ordinal) && !s.StartsWith("Joystick", StringComparison.Ordinal); }
    /// <summary>The player's own binding of an action (first keyboard key), else the data fallback.</summary>
    KeyCode ActionKey(string action, string fallback)
    {
        var a = InputMap.Find(action); if (a != null) foreach (var k in a.cur) if (Kb(k)) return k;
        return Parse(fallback, KeyCode.None);
    }
    static KeyCode FirstKb(KeyCode[] ks, KeyCode fb) { if (ks != null) foreach (var k in ks) if (Kb(k)) return k; return fb; }
    void ResolveKeys()
    {
        var I = C.input; kF = ActionKey("moveForward", I.forward); kB = ActionKey("moveBack", I.back); kL = ActionKey("moveLeft", I.left); kR = ActionKey("moveRight", I.right);
        kRun = ActionKey("run", I.run); kStrike = ActionKey("strike", I.strike); kGuard = ActionKey("guard", I.guard);
    }
    void MoveKeys(float x, float y, bool run)
    {
        float dz = C.input.deadzone; SetHeld(kF, y > dz); SetHeld(kB, y < -dz); SetHeld(kR, x > dz); SetHeld(kL, x < -dz); SetHeld(kRun, run && (y > dz || y < -dz || x > dz || x < -dz));
    }
    void StopMove() { MoveKeys(0f, 0f, false); }

    // ------------------------------------------------------------------ world access
    static DuelFighter Me() { var p = GameLoop.Player; return p != null && p.gameObject.activeInHierarchy ? p : null; }
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }
    static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
    List<DuelFighter> Enemies()
    {
        if (enFrame != Time.frameCount)
        {
            enFrame = Time.frameCount; en.Clear();
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f != null && !f.name.Contains(Roster.PlayerObject)) en.Add(f);
        }
        return en;
    }
    DuelFighter FindEnemy(string spawn) { string key = "#" + spawn; foreach (var f in Enemies()) if (f != null && f.name.Contains(key)) return f; return null; }
    static bool Engaged(DuelFighter f, DuelFighter me) { if (f == null || me == null || f.Dead) return false; var b = f.GetComponent<AIBrain>(); return f.opponent == me && b != null && b.enabled; }
    GameHUD Hud() { if (hud == null) hud = FindFirstObjectByType<GameHUD>(); return hud; }
    CameraRig Rig() { if (rig == null) rig = FindFirstObjectByType<CameraRig>(); return rig; }
    static Transform CamT(DuelFighter me) { var pb = me != null ? me.GetComponent<PlayerBrain>() : null; if (pb != null && pb.cam != null) return pb.cam; return Camera.main != null ? Camera.main.transform : null; }
    static bool SpotWorld(string zone, string spot, out Vector3 w)
    {
        w = Vector3.zero; var sp = ZoneData.Spot(ZoneData.Zone(zone), spot); if (sp == null || sp.pos == null || sp.pos.Length < 2) return false;
        float gy, d; if (!ZoneGround.Probe(new Vector3(sp.pos[0], 2f, sp.pos[1]), null, out gy, out d)) gy = 0f; w = new Vector3(sp.pos[0], gy, sp.pos[1]); return true;
    }
    static float DoorBoundary(string gateId) { var K = SpanData.Get(); foreach (var z in K.zones) if (z != null && z.gate != null && z.gate.id == gateId) return z.offsetZ + z.gate.boundaryLocal; return float.NaN; }
    static float GiveUpGap(DuelFighter f) { var r = ChaseTable.Enabled && f != null ? ChaseTable.Row(Roster.IdOf(f.transform)) : null; return r != null ? r.giveUpGap : float.NaN; }
    string F2(float v) { return v.ToString("F2", IC); }

    // ------------------------------------------------------------------ per frame: focus, taps, assist
    void Update()
    {
        bool f = Application.isFocused; if (!f && focused) ReleaseAll(); focused = f;
        TapTick();
        if (assistOn && C != null && C.combat.assistHpFloorFrac > 0f)
        {
            var me = Me();
            if (me != null && !me.Dead && me.maxHealth > 0f)
            {
                float floor = C.combat.assistHpFloorFrac * me.maxHealth;
                if (me.health < floor) { assists++; if (assists <= 5) Ev("assist: player hp " + F2(me.health) + " -> " + F2(floor)); me.health = floor; var h = me.GetComponent<Health>(); if (h != null) h.current = floor; }
            }
        }
        if (R != null && R.result == "RUNNING" && C != null && Time.realtimeSinceStartup - runT0 > C.globalTimeoutS && !aborted) { aborted = true; }
    }

    // ------------------------------------------------------------------ report helpers
    void Chk(string name, bool ok, string detail) { if (cur == null) return; cur.checks.Add(new ZrCheck { name = name, ok = ok, detail = detail ?? "" }); }
    void Ev(string s) { if (cur != null && cur.evidence.Count < 80) cur.evidence.Add(F2(Time.realtimeSinceStartup - stepT0) + " s: " + s); }
    void Write()
    {
        try
        {
            R.assists = assists; R.seconds = Time.realtimeSinceStartup - runT0; lock (logLock) { R.exceptionsLogged = 0; R.errorsLogged = 0; foreach (var e in errs) { if (e[0] == "Exception") R.exceptionsLogged++; else R.errorsLogged++; } }
            Directory.CreateDirectory(dir); string p = Path.Combine(dir, "routebot.json"), tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(R, true)); if (File.Exists(p)) File.Delete(p); File.Move(tmp, p);
        }
        catch (Exception e) { Debug.LogWarning("[RouteBot] cannot write the report: " + e.Message); }
    }

    // ------------------------------------------------------------------ frames
    IEnumerator Shot(string label)
    {
        var cp = C.capture; if (!cp.enabled || cur == null) yield break;
        for (int i = 0; i < cp.settleFrames; i++) yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D tex = null; RenderTexture rt = null; string why = "";
        try
        {
            if (cp.mode == "camera" && Camera.main != null)
            {
                var cam = Camera.main; var keep = cam.targetTexture; rt = new RenderTexture(cp.width, cp.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = rt; cam.Render(); cam.targetTexture = keep; var prev = RenderTexture.active; RenderTexture.active = rt;
                tex = new Texture2D(cp.width, cp.height, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, cp.width, cp.height), 0, 0); tex.Apply(); RenderTexture.active = prev;
            }
            else tex = ScreenCapture.CaptureScreenshotAsTexture();
        }
        catch (Exception e) { why = e.Message; }
        if (rt != null) { rt.Release(); Destroy(rt); }
        if (tex == null) { Ev("frame '" + label + "' failed: " + why); yield break; }
        try
        {
            float lt = Luma(tex, 0.9f, 1f), lb = Luma(tex, 0f, 0.1f); if (cp.flipY) Flip(tex);
            frameNo++; string fd = Path.Combine(dir, string.IsNullOrEmpty(cp.folder) ? "frames" : cp.folder); Directory.CreateDirectory(fd);
            string fn = frameNo.ToString("00", IC) + "_" + cur.id + "_" + label + ".png"; File.WriteAllBytes(Path.Combine(fd, fn), tex.EncodeToPNG());
            cur.frames.Add(new ZrFrame { label = label, file = (string.IsNullOrEmpty(cp.folder) ? "frames" : cp.folder) + "/" + fn, w = tex.width, h = tex.height, lumaTop = lt, lumaBottom = lb });
        }
        catch (Exception e) { Ev("frame '" + label + "' not written: " + e.Message); }
        Destroy(tex);
    }
    static float Luma(Texture2D t, float y0, float y1)
    {
        int h = t.height, w = t.width, a = Mathf.Clamp((int)(y0 * h), 0, h - 1), b = Mathf.Clamp((int)(y1 * h), a + 1, h); double sum = 0; int n = 0;
        for (int y = a; y < b; y += 8) for (int x = 0; x < w; x += 8) { var c = t.GetPixel(x, y); sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; n++; }
        return n > 0 ? (float)(sum / n) : 0f;
    }
    static void Flip(Texture2D t)
    {
        int w = t.width, h = t.height; var px = t.GetPixels32(); var o = new Color32[px.Length];
        for (int y = 0; y < h; y++) Array.Copy(px, y * w, o, (h - 1 - y) * w, w);
        t.SetPixels32(o); t.Apply();
    }

    // ------------------------------------------------------------------ the run
    IEnumerator Drive(IEnumerator root, Action<Exception> fail)   // runs nested step coroutines by hand so an exception inside one is caught and reported, not lost
    {
        var st = new Stack<IEnumerator>(); st.Push(root);
        while (st.Count > 0)
        {
            bool more = false;
            try { more = st.Peek().MoveNext(); } catch (Exception e) { fail(e); yield break; }
            if (!more) { st.Pop(); continue; }
            var c = st.Peek().Current; var sub = c as IEnumerator; if (sub != null) { st.Push(sub); continue; }
            yield return c;
        }
    }

    IEnumerator Main()
    {
        runT0 = Time.realtimeSinceStartup; R = new ZrReport { startedUtc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion, build = Application.version, dir = dir, resolution = Screen.width + "x" + Screen.height, inputMode = "Windows SendInput (keyboard)" };
        var sb = new StringBuilder(); foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-", StringComparison.Ordinal) && !a.StartsWith("-logFile", StringComparison.Ordinal)) sb.Append(a).Append(' '); R.commandLine = sb.ToString().Trim();
        Write();
        // infrastructure step: zones on, the game is ready, keys reach the game
        bool ready = false; yield return RunStep(new ZrStep { id = "s00_ready_input", name = "Zones on, game ready, keyboard input reaches the game", kind = "ready", timeoutS = C.startWaitS + C.input.focusWaitS + 20f }, true, v => ready = v);
        if (!ready) { aborted = true; R.note = "stopped at the start: see step s00_ready_input"; }
        foreach (var s in C.steps)
        {
            if (s == null) continue;
            if (onlyIds.Length > 0 && Array.IndexOf(onlyIds, s.id) < 0) { Skip(s, "not in -routebot-only"); continue; }
            if (aborted) { Skip(s, R.note.Length > 0 ? R.note : "run aborted (global timeout or the start failed)"); continue; }
            yield return RunStep(s, false, null);
        }
        Finish();
    }
    void Skip(ZrStep s, string why) { var r = new ZrStepRes { id = s.id, name = s.name, kind = s.kind, status = "SKIP", why = why }; R.steps.Add(r); Write(); }

    IEnumerator RunStep(ZrStep s, bool infra, Action<bool> result)
    {
        cur = new ZrStepRes { id = s.id, name = s.name, kind = s.kind, timeoutS = s.timeoutS }; R.steps.Add(cur); curStep = s; stepT0 = Time.realtimeSinceStartup; logStart = LogMark(); lock (logLock) errStart = errs.Count;
        assistOn = s.assist; nextStrike = 0f; Debug.Log("[RouteBot] step " + s.id + " begins: " + s.name);
        bool done = false; Exception boom = null;
        var co = StartCoroutine(Drive(StepBody(s), e => boom = e)); var watch = StartCoroutine(DoneFlag(co, () => done = true));
        while (!done && Time.realtimeSinceStartup - stepT0 < s.timeoutS && !(aborted && !infra)) yield return null;
        bool timedOut = !done;
        if (timedOut) { StopCoroutine(co); StopCoroutine(watch); Chk("timeout", false, "the step did not finish inside " + F2(s.timeoutS) + " s"); }
        if (boom != null) Chk("script_error", false, boom.GetType().Name + ": " + boom.Message);
        ReleaseAll(); assistOn = false;
        yield return Shot("end");
        cur.seconds = Time.realtimeSinceStartup - stepT0;
        int exc = 0; lock (logLock)
        {
            for (int i = errStart; i < errs.Count; i++) { var e = errs[i]; if (Array.IndexOf(C.failOnLogTypes, e[0]) >= 0) { exc++; if (cur.exceptions.Count < 6) cur.exceptions.Add(e[0] + ": " + e[1]); } }
            for (int i = logStart; i < lines.Count && cur.logs.Count < 16; i++) cur.logs.Add(lines[i].text);
        }
        if (!infra) Chk("no_exception", exc == 0, exc == 0 ? "none logged in this step" : exc + " logged: " + cur.exceptions[0]);
        bool ok = cur.checks.Count > 0; string firstBad = "";
        foreach (var c in cur.checks) if (!c.ok) { ok = false; if (firstBad.Length == 0) firstBad = c.name + ": " + c.detail; }
        cur.status = ok ? "PASS" : "FAIL"; cur.why = ok ? "all " + cur.checks.Count + " checks passed" : (cur.checks.Count == 0 ? "no checks ran" : firstBad);
        if (cur.status == "PASS") R.pass++; else R.fail++;
        Debug.Log("[RouteBot] step " + s.id + " " + cur.status + " in " + F2(cur.seconds) + " s: " + cur.why);
        Write(); if (result != null) result(ok); cur = null;
    }
    IEnumerator DoneFlag(Coroutine co, Action fin) { yield return co; fin(); }

    void Finish()
    {
        foreach (var r in R.steps) if (r.status == "SKIP") R.skip++;
        R.result = R.fail == 0 && R.pass > 0 ? "PASS" : "FAIL"; R.finishedUtc = DateTime.UtcNow.ToString("o"); Write();
        Debug.Log("[RouteBot] " + R.result + ": " + R.pass + " pass, " + R.fail + " fail, " + R.skip + " skipped -> " + Path.Combine(dir, "routebot.json"));
        ReleaseAll(); if (C.quit) Application.Quit(R.result == "PASS" ? 0 : 1);
    }

    IEnumerator StepBody(ZrStep s)
    {
        switch (s.kind)
        {
            case "ready": return ReadyStep(s);
            case "restart": return RestartStep(s);
            case "kill": return KillStep(s);
            case "walk": return WalkStep(s);
            case "skip_past": return SkipStep(s);
            case "trade": return TradeStep(s);
        }
        return Unknown(s);
    }
    IEnumerator Unknown(ZrStep s) { Chk("known_kind", false, "step kind '" + s.kind + "' does not exist"); yield break; }

    // ------------------------------------------------------------------ ready + input self test
    IEnumerator ReadyStep(ZrStep s)
    {
        Chk("zones_active", ZoneSave.Active, ZoneSave.Active ? "zones are on" : "zones are off: start the game with -zones (and not in a harness mode)");
        if (!ZoneSave.Active) yield break;
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < C.startWaitS && (!ZoneRuntime.Owns || Me() == null || ZoneGroundHold.Active || Camera.main == null || GameLoopDriver.Transitioning)) yield return null;
        bool rdy = ZoneRuntime.Owns && Me() != null && !ZoneGroundHold.Active && Camera.main != null;
        Chk("game_ready", rdy, rdy ? "player " + Roster.PlayerName + " at " + Me().transform.position.ToString("F1") + ", span " + (ZoneSpan.Live ? "live" : "not live") : "zones, player or camera not ready after " + F2(C.startWaitS) + " s");
        if (!rdy) yield break;
        for (int i = 0; i < C.readyExtraFrames; i++) yield return null;
        ResolveKeys(); Ev("keys: fwd " + kF + " back " + kB + " left " + kL + " right " + kR + " run " + kRun + " strike " + kStrike + " guard " + kGuard + "; MenuUI open " + MenuUI.Open);
        Chk("zone_span_live", ZoneSpan.Live, ZoneSpan.Live ? "the span is bound (walking doors)" : "ZoneSpan is not live: doors would fall back to the flash crossing");
        Chk("os_input_available", ZrOs.Available, ZrOs.Available ? "SendInput loaded" : "SendInput unavailable: " + ZrOs.LastError);
        if (!ZrOs.Available) yield break;
        float tf = Time.realtimeSinceStartup; bool fronted = false;
        while (C.input.requireFocus && !Application.isFocused && Time.realtimeSinceStartup - tf < C.input.focusWaitS) { if (!fronted) { fronted = true; ZrOs.Front(); } yield return null; }
        Chk("window_focused", Application.isFocused || !C.input.requireFocus, Application.isFocused ? "the game window has the keyboard focus" : "the game window never got focus in " + F2(C.input.focusWaitS) + " s: keys would go to another program, so none are sent");
        if (!Application.isFocused && C.input.requireFocus) yield break;
        KeyCode tk = Parse(C.input.selfTestKey, KeyCode.LeftShift); SetHeld(tk, true); bool seen = false; int fr = 0;
        for (; fr < C.input.selfTestFrames; fr++) { bool g = false; try { g = Input.GetKey(tk); } catch (Exception) { } if (g) { seen = true; break; } yield return null; }
        SetHeld(tk, false);
        Chk("keys_reach_game", seen, seen ? "Input.GetKey(" + tk + ") saw the injected key after " + fr + " frames" : "Input.GetKey(" + tk + ") never saw the injected key in " + C.input.selfTestFrames + " frames (" + ZrOs.LastError + ")");
        yield return Shot("ready");
    }

    // ------------------------------------------------------------------ restart (second pass / fresh start): the death path's own reload, with kill records cleared
    IEnumerator RestartStep(ZrStep s)
    {
        var old = Me(); long now = ZoneCore.NowMs(); int revived = 0;
        foreach (var id in s.revive)
        {
            ZZone z; var row = ZoneData.FindSpawn(id, out z); if (row == null) { Chk("spawn_known_" + id, false, "zones.json has no spawn '" + id + "'"); continue; }
            var prev = ZoneSave.Rec(z.id, row.id);
            ZoneSave.Put(new SpawnRec { zone = z.id, spawn = row.id, creature = prev != null ? prev.creature : "", generation = prev != null ? prev.generation : 0, killedAtMs = 0, respawnAtMs = 0 }); revived++;
        }
        ZoneSave.SetPlayer(s.zone, s.spot, 1f); ZoneSave.Flush(true);
        Ev("save: " + revived + " spawn(s) alive again, player -> " + s.zone + " / " + s.spot + " at full health; GameLoop.Retry()");
        yield return Shot("before");
        ReleaseAll(); GameLoop.Retry();
        float t0 = Time.realtimeSinceStartup;
        while (old != null && Time.realtimeSinceStartup - t0 < 45f) yield return null;
        Chk("scene_reloaded", old == null, old == null ? "the old player object was replaced" : "the scene did not reload in 45 s (GameLoop.Retry had no effect)");
        if (old != null) yield break;
        hud = null; rig = null;
        t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < C.startWaitS && (!ZoneRuntime.Owns || Me() == null || ZoneGroundHold.Active || Camera.main == null || GameLoopDriver.Transitioning)) yield return null;
        var me = Me(); Chk("player_ready_again", me != null && ZoneRuntime.Owns && !ZoneGroundHold.Active, me != null ? "player at " + me.transform.position.ToString("F1") : "no player after the reload");
        if (me == null) yield break;
        for (int i = 0; i < C.readyExtraFrames; i++) yield return null;
        if (s.spawn.Length > 0)
        {
            float tw = Time.realtimeSinceStartup; DuelFighter f = null;
            while (Time.realtimeSinceStartup - tw < C.combat.spawnWaitS) { f = FindEnemy(s.spawn); if (f != null) break; yield return null; }
            Chk("spawn_alive_" + s.spawn, f != null && !f.Dead, f != null ? s.spawn + " stands at " + f.transform.position.ToString("F1") + ", hp " + F2(f.health) + "/" + F2(f.maxHealth) : s.spawn + " did not appear in " + F2(C.combat.spawnWaitS) + " s");
        }
        var zz = ZoneData.Zone(s.zone); if (zz != null && zz.limits != null && !float.IsNaN(zz.limits.zMax)) Chk("zone_is_" + s.zone, Mathf.Abs(ArenaLimits.ZoneZMax - zz.limits.zMax) < 0.01f, "ArenaLimits.ZoneZMax " + F2(ArenaLimits.ZoneZMax) + ", zone " + s.zone + " expects " + F2(zz.limits.zMax));
        Chk("player_full_hp", me.maxHealth > 0f && me.health >= me.maxHealth * 0.95f, "hp " + F2(me.health) + "/" + F2(me.maxHealth));
        Vector3 sw; if (SpotWorld(s.zone, s.spot, out sw)) { float d = Flat(me.transform.position, sw); Chk("at_start_spot", d <= 3f, "player is " + F2(d) + " m from " + s.zone + "/" + s.spot); }
        yield return Shot("after");
    }

    // ------------------------------------------------------------------ camera follow accumulator
    void CamSample(CamAcc a, bool force)
    {
        float now = Time.realtimeSinceStartup; if (!force && now < a.next) return; a.next = now + C.combat.camSampleS;
        var me = Me(); var cam = Camera.main; a.n++;
        if (me == null || cam == null) { a.bad++; return; }
        Vector3 cp = cam.transform.position, pp = me.transform.position; bool ok = true;
        if (!Finite(cp)) { a.nonFinite++; ok = false; }
        float d = Flat(cp, pp); if (d > a.maxDist) a.maxDist = d; if (d > C.combat.camMaxDistM) { a.far++; ok = false; }
        var r = Rig();
        if (r != null)
        {
            if (r.target == null || Flat(r.target.position, pp) > 1.5f) { a.wrongTarget++; ok = false; }
            if (r.enemy != null && !r.enemy.gameObject.activeInHierarchy) { a.staleLock++; ok = false; }   // the v107 bug family: the lock camera on an inactive template / a destroyed clone
        }
        if (!ok) a.bad++;
        if (float.IsNaN(a.camZ0)) { a.camZ0 = cp.z; a.plZ0 = pp.z; }
        a.camZ1 = cp.z; a.plZ1 = pp.z;
    }
    void CamChecks(CamAcc a, float minFollowFrac, string tag)
    {
        Chk("camera_follows" + tag, a.n > 0 && a.bad <= Mathf.Max(0, a.n / 20), a.n + " samples, " + a.bad + " bad (far " + a.far + ", wrong target " + a.wrongTarget + ", stale lock " + a.staleLock + ", not finite " + a.nonFinite + "), max camera-player distance " + F2(a.maxDist) + " m (limit " + F2(C.combat.camMaxDistM) + ")");
        float dp = a.plZ1 - a.plZ0, dc = a.camZ1 - a.camZ0;
        if (!float.IsNaN(a.plZ0) && Mathf.Abs(dp) >= 3f) Chk("camera_moved_with_player" + tag, Mathf.Abs(dc) >= minFollowFrac * Mathf.Abs(dp) && Mathf.Sign(dc) == Mathf.Sign(dp), "player z moved " + F2(dp) + " m, camera z moved " + F2(dc) + " m (needs >= " + F2(minFollowFrac) + " of it)");
    }
    void GroundCheck(string tag)
    {
        var me = Me(); if (me == null) { Chk("on_ground" + tag, false, "no player"); return; }
        float gy, d; bool hit = ZoneGround.Probe(me.transform.position + Vector3.up * 0.1f, me, out gy, out d); var cc = me.GetComponent<CharacterController>();
        float dy = me.transform.position.y - gy; bool ok = hit && Mathf.Abs(dy) <= C.nav.groundTolM && !ZoneGroundHold.Active;
        Chk("on_ground" + tag, ok, Roster.PlayerName + " feet y " + F2(me.transform.position.y) + ", ground " + (hit ? "y " + F2(gy) + ", gap " + F2(dy) + " m (limit " + F2(C.nav.groundTolM) + ")" : "NOT FOUND below") + ", isGrounded " + (cc != null ? cc.isGrounded.ToString() : "n/a") + ", hold " + ZoneGroundHold.Active);
    }

    // ------------------------------------------------------------------ walking
    /// <summary>Walks to a point with real keys. Sets walkOk / walkWhy. Optional camera accumulator; optional per-frame hook.</summary>
    IEnumerator Walk(Vector3 target, bool run, float arriveM, CamAcc cs, Action tick)
    {
        walkOk = false; walkWhy = ""; var me = Me(); if (me == null) { walkWhy = "no player"; yield break; }
        float start = Flat(me.transform.position, target), halfShot = start * 0.5f; bool shotHalf = false; Vector3 lastPos = me.transform.position; float lastT = Time.realtimeSinceStartup;
        while (true)
        {
            me = Me(); if (me == null || me.Dead) { StopMove(); walkWhy = me == null ? "player gone" : "player died"; yield break; }
            float d = Flat(me.transform.position, target);
            if (d <= arriveM) { StopMove(); walkOk = true; yield break; }
            if (Duel.Frozen || RTFinisher.Busy) StopMove();
            else
            {
                var ct = CamT(me); Vector3 dir = target - me.transform.position; dir.y = 0f; dir.Normalize();
                Vector3 fwd = ct != null ? Vector3.ProjectOnPlane(ct.forward, Vector3.up).normalized : Vector3.forward, right = ct != null ? Vector3.ProjectOnPlane(ct.right, Vector3.up).normalized : Vector3.right;
                MoveKeys(Vector3.Dot(dir, right), Vector3.Dot(dir, fwd), run && d > C.nav.runBeyondM);
            }
            if (cs != null) CamSample(cs, false); if (tick != null) tick();
            if (!shotHalf && d <= halfShot) { shotHalf = true; yield return Shot("halfway"); }
            if (Time.realtimeSinceStartup - lastT >= C.nav.stuckSeconds)
            {
                if (Flat(me.transform.position, lastPos) < C.nav.stuckMoveM && !Duel.Frozen && !RTFinisher.Busy) { StopMove(); walkWhy = "stuck: moved " + F2(Flat(me.transform.position, lastPos)) + " m in " + F2(C.nav.stuckSeconds) + " s at " + me.transform.position.ToString("F1") + " (focus " + Application.isFocused + ")"; yield break; }
                lastPos = me.transform.position; lastT = Time.realtimeSinceStartup;
            }
            yield return null;
        }
    }

    IEnumerator WalkStep(ZrStep s)
    {
        Vector3 target; if (!SpotWorld(s.toZone, s.toSpot, out target)) { Chk("target_spot", false, "zones.json has no spot with a pos: " + s.toZone + "/" + s.toSpot); yield break; }
        var me = Me(); if (me == null) { Chk("player", false, "no player"); yield break; }
        var cs = new CamAcc(); int mark = LogMark(); CamSample(cs, true); Ev("from " + me.transform.position.ToString("F1") + " to " + s.toZone + "/" + s.toSpot + " " + target.ToString("F1") + ", " + (s.slow ? "walking" : "running"));
        yield return Shot("start");
        yield return Walk(target, !s.slow, C.nav.arriveM, cs, null);
        Chk("arrived", walkOk, walkOk ? "player at " + Me().transform.position.ToString("F1") + " (target " + target.ToString("F1") + ")" : walkWhy);
        var z = ZoneData.Zone(s.toZone); float want = z != null && z.limits != null ? z.limits.zMax : float.NaN;
        if (!float.IsNaN(want))
        {
            float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < 4f && !(Mathf.Abs(ArenaLimits.ZoneZMax - want) < 0.01f)) { CamSample(cs, false); yield return null; }
            Chk("zone_switched", Mathf.Abs(ArenaLimits.ZoneZMax - want) < 0.01f, "ArenaLimits.ZoneZMax = " + F2(ArenaLimits.ZoneZMax) + ", zone " + s.toZone + " expects " + F2(want));
        }
        float td = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - td < s.dwellS) { CamSample(cs, false); yield return null; }
        CamSample(cs, true); yield return Shot("arrived");
        CamChecks(cs, s.camMinFollowFrac, "");
        Chk("no_flash_crossing", !LogHas(C.flashLog, mark), LogHas(C.flashLog, mark) ? "the flash crossing ran (the span did not walk the door): " + LogFirst(C.flashLog, mark) : "no '" + C.flashLog.Trim() + "' line: the door was walked");
        foreach (var b in C.badLogs) if (LogHas(b, mark)) Chk("no_bad_log", false, "'" + b + "' logged: " + LogFirst(b, mark));
        if (s.ground) GroundCheck("");
    }

    // ------------------------------------------------------------------ fighting
    void Fight(DuelFighter me, DuelFighter foe)
    {
        if (Duel.Frozen || RTFinisher.Busy) { StopMove(); SetHeld(kGuard, false); return; }
        float gap = Flat(me.transform.position, foe.transform.position); var K = C.combat; var ct = CamT(me);
        if (gap > K.approachGapM)
        {
            Vector3 dir = foe.transform.position - me.transform.position; dir.y = 0f; dir.Normalize();
            Vector3 fwd = ct != null ? Vector3.ProjectOnPlane(ct.forward, Vector3.up).normalized : Vector3.forward, right = ct != null ? Vector3.ProjectOnPlane(ct.right, Vector3.up).normalized : Vector3.right;
            MoveKeys(Vector3.Dot(dir, right), Vector3.Dot(dir, fwd), gap > C.nav.runBeyondM);
        }
        else StopMove();
        bool wind = foe.state == FState.Windup || foe.state == FState.Active;
        if (K.guard.enabled && wind && gap <= K.guard.gapM && me.stamina > K.staminaMinFrac * me.maxStamina) { SetHeld(kGuard, true); guarding = true; return; }
        if (guarding) { SetHeld(kGuard, false); guarding = false; }
        if (gap <= K.attackGapM && Time.realtimeSinceStartup >= nextStrike && me.stamina >= K.staminaMinFrac * me.maxStamina) { TapKey(kStrike); nextStrike = Time.realtimeSinceStartup + K.strikeIntervalS; }
    }

    IEnumerator KillStep(ZrStep s)
    {
        float t0 = Time.realtimeSinceStartup; DuelFighter tgt = null; bool saw = false; int mark = LogMark();
        float barMax = -1f, barMin = 2f, barLast = -1f, maxDev = 0f, playerDev = 0f; int hudSamples = 0, hudTargetOk = 0, nameOk = 0, nameSamples = 0, hudWrong = 0; string nameSeen = ""; bool shotEngaged = false, shotHalf = false, hitSeen = false;
        var cs = new CamAcc(); CamSample(cs, true); yield return Shot("start");
        while (true)
        {
            var me = Me(); if (me == null || me.Dead) { Chk("player_alive", false, me == null ? "the player object is gone" : "the player died while fighting " + s.spawn + " (assist floor " + F2(C.combat.assistHpFloorFrac) + ")"); yield break; }
            tgt = FindEnemy(s.spawn);
            if (tgt == null)
            {
                if (saw) break;
                Vector3 sp = Vector3.zero; bool walkIn = s.pushZone.Length > 0 && SpotWorld(s.pushZone, s.pushSpot, out sp);
                if (!walkIn && Time.realtimeSinceStartup - t0 > C.combat.spawnWaitS) { Chk("target_spawned", false, s.spawn + " never appeared within " + F2(C.combat.spawnWaitS) + " s"); yield break; }
                if (walkIn && Flat(me.transform.position, sp) > C.nav.arriveM) { Vector3 dd = sp - me.transform.position; dd.y = 0f; dd.Normalize(); var ct0 = CamT(me); Vector3 f0 = ct0 != null ? Vector3.ProjectOnPlane(ct0.forward, Vector3.up).normalized : Vector3.forward, r0 = ct0 != null ? Vector3.ProjectOnPlane(ct0.right, Vector3.up).normalized : Vector3.right; MoveKeys(Vector3.Dot(dd, r0), Vector3.Dot(dd, f0), false); }
                else StopMove();
                CamSample(cs, false); yield return null; continue;
            }
            if (!saw) { saw = true; Ev(s.spawn + " found at " + tgt.transform.position.ToString("F1") + ", hp " + F2(tgt.health) + "/" + F2(tgt.maxHealth) + ", engaged " + Engaged(tgt, me)); }
            if (tgt.Dead) break;
            // the player's real target: the game picks the nearest engaged enemy; fight that one, the step target is what has to die
            var foe = me.opponent != null && !me.opponent.Dead && me.opponent.gameObject.activeInHierarchy && !me.opponent.name.Contains(Roster.PlayerObject) ? me.opponent : tgt;
            Fight(me, foe);
            // HUD: what the boss bar draws
            var h = Hud(); var gl = GameLoop.Enemy;
            if (me.opponent == tgt)
            {
                hudSamples++; if (gl == tgt && h != null && h.ef == tgt && h.enemy != null) hudTargetOk++; else hudWrong++;
                if (gl == tgt) { nameSamples++; string nm = GameLoop.EnemyName(); nameSeen = nm; if (nm != null && s.expectName.Length > 0 && nm.IndexOf(s.expectName, StringComparison.OrdinalIgnoreCase) >= 0) nameOk++; }
                if (h != null && h.enemy != null && h.enemy.max > 0f)
                {
                    float fr = Mathf.Clamp01(h.enemy.current / h.enemy.max), real = tgt.maxHealth > 0f ? Mathf.Clamp01(tgt.health / tgt.maxHealth) : 0f;
                    if (barMax < 0f || fr > barMax) barMax = fr; if (fr < barMin) barMin = fr; barLast = fr; float dv = Mathf.Abs(fr - real); if (dv > maxDev) maxDev = dv;
                    if (!hitSeen && fr < 0.98f) { hitSeen = true; Ev("first hit on " + s.spawn + ": bar " + F2(fr) + ", hp " + F2(tgt.health) + "/" + F2(tgt.maxHealth)); }
                    if (!shotHalf && fr < 0.5f) { shotHalf = true; yield return Shot("bar_below_half"); }
                }
                if (h != null && h.player != null && h.player.max > 0f && me.maxHealth > 0f) { float pd = Mathf.Abs(h.player.current / h.player.max - me.health / me.maxHealth); if (pd > playerDev) playerDev = pd; }
                if (!shotEngaged) { shotEngaged = true; Ev("target on the HUD: name '" + nameSeen + "'"); yield return Shot("engaged"); }
            }
            CamSample(cs, false); yield return null;
        }
        // dead (or removed): let the death play out and look for trouble
        var tdead = tgt;
        for (int i = 0; i < 3; i++) yield return null;   // the HUD reads the Health one frame after the last blow: take the end value from the bar itself
        { var hh = Hud(); var th = tdead != null ? tdead.GetComponent<Health>() : null; if (hh != null && hh.enemy != null && th != null && hh.enemy == th && hh.enemy.max > 0f) { barLast = Mathf.Clamp01(hh.enemy.current / hh.enemy.max); if (barLast < barMin) barMin = barLast; if (barMax < 0f) barMax = barLast; } }
        Ev(s.spawn + " dead after " + F2(Time.realtimeSinceStartup - t0) + " s; bar min " + F2(barMin) + ", assists so far " + assists);
        ReleaseAll(); yield return Shot("dead");
        float ts = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - ts < s.settleS) { CamSample(cs, false); yield return null; }
        float tf = Time.realtimeSinceStartup; var rg = Rig();
        while (Time.realtimeSinceStartup - tf < s.finisherWaitS && (RTFinisher.Busy || Duel.Frozen || (rg != null && rg.Yield))) { CamSample(cs, false); yield return null; }
        CamSample(cs, true); yield return Shot("settled");
        var me2 = Me();
        Chk("target_dead", tdead != null ? tdead.Dead : LogHas("[Zones] kill " + s.spawn, mark), tdead != null ? s.spawn + " state " + tdead.state + ", hp " + F2(tdead.health) : "removed after death");
        Chk("kill_recorded", LogHas("[Zones] kill " + s.spawn, mark), LogHas("[Zones] kill " + s.spawn, mark) ? LogFirst("[Zones] kill " + s.spawn, mark) : "no '[Zones] kill " + s.spawn + "' line (ZoneRuntime.KillWatch did not run)");
        Chk("death_completed", !RTFinisher.Busy && !Duel.Frozen && (rg == null || !rg.Yield), "finisher busy " + RTFinisher.Busy + ", Duel.Frozen " + Duel.Frozen + ", camera yielded " + (rg != null && rg.Yield));
        Chk("player_alive", me2 != null && !me2.Dead, me2 != null ? "player hp " + F2(me2.health) + "/" + F2(me2.maxHealth) : "no player");
        Chk("hud_follows_target", hudSamples > 0 && hudWrong <= Mathf.Max(2, hudSamples / 10), hudSamples + " frames with the player's target on " + s.spawn + ", HUD on the same fighter in " + hudTargetOk + ", not in " + hudWrong);
        if (s.expectName.Length > 0) Chk("hud_name", nameSamples > 0 && nameOk >= nameSamples * 0.9f, "name read '" + nameSeen + "' in " + nameOk + " of " + nameSamples + " frames, expected '" + s.expectName + "'");
        Chk("health_bar_falls", barMax >= 0f && barMax - barMin >= s.minBarDropFrac && barLast <= C.combat.barEndMaxFrac, "boss bar value: highest " + F2(barMax) + ", lowest " + F2(barMin) + ", last " + F2(barLast) + " (needs a fall of >= " + F2(s.minBarDropFrac) + " and an end value <= " + F2(C.combat.barEndMaxFrac) + ")");
        Chk("health_bar_tracks_hp", barMax >= 0f && maxDev <= C.combat.hudMaxDeviation, "largest gap between the bar's Health and the fighter's hp " + F2(maxDev) + " (limit " + F2(C.combat.hudMaxDeviation) + "); the player bar's largest gap " + F2(playerDev));
        CamChecks(cs, 0f, "");
        if (assists > 0) Ev("assist used " + assists + " time(s) up to here (all steps)");
    }

    // ------------------------------------------------------------------ walk past a live enemy
    IEnumerator SkipStep(ZrStep s)
    {
        var me = Me(); if (me == null) { Chk("player", false, "no player"); yield break; }
        Vector3 door, push = Vector3.zero; if (!SpotWorld(s.toZone, s.toSpot, out door)) { Chk("target_spot", false, s.toZone + "/" + s.toSpot + " has no pos"); yield break; }
        bool havePush = s.pushZone.Length > 0 && SpotWorld(s.pushZone, s.pushSpot, out push); if (!havePush) push = door;
        float tw = Time.realtimeSinceStartup; DuelFighter pit = null; while (Time.realtimeSinceStartup - tw < C.combat.spawnWaitS) { pit = FindEnemy(s.spawn); if (pit != null) break; yield return null; }
        Chk("enemy_alive_at_start", pit != null && !pit.Dead, pit != null ? s.spawn + " at " + pit.transform.position.ToString("F1") + ", engaged " + Engaged(pit, me) : s.spawn + " not found");
        if (pit == null) yield break;
        float boundary = DoorBoundary(s.doorGate), giveUp = GiveUpGap(pit); int mark = LogMark();
        Ev("door boundary z " + F2(boundary) + " (zone_span.json " + s.doorGate + "), data give-up gap " + F2(giveUp) + " m (chase.json), walking (no attack, no guard)");
        bool pitEng = false, through = false, both = false, gaveUp = false, shotEng = false, shotThrough = false, shotBoth = false; float maxGap = 0f, throughAt = -1f; string gaveWhy = "";
        var cs = new CamAcc(); CamSample(cs, true); yield return Shot("start");
        Action observe = () =>
        {
            var m = Me(); if (m == null) return; var p = pit; if (p == null) return;
            var sob = s.otherSpawn.Length > 0 ? FindEnemy(s.otherSpawn) : null;
            if (Engaged(p, m)) pitEng = true;
            float gap = Flat(m.transform.position, p.transform.position); if (pitEng && gap > maxGap) maxGap = gap;
            if (pitEng && !float.IsNaN(boundary) && p.transform.position.z > boundary + s.throughMarginM && !through) { through = true; throughAt = Time.realtimeSinceStartup - stepT0; Ev(s.spawn + " is through the door at z " + F2(p.transform.position.z) + " (boundary " + F2(boundary) + "), gap to the player " + F2(gap) + " m"); }
            if (Engaged(p, m) && Engaged(sob, m) && !both) { both = true; Ev("both engaged: " + s.spawn + " and " + s.otherSpawn + " are on the player (maxEngaged " + ZoneData.Get().engage.maxEngaged + ")"); }
            var ca = p.GetComponent<ChaseAgent>(); bool ca_giving = ca != null && ca.GivingUpNow;
            if (!gaveUp && pitEng && ((!float.IsNaN(giveUp) && gap >= giveUp) || ca_giving || LogHas("[Zones] leash: " + s.spawn, mark))) { gaveUp = true; gaveWhy = "gap " + F2(gap) + " m (data give-up " + F2(giveUp) + "), chase agent giving up " + ca_giving + ", leash log " + LogHas("[Zones] leash: " + s.spawn, mark); Ev(s.spawn + " gave up: " + gaveWhy); }
        };
        // 1) walk the door, Pitborn on the player's heels
        yield return Walk(door, !s.slow, C.nav.arriveM, cs, observe);
        Chk("arrived_door", walkOk, walkOk ? "player at " + Me().transform.position.ToString("F1") : walkWhy);
        yield return Shot("through_door");
        // 2) push on toward the second enemy until it engages (or the data standoff gap)
        float tp = Time.realtimeSinceStartup;
        while (havePush && Time.realtimeSinceStartup - tp < 40f)
        {
            me = Me(); if (me == null || me.Dead) break; var sob = s.otherSpawn.Length > 0 ? FindEnemy(s.otherSpawn) : null;
            if (sob != null && (Engaged(sob, me) || Flat(me.transform.position, sob.transform.position) <= s.pushUntilGapM)) break;
            Vector3 dir = push - me.transform.position; dir.y = 0f; if (dir.magnitude < 1f) break; dir.Normalize(); var ct = CamT(me);
            Vector3 fwd = ct != null ? Vector3.ProjectOnPlane(ct.forward, Vector3.up).normalized : Vector3.forward, right = ct != null ? Vector3.ProjectOnPlane(ct.right, Vector3.up).normalized : Vector3.right;
            MoveKeys(Vector3.Dot(dir, right), Vector3.Dot(dir, fwd), false); observe(); CamSample(cs, false); yield return null;
        }
        StopMove();
        // 3) stand and watch: both on the player, or the chaser gives up
        float tf = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - tf < s.followWaitS && !both && !gaveUp) { observe(); CamSample(cs, false); if (!shotEng && pitEng) { shotEng = true; yield return Shot("chased"); } yield return null; }
        observe(); CamSample(cs, true);
        if (through && !shotThrough) { shotThrough = true; yield return Shot("chaser_through"); }
        if (both && !shotBoth) { shotBoth = true; yield return Shot("both_engaged"); }
        Chk("enemy_engaged_player", pitEng, pitEng ? s.spawn + " engaged the walking player" : s.spawn + " never engaged (aggro radius never reached?)");
        Chk("chased_through_door", through, through ? s.spawn + " crossed the door at " + F2(throughAt) + " s (physically, not teleported home)" : "never beyond z " + F2(boundary + s.throughMarginM) + (pit != null ? ", last at " + pit.transform.position.ToString("F1") : ", object gone"));
        Chk("fights_alongside_or_gives_up", both || gaveUp, both ? "both enemies engaged at once" : gaveUp ? "gave up: " + gaveWhy : "neither within " + F2(s.followWaitS) + " s: " + s.spawn + " engaged " + pitEng + ", max gap " + F2(maxGap) + " m");
        CamChecks(cs, s.camMinFollowFrac, "");
        Chk("no_flash_crossing", !LogHas(C.flashLog, mark), LogHas(C.flashLog, mark) ? LogFirst(C.flashLog, mark) : "door walked, no flash");
        foreach (var b in C.badLogs) if (LogHas(b, mark)) Chk("no_bad_log", false, "'" + b + "' logged: " + LogFirst(b, mark));
    }

    // ------------------------------------------------------------------ the trader
    ShopController Shop()
    {
        var ui = MarketUI.Instance; if (ui == null) return null;
        var f = typeof(MarketUI).GetField("shop", BindingFlags.Instance | BindingFlags.NonPublic); return f == null ? null : f.GetValue(ui) as ShopController;
    }
    string ShopLine() { var sc = Shop(); if (sc == null) return "shop n/a"; var l = sc.Selected; return (sc.BuyTab ? "BUY" : "SELL") + " tab, " + sc.Stock.lines.Count + " lines, selected " + (l != null ? l.name + " price " + l.price + " canSell " + l.canSell + (l.why.Length > 0 ? " why " + l.why : "") : "none") + ", armed '" + sc.Armed + "', note " + sc.NoteKey; }
    IEnumerator TapWait(KeyCode k, int extra) { TapKey(k); int n = Mathf.Max(2, C.input.tapFrames) + C.input.gapFrames + extra; for (int i = 0; i < n; i++) yield return null; }

    IEnumerator TradeStep(ZrStep s)
    {
        var mc = MarketCfg.Get(); Chk("market_enabled", mc.enabled, mc.enabled ? "market.json on" : "market.json off: start with -zones");
        if (!mc.enabled) yield break;
        var me = Me(); if (me == null) { Chk("player", false, "no player"); yield break; }
        float sx, sz; MarketWorld.StallXZ(mc.stall, out sx, out sz); var stall = new Vector3(sx, me.transform.position.y, sz); var cs = new CamAcc(); CamSample(cs, true);
        KeyCode kInt = FirstKb(MarketCfg.Keys(mc.keys.interact), KeyCode.E), kOk = FirstKb(MarketCfg.Keys(mc.keys.confirm), KeyCode.E), kEsc = FirstKb(MarketCfg.Keys(mc.keys.close), KeyCode.Escape), kDn = FirstKb(MarketCfg.Keys(mc.keys.down), KeyCode.S);
        KeyCode kTab = KeyCode.None; foreach (var n in MarketBuyRules.Cur.tabKeys) { var k = Parse(n, KeyCode.None); if (Kb(k)) { kTab = k; break; } }
        Ev("stall at " + stall.ToString("F1") + " radius " + F2(mc.stall.radius) + "; keys interact " + kInt + " confirm " + kOk + " close " + kEsc + " down " + kDn + " tab " + kTab);
        yield return Shot("start");
        // walk to the stall until the game says it is in reach
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < 60f && !MarketUI.InReach)
        {
            me = Me(); if (me == null || me.Dead) break; Vector3 dir = stall - me.transform.position; dir.y = 0f; float d = dir.magnitude; dir.Normalize(); var ct = CamT(me);
            Vector3 fwd = ct != null ? Vector3.ProjectOnPlane(ct.forward, Vector3.up).normalized : Vector3.forward, right = ct != null ? Vector3.ProjectOnPlane(ct.right, Vector3.up).normalized : Vector3.right;
            if (d > mc.stall.radius * 0.5f) MoveKeys(Vector3.Dot(dir, right), Vector3.Dot(dir, fwd), false); else StopMove();
            CamSample(cs, false); yield return null;
        }
        StopMove(); for (int i = 0; i < 6; i++) yield return null;
        me = Me(); Chk("reached_stall", MarketUI.InReach, "MarketUI.InReach " + MarketUI.InReach + ", player " + (me != null ? me.transform.position.ToString("F1") : "none") + ", stall distance " + (me != null ? F2(Flat(me.transform.position, stall)) : "?") + " m (radius " + F2(mc.stall.radius) + ")");
        if (!MarketUI.InReach) yield break;
        if (s.ground) GroundCheck("_at_stall");
        int coins0 = LootSave.Coins, owned0 = LootSave.Owned.Count;
        for (int tries = 0; tries < 4 && !MarketUI.Open; tries++) yield return TapWait(kInt, s.tapSettleFrames);
        Chk("trader_opens", MarketUI.Open, "MarketUI.Open " + MarketUI.Open + "; " + ShopLine() + "; Duel.Frozen " + Duel.Frozen);
        if (!MarketUI.Open) yield break;
        yield return Shot("open");
        // BUY: Tab switches to the buy side, confirm arms, confirm buys
        Chk("buy_side_on", MarketBuyRules.Cur.enabled && kTab != KeyCode.None, MarketBuyRules.Cur.enabled ? "hub.json stall.buy on, tab key " + kTab : "hub.json stall.buy is off: no Buy tab");
        if (MarketBuyRules.Cur.enabled && kTab != KeyCode.None)
        {
            yield return TapWait(kTab, s.tapSettleFrames); var sc = Shop(); Chk("buy_tab_shown", sc != null && sc.BuyTab, ShopLine()); yield return Shot("buy_tab");
            int c1 = LootSave.Coins, o1 = LootSave.Owned.Count; string line = ShopLine();
            yield return TapWait(kOk, s.tapSettleFrames); string armed = ShopLine(); yield return TapWait(kOk, s.tapSettleFrames);
            int c2 = LootSave.Coins, o2 = LootSave.Owned.Count;
            Chk("bought", c2 < c1 && o2 == o1 + 1, "coins " + c1 + " -> " + c2 + ", carried items " + o1 + " -> " + o2 + "; before: " + line + "; armed: " + armed + "; after: " + ShopLine());
            yield return Shot("bought");
            // SELL: back to the sell tab, find a line that can be sold, confirm twice
            yield return TapWait(kTab, s.tapSettleFrames); sc = Shop(); Chk("sell_tab_shown", sc != null && !sc.BuyTab, ShopLine());
            int presses = 0; while (presses < s.maxDownPresses) { sc = Shop(); if (sc != null && sc.Selected != null && sc.Selected.canSell) break; yield return TapWait(kDn, 0); presses++; }
            sc = Shop(); Chk("sellable_line_found", sc != null && sc.Selected != null && sc.Selected.canSell, ShopLine() + " after " + presses + " Down presses");
            int c3 = LootSave.Coins, o3 = LootSave.Owned.Count; yield return TapWait(kOk, s.tapSettleFrames); yield return TapWait(kOk, s.tapSettleFrames); int c4 = LootSave.Coins, o4 = LootSave.Owned.Count;
            Chk("sold", c4 > c3 && o4 == o3 - 1, "coins " + c3 + " -> " + c4 + ", carried items " + o3 + " -> " + o4 + "; " + ShopLine());
            yield return Shot("sold");
            Chk("coins_changed", c2 != coins0 && c4 != c2, "coins at the start " + coins0 + ", after buying " + c2 + ", after selling " + c4);
        }
        // CLOSE
        for (int i = 0; i < s.closeTries && MarketUI.Open; i++) yield return TapWait(kEsc, s.tapSettleFrames);
        Chk("trader_closes", !MarketUI.Open, "MarketUI.Open " + MarketUI.Open + " after " + s.closeTries + " close tries");
        for (int i = 0; i < 12; i++) yield return null;
        Chk("controls_returned", !Duel.Frozen, "Duel.Frozen " + Duel.Frozen + " 12 frames after the panel closed");
        yield return Shot("closed");
        if (s.ground) GroundCheck("_after");
        CamChecks(cs, 0f, "");
    }
}
