using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// CHARTEX v1 (laneASSETAUDIT, 2026-11-08). UNPROVEN until the Unity dev compiles it and a real build runs (import settings only show up in a build).
// Why: Dom's character textures are 4096 on disk, but every Assets/Characters/**/Tex importer was capped at 2048 for Standalone, so the build showed 2K.
// What: an AssetPostprocessor that, for a texture under a rule's pathPrefix, sets the Standalone override (and the default platform) to rule.maxSize with high-quality compression (BC7).
//   A max size is a cap, never an upscale: a 2048 file stays 2048. All numbers are rows in Assets/Data/char_tex_import.json (enabled false = this script does nothing; nothing is hard-coded here).
//   First match wins, so a character can be pinned lower (Dracula 2048). Excluded name parts (mask maps) are left to HdrpCharMats (linear 2048).
// One-off re-import of the existing files: menu Hades/Characters/Apply character texture import size, or -executeMethod CharTexImport.ApplyAll.
public class CharTexImport : AssetPostprocessor
{
    [Serializable] public class Rule { public string pathPrefix = ""; public int maxSize = 4096; public string[] excludeContains = new string[0]; }
    [Serializable] public class Cfg
    {
        public bool enabled = true; public string platform = "Standalone"; public string compression = "CompressedHQ"; public int compressionQuality = 100;
        public bool crunched = false; public Rule[] rules = new Rule[0];
    }
    const string DataPath = "Assets/Data/char_tex_import.json";
    static Cfg cached; static DateTime cachedStamp;

    static Cfg Load()
    {
        if (!File.Exists(DataPath)) return null;
        var t = File.GetLastWriteTimeUtc(DataPath);
        if (cached == null || t != cachedStamp) { try { cached = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath)); cachedStamp = t; } catch (Exception e) { Debug.LogWarning("CHARTEX bad json " + e.Message); cached = null; } }
        return cached;
    }

    static Rule Match(Cfg c, string path)
    {
        if (c.rules == null) return null;
        foreach (var r in c.rules)
        {
            if (r == null || string.IsNullOrEmpty(r.pathPrefix) || !path.StartsWith(r.pathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.excludeContains != null) foreach (var x in r.excludeContains) if (!string.IsNullOrEmpty(x) && path.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return r;
        }
        return null;
    }

    static TextureImporterCompression Comp(string s)
    {
        TextureImporterCompression v;
        return Enum.TryParse(s, true, out v) ? v : TextureImporterCompression.CompressedHQ;
    }

    // true when the importer was changed
    static bool Apply(TextureImporter ti, string path)
    {
        var c = Load(); if (c == null || !c.enabled || ti == null) return false;
        var r = Match(c, path); if (r == null) return false;
        bool changed = false;
        var s = ti.GetPlatformTextureSettings(c.platform);
        var comp = Comp(c.compression);
        if (!s.overridden || s.maxTextureSize != r.maxSize || s.textureCompression != comp || s.compressionQuality != c.compressionQuality || s.crunchedCompression != c.crunched)
        {
            s.overridden = true; s.maxTextureSize = r.maxSize; s.format = TextureImporterFormat.Automatic;
            s.textureCompression = comp; s.compressionQuality = c.compressionQuality; s.crunchedCompression = c.crunched;
            ti.SetPlatformTextureSettings(s); changed = true;
        }
        if (ti.maxTextureSize != r.maxSize) { ti.maxTextureSize = r.maxSize; changed = true; }
        return changed;
    }

    void OnPreprocessTexture() { Apply(assetImporter as TextureImporter, assetPath); }

    [MenuItem("Hades/Characters/Apply character texture import size")]
    public static void ApplyAll()
    {
        var c = Load(); if (c == null) { Debug.LogWarning("CHARTEX missing or bad " + DataPath); return; }
        if (!c.enabled) { Debug.Log("CHARTEX disabled in " + DataPath + ": nothing done"); return; }
        int n = 0, seen = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Characters" }))
        {
            var p = AssetDatabase.GUIDToAssetPath(guid); var ti = AssetImporter.GetAtPath(p) as TextureImporter; if (ti == null) continue;
            seen++;
            if (Apply(ti, p)) { ti.SaveAndReimport(); n++; }
        }
        Debug.Log("CHARTEX textures seen " + seen + ", reimported " + n + " (rules " + (c.rules != null ? c.rules.Length : 0) + ", " + c.compression + ", platform " + c.platform + ")");
    }
}
