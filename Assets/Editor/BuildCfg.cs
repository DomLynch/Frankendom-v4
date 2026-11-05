using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// V66HUD: data-driven player build options for BuildPlayerCmd.Run (Assets/Editor/BuildPlayer.cs used to hard-code BuildOptions.Development, which puts the
// "Development Build" watermark and the dev console into every capture and into the release candidate).
// Source of truth: Assets/Data/build.json { developmentBuild, allowDebugging, connectProfiler, scenes }. developmentBuild is false by default.
// Overrides, strongest first: -releasebuild on the Unity command line (forces a release build), -devbuild on the command line or the file Tools/build_dev.txt (forces a development build).
// The chosen options are logged as "BUILD OPTIONS: ..." and EditorUserBuildSettings is set to match, so the Build Settings window never disagrees with the build.
public static class BuildCfg
{
    [Serializable] class Cfg { public bool developmentBuild = false, allowDebugging = false, connectProfiler = false; public string[] scenes = { "Assets/Scenes/HadesSlice.unity" }; }

    static Cfg Load()
    {
        var c = new Cfg();
        try
        {
            const string p = "Assets/Data/build.json";
            if (File.Exists(p)) { var j = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (j != null) c = j; }
        }
        catch (Exception e) { Debug.LogWarning("BuildCfg: build.json unreadable, defaults (release build): " + e.Message); }
        if (c.scenes == null || c.scenes.Length == 0) c.scenes = new[] { "Assets/Scenes/HadesSlice.unity" };
        return c;
    }
    static bool Has(string a) { return Array.IndexOf(Environment.GetCommandLineArgs(), a) >= 0; }

    public static string[] Scenes() { return Load().scenes; }

    public static BuildOptions Options()
    {
        var c = Load(); bool dev = c.developmentBuild; string src = "Assets/Data/build.json";
        if (Has("-devbuild") || File.Exists("Tools/build_dev.txt")) { dev = true; src = Has("-devbuild") ? "-devbuild" : "Tools/build_dev.txt"; }
        if (Has("-releasebuild")) { dev = false; src = "-releasebuild"; }
        var o = BuildOptions.None;
        bool dbg = dev && c.allowDebugging, prof = dev && c.connectProfiler;
        if (dev) o |= BuildOptions.Development;
        if (dbg) o |= BuildOptions.AllowDebugging;
        if (prof) o |= BuildOptions.ConnectWithProfiler;
        EditorUserBuildSettings.development = dev; EditorUserBuildSettings.allowDebugging = dbg; EditorUserBuildSettings.connectProfiler = prof;
        Debug.Log("BUILD OPTIONS: " + (dev ? "DEVELOPMENT build" : "RELEASE build (no Development watermark)") + " allowDebugging=" + dbg + " connectProfiler=" + prof + " (from " + src + ")");
        return o;
    }
}
