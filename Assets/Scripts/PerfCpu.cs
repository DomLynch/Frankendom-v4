using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

// laneOPT5 (2026-11-06): CPU-side frame-cost switches, ALL DARK. Data: perf_cpu.json (StreamingAssets in the player, Assets/Data in the editor). Missing file or enabled=false = this file does nothing and no object is created.
// None of these touches the sim: they change how the frame is skinned / scheduled / paced, never a sim input. (vsync0 changes frame cadence only: perf-only runs, never the simhash / balance gates; see MANIFEST.)
//   skin4     QualitySettings.skinWeights Unlimited(255) -> FourBones. Look-identical when no skinned mesh has more than maxInfluences (4) influences per vertex: the Joan and Pitborn GLBs carry JOINTS_0/WEIGHTS_0 only.
//             Guarded: every SkinnedMeshRenderer mesh is checked first (Mesh.GetBonesPerVertex); an unknown mesh or one above the limit (an 8-influence MetaHuman head) refuses, and a later one reverts.
//   jobWorkers  JobsUtility.JobWorkerCount (0 = leave Unity's default, which is processorCount - 1).
//   vsync0    QualitySettings.vSyncCount 0 + targetFrameRate -1, re-applied every recheck. PERF READING ONLY.
//   stats     one log line at statsFirstSec and then every statsEverySec: frames, avg / max ms, gen0 GCs, managed bytes per frame, vsync, target fps, refresh, skinWeights, job workers.
// Command line (same exe, same data, for A/B arms): -cpuopt:skin4,jobs=12,vsync0,stats   (only the listed items are ON, whatever the json says)   -cpuopt:none   (everything off)
[Serializable]
public class PerfCpuCfg
{
    public bool enabled = false;
    public bool skin4 = false;
    public int maxInfluences = 4;
    public int jobWorkers = 0;
    public bool vsync0 = false;
    public bool stats = false;
    public float statsFirstSec = 5f;
    public float statsEverySec = 30f;
    public float recheckSec = 1f;
    public int applyAfterFrames = 5;
    public string note;
}

public static class PerfCpu
{
    static PerfCpuCfg cfg;
    public static PerfCpuCfg C { get { if (cfg == null) Load(); return cfg; } }
    static bool fromCli;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; fromCli = false; seen.Clear(); }

    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return a.Substring(p.Length); return null; }

    static void Load()
    {
        cfg = new PerfCpuCfg();
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "perf_cpu.json"), Path.Combine(Application.dataPath, "Data/perf_cpu.json") })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var r = JsonUtility.FromJson<PerfCpuCfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF));
                if (r != null) cfg = r;
            }
            catch (Exception e) { Debug.LogWarning("PerfCpu: perf_cpu.json unreadable: " + e.Message); }
            break;
        }
        string cli = Arg("-cpuopt:");
        if (cli != null)
        {
            fromCli = true;
            cfg.skin4 = false; cfg.vsync0 = false; cfg.stats = false; cfg.jobWorkers = 0; cfg.enabled = false;
            foreach (var tok in cli.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = tok.Trim().ToLowerInvariant();
                if (t == "none") continue;
                cfg.enabled = true;
                if (t == "skin4") cfg.skin4 = true;
                else if (t == "vsync0") cfg.vsync0 = true;
                else if (t == "stats") cfg.stats = true;
                else if (t.StartsWith("jobs=", StringComparison.Ordinal)) { int n; if (int.TryParse(t.Substring(5), out n)) cfg.jobWorkers = n; }
                else Debug.LogWarning("PerfCpu: unknown -cpuopt token '" + tok + "'");
            }
        }
        if (cfg.maxInfluences < 1) cfg.maxInfluences = 1;
        if (cfg.recheckSec < 0.2f) cfg.recheckSec = 0.2f;
        if (cfg.applyAfterFrames < 1) cfg.applyAfterFrames = 1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = C;
        if (!c.enabled || !(c.skin4 || c.vsync0 || c.stats || c.jobWorkers > 0)) return;   // dark: no object at all
        if (UnityEngine.Object.FindFirstObjectByType<PerfCpuDriver>() != null) return;
        var go = new GameObject("PerfCpu"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<PerfCpuDriver>();
        Debug.Log("[PerfCpu] on: " + (c.skin4 ? "skin4 " : "") + (c.jobWorkers > 0 ? "jobs=" + c.jobWorkers + " " : "") + (c.vsync0 ? "vsync0 " : "") + (c.stats ? "stats " : "") + (fromCli ? "(command line)" : "(json)"));
    }

    // ---- skin weights guard -------------------------------------------------------------------------------------------------------------------
    static readonly Dictionary<Mesh, int> seen = new Dictionary<Mesh, int>();   // mesh -> max influences per vertex (-1 = unknown)

    /// <summary>Max bone influences per vertex over every SkinnedMeshRenderer in the scene; -1 when a mesh cannot be read (treated as unsafe).</summary>
    public static int MaxInfluences(out int meshes)
    {
        meshes = 0; int worst = 0;
        var smrs = UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < smrs.Length; i++)
        {
            var m = smrs[i] != null ? smrs[i].sharedMesh : null; if (m == null) continue;
            int w;
            if (!seen.TryGetValue(m, out w)) { w = MeshMax(m); seen[m] = w; }
            meshes++;
            if (w < 0) return -1;
            if (w > worst) worst = w;
        }
        return worst;
    }

    static int MeshMax(Mesh m)
    {
        try
        {
            var bpv = m.GetBonesPerVertex();
            if (bpv.IsCreated && bpv.Length > 0) { int mx = 0; for (int i = 0; i < bpv.Length; i++) if (bpv[i] > mx) mx = bpv[i]; return mx; }
            return m.bindposeCount > 0 ? 4 : 0;   // no variable-weight table: the legacy layout (BoneWeight, 4 per vertex), which cannot exceed 4; no bind poses = no skin at all
        }
        catch (Exception) { return -1; }
    }
}

