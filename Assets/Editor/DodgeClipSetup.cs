using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Adds the baked attack clips (Assets/Data/dodge_clips.json "bake") to the character Animator controllers as new Base Layer states.
// Run once after `python Tools/bake_dodge_clips.py` has written Assets/Characters/<Name>/<Name>_attacks.glb:
//   menu Tools > Combat > Add Dodge Clips, or batch  -executeMethod DodgeClipSetup.Run   (only the dev opens the Editor).
// Idempotent: run again to refresh the clip assets and states. Per clip: the AnimationClip is copied out of the imported GLB into
// <outClipDir>/<state>.anim with every curve path remapped (by bone name) to the paths the controller's own `referenceState` clip uses, so it plays on the
// character whatever the GLB hierarchy root is called; the state (same name) is added to Base Layer or re-pointed. Controllers are backed up once to
// Tools/_backup_laneB_dodgesetup/. DuelFighter only needs the state names (dodge_clips.json rows); without these states it falls back to the old clips.
public static class DodgeClipSetup
{
    [Serializable] class BakeClip { public string state; }
    [Serializable] class BakeEntry { public string character, controller, outGlb, outClipDir, referenceState; public BakeClip[] clips; }
    [Serializable] class Cfg { public BakeEntry[] bake; }

    [MenuItem("Tools/Combat/Add Dodge Clips")]
    public static void Run()
    {
        string json = File.ReadAllText("Assets/Data/dodge_clips.json");
        var cfg = JsonUtility.FromJson<Cfg>(json);
        if (cfg == null || cfg.bake == null || cfg.bake.Length == 0) { Debug.LogWarning("DodgeClipSetup: no bake entries in dodge_clips.json"); return; }
        Roster.Reload();   // bake rows are keyed by roster id; the controller path comes from roster.json when the row names one (the dodge_clips.json path is the fallback)
        for (int i = 0; i < cfg.bake.Length; i++) { var rc = Roster.Get(cfg.bake[i].character); if (!string.IsNullOrEmpty(rc.controller)) cfg.bake[i].controller = rc.controller; Setup(cfg.bake[i]); }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("DodgeClipSetup: done");
    }

    static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        var st = sm.states;
        for (int i = 0; i < st.Length; i++) if (st[i].state != null && st[i].state.name == name) return st[i].state;
        return null;
    }

    static void EnsureFolder(string dir)
    {
        if (AssetDatabase.IsValidFolder(dir)) return;
        string parent = Path.GetDirectoryName(dir).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(dir));
    }

    static string Leaf(string path) { int k = path.LastIndexOf('/'); return k >= 0 ? path.Substring(k + 1) : path; }

    static void Setup(BakeEntry e)
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(e.controller);
        if (ctrl == null) { Debug.LogWarning("DodgeClipSetup: controller not found " + e.controller); return; }
        if (!File.Exists(e.outGlb)) { Debug.LogWarning("DodgeClipSetup: " + e.outGlb + " missing; run  python Tools/bake_dodge_clips.py " + e.character); return; }
        AssetDatabase.ImportAsset(e.outGlb, ImportAssetOptions.ForceSynchronousImport);

        var baseSm = ctrl.layers[0].stateMachine;
        var refState = FindState(baseSm, e.referenceState);
        var refClip = refState != null ? refState.motion as AnimationClip : null;
        if (refClip == null) { Debug.LogWarning("DodgeClipSetup: reference state '" + e.referenceState + "' has no clip in " + e.controller); return; }

        // bone leaf name -> full curve path used by the controller's own clips
        var map = new Dictionary<string, string>();
        foreach (var b in AnimationUtility.GetCurveBindings(refClip))
        {
            string p = b.path;
            while (!string.IsNullOrEmpty(p))
            {
                string leaf = Leaf(p);
                if (!map.ContainsKey(leaf)) map[leaf] = p;
                int k = p.LastIndexOf('/');
                p = k > 0 ? p.Substring(0, k) : "";
            }
        }

        try
        {
            string dir = "Tools/_backup_laneB_dodgesetup";
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, Path.GetFileName(e.controller));
            if (!File.Exists(dst)) File.Copy(e.controller, dst);
        }
        catch (Exception ex) { Debug.LogWarning("DodgeClipSetup: backup failed " + ex.Message); }

        var src = new Dictionary<string, AnimationClip>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(e.outGlb))
        {
            var c = o as AnimationClip;
            if (c != null && !c.name.StartsWith("__preview__")) src[c.name] = c;
        }
        EnsureFolder(e.outClipDir);

        int added = 0;
        for (int i = 0; i < e.clips.Length; i++)
        {
            string name = e.clips[i].state;
            AnimationClip s;
            if (!src.TryGetValue(name, out s)) { Debug.LogWarning("DodgeClipSetup: clip '" + name + "' not in " + e.outGlb + " (found: " + string.Join(", ", new List<string>(src.Keys).ToArray()) + ")"); continue; }

            string apath = e.outClipDir + "/" + name + ".anim";
            var dstClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(apath);
            bool isNew = dstClip == null;
            if (isNew) dstClip = new AnimationClip(); else dstClip.ClearCurves();
            dstClip.name = name; dstClip.frameRate = s.frameRate > 0f ? s.frameRate : 60f; dstClip.legacy = false;
            int hit = 0, miss = 0;
            foreach (var b in AnimationUtility.GetCurveBindings(s))
            {
                var curve = AnimationUtility.GetEditorCurve(s, b);
                var nb = b; string np;
                if (map.TryGetValue(Leaf(b.path), out np)) { nb.path = np; hit++; } else miss++;
                AnimationUtility.SetEditorCurve(dstClip, nb, curve);
            }
            var set = AnimationUtility.GetAnimationClipSettings(s);
            set.loopTime = false; set.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(dstClip, set);
            if (isNew) AssetDatabase.CreateAsset(dstClip, apath); else EditorUtility.SetDirty(dstClip);

            var state = FindState(baseSm, name);
            if (state == null) state = baseSm.AddState(name);
            state.motion = dstClip; state.writeDefaultValues = true; state.speed = 1f;
            added++;
            Debug.Log("DodgeClipSetup: " + e.character + " state '" + name + "' len " + dstClip.length.ToString("0.000") + " s, paths remapped " + hit + ", unmatched " + miss + (miss > 0 ? "  (WARNING: unmatched bones will not animate)" : ""));
        }
        EditorUtility.SetDirty(ctrl);
        Debug.Log("DodgeClipSetup: " + e.controller + " +" + added + " dodge states");
    }
}
