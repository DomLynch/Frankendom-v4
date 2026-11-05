using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Lane E proof harness (head / neck): -headsheet:<id> -out:<dir>   (separate from MoveSheet, which is owned by lane D; same style and same determinism).
//   kind "idle":  both fighters placed, the player idles facing the camera; one PNG per camera in the row (<id>_<cam>.png) + optional live game-camera screenshot (<id>_live.png)
//   kind "orbit": the player is locked on the enemy while the enemy is teleported around him through 360 degrees; `frames` tiles evenly over the orbit -> <id>_sheet.png (cols x rows)
// Always writes <id>_log.txt: per sampled tick the head yaw / pitch RELATIVE TO THE TORSO before (raw) and after the HeadClamp, the Animator state and every active particle system.
// Data: Assets/Data/head_sheet.json (StreamingAssets copy for a player, or -headsheetdata:<file>). Flags: -noclamp (HeadClamp off: shows the raw animation), -isolate not supported.
[DefaultExecutionOrder(1000)]
public class HeadSheet : MonoBehaviour
{
    [Serializable] public class Cam { public string name = "face"; public float dist = 2.5f, height = 1.55f, lookY = 1.45f, fov = 30f, angleDeg = 0f, minAboveGround = 0.6f; public int w = 1280, h = 720; }
    [Serializable] public class Row
    {
        public string id = "", kind = "idle", opponent = "";   // opponent object name; "" = the roster enemy
        public int settleTicks = -1;   // -1 = Data.settleTicks
        public float z = 12.6f, x = 0f, oppDist = 7f;                 // idle: the player at (x,z), opponent oppDist further along +z
        public float radius = 3f, seconds = 12f; public int frames = 6, cols = 3, tileW = 640, tileH = 360;   // orbit
        public int[] captureTicks = { 0 };                             // idle: ticks after the settle time at which every camera is captured
        public bool liveShot = false, hideOpponent = false;   // hideOpponent: the orbiting target is not drawn (it would walk through the camera)
        public Cam[] cams = new Cam[0];
    }
    [Serializable] public class Data
    {
        public float tickHz = 60f; public int settleTicks = 120, supersample = 2, logEvery = 6, timeoutTicks = 4000;
        public string playerName = "", enemyName = "";   // scene object names; "" = roster.json (Roster.PlayerObject / EnemyObject)
        public int[] groundMaskExcludeLayers = { 8, 9 };
        public float pullMinDist = 1.2f, pullMargin = 0.15f;
        public Row[] rows = new Row[0];
    }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string id = Arg("-headsheet:"); if (string.IsNullOrEmpty(id)) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<HeadSheet>() != null) return;
        var go = new GameObject("HeadSheet"); DontDestroyOnLoad(go); go.AddComponent<HeadSheet>().rowId = id;
    }

    public string rowId;
    enum Phase { Init, Run, Done }
    Phase phase = Phase.Init; Data D; Row row; string outDir; int frame, tick0, B, orbitTick, orbitTotal, nextCap;
    DuelFighter player, enemy; HeadClamp clamp; Transform playerGround; Vector3 start, axisFwd; float groundY;
    readonly List<string> log = new List<string>();
    readonly List<(string name, Camera cam, RenderTexture rt, int w, int h)> cams = new List<(string, Camera, RenderTexture, int, int)>();
    Texture2D sheet; int sheetW, sheetH, doneCaps, wantCaps; Texture2D readTex; RenderTexture small;
    float maxAbsYawRaw, maxAbsYawNow, maxAbsPitchRaw, maxAbsPitchNow;

    static Data LoadData()
    {
        var paths = new List<string>(); string cli = Arg("-headsheetdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "head_sheet.json")); paths.Add(Path.Combine(Application.dataPath, "Data/head_sheet.json"));
        foreach (var p in paths) { try { if (File.Exists(p)) { var d = JsonUtility.FromJson<Data>(File.ReadAllText(p)); if (d != null) return d; } } catch (Exception ex) { Debug.LogError("HeadSheet: cannot read " + p + ": " + ex.Message); } }
        return null;
    }

    void Fail(string why)
    {
        Debug.LogError("HeadSheet: " + why);
        try { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, (rowId ?? "head") + "_log.txt"), "ERROR " + why + "\n"); } catch { }
        Quit(2);
    }

    void Quit(int code)
    {
        phase = Phase.Done; Time.captureFramerate = 0;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    int GroundMask() { int m = PhysMasks.NotPhys; if (D.groundMaskExcludeLayers != null) foreach (int l in D.groundMaskExcludeLayers) m &= ~(1 << l); return m; }

    float GroundAt(float x, float z, float fallback)
    {
        return Physics.Raycast(new Vector3(x, 30f, z), Vector3.down, out var hit, 60f, GroundMask(), QueryTriggerInteraction.Ignore) ? hit.point.y : fallback;
    }

    void Place(DuelFighter f, Vector3 p, Vector3 facing, bool keepControllerOff = false)
    {
        var cc = f.GetComponent<CharacterController>(); p.y = GroundAt(p.x, p.z, p.y);
        if (cc) cc.enabled = false; f.transform.position = p; f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc && !keepControllerOff) cc.enabled = true; Physics.SyncTransforms();
    }

    RenderTexture MakeRT(int w, int h) { var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32); rt.antiAliasing = 1; rt.Create(); return rt; }

    Camera MakeCam(string name, RenderTexture rt, float fov)
    {
        var main = Camera.main; if (main == null) main = FindFirstObjectByType<Camera>();
        var go = new GameObject("HeadSheetCam_" + name); go.transform.SetParent(transform, false);
        var c = go.AddComponent<Camera>(); if (main != null) c.CopyFrom(main);
        c.targetTexture = rt; c.fieldOfView = fov; c.enabled = true; c.depth = -50f; c.tag = "Untagged"; c.nearClipPlane = 0.05f;
        var hd = go.GetComponent<HDAdditionalCameraData>(); if (hd == null) hd = go.AddComponent<HDAdditionalCameraData>();
        var hdMain = main != null ? main.GetComponent<HDAdditionalCameraData>() : null; if (hdMain != null) hdMain.CopyTo(hd);
        return c;
    }

    void Init()
    {
        outDir = Arg("-out:"); if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Application.dataPath, "..", "PlayShots"); Directory.CreateDirectory(outDir);
        D = LoadData(); if (D == null) { Fail("head_sheet.json not found"); return; }
        foreach (var r in D.rows) if (r != null && r.id == rowId) row = r;
        if (row == null) { Fail("no row '" + rowId + "' in head_sheet.json"); return; }
        player = Find(string.IsNullOrEmpty(D.playerName) ? Roster.PlayerObject : D.playerName);
        enemy = Find(!string.IsNullOrEmpty(row.opponent) ? row.opponent : string.IsNullOrEmpty(D.enemyName) ? Roster.EnemyObject : D.enemyName);
        if (player == null || enemy == null) { Fail("fighters not found"); return; }
        clamp = player.GetComponentInChildren<HeadClamp>(true); if (clamp == null) clamp = player.GetComponent<HeadClamp>();
        natural = row.kind == "natural";   // the game exactly as it starts: no teleport, brains stay on, cameras follow the player's own pose
        if (!natural) foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        axisFwd = natural ? player.transform.forward : Vector3.forward; start = natural ? player.transform.position : new Vector3(row.x, 0f, row.z);
        if (natural) { }
        else Place(player, start, axisFwd);
        if (natural) { }
        else if (row.kind == "orbit") Place(enemy, start + axisFwd * row.radius, -axisFwd, true); else Place(enemy, start + axisFwd * row.oppDist, -axisFwd);
        if (row.hideOpponent) foreach (var r in enemy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        player.stamina = player.maxStamina; enemy.stamina = enemy.maxStamina; groundY = player.transform.position.y;
        Time.captureFramerate = Mathf.RoundToInt(D.tickHz); QualitySettings.vSyncCount = 0;
        tick0 = Duel.TickCount; B = tick0 + (row.settleTicks >= 0 ? row.settleTicks : D.settleTicks);
        int ss = Mathf.Max(1, D.supersample);
        if (row.kind == "orbit")
        {
            orbitTotal = Mathf.Max(2, Mathf.RoundToInt(row.seconds * D.tickHz)); wantCaps = Mathf.Max(1, row.frames);
            var c = row.cams != null && row.cams.Length > 0 ? row.cams[0] : new Cam { name = "orbit", dist = 3.5f, height = 1.7f, lookY = 1.3f, fov = 40f };
            var rt = MakeRT(row.tileW * ss, row.tileH * ss); cams.Add((c.name, MakeCam(c.name, rt, c.fov), rt, row.tileW, row.tileH)); camDef.Add(c);
            small = MakeRT(row.tileW, row.tileH); readTex = new Texture2D(row.tileW, row.tileH, TextureFormat.RGBA32, false, false);
            int cols = Mathf.Max(1, row.cols), rows = Mathf.CeilToInt(wantCaps / (float)cols); sheetW = cols * row.tileW; sheetH = rows * row.tileH;
            sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false, false);
            var fill = new Color32[sheetW * sheetH]; for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(20, 20, 20, 255); sheet.SetPixels32(fill);
        }
        else
        {
            foreach (var c in row.cams) { var rt = MakeRT(c.w * ss, c.h * ss); cams.Add((c.name, MakeCam(c.name, rt, c.fov), rt, c.w, c.h)); camDef.Add(c); }
            wantCaps = (row.captureTicks != null ? row.captureTicks.Length : 1) * cams.Count + (row.liveShot ? 1 : 0);
        }
        log.Add("# row=" + row.id + " kind=" + row.kind + " clamp=" + (clamp != null ? "attached restFromBindposes=" + clamp.restKnown + " mask=" + clamp.foundMask : "NONE") + " ForceOff=" + HeadClamp.ForceOff + " settleTicks=" + D.settleTicks);
        log.Add("tick\tt\torbitDeg\tdracYawWorld\tstate\tclip\tlayers\theadYawRaw\theadPitchRaw\theadYawNow\theadPitchNow\tlimitYaw\tlimitPitch\ttorsoYawRoot");
        phase = Phase.Run;
    }
    readonly List<Cam> camDef = new List<Cam>(); bool natural;

    void Update()
    {
        if (phase == Phase.Done) return;
        frame++;
        if (phase == Phase.Init) { Init(); if (phase != Phase.Run) return; }
        if (frame > D.timeoutTicks + D.settleTicks + orbitTotal + 300) { log.Add("# TIMEOUT"); Finish(); return; }
        if (row.kind == "orbit" && Duel.TickCount >= B)
        {
            // the target moves round the player; the player (locked on, DuelFighter.faceOpp) turns with it
            orbitTick = Mathf.Min(orbitTotal, Duel.TickCount - B);
            float th = 360f * orbitTick / orbitTotal * Mathf.Deg2Rad; Vector3 d = player.transform.position;
            Vector3 p = d + new Vector3(Mathf.Sin(th), 0f, Mathf.Cos(th)) * row.radius; p.y = GroundAt(p.x, p.z, d.y);
            enemy.transform.position = p; enemy.transform.rotation = Quaternion.LookRotation(new Vector3(d.x - p.x, 0f, d.z - p.z));
        }
    }

    Animator Anim() { var f = player.GetComponent<Fighter>(); return f != null ? f.anim : null; }

    void LateUpdate()
    {
        if (phase != Phase.Run) return;
        PlaceCams();
        float yawN = 0, pitN = 0, torso = 0, hips = 0; if (clamp != null) clamp.Measure(out yawN, out pitN, out torso, out hips);
        int age = Duel.TickCount - B;
        if (age >= 0 && (age % Mathf.Max(1, D.logEvery)) == 0 && (row.kind == "orbit" ? age <= orbitTotal : age < 60 * 12)) LogRow(age, yawN, pitN, torso);
        if (age >= 0 && clamp != null) { maxAbsYawRaw = Mathf.Max(maxAbsYawRaw, Mathf.Abs(clamp.rawYaw)); maxAbsPitchRaw = Mathf.Max(maxAbsPitchRaw, Mathf.Abs(clamp.rawPitch)); maxAbsYawNow = Mathf.Max(maxAbsYawNow, Mathf.Abs(yawN)); maxAbsPitchNow = Mathf.Max(maxAbsPitchNow, Mathf.Abs(pitN)); }
        if (row.kind == "orbit")
        {
            if (nextCap < wantCaps && age >= Mathf.RoundToInt(nextCap * (float)orbitTotal / wantCaps)) { int k = nextCap++; log.Add("# capture tile " + k + " at orbit " + (360f * Mathf.Min(orbitTotal, age) / orbitTotal).ToString("F0") + " deg, tick " + Duel.TickCount); StartCoroutine(CaptureTile(k)); }
        }
        else
        {
            int n = row.captureTicks != null ? row.captureTicks.Length : 0;
            while (nextCap < n && age >= row.captureTicks[nextCap]) { int k = nextCap++; log.Add("# capture " + k + " at tick " + Duel.TickCount + " (after settle +" + age + ")"); StartCoroutine(CaptureStill(k)); }
        }
    }

    void LogRow(int age, float yawN, float pitN, float torso)
    {
        var I = System.Globalization.CultureInfo.InvariantCulture; var an = Anim(); string st = "-", clip = "-", layers = "";
        if (an != null)
        {
            foreach (var ci in an.GetCurrentAnimatorClipInfo(0)) clip += ci.clip != null ? ci.clip.name + ":" + ci.weight.ToString("F2", I) + "," : "?,";
            for (int i = 1; i < an.layerCount; i++) if (an.GetLayerWeight(i) > 0.001f) layers += an.GetLayerName(i) + "=" + an.GetLayerWeight(i).ToString("F2", I) + ";";
            st = player.state.ToString();
        }
        float th = row.kind == "orbit" ? 360f * Mathf.Min(orbitTotal, age) / orbitTotal : 0f;
        log.Add(string.Join("\t", new[] { Duel.TickCount.ToString(), (age / D.tickHz).ToString("F2", I), th.ToString("F0", I), player.transform.eulerAngles.y.ToString("F0", I), st, clip, layers,
            clamp != null ? clamp.rawYaw.ToString("F1", I) : "-", clamp != null ? clamp.rawPitch.ToString("F1", I) : "-", yawN.ToString("F1", I), pitN.ToString("F1", I),
            clamp != null ? clamp.limitYaw.ToString("F0", I) : "-", clamp != null ? clamp.limitPitch.ToString("F0", I) : "-", torso.ToString("F0", I) }));
    }

    void PlaceCams()
    {
        for (int i = 0; i < cams.Count; i++)
        {
            var c = camDef[i]; Vector3 d = player.transform.position; if (row.kind != "orbit" && !natural) d = new Vector3(start.x, groundY, start.z);
            Vector3 fw = natural ? Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized : axisFwd; if (natural) groundY = player.transform.position.y;
            Vector3 dir = Quaternion.Euler(0f, c.angleDeg, 0f) * fw;
            if (row.kind == "orbit") d = new Vector3(start.x, groundY, start.z);   // fixed camera: relative to the start pose, never follows
            Vector3 look = new Vector3(d.x, d.y + c.lookY, d.z), flat = new Vector3(d.x, 0f, d.z) + dir * c.dist;
            float gy = Physics.Raycast(new Vector3(flat.x, d.y + 3f, flat.z), Vector3.down, out var gh, 8f, GroundMask(), QueryTriggerInteraction.Ignore) ? gh.point.y : d.y;
            /* ground probe starts just above his head: never lands on an arch or roof */  Vector3 pos = new Vector3(flat.x, Mathf.Max(d.y + c.height, gy + c.minAboveGround), flat.z);
            Vector3 to = pos - look; float len = to.magnitude;
            if (Physics.Raycast(look, to / len, out var hit, len, GroundMask(), QueryTriggerInteraction.Ignore)) pos = look + to / len * Mathf.Max(D.pullMinDist, hit.distance - D.pullMargin);
            cams[i].cam.transform.position = pos; cams[i].cam.transform.rotation = Quaternion.LookRotation(look - pos);
        }
    }

    IEnumerator CaptureStill(int k)
    {
        yield return new WaitForEndOfFrame();
        for (int i = 0; i < cams.Count; i++)
        {
            var e = cams[i]; var sm = MakeRT(e.w, e.h); Graphics.Blit(e.rt, sm); var tex = new Texture2D(e.w, e.h, TextureFormat.RGB24, false, false);
            var prev = RenderTexture.active; RenderTexture.active = sm; tex.ReadPixels(new Rect(0, 0, e.w, e.h), 0, 0, false); tex.Apply(); RenderTexture.active = prev;
            string fn = row.id + "_" + e.name + (row.captureTicks.Length > 1 ? "_t" + k : "") + ".png"; File.WriteAllBytes(Path.Combine(outDir, fn), tex.EncodeToPNG());
            Destroy(tex); sm.Release(); Destroy(sm); doneCaps++; log.Add("# wrote " + fn + " cam pos " + e.cam.transform.position.ToString("F2"));
        }
        if (k == 0 && row.liveShot) { string fn = row.id + "_live.png"; CaptureUtil.HideConsole(); ScreenCapture.CaptureScreenshot(Path.Combine(outDir, fn)); doneCaps++; log.Add("# wrote " + fn + " (live game camera)"); DumpParticles(); yield return null; yield return null; }
        if (doneCaps >= wantCaps) Finish();
    }

    IEnumerator CaptureTile(int k)
    {
        yield return new WaitForEndOfFrame();
        var e = cams[0]; Graphics.Blit(e.rt, small);
        var prev = RenderTexture.active; RenderTexture.active = small; readTex.ReadPixels(new Rect(0, 0, e.w, e.h), 0, 0, false); RenderTexture.active = prev;
        var px = readTex.GetPixels32(); for (int i = 0; i < px.Length; i++) px[i].a = 255;
        int cols = Mathf.Max(1, row.cols), rows = sheetH / e.h, col = k % cols, rw = k / cols;
        sheet.SetPixels32(col * e.w, (rows - 1 - rw) * e.h, e.w, e.h, px);
        doneCaps++; if (doneCaps >= wantCaps) { File.WriteAllBytes(Path.Combine(outDir, row.id + "_sheet.png"), sheet.EncodeToPNG()); log.Add("# wrote " + row.id + "_sheet.png"); Finish(); }
    }

    void DumpParticles()
    {
        log.Add("# active particle systems (name | emitting | alive | rate | world pos)");
        foreach (var ps in FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None))
        {
            if (!ps.gameObject.activeInHierarchy) continue; var em = ps.emission;
            log.Add("#   " + ps.name + " | " + ps.isEmitting + " | " + ps.particleCount + " | " + em.rateOverTime.constant.ToString("F1") + " | " + ps.transform.position.ToString("F1") + " | parent " + (ps.transform.parent != null ? ps.transform.parent.name : "-"));
        }
    }

    void Finish()
    {
        if (phase == Phase.Done) return;
        log.Add("# head vs torso over the run (deg): max |yaw| raw=" + maxAbsYawRaw.ToString("F1") + " after clamp=" + maxAbsYawNow.ToString("F1") + "; max |pitch| raw=" + maxAbsPitchRaw.ToString("F1") + " after clamp=" + maxAbsPitchNow.ToString("F1"));
        try { File.WriteAllText(Path.Combine(outDir, rowId + "_log.txt"), string.Join("\n", log) + "\n"); } catch (Exception ex) { Debug.LogError("HeadSheet: log write failed " + ex.Message); }
        Quit(0);
    }
}
