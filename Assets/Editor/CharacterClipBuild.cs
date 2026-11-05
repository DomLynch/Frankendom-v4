using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Debug = UnityEngine.Debug;

// CLIPBUILD v1 (laneCLIPBUILD). ONE step that builds the base clip set of every roster character (player + enemy) from RAW Mixamo sources onto the character's OWN
// skeleton, wires the controller and the guard layer, and VALIDATES the result. No character is named here: ids come from Roster / roster.json, everything else is
// Assets/Data/clip_build.json (copy in StreamingAssets).
//   RunForRoster()   python Tools/clip_build.py --roster --install --gates, import the glbs, controller (null-motion template, never Dracula's), re-point states by clip name,
//                    guard layer with a mask generated for the character's own root and assigned to roster.guardMask, then Validate(). Returns false on any error.
//   ValidateOnly()   static checks only (no python, no writes): menu Tools > Combat > Validate Character Clips, batch -executeMethod CharacterClipBuild.ValidateOnly (exit 3 on error).
//   RunBatch()       batch entry: RunForRoster then EditorApplication.Exit.
// Hook point (owned by laneHCKIT3, NOT edited here): first lines of BuildPlayerCmd.Run():
//   if (!System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a == "-noclipbuild") && !CharacterClipBuild.RunForRoster()) { Debug.LogError("CLIPBUILD failed, build aborted"); EditorApplication.Exit(3); return; }
// attacks / dodges / finishers keep using AttackClipSetup / DodgeClipSetup / FinisherClipSetup (clip_build.json "delegated"); Validate() checks their states exist too.
public static class CharacterClipBuild
{
    const string CfgPath = "Assets/Data/clip_build.json";
    const string LayerName = "GuardUpper";

    [Serializable] class Row { public string state = ""; }
    [Serializable] class ToolsCfg { public string python = "python"; }
    [Serializable] class TemplateCfg { public string asset = ""; }
    [Serializable] class GuardCfg { public string[] states = new string[0]; public string[] upperTokens = new string[0]; public string[] lowerTokens = new string[0]; }
    [Serializable] class Cfg { public ToolsCfg tools = new ToolsCfg(); public TemplateCfg controllerTemplate = new TemplateCfg(); public GuardCfg guard = new GuardCfg(); public Row[] @base = new Row[0]; public string[] skipIds = new string[0]; }
    [Serializable] class AtkRow { public string character = ""; public string move = ""; public bool enabled = true; public string clip = ""; public string fallbackClip = ""; public bool allowFallback = false; }
    [Serializable] class AtkCfg { public AtkRow[] rows = new AtkRow[0]; }

    static List<string> errors = new List<string>();
    static void Err(string m) { errors.Add(m); Debug.LogError("CLIPBUILD " + m); }

