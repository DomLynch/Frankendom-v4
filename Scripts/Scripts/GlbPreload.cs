using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

// lanePERF3 v100 C3: load the runtime-loaded GLBs (gear items: gear.json items[].glb, plus perf_fixes.json C3.files) with glTFast DURING THE INTRO, one file every C3.framesBetweenFiles frames,
// and keep the parsed GltfImport so GearAssetLoader only has to instantiate it (no file read / parse / texture decode / upload hitch when an item is first equipped or dropped).
// Consumer: GearAssetLoader.LoadGlb asks GlbPreload.Take(file) first (one-line hook, see patches\GearAssetLoader_C3.py); a miss or a failed preload = the old load path, nothing else changes.
// Not covered on purpose: the Joan / Dracula / Pitborn body models are imported ASSETS inside the scene (Assets\Characters\*\*_combat.glb), they are loaded with the scene, not by code, so they cannot be
// preloaded from here (the 17 s first frame of v76 is the scene load; overlapping it needs a splash scene that loads the arena additively, which is a boot/scene change, not a data fix).
// Switch: -perffix:C3 / -noperffix:C3 / perf_fixes.json C3.enabled. Memory: parsed imports stay resident until Take()n; C3.maxTotalMb caps what is preloaded.
public static class GlbPreload
{
    [Serializable] class GearFile { public GearItem[] items = new GearItem[0]; }
    [Serializable] class GearItem { public string glb = ""; }

    static readonly Dictionary<string, Task<GLTFast.GltfImport>> cache = new Dictionary<string, Task<GLTFast.GltfImport>>(StringComparer.OrdinalIgnoreCase);
    public static int Started, Done, Failed, Taken; public static float Seconds;   // for the log line / perfbench

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cache.Clear(); Started = Done = Failed = Taken = 0; Seconds = 0f; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!PerfFixes.On("C3")) return;
        if (PerfFlags.HarnessRun() && !PerfFixes.C.C3.inHarness) return;
        var go = new GameObject("GlbPreload"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<GlbPreloadRunner>();
    }

    /// <summary>The preloaded import for this file (removed from the cache: the caller owns and disposes it), or null when nothing was preloaded for it. The task may still be running: await it.</summary>
    public static Task<GLTFast.GltfImport> Take(string file)
    {
        if (string.IsNullOrEmpty(file) || cache.Count == 0) return null;
        string key = Key(file); Task<GLTFast.GltfImport> t;
        if (!cache.TryGetValue(key, out t)) return null;
        cache.Remove(key); Taken++; return t;
    }

    static string Key(string file) { try { return Path.GetFullPath(file); } catch (Exception) { return file; } }

    /// <summary>Same lookup order as GearAssetLoader.FindFile: StreamingAssets\Gear\&lt;name&gt;, StreamingAssets\&lt;glb&gt;, &lt;project&gt;\&lt;glb&gt;, absolute.</summary>
    public static string FindFile(string glb)
    {
        if (string.IsNullOrEmpty(glb)) return null;
        try
        {
            string name = Path.GetFileName(glb);
            string[] c = { Path.Combine(Application.streamingAssetsPath, "Gear/" + name), Path.Combine(Application.streamingAssetsPath, glb), Path.GetFullPath(Path.Combine(Application.dataPath, "../" + glb)), glb };
            for (int i = 0; i < c.Length; i++) if (Path.IsPathRooted(c[i]) && File.Exists(c[i])) return c[i];
        }
        catch (Exception) { }
        return null;
    }

    internal static List<string> Wanted()
    {
        var c = PerfFixes.C.C3; var list = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Action<string> add = g => { string f = FindFile(g); if (f != null && seen.Add(Key(f))) list.Add(f); };
        foreach (var f in c.files) add(f);
        if (c.fromGearJson)
        {
            var txt = HudCfg.ReadData("gear.json");
            if (txt != null) { try { var g = JsonUtility.FromJson<GearFile>(txt); if (g != null && g.items != null) foreach (var it in g.items) if (it != null && !string.IsNullOrEmpty(it.glb)) add(it.glb); } catch (Exception e) { Debug.LogWarning("[GlbPreload] gear.json: " + e.Message); } }
        }
        return list;
    }

    internal static async Task<GLTFast.GltfImport> LoadAsync(string file)
    {
        GLTFast.GltfImport g = null;
        try
        {
            g = new GLTFast.GltfImport();
            bool ok = await g.LoadFile(file);
            if (!ok) { g.Dispose(); Failed++; return null; }
            Done++; return g;
        }
        catch (Exception e) { Debug.LogWarning("[GlbPreload] " + file + ": " + e.Message); try { if (g != null) g.Dispose(); } catch (Exception) { } Failed++; return null; }
    }

    internal static void Add(string file, Task<GLTFast.GltfImport> t) { cache[Key(file)] = t; Started++; }
}

public class GlbPreloadRunner : MonoBehaviour
{
    IEnumerator Start()
    {
        var c = PerfFixes.C.C3; float t0 = Time.realtimeSinceStartup;
        for (int i = 0; i < Mathf.Max(0, c.startDelayFrames); i++) yield return null;
        var files = GlbPreload.Wanted(); long budget = (long)(Mathf.Max(1f, c.maxTotalMb) * 1048576f), used = 0; var pending = new List<Task<GLTFast.GltfImport>>();
        foreach (var f in files)
        {
            long len = 0; try { len = new FileInfo(f).Length; } catch (Exception) { }
            if (used + len > budget) { Debug.Log("[GlbPreload] skipped (budget " + c.maxTotalMb + " MB): " + f); continue; }
            used += len;
            var t = GlbPreload.LoadAsync(f); GlbPreload.Add(f, t); pending.Add(t);
            for (int i = 0; i < Mathf.Max(1, c.framesBetweenFiles); i++) yield return null;
        }
        foreach (var t in pending) while (!t.IsCompleted) yield return null;
        GlbPreload.Seconds = Time.realtimeSinceStartup - t0;
        Debug.Log("[GlbPreload] files " + files.Count + ", started " + GlbPreload.Started + ", loaded " + GlbPreload.Done + ", failed " + GlbPreload.Failed + ", " + (used / 1048576f).ToString("0.0") + " MB, " + GlbPreload.Seconds.ToString("0.0") + " s after scene load");
        Destroy(gameObject);
    }
}
