#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

// APVZONE v3 (laneVIS_apv, 2026-11-11). NEW EDITOR FILE, marker "APVZONE v3". UNPROVEN IN UNITY (Roslyn-compiled only). Successor of the parked ApvBakeZones.cs (APVZONES v2), whose one run (2026-11-10 16:40) never left the
// lightmapper PREPARE stage in 12 min: it flagged ALL 5370 MeshRenderers of all 7 zones (every LOD of every LODGroup, 13.6 M triangles, LightBaker input 3287 MB, Editor 28 GB) and an abort left 7 APV_* volumes in the scene.
// What v3 changes (every number comes from Assets/Data/apv_zones.json, nothing at runtime reads it):
//   * ZONE SELECTION: -apvZone <id[,id]|all>. Only those volume rows are created and baked; only their zone roots are activated for the bake (+ alwaysRoots).
//   * CASTER SET instead of "everything static": ContributeGI is CLEARED on every renderer under staticRoots first, then set only on renderers that (a) are active, (b) are not dynamic/excluded, (c) intersect a chosen volume grown by caster.marginM,
//     (d) are at least caster.minSizeM across, (e) are the ONE chosen LOD of their LODGroup (lowest-poly LOD with at least caster.minLodTris triangles), (f) fit caster.triBudget (densest small things dropped first).
//     The ORIGINAL ContributeGI/ReflectionProbeStatic flags are written to Logs\apvz_restore.json before the first change and put back after the bake. ReceiveGI = Light Probes is kept on renderers inside a chosen volume (needed at runtime).
//   * STRAYS: every APV_* volume / proxy / sky object is deleted before a run, and again on ANY failure; Frankendom > APV > Restore (strays + roots + flags) fixes a crashed Editor by hand.
//   * REPORT: per-zone table (casters, triangles, materials, calculated texture MB) in the dry run; probe/cell count and MB after the bake; Assets/Data/apv_bake_stamp.json (zone ids + md5 of the layout inputs) for check_apv_stale.py.
// Command line (ONE Editor, alone, NO -quit, NO -nographics):  Unity -batchmode -projectPath <proj> -executeMethod ApvBakeZone.RunBatch -apvZone sobek_reach [-apvMode dry|bake] [-apvProfile fast|final] -logFile <log>
// Exit codes: 0 ok, 2 plan/args, 3 scene missing, 4 bake timed out (cancelled, cleaned), 5 bake would not start, 6 data over budget, 7 no baked data, 8 exception, 9 caster gate (0 casters or over the hard cap).
public static class ApvBakeZone
{
    [Serializable] public class Vol { public string id = "", zone = "", root = "", layoutFile = "", note = ""; public float[] center = new float[3], size = new float[3]; public bool enabled = true; }
    [Serializable] public class Density { public float minDistanceBetweenProbes = 1f; public int simplificationLevels = 3; public bool virtualOffset = true, dilation = true, skyOcclusion = false; }
    [Serializable] public class StaticSky { public string mode = "auto"; public float hdriExposure = -999f, gradientMultiplier = 0f; public string ultraFile = "Assets/Data/hdrp_lighting_ultra.json"; }
    [Serializable] public class Gates { public long minBytes = 20000; public double maxMB = 64, maxBakeMinutes = 12; public int maxCasters = 2500; public long hardTriCap = 6000000; }
    [Serializable] public class Caster { public float marginM = 12f, minSizeM = 0.6f, minLodTris = 24; public long triBudget = 2000000; public bool onePerLodGroup = true; }
    [Serializable] public class Plan
    {
        public bool enabled = true; public string profile = "fast";
        public string scene = "Assets/Scenes/HadesSlice.unity", bakingSetName = "HadesSlice_APV", outputDir = "Assets/Scenes/ApvBakes/hadesslice", lightmapper = "ProgressiveCPU", backupDir = "Tools", stampFile = "Assets/Data/apv_bake_stamp.json";
        public int directSamples = 32, indirectSamples = 256, bounces = 2;
        public Density densityFinal = new Density(), densityFast = new Density { minDistanceBetweenProbes = 2f };
        public Vol[] volumes = new Vol[0]; public Caster caster = new Caster();
        public string[] alwaysRoots = new string[0], hideRows = new string[0], staticRoots = new string[0], excludeNodes = new string[0], lightsMixed = new string[0], stampInputs = new string[0];
        public int activateDepth = 2; public bool setReceiveGiProbes = true;
        public StaticSky staticSky = new StaticSky(); public Gates gates = new Gates();
    }
    [Serializable] class UltraSky { public float[] top, mid, bottom; public float exposure = 0f, hdriExposure = 10f; }
    [Serializable] class UltraRoot { public UltraSky sky; }
    [Serializable] class Restore { public string scene = ""; public List<string> paths = new List<string>(), flagPaths = new List<string>(); public List<int> flagVals = new List<int>(); }
    [Serializable] class Stamp { public string bakedAt = "", profile = "", zones = ""; public List<string> inputs = new List<string>(), md5 = new List<string>(); public long bytes; public int cells; public long probes; }

    const string ReportPath = "Logs/apvz_report.json", RestorePath = "Logs/apvz_restore.json", VolPrefix = "APV_", SkyName = "APV Static Lighting Sky";
    const StaticEditorFlags BakeFlags = StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic;
    static Plan P; static string setPath; static System.Diagnostics.Stopwatch clock; static readonly List<string> notes = new List<string>(); static int ticks, madeVolumes;
    static Restore restore = new Restore(); static bool batchMode, bakeDone, isDry; static List<Vol> chosen = new List<Vol>(); static string zoneArg = "";

    static void Note(string s) { notes.Add(s); Debug.Log("[ApvBakeZone] " + s); }
    static string Arg(string name) { var a = Environment.GetCommandLineArgs(); for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1]; return null; }

    static Plan Load(out string err)
    {
        err = null; string p = Path.Combine(Application.dataPath, "Data/apv_zones.json"); if (!File.Exists(p)) { err = "Assets/Data/apv_zones.json not found (run apply_apvzone.py --apply first)"; return null; }
        try
        {
            var pl = JsonUtility.FromJson<Plan>(File.ReadAllText(p, Encoding.UTF8));
            if (pl == null) { err = "apv_zones.json empty"; return null; }
            var pr = Arg("-apvProfile"); if (!string.IsNullOrEmpty(pr)) pl.profile = pr;
            if (pl.profile != "final" && pl.profile != "fast") { err = "profile must be final or fast"; return null; }
            foreach (var v in pl.volumes) if (v == null || v.center == null || v.size == null || v.center.Length < 3 || v.size.Length < 3 || v.size[0] <= 0 || v.size[1] <= 0 || v.size[2] <= 0) { err = "a volume row is malformed"; return null; }
            return pl;
        }
        catch (Exception e) { err = "apv_zones.json unreadable: " + e.Message; return null; }
    }

    static bool Match(string pat, string name)
    {
        if (string.IsNullOrEmpty(pat)) return false;
        if (pat.StartsWith("*") && pat.EndsWith("*") && pat.Length > 2) return name.IndexOf(pat.Substring(1, pat.Length - 2), StringComparison.Ordinal) >= 0;
        if (pat.EndsWith("*")) return name.StartsWith(pat.Substring(0, pat.Length - 1), StringComparison.Ordinal);
        if (pat.StartsWith("*")) return name.EndsWith(pat.Substring(1), StringComparison.Ordinal);
        return name == pat;
    }
    static bool Any(string[] pats, string name) { if (pats != null) foreach (var p in pats) if (Match(p, name)) return true; return false; }

    [MenuItem("Frankendom/APV/Zone bake: dry run (counts only)")] public static void MenuDry() { Run(true); }
    [MenuItem("Frankendom/APV/Zone bake: bake zones in -apvZone / first volume")] public static void Menu() { Run(false); }
    [MenuItem("Frankendom/APV/Restore (strays, zone roots, static flags)")] public static void MenuRestore() { RestoreAll(true); }
    public static void RunBatch() { Run(Arg("-apvMode") == "dry"); }

    static void Fail(int code, string why)
    {
        Debug.LogError("[ApvBakeZone] FAIL " + code + ": " + why);
        try { if (!isDry) RestoreAll(!bakeDone); } catch (Exception e) { Debug.LogError("[ApvBakeZone] restore after failure threw: " + e.Message); }
        try { WriteReport(code, why, 0, 0, 0, 0); } catch { }
        if (batchMode) EditorApplication.Exit(code);
    }

    static string PathOf(Transform t) { var l = new List<int>(); for (var c = t; c != null; c = c.parent) l.Insert(0, c.GetSiblingIndex()); return string.Join(".", l.Select(i => i.ToString()).ToArray()); }
    static Transform FindByPath(Scene sc, string path)
    {
        var idx = path.Split('.').Select(int.Parse).ToArray(); var roots = sc.GetRootGameObjects(); if (idx[0] >= roots.Length) return null; var t = roots[idx[0]].transform;
        for (int i = 1; i < idx.Length; i++) { if (idx[i] >= t.childCount) return null; t = t.GetChild(idx[i]); }
        return t;
    }

    // delete APV_* volumes, the static sky object (all rebuilt by a bake run). Returns the number removed.
    static int KillStrays(Scene sc)
    {
        int n = 0;
        foreach (var root in sc.GetRootGameObjects().ToArray())
        {
            foreach (var pv in root.GetComponentsInChildren<ProbeVolume>(true).ToArray()) if (pv != null && pv.gameObject.name.StartsWith(VolPrefix)) { UnityEngine.Object.DestroyImmediate(pv.gameObject); n++; }
        }
        foreach (var root in sc.GetRootGameObjects().ToArray()) if (root != null && (root.name.StartsWith("APV_BakeProxies") || root.name == SkyName)) { UnityEngine.Object.DestroyImmediate(root); n++; }
        return n;
    }

    // roots to switch on for this run: the chosen volumes' roots + alwaysRoots. Ring groups and rows (depth 2) too, hideRows stay off. Every switched object is recorded.
    static int ActivateZones(Scene sc, bool dry)
    {
        var want = new List<string>(P.alwaysRoots); foreach (var v in chosen) if (!string.IsNullOrEmpty(v.root)) want.Add(v.root);
        int changed = 0, kept = 0;
        foreach (var root in sc.GetRootGameObjects())
        {
            if (!want.Contains(root.name)) continue;
            var stack = new List<KeyValuePair<Transform, int>> { new KeyValuePair<Transform, int>(root.transform, 0) };
            while (stack.Count > 0)
            {
                var cur = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); var t = cur.Key;
                if (!t.gameObject.activeSelf)
                {
                    if (cur.Value > 0 && Any(P.hideRows, t.name)) { kept++; continue; }
                    changed++; if (!dry) { restore.paths.Add(PathOf(t)); t.gameObject.SetActive(true); }
                }
                if (cur.Value < P.activateDepth) for (int i = 0; i < t.childCount; i++) stack.Add(new KeyValuePair<Transform, int>(t.GetChild(i), cur.Value + 1));
            }
        }
        Note("2 zone activation (" + string.Join("+", want.ToArray()) + "): " + changed + " inactive object(s) " + (dry ? "would be switched on" : "switched on for the bake") + ", " + kept + " hideRows kept off");
        return changed;
    }

    // Restore: strays (only when strays=true, i.e. before a successful bake), inactive roots, static flags. Saves the scene when anything changed.
    static void RestoreAll(bool strays)
    {
        Restore r = restore;
        if ((r == null || (r.paths.Count == 0 && r.flagPaths.Count == 0)) && File.Exists(RestorePath)) r = JsonUtility.FromJson<Restore>(File.ReadAllText(RestorePath));
        var sc = SceneManager.GetActiveScene(); string want = r != null && !string.IsNullOrEmpty(r.scene) ? r.scene : (P != null ? P.scene : "Assets/Scenes/HadesSlice.unity");
        if (!sc.path.EndsWith(Path.GetFileName(want))) sc = EditorSceneManager.OpenScene(want, OpenSceneMode.Single);
        int off = 0, fl = 0, kill = 0;
        if (r != null)
        {
            foreach (var p in r.paths) { var t = FindByPath(sc, p); if (t != null && t.gameObject.activeSelf) { t.gameObject.SetActive(false); off++; } }
            for (int i = 0; i < r.flagPaths.Count; i++) { var t = FindByPath(sc, r.flagPaths[i]); if (t == null) continue; var go = t.gameObject; var cur = GameObjectUtility.GetStaticEditorFlags(go); var orig = (StaticEditorFlags)r.flagVals[i]; var back = (cur & ~BakeFlags) | (orig & BakeFlags); if (back != cur) { GameObjectUtility.SetStaticEditorFlags(go, back); fl++; } }
        }
        if (strays) kill = KillStrays(sc);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc); AssetDatabase.SaveAssets();
        if (File.Exists(RestorePath)) File.Delete(RestorePath); restore = new Restore();
        Debug.Log("[ApvBakeZone] restored: " + off + " object(s) back to inactive, " + fl + " static-flag set(s) put back, " + kill + " stray APV object(s) deleted, scene saved");
    }

    static void Run(bool dry)
    {
        clock = System.Diagnostics.Stopwatch.StartNew(); batchMode = Application.isBatchMode; notes.Clear(); bakeDone = false; isDry = dry;
        try
        {
            string err; P = Load(out err); if (P == null) { Fail(2, err); return; }
            if (!P.enabled && !dry) { Note("plan enabled=false: nothing to do"); if (batchMode) EditorApplication.Exit(0); return; }
            if (!File.Exists(P.scene)) { Fail(3, "scene not found: " + P.scene); return; }
            if (File.Exists(RestorePath) && !dry) { Fail(2, RestorePath + " exists: a previous run did not restore. Run Frankendom > APV > Restore first"); return; }
            zoneArg = Arg("-apvZone"); if (string.IsNullOrEmpty(zoneArg)) zoneArg = (P.volumes.FirstOrDefault(v => v.enabled) ?? new Vol()).id;
            var ids = zoneArg == "all" ? P.volumes.Where(v => v.enabled).Select(v => v.id).ToList() : zoneArg.Split(',').Select(s => s.Trim()).ToList();
            chosen = P.volumes.Where(v => ids.Contains(v.id) || ids.Contains(v.zone)).ToList();
            if (chosen.Count == 0 || chosen.Count != ids.Count) { Fail(2, "-apvZone '" + zoneArg + "' does not match volume ids in the plan: " + string.Join(",", P.volumes.Select(v => v.id).ToArray())); return; }
            Note("1 zones: " + string.Join(",", chosen.Select(v => v.id).ToArray()) + ", profile " + P.profile + ", mode " + (dry ? "dry" : "bake"));
            string proj = Directory.GetParent(Application.dataPath).FullName;
            if (!dry)
            {
                string bk = Path.Combine(proj, P.backupDir, "_backup_apvz_" + DateTime.Now.ToString("yyyyMMdd_HHmm")); Directory.CreateDirectory(bk);
                File.Copy(P.scene, Path.Combine(bk, Path.GetFileName(P.scene)), true); if (File.Exists(P.scene + ".meta")) File.Copy(P.scene + ".meta", Path.Combine(bk, Path.GetFileName(P.scene) + ".meta"), true);
                Note("1 backup -> " + bk);
            }
            var sc = EditorSceneManager.OpenScene(P.scene, OpenSceneMode.Single);
            int strayN = UnityEngine.Object.FindObjectsByType<ProbeVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None).Count(p => p.gameObject.name.StartsWith(VolPrefix));
            Note("2 scene " + sc.path + ": " + strayN + " APV_* volume(s) from earlier runs " + (dry ? "(would be deleted)" : "(deleted)") + ", " + UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length + " reflection probe(s)");
            restore = new Restore { scene = P.scene };
            if (!dry) KillStrays(sc);
            ActivateZones(sc, dry);
            if (!dry) { Directory.CreateDirectory("Logs"); File.WriteAllText(RestorePath, JsonUtility.ToJson(restore)); }   // roots recorded BEFORE any scene save
            long tris; int casters = SelectCasters(sc, dry, out tris);
            if (casters == 0) { Fail(9, "0 casters selected: the bake would see no geometry (roots inactive? volume bounds wrong?)"); return; }
            if (casters > P.gates.maxCasters || tris > P.gates.hardTriCap) { Fail(9, casters + " casters / " + tris + " triangles over the hard cap (" + P.gates.maxCasters + " / " + P.gates.hardTriCap + "): raise caster.minSizeM or lower caster.triBudget"); return; }
            int mixed = SetLights(dry);
            if (dry) { Note("DRY RUN done: nothing changed (" + casters + " casters, " + tris + " triangles, " + mixed + " lights would go Mixed)"); WriteReport(0, "dry", 0, 0, 0, 0); if (batchMode) EditorApplication.Exit(0); return; }
            madeVolumes = PlaceVolumes(sc);
            BuildStaticSky(sc); ApplyLightingSettings(sc);
            EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
            if (!SetupBakingSet(sc)) { Fail(7, "baking set could not be set up (see lines above)"); return; }
            AssetDatabase.SaveAssets();
            ticks = 0; EditorApplication.update += StartTick;   // BakeAsync needs HDRP's pipeline instance: start from the update loop
        }
        catch (Exception e) { Fail(8, "exception: " + e.Message + "\n" + e.StackTrace); }
    }

    static string Fmt(float[] a) { return "(" + a[0].ToString("0.#") + ", " + a[1].ToString("0.#") + ", " + a[2].ToString("0.#") + ")"; }

    static bool Dynamic(Transform t) { for (var p = t; p != null; p = p.parent) { if (p.GetComponent<Animator>() != null || p.GetComponent<Rigidbody>() != null || p.GetComponent<SkinnedMeshRenderer>() != null) return true; if (Any(P.excludeNodes, p.name)) return true; } return false; }

    static long Tris(Mesh m, Dictionary<Mesh, long> cache)
    {
        if (m == null) return 0; long n; if (cache.TryGetValue(m, out n)) return n; n = 0;
        for (int s = 0; s < m.subMeshCount; s++) n += m.GetIndexCount(s) / 3; cache[m] = n; return n;
    }
    static long RTris(Renderer r, Dictionary<Mesh, long> cache) { var mf = r.GetComponent<MeshFilter>(); return mf != null ? Tris(mf.sharedMesh, cache) : 0; }

    // ONE LOD per group: the lowest-poly LOD that still has at least minLodTris triangles (billboards / culled LODs skipped). Index of that LOD, or -1.
    static int PickLod(LODGroup g, Dictionary<Mesh, long> cache)
    {
        var lods = g.GetLODs();
        for (int i = lods.Length - 1; i >= 0; i--) { long t = 0; foreach (var r in lods[i].renderers) if (r != null) t += RTris(r, cache); if (t >= (long)P.caster.minLodTris) return i; }
        return -1;
    }
    static bool InLod(LODGroup g, Renderer mr, int lod) { var lods = g.GetLODs(); if (lod < 0 || lod >= lods.Length) return false; foreach (var r in lods[lod].renderers) if (r == mr) return true; return false; }

    class Cand { public MeshRenderer mr; public long tris; public float diag; public int vol; }

    static int SelectCasters(Scene sc, bool dry, out long totalTris)
    {
        var cache = new Dictionary<Mesh, long>(); var lodPick = new Dictionary<LODGroup, int>(); var cands = new List<Cand>(); var receivers = new List<MeshRenderer>();
        var boxes = chosen.Select(v => new Bounds(new Vector3(v.center[0], v.center[1], v.center[2]), new Vector3(v.size[0], v.size[1], v.size[2]))).ToArray();
        var grown = boxes.Select(b => { var g = b; g.Expand(P.caster.marginM * 2f); return g; }).ToArray();
        int all = 0, off = 0, inactive = 0, dynamicN = 0, outside = 0, small = 0, lodSkip = 0;
        foreach (var root in sc.GetRootGameObjects())
        {
            if (!Any(P.staticRoots, root.name)) continue;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                all++;
                var f = GameObjectUtility.GetStaticEditorFlags(mr.gameObject);
                if (!dry) { restore.flagPaths.Add(PathOf(mr.transform)); restore.flagVals.Add((int)f); GameObjectUtility.SetStaticEditorFlags(mr.gameObject, f & ~StaticEditorFlags.ContributeGI); off++; }   // nobody casts unless selected below
                if (!mr.gameObject.activeInHierarchy) { inactive++; continue; }
                if (Dynamic(mr.transform)) { dynamicN++; continue; }
                var b = mr.bounds; int vi = -1; for (int i = 0; i < grown.Length; i++) if (grown[i].Intersects(b)) { vi = i; break; }
                if (vi < 0) { outside++; continue; }
                if (boxes.Any(x => x.Intersects(b))) receivers.Add(mr);
                float diag = b.size.magnitude; if (diag < P.caster.minSizeM) { small++; continue; }
                if (P.caster.onePerLodGroup) { var lg = mr.GetComponentInParent<LODGroup>(); if (lg != null) { int pick; if (!lodPick.TryGetValue(lg, out pick)) { pick = PickLod(lg, cache); lodPick[lg] = pick; } if (!InLod(lg, mr, pick)) { lodSkip++; continue; } } }
                cands.Add(new Cand { mr = mr, tris = RTris(mr, cache), diag = diag, vol = vi });
            }
        }
        long total = cands.Sum(c => c.tris); int dropped = 0;
        if (total > P.caster.triBudget)
        {
            foreach (var c in cands.OrderByDescending(c => c.tris / Mathf.Max(0.25f, c.diag * c.diag)).ToList()) { if (total <= P.caster.triBudget) break; total -= c.tris; cands.Remove(c); dropped++; }   // densest-per-area first: heavy small clutter, never the big ground/walls first
        }
        Note("3 casters: " + all + " renderers under staticRoots; skipped " + inactive + " inactive, " + dynamicN + " dynamic/excluded, " + outside + " outside the volumes + " + P.caster.marginM + " m, " + small + " smaller than " + P.caster.minSizeM + " m, " + lodSkip + " other LODs; " + dropped + " dropped for the triangle budget " + P.caster.triBudget + " -> " + cands.Count + " casters, " + total + " triangles");
        for (int i = 0; i < chosen.Count; i++)
        {
            var mine = cands.Where(c => c.vol == i).ToList(); var mats = new HashSet<Material>(); double texMB = 0; var seen = new HashSet<Texture>();
            foreach (var c in mine) foreach (var m in c.mr.sharedMaterials) { if (m == null || !mats.Add(m)) continue; Texture t = null; if (m.HasProperty("_BaseColorMap")) t = m.GetTexture("_BaseColorMap"); if (t == null && m.HasProperty("_MainTex")) t = m.GetTexture("_MainTex"); if (t != null && seen.Add(t)) texMB += t.width * (double)t.height * 4 * 1.33 / 1048576.0; }
            Note("3   " + chosen[i].id + ": " + mine.Count + " casters, " + mine.Sum(c => c.tris) + " tris, " + mats.Count + " materials, " + texMB.ToString("0") + " MB base maps if expanded to RGBA8 + mips (calculated)");
        }
        if (!dry)
        {
            foreach (var c in cands) { var go = c.mr.gameObject; GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(go) | StaticEditorFlags.ContributeGI); }
            if (P.setReceiveGiProbes) foreach (var mr in receivers) if (mr.receiveGI != ReceiveGI.LightProbes) mr.receiveGI = ReceiveGI.LightProbes;
            Note("3 flags: ContributeGI cleared on " + off + " renderers, set on " + cands.Count + "; ReceiveGI=LightProbes on " + receivers.Count + " receivers inside the volumes");
        }
        totalTris = total; return cands.Count;
    }

    static int SetLights(bool dry)
    {
        int changed = 0, already = 0; var seen = new List<string>();
        foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            seen.Add(l.name + ":" + l.lightmapBakeType); if (!Any(P.lightsMixed, l.name)) continue;
            if (l.lightmapBakeType == LightmapBakeType.Mixed) { already++; continue; }
            if (l.lightmapBakeType != LightmapBakeType.Realtime) continue;
            changed++; if (!dry) { l.lightmapBakeType = LightmapBakeType.Mixed; EditorUtility.SetDirty(l); }
        }
        Note("4 lights: " + seen.Count + " in the scene, " + already + " of the " + P.lightsMixed.Length + " plan names already Mixed, " + changed + " " + (dry ? "would go" : "set") + " Mixed. Mixed in the scene: " + string.Join(", ", seen.Where(s => s.EndsWith("Mixed")).ToArray()));
        return changed;
    }

    static int PlaceVolumes(Scene sc)
    {
        int n = 0;
        foreach (var v in chosen)
        {
            var go = new GameObject(VolPrefix + v.id); SceneManager.MoveGameObjectToScene(go, sc);
            go.transform.position = new Vector3(v.center[0], v.center[1], v.center[2]);
            var pv = go.AddComponent<ProbeVolume>(); pv.mode = ProbeVolume.Mode.Local; pv.size = new Vector3(v.size[0], v.size[1], v.size[2]); n++;
            Note("5 probe volume " + go.name + " centre " + Fmt(v.center) + " size " + Fmt(v.size));
        }
        return n;
    }

    static void BuildStaticSky(Scene sc)
    {
        if (P.staticSky.mode == "off") { Note("5 static lighting sky: off by plan, probes will bake a BLACK sky (Armagedom measured -12..-16 luma)"); return; }
        UltraSky u = null; try { if (File.Exists(P.staticSky.ultraFile)) { var r = JsonUtility.FromJson<UltraRoot>(File.ReadAllText(P.staticSky.ultraFile)); if (r != null) u = r.sky; } } catch (Exception e) { Note("ultra sky read failed: " + e.Message); }
        float ev = P.staticSky.hdriExposure > -900f ? P.staticSky.hdriExposure : (u != null ? u.hdriExposure : 10f);
        VolumeProfile found = null; HDRISky srcHdri = null; float best = float.MinValue;
        foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var sp = v.sharedProfile; if (!v.isGlobal || sp == null || v.priority < best) continue; HDRISky hs;
            if (sp.TryGet(out hs) && hs.hdriSky != null && hs.hdriSky.value != null) { found = sp; srcHdri = hs; best = v.priority; }
        }
        string dir = P.outputDir.TrimEnd('/'); Directory.CreateDirectory(dir); string path = dir + "/ApvStaticSky.asset";
        var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path); if (prof == null) { prof = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(prof, path); }
        prof.components.Clear(); int skyId; string how;
        if (srcHdri != null && P.staticSky.mode != "gradient")
        {
            var ve = prof.Add<VisualEnvironment>(true); ve.skyType.Override((int)SkyType.HDRI); ve.skyAmbientMode.Override(SkyAmbientMode.Static);
            var h = prof.Add<HDRISky>(true); h.hdriSky.Override(srcHdri.hdriSky.value); h.exposure.Override(ev); h.rotation.Override(srcHdri.rotation.value);
            skyId = (int)SkyType.HDRI; how = "HDRI " + srcHdri.hdriSky.value.name + " exposure " + ev + " (from a scene Volume)";
        }
        else
        {
            if (u == null || u.top == null || u.mid == null || u.bottom == null || u.top.Length < 3 || u.mid.Length < 3 || u.bottom.Length < 3) { Note("WARN static sky: no HDRI volume and no usable sky block in " + P.staticSky.ultraFile + ": probes bake a black sky"); return; }
            var ve = prof.Add<VisualEnvironment>(true); ve.skyType.Override((int)SkyType.Gradient); ve.skyAmbientMode.Override(SkyAmbientMode.Static);
            var g = prof.Add<GradientSky>(true); g.top.Override(new Color(u.top[0], u.top[1], u.top[2])); g.middle.Override(new Color(u.mid[0], u.mid[1], u.mid[2])); g.bottom.Override(new Color(u.bottom[0], u.bottom[1], u.bottom[2]));
            float mul = P.staticSky.gradientMultiplier > 0f ? P.staticSky.gradientMultiplier : Mathf.Pow(2f, ev) / 1.2f; g.multiplier.Override(mul); g.gradientDiffusion.Override(3.5f);
            skyId = (int)SkyType.Gradient; how = "Gradient multiplier " + mul.ToString("0");
        }
        EditorUtility.SetDirty(prof); AssetDatabase.SaveAssets();
        var go = new GameObject(SkyName); SceneManager.MoveGameObjectToScene(go, sc); var sl = go.AddComponent<StaticLightingSky>(); sl.profile = prof; sl.staticLightingSkyUniqueID = skyId;
        Note("5 static lighting sky: " + how);
    }

    static void ApplyLightingSettings(Scene sc)
    {
        LightingSettings ls;
        if (!Lightmapping.TryGetLightingSettings(out ls) || ls == null)
        {
            ls = new LightingSettings { name = sc.name + " Lighting" }; string dir = P.outputDir.TrimEnd('/'); Directory.CreateDirectory(dir);
            AssetDatabase.CreateAsset(ls, dir + "/" + sc.name + " LightingSettings.lighting"); Lightmapping.lightingSettings = ls;
        }
        ls.lightmapper = P.lightmapper == "ProgressiveGPU" ? LightingSettings.Lightmapper.ProgressiveGPU : LightingSettings.Lightmapper.ProgressiveCPU;
        ls.directSampleCount = P.directSamples; ls.indirectSampleCount = P.indirectSamples; ls.maxBounces = P.bounces; ls.autoGenerate = false; ls.bakedGI = true; EditorUtility.SetDirty(ls);
        Note("6 lighting settings: " + P.lightmapper + ", direct " + P.directSamples + ", indirect " + P.indirectSamples + ", bounces " + P.bounces);
    }

    static void SetBool(SerializedObject so, string path, bool v) { var p = so.FindProperty(path); if (p == null) { Note("WARN property " + path + " not found (package changed?): left at its default"); return; } p.boolValue = v; }

    static bool SetupBakingSet(Scene sc)
    {
        try
        {
            setPath = P.outputDir.TrimEnd('/') + "/" + P.bakingSetName + ".asset"; Directory.CreateDirectory(Path.GetDirectoryName(setPath));
            var set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(setPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>(); set.name = P.bakingSetName;
                var sd = typeof(ProbeVolumeBakingSet).GetMethod("SetDefaults", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (sd != null) sd.Invoke(set, null); else Note("WARN SetDefaults not found");
                AssetDatabase.CreateAsset(set, setPath); Note("6 created baking set " + setPath);
            }
            else Note("6 reusing baking set " + setPath);
            var d = P.profile == "fast" ? P.densityFast : P.densityFinal; var so = new SerializedObject(set);
            so.FindProperty("singleSceneMode").boolValue = false; so.FindProperty("minDistanceBetweenProbes").floatValue = d.minDistanceBetweenProbes; so.FindProperty("simplificationLevels").intValue = d.simplificationLevels; so.FindProperty("skyOcclusion").boolValue = d.skyOcclusion;
            SetBool(so, "settings.virtualOffsetSettings.useVirtualOffset", d.virtualOffset); SetBool(so, "settings.dilationSettings.enableDilation", d.dilation); so.ApplyModifiedPropertiesWithoutUndo();
            if (!set.TryAddScene(AssetDatabase.AssetPathToGUID(sc.path))) Note("6 scene already in a baking set: keeping the existing assignment");
            EditorUtility.SetDirty(set); AssetDatabase.SaveAssets();
            var sync = typeof(ProbeVolumeBakingSet).GetMethod("SyncBakingSets", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public); if (sync != null) sync.Invoke(null, null); else Note("WARN SyncBakingSets not found");
            Note("6 density (" + P.profile + "): min probe distance " + d.minDistanceBetweenProbes + " m, simplification levels " + d.simplificationLevels + ", virtual offset " + d.virtualOffset + ", dilation " + d.dilation);
            return true;
        }
        catch (Exception e) { Note("baking set setup failed: " + e.Message); return false; }
    }

    static void ForcePipeline()
    {
        var cam = Camera.main; GameObject tmp = null; if (cam == null) { tmp = new GameObject("ApvBakeCam"); cam = tmp.AddComponent<Camera>(); }
        try { cam.Render(); } catch (Exception e) { Note("camera render to create the pipeline threw: " + e.Message); }
        if (tmp != null) UnityEngine.Object.DestroyImmediate(tmp);
    }

    const int MaxStartTicks = 120;
    static void StartTick()
    {
        try
        {
            ticks++; var prv = ProbeReferenceVolume.instance;
            if (!prv.isInitialized) { if (ticks == 1 || ticks % 20 == 0) ForcePipeline(); if (ticks < MaxStartTicks) return; EditorApplication.update -= StartTick; Fail(5, "ProbeReferenceVolume never initialised, so no bake can start"); return; }
            EditorApplication.update -= StartTick; Note("6 probe system ready after " + ticks + " editor ticks");
            clock.Reset(); clock.Start();   // the time limit counts the BAKE, not the Editor start
            if (!AdaptiveProbeVolumes.BakeAsync()) { Fail(5, "AdaptiveProbeVolumes.BakeAsync did not start"); return; }
            Note("6 bake started " + DateTime.Now.ToString("HH:mm:ss")); EditorApplication.update += Poll;
        }
        catch (Exception e) { EditorApplication.update -= StartTick; Fail(8, "exception while starting the bake: " + e.Message); }
    }

    static void Poll()
    {
        try
        {
            if (AdaptiveProbeVolumes.isRunning)
            {
                if (clock.Elapsed.TotalMinutes > P.gates.maxBakeMinutes) { AdaptiveProbeVolumes.Cancel(); EditorApplication.update -= Poll; Fail(4, "bake cancelled after " + P.gates.maxBakeMinutes + " min (gates.maxBakeMinutes): STALL RULE, see READY.md section 6"); }
                return;
            }
            EditorApplication.update -= Poll; Finish();
        }
        catch (Exception e) { EditorApplication.update -= Poll; Fail(8, "exception while polling: " + e.Message); }
    }

    static string Md5(string f) { if (!File.Exists(f)) return "missing"; using (var m = MD5.Create()) return BitConverter.ToString(m.ComputeHash(File.ReadAllBytes(f))).Replace("-", "").Substring(0, 12); }

    static void Finish()
    {
        var set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(setPath); bool baked = set != null && set.HasBakedData();
        double bakeMin = clock.Elapsed.TotalMinutes; bakeDone = baked;
        RestoreAll(!baked);   // roots back to inactive, ContributeGI back to the original, scene saved; strays deleted only if nothing was baked
        AssetDatabase.Refresh();
        string dir = Path.GetDirectoryName(setPath).Replace('\\', '/'); long bytes = 0; var files = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) { if (f.EndsWith(".meta")) continue; var n = new FileInfo(f).Length; bytes += n; files.Add(f.Replace('\\', '/') + " " + n + " B"); }
        int cells = 0; long probes = 0;
        try
        {
            var fld = typeof(ProbeVolumeBakingSet).GetField("cellDescs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); var dict = fld != null && set != null ? fld.GetValue(set) as IDictionary : null;
            if (dict != null) foreach (var cd in dict.Values) { cells++; var pf = cd.GetType().GetField("probeCount"); if (pf != null) probes += Convert.ToInt64(pf.GetValue(cd)); }
        }
        catch (Exception e) { Note("WARN probe count by reflection failed: " + e.Message); }
        double mb = bytes / 1048576.0;
        Note("7 bake done in " + bakeMin.ToString("0.0") + " min (bake only); baked data " + baked + ", " + cells + " cell(s), " + probes + " probe(s), " + mb.ToString("0.00") + " MB in " + files.Count + " files (maxMB " + P.gates.maxMB + ")"); foreach (var f in files) Note("  " + f);
        int code = 0; string why = "";
        if (!baked || bytes < P.gates.minBytes) { code = 7; why = "no baked data after the bake (" + bytes + " bytes)"; }
        else if (mb > P.gates.maxMB) { code = 6; why = "baked data " + mb.ToString("0.00") + " MB over gates.maxMB " + P.gates.maxMB + ": use profile fast or shrink a volume, rebake"; }
        if (code == 0) WriteStamp(bytes, cells, probes);
        WriteReport(code, why, bytes, files.Count, cells, probes);
        if (code != 0) Debug.LogError("[ApvBakeZone] FAIL " + code + ": " + why); else Debug.Log("[ApvBakeZone] OK");
        if (batchMode) EditorApplication.Exit(code);
    }

    static void WriteStamp(long bytes, int cells, long probes)
    {
        try
        {
            var s = new Stamp { bakedAt = DateTime.Now.ToString("s"), profile = P.profile, zones = string.Join(",", chosen.Select(v => v.id).ToArray()), bytes = bytes, cells = cells, probes = probes };
            var ins = new List<string>(P.stampInputs); foreach (var v in chosen) if (!string.IsNullOrEmpty(v.layoutFile) && !ins.Contains(v.layoutFile)) ins.Add(v.layoutFile);
            foreach (var i in ins) { s.inputs.Add(i); s.md5.Add(Md5(i)); }
            File.WriteAllText(P.stampFile, JsonUtility.ToJson(s, true)); Note("7 stamp -> " + P.stampFile);
        }
        catch (Exception e) { Note("WARN stamp not written: " + e.Message); }
    }

    static void WriteReport(int code, string why, long bytes, int files, int cells, long probes)
    {
        Directory.CreateDirectory("Logs"); var sb = new StringBuilder();
        sb.Append("{\"exit\":" + code + ",\"why\":\"" + why.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ") + "\",\"zones\":\"" + zoneArg + "\",\"minutes\":" + (clock != null ? clock.Elapsed.TotalMinutes : 0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(",\"bytes\":" + bytes + ",\"files\":" + files + ",\"cells\":" + cells + ",\"probes\":" + probes + ",\"volumes\":" + madeVolumes + ",\"profile\":\"" + (P != null ? P.profile : "") + "\",\"notes\":[");
        sb.Append(string.Join(",", notes.Select(n => "\"" + n.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ") + "\"").ToArray())); sb.Append("]}");
        File.WriteAllText(ReportPath, sb.ToString());
    }
}
#endif
