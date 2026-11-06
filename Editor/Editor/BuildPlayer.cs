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
        var r = BuildPipeline.BuildPlayer(BuildCfg.Scenes(), dir + "/" + BuildCfg.ExeName(), BuildTarget.StandaloneWindows64, BuildCfg.Options());   // HCKIT3 v93: scene list from build.json (was a literal array here)
        Debug.Log("BUILD " + r.summary.result + " " + r.summary.totalSize + " errors=" + r.summary.totalErrors);
        BuildCfg.WriteBuildInfo(dir, r);   // HCKIT3 v93: <dir>/build_info.json (exe, dataDir, version) for the gate
    }
}
