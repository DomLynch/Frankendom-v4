#if UNITY_EDITOR
// Integration of the MetaHuman head (owner = mhhead_attach.json fighterName) into HadesSlice (HDRP). Data: Assets/Data/mhhead_attach.json. Batch entry points (never open the interactive Editor for these):
//   1) -executeMethod MHIntegrate.Prepare   MH_HDRP define (Standalone), mhrig.dll importer (Win x64 only), hair shader graph into Always Included Shaders. Exits 0.
//   2) -executeMethod MHIntegrate.Run       (needs step 1 compiled) RebuildAllFromCanonical, diffusion profile registration, HDRP asset SSS on, head object on the fighterName character's head bone, scene saved.
//   3) -executeMethod MHIntegrate.Verify    read-only check of the saved scene + settings, prints [MHINTEGRATE] PASS/FAIL lines, exit 3 on FAIL.
// Exit codes: 0 ok, 2 prerequisite missing (define / files), 3 verify FAIL, 4 exception.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MetaHumanRig;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

public static class MHIntegrate
{
    const string Define = "MH_HDRP";
    const string HdrpFolder = "Assets/MetaHumanHead/HDRP";
    const string ProfilePath = HdrpFolder + "/MH_Skin_DiffusionProfile.asset";
    const string PluginPath = "Assets/MetaHumanHead/Plugins/x86_64/mhrig.dll";
    const string HairShaderPath = "Assets/MetaHumanHead/Shaders/MH_HairCards.shadergraph";
    const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly StringBuilder Rep = new StringBuilder();

