using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;

// lanePERF4 v100: the BUILD-side half of the memory kit. Every step is a DRY RUN unless the command line has -apply, and every step has its own "enabled" flag in perf_mem_build.json (all false).
//   E1 remove the unused MetaHumanHead object from the scene  |  E2 drop LOD1/LOD2 of the listed meshes (arena floor)  |  E5 texture import settings of texture_plan.json (TextureImporter files only)
// Menu: Hades > Perf > PerfMem dry run / apply.   Batch: Unity.exe -batchmode -projectPath ... -executeMethod PerfMemApply.DryRun   (or .Apply, or add -apply to .Run)
// Writes only: the scene (E1 / E2) and the .meta of the listed textures (E5). Logs "[PerfMemApply] ..." lines; the unity-dev diffs git/backup before committing.
public static class PerfMemApply
{
    [Serializable] public class Step { public bool enabled = false; public string[] names = new string[0]; public string plan; public string platform = "Standalone"; public Rule[] rules = new Rule[0]; }
    [Serializable] public class Rule { public string match; public int keepLods = 1; }
    [Serializable] public class BuildCfg { public string scene = "Assets/Scenes/HadesSlice.unity"; public Step E1 = new Step(); public Step E2 = new Step(); public Step E5 = new Step(); }
    [Serializable] public class SetRow { public string format; public int maxTextureSize; public bool streamingMipmaps; }
    [Serializable] public class TexRow { public string path; public int w, h; public float nowMB, estNewMB; public SetRow set = new SetRow(); public string group; }
    [Serializable] public class Plan { public TexRow[] rows = new TexRow[0]; }

    [MenuItem("Hades/Perf/PerfMem dry run")] public static void DryRun() { Run(false); }
    [MenuItem("Hades/Perf/PerfMem apply (enabled steps)")] public static void Apply() { Run(true); }
    public static void Run() { bool a = false; foreach (var s in Environment.GetCommandLineArgs()) if (s == "-apply") a = true; Run(a); }

    static string Read(string assetPath) { var p = Path.Combine(Directory.GetCurrentDirectory(), assetPath.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(p) ? File.ReadAllText(p).TrimStart((char)0xFEFF) : null; }

    static void Run(bool apply)
    {
        var txt = Read("Assets/Data/perf_mem_build.json"); if (txt == null) { Debug.LogError("[PerfMemApply] Assets/Data/perf_mem_build.json missing"); return; }
        var cfg = JsonUtility.FromJson<BuildCfg>(txt); var log = new StringBuilder("[PerfMemApply] " + (apply ? "APPLY" : "DRY RUN") + "\n");
        if (cfg.E1 == null) cfg.E1 = new Step(); if (cfg.E2 == null) cfg.E2 = new Step(); if (cfg.E5 == null) cfg.E5 = new Step();
        bool sceneDirty = false; UnityEngine.SceneManagement.Scene sc = default;
        if (cfg.E1.enabled || cfg.E2.enabled) sc = EditorSceneManager.OpenScene(cfg.scene, OpenSceneMode.Single);
        if (cfg.E1.enabled) sceneDirty |= E1(cfg.E1, apply, log); else log.AppendLine("E1 disabled (perf_mem_build.json)");
        if (cfg.E2.enabled) sceneDirty |= E2(cfg.E2, apply, log); else log.AppendLine("E2 disabled");
        if (apply && sceneDirty) { EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc); log.AppendLine("scene saved: " + cfg.scene); }
        if (cfg.E5.enabled) E5(cfg.E5, apply, log); else log.AppendLine("E5 disabled");
        Debug.Log(log.ToString());
    }

