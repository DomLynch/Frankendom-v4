using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

// JOAN v76 (laneJOAN). Player swap as data + one editor step, mirroring how Dracula's fighter is built (BuildSlice.Character in Editor_URP~: root GameObject with Fighter/Flinch/DuelFighter..., a
// "Visual" child, the glb instance named "Model" with an Animator, a WeaponSocket_R under mixamorig:RightHand with the sword LODs and GripPose on the Animator).
// The HDRP scene is binary, so the fighter object cannot be created from text: this tool CLONES the template character's root (roster id joan_swap.json "templateId") inside HadesSlice.unity,
// swaps its Model for the new character's glb, rebuilds the sword socket from the template's, and re-points the scene's references between the two characters.
// All names are DATA: Assets/Data/joan_swap.json (ids, source glb, hidden node prefixes, material map) + Assets/Data/roster.json (objectName, modelGlb, controller, heightM, guardMask).
//   Hades > Joan > 1 Import model + controller    glb -> clips on a copy of the template controller, guard mask copy, folders
//   Hades > Joan > 2 Build scene object           clone template root -> new character object (Model, scale to heightM, materials, socket)
//   Hades > Joan > 3 Apply roster player to scene the roster 'player' object is active and carries the scene references; the other roster characters (not the enemy) are switched off
// Switch back to Dracula = roster.json "player": "Dracula" + menu 3 (nothing else). No code names a character: ids come from joan_swap.json / Roster.
public static class JoanSwap
{
    [System.Serializable] public class MatRow { public string glbMaterial = "", mat = "", tex = "", ext = "jpg"; public float smoothness = 0.25f, alphaClip = 0f; public float[] tint = null; public bool doubleSided = false; }   // v84 tint: optional _BaseColor multiplier (de-red the Tripo head)   // v77: ext = base texture extension (png keeps alpha), alphaClip > 0 = alpha-tested cutout (hair cards), doubleSided
    [System.Serializable]
    public class Cfg
    {
        public string newId = "", templateId = "", sourceGlb = "", scene = "Assets/Scenes/HadesSlice.unity";
        public string rightHand = "mixamorig:RightHand", middle = "mixamorig:RightHandMiddle1", socket = "WeaponSocket_R";
        public int layer = 8; public float socketReach = 0.55f, glbHeightM = 0f;
        public string[] hideNodePrefixes = new string[0]; public MatRow[] materials = new MatRow[0];
    }

    const string DataPath = "Assets/Data/joan_swap.json";

    static Cfg Load()
    {
        if (!File.Exists(DataPath)) { Debug.LogError("JOANSWAP missing " + DataPath); return null; }
        var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath));
        if (c == null || string.IsNullOrEmpty(c.newId) || string.IsNullOrEmpty(c.templateId)) { Debug.LogError("JOANSWAP bad " + DataPath); return null; }
        return c;
    }

    static string Short(string clipName) { int i = clipName.IndexOf('|'); return i >= 0 ? clipName.Substring(i + 1) : clipName; }
    // NODRAC v93: a clip / mask under ANOTHER roster character's folder is foreign. It is re-pointed to the same-named asset in the new character's own folder, or cleared (never kept).
    static bool IsForeign(string path, Ctx cx) { if (string.IsNullOrEmpty(path)) return false; foreach (var d in cx.foreignDirs) if (path.StartsWith(d)) return true; return false; }   // NODRAC v93
    static AnimationClip OwnClipByName(string clipName, Ctx cx)   // NODRAC v93
    {
        string nm = Short(clipName);
        foreach (var g in AssetDatabase.FindAssets("t:AnimationClip", new[] { cx.ownDir }))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview") && Short(c.name) == nm) { cx.n++; return c; } }
        }
        cx.dropped++; Debug.LogWarning("JOANSWAP clip '" + nm + "' belongs to another character: state motion cleared (no same-named clip in " + cx.ownDir + " yet: run AttackClipSetup / DodgeClipSetup / FinisherClipSetup)");
        return null;
    }

    static void EnsureFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    // ------------------------------------------------------------------ 1 import
    class Ctx { public Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>(); public string from = ""; public int n, miss; public string ownDir = ""; public List<string> foreignDirs = new List<string>(); public int dropped; }   // NODRAC v93: ownDir / foreignDirs / dropped

    [MenuItem("Hades/Joan/1 Import model + controller")]
    public static void ImportModel()
    {
        var cfg = Load(); if (cfg == null) return;
        Roster.Reload(); var jr = Roster.Get(cfg.newId); var tr = Roster.Get(cfg.templateId);
        string dir = Path.GetDirectoryName(jr.modelGlb).Replace('\\', '/');
        EnsureFolder(dir); EnsureFolder(dir + "/Mats"); EnsureFolder(dir + "/Tex");
        if (!string.IsNullOrEmpty(jr.finisherClipFolder)) EnsureFolder(jr.finisherClipFolder);
        if (!File.Exists(jr.modelGlb)) { Debug.LogError("JOANSWAP " + jr.modelGlb + " missing: run _agents/laneJOAN/tools/build_joan_combat.py (it writes it from " + cfg.sourceGlb + ")"); return; }
        AssetDatabase.ImportAsset(jr.modelGlb, ImportAssetOptions.ForceSynchronousImport);
        if (!string.IsNullOrEmpty(jr.animGlb) && File.Exists(jr.animGlb)) AssetDatabase.ImportAsset(jr.animGlb, ImportAssetOptions.ForceSynchronousImport);
        if (!File.Exists(jr.controller)) { if (!AssetDatabase.CopyAsset(tr.controller, jr.controller)) { Debug.LogError("JOANSWAP controller copy failed " + tr.controller + " -> " + jr.controller); return; } }
        if (!string.IsNullOrEmpty(jr.guardMask) && !File.Exists(jr.guardMask)) AssetDatabase.CreateAsset(new AvatarMask(), jr.guardMask);   // NODRAC v93: was CopyAsset(template mask) = another rig's bone paths; GuardLayerSetup.Run writes this character's own
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(jr.controller);
        if (ctrl == null) { Debug.LogError("JOANSWAP controller not loadable " + jr.controller); return; }
        var cx = new Ctx { from = tr.modelGlb, ownDir = dir };
        foreach (var rr in Roster.All) { if (rr == null || rr.id == jr.id || string.IsNullOrEmpty(rr.modelGlb)) continue; string fd = Path.GetDirectoryName(rr.modelGlb).Replace('\\', '/') + "/"; if (!cx.foreignDirs.Contains(fd)) cx.foreignDirs.Add(fd); }   // NODRAC v93
        { var lyrs = ctrl.layers; var own = string.IsNullOrEmpty(jr.guardMask) ? null : AssetDatabase.LoadAssetAtPath<AvatarMask>(jr.guardMask); bool ch = false;   // NODRAC v93: a layer mask of another character is replaced by this character's roster mask (GuardLayerSetup.Run fills its bone paths)
          for (int li = 0; li < lyrs.Length; li++) { var am = lyrs[li].avatarMask; if (am != null && IsForeign(AssetDatabase.GetAssetPath(am), cx)) { lyrs[li].avatarMask = own; ch = true; Debug.LogWarning("JOANSWAP layer '" + lyrs[li].name + "' mask was another character's: now " + (own != null ? jr.guardMask : "none")); } }
          if (ch) ctrl.layers = lyrs; }
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(jr.modelGlb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview")) cx.clips[Short(c.name)] = c; }
        foreach (var l in ctrl.layers) WalkSm(l.stateMachine, cx);
        EditorUtility.SetDirty(ctrl); AssetDatabase.SaveAssets();
        Debug.Log("JOANSWAP import: " + jr.modelGlb + " clips=" + cx.clips.Count + " repointed=" + cx.n + " missing=" + cx.miss + " foreignCleared=" + cx.dropped + " controller=" + jr.controller + " (attack / dodge / finisher states: run AttackClipSetup, DodgeClipSetup, FinisherClipSetup next)");
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
            string cpath = AssetDatabase.GetAssetPath(clip);   // NODRAC v93
            if (cpath != cx.from) return IsForeign(cpath, cx) ? OwnClipByName(clip.name, cx) : m;   // NODRAC v93: was 'return m' = any template .anim (Attack/Dodge/FinisherClips) stayed on the new character
            AnimationClip j;
            if (cx.clips.TryGetValue(Short(clip.name), out j)) { cx.n++; return j; }
            cx.miss++; Debug.LogWarning("JOANSWAP no clip '" + Short(clip.name) + "' in the new combat glb: state keeps the template clip");
            return m;
        }
        var bt = m as BlendTree;
        if (bt != null) { var ch = bt.children; for (int i = 0; i < ch.Length; i++) ch[i].motion = Remap(ch[i].motion, cx); bt.children = ch; }
        return m;
    }

    // ------------------------------------------------------------------ 2 scene object
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
            // BakeMesh output (useScale false) is in the renderer's local space without its scale: TransformPoint adds the scale
            for (int i = 0; i < vs.Length; i++) { float y = tf.TransformPoint(vs[i]).y; if (y < lo) lo = y; if (y > hi) hi = y; }
            Object.DestroyImmediate(m);
        }
        return hi > lo ? hi - lo : 0f;
    }

    static Transform FindBone(Transform root, string n) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t; return null; }

    [MenuItem("Hades/Joan/2 Build scene object")]
    public static void BuildSceneObject()
    {
        var cfg = Load(); if (cfg == null) return;
        Roster.Reload(); var jr = Roster.Get(cfg.newId); var tr = Roster.Get(cfg.templateId);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(jr.modelGlb);
        if (prefab == null) { Debug.LogError("JOANSWAP model not imported " + jr.modelGlb + " (run menu 1)"); return; }
        var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(jr.controller);
        var sc = EditorSceneManager.OpenScene(cfg.scene);
        var tRoot = FindRoot(sc, tr.objectName);
        if (tRoot == null) { Debug.LogError("JOANSWAP template object '" + tr.objectName + "' not in " + cfg.scene); return; }
        var old = FindRoot(sc, jr.objectName); if (old != null && old == tRoot) old = null; if (old != null) old.name = jr.objectName + "_old";   // v79 idempotent: the previous Joan is kept until every scene reference to it has been re-pointed to the new one (v77 shipped a dead camera because the old object was destroyed first)
        var tAnim = tRoot.GetComponentInChildren<Animator>(true);
        if (tAnim == null) { Debug.LogError("JOANSWAP template has no Animator"); return; }
        bool tWas = tRoot.activeSelf; tRoot.SetActive(true);
        float tH = WorldHeight(tRoot);
        var tSock = FindBone(tRoot.transform, cfg.socket);

        var root = Object.Instantiate(tRoot); root.name = jr.objectName;
        var cAnim = root.GetComponentInChildren<Animator>(true);
        var cModel = cAnim.transform; var parent = cModel.parent; string modelName = cModel.name;
        Vector3 lp = cModel.localPosition; Quaternion lr = cModel.localRotation; Vector3 ls = cModel.localScale; int sib = cModel.GetSiblingIndex();
        Object.DestroyImmediate(cModel.gameObject);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        inst.name = modelName; inst.transform.localPosition = lp; inst.transform.localRotation = lr; inst.transform.localScale = Vector3.one; inst.transform.SetSiblingIndex(sib);
        foreach (var t in inst.GetComponentsInChildren<Transform>(true))
            foreach (var pfx in cfg.hideNodePrefixes) if (!string.IsNullOrEmpty(pfx) && t.name.StartsWith(pfx)) { t.gameObject.SetActive(false); break; }
        GearGlbHidden.Apply(inst, jr.modelGlb);   // kitGEAR v79: nodes with glTF extras {"hidden":true} (the 4 gear layers) are inactive until GearSlots equips them`n        foreach (var lg in inst.GetComponentsInChildren<Animation>(true)) Object.DestroyImmediate(lg);

        // scale to heightM (0 = the template's model scale)
        float h1 = WorldHeight(inst);
        float s = ls.x;
        if (jr.heightM > 0f)
        {
            if (h1 > 0.1f) s = jr.heightM / h1;
            else if (cfg.glbHeightM > 0f) s = jr.heightM / cfg.glbHeightM;
        }
        inst.transform.localScale = Vector3.one * s;
        // v77 note: WorldHeight() double-applies the scale (BakeMesh + TransformPoint): 1.762 m at scale 1 reads 1.417 m at 0.8966 although the real height is then 1.58 m. The first scale above is correct; do NOT re-measure and correct.
        Debug.Log("JOANSWAP height: template world " + tH.ToString("F3") + " m (joan_swap rows assume ~1.71), new glb at scale 1 = " + h1.ToString("F3") + " m, model localScale " + s.ToString("F4") + " -> " + WorldHeight(inst).ToString("F3") + " m (roster heightM " + jr.heightM.ToString("F2") + ")");

        var anim = inst.GetComponent<Animator>(); if (anim == null) anim = inst.AddComponent<Animator>();
        anim.runtimeAnimatorController = ctrl; anim.applyRootMotion = tAnim.applyRootMotion; anim.cullingMode = tAnim.cullingMode; anim.updateMode = tAnim.updateMode;
        if (tAnim.GetComponent<GripPose>() != null && anim.GetComponent<GripPose>() == null) anim.gameObject.AddComponent<GripPose>();

        ApplyMaterials(inst, cfg, Path.GetDirectoryName(jr.modelGlb).Replace('\\', '/'));
        foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = cfg.layer;
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = ShadowCastingMode.On; r.receiveShadows = true; }

        var fx = root.GetComponent<Fighter>(); if (fx != null) { fx.anim = anim; fx.model = inst.transform; }
        else Debug.LogWarning("JOANSWAP template root has no Fighter component");

        // capsule: scale the template's height by the height ratio (radius untouched: it is the duel spacing)
        var cc = root.GetComponent<CharacterController>();
        if (cc != null && tH > 0.1f && jr.heightM > 0f) { float k = jr.heightM / tH; cc.height *= k; var c = cc.center; c.y *= k; cc.center = c; }

        // sword socket: the template's socket (LODs, TrailTip) re-seated on the new right hand (BuildSliceWeapons.AttachWeapon maths: toward Middle1, 0.55 of the reach, real size)
        var hand = FindBone(inst.transform, cfg.rightHand); var mid = FindBone(inst.transform, cfg.middle);
        if (tSock == null) Debug.LogWarning("JOANSWAP template has no " + cfg.socket + ": no weapon attached");
        else if (hand == null || mid == null) Debug.LogWarning("JOANSWAP new rig lacks " + cfg.rightHand + " / " + cfg.middle + ": no weapon attached");
        else
        {
            var sock = Object.Instantiate(tSock.gameObject); sock.name = cfg.socket; sock.transform.SetParent(hand, false);
            Vector3 dLocal = hand.InverseTransformPoint(mid.position); float reach = dLocal.magnitude; Vector3 fdir = dLocal.normalized;
            sock.transform.localPosition = fdir * reach * cfg.socketReach;
            sock.transform.localRotation = Quaternion.FromToRotation(Vector3.up, fdir);
            sock.transform.localScale = Vector3.one / Mathf.Max(0.0001f, hand.lossyScale.x);
            foreach (var t in sock.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = cfg.layer;
            Debug.Log("JOANSWAP socket on " + hand.name + ": reach " + reach.ToString("F4") + " m (local), hand lossyScale " + hand.lossyScale.x.ToString("F3"));
        }

        root.SetActive(true); tRoot.SetActive(tWas);
        if (old != null) { int rp = Repoint(old, root, sc); Object.DestroyImmediate(old); Debug.Log("JOANSWAP idempotent rebuild: " + rp + " scene references re-pointed from the previous " + jr.objectName + " object to the new one"); }
        EditorSceneManager.MarkSceneDirty(sc);
        EditorSceneManager.SaveScene(sc);
        Debug.Log("JOANSWAP built '" + root.name + "' from '" + tRoot.name + "' (scene saved). Next: menu 3, then HdrpCharLayer.Run + HdrpFixes.Run (light layer + rim rigs), HdrpCharMats.Run.");
    }

    static void ApplyMaterials(GameObject inst, Cfg cfg, string dir)
    {
        var lit = Shader.Find("HDRP/Lit"); int n = 0;
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials; bool ch = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                foreach (var row in cfg.materials)
                {
                    if (string.IsNullOrEmpty(row.glbMaterial) || !mats[i].name.StartsWith(row.glbMaterial)) continue;
                    var m = GetOrCreateMat(row, lit, dir);
                    if (m != null) { mats[i] = m; ch = true; n++; }
                    break;
                }
            }
            if (ch) r.sharedMaterials = mats;
        }
        Debug.Log("JOANSWAP materials assigned " + n + " (slots without a joan_swap.json row keep the glb material)");
    }

    static Material GetOrCreateMat(MatRow row, Shader lit, string dir)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(row.mat);
        if (m != null) { m.SetFloat("_Smoothness", row.smoothness); if (row.tint != null && row.tint.Length >= 3) m.SetColor("_BaseColor", new Color(row.tint[0], row.tint[1], row.tint[2], 1f)); EditorUtility.SetDirty(m); AssetDatabase.SaveAssets(); return m; }   // v84: an existing material takes the row values again
        string b = dir + "/Tex/" + row.tex + "_base." + (string.IsNullOrEmpty(row.ext) ? "jpg" : row.ext), nrm = dir + "/Tex/" + row.tex + "_normal.jpg";
        if (!File.Exists(b)) { Debug.LogWarning("JOANSWAP no " + b + " yet (run Tools/char_tex_install.py, then menu 2 again): " + row.glbMaterial + " keeps the glb material"); return null; }
        AssetDatabase.ImportAsset(b); if (File.Exists(nrm)) AssetDatabase.ImportAsset(nrm);
        EnsureFolder(Path.GetDirectoryName(row.mat).Replace('\\', '/'));
        m = new Material(lit) { name = Path.GetFileNameWithoutExtension(row.mat) };
        m.SetTexture("_BaseColorMap", AssetDatabase.LoadAssetAtPath<Texture2D>(b));
        if (File.Exists(nrm)) { var ti = AssetImporter.GetAtPath(nrm) as TextureImporter; if (ti != null && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); } m.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>(nrm)); m.EnableKeyword("_NORMALMAP"); }
        m.SetFloat("_Smoothness", row.smoothness); m.SetFloat("_Metallic", 0f);
        if (row.tint != null && row.tint.Length >= 3) m.SetColor("_BaseColor", new Color(row.tint[0], row.tint[1], row.tint[2], 1f));
        if (row.alphaClip > 0f) { m.SetFloat("_AlphaCutoffEnable", 1f); m.SetFloat("_AlphaCutoff", row.alphaClip); m.EnableKeyword("_ALPHATEST_ON"); m.SetOverrideTag("RenderType", "TransparentCutout"); m.renderQueue = 2450; }   // v77: hair cards are alpha-tested, not blended
        if (row.doubleSided) { m.SetFloat("_DoubleSidedEnable", 1f); m.SetFloat("_CullMode", 0f); m.SetFloat("_CullModeForward", 0f); m.EnableKeyword("_DOUBLESIDED_ON"); }
        AssetDatabase.CreateAsset(m, row.mat); HDMaterial.ValidateMaterial(m);
        return m;
    }

    // ------------------------------------------------------------------ 3 apply roster player
    [MenuItem("Hades/Joan/3 Apply roster player to scene")]
    public static void ApplyPlayer()
    {
        var cfg = Load(); if (cfg == null) return;
        Roster.Reload();
        var sc = EditorSceneManager.OpenScene(cfg.scene);
        var player = FindRoot(sc, Roster.PlayerObject);
        if (player == null) { Debug.LogError("JOANSWAP roster player object '" + Roster.PlayerObject + "' is not in the scene (menu 2 builds it)"); return; }
        int refs = 0, off = 0;
        foreach (var r in Roster.All)
        {
            if (r == null || r.id == Roster.PlayerId || r.id == Roster.EnemyId) continue;
            var g = FindRoot(sc, r.objectName); if (g == null || g == player) continue;
            refs += Repoint(g, player, sc);
            if (g.activeSelf) { g.SetActive(false); off++; }
        }
        player.SetActive(true);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
        Debug.Log("JOANSWAP player = " + Roster.PlayerId + " (object " + player.name + "), scene references re-pointed " + refs + ", other roster characters switched off " + off);
    }

    static void BuildMap(Transform from, Transform to, Dictionary<Object, Object> map)
    {
        var toPaths = new Dictionary<string, Transform>();
        foreach (var t in to.GetComponentsInChildren<Transform>(true)) toPaths[RelPath(t, to)] = t;
        foreach (var f in from.GetComponentsInChildren<Transform>(true))
        {
            Transform t; if (!toPaths.TryGetValue(RelPath(f, from), out t)) continue;
            map[f] = t; map[f.gameObject] = t.gameObject;
            var fcs = f.GetComponents<Component>(); var tcs = t.GetComponents<Component>(); var seen = new Dictionary<System.Type, int>();
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

    // every serialized object reference in the scene (outside both characters) that points into 'from' is re-pointed to the same-path counterpart in 'to'
    static int Repoint(GameObject from, GameObject to, Scene sc)
    {
        var map = new Dictionary<Object, Object>(); BuildMap(from.transform, to.transform, map);
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
                    Object tgt;
                    if (map.TryGetValue(it.objectReferenceValue, out tgt)) { it.objectReferenceValue = tgt; ch = true; n++; }
                    else
                    {
                        var oc = it.objectReferenceValue as Component; var og = it.objectReferenceValue as GameObject;
                        var ot = oc != null ? oc.transform : og != null ? og.transform : null;
                        if (ot != null && ot.IsChildOf(from.transform)) { unmapped++; Debug.LogWarning("JOANSWAP unmapped reference " + mb.name + "." + it.propertyPath + " -> " + ot.name); }
                    }
                }
                if (ch) { so.ApplyModifiedProperties(); EditorUtility.SetDirty(mb); }
            }
        }
        if (unmapped > 0) Debug.LogWarning("JOANSWAP " + unmapped + " references into '" + from.name + "' had no counterpart under '" + to.name + "'");
        return n;
    }

    // batch entry points: Unity.exe -batchmode -projectPath ... -executeMethod JoanSwap.RunAllBatch
    public static void RunAllBatch() { ImportModel(); BuildSceneObject(); ApplyPlayer(); }
    public static void ApplyPlayerBatch() { ApplyPlayer(); }
}
