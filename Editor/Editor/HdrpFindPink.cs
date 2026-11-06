using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class HdrpFindPink
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        var seen = new HashSet<string>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { if (seen.Add("NULL " + r.name)) Debug.Log("HDRPPINK NULLMAT on " + r.transform.root.name + "/" + r.name); continue; }
                string sn = m.shader != null ? m.shader.name : "null";
                string mp = AssetDatabase.GetAssetPath(m); bool bad = !mp.EndsWith(".glb");
                if (bad && seen.Add(m.name)) Debug.Log($"HDRPPINK {m.name} shader={sn} path={AssetDatabase.GetAssetPath(m)} e.g. {r.transform.root.name}/{r.name}");
            }
    }
}