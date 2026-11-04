using UnityEditor;
public static class HdrpBatch
{
    public static void Look() { AssetDatabase.Refresh(); foreach (var g in AssetDatabase.FindAssets("ue_ t:Texture2D", new[] { "Assets/VFX/Flipbooks" })) AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(g), ImportAssetOptions.ForceUpdate); AssetDatabase.SaveAssets(); HdrpScene.Run(); HdrpFixes.Run(); HdrpFurnace.RunDefault(); HdrpGateway.Run(); HdrpShadowAudit.Run(); HdrpShot.Run(); }
    public static void Run() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); HdrpFixes.Run(); HdrpFurnace.RunDefault(); HdrpShot.Run(); BuildPlayerCmd.Run(); }
    public static void Audit() { HdrpFixes.Run(); HdrpFurnace.RunDefault(); HdrpShadowAudit.Run(); }
    public static void LookBuild() { Look(); BuildPlayerCmd.Run(); }
    public static void Sky() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); HdrpSkyDiag.Run(); }
    public static void Door() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); HdrpDoorDiag.Run(); }
    public static void SkyB() { HdrpSkyBracket.Run(); }
    public static void Big() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); Look(); BuildPlayerCmd.Run(); }
    public static void Final() { Look(); BuildPlayerCmd.Run(); }
    public static void V22() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); Look(); BuildPlayerCmd.Run(); }
    public static void V23() { AssetDatabase.Refresh(); PropsLayout.RebuildAndSave(); HdrpGltfMats.Refresh(); Look(); BuildPlayerCmd.Run(); }
    public static void V25() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); Look(); BuildPlayerCmd.Run(); }
    public static void Noop() { AssetDatabase.Refresh(); AssetDatabase.SaveAssets(); }
    public static void World() { AssetDatabase.Refresh(); PropsLayout.RebuildAndSave(); HdrpGltfMats.Refresh(); HdrpWorldMats.Run(); Look(); }
    public static void FixesBuild() { HdrpFixes.Run(); BuildPlayerCmd.Run(); }
    public static void Build() { BuildPlayerCmd.Run(); }
    public static void V40() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); HdrpBridgeMats.Run(); Look(); GuardLayerSetup.Run(); HdrpCharLayer.Run(); BuildPlayerCmd.Run(); }
    public static void V41() { AssetDatabase.Refresh(); AttackClipSetup.Run(); GuardLayerSetup.Run(); BuildPlayerCmd.Run(); }
    public static void V41b() { AssetDatabase.Refresh(); HdrpFixes.Run(); HdrpBridgeMats.Run(); BuildPlayerCmd.Run(); }
    public static void V43m() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); HdrpGltfMats.Run(); HdrpWorldMats.Run(); HdrpBridgeMats.Run(); HdrpTrench.Run(); HdrpApproach.Run(); PropsLayout.RebuildAndSave(); AttackClipSetup.Run(); GuardLayerSetup.Run(); Look(); HdrpCharLayer.Run(); DeckDiag(); BuildPlayerCmd.Run(); }
    public static void DeckDiag() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); UnityEngine.Physics.SyncTransforms(); foreach (var z in new[] { 2f, 6f, 8.5f, 10f, 12f, 14f, 16f, 18f, 19f, 20f, 21f, 22f, 23f, 24f, 26f, 28f }) { string s = "DECKY z=" + z + " :"; foreach (var x in new[] { 0f, 2f, -2f }) { s += UnityEngine.Physics.Raycast(new UnityEngine.Vector3(x, 30f, z), UnityEngine.Vector3.down, out var hit, 60f) ? " x" + x + "=" + hit.point.y.ToString("F2") + "(" + hit.collider.name + ")" : " x" + x + "=none"; } UnityEngine.Debug.Log(s); } }
    public static void DeckDiag2() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); UnityEngine.Physics.SyncTransforms(); var sb = new System.Text.StringBuilder(); foreach (var r in UnityEngine.Object.FindObjectsByType<UnityEngine.Renderer>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { var p = r.transform; string path = r.name; while (p.parent != null && path.Length < 120) { p = p.parent; path = p.name + "/" + path; } if (r.bounds.center.z > 10f && r.bounds.center.z < 26f && System.Math.Abs(r.bounds.center.x) < 4f && r.bounds.max.y > -1f && r.bounds.max.y < 3f) sb.AppendLine("DECKR " + path + " top=" + r.bounds.max.y.ToString("F2") + " min=" + r.bounds.min.y.ToString("F2") + " z=" + r.bounds.center.z.ToString("F1") + " x=" + r.bounds.center.x.ToString("F1")); } UnityEngine.Debug.Log(sb.ToString()); foreach (var n in new[] { "Dracula", "Pitborn" }) { var g = UnityEngine.GameObject.Find(n); if (g != null) { var cc = g.GetComponent<UnityEngine.CharacterController>(); UnityEngine.Debug.Log("DECKF " + n + " pos=" + g.transform.position + " cc=" + (cc != null ? "r=" + cc.radius + " h=" + cc.height + " c=" + cc.center + " skin=" + cc.skinWidth : "none")); } } }
    public static void Diag3() { DeckDiag2(); var sb = new System.Text.StringBuilder(); foreach (var ps in UnityEngine.Object.FindObjectsByType<UnityEngine.ParticleSystem>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { var p = ps.transform; string path = ps.name; while (p.parent != null) { p = p.parent; path = p.name + "/" + path; } sb.AppendLine("PSYS " + path + " pos=" + ps.transform.position + " rate=" + ps.emission.rateOverTimeMultiplier + " max=" + ps.main.maxParticles + " active=" + ps.gameObject.activeInHierarchy + " play=" + ps.main.playOnAwake); } UnityEngine.Debug.Log(sb.ToString()); }
    public static void V44() { AssetDatabase.Refresh(); HdrpApproach.Run(); PropsLayout.RebuildAndSave(); DeckDiag2(); BuildPlayerCmd.Run(); }
    public static void V47() { AssetDatabase.Refresh(); HdrpWorldMats.Run(); HdrpBridgeMats.Run(); HdrpTrench.Run(); Look(); HdrpApproach.Run(); HdrpDoorway.Run(); PropsLayout.RebuildAndSave(); HdrpCharLayer.Run(); DeckDiag2(); BuildPlayerCmd.Run(); }
    public static void CharBuild() { AssetDatabase.Refresh(); HdrpCharLayer.Run(); BuildPlayerCmd.Run(); }
    public static void V43() { AssetDatabase.Refresh(); HdrpGltfMats.Run(); AttackClipSetup.Run(); GuardLayerSetup.Run(); BuildPlayerCmd.Run(); }
}




























