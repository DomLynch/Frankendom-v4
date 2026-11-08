#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// unity-dev v109: -lumatest:<dir>. ONE moment, four capture paths of the same frame, mean luma logged for each, all four saved as PNG (no desktop grab, no Win+PrtSc):
//   A  ScreenCapture.CaptureScreenshotAsTexture (the game's own back buffer) -> PNG straight to disk, no Blit.
//   B  the same texture through Graphics.Blit into an ARGB32 Default RT + ReadPixels (what ZoneCamTrace does at its scale/flip step).
//   C  Camera.main.Render into an sRGB RT, ReadPixels (what ZoneShot / the RT harness path does).
//   D  Camera.main.Render into a Default-ReadWrite RT, ReadPixels.
// Runs once, 3 s after zones are live, logs "[LumaTest] A=.. B=.. C=.. D=..", then quits. Without the flag: nothing is created. Not a harness flag.
public sealed class ZoneLumaTest : MonoBehaviour
{
    static string dir;
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        dir = Arg("-lumatest:"); if (string.IsNullOrEmpty(dir)) return;
        var go = new GameObject("ZoneLumaTest"); DontDestroyOnLoad(go); go.AddComponent<ZoneLumaTest>();
    }

    void Start() { StartCoroutine(Run()); }

    static float Mean(Texture2D t)
    {
        var px = t.GetPixels32(); double sum = 0; int n = 0;
        for (int i = 0; i < px.Length; i += 7) { var c = px[i]; sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; n++; }
        return n > 0 ? (float)(sum / n) : 0f;
    }

    static Texture2D FromRt(RenderTexture rt)
    {
        var prev = RenderTexture.active; RenderTexture.active = rt;
        var t = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false); t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); t.Apply(); RenderTexture.active = prev; return t;
    }

    static void Save(string name, Texture2D t) { try { File.WriteAllBytes(Path.Combine(dir, name), t.EncodeToPNG()); } catch (Exception e) { Debug.LogWarning("[LumaTest] save " + name + ": " + e.Message); } }

    IEnumerator Run()
    {
        try { Directory.CreateDirectory(dir); } catch (Exception) { yield break; }
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < 60f && (!ZoneRuntime.Owns || GameLoop.Player == null || Camera.main == null)) yield return null;
        float tw = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - tw < 3f) yield return null;
        yield return new WaitForEndOfFrame();
        float a = -1, b = -1, c = -1, d = -1;
        try
        {
            var src = ScreenCapture.CaptureScreenshotAsTexture(); a = Mean(src); Save("A_screencapture_direct.png", src);
            var rtB = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default); Graphics.Blit(src, rtB);
            var tb = FromRt(rtB); b = Mean(tb); Save("B_blit_argb32_default.png", tb); RenderTexture.ReleaseTemporary(rtB); Destroy(tb); Destroy(src);
            var cam = Camera.main; var keep = cam.targetTexture; int w = 1920, h = 1080;
            var rtC = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); cam.targetTexture = rtC; cam.Render(); cam.targetTexture = keep;
            var tc = FromRt(rtC); c = Mean(tc); Save("C_camrender_srgb_rt.png", tc); rtC.Release(); Destroy(rtC); Destroy(tc);
            var rtD = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default); cam.targetTexture = rtD; cam.Render(); cam.targetTexture = keep;
            var td = FromRt(rtD); d = Mean(td); Save("D_camrender_default_rt.png", td); rtD.Release(); Destroy(rtD); Destroy(td);
        }
        catch (Exception e) { Debug.LogWarning("[LumaTest] failed: " + e.Message); }
        Debug.Log("[LumaTest] mean luma 0-255: A screencapture direct " + a.ToString("F1") + " | B blit ARGB32 Default " + b.ToString("F1") + " | C cam.Render sRGB RT " + c.ToString("F1") + " | D cam.Render Default RT " + d.ToString("F1") + " | colorSpace " + QualitySettings.activeColorSpace);
        try   // v109: exposure + tonemapping of the live volume stack and the camera, next to the luma line (log only, no look change)
        {
            var st = VolumeManager.instance.stack; var ex = st.GetComponent<Exposure>(); var tm = st.GetComponent<Tonemapping>(); var cam = Camera.main; var hd = cam != null ? cam.GetComponent<HDAdditionalCameraData>() : null;
            Debug.Log("[LumaTest] exposure mode " + (ex != null ? ex.mode.value.ToString() : "none") + " fixed " + (ex != null ? ex.fixedExposure.value.ToString("F2") : "-") + " compensation " + (ex != null ? ex.compensation.value.ToString("F2") : "-")
                + " | tonemapping " + (tm != null ? tm.mode.value.ToString() : "none") + " | tier " + QualityTiers.Current + " | cam " + (cam != null ? cam.name : "none") + " hdr " + (cam != null && cam.allowHDR) + " renderScale " + Screen.width + "x" + Screen.height
                + " | aa " + (hd != null ? hd.antialiasing.ToString() : "-") + " | colorSpace " + QualitySettings.activeColorSpace);
        }
        catch (Exception e) { Debug.LogWarning("[LumaTest] exposure log failed: " + e.Message); }
        Application.Quit();
    }
}
#else // FIXHARNESS v1 nl=0
#endif // FIXHARNESS v1
