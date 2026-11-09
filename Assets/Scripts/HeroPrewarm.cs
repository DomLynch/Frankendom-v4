using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// HEROPREWARM v1 (laneFPSX_D kit3, 2026-11-08). UNPROVEN IN UNITY. Nothing here touches the sim, Duel, AIBrain, RNG or any tick.
// What it does: for every row of hero_prewarm.json "rows" it finds the scene object(s) by NAME, collects (mesh, material, submesh) of their renderers (every LOD, or one LOD),
// and draws each combination once per frame for drawFrames frames at a SUB-PIXEL size just in front of Camera.main (Graphics.DrawMesh, same technique as FxPrewarm / Prewarm92).
// Purpose: the first draw of a big hero mesh (250k-tri cedar, 150k arch, 120k house / ring) uploads its vertex / index buffers and builds its pipeline states in the frame the ring wakes;
// doing that draw earlier moves that cost to a frame the player does not see. It does NOT prove a hitch exists: no log in the tree names a hero mesh as a hitch cause (laneHITCH item 8).
// WHEN: rows without whenZ run right after the scene load, while StartupWarm.Done is still false (= behind the black StartupWarm cover) for at most maxCoverFrames frames, all rows of a frame
// (rowsPerFrame is the limit once the cover is gone). A row with whenZ > 0 waits until Camera.main z >= whenZ (zone approach). A row with late=true is searched among SkinnedMeshRenderers
// every pollSeconds (AmbientHorses builds its horses at run time, inactive, named AmbientHorse_<herd>_<i>) and is warmed once when found, up to lateMaxSeconds.
// SAFE DEFAULT: enabled=false in the class, so a missing file, a missing "enabled" key or an unreadable file = OFF. Also OFF in batch mode, with -noheroprewarm, and in harness / proof runs
// (PerfFlags.HarnessRun) unless inHarness or -heroprewarmharness. Log: "[HeroPrewarm] ..." lines (per row: objects, draws, the frame time of the frame that carried them).
public class HeroPrewarm : MonoBehaviour
{
    [Serializable]
    public class Row
    {
        public string label = "";          // log only
        public string match = "";          // GameObject name of the prop row (LODGroup root, e.g. z5h_humbaba_cedar) or, with late, of the root object
        public string mode = "exact";      // "exact" | "prefix"
        public int lod = -1;               // -1 = every LOD renderer under the object (LOD1/2 upload when the LOD switches), else this LOD index only
        public int maxObjects = 1;         // objects matched per row (one is enough when the same asset is instanced many times)
        public bool late = false;          // search SkinnedMeshRenderer roots every pollSeconds until found (runtime-loaded GLBs)
        public bool castShadows = true;    // draw with shadows ON so the shadow-caster pass state exists too
        public float whenZ = 0f;           // > 0: wait until Camera.main z >= whenZ
    }

    [Serializable]
    public class Cfg
    {
        public bool enabled = false;
        public bool inHarness = false;
        public int startDelayFrames = 2, maxWaitFrames = 300, maxCoverFrames = 8, rowsPerFrame = 1, drawFrames = 3;
        public float distanceM = 2f, sizeM = 0.00002f, pollSeconds = 1f, lateMaxSeconds = 180f;
        public bool log = true;
        public Row[] rows = new Row[0];
    }

    class Draw { public Mesh mesh; public Material mat; public int sub; public bool shadows; }
    class Active { public string label; public List<Draw> draws = new List<Draw>(); public int left; public int objs; }

