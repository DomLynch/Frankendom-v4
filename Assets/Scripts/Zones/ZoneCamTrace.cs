using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

// CAMZONE v1 (laneCAMZONE, UNPROVEN in Unity): -camtrace:<dir>. A PASSIVE recorder for the zone camera: it reads the game, it moves nothing, it is not a harness flag (nothing in GameLoop.HarnessOn, MenuCfg.Harness,
// PerfFlags.HarnessRun, RuntimeQuality.IsHarness, menu.json harnessArgs matches "-camtrace:"; it never sets Time.captureFramerate), so it runs together with -zones, -zonexing-force, -routebot and -camscene.
// Without the flag nothing exists: Boot() returns on its first test, camtrace.json is never read.
//  <dir>\camtrace.csv     one row per rendered frame (LateUpdate after CameraRig): time, frame, frame ms, zone, scene step, Joan (pos, speed), camera (pos, look, pitch, yaw, fov, height above Joan, lock weight, lock flag,
//                         yield, finisher), the camera target (name, gap, state, chase state, engaged), the pose numbers of the last lock frame (gap used, base height, back, collision hit / wanted distance), and for up to
//                         csv.engagedSlots enemies fighting Joan (or giving up): name, state, gap, brain on/off, chase mode. No allocation per row (own number formatter, one StringBuilder, a flush every csv.flushEveryS).
//  <dir>\frames\f_NNNNNN.jpg|png   game-only pictures at frames.hz: ScreenCapture.CaptureScreenshotAsTexture at the end of the frame (the game's own output, never a desktop or window grab), scaled on the GPU to
//                         frames.width (0 = native, e.g. 2560x1600) and written upright. Orientation is MEASURED, not assumed: for frames.calibFrames frames the recorder draws a marker in the top-left corner of the game
//                         screen, looks for it in the captured texture, and flips the output if it arrived at the bottom (frames.flipY is only the fallback when the marker is not seen). The marker never reaches a saved frame.
//                         The JPG/PNG encode runs on the main thread (measured per capture: column capMs, summary line); the file write runs on a thread-pool thread.
//  <dir>\frames.csv       index, time (same clock as camtrace.csv), file.   <dir>\camtrace_meta.json   resolution, orientation result, build, command line.   <dir>\camtrace_data.json   the data used.
// Convert: python -I _agents\laneCAMZONE\frames_to_mp4.py <dir>   (ffmpeg, real-time timing from frames.csv).   Judge: python -I _agents\laneCAMZONE\check_camtrace.py <dir>   (the pass rule is camtrace.json passRule).
[Serializable] public class CtCsv { public string name = "camtrace.csv"; public float flushEveryS = 2f, scanEveryS = 0.1f; public int decimals = 2, engagedSlots = 3; }
[Serializable] public class CtFrames
{
    public bool enabled = true, flipY = false, autoOrient = true, whileEngagedOnly = false;
    public float hz = 6f, startAfterS = 0f; public string format = "jpg", mode = "texture", folder = "frames";
    public int jpgQuality = 88, width = 1600, maxFrames = 6000, calibFrames = 4, markerPx = 48, maxPending = 24; public float[] markerRgb = new float[] { 1f, 0f, 1f };
}
[Serializable] public class CtPass
{
    public int engagedMin = 2; public float lockMinW = 0.5f, maxCamHeightM = 6f, maxPitchDeg = 26f, maxLockedGapM = 9.5f, lockNotFightingS = 0.5f, frozenWindowS = 1f, frozenPlayerMoveM = 3f, frozenCamMoveM = 0.15f, yawRateMaxDegPerS = 200f, yawRateWindowS = 0.1f, hitchMs = 250f;
}
[Serializable] public class CtStep
{
    public string id = "", kind = "", zone = "", spot = "", name = ""; public float[] pos = new float[0]; public bool run = true;
    public float seconds, timeoutS = 60f, arriveM = 1.5f, untilGapM; public int untilEngaged; public bool cycle; public string[] revive = new string[0];
    public string startZone = "", startSpot = "", boss = ""; public float killAfter, killWaitS = 120f, gapS = 1.5f; public int count = 1;   // CODE5 v1: startZone/startSpot = begin at a zone spot (copy save); boss+killAfter = kill that spawn N s after it engages; kind forceDeath: count deaths of Joan
    public string stall = "", action = "", item = "";   // CLOSE5B v1: kind trade (stall id of market.json, action sell|buy, item gear id, '' = first sellable / buyable line), kind equip (item, '' = last picked up / bought; action unequip), kind pickup (item, '' = nearest)
}
[Serializable] public class CtScene
{
    public bool enabled = true, quit = true; public float globalTimeoutS = 240f, startWaitS = 90f, hpFloorFrac = 0.5f, approachGapM = 2.2f, attackGapM = 2.6f, strikeIntervalS = 0.55f, staminaMinFrac = 0.2f; public int readyExtraFrames = 30;
    public float stuckS = 60f, reviveWaitS = 40f; public bool evtLog = true; public string startZone = "", startSpot = "";   // CODE5 v1: STUCK watchdog (0 = off), forceDeath revive wait, [CamScene-EVT] lines, scene start spot
    public CtStep[] steps = new CtStep[0];
}
[Serializable] public class CtCfg { public bool enabled = true; public CtCsv csv = new CtCsv(); public CtFrames frames = new CtFrames(); public CtPass passRule = new CtPass(); public CtScene scene = new CtScene(); }
[Serializable] public class CtMeta { public string tool = "ZoneCamTrace v1 (laneCAMZONE)", unity = "", build = "", commandLine = "", startedUtc = "", orientation = "", note = ""; public int screenW, screenH, frameW, frameH; public bool flipApplied; public float lumaSrc, lumaOut; }

