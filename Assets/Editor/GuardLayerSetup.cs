using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Adds the upper-body "GuardUpper" Animator layer used by DuelFighter for guard-while-moving (dodge.json guard.layer).
// For each character controller: an AvatarMask (spine chain + arms + head + hand IK, legs/hips off), an Override layer at weight 0 and the four guard states
// (guard_idle / guard_left / guard_right / guard_up) reusing the Base Layer's guard clips. Idempotent: run again to refresh the mask and states.
// Run: menu Tools > Combat > Add Guard Upper-Body Layer, or batch  -executeMethod GuardLayerSetup.Run  (only the dev opens the Editor).
// The mask paths are read from the guard_idle clip's own curve bindings, so they match whatever hierarchy the glb import produced.
public static class GuardLayerSetup
{
    const string LayerName = "GuardUpper";
    // controllers + mask paths: roster.json rows (controller, guardMask); a row without guardMask gets Assets/Characters/GuardUpper_<controller name>.mask
    static readonly string[] GuardStates = { "guard_idle", "guard_left", "guard_right", "guard_up" };
    // a bone path is "upper body" when it contains one of these (everything under the spine chain: shoulders, arms, hands, neck, head, cape) ...
    static readonly string[] UpperTokens = { "Spine", "ik_hand" };
    // ... and none of these (hips, legs, feet, skirt)
    static readonly string[] LowerTokens = { "UpLeg", "Leg", "Foot", "Toe", "ik_foot", "thigh", "calf", "skirt", "hip_fix", "knee", "Neck", "Head", "Eye", "Jaw" };   // Neck/Head chain off: the base layer drives the head (guard clip put it 40-60 deg left on idle legs)

    [MenuItem("Tools/Combat/Add Guard Upper-Body Layer")]
    public static void Run()
    {
        foreach (var r in RosterEditor.Characters()) if (!string.IsNullOrEmpty(r.controller)) Setup(r.controller, r.guardMask);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("GuardLayerSetup: done");
    }

    // v93: Joan only (batch: -executeMethod GuardLayerSetup.RunJoan). Rebuilds GuardUpper_Joan.mask from Joan's own guard_idle clip bindings (the old mask had Rig/ paths, 0 of 54 resolved on the Armature) and re-points
    // the GuardUpper layer of Joan.controller at it (it still used GuardUpper_Dracula.mask). Dracula / Pitborn controllers are not touched.
    public static void RunJoan()
    {
        int n = 0;
        foreach (var r in RosterEditor.Characters()) if (!string.IsNullOrEmpty(r.controller) && r.controller.Contains("Joan")) { Setup(r.controller, r.guardMask); n++; }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("GuardLayerSetup: RunJoan done (" + n + " controller)");
    }

    static bool IsUpper(string p)
    {
        bool up = false;
        for (int i = 0; i < UpperTokens.Length; i++) if (p.Contains(UpperTokens[i])) { up = true; break; }
        if (!up) return false;
        for (int i = 0; i < LowerTokens.Length; i++) if (p.Contains(LowerTokens[i])) return false;
        return true;
    }

    static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        var st = sm.states;
        for (int i = 0; i < st.Length; i++) if (st[i].state != null && st[i].state.name == name) return st[i].state;
        return null;
    }

    static void Setup(string path, string guardMask)
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (ctrl == null) { Debug.LogWarning("GuardLayerSetup: controller not found " + path); return; }
        var baseSm = ctrl.layers[0].stateMachine;
        var idleState = FindState(baseSm, "guard_idle");
        var idleClip = idleState != null ? idleState.motion as AnimationClip : null;
        if (idleClip == null) { Debug.LogWarning("GuardLayerSetup: no guard_idle clip in " + path); return; }

        // backup next to the project Tools folder once per controller
        try
        {
            string dir = "Tools/_backup_move_guardlayer";
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, Path.GetFileName(path));
            if (!File.Exists(dst)) File.Copy(path, dst);
        }
        catch (System.Exception ex) { Debug.LogWarning("GuardLayerSetup: backup failed " + ex.Message); }

        // mask: every animated path of the clip plus its ancestors; upper-body paths active
        var paths = new SortedSet<string>();
        foreach (var b in AnimationUtility.GetCurveBindings(idleClip))
        {
            string p = b.path;
            while (!string.IsNullOrEmpty(p))
            {
                paths.Add(p);
                int k = p.LastIndexOf('/');
                p = k > 0 ? p.Substring(0, k) : "";
            }
        }
        var list = new List<string>(paths);
        string maskPath = !string.IsNullOrEmpty(guardMask) ? guardMask : "Assets/Characters/GuardUpper_" + Path.GetFileNameWithoutExtension(path) + ".mask";
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
        bool created = mask == null;
        if (created) mask = new AvatarMask();
        mask.transformCount = list.Count;
        int active = 0;
        for (int i = 0; i < list.Count; i++)
        {
            bool on = IsUpper(list[i]);
            mask.SetTransformPath(i, list[i]);
            mask.SetTransformActive(i, on);
            if (on) active++;
        }
        if (created) AssetDatabase.CreateAsset(mask, maskPath); else EditorUtility.SetDirty(mask);

        // layer
        int idx = -1;
        var layers = ctrl.layers;
        for (int i = 0; i < layers.Length; i++) if (layers[i].name == LayerName) idx = i;
        if (idx < 0) { ctrl.AddLayer(LayerName); layers = ctrl.layers; idx = layers.Length - 1; }
        var layer = layers[idx];
        layer.avatarMask = mask;
        layer.blendingMode = AnimatorLayerBlendingMode.Override;
        layer.defaultWeight = 0f;
        ctrl.layers = layers;

        var sm = ctrl.layers[idx].stateMachine;
        for (int i = 0; i < GuardStates.Length; i++)
        {
            var src = FindState(baseSm, GuardStates[i]);
            if (src == null || src.motion == null) { Debug.LogWarning("GuardLayerSetup: base state missing " + GuardStates[i] + " in " + path); continue; }
            var dst = FindState(sm, GuardStates[i]);
            if (dst == null) dst = sm.AddState(GuardStates[i]);
            dst.motion = src.motion;
            dst.writeDefaultValues = true;
        }
        EditorUtility.SetDirty(ctrl);
        Debug.Log("GuardLayerSetup: " + path + " layer '" + LayerName + "' index " + idx + ", mask " + maskPath + " (" + active + " of " + list.Count + " paths active)");
    }
}
