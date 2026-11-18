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
    static readonly HashSet<string> everStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // PRELOADHITCH v1: every key a parse was started for this session (Take removes it from cache, this stays)
    static readonly Dictionary<string, SmoothAgent> agents = new Dictionary<string, SmoothAgent>(StringComparer.OrdinalIgnoreCase);
    public static int Claimed, SkippedDup, DeferFrames;   // PRELOADHITCH v1: for the log line
    public static int Started, Done, Failed, Taken; public static float Seconds;   // for the log line / perfbench
    public static bool Expected, Tier0Done;   // v3: StartupWarm holds its cover until Joan's gear tier is parsed (Expected = a preload runner was started)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cache.Clear(); everStarted.Clear(); agents.Clear(); Claimed = SkippedDup = DeferFrames = 0; Started = Done = Failed = Taken = 0; Seconds = 0f; Expected = Tier0Done = false; if (deferGo != null) UnityEngine.Object.Destroy(deferGo); deferGo = null; deferAgent = null; }

    // K30: ONE DontDestroyOnLoad defer agent for every glTFast import of the gear path (GearAssetLoader, this class), so no import depends on glTFast's global default agent. Recreated if its object was destroyed.
    static GameObject deferGo; static GLTFast.TimeBudgetPerFrameDeferAgent deferAgent;
    /// <summary>Behind the start cover nothing is playing, so tier 0 parses with a big per-frame budget (perf_fixes C3.tier0BudgetMs); RestoreBudget puts the gentle in-play budget (gear.json deferFrameBudget) back.</summary>
    public static void BoostBudget(float ms) { var _ = DeferAgent; if (deferAgent != null) deferAgent.SetFrameBudget(Mathf.Clamp(ms, 0.01f, 50f)); }
    public static void RestoreBudget() { float b = 0.5f; try { b = GearTable.Get().deferFrameBudget; } catch (Exception) { } if (deferAgent != null) deferAgent.SetFrameBudget(Mathf.Clamp(b, 0.01f, 5f)); }
    public static GLTFast.IDeferAgent DeferAgent
    {
        get
        {
            if (deferAgent == null)   // Unity null check: also true when the object was destroyed
            {
                deferGo = new GameObject("GlbDeferAgent"); UnityEngine.Object.DontDestroyOnLoad(deferGo);
                deferAgent = deferGo.AddComponent<GLTFast.TimeBudgetPerFrameDeferAgent>();
                float b = 0.5f; try { b = GearTable.Get().deferFrameBudget; } catch (Exception) { }
                deferAgent.SetFrameBudget(Mathf.Clamp(b, 0.01f, 5f));
            }
            return deferAgent;
        }
    }

    /// <summary>K30: parse this file now if nothing is cached for it yet (the loading card preloads the player's loadout with it). Returns true when a parse was started.</summary>
    public static bool Ensure(string file)
    {
        if (string.IsNullOrEmpty(file)) return false;
        string key = Key(file); if (cache.ContainsKey(key)) return false;
        Add(file, LoadAsync(file)); return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!PerfFixes.On("C3")) return;
        if (PerfFlags.HarnessRun() && !PerfFixes.C.C3.inHarness) return;
        Expected = true;
        var go = new GameObject("GlbPreload"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<GlbPreloadRunner>();
    }

    /// <summary>The preloaded import for this file (removed from the cache: the caller owns and disposes it), or null when nothing was preloaded for it. The task may still be running: await it.</summary>
    public static Task<GLTFast.GltfImport> Take(string file, bool urgent = true)
    {
        if (string.IsNullOrEmpty(file) || cache.Count == 0) return null;
        string key = Key(file); Task<GLTFast.GltfImport> t;
        if (!cache.TryGetValue(key, out t)) return null;
        cache.Remove(key); Taken++;
        SmoothAgent a; if (urgent && agents.TryGetValue(key, out a)) a.Urgent = true;   // PRELOADHITCH v1: a consumer that needs it now (equip) ends the calm gating; the hidden hub load passes urgent false
        return t;
    }

    static string Key(string file) { try { return Path.GetFullPath(file); } catch (Exception) { return file; } }

    // ---------------------------------------------------------------- PRELOADHITCH v1 (perf_fixes.json C3.lateSmooth; false = everything below is dormant)
    public static bool Smooth { get { try { return PerfFixes.On("C3") && PerfFixes.C.C3.lateSmooth; } catch (Exception) { return false; } } }
    public static bool WasStarted(string file) { return !string.IsNullOrEmpty(file) && everStarted.Contains(Key(file)); }

    /// <summary>glTFast defer agent that keeps the preload from landing as consecutive 100-300 ms frames: real-ms budget per frame (one non-sliceable step, e.g. one texture decode, is all that fits), then
    /// nothing for lateSpacingFrames after a frame that was longer than lateHeavyMs (our step or someone else's hitch), nothing while the player fights (lateHoldWhileEngaged), each wait capped by lateMaxWaitS.
    /// A plain C# class: no serialized state.</summary>
    internal sealed class SmoothAgent : GLTFast.IDeferAgent
    {
        public bool Urgent; readonly float budgetS, heavyS, maxWaitS; readonly int spacing; readonly bool holdEngaged;
        int frame = -1, lastHeavyFrame = -100000; double frameStart, waitSince = -1;
        public SmoothAgent()
        {
            var c = PerfFixes.C.C3;
            budgetS = Mathf.Max(0.0001f, c.lateBudgetMs * 0.001f); heavyS = Mathf.Max(0.001f, c.lateHeavyMs * 0.001f); spacing = Mathf.Max(0, c.lateSpacingFrames);
            holdEngaged = c.lateHoldWhileEngaged; maxWaitS = Mathf.Max(0f, c.lateMaxWaitS);
        }
        static bool Engaged() { try { var p = GameLoop.Player; return p != null && p.opponent != null && !p.opponent.Dead; } catch (Exception) { return false; } }
        bool Fits(float duration)
        {
            int f = Time.frameCount; double now = Time.realtimeSinceStartupAsDouble;
            if (f != frame) { frame = f; frameStart = now; if (Time.unscaledDeltaTime > heavyS) lastHeavyFrame = f - 1; }
            if (Urgent) return duration <= budgetS - (now - frameStart);
            bool hold = f - lastHeavyFrame < spacing || (holdEngaged && Engaged());
            if (hold)
            {
                if (waitSince < 0) waitSince = now;
                if (now - waitSince < maxWaitS) { DeferFrames++; return false; }   // wait, up to the cap
            }
            bool fits = duration <= budgetS - (now - frameStart);
            if (fits) waitSince = -1;
            return fits;
        }
        public bool ShouldDefer() { return !Fits(0f); }
        public bool ShouldDefer(float duration) { return !Fits(duration); }
        public async Task BreakPoint() { if (ShouldDefer()) await Task.Yield(); }
        public async Task BreakPoint(float duration) { if (ShouldDefer(duration)) await Task.Yield(); }
    }

    /// <summary>The hub NPC loader asks for a file the late tier has not reached yet: parse it now through the smooth agent and register it, so the late tier skips it and HubNpcs takes this very import (one parse, not two).</summary>
    public static bool Claim(string file)
    {
        if (!Smooth || string.IsNullOrEmpty(file)) return false;
        string key = Key(file); if (everStarted.Contains(key) || cache.ContainsKey(key)) return false;
        Claimed++; Add(file, LoadAsync(file, true)); Debug.Log("[GlbPreload] claimed for a consumer before the late tier: " + Path.GetFileName(file)); return true;
    }
    /// <summary>Smooth agent for a consumer's own (non-preloaded) import, or null (= the glTFast default agent, the old behaviour) when the mode is off.</summary>
    public static GLTFast.IDeferAgent ConsumerAgent() { return Smooth ? new SmoothAgent() : null; }

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

    /// <summary>TIER 0 (v2): Joan's worn gear (C3.eagerFiles), parsed first and fast. Empty when the config has no eagerFiles (old single-tier behaviour).</summary>
    internal static List<string> WantedEager()
    {
        var c = PerfFixes.C.C3; var list = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (c.eagerFiles != null) foreach (var g in c.eagerFiles) { string f = FindFile(g); if (f != null && seen.Add(Key(f))) list.Add(f); }
        return list;
    }

    internal static List<string> Wanted()
    {
        var c = PerfFixes.C.C3; var list = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (c.eagerFiles != null) foreach (var g in c.eagerFiles) { string f = FindFile(g); if (f != null) seen.Add(Key(f)); }   // tier 0 files are not listed again
        Action<string> add = g => { string f = FindFile(g); if (f != null && seen.Add(Key(f))) list.Add(f); };
        foreach (var f in c.files) add(f);
        foreach (var f in MonsterLook.PreloadFiles()) add(f);   // MONSTERLOOK v1: the look glbs of every enabled monster row are parsed during the intro too
        if (c.fromGearJson)
        {
            var txt = HudCfg.ReadData("gear.json");
            if (txt != null) { try { var g = JsonUtility.FromJson<GearFile>(txt); if (g != null && g.items != null) foreach (var it in g.items) if (it != null && !string.IsNullOrEmpty(it.glb)) add(it.glb); } catch (Exception e) { Debug.LogWarning("[GlbPreload] gear.json: " + e.Message); } }
        }
        return list;
    }

    internal static async Task<GLTFast.GltfImport> LoadAsync(string file, bool smooth = false)
    {
        GLTFast.GltfImport g = null;
        try
        {
            GLTFast.IDeferAgent da = DeferAgent;
            if (smooth) { var sa = new SmoothAgent(); agents[Key(file)] = sa; da = sa; }   // PRELOADHITCH v1
            g = new GLTFast.GltfImport(deferAgent: da);   // K30
            bool ok = await g.LoadFile(file);
            if (!ok) { g.Dispose(); Failed++; return null; }
            Done++; return g;
        }
        catch (Exception e) { Debug.LogWarning("[GlbPreload] " + file + ": " + e.ToString() /* K30: full stack */); try { if (g != null) g.Dispose(); } catch (Exception) { } Failed++; return null; }
    }

    internal static void Add(string file, Task<GLTFast.GltfImport> t) { cache[Key(file)] = t; everStarted.Add(Key(file)); Started++; }
}