[DefaultExecutionOrder(2000)]
public sealed class ZoneCamTrace : MonoBehaviour
{
    public static string Step = "";                 // set by ZoneCamScene (a column of the csv)
    public static int EngagedNow; public static float NearestGap = 99f; public static bool Running { get { return inst != null; } }
    public static CtCfg Cfg { get { return inst != null ? inst.C : null; } }
    static ZoneCamTrace inst; static string dir;

    sealed class En { public DuelFighter df; public AIBrain ai; public ChaseAgent ch; public string name = ""; }
    CtCfg C; StreamWriter csv, idx; readonly StringBuilder sb = new StringBuilder(1 << 16), sbIdx = new StringBuilder(1 << 12);
    float t0, nextFlush, nextScan, nextCap, capMsPrev, capMsSum, capMsMax; int rows, capCount, capPending, capSkipped, calibLeft, calibUp, calibDown, errors; bool calibrating, closed, flip, orientKnown, capBusy;
    CameraRig rig; Camera cam; DuelFighter player; Transform camT; float nextBind; Vector3 lastP; bool hasLastP; float lastMs;
    readonly List<En> en = new List<En>(); readonly Dictionary<UnityEngine.Object, string> names = new Dictionary<UnityEngine.Object, string>(); readonly Dictionary<DuelFighter, En> byId = new Dictionary<DuelFighter, En>();
    static readonly string[] stNames = Enum.GetNames(typeof(FState)), chNames = Enum.GetNames(typeof(ChaseMode)); static FieldInfo zoneField; static bool zoneFieldTried;
    static readonly WaitForEndOfFrame eof = new WaitForEndOfFrame();
    Texture2D outTex; CtMeta meta = new CtMeta();

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; Step = ""; EngagedNow = 0; NearestGap = 99f; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        dir = Arg("-camtrace:"); if (string.IsNullOrEmpty(dir) || inst != null) return;
        var go = new GameObject("ZoneCamTrace"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneCamTrace>();
    }

