using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Debug = UnityEngine.Debug;

// ZONEHITCH v1 (laneZONEHITCH): the door-1 crossing hitch (v107 -zonexing: worst frame 808 ms outbound, 172 ms inbound). Three parts, all DARK unless zones run (ZoneSpan binds) and all behind zone_hitch.json:
//  1. TIMING (log.enabled): a one-frame stopwatch around every zone step (span bind / poll / pace / skyline, ring on/off, SpanWarm, Sobek spawn: template load, Instantiate, attachers, activate, chase). A step over log.stepMs is logged
//     as "[ZoneHitch] step <name> <ms> ms" and sent to ZoneXing as event "ms:<name>=<ms>" (zonexing csv + json name it). A frame over log.frameMs is logged as "[ZoneHitch] FRAME" with the zone-code ms, the ms OUTSIDE zone
//     code (render / physics / other systems), the player z, GC counts, texture streaming load counts and managed memory: so the next run says whether the stall is a ring, the spawn, or not zone code at all.
//  2. SPREAD (spread.rowsPerFrame > 0): a ring group wakes / retires as batches of rows per frame (parent first, then children), not as one SetActive of 40-110 rows. The rings that start inactive get their rows
//     switched off once under the inactive parent (no callbacks run). 0 = the old whole-ring toggle.
//  3. WARM (warm.enabled): once the span is bound, behind an opaque cover: Resources.Load the creature prefabs of every zone row (no asset load inside the wake frame), draw every creature material once, render each
//     inactive ring once with a temporary small camera (shader variants, mesh and texture upload), touch the skyline group, then prep the rings for spread. Nothing here touches the sim, Duel, AIBrain or any tick.
// Switches: -zonehitch:off (all three off = the v107 behaviour, for the paired proof), -zonehitch:log (timing only). zones off = nothing in this file runs (it is only started by ZoneSpan.Bind). UNPROVEN IN UNITY.
[Serializable] public class ZhLog { public bool enabled = true, toXing = true; public float stepMs = 6f, frameMs = 120f; public int maxLines = 300; }
[Serializable] public class ZhSpread { public int rowsPerFrame = 16; }
[Serializable] public class ZhWarm
{
    public bool enabled = true, prefabs = true, materials = true, rings = true, skyline = true, cover = true;
    public float startDelaySeconds = 0.25f, camHeightM = 4f, camBackM = 16f, camFov = 60f, camFar = 400f; public int framesBetween = 1, rtWidth = 320, rtHeight = 200, maxFrames = 600;
}
[Serializable] public class ZhCfg { public bool enabled = true; public ZhLog log = new ZhLog(); public ZhSpread spread = new ZhSpread(); public ZhWarm warm = new ZhWarm(); }

public static class ZoneHitch
{
    static ZhCfg c; static bool logOnly, forcedOff; static int lines, frameNo = -1, gc0, gc1, gc2;
    static double curMs, lastMs; static readonly List<string> curSteps = new List<string>(), lastSteps = new List<string>();
    public static event Action<string> OnNote;   // ZoneXing marker column
    public static bool Warming;                   // true from Bound until the warm is over: ZoneXing waits for it
    public static float PlayerZ = float.NaN;     // set by the span each poll, only for the FRAME line

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { c = null; lines = 0; frameNo = -1; curMs = lastMs = 0; curSteps.Clear(); lastSteps.Clear(); OnNote = null; Warming = false; rs.Clear(); logOnly = false; forcedOff = false; }

