using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// laneWHIRLFIX FEET CLIP HARNESS: -feetclip:<dir> [-feetclipdata:<json>]   (MEASURING ONLY, OFF unless the flag is given; every number is feetclip.json)
// What Dom asked: game frames (never screen grabs of the desktop) at REAL game speed (no Time.captureFramerate, no frozen camera), the real gameplay camera, Joan walking toward Pitborn and then round him
//   with both pairs of feet in view for approachSec + orbitSec (10 s), then holdSec standing still; the whole frame as a video and a FIXED screen region at native resolution (no foot-following crop).
// How: every rendered frame, at end of frame, ScreenCapture.CaptureScreenshotIntoRenderTexture (the game's final image, GPU copy) -> (a) Graphics.Blit to videoW x videoH, (b) CopyTexture of the fixed region
//   (regionX/Y/W/H are FRACTIONS of the screen, origin top-left) -> AsyncGPUReadback (no stall). Frames are buffered in RAM (maxFrames) and written AFTER the clip: video/v_NNNN.jpg, region/r_NNNN.png (lossless),
//   clip_frames.csv (frame, Time.frameCount, real seconds, unscaled dt, phase, camera pos/rot/fov, both fighters' positions => ground-locked re-projection is possible), clip_meta.json (settings, args, effective volume stack).
//   _agents\laneWHIRLFIX\tools\clip_to_mp4.py builds the MP4 (real timestamps from the csv) and clip_score.py scores the region per phase.
// Capture adds GPU/RAM cost: fps in the clip is the fps of the capture run, compare clips only with clips. UNPROVEN in Unity: CaptureScreenshotIntoRenderTexture orientation (flipY in the json; first_full.png shows it).   // laneWHIRLFIX
[DefaultExecutionOrder(-56)]
public class FeetClip : MonoBehaviour
{
    [Serializable]
    public class Cfg
    {
        public bool enabled = true, requireResolution = true, muteAudio = true, flipY = false;
        public int width = 2560, height = 1600, videoW = 1280, videoH = 800, maxFrames = 1200, jpgQuality = 92;
        public float regionX = 0.2f, regionY = 0.55f, regionW = 0.6f, regionH = 0.45f;
        public float settleSec = 2f, approachSec = 3f, orbitSec = 7f, holdSec = 3f, orbitRadiusM = 2.4f, orbitDegDir = 1f, radialGain = 1.5f, bootWaitSec = 30f, timeoutSec = 180f, exitGraceSec = 3f;
    }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F4", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Arg("-feetclip:") == null) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<FeetClip>() != null) return;
            var go = new GameObject("FeetClip"); DontDestroyOnLoad(go); go.AddComponent<FeetClip>();
        }
        catch (Exception e) { Debug.LogError("[feetclip] boot failed: " + e); }
    }

    class Fr { public byte[] vid, reg; public int n, fc; public float t, dt; public string phase, cam; }

    Cfg C = new Cfg(); string dir, logPath;
    float t0, exitAt; bool exiting; int exitCode;
    DuelFighter Pl, En; CameraRig rig; Camera mainCam;
    string phase = "";
    readonly List<Fr> frames = new List<Fr>(); readonly List<string> fails = new List<string>();
    int pending;

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[feetclip] " + s);
    }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-feetclip:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "feetclip"); } catch { dir = "feetclip"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "clip_log.txt"); try { File.WriteAllText(logPath, ""); } catch { }
        string cli = Arg("-feetclipdata:");
        foreach (var p in new[] { cli, Path.Combine(Application.streamingAssetsPath, "feetclip.json"), Path.Combine(Application.dataPath, "Data/feetclip.json") })
        {
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
            try { var c = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart('﻿')); if (c != null) { C = c; Log("data " + p); break; } } catch (Exception e) { Debug.LogWarning("[feetclip] " + p + ": " + e.Message); }
        }
        if (C.muteAudio) AudioListener.volume = 0f;
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

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }

    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > C.exitGraceSec) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT"); Finish(3, "timeout"); return; }
        if (Pl == null || En == null) return;
        Vector3 d = En.transform.position - Pl.transform.position; d.y = 0f; float dist = d.magnitude; Vector3 rad = dist > 1e-4f ? d / dist : Vector3.forward;
        if (phase == "approach") Pl.moveDir = dist > C.orbitRadiusM ? rad : Vector3.zero;
        else if (phase == "orbit")
        {
            Vector3 tan = Vector3.Cross(Vector3.up, rad) * (C.orbitDegDir < 0f ? -1f : 1f);
            Pl.moveDir = (tan + rad * Mathf.Clamp((dist - C.orbitRadiusM) * C.radialGain, -1f, 1f)).normalized;
        }
        else Pl.moveDir = Vector3.zero;
        En.moveDir = Vector3.zero;
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
        if (!C.enabled) { Finish(0, "disabled"); yield break; }
        if ((Screen.width != C.width || Screen.height != C.height) && C.width > 0) { Screen.SetResolution(C.width, C.height, false); yield return null; yield return null; }
        if (C.requireResolution && (Screen.width != C.width || Screen.height != C.height)) { fails.Add("resolution " + Screen.width + "x" + Screen.height); Finish(1, "resolution"); yield break; }
        if (RuntimeQuality.Fast) { fails.Add("-fast"); Finish(1, "fast flags"); yield break; }
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;   // Joan is driven here, the foe stands; the camera rig stays live (real gameplay camera)
        Pl.opponent = En; En.opponent = Pl; Pl.guardHeld = false; En.guardHeld = false; rig.lockEnabled = true;
        float us = Time.unscaledTime + C.settleSec; while (Time.unscaledTime < us && !exiting) yield return null;

        int sw = Screen.width, sh = Screen.height;
        int rx = Mathf.Clamp(Mathf.RoundToInt(C.regionX * sw), 0, sw - 8), rw = Mathf.Clamp(Mathf.RoundToInt(C.regionW * sw), 8, sw - rx);
        int ryTop = Mathf.Clamp(Mathf.RoundToInt(C.regionY * sh), 0, sh - 8), rh = Mathf.Clamp(Mathf.RoundToInt(C.regionH * sh), 8, sh - ryTop);
        int ryBottomOrigin = sh - ryTop - rh;   // RT origin is bottom-left
        var rtFull = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = "feetclipFull" };
        var rtFlip = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32) { name = "feetclipFlip" };
        var rtVid = new RenderTexture(C.videoW, C.videoH, 0, RenderTextureFormat.ARGB32) { name = "feetclipVid" };
        var rtReg = new RenderTexture(rw, rh, 0, RenderTextureFormat.ARGB32) { name = "feetclipReg" };
        rtFull.Create(); rtFlip.Create(); rtVid.Create(); rtReg.Create();
        float total = C.approachSec + C.orbitSec + C.holdSec; float start = Time.unscaledTime; float lastT = start; int n = 0; bool first = true;
        Log("clip start: " + total.ToString("F1", IC) + " s real time, region " + rx + "," + ryTop + " " + rw + "x" + rh + " (top-left px), video " + C.videoW + "x" + C.videoH);
        while (!exiting && Time.unscaledTime - start < total && n < C.maxFrames)
        {
            float el = Time.unscaledTime - start;
            string ph = el < C.approachSec ? "approach" : el < C.approachSec + C.orbitSec ? "orbit" : "hold";
            if (ph != phase) { phase = ph; Log("phase " + ph); }
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshotIntoRenderTexture(rtFull);
            RenderTexture src = rtFull;
            if (C.flipY) { Graphics.Blit(rtFull, rtFlip, new Vector2(1f, -1f), new Vector2(0f, 1f)); src = rtFlip; }
            Graphics.Blit(src, rtVid);
            Graphics.CopyTexture(src, 0, 0, rx, ryBottomOrigin, rw, rh, rtReg, 0, 0, 0, 0);
            var fr = new Fr { n = n, fc = Time.frameCount, t = Time.unscaledTime - start, dt = Time.unscaledTime - lastT, phase = phase };
            lastT = Time.unscaledTime;
            Vector3 cp = mainCam.transform.position, pp = Pl.transform.position, ep = En.transform.position; Vector3 ce = mainCam.transform.eulerAngles;
            fr.cam = F(cp.x) + "," + F(cp.y) + "," + F(cp.z) + "," + F(ce.x) + "," + F(ce.y) + "," + F(ce.z) + "," + F(mainCam.fieldOfView) + "," + F(pp.x) + "," + F(pp.z) + "," + F(ep.x) + "," + F(ep.z);
            frames.Add(fr); pending += 2;
            AsyncGPUReadback.Request(rtVid, 0, TextureFormat.RGBA32, (AsyncGPUReadbackRequest r) => { pending--; if (!r.hasError) fr.vid = r.GetData<byte>().ToArray(); });
            AsyncGPUReadback.Request(rtReg, 0, TextureFormat.RGBA32, (AsyncGPUReadbackRequest r) => { pending--; if (!r.hasError) fr.reg = r.GetData<byte>().ToArray(); });
            if (first)
            {
                first = false;
                var one = new Texture2D(sw, sh, TextureFormat.RGB24, false); var prev = RenderTexture.active; RenderTexture.active = src;
                one.ReadPixels(new Rect(0, 0, sw, sh), 0, 0, false); RenderTexture.active = prev; File.WriteAllBytes(Path.Combine(dir, "first_full.png"), one.EncodeToPNG()); Destroy(one);
            }
            n++;
        }
        phase = ""; Pl.moveDir = Vector3.zero;
        float real = Time.unscaledTime - start; Log("captured " + n + " frames in " + real.ToString("F2", IC) + " s = " + (n / Mathf.Max(0.001f, real)).ToString("F1", IC) + " fps; waiting for readbacks");
        int guard = 0; while (pending > 0 && guard++ < 600) { AsyncGPUReadback.WaitAllRequests(); yield return null; }
        // write after the clip (the game clock no longer matters)
        string vd = Path.Combine(dir, "video"), rd = Path.Combine(dir, "region"); Directory.CreateDirectory(vd); Directory.CreateDirectory(rd);
        var csv = new StringBuilder("frame,unityFrame,realSec,dtSec,phase,camX,camY,camZ,camPitch,camYaw,camRoll,fov,plX,plZ,enX,enZ\n");
        var tv = new Texture2D(C.videoW, C.videoH, TextureFormat.RGBA32, false); var tr = new Texture2D(rw, rh, TextureFormat.RGBA32, false); int missing = 0;
        foreach (var fr in frames)
        {
            csv.Append(fr.n + "," + fr.fc + "," + F(fr.t) + "," + F(fr.dt) + "," + fr.phase + "," + fr.cam + "\n");
            if (fr.vid == null || fr.reg == null) { missing++; continue; }
            tv.LoadRawTextureData(fr.vid); tv.Apply(false); File.WriteAllBytes(Path.Combine(vd, "v_" + fr.n.ToString("0000") + ".jpg"), tv.EncodeToJPG(C.jpgQuality));
            tr.LoadRawTextureData(fr.reg); tr.Apply(false); File.WriteAllBytes(Path.Combine(rd, "r_" + fr.n.ToString("0000") + ".png"), tr.EncodeToPNG());
            fr.vid = null; fr.reg = null;
        }
        File.WriteAllText(Path.Combine(dir, "clip_frames.csv"), csv.ToString());
        if (missing > 0) fails.Add(missing + " frame(s) without readback data");
        try
        {
            var m = new StringBuilder("{\"harness\":\"feetclip\",\"frames\":" + n + ",\"realSec\":" + F(real) + ",\"avgFps\":" + F(n / Mathf.Max(0.001f, real)) + ",\"timeScale\":" + F(Time.timeScale) + ",\"captureFramerate\":" + Time.captureFramerate);
            m.Append(",\"screen\":\"" + Screen.width + "x" + Screen.height + "\",\"region\":\"" + rx + "," + ryTop + "," + rw + "," + rh + "\",\"flipY\":" + (C.flipY ? "true" : "false") + ",\"unity\":\"" + Application.unityVersion + "\",\"flagsPath\":\"" + (RuntimeQuality.FlagsPath ?? "").Replace("\\", "/") + "\"");
            m.Append(",\"cmdline\":\"" + string.Join(" ", Array.FindAll(Environment.GetCommandLineArgs(), a => a.StartsWith("-qset:") || a.StartsWith("-hide:") || a.StartsWith("-renderscale:"))).Replace("\"", "'") + "\"");
            m.Append(",\"effective\":\"" + EffectiveStack().Replace("\"", "'") + "\"");
            var hd = mainCam.GetComponent<HDAdditionalCameraData>(); if (hd != null) m.Append(",\"hdAntialiasing\":\"" + hd.antialiasing + "\",\"hdDynamicRes\":" + (hd.allowDynamicResolution ? "true" : "false"));
            m.Append("}"); File.WriteAllText(Path.Combine(dir, "clip_meta.json"), m.ToString());
        }
        catch (Exception e) { Log("meta failed: " + e.Message); }
        Finish(fails.Count == 0 ? 0 : 1, fails.Count == 0 ? "clip written" : string.Join("; ", fails));
    }

    string EffectiveStack()
    {
        var sb = new StringBuilder();
        try
        {
            var st = VolumeManager.instance.stack;
            if (st != null)
            {
                var ao = st.GetComponent<ScreenSpaceAmbientOcclusion>(); var gi = st.GetComponent<GlobalIllumination>(); var cs = st.GetComponent<ContactShadows>(); var fg = st.GetComponent<Fog>();
                if (ao != null) sb.Append("ssao.active=" + ao.active + "; ");
                if (gi != null) sb.Append("ssgi.active=" + gi.active + "; ");
                if (cs != null) sb.Append("contact.active=" + cs.active + "; ");
                if (fg != null) sb.Append("fog.volumetric=" + fg.enableVolumetricFog.value + "; ");
            }
        }
        catch (Exception e) { sb.Append("err " + e.Message); }
        return sb.ToString();
    }

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime;
        Log("EXIT code " + code + " (" + why + ")");
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
