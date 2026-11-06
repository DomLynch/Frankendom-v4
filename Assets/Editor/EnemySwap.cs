using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

// LADDER v92 (laneLADDER). Any new enemy as DATA + one editor step, the enemy-side twin of JoanSwap (Hades > Joan menu). Nothing here names a character: ids, objects, glbs, controller, size come from
// Assets/Data/enemy_swap.json (the build rows) + Assets/Data/roster.json (objectName, modelGlb, animGlb, controller, guardMask, heightM, weapon, armoured). The HDRP scene is binary, so the fighter object is
// CLONED from a template character's root (default: the roster's default enemy) inside a COPY of the base scene (HadesSlice_<id>.unity), the Model is swapped for the new character's glb, the weapon socket is
// re-seated, and every scene reference to the template (camera, HUD, the player's DuelFighter.opponent, rim rigs ...) is re-pointed to the new root. GameLoop loads that scene when the opponents.json ladder
// row says  "character": "<id>"  (LadderResolve: scenePattern "HadesSlice_{id}").
//   Hades > Enemy > 0 Dry run (log only)    what the other steps would do; writes nothing, opens no scene
//   Hades > Enemy > 1 Import models + controllers   glb copied to roster modelGlb (when sourceGlb is set), controller = copy of the template's with clips re-pointed by name, guard mask copy, folders
//   Hades > Enemy > 2 Build scenes          one scene per enemy row (fresh copy of the base scene every time = idempotent), exactly one active fighter per side, checked and logged
//   Hades > Enemy > 3 Apply roster enemy to base scene   in-place switch of the base scene (roster.json "enemy" object active, the other non-player roster characters off, references re-pointed)
//   Hades > Enemy > 4 Check scenes          read-only: every enemy scene has exactly one active DuelFighter per side
// With no rows in enemy_swap.json "enemies" every step logs "nothing to do" and returns BEFORE touching an asset or a scene (the 'no new enemy => nothing changes' guarantee).
public static class EnemySwap
{
    [Serializable] public class MatRow { public string glbMaterial = "", mat = "", tex = "", ext = "jpg"; public float smoothness = 0.25f, alphaClip = 0f; public bool doubleSided = false; }
    [Serializable]
    public class Row
    {
        public string id = "";                     // roster.json characters[].id of the new enemy
        public string sourceGlb = "", sourceAnimGlb = "";   // optional: copied to the roster row's modelGlb / animGlb when those are missing or older. "" = the roster glb is already in Assets
        public string templateId = "";             // roster id whose scene root is cloned ("" = cfg.templateId, then the roster default enemy)
        public string scene = "";                  // "" = cfg.scenePattern
        public bool inPlace = false;               // true = build into cfg.baseScene (template stays, inactive) instead of a scene copy
        public bool pruneOthers = true;            // own scene: delete the template root and the other non-player roster characters (only this enemy's meshes/textures stay in memory); false = leave them inactive
        public bool keepWeapon = true;             // clone the template's weapon socket onto the new right hand; false = natural weapons / none
        public string rightHand = "mixamorig:RightHand", middle = "mixamorig:RightHandMiddle1", socket = "WeaponSocket_R";
        public float socketReach = 0.55f, glbHeightM = 0f;   // glbHeightM: rest-pose skinned height, used only when BakeMesh cannot measure
        public string[] hideNodePrefixes = new string[0];
        public MatRow[] materials = new MatRow[0];
    }
    [Serializable]
    public class Cfg
    {
        public string baseScene = "Assets/Scenes/HadesSlice.unity", scenePattern = "Assets/Scenes/HadesSlice_{id}.unity", templateId = "";
        // HCFIX: no 'layer' here. The fighter layer is character_setup.json rigs[].layer (CharacterSetup.Rig), looked up per enemy by FighterLayer().
        public Row[] enemies = new Row[0];
    }

    const string DataPath = "Assets/Data/enemy_swap.json";

    /// <summary>HCFIX: the fighter layer of this enemy = character_setup.json rig profile (characters[].rig, else the first rig), the same value CharacterSetup passes to JoanSwap. One source, no copy in enemy_swap.json.</summary>
    static int FighterLayer(string id)
    {
        var d = CharacterSetup.Load(); CharacterSetup.Rig rig = null;
        foreach (var c in d.characters) if (c != null && c.id == id) { foreach (var g in d.rigs) if (g != null && g.id == c.rig) rig = g; break; }
        if (rig == null && d.rigs.Length > 0) rig = d.rigs[0];
        return (rig != null ? rig : new CharacterSetup.Rig()).layer;
    }

    static Cfg Load()
    {
        if (!File.Exists(DataPath)) { Debug.Log("ENEMYSWAP no " + DataPath + ": nothing to do"); return null; }
        Cfg c = null;
        try { c = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath)); } catch (Exception e) { Debug.LogError("ENEMYSWAP bad " + DataPath + ": " + e.Message); return null; }
        if (c == null) { Debug.LogError("ENEMYSWAP bad " + DataPath); return null; }
        if (c.enemies == null) c.enemies = new Row[0];
        return c;
    }

    /// <summary>cfg, or null when there is nothing to do (no file / no rows). Logs the reason.</summary>
    static Cfg LoadWithRows(string step)
    {
        var c = Load(); if (c == null) return null;
        if (c.enemies.Length == 0) { Debug.Log("ENEMYSWAP " + step + ": 0 enemy rows in enemy_swap.json, nothing to do (no asset or scene touched)"); return null; }
        return c;
    }

    static string TemplateOf(Cfg c, Row r) { return !string.IsNullOrEmpty(r.templateId) ? r.templateId : !string.IsNullOrEmpty(c.templateId) ? c.templateId : Roster.DefaultEnemyId; }
    public static string ScenePathOf(Cfg c, Row r)
    {
        if (r.inPlace) return c.baseScene;
        return !string.IsNullOrEmpty(r.scene) ? r.scene : c.scenePattern.Replace("{id}", r.id);
    }

    /// <summary>The scene list for BuildPipeline.BuildPlayer: the base scene first, then every enemy scene file that exists. With no enemy rows it is exactly { baseScene } (the old literal).</summary>
    public static string[] BuildScenes(string baseScene)
    {
        var c = Load(); var found = new List<string>();
        if (c != null && c.enemies != null)
            foreach (var r in c.enemies)
            {
                if (r == null || string.IsNullOrEmpty(r.id) || r.inPlace) continue;
                string p = ScenePathOf(c, r);
                if (File.Exists(p)) found.Add(p); else Debug.LogWarning("ENEMYSWAP BuildScenes: " + p + " does not exist yet (run Hades > Enemy > 2 Build scenes); the build will not contain enemy '" + r.id + "'");
            }
        return LadderResolve.SceneList(baseScene, found.ToArray());
    }

    static void EnsureFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    static string Short(string clipName) { int i = clipName.IndexOf('|'); return i >= 0 ? clipName.Substring(i + 1) : clipName; }

    // ------------------------------------------------------------------ 0 dry run
    [MenuItem("Hades/Enemy/0 Dry run (log only)")]
    public static void DryRun()
    {
        var cfg = LoadWithRows("dry run"); if (cfg == null) return;
        Roster.Reload(); int bad = 0;
        foreach (var row in cfg.enemies)
        {
            if (row == null) continue;
            var sb = new System.Text.StringBuilder("ENEMYSWAP dry run '" + row.id + "': ");
            if (string.IsNullOrEmpty(row.id) || !Roster.Has(row.id)) { Debug.LogError(sb + "NO roster.json row with that id (add it first)"); bad++; continue; }
            var er = Roster.Get(row.id); string tid = TemplateOf(cfg, row); var tr = Roster.Get(tid);
            if (!Roster.Has(tid)) { sb.Append("template '" + tid + "' has no roster row; "); bad++; }
            if (er.id == Roster.PlayerId) { sb.Append("is the PLAYER id; "); bad++; }
            bool haveSrc = !string.IsNullOrEmpty(row.sourceGlb) && File.Exists(row.sourceGlb);
            if (!File.Exists(er.modelGlb) && !haveSrc) { sb.Append("modelGlb " + er.modelGlb + " missing and no readable sourceGlb; "); bad++; }
            sb.Append("model " + er.modelGlb + (File.Exists(er.modelGlb) ? " (exists)" : " (will be copied from " + row.sourceGlb + ")") + "; controller " + er.controller + (File.Exists(er.controller) ? " (exists)" : " (will copy " + tr.controller + ")") + "; scene " + ScenePathOf(cfg, row) + (File.Exists(ScenePathOf(cfg, row)) ? " (will be rebuilt from " + cfg.baseScene + ")" : " (new copy of " + cfg.baseScene + ")") + "; template " + tid + "; height " + er.heightM.ToString("F2") + " m");
            Debug.Log(sb.ToString());
        }
        Debug.Log("ENEMYSWAP dry run: " + cfg.enemies.Length + " row(s), " + bad + " problem(s); nothing was written");
    }

    // ------------------------------------------------------------------ 1 import
    class Ctx { public Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>(); public string from = ""; public int n, miss; }

    static void CopyIfNewer(string src, string dst)
    {
        if (string.IsNullOrEmpty(src) || !File.Exists(src) || string.IsNullOrEmpty(dst)) return;
        if (File.Exists(dst) && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dst)) return;
        EnsureFolder(Path.GetDirectoryName(dst).Replace('\\', '/'));
        File.Copy(src, dst, true);
        Debug.Log("ENEMYSWAP copied " + src + " -> " + dst);
    }

    [MenuItem("Hades/Enemy/1 Import models + controllers")]
    public static void ImportModels()
    {
        var cfg = LoadWithRows("import"); if (cfg == null) return;
        Roster.Reload();
        foreach (var row in cfg.enemies) if (row != null) ImportOne(cfg, row);
        AssetDatabase.SaveAssets();
    }

    static void ImportOne(Cfg cfg, Row row)
    {
        if (string.IsNullOrEmpty(row.id) || !Roster.Has(row.id)) { Debug.LogError("ENEMYSWAP import: '" + row.id + "' has no roster.json row"); return; }
        var er = Roster.Get(row.id); string tid = TemplateOf(cfg, row);
        if (!Roster.Has(tid)) { Debug.LogError("ENEMYSWAP import: template '" + tid + "' has no roster.json row"); return; }
        var tr = Roster.Get(tid);
        if (er.modelGlb.Length == 0) { Debug.LogError("ENEMYSWAP import: roster row '" + row.id + "' has no modelGlb"); return; }
        string dir = Path.GetDirectoryName(er.modelGlb).Replace('\\', '/');
        EnsureFolder(dir); EnsureFolder(dir + "/Mats"); EnsureFolder(dir + "/Tex");
        if (!string.IsNullOrEmpty(er.finisherClipFolder)) EnsureFolder(er.finisherClipFolder);
        CopyIfNewer(row.sourceGlb, er.modelGlb); CopyIfNewer(row.sourceAnimGlb, er.animGlb);
        if (!File.Exists(er.modelGlb)) { Debug.LogError("ENEMYSWAP " + er.modelGlb + " missing (set sourceGlb in enemy_swap.json, or build it with the combat-glb script and put it there)"); return; }
        AssetDatabase.ImportAsset(er.modelGlb, ImportAssetOptions.ForceSynchronousImport);
        if (!string.IsNullOrEmpty(er.animGlb) && File.Exists(er.animGlb)) AssetDatabase.ImportAsset(er.animGlb, ImportAssetOptions.ForceSynchronousImport);
        if (string.IsNullOrEmpty(er.controller)) { Debug.LogError("ENEMYSWAP roster row '" + row.id + "' has no controller path"); return; }
        if (!File.Exists(er.controller)) { if (!AssetDatabase.CopyAsset(tr.controller, er.controller)) { Debug.LogError("ENEMYSWAP controller copy failed " + tr.controller + " -> " + er.controller); return; } }
        if (!string.IsNullOrEmpty(er.guardMask) && !File.Exists(er.guardMask) && File.Exists(tr.guardMask)) AssetDatabase.CopyAsset(tr.guardMask, er.guardMask);
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(er.controller);
        if (ctrl == null) { Debug.LogError("ENEMYSWAP controller not loadable " + er.controller); return; }
        var cx = new Ctx { from = tr.modelGlb };
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(er.modelGlb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview")) cx.clips[Short(c.name)] = c; }
        foreach (var l in ctrl.layers) WalkSm(l.stateMachine, cx);
        EditorUtility.SetDirty(ctrl);
        Debug.Log("ENEMYSWAP import '" + row.id + "': " + er.modelGlb + " clips=" + cx.clips.Count + " repointed=" + cx.n + " missing=" + cx.miss + " controller=" + er.controller + " (a state whose clip is missing keeps the template clip; attack / dodge / finisher bakes: Tools > Combat menus, see NEW_ENEMY_CHECKLIST.md)");
    }

    static void WalkSm(AnimatorStateMachine sm, Ctx cx)
    {
        foreach (var cs in sm.states) { var st = cs.state; var m = Remap(st.motion, cx); if (m != st.motion) { st.motion = m; EditorUtility.SetDirty(st); } }
        foreach (var sub in sm.stateMachines) WalkSm(sub.stateMachine, cx);
    }

    static Motion Remap(Motion m, Ctx cx)
    {
        if (m == null) return null;
        var clip = m as AnimationClip;
        if (clip != null)
        {
            if (AssetDatabase.GetAssetPath(clip) != cx.from) return m;   // only clips that come from the template's combat glb
            AnimationClip j;
            if (cx.clips.TryGetValue(Short(clip.name), out j)) { cx.n++; return j; }
            cx.miss++; Debug.LogWarning("ENEMYSWAP no clip '" + Short(clip.name) + "' in the new combat glb: state keeps the template clip");
            return m;
        }
        var bt = m as BlendTree;
        if (bt != null) { var ch = bt.children; for (int i = 0; i < ch.Length; i++) ch[i].motion = Remap(ch[i].motion, cx); bt.children = ch; }
        return m;
    }

    // ------------------------------------------------------------------ 2 scenes
    static GameObject FindRoot(Scene sc, string objectName)
    {
        foreach (var g in sc.GetRootGameObjects()) if (g.name == objectName) return g;
        return null;
    }

    // world height of the visible skinned meshes at the current pose / scale (BakeMesh, so it is the real skinned height, not the bind-space bounds)
    static float WorldHeight(GameObject go)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var s in go.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (!s.gameObject.activeInHierarchy || s.sharedMesh == null) continue;
            var m = new Mesh(); s.BakeMesh(m);
            var vs = m.vertices; var tf = s.transform;
            for (int i = 0; i < vs.Length; i++) { float y = tf.TransformPoint(vs[i]).y; if (y < lo) lo = y; if (y > hi) hi = y; }
            UnityEngine.Object.DestroyImmediate(m);
        }
        return hi > lo ? hi - lo : 0f;
    }

    static Transform FindBone(Transform root, string n) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t; return null; }

    [MenuItem("Hades/Enemy/2 Build scenes")]
    public static void BuildScenesMenu()
    {
        var cfg = LoadWithRows("build scenes"); if (cfg == null) return;
        Roster.Reload();
        int ok = 0;
        foreach (var row in cfg.enemies) if (row != null && BuildOne(cfg, row)) ok++;
        Debug.Log("ENEMYSWAP built " + ok + " / " + cfg.enemies.Length + " enemy scene(s). Next: Hades > Enemy > 4 Check scenes; the build picks the scenes up through EnemySwap.BuildScenes (BuildPlayer hunk).");
    }

    static bool BuildOne(Cfg cfg, Row row)
    {
        if (string.IsNullOrEmpty(row.id) || !Roster.Has(row.id)) { Debug.LogError("ENEMYSWAP build: '" + row.id + "' has no roster.json row"); return false; }
        if (row.id == Roster.PlayerId) { Debug.LogError("ENEMYSWAP build: '" + row.id + "' is the player"); return false; }
        var er = Roster.Get(row.id); string tid = TemplateOf(cfg, row);   int fLayer = FighterLayer(row.id);   // HCFIX: character_setup.json rig profile
        if (!Roster.Has(tid)) { Debug.LogError("ENEMYSWAP build: template '" + tid + "' has no roster.json row"); return false; }
        var tr = Roster.Get(tid);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(er.modelGlb);
        if (prefab == null) { Debug.LogError("ENEMYSWAP model not imported " + er.modelGlb + " (run menu 1)"); return false; }
        var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(er.controller);
        if (ctrl == null) { Debug.LogError("ENEMYSWAP controller not loadable " + er.controller + " (run menu 1)"); return false; }

        // the target scene: a FRESH copy of the base scene every run (so a rebuild can never carry a second root), or the base scene itself for inPlace
        string path = ScenePathOf(cfg, row);
        if (!row.inPlace)
        {
            EditorSceneManager.OpenScene(cfg.baseScene);   // leave any scene we are about to delete
            if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
            if (!AssetDatabase.CopyAsset(cfg.baseScene, path)) { Debug.LogError("ENEMYSWAP could not copy " + cfg.baseScene + " -> " + path); return false; }
            AddToBuildSettings(path);
        }
        var sc = EditorSceneManager.OpenScene(path);
        var tRoot = FindRoot(sc, tr.objectName);
        if (tRoot == null) { Debug.LogError("ENEMYSWAP template object '" + tr.objectName + "' not in " + path); return false; }
        var olds = new List<GameObject> { tRoot };   // everything that is replaced; references to each are re-pointed to the new root
        var same = FindRoot(sc, er.objectName);
        if (same != null && same != tRoot) { same.name = er.objectName + "_old"; olds.Add(same); }   // inPlace rebuild: the previous object of this enemy is kept until every reference is re-pointed
        else if (same == tRoot) tRoot.name = er.objectName + "_tpl";
        var tAnim = tRoot.GetComponentInChildren<Animator>(true);
        if (tAnim == null) { Debug.LogError("ENEMYSWAP template has no Animator"); return false; }
        tRoot.SetActive(true);
        float tH = WorldHeight(tRoot);
        var tSock = FindBone(tRoot.transform, row.socket);
        var tRend = tRoot.GetComponentInChildren<Renderer>(false); uint tLayerMask = tRend != null ? tRend.renderingLayerMask : 0xFFFFFFFFu;

        var root = UnityEngine.Object.Instantiate(tRoot); root.name = er.objectName;
        var cAnim = root.GetComponentInChildren<Animator>(true);
        var cModel = cAnim.transform; var parent = cModel.parent; string modelName = cModel.name;
        Vector3 lp = cModel.localPosition; Quaternion lr = cModel.localRotation; Vector3 ls = cModel.localScale; int sib = cModel.GetSiblingIndex();
        UnityEngine.Object.DestroyImmediate(cModel.gameObject);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        inst.name = modelName; inst.transform.localPosition = lp; inst.transform.localRotation = lr; inst.transform.localScale = Vector3.one; inst.transform.SetSiblingIndex(sib);
        foreach (var t in inst.GetComponentsInChildren<Transform>(true))
            foreach (var pfx in row.hideNodePrefixes) if (!string.IsNullOrEmpty(pfx) && t.name.StartsWith(pfx)) { t.gameObject.SetActive(false); break; }
        foreach (var lg in inst.GetComponentsInChildren<Animation>(true)) UnityEngine.Object.DestroyImmediate(lg);

        // scale to heightM (0 = the template's model scale); WorldHeight at scale 1 is the correct basis (see JoanSwap v77 note: do not re-measure after scaling)
        float h1 = WorldHeight(inst); float s = ls.x;
        if (er.heightM > 0f) { if (h1 > 0.1f) s = er.heightM / h1; else if (row.glbHeightM > 0f) s = er.heightM / row.glbHeightM; }
        inst.transform.localScale = Vector3.one * s;
        Debug.Log("ENEMYSWAP height '" + row.id + "': template world " + tH.ToString("F3") + " m, new glb at scale 1 = " + h1.ToString("F3") + " m, model localScale " + s.ToString("F4") + " (roster heightM " + er.heightM.ToString("F2") + ")");

        var anim = inst.GetComponent<Animator>(); if (anim == null) anim = inst.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl; anim.applyRootMotion = tAnim.applyRootMotion; anim.cullingMode = tAnim.cullingMode; anim.updateMode = tAnim.updateMode;
        if (tAnim.GetComponent<GripPose>() != null && anim.GetComponent<GripPose>() == null) anim.gameObject.AddComponent<GripPose>();

        ApplyMaterials(inst, row, Path.GetDirectoryName(er.modelGlb).Replace('\\', '/'));
        foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = fLayer;
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = ShadowCastingMode.On; r.receiveShadows = true; r.renderingLayerMask = tLayerMask; }   // light layers: same as the template's (HdrpCharLayer only knows the scene it ran on)

        var fx = root.GetComponent<Fighter>(); if (fx != null) { fx.anim = anim; fx.model = inst.transform; }
        else Debug.LogWarning("ENEMYSWAP template root has no Fighter component");
        var pb = root.GetComponent<PlayerBrain>();
        if (pb != null) { UnityEngine.Object.DestroyImmediate(pb); Debug.Log("ENEMYSWAP removed the template's PlayerBrain (an enemy is driven by AIBrain)"); }
        if (root.GetComponent<AIBrain>() == null) Debug.LogWarning("ENEMYSWAP the new root has no AIBrain: the template must be an enemy (default = the roster's default enemy)");

        // capsule: the template's height scaled by the height ratio (radius untouched: it is the duel spacing)
        var cc = root.GetComponent<CharacterController>();
        if (cc != null && tH > 0.1f && er.heightM > 0f) { float k = er.heightM / tH; cc.height *= k; var c = cc.center; c.y *= k; cc.center = c; }

        // weapon socket: the template's socket (LODs, TrailTip) re-seated on the new right hand
        var hand = FindBone(inst.transform, row.rightHand); var mid = FindBone(inst.transform, row.middle);
        if (!row.keepWeapon) { }
        else if (tSock == null) Debug.LogWarning("ENEMYSWAP template has no " + row.socket + ": no weapon attached (keepWeapon false silences this)");
        else if (hand == null || mid == null) Debug.LogWarning("ENEMYSWAP new rig lacks " + row.rightHand + " / " + row.middle + ": no weapon attached");
        else
        {
            var sock = UnityEngine.Object.Instantiate(tSock.gameObject); sock.name = row.socket; sock.transform.SetParent(hand, false);
            Vector3 dLocal = hand.InverseTransformPoint(mid.position); float reach = dLocal.magnitude; Vector3 fdir = dLocal.normalized;
            sock.transform.localPosition = fdir * reach * row.socketReach;
            sock.transform.localRotation = Quaternion.FromToRotation(Vector3.up, fdir);
            sock.transform.localScale = Vector3.one / Mathf.Max(0.0001f, hand.lossyScale.x);
            foreach (var t in sock.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = fLayer;
            Debug.Log("ENEMYSWAP socket on " + hand.name + ": reach " + reach.ToString("F4") + " m (local)");
        }

        root.SetActive(true);
        // every scene reference to a replaced root -> the new root, then remove / switch off the replaced roots (idempotent: a second run starts from a fresh scene copy, or finds the _old root and re-points it)
        int rp = 0; foreach (var o in olds) rp += Repoint(o, root, sc);
        foreach (var o in olds)
        {
            if (row.inPlace && o == tRoot && tr.objectName != er.objectName) { o.SetActive(false); }   // the template stays in the base scene, switched off
            else UnityEngine.Object.DestroyImmediate(o);
        }
        int pruned = 0, off = 0;
        foreach (var r in Roster.All)
        {
            if (r == null || r.id == er.id || r.id == Roster.PlayerId) continue;
            var g = FindRoot(sc, r.objectName); if (g == null || g == root) continue;
            if (row.inPlace || !row.pruneOthers) { if (g.activeSelf) { g.SetActive(false); off++; } }
            else { UnityEngine.Object.DestroyImmediate(g); pruned++; }
        }
        bool sides = CheckSides(sc, Roster.PlayerObject, er.objectName, true);
        EditorSceneManager.MarkSceneDirty(sc);
        EditorSceneManager.SaveScene(sc);
        Debug.Log("ENEMYSWAP built '" + root.name + "' from '" + tr.objectName + "' in " + path + ": " + rp + " scene references re-pointed, " + pruned + " other characters deleted, " + off + " switched off, sides " + (sides ? "OK" : "FAILED") + " (scene saved)");
        return sides;
    }

    static void AddToBuildSettings(string path)
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in list) if (s.path == path) { if (!s.enabled) { s.enabled = true; EditorBuildSettings.scenes = list.ToArray(); } return; }
        list.Add(new EditorBuildSettingsScene(path, true)); EditorBuildSettings.scenes = list.ToArray();
        Debug.Log("ENEMYSWAP added " + path + " to the editor build settings");
    }

    // exactly ONE active fighter per side: one active root named playerObject, one named enemyObject, no other active DuelFighter and no duplicate root of either name. Logs every violation.
    static bool CheckSides(Scene sc, string playerObject, string enemyObject, bool repair)
    {
        int pl = 0, en = 0, other = 0; bool ok = true;
        foreach (var g in sc.GetRootGameObjects())
        {
            bool fighter = g.GetComponentInChildren<DuelFighter>(true) != null;
            if (!fighter) continue;
            if (g.name == playerObject) { if (g.activeInHierarchy) pl++; else { Debug.LogError("ENEMYSWAP [" + sc.name + "] player root '" + g.name + "' is inactive"); ok = false; } }
            else if (g.name == enemyObject) { if (g.activeInHierarchy) en++; else { Debug.LogError("ENEMYSWAP [" + sc.name + "] enemy root '" + g.name + "' is inactive"); ok = false; } }
            else if (g.activeInHierarchy)
            {
                if (repair) { g.SetActive(false); Debug.LogWarning("ENEMYSWAP [" + sc.name + "] switched off the extra active fighter '" + g.name + "'"); }
                else { other++; ok = false; Debug.LogError("ENEMYSWAP [" + sc.name + "] extra active fighter '" + g.name + "'"); }
            }
        }
        if (pl != 1) { Debug.LogError("ENEMYSWAP [" + sc.name + "] " + pl + " active player roots named '" + playerObject + "' (need exactly 1)"); ok = false; }
        if (en != 1) { Debug.LogError("ENEMYSWAP [" + sc.name + "] " + en + " active enemy roots named '" + enemyObject + "' (need exactly 1)"); ok = false; }
        return ok;
    }

    static void ApplyMaterials(GameObject inst, Row row, string dir)
    {
        var lit = Shader.Find("HDRP/Lit"); int n = 0;
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials; bool ch = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                foreach (var mr in row.materials)
                {
                    if (string.IsNullOrEmpty(mr.glbMaterial) || !mats[i].name.StartsWith(mr.glbMaterial)) continue;
                    var m = GetOrCreateMat(mr, lit, dir);
                    if (m != null) { mats[i] = m; ch = true; n++; }
                    break;
                }
            }
            if (ch) r.sharedMaterials = mats;
        }
        Debug.Log("ENEMYSWAP materials assigned " + n + " (slots without an enemy_swap.json material row keep the glb material)");
    }

    static Material GetOrCreateMat(MatRow row, Shader lit, string dir)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(row.mat);
        if (m != null) return m;
        string b = dir + "/Tex/" + row.tex + "_base." + (string.IsNullOrEmpty(row.ext) ? "jpg" : row.ext), nrm = dir + "/Tex/" + row.tex + "_normal.jpg";
        if (!File.Exists(b)) { Debug.LogWarning("ENEMYSWAP no " + b + " yet (put the texture there, then run menu 2 again): " + row.glbMaterial + " keeps the glb material"); return null; }
        AssetDatabase.ImportAsset(b); if (File.Exists(nrm)) AssetDatabase.ImportAsset(nrm);
        EnsureFolder(Path.GetDirectoryName(row.mat).Replace('\\', '/'));
        m = new Material(lit) { name = Path.GetFileNameWithoutExtension(row.mat) };
        m.SetTexture("_BaseColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(b));
        if (File.Exists(nrm)) { var ti = AssetImporter.GetAtPath(nrm) as TextureImporter; if (ti != null && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); } m.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>(nrm)); m.EnableKeyword("_NORMALMAP"); }
        m.SetFloat("_Smoothness", row.smoothness); m.SetFloat("_Metallic", 0f);
        if (row.alphaClip > 0f) { m.SetFloat("_AlphaCutoffEnable", 1f); m.SetFloat("_AlphaCutoff", row.alphaClip); m.EnableKeyword("_ALPHATEST_ON"); m.SetOverrideTag("RenderType", "TransparentCutout"); m.renderQueue = 2450; }
        if (row.doubleSided) { m.SetFloat("_DoubleSidedEnable", 1f); m.SetFloat("_CullMode", 0f); m.SetFloat("_CullModeForward", 0f); m.EnableKeyword("_DOUBLESIDED_ON"); }
        AssetDatabase.CreateAsset(m, row.mat); HDMaterial.ValidateMaterial(m);
        return m;
    }

    // ------------------------------------------------------------------ 3 apply roster enemy (in place, base scene)
    [MenuItem("Hades/Enemy/3 Apply roster enemy to base scene")]
    public static void ApplyEnemy()
    {
        var cfg = LoadWithRows("apply roster enemy"); if (cfg == null) return;
        Roster.Reload();
        var sc = EditorSceneManager.OpenScene(cfg.baseScene);
        var enemy = FindRoot(sc, Roster.EnemyObject);
        if (enemy == null) { Debug.LogError("ENEMYSWAP roster enemy object '" + Roster.EnemyObject + "' is not in " + cfg.baseScene + " (menu 2 with inPlace true builds it)"); return; }
        int refs = 0, off = 0;
        foreach (var r in Roster.All)
        {
            if (r == null || r.id == Roster.EnemyId || r.id == Roster.PlayerId) continue;
            var g = FindRoot(sc, r.objectName); if (g == null || g == enemy) continue;
            refs += Repoint(g, enemy, sc);
            if (g.activeSelf) { g.SetActive(false); off++; }
        }
        enemy.SetActive(true);
        bool ok = CheckSides(sc, Roster.PlayerObject, Roster.EnemyObject, true);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
        Debug.Log("ENEMYSWAP enemy = " + Roster.EnemyId + " (object " + enemy.name + "), references re-pointed " + refs + ", other characters switched off " + off + ", sides " + (ok ? "OK" : "FAILED"));
    }

    // ------------------------------------------------------------------ 4 check
    [MenuItem("Hades/Enemy/4 Check scenes")]
    public static void CheckScenes()
    {
        var cfg = LoadWithRows("check"); if (cfg == null) return;
        Roster.Reload(); int bad = 0;
        foreach (var row in cfg.enemies)
        {
            if (row == null || !Roster.Has(row.id)) { bad++; continue; }
            string path = ScenePathOf(cfg, row);
            if (!File.Exists(path)) { Debug.LogError("ENEMYSWAP check: " + path + " missing"); bad++; continue; }
            var sc = EditorSceneManager.OpenScene(path);
            if (!CheckSides(sc, Roster.PlayerObject, Roster.Get(row.id).objectName, false)) bad++;
        }
        Debug.Log("ENEMYSWAP check: " + cfg.enemies.Length + " scene(s), " + bad + " problem(s)");
    }

    // ------------------------------------------------------------------ re-point
    static void BuildMap(Transform from, Transform to, Dictionary<UnityEngine.Object, UnityEngine.Object> map)
    {
        var toPaths = new Dictionary<string, Transform>();
        foreach (var t in to.GetComponentsInChildren<Transform>(true)) toPaths[RelPath(t, to)] = t;
        foreach (var f in from.GetComponentsInChildren<Transform>(true))
        {
            Transform t; if (!toPaths.TryGetValue(RelPath(f, from), out t)) continue;
            map[f] = t; map[f.gameObject] = t.gameObject;
            var fcs = f.GetComponents<Component>(); var tcs = t.GetComponents<Component>(); var seen = new Dictionary<Type, int>();
            foreach (var fc in fcs)
            {
                if (fc == null) continue;
                var ty = fc.GetType(); int k; seen.TryGetValue(ty, out k); seen[ty] = k + 1;
                int cnt = 0; foreach (var tc in tcs) { if (tc != null && tc.GetType() == ty) { if (cnt == k) { map[fc] = tc; break; } cnt++; } }
            }
        }
    }

    static string RelPath(Transform t, Transform root)
    {
        var sb = new List<string>();
        while (t != null && t != root) { sb.Add(t.name); t = t.parent; }
        sb.Reverse(); return string.Join("/", sb);
    }

    // every serialized object reference in the scene (outside both characters) that points into 'from' is re-pointed to the same-path counterpart in 'to'; references with no counterpart are logged
    static int Repoint(GameObject from, GameObject to, Scene sc)
    {
        var map = new Dictionary<UnityEngine.Object, UnityEngine.Object>(); BuildMap(from.transform, to.transform, map);
        int n = 0, unmapped = 0;
        foreach (var root in sc.GetRootGameObjects())
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb.transform.IsChildOf(from.transform) || mb.transform.IsChildOf(to.transform)) continue;
                var so = new SerializedObject(mb); var it = so.GetIterator(); bool ch = false;
                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                    UnityEngine.Object tgt;
                    if (map.TryGetValue(it.objectReferenceValue, out tgt)) { it.objectReferenceValue = tgt; ch = true; n++; }
                    else
                    {
                        var oc = it.objectReferenceValue as Component; var og = it.objectReferenceValue as GameObject;
                        var ot = oc != null ? oc.transform : og != null ? og.transform : null;
                        if (ot != null && ot.IsChildOf(from.transform)) { unmapped++; Debug.LogWarning("ENEMYSWAP unmapped reference " + mb.name + "." + it.propertyPath + " -> " + ot.name); }
                    }
                }
                if (ch) { so.ApplyModifiedProperties(); EditorUtility.SetDirty(mb); }
            }
        }
        if (unmapped > 0) Debug.LogWarning("ENEMYSWAP " + unmapped + " references into '" + from.name + "' had no counterpart under '" + to.name + "' (a different rig: check camera / HUD targets)");
        return n;
    }

    // batch entry points: Unity.exe -batchmode -projectPath ... -executeMethod EnemySwap.RunAllBatch
    public static void RunAllBatch() { ImportModels(); BuildScenesMenu(); CheckScenes(); }
    public static void DryRunBatch() { DryRun(); }
}
