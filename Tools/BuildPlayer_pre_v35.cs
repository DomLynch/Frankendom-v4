using UnityEditor;
using UnityEngine;

public static class BuildPlayerCmd
{
    public static void Run()
    {
        // data layer -> StreamingAssets so the player reads the same JSON as the editor (loaders look in Application.streamingAssetsPath when Assets/Data is absent)
        System.IO.Directory.CreateDirectory("Assets/StreamingAssets");
        foreach (var f in System.IO.Directory.GetFiles("Assets/Data", "*.json")) System.IO.File.Copy(f, "Assets/StreamingAssets/" + System.IO.Path.GetFileName(f), true);
        AssetDatabase.Refresh();
        PlayerSettings.productName = "HadesSlice";
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1920; PlayerSettings.defaultScreenHeight = 1080;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 });
        var r = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/HadesSlice.unity" }, (System.IO.File.Exists("Tools/build_dir.txt") ? System.IO.File.ReadAllText("Tools/build_dir.txt").Trim() : "Builds/HDRP_ultra") + "/HadesSlice.exe", BuildTarget.StandaloneWindows64, BuildOptions.Development);
        Debug.Log("BUILD " + r.summary.result + " " + r.summary.totalSize + " errors=" + r.summary.totalErrors);
    }
}


