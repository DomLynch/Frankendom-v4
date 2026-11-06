using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// V66HUD: data-driven player build options for BuildPlayerCmd.Run (Assets/Editor/BuildPlayer.cs used to hard-code BuildOptions.Development, which puts the
// "Development Build" watermark and the dev console into every capture and into the release candidate).
// Source of truth: Assets/Data/build.json { developmentBuild, allowDebugging, connectProfiler, scenes }. developmentBuild is false by default.
// Overrides, strongest first: -releasebuild on the Unity command line (forces a release build), -devbuild on the command line or the file Tools/build_dev.txt (forces a development build).
// The chosen options are logged as "BUILD OPTIONS: ..." and EditorUserBuildSettings is set to match, so the Build Settings window never disagrees with the build.
// HCKIT3 v93: build IDENTITY (company / product / exe name / output folder / Unity path / chain flags / swap template) lives in ONE file, Tools/build_config.json, read here for the C# side
// and by Tools/lib/hckit3_common.ps1 for every script. This file is a superset of RCHYGIENE v100's BuildCfg (ExeName, ApplyPlayerSettings keep their names). Assets/Data/build.json keeps only the
// dev/release options + scene list because it ships in the player. If Tools/build_config.json is missing, the old literals apply (exe HadesSlice.exe, Builds/HDRP_ultra, product HadesSlice, ...).
public static class BuildCfg
{
    [Serializable] class Cfg
    {
        public bool developmentBuild = false, allowDebugging = false, connectProfiler = false; public string[] scenes = { "Assets/Scenes/HadesSlice.unity" };
        // RCHYGIENE v100 legacy identity keys: only used when Tools/build_config.json does not exist
        public string companyName = "", productName = "", bundleVersion = "", exeName = "", iconPath = ""; public int runInBackground = -1, showUnitySplash = -1;
    }

    // ---- HCKIT3 v93: Tools/build_config.json (JsonUtility ignores the "_about"/"preSteps"/"batch" keys the PowerShell side uses)
    [Serializable] public class IdentitySec { public string companyName = "", productName = "", bundleVersion = "", iconPath = ""; public int showUnitySplash = -1; }
    [Serializable] public class BuildSec
    {
        public string exeName = "HadesSlice.exe", buildRoot = "Builds", buildDirPrefix = "HDRP_", defaultDir = "Builds/HDRP_ultra";
        public bool writeBuildInfo = true, reportStaleStreaming = true, pruneStaleStreaming = false;
        public string[] editorOnlyData = new string[0];
    }
    [Serializable] public class PlayerSec { public string fullScreenMode = "FullScreenWindow", graphicsApi = "Direct3D12"; public bool resizableWindow = true, runInBackground = true; public int defaultWidth = 1920, defaultHeight = 1080; }
    [Serializable] public class DeckArea { public float[] center = { 0f, 0f, 15f }; public float[] size = { 18f, 100f, 38f }; }
    [Serializable] public class ChainSec
    {
        public string name = "Production", legacyAlias = "V51";
        // defaults = what HdrpBatch.V51 did before HCKIT3 (true) + the steps it skipped (false)
        public bool controlsInputAxes = true, alwaysIncludeShaders = true, attackClipSetup = true, dodgeClipSetup = true;
        public bool guardLayerSetup = true, woundDecalRef = true, v1FinisherClipSetup = true, finisherClipSetup = false, finisherReadable = false, joanSwap = false;   // INT99 v99: guardLayerSetup + woundDecalRef = what live V51 runs
        public bool gltfMats = true, worldMats = true, bridgeMats = true, look = true, hdrpShot = true, ground = true, propsLayout = true, doorway = true, charLayer = true, deckDiag = true, occlusion = false;   // OCCL v101: occlusion culling bake (A/B), default off
        public DeckArea deckDiagArea = new DeckArea();
    }
    [Serializable] public class SwapSec { public string templateId = ""; }
    [Serializable] public class Conf
    {
        public IdentitySec identity = new IdentitySec(); public BuildSec build = new BuildSec(); public PlayerSec player = new PlayerSec();
        public ChainSec chain = new ChainSec(); public SwapSec swap = new SwapSec();
    }
    [Serializable] class BuildInfo { public string exe = "", dataDir = "", version = "", time = "", result = "", productName = "", companyName = "", unityVersion = ""; public ulong totalSize = 0; }

    public const string ConfigPath = "Tools/build_config.json";

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

