using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System.Collections.Generic;
// unity-dev v109a: one batch for the monster swap: (1) Pitborn template capsule radius 0.70 / centre y 1.05 / height 2.10 in HadesSlice.unity (DuelFighter.radius stays 0.4),
// (2) V1 finisher clips rebuilt on the new rigs, (3) EnemySwap build scenes (Sobek scene from the template), (4) creature prefabs re-saved.
public static class MonsterBatch
{
    public static void Run()
    {
        var sc = EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity");
        GameObject pit = null;
        foreach (var g in sc.GetRootGameObjects()) if (g.name == "Pitborn") { pit = g; break; }
        if (pit == null) Debug.LogError("[MonsterBatch] no root 'Pitborn' in HadesSlice.unity");
        else
        {
            var cc = pit.GetComponent<CharacterController>();
            if (cc == null) Debug.LogError("[MonsterBatch] Pitborn has no CharacterController");
            else
            {
                Debug.Log("[MonsterBatch] Pitborn capsule before: radius " + cc.radius + " centre " + cc.center + " height " + cc.height);
                cc.radius = 0.70f; cc.center = new Vector3(cc.center.x, 1.05f, cc.center.z); cc.height = 2.10f;
                var df = pit.GetComponent<DuelFighter>();
                Debug.Log("[MonsterBatch] Pitborn capsule after: radius " + cc.radius + " centre " + cc.center + " height " + cc.height + " | DuelFighter.radius " + (df != null ? df.radius.ToString("F2") : "none"));
            }
            // v109c HEADSWAP: the Pitborn mesh has two sub-meshes (body, head): every skinned renderer of the template needs [body mat, pitborn_head_0.mat] or the head is drawn with the body atlas
            var headMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/Pitborn/Mats/pitborn_head_0.mat");
            if (headMat == null) Debug.LogError("[MonsterBatch] pitborn_head_0.mat not found");
            else
                foreach (var smr in pit.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr.sharedMesh == null) continue; int sub = smr.sharedMesh.subMeshCount; var cur = smr.sharedMaterials;
                    Debug.Log("[MonsterBatch] " + smr.name + " submeshes " + sub + " materials " + cur.Length);
                    if (sub >= 2 && (cur.Length < 2 || cur[1] != headMat)) { var nm = new Material[sub]; for (int i = 0; i < sub; i++) nm[i] = i < cur.Length ? cur[i] : headMat; nm[1] = headMat; smr.sharedMaterials = nm; Debug.Log("[MonsterBatch] " + smr.name + " head slot assigned"); }
                }
            {
                EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
            }
        }
        AssetDatabase.SaveAssets();
        AttackClipSetup.Run();   // v109c CLAWMOTION: bakes the claw_* states (and the new rows) into the controllers
        DodgeClipSetup.Run();
        AssetDatabase.SaveAssets();
        bool fin = V1FinisherClipSetup.RunChecked();
        Debug.Log("[MonsterBatch] V1 finisher clips ok=" + fin);
        AssetDatabase.SaveAssets();
        EnemySwap.BuildScenesMenu();
        AssetDatabase.SaveAssets();
        int miss = ZoneCreaturePrefab.SaveAll();
        AssetDatabase.SaveAssets();
        Debug.Log("[MonsterBatch] done, creature prefabs missing " + miss);
    }
}
