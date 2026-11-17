using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// TEXSTREAM v1 (unitydev, Strategy GO 17 Nov): sets the importer flag streamingMipmaps on the WORLD / PROP / TERRAIN textures only, then builds the player (BuildPlayerCmd.Run).
// Scope = every 2D TextureImporter under Assets/HDRP/Masks and Assets/Env (props map their GLB textures to these assets). NOT: Characters/*, MetaHumanHead, VFX, UI, Assets/Textures (backdrop plates), sprites, cubemaps, no-mip textures.
// v2 (15:49): the first run reimported all 761 inside ONE StartAssetEditing block and the Editor climbed to 38 GB in 20 s (mem_watch stopped it at 3.6 GB free). Now the importers are reimported in small chunks (chunk size = data below, -texchunk:<n>),
// each chunk followed by UnloadUnusedAssetsImmediate + GC, with the memory logged per chunk, so the peak is one chunk's worth, not all of them.
// Reversal: -texstreamoff turns the flag OFF on the same set (a re-bake); the runtime side is switched off by texture_streaming.json "enabled": false without any rebake.
public static class TexStreamBatch
{
    static readonly string[] Roots = { "Assets/HDRP/Masks/", "Assets/Env/" };
    static int ArgInt(string key, int def) { foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith(key)) { int v; if (int.TryParse(a.Substring(key.Length), out v)) return v; } return def; }
    public static void Run()
    {
        bool off = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-texstreamoff") >= 0;
        int phase = ArgInt("-texphase:", 0);   // 1 = importer flags only, then quit (run with -nographics, no scene); 2 = the player build only; 0 = both (old behaviour)
        if (phase == 2) { BuildPlayerCmd.Run(); return; }
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);   // no HadesSlice (opening it costs 25-30 GB)
        int chunk = Mathf.Max(1, ArgInt("-texchunk:", 12));
        int already = 0, skipped = 0, seen = 0; var todo = new List<string>();
        foreach (var g in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/HDRP/Masks", "Assets/Env" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(g); bool inScope = false; foreach (var r in Roots) if (path.StartsWith(r)) inScope = true;
            if (!inScope) continue;
            var ti = AssetImporter.GetAtPath(path) as TextureImporter; if (ti == null) continue; seen++;
            if (ti.textureShape != TextureImporterShape.Texture2D || !ti.mipmapEnabled || ti.textureType == TextureImporterType.Sprite || ti.textureType == TextureImporterType.Cursor || ti.textureType == TextureImporterType.Lightmap) { skipped++; continue; }
            if (ti.streamingMipmaps == !off) { already++; continue; }
            todo.Add(path);
        }
        Debug.Log("TEXSTREAM importers: seen " + seen + ", to change " + todo.Count + ", already " + already + ", skipped " + skipped + ", chunk " + chunk + ", streamingMipmaps " + (!off));
        long bytes = 0; int done = 0;
        for (int i = 0; i < todo.Count; i += chunk)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int j = i; j < todo.Count && j < i + chunk; j++)
                {
                    var ti = AssetImporter.GetAtPath(todo[j]) as TextureImporter; if (ti == null) continue;
                    ti.streamingMipmaps = !off; ti.streamingMipmapsPriority = 0; done++;
                    try { bytes += new FileInfo(todo[j]).Length; } catch { }
                    EditorUtility.SetDirty(ti); ti.SaveAndReimport();
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            EditorUtility.UnloadUnusedAssetsImmediate(); System.GC.Collect();
            Debug.Log("TEXSTREAM chunk " + (i / chunk + 1) + "/" + ((todo.Count + chunk - 1) / chunk) + ": " + done + " done (" + (bytes / 1048576L) + " MB source), reserved " + (UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576L) + " MB, managed " + (System.GC.GetTotalMemory(false) / 1048576L) + " MB");
        }
        Debug.Log("TEXSTREAM importers done: changed " + done + " (" + (bytes / 1048576L) + " MB source)");
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        if (phase == 1) { Debug.Log("TEXSTREAM phase 1 done, quitting (no build)"); EditorApplication.Exit(0); return; }
        BuildPlayerCmd.Run();
    }
}
