using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

// STARTUP v102 (laneSTARTUP). The first rendered frames of the real HDRP scene stall 0.8-1.4 s (D3D12 pipeline states, volumetric fog + shadow atlas allocation, first SSGI/post pass). v100 and v101 logs show the same stall
// (frame 2: 752-1150 ms in v100, 778-1115 ms in v101); in the cam / flow harness logs it lands later (frame 5 / 14) because those harnesses start their own capture a few frames after the load.
// Prewarm92 / FxPrewarm start 0.75-1.5 s after the load and do not run in harnesses, so nothing paid this cost before the first gameplay frames.
// This component renders the main camera warmRenders times from the first frames after the scene load (Camera.Render), behind an opaque IMGUI cover in player runs, so the one-time cost is paid on purpose and logged, not in the middle of the fight start.
// Nothing here touches the sim: no tick, no RNG, no gameplay state (holdSim is the only sim touch, off by default). Data: startup_warm.json. Off: enabled=false or -nostartupwarm.
// Log lines: "[StartupWarm] render <i> <ms> ms" and "[StartupWarm] done ...".
public class StartupWarm : MonoBehaviour
{
    [Serializable] public class CoverC { public bool enabled = true; public float r = 0f, g = 0f, b = 0f; public int holdFramesAfter = 2; }
    [Serializable] public class HoldC { public bool enabled = false; }
    [Serializable] public class HarnessC { public bool warmRenders = true; }
    [Serializable]
    public class Cfg
    {
        public bool enabled = true; public int warmRenders = 2, startDelayFrames = 1, maxWaitFrames = 300, framesBetweenRenders = 1;
        public CoverC cover = new CoverC(); public HoldC holdSim = new HoldC(); public HarnessC inHarness = new HarnessC();
    }

    static Cfg cfg;
    public static bool Done;   // true once the warm renders (and the cover hold) are over
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; Done = false; }

    static Cfg Load()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        try { var txt = HudCfg.ReadData("startup_warm.json"); if (txt != null) { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } }
        catch (Exception e) { Debug.LogWarning("[StartupWarm] startup_warm.json: " + e.Message); }
        if (cfg.cover == null) cfg.cover = new CoverC(); if (cfg.holdSim == null) cfg.holdSim = new HoldC(); if (cfg.inHarness == null) cfg.inHarness = new HarnessC();
        cfg.warmRenders = Mathf.Max(0, cfg.warmRenders); cfg.startDelayFrames = Mathf.Max(0, cfg.startDelayFrames); cfg.framesBetweenRenders = Mathf.Max(1, cfg.framesBetweenRenders);
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        foreach (var a in Environment.GetCommandLineArgs()) if (a.Equals("-nostartupwarm", StringComparison.OrdinalIgnoreCase)) return;
        var c = Load(); if (!c.enabled || c.warmRenders <= 0) return;
        bool harness = PerfFlags.HarnessRun();
        if (harness && !c.inHarness.warmRenders) return;
        if (FindFirstObjectByType<StartupWarm>() != null) return;
        var go = new GameObject("StartupWarm"); DontDestroyOnLoad(go); go.AddComponent<StartupWarm>().harness = harness;
    }

    bool harness, cover, froze; Texture2D tex;

    IEnumerator Start()
    {
        var c = cfg; cover = c.cover.enabled && !harness;
        if (cover) { tex = new Texture2D(1, 1); tex.SetPixel(0, 0, new Color(c.cover.r, c.cover.g, c.cover.b, 1f)); tex.Apply(); }
        if (c.holdSim.enabled && !harness && !Duel.Frozen) { Duel.Frozen = true; froze = true; }
        for (int i = 0; i < c.startDelayFrames; i++) yield return null;
        Camera cam = null; for (int i = 0; i < c.maxWaitFrames && (cam = Camera.main) == null; i++) yield return null;
        float t0 = Time.realtimeSinceStartup; var sw = new Stopwatch(); double total = 0, first = 0, last = 0; int done = 0;
        for (int r = 0; r < c.warmRenders && cam != null; r++)
        {
            cam = Camera.main; if (cam == null) break;
            sw.Restart(); try { cam.Render(); } catch (Exception e) { Debug.LogWarning("[StartupWarm] render " + r + ": " + e.Message); }
            sw.Stop(); double ms = sw.Elapsed.TotalMilliseconds; total += ms; done++; if (r == 0) first = ms; last = ms;
            Debug.Log("[StartupWarm] render " + r + " " + ms.ToString("0") + " ms");
            for (int k = 0; k < c.framesBetweenRenders; k++) yield return null;
        }
        for (int i = 0; i < c.cover.holdFramesAfter && cover; i++) yield return null;
        Debug.Log("[StartupWarm] done renders " + done + " (first " + first.ToString("0") + " ms, last " + last.ToString("0") + " ms, total " + total.ToString("0") + " ms), " + (Time.realtimeSinceStartup - t0).ToString("0.00") + " s wall, cover " + cover + ", sim hold " + froze + ", harness " + harness);
        Release();
    }

    void Release()
    {
        if (froze) { Duel.Frozen = false; froze = false; }
        cover = false; Done = true;
        if (tex != null) { Destroy(tex); tex = null; }
    }
    void OnDestroy() { if (froze) Duel.Frozen = false; }

    void OnGUI()
    {
        if (!cover || tex == null || Event.current.type != EventType.Repaint) return;
        GUI.depth = -10000; GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tex, ScaleMode.StretchToFill, false);
    }
}
