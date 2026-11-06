using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// FEET BURST HARNESS (lane FEETCAP, v1): -feetburst:<dir> [-feetburstdata:<json>]   (run at native 2560x1600: -screen-width 2560 -screen-height 1600 -screen-fullscreen 0; NO -fast: the real runtime_flags.json)
// Why: Dom sees a circling, flickering whirlwind on the floor at the player's feet. It is obvious in motion and barely visible in one screenshot, so single frames cannot show it.
// What: CONSECUTIVE rendered frames. The scene's own fighters and real CameraRig run in normal play for settleSec (lock cam), then the camera pose is frozen. Time.captureFramerate = captureFps and
//   Time.fixedDeltaTime = 1/captureFps, so one rendered frame = one sim tick whatever the machine fps is. Per state (idle / walk / turn, Assets/Data/feetburst.json) `frames` frames are read back at end of
//   frame (a cropped ReadPixels around the projected foot point, optionally ScreenCapture), each frame's Time.frameCount / Time.time is logged and ANY gap in frameCount fails the run.
// Output (<dir>): <state>/f_0000.png (feet crop, lossless), ref_<state>_<k>.png (full native frame at refFrames), feetburst_frames.csv, feetburst_meta.json (render settings + states), feetburst_log.txt. Analyze with
//   python _agents\laneFEETCAP\tools\feet_flicker.py <dir>. Exit codes: 0 done, 1 guard FAIL (resolution / -fast / frame gap), 2 exception / setup failure, 3 timeout. Nothing else on disk is changed.
// OFF unless -feetburst: is given. Every number is Assets/Data/feetburst.json (StreamingAssets copy); the characters come from Roster.   // FEETCAP v1
[DefaultExecutionOrder(-57)]
public class FeetBurst : MonoBehaviour
{
    [Serializable]
    public class Cfg
    {
        public bool enabled = true, requireResolution = true, muteAudio = true;
        public int width = 2560, height = 1600, captureFps = 60, frames = 90, cropW = 640, cropH = 400, wideW = 1280, wideH = 800, stateSettleFrames = 45;
        public string[] states = { "idle", "walk", "turn" };
        public int[] refFrames = { 0, 45, 89 };
        public float footYFrac = 0.3f, bootWaitSec = 30f, settleSec = 2f, walkDirDeg = 0f, walkReverseAtFrac = 0f, turnDegPerSec = 90f, turnRadiusM = 3f, timeoutSec = 420f, exitGraceSec = 3f;
        public string readMethod = "readpixels";
    }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F4", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Arg("-feetburst:") == null) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<FeetBurst>() != null) return;
            var go = new GameObject("FeetBurst"); DontDestroyOnLoad(go); go.AddComponent<FeetBurst>();
        }
        catch (Exception e) { Debug.LogError("[feetburst] boot failed: " + e); }
    }

    Cfg C = new Cfg(); string dir, logPath, csvPath, dataSrc = "(defaults)";
    float t0, exitAt; bool exiting; int exitCode;
    DuelFighter Pl, En; CameraRig rig; Camera mainCam;
    bool holdCam; Vector3 camPos; Quaternion camRot; float camFov;
    Vector3 plStart, enStart; Quaternion plRot, enRot;
    string curState = ""; int curFrame = -1; float turnAng0;
    readonly List<string> fails = new List<string>(), metaStates = new List<string>();
    int totalFrames;

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[feetburst] " + s);
    }
    void Fail(string s) { fails.Add(s); Log("FAIL " + s); }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-feetburst:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "feetburst"); } catch { dir = "feetburst"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "feetburst_log.txt"); csvPath = Path.Combine(dir, "feetburst_frames.csv");
        try { File.WriteAllText(logPath, ""); } catch { }
        LoadCfg();
        if (C.muteAudio) AudioListener.volume = 0f;
    }
    void Start() { StartCoroutine(Guarded(Main())); }

    void LoadCfg()
    {
        var paths = new List<string>(); string cli = Arg("-feetburstdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "feetburst.json")); paths.Add(Path.Combine(Application.dataPath, "Data/feetburst.json"));
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart('﻿'));
                if (c != null) { C = c; dataSrc = p; if (C.states == null || C.states.Length == 0) C.states = new Cfg().states; if (C.refFrames == null) C.refFrames = new int[0]; return; }
            }
            catch (Exception e) { Debug.LogWarning("[feetburst] cannot read " + p + ": " + e.Message); dataSrc = p + " (unreadable: defaults)"; }
        }
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
        if (Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT after " + C.timeoutSec.ToString("F0", IC) + " s"); Finish(3, "timeout"); return; }
        // the state is driven by the frame index (deterministic), never by wall time
        if (Pl == null || En == null || mainCam == null || curFrame < 0) return;
        if (curState == "walk")
        {
            float frac = C.frames > 0 ? (float)curFrame / C.frames : 0f; float ang = C.walkDirDeg + ((C.walkReverseAtFrac > 0f && frac >= C.walkReverseAtFrac) ? 180f : 0f);
            Vector3 r = camRot * Vector3.right; r.y = 0f; r.Normalize(); Vector3 f = camRot * Vector3.forward; f.y = 0f; f.Normalize();
            Pl.moveDir = (r * Mathf.Cos(ang * Mathf.Deg2Rad) + f * Mathf.Sin(ang * Mathf.Deg2Rad)).normalized;
        }
        else if (curState == "turn")
        {
            Pl.moveDir = Vector3.zero;
            float a = turnAng0 + C.turnDegPerSec * Mathf.Deg2Rad * (curFrame / (float)C.captureFps);
            PlaceAt(En, plStart + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * C.turnRadiusM, enRot, true);
        }
        else Pl.moveDir = Vector3.zero;
    }
    void LateUpdate() { if (holdCam && mainCam != null) { mainCam.transform.SetPositionAndRotation(camPos, camRot); mainCam.fieldOfView = camFov; } }

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    IEnumerator Frames(int n) { for (int i = 0; i < n && !exiting; i++) yield return null; }

    void PlaceAt(DuelFighter f, Vector3 p, Quaternion rot, bool keepRot)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, p.y + 20f, p.z), Vector3.down, out var hit, 40f, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; if (!keepRot) f.transform.rotation = rot;
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }

    // laneWHIRLFIX: what the renderer will really see (VolumeManager stack) and which Volumes carry the effect components; a -qset flag that did not land shows up here
    string EffectiveStack()
    {
        var sb = new StringBuilder();
        try
        {
            var st = VolumeManager.instance.stack;
            if (st != null)
            {
                var ao = st.GetComponent<ScreenSpaceAmbientOcclusion>(); var gi = st.GetComponent<GlobalIllumination>(); var cs = st.GetComponent<ContactShadows>(); var fg = st.GetComponent<Fog>(); var sr = st.GetComponent<ScreenSpaceReflection>();
                if (ao != null) sb.Append("ssao.active=" + ao.active + " ssao.intensity=" + F(ao.intensity.value) + "; ");
                if (gi != null) sb.Append("ssgi.active=" + gi.active + " ssgi.enable=" + gi.enable.value + " ssgi.quality=" + gi.quality.value + "; ");
                if (cs != null) sb.Append("contact.active=" + cs.active + " contact.enable=" + cs.enable.value + "; ");
                if (fg != null) sb.Append("fog.active=" + fg.active + " fog.volumetric=" + fg.enableVolumetricFog.value + "; ");
                if (sr != null) sb.Append("ssr.active=" + sr.active + "; ");
            }
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (v == null || !v.enabled || v.profile == null) continue;
                sb.Append("[vol " + v.name + " prio=" + F(v.priority) + " global=" + v.isGlobal + " w=" + F(v.weight));
                foreach (var c in v.profile.components) if (c != null) sb.Append(" " + c.GetType().Name + (c.active ? "+" : "-"));
                sb.Append("] ");
            }
        }
        catch (Exception e) { sb.Append("err " + e.Message); }
        return sb.ToString();
    }

    // the render settings that are in force during the burst (so two runs can be compared like for like)
    string RenderInfo()
    {
        var sb = new StringBuilder();
        try
        {
            sb.Append("\"unity\":\"" + Application.unityVersion + "\",\"screen\":\"" + Screen.width + "x" + Screen.height + "\",\"fullscreen\":" + (Screen.fullScreen ? "true" : "false") + ",\"quality\":\"" + QualitySettings.names[QualitySettings.GetQualityLevel()] + "\"");
            sb.Append(",\"pipeline\":\"" + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none") + "\"");
            sb.Append(",\"vSync\":" + QualitySettings.vSyncCount + ",\"targetFrameRate\":" + Application.targetFrameRate + ",\"captureFramerate\":" + Time.captureFramerate + ",\"fixedDeltaTime\":" + F(Time.fixedDeltaTime) + ",\"timeScale\":" + F(Time.timeScale));
            sb.Append(",\"lodBias\":" + F(QualitySettings.lodBias) + ",\"shadowDistance\":" + F(QualitySettings.shadowDistance) + ",\"msaa\":" + QualitySettings.antiAliasing + ",\"scalerW\":" + F(ScalableBufferManager.widthScaleFactor) + ",\"scalerH\":" + F(ScalableBufferManager.heightScaleFactor));
            sb.Append(",\"flagsPath\":\"" + (RuntimeQuality.FlagsPath ?? "").Replace("\\", "/") + "\",\"fast\":" + (RuntimeQuality.Fast ? "true" : "false"));
            if (mainCam != null)
            {
                sb.Append(",\"camFov\":" + F(mainCam.fieldOfView) + ",\"camNear\":" + F(mainCam.nearClipPlane) + ",\"camPos\":\"" + mainCam.transform.position.ToString("F3") + "\",\"allowDynamicRes\":" + (mainCam.allowDynamicResolution ? "true" : "false") + ",\"pixelW\":" + mainCam.pixelWidth + ",\"pixelH\":" + mainCam.pixelHeight);
                var hd = mainCam.GetComponent<HDAdditionalCameraData>();
                if (hd != null) sb.Append(",\"hdAntialiasing\":\"" + hd.antialiasing + "\",\"hdDynamicRes\":" + (hd.allowDynamicResolution ? "true" : "false"));
            }
            int vols = 0; foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None)) if (v != null && v.enabled) vols++;
            sb.Append(",\"enabledVolumes\":" + vols);
            sb.Append(",\"cmdline\":\"" + string.Join(" ", Array.FindAll(Environment.GetCommandLineArgs(), a => a.StartsWith("-qset:") || a.StartsWith("-hide:") || a.StartsWith("-renderscale:") || a.StartsWith("-tier:"))).Replace("\"", "'") + "\"");   // laneWHIRLFIX
            sb.Append(",\"effective\":\"" + EffectiveStack().Replace("\"", "'") + "\"");   // laneWHIRLFIX
        }
        catch (Exception e) { sb.Append(",\"settingsError\":\"" + e.Message.Replace("\"", "'") + "\""); }
        return sb.ToString();
    }

    static float MeanLuma(Color32[] px)
    {
        double s = 0; int n = 0; for (int i = 0; i < px.Length; i += 3) { s += px[i].r * 0.299 + px[i].g * 0.587 + px[i].b * 0.114; n++; }
        return n > 0 ? (float)(s / (255.0 * n)) : 0f;
    }

    // read a screen rect (pixels, origin bottom-left) at end of frame
    Texture2D Read(int x, int y, int w, int h)
    {
        if (C.readMethod == "screencapture")
        {
            var full = ScreenCapture.CaptureScreenshotAsTexture(); if (full == null) return null;
            var crop = new Texture2D(w, h, TextureFormat.RGB24, false); crop.SetPixels(full.GetPixels(x, y, w, h)); crop.Apply(); Destroy(full); return crop;
        }
        var t = new Texture2D(w, h, TextureFormat.RGB24, false); t.ReadPixels(new Rect(x, y, w, h), 0, 0, false); t.Apply(false); return t;
    }

    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((Pl = Find(Roster.PlayerObject)) == null || (En = Find(Roster.EnemyObject)) == null || (rig = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSec) { Log("fighters / CameraRig not found (" + Roster.PlayerObject + "/" + Roster.EnemyObject + ")"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        mainCam = rig.GetComponent<Camera>(); if (mainCam == null) mainCam = Camera.main;
        if (!C.enabled) { Log("feetburst.json enabled=false"); Finish(0, "disabled"); yield break; }
        if ((Screen.width != C.width || Screen.height != C.height) && C.width > 0 && C.height > 0) { Log("resolution " + Screen.width + "x" + Screen.height + ", asking for " + C.width + "x" + C.height); Screen.SetResolution(C.width, C.height, false); float u0 = Time.unscaledTime + 1.5f; while (Time.unscaledTime < u0) yield return null; }
        if (C.requireResolution && (Screen.width != C.width || Screen.height != C.height)) { Fail("resolution is " + Screen.width + "x" + Screen.height + ", need " + C.width + "x" + C.height); Finish(1, "resolution"); yield break; }
        if (RuntimeQuality.Fast) { Fail("-fast given: the burst must use the real runtime_flags.json"); Finish(1, "fast flags"); yield break; }
        int cw = Mathf.Min(C.cropW, Screen.width), ch = Mathf.Min(C.cropH, Screen.height), ww = Mathf.Min(C.wideW, Screen.width), wh = Mathf.Min(C.wideH, Screen.height);

        // normal play: brains off so nothing moves on its own, the real rig frames the fight, then the pose is frozen
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        Pl.opponent = En; En.opponent = Pl; Pl.moveDir = Vector3.zero; En.moveDir = Vector3.zero; Pl.guardHeld = false; En.guardHeld = false;
        Time.timeScale = 1f; rig.lockEnabled = true;
        float us = Time.unscaledTime + C.settleSec; while (Time.unscaledTime < us && !exiting) yield return null;
        camPos = mainCam.transform.position; camRot = mainCam.transform.rotation; camFov = mainCam.fieldOfView;
        plStart = Pl.transform.position; plRot = Pl.transform.rotation; enStart = En.transform.position; enRot = En.transform.rotation;
        Vector3 toFoe = enStart - plStart; toFoe.y = 0f; turnAng0 = Mathf.Atan2(toFoe.z, toFoe.x);   // the foe starts where it stood: the orbit begins there
        rig.enabled = false; holdCam = true;
        Time.captureFramerate = Mathf.Max(1, C.captureFps); Time.fixedDeltaTime = 1f / Mathf.Max(1, C.captureFps); QualitySettings.vSyncCount = 0;
        yield return Frames(3);
        Log("start " + RenderInfo() + " data " + dataSrc + " roster " + Roster.PlayerId + " vs " + Roster.EnemyId + " crop " + cw + "x" + ch + " wide " + ww + "x" + wh + " frames " + C.frames + " states " + string.Join(",", C.states) + " out " + dir);
        File.WriteAllText(csvPath, "state,idx,unityFrame,simTime,wallSec,plX,plY,plZ,plYawDeg,footPxX,footPxY,cropX,cropY,cropW,cropH,cropLuma,wideLuma\n");

        foreach (var st in C.states)
        {
            if (exiting) yield break;
            curState = st; curFrame = -1;
            PlaceAt(Pl, plStart, plRot, false); PlaceAt(En, enStart, enRot, false); Pl.moveDir = Vector3.zero; En.moveDir = Vector3.zero; Pl.guardHeld = false; En.guardHeld = false;
            yield return Frames(C.stateSettleFrames);   // pose settled / animation blended before the first recorded frame
            string sdir = Path.Combine(dir, st); Directory.CreateDirectory(sdir);
            var tex = new List<Texture2D>(); int lastFrame = -1; int gaps = 0; float sumDt = 0f; int nDt = 0; float lastSim = 0f;
            for (int k = 0; k < C.frames && !exiting; k++)
            {
                curFrame = k;
                yield return null;                       // frame boundary: Update drives the state for frame k
                yield return new WaitForEndOfFrame();    // the frame is fully rendered
                CaptureUtil.HideConsole();
                int uf = Time.frameCount; float sim = Time.time, wall = Time.realtimeSinceStartup - t0;
                if (lastFrame >= 0 && uf != lastFrame + 1) { gaps++; Log("FRAME GAP " + st + " idx " + k + ": unity frame " + lastFrame + " -> " + uf); }
                if (lastFrame >= 0) { sumDt += sim - lastSim; nDt++; }
                lastFrame = uf; lastSim = sim;
                Vector3 sp = mainCam.WorldToScreenPoint(Pl.transform.position);
                int fx = Mathf.RoundToInt(sp.x), fy = Mathf.RoundToInt(sp.y);
                int cx = Mathf.Clamp(fx - cw / 2, 0, Screen.width - cw), cy = Mathf.Clamp(fy - Mathf.RoundToInt(ch * C.footYFrac), 0, Screen.height - ch);
                int wx = Mathf.Clamp(fx - ww / 2, 0, Screen.width - ww), wy = Mathf.Clamp(fy - wh / 2, 0, Screen.height - wh);
                var ct = Read(cx, cy, cw, ch);
                if (ct == null) { Fail("read failed " + st + " " + k); continue; }
                float cl = MeanLuma(ct.GetPixels32());
                float wl = -1f; { var wt = Read(wx, wy, ww, wh); if (wt != null) { wl = MeanLuma(wt.GetPixels32()); Destroy(wt); } }
                if (k == 0 && cl < 0.002f && C.readMethod != "screencapture") { Log("first crop is black with readpixels: switching to screencapture and restarting " + st); C.readMethod = "screencapture"; Destroy(ct); k = -1; lastFrame = -1; sumDt = 0f; nDt = 0; continue; }
                tex.Add(ct); totalFrames++;
                File.AppendAllText(csvPath, st + "," + k + "," + uf + "," + sim.ToString("F5", IC) + "," + wall.ToString("F3", IC) + "," + Pl.transform.position.x.ToString("F4", IC) + "," + Pl.transform.position.y.ToString("F4", IC) + "," + Pl.transform.position.z.ToString("F4", IC) + "," + Pl.transform.eulerAngles.y.ToString("F2", IC)
                    + "," + fx + "," + fy + "," + cx + "," + cy + "," + cw + "," + ch + "," + cl.ToString("F5", IC) + "," + wl.ToString("F5", IC) + "\n");
                foreach (int rf in C.refFrames) if (rf == k)
                {
                    var full = ScreenCapture.CaptureScreenshotAsTexture();
                    if (full != null) { File.WriteAllBytes(Path.Combine(dir, "ref_" + st + "_" + k.ToString("0000") + ".png"), full.EncodeToPNG()); Destroy(full); }
                }
            }
            curFrame = -1; Pl.moveDir = Vector3.zero;
            // write the buffered crops (the sim clock is locked, so the disk time cannot skip frames)
            for (int i = 0; i < tex.Count; i++) { File.WriteAllBytes(Path.Combine(sdir, "f_" + i.ToString("0000") + ".png"), tex[i].EncodeToPNG()); Destroy(tex[i]); }
            float meanDt = nDt > 0 ? sumDt / nDt : 0f;
            string line = st + ": " + tex.Count + " frames, unity frame gaps " + gaps + ", mean sim dt " + F(meanDt) + " s (want " + F(1f / C.captureFps) + "), plPos " + Pl.transform.position.ToString("F2");
            Log(line); metaStates.Add("{\"state\":\"" + st + "\",\"frames\":" + tex.Count + ",\"gaps\":" + gaps + ",\"meanSimDt\":" + F(meanDt) + "}");
            if (gaps > 0) Fail(st + ": " + gaps + " skipped frame(s)");
            if (nDt > 0 && Mathf.Abs(meanDt - 1f / C.captureFps) > 0.0005f) Fail(st + ": mean sim dt " + F(meanDt) + " != 1/" + C.captureFps);
        }
        holdCam = false;
        Finish(fails.Count == 0 ? 0 : 1, fails.Count == 0 ? "all states captured" : fails.Count + " problem(s)");
    }

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; holdCam = false; Time.captureFramerate = 0;
        try
        {
            string meta = "{\"harness\":\"feetburst\",\"verdict\":\"" + (code == 0 ? "DONE" : "FAIL") + "\",\"exit\":" + code + ",\"why\":\"" + why + "\",\"captureFps\":" + C.captureFps + ",\"frames\":" + C.frames + ",\"cropW\":" + C.cropW + ",\"cropH\":" + C.cropH + ",\"footYFrac\":" + F(C.footYFrac) + ",\"readMethod\":\"" + C.readMethod + "\",\"states\":[" + string.Join(",", metaStates.ToArray()) + "],\"render\":{" + RenderInfo() + "},\"totalFrames\":" + totalFrames + "}";
            File.WriteAllText(Path.Combine(dir, "feetburst_meta.json"), meta);
        }
        catch (Exception e) { Log("meta failed: " + e.Message); }
        Log("EXIT code " + code + " (" + why + ")");
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
