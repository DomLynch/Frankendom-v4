using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// FK3: bake the two-hand clip sets (two_hand_clips.json sets[].glb) into AnimationClips th_<set>_<state> under Assets/Characters/Joan/TwoHandClips and add them as states to Joan.controller (not wired by transitions: they ship with the controller so
// TwoHandClips can find them at runtime). Bone paths are remapped to the controller's own curve paths (same as AttackClipSetup). Batch: -executeMethod TwoHandClipSetup.Run
public static class TwoHandClipSetup
{
    const string Ctl = "Assets/Characters/Joan.controller", Dir = "Assets/Characters/Joan/TwoHandClips";
    static string Leaf(string p) { int k = p.LastIndexOf('/'); return k >= 0 ? p.Substring(k + 1) : p; }
    [MenuItem("Tools/Combat/Bake Two-Hand Clips")]
    public static void Run()
    {
        var d = JsonUtility.FromJson<TwoHandClips.Data>(File.ReadAllText("Assets/Data/two_hand_clips.json").TrimStart('\uFEFF'));
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(Ctl); if (ctrl == null) { Debug.LogError("TwoHandClipSetup: no " + Ctl); return; }
        var sm = ctrl.layers[0].stateMachine; AnimationClip refClip = null;
        foreach (var st in sm.states) if (st.state.name == "guard_idle") refClip = st.state.motion as AnimationClip;
        if (refClip == null) { Debug.LogError("TwoHandClipSetup: guard_idle has no clip"); return; }
        var map = new Dictionary<string, string>();
        foreach (var b in AnimationUtility.GetCurveBindings(refClip)) { string p = b.path; while (!string.IsNullOrEmpty(p)) { string lf = Leaf(p); if (!map.ContainsKey(lf)) map[lf] = p; int k = p.LastIndexOf('/'); p = k > 0 ? p.Substring(0, k) : ""; } }
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/Characters/Joan", "TwoHandClips");
        var keep = new HashSet<string>(); foreach (var s0 in d.sets) foreach (var p0 in s0.map) keep.Add(p0.to);
        foreach (var x in new List<ChildAnimatorState>(sm.states)) if (x.state.name.StartsWith("th_") && !keep.Contains(x.state.name)) { string nm = x.state.name; sm.RemoveState(x.state); Debug.Log("TwoHandClipSetup: removed stale state " + nm); }
        int added = 0;
        foreach (var s in d.sets)
        {
            AssetDatabase.ImportAsset(s.glb, ImportAssetOptions.ForceSynchronousImport);
            var src = new Dictionary<string, AnimationClip>(); foreach (var o in AssetDatabase.LoadAllAssetsAtPath(s.glb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) src[c.name] = c; }
            foreach (var pr in s.map)
            {
                AnimationClip sc; if (!src.TryGetValue(pr.from, out sc)) { Debug.LogWarning("TwoHandClipSetup: '" + pr.from + "' not in " + s.glb); continue; }
                string apath = Dir + "/" + pr.to + ".anim"; var dst = AssetDatabase.LoadAssetAtPath<AnimationClip>(apath); bool isNew = dst == null;
                if (isNew) dst = new AnimationClip(); else dst.ClearCurves();
                dst.name = pr.to; dst.frameRate = sc.frameRate > 0f ? sc.frameRate : 30f; dst.legacy = false; int hit = 0, miss = 0;
                foreach (var b in AnimationUtility.GetCurveBindings(sc)) { var cv = AnimationUtility.GetEditorCurve(sc, b); var nb = b; string np; if (map.TryGetValue(Leaf(b.path), out np)) { nb.path = np; hit++; } else miss++; AnimationUtility.SetEditorCurve(dst, nb, cv); }
                var st = AnimationUtility.GetAnimationClipSettings(sc); st.loopTime = pr.loop; st.loopBlend = pr.loop; AnimationUtility.SetAnimationClipSettings(dst, st);
                if (isNew) AssetDatabase.CreateAsset(dst, apath); else EditorUtility.SetDirty(dst);
                AnimatorState state = null; foreach (var x in sm.states) if (x.state.name == pr.to) state = x.state;
                if (state == null) state = sm.AddState(pr.to);
                state.motion = dst; state.writeDefaultValues = true; state.speed = 1f; added++;
                Debug.Log("TwoHandClipSetup: " + pr.to + " len " + dst.length.ToString("0.000") + " s loop " + pr.loop + " paths " + hit + " unmatched " + miss);
            }
        }
        EditorUtility.SetDirty(ctrl); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); Debug.Log("TwoHandClipSetup: +" + added + " states in " + Ctl);
    }
}