using UnityEditor;
using UnityEngine;

public static class TestAnim
{
    public static string Check()
    {
        var guids = AssetDatabase.FindAssets("t:Material matte_cw4_stairs");
        if (guids.Length == 0) return "not found";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guids[0]));
        var col = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.black;
        var tex = mat.HasProperty("_BaseColorMap") ? mat.GetTexture("_BaseColorMap") : null;
        float sm = mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness") : -1f;
        return "mat=" + mat.name + " shader=" + mat.shader.name + " baseColor=" + col + " tex=" + (tex != null ? tex.name : "null") + " smoothness=" + sm;
    }
}