    void Start()
    {
        C = new CtCfg(); var t = HudCfg.ReadData("camtrace.json");
        if (t != null) { try { var k = JsonUtility.FromJson<CtCfg>(t); if (k != null) C = k; } catch (Exception e) { Debug.LogWarning("[CamTrace] camtrace.json unreadable: " + e.Message); } }
        if (C.csv == null) C.csv = new CtCsv(); if (C.frames == null) C.frames = new CtFrames(); if (C.passRule == null) C.passRule = new CtPass(); if (C.scene == null) C.scene = new CtScene();
        if (!C.enabled) { Debug.Log("[CamTrace] camtrace.json enabled=false: off"); Destroy(gameObject); return; }
        try
        {
            Directory.CreateDirectory(dir); if (C.frames.enabled) Directory.CreateDirectory(Path.Combine(dir, C.frames.folder));
            csv = new StreamWriter(Path.Combine(dir, C.csv.name), false, new UTF8Encoding(false)); idx = new StreamWriter(Path.Combine(dir, "frames.csv"), false, new UTF8Encoding(false)); idx.WriteLine("idx,t,file");
            if (t != null) File.WriteAllText(Path.Combine(dir, "camtrace_data.json"), t);
        }
        catch (Exception e) { Debug.LogWarning("[CamTrace] cannot write to " + dir + ": " + e.Message); Destroy(gameObject); return; }
        t0 = Time.unscaledTime; calibLeft = C.frames.enabled && C.frames.autoOrient ? Mathf.Max(1, C.frames.calibFrames) : 0; flip = C.frames.flipY; orientKnown = calibLeft == 0;
        meta.unity = Application.unityVersion; meta.build = Application.version + " " + Application.platform; meta.commandLine = string.Join(" ", Environment.GetCommandLineArgs()); meta.startedUtc = DateTime.UtcNow.ToString("o"); meta.screenW = Screen.width; meta.screenH = Screen.height;
        Header(); Debug.Log("[CamTrace] on: " + dir + " (csv every frame, frames " + (C.frames.enabled ? C.frames.hz.ToString("F1") + " Hz " + C.frames.format + " width " + C.frames.width : "off") + ", screen " + Screen.width + "x" + Screen.height + ")");
    }

