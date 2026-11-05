using UnityEditor; using UnityEditor.Rendering; using UnityEngine; using UnityEngine.Rendering; using UnityEngine.Rendering.HighDefinition; using System.IO; using System.Linq;
public static class HdrpSetup {
  public static void Run() {
    Directory.CreateDirectory("Assets/HDRP");
    var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
    AssetDatabase.CreateAsset(asset, "Assets/HDRP/HDRP_Max.asset");
    GraphicsSettings.defaultRenderPipeline = asset;
    int q0 = QualitySettings.GetQualityLevel(); for (int i = 0; i < QualitySettings.names.Length; i++) { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = asset; } QualitySettings.SetQualityLevel(q0, false);
    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
    PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
    PlayerSettings.colorSpace = ColorSpace.Linear;
    LookData.Reload(); var DR = LookData.Get().dynamicRes;   // lane LOOK: dynamic-res limits + shadow request cap from look.json (defaults 60 / 100 / 256)
    var so = new SerializedObject(asset); var s = so.FindProperty("m_RenderPipelineSettings");
    void B(SerializedProperty p, string n, bool v) { var q = p.FindPropertyRelative(n); if (q == null) Debug.LogWarning("HDRP setting missing: " + n); else q.boolValue = v; }
    B(s, "supportVolumetrics", true); B(s, "supportSSR", true); B(s, "supportSSRTransparent", true); B(s, "supportSSGI", true); B(s, "supportSSAO", true);
    B(s, "supportRayTracing", true); B(s, "supportDecals", true); B(s, "supportSubsurfaceScattering", true); B(s, "supportMotionVectors", true);
    var lm = s.FindPropertyRelative("supportedLitShaderMode"); if (lm != null) lm.intValue = 1; else Debug.LogWarning("HDRP setting missing: supportedLitShaderMode");
    var hs = s.FindPropertyRelative("hdShadowInitParams"); if (hs != null) { var m = hs.FindPropertyRelative("maxShadowRequests"); if (m != null) m.intValue = DR.maxShadowRequests; B(hs, "supportScreenSpaceShadows", true); }
    var dr = s.FindPropertyRelative("dynamicResolutionSettings"); if (dr != null) { B(dr, "enabled", true); var mn = dr.FindPropertyRelative("minPercentage"); if (mn != null) mn.floatValue = DR.minPercentage; var mx = dr.FindPropertyRelative("maxPercentage"); if (mx != null) mx.floatValue = DR.maxPercentage; }
    so.ApplyModifiedProperties(); AssetDatabase.SaveAssets();
    Debug.Log("HDRPSETUP asset created; rayTracing=" + SystemInfo.supportsRayTracing + " gfx=" + SystemInfo.graphicsDeviceType + " dev=" + SystemInfo.graphicsDeviceName);
    ConvertUrpLit(); AssetDatabase.SaveAssets();
    Debug.Log("HDRPSETUP done");
  }
  static void ConvertUrpLit() {
    var hd = Shader.Find("HDRP/Lit"); int n = 0;
    foreach (var g in AssetDatabase.FindAssets("t:Material")) { var path = AssetDatabase.GUIDToAssetPath(g); var m = AssetDatabase.LoadAssetAtPath<Material>(path); if (m == null || m.shader == null || !m.shader.name.StartsWith("Universal Render Pipeline/")) continue;
      var baseMap = m.GetTexture("_BaseMap"); var col = m.GetColor("_BaseColor"); var nrm = m.GetTexture("_BumpMap"); var em = m.GetTexture("_EmissionMap"); var ec = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
      float met = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f, sm = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : 0.5f;
      m.shader = hd; m.SetTexture("_BaseColorMap", baseMap); m.SetColor("_BaseColor", col); if (nrm) m.SetTexture("_NormalMap", nrm); m.SetFloat("_Metallic", met); m.SetFloat("_Smoothness", sm);
      if (em != null || ec.maxColorComponent > 0) { m.SetTexture("_EmissiveColorMap", em); m.SetColor("_EmissiveColor", ec); }
      HDMaterial.ValidateMaterial(m); EditorUtility.SetDirty(m); n++; }
    Debug.Log("HDRPSETUP converted URP materials: " + n);
  }
}
