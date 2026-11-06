using UnityEditor;
using UnityEditor.SceneManagement;
// unity-dev v107: batch entry for Zone2Build: opens the base scene, builds zone 2 + camp rows, SAVES the scene. (The menu only works on the open scene.)
public static class Zone2BuildBatch
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        Zone2Build.RunAndSave();
        AssetDatabase.SaveAssets();
    }
}
