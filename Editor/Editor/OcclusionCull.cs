using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// OCCL (laneOCCLUSION): static-world occlusion culling as a data-driven scene step. Dom: fps ~23 at Ultra 1080p; the 81 WorldBehind props (z 34..126, behind the 10 m gate wall) are
// 29-42 % of the world triangles in the lock-cam frustum and are never visible. This step flags the static world (OccludeeStatic), the gate wall + big walls/cliffs (OccluderStatic),
// switches camera.useOcclusionCulling on and bakes StaticOcclusionCulling. ALL numbers/lists live in Assets/Data/occlusion.json (EDITOR-ONLY, never read in the player).
// Run AFTER PropsLayout.RebuildAndSave + HdrpDoorway.Run (the props and the gate must exist) and BEFORE BuildPlayerCmd.Run. Hook: HdrpBatch.Production, see MANIFEST.md.
// Idempotent: clearFirst removes our two flags everywhere, then re-applies. enabled=false = kill switch (clears flags + baked data, saves).
public static class OcclusionCull
{
    public const string DataPath = "Data/occlusion.json";   // relative to Application.dataPath (Assets/)

    [Serializable] class Occludee { public string[] groups = new string[0], extraRootNames = new string[0]; public float maxRadiusFromOrigin = 0f; }
    [Serializable] class Occluder { public string[] rootNames = new string[0], assetPatterns = new string[0]; public float minLongestSide = 8f, minThickness = 1f; public bool lod0Only = true; }
    [Serializable] class Skip { public string[] rootNames = new string[0], nameContains = new string[0]; public bool skipSkinned = true, skipParticles = true, skipTransparentMaterials = true; }
    [Serializable] class Compute { public float smallestOccluder = 5f, smallestHole = 0.25f, backfaceThreshold = 100f; public bool bake = true; }
    [Serializable] class Cam { public bool mainCamera = true, useOcclusionCulling = true; public string[] offNames = new string[0]; }
    [Serializable] class Row
    {
        public bool enabled = true, clearFirst = true;
        public string scenePath = "Assets/Scenes/HadesSlice.unity", arenaName = "Arena", report = "TestOut/occlusion_report.txt";
        public Occludee occludee = new Occludee(); public Occluder occluder = new Occluder(); public Skip skip = new Skip(); public Compute compute = new Compute(); public Cam camera = new Cam();
    }

    const StaticEditorFlags Both = StaticEditorFlags.OccludeeStatic | StaticEditorFlags.OccluderStatic;

    public static void RunBatch() { Run(); }

    [MenuItem("Hades/Occlusion/Apply flags + bake from occlusion.json")]
    public static void Run()
    {
        string path = Path.Combine(Application.dataPath, DataPath);
        if (!File.Exists(path)) { Debug.LogWarning("OCCL: missing " + path + " (step skipped)"); return; }
        var R = JsonUtility.FromJson<Row>(File.ReadAllText(path));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sc = EditorSceneManager.OpenScene(R.scenePath);
        var sb = new System.Text.StringBuilder();
        var fighters = new HashSet<string>(RosterEditor.FighterObjects());

        // 1) clear our flags everywhere (idempotent re-run; the kill switch ends here)
        int cleared = 0;
        if (R.clearFirst || !R.enabled)
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var f = GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                if ((f & Both) != 0) { GameObjectUtility.SetStaticEditorFlags(t.gameObject, f & ~Both); cleared++; }
            }
        if (!R.enabled)
        {
            StaticOcclusionCulling.Clear();
            EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
            Debug.Log("OCCL disabled (occlusion.json enabled=false): cleared " + cleared + " flags and the baked data");
            return;
        }

        // 2) occluders + occludees
        var arena = GameObject.Find(R.arenaName);
        int occludees = 0, occluders = 0, skipped = 0;
        var seen = new HashSet<Renderer>();
        var rx = new List<Regex>(); foreach (var p in R.occluder.assetPatterns) rx.Add(new Regex(p, RegexOptions.IgnoreCase));
        var roots = new List<Transform>();
        if (arena != null) foreach (var g in R.occludee.groups) { var c = arena.transform.Find(g); if (c != null) roots.Add(c); else sb.AppendLine("  group not in scene: " + g); }
        foreach (var n in R.occludee.extraRootNames) { var go = GameObject.Find(n); if (go != null) roots.Add(go.transform); else sb.AppendLine("  extra root not in scene: " + n); }