public class PerfCpuDriver : MonoBehaviour
{
    PerfCpuCfg c; int frame; float nextCheck, nextStats; bool skinOn, skinRefused, jobsDone;
    SkinWeights prevSkin; int lastSmr = -1;
    double sumMs, maxMs; int n; int gc0; long mem0;

    void Awake() { c = PerfCpu.C; nextStats = Time.unscaledTime + Mathf.Max(0.5f, c.statsFirstSec); gc0 = GC.CollectionCount(0); mem0 = GC.GetTotalMemory(false); }

    void Update()
    {
        frame++;
        float now = Time.unscaledTime;
        if (c.stats) { double ms = Time.unscaledDeltaTime * 1000.0; sumMs += ms; if (ms > maxMs) maxMs = ms; n++; }
        if (frame < c.applyAfterFrames) return;
        if (!jobsDone) { jobsDone = true; if (c.jobWorkers > 0) { int want = Mathf.Clamp(c.jobWorkers, 1, JobsUtility.JobWorkerMaximumCount); JobsUtility.JobWorkerCount = want; Debug.Log("[PerfCpu] JobWorkerCount -> " + JobsUtility.JobWorkerCount + " (max " + JobsUtility.JobWorkerMaximumCount + ", processors " + SystemInfo.processorCount + ")"); } }
        if (now >= nextCheck)
        {
            nextCheck = now + c.recheckSec;
            if (c.vsync0 && (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != -1)) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; }
            if (c.skin4 && !skinRefused) CheckSkin();
        }
        if (c.stats && now >= nextStats)
        {
            nextStats = now + Mathf.Max(1f, c.statsEverySec);
            if (n > 0)
            {
                int gc = GC.CollectionCount(0); long mem = GC.GetTotalMemory(false);
                double avg = sumMs / n;
                Debug.Log("[PerfCpu] stats frames " + n + " avg " + avg.ToString("F2") + " ms (" + (1000.0 / avg).ToString("F1") + " fps) max " + maxMs.ToString("F1") + " ms, gen0 GCs " + (gc - gc0)
                    + ", managed " + (mem / 1048576.0).ToString("F0") + " MB (" + ((mem - mem0) / (double)n).ToString("F0") + " B/frame net), vSync " + QualitySettings.vSyncCount + " targetFps " + Application.targetFrameRate
                    + " refresh " + Screen.currentResolution.refreshRateRatio.value.ToString("F1") + " Hz, " + Screen.width + "x" + Screen.height + ", skinWeights " + QualitySettings.skinWeights + ", jobWorkers " + JobsUtility.JobWorkerCount);
                sumMs = 0; maxMs = 0; n = 0; gc0 = gc; mem0 = mem;
            }
        }
    }

    void CheckSkin()
    {
        int meshes; int mx = PerfCpu.MaxInfluences(out meshes);
        bool ok = mx >= 0 && mx <= c.maxInfluences && (meshes > 0 || skinOn);   // never switch on before the fighters exist (zero meshes proves nothing)
        if (ok && !skinOn)
        {
            prevSkin = QualitySettings.skinWeights;
            if (prevSkin == SkinWeights.FourBones || prevSkin == SkinWeights.TwoBones || prevSkin == SkinWeights.OneBone) { skinRefused = true; Debug.Log("[PerfCpu] skin4: skinWeights already " + prevSkin + ", nothing to do"); return; }
            QualitySettings.skinWeights = SkinWeights.FourBones; skinOn = true;
            Debug.Log("[PerfCpu] skin4 ON: skinWeights " + prevSkin + " -> FourBones (" + meshes + " skinned meshes, max influences per vertex " + mx + ")");
        }
        else if (ok && skinOn)
        {
            if (QualitySettings.skinWeights != SkinWeights.FourBones) { QualitySettings.skinWeights = SkinWeights.FourBones; Debug.Log("[PerfCpu] skin4 re-applied (a quality-level switch reset it)"); }   // QualityTiers calls SetQualityLevel on a tier change
        }
        else if (!ok && skinOn)
        {
            QualitySettings.skinWeights = prevSkin; skinOn = false; skinRefused = true;
            Debug.LogWarning("[PerfCpu] skin4 REVERTED to " + prevSkin + ": a skinned mesh now has max influences " + mx + " (limit " + c.maxInfluences + ")");
        }
        else if (!ok && !skinOn)
        {
            if (lastSmr != meshes) { lastSmr = meshes; Debug.Log("[PerfCpu] skin4 waiting: max influences " + mx + " over " + meshes + " meshes (limit " + c.maxInfluences + (mx < 0 ? ", a mesh is unreadable" : "") + ")"); }
        }
    }
}
