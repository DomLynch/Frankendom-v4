using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// HCKIT5 v95 (HC4 K9): ONE place for the machine paths. Tools/paths.json (NOT shipped, NOT in Assets/) holds
//   { "projectRoot": "", "kitsOut": "C:/Users/dom12/Documents/kits_out", "unityExe": "...", "blenderExe": "...", "handoverDir": "C:/HANDOVER-Macbook" }
// and every Editor json / tool writes  "${kitsOut}/3d/..."  instead of an absolute string. Resolve() expands ${name}; a missing file or key falls back to the compiled defaults below
// (= the literals the project used before, so behaviour is identical without paths.json). projectRoot "" = the folder that holds Assets/. The python twin is Tools/paths.py.
public static class ProjectPaths
{
    [Serializable] class Cfg { public string projectRoot = "", kitsOut = "", unityExe = "", blenderExe = "", handoverDir = ""; }

    static Cfg c;

    static Cfg C
    {
        get
        {
            if (c != null) return c;
            c = new Cfg();
            try { string p = Path.Combine(Root(), "Tools/paths.json"); if (File.Exists(p)) JsonUtility.FromJsonOverwrite(File.ReadAllText(p), c); }
            catch (Exception e) { Debug.LogWarning("ProjectPaths: Tools/paths.json unreadable (" + e.Message + "), using defaults"); }
            return c;
        }
    }

    static string Root() { return Path.GetDirectoryName(Application.dataPath).Replace('\\', '/'); }

    public static void Reload() { c = null; }

    // true when the Editor/batch command line carries -name (one-shot tools that overwrite tuned data are guarded behind an explicit flag, e.g. -importV3)
    public static bool Flag(string name) { foreach (var a in Environment.GetCommandLineArgs()) if (string.Equals(a, "-" + name, StringComparison.OrdinalIgnoreCase)) return true; return false; }

    public static string Get(string key)
    {
        switch (key)
        {
            case "projectRoot": return C.projectRoot.Length > 0 ? C.projectRoot : Root();
            case "kitsOut": return C.kitsOut.Length > 0 ? C.kitsOut : "C:/Users/dom12/Documents/kits_out";
            case "unityExe": return C.unityExe.Length > 0 ? C.unityExe : "C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe";
            case "blenderExe": return C.blenderExe.Length > 0 ? C.blenderExe : "C:/Program Files/Blender Foundation/Blender 5.2/blender.exe";
            case "handoverDir": return C.handoverDir.Length > 0 ? C.handoverDir : "C:/HANDOVER-Macbook";
        }
        return "";
    }

    // "${kitsOut}/3d/x.glb" -> "C:/Users/dom12/Documents/kits_out/3d/x.glb"; text without ${...} is returned unchanged (old absolute paths keep working)
    public static string Resolve(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf("${", StringComparison.Ordinal) < 0) return s;
        foreach (var k in new[] { "projectRoot", "kitsOut", "unityExe", "blenderExe", "handoverDir" }) s = s.Replace("${" + k + "}", Get(k));
        return s;
    }
}