        foreach (var root in roots)
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!seen.Add(r)) continue;
                if (Skipped(r, R.skip, fighters, false)) { skipped++; continue; }
                if (R.occludee.maxRadiusFromOrigin > 0f && new Vector2(r.bounds.center.x, r.bounds.center.z).magnitude > R.occludee.maxRadiusFromOrigin) { skipped++; continue; }
                Add(r.gameObject, StaticEditorFlags.OccludeeStatic); occludees++;
                if (IsOccluder(r, root, R, rx)) { Add(r.gameObject, StaticEditorFlags.OccluderStatic); occluders++; }
            }
        // gate wall (and anything else named in occluder.rootNames) wherever it lives in the scene
        foreach (var n in R.occluder.rootNames)
        {
            Transform gt = null;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (t.name == n) { gt = t; break; }
            if (gt == null) { sb.AppendLine("  occluder root not in scene: " + n); continue; }
            foreach (var r in gt.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (Skipped(r, R.skip, fighters, true)) { skipped++; continue; }
                var f = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                if ((f & StaticEditorFlags.OccludeeStatic) == 0) occludees++;
                if ((f & StaticEditorFlags.OccluderStatic) == 0) occluders++;
                Add(r.gameObject, Both);
            }
        }

        // 3) cameras
        int camsOn = 0, camsOff = 0;
        if (R.camera.mainCamera && Camera.main != null) { Camera.main.useOcclusionCulling = R.camera.useOcclusionCulling; camsOn++; EditorUtility.SetDirty(Camera.main); }
        foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (Array.IndexOf(R.camera.offNames, c.name) >= 0) { c.useOcclusionCulling = false; camsOff++; EditorUtility.SetDirty(c); }

        // 4) bake
        bool baked = false;
        if (R.compute.bake)
        {
            StaticOcclusionCulling.smallestOccluder = R.compute.smallestOccluder;
            StaticOcclusionCulling.smallestHole = R.compute.smallestHole;
            StaticOcclusionCulling.backfaceThreshold = R.compute.backfaceThreshold;
            double t0 = sw.Elapsed.TotalSeconds;
            baked = StaticOcclusionCulling.Compute();   // blocking in batch mode; bakes the open scene; result is an OcclusionCullingData asset beside the scene
            sb.AppendLine("  bake " + (baked ? "OK" : "FAILED") + " in " + (sw.Elapsed.TotalSeconds - t0).ToString("F1") + " s, umbraDataSize=" + StaticOcclusionCulling.umbraDataSize + " bytes");
        }
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);

        string line = "OCCL occludees=" + occludees + " occluders=" + occluders + " skipped=" + skipped + " cleared=" + cleared + " camsOn=" + camsOn + " camsOff=" + camsOff + " baked=" + baked + " total=" + sw.Elapsed.TotalSeconds.ToString("F1") + " s\n" + sb;
        Debug.Log(line);
        try { string dir = Path.GetDirectoryName(R.report); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); File.WriteAllText(R.report, line); } catch (Exception e) { Debug.LogWarning("OCCL report not written: " + e.Message); }
    }

    static void Add(GameObject go, StaticEditorFlags add) { GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(go) | add); }

    // occluderRoot=true: the renderer sits under an occluder.rootNames object (the gate wall): the nameContains filter is not applied (a gate may legitimately contain "Gate" etc.), rootNames/characters still are
    static bool Skipped(Renderer r, Skip S, HashSet<string> fighters, bool occluderRoot)
    {
        if (S.skipParticles && r is ParticleSystemRenderer) return true;
        if (r.GetComponentInParent<CharacterController>() != null || r.GetComponentInParent<Animator>() != null) return true;
        if (S.skipSkinned && r.GetComponentInParent<SkinnedMeshRenderer>() != null) return true;
        if (S.skipTransparentMaterials)
            foreach (var m in r.sharedMaterials) if (m != null && (m.renderQueue >= 2500 || (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f))) return true;
        for (var t = r.transform; t != null; t = t.parent)
        {
            if (fighters.Contains(t.name)) return true;
            if (!occluderRoot && Array.IndexOf(S.rootNames, t.name) >= 0) return true;
            if (!occluderRoot) foreach (var s in S.nameContains) if (t.name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // a prop root (direct child of the group) is an occluder when its id matches a pattern and its renderer bounds are big and thick enough; lod0Only restricts it to the LOD0 child
    static bool IsOccluder(Renderer r, Transform groupRoot, Row R, List<Regex> rx)
    {
        Transform prop = r.transform; while (prop.parent != null && prop.parent != groupRoot) prop = prop.parent;
        if (prop.parent != groupRoot) return false;
        bool match = false; foreach (var e in rx) if (e.IsMatch(prop.name)) { match = true; break; }
        if (!match) return false;
        if (R.occluder.lod0Only) { bool inLod0 = false; for (var t = r.transform; t != null && t != prop; t = t.parent) if (t.name == "LOD0") inLod0 = true; if (!inLod0) return false; }
        var b = r.bounds; float a = b.size.x, c = b.size.z, h = b.size.y;
        float longest = Mathf.Max(a, Mathf.Max(c, h)), thick = Mathf.Min(a, c);
        return longest >= R.occluder.minLongestSide && thick >= R.occluder.minThickness;
    }
}
