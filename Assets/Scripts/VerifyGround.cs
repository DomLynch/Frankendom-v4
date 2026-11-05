using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// Acceptance harness (player side): -verifyGround [-verifyOut:<dir>] [-verifydata:<file>].  Lane F.
//  1. Switches Duel / PlayerBrain / AIBrain / TouchControls / CameraRig (and any Alpha controller) off and walks the fighters one at a time
//     (the player, then the enemy) through a grid of lane points (verify_ground.json: xs x zs, plus edgeXs at the middle z).
//     At each point the fighter is teleported (CharacterController off, position, on) and settles for settleSeconds (gravity runs).
//  2. Per point: <dir>/<fighter>_x<x>_z<z>_side.png (fixed side camera, render-texture readback) and _game.png (the normal game camera, with the OnGUI label).
//  3. <dir>/verify.csv: root y, foot / toe bone y, sole y (lowest baked skinned vertex under the feet), physics ground hit y (fighter layers + TreadRay ignored),
//     the VISIBLE top y (upward-facing triangles of readable non-fighter meshes, plus Terrain) and PASS / FAIL / WARN.
//  4. Then a fightSeconds autoplay fight (brains + Duel + CameraRig back on, the player driven by the AI exactly like -autoplay) with fight_<n>.png every shotEvery s.
//  5. Exit code: 0 no FAIL (WARN allowed), 1 any FAIL, 2 internal error. <dir>/verify_summary.txt has the verdict.
// All numbers are data: Assets/Data/verify_ground.json (StreamingAssets copy for a player, or -verifydata:<file>). The defaults below only mirror that file.
[DefaultExecutionOrder(-90)]
public class VerifyGround : MonoBehaviour
{
    // ---------------- data ----------------
    [Serializable]
    public class Data
    {
        public float[] xs = { -3f, 0f, 3f };
        public float[] zs = { 11.5f, 14.5f, 17.5f, 20.5f, 23f };
        public float[] edgeXs = { -3.1f, 3.1f };
        public float settleSeconds = 1f, tolerance = 0.05f, fightSeconds = 60f, shotEvery = 3f, fightYTol = 0.15f;
        public string[] fighters = new string[0];   // scene object names in test order; empty = { Roster.PlayerObject, Roster.EnemyObject }
        public int[] groundMaskExcludeLayers = { 8, 9 };
        public bool gameLocomotion = false;        // true: DuelFighter stays on (its own gravity AND its +-2.45 m stair clamp); false: harness gravity only, no clamp
        public float castFromY = 6f, dropHeight = 0.3f, groundRayUp = 3f, groundRayLen = 20f;
        public float oppGap = 3f, oppXMax = 2f, oppZMin = 9f, oppZMax = 24f;
        public bool verifyNoMotionBlur = true;     // v92: a global high-priority Volume forces MotionBlur 0 for the verify run only (smeared shots hid the details visual-qc reads); the game profile keeps its value
        public float gameCamSeconds = 0.7f;        // CameraRig is switched back on for the last part of the settle so the game cam shot is the normal rig pose
        public float sideDist = 4.5f, sideHeight = 1.2f, sideFov = 35f, sideLookY = 1f, sideSign = 1f, sideNear = 0.1f;
        public int sideW = 1280, sideH = 720;
        public bool pullIn = true; public float pullMinDist = 2.2f, pullMargin = 0.15f;
        public float ankleHeight = 0.10f;          // ankle (Foot bone) above the sole when planted (foot_ground.json ankleHeight); used when no sole could be baked
        public bool useSole = true;                // sole = lowest baked SkinnedMeshRenderer vertex under the feet; false = ankle bone - ankleHeight
        public float topAbove = 0.8f, topBelow = 1.5f, minNormalY = 0.2f;   // visible-top band around the physics ground (or root) y; only upward-facing triangles count
        public float boundsBand = 0.1f;            // an unreadable renderer counts (-> WARN) when its bounds straddle the reference surface within this
        public float maxRendererSizeX = 120f;
        public float[] laneMin = { -4f, -1f, 8f }, laneMax = { 4f, 4f, 30f };
        public string[] excludeNames = { "Fog", "Haze", "Ash", "Smoke", "Embers", "Door", "Shaft", "Glow", "Sky" };
        public float startWaitSeconds = 20f, maxTotalSeconds = 900f;
        public int shotWidth = 0;                  // reserved
        public HeadCfg head = new HeadCfg();
    }