    // ---- number formatting without allocation ------------------------------------------------------------------------
    static readonly long[] P10 = { 1, 10, 100, 1000, 10000, 100000 };
    static void I(StringBuilder s, long v) { if (v < 0) { s.Append('-'); v = -v; } if (v == 0) { s.Append('0'); return; } long p = 1; while (p <= v / 10) p *= 10; for (; p >= 1; p /= 10) s.Append((char)('0' + (int)((v / p) % 10))); }
    void F(StringBuilder s, float v, int dec)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) { s.Append("nan"); return; }
        if (dec < 0) dec = 0; if (dec > 5) dec = 5; bool neg = v < 0f; if (neg) v = -v; long m = P10[dec]; double scaled = (double)v * m + 0.5; if (scaled > 9e15) { s.Append("big"); return; }
        long x = (long)scaled; if (neg && x != 0) s.Append('-'); I(s, x / m);
        if (dec > 0) { s.Append('.'); long fr = x % m; for (long p = m / 10; p >= 1; p /= 10) s.Append((char)('0' + (int)((fr / p) % 10))); }
    }
    void V(StringBuilder s, Vector3 v, int d) { F(s, v.x, d); s.Append(','); F(s, v.y, d); s.Append(','); F(s, v.z, d); }
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }
    string Nm(UnityEngine.Object o) { if (o == null) return "-"; string n; if (!names.TryGetValue(o, out n)) { n = o.name.Replace(",", "_").Replace(" (Clone)", "").Replace("(Clone)", ""); names[o] = n; } return n; }
    public static string Zone()
    {
        var zr = ZoneRuntime.Inst; if (zr == null) return "-";
        if (!zoneFieldTried) { zoneFieldTried = true; try { zoneField = typeof(ZoneRuntime).GetField("zone", BindingFlags.Instance | BindingFlags.NonPublic); } catch (Exception) { } }
        if (zoneField == null) return "?"; var z = zoneField.GetValue(zr) as ZZone; return z != null ? z.id : "-";
    }

    void Header()
    {
        sb.Append("t,frame,ms,zone,step,px,py,pz,spd,cx,cy,cz,lx,ly,lz,pitch,yaw,fov,hAbove,lockW,lockEn,lockOn,yield,fin,tgt,tgtGap,tgtState,tgtChase,tgtEng,dPose,baseH,baseBack,colHit,colWant,camDist,nEng");
        for (int i = 1; i <= C.csv.engagedSlots; i++) sb.Append(",e").Append(i).Append(",e").Append(i).Append("State,e").Append(i).Append("Gap,e").Append(i).Append("Brain,e").Append(i).Append("Chase");
        sb.Append(",capPrev,capMsPrev"); for (int i = 1; i <= C.csv.engagedSlots; i++) sb.Append(",e").Append(i).Append("x,e").Append(i).Append("z"); sb.Append(",eeGap,eeAng\n");   // FOESEP v1: enemy x,z + enemy-to-enemy gap + angle at Joan
    }

    // ---- binding + the engaged list ----------------------------------------------------------------------------------
    void Bind()
    {
        if (player != null && rig != null && cam != null) return;
        if (Time.unscaledTime < nextBind) return; nextBind = Time.unscaledTime + 0.5f;
        if (player == null) { player = GameLoop.Player; if (player == null) foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { player = f; break; } }
        if (rig == null) rig = FindFirstObjectByType<CameraRig>(); if (cam == null) cam = Camera.main; camT = cam != null ? cam.transform : null; hasLastP = false;
    }
    void Scan()
    {
        if (Time.unscaledTime < nextScan) return; nextScan = Time.unscaledTime + Mathf.Max(0.02f, C.csv.scanEveryS);
        en.Clear(); if (player == null) return; Vector3 pp = player.transform.position;
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (f == null || f == player || !f.gameObject.activeInHierarchy) continue;
            En e; if (!byId.TryGetValue(f, out e) || e.df == null) { e = new En { df = f, ai = f.GetComponent<AIBrain>(), ch = f.GetComponent<ChaseAgent>(), name = Nm(f) }; byId[f] = e; }
            bool engaged = f.opponent == player, giving = e.ch != null && e.ch.GivingUpNow; if (!engaged && !giving) continue;
            float g = Flat(pp, f.transform.position); int at = en.Count; for (int i = 0; i < en.Count; i++) if (g < Flat(pp, en[i].df.transform.position)) { at = i; break; } en.Insert(at, e);
        }
    }

    void LateUpdate()
    {
        if (csv == null || closed) return;
        try { Record(); } catch (Exception e) { errors++; if (errors <= 3) Debug.LogWarning("[CamTrace] row failed: " + e.Message); if (errors > 20) { Debug.LogWarning("[CamTrace] too many errors, recorder stopped"); Close(); } }
    }

    void FoeCols(Vector3 pp)   // FOESEP v1: per slot x,z of the enemy, then the distance between the first two enemies and the angle they make at Joan
    {
        int dec = C.csv.decimals;
        for (int i = 0; i < C.csv.engagedSlots; i++) { sb.Append(','); if (i < en.Count && en[i].df != null) { Vector3 p = en[i].df.transform.position; F(sb, p.x, dec); sb.Append(','); F(sb, p.z, dec); } else sb.Append("0,0"); }
        float gap = 0f, ang = 0f; if (en.Count >= 2 && en[0].df != null && en[1].df != null) { Vector3 a = en[0].df.transform.position, b = en[1].df.transform.position; gap = FoeSep.PairGap(a, b); ang = FoeSep.PairAngle(a, b, pp); }
        sb.Append(','); F(sb, gap, dec); sb.Append(','); F(sb, ang, 1);
    }
    void Record()
    {
        Bind(); Scan(); float now = Time.unscaledTime; float ms = Time.unscaledDeltaTime * 1000f; int dec = C.csv.decimals;
        Vector3 pp = player != null ? player.transform.position : Vector3.zero, cp = camT != null ? camT.position : Vector3.zero;
        float spd = 0f; if (player != null) { if (hasLastP && ms > 0.01f) spd = Flat(pp, lastP) / (ms * 0.001f); lastP = pp; hasLastP = true; }
        int nEng = 0; float near = 99f; for (int i = 0; i < en.Count; i++) { var e = en[i]; if (e.df == null || e.df.Dead || e.df.opponent != player) continue; nEng++; float g = Flat(pp, e.df.transform.position); if (g < near) near = g; }
        EngagedNow = nEng; NearestGap = near;
        float pitch = 0f, yaw = 0f; if (camT != null) { pitch = camT.eulerAngles.x; if (pitch > 180f) pitch -= 360f; yaw = camT.eulerAngles.y; }
        var tgt = rig != null ? rig.enemy : null; DuelFighter tdf = tgt != null ? tgt.GetComponent<DuelFighter>() : null; ChaseAgent tch = tgt != null ? tgt.GetComponent<ChaseAgent>() : null;
        F(sb, now - t0, 3); sb.Append(','); I(sb, Time.frameCount); sb.Append(','); F(sb, ms, 2); sb.Append(',').Append(Zone()).Append(',').Append(Step.Length == 0 ? "-" : Step).Append(',');
        V(sb, pp, dec); sb.Append(','); F(sb, spd, 2); sb.Append(','); V(sb, cp, dec); sb.Append(',');
        V(sb, rig != null ? rig.LookAt : Vector3.zero, dec); sb.Append(','); F(sb, pitch, 1); sb.Append(','); F(sb, yaw, 1); sb.Append(','); F(sb, cam != null ? cam.fieldOfView : 0f, 1); sb.Append(','); F(sb, cp.y - pp.y, dec); sb.Append(',');
        F(sb, rig != null ? rig.LockW : 0f, 2); sb.Append(',').Append(rig != null && rig.lockEnabled ? '1' : '0').Append(',').Append(rig != null && rig.dbgLockOn ? '1' : '0').Append(',').Append(rig != null && rig.Yield ? '1' : '0').Append(',').Append(RTFinisher.Busy ? '1' : '0').Append(',');
        sb.Append(Nm(tgt)).Append(','); F(sb, tgt != null ? Flat(pp, tgt.position) : 0f, dec); sb.Append(',').Append(tdf != null ? stNames[(int)tdf.state] : "-").Append(',').Append(tch != null ? chNames[(int)tch.Mode] : "-").Append(',').Append(tdf != null && tdf.opponent == player ? '1' : '0').Append(',');
        F(sb, rig != null ? rig.dbgGap : 0f, dec); sb.Append(','); F(sb, rig != null ? rig.dbgBaseH : 0f, dec); sb.Append(','); F(sb, rig != null ? rig.dbgBack : 0f, dec); sb.Append(',').Append(rig != null && rig.dbgColHit ? '1' : '0').Append(',');
        F(sb, rig != null ? rig.dbgColWanted : 0f, dec); sb.Append(','); F(sb, rig != null && camT != null ? Vector3.Distance(cp, rig.LookAt) : 0f, dec); sb.Append(','); I(sb, nEng);
        for (int i = 0; i < C.csv.engagedSlots; i++)
        {
            if (i < en.Count && en[i].df != null) { var e = en[i]; sb.Append(',').Append(e.name).Append(',').Append(stNames[(int)e.df.state]).Append(','); F(sb, Flat(pp, e.df.transform.position), dec); sb.Append(',').Append(e.ai != null && e.ai.enabled ? '1' : '0').Append(',').Append(e.ch != null ? chNames[(int)e.ch.Mode] : "-"); }
            else sb.Append(",-,-,0,0,-");
        }
        sb.Append(',').Append(capMsPrev > 0f ? '1' : '0').Append(','); F(sb, capMsPrev, 1); capMsPrev = 0f; FoeCols(pp); sb.Append('\n'); rows++;   /* FOESEP v1 */ lastMs = ms;
        if (now >= nextFlush) { nextFlush = now + Mathf.Max(0.5f, C.csv.flushEveryS); Flush(); }
        if (C.frames.enabled && !capBusy && now - t0 >= C.frames.startAfterS && (calibLeft > 0 || now >= nextCap) && capCount < C.frames.maxFrames && (calibLeft > 0 || !C.frames.whileEngagedOnly || nEng > 0))
        {
            if (calibLeft == 0 && capPending >= C.frames.maxPending) { capSkipped++; nextCap = now + 1f / Mathf.Max(0.1f, C.frames.hz); }
            else { capBusy = true; calibrating = calibLeft > 0; StartCoroutine(Capture(now - t0)); }
        }
    }

    void Flush()
    {
        try { if (sb.Length > 0) { csv.Write(sb.ToString()); sb.Length = 0; } csv.Flush(); if (sbIdx.Length > 0) { idx.Write(sbIdx.ToString()); sbIdx.Length = 0; } idx.Flush(); } catch (Exception e) { Debug.LogWarning("[CamTrace] flush failed: " + e.Message); }
    }

    // ---- game-only frames --------------------------------------------------------------------------------------------
    void OnGUI()
    {
        if (!calibrating || Event.current.type != EventType.Repaint) return;
        var c = C.frames.markerRgb; var ZK = ZoneUiCfg.Get().camTrace; var fb = ZK.fallbackRgb; GUI.depth = ZK.depth; var old = GUI.color; GUI.color = new Color(c.Length > 0 ? c[0] : fb[0], c.Length > 1 ? c[1] : fb[1], c.Length > 2 ? c[2] : fb[2], ZK.alpha);   // UIDATA_C v1
        GUI.DrawTexture(new Rect(0f, 0f, C.frames.markerPx, C.frames.markerPx), Texture2D.whiteTexture); GUI.color = old;
    }
    bool IsMarker(Color p) { var c = C.frames.markerRgb; var ZK = ZoneUiCfg.Get().camTrace; var fb = ZK.fallbackRgb; float r = c.Length > 0 ? c[0] : fb[0], g = c.Length > 1 ? c[1] : fb[1], b = c.Length > 2 ? c[2] : fb[2]; return Mathf.Abs(p.r - r) < ZK.markerTol && Mathf.Abs(p.g - g) < ZK.markerTol && Mathf.Abs(p.b - b) < ZK.markerTol; }   // UIDATA_C v1
    static float Luma(Texture2D t) { float s = 0f; int n = 0; for (int y = 4; y < t.height; y += Mathf.Max(1, t.height / 8)) for (int x = 4; x < t.width; x += Mathf.Max(1, t.width / 8)) { var c = t.GetPixel(x, y); s += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; n++; } return n > 0 ? s / n : 0f; }

    IEnumerator Capture(float tGame)
    {
        yield return eof;
        try { CaptureNow(tGame); }
        catch (Exception e) { errors++; if (errors <= 3) Debug.LogWarning("[CamTrace] frame failed: " + e.Message); nextCap = Time.unscaledTime + 1f; }
        finally { capBusy = false; calibrating = calibLeft > 0 && calibrating; }
    }

    void CaptureNow(float tGame)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); Texture2D src = null;
        try { src = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Debug.LogWarning("[CamTrace] capture failed: " + e.Message); }
        if (src == null) { calibrating = false; calibLeft = 0; nextCap = Time.unscaledTime + 1f; return; }
        if (calibLeft > 0)
        {
            int px = Mathf.Min(C.frames.markerPx, Mathf.Min(src.width, src.height)) / 2; bool up = IsMarker(src.GetPixel(px, src.height - px)), dn = IsMarker(src.GetPixel(px, px));
            if (up && !dn) calibUp++; else if (dn && !up) calibDown++;
            calibLeft--; calibrating = calibLeft > 0;
            if (calibLeft == 0)
            {
                string o = calibUp > calibDown ? "upright" : calibDown > calibUp ? "flipped" : "unknown"; flip = o == "flipped" || (o == "unknown" && C.frames.flipY); orientKnown = true;
                meta.orientation = o; meta.flipApplied = flip; meta.frameW = src.width; meta.frameH = src.height; meta.lumaSrc = Luma(src);
                Debug.Log("[CamTrace] orientation: marker seen " + (o == "upright" ? "at the TOP of the texture (upright)" : o == "flipped" ? "at the BOTTOM of the texture (flipped, the output is turned over)" : "nowhere (unknown, frames.flipY = " + C.frames.flipY + ")") + ", capture " + src.width + "x" + src.height);
                try { File.WriteAllText(Path.Combine(dir, "camtrace_meta.json"), JsonUtility.ToJson(meta, true)); } catch (Exception) { }
            }
            Destroy(src); nextCap = Time.unscaledTime; return;   // calibration frames are never saved
        }
        int w = C.frames.width > 0 ? Mathf.Min(C.frames.width, src.width) : src.width; w &= ~1; int h = Mathf.Max(2, Mathf.RoundToInt(w * (float)src.height / src.width)) & ~1;
        Texture2D tex = src;
        if (w != src.width || h != src.height || flip)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            if (flip) Graphics.Blit(src, rt, new Vector2(1f, -1f), new Vector2(0f, 1f)); else Graphics.Blit(src, rt);
            if (outTex == null || outTex.width != w || outTex.height != h) { if (outTex != null) Destroy(outTex); outTex = new Texture2D(w, h, TextureFormat.RGB24, false); }
            var prev = RenderTexture.active; RenderTexture.active = rt; outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); tex = outTex;
        }
        bool png = string.Equals(C.frames.format, "png", StringComparison.OrdinalIgnoreCase);
        byte[] bytes = png ? tex.EncodeToPNG() : tex.EncodeToJPG(Mathf.Clamp(C.frames.jpgQuality, 1, 100));
        if (meta.lumaOut == 0f) { meta.lumaOut = Luma(tex); Debug.Log("[CamTrace] picture mean luma source " + meta.lumaSrc.ToString("F3") + " -> written " + meta.lumaOut.ToString("F3") + " (a large difference = a colour change in the scaled path)"); try { File.WriteAllText(Path.Combine(dir, "camtrace_meta.json"), JsonUtility.ToJson(meta, true)); } catch (Exception) { } }
        Destroy(src);
        string name = "f_" + capCount.ToString("D6") + (png ? ".png" : ".jpg"), path = Path.Combine(Path.Combine(dir, C.frames.folder), name);
        sbIdx.Append(capCount).Append(',').Append(tGame.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(C.frames.folder).Append('/').Append(name).Append("\n");
        Interlocked.Increment(ref capPending); ThreadPool.QueueUserWorkItem(_ => { try { File.WriteAllBytes(path, bytes); } catch (Exception) { } finally { Interlocked.Decrement(ref capPending); } });
        capCount++; float cms = (float)sw.Elapsed.TotalMilliseconds; capMsPrev = Mathf.Max(cms, 0.1f); capMsSum += cms; if (cms > capMsMax) capMsMax = cms;
        nextCap = Time.unscaledTime + 1f / Mathf.Max(0.1f, C.frames.hz);
    }

    // ---- end ---------------------------------------------------------------------------------------------------------
    public static void Finish() { if (inst != null) inst.Close(); }
    void OnApplicationQuit() { Close(); }
    void Close()
    {
        if (closed) return; closed = true; Flush();
        try { csv.Close(); idx.Close(); } catch (Exception) { }
        Debug.Log("[CamTrace] done: " + rows + " rows, " + capCount + " frames (" + capSkipped + " skipped for a full write queue), capture+encode mean " + (capCount > 0 ? (capMsSum / capCount).ToString("F1") : "0") + " ms max " + capMsMax.ToString("F1") + " ms -> " + dir);
    }
}
