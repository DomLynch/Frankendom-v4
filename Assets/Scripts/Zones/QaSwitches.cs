using System;
using UnityEngine;

// QASWITCH v1 (Lead, 18 Nov): command-line switches for CPU/GPU hunts, QA only. Nothing runs without a -qa_* argument (one static scan, no component, no data, no serialized field).
//   -qa_hide_r              every spawned zone enemy gets its Renderers disabled (logic keeps running): splits render cost from logic cost.
//   -qa_no:<a,b,...>        skip per-enemy attachers by name: monsterlook, armhold, weapongrip, tailspring, footground, footyaw, headclamp, guardtorso, gearslots, hitreact, prebuild (ragdoll prebuild), chase (ChaseAgent).
// Example: HadesSlice.exe ... -qa_no:footground,hitreact
public static class QaSwitches
{
    static bool init; static bool hideR; static string no = "";
    static void Init()
    {
        if (init) return; init = true;
        try { foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-qa_hide_r") hideR = true; else if (a.StartsWith("-qa_no:", StringComparison.Ordinal)) no = "," + a.Substring(7).ToLowerInvariant() + ","; } }
        catch (Exception) { }
        if (hideR || no.Length > 0) Debug.Log("[QaSwitches] hide renderers " + hideR + ", skip " + (no.Length > 0 ? no : "(none)"));
    }
    // -qa_fps:N (QA only, K7 crash fix): cap the frame rate of a QA player (Application.targetFrameRate = N, vSyncCount 0) so it cannot hog the shared iGPU; re-asserted by Tick() because later quality code may reset it.
    static int capFps = -1;
    public static void Tick() { if (capFps < 0) { capFps = 0; try { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-qa_fps:", StringComparison.Ordinal)) { int v; if (int.TryParse(a.Substring(8), out v) && v > 0) capFps = v; } } catch (Exception) { } if (capFps > 0) Debug.Log("[QaSwitches] frame cap " + capFps); } if (capFps > 0 && (Application.targetFrameRate != capFps || QualitySettings.vSyncCount != 0)) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = capFps; } }
    public static bool HideRenderers { get { Init(); return hideR; } }
    public static bool Skip(string what) { Init(); return no.Length > 0 && no.IndexOf("," + what + ",", StringComparison.Ordinal) >= 0; }
    public static void ApplyHide(GameObject go) { if (!HideRenderers || go == null) return; foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false; }
}
