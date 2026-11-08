using UnityEditor;
using UnityEngine;

public static class BuildPlayerCmd
{
    public static void Run()
    {
        // HCKIT3 v93: identity, window, graphics API, exe name, output folder and the data copy come from BuildCfg (Tools/build_config.json); was: productName/1920x1080/D3D12/"HadesSlice.exe"/Tools/build_dir.txt literals here.
        // data layer -> StreamingAssets so the player reads the same JSON as the editor (loaders look in Application.streamingAssetsPath when Assets/Data is absent); editor-only tool configs (build.editorOnlyData) are skipped
        BuildCfg.CopyDataToStreaming();
        AssetDatabase.Refresh();
        BuildCfg.ApplyPlayerSettings();
        string dir = BuildCfg.OutputDir();   // HCKIT3 v93: -buildDir <folder> (Tools/build_and_gate.ps1 passes it), legacy Tools/build_dir.txt, else build.defaultDir
        // FPSD v1: per-pass GPU ms. Core RP's ProfilingSampler only reads GPU time (and only sets MarkerFlags.SampleGPU) when UNITY_USE_RECORDER is defined, and nothing in this project defines it,
        // so gpuElapsedTime is 0.0 even in a development build. -gpupass (or Tools/build_gpupass.txt) adds it for THIS player build only (BuildPlayerOptions.extraScriptingDefines), no ProjectSettings edit.
        // Use together with -devbuild and -profilebuild. Without the flag this method builds exactly what it built before.
        bool gpuPass = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-gpupass") >= 0 || System.IO.File.Exists("Tools/build_gpupass.txt");
        var bpo = new BuildPlayerOptions { scenes = BuildCfg.Scenes(), locationPathName = dir + "/" + BuildCfg.ExeName(), target = BuildTarget.StandaloneWindows64, options = BuildCfg.Options() };
        if (gpuPass) { bpo.extraScriptingDefines = new[] { "UNITY_USE_RECORDER" }; Debug.Log("BUILD FPSD: extraScriptingDefines UNITY_USE_RECORDER (per-pass GPU timing build, use with -devbuild)"); }
        // FIXHARNESS v1: release profile. -noharness (or the file Tools/build_noharness.txt) adds FRANKEN_NO_HARNESS to THIS player build only, which compiles the test/proof/capture harnesses out (#if !FRANKEN_NO_HARNESS). Default = harness ON = exactly the old build.
        bool noHarness = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-noharness") >= 0 || System.IO.File.Exists("Tools/build_noharness.txt");
        if (noHarness) { var dl = new System.Collections.Generic.List<string>(bpo.extraScriptingDefines ?? new string[0]); dl.Add("FRANKEN_NO_HARNESS"); bpo.extraScriptingDefines = dl.ToArray(); Debug.Log("BUILD FIXHARNESS: extraScriptingDefines FRANKEN_NO_HARNESS (release build: harnesses compiled out)"); }
        var r = BuildPipeline.BuildPlayer(bpo);   // FPSD v1: was BuildPipeline.BuildPlayer(BuildCfg.Scenes(), path, target, BuildCfg.Options()), same arguments
        Debug.Log("BUILD " + r.summary.result + " " + r.summary.totalSize + " errors=" + r.summary.totalErrors);
        BuildCfg.WriteBuildInfo(dir, r);   // HCKIT3 v93: <dir>/build_info.json (exe, dataDir, version) for the gate
    }
}
