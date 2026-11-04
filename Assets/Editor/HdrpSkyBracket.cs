using UnityEngine; using UnityEngine.Rendering; using UnityEngine.Rendering.HighDefinition; using UnityEditor; using UnityEditor.SceneManagement;
public static class HdrpSkyBracket { public static void Run() {
 EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
 var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/HDRP/Global.asset"); prof.TryGet(out GradientSky g);
 foreach (var e in new[] { 12f, 14f, 16f }) { g.exposure.Override(e); HdrpShot.Run(false); System.IO.File.Copy("Shots/hdrp_wide.png", "Shots/_sky_e" + (int)e + ".png", true); }
 } }
