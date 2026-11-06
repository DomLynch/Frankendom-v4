#if UNITY_EDITOR
// Batch: -executeMethod MHPlayerBuild.Build [-mhbuild <dir>]  -> Windows x64 Mono player <project>\Build\MHTest.exe of Assets/Scenes/MHTest.unity (frame timing stats on).
// Run the player WITH a graphics device (never -nographics):  MHTest.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -mhout <dir>   (17 rig tests -> player_results.json)
//                                                             MHTest.exe ... -mhperf -mhout <dir>                                               (perf gate -> perf_report.json/.md)
using System.IO; using UnityEditor; using UnityEditor.Build.Reporting; using UnityEngine;
public static class MHPlayerBuild
{
    public static void Build()
    {
        string dir = "Build"; var a = System.Environment.GetCommandLineArgs(); for (int i = 0; i < a.Length - 1; i++) if (a[i] == "-mhbuild") dir = a[i + 1];
        Directory.CreateDirectory(dir); PlayerSettings.enableFrameTimingStats = true; PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        var o = new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/MHTest.unity" }, locationPathName = Path.Combine(dir, "MHTest.exe"), target = BuildTarget.StandaloneWindows64, options = BuildOptions.None };
        var r = BuildPipeline.BuildPlayer(o); Debug.Log("[MHBUILD] " + r.summary.result + " size=" + r.summary.totalSize + " errors=" + r.summary.totalErrors);
        EditorApplication.Exit(r.summary.result == BuildResult.Succeeded ? 0 : 6);
    }
}
#endif
