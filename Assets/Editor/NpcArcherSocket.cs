using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// FK/COO archers armed: add WeaponSocket_R under mixamorig:RightHand on the creature prefabs that have none (NpcSlagCrossbowman, NpcCedarArcher, NpcHuntsman), local transform copied from the NpcPitGladiator socket
// (the armed NPC template). Idempotent: skipped when the prefab already has the socket. Batch: -executeMethod NpcArcherSocket.Run
public static class NpcArcherSocket
{
    static readonly string[] Ids = { "NpcSlagCrossbowman", "NpcCedarArcher", "NpcHuntsman" };
    static Transform Find(Transform t, string name) { if (t.name == name) return t; foreach (Transform c in t) { var r = Find(c, name); if (r != null) return r; } return null; }
    [MenuItem("Hades/Enemy/Add archer weapon sockets")]
    public static void Run()
    {
        var tp = "Assets/Resources/Creatures/NpcPitGladiator.prefab"; var troot = PrefabUtility.LoadPrefabContents(tp);
        var ts = Find(troot.transform, "WeaponSocket_R");
        if (ts == null) { Debug.LogError("NpcArcherSocket: template has no WeaponSocket_R"); PrefabUtility.UnloadPrefabContents(troot); return; }
        Vector3 lp = ts.localPosition, ls = ts.localScale; Quaternion lr = ts.localRotation; string parentName = ts.parent != null ? ts.parent.name : "?";
        Debug.Log("NpcArcherSocket: template socket under " + parentName + " pos " + lp + " scale " + ls);
        PrefabUtility.UnloadPrefabContents(troot);
        int added = 0;
        foreach (var id in Ids)
        {
            string p = "Assets/Resources/Creatures/" + id + ".prefab"; var root = PrefabUtility.LoadPrefabContents(p);
            if (Find(root.transform, "WeaponSocket_R") != null) { Debug.Log("NpcArcherSocket: " + id + " already has WeaponSocket_R"); PrefabUtility.UnloadPrefabContents(root); continue; }
            var hand = Find(root.transform, "mixamorig:RightHand");
            if (hand == null) { Debug.LogError("NpcArcherSocket: " + id + " has no mixamorig:RightHand"); PrefabUtility.UnloadPrefabContents(root); continue; }
            var go = new GameObject("WeaponSocket_R"); go.transform.SetParent(hand, false); go.transform.localPosition = lp; go.transform.localRotation = lr; go.transform.localScale = ls;
            PrefabUtility.SaveAsPrefabAsset(root, p); PrefabUtility.UnloadPrefabContents(root); added++; Debug.Log("NpcArcherSocket: " + id + " socket added");
        }
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); Debug.Log("NpcArcherSocket: +" + added + " sockets");
    }
}