    static Cfg Load()
    {
        if (!File.Exists(CfgPath)) { Err("missing " + CfgPath); return null; }
        var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(CfgPath));
        if (c == null || c.@base == null || c.@base.Length == 0) { Err(CfgPath + " has no base rows"); return null; }
        return c;
    }

    static List<RosterCharacter> BuildIds(Cfg cfg)
    {
        var list = new List<RosterCharacter>(); Roster.Reload();
        foreach (var id in new[] { Roster.PlayerId, Roster.EnemyId }) { var r = Roster.Get(id); if (r != null && r.id.Length > 0 && Array.IndexOf(cfg.skipIds, r.id) < 0 && !list.Exists(x => x.id == r.id)) list.Add(r); }   // skipIds: clip_build.json (reason there)
        return list;
    }

    [MenuItem("Tools/Combat/Build Character Clips")]
    public static bool RunForRoster()
    {
        errors.Clear(); var cfg = Load(); if (cfg == null) return false;
        var ids = BuildIds(cfg);
        // 1) python (CPU): raw Mixamo -> <Id>_combat.glb + gates. Non-zero exit = a failed gate (named in the log) or a config error.
        var psi = new ProcessStartInfo(cfg.tools.python, "Tools/clip_build.py --roster --install --gates") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        try
        {
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd(); p.WaitForExit(900000);
                Debug.Log("CLIPBUILD python:\n" + o); if (e.Length > 0) Debug.LogWarning("CLIPBUILD python stderr:\n" + e);
                if (!p.HasExited || p.ExitCode != 0) Err("clip_build.py failed (exit " + (p.HasExited ? p.ExitCode.ToString() : "timeout") + "), see the log above for the failing gate / clip");
            }
        }
        catch (Exception ex) { Err("cannot start python '" + cfg.tools.python + "': " + ex.Message); }
        if (errors.Count > 0) return false;
        AssetDatabase.Refresh();
        foreach (var r in ids)
        {
            if (string.IsNullOrEmpty(r.modelGlb) || !File.Exists(r.modelGlb)) { Err(r.id + ": modelGlb missing " + r.modelGlb); continue; }
            AssetDatabase.ImportAsset(r.modelGlb, ImportAssetOptions.ForceSynchronousImport);
            var ctrl = EnsureController(cfg, r); if (ctrl == null) continue;
            PointStates(cfg, ctrl, r);
            GuardLayer(cfg, ctrl, r);
            EditorUtility.SetDirty(ctrl);
        }
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        bool ok = Validate(cfg, ids) && errors.Count == 0;
        Debug.Log("CLIPBUILD " + (ok ? "OK" : "FAILED (" + errors.Count + " errors)"));
        return ok;
    }

    [MenuItem("Tools/Combat/Validate Character Clips")]
    public static void ValidateOnly()
    {
        errors.Clear(); var cfg = Load(); bool ok = cfg != null && Validate(cfg, BuildIds(cfg)) && errors.Count == 0;
        Debug.Log("CLIPBUILD validate-only " + (ok ? "OK" : "FAILED (" + errors.Count + " errors)"));
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 3);
    }

    public static void RunBatch() { bool ok = RunForRoster(); if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 3); }

    // one-time helper: Assets/Characters/_ClipTemplate.controller with the base states and NO motions (the template nothing can inherit another character's clips from)
    [MenuItem("Tools/Combat/Create Null-Motion Clip Template")]
    public static void CreateTemplate()
    {
        var cfg = Load(); if (cfg == null) return;
        string path = cfg.controllerTemplate.asset; if (string.IsNullOrEmpty(path)) { Err("controllerTemplate.asset empty"); return; }
        if (File.Exists(path)) { Debug.Log("CLIPBUILD template exists " + path); return; }
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        foreach (var row in cfg.@base) { var st = sm.AddState(row.state); st.motion = null; st.writeDefaultValues = true; }
        AssetDatabase.SaveAssets(); Debug.Log("CLIPBUILD template created " + path + " (" + cfg.@base.Length + " states, no motions)");
    }

    // ---------------------------------------------------------------- controller
    static AnimatorController EnsureController(Cfg cfg, RosterCharacter r)
    {
        if (string.IsNullOrEmpty(r.controller)) { Err(r.id + ": roster row has no controller"); return null; }
        if (!File.Exists(r.controller))
        {
            if (!File.Exists(cfg.controllerTemplate.asset)) CreateTemplate();
            if (!AssetDatabase.CopyAsset(cfg.controllerTemplate.asset, r.controller)) { Err(r.id + ": cannot copy template to " + r.controller); return null; }
            var fresh = AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
            foreach (var l in fresh.layers) NullMotions(l.stateMachine);   // belt and braces: nothing inherited
            return fresh;
        }
        return AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
    }

    static void NullMotions(AnimatorStateMachine sm)
    {
        foreach (var cs in sm.states) cs.state.motion = null;
        foreach (var sub in sm.stateMachines) NullMotions(sub.stateMachine);
    }

    static void AllStates(AnimatorStateMachine sm, List<AnimatorState> into)
    {
        foreach (var cs in sm.states) if (cs.state != null) into.Add(cs.state);
        foreach (var sub in sm.stateMachines) AllStates(sub.stateMachine, into);
    }

    static Dictionary<string, AnimationClip> GlbClips(string glb)
    {
        var map = new Dictionary<string, AnimationClip>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(glb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) map[c.name] = c; }
        return map;
    }

    // every state whose name equals a clip of the character's own combat glb plays that clip (all layers); missing base rows get a state. attack/dodge/finisher states are not touched.
    static void PointStates(Cfg cfg, AnimatorController ctrl, RosterCharacter r)
    {
        var clips = GlbClips(r.modelGlb);
        if (clips.Count == 0) { Err(r.id + ": no AnimationClips inside " + r.modelGlb + " (glb import animationMethod?)"); return; }
        var baseSm = ctrl.layers[0].stateMachine; var all = new List<AnimatorState>();
        foreach (var l in ctrl.layers) AllStates(l.stateMachine, all);
        int n = 0;
        foreach (var st in all) { AnimationClip c; if (clips.TryGetValue(st.name, out c) && st.motion != c) { st.motion = c; EditorUtility.SetDirty(st); n++; } }
        foreach (var row in cfg.@base)
        {
            AnimationClip c; if (!clips.TryGetValue(row.state, out c)) { Err(r.id + ": glb has no clip '" + row.state + "'"); continue; }
            if (!all.Exists(s => s.name == row.state)) { var ns = baseSm.AddState(row.state); ns.motion = c; ns.writeDefaultValues = true; n++; }
        }
        Debug.Log("CLIPBUILD " + r.id + ": " + n + " states pointed at " + r.modelGlb);
    }

    // ---------------------------------------------------------------- guard layer (mask from the character's own hierarchy, assigned to roster.guardMask)
    static bool IsUpper(Cfg cfg, string p)
    {
        bool up = false;
        foreach (var t in cfg.guard.upperTokens) if (p.Contains(t)) { up = true; break; }
        if (!up) return false;
        foreach (var t in cfg.guard.lowerTokens) if (p.Contains(t)) return false;
        return true;
    }

    static AnimatorState Find(AnimatorStateMachine sm, string name) { foreach (var cs in sm.states) if (cs.state != null && cs.state.name == name) return cs.state; return null; }

    static void GuardLayer(Cfg cfg, AnimatorController ctrl, RosterCharacter r)
    {
        var baseSm = ctrl.layers[0].stateMachine; var gi = Find(baseSm, "guard_idle");
        var idle = gi != null ? gi.motion as AnimationClip : null;
        if (idle == null) { Err(r.id + ": no guard_idle clip for the guard layer"); return; }
        var paths = new SortedSet<string>();
        foreach (var b in AnimationUtility.GetCurveBindings(idle)) { string p = b.path; while (!string.IsNullOrEmpty(p)) { paths.Add(p); int k = p.LastIndexOf('/'); p = k > 0 ? p.Substring(0, k) : ""; } }
        var list = new List<string>(paths);
        string maskPath = !string.IsNullOrEmpty(r.guardMask) ? r.guardMask : Roster.Get(r.id).guardMask;
        if (string.IsNullOrEmpty(maskPath)) { Err(r.id + ": roster guardMask empty"); return; }
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath); bool created = mask == null; if (created) mask = new AvatarMask();
        mask.transformCount = list.Count; int active = 0;
        for (int i = 0; i < list.Count; i++) { bool on = IsUpper(cfg, list[i]); mask.SetTransformPath(i, list[i]); mask.SetTransformActive(i, on); if (on) active++; }
        if (created) AssetDatabase.CreateAsset(mask, maskPath); else EditorUtility.SetDirty(mask);
        int idx = -1; var layers = ctrl.layers;
        for (int i = 0; i < layers.Length; i++) if (layers[i].name == LayerName) idx = i;
        if (idx < 0) { ctrl.AddLayer(LayerName); layers = ctrl.layers; idx = layers.Length - 1; }
        var layer = layers[idx]; layer.avatarMask = mask; layer.blendingMode = AnimatorLayerBlendingMode.Override; layer.defaultWeight = 0f; ctrl.layers = layers;   // THE FIX: the layer uses roster.guardMask, not a copied template mask
        var sm = ctrl.layers[idx].stateMachine;
        foreach (var name in cfg.guard.states)
        {
            var src = Find(baseSm, name); if (src == null || src.motion == null) { Err(r.id + ": base guard state missing " + name); continue; }
            var dst = Find(sm, name) ?? sm.AddState(name); dst.motion = src.motion; dst.writeDefaultValues = true;
        }
        Debug.Log("CLIPBUILD " + r.id + ": guard mask " + maskPath + " (" + active + " of " + list.Count + " paths active, root " + (list.Count > 0 ? list[0].Split('/')[0] : "?") + ")");
    }

    // ---------------------------------------------------------------- validate (static, no play mode)
    static bool Validate(Cfg cfg, List<RosterCharacter> ids)
    {
        var folders = new Dictionary<string, string>();
        foreach (var rc in RosterEditor.Characters()) folders[rc.id] = RosterEditor.Folder(rc) + "/";
        AtkCfg atk = null; try { atk = JsonUtility.FromJson<AtkCfg>(File.ReadAllText("Assets/Data/attack_clips.json")); } catch (Exception) { }
        foreach (var r in ids)
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
            if (ctrl == null) { Err(r.id + ": controller missing " + r.controller); continue; }
            string own = RosterEditor.Folder(r) + "/"; var names = new HashSet<string>(); var all = new List<AnimatorState>();
            foreach (var l in ctrl.layers) AllStates(l.stateMachine, all);
            foreach (var st in all)
            {
                names.Add(st.name);
                // V1 every state has a motion, and it belongs to this character
                if (st.motion == null) { Err(r.id + ": state '" + st.name + "' has no motion"); continue; }
                string mp = AssetDatabase.GetAssetPath(st.motion);
                foreach (var kv in folders) if (kv.Key != r.id && mp.StartsWith(kv.Value)) Err(r.id + ": state '" + st.name + "' plays " + kv.Key + "'s clip (" + mp + ")");
            }
            // V2 curve paths of the base clips resolve against the model hierarchy (root name read from the model)
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(r.modelGlb);
            if (model == null) Err(r.id + ": model not loadable " + r.modelGlb);
            else
            {
                foreach (var row in cfg.@base)
                {
                    var st = all.Find(s => s.name == row.state); var clip = st != null ? st.motion as AnimationClip : null;
                    if (clip == null) { Err(r.id + ": base state '" + row.state + "' missing or not a clip"); continue; }
                    int bad = 0;
                    foreach (var b in AnimationUtility.GetCurveBindings(clip)) if (!string.IsNullOrEmpty(b.path) && model.transform.Find(b.path) == null) bad++;
                    if (bad > 0) Err(r.id + ": clip '" + row.state + "' has " + bad + " curve paths that do not exist on " + r.modelGlb);
                }
            }
            // V3 guard layer uses roster.guardMask, every active path exists, at least one active
            int gl = -1; for (int i = 0; i < ctrl.layers.Length; i++) if (ctrl.layers[i].name == LayerName) gl = i;
            if (gl < 0) Err(r.id + ": no " + LayerName + " layer");
            else
            {
                var m = ctrl.layers[gl].avatarMask; string mp = m != null ? AssetDatabase.GetAssetPath(m) : "";
                if (m == null || mp != r.guardMask) Err(r.id + ": " + LayerName + " mask is '" + mp + "', roster says '" + r.guardMask + "'");
                else
                {
                    int act = 0, missing = 0;
                    for (int i = 0; i < m.transformCount; i++) { if (!m.GetTransformActive(i)) continue; act++; if (model != null && model.transform.Find(m.GetTransformPath(i)) == null) missing++; }
                    if (act == 0) Err(r.id + ": guard mask has no active path");
                    if (missing > 0) Err(r.id + ": guard mask has " + missing + " active paths that do not exist on the model root (wrong rig root name?)");
                }
            }
            // V4 attack rows: clip state exists, else the row relies on its fallback (error unless allowFallback)
            if (atk != null) foreach (var row in atk.rows)
                {
                    if (!row.enabled || (row.character.Length > 0 && row.character != r.id)) continue;
                    if (row.clip.Length > 0 && !names.Contains(row.clip) && !row.allowFallback) Err(r.id + ": attack row '" + row.move + "' needs state '" + row.clip + "' (would silently use fallback '" + row.fallbackClip + "')");
                }
        }
        // V5 report from the python step: present, newer than the model, no failed gates recorded (python exits non-zero on failure; this only proves it ran)
        if (!File.Exists("Tools/clip_build_report.json")) Err("Tools/clip_build_report.json missing: run RunForRoster once");
        else foreach (var r in ids) if (File.Exists(r.modelGlb) && File.GetLastWriteTimeUtc("Tools/clip_build_report.json") < File.GetLastWriteTimeUtc(r.modelGlb).AddMinutes(-1)) Err(r.id + ": clip_build_report.json is older than " + r.modelGlb + " (clips changed without the gates)");
        return errors.Count == 0;
    }
}
