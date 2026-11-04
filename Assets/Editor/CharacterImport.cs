using UnityEditor;
using UnityEngine;

// Generic rig import: the 6 clips are baked onto each character's own 106-bone rig (see Tools/merge_clips.py).
public class CharacterImport : AssetPostprocessor
{
    static readonly string[] Loops = { "idle", "walk", "run" };

    public static void FixClips()
    {
        foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Characters/Dracula", "Assets/Characters/Pitborn" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            var mi = (ModelImporter)AssetImporter.GetAtPath(path);
            var defs = mi.defaultClipAnimations;
            var list = new System.Collections.Generic.List<ModelImporterClipAnimation>();
            foreach (var c in defs)
            {
                bool loop = System.Array.IndexOf(Loops, c.name) >= 0;
                c.loopTime = loop; c.loopPose = loop;
                c.lockRootRotation = false; c.lockRootHeightY = false; c.lockRootPositionXZ = false;
                list.Add(c);
                Debug.Log("CLIP " + path + " " + c.name + " " + c.firstFrame + "-" + c.lastFrame);
            }
            mi.clipAnimations = list.ToArray();
            mi.SaveAndReimport();
        }
    }

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith("Assets/Characters/")) return;
        var mi = (ModelImporter)assetImporter;
        mi.animationType = ModelImporterAnimationType.Generic;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.globalScale = 1f;
        mi.useFileScale = true;
        mi.importCameras = false;
        mi.importLights = false;
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        mi.optimizeGameObjects = false;
        mi.importAnimation = true;
        mi.resampleCurves = true;
    }
}
