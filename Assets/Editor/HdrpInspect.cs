using UnityEditor;
using UnityEngine;

public static class HdrpInspect
{
    public static void Steps()
    {
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r.name != "steps" && !r.transform.parent.name.Contains("steps")) continue;
            foreach (var m in r.sharedMaterials) Debug.Log($"HDRPINSPECT {r.transform.root.name}/{r.name} mat={m.name} shader={m.shader.name} metal={m.GetFloat("_Metallic")} smooth={m.GetFloat("_Smoothness")} mask={(m.GetTexture("_MaskMap") != null)} base={(m.GetTexture("_BaseColorMap") != null ? m.GetTexture("_BaseColorMap").name : "none")} col={m.GetColor("_BaseColor")} dbl={m.GetFloat("_DoubleSidedEnable")} normal={(m.GetTexture("_NormalMap") != null)} layerMask={r.renderingLayerMask}");
        }
    }
}