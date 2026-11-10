#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// GRDSETUP (laneFPSLEVERS): Editor report for the dark GPU Resident Drawer switch (read-only). Nothing here runs by itself.
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod GrdSetup.Report -logFile TestOut\grd_report.log     READ-ONLY, writes TestOut\grd_report.txt
// The only project change the kit needs (ProjectSettings\GraphicsSettings.asset m_BrgStripping 0 -> 2 = Keep All) is a text patch made by apply_grd.py: the Editor API
// EditorGraphicsSettings.batchRendererGroupShaderStrippingMode is read-only. The HDRP asset keeps gpuResidentDrawerSettings.mode = 0 (Disabled): the drawer stays OFF in every build unless the harness passes -grd:1|2 (GrdArm.cs).
// Keep All makes the build keep every DOTS-instancing shader variant: LOOK-NEUTRAL (no pixel changes) but the build gets bigger/slower to compile and the player loads more variants.
public static class GrdSetup
{
    static string Proj => Path.GetDirectoryName(Application.dataPath);
    static string ReportPath => Path.Combine(Proj, "TestOut", "grd_report.txt");

    [MenuItem("Tools/GRD/Report (read-only)")]
    public static void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("GRDSETUP REPORT " + System.DateTime.Now.ToString("s"));
        sb.AppendLine("BatchRendererGroup Variants = " + EditorGraphicsSettings.batchRendererGroupShaderStrippingMode + " (drawer needs KeepAll)");
        sb.AppendLine("Active pipeline asset = " + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none"));
        foreach (var g in AssetDatabase.FindAssets("t:HDRenderPipelineAsset"))
        {
            var p = AssetDatabase.GUIDToAssetPath(g); var a = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(p); if (a == null) continue;
            var d = a.currentPlatformRenderPipelineSettings.gpuResidentDrawerSettings;
            sb.AppendLine("  " + p + ": drawer=" + d.mode + " gpuOcclusion=" + d.enableOcclusionCullingInCameras + " depthPrepassForOccluders=" + d.useDepthPrepassForOccluders + " litMode=" + a.currentPlatformRenderPipelineSettings.supportedLitShaderMode);
        }
        sb.AppendLine("SRP batcher (asset flag is internal; Graphics log says): see Editor.log");
        string scenePath = "Assets/Scenes/HadesSlice.unity";
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-grdScene:")) scenePath = a.Substring(10);
        if (File.Exists(Path.Combine(Proj, scenePath)))
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);   // opened, never saved
            int mesh = 0, skinned = 0, other = 0, noMat = 0, litMats = 0, otherMats = 0;
            var shaders = new Dictionary<string, int>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r is MeshRenderer) mesh++; else if (r is SkinnedMeshRenderer) skinned++; else other++;
                if (!(r is MeshRenderer)) continue;
                var ms = r.sharedMaterials; if (ms.Length == 0) { noMat++; continue; }
                foreach (var m in ms)
                {
                    if (m == null || m.shader == null) { noMat++; continue; }
                    var n = m.shader.name; shaders[n] = shaders.TryGetValue(n, out var c) ? c + 1 : 1;
                    if (n.StartsWith("HDRP/Lit")) litMats++; else otherMats++;
                }
            }
            sb.AppendLine("Scene " + scenePath + ": MeshRenderer=" + mesh + " (GRD-eligible type) SkinnedMeshRenderer=" + skinned + " (never GRD) other renderers=" + other);
            sb.AppendLine("  MeshRenderer material slots: HDRP/Lit=" + litMats + " other shader=" + otherMats + " empty=" + noMat + "  (shader-name heuristic; Lit has DOTS instancing variants, custom Furnace/* shaders do not)");
            foreach (var kv in shaders) sb.AppendLine("    " + kv.Value + " x " + kv.Key);
        }
        else sb.AppendLine("Scene not found: " + scenePath);
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, sb.ToString());
        Debug.Log("[GrdSetup] report written " + ReportPath + "\n" + sb);
    }

}
#endif

