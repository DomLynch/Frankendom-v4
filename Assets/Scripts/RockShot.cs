#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// ROCK SHOT HARNESS (Visual QC, kit for v104; harness only, OFF unless launched with -rockshot).
// Usage: HadesSlice.exe -rockshot [-rockshotdata:<json>] [-out:<dir>] -screen-width 1920 -screen-height 1080 -screen-fullscreen 0   (NO -fast: the real runtime_flags.json)
// Why: texture-compression close-ups (BC7/BC5) need a frame that is LIT and close to a surface. FloorShot can only aim along the player's forward line and a black frame passed once.
// What: for each target in rockshot.json (kind "renderer": nearest enabled renderer whose hierarchy path contains `match`; kind "bone": a bone/child of the Player or Enemy fighter whose name contains `match`),
//   the camera is put `distM` metres from the target's bounds on the side the key light comes from (Key_ColdSun, else the first directional light), looking at its centre. The LODGroup above a
//   renderer target is forced to LOD0 so both builds show the highest mesh. Several yaw offsets around the light side (yawTryDeg) are tried: a frame with more than maxBlackFraction (0.5) of pixels darker than
//   blackLuma (0.04) is REJECTED automatically and the next yaw is tried. If none passes the target FAILs (exit code 4): a black frame can never count as a pass.
// Output (default <exe dir>\rockshot\): rock_<id>_<k>.png (+ sidecar), rockshot_log.txt, rockshot_summary.txt (per target: accepted yaw, black fraction, mean luma, camera pos, target world pos).
// Exit codes: 0 all targets accepted, 1 resolution/-fast guard, 2 exception/setup, 3 timeout, 4 at least one target had no lit frame.
// Nothing on disk besides <out> is changed. Every number is rockshot.json (Data == StreamingAssets); C# defaults mirror the shipped file.
[DefaultExecutionOrder(-57)]
public class RockShot : MonoBehaviour
{
    [Serializable]
    public class Target
    {
        public string id = "target", kind = "renderer", who = "Player", match = "rock_boulder", note;
        public float distM = 1f, upM = 0.15f, aimUpM = 0f, fov = 40f, near = 0.03f;
        public float[] yawTryDeg = { 0f, 40f, -40f, 80f, -80f, 140f, -140f, 180f };
    }
    [Serializable]
    public class Cfg
    {
        public bool enabled = true, requireResolution = true, muteAudio = true, forceLod0 = true;
        public int width = 1920, height = 1080, shotsPerTarget = 2;
        public float bootWaitSec = 30f, settleSec = 1.5f, targetSettleSec = 0.8f, shotIntervalSec = 0.4f, timeoutSec = 300f, exitGraceSec = 3f;
        public float maxBlackFraction = 0.5f, blackLuma = 0.04f, minMeanLuma = 0.02f;
        public string keyLightName = "Key_ColdSun";
        public Target[] targets;
    }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F3", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-rockshot")) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<RockShot>() != null) return;
            var go = new GameObject("RockShot"); DontDestroyOnLoad(go); go.AddComponent<RockShot>();
        }
        catch (Exception e) { Debug.LogError("[rockshot] boot failed: " + e); }
    }

    Cfg C = new Cfg(); string dir, logPath, dataSrc = "(defaults)";
    float t0, exitAt; bool exiting; int exitCode;
    DuelFighter Pl, En; CameraRig rig; Camera mainCam;
    readonly List<string> rows = new List<string>(), fails = new List<string>();
    int shots;

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[rockshot] " + s);
    }
    void Fail(string s) { fails.Add(s); Log("FAIL " + s); }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "rockshot"); } catch { dir = "rockshot"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "rockshot_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        LoadCfg();
        if (C.muteAudio) AudioListener.volume = 0f;
    }

    void LoadCfg()
    {
        var paths = new List<string>(); string cli = Arg("-rockshotdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "rockshot.json")); paths.Add(Path.Combine(Application.dataPath, "Data/rockshot.json"));
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var c = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart('﻿'));
                if (c != null) { C = c; dataSrc = p; if (C.targets == null || C.targets.Length == 0) C.targets = new[] { new Target() }; return; }
            }
            catch (Exception e) { Debug.LogWarning("[rockshot] cannot read " + p + ": " + e.Message); dataSrc = p + " (unreadable: defaults)"; }
        }
        C.targets = new[] { new Target() };
    }

    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > C.exitGraceSec) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT after " + C.timeoutSec.ToString("F0", IC) + " s"); Finish(3, "timeout"); }
    }
    bool hold; Vector3 holdPos; Quaternion holdRot; float holdFov, holdNear;
    void LateUpdate() { if (hold && mainCam != null) { mainCam.transform.SetPositionAndRotation(holdPos, holdRot); mainCam.fieldOfView = holdFov; mainCam.nearClipPlane = holdNear; } }

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    IEnumerator Wait(float s) { float u = Time.unscaledTime + s; while (Time.unscaledTime < u && !exiting) yield return null; }
    static string PathOf(Transform t) { var sb = new StringBuilder(t.name); for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/"); return sb.ToString(); }

    // -------- target lookup: world centre + horizontal radius --------
    bool Resolve(Target t, out Vector3 centre, out float radius, out string what)
    {
        centre = Vector3.zero; radius = 0.3f; what = "";
        if (t.kind == "bone")
        {
            var f = t.who == "Enemy" ? En : Pl; if (f == null) return false;
            Transform best = null;
            foreach (var tr in f.GetComponentsInChildren<Transform>(true))
                if (tr.name.IndexOf(t.match, StringComparison.OrdinalIgnoreCase) >= 0) { best = tr; break; }
            if (best == null) return false;
            centre = best.position; radius = 0.25f; what = PathOf(best); return true;
        }
        Renderer pick = null; float bd = float.MaxValue; Vector3 from = Pl != null ? Pl.transform.position : Vector3.zero;
        foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (PathOf(r.transform).IndexOf(t.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            float d = (r.bounds.center - from).sqrMagnitude; if (d < bd) { bd = d; pick = r; }
        }
        if (pick == null) return false;
        if (C.forceLod0) { var lg = pick.GetComponentInParent<LODGroup>(); if (lg != null) lg.ForceLOD(0); }
        var b = pick.bounds; centre = b.center; radius = Mathf.Max(b.extents.x, b.extents.z); what = PathOf(pick.transform); return true;
    }

    Vector3 LightDirToLight()
    {
        Light key = null;
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (l == null || l.type != LightType.Directional) continue;
            if (l.name == C.keyLightName) { key = l; break; }
            if (key == null) key = l;
        }
        Vector3 d = key != null ? -key.transform.forward : new Vector3(1f, 1f, 0f);
        d.y = 0f; if (d.sqrMagnitude < 1e-3f) d = Vector3.right; return d.normalized;
    }

    // luma stats over the whole frame (every 4th pixel)
    void Stats(Texture2D t, out float black, out float mean)
    {
        var px = t.GetPixels32(); int n = 0, nb = 0; double sum = 0; float thr = C.blackLuma;
        for (int i = 0; i < px.Length; i += 4) { float l = (px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f) / 255f; sum += l; if (l < thr) nb++; n++; }
        black = n > 0 ? (float)nb / n : 1f; mean = n > 0 ? (float)(sum / n) : 0f;
    }

    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((Pl = Find(Roster.PlayerObject)) == null || (En = Find(Roster.EnemyObject)) == null || (rig = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSec) { Log("fighters / CameraRig not found"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        mainCam = rig.GetComponent<Camera>(); if (mainCam == null) mainCam = Camera.main;
        if (!C.enabled) { Log("rockshot.json enabled=false"); Finish(0, "disabled"); yield break; }
        if ((Screen.width != C.width || Screen.height != C.height) && C.width > 0 && C.height > 0) { Log("resolution " + Screen.width + "x" + Screen.height + ", asking for " + C.width + "x" + C.height); Screen.SetResolution(C.width, C.height, false); yield return Wait(1.0f); }
        if (C.requireResolution && (Screen.width != C.width || Screen.height != C.height)) { Fail("resolution is " + Screen.width + "x" + Screen.height + ", need " + C.width + "x" + C.height); Finish(1, "resolution"); yield break; }
        if (RuntimeQuality.Fast) { Fail("-fast given: use the real runtime_flags.json"); Finish(1, "fast flags"); yield break; }
        Log("start data " + dataSrc + " flags " + RuntimeQuality.FlagsPath + " roster " + Roster.PlayerId + " vs " + Roster.EnemyId);

        yield return Wait(1f);
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        Pl.opponent = En; En.opponent = Pl; Pl.moveDir = Vector3.zero; En.moveDir = Vector3.zero; Pl.guardHeld = false; En.guardHeld = false;
        Time.timeScale = 1f; rig.enabled = false;
        yield return Wait(C.settleSec);

        Vector3 toLight = LightDirToLight();
        Log("key light side (horizontal, towards the light) " + toLight.ToString("F3"));
        foreach (var t in C.targets)
        {
            if (exiting) yield break;
            Vector3 centre; float radius; string what;
            if (!Resolve(t, out centre, out radius, out what)) { Fail(t.id + ": no " + t.kind + " matching '" + t.match + "'"); rows.Add(t.id + ": NOT FOUND (" + t.match + ")"); continue; }
            Log("=== target " + t.id + " -> " + what + " centre " + centre.ToString("F2") + " radius " + F(radius) + " ===");
            bool ok = false; string tried = "";
            foreach (float yaw in t.yawTryDeg)
            {
                Vector3 sideDir = Quaternion.Euler(0f, yaw, 0f) * toLight;
                Vector3 pos = centre + sideDir * (radius + t.distM); pos.y = centre.y + t.upM;
                Vector3 aim = centre + Vector3.up * t.aimUpM;
                holdPos = pos; holdRot = Quaternion.LookRotation(aim - pos, Vector3.up); holdFov = t.fov; holdNear = Mathf.Max(0.01f, t.near); hold = true;
                yield return Wait(C.targetSettleSec);
                yield return new WaitForEndOfFrame();
                CaptureUtil.HideConsole();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex == null) { Fail(t.id + ": screenshot failed"); continue; }
                float black, mean; Stats(tex, out black, out mean);
                bool accept = black <= C.maxBlackFraction && mean >= C.minMeanLuma;
                tried += " yaw" + yaw.ToString("F0", IC) + "=black" + F(black) + "/mean" + F(mean) + (accept ? "(OK)" : "(REJECT)");
                Log(t.id + " yaw " + yaw.ToString("F0", IC) + " cam " + pos.ToString("F2") + " black " + F(black) + " mean " + F(mean) + (accept ? " ACCEPT" : " REJECT (too dark)"));
                if (!accept) { Destroy(tex); continue; }
                for (int k = 0; k < Mathf.Max(1, C.shotsPerTarget) && !exiting; k++)
                {
                    if (k > 0) { yield return Wait(C.shotIntervalSec); yield return new WaitForEndOfFrame(); tex = ScreenCapture.CaptureScreenshotAsTexture(); if (tex == null) break; }
                    string png = Path.Combine(dir, "rock_" + t.id + "_" + k.ToString("00") + ".png");
                    try
                    {
                        File.WriteAllBytes(png, tex.EncodeToPNG()); shots++;
                        CaptureMeta.WriteFor(png, "\"harness\":\"rockshot\",\"target\":\"" + t.id + "\",\"yaw\":" + F(yaw) + ",\"camPos\":\"" + pos.ToString("F2") + "\",\"targetPos\":\"" + centre.ToString("F2") + "\",\"black\":" + F(black) + ",\"mean\":" + F(mean));
                    }
                    catch (Exception e) { Fail(t.id + ": write failed: " + e.Message); }
                    Destroy(tex);
                }
                rows.Add(t.id + ": ACCEPTED yaw " + yaw.ToString("F0", IC) + " cam " + pos.ToString("F2") + " target " + what + " @ " + centre.ToString("F2") + " black " + F(black) + " mean " + F(mean));
                ok = true; break;
            }
            hold = false;
            if (!ok) { Fail(t.id + ": no lit frame (every yaw >" + F(C.maxBlackFraction) + " black or too dark)"); rows.Add(t.id + ": REJECTED ALL:" + tried); }
        }
        hold = false;
        Finish(fails.Count == 0 ? 0 : 4, fails.Count == 0 ? "all targets lit and captured" : fails.Count + " problem(s)");
    }

    void Start() { StartCoroutine(Guarded(Main())); }
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

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; hold = false;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("rockshot_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height + "  data " + dataSrc);
            sb.AppendLine("VERDICT: " + (code == 0 ? "DONE" : "FAIL") + "   (exit code " + code + ", " + why + ")  PNGs " + shots);
            foreach (var r in rows) sb.AppendLine("  " + r);
            foreach (var f in fails) sb.AppendLine("  FAIL " + f);
            File.WriteAllText(Path.Combine(dir, "rockshot_summary.txt"), sb.ToString());
        }
        catch (Exception e) { Log("summary failed: " + e.Message); }
        Log("EXIT code " + code + " (" + why + ")");
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
#else // FIXHARNESS v1 nl=0
#endif // FIXHARNESS v1