    const double MsPerS = 1000.0;   // unit conversion
    const float MinDistanceM = 0.5f, MinSizeM = 1e-6f, MinPollS = 0.1f, MinExtentM = 1e-4f;   // guards against a bad json value, not tuning
    static Cfg cfg;
    public static bool Done;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; Done = false; }

    static Cfg Load()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        try { var txt = HudCfg.ReadData("hero_prewarm.json"); if (txt != null) { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } }
        catch (Exception e) { Debug.LogWarning("[HeroPrewarm] hero_prewarm.json: " + e.Message); cfg = new Cfg(); }
        if (cfg.rows == null) cfg.rows = new Row[0];
        cfg.startDelayFrames = Mathf.Max(0, cfg.startDelayFrames); cfg.maxWaitFrames = Mathf.Max(1, cfg.maxWaitFrames); cfg.maxCoverFrames = Mathf.Max(0, cfg.maxCoverFrames);
        cfg.rowsPerFrame = Mathf.Max(1, cfg.rowsPerFrame); cfg.drawFrames = Mathf.Max(1, cfg.drawFrames);
        cfg.distanceM = Mathf.Max(MinDistanceM, cfg.distanceM); cfg.sizeM = Mathf.Max(MinSizeM, cfg.sizeM); cfg.pollSeconds = Mathf.Max(MinPollS, cfg.pollSeconds);
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        bool harnessArg = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("-noheroprewarm", StringComparison.OrdinalIgnoreCase)) return;
            if (a.Equals("-heroprewarmharness", StringComparison.OrdinalIgnoreCase)) harnessArg = true;
        }
        var c = Load(); if (!c.enabled || c.rows.Length == 0) return;
        if (PerfFlags.HarnessRun() && !c.inHarness && !harnessArg) return;
        if (FindFirstObjectByType<HeroPrewarm>() != null) return;
        var go = new GameObject("HeroPrewarm"); DontDestroyOnLoad(go); go.AddComponent<HeroPrewarm>();
    }

    static bool NameOk(string name, Row r)
    {
        if (string.IsNullOrEmpty(r.match) || name == null) return false;
        return string.Equals(r.mode, "prefix", StringComparison.Ordinal) ? name.StartsWith(r.match, StringComparison.Ordinal) : name == r.match;
    }

    static void AddRenderer(Renderer rd, bool shadows, List<Draw> outList, List<Draw> seen)
    {
        if (rd == null) return;
        Mesh m = null; var smr = rd as SkinnedMeshRenderer;
        if (smr != null) m = smr.sharedMesh; else { var mf = rd.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
        if (m == null) return;
        var sm = rd.sharedMaterials;
        for (int i = 0; i < sm.Length && i < m.subMeshCount; i++)
        {
            if (sm[i] == null) continue;
            bool dup = false; for (int k = 0; k < outList.Count; k++) if (outList[k].mat == sm[i] && outList[k].mesh == m && outList[k].sub == i) { dup = true; break; }
            if (dup) continue;   // (GetInstanceID is obsolete in this Unity: plain reference comparison, the lists are tens of entries)
            outList.Add(new Draw { mesh = m, mat = sm[i], sub = i, shadows = shadows });
        }
    }

    // returns the number of objects matched; fills the draw list
    static int Collect(Row r, LODGroup[] groups, Active act)
    {
        var seen = act.draws; int objs = 0; int cap = Mathf.Max(1, r.maxObjects);
        if (!r.late)
        {
            for (int g = 0; g < groups.Length && objs < cap; g++)
            {
                var lg = groups[g]; if (lg == null || !NameOk(lg.gameObject.name, r)) continue;
                objs++;
                if (r.lod < 0) { foreach (var rd in lg.GetComponentsInChildren<Renderer>(true)) AddRenderer(rd, r.castShadows, act.draws, seen); }
                else
                {
                    var lods = lg.GetLODs(); if (lods.Length == 0) continue;
                    var L = lods[Mathf.Min(r.lod, lods.Length - 1)];
                    if (L.renderers != null) foreach (var rd in L.renderers) AddRenderer(rd, r.castShadows, act.draws, seen);
                }
            }
        }
        else
        {
            var roots = new HashSet<Transform>();
            foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (smr == null) continue; var root = smr.transform.root; if (root == null || !NameOk(root.name, r) || roots.Contains(root)) continue;
                if (roots.Count >= cap) break;
                roots.Add(root);
            }
            foreach (var root in roots) { objs++; foreach (var rd in root.GetComponentsInChildren<Renderer>(true)) AddRenderer(rd, r.castShadows, act.draws, seen); }
        }
        act.objs = objs; return objs;
    }

    IEnumerator Start()
    {
        var c = cfg; var sw = new Stopwatch(); var sb = new StringBuilder(); double total = 0; float t0 = Time.realtimeSinceStartup;
        for (int i = 0; i < c.startDelayFrames; i++) yield return null;
        Camera cam = null; for (int i = 0; i < c.maxWaitFrames && (cam = Camera.main) == null; i++) yield return null;
        if (cam == null) { Debug.LogWarning("[HeroPrewarm] no Camera.main, nothing warmed"); Done = true; Destroy(gameObject); yield break; }
        LODGroup[] groups = null;
        var pending = new List<Row>(); var late = new List<Row>();
        foreach (var r in c.rows) { if (r == null || string.IsNullOrEmpty(r.match)) continue; if (r.late) late.Add(r); else pending.Add(r); }
        var live = new List<Active>(); var carried = new List<string>(); int frames = 0, coverFrames = 0, warmed = 0, missed = 0; float lateT = 0f; double worstFrameMs = 0;
        var lateDone = new HashSet<Row>();
        while (pending.Count > 0 || late.Count > lateDone.Count || live.Count > 0)
        {
            if (frames > 0 && carried.Count > 0)   // the frame that carried the draws queued last frame
            {
                double dt = Time.unscaledDeltaTime * MsPerS; if (dt > worstFrameMs) worstFrameMs = dt;
                if (c.log) Debug.Log("[HeroPrewarm] frame " + dt.ToString("F1", CultureInfo.InvariantCulture) + " ms carried: " + string.Join(", ", carried.ToArray()) + (StartupWarm.Done ? "" : " (cover up)"));
                carried.Clear();
            }
            cam = Camera.main; if (cam == null) { yield return null; frames++; continue; }
            bool cover = !StartupWarm.Done && coverFrames < c.maxCoverFrames; if (!StartupWarm.Done) coverFrames++;
            int budget = cover ? int.MaxValue : c.rowsPerFrame;
            for (int i = 0; i < pending.Count && budget > 0; )
            {
                var r = pending[i];
                if (r.whenZ > 0f && cam.transform.position.z < r.whenZ) { i++; continue; }
                sw.Restart();
                if (groups == null) groups = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var act = new Active { label = string.IsNullOrEmpty(r.label) ? r.match : r.label, left = c.drawFrames };
                int n = Collect(r, groups, act); sw.Stop(); total += sw.Elapsed.TotalMilliseconds;
                if (n == 0 || act.draws.Count == 0) { missed++; Debug.LogWarning("[HeroPrewarm] row " + act.label + ": object '" + r.match + "' not found in the scene (zone not built into this scene?), skipped"); }
                else { live.Add(act); warmed++; if (c.log) Debug.Log("[HeroPrewarm] row " + act.label + ": " + n + " object(s), " + act.draws.Count + " mesh/material draw(s), collect " + sw.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms"); }
                pending.RemoveAt(i); budget--;
            }
            lateT += Time.unscaledDeltaTime;
            if (late.Count > lateDone.Count && lateT >= c.pollSeconds)
            {
                lateT = 0f;
                foreach (var r in late)
                {
                    if (lateDone.Contains(r)) continue;
                    var act = new Active { label = string.IsNullOrEmpty(r.label) ? r.match : r.label, left = c.drawFrames };
                    if (Collect(r, null, act) > 0 && act.draws.Count > 0) { live.Add(act); lateDone.Add(r); warmed++; if (c.log) Debug.Log("[HeroPrewarm] late row " + act.label + ": " + act.objs + " object(s), " + act.draws.Count + " draw(s)"); }
                    else if (Time.realtimeSinceStartup - t0 > c.lateMaxSeconds) { lateDone.Add(r); missed++; Debug.LogWarning("[HeroPrewarm] late row " + act.label + ": '" + r.match + "' never appeared within " + c.lateMaxSeconds + " s, skipped"); }
                }
            }
            if (live.Count > 0)
            {
                Transform ct = cam.transform; Vector3 pos = ct.position + ct.forward * c.distanceM;
                for (int a = live.Count - 1; a >= 0; a--)
                {
                    var act = live[a];
                    foreach (var d in act.draws)
                    {
                        if (d.mesh == null || d.mat == null) continue;
                        var b = d.mesh.bounds.size; float ext = Mathf.Max(MinExtentM, Mathf.Max(b.x, Mathf.Max(b.y, b.z)));
                        var m = Matrix4x4.TRS(pos, ct.rotation, Vector3.one * (c.sizeM / ext));
                        try { Graphics.DrawMesh(d.mesh, m, d.mat, 0, (Camera)null, d.sub, (MaterialPropertyBlock)null, d.shadows ? ShadowCastingMode.On : ShadowCastingMode.Off, false); }
                        catch (Exception e) { Debug.LogWarning("[HeroPrewarm] draw " + act.label + ": " + e.Message); }
                    }
                    if (act.left == c.drawFrames) carried.Add(act.label);
                    if (--act.left <= 0) live.RemoveAt(a);
                }
            }
            frames++; yield return null;
        }
        if (carried.Count > 0) { yield return null; if (c.log) Debug.Log("[HeroPrewarm] last frame " + (Time.unscaledDeltaTime * MsPerS).ToString("F1", CultureInfo.InvariantCulture) + " ms carried: " + string.Join(", ", carried.ToArray())); }
        Debug.Log("[HeroPrewarm] done rows warmed " + warmed + ", not found " + missed + ", collect " + total.ToString("F0", CultureInfo.InvariantCulture) + " ms, worst carrying frame " + worstFrameMs.ToString("F1", CultureInfo.InvariantCulture) + " ms, "
                  + (Time.realtimeSinceStartup - t0).ToString("F2", CultureInfo.InvariantCulture) + " s wall, cover frames used " + coverFrames);
        Done = true; Destroy(gameObject);
    }
}
