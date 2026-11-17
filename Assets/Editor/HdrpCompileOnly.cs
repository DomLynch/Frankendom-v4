using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

// COMPILE-ONLY PATH (Strategy/Lead 17 Nov): compiles the PLAYER script assemblies with PlayerBuildInterface.CompilePlayerScripts into a temp folder, with no player build, no scene opened and no
// "previous full player build" requirement (BuildOptions.BuildScriptsOnly refused: Can't verify script data layout). Tools\build_compileonly.ps1 then copies ONLY the project's own dlls over a physical copy of the live
// build's Managed folder. Only valid when no serialized layout changed (the guard refuses new public / [SerializeField] fields).
// Entry point: -executeMethod HdrpCompileOnly.Run -compileOut <folder>
public static class HdrpCompileOnly
{
    public static void Run()
    {
        string outDir = "TestOut/FkCompileOut"; var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == "-compileOut") outDir = a[i + 1];
        int code = 1;
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var s = new ScriptCompilationSettings { target = BuildTarget.StandaloneWindows64, group = BuildTargetGroup.Standalone, options = ScriptCompilationOptions.None };
            var r = PlayerBuildInterface.CompilePlayerScripts(s, outDir);
            var dlls = Directory.GetFiles(outDir, "*.dll").Select(Path.GetFileName).ToArray();
            Debug.Log("COMPILE-ONLY assemblies reported " + (r.assemblies != null ? r.assemblies.Count : -1) + ", dll files " + dlls.Length + " in " + Path.GetFullPath(outDir) + ": " + string.Join(", ", dlls.Where(n => n.StartsWith("Assembly-CSharp")).ToArray()));
            code = (r.assemblies != null && r.assemblies.Count > 0 && File.Exists(Path.Combine(outDir, "Assembly-CSharp.dll"))) ? 0 : 1;
        }
        catch (System.Exception e) { Debug.LogError("COMPILE-ONLY failed: " + e); }
        EditorApplication.Exit(code);
    }
}
