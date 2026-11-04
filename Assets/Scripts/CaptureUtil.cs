using System;
using System.Reflection;
using UnityEngine;

// V61C: one shared helper that keeps the Unity Development Console (red exception lines at the bottom of the frame) and any IMGUI log reporter OUT of captured frames.
// Capture paths (FinisherAuto, PostureProof, BalanceAuto, MoveSheet) call CaptureUtil.HideConsole() right before a screenshot. Besides that a tiny guard object (created only when a capture harness
// is on the command line) hides it every frame, because a new error line re-opens the console in a development player.
// NOTE: the small "Development build" watermark at the bottom right is drawn by the player of a Development build and cannot be hidden from script; build WITHOUT "Development Build" for presentation captures.
public static class CaptureUtil
{
    static Type reporterType; static bool reporterLooked; static float nextReporter;

    /// <summary>Hide the development console and any "Reporter" IMGUI log overlay. Cheap, idempotent, never throws.</summary>
    public static void HideConsole()
    {
        try { if (Debug.developerConsoleVisible) Debug.developerConsoleVisible = false; } catch { }
        try { Debug.developerConsoleEnabled = false; } catch { }
        try { HideReporters(); } catch { }
    }

    static void HideReporters()
    {
        if (Time.realtimeSinceStartup < nextReporter) return;
        nextReporter = Time.realtimeSinceStartup + 3f;
        if (!reporterLooked)
        {
            reporterLooked = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null; try { t = asm.GetType("Reporter", false); } catch { }
                if (t != null && typeof(MonoBehaviour).IsAssignableFrom(t)) { reporterType = t; break; }
            }
        }
        if (reporterType == null) return;
        foreach (var o in UnityEngine.Object.FindObjectsByType(reporterType, FindObjectsSortMode.None)) { var mb = o as MonoBehaviour; if (mb != null && mb.enabled) mb.enabled = false; }
    }

    static bool HarnessRequested()
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (a == "-finisherauto" || a == "-postureproof" || a == "-balanceauto" || a.StartsWith("-sheet:", StringComparison.Ordinal) || a.StartsWith("-out:", StringComparison.Ordinal)) return true;
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (!HarnessRequested()) return;
            HideConsole();
            if (UnityEngine.Object.FindAnyObjectByType<CaptureConsoleGuard>() != null) return;
            var go = new GameObject("CaptureConsoleGuard"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<CaptureConsoleGuard>();
        }
        catch (Exception e) { Debug.LogWarning("[CaptureUtil] boot: " + e.Message); }
    }
}

[DefaultExecutionOrder(32000)]
public class CaptureConsoleGuard : MonoBehaviour
{
    void Update() { CaptureUtil.HideConsole(); }
    void LateUpdate() { CaptureUtil.HideConsole(); }
}