    static bool E1(Step s, bool apply, StringBuilder log)
    {
        int n = 0;
        foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (Array.IndexOf(s.names, t.name) < 0) continue;
                long before = 0; foreach (var r in t.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.sharedMaterials) if (m != null) before++;
                log.AppendLine("E1 " + (apply ? "removing " : "would remove ") + PathOf(t) + " (renderers+materials refs " + before + ", active=" + t.gameObject.activeSelf + ")");
                if (apply) { if (PrefabUtility.IsPartOfPrefabInstance(t.gameObject)) PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject), PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction); UnityEngine.Object.DestroyImmediate(t.gameObject); }
                n++; break;   // a destroyed object invalidates the children list: one per root, rerun for more
            }
        if (n == 0) log.AppendLine("E1 nothing found (already removed?)");
        return apply && n > 0;
    }

    static bool E2(Step s, bool apply, StringBuilder log)
    {
        bool dirty = false;
        foreach (var rule in s.rules)
        {
            foreach (var g in UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (PathOf(g.transform).IndexOf(rule.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var lods = g.GetLODs(); if (lods.Length <= rule.keepLods) { log.AppendLine("E2 " + PathOf(g.transform) + " has " + lods.Length + " LOD(s), nothing to drop"); continue; }
                int drop = 0; for (int i = rule.keepLods; i < lods.Length; i++) drop += lods[i].renderers.Length;
                log.AppendLine("E2 " + (apply ? "dropping " : "would drop ") + "LOD" + rule.keepLods + ".." + (lods.Length - 1) + " (" + drop + " renderers) of " + PathOf(g.transform));
                if (!apply) continue;
                var keep = new LOD[rule.keepLods]; Array.Copy(lods, keep, rule.keepLods); keep[rule.keepLods - 1].screenRelativeTransitionHeight = lods[lods.Length - 1].screenRelativeTransitionHeight;
                var go = g.gameObject;
                if (PrefabUtility.IsPartOfPrefabInstance(go)) PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(go), PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                var doomed = new List<GameObject>(); for (int i = rule.keepLods; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) doomed.Add(r.gameObject);
                g.SetLODs(keep); foreach (var d in doomed) if (d != null) UnityEngine.Object.DestroyImmediate(d);
                g.RecalculateBounds(); dirty = true;
            }
        }
        return dirty;
    }

    static TextureImporterFormat Fmt(string s)
    {
        switch ((s ?? "").ToUpperInvariant()) { case "BC7": return TextureImporterFormat.BC7; case "BC5": return TextureImporterFormat.BC5; case "BC6H": return TextureImporterFormat.BC6H; default: return TextureImporterFormat.Automatic; }
    }

    static void E5(Step s, bool apply, StringBuilder log)
    {
        var txt = Read(s.plan); if (txt == null) { log.AppendLine("E5 plan missing: " + s.plan); return; }
        var plan = JsonUtility.FromJson<Plan>(txt); double now = 0, est = 0, after = 0;
        foreach (var r in plan.rows)
        {
            var ti = AssetImporter.GetAtPath(r.path) as TextureImporter; if (ti == null) { log.AppendLine("E5 SKIP (no TextureImporter): " + r.path); continue; }
            var tex = AssetDatabase.LoadAssetAtPath<Texture>(r.path); double nowMB = tex != null ? Profiler.GetRuntimeMemorySizeLong(tex) / 1048576.0 : -1;
            var ps = ti.GetPlatformTextureSettings(s.platform);
            string was = "max " + ps.maxTextureSize + " fmt " + ps.format + " ovr " + ps.overridden + " stream " + ti.streamingMipmaps;
            log.AppendLine("E5 " + (apply ? "set " : "would set ") + r.path + " [" + was + "] -> " + (r.set.format ?? "") + " max " + r.set.maxTextureSize + " stream " + r.set.streamingMipmaps + "  now " + nowMB.ToString("F1") + " MB, plan est " + r.estNewMB.ToString("F1") + " MB");
            now += nowMB; est += r.estNewMB;
            if (!apply) continue;
            ps.overridden = true; if (ps.maxTextureSize <= 0) ps.maxTextureSize = ti.maxTextureSize;
            if (r.set.maxTextureSize > 0) ps.maxTextureSize = r.set.maxTextureSize;
            var f = Fmt(r.set.format); if (f != TextureImporterFormat.Automatic) ps.format = f;
            ti.SetPlatformTextureSettings(ps); if (r.set.streamingMipmaps && ti.mipmapEnabled) ti.streamingMipmaps = true;
            ti.SaveAndReimport();
            var t2 = AssetDatabase.LoadAssetAtPath<Texture>(r.path); if (t2 != null) after += Profiler.GetRuntimeMemorySizeLong(t2) / 1048576.0;
        }
        log.AppendLine("E5 total now " + now.ToString("F1") + " MB, plan " + est.ToString("F1") + " MB" + (apply ? ", measured after " + after.ToString("F1") + " MB" : ""));
    }

    static string PathOf(Transform t) { string p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }
}
