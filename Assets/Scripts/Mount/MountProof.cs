using System;
using System.IO;
using UnityEngine;

// MOUNTPROOF v1 (unity-build, Dom order "get horse riding in"): the -mountproof launch flag drives ONE scripted ride so the mount can be proven without a human:
// wait for the horse to load, summon (virtual M), walk, gallop, stop, dismount, with [MountProof] log lines and in-game screenshots (-out:<dir>, default <build>\MountProof).
// Needs mount.json enabled=true in the build (test copy). Joan is driven here (PlayerBrain / AIBrain / TouchControls off, like FeetClip); the save is never written (MountRider.ProofOwn only fakes "owned" in memory).
// Does nothing without the flag, and never in harness runs.
[DefaultExecutionOrder(-150)]   // before MountRider (-100): the move intent is set, then the rider multiplies the speed on the sim tick
public sealed class MountProof : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Has(string flag) { foreach (var a in Environment.GetCommandLineArgs()) if (a == flag) return true; return false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (!Has("-mountproof") || MenuCfg.Harness) return; var go = new GameObject("MountProof"); DontDestroyOnLoad(go); go.AddComponent<MountProof>(); }

    DuelFighter df; float t0 = -1f, nextBind, nextLog, phaseT, moved0; int stage; string dir; Vector3 startPos; float minY = 1e9f, maxDropY;
    const float WarmS = 6f, LoadMaxS = 60f, WalkS = 5f, GallopS = 4f, StopS = 1.5f;   // hc:ok proof timeline in seconds (test tool, not a gameplay value)

    void Awake() { dir = Arg("-out:") ?? Path.Combine(Application.dataPath, "..", "MountProof"); try { Directory.CreateDirectory(dir); } catch (Exception) { } MountRider.ProofOwn = true; Debug.Log("[MountProof] armed, frames -> " + dir); }

    bool Bind()
    {
        if (df != null) return true; if (Time.unscaledTime < nextBind) return false; nextBind = Time.unscaledTime + 0.5f;   // hc:ok poll period
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { df = f; break; }
        if (df != null) foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        return df != null;
    }

    void Shot(string name) { try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png")); Debug.Log("[MountProof] shot " + name); } catch (Exception e) { Debug.LogWarning("[MountProof] shot failed " + e.Message); } }
    void Next(string why) { stage++; phaseT = Time.time; Debug.Log("[MountProof] stage " + stage + " (" + why + ") " + MountRider.ProofInfo()); }

    bool shotDone, foot = Has("-footfirst"); int footState = -1; float footT, footD; Vector3 footP;   // -footfirst: measure Joan's on-foot walk and run speed first (baseline for the mounted numbers)
    void Update()
    {
        if (!Bind()) return; if (t0 < 0f) { t0 = Time.time; startPos = df.transform.position; phaseT = Time.time; }
        var p = df.transform.position; if (p.y < minY) minY = p.y; if (startPos.y - p.y > maxDropY) maxDropY = startPos.y - p.y;
        if (Time.time >= nextLog) { nextLog = Time.time + 1f; Debug.Log("[MountProof] t=" + (Time.time - t0).ToString("F1") + " stage " + stage + " " + MountRider.ProofInfo()); }   // hc:ok 1 s log period
        float st = Time.time - phaseT;
        switch (stage)
        {
            case 0:   // wait for zones + horse
                if (Time.time - t0 > LoadMaxS) { Debug.Log("[MountProof] FAIL horse never loaded in " + LoadMaxS + " s: " + MountRider.ProofInfo()); stage = 99; Finish(); return; }
                if (foot && footState < 3 && Time.time - t0 > WarmS && MountRider.ProofReady)
                {
                    if (footState < 0) { footState = 0; footT = Time.time; footP = df.transform.position; }
                    float ft = Time.time - footT; var fp = df.transform.position;
                    if (footState == 0) { df.moveDir = Vector3.forward; df.run = false; if (ft > 4f) { var d0 = fp - footP; d0.y = 0f; Debug.Log("[MountProof] FOOT walk covered " + d0.magnitude.ToString("F2") + " m in 4.0 s = " + (d0.magnitude / 4f).ToString("F2") + " m/s (walkSpeed " + df.walkSpeed.ToString("F2") + ")"); footState = 1; footT = Time.time; footP = fp; } }   // hc:ok seconds of the foot baseline
                    else if (footState == 1) { df.moveDir = Vector3.forward; df.run = true; if (ft > 3f) { var d1 = fp - footP; d1.y = 0f; Debug.Log("[MountProof] FOOT run covered " + d1.magnitude.ToString("F2") + " m in 3.0 s = " + (d1.magnitude / 3f).ToString("F2") + " m/s (runSpeed " + df.runSpeed.ToString("F2") + ")"); footState = 2; footT = Time.time; } }   // hc:ok seconds of the foot baseline
                    else { df.moveDir = Vector3.zero; df.run = false; if (ft > 1.5f) footState = 3; }   // hc:ok settle seconds
                    break;
                }
                if (Time.time - t0 > WarmS && MountRider.ProofReady) { Debug.Log("[MountProof] refuse check before summon: " + (MountRider.ProofRefuse() ?? "none")); Shot("0_before"); MountRider.ProofKey = true; shotDone = false; Next("summon pressed"); }
                break;
            case 1:   // did it mount?
                if (MountRider.Mounted) { moved0 = 0f; startPos = df.transform.position; shotDone = false; Next("MOUNTED"); stage = 2; phaseT = Time.time; }
                else if (st > 3f) { Debug.Log("[MountProof] FAIL not mounted 3 s after the summon press, refuse=" + (MountRider.ProofRefuse() ?? "none") + " " + MountRider.ProofInfo()); stage = 99; Finish(); }   // hc:ok seconds
                break;
            case 2:   // walk
                df.moveDir = Vector3.forward; df.run = false;
                if (!shotDone && st > WalkS * 0.6f) { shotDone = true; Shot("1_walk"); }   // hc:ok mid-walk frame
                if (st > WalkS) { Seg(WalkS, "walk"); shotDone = false; stage = 3; phaseT = Time.time; }
                break;
            case 3:   // gallop
                df.moveDir = Vector3.forward; df.run = true;
                if (!shotDone && st > GallopS * 0.6f) { shotDone = true; Shot("2_gallop"); }   // hc:ok mid-gallop frame
                if (st > GallopS) { Seg(GallopS, "gallop"); stage = 4; phaseT = Time.time; }
                break;
            case 4:   // stop
                df.moveDir = Vector3.zero; df.run = false;
                if (st > StopS) { Shot("3_stopped"); MountRider.ProofKey = true; Next("dismount pressed"); stage = 5; }
                break;
            case 5:   // dismounted?
                if (!MountRider.Mounted) { Shot("4_dismounted"); Next("DISMOUNTED"); stage = 6; phaseT = Time.time; }
                else if (st > 3f) { Debug.Log("[MountProof] FAIL still mounted 3 s after the dismount press"); stage = 99; Finish(); }   // hc:ok seconds
                break;
            case 6:
                if (st > 2f) { Debug.Log("[MountProof] DONE minPlayerY " + minY.ToString("F2") + " maxDropFromStart " + maxDropY.ToString("F2") + " (a drop above 1 m = fell or sank)"); stage = 99; Finish(); }   // hc:ok seconds
                break;
        }
    }

    void Seg(float secs, string what)
    {
        var d = df.transform.position - startPos; d.y = 0f; float cov = d.magnitude - moved0;
        Debug.Log("[MountProof] " + what + " covered " + cov.ToString("F2") + " m in " + secs.ToString("F1") + " s = " + (cov / secs).ToString("F2") + " m/s (df.walkSpeed " + df.walkSpeed.ToString("F2") + ", runSpeed " + df.runSpeed.ToString("F2") + ")");
        moved0 = d.magnitude;
    }

    bool quitting;
    void Finish() { if (quitting) return; quitting = true; Debug.Log("[MountProof] END, quitting"); MountRider.ProofOwn = false; Invoke(nameof(Quit), 2f); }   // hc:ok seconds for the last screenshot to flush
    void Quit() { Application.Quit(); }
}
