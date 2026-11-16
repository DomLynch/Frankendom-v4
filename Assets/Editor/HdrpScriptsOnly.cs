using UnityEditor;
using UnityEngine;

// SCRIPTS-ONLY BUILD (Strategy/COO 16 Nov 23:31, ported from FrontierDom Slice0Setup.BuildScriptsOnly): rebuilds only the scripts (Assembly-CSharp.dll) into a player folder that already holds a full build.
// The caller (Tools\build_scriptsonly.ps1) first copies the live build to the new tag folder and passes it with -buildDir. No scene is opened or re-serialized, so a new serialized field takes its code default
// until the next bake. Data json twins are copied to StreamingAssets as in a normal build. ApplyPlayerSettings is NOT called (it can dirty ProjectSettings and force a graphics-asset re-import).
// Entry point: -executeMethod HdrpScriptsOnly.Run
public static class HdrpScriptsOnly
{
    public static void Run()
    {
        BuildCfg.CopyDataToStreaming();
        AssetDatabase.Refresh();
        string dir = BuildCfg.OutputDir();
        var bpo = new BuildPlayerOptions { scenes = BuildCfg.Scenes(), locationPathName = dir + "/" + BuildCfg.ExeName(), target = BuildTarget.StandaloneWindows64, options = BuildCfg.Options() | BuildOptions.BuildScriptsOnly };
        var r = BuildPipeline.BuildPlayer(bpo);
        Debug.Log("BUILD SCRIPTS-ONLY " + r.summary.result + " " + r.summary.totalSize + " errors=" + r.summary.totalErrors + " time=" + r.summary.totalTime);
        BuildCfg.WriteBuildInfo(dir, r);
        EditorApplication.Exit(r.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
    }
}
