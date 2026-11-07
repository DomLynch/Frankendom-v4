using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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
                EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
            }
        }
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
