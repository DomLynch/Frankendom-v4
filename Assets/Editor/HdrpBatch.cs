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
    public static void DeckDiag2() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); UnityEngine.Physics.SyncTransforms(); var sb = new System.Text.StringBuilder(); foreach (var r in UnityEngine.Object.FindObjectsByType<UnityEngine.Renderer>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { var p = r.transform; string path = r.name; while (p.parent != null && path.Length < 120) { p = p.parent; path = p.name + "/" + path; } if (r.bounds.center.z > 10f && r.bounds.center.z < 26f && System.Math.Abs(r.bounds.center.x) < 4f && r.bounds.max.y > -1f && r.bounds.max.y < 3f) sb.AppendLine("DECKR " + path + " top=" + r.bounds.max.y.ToString("F2") + " min=" + r.bounds.min.y.ToString("F2") + " z=" + r.bounds.center.z.ToString("F1") + " x=" + r.bounds.center.x.ToString("F1")); } UnityEngine.Debug.Log(sb.ToString()); foreach (var n in RosterEditor.FighterObjects()) { var g = UnityEngine.GameObject.Find(n); if (g != null) { var cc = g.GetComponent<UnityEngine.CharacterController>(); UnityEngine.Debug.Log("DECKF " + n + " pos=" + g.transform.position + " cc=" + (cc != null ? "r=" + cc.radius + " h=" + cc.height + " c=" + cc.center + " skin=" + cc.skinWidth : "none")); } } }
    public static void Diag3() { DeckDiag2(); var sb = new System.Text.StringBuilder(); foreach (var ps in UnityEngine.Object.FindObjectsByType<UnityEngine.ParticleSystem>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { var p = ps.transform; string path = ps.name; while (p.parent != null) { p = p.parent; path = p.name + "/" + path; } sb.AppendLine("PSYS " + path + " pos=" + ps.transform.position + " rate=" + ps.emission.rateOverTimeMultiplier + " max=" + ps.main.maxParticles + " active=" + ps.gameObject.activeInHierarchy + " play=" + ps.main.playOnAwake); } UnityEngine.Debug.Log(sb.ToString()); }
    public static void V44() { AssetDatabase.Refresh(); HdrpApproach.Run(); PropsLayout.RebuildAndSave(); DeckDiag2(); BuildPlayerCmd.Run(); }
    public static void V47() { AssetDatabase.Refresh(); HdrpWorldMats.Run(); HdrpBridgeMats.Run(); HdrpTrench.Run(); Look(); HdrpApproach.Run(); HdrpDoorway.Run(); PropsLayout.RebuildAndSave(); HdrpCharLayer.Run(); DeckDiag2(); BuildPlayerCmd.Run(); }
    public static void DeckDiag4() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); var lane = new UnityEngine.Bounds(new UnityEngine.Vector3(0f, 1f, 17.5f), new UnityEngine.Vector3(6f, 4f, 11f)); var sb = new System.Text.StringBuilder(); foreach (var r in UnityEngine.Object.FindObjectsByType<UnityEngine.Renderer>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { if (!r.enabled || r.gameObject.name.Contains("LOD1") || r.gameObject.name.Contains("LOD2")) continue; var b = r.bounds; if (!b.Intersects(lane)) continue; if (b.max.y < 0.12f || b.max.y > 3.5f) continue; var p = r.transform; string path = r.name; int d = 0; while (p.parent != null && d++ < 5) { p = p.parent; path = p.name + "/" + path; } if (r.GetComponentInParent<UnityEngine.CharacterController>() != null) continue; sb.AppendLine("LANER " + path + " minY=" + b.min.y.ToString("F2") + " maxY=" + b.max.y.ToString("F2") + " x=" + b.min.x.ToString("F1") + ".." + b.max.x.ToString("F1") + " z=" + b.min.z.ToString("F1") + ".." + b.max.z.ToString("F1")); } UnityEngine.Debug.Log(sb.ToString()); }
    public static void DeckDiag5() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); UnityEngine.Physics.SyncTransforms(); var sb = new System.Text.StringBuilder(); foreach (var pt in new[] { new UnityEngine.Vector2(0f, 12.6f), new UnityEngine.Vector2(0f, 16.2f), new UnityEngine.Vector2(1.5f, 12.6f), new UnityEngine.Vector2(-1.5f, 12.6f) }) { foreach (var h in UnityEngine.Physics.RaycastAll(new UnityEngine.Vector3(pt.x, 8f, pt.y), UnityEngine.Vector3.down, 30f)) sb.AppendLine("RAYHIT x=" + pt.x + " z=" + pt.y + " " + h.collider.name + " (" + h.collider.transform.parent?.name + ") y=" + h.point.y.ToString("F2") + " layer=" + h.collider.gameObject.layer); foreach (var r in UnityEngine.Object.FindObjectsByType<UnityEngine.Renderer>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { var b = r.bounds; if (pt.x >= b.min.x && pt.x <= b.max.x && pt.y >= b.min.z && pt.y <= b.max.z && b.max.y > -0.6f && b.min.y < 1.6f && b.size.x < 40f && r.GetComponentInParent<UnityEngine.CharacterController>() == null) sb.AppendLine("RENDER x=" + pt.x + " z=" + pt.y + " " + r.transform.parent?.name + "/" + r.name + " minY=" + b.min.y.ToString("F2") + " maxY=" + b.max.y.ToString("F2") + " enabled=" + r.enabled + " active=" + r.gameObject.activeInHierarchy); } } foreach (var n in RosterEditor.FighterObjects()) { var g = UnityEngine.GameObject.Find(n); if (g == null) continue; var hips = g.GetComponentsInChildren<UnityEngine.Transform>(true); foreach (var tr in hips) if (tr.name == "mixamorig:LeftFoot" || tr.name == "mixamorig:Hips") sb.AppendLine("BONE " + n + " " + tr.name + " y=" + tr.position.y.ToString("F2")); sb.AppendLine("ROOT " + n + " pos=" + g.transform.position + " scale=" + g.transform.lossyScale); } UnityEngine.Debug.Log(sb.ToString()); }
    public static void V50() { AssetDatabase.Refresh(); HdrpGltfMats.Refresh(); HdrpGltfMats.Run(); HdrpWorldMats.Run(); HdrpBridgeMats.Run(); Look(); HdrpDoorway.Run(); HdrpGround.Run(); PropsLayout.RebuildAndSave(); HdrpCharLayer.Run(); DeckDiag5(); BuildPlayerCmd.Run(); }
    public static void DeckDiag6() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); UnityEngine.Physics.SyncTransforms(); var area = new UnityEngine.Bounds(new UnityEngine.Vector3(0f, 0f, 15f), new UnityEngine.Vector3(18f, 100f, 38f)); var sb = new System.Text.StringBuilder(); foreach (var r in UnityEngine.Object.FindObjectsByType<UnityEngine.Renderer>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { if (!r.gameObject.activeInHierarchy || !r.enabled) continue; if (r is UnityEngine.ParticleSystemRenderer) continue; var b = r.bounds; if (!b.Intersects(area) || b.max.y <= 0.03f) continue; if (r.GetComponentInParent<UnityEngine.CharacterController>() != null) continue; var p = r.transform; string path = r.name; int d = 0; while (p.parent != null && d++ < 6) { p = p.parent; path = p.name + "/" + path; } sb.AppendLine("RENDP " + path + " minY=" + b.min.y.ToString("F2") + " maxY=" + b.max.y.ToString("F2") + " x=" + b.min.x.ToString("F1") + ".." + b.max.x.ToString("F1") + " z=" + b.min.z.ToString("F1") + ".." + b.max.z.ToString("F1")); } foreach (var c in UnityEngine.Object.FindObjectsByType<UnityEngine.Collider>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { if (!c.gameObject.activeInHierarchy || !c.enabled) continue; var b = c.bounds; if (!b.Intersects(area)) continue; if (c.GetComponentInParent<UnityEngine.CharacterController>() != null) continue; var p = c.transform; string path = c.name; int d = 0; while (p.parent != null && d++ < 6) { p = p.parent; path = p.name + "/" + path; } sb.AppendLine("COLLP " + path + " type=" + c.GetType().Name + " trig=" + c.isTrigger + " layer=" + c.gameObject.layer + " minY=" + b.min.y.ToString("F2") + " maxY=" + b.max.y.ToString("F2") + " x=" + b.min.x.ToString("F1") + ".." + b.max.x.ToString("F1") + " z=" + b.min.z.ToString("F1") + ".." + b.max.z.ToString("F1")); } System.IO.File.WriteAllText("TestOut/lane_dump_v50.txt", sb.ToString()); UnityEngine.Debug.Log("LANEDUMP written"); }
    public static void V51() { AssetDatabase.Refresh(); ControlsInputAxes.RunBatch(); AlwaysIncludeShaders.RunBatch(); AttackClipSetup.Run(); DodgeClipSetup.Run(); GuardLayerSetup.RunJoan(); HdrpGltfMats.Refresh(); HdrpGltfMats.Run(); HdrpWorldMats.Run(); HdrpBridgeMats.Run(); Look(); HdrpGround.Run(); PropsLayout.RebuildAndSave(); HdrpDoorway.Run(); HdrpCharLayer.Run(); DeckDiag6(); BuildPlayerCmd.Run(); }
    public static void DeckDiag7() { UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/HadesSlice.unity"); var sb = new System.Text.StringBuilder(); foreach (var tr in UnityEngine.Object.FindObjectsByType<UnityEngine.Transform>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None)) { if (tr.name.ToLower().Contains("paving") || tr.name == "WorldEdge" || tr.name == "WorldTerrain") { var rs = tr.GetComponentsInChildren<UnityEngine.Renderer>(true); string b = ""; foreach (var r in rs) { b += " [" + r.name + " en=" + r.enabled + " minY=" + r.bounds.min.y.ToString("F2") + " maxY=" + r.bounds.max.y.ToString("F2") + " x=" + r.bounds.min.x.ToString("F1") + ".." + r.bounds.max.x.ToString("F1") + " z=" + r.bounds.min.z.ToString("F1") + ".." + r.bounds.max.z.ToString("F1") + "]"; if (b.Length > 900) break; } sb.AppendLine("PAV " + tr.name + " active=" + tr.gameObject.activeInHierarchy + " pos=" + tr.position + " children=" + tr.childCount + " renderers=" + rs.Length + b); } } UnityEngine.Debug.Log(sb.ToString()); System.IO.File.WriteAllText("TestOut/pav_dump.txt", sb.ToString()); }
    public static void CharBuild() { AssetDatabase.Refresh(); HdrpCharLayer.Run(); BuildPlayerCmd.Run(); }
    public static void V43() { AssetDatabase.Refresh(); HdrpGltfMats.Run(); AttackClipSetup.Run(); GuardLayerSetup.Run(); BuildPlayerCmd.Run(); }
}


































