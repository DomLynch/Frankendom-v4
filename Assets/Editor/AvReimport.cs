#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// AV bundle (laneAVBUNDLE), STAGED, not compiled in Unity. Editor only. Reimports every .glb/.gltf under the folders listed in Assets/Data/gltf_texture_compress.json
// includePathContains (the JSON is NOT part of the importer hash, so a widened list needs this once). No scene change. Run:
//   Unity.exe -batchmode -quit -accept-apiupdate -silent-crashes -projectPath <proj> -executeMethod AvReimport.Run -logFile TestOut\av_reimport.log
public static class AvReimport
{
    [Serializable] class Cfg { public string[] includePathContains = new string[0]; }

    public static void Run()
    {
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(Path.Combine(Application.dataPath, "Data/gltf_texture_compress.json")));
        int n = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var dir in cfg.includePathContains)
            {
                string d = dir.TrimEnd('/');
                if (!AssetDatabase.IsValidFolder(d)) { Debug.LogWarning("AVREIMPORT: not a folder " + d); continue; }
                foreach (var ext in new[] { "*.glb", "*.gltf" })
                    foreach (var f in Directory.GetFiles(d, ext, SearchOption.AllDirectories))
                    { AssetDatabase.ImportAsset(f.Replace('\\', '/'), ImportAssetOptions.ForceUpdate); n++; }
            }
        }
        finally { AssetDatabase.StopAssetEditing(); }
        AssetDatabase.SaveAssets();
        Debug.Log("AVREIMPORT done: " + n + " files in " + sw.Elapsed.TotalSeconds.ToString("F0") + " s");
    }
}
#endif
