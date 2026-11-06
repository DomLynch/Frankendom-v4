using UnityEditor;
using UnityEngine;
public static class HdrpReimport
{
    public static void Run()
    {
        int n = 0;
        AssetDatabase.StartAssetEditing();
        try { foreach (var g in AssetDatabase.FindAssets("glob:\"*.glb\"")) { var p = AssetDatabase.GUIDToAssetPath(g); if (p.EndsWith(".glb")) { AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate); n++; } } }
        finally { AssetDatabase.StopAssetEditing(); }
        AssetDatabase.SaveAssets();
        Debug.Log("HDRPREIMPORT glb=" + n);
    }
}