    /// <summary>HCKIT3 v93: value of "-name value" or "-name:value" on the Unity command line, "" when absent.</summary>
    public static string ArgValue(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == name && i + 1 < a.Length) return a[i + 1];
            if (a[i].StartsWith(name + ":", StringComparison.Ordinal)) return a[i].Substring(name.Length + 1);
        }
        return "";
    }

    /// <summary>HCKIT3 v93: Tools/build_config.json (defaults = today's behaviour when the file is missing or unreadable; legacy RCHYGIENE build.json identity keys fill in then).</summary>
    public static Conf Config()
    {
        var c = new Conf(); bool found = false;
        try
        {
            if (File.Exists(ConfigPath)) { var j = JsonUtility.FromJson<Conf>(File.ReadAllText(ConfigPath).TrimStart((char)0xFEFF)); if (j != null) { c = j; found = true; } }
        }
        catch (Exception e) { Debug.LogWarning("BuildCfg: " + ConfigPath + " unreadable, defaults: " + e.Message); }
        if (c.identity == null) c.identity = new IdentitySec(); if (c.build == null) c.build = new BuildSec(); if (c.player == null) c.player = new PlayerSec();
        if (c.chain == null) c.chain = new ChainSec(); if (c.swap == null) c.swap = new SwapSec(); if (c.chain.deckDiagArea == null) c.chain.deckDiagArea = new DeckArea();
        if (!found)
        {
            var l = Load();   // legacy: identity keys RCHYGIENE v100 put into build.json
            c.identity.companyName = l.companyName; c.identity.productName = string.IsNullOrEmpty(l.productName) ? "HadesSlice" : l.productName; c.identity.bundleVersion = l.bundleVersion;
            c.identity.iconPath = l.iconPath; c.identity.showUnitySplash = l.showUnitySplash;
            if (!string.IsNullOrEmpty(l.exeName)) c.build.exeName = l.exeName;
            if (l.runInBackground >= 0) c.player.runInBackground = l.runInBackground != 0;
        }
        if (string.IsNullOrEmpty(c.build.exeName)) c.build.exeName = "HadesSlice.exe";
        return c;
    }

    public static string[] Scenes() { return Load().scenes; }

    /// <summary>The player exe file name (build_config.json build.exeName). RCHYGIENE v100 name kept. Scripts read the same key.</summary>
    public static string ExeName() { return Config().build.exeName; }

    /// <summary>HCKIT3 v93: output folder of the player build: -buildDir on the command line, else the legacy Tools/build_dir.txt (deprecated, logged), else build.defaultDir. No global mutable file needed any more.</summary>
    public static string OutputDir()
    {
        string a = ArgValue("-buildDir"); if (!string.IsNullOrEmpty(a)) return a.Trim().TrimEnd('/', '\\');
        const string legacy = "Tools/build_dir.txt";
        if (File.Exists(legacy)) { Debug.LogWarning("BuildCfg: using DEPRECATED " + legacy + " (pass -buildDir <folder> instead; Tools/build_and_gate.ps1 does)"); return File.ReadAllText(legacy).Trim().TrimEnd('/', '\\'); }
        return Config().build.defaultDir;
    }

    /// <summary>Pushes build_config.json identity / version / window / graphics API / icon / splash into PlayerSettings right before a player build (RCHYGIENE v100 name kept; the window + graphics lines moved here from BuildPlayer.cs with the same values). Empty or negative values change nothing.</summary>
    public static void ApplyPlayerSettings()
    {
        var c = Config(); var id = c.identity; var pl = c.player;
        if (!string.IsNullOrEmpty(id.companyName)) PlayerSettings.companyName = id.companyName;
        if (!string.IsNullOrEmpty(id.productName)) PlayerSettings.productName = id.productName;
        if (!string.IsNullOrEmpty(id.bundleVersion)) PlayerSettings.bundleVersion = id.bundleVersion;
        if (id.showUnitySplash >= 0) PlayerSettings.SplashScreen.show = id.showUnitySplash != 0;
        if (!string.IsNullOrEmpty(id.iconPath))
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(id.iconPath);
            if (tex != null) PlayerSettings.SetIcons(UnityEditor.Build.NamedBuildTarget.Unknown, new[] { tex }, UnityEditor.IconKind.Application);   // Unknown = the default icon slot
            else Debug.LogWarning("BuildCfg: iconPath '" + id.iconPath + "' is not a Texture2D asset, icon left as it is");
        }
        FullScreenMode fm; if (Enum.TryParse(pl.fullScreenMode, out fm)) PlayerSettings.fullScreenMode = fm; else Debug.LogWarning("BuildCfg: player.fullScreenMode '" + pl.fullScreenMode + "' unknown, left as it is");
        PlayerSettings.resizableWindow = pl.resizableWindow; PlayerSettings.runInBackground = pl.runInBackground;
        PlayerSettings.defaultScreenWidth = pl.defaultWidth; PlayerSettings.defaultScreenHeight = pl.defaultHeight;
        UnityEngine.Rendering.GraphicsDeviceType api;
        if (Enum.TryParse(pl.graphicsApi, out api)) { PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false); PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { api }); }
        else Debug.LogWarning("BuildCfg: player.graphicsApi '" + pl.graphicsApi + "' unknown, graphics APIs left as they are");
        Debug.Log("BUILD IDENTITY: company='" + PlayerSettings.companyName + "' product='" + PlayerSettings.productName + "' version=" + PlayerSettings.bundleVersion + " runInBackground=" + PlayerSettings.runInBackground + " exe=" + c.build.exeName + " api=" + pl.graphicsApi + " (from " + (File.Exists(ConfigPath) ? ConfigPath : "legacy build.json + defaults") + ")");
    }

    /// <summary>HCKIT3 v93: copies Assets/Data/*.json to Assets/StreamingAssets except build.editorOnlyData (editor-only tool configs with absolute C:/ paths no longer ship). Logs stale StreamingAssets json (no Assets/Data twin, or editor-only) and, only when build.pruneStaleStreaming is true, moves them to the Unity trash (recoverable).</summary>
    public static void CopyDataToStreaming()
    {
        var c = Config(); var skip = new System.Collections.Generic.HashSet<string>(c.build.editorOnlyData ?? new string[0], StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory("Assets/StreamingAssets");
        int copied = 0; var skipped = new System.Collections.Generic.List<string>();
        foreach (var f in Directory.GetFiles("Assets/Data", "*.json"))
        {
            string n = Path.GetFileName(f);
            if (skip.Contains(n)) { skipped.Add(n); continue; }
            File.Copy(f, "Assets/StreamingAssets/" + n, true); copied++;
        }
        var stale = new System.Collections.Generic.List<string>();
        foreach (var f in Directory.GetFiles("Assets/StreamingAssets", "*.json"))
        {
            string n = Path.GetFileName(f);
            if (skip.Contains(n) || !File.Exists("Assets/Data/" + n)) stale.Add(n);
        }
        Debug.Log("BUILD DATA: copied " + copied + " json Data -> StreamingAssets, editor-only not copied: [" + string.Join(", ", skipped.ToArray()) + "]");
        if (stale.Count > 0 && c.build.reportStaleStreaming)
            Debug.LogWarning("BUILD DATA: " + stale.Count + " StreamingAssets json ship although they are editor-only or have no Assets/Data twin: [" + string.Join(", ", stale.ToArray()) + "]" + (c.build.pruneStaleStreaming ? " (pruning to the Unity trash)" : " (set build.pruneStaleStreaming true to move them to the trash, or remove them by hand)"));
        if (c.build.pruneStaleStreaming) foreach (var n in stale) AssetDatabase.MoveAssetToTrash("Assets/StreamingAssets/" + n);
    }

    /// <summary>HCKIT3 v93: writes build_info.json (exe, dataDir, version = -buildTag, time, result) next to the exe; the gate reads exe + _Data names from it instead of hard-coding HadesSlice.</summary>
    public static void WriteBuildInfo(string dir, UnityEditor.Build.Reporting.BuildReport r)
    {
        var c = Config(); if (!c.build.writeBuildInfo || r == null) return;
        try
        {
            string exe = string.IsNullOrEmpty(r.summary.outputPath) ? c.build.exeName : Path.GetFileName(r.summary.outputPath);
            var bi = new BuildInfo { exe = exe, dataDir = Path.GetFileNameWithoutExtension(exe) + "_Data", version = ArgValue("-buildTag"), time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), result = r.summary.result.ToString(), productName = PlayerSettings.productName, companyName = PlayerSettings.companyName, unityVersion = Application.unityVersion, totalSize = r.summary.totalSize };
            if (r.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) { Debug.LogWarning("BuildCfg: build result " + bi.result + ", build_info.json not written"); return; }
            Directory.CreateDirectory(dir);
            File.WriteAllText(dir + "/build_info.json", JsonUtility.ToJson(bi, true));
            Debug.Log("BUILD INFO: " + dir + "/build_info.json exe=" + bi.exe + " dataDir=" + bi.dataDir + " version=" + bi.version);
        }
        catch (Exception e) { Debug.LogWarning("BuildCfg: build_info.json not written: " + e.Message); }
    }

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
