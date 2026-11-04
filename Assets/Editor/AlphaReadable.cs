using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// ALPHA lane B2: makes sure the runtime mesh cut (ADMeshCut) gets readable meshes. Data: Assets/Data/alpha_readable.json.
// Idempotent. glTFast 6.20 has no mesh Read/Write importer setting (ImportSettings only has texturesReadable), so for each model this
//  (1) sets an importer property from "importerKeys" if one exists and is false (then reimports), (2) logs every mesh's isReadable.
// Batch: Unity -batchmode -executeMethod AlphaReadable.Run -quit
public static class AlphaReadable
{
    [Serializable] class Cfg { public string[] models = new string[0]; public string[] importerKeys = new string[0]; }

    [MenuItem("Tools/Alpha/Make models readable")]
    public static void Run()
    {
        string path = Path.Combine(Application.dataPath, "Data/alpha_readable.json");
        if (!File.Exists(path)) { Debug.LogError("[AlphaReadable] missing " + path); return; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(path));
        int changed = 0, unreadable = 0, meshes = 0;
        foreach (string m in cfg.models)
        {
            var imp = AssetImporter.GetAtPath(m);
            if (imp == null) { Debug.LogWarning("[AlphaReadable] not found: " + m); continue; }
            bool set = false; string keyUsed = "none";
            var so = new SerializedObject(imp);
            foreach (string k in cfg.importerKeys)
            {
                var p = so.FindProperty("importSettings." + k) ?? so.FindProperty(k);
                if (p == null || p.propertyType != SerializedPropertyType.Boolean) continue;
                keyUsed = p.propertyPath;
                if (!p.boolValue) { p.boolValue = true; set = true; }
                break;
            }
            if (set) { so.ApplyModifiedPropertiesWithoutUndo(); imp.SaveAndReimport(); changed++; }
            int n = 0, bad = 0;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(m))
            {
                var mesh = o as Mesh; if (mesh == null) continue;
                n++; if (!mesh.isReadable) { bad++; Debug.LogWarning("[AlphaReadable]   NOT readable: " + m + " :: " + mesh.name); }
            }
            meshes += n; unreadable += bad;
            Debug.Log("[AlphaReadable] " + m + " importerKey=" + keyUsed + (set ? " (SET, reimported)" : " (unchanged)") + " meshes=" + n + " unreadable=" + bad);
        }
        Debug.Log("[AlphaReadable] done: files changed=" + changed + " meshes=" + meshes + " unreadable=" + unreadable +
                  (unreadable > 0 ? "  -> ADMeshCut will use the bone-scale fallback for those" : "  -> all readable"));
    }
}