    public static ZhCfg C
    {
        get
        {
            if (c != null) return c; c = new ZhCfg();
            try { var t = HudCfg.ReadData("zone_hitch.json"); if (t != null) { var k = JsonUtility.FromJson<ZhCfg>(t); if (k != null) c = k; } } catch (Exception e) { Debug.LogWarning("[ZoneHitch] zone_hitch.json unreadable, defaults: " + e.Message); }
            if (c.log == null) c.log = new ZhLog(); if (c.spread == null) c.spread = new ZhSpread(); if (c.warm == null) c.warm = new ZhWarm();
            try { foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-zonehitch:off") forcedOff = true; else if (a == "-zonehitch:log") logOnly = true; } } catch (Exception) { }
            return c;
        }
    }
    public static bool On { get { return C.enabled && !forcedOff; } }
    public static bool Timing { get { return On && c.log.enabled; } }
    public static int Rows { get { return On && !logOnly ? Mathf.Max(0, c.spread.rowsPerFrame) : 0; } }
    public static bool WarmOn { get { return On && !logOnly && c.warm.enabled; } }

    // ------------------------------------------------------------------ timing
    public static long Mark() { return Timing ? Stopwatch.GetTimestamp() : 0L; }
    /// <summary>Records the ms since t (this frame's zone-code total + step list), logs it when it is over log.stepMs, and restarts t.</summary>
    public static void Lap(string name, ref long t)
    {
        if (t == 0L) return; long now = Stopwatch.GetTimestamp(); double ms = (now - t) * 1000.0 / Stopwatch.Frequency; t = now;
        if (ms < 0.05) return; curMs += ms;
        if (ms >= c.log.stepMs)
        {
            curSteps.Add(name + "=" + ms.ToString("F1", CultureInfo.InvariantCulture));
            if (lines < c.log.maxLines) { lines++; Debug.Log("[ZoneHitch] step " + name + " " + ms.ToString("F1", CultureInfo.InvariantCulture) + " ms (frame " + Time.frameCount + ", z " + PlayerZ.ToString("F1", CultureInfo.InvariantCulture) + ")"); }
            if (c.log.toXing && OnNote != null) OnNote("ms:" + name + "=" + ms.ToString("F0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Called once per frame (ZoneHitchRunner.Update, early): dt is the frame that just ended, its zone-code steps are in cur*, which is swapped to last*.</summary>
    internal static void FrameTick(float dtUnscaled)
    {
        lastMs = curMs; curMs = 0; lastSteps.Clear(); lastSteps.AddRange(curSteps); curSteps.Clear();
        if (!Timing) return; double dt = dtUnscaled * 1000.0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        if (dt >= c.log.frameMs && lines < c.log.maxLines)
        {
            lines++; var sb = new StringBuilder("[ZoneHitch] FRAME ").Append(Time.frameCount - 1).Append(" dt ").Append(dt.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms: zone code ").Append(lastMs.ToString("F1", CultureInfo.InvariantCulture))
              .Append(" ms, OUTSIDE zone code ").Append((dt - lastMs).ToString("F0", CultureInfo.InvariantCulture)).Append(" ms; z ").Append(PlayerZ.ToString("F1", CultureInfo.InvariantCulture)).Append("; steps [").Append(string.Join(" ", lastSteps.ToArray()))
              .Append("]; gc +").Append(g0 - gc0).Append('/').Append(g1 - gc1).Append('/').Append(g2 - gc2).Append("; tex streaming loading ").Append(Texture.streamingTextureLoadingCount).Append(" pending ").Append(Texture.streamingTexturePendingLoadCount)
              .Append("; mono MB ").Append((UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() >> 20)).Append(" total MB ").Append((UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20));
            Debug.Log(sb.ToString());
            if (c.log.toXing && OnNote != null) OnNote("frame:" + dt.ToString("F0", CultureInfo.InvariantCulture) + "ms/zone" + lastMs.ToString("F0", CultureInfo.InvariantCulture));
        }
        gc0 = g0; gc1 = g1; gc2 = g2;
    }

    // ------------------------------------------------------------------ spread: a ring group as batches of rows
    sealed class RS { public bool prepped, full; public int mode, cur; }   // mode 0 idle, 1 turning on, 2 turning off
    static readonly Dictionary<Transform, RS> rs = new Dictionary<Transform, RS>();
    static RS State(Transform u) { RS s; if (!rs.TryGetValue(u, out s)) { s = new RS(); rs[u] = s; } return s; }

    /// <summary>True while this ring group still has work to do to reach the wanted state.</summary>
    public static bool Pending(Transform u, bool want)
    {
        if (u == null) return false; bool act = u.gameObject.activeSelf;
        if (Rows <= 0) return act != want;
        if (!want) return act;
        if (!act) return true; RS s; return rs.TryGetValue(u, out s) && s.prepped && !s.full;
    }
    /// <summary>One frame of work on a ring group (call only while Pending): the parent first, then up to Rows children per call.</summary>
    public static void Step(Transform u, bool want)
    {
        int rows = Rows; var g = u.gameObject; if (rows <= 0) { g.SetActive(want); return; }
        var s = State(u); int n = u.childCount;
        if (want)
        {
            if (s.mode != 1) { s.mode = 1; s.cur = 0; }
            if (!g.activeSelf) g.SetActive(true);
            if (!s.prepped) { s.full = true; s.mode = 0; return; }   // never staged: every child came up with the parent
            int end = Mathf.Min(n, s.cur + rows); for (int k = s.cur; k < end; k++) { var ch = u.GetChild(k).gameObject; if (!ch.activeSelf) ch.SetActive(true); }
            s.cur = end; if (end >= n) { s.full = true; s.mode = 0; }
        }
        else
        {
            if (s.mode != 2) { s.mode = 2; s.cur = 0; s.full = false; }
            if (!g.activeSelf) { s.mode = 0; return; }
            s.prepped = true;   // from here the rows are individually inactive, so the next wake is staged too
            int end = Mathf.Min(n, s.cur + rows); for (int k = s.cur; k < end; k++) { var ch = u.GetChild(k).gameObject; if (ch.activeSelf) ch.SetActive(false); }
            s.cur = end; if (end >= n) { g.SetActive(false); s.mode = 0; }
        }
    }
    /// <summary>An inactive ring group: switch its rows off once under the inactive parent (free: no OnDisable runs), so its first wake is staged as well.</summary>
    public static void Prep(Transform u)
    {
        if (u == null || Rows <= 0 || u.gameObject.activeSelf) return; var s = State(u); if (s.prepped) return;
        for (int k = 0; k < u.childCount; k++) { var ch = u.GetChild(k).gameObject; if (ch.activeSelf) ch.SetActive(false); }
        s.prepped = true; s.full = false; s.mode = 0;
    }

    // ------------------------------------------------------------------ bound: start the watcher and the warm
    static ZoneHitchRunner runner;
    public static void Bound(ZoneSpan span, Transform skyline, List<Transform> offUnits)
    {
        if (!On) return;
        if (runner == null) { var go = new GameObject("ZoneHitch"); UnityEngine.Object.DontDestroyOnLoad(go); runner = go.AddComponent<ZoneHitchRunner>(); }
        runner.StartWarm(skyline, offUnits);
    }
    internal static void Note(string s) { if (OnNote != null) OnNote(s); }
}

[DefaultExecutionOrder(-32000)]
public sealed class ZoneHitchRunner : MonoBehaviour
{
    bool cover, running; Texture2D tex; Coroutine co;
    void Update() { ZoneHitch.FrameTick(Time.unscaledDeltaTime); }
    void OnDestroy() { if (tex != null) Destroy(tex); }

    public void StartWarm(Transform skyline, List<Transform> units)
    {
        if (co != null) { StopCoroutine(co); co = null; }
        if (!ZoneHitch.WarmOn) { foreach (var u in units) ZoneHitch.Prep(u); return; }
        ZoneHitch.Warming = true; co = StartCoroutine(Warm(skyline, units));
    }

    void OnGUI()
    {
        if (!cover || tex == null || Event.current.type != EventType.Repaint) return;
        GUI.depth = ZoneUiCfg.Get().hitch.depth; GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tex, ScaleMode.StretchToFill, false);   // UIDATA_C v1
    }

    IEnumerator Warm(Transform skyline, List<Transform> units)
    {
        var W = ZoneHitch.C.warm; var sw = new Stopwatch(); var sb = new StringBuilder(); double total = 0; running = true;
        if (W.cover && !PerfFlags.HarnessRun()) { tex = new Texture2D(1, 1); tex.SetPixel(0, 0, Color.black); tex.Apply(); cover = true; }
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < W.startDelaySeconds) yield return null;
        Camera main = null; for (int i = 0; i < W.maxFrames && (main = Camera.main) == null; i++) yield return null;
        var prefabs = new List<GameObject>(); Camera wc = null; RenderTexture rt = null; GameObject wg = null;
        try
        {
            if (W.prefabs) { sw.Restart(); var zr = ZoneRuntime.Inst; if (zr != null) prefabs = zr.SpanPreload(); Step(sb, "prefabs " + prefabs.Count, sw, ref total); }
            yield return null;
            if (main != null && (W.materials || W.rings))
            {
                wg = new GameObject("ZoneHitchCam"); wg.transform.SetParent(transform, false); wc = wg.AddComponent<Camera>(); wc.CopyFrom(main); wc.enabled = false; wc.fieldOfView = W.camFov; wc.farClipPlane = W.camFar; wc.nearClipPlane = 0.1f;
                rt = new RenderTexture(Mathf.Max(64, W.rtWidth), Mathf.Max(64, W.rtHeight), 24, RenderTextureFormat.ARGB32); wc.targetTexture = rt;
                var hd = main.GetComponent<HDAdditionalCameraData>(); if (hd != null) { var nd = wg.GetComponent<HDAdditionalCameraData>(); if (nd == null) nd = wg.AddComponent<HDAdditionalCameraData>(); hd.CopyTo(nd); }
                wc.cullingMask = main.cullingMask;
            }
            if (wc != null && W.materials && prefabs.Count > 0)
            {
                sw.Restart(); int drawn = DrawMaterials(wc, prefabs); wc.transform.position = new Vector3(0f, -500f, 0f); wc.transform.rotation = Quaternion.identity;
                RenderOnce(wc, drawn, prefabs); Step(sb, "materials " + drawn, sw, ref total); for (int k = 0; k < W.framesBetween; k++) yield return null;
            }
            if (wc != null && W.rings)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i]; if (u == null || u.gameObject.activeSelf) continue; sw.Restart();
                    u.gameObject.SetActive(true); Vector3 ctr; float rad = Bounds(u, out ctr);
                    wc.transform.position = ctr + new Vector3(0f, W.camHeightM, -Mathf.Max(W.camBackM, rad * 0.6f)); wc.transform.rotation = Quaternion.LookRotation((ctr - wc.transform.position).normalized, Vector3.up);
                    RenderOnce(wc, 0, null); u.gameObject.SetActive(false); Step(sb, "ring " + u.name, sw, ref total);
                    for (int k = 0; k < W.framesBetween; k++) yield return null;
                }
            }
            if (W.skyline && skyline != null) { sw.Restart(); var p = skyline.position; skyline.position = p + new Vector3(0f, 0f, 0.001f); if (wc != null) RenderOnce(wc, 0, null); skyline.position = p; Step(sb, "skyline", sw, ref total); yield return null; }
        }
        finally
        {
            if (wc != null) wc.targetTexture = null; if (rt != null) { rt.Release(); Destroy(rt); } if (wg != null) Destroy(wg);
            foreach (var u in units) ZoneHitch.Prep(u);   // rows off under the inactive parents: from now on the first wake is staged too
            cover = false; running = false; co = null; ZoneHitch.Warming = false;
        }
        Debug.Log("[ZoneHitch] warm done " + total.ToString("F0", CultureInfo.InvariantCulture) + " ms: " + sb + "(" + (Time.realtimeSinceStartup - t0).ToString("F2", CultureInfo.InvariantCulture) + " s wall, cover " + (tex != null) + ")");
        ZoneHitch.Note("warm:done");
        if (tex != null) { Destroy(tex); tex = null; }
    }

    static void Step(StringBuilder sb, string what, Stopwatch sw, ref double total) { sw.Stop(); double ms = sw.Elapsed.TotalMilliseconds; total += ms; sb.Append(what).Append(' ').Append(ms.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms; "); }
    static float Bounds(Transform u, out Vector3 ctr)
    {
        var rr = u.GetComponentsInChildren<Renderer>(true); bool any = false; Bounds b = new Bounds(u.position, Vector3.zero);
        for (int i = 0; i < rr.Length; i++) { if (rr[i] == null) continue; if (!any) { b = rr[i].bounds; any = true; } else b.Encapsulate(rr[i].bounds); }
        ctr = b.center; return b.extents.magnitude;
    }

    // the creature prefab materials, drawn once in front of the warm camera (the draw is queued for the next Render of that camera)
    static readonly List<Material> mats = new List<Material>(); static readonly List<Mesh> meshes = new List<Mesh>(); static readonly List<int> subs = new List<int>();
    static int DrawMaterials(Camera cam, List<GameObject> prefabs)
    {
        mats.Clear(); meshes.Clear(); subs.Clear(); var seen = new HashSet<Material>();
        foreach (var pf in prefabs)
        {
            if (pf == null) continue;
            foreach (var r in pf.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = null; var smr = r as SkinnedMeshRenderer; if (smr != null) m = smr.sharedMesh; else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
                if (m == null) continue; var sm = r.sharedMaterials;
                for (int i = 0; i < sm.Length; i++) { if (sm[i] == null || i >= m.subMeshCount || !seen.Add(sm[i])) continue; mats.Add(sm[i]); meshes.Add(m); subs.Add(i); }
            }
        }
        return mats.Count;
    }
    void RenderOnce(Camera cam, int drawn, List<GameObject> unused)
    {
        try
        {
            if (drawn > 0 && mats.Count > 0)
            {
                cam.transform.position = new Vector3(0f, -500f, 0f); cam.transform.rotation = Quaternion.identity; Vector3 at = cam.transform.position + new Vector3(0f, 0f, 6f);
                for (int i = 0; i < mats.Count; i++) Graphics.DrawMesh(meshes[i], Matrix4x4.TRS(at, Quaternion.identity, Vector3.one * 0.2f), mats[i], 0, cam, subs[i]);
            }
            cam.Render();
        }
        catch (Exception e) { Debug.LogWarning("[ZoneHitch] warm render: " + e.Message); }
    }
}