    // Head close-up check (the player's original head): data block "head" of verify_ground.json
    [Serializable]
    public class HeadCfg
    {
        public bool enabled = true;
        public float distance = 0.9f, fov = 25f, maxYawDeg = 35f, lookUp = 0f, gapTol = 0.03f, restGap = -1f;   // restGap < 0 = the neck-head distance measured at boot
        public int tile = 512;
        public string bone = "mixamorig:Head", neckBone = "mixamorig:Neck", rendererName = "head", metaHumanName = "MetaHumanHead";
        public string forwardAxis = "auto";        // head bone local axis that points out of the face: auto (the one nearest the body forward at boot) | +z | -z | +x | -x | +y | -y
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
        if (!Flag("-verifyGround")) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<VerifyGround>() != null) return;
        var go = new GameObject("VerifyGround");
        DontDestroyOnLoad(go);
        go.AddComponent<VerifyGround>();
    }

    // ---------------- state ----------------
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    Data D; string outDir; float t0; bool done;
    string overlay = "";
    DuelFighter[] fighters;                       // in test order
    readonly Dictionary<DuelFighter, float> vy = new Dictionary<DuelFighter, float>();
    readonly Dictionary<DuelFighter, Vector3> origPos = new Dictionary<DuelFighter, Vector3>();
    readonly Dictionary<DuelFighter, Quaternion> origRot = new Dictionary<DuelFighter, Quaternion>();
    readonly List<Behaviour> switchedOff = new List<Behaviour>();
    CameraRig rig; Camera mainCam, sideCam; RenderTexture sideRT; Texture2D sideTex;
    int groundMask;
    readonly List<RC> cands = new List<RC>();
    Terrain[] terrains;
    Mesh bakeMesh;
    readonly List<Row> rows = new List<Row>();
    string csvPath;
    // fight stats
    int fightFrames; float fightMinDt = 9f, fightMaxDt; float fightFpsTime; int fightFpsFrames;
    readonly Dictionary<string, float> fightYWorst = new Dictionary<string, float>(), fightYWorstT = new Dictionary<string, float>(), fightYMin = new Dictionary<string, float>(), fightYMax = new Dictionary<string, float>(); bool fightYFail;
    string fightNote = "";
    // head check
    RenderTexture headRT; Texture2D headTex; Transform headBone, neckBone; Vector3 headAxis = Vector3.forward; float restGap = -1f;
    string headVerdict = "SKIPPED"; readonly List<string> headLines = new List<string>();
    readonly List<string> notes = new List<string>();

    class Row
    {
        public string fighter, verdict, method, topName, boundsName, groundName, side, game, note;
        public int idx, total;
        public float tx, tz, x, z, rootY, lfY, rfY, ltbY, rtbY, lteY, rteY, sole, ground, ccBottom, top, boundsTop, topL, topR;
        public int unreadable;
        public float footRef, dFoot, dGround, dBone;
        public string footRefKind;
    }

    class RC
    {
        public Renderer r; public Mesh mesh; public Bounds b; public bool readable;
        public Vector3[] wv; public int[] tris; public bool flip; public bool built;
        public void Build()
        {
            if (built) return; built = true;
            var v = mesh.vertices; var l2w = r.transform.localToWorldMatrix; wv = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) wv[i] = l2w.MultiplyPoint3x4(v[i]);
            tris = mesh.triangles; flip = l2w.determinant < 0f;
        }
    }

    // ---------------- data loading ----------------
    static Data LoadData(out string src)
    {
        var paths = new List<string>();
        string cli = Arg("-verifydata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "verify_ground.json"));
        paths.Add(Path.Combine(Application.dataPath, "Data/verify_ground.json"));
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) { var d = JsonUtility.FromJson<Data>(File.ReadAllText(p)); if (d != null) { src = p; return d; } } }
            catch (Exception ex) { Debug.LogError("VerifyGround: cannot read " + p + ": " + ex.Message); }
        }
        src = "(built-in defaults: verify_ground.json not found)";
        return new Data();
    }

    static string DefaultOutDir()
    {
        string exeDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string exeName = Path.GetFileName(exeDir.TrimEnd('\\', '/'));
        return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "TestOut", "verify_" + exeName));
    }

    // ---------------- lifecycle ----------------
    void Start()
    {
        t0 = Time.realtimeSinceStartup;
        outDir = Arg("-verifyOut:"); if (string.IsNullOrEmpty(outDir)) outDir = DefaultOutDir();
        try { Directory.CreateDirectory(outDir); } catch (Exception ex) { Debug.LogError("VerifyGround: out dir: " + ex.Message); }
        StartCoroutine(Guard(Main()));
    }

    void Update()
    {
        if (done) return;
        if (Time.realtimeSinceStartup - t0 > (D != null ? D.maxTotalSeconds : 900f)) Abort("overall timeout");
    }

    IEnumerator Guard(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Abort("exception: " + ex); yield break; }
            yield return cur;
        }
    }

    void Abort(string why)
    {
        if (done) return;
        Debug.LogError("VerifyGround: INTERNAL ERROR " + why);
        try { File.WriteAllText(Path.Combine(outDir, "verify_summary.txt"), "VERDICT: ERROR\nexit code 2\n" + why + "\nrows done: " + rows.Count + "\n"); } catch { }
        Quit(2);
    }

    void Quit(int code)
    {
        done = true;
        Debug.Log("VerifyGround: exit " + code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }

    void OnGUI()
    {
        if (string.IsNullOrEmpty(overlay)) return;
        var st = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(12, Screen.height / 30), fontStyle = FontStyle.Bold };
        var r = new Rect(12, Mathf.Max(8, Screen.height * 120f / 1080f), Screen.width - 24, st.fontSize * 3);   // below the HUD name/bars (QC: label drew over the player name)
        st.normal.textColor = Color.black; GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), overlay, st);
        st.normal.textColor = new Color(1f, 0.95f, 0.3f); GUI.Label(r, overlay, st);
    }

    void OnDestroy() { if (sideRT) sideRT.Release(); }

    // ---------------- main flow ----------------
    IEnumerator Main()
    {
        string src; D = LoadData(out src);
        if (D.fighters == null || D.fighters.Length < 2) D.fighters = new[] { Roster.PlayerObject, Roster.EnemyObject };   // test order: player, then enemy
        notes.Add("data: " + src);
        groundMask = ~0; foreach (int l in D.groundMaskExcludeLayers) groundMask &= ~(1 << l);

        // wait for the scene: both fighters + the camera rig
        DuelFighter dr = null, pi = null;
        float wait0 = Time.realtimeSinceStartup;
        while (true)
        {
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) { if (f.name == D.fighters[0]) dr = f; else if (f.name == D.fighters[1]) pi = f; }
            if (dr != null && pi != null && Camera.main != null) break;
            if (Time.realtimeSinceStartup - wait0 > D.startWaitSeconds) { Abort("fighters / camera not found (" + D.fighters[0] + "/" + D.fighters[1] + ")"); yield break; }
            yield return null;
        }
        for (int i = 0; i < 30; i++) yield return null;   // let every Awake/Start settle (rig target, FootGround attach)
        fighters = new[] { dr, pi };
        mainCam = Camera.main; rig = mainCam.GetComponent<CameraRig>(); if (rig == null) rig = FindFirstObjectByType<CameraRig>();
        if (D.verifyNoMotionBlur)   // v92
        {
            try
            {
                var nbGo = new GameObject("VerifyNoMotionBlur"); var nbV = nbGo.AddComponent<UnityEngine.Rendering.Volume>();
                nbV.isGlobal = true; nbV.priority = 10000f; nbV.weight = 1f;
                var nbP = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                var nbMb = nbP.Add<MotionBlur>(true); nbMb.intensity.Override(0f); nbMb.active = true; nbV.sharedProfile = nbP;
            }
            catch (Exception ex) { Debug.LogWarning("[VERIFY] no-motion-blur volume failed: " + ex.Message); }
        }

        BrainsOff();
        foreach (var f in fighters) { origPos[f] = f.transform.position; origRot[f] = f.transform.rotation; vy[f] = -1f; }
        BuildCandidates();
        MakeSideCam();
        if (D.head != null && D.head.enabled) HeadSetup(fighters[0]);

        // points
        var pts = new List<Vector2>();
        foreach (float z in D.zs) foreach (float x in D.xs) pts.Add(new Vector2(x, z));
        float midZ = D.zs.Length > 0 ? D.zs[D.zs.Length / 2] : 17.5f;
        foreach (float x in D.edgeXs) pts.Add(new Vector2(x, midZ));

        csvPath = Path.Combine(outDir, "verify.csv");
        File.WriteAllText(csvPath, CsvHeader() + "\n");

        for (int fi = 0; fi < fighters.Length; fi++)
        {
            var subj = fighters[fi]; var opp = fighters[1 - fi];
            for (int pi2 = 0; pi2 < pts.Count; pi2++)
            {
                Vector2 p = pts[pi2];
                overlay = "VERIFY " + subj.name + "  pt " + (pi2 + 1) + "/" + pts.Count + "  x=" + p.x.ToString("0.0#", IC) + " z=" + p.y.ToString("0.0#", IC);
                Teleport(subj, new Vector3(p.x, 0f, p.y), fi == 0 ? Vector3.forward : Vector3.back);
                float oz = p.y + (fi == 0 ? D.oppGap : -D.oppGap);
                if (oz > D.oppZMax || oz < D.oppZMin) oz = p.y - (oz - p.y);
                float ox = Mathf.Clamp(p.x, -D.oppXMax, D.oppXMax);
                Teleport(opp, new Vector3(ox, 0f, oz), new Vector3(subj.transform.position.x - ox, 0f, subj.transform.position.z - oz));

                float t = 0f; bool rigOn = false;
                while (t < D.settleSeconds)
                {
                    if (!rigOn && rig != null && t >= D.settleSeconds - D.gameCamSeconds) { rig.enabled = true; rigOn = true; }
                    if (!D.gameLocomotion) { Gravity(subj); Gravity(opp); }
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (!D.gameLocomotion) { Gravity(subj); Gravity(opp); }
                var row = Measure(subj, p, pi2, pts.Count);
                PlaceSideCam(subj, row);
                yield return new WaitForEndOfFrame();
                CaptureSide(Path.Combine(outDir, row.side));
                CaptureGame(Path.Combine(outDir, row.game), row);
                if (rig != null) rig.enabled = false;
                rows.Add(row);
                File.AppendAllText(csvPath, CsvRow(row) + "\n");
                Debug.Log("[VERIFY] " + row.verdict + " " + subj.name + " pt" + (pi2 + 1) + " x=" + row.x.ToString("F2", IC) + " z=" + row.z.ToString("F2", IC)
                    + " " + row.footRefKind + "=" + Fmt(row.footRef) + " ground=" + Fmt(row.ground) + " top=" + Fmt(row.top) + " (" + row.method + ", " + row.topName + ") dFoot=" + Fmt(row.dFoot) + " dGround=" + Fmt(row.dGround) + " " + row.note);
                yield return null;
            }
        }
        overlay = "";
        if (sideCam) sideCam.enabled = false;

        // ---- head close-up check (the player's original head) ----
        if (D.head != null && D.head.enabled)
        {
            var dracH = fighters[0]; var pitH = fighters[1];
            overlay = "VERIFY HEAD";
            Teleport(dracH, new Vector3(0f, 0f, midZ), Vector3.forward);
            Teleport(pitH, new Vector3(0f, 0f, Mathf.Min(midZ + D.oppGap, D.oppZMax)), Vector3.back);
            float ht = 0f;
            while (ht < D.settleSeconds) { if (!D.gameLocomotion) { Gravity(dracH); Gravity(pitH); } ht += Time.unscaledDeltaTime; yield return null; }
            if (!D.gameLocomotion) { Gravity(dracH); Gravity(pitH); }
            HeadMeasure(dracH);
            string[] hn = { "front", "right", "left", "back" };
            var tiles = new Color[4][];
            sideCam.targetTexture = headRT; sideCam.fieldOfView = D.head.fov; sideCam.nearClipPlane = 0.05f; sideCam.enabled = true;
            for (int hi = 0; hi < 4; hi++)
            {
                PlaceHeadCam(dracH, hi);
                yield return new WaitForEndOfFrame();
                var prevRT = RenderTexture.active; RenderTexture.active = headRT;
                headTex.ReadPixels(new Rect(0, 0, D.head.tile, D.head.tile), 0, 0, false); headTex.Apply(false);
                RenderTexture.active = prevRT;
                tiles[hi] = headTex.GetPixels();
                File.WriteAllBytes(Path.Combine(outDir, "head_" + hn[hi] + ".png"), headTex.EncodeToPNG());
                yield return null;
            }
            sideCam.enabled = false; sideCam.targetTexture = sideRT;
            int ts = D.head.tile; var sheet = new Texture2D(ts * 2, ts * 2, TextureFormat.RGB24, false, false);
            sheet.SetPixels(0, ts, ts, ts, tiles[0]); sheet.SetPixels(ts, ts, ts, ts, tiles[1]); sheet.SetPixels(0, 0, ts, ts, tiles[2]); sheet.SetPixels(ts, 0, ts, ts, tiles[3]);
            sheet.Apply(false);
            File.WriteAllBytes(Path.Combine(outDir, "head_sheet.png"), sheet.EncodeToPNG()); Destroy(sheet);
            overlay = "";
        }

        // ---- fight phase ----
        // v79 fighters check: exactly two active fighters (player + enemy), distinct names (v79 had a stale second Joan root active)
        int activeFighters = 0; var fnames = new System.Collections.Generic.HashSet<string>(); bool dupFighter = false;
        foreach (var fdf in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (fdf != null && fdf.gameObject.activeInHierarchy) { activeFighters++; if (!fnames.Add(fdf.name)) dupFighter = true; }
        bool fightersFail = activeFighters != 2 || dupFighter;
        Debug.Log("[VERIFY] FIGHTERS " + (fightersFail ? "FAIL" : "PASS") + " active=" + activeFighters + " names=" + string.Join(",", new System.Collections.Generic.List<string>(fnames).ToArray()));
        FightOn();
        float ft = 0f, nextShot = D.shotEvery; int shotN = 0; int ow = 0;
        int camFrames = 0, camOut = 0; float camDistWorst = 0f;   // v78 cam check: the game camera must keep the fighters in view (v77 shipped a dead camera, rock close-up, with the ground check PASS)
        yield return null;
        while (ft < D.fightSeconds)
        {
            float dt = Time.unscaledDeltaTime; ft += dt; fightFrames++;
            if (ft > 2f) { fightMinDt = Mathf.Min(fightMinDt, dt); fightMaxDt = Mathf.Max(fightMaxDt, dt); fightFpsTime += dt; fightFpsFrames++; }
            overlay = "VERIFY FIGHT  t=" + ft.ToString("F1", IC) + "s  " + HpText();
            if (fighters != null) foreach (var ff in fighters)   // both fighters must stay on the ground for the whole fight (audit: one fell out of the world at ~50 s)
            {
                if (!ff) continue;
                try
                {
                    float fy = ff.transform.position.y; float gy = 0f;
                    if (Physics.Raycast(ff.transform.position + Vector3.up * 3f, Vector3.down, out var gh, 20f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) gy = gh.point.y;
                    float dev = Mathf.Abs(fy - gy); if (float.IsNaN(fy)) dev = 999f;
                    if (!fightYWorst.ContainsKey(ff.name) || dev > fightYWorst[ff.name]) { fightYWorst[ff.name] = dev; fightYWorstT[ff.name] = ft; }
                    if (!fightYMin.ContainsKey(ff.name) || fy < fightYMin[ff.name]) fightYMin[ff.name] = fy;
                    if (!fightYMax.ContainsKey(ff.name) || fy > fightYMax[ff.name]) fightYMax[ff.name] = fy;
                }
                catch { }
            }
            if (ft > 2f)
            {
                var mc = Camera.main;
                if (mc == null || fighters == null) { camFrames++; camOut++; }
                else
                {
                    Vector3 cc = Vector3.zero; int cn = 0; bool anyDead = false; foreach (var ff in fighters) if (ff && ff.gameObject.activeInHierarchy) { cc += ff.transform.position; cn++; if (ff.Dead) anyDead = true; }   // v85 alive only: after a death / the victory screen the camera frames the corpse on purpose (a 60-s fight left 72% of a 150-s window 'out of view')
                    if (cn > 0 && !anyDead && GameLoop.Outcome == GameLoop.Result.None) { cc = cc / cn + Vector3.up; var vp = mc.WorldToViewportPoint(cc); float cd = Vector3.Distance(mc.transform.position, cc); camFrames++; if (vp.z < 0.1f || vp.x < 0.02f || vp.x > 0.98f || vp.y < 0.02f || vp.y > 0.98f || cd > 14f) camOut++; if (cd > camDistWorst) camDistWorst = cd; }
                }
            }
            if (ft >= nextShot)
            {
                nextShot += D.shotEvery; shotN++;
                yield return new WaitForEndOfFrame();
                CaptureGame(Path.Combine(outDir, "fight_" + shotN + ".png"), null);
                ow++;
            }
            yield return null;
        }
        overlay = "";
        fightNote = "shots=" + ow + " " + HpText();
        foreach (var kv in fightYWorst) if (kv.Value > D.fightYTol) fightYFail = true;
        WriteSummary();
        int fails = 0; foreach (var r in rows) if (r.verdict == "FAIL") fails++;
        if (fightYFail) fails++;
        bool camFail = camFrames > 0 && camOut > camFrames * 0.3f;
        Debug.Log("[VERIFY] CAM " + (camFail ? "FAIL" : "PASS") + " framesOut=" + camOut + "/" + camFrames + " worstDistToFighters=" + camDistWorst.ToString("F1", IC) + " m (limit 14 m, fail when out of view > 30% of frames)");
        if (camFail) fails++;
        if (fightersFail) fails++;
        Quit(fails > 0 || headVerdict == "FAIL" ? 1 : 0);
    }

    // ---------------- head check ----------------
    static Vector3 ParseAxis(string a)
    {
        if (string.IsNullOrEmpty(a)) return Vector3.zero;
        a = a.Trim().ToLowerInvariant(); float sg = a.StartsWith("-", StringComparison.Ordinal) ? -1f : 1f;
        if (a.EndsWith("x", StringComparison.Ordinal)) return Vector3.right * sg;
        if (a.EndsWith("y", StringComparison.Ordinal)) return Vector3.up * sg;
        if (a.EndsWith("z", StringComparison.Ordinal)) return Vector3.forward * sg;
        return Vector3.zero;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward; }

    void HeadSetup(DuelFighter drac)
    {
        headRT = new RenderTexture(D.head.tile, D.head.tile, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 }; headRT.Create();
        headTex = new Texture2D(D.head.tile, D.head.tile, TextureFormat.RGB24, false, false);
        headBone = FindBone(drac.transform, D.head.bone); neckBone = FindBone(drac.transform, D.head.neckBone);
        if (headBone == null) return;
        Vector3 ax = ParseAxis(D.head.forwardAxis);
        if (ax == Vector3.zero)
        {
            // auto: the local axis of the head bone that points most along the body forward at boot (the Mixamo head bone has no fixed "forward")
            Vector3 fwd = Flat(drac.transform.forward); float best = -2f;
            foreach (var c in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            { float d = Vector3.Dot(headBone.TransformDirection(c), fwd); if (d > best) { best = d; ax = c; } }
        }
        headAxis = ax;
        if (neckBone != null) restGap = D.head.restGap >= 0f ? D.head.restGap : Vector3.Distance(neckBone.position, headBone.position);
        notes.Add("head: bone " + D.head.bone + " forward axis (local) " + headAxis + ", rest neck-head gap " + Fmt(restGap));
    }

    void HeadMeasure(DuelFighter drac)
    {
        var HC = D.head; var I = IC; bool fail = false; var sb = new StringBuilder();
        if (headBone == null) { headVerdict = "FAIL"; headLines.Add("bone '" + HC.bone + "' not found under " + drac.name); Debug.Log("[VERIFY] HEAD FAIL " + headLines[0]); return; }
        var mh = GameObject.Find(HC.metaHumanName);   // finds active objects only
        bool mhActive = mh != null && mh.activeInHierarchy;
        float ang = Vector3.Angle(headBone.TransformDirection(headAxis), drac.transform.forward);
        // original head renderer: name == rendererName (else contains it), not under the MetaHuman head
        Renderer hr = null;
        foreach (var r in drac.GetComponentsInChildren<Renderer>(true))
        {
            if (InsideNamed(r.transform, HC.metaHumanName)) continue;
            if (string.Equals(r.name, HC.rendererName, StringComparison.OrdinalIgnoreCase)) { hr = r; break; }
        }
        if (hr == null)
            foreach (var r in drac.GetComponentsInChildren<Renderer>(true))
            {
                if (InsideNamed(r.transform, HC.metaHumanName) || r.name.IndexOf("metahuman", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (r.name.IndexOf(HC.rendererName, StringComparison.OrdinalIgnoreCase) >= 0) { hr = r; break; }
            }
        if (hr == null)   // v77 head fallback: a character whose head is part of the body mesh (Joan, chef rig) has no renderer named like the head: use its first skinned body mesh
            foreach (var r in drac.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { if (InsideNamed(r.transform, HC.metaHumanName) || r.name.IndexOf("metahuman", StringComparison.OrdinalIgnoreCase) >= 0) continue; if (r.enabled && r.gameObject.activeInHierarchy) { hr = r; break; } }
        bool hrOn = hr != null && hr.enabled && hr.gameObject.activeInHierarchy;
        float gap = neckBone != null ? Vector3.Distance(neckBone.position, headBone.position) : float.NaN;
        float gd = float.IsNaN(gap) || restGap < 0f ? float.NaN : gap - restGap;
        float headY = headBone.position.y;
        if (mhActive) { fail = true; sb.Append("MetaHumanHead ACTIVE; "); }
        if (ang > HC.maxYawDeg) { fail = true; sb.Append("head faces " + ang.ToString("F1", I) + " deg off the body (max " + HC.maxYawDeg.ToString("F0", I) + "); "); }
        if (!hrOn) { fail = true; sb.Append(hr == null ? "original head renderer '" + HC.rendererName + "' not found; " : "original head renderer '" + hr.name + "' disabled; "); }
        if (!float.IsNaN(gd) && Mathf.Abs(gd) > HC.gapTol) sb.Append("note: neck-head gap differs from rest by " + gd.ToString("F3", I) + " m; ");
        headVerdict = fail ? "FAIL" : "PASS";
        string line = "MetaHumanHeadActive=" + mhActive + " headAngleDeg=" + ang.ToString("F1", I) + " (" + (ang <= HC.maxYawDeg ? "PASS" : "FAIL") + ") headRenderer=" + (hr != null ? hr.name : "none") + " enabled=" + hrOn
            + " neckHeadGap=" + Fmt(gap) + " rest=" + Fmt(restGap) + " delta=" + Fmt(gd) + " headY=" + headY.ToString("F3", I) + " rootY=" + drac.transform.position.y.ToString("F3", I) + " " + sb;
        headLines.Add(line);
        Debug.Log("[VERIFY] HEAD " + headVerdict + " " + line);
    }

    static bool InsideNamed(Transform t, string n)
    {
        for (; t != null; t = t.parent) if (t.name == n) return true;
        return false;
    }

    // fixed world axes relative to the body root forward: 0 front, 1 right, 2 left, 3 back; camera distance from the head bone, looking at it
    void PlaceHeadCam(DuelFighter drac, int view)
    {
        Vector3 fwd = Flat(drac.transform.forward), right = Vector3.Cross(Vector3.up, fwd);
        Vector3 dir = view == 0 ? fwd : view == 1 ? right : view == 2 ? -right : -fwd;
        Vector3 look = headBone != null ? headBone.position + Vector3.up * D.head.lookUp : drac.transform.position + Vector3.up * 1.7f;
        Vector3 pos = look + dir * D.head.distance;
        sideCam.transform.position = pos; sideCam.transform.rotation = Quaternion.LookRotation(look - pos);
    }

    string HpText()
    {
        var sb = new StringBuilder();
        if (fighters != null) foreach (var f in fighters)
        {
            if (!f) continue;
            try
            {
                var h = f.GetComponent<Health>();
                if (h != null) sb.Append(f.name).Append(" hp ").Append(h.current.ToString("F0", IC)).Append("  ");
            }
            catch { }
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------- brains ----------------
    void BrainsOff()
    {
        foreach (var al in FindObjectsByType<ArenaLimits>(FindObjectsSortMode.None)) al.enabled = false;   // the grid phase teleports to points the lane limits could clamp: limits are back on for the fight
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (b is PlayerBrain || b is AIBrain || b is Duel || b is TouchControls || b is CameraRig)
            {
                if (b.enabled) { switchedOff.Add(b); b.enabled = false; }
            }
        }
        if (!D.gameLocomotion) foreach (var f in fighters) { f.moveDir = Vector3.zero; f.run = false; f.guardHeld = false; f.enabled = false; }
    }

    void FightOn()
    {
        foreach (var al in FindObjectsByType<ArenaLimits>(FindObjectsSortMode.None)) al.enabled = true;
        // positions back to where the scene put them
        foreach (var f in fighters)
        {
            var cc = f.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
            f.transform.position = origPos[f]; f.transform.rotation = origRot[f];
            if (cc) cc.enabled = true;
            vy[f] = -1f; f.moveDir = Vector3.zero;
        }
        Physics.SyncTransforms();
        foreach (var f in fighters) f.enabled = true;
        // -autoplay semantics: PlayerBrain stays off, the player's AIBrain runs in "auto" mode (the private flag AIBrain.Awake sets from -autoplay), rig lockRange 80
        foreach (var b in switchedOff)
        {
            if (b == null || b is PlayerBrain) continue;
            if (b is AIBrain)
            {
                var fld = typeof(AIBrain).GetField("auto", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld != null) fld.SetValue(b, true); else notes.Add("AIBrain.auto field not found: " + Roster.PlayerName + " AI not forced to auto");
            }
            b.enabled = true;
        }
        foreach (var f in fighters)
        {
            var ai = f.GetComponent<AIBrain>();   // the player's brain was disabled in Awake (no -autoplay) so it is not in switchedOff
            if (ai != null && !ai.enabled)
            {
                var fld = typeof(AIBrain).GetField("auto", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fld != null) fld.SetValue(ai, true);
                ai.enabled = true;
            }
        }
        if (rig != null) { rig.lockRange = 80f; rig.enabled = true; }
        Time.timeScale = 1f;
    }

    // ---------------- placement ----------------
    void Teleport(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        float y = f.transform.position.y;
        if (Physics.Raycast(new Vector3(p.x, D.castFromY, p.z), Vector3.down, out var hit, D.castFromY + 5f, groundMask, QueryTriggerInteraction.Ignore)) y = hit.point.y + D.dropHeight;
        else notes.Add("teleport: no ground under (" + p.x.ToString("F1", IC) + "," + p.z.ToString("F1", IC) + ")");
        p.y = y;
        if (cc) cc.enabled = false;
        f.transform.position = p;
        if (facing.sqrMagnitude > 1e-6f) f.transform.rotation = Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.z));
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
        vy[f] = -1f;
    }

    // same gravity as DuelFighter.Update: vy = grounded ? -1 : vy - 20 dt
    void Gravity(DuelFighter f)
    {
        var cc = f.GetComponent<CharacterController>(); if (cc == null || !cc.enabled) return;
        float dt = Mathf.Max(Time.deltaTime, 1f / 240f);
        float v = cc.isGrounded ? -1f : vy[f] - 20f * dt;
        vy[f] = v;
        cc.Move(new Vector3(0f, v * dt, 0f));
    }

    // ---------------- renderer candidates ----------------
    bool NameExcluded(string n)
    {
        foreach (var e in D.excludeNames) if (n.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    void BuildCandidates()
    {
        var lane = new Bounds(); lane.SetMinMax(new Vector3(D.laneMin[0], D.laneMin[1], D.laneMin[2]), new Vector3(D.laneMax[0], D.laneMax[1], D.laneMax[2]));
        int total = 0, unread = 0;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            total++;
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;
            if (r.gameObject.layer == 8 || r.GetComponentInParent<DuelFighter>() != null) continue;
            var b = r.bounds;
            if (b.size.x >= D.maxRendererSizeX) continue;
            if (NameExcluded(r.name)) continue;
            if (!b.Intersects(lane)) continue;
            Mesh m = null;
            var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh;
            var smr = r as SkinnedMeshRenderer; if (smr != null) m = smr.sharedMesh;
            if (m == null) continue;
            var rc = new RC { r = r, mesh = m, b = b, readable = m.isReadable };
            if (!rc.readable) unread++;
            cands.Add(rc);
        }
        terrains = Terrain.activeTerrains;
        notes.Add("renderers in scene " + total + ", lane candidates " + cands.Count + " (unreadable meshes " + unread + "), terrains " + (terrains != null ? terrains.Length : 0));
    }

    // ---------------- measurements ----------------
    static Transform FindBone(Transform root, string n)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
        return null;
    }

    struct Top { public float meshTop, boundsTop; public string meshName, boundsName; public int unreadable; public bool unreadRelevant; }

    Top QueryTop(float x, float z, float refY)
    {
        var res = new Top { meshTop = float.NaN, boundsTop = float.NaN, meshName = "", boundsName = "" };
        float lo = refY - D.topBelow, hi = refY + D.topAbove;
        const float eps = 1e-4f;
        foreach (var rc in cands)
        {
            if (!rc.readable) continue;
            var b = rc.b;
            if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z || b.max.y < lo || b.min.y > hi) continue;
            rc.Build();
            var wv = rc.wv; var tr = rc.tris;
            for (int i = 0; i + 2 < tr.Length; i += 3)
            {
                Vector3 a = wv[tr[i]], bb = wv[tr[i + 1]], c = wv[tr[i + 2]];
                float mnx = Mathf.Min(a.x, Mathf.Min(bb.x, c.x)); if (x < mnx - eps) continue;
                float mxx = Mathf.Max(a.x, Mathf.Max(bb.x, c.x)); if (x > mxx + eps) continue;
                float mnz = Mathf.Min(a.z, Mathf.Min(bb.z, c.z)); if (z < mnz - eps) continue;
                float mxz = Mathf.Max(a.z, Mathf.Max(bb.z, c.z)); if (z > mxz + eps) continue;
                float d = (bb.z - c.z) * (a.x - c.x) + (c.x - bb.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-9f) continue;
                float u = ((bb.z - c.z) * (x - c.x) + (c.x - bb.x) * (z - c.z)) / d;
                float v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                float w = 1f - u - v;
                if (u < -eps || v < -eps || w < -eps) continue;
                float y = u * a.y + v * bb.y + w * c.y;
                if (y < lo || y > hi) continue;
                Vector3 n = Vector3.Cross(bb - a, c - a); float len = n.magnitude; if (len < 1e-12f) continue;
                float ny = n.y / len; if (rc.flip) ny = -ny;
                if (ny < D.minNormalY) continue;
                if (float.IsNaN(res.meshTop) || y > res.meshTop) { res.meshTop = y; res.meshName = rc.r.name; }
            }
        }
        foreach (var t in terrains ?? new Terrain[0])
        {
            if (t == null || t.terrainData == null) continue;
            Vector3 tp = t.GetPosition(), sz = t.terrainData.size;
            if (x < tp.x || x > tp.x + sz.x || z < tp.z || z > tp.z + sz.z) continue;
            float y = t.SampleHeight(new Vector3(x, 0f, z)) + tp.y;
            if (y < lo || y > hi) continue;
            if (float.IsNaN(res.meshTop) || y > res.meshTop) { res.meshTop = y; res.meshName = "Terrain:" + t.name; }
        }
        float surf = float.IsNaN(res.meshTop) ? refY : Mathf.Max(refY, res.meshTop);
        foreach (var rc in cands)
        {
            if (rc.readable) continue;
            var b = rc.b;
            if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z) continue;
            res.unreadable++;
            if (b.max.y >= surf - D.boundsBand && b.min.y <= surf + D.boundsBand)
            {
                res.unreadRelevant = true;
                if (float.IsNaN(res.boundsTop) || b.max.y > res.boundsTop) { res.boundsTop = b.max.y; res.boundsName = rc.r.name; }
            }
        }
        return res;
    }

    float SoleY(DuelFighter f)
    {
        float best = float.NaN; Vector3 root = f.transform.position;
        if (bakeMesh == null) bakeMesh = new Mesh();
        foreach (var smr in f.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (!smr.enabled || smr.sharedMesh == null) continue;
            try
            {
                smr.BakeMesh(bakeMesh, true);
                var vs = bakeMesh.vertices;
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                for (int i = 0; i < vs.Length; i++)
                {
                    Vector3 w = m.MultiplyPoint3x4(vs[i]);
                    float dx = w.x - root.x, dz = w.z - root.z;
                    if (dx * dx + dz * dz > 0.64f || w.y > root.y + 0.5f || w.y < root.y - 1f) continue;
                    if (float.IsNaN(best) || w.y < best) best = w.y;
                }
            }
            catch (Exception) { }
        }
        return best;
    }

    static string N(float v) { return float.IsNaN(v) ? "" : v.ToString("F3", IC); }
    static string Fmt(float v) { return float.IsNaN(v) ? "n/a" : v.ToString("F3", IC); }

    static string CoordName(float v) { return v.ToString("0.0#", IC); }

    Row Measure(DuelFighter f, Vector2 p, int idx, int total)
    {
        var r = new Row { fighter = f.name, idx = idx + 1, total = total, tx = p.x, tz = p.y, note = "" };
        Vector3 pos = f.transform.position; r.x = pos.x; r.z = pos.z; r.rootY = pos.y;
        var cc = f.GetComponent<CharacterController>(); r.ccBottom = cc != null ? cc.bounds.min.y : float.NaN;
        Transform lf = FindBone(f.transform, "mixamorig:LeftFoot"), rf = FindBone(f.transform, "mixamorig:RightFoot");
        Transform ltb = FindBone(f.transform, "mixamorig:LeftToeBase"), rtb = FindBone(f.transform, "mixamorig:RightToeBase");
        Transform lte = FindBone(f.transform, "mixamorig:LeftToe_End"), rte = FindBone(f.transform, "mixamorig:RightToe_End");
        r.lfY = lf != null ? lf.position.y : float.NaN; r.rfY = rf != null ? rf.position.y : float.NaN;
        r.ltbY = ltb != null ? ltb.position.y : float.NaN; r.rtbY = rtb != null ? rtb.position.y : float.NaN;
        r.lteY = lte != null ? lte.position.y : float.NaN; r.rteY = rte != null ? rte.position.y : float.NaN;
        if (lf == null || rf == null) r.note += "foot bones missing; ";
        r.sole = D.useSole ? SoleY(f) : float.NaN;
        // physics ground (all colliders except the fighter layers and the TreadRay layer)
        r.ground = float.NaN; r.groundName = "";
        if (Physics.Raycast(new Vector3(pos.x, pos.y + D.groundRayUp, pos.z), Vector3.down, out var hit, D.groundRayLen, groundMask, QueryTriggerInteraction.Ignore)) { r.ground = hit.point.y; r.groundName = hit.collider.name; }
        else r.note += "no physics ground; ";
        float refY = float.IsNaN(r.ground) ? pos.y : r.ground;
        // visible top under the root point (and under each foot, informational)
        var top = QueryTop(pos.x, pos.z, refY);
        r.top = top.meshTop; r.topName = top.meshName; r.boundsTop = top.boundsTop; r.boundsName = top.boundsName; r.unreadable = top.unreadable;
        r.topL = lf != null ? QueryTop(lf.position.x, lf.position.z, refY).meshTop : float.NaN;
        r.topR = rf != null ? QueryTop(rf.position.x, rf.position.z, refY).meshTop : float.NaN;
        // foot reference: baked sole when available, else the ankle bone minus the data ankle height
        float boneMin = (lf != null && rf != null) ? Mathf.Min(r.lfY, r.rfY) : float.NaN;
        r.dBone = float.IsNaN(r.top) || float.IsNaN(boneMin) ? float.NaN : boneMin - r.top;
        if (!float.IsNaN(r.sole)) { r.footRef = r.sole; r.footRefKind = "sole"; }
        else { r.footRef = boneMin - D.ankleHeight; r.footRefKind = "ankle-" + D.ankleHeight.ToString("0.00", IC); }
        r.dFoot = float.IsNaN(r.top) ? float.NaN : r.footRef - r.top;
        r.dGround = float.IsNaN(r.top) || float.IsNaN(r.ground) ? float.NaN : r.ground - r.top;
        if (D.gameLocomotion && (Mathf.Abs(r.x - p.x) > 0.05f)) r.note += "x clamped by DuelFighter (" + p.x.ToString("F2", IC) + "->" + r.x.ToString("F2", IC) + "); ";
        else if (Mathf.Abs(r.x - p.x) > 0.05f || Mathf.Abs(r.z - p.y) > 0.05f) r.note += "CC drifted from the target; ";
        // verdict
        if (top.unreadRelevant) { r.method = "bounds"; r.verdict = "WARN"; r.note += "unreadable renderer " + top.boundsName + " top " + Fmt(top.boundsTop) + "; "; }
        else if (float.IsNaN(r.top)) { r.method = "none"; r.verdict = "FAIL"; r.note += "no visible surface found under the point; "; }
        else
        {
            r.method = "mesh";
            bool ok = !float.IsNaN(r.dFoot) && Mathf.Abs(r.dFoot) <= D.tolerance && !float.IsNaN(r.dGround) && Mathf.Abs(r.dGround) <= D.tolerance;
            r.verdict = ok ? "PASS" : "FAIL";
            if (!ok) { if (float.IsNaN(r.dGround)) r.note += "no physics ground; "; else if (Mathf.Abs(r.dGround) > D.tolerance) r.note += "collider " + (r.dGround > 0 ? "above" : "below") + " visible by " + Mathf.Abs(r.dGround).ToString("F3", IC) + "; "; if (Mathf.Abs(r.dFoot) > D.tolerance) r.note += "foot " + (r.dFoot > 0 ? "floats" : "sinks") + " " + Mathf.Abs(r.dFoot).ToString("F3", IC) + "; "; }
        }
        string stem = f.name + "_x" + CoordName(p.x) + "_z" + CoordName(p.y);
        r.side = stem + "_side.png"; r.game = stem + "_game.png";
        return r;
    }

    // ---------------- cameras / capture ----------------
    void MakeSideCam()
    {
        sideRT = new RenderTexture(D.sideW, D.sideH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 }; sideRT.Create();
        sideTex = new Texture2D(D.sideW, D.sideH, TextureFormat.RGB24, false, false);
        var go = new GameObject("VerifyGroundSideCam"); go.transform.SetParent(transform, false);
        sideCam = go.AddComponent<Camera>();
        sideCam.CopyFrom(mainCam);
        sideCam.targetTexture = sideRT; sideCam.fieldOfView = D.sideFov; sideCam.nearClipPlane = D.sideNear; sideCam.enabled = true; sideCam.depth = -50f; sideCam.tag = "Untagged";
        var hd = go.GetComponent<HDAdditionalCameraData>(); if (hd == null) hd = go.AddComponent<HDAdditionalCameraData>();
        var hdMain = mainCam.GetComponent<HDAdditionalCameraData>(); if (hdMain != null) hdMain.CopyTo(hd);
        var al = go.GetComponent<AudioListener>(); if (al) Destroy(al);
    }

    void PlaceSideCam(DuelFighter f, Row r)
    {
        float gy = float.IsNaN(r.ground) ? f.transform.position.y : r.ground;
        Vector3 look = new Vector3(r.x, gy + D.sideLookY, r.z);
        Vector3 pos = new Vector3(r.x + (D.sideSign >= 0f ? 1f : -1f) * D.sideDist, gy + D.sideHeight, r.z);
        if (D.pullIn)
        {
            Vector3 to = pos - look; float len = to.magnitude;
            if (Physics.Raycast(look, to / len, out var h, len, groundMask, QueryTriggerInteraction.Ignore))
            {
                pos = look + to / len * Mathf.Max(D.pullMinDist, h.distance - D.pullMargin);
                r.note += "side cam pulled in by " + h.collider.name + "; ";
            }
        }
        sideCam.transform.position = pos; sideCam.transform.rotation = Quaternion.LookRotation(look - pos);
    }

    void CaptureSide(string path)
    {
        var prev = RenderTexture.active; RenderTexture.active = sideRT;
        sideTex.ReadPixels(new Rect(0, 0, D.sideW, D.sideH), 0, 0, false); sideTex.Apply(false);
        RenderTexture.active = prev;
        File.WriteAllBytes(path, sideTex.EncodeToPNG());
    }

    void CaptureGame(string path, Row r)
    {
        var tex = ScreenCapture.CaptureScreenshotAsTexture();   // final frame including the OnGUI label
        if (tex == null) { if (r != null) r.note += "game shot failed; "; return; }
        float lum = 0f; int n = 0;
        for (int yy = 1; yy < 9; yy++) for (int xx = 1; xx < 16; xx++) { Color c = tex.GetPixel(tex.width * xx / 16, tex.height * yy / 9); lum += c.r + c.g + c.b; n++; }
        if (lum / Mathf.Max(1, n) < 0.003f && r != null) r.note += "game shot is black (window minimised?); ";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Destroy(tex);
    }

    // ---------------- output ----------------
    static string CsvHeader()
    {
        return "fighter,idx,targetX,targetZ,x,z,rootY,leftFootY,rightFootY,leftToeBaseY,rightToeBaseY,leftToeEndY,rightToeEndY,soleY,ccBottomY,groundHitY,groundCollider,"
            + "visibleTop,method,topRenderer,boundsTop,boundsRenderer,unreadableUnderPoint,topUnderLeftFoot,topUnderRightFoot,footRefKind,footRefY,dFoot,dGround,dBoneRaw,verdict,side,game,note";
    }

    static string Csv(string s) { return "\"" + (s ?? "").Replace("\"", "'") + "\""; }

    static string CsvRow(Row r)
    {
        return string.Join(",", new[] {
            r.fighter, r.idx.ToString(), N(r.tx), N(r.tz), N(r.x), N(r.z), N(r.rootY), N(r.lfY), N(r.rfY), N(r.ltbY), N(r.rtbY), N(r.lteY), N(r.rteY), N(r.sole), N(r.ccBottom), N(r.ground), Csv(r.groundName),
            N(r.top), r.method, Csv(r.topName), N(r.boundsTop), Csv(r.boundsName), r.unreadable.ToString(), N(r.topL), N(r.topR), r.footRefKind, N(r.footRef), N(r.dFoot), N(r.dGround), N(r.dBone), r.verdict, r.side, r.game, Csv(r.note) });
    }

    void WriteSummary()
    {
        int pass = 0, fail = 0, warn = 0;
        Row worstFoot = null, worstGround = null;
        foreach (var r in rows)
        {
            if (r.verdict == "PASS") pass++; else if (r.verdict == "FAIL") fail++; else warn++;
            if (!float.IsNaN(r.dFoot) && (worstFoot == null || Mathf.Abs(r.dFoot) > Mathf.Abs(worstFoot.dFoot))) worstFoot = r;
            if (!float.IsNaN(r.dGround) && (worstGround == null || Mathf.Abs(r.dGround) > Mathf.Abs(worstGround.dGround))) worstGround = r;
        }
        var sb = new StringBuilder();
        if (fightYFail) fail++;   // a fighter left the ground band during the fight
        bool anyFail = fail > 0 || headVerdict == "FAIL";
        sb.AppendLine("VERDICT: " + (anyFail ? "FAIL" : warn > 0 ? "PASS_WITH_WARNINGS" : "PASS") + "   (exit code " + (anyFail ? 1 : 0) + ")");
        sb.AppendLine("rows: " + rows.Count + "   PASS " + pass + "   FAIL " + fail + "   WARN " + warn + "   tolerance " + D.tolerance.ToString("F3", IC) + " m");
        sb.AppendLine("mode: " + (D.gameLocomotion ? "game locomotion (DuelFighter gravity + stair clamp)" : "harness gravity, no clamp") + "   foot ref: " + (D.useSole ? "baked sole (fallback ankle bone - " + D.ankleHeight.ToString("0.00", IC) + ")" : "ankle bone"));
        if (worstFoot != null) sb.AppendLine("worst foot delta  : " + worstFoot.dFoot.ToString("F3", IC) + " m  " + worstFoot.fighter + " x=" + worstFoot.tx.ToString("F1", IC) + " z=" + worstFoot.tz.ToString("F1", IC) + " (" + worstFoot.topName + ")");
        if (worstGround != null) sb.AppendLine("worst ground delta: " + worstGround.dGround.ToString("F3", IC) + " m  " + worstGround.fighter + " x=" + worstGround.tx.ToString("F1", IC) + " z=" + worstGround.tz.ToString("F1", IC) + " (collider " + worstGround.groundName + " vs " + worstGround.topName + ")");
        sb.AppendLine("--- FAIL rows ---");
        foreach (var r in rows) if (r.verdict == "FAIL") sb.AppendLine(r.fighter + " x=" + r.tx.ToString("F1", IC) + " z=" + r.tz.ToString("F1", IC) + " dFoot=" + Fmt(r.dFoot) + " dGround=" + Fmt(r.dGround) + " top=" + r.topName + " : " + r.note);
        sb.AppendLine("--- WARN rows (unreadable meshes: bounds only) ---");
        foreach (var r in rows) if (r.verdict == "WARN") sb.AppendLine(r.fighter + " x=" + r.tx.ToString("F1", IC) + " z=" + r.tz.ToString("F1", IC) + " boundsTop=" + Fmt(r.boundsTop) + " (" + r.boundsName + ") meshTop=" + Fmt(r.top) + " dGround=" + Fmt(r.dGround) + " dFoot=" + Fmt(r.dFoot));
        sb.AppendLine("HEAD: " + headVerdict);
        foreach (var hl in headLines) sb.AppendLine("  " + hl);
        sb.AppendLine("--- fight ---");
        float avg = fightFpsTime > 0f ? fightFpsFrames / fightFpsTime : 0f;
        sb.AppendLine("seconds " + D.fightSeconds.ToString("F0", IC) + "  frames " + fightFrames + "  avg_fps " + avg.ToString("F1", IC) + "  min_fps " + (fightMaxDt > 0f ? (1f / fightMaxDt).ToString("F1", IC) : "n/a") + "  max_fps " + (fightMinDt < 9f ? (1f / fightMinDt).ToString("F1", IC) : "n/a") + "  res " + Screen.width + "x" + Screen.height + "  gpu " + SystemInfo.graphicsDeviceName);
        sb.AppendLine(fightNote);
        foreach (var kv in fightYWorst) sb.AppendLine("fight Y " + kv.Key + ": worst |y - ground| " + kv.Value.ToString("F3", IC) + " m at t=" + (fightYWorstT.ContainsKey(kv.Key) ? fightYWorstT[kv.Key].ToString("F1", IC) : "?") + "  y range " + (fightYMin.ContainsKey(kv.Key) ? fightYMin[kv.Key].ToString("F2", IC) : "?") + " .. " + (fightYMax.ContainsKey(kv.Key) ? fightYMax[kv.Key].ToString("F2", IC) : "?") + "  tolerance " + D.fightYTol.ToString("F2", IC) + (kv.Value > D.fightYTol ? "  FAIL" : "  ok"));
        sb.AppendLine("--- notes ---");
        foreach (var n in notes) sb.AppendLine(n);
        File.WriteAllText(Path.Combine(outDir, "verify_summary.txt"), sb.ToString());
        Debug.Log("[VERIFY] SUMMARY PASS " + pass + " FAIL " + fail + " WARN " + warn);
    }
}

