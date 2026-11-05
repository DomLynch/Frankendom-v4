#if UNITY_EDITOR
// Reproducible rebuild from CANONICAL only (lane A3 process fix).
//   MHRebuildAll.RebuildAllFromCanonical()  (batch: -executeMethod MHRebuildAll.RebuildAllFromCanonical -mhcanon <third_party dir>)
// 1. copies the look file (mhhead_attach.json builder.lookFile) and look\textures_shared\*.png (ONE shared texture folder) into StreamingAssets\MetaHumanHead\{look,textures} and Assets\MetaHumanHead\Textures (only when bytes differ)
// 2. sets import settings (normals = Normal map, SRMF/masks/WM linear+readable, eyes readable + uncompressed)
// 3. MOVES every saved HDRP material/asset file in Assets\MetaHumanHead\HDRP to <project>\_agents\bak\hdrp_<time> (no deletes) and regenerates skin, eyes/eyeshell/lash/saliva/teeth, hair/brow materials
//    (stale-asset bug: the eye/teeth Save() uses GenerateUniqueAssetPath, so re-runs made "MH_HDRP_teeth 7.mat" etc. while the scene kept using the oldest file)
// 4. clears static caches of the eye/skin/hair material classes (reflection) and writes TestOut\rebuild_report.txt
using System; using System.Collections; using System.IO; using System.Reflection; using System.Text; using System.Security.Cryptography;
using UnityEditor; using UnityEngine; using MetaHumanRig;

public static class MHRebuildAll
{
    public const string DefaultCanon = @"C:\Users\dom12\Documents\kits_out\3d\metahuman\third_party";
    const string HdrpFolder = "Assets/MetaHumanHead/HDRP";
    static string Arg(string n, string d) { var a = Environment.GetCommandLineArgs(); for (int i = 0; i < a.Length - 1; i++) if (a[i] == n) return a[i + 1]; return d; }
    static string Hash(string p) { using (var m = MD5.Create()) using (var f = File.OpenRead(p)) return BitConverter.ToString(m.ComputeHash(f)); }
    static bool CopyIfDiff(string src, string dst, StringBuilder rep)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(src).Length && Hash(dst) == Hash(src)) return false;
        if (File.Exists(dst)) { string bk = Path.Combine("_agents", "bak", "rebuild_" + DateTime.Now.ToString("HHmmss")); Directory.CreateDirectory(bk); File.Copy(dst, Path.Combine(bk, Path.GetFileName(dst)), true); }
        File.Copy(src, dst, true); rep.AppendLine("  updated " + dst); return true;
    }

    static readonly string[] NormalMaps = { "Head_Normal", "Head_Normal_GL", "Head_Detail_Normal", "Eyes_Normal", "Teeth_Normal" };
    static void ImportSettings(string assetPath, StringBuilder rep)
    {
        var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter; if (ti == null) return; string n = Path.GetFileNameWithoutExtension(assetPath);
        bool isNormal = Array.IndexOf(NormalMaps, n) >= 0, linear = n.Contains("SRMF") || n.Contains("Mask") || n.Contains("_WM");
        bool changed = false;
        if (isNormal) { if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; } }
        else { if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; changed = true; } if (ti.sRGBTexture == linear) { ti.sRGBTexture = !linear; changed = true; } }
        bool readable = linear || n.StartsWith("Eyes") || n.StartsWith("Eyelashes") || n.StartsWith("Teeth_Color");
        if (readable && !ti.isReadable) { ti.isReadable = true; changed = true; }
        if (n.StartsWith("Eyes") && ti.textureCompression != TextureImporterCompression.Uncompressed) { ti.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
        if (changed) { ti.SaveAndReimport(); rep.AppendLine("  import settings " + n + " normal=" + isNormal + " linear=" + linear + " readable=" + readable); }
    }

    static void ClearStatics(Type t, StringBuilder rep)
    {
        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            if (typeof(IDictionary).IsAssignableFrom(f.FieldType)) { var d = f.GetValue(null) as IDictionary; if (d != null && d.Count > 0) { rep.AppendLine("  cleared " + t.Name + "." + f.Name + " (" + d.Count + ")"); d.Clear(); } }
    }

    public static string RebuildAllFromCanonical()
    {
        var rep = new StringBuilder(); string canon = Arg("-mhcanon", DefaultCanon); string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        rep.AppendLine("RebuildAllFromCanonical " + stamp + " canon=" + canon);
        var mhCfg = MHAttachData.Load(); string lookDst = MHIntegrate.LookAsset(mhCfg);
        string lookSrc = Arg("-mhlook", Path.Combine(canon, mhCfg.builder.lookFile.Replace('/', Path.DirectorySeparatorChar))); rep.AppendLine("  look source " + lookSrc);
        CopyIfDiff(lookSrc, lookDst, rep);
        string tex = Path.Combine(canon, @"look\textures_shared"); int nt = 0;
        if (Directory.Exists(tex)) foreach (var f in Directory.GetFiles(tex, "*.png")) { string n = Path.GetFileName(f); bool a = CopyIfDiff(f, "Assets/MetaHumanHead/Textures/" + n, rep); bool b = CopyIfDiff(f, "Assets/StreamingAssets/MetaHumanHead/textures/" + n, rep); if (a || b) nt++; }
        else rep.AppendLine("  WARNING no textures_shared folder");
        AssetDatabase.Refresh();
        foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/MetaHumanHead/Textures" })) ImportSettings(AssetDatabase.GUIDToAssetPath(g), rep);
        string bak = Path.Combine("_agents", "bak", "hdrp_" + stamp); int moved = 0; Directory.CreateDirectory(bak);
        if (Directory.Exists(HdrpFolder)) foreach (var f in Directory.GetFiles(HdrpFolder)) { File.Move(f, Path.Combine(bak, Path.GetFileName(f))); moved++; }
        rep.AppendLine("  moved " + moved + " stale HDRP asset files to " + bak);
        AssetDatabase.Refresh();
        string err; var look = MHLook.Load(lookDst, out err); if (look == null) { rep.AppendLine("FAIL look: " + err); Write(rep); throw new Exception("look load failed " + err); }
        foreach (var t in new[] { typeof(MHEyeHdrp), typeof(MHSkinHdrp), typeof(MHHairHdrp) }) ClearStatics(t, rep);
        MHSkinHdrp.CreateAssets(look, HdrpFolder); AssetDatabase.Refresh();
        MHEyeHdrp.CreateAssets(look, HdrpFolder);
        MHHairHdrp.CreateAssets(look, HdrpFolder);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        foreach (var t in new[] { typeof(MHEyeHdrp), typeof(MHSkinHdrp), typeof(MHHairHdrp) }) ClearStatics(t, rep);
        int dup = 0; foreach (var f in Directory.GetFiles(HdrpFolder, "*.mat")) { rep.AppendLine("  asset " + Path.GetFileName(f) + " " + new FileInfo(f).Length + " B"); if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @" \d+$")) dup++; }
        rep.AppendLine("  textures updated: " + nt + ", duplicate-named assets: " + dup);
        Write(rep); return rep.ToString();
    }
    static void Write(StringBuilder rep) { Directory.CreateDirectory("TestOut"); File.WriteAllText("TestOut/rebuild_report.txt", rep.ToString()); Debug.Log("[MHRebuildAll] " + rep); }
}
#endif
