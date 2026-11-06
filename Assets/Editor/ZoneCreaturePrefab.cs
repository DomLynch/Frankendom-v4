using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// ZONES v1 (editor only): Hades > Zones > Save creature prefabs. ZONES-ATTACH v1 (INT107): creature.scene (zones.json creatures[].scene) names the enemy scene; empty = the zone's scene.
// For every zones.json creature with source "resources": open that scene ADDITIVELY, find the creature's root object by its roster objectName, save it as Assets/Resources/<creature.path>.prefab, close the scene.
// SaveAll() returns the number of resources creatures whose prefab file does NOT exist afterwards (0 = all there). BatchSave() = SaveAll + exit code (0 ok, 3 missing) for the live command list.
public static class ZoneCreaturePrefab
{
    [MenuItem("Hades/Zones/Save creature prefabs")]
    static void Run() { int miss = SaveAll(); if (miss > 0) Debug.LogError("[ZoneCreaturePrefab] " + miss + " creature prefab(s) missing"); }

    public static void BatchSave() { int miss = SaveAll(); EditorApplication.Exit(miss == 0 ? 0 : 3); }

    public static int SaveAll()
    {
        var T = ZoneData.Get(); int saved = 0, missing = 0;
        foreach (var c in T.creatures)
        {
            if (c == null || c.source != "resources" || c.path.Length == 0) continue;
            string dst = "Assets/Resources/" + c.path + ".prefab";
            string sceneName = c.scene;
            if (string.IsNullOrEmpty(sceneName)) foreach (var z in T.zones) if (z != null && z.scene.Length > 0) foreach (var s in z.spawns) if (s != null) foreach (var p in s.picks) if (p != null && p.creature == c.id && string.IsNullOrEmpty(sceneName)) sceneName = z.scene;
            string scenePath = string.IsNullOrEmpty(sceneName) ? null : "Assets/Scenes/" + sceneName + ".unity";
            if (scenePath == null || !File.Exists(scenePath)) { Debug.LogError("[ZoneCreaturePrefab] " + c.id + ": enemy scene not found (" + scenePath + ")"); if (!File.Exists(dst)) missing++; continue; }
            Scene sc = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            string obj = Roster.Get(c.id).objectName; GameObject root = null;
            foreach (var r in sc.GetRootGameObjects()) if (r.name.Contains(obj)) { root = r; break; }
            if (root == null) { Debug.LogError("[ZoneCreaturePrefab] " + c.id + ": no root named '" + obj + "' in " + scenePath); EditorSceneManager.CloseScene(sc, true); if (!File.Exists(dst)) missing++; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            PrefabUtility.SaveAsPrefabAsset(root, dst); saved++; Debug.Log("[ZoneCreaturePrefab] saved " + dst);
            EditorSceneManager.CloseScene(sc, true);
            if (!File.Exists(dst)) missing++;
        }
        AssetDatabase.Refresh(); Debug.Log("[ZoneCreaturePrefab] done, " + saved + " prefab(s), " + missing + " missing"); return missing;
    }
}
