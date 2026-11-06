using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
// unity-dev v107 diagnostic: list root GameObjects (active, children count, fighter-ish components) of the base scene and the Sobek scene.
public static class SceneRootsDump
{
    public static void Run()
    {
        var sb = new StringBuilder();
        foreach (var path in new[] { "Assets/Scenes/HadesSlice.unity", "Assets/Scenes/HadesSlice_Sobek.unity" })
        {
            var sc = EditorSceneManager.OpenScene(path);
            sb.AppendLine("== " + path);
            foreach (var g in sc.GetRootGameObjects())
            {
                int smr = g.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                bool hasBrain = g.GetComponentInChildren<AIBrain>(true) != null, hasDuel = g.GetComponentInChildren<DuelFighter>(true) != null;
                sb.AppendLine(g.name + " | active " + g.activeSelf + " | skinned " + smr + " | AIBrain " + hasBrain + " | DuelFighter " + hasDuel);
            }
        }
        File.WriteAllText("TestOut/scene_roots.txt", sb.ToString());
    }
}
