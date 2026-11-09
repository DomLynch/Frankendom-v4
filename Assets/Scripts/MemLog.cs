using System;
using UnityEngine;
using UnityEngine.Profiling;

// MEMLOG v1 (unity-build, laneMEM step 0): read-only memory log, no behaviour change. One line at +20 s, +60 s and +180 s after the scene loads:
// "[MemLog] t=60s texNow=x.xx GB texNonStreaming=x.xx GB reserved=x.xx GB allocated=x.xx GB mono=x.xx GB workingSet=x.xx GB private=x.xx GB".
// Never runs in batchmode / harness-free headless runs (no player). Safe: every call is in try/catch and only reads counters.
public class MemLog : MonoBehaviour
{
    static MemLog inst;
    static readonly float[] marks = { 20f, 60f, 180f };   // hc:ok seconds after load for the three log lines
    int next;
    float t0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || Application.isBatchMode) return;
        var go = new GameObject("MemLog"); DontDestroyOnLoad(go); inst = go.AddComponent<MemLog>();
    }

    void Start() { t0 = Time.realtimeSinceStartup; }

    static string Gb(long bytes) { return (bytes / 1073741824.0).ToString("F2"); }   // hc:ok bytes per GiB

    void Update()
    {
        if (next >= marks.Length) { enabled = false; return; }
        if (Time.realtimeSinceStartup - t0 < marks[next]) return;
        float mark = marks[next++];
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            Debug.Log("[MemLog] t=" + mark.ToString("F0") + "s texNow=" + Gb((long)Texture.currentTextureMemory) + " GB texNonStreaming=" + Gb((long)Texture.nonStreamingTextureMemory)
                + " GB reserved=" + Gb(Profiler.GetTotalReservedMemoryLong()) + " GB allocated=" + Gb(Profiler.GetTotalAllocatedMemoryLong())
                + " GB mono=" + Gb(Profiler.GetMonoUsedSizeLong()) + " GB gc=" + Gb(GC.GetTotalMemory(false)) + " GB workingSet=" + Gb(p.WorkingSet64) + " GB private=" + Gb(p.PrivateMemorySize64) + " GB");
        }
        catch (Exception e) { Debug.LogWarning("[MemLog] failed: " + e.Message); }
    }
}
