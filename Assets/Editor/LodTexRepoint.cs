// LodTexRepoint (laneDEDUPE v2, STAGED, NOT COMPILED, NOT RUN). Editor only. Data: Assets/Data/lod_tex_map.json (copy of laneDEDUPE\v2\out\lod_tex_map.json).
// After dedupe_apply.py --install, the LOD1/LOD2 GLBs carry 1x1 placeholder images where they had byte-identical copies of LOD0's textures.
// This pass re-points every HDRP LOD material slot at LOD0's texture, so the placeholders are never referenced and the 4K copies are gone from the build.
// Call it LAST in HdrpGltfMats.Run() (after HdrpCharMats.Run()) and from the menu: HdrpGltfMats.Run rebuilds the LOD mats from the placeholders every time.
using System; using System.Collections.Generic; using System.IO; using UnityEditor; using UnityEngine;
public static class LodTexRepoint
{
    [Serializable] class Slot { public string lod0Material; public string md5; public int lodImage; public int lod0Image; }
    [Serializable] class Entry { public string glb; public string material; public bool maskRepoint; public string maskFromLod0Material; public string[] slotNames; }
    const string Map = "Assets/Data/lod_tex_map.json";
    static string Safe(string glbPath, string mat) => (Path.GetFileNameWithoutExtension(glbPath) + "_" + mat).Replace('/', '_').Replace(':', '_');
    static string MatPath(string glbPath, string mat) => "Assets/HDRP/Mats/" + Safe(glbPath, mat) + ".mat";
    static readonly (string slot, string prop)[] Props = { ("base", "_BaseColorMap"), ("normal", "_NormalMap"), ("emis", "_EmissiveColorMap") };

    [MenuItem("Frankendom/LOD texture repoint (dedupe)")]
    public static void Run()
    {
        if (!File.Exists(Map)) { Debug.Log("[LodTexRepoint] no " + Map + ": nothing to do (dedupe not installed)"); return; }
        var root = MiniJson.Parse(File.ReadAllText(Map)) as Dictionary<string, object>; var list = (List<object>)root["entries"];
        int done = 0, skip = 0, miss = 0; var warn = new List<string>();
        foreach (var o in list)
        {
            var e = (Dictionary<string, object>)o; string glb = (string)e["glb"], mn = (string)e["material"]; var slots = (Dictionary<string, object>)e["slots"];
            var lodMat = AssetDatabase.LoadAssetAtPath<Material>(MatPath(glb, mn));
            if (lodMat == null) { skip++; continue; }                         // this LOD material is not in the scene/HDRP set: nothing to re-point
            string l0glb = e.TryGetValue("lod0", out var l0o) ? (string)l0o : FindLod0(glb); bool ch = false;
            foreach (var (slot, prop) in Props)
            {
                if (!slots.TryGetValue(slot, out var so)) continue; string l0m = (string)((Dictionary<string, object>)so)["lod0Material"];
                var src = AssetDatabase.LoadAssetAtPath<Material>(MatPath(l0glb, l0m)); var tex = src != null && src.HasProperty(prop) ? src.GetTexture(prop) : null;
                if (tex == null) { miss++; warn.Add(glb + " " + mn + " " + slot); continue; }
                if (lodMat.GetTexture(prop) != tex) { lodMat.SetTexture(prop, tex); ch = true; }
            }
            if (e.TryGetValue("maskRepoint", out var mr) && (bool)mr && e["maskFromLod0Material"] != null)
            {
                var src = AssetDatabase.LoadAssetAtPath<Material>(MatPath(l0glb, (string)e["maskFromLod0Material"])); var tex = src != null ? src.GetTexture("_MaskMap") : null;
                if (tex != null) { if (lodMat.GetTexture("_MaskMap") != tex) { lodMat.SetTexture("_MaskMap", tex); ch = true; } } else { miss++; warn.Add(glb + " " + mn + " mask"); }
            }
            if (ch) { EditorUtility.SetDirty(lodMat); done++; }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[LodTexRepoint] materials re-pointed=" + done + " not-in-set=" + skip + " unresolved-slots=" + miss + (miss > 0 ? " FIRST: " + warn[0] : ""));
        if (miss > 0) Debug.LogWarning("[LodTexRepoint] unresolved slots keep the 1x1 placeholder (visible!): " + string.Join(" | ", warn.GetRange(0, Math.Min(20, warn.Count))));
    }
    // LOD0 file of the same family: ..._LOD1.glb / ..._LOD2.glb / _mid / _lod -> ..._LOD0.glb ; the plan's `lod0` field is authoritative, this is only the name rule
    static string FindLod0(string glb)
    {
        string d = Path.GetDirectoryName(glb).Replace('\\', '/'), n = Path.GetFileNameWithoutExtension(glb);
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^(.+?)[_-]lod\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return d + "/" + (m.Success ? m.Groups[1].Value : n) + "_LOD0.glb";
    }
}
// MiniJson: HdrpConvert.cs references one (grep); check its namespace/signature before compiling. Otherwise use the project's existing JSON reader (JsonUtility cannot read this dictionary shape). If none, drop in the common MiniJSON class (public domain) as MiniJson.Parse.
