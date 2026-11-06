using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// FLOOR SHOT HARNESS (lane FLOORSHOT, v102): -floorshot [-out:<dir>] [-floorshotdata:<json>]   (run at 1920x1080: -screen-width 1920 -screen-height 1080 -screen-fullscreen 0; NO -fast: the real runtime_flags.json)
// Why: Dom sees a flickering, dithered grey patch with hard edges on the arena floor behind Joan from a low camera behind her; no existing capture covers that pose, so there is no frame to diff.
// What: the scene's own fighters and real CameraRig (brains off, rig disabled), the camera placed at each pose of Assets/Data/floorshot.json (relative to the roster player), and for EACH arm (A/B state, all data flags:
//   QualitySettings.lodBias, LODGroup.ForceLOD, LODGroup.fadeMode on the floor groups, hide blood/wound/gore renderers, disable DecalProjectors) shotsPerArm frames every shotIntervalSec (default 8 x 0.25 s = 2 s) so a flicker shows
//   as a change between frames. Every arm is applied on top of the baseline and fully undone before the next one.
// Floor groups: every LODGroup whose hierarchy path contains floorMatch (ground_paving). Their fadeMode / animateCrossFading / LOD count / per-LOD visibility are logged, so the log states the LOD fade mode of the real scene.
// Metrics per arm and pose over the floor region (screen fractions): meanDiff (mean abs luma change between consecutive shots), maxBlockDiff (largest blockPx block mean change = a LOCAL flicker patch), hf (mean neighbour-pixel
// luma step = stipple/dither energy). The summary ranks arms by maxBlockDiff; a patch that vanishes in one arm names the cause. Verdict is informational: exit 0 on a completed run.
// Output (default <exe dir>\floorshot\): floor_<pose>_<arm>_<k>.png (+ .json via CaptureMeta), floorshot_log.txt, floorshot_summary.txt. Exit codes: 0 done, 1 guard FAIL (resolution / -fast), 2 exception / setup failure, 3 timeout.
// Nothing on disk besides <out> is changed. Every number is Assets/Data/floorshot.json (StreamingAssets copy); the characters come from Roster.
[DefaultExecutionOrder(-58)]
public class FloorShot : MonoBehaviour
{
    [Serializable]
    public class Pose { public string id = "pose"; public float behindM = 2.4f, leftM = 0f, heightM = 0.75f, lookAheadM = 1f, lookHeightM = 0.6f, fov = 50f, near = 0.05f; }
    [Serializable]
    public class Arm { public string id = "arm"; public string note; public float lodBias = 0f; public int forceLod = -1; public string fadeMode = ""; public string[] hideRendererMatch; public bool decalProjectorsOff; }
    [Serializable]
    public class Cfg
    {
        public bool enabled = true, requireResolution = true, muteAudio = true;
        public int width = 1920, height = 1080, shotsPerArm = 8, blockPx = 32;
        public float bootWaitSec = 30f, settleSec = 1.5f, armSettleSec = 0.6f, shotIntervalSec = 0.25f, timeoutSec = 240f, exitGraceSec = 3f, flickerBlockDiffWarn = 0.012f;
        public string floorMatch = "ground_paving";
        public float[] region = { 0f, 0f, 1f, 0.5f };
        public Pose[] poses;
        public Arm[] arms;
    }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F3", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-floorshot")) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<FloorShot>() != null) return;
            var go = new GameObject("FloorShot"); DontDestroyOnLoad(go); go.AddComponent<FloorShot>();
        }
        catch (Exception e) { Debug.LogError("[floorshot] boot failed: " + e); }
    }

    Cfg C = new Cfg(); string dir, logPath, dataSrc = "(defaults)";
    float t0, exitAt; bool exiting; int exitCode;
    DuelFighter Pl, En; CameraRig rig; Camera mainCam;
    Pose curPose; bool holdPose;
    readonly List<string> rows = new List<string>(), fails = new List<string>();
    readonly List<LODGroup> floorGroups = new List<LODGroup>();
    int shots;
    class Res { public string pose, arm; public float meanDiff, maxBlockDiff, hf; public int n; }
    readonly List<Res> results = new List<Res>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[floorshot] " + s);
    }
    void Fail(string s) { fails.Add(s); Log("FAIL " + s); }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:"); if (string.IsNullOrEmpty(o)) o = Arg("-floorout:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "floorshot"); } catch { dir = "floorshot"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "floorshot_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        LoadCfg();
        if (C.muteAudio) AudioListener.volume = 0f;
    }
    void Start() { StartCoroutine(Guarded(Main())); }

    void LoadCfg()
    {
        var paths = new List<string>(); string cli = Arg("-floorshotdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "floorshot.json")); paths.Add(Path.Combine(Application.dataPath, "Data/floorshot.json"));
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart('﻿'));
                if (c != null) { C = c; dataSrc = p; if (C.poses == null || C.poses.Length == 0) C.poses = new[] { new Pose() }; if (C.arms == null || C.arms.Length == 0) C.arms = new[] { new Arm { id = "baseline" } }; if (C.region == null || C.region.Length < 4) C.region = new Cfg().region; return; }
            }
            catch (Exception e) { Debug.LogWarning("[floorshot] cannot read " + p + ": " + e.Message); dataSrc = p + " (unreadable: defaults)"; }
        }
        C.poses = new[] { new Pose() }; C.arms = new[] { new Arm { id = "baseline" } };
    }

    IEnumerator Guarded(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Log("EXCEPTION: " + ex); Finish(2, "exception"); yield break; }
            yield return cur;
        }
    }

    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > C.exitGraceSec) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT after " + C.timeoutSec.ToString("F0", IC) + " s"); Finish(3, "timeout"); }
    }
    void LateUpdate() { if (holdPose && curPose != null && Pl != null && mainCam != null) ApplyPose(curPose); }

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    IEnumerator Wait(float s) { float u = Time.unscaledTime + s; while (Time.unscaledTime < u && !exiting) yield return null; }

    // camera relative to the player fighter: behind along -forward, to her left, up from her feet, aimed ahead of her
    void ApplyPose(Pose p)
    {
        Vector3 fwd = Pl.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();
        Vector3 left = -Vector3.Cross(Vector3.up, fwd);
        Vector3 feet = Pl.transform.position;
        Vector3 pos = feet - fwd * p.behindM + left * p.leftM + Vector3.up * p.heightM;
        Vector3 aim = feet + fwd * p.lookAheadM + Vector3.up * p.lookHeightM;
        mainCam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(aim - pos, Vector3.up));
        mainCam.fieldOfView = p.fov; mainCam.nearClipPlane = Mathf.Max(0.01f, p.near);
    }

    static string PathOf(Transform t) { var sb = new StringBuilder(t.name); for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/"); return sb.ToString(); }

    // ---------------- arm state (apply / undo) ----------------
    class Saved { public float lodBias; public Dictionary<LODGroup, LODFadeMode> fade = new Dictionary<LODGroup, LODFadeMode>(); public List<Renderer> hidden = new List<Renderer>(); public List<DecalProjector> decals = new List<DecalProjector>(); }
    Saved ApplyArm(Arm a)
    {
        var s = new Saved { lodBias = QualitySettings.lodBias };
        if (a.lodBias > 0f) QualitySettings.lodBias = a.lodBias;
        LODFadeMode fm; bool setFade = !string.IsNullOrEmpty(a.fadeMode) && Enum.TryParse(a.fadeMode, true, out fm);
        foreach (var g in floorGroups)
        {
            if (g == null) continue;
            s.fade[g] = g.fadeMode;
            if (setFade) { Enum.TryParse(a.fadeMode, true, out fm); g.fadeMode = fm; }
            if (a.forceLod >= 0) g.ForceLOD(Mathf.Min(a.forceLod, Mathf.Max(0, g.lodCount - 1)));
        }
        if (a.hideRendererMatch != null && a.hideRendererMatch.Length > 0)
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled) continue;
                foreach (var m in a.hideRendererMatch) if (!string.IsNullOrEmpty(m) && r.name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { r.enabled = false; s.hidden.Add(r); break; }
            }
        if (a.decalProjectorsOff)
            foreach (var d in FindObjectsByType<DecalProjector>(FindObjectsSortMode.None)) if (d != null && d.enabled) { d.enabled = false; s.decals.Add(d); }
        return s;
    }
    void UndoArm(Saved s)
    {
        QualitySettings.lodBias = s.lodBias;
        foreach (var kv in s.fade) if (kv.Key != null) { kv.Key.fadeMode = kv.Value; kv.Key.ForceLOD(-1); }
        foreach (var r in s.hidden) if (r != null) r.enabled = true;
        foreach (var d in s.decals) if (d != null) d.enabled = true;
    }

    // which LODs of the floor groups the camera currently renders (isVisible per LOD): a flip between shots = a LOD switch under the camera
    string LodState()
    {
        var sb = new StringBuilder();
        foreach (var g in floorGroups)
        {
            if (g == null) continue; var lods = g.GetLODs(); sb.Append(g.name).Append('[');
            for (int i = 0; i < lods.Length; i++) { bool vis = false; foreach (var r in lods[i].renderers) if (r != null && r.isVisible) { vis = true; break; } sb.Append(vis ? '1' : '0'); }
            sb.Append("] ");
        }
        return sb.ToString().Trim();
    }

    // ---------------- main ----------------
    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((Pl = Find(Roster.PlayerObject)) == null || (En = Find(Roster.EnemyObject)) == null || (rig = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSec) { Log("fighters / CameraRig not found (" + Roster.PlayerObject + "/" + Roster.EnemyObject + ")"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        mainCam = rig.GetComponent<Camera>(); if (mainCam == null) mainCam = Camera.main;
        if (!C.enabled) { Log("floorshot.json enabled=false"); Finish(0, "disabled"); yield break; }
        if ((Screen.width != C.width || Screen.height != C.height) && C.width > 0 && C.height > 0) { Log("resolution " + Screen.width + "x" + Screen.height + ", asking for " + C.width + "x" + C.height); Screen.SetResolution(C.width, C.height, false); yield return Wait(1f); }
        if (C.requireResolution && (Screen.width != C.width || Screen.height != C.height)) { Fail("resolution is " + Screen.width + "x" + Screen.height + ", need " + C.width + "x" + C.height); Finish(1, "resolution"); yield break; }
        if (RuntimeQuality.Fast) { Fail("-fast given: the floor frames must use the real runtime_flags.json"); Finish(1, "fast flags"); yield break; }

        foreach (var g in FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (PathOf(g.transform).IndexOf(C.floorMatch, StringComparison.OrdinalIgnoreCase) >= 0) floorGroups.Add(g);
        Log("start res " + Screen.width + "x" + Screen.height + " flags " + RuntimeQuality.FlagsPath + " data " + dataSrc + " roster " + Roster.PlayerId + " vs " + Roster.EnemyId + " lodBias " + QualitySettings.lodBias + " poses " + C.poses.Length + " arms " + C.arms.Length + " out " + dir);
        foreach (var g in floorGroups)
        {
            string l = "FLOOR LODGroup " + PathOf(g.transform) + " fadeMode=" + g.fadeMode + " animateCrossFading=" + g.animateCrossFading + " lodCount=" + g.lodCount + " size=" + F(g.size) + " heights";
            var lods = g.GetLODs(); for (int i = 0; i < lods.Length; i++) l += " LOD" + i + "=" + F(lods[i].screenRelativeTransitionHeight) + "/fadeW" + F(lods[i].fadeTransitionWidth) + "/r" + lods[i].renderers.Length;
            Log(l); rows.Add(l);
        }
        if (floorGroups.Count == 0) { Fail("no LODGroup whose path contains '" + C.floorMatch + "' (the floor name changed? edit floorMatch)"); }

        yield return Wait(1f);
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        Pl.opponent = En; En.opponent = Pl; Pl.moveDir = Vector3.zero; En.moveDir = Vector3.zero; Pl.guardHeld = false; En.guardHeld = false;
        Time.timeScale = 1f;
        rig.enabled = false; holdPose = true;
        yield return Wait(C.settleSec);

        foreach (var pose in C.poses)
        {
            curPose = pose; ApplyPose(pose);
            Log("=== pose " + pose.id + " cam " + mainCam.transform.position.ToString("F2") + " fov " + F(pose.fov) + " ===");
            yield return Wait(C.armSettleSec);
            Texture2D prev = null;
            foreach (var arm in C.arms)
            {
                if (exiting) yield break;
                var saved = ApplyArm(arm);
                Log("--- arm " + arm.id + (string.IsNullOrEmpty(arm.note) ? "" : " (" + arm.note + ")") + " lodBias " + QualitySettings.lodBias + " fade " + (floorGroups.Count > 0 && floorGroups[0] != null ? floorGroups[0].fadeMode.ToString() : "-"));
                yield return Wait(C.armSettleSec);
                var res = new Res { pose = pose.id, arm = arm.id }; float sumDiff = 0f, sumBlock = 0f, sumHf = 0f; int nd = 0, hn = 0; prev = null;
                string lodSeq = "";
                for (int k = 0; k < Mathf.Max(1, C.shotsPerArm) && !exiting; k++)
                {
                    yield return new WaitForEndOfFrame();
                    CaptureUtil.HideConsole();
                    var tex = ScreenCapture.CaptureScreenshotAsTexture();
                    if (tex == null) { Fail("screenshot failed: " + pose.id + " " + arm.id + " " + k); continue; }
                    shots++;
                    float diff = -1f, blk = -1f, hf = Hf(tex);
                    if (prev != null) { Diff(prev, tex, out diff, out blk); sumDiff += diff; sumBlock = Mathf.Max(sumBlock, blk); nd++; }
                    sumHf += hf; hn++;
                    string ls = LodState(); lodSeq += (lodSeq.Length > 0 ? " > " : "") + ls;
                    string stem = "floor_" + pose.id + "_" + arm.id + "_" + k.ToString("00");
                    string png = Path.Combine(dir, stem + ".png");
                    try
                    {
                        File.WriteAllBytes(png, tex.EncodeToPNG());
                        CaptureMeta.WriteFor(png, "\"harness\":\"floorshot\",\"pose\":\"" + pose.id + "\",\"arm\":\"" + arm.id + "\",\"shot\":" + k + ",\"camPos\":\"" + mainCam.transform.position.ToString("F2") + "\",\"fov\":" + F(mainCam.fieldOfView)
                            + ",\"lodBias\":" + F(QualitySettings.lodBias) + ",\"lodState\":\"" + ls + "\",\"diffPrev\":" + F(diff) + ",\"blockDiffPrev\":" + F(blk) + ",\"hf\":" + F(hf) + ",\"screen\":\"" + Screen.width + "x" + Screen.height + "\",\"wallSec\":" + F(Time.realtimeSinceStartup - t0));
                    }
                    catch (Exception e) { Fail(stem + ": write failed: " + e.Message); }
                    if (prev != null) Destroy(prev);
                    prev = tex;
                    yield return Wait(C.shotIntervalSec);
                }
                if (prev != null) { Destroy(prev); prev = null; }
                res.n = nd; res.meanDiff = nd > 0 ? sumDiff / nd : 0f; res.maxBlockDiff = sumBlock; res.hf = hn > 0 ? sumHf / hn : 0f;
                results.Add(res);
                string line = pose.id + " / " + arm.id + ": meanDiff " + F(res.meanDiff) + " maxBlockDiff " + F(res.maxBlockDiff) + " hf " + F(res.hf) + " lod visible (LOD0..n per shot) " + lodSeq;
                rows.Add(line); Log(line);
                UndoArm(saved);
            }
        }
        holdPose = false;
        Finish(fails.Count == 0 ? 0 : 1, fails.Count == 0 ? "all arms captured" : fails.Count + " problem(s)");
    }

    // ---------------- metrics over the floor region (screen fractions, y from the bottom = texture rows from 0) ----------------
    void Reg(Texture2D t, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Mathf.Clamp(Mathf.RoundToInt(C.region[0] * t.width), 0, t.width - 2); y0 = Mathf.Clamp(Mathf.RoundToInt(C.region[1] * t.height), 0, t.height - 2);
        x1 = Mathf.Clamp(Mathf.RoundToInt(C.region[2] * t.width), x0 + 2, t.width); y1 = Mathf.Clamp(Mathf.RoundToInt(C.region[3] * t.height), y0 + 2, t.height);
    }
    static float L(Color32 c) { return (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) / 255f; }

    float Hf(Texture2D t)
    {
        int x0, y0, x1, y1; Reg(t, out x0, out y0, out x1, out y1);
        var px = t.GetPixels32(); int W = t.width; double sum = 0; long n = 0;
        for (int y = y0; y < y1 - 1; y++) for (int x = x0; x < x1 - 1; x++) { float l = L(px[y * W + x]); sum += Mathf.Abs(l - L(px[y * W + x + 1])) + Mathf.Abs(l - L(px[(y + 1) * W + x])); n++; }
        return n > 0 ? (float)(sum / (2.0 * n)) : 0f;
    }

    void Diff(Texture2D a, Texture2D b, out float mean, out float maxBlock)
    {
        int x0, y0, x1, y1; Reg(a, out x0, out y0, out x1, out y1);
        var pa = a.GetPixels32(); var pb = b.GetPixels32(); int W = a.width; int B = Mathf.Max(4, C.blockPx);
        double sum = 0; long n = 0; maxBlock = 0f;
        for (int by = y0; by < y1; by += B)
            for (int bx = x0; bx < x1; bx += B)
            {
                double bs = 0; int bn = 0;
                for (int y = by; y < Mathf.Min(by + B, y1); y++) for (int x = bx; x < Mathf.Min(bx + B, x1); x++) { int i = y * W + x; bs += Mathf.Abs(L(pa[i]) - L(pb[i])); bn++; }
                if (bn == 0) continue; sum += bs; n += bn; float m = (float)(bs / bn); if (m > maxBlock) maxBlock = m;
            }
        mean = n > 0 ? (float)(sum / n) : 0f;
    }

    // ---------------- end ----------------
    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; holdPose = false;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("floorshot_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height + "  flags " + RuntimeQuality.FlagsPath + " fast=" + RuntimeQuality.Fast);
            sb.AppendLine("VERDICT: " + (code == 0 ? "DONE" : "FAIL") + "   (exit code " + code + ", " + why + ")  PNGs " + shots + "  region " + F(C.region[0]) + "," + F(C.region[1]) + "," + F(C.region[2]) + "," + F(C.region[3]));
            foreach (var r in rows) sb.AppendLine("  " + r);
            var rank = new List<Res>(results); rank.Sort((a, b) => b.maxBlockDiff.CompareTo(a.maxBlockDiff));
            sb.AppendLine("RANKING by maxBlockDiff (largest local flicker first; warn above " + F(C.flickerBlockDiffWarn) + "): the arm whose value drops to the noise floor while baseline stays high removed the patch");
            foreach (var r in rank) sb.AppendLine("  " + (r.maxBlockDiff > C.flickerBlockDiffWarn ? "FLICKER " : "calm    ") + r.pose + " / " + r.arm + "  maxBlockDiff " + F(r.maxBlockDiff) + "  meanDiff " + F(r.meanDiff) + "  hf " + F(r.hf));
            foreach (var f in fails) sb.AppendLine("  FAIL " + f);
            File.WriteAllText(Path.Combine(dir, "floorshot_summary.txt"), sb.ToString());
        }
        catch (Exception e) { Log("summary failed: " + e.Message); }
        Log("EXIT code " + code + " (" + why + ")");
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
