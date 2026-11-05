using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Proof harness (player side): -sheet:<id> -out:<dir>. Performs ONE move of the roster player (or enemy) on a standing opponent and writes
//   <dir>/<id>_side.png   4x3 contact sheet, side camera (perpendicular to the fighters' axis)
//   <dir>/<id>_34.png     4x3 contact sheet, 3/4 camera (45 degrees)
//   <dir>/<id>_log.txt    per tile: tick, time, sim state, Animator state name, clip time, torso lean ...
// Everything (rows, camera numbers, tile size, timings) is data: Assets/Data/move_sheet.json (copy in StreamingAssets for a player; or -sheetdata:<file>).
// Determinism: Time.captureFramerate = tickHz, so one rendered frame = exactly one Duel tick (1/60 s); tiles are placed on sim ticks, not on wall time.
// Input: the harness switches PlayerBrain / AIBrain / TouchControls off and drives DuelFighter through its public API
// (Press, PressSidestep, moveDir, run, guardHeld), so PlayerBrain needs no hook.
[DefaultExecutionOrder(-100)]
public class MoveSheet : MonoBehaviour
{
    // ---------------- data ----------------
    [Serializable] public class Row
    {
        public string id = "", subject = "", action = "press", press = "", durationFrom = "", dir = "none";
        public int durationTicks = 0, tailTicks = 0, side = 0;
        public string then = ""; public int thenTick = 0, thenSide = 0;   // P1: a SECOND press `thenTick` sim ticks after the first (roll / sidestep / backstep / light / ...), to prove cancel windows and the press queue
        public bool guard, run, follow;
        public float separation = 2.2f, startZ = 0f;
        public string hand = "";   // handcam rows: "right" | "left" ("" = handcam.hand; the -hand: argument wins)
    }
    // hand close-up mode (action "handcam"): 3 cameras on the weapon-hand bone at 3 moments -> 3x3 sheet (rows = moment, cols = front / side / below)
    [Serializable] public class HandCam
    {
        public string hand = "right", frame = "weapon";   // frame: "weapon" = 3 cameras 120 deg apart around the shaft, looking at the grip with the shaft vertical; "subject" = front / side / below of the subject
        public int tile = 640, idleAge = -3, midBeforeActive = 4;   // idle = ticks relative to the press; mid-swing = Active start minus this; impact = middle of the Active window
        public float dist = 0.6f, fov = 30f, toward = 0.10f, frontUp = 0.15f, nearClip = 0.05f;   // toward = aim offset along the weapon shaft (+Y of the socket)
        public bool lights = true; public float lightLumen = 300f, lightRange = 3f;              // three non-shadow point lights, one at each camera
        public float pullMinDist = 0.25f, pullMargin = 0.05f;
    }
    [Serializable] public class Cam { public float dist = 4.5f, height = 1.2f, fov = 35f, lookY = 1f, angleDeg = 45f, sign = 1f; }
    [Serializable] public class PullIn { public bool enabled = true; public float minDist = 2.2f, margin = 0.15f; }
    [Serializable] public class Arena { public float x = 0f, z = 12.6f; }
    [Serializable] public class Data
    {
        public float tickHz = 60f, preSeconds = 0.2f;
        public int tiles = 12, cols = 4, tileW = 480, tileH = 270, supersample = 2, settleTicks = 90, timeoutTicks = 1800, markerPx = 3;
        public string playerName = "", enemyName = "";   // scene object names; "" = roster.json (Roster.PlayerObject / EnemyObject)
        public Arena arena = new Arena();
        public int[] groundMaskExcludeLayers = { 8, 9 };
        public bool isolate; public float[] isolateColor = { 0.22f, 0.22f, 0.24f };
        public PullIn pullIn = new PullIn();
        public Cam side = new Cam(), threeQuarter = new Cam { dist = 5f, height = 1.8f, angleDeg = 45f };
        public HandCam handcam = new HandCam();
        public Row[] moves = new Row[0];
    }

