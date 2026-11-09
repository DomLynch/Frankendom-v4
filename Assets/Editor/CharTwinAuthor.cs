#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// CHARTWIN AUTHOR v1 (laneVIS4, 2026-11-09). UNPROVEN until the Unity dev runs it. NEW EDITOR FILE. EDITOR ONLY: it creates the twin materials that CharTwin.cs swaps in at runtime. It never edits an original .mat.
// Run (Unity dev, Editor idle, once per json change):  Unity -batchmode -quit -projectPath <proj> -executeMethod CharTwinAuthor.BuildBatch      or the menu  Frankendom > VIS4 > Build char twins.
// Reads the "author" block of char_sss.json and char_hair.json (StreamingAssets copy, the same bytes as Data):
//   author[] { twin (Resources path, no extension, e.g. CharTwin/joan_head_0_sss), from (name of the ORIGINAL material, exact), fromFolder (part of its asset path, e.g. /Characters/Joan/),
//              materialType ("SSS" | "Aniso" | "" = leave), diffusionProfileGuid ("" = none; the HDRP package Skin profile guid is 404820c4cf36ad944862fa59c56064f0), floats[] { name, value } }
//   A twin is the ORIGINAL copied (same shader, textures, keywords) with Material Type and diffusion profile set through HDRP's own API (HDMaterial.SetMaterialType validates keywords + stencil), then the listed floats.
//   Everything written is a new asset under Assets/Resources/<twin>.mat, so it is in the player build and its shader variant is compiled in. Re-running updates the same asset in place (same guid).
public static class CharTwinAuthor
{
    [Serializable] public class Fl { public string name = ""; public float value; }
    [Serializable] public class Mk { public string twin = "", from = "", fromFolder = "", materialType = "", diffusionProfileGuid = ""; public Fl[] floats = new Fl[0]; }
    [Serializable] public class Root { public Mk[] author = new Mk[0]; public string resourcesDir = ""; public string[] files = new string[0]; }

    [MenuItem("Frankendom/VIS4/Build char twins")]
    public static void Menu() { int bad = Build(); Debug.Log("[CharTwinAuthor] done, problems=" + bad); }

    public static void BuildBatch() { int bad = Build(); EditorApplication.Exit(bad == 0 ? 0 : 1); }

    static int Build()
    {
        int bad = 0, made = 0;
        foreach (var rel in new[] { "char_sss.json", "char_hair.json" })
        {
            string p = Path.Combine(Application.streamingAssetsPath, rel); if (!File.Exists(p)) { Debug.Log("[CharTwinAuthor] no " + rel + ": skipped"); continue; }
            Root R; try { R = JsonUtility.FromJson<Root>(File.ReadAllText(p, Encoding.UTF8)); } catch (Exception e) { Debug.LogError("[CharTwinAuthor] bad " + rel + ": " + e.Message); bad++; continue; }
            string dir = string.IsNullOrEmpty(R.resourcesDir) ? "Assets/Resources" : R.resourcesDir;
            foreach (var mk in R.author)
            {
                if (mk == null || string.IsNullOrEmpty(mk.twin) || string.IsNullOrEmpty(mk.from)) continue;
                try { if (BuildOne(mk, dir)) made++; else bad++; } catch (Exception e) { Debug.LogError("[CharTwinAuthor] " + mk.twin + " failed: " + e); bad++; }
            }
        }
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        Debug.Log("[CharTwinAuthor] twins written/updated=" + made + " problems=" + bad);
        return bad;
    }

    static bool BuildOne(Mk mk, string dir)
    {
        string src = null;
        foreach (var g in AssetDatabase.FindAssets("t:Material " + mk.from))
        {
            string ap = AssetDatabase.GUIDToAssetPath(g);
            if (!string.Equals(Path.GetFileNameWithoutExtension(ap), mk.from, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(mk.fromFolder) && ap.IndexOf(mk.fromFolder, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (ap.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;   // never copy from a twin
            src = ap; break;
        }
        if (src == null) { Debug.LogError("[CharTwinAuthor] original material not found: " + mk.from + " " + mk.fromFolder); return false; }
        string dst = dir + "/" + mk.twin + ".mat"; Directory.CreateDirectory(Path.GetDirectoryName(dst));
        var original = AssetDatabase.LoadAssetAtPath<Material>(src);
        var twin = AssetDatabase.LoadAssetAtPath<Material>(dst);
        if (twin == null) { if (!AssetDatabase.CopyAsset(src, dst)) { Debug.LogError("[CharTwinAuthor] cannot copy " + src + " -> " + dst); return false; } twin = AssetDatabase.LoadAssetAtPath<Material>(dst); }
        else EditorUtility.CopySerialized(original, twin);   // refresh from the original, same asset / guid
        twin.name = Path.GetFileNameWithoutExtension(dst);
        if (!string.IsNullOrEmpty(mk.materialType))
        {
            MaterialId id = MaterialId.LitStandard;
            if (string.Equals(mk.materialType, "SSS", StringComparison.OrdinalIgnoreCase)) id = MaterialId.LitSSS;
            else if (string.Equals(mk.materialType, "Aniso", StringComparison.OrdinalIgnoreCase)) id = MaterialId.LitAniso;
            else if (!Enum.TryParse(mk.materialType, true, out id)) { Debug.LogError("[CharTwinAuthor] unknown materialType " + mk.materialType); return false; }
            if (!twin.SetMaterialType(id)) { Debug.LogError("[CharTwinAuthor] " + twin.name + ": shader has no Material Type property"); return false; }
        }
        if (!string.IsNullOrEmpty(mk.diffusionProfileGuid))
        {
            var prof = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(AssetDatabase.GUIDToAssetPath(mk.diffusionProfileGuid));
            if (prof == null) { Debug.LogError("[CharTwinAuthor] diffusion profile not found: " + mk.diffusionProfileGuid); return false; }
            HDMaterial.SetDiffusionProfile(twin, prof);
        }
        if (mk.floats != null) foreach (var f in mk.floats) if (f != null && twin.HasProperty(f.name)) twin.SetFloat(f.name, f.value); else Debug.LogWarning("[CharTwinAuthor] " + twin.name + " has no property " + (f != null ? f.name : "?"));
        HDMaterial.ValidateMaterial(twin);   // keywords + stencil after every change
        EditorUtility.SetDirty(twin);
        Debug.Log("[CharTwinAuthor] " + dst + " <- " + src + " type=" + mk.materialType + " profile=" + mk.diffusionProfileGuid);
        return true;
    }
}
#endif