public class GlbPreloadRunner : MonoBehaviour
{
    IEnumerator Start()
    {
        var c = PerfFixes.C.C3; float t0 = Time.realtimeSinceStartup;
        long budget = (long)(Mathf.Max(1f, c.maxTotalMb) * 1048576f), used = 0; var pending = new List<Task<GLTFast.GltfImport>>();
        var eager = GlbPreload.WantedEager();
        if (eager.Count > 0)
        {   // TIER 0: Joan's worn gear first, a couple of frames apart, so the armour is on within seconds of the first frame
            if (c.tier0BudgetMs > 0f) GlbPreload.BoostBudget(c.tier0BudgetMs);
            for (int i = 0; i < Mathf.Max(0, c.eagerStartFrames); i++) yield return null;
            float te = Time.realtimeSinceStartup;
            foreach (var f in eager)
            {
                long len = 0; try { len = new FileInfo(f).Length; } catch (Exception) { }
                if (used + len > budget) { Debug.Log("[GlbPreload] skipped (budget " + c.maxTotalMb + " MB): " + f); continue; }
                used += len;
                var t = GlbPreload.LoadAsync(f); GlbPreload.Add(f, t); pending.Add(t);
                for (int i = 0; i < Mathf.Max(1, c.eagerFramesBetween); i++) yield return null;
            }
            foreach (var t in pending) while (!t.IsCompleted) yield return null;
            GlbPreload.Tier0Done = true; if (c.tier0BudgetMs > 0f) GlbPreload.RestoreBudget();
            Debug.Log("[GlbPreload] tier0 (Joan gear) " + eager.Count + " files, " + (used / 1048576f).ToString("0.0") + " MB, " + (Time.realtimeSinceStartup - te).ToString("0.0") + " s");
            for (int i = 0; i < Mathf.Max(0, c.lateDelayFrames); i++) yield return null;
        }
        else for (int i = 0; i < Mathf.Max(0, c.startDelayFrames); i++) yield return null;
        GlbPreload.Tier0Done = true;   // (also when there is no tier 0)
        var files = GlbPreload.Wanted(); int gap = eager.Count > 0 ? Mathf.Max(1, c.lateFramesBetween) : Mathf.Max(1, c.framesBetweenFiles);
        bool smooth = eager.Count > 0 && GlbPreload.Smooth;   // PRELOADHITCH v1 (late tier only; a single-tier config keeps the old path)
        foreach (var f in files)
        {
            if (smooth && GlbPreload.WasStarted(f)) { GlbPreload.SkippedDup++; continue; }   // a consumer (hub NPCs) already parsed / claimed this file: no second parse
            long len = 0; try { len = new FileInfo(f).Length; } catch (Exception) { }
            if (used + len > budget) { Debug.Log("[GlbPreload] skipped (budget " + c.maxTotalMb + " MB): " + f); continue; }
            used += len;
            var t = GlbPreload.LoadAsync(f, smooth); GlbPreload.Add(f, t); pending.Add(t);
            if (smooth) while (!t.IsCompleted) yield return null;   // one file at a time: its heavy steps never overlap another file's
            for (int i = 0; i < gap; i++) yield return null;
        }
        foreach (var t in pending) while (!t.IsCompleted) yield return null;
        GlbPreload.Seconds = Time.realtimeSinceStartup - t0;
        Debug.Log("[GlbPreload] files " + files.Count + ", started " + GlbPreload.Started + ", loaded " + GlbPreload.Done + ", failed " + GlbPreload.Failed + ", " + (used / 1048576f).ToString("0.0") + " MB, " + GlbPreload.Seconds.ToString("0.0") + " s after scene load" + (smooth ? ", smooth: claimed " + GlbPreload.Claimed + ", skipped duplicates " + GlbPreload.SkippedDup + ", defer polls " + GlbPreload.DeferFrames : ""));
        Destroy(gameObject);
    }
}
