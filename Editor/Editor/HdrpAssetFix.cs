using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// flips HDRP asset feature flags (data: names listed in Assets/Data/hdrp_asset_flags.txt, one per line, "name=true|false")
public static class HdrpAssetFix
{
    public static void Run()
    {
        var a = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>("Assets/HDRP/HDRP_Max.asset"); var so = new SerializedObject(a); var s = so.FindProperty("m_RenderPipelineSettings");
        foreach (var line in System.IO.File.ReadAllLines("Assets/Data/hdrp_asset_flags.txt"))
        {
            var kv = line.Split('='); if (kv.Length != 2) continue; var p = s.FindPropertyRelative(kv[0].Trim());
            if (p == null) { Debug.LogWarning("HDRP flag missing: " + kv[0]); continue; }
            if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = kv[1].Trim() == "true";
        }
        so.ApplyModifiedProperties(); EditorUtility.SetDirty(a); AssetDatabase.SaveAssets(); Debug.Log("HDRPASSET flags applied");
    }
}