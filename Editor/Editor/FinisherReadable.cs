using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Makes sure the runtime finisher mesh cut (MeshCut) gets readable meshes. Data: Assets/Data/finisher_readable.json.
// Idempotent. glTFast 6.20 has no mesh Read/Write importer setting (ImportSettings only has texturesReadable), so for each model this
//  (1) sets an importer property from "importerKeys" if one exists and is false (then reimports), (2) logs every mesh's isReadable.
// Batch: Unity -batchmode -executeMethod FinisherReadable.Run -quit
public static class FinisherReadable
{
    [Serializable] class Cfg { public string[] models = new string[0]; public string[] importerKeys = new string[0]; }

    [MenuItem("Tools/Finishers/Make models readable")]
    public static void Run()
    {
        string path = Path.Combine(Application.dataPath, "Data/finisher_readable.json");
        if (!File.Exists(path)) { Debug.LogError("[FinisherReadable] missing " + path); return; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(path));
        int changed = 0, unreadable = 0, meshes = 0;
        // HCKIT3 v93: every roster character modelGlb + animGlb is checked as well (json "models" stays as extra entries), so a new roster row needs no edit of finisher_readable.json. Neutral today: the 6 roster glbs ARE the 6 listed models.
        Roster.Reload(); var list = new System.Collections.Generic.List<string>(cfg.models);
        foreach (var rc in Roster.All) { if (!string.IsNullOrEmpty(rc.modelGlb) && !list.Contains(rc.modelGlb)) list.Add(rc.modelGlb); if (!string.IsNullOrEmpty(rc.animGlb) && !list.Contains(rc.animGlb)) list.Add(rc.animGlb); }
        foreach (string m in list)
        {
            var imp = AssetImporter.GetAtPath(m);
            if (imp == null) { Debug.LogWarning("[FinisherReadable] not found: " + m); continue; }
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
                n++; if (!mesh.isReadable) { bad++; Debug.LogWarning("[FinisherReadable]   NOT readable: " + m + " :: " + mesh.name); }
            }
            meshes += n; unreadable += bad;
            Debug.Log("[FinisherReadable] " + m + " importerKey=" + keyUsed + (set ? " (SET, reimported)" : " (unchanged)") + " meshes=" + n + " unreadable=" + bad);
        }
        Debug.Log("[FinisherReadable] done: files changed=" + changed + " meshes=" + meshes + " unreadable=" + unreadable +
                  (unreadable > 0 ? "  -> MeshCut will use the bone-scale fallback for those" : "  -> all readable"));
    }
}
