using System;
using System.IO;
using UnityEngine;

// FLICKERREC v1 (unity-build, Dom "zones 1 and 2 flash / flicker"): the -flickrec launch flag records a fixed-camera frame sequence in REAL game time (Time.captureFramerate 30, the game's own ScreenCapture, no desktop grab)
// so frame-to-frame luminance deltas can be computed per screen region. Phase 1 = Joan stands still (s_####.png), phase 2 = Joan walks forward (w_####.png). Joan is driven here (brains off), the save is never written.
// Flags: -flickrec  -out:<dir>  -flickstill:<frames, default 240>  -flickwalk:<frames, default 240>. Does nothing without -flickrec, never in harness runs.
// CAMLOCK v1: -camlock:<seconds> (with the fight bot, -autoplay -record) freezes the CameraRig after that many seconds of play so the camera is FIXED while the fighters move: the floor next to the fighters is then
// static in every frame and any frame-to-frame change in it is lighting / shadow / decal shimmer, not camera motion. Flag-only, never in harness runs, nothing written.
public sealed class CamLock : MonoBehaviour
{
    float t0 = -1f, lockAt; bool done;   // hc:ok -1 = not started
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (MenuCfg.Harness) return; float s = -1f;   // hc:ok -1 = flag absent
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-camlock:", StringComparison.Ordinal)) float.TryParse(a.Substring(9), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s);   // hc:ok flag parsing, sentinel and log format of a test tool
        if (s < 0f) return; var go = new GameObject("CamLock"); DontDestroyOnLoad(go); go.AddComponent<CamLock>().lockAt = s;   // hc:ok flag parsing, sentinel and log format of a test tool
    }
    void Update()
    {
        if (done) return; if (t0 < 0f) t0 = Time.time;   // hc:ok -1 sentinel if (Time.time - t0 < lockAt) return;
        int n = 0; foreach (var c in FindObjectsByType<CameraRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) { c.enabled = false; n++; }   // hc:ok flag parsing, sentinel and log format of a test tool
        done = n > 0; if (done) Debug.Log("[CamLock] camera rig frozen at t=" + (Time.time - t0).ToString("F1") + " s (" + n + " rig)");   // hc:ok flag parsing, sentinel and log format of a test tool
    }
}

[DefaultExecutionOrder(-150)]
public sealed class FlickerRec : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Has(string flag) { foreach (var a in Environment.GetCommandLineArgs()) if (a == flag) return true; return false; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (!Has("-flickrec") || MenuCfg.Harness) return; var go = new GameObject("FlickerRec"); DontDestroyOnLoad(go); go.AddComponent<FlickerRec>(); }

    DuelFighter df; string dir; float t0 = -1f, nextBind; int nStill = 240, nWalk = 240, n;   // hc:ok default frame counts of a proof tool
    const float WarmS = 10f;   // hc:ok seconds to let the zone, the lights and the streaming settle before recording
    bool started;

    void Awake()
    {
        dir = Arg("-out:") ?? Path.Combine(Application.dataPath, "..", "FlickerRec"); try { Directory.CreateDirectory(dir); } catch (Exception) { }
        int v; if (int.TryParse(Arg("-flickstill:"), out v)) nStill = v; if (int.TryParse(Arg("-flickwalk:"), out v)) nWalk = v;
        Debug.Log("[FlickerRec] armed still " + nStill + " walk " + nWalk + " frames -> " + dir);
    }

    bool Bind()
    {
        if (df != null) return true; if (Time.unscaledTime < nextBind) return false; nextBind = Time.unscaledTime + 0.5f;   // hc:ok poll period
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { df = f; break; }
        if (df != null) foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is TouchControls) b.enabled = false;
        return df != null;
    }

    void Update()
    {
        if (!Bind()) return; if (t0 < 0f) t0 = Time.time;
        if (!started)
        {
            df.moveDir = Vector3.zero;
            if (Time.time - t0 < WarmS) return;
            started = true; Time.captureFramerate = 30; Debug.Log("[FlickerRec] recording starts, player " + df.transform.position.ToString("F2") + " zone t=" + Time.time.ToString("F1"));   // hc:ok 30 fps capture clock
        }
        if (n < nStill) { df.moveDir = Vector3.zero; df.run = false; ScreenCapture.CaptureScreenshot(Path.Combine(dir, "s_" + n.ToString("0000") + ".png")); }
        else if (n < nStill + nWalk) { df.moveDir = Vector3.forward; df.run = false; ScreenCapture.CaptureScreenshot(Path.Combine(dir, "w_" + (n - nStill).ToString("0000") + ".png")); }
        else if (n == nStill + nWalk + 5) { Debug.Log("[FlickerRec] DONE " + n + " frames, player " + df.transform.position.ToString("F2")); df.moveDir = Vector3.zero; Time.captureFramerate = 0; Invoke(nameof(Quit), 3f); }   // hc:ok 5 = frames for the last screenshots to flush, 3 s quit delay
        n++;
    }
    void Quit() { Application.Quit(); }
}
