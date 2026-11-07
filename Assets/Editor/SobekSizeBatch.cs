using UnityEditor;
// unity-dev v108b: batch entry for SOBEKSIZE: EnemySwap build scenes, then re-save the creature prefabs.
public static class SobekSizeBatch
{
    public static void Run()
    {
        EnemySwap.BuildScenesMenu();
        AssetDatabase.SaveAssets();
        int miss = ZoneCreaturePrefab.SaveAll();
        AssetDatabase.SaveAssets();
        UnityEngine.Debug.Log("[SobekSizeBatch] done, creature prefabs missing " + miss);
    }
}
