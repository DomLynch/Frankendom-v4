using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// FK16 (unity-dev): bake the OpenTrident rider clips (Assets/Characters/Joan_rider_clips.glb: rider_idle, rider_gallop) into AnimationClips under Assets/Characters/Joan/RiderClips and add them as UNWIRED states
// to Joan.controller (same method as TwoHandClipSetup: bone paths are remapped to the controller's own curve paths, so they ship with the controller and RiderPose can find them at runtime).
// Batch: -executeMethod RiderClipSetup.BakeAndBuild (bakes, then the normal HdrpBatch player build in the same Editor session).
public static class RiderClipSetup
{
    const string Ctl = "Assets/Characters/Joan.controller", Glb = "Assets/Characters/Joan_rider_clips.glb", Dir = "Assets/Characters/Joan/RiderClips";
    static readonly string[] Names = { "rider_idle", "rider_gallop" };
    static string Leaf(string p) { int k = p.LastIndexOf('/'); return k >= 0 ? p.Substring(k + 1) : p; }
    [MenuItem("Tools/Combat/Bake Rider Clips")]
    public static void Run()
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(Ctl); if (ctrl == null) { Debug.LogError("RiderClipSetup: no " + Ctl); return; }
        var sm = ctrl.layers[0].stateMachine; AnimationClip refClip = null;
        foreach (var st in sm.states) if (st.state.name == "guard_idle") refClip = st.state.motion as AnimationClip;
        if (refClip == null) { Debug.LogError("RiderClipSetup: guard_idle has no clip"); return; }
        var map = new Dictionary<string, string>();
        foreach (var b in AnimationUtility.GetCurveBindings(refClip)) { string p = b.path; while (!string.IsNullOrEmpty(p)) { string lf = Leaf(p); if (!map.ContainsKey(lf)) map[lf] = p; int k = p.LastIndexOf('/'); p = k > 0 ? p.Substring(0, k) : ""; } }
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/Characters/Joan", "RiderClips");
        AssetDatabase.ImportAsset(Glb, ImportAssetOptions.ForceSynchronousImport);
        var src = new Dictionary<string, AnimationClip>(); foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Glb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) src[c.name] = c; }
        int added = 0;
        foreach (var nm in Names)
        {
            AnimationClip sc; if (!src.TryGetValue(nm, out sc)) { Debug.LogWarning("RiderClipSetup: '" + nm + "' not in " + Glb); continue; }
            string apath = Dir + "/" + nm + ".anim"; var dst = AssetDatabase.LoadAssetAtPath<AnimationClip>(apath); bool isNew = dst == null;
            if (isNew) dst = new AnimationClip(); else dst.ClearCurves();
            dst.name = nm; dst.frameRate = sc.frameRate > 0f ? sc.frameRate : 30f; dst.legacy = false; int hit = 0, miss = 0;
            foreach (var b in AnimationUtility.GetCurveBindings(sc)) { var cv = AnimationUtility.GetEditorCurve(sc, b); var nb = b; string np; if (map.TryGetValue(Leaf(b.path), out np)) { nb.path = np; hit++; } else miss++; AnimationUtility.SetEditorCurve(dst, nb, cv); }
            var st = AnimationUtility.GetAnimationClipSettings(sc); st.loopTime = true; st.loopBlend = true; AnimationUtility.SetAnimationClipSettings(dst, st);
            if (isNew) AssetDatabase.CreateAsset(dst, apath); else EditorUtility.SetDirty(dst);
            AnimatorState state = null; foreach (var x in sm.states) if (x.state.name == nm) state = x.state;
            if (state == null) state = sm.AddState(nm);
            state.motion = dst; state.writeDefaultValues = true; state.speed = 1f; added++;
            Debug.Log("RiderClipSetup: " + nm + " len " + dst.length.ToString("0.000") + " s paths " + hit + " unmatched " + miss);
        }
        EditorUtility.SetDirty(ctrl); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); Debug.Log("RiderClipSetup: +" + added + " states in " + Ctl);
    }
    public static void BakeAndBuild() { Run(); BuildPlayerCmd.Run(); }
}