    static void L(string s) { Rep.AppendLine(s); Debug.Log("[MHINTEGRATE] " + s); }
    static void Done(int code, string name)
    {
        try { Directory.CreateDirectory("TestOut"); File.WriteAllText("TestOut/mhintegrate_" + name + ".txt", Rep.ToString()); } catch { }
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }
    static bool HasDefine() { return Array.IndexOf((PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone) ?? "").Split(';'), Define) >= 0; }

    // ---------------------------------------------------------------- step 1
    public static void Prepare()
    {
        Rep.Length = 0;
        try
        {
            if (!HasDefine())
            {
                string cur = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone) ?? "";
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, string.IsNullOrEmpty(cur) ? Define : cur + ";" + Define);
                L("added scripting define " + Define + " (Standalone)");
            }
            else L("scripting define " + Define + " already set");

            var pi = AssetImporter.GetAtPath(PluginPath) as PluginImporter;
            if (pi == null) L("WARNING plugin importer not found at " + PluginPath + " (file not imported yet?)");
            else
            {
                pi.SetCompatibleWithAnyPlatform(false); pi.SetCompatibleWithEditor(true); pi.SetEditorData("CPU", "x86_64"); pi.SetEditorData("OS", "Windows");
                pi.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true); pi.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
                pi.SaveAndReimport(); L("mhrig.dll importer: Editor + Windows x64 only");
            }

            var sh = AssetDatabase.LoadAssetAtPath<Shader>(HairShaderPath) ?? Shader.Find("Shader Graphs/MH_HairCards");
            if (sh == null) L("WARNING hair shader graph not importable yet (" + HairShaderPath + "); hair falls back to the dark placeholder");
            else L(AddAlwaysIncluded(sh) ? "hair shader '" + sh.name + "' added to Graphics > Always Included Shaders" : "hair shader '" + sh.name + "' already in Always Included Shaders");

            AssetDatabase.SaveAssets(); L("Prepare done. Scripts recompile on the next editor start; run MHIntegrate.Run next.");
            Done(0, "prepare");
        }
        catch (Exception e) { L("EXCEPTION " + e); Done(4, "prepare"); }
    }

    static bool AddAlwaysIncluded(Shader s)
    {
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset"); if (gs == null || gs.Length == 0) return false;
        var so = new SerializedObject(gs[0]); var arr = so.FindProperty("m_AlwaysIncludedShaders"); if (arr == null) return false;
        for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == s) return false;
        arr.arraySize++; arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = s; so.ApplyModifiedPropertiesWithoutUndo(); return true;
    }

    // ---------------------------------------------------------------- step 2
    // the look file the head builder reads: StreamingAssets/<builder.dataFolder>/<builder.lookFile> (mhhead_attach.json)
    public static string LookAsset(MHAttachData cfg) { return "Assets/StreamingAssets/" + cfg.builder.dataFolder + "/" + cfg.builder.lookFile; }
    public static string LookAsset() { return LookAsset(MHAttachData.Load()); }

    public static void Run()
    {
        Rep.Length = 0;
        try
        {
            var cfg = MHAttachData.Load();
            if (!HasDefine()) { L("FAIL define " + Define + " missing: run MHIntegrate.Prepare first (then Run in a new editor start so scripts recompile)"); Done(2, "run"); return; }
            if (!File.Exists(LookAsset(cfg))) { L("FAIL Assets/StreamingAssets/MetaHumanHead is missing (data copy not done)"); Done(2, "run"); return; }
            Backup(cfg.scene);

            // canonical folder: MHRebuildAll reads -mhcanon from the command line (default = MHRebuildAll.DefaultCanon); the data file only documents/validates it
            bool hasArg = Array.IndexOf(Environment.GetCommandLineArgs(), "-mhcanon") >= 0;
            if (!hasArg && !string.IsNullOrEmpty(cfg.canonFolder) && !string.Equals(Path.GetFullPath(cfg.canonFolder), Path.GetFullPath(MHRebuildAll.DefaultCanon), StringComparison.OrdinalIgnoreCase))
                L("WARNING mhhead_attach.json canonFolder differs from MHRebuildAll.DefaultCanon; pass -mhcanon \"" + cfg.canonFolder + "\" on the command line to use it");
            L("RebuildAllFromCanonical ...");
            MHRebuildAll.RebuildAllFromCanonical();

            RegisterDiffusionProfile();
            EnsureSss();

            var scene = EditorSceneManager.OpenScene(cfg.scene, OpenSceneMode.Single);
            if (!Integrate(cfg, scene)) { Done(2, "run"); return; }
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            L("scene saved: " + cfg.scene + ". Next: MHIntegrate.Verify, then a player build + capture.");
            Done(0, "run");
        }
        catch (Exception e) { L("EXCEPTION " + e); Done(4, "run"); }
    }

    static void Backup(string scenePath)
    {
        string dir = "Tools/_backup_mhhead_" + DateTime.Now.ToString("HHmm"); Directory.CreateDirectory(dir);
        foreach (var f in new[] { scenePath, "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/ProjectSettings.asset", "Assets/HDRP/HDRP_Max.asset", "Assets/HDRP/Global.asset", "Assets/HDRPDefaultResources/DefaultSettingsVolumeProfile.asset" })
            if (File.Exists(f)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        L("backup of scene + settings in " + dir);
    }

    // ---- diffusion profile: HDRP only uses profiles listed in a Volume's Diffusion Profile List / the Global Settings default volume profile
    static VolumeProfile DefaultVolumeProfile()
    {
        try
        {
            var st = typeof(HDRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.HighDefinition.HDRPDefaultVolumeProfileSettings"); if (st == null) return null;
            MethodInfo gen = null; foreach (var mi in typeof(GraphicsSettings).GetMethods(All)) if (mi.Name == "TryGetRenderPipelineSettings" && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 1) { gen = mi; break; }
            if (gen == null) return null; var args = new object[] { null };
            if (!(bool)gen.MakeGenericMethod(st).Invoke(null, args) || args[0] == null) return null;
            return st.GetProperty("volumeProfile", All).GetValue(args[0]) as VolumeProfile;
        }
        catch { return null; }
    }
    static bool InList(VolumeProfile vp, DiffusionProfileSettings p)
    {
        DiffusionProfileList l; if (vp == null || !vp.TryGet(out l) || l.diffusionProfiles == null || l.diffusionProfiles.value == null) return false;
        return Array.IndexOf(l.diffusionProfiles.value, p) >= 0;
    }
    static bool AddToList(VolumeProfile vp, DiffusionProfileSettings p)
    {
        var vu = typeof(HDRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.HighDefinition.VolumeUtils"); var add = vu != null ? vu.GetMethod("TryAddSingleDiffusionProfile", All) : null;
        if (add == null) return false;
        bool ok = (bool)add.Invoke(null, new object[] { vp, p }); EditorUtility.SetDirty(vp); return ok;
    }
    static void RegisterDiffusionProfile()
    {
        var prof = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(ProfilePath);
        if (prof == null) { L("FAIL diffusion profile asset missing: " + ProfilePath); return; }
        var dv = DefaultVolumeProfile(); var gv = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/HDRP/Global.asset");
        foreach (var t in new[] { Tuple.Create("HDRP Global Settings default volume profile", dv), Tuple.Create("scene volume Assets/HDRP/Global.asset", gv) })
        {
            if (t.Item2 == null) { L("diffusion profile: " + t.Item1 + " not found (skipped)"); continue; }
            if (InList(t.Item2, prof)) { L("diffusion profile already listed in " + t.Item1); continue; }
            bool ok = AddToList(t.Item2, prof);
            L("diffusion profile " + (ok && InList(t.Item2, prof) ? "registered in " : "COULD NOT be registered in ") + t.Item1 + (t.Item2 == gv && !InList(t.Item2, prof) ? " (not fatal: lists accumulate across volumes)" : ""));
        }
        AssetDatabase.SaveAssets();
    }

    // ---- HDRP pipeline asset: Subsurface Scattering ON in every HDRenderPipelineAsset in use (SerializedObject, no YAML edit)
    static void EnsureSss()
    {
        var set = new List<HDRenderPipelineAsset>();
        Action<UnityEngine.Rendering.RenderPipelineAsset> add = a => { var h = a as HDRenderPipelineAsset; if (h != null && !set.Contains(h)) set.Add(h); };
        add(GraphicsSettings.defaultRenderPipeline); add(AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>("Assets/HDRP/HDRP_Max.asset"));
        int q0 = QualitySettings.GetQualityLevel(); for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, false); add(QualitySettings.renderPipeline); }
        QualitySettings.SetQualityLevel(q0, false);
        foreach (var a in set)
        {
            bool on = a.currentPlatformRenderPipelineSettings.supportSubsurfaceScattering;
            if (!on)
            {
                var so = new SerializedObject(a); var p = so.FindProperty("m_RenderPipelineSettings.supportSubsurfaceScattering");
                if (p != null) { p.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(a); }
            }
            L("HDRP asset " + a.name + ": supportSubsurfaceScattering=" + a.currentPlatformRenderPipelineSettings.supportSubsurfaceScattering + (on ? " (was on)" : " (turned on)"));
        }
        AssetDatabase.SaveAssets();
    }

    // ---- scene
    static Transform FindFighter(MHAttachData cfg)
    {
        foreach (var f in UnityEngine.Object.FindObjectsByType<Fighter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (f.name.IndexOf(cfg.fighterName, StringComparison.OrdinalIgnoreCase) >= 0 || (f.model != null && f.model.name.IndexOf(cfg.fighterName, StringComparison.OrdinalIgnoreCase) >= 0)) return f.transform;
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>()) if (t.gameObject.scene.IsValid() && t.name == cfg.fighterName) return t;
        return null;
    }
    static Transform FindBone(Transform root, string[] names)
    {
        var all = root.GetComponentsInChildren<Transform>(true);
        foreach (var n in names) foreach (var t in all) if (t.name == n) return t;
        foreach (var t in all) if (t.name.EndsWith("Head", StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
    static Material Mat(string name) { return AssetDatabase.LoadAssetAtPath<Material>(HdrpFolder + "/" + name + ".mat"); }

    static bool Integrate(MHAttachData cfg, UnityEngine.SceneManagement.Scene scene)
    {
        var root = FindFighter(cfg); if (root == null) { L("FAIL fighter '" + cfg.fighterName + "' not found in " + cfg.scene); return false; }
        var bone = FindBone(root, cfg.boneNames); if (bone == null) { L("FAIL head bone not found under " + root.name + " (tried " + string.Join(", ", cfg.boneNames) + ")"); return false; }
        L("fighter=" + root.name + " bone=" + bone.name + " (path " + PathOf(bone, root) + ") boneLossyScale=" + bone.lossyScale.ToString("F3") + " rootLossyScale=" + root.lossyScale.ToString("F3"));

        // existing head object (idempotent)
        Transform mh = null; foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == cfg.objectName) { mh = t; break; }
        if (mh == null) mh = new GameObject(cfg.objectName).transform;
        mh.SetParent(bone, false); mh.localPosition = Vector3.zero; mh.localRotation = Quaternion.identity; mh.localScale = Vector3.one;

        var b = mh.GetComponent<MHHeadBuilder>(); if (b == null) b = mh.gameObject.AddComponent<MHHeadBuilder>();
        b.dataFolder = cfg.builder.dataFolder; b.lookFile = cfg.builder.lookFile; b.lod = cfg.builder.lod; b.useJobs = cfg.builder.useJobs; b.buildHairCards = cfg.builder.buildHairCards;

        // saved HDRP material assets (the player cannot create the diffusion-profile-linked skin material at run time)
        var keep = new List<Material>(); var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(HdrpFolder, "*.mat"))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(f.Replace('\\', '/')); if (m == null) continue; keep.Add(m); string n = m.name.ToLowerInvariant(); sb.Append(m.name).Append(' ');
            if (n.Contains("skin")) b.headMaterial = m;
            else if (n.Contains("teeth")) b.teethMaterial = m;
            else if (n.Contains("shell")) b.eyeshellMaterial = m;
            else if (n.Contains("lash")) b.eyelashMaterial = m;
            else if (n.Contains("saliva")) b.salivaMaterial = m;
            else if (n.Contains("eye")) b.eyeMaterial = m;      // hair/brow cards stay builder-created (per-part atlas); their saved copies are in keepAlive
        }
        L("materials in " + HdrpFolder + ": " + sb + "| head=" + Nm(b.headMaterial) + " teeth=" + Nm(b.teethMaterial) + " eye=" + Nm(b.eyeMaterial) + " eyeshell=" + Nm(b.eyeshellMaterial) + " lash=" + Nm(b.eyelashMaterial) + " saliva=" + Nm(b.salivaMaterial));
        if (b.headMaterial == null) { L("FAIL skin material asset missing after rebuild"); return false; }

        var at = mh.GetComponent<MHHeadAttach>(); if (at == null) at = mh.gameObject.AddComponent<MHHeadAttach>();
        at.bone = bone; at.fighterRoot = root; at.rootToBoneRest = Quaternion.Inverse(root.rotation) * bone.rotation; at.keepAlive = keep.ToArray();
        Renderer src = null;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r.transform.IsChildOf(mh)) continue;
            if (string.Equals(r.name, cfg.layerSourceRenderer, StringComparison.OrdinalIgnoreCase)) { src = r; break; }
        }
        at.layerSource = src;
        if (mh.GetComponent<MHHeadLod>() == null) mh.gameObject.AddComponent<MHHeadLod>();
        at.ApplyPose(cfg.attach);                                       // preview rotation + scale; position is aligned to the body at run time

        int hidden = 0;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r.transform.IsChildOf(mh)) continue;
            foreach (var n in cfg.hideRenderers) if (string.Equals(r.name, n, StringComparison.OrdinalIgnoreCase)) { r.enabled = !cfg.hideOriginalHead; hidden++; L("original renderer '" + r.name + "' (" + r.GetType().Name + ") enabled=" + r.enabled); break; }
        }
        if (hidden == 0) L("WARNING no original head renderer matched " + string.Join(",", cfg.hideRenderers) + "; check the glb mesh node names (the owner's <id>_combat.glb has node 'head')");
        L("created/updated '" + cfg.objectName + "' under " + bone.name + " restRot(root->bone)=" + at.rootToBoneRest.eulerAngles.ToString("F1") + " layerSource=" + (src != null ? src.name : "none"));
        return true;
    }
    static string Nm(UnityEngine.Object o) { return o != null ? o.name : "null"; }
    static string PathOf(Transform t, Transform root) { var s = t.name; for (var p = t.parent; p != null && p != root.parent; p = p.parent) s = p.name + "/" + s; return s; }

    // ---------------------------------------------------------------- step 3
    public static void Verify()
    {
        Rep.Length = 0; bool fail = false;
        Action<bool, string> chk = (ok, s) => { Rep.AppendLine((ok ? "PASS " : "FAIL ") + s); Debug.Log("[MHINTEGRATE] " + (ok ? "PASS " : "FAIL ") + s); fail |= !ok; };
        try
        {
            var cfg = MHAttachData.Load();
            chk(HasDefine(), "scripting define " + Define + " (Standalone)");
            chk(File.Exists("Assets/StreamingAssets/MetaHumanHead/mesh/unity_meta.json") && File.Exists("Assets/StreamingAssets/MetaHumanHead/rig/MH_s2_Head_runtime.dna"), "StreamingAssets/MetaHumanHead data present");
            var pi = AssetImporter.GetAtPath(PluginPath) as PluginImporter; chk(pi != null && pi.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64) && !pi.GetCompatibleWithAnyPlatform(), "mhrig.dll importer = Windows x64 only");
            chk(AssetDatabase.LoadAssetAtPath<Shader>(HairShaderPath) != null, "hair shader graph imports as a shader");
            var prof = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(ProfilePath); chk(prof != null, "diffusion profile asset");
            chk(prof != null && InList(DefaultVolumeProfile(), prof), "diffusion profile listed in HDRP Global Settings default volume profile");
            var hd = GraphicsSettings.defaultRenderPipeline as HDRenderPipelineAsset; chk(hd != null && hd.currentPlatformRenderPipelineSettings.supportSubsurfaceScattering, "default HDRP asset: Subsurface Scattering on");
            var scene = EditorSceneManager.OpenScene(cfg.scene, OpenSceneMode.Single);
            var root = FindFighter(cfg); chk(root != null, "fighter '" + cfg.fighterName + "' in scene");
            if (root != null)
            {
                MHHeadBuilder b = null; foreach (var x in root.GetComponentsInChildren<MHHeadBuilder>(true)) { b = x; break; }
                chk(b != null, "MHHeadBuilder under the fighter");
                if (b != null)
                {
                    var bone = FindBone(root, cfg.boneNames); chk(bone != null && b.transform.parent == bone, "head object is a child of the head bone (" + (b.transform.parent != null ? b.transform.parent.name : "none") + ")");
                    chk(b.dataFolder == cfg.builder.dataFolder && b.lookFile == cfg.builder.lookFile && b.lod == cfg.builder.lod && b.useJobs, "builder fields = data (dataFolder/lookFile/lod/useJobs)");
                    chk(b.headMaterial != null && b.teethMaterial != null, "skin + teeth material assets assigned");
                    chk(b.GetComponent<MHHeadAttach>() != null && b.GetComponent<MHHeadLod>() != null, "MHHeadAttach + MHHeadLod present");
                }
                int on = 0; foreach (var r in root.GetComponentsInChildren<Renderer>(true)) { if (b != null && r.transform.IsChildOf(b.transform)) continue; foreach (var n in cfg.hideRenderers) if (string.Equals(r.name, n, StringComparison.OrdinalIgnoreCase) && r.enabled) on++; }
                chk(!cfg.hideOriginalHead || on == 0, "original head renderer(s) disabled (still enabled: " + on + ")");
            }
        }
        catch (Exception e) { chk(false, "exception " + e.Message); }
        Rep.AppendLine(fail ? "[MHINTEGRATE] OVERALL FAIL" : "[MHINTEGRATE] OVERALL PASS"); Debug.Log(fail ? "[MHINTEGRATE] OVERALL FAIL" : "[MHINTEGRATE] OVERALL PASS");
        Done(fail ? 3 : 0, "verify");
    }
}
#endif
