using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// v79 diagnostic (read-only, no scene save): lists renderers that can cast "ghost" shadows: ShadowsOnly renderers, anything still active under a roster character that is not in the fight,
// and every root object named like a roster character with its active state. Batch: -executeMethod SceneDump.Run, writes TestOut/scene_dump.txt
public static class SceneDump
{
    static string Path(Transform t) { var sb = new StringBuilder(t.name); while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); } return sb.ToString(); }
    public static void Run()
    {
        var sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); var sb = new StringBuilder();
        sb.AppendLine("== roots (active) ==");
        foreach (var g in sc.GetRootGameObjects()) { int n = g.GetComponentsInChildren<Renderer>(true).Length, na = 0; foreach (var r in g.GetComponentsInChildren<Renderer>(true)) if (r.enabled && r.gameObject.activeInHierarchy) na++; if (n > 0 && (g.name.Contains("Dracula") || g.name.Contains("Joan") || g.name.Contains("Pitborn") || g.name.Contains("Crimson"))) sb.AppendLine(g.name + " activeSelf=" + g.activeSelf + " renderers=" + n + " activeRenderers=" + na); }
        sb.AppendLine("== ALL roots with skinned meshes (name, activeSelf, pos, active SMRs, SMR names) =="); foreach (var g in sc.GetRootGameObjects()) { var sm = g.GetComponentsInChildren<SkinnedMeshRenderer>(true); if (sm.Length == 0) continue; int a = 0; var nm = new StringBuilder(); foreach (var s in sm) { if (s.enabled && s.gameObject.activeInHierarchy) a++; nm.Append(s.name + ","); } sb.AppendLine(g.name + " activeSelf=" + g.activeSelf + " pos=" + g.transform.position + " activeSMR=" + a + "/" + sm.Length + " [" + nm + "]"); }
        sb.AppendLine("== all GameObjects named Joan* / Dracula* anywhere =="); foreach (var tr in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (tr.name.StartsWith("Joan") || tr.name.StartsWith("Dracula")) sb.AppendLine(Path(tr) + " activeSelf=" + tr.gameObject.activeSelf + " inHier=" + tr.gameObject.activeInHierarchy);
        sb.AppendLine("== ShadowsOnly renderers ==");
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) sb.AppendLine(Path(r.transform) + " enabled=" + r.enabled + " active=" + r.gameObject.activeInHierarchy);
        sb.AppendLine("== active renderers named Dracula / Crimson / ghost under any root ==");
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) { string p = Path(r.transform); if (r.enabled && r.shadowCastingMode != ShadowCastingMode.Off && (p.Contains("Dracula") || p.Contains("Crimson") || p.ToLower().Contains("ghost") || p.ToLower().Contains("proxy"))) sb.AppendLine(p + " mode=" + r.shadowCastingMode); }
        sb.AppendLine("== lights with shadows near the arena ==");
        foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (l.shadows != LightShadows.None) sb.AppendLine(Path(l.transform) + " type=" + l.type + " shadows=" + l.shadows + " pos=" + l.transform.position);
        System.IO.File.WriteAllText("TestOut/scene_dump.txt", sb.ToString()); Debug.Log("SCENEDUMP written");
    }
}