    // ---------------- boot ----------------
    static string Arg(string prefix)
    {
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length);
        return null;
    }
    static bool Flag(string name) { return Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string mv = Arg("-sheet:"); if (!string.IsNullOrEmpty(mv)) CaptureUtil.HideConsole();
        if (string.IsNullOrEmpty(mv)) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<MoveSheet>() != null) return;
        var go = new GameObject("MoveSheet");
        DontDestroyOnLoad(go);
        go.AddComponent<MoveSheet>().moveId = mv;
    }

    // ---------------- state ----------------
    public string moveId;
    enum Phase { Init, Run, Finish, Done }
    Phase phase = Phase.Init;
    Data D; Row row; string outDir; int frame;
    DuelFighter player, enemy, subj, opp; Fighter subjFx;
    Transform hips, neck, wsock; float wYmax;   // weapon socket + far end of the weapon along its shaft (socket-local Y)
    Camera camSide, cam34; RenderTexture rtSide, rt34, rtSmall; Texture2D readTex;
    Color32[] sheetSide, sheet34; int sheetW, sheetH;
    int tick0, B, dur, pre; int[] ages; int nextTile, doneTiles; bool pressed, thenDone;
    Vector3 axis, fixedMid, subjFwd; float groundY;
    readonly List<string> log = new List<string>();
    readonly Dictionary<int, string> hashName = new Dictionary<int, string>();
    int pullSide, pull34; string note = "";
    // hand close-up mode
    bool handMode; HandCam hc; string handSide = "Right"; Transform handBone, midBone, socket; Transform[] digits = new Transform[5]; Light[] hLights = new Light[3];
    readonly Camera[] hCams = new Camera[3]; readonly RenderTexture[] hRts = new RenderTexture[3]; RenderTexture hSmall; Texture2D hRead; Color32[] hSheet; int hSheetSize; readonly int[] pullH = new int[3];
    static readonly string[] MomentNames = { "idle", "midswing", "impact" };
    static readonly string[] ViewNames = { "front", "side", "below" };
    static readonly string[] DigitNames = { "Index2", "Middle2", "Ring2", "Pinky2", "Thumb2" };

    // ---------------- helpers ----------------
    static Data LoadData()
    {
        var paths = new List<string>();
        string cli = Arg("-sheetdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "move_sheet.json"));
        paths.Add(Path.Combine(Application.dataPath, "Data/move_sheet.json"));
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) { var d = JsonUtility.FromJson<Data>(File.ReadAllText(p)); if (d != null) return d; } }
            catch (Exception ex) { Debug.LogError("MoveSheet: cannot read " + p + ": " + ex.Message); }
        }
        return null;
    }

    void Fail(string why)
    {
        Debug.LogError("MoveSheet: " + why);
        try { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, (moveId ?? "sheet") + "_log.txt"), "ERROR " + why + "\n"); } catch { }
        Quit(2);
    }

    void Quit(int code)
    {
        phase = Phase.Done;
        Time.captureFramerate = 0;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }

    DuelFighter FindFighter(string n)
    {
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f;
        return null;
    }

    int GroundMask()
    {
        int m = ~0;
        if (D.groundMaskExcludeLayers != null) foreach (int l in D.groundMaskExcludeLayers) m &= ~(1 << l);
        return m;
    }

    void Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, GroundMask(), QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }

    static Transform FindBone(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    Vector3 DirVector(string d, Vector3 fwd)
    {
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        switch (d)
        {
            case "forward": return fwd;
            case "back": return -fwd;
            case "left": return -right;
            case "right": return right;
            default: return Vector3.zero;
        }
    }

    int DurationTicks()
    {
        int n = row.durationTicks;
        string from = row.durationFrom ?? "";
        if (from.StartsWith("move:", StringComparison.Ordinal))
        {
            var m = subj.moves != null ? subj.moves.Get(from.Substring(5)) : null;
            if (m != null) n = m.Windup(false) + m.Active(false) + m.Recovery(false); else note += "move '" + from + "' not in the MoveTable; ";
        }
        else if (from.StartsWith("dodge:", StringComparison.Ordinal))
        {
            var dd = DodgeTable.Get(); string k = from.Substring(6);
            DodgeTable.DodgeRow r = k == "roll" ? dd.roll : k == "backstep" ? dd.backstep : k == "sidestep" ? (DodgeTable.DodgeRow)dd.sidestep : null;
            if (r != null) n = r.ticks; else note += "dodge row '" + k + "' unknown; ";
            if (!dd.enabled) note += "dodge.json enabled=false (sidestep will not start); ";
        }
        return Mathf.Max(1, n + row.tailTicks);
    }

    // ---------------- init ----------------
    void Init()
    {
        outDir = Arg("-out:"); if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Application.dataPath, "..", "PlayShots");
        Directory.CreateDirectory(outDir);
        D = LoadData();
        if (D == null) { Fail("move_sheet.json not found (StreamingAssets, Assets/Data or -sheetdata:)"); return; }
        foreach (var r in D.moves) if (r != null && r.id == moveId) row = r;
        if (row == null) { Fail("no row '" + moveId + "' in move_sheet.json"); return; }
        string pName = string.IsNullOrEmpty(D.playerName) ? Roster.PlayerObject : D.playerName, eName = string.IsNullOrEmpty(D.enemyName) ? Roster.EnemyObject : D.enemyName;
        player = FindFighter(pName); enemy = FindFighter(eName);
        if (player == null || enemy == null) { Fail("fighters not found (" + pName + "/" + eName + ")"); return; }
        bool subjEnemy = !string.IsNullOrEmpty(row.subject) && (row.subject == eName || row.subject == Roster.EnemyId);   // subject: character id or object name; "" = the player
        subj = subjEnemy ? enemy : player; opp = subj == player ? enemy : player;
        subjFx = subj.GetComponent<Fighter>();
        if (subjFx == null || subjFx.anim == null) { Fail("subject has no Fighter.anim"); return; }
        foreach (var c in subjFx.anim.runtimeAnimatorController.animationClips) hashName[Animator.StringToHash(c.name)] = c.name;
        hips = FindBone(subj.transform, "mixamorig:Hips"); neck = FindBone(subj.transform, "mixamorig:Neck");
        wsock = FindBone(subj.transform, "WeaponSocket_R");
        if (wsock != null && wsock.childCount > 0)
        {
            wYmax = -999f;
            foreach (var rd in wsock.GetChild(0).GetComponentsInChildren<Renderer>(true)) { var b = rd.localBounds; for (int c = 0; c < 8; c++) { Vector3 cr = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1)); wYmax = Mathf.Max(wYmax, wsock.InverseTransformPoint(rd.transform.TransformPoint(cr)).y); } }
        }

        // AI / keyboard / touch off; the harness is the only input
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;

        // fixed setup: both fighters standing on the axis (+z), facing each other, `separation` apart
        float z0 = row.startZ != 0f ? row.startZ : D.arena.z;
        Place(player, new Vector3(D.arena.x, 0f, z0), Vector3.forward);
        Place(enemy, new Vector3(D.arena.x, 0f, z0 + row.separation), Vector3.back);
        player.stamina = player.maxStamina; enemy.stamina = enemy.maxStamina;
        axis = Flat(opp.transform.position - subj.transform.position).normalized; subjFwd = axis; groundY = subj.transform.position.y;

        // timing in sim ticks
        handMode = row.action == "handcam"; hc = D.handcam ?? new HandCam();
        pre = Mathf.RoundToInt(D.preSeconds * D.tickHz); dur = DurationTicks();
        int n = Mathf.Max(2, D.tiles); ages = new int[n];
        for (int k = 0; k < n; k++) ages[k] = -pre + Mathf.RoundToInt(k * (pre + dur) / (n - 1f));
        if (handMode) { if (!HandTiming()) return; n = 3; pre = Mathf.Max(0, -hc.idleAge); }
        Time.captureFramerate = Mathf.RoundToInt(D.tickHz); QualitySettings.vSyncCount = 0;
        tick0 = Duel.TickCount; B = tick0 + Mathf.Max(D.settleTicks, pre + 30);
        if (handMode) { HandInit(); return; }

        // cameras and render targets
        int ss = Mathf.Max(1, D.supersample);
        rtSide = MakeRT(D.tileW * ss, D.tileH * ss); rt34 = MakeRT(D.tileW * ss, D.tileH * ss); rtSmall = MakeRT(D.tileW, D.tileH);
        readTex = new Texture2D(D.tileW, D.tileH, TextureFormat.RGBA32, false, false);
        camSide = MakeCam("MoveSheetCam_side", rtSide, D.side.fov); cam34 = MakeCam("MoveSheetCam_34", rt34, D.threeQuarter.fov);
        int cols = Mathf.Max(1, D.cols), rows = Mathf.CeilToInt(n / (float)cols);
        sheetW = cols * D.tileW; sheetH = rows * D.tileH; sheetSide = new Color32[sheetW * sheetH]; sheet34 = new Color32[sheetW * sheetH];
        phase = Phase.Run;
    }

    RenderTexture MakeRT(int w, int h)
    {
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32); rt.antiAliasing = 1; rt.Create(); return rt;
    }

    Camera MakeCam(string name, RenderTexture rt, float fov)
    {
        var main = Camera.main; if (main == null) main = FindFirstObjectByType<Camera>();
        var go = new GameObject(name); go.transform.SetParent(transform, false);
        var c = go.AddComponent<Camera>();
        if (main != null) c.CopyFrom(main);
        c.targetTexture = rt; c.fieldOfView = fov; c.enabled = true; c.depth = -50f; c.tag = "Untagged";
        var hd = go.GetComponent<HDAdditionalCameraData>(); if (hd == null) hd = go.AddComponent<HDAdditionalCameraData>();
        var hdMain = main != null ? main.GetComponent<HDAdditionalCameraData>() : null;
        if (hdMain != null) hdMain.CopyTo(hd);
        if (D.isolate || Flag("-isolate"))
        {
            c.cullingMask = 1 << 8;
            var col = D.isolateColor != null && D.isolateColor.Length >= 3 ? new Color(D.isolateColor[0], D.isolateColor[1], D.isolateColor[2], 1f) : Color.gray;
            c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = col;
            hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; hd.backgroundColorHDR = col;
        }
        return c;
    }

    // ---------------- per frame ----------------
    void Update()
    {
        if (phase == Phase.Done) return;
        frame++;
        if (phase == Phase.Init) { Init(); if (phase != Phase.Run) return; }
        if (phase != Phase.Run) return;
        if (frame > D.timeoutTicks + D.settleTicks + dur + 200) { note += "TIMEOUT (sim did not advance); "; Finish(); return; }
        if (!pressed) { fixedMid = (player.transform.position + enemy.transform.position) * 0.5f; axis = Flat(opp.transform.position - subj.transform.position).normalized; subjFwd = axis; groundY = subj.transform.position.y; }
        if (!pressed && Duel.TickCount + 1 >= B) { pressed = true; DoPress(); }
        if (pressed && !thenDone && !string.IsNullOrEmpty(row.then) && Duel.TickCount >= B + row.thenTick)
        {
            thenDone = true; subj.stamina = subj.maxStamina;
            if (row.then == "sidestep") subj.PressSidestep(row.thenSide); else if (row.then == "upgrade") log.Add("# hold-to-heavy upgrade " + (subj.UpgradeToHeavy(16) ? "OK" : "REFUSED")); else subj.Press(row.then);
            log.Add("# then-press '" + row.then + "' at tick " + Duel.TickCount + " (+" + (Duel.TickCount - B) + ") state=" + subj.state + ":" + subj.t);
        }
        if (pressed && row.action == "hold")
        {
            bool active = Duel.TickCount < B + dur;
            subj.moveDir = active ? DirVector(row.dir, subjFwd) : Vector3.zero;
            subj.run = active && row.run; subj.guardHeld = active && row.guard; subj.guardSide = 0;
        }
        else if (pressed && Duel.TickCount >= B + 3) subj.moveDir = Vector3.zero;   // one-shot moves (roll with a direction): the key direction is only needed for the first ticks
    }

    void DoPress()
    {
        subj.stamina = subj.maxStamina;
        subj.moveDir = DirVector(row.dir, subjFwd);
        subj.run = row.run; subj.guardHeld = row.guard; subj.guardSide = 0;
        switch (row.action)
        {
            case "press": case "handcam": subj.Press(row.press); break;
            case "sidestep": subj.PressSidestep(row.side); break;
            case "hold": break;
            default: note += "unknown action '" + row.action + "'; "; break;
        }
        log.Add("# press at tick " + Duel.TickCount + " frame " + frame + " subject=" + subj.name + " state=" + subj.state);
    }

    void LateUpdate()
    {
        if (phase != Phase.Run) return;
        if (handMode)
        {
            PlaceHandCams();
            if (nextTile < ages.Length && Duel.TickCount >= B + ages[nextTile]) { int hk = nextTile++; log.Add(HandDescribe(hk)); StartCoroutine(HandCapture(hk)); }
            return;
        }
        PlaceCameras();
        if (nextTile < ages.Length && Duel.TickCount >= B + ages[nextTile])
        {
            int k = nextTile++;
            log.Add(Describe(k));
            StartCoroutine(Capture(k));
        }
    }

    // ---------------- cameras ----------------
    void PlaceCameras()
    {
        if (camSide == null) return;
        Vector3 s = subj.transform.position;
        Vector3 target = row.follow ? new Vector3(s.x, 0f, s.z) : new Vector3(fixedMid.x, 0f, fixedMid.z);
        float gy = row.follow ? s.y : groundY;
        Vector3 subjRight = Vector3.Cross(Vector3.up, subjFwd);
        Vector3 dSide = subjRight * (D.side.sign >= 0f ? 1f : -1f);
        float ang = D.threeQuarter.angleDeg * Mathf.Deg2Rad;
        Vector3 d34 = (subjRight * (D.threeQuarter.sign >= 0f ? 1f : -1f) * Mathf.Cos(ang) + subjFwd * Mathf.Sin(ang)).normalized;
        PlaceOne(camSide, D.side, target, gy, dSide, ref pullSide);
        PlaceOne(cam34, D.threeQuarter, target, gy, d34, ref pull34);
    }

    void PlaceOne(Camera c, Cam p, Vector3 target, float gy, Vector3 dir, ref int pulled)
    {
        Vector3 look = new Vector3(target.x, gy + p.lookY, target.z);
        Vector3 pos = new Vector3(target.x, gy + p.height, target.z) + dir * p.dist;
        if (D.pullIn.enabled)
        {
            Vector3 to = pos - look; float len = to.magnitude;
            if (Physics.Raycast(look, to / len, out var hit, len, GroundMask(), QueryTriggerInteraction.Ignore))
            {
                float nd = Mathf.Max(D.pullIn.minDist, hit.distance - D.pullIn.margin);
                pos = look + to / len * nd; pulled++;
            }
        }
        c.transform.position = pos; c.transform.rotation = Quaternion.LookRotation(look - pos);
    }

    // ---------------- capture ----------------
    string AnimName(int hash) { string n; return hashName.TryGetValue(hash, out n) ? n : "#" + hash; }

    string Describe(int k)
    {
        var an = subjFx.anim; var si = an.GetCurrentAnimatorStateInfo(0);
        string cur = AnimName(si.shortNameHash), next = "-"; float nextN = 0f, trN = 0f;
        if (an.IsInTransition(0)) { var ns = an.GetNextAnimatorStateInfo(0); next = AnimName(ns.shortNameHash); nextN = ns.normalizedTime; trN = an.GetAnimatorTransitionInfo(0).normalizedTime; }
        var sb = new StringBuilder();
        foreach (var ci in an.GetCurrentAnimatorClipInfo(0)) sb.Append(ci.clip != null ? ci.clip.name : "?").Append(':').Append(ci.weight.ToString("F2")).Append(',');
        string layers = "";
        for (int i = 1; i < an.layerCount; i++) if (an.GetLayerWeight(i) > 0.001f) layers += an.GetLayerName(i) + "=" + an.GetLayerWeight(i).ToString("F2") + ";";
        float lean = 0f, side = 0f, fwd = 0f, hipsY = 0f;
        if (hips != null && neck != null)
        {
            Vector3 v = neck.position - hips.position; lean = Vector3.Angle(v, Vector3.up);
            Vector3 lv = subj.transform.InverseTransformDirection(v);
            side = Mathf.Atan2(lv.x, lv.y) * Mathf.Rad2Deg; fwd = Mathf.Atan2(lv.z, lv.y) * Mathf.Rad2Deg; hipsY = hips.position.y - subj.transform.position.y;
        }
        Vector3 p = subj.transform.position;
        var I = System.Globalization.CultureInfo.InvariantCulture;
        var vis = subj.transform.Find("Visual");   // Flinch rotates this pivot on a landed hit: a tilt here is NOT the clip
        float pivotTilt = vis != null ? Vector3.Angle(vis.up, Vector3.up) : -1f;
        int age = Duel.TickCount - B;
        string wpn = "-";
        if (wsock != null)
        {
            var sbw = new StringBuilder(); sbw.Append("sockActive=").Append(wsock.gameObject.activeInHierarchy ? 1 : 0).Append(" scale=").Append(wsock.lossyScale.y.ToString("F2", I)).Append(" lods=");
            for (int ci = 0; ci < wsock.childCount; ci++) { var ch = wsock.GetChild(ci); int en = 0, nvis = 0, tot = 0; foreach (var rd in ch.GetComponentsInChildren<Renderer>(true)) { tot++; if (rd.enabled) en++; if (rd.isVisible) nvis++; } sbw.Append(ch.name).Append(':').Append(ch.gameObject.activeSelf ? 'A' : 'a').Append(en).Append('/').Append(tot).Append("/vis").Append(nvis).Append(' '); }
            Vector3 headP = wsock.TransformPoint(0f, wYmax, 0f), chest = opp.transform.position + Vector3.up * 1.2f * Mathf.Max(1f, opp.scale);
            sbw.Append("hand=" + V3s(wsock.position) + " head=" + V3s(headP) + " headY=" + (headP.y - subj.transform.position.y).ToString("F2", I) + " head->oppChest=" + Vector3.Distance(headP, chest).ToString("F2", I) + " hand->oppChest=" + Vector3.Distance(wsock.position, chest).ToString("F2", I));
            wpn = sbw.ToString();
        }
        return string.Join("\t", new[] {
            k.ToString(), Duel.TickCount.ToString(), age.ToString(), (age / D.tickHz).ToString("F3", I),
            subj.state + ":" + subj.t, cur, si.normalizedTime.ToString("F3", I), (si.normalizedTime * si.length).ToString("F3", I), si.length.ToString("F2", I),
            next, nextN.ToString("F3", I), trN.ToString("F2", I), an.speed.ToString("F2", I), sb.ToString(), layers,
            p.x.ToString("F2", I) + "," + p.y.ToString("F2", I) + "," + p.z.ToString("F2", I), hipsY.ToString("F2", I),
            lean.ToString("F0", I), side.ToString("F0", I), fwd.ToString("F0", I), opp.state.ToString(),
            pivotTilt.ToString("F0", I), subj.transform.lossyScale.y.ToString("F2", I), wpn });
    }

    static Color32 PhaseColor(DuelFighter f, int age)
    {
        if (age < 0) return new Color32(40, 200, 60, 255);
        switch (f.state)
        {
            case FState.Windup: return new Color32(255, 150, 0, 255);
            case FState.Active: return new Color32(255, 30, 30, 255);
            case FState.Recovery: return new Color32(40, 110, 255, 255);
            case FState.Roll: return new Color32(230, 40, 230, 255);
            case FState.Stagger: case FState.Down: case FState.ParryRecover: return new Color32(0, 220, 220, 255);
            default: return new Color32(150, 150, 150, 255);
        }
    }

    IEnumerator Capture(int k)
    {
        int age = Duel.TickCount - B;
        var color = PhaseColor(subj, age);
        yield return new WaitForEndOfFrame();   // both cameras rendered this frame into their RenderTextures
        Blit(rtSide, sheetSide, k, color); Blit(rt34, sheet34, k, color);
        doneTiles++;
        if (doneTiles >= ages.Length) Finish();
    }

    void Blit(RenderTexture big, Color32[] sheet, int k, Color32 marker)
    {
        Graphics.Blit(big, rtSmall);
        var prev = RenderTexture.active; RenderTexture.active = rtSmall;
        readTex.ReadPixels(new Rect(0, 0, D.tileW, D.tileH), 0, 0, false); RenderTexture.active = prev;
        var px = readTex.GetPixels32();
        int cols = Mathf.Max(1, D.cols), rows = sheetH / D.tileH;
        int col = k % cols, rw = k / cols, ox = col * D.tileW, oy = (rows - 1 - rw) * D.tileH;   // tile 0 = top-left; pixel rows run bottom-up
        int bar = Mathf.Max(1, Mathf.RoundToInt(D.tileW * (k + 1f) / ages.Length));
        for (int y = 0; y < D.tileH; y++)
            for (int x = 0; x < D.tileW; x++)
            {
                Color32 c = px[y * D.tileW + x]; c.a = 255;
                if (y >= D.tileH - D.markerPx && x < bar) c = marker;   // marker bar at the top of the tile
                sheet[(oy + y) * sheetW + ox + x] = c;
            }
    }

    void Finish()
    {
        if (phase == Phase.Finish || phase == Phase.Done) return;
        phase = Phase.Finish;
        if (handMode) { HandFinish(); return; }
        try
        {
            WriteSheet(sheetSide, Path.Combine(outDir, moveId + "_side.png"));
            WriteSheet(sheet34, Path.Combine(outDir, moveId + "_34.png"));
            var sb = new StringBuilder();
            sb.AppendLine("# MoveSheet move=" + moveId + " subject=" + subj.name + " action=" + row.action + " press=" + row.press + " dir=" + row.dir + " follow=" + row.follow);
            sb.AppendLine("# tickHz=" + D.tickHz + " captureFramerate=" + Time.captureFramerate + " durationTicks=" + dur + " preTicks=" + pre + " B(tick of press)=" + B + " tiles=" + ages.Length + " separation=" + row.separation + " frames=" + frame);
            sb.AppendLine("# tile ages (ticks): " + string.Join(",", Array.ConvertAll(ages, a => a.ToString())));
            sb.AppendLine("# camera pull-in frames: side=" + pullSide + " 34=" + pull34 + (D.isolate || Flag("-isolate") ? " (isolate: layer 8 only)" : ""));
            if (note.Length > 0) sb.AppendLine("# NOTE " + note);
            sb.AppendLine("# marker bar (top of tile): length = (tile+1)/tiles; green = before the move, orange = windup, red = active, blue = recovery, magenta = roll/dodge, grey = loco/guard, cyan = stagger");
            sb.AppendLine("# cols: tile\ttick\tageTicks\tt_rel_s\tsimState:t\tanimState\tnormTime\tclipTime_s\tstateLen_s\tnextState\tnextNorm\ttransNorm\tanimSpeed\tclips(weight)\textraLayers\tposX,Y,Z\thipsH_m\ttorsoLean_deg(neck-hips vs up)\tleanSide_deg\tleanFwd_deg\topponentState\tvisualPivotTilt_deg(Flinch)\trootScaleY\tweapon(socket active, scale, LOD children A=active enabledRenderers/total/visible, hand and head world pos, head height, head and hand distance to opponent chest)");
            foreach (var l in log) sb.AppendLine(l);
            File.WriteAllText(Path.Combine(outDir, moveId + "_log.txt"), sb.ToString());
        }
        catch (Exception ex) { Debug.LogError("MoveSheet: write failed: " + ex); }
        Quit(0);
    }

    void WriteSheet(Color32[] px, string path)
    {
        var t = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false, false);
        t.SetPixels32(px); t.Apply(false);
        File.WriteAllBytes(path, t.EncodeToPNG());
        Destroy(t);
    }

    void OnDestroy()
    {
        if (rtSide) rtSide.Release(); if (rt34) rt34.Release(); if (rtSmall) rtSmall.Release();
        for (int i = 0; i < hRts.Length; i++) if (hRts[i]) hRts[i].Release();
        if (hSmall) hSmall.Release();
    }

    // ================= hand close-up mode (-sheet:<handcam row id>, optional -hand:right|left) =================
    // Moments (ticks relative to the press): idle = hc.idleAge; midswing = Active start - hc.midBeforeActive; impact = middle of the Active window
    // (read from the subject's MoveTable row of the pressed move). Cameras sit hc.dist from the hand bone (aim point nudged along the weapon shaft by hc.toward):
    // front = from the subject's forward and a bit above, side = from the subject's right, below = under the hand looking up.
    bool HandTiming()
    {
        string mid = (row.durationFrom ?? "").StartsWith("move:", StringComparison.Ordinal) ? row.durationFrom.Substring(5) : "light_right";
        var m = subj.moves != null ? subj.moves.Get(mid) : null;
        if (m == null) { Fail("handcam: move '" + mid + "' not in the MoveTable"); return false; }
        int w = m.Windup(false), a = m.Active(false);
        ages = new[] { hc.idleAge, w - hc.midBeforeActive, w + Mathf.Max(0, a / 2) };
        return true;
    }

    static Transform FindUnder(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    void HandInit()
    {
        string ha = Arg("-hand:"); if (string.IsNullOrEmpty(ha)) ha = !string.IsNullOrEmpty(row.hand) ? row.hand : hc.hand;
        handSide = ha.ToLowerInvariant().StartsWith("l", StringComparison.Ordinal) ? "Left" : "Right";
        handBone = FindUnder(subj.transform, "mixamorig:" + handSide + "Hand"); midBone = FindUnder(subj.transform, "mixamorig:" + handSide + "HandMiddle1");
        if (handBone == null) { Fail("handcam: no mixamorig:" + handSide + "Hand under " + subj.name); return; }
        for (int i = 0; i < 5; i++) digits[i] = FindUnder(subj.transform, "mixamorig:" + handSide + "Hand" + DigitNames[i]);
        socket = FindUnder(handBone, "WeaponSocket_" + handSide.Substring(0, 1));
        if (socket == null) note += "no WeaponSocket_" + handSide.Substring(0, 1) + " under the hand bone (weapon not attached to this hand); ";
        int ts = Mathf.Max(64, hc.tile), ss = Mathf.Max(1, D.supersample);
        for (int i = 0; i < 3; i++) { hRts[i] = MakeRT(ts * ss, ts * ss); hCams[i] = MakeCam("MoveSheetHandCam_" + ViewNames[i], hRts[i], hc.fov); hCams[i].nearClipPlane = hc.nearClip; }
        hSmall = MakeRT(ts, ts); hRead = new Texture2D(ts, ts, TextureFormat.RGBA32, false, false);
        hSheetSize = ts * 3; hSheet = new Color32[hSheetSize * hSheetSize];
        if (hc.lights)
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("MoveSheetHandLight_" + ViewNames[i]); go.transform.SetParent(hCams[i].transform, false);
                var l = go.AddComponent<Light>(); l.type = LightType.Point; l.range = hc.lightRange; l.shadows = LightShadows.None; l.color = Color.white;
                var hd = go.GetComponent<HDAdditionalLightData>(); if (hd == null) hd = go.AddComponent<HDAdditionalLightData>();
                hd.SetIntensity(hc.lightLumen, UnityEngine.Rendering.LightUnit.Lumen);
                uint allBits = uint.MaxValue; hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)allBits;   // lights every rendering layer (fighters use the character bit)
                hLights[i] = l;
            }
        phase = Phase.Run;
    }

    void PlaceHandCams()
    {
        if (handBone == null || hCams[0] == null) return;
        Vector3 shaft = socket != null ? socket.up : handBone.up;
        Vector3 aim = handBone.position + shaft.normalized * hc.toward;
        Vector3 f = subj.transform.forward, r = subj.transform.right;
        Vector3[] dirs = { (f + Vector3.up * (hc.frontUp / Mathf.Max(0.01f, hc.dist))).normalized, r, Vector3.down };
        bool wf = hc.frame == "weapon" && socket != null;
        if (wf) { Vector3 ax = shaft.normalized, rf = Vector3.ProjectOnPlane(socket.right, ax).normalized; dirs[0] = rf; dirs[1] = Quaternion.AngleAxis(90f, ax) * rf; dirs[2] = -ax; }   // views 0 / 1 = across the shaft 90 deg apart, view 2 = from the butt end looking up the shaft (the fist cross-section)
        for (int i = 0; i < 3; i++)
        {
            Vector3 pos = aim + dirs[i] * hc.dist;
            if (D.pullIn.enabled)
            {
                Vector3 to = pos - aim; float len = to.magnitude;
                if (Physics.Raycast(aim, to / len, out var hit, len, GroundMask(), QueryTriggerInteraction.Ignore)) { pos = aim + to / len * Mathf.Max(hc.pullMinDist, hit.distance - hc.pullMargin); pullH[i]++; }
            }
            hCams[i].transform.position = pos; hCams[i].transform.rotation = Quaternion.LookRotation(aim - pos, wf ? (i == 2 ? socket.right : shaft) : (i == 2 ? f : Vector3.up));
        }
    }

    static float Ang(Vector3 localPos) { return Mathf.Atan2(localPos.z, localPos.x) * Mathf.Rad2Deg; }

    string HandDescribe(int k)
    {
        var I = System.Globalization.CultureInfo.InvariantCulture; var sb = new StringBuilder();
        var an = subjFx.anim; var si = an.GetCurrentAnimatorStateInfo(0);
        int age = Duel.TickCount - B;
        sb.AppendLine("## moment " + k + " " + MomentNames[k] + "  target age " + ages[k] + "  actual age " + age + " ticks (" + (age / D.tickHz).ToString("F3", I) + " s)  tick " + Duel.TickCount + "  sim " + subj.state + ":" + subj.t + "  anim " + AnimName(si.shortNameHash) + " norm " + si.normalizedTime.ToString("F2", I) + " clipT " + (si.normalizedTime * si.length).ToString("F3", I) + "s");
        Vector3 H = handBone.position; Vector3 P = midBone != null ? (H + midBone.position) * 0.5f : H;
        sb.AppendLine("   hand bone world " + V(H) + "   palm centre (hand+Middle1)/2 world " + V(P) + "   subject " + V(subj.transform.position));
        if (socket == null) { sb.AppendLine("   NO WEAPON SOCKET: nothing to measure against"); return sb.ToString().TrimEnd(); }
        sb.AppendLine("   socket world " + V(socket.position) + "   socket up (shaft) " + V(socket.up) + "   dist hand bone -> socket origin " + Vector3.Distance(H, socket.position).ToString("F3", I) + " m");
        // handle extent along the shaft: local bounds of every renderer under the socket's first LOD, in socket space
        float ymin = float.MaxValue, ymax = float.MinValue; Transform lod = socket.childCount > 0 ? socket.GetChild(0) : socket;
        foreach (var rd in lod.GetComponentsInChildren<Renderer>(true))
        {
            var b = rd.localBounds;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                Vector3 lp = socket.InverseTransformPoint(rd.transform.TransformPoint(corner));
                ymin = Mathf.Min(ymin, lp.y); ymax = Mathf.Max(ymax, lp.y);
            }
        }
        Vector3 hl = socket.InverseTransformPoint(H), pl = socket.InverseTransformPoint(P);
        if (ymax > ymin)
        {
            float len = ymax - ymin;
            string Third(float y) { float fr = (y - ymin) / len; return (fr * 100f).ToString("F0", I) + "% of length -> " + (fr < 1f / 3f ? "LOWER THIRD" : fr < 2f / 3f ? "MIDDLE" : "HEAD END"); }
            sb.AppendLine("   weapon extent along shaft (socket-local Y, GLB origin = grip point): " + ymin.ToString("F3", I) + " .. " + ymax.ToString("F3", I) + " m (length " + len.ToString("F3", I) + ")");
            sb.AppendLine("   hand bone in the weapon's local frame " + V(hl) + " : " + Third(hl.y) + ", " + (hl.y - ymin).ToString("F3", I) + " m from the butt end, off-axis " + Mathf.Sqrt(hl.x * hl.x + hl.z * hl.z).ToString("F3", I) + " m");
            sb.AppendLine("   palm centre in the weapon's local frame " + V(pl) + " : " + Third(pl.y) + ", off-axis (handle through the palm = ~0) " + Mathf.Sqrt(pl.x * pl.x + pl.z * pl.z).ToString("F3", I) + " m");
        }
        else sb.AppendLine("   weapon renderers not found under " + lod.name + "; hand bone in socket frame " + V(hl) + ", palm " + V(pl));
        // digits in the weapon frame: radial distance to the shaft axis (wrap = small and similar) and angle around it (thumb opposite = ~180 deg from the finger mean)
        float fingerAngSum = 0f; int nf = 0; float thumbAng = 0f;
        for (int i = 0; i < 5; i++)
        {
            if (digits[i] == null) { sb.AppendLine("   " + DigitNames[i] + ": bone missing"); continue; }
            Vector3 dl = socket.InverseTransformPoint(digits[i].position); float rad = Mathf.Sqrt(dl.x * dl.x + dl.z * dl.z), an2 = Ang(dl);
            sb.AppendLine("   " + DigitNames[i] + " local " + V(dl) + " radial " + rad.ToString("F3", I) + " m  angle around shaft " + an2.ToString("F0", I) + " deg");
            if (i < 4) { fingerAngSum += Mathf.Cos(an2 * Mathf.Deg2Rad); nf++; } else thumbAng = an2;
        }
        if (nf > 0) { float meanFinger = Mathf.Atan2(0f, 0f); float sx = 0f, sz = 0f; for (int i = 0; i < 4; i++) if (digits[i] != null) { Vector3 dl = socket.InverseTransformPoint(digits[i].position); sx += dl.x; sz += dl.z; } meanFinger = Mathf.Atan2(sz, sx) * Mathf.Rad2Deg; sb.AppendLine("   fingers mean angle " + meanFinger.ToString("F0", I) + " deg, thumb " + thumbAng.ToString("F0", I) + " deg, thumb-vs-fingers " + Mathf.Abs(Mathf.DeltaAngle(meanFinger, thumbAng)).ToString("F0", I) + " deg (opposed = ~180)"); }
        sb.AppendLine("   GripPose component present: " + (subj.GetComponentInChildren<GripPose>(true) != null) + "   pivot(Visual) tilt " + (subj.transform.Find("Visual") != null ? Vector3.Angle(subj.transform.Find("Visual").up, Vector3.up).ToString("F0", I) : "-") + " deg");
        return sb.ToString().TrimEnd();
    }

    static string V3s(Vector3 v) { var I = System.Globalization.CultureInfo.InvariantCulture; return "(" + v.x.ToString("F2", I) + "," + v.y.ToString("F2", I) + "," + v.z.ToString("F2", I) + ")"; }
    static string V(Vector3 v) { var I = System.Globalization.CultureInfo.InvariantCulture; return "(" + v.x.ToString("F3", I) + "," + v.y.ToString("F3", I) + "," + v.z.ToString("F3", I) + ")"; }

    IEnumerator HandCapture(int k)
    {
        yield return new WaitForEndOfFrame();
        var marker = k == 0 ? new Color32(40, 200, 60, 255) : k == 1 ? new Color32(255, 150, 0, 255) : new Color32(255, 30, 30, 255);
        for (int c = 0; c < 3; c++)
        {
            Graphics.Blit(hRts[c], hSmall);
            var prev = RenderTexture.active; RenderTexture.active = hSmall;
            int ts = hSmall.width; hRead.ReadPixels(new Rect(0, 0, ts, ts), 0, 0, false); RenderTexture.active = prev;
            var px = hRead.GetPixels32(); int ox = c * ts, oy = (2 - k) * ts;   // row = moment (top = idle), col = front/side/below
            for (int y = 0; y < ts; y++)
                for (int x = 0; x < ts; x++)
                {
                    Color32 col = px[y * ts + x]; col.a = 255;
                    if (y >= ts - D.markerPx && x < ts) col = marker;   // thin full-width bar: green idle, orange mid-swing, red impact
                    hSheet[(oy + y) * hSheetSize + ox + x] = col;
                }
        }
        doneTiles++;
        if (doneTiles >= 3) Finish();
    }

    void HandFinish()
    {
        try
        {
            var t = new Texture2D(hSheetSize, hSheetSize, TextureFormat.RGBA32, false, false);
            t.SetPixels32(hSheet); t.Apply(false);
            File.WriteAllBytes(Path.Combine(outDir, moveId + ".png"), t.EncodeToPNG()); Destroy(t);
            var sb = new StringBuilder();
            sb.AppendLine("# MoveSheet handcam id=" + moveId + " subject=" + subj.name + " hand=" + handSide + " press=" + row.press + " tickHz=" + D.tickHz + " B(press tick)=" + B + " frames=" + frame);
            sb.AppendLine("# sheet " + moveId + ".png: " + hSheetSize + "x" + hSheetSize + ", tile " + hSmall.width + ", rows = moment (idle / midswing / impact), cols = " + (hc.frame == "weapon" ? "weapon-frame views: across the shaft at 0 and 90 deg (shaft vertical, head up), and from the butt end looking up the shaft" : "front / side / below") + "; top bar colour green / orange / red");
            sb.AppendLine("# camera: dist " + hc.dist + " m, fov " + hc.fov + ", aim = hand bone + shaft * " + hc.toward + "; pull-in frames front/side/below = " + pullH[0] + "/" + pullH[1] + "/" + pullH[2] + (D.isolate || Flag("-isolate") ? "; isolate (layer 8 only)" : "") + "; point lights " + (hc.lights ? hc.lightLumen + " lm" : "off"));
            sb.AppendLine("# moment ages (ticks vs press): " + string.Join(",", Array.ConvertAll(ages, a => a.ToString())));
            if (note.Length > 0) sb.AppendLine("# NOTE " + note);
            foreach (var l in log) sb.AppendLine(l);
            File.WriteAllText(Path.Combine(outDir, moveId + "_log.txt"), sb.ToString());
        }
        catch (Exception ex) { Debug.LogError("MoveSheet: handcam write failed: " + ex); }
        Quit(0);
    }
}
