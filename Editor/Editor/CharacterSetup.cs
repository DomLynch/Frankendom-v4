using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

// HCKIT5 v95 (HC4 K5 + K7 + K8): "a new monster = one roster row + its assets, no code edit".
//   Hades > Characters > Set up enemy from roster row    (batch: -executeMethod CharacterSetup.SetupEnemyBatch)
//   Hades > Characters > Set up player from roster row   (batch: -executeMethod CharacterSetup.SetupPlayerBatch)
//   Hades > Characters > Ensure missing clip states      (batch: -executeMethod CharacterSetup.EnsureStatesBatch)
// For the roster's enemy (or player) this runs, idempotently and for THAT character only:
//   1 controller   missing -> copy of the null-motion template (character_setup.json controllerTemplate, laneCLIPBUILD Tools > Combat > Create Null-Motion Clip Template); never another character's controller
//   2 import       JoanSwap.ImportModel with a Cfg built from roster row + character_setup.json (rig profile, materials); states are filled by clip name from the character's own combat glb
//   3 clips        AttackClipSetup / DodgeClipSetup restricted to this id (ClipSets.Only): attack_clips.json / dodge_clips.json "clipSets" + roster "clipSet"
//   4 guard        GuardLayerSetup.RunFor(id): AvatarMask from the character's OWN guard_idle bindings (its own root)
//   5 states       EnsureStates: states the data names that are missing from the controller and have a baked .anim already; the rest are listed as TODO (nothing is invented)
//   6 scene        JoanSwap.BuildSceneObject (clone of the roster row with "templateFor": "<role>", Model swap, scale to heightM, socket from the rig profile) + ApplyEnemy / ApplyPlayer
//   7 look         HdrpCharLayer.Run (light layer group from the roster, see RosterGroups) and HdrpFixes.Run (rim rigs keyed by role)
// Data: Assets/Data/character_setup.json (rig profiles, per-character source glb / materials / template) + roster.json optional keys clipSet, templateFor, lightLayerGroup.
public static class CharacterSetup
{
    [Serializable] public class Rig { public string id = "mixamo106", rightHand = "mixamorig:RightHand", middle = "mixamorig:RightHandMiddle1", socket = "WeaponSocket_R"; public int layer = 8; public float socketReach = 0.55f; public string[] hideNodePrefixes = new string[] { "Layer_", "Icosphere" }; }
    [Serializable] public class Row { public string id = "", rig = "", templateId = "", sourceGlb = ""; public float glbHeightM = 0f; public JoanSwap.MatRow[] materials = new JoanSwap.MatRow[0]; }
    [Serializable] public class Doc { public string scene = "Assets/Scenes/HadesSlice.unity", controllerTemplate = "Assets/Characters/_ClipTemplate.controller"; public Rig[] rigs = new Rig[0]; public Row[] characters = new Row[0]; public string[] stateSources = new string[0]; }
    [Serializable] class DSect { public string clip = "", clipLeft = "", clipRight = "", clipBack = ""; }
    [Serializable] class DodgeDoc { public DSect roll, backstep, sidestep; }
    [Serializable] class ClipName { public string state = ""; }
    [Serializable] class SetName { public string id = ""; public ClipName[] clips = new ClipName[0]; }

    const string DataFile = "Assets/Data/character_setup.json";

    // ---------------------------------------------------------------- roster extras (Roster.cs ignores these keys)
    [Serializable] public class RowExt { public string id = "", clipSet = "", templateFor = "", lightLayerGroup = ""; }
    [Serializable] class RosterExt { public RowExt[] characters = new RowExt[0]; }

    public static Dictionary<string, RowExt> Ext()
    {
        var o = new Dictionary<string, RowExt>();
        string p = "Assets/Data/roster.json";
        if (!File.Exists(p)) return o;
        var r = JsonUtility.FromJson<RosterExt>(File.ReadAllText(p));
        if (r != null && r.characters != null) foreach (var c in r.characters) if (c != null && c.id.Length > 0) o[c.id] = c;
        return o;
    }

    public static Doc Load()
    {
        var d = new Doc();
        if (File.Exists(DataFile)) JsonUtility.FromJsonOverwrite(File.ReadAllText(DataFile), d);
        else Debug.LogWarning("CHARSETUP no " + DataFile + ": built-in defaults");
        return d;
    }

    static Row RowFor(Doc d, string id) { foreach (var r in d.characters) if (r != null && r.id == id) return r; return new Row { id = id }; }
    static Rig RigFor(Doc d, Row row)
    {
        foreach (var g in d.rigs) if (g != null && g.id == row.rig) return g;
        return d.rigs.Length > 0 ? d.rigs[0] : new Rig();
    }

    // template = the row's templateId, else the roster row that says "templateFor": "<role>", else the other roster character of that role
    static string TemplateOf(Doc d, string id, string role)
    {
        var row = RowFor(d, id); if (row.templateId.Length > 0) return row.templateId;
        foreach (var kv in Ext()) if (kv.Value.templateFor == role && kv.Key != id) return kv.Key;
        return role == "enemy" ? Roster.PlayerId : Roster.EnemyId;
    }

    static bool InRoster(string id) { foreach (var r in Roster.All) if (r != null && r.id == id) return true; return false; }

    // ---------------------------------------------------------------- menu / batch
    [MenuItem("Hades/Characters/Set up enemy from roster row")] public static void SetupEnemy() { Setup("enemy"); }
    [MenuItem("Hades/Characters/Set up player from roster row")] public static void SetupPlayer() { Setup("player"); }
    [MenuItem("Hades/Characters/Ensure missing clip states")] public static void EnsureStatesMenu() { Roster.Reload(); foreach (var r in Roster.All) if (r != null && r.id.Length > 0) EnsureStates(r.id); AssetDatabase.SaveAssets(); }
    public static void SetupEnemyBatch() { Setup("enemy"); }
    public static void SetupPlayerBatch() { Setup("player"); }
    public static void EnsureStatesBatch() { EnsureStatesMenu(); }

    public static void Setup(string role)
    {
        Roster.Reload();
        string id = role == "player" ? Roster.PlayerId : Roster.EnemyId;
        if (!InRoster(id)) { Debug.LogError("CHARSETUP roster " + role + " '" + id + "' has no row in roster.json"); return; }
        var d = Load(); var r = Roster.Get(id); var row = RowFor(d, id); var rig = RigFor(d, row);
        string tpl = TemplateOf(d, id, role);
        if (tpl == id) { Debug.LogError("CHARSETUP '" + id + "' would be its own scene template: add \"templateFor\": \"" + role + "\" to another roster row"); return; }

        // 1 controller
        if (!CreateControllerFromTemplate(d, r)) return;

        // 2 import (JoanSwap with a Cfg made from data; the tool itself is unchanged)
        var cfg = new JoanSwap.Cfg { newId = id, templateId = tpl, sourceGlb = row.sourceGlb, scene = d.scene, rightHand = rig.rightHand, middle = rig.middle, socket = rig.socket, layer = rig.layer, socketReach = rig.socketReach, glbHeightM = row.glbHeightM, hideNodePrefixes = rig.hideNodePrefixes, materials = row.materials };
        JoanSwap.Override = cfg;
        try
        {
            if (!File.Exists(r.modelGlb)) { Debug.LogError("CHARSETUP " + r.modelGlb + " missing (roster modelGlb): build the character's combat glb first"); return; }
            JoanSwap.ImportModel();
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
            if (ctrl != null) { int n = FillNullStates(ctrl, r.modelGlb); EditorUtility.SetDirty(ctrl); AssetDatabase.SaveAssets(); Debug.Log("CHARSETUP " + id + ": " + n + " controller states filled from " + r.modelGlb); }

            // 3 clips for this character only
            ClipSets.Only = id;
            AttackClipSetup.Run();
            DodgeClipSetup.Run();
            ClipSets.Only = "";

            // 4 guard layer + mask from the character's own root
            GuardLayerSetup.RunFor(id);

            // 5 states the data names but the controller lacks
            EnsureStates(id);

            // 6 scene
            JoanSwap.BuildSceneObject();
            if (role == "player") JoanSwap.ApplyPlayer(); else ApplyEnemy();
        }
        finally { JoanSwap.Override = null; ClipSets.Only = ""; }

        // 7 look
        HdrpCharLayer.Run();
        HdrpFixes.Run();
        Debug.Log("CHARSETUP done: " + role + " = " + id + " (template " + tpl + "). Still manual: art (Tools/char_tex_install.py -> JoanSwap materials), finisher clipImport row, weapon prefab, then Tools/new_character_check.py <id>");
    }

    // ---------------------------------------------------------------- 1 controller from the null-motion template
    public static bool CreateControllerFromTemplate(Doc d, RosterCharacter r)
    {
        if (string.IsNullOrEmpty(r.controller)) { Debug.LogError("CHARSETUP roster '" + r.id + "' has no controller path"); return false; }
        if (File.Exists(r.controller)) return true;
        if (!File.Exists(d.controllerTemplate)) { Debug.LogError("CHARSETUP no controller template " + d.controllerTemplate + ": run Tools > Combat > Create Null-Motion Clip Template (laneCLIPBUILD) or set character_setup.json controllerTemplate"); return false; }
        JoanSwap.EnsureFolder(Path.GetDirectoryName(r.controller).Replace('\\', '/'));
        if (!AssetDatabase.CopyAsset(d.controllerTemplate, r.controller)) { Debug.LogError("CHARSETUP copy failed " + d.controllerTemplate + " -> " + r.controller); return false; }
        Debug.Log("CHARSETUP controller " + r.controller + " created from template " + d.controllerTemplate);
        return true;
    }

    // states with no motion take the same-named clip of the character's own combat glb
    public static int FillNullStates(AnimatorController ctrl, string glb)
    {
        var clips = new Dictionary<string, AnimationClip>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(glb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview")) clips[JoanSwap.Short(c.name)] = c; }
        int n = 0;
        foreach (var l in ctrl.layers) n += Fill(l.stateMachine, clips);
        return n;
    }

    static int Fill(AnimatorStateMachine sm, Dictionary<string, AnimationClip> clips)
    {
        int n = 0;
        foreach (var cs in sm.states)
        {
            var st = cs.state; if (st == null || st.motion != null) continue;
            AnimationClip c; if (clips.TryGetValue(st.name, out c)) { st.motion = c; EditorUtility.SetDirty(st); n++; }
        }
        foreach (var sub in sm.stateMachines) n += Fill(sub.stateMachine, clips);
        return n;
    }

    // ---------------------------------------------------------------- 5 missing states generator (HC5 W: Pitborn atk_* / dodge_diveroll)
    static bool HasState(AnimatorStateMachine sm, string name)
    {
        foreach (var cs in sm.states) if (cs.state != null && cs.state.name == name) return true;
        foreach (var sub in sm.stateMachines) if (HasState(sub.stateMachine, name)) return true;
        return false;
    }

    // every state name the data asks THIS character for: its clipSet (roster "clipSet") and explicit bake clips (attack + dodge), its own attack_clips rows (clip / fallbackClip), and the dodge.json clips
    [Serializable] class AtkRow { public string character = "", clip = "", fallbackClip = ""; public bool enabled = true; }
    [Serializable] class AtkDoc { public AtkRow[] rows = new AtkRow[0]; }
    [Serializable] class BakeName { public string character = ""; public ClipName[] clips = new ClipName[0]; }
    [Serializable] class BakeDoc { public BakeName[] bake = new BakeName[0]; public SetName[] clipSets = new SetName[0]; }

    static List<string> DataStates(string id)
    {
        var o = new List<string>();
        Action<string> add = s => { if (!string.IsNullOrEmpty(s) && !o.Contains(s)) o.Add(s); };
        string cs; Ext().TryGetValue(id, out var ext); cs = ext != null ? ext.clipSet : "";
        foreach (var f in new[] { "Assets/Data/attack_clips.json", "Assets/Data/dodge_clips.json" })
        {
            if (!File.Exists(f)) continue;
            string txt = File.ReadAllText(f); var b = JsonUtility.FromJson<BakeDoc>(txt);
            if (b == null) continue;
            bool explicitRow = false;
            foreach (var e in b.bake) if (e.character == id) { explicitRow = true; foreach (var c in e.clips) add(c.state); }
            if (!explicitRow && cs.Length > 0) foreach (var s in b.clipSets) if (s.id == cs) foreach (var c in s.clips) add(c.state);
        }
        if (File.Exists("Assets/Data/attack_clips.json"))
        {
            var a = JsonUtility.FromJson<AtkDoc>(File.ReadAllText("Assets/Data/attack_clips.json"));
            if (a != null) foreach (var r in a.rows) if (r.enabled && r.character == id) { add(r.clip); add(r.fallbackClip); }
        }
        if (File.Exists("Assets/Data/dodge.json"))
        {
            var dd = JsonUtility.FromJson<DodgeDoc>(File.ReadAllText("Assets/Data/dodge.json"));
            foreach (var s in new[] { dd.roll, dd.backstep, dd.sidestep }) if (s != null) { add(s.clip); add(s.clipLeft); add(s.clipRight); add(s.clipBack); }
        }
        return o;
    }

    // A bare state (same as AttackClipSetup adds: no transitions, writeDefaultValues) for every data state the controller lacks AND whose baked <state>.anim already exists
    // in the character's AttackClips / DodgeClips / finisher folder (character_setup.json stateSources patterns). Missing sources are logged as TODO, nothing is invented.
    public static int EnsureStates(string id)
    {
        var d = Load(); var r = Roster.Get(id);
        var ctrl = string.IsNullOrEmpty(r.controller) ? null : AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
        if (ctrl == null) { Debug.LogWarning("CHARSETUP EnsureStates: no controller for " + id); return 0; }
        var sm = ctrl.layers[0].stateMachine; int added = 0; var todo = new List<string>();
        string[] pats = d.stateSources.Length > 0 ? d.stateSources : new[] { "{folder}/AttackClips/{state}.anim", "{folder}/DodgeClips/{state}.anim", "{finisherClipFolder}/{state}.anim" };
        foreach (var s in DataStates(id))
        {
            if (HasState(sm, s)) continue;
            AnimationClip clip = null;
            foreach (var p in pats)
            {
                string path = p.Replace("{folder}", RosterEditor.Folder(r)).Replace("{finisherClipFolder}", r.finisherClipFolder).Replace("{state}", s).Replace("{id}", r.id);
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path); if (clip != null) break;
            }
            if (clip == null) { todo.Add(s); continue; }
            var st = sm.AddState(s); st.motion = clip; st.writeDefaultValues = true; st.speed = 1f; added++;
        }
        if (added > 0) EditorUtility.SetDirty(ctrl);
        Debug.Log("CHARSETUP " + id + " states: +" + added + (todo.Count > 0 ? ", TODO (no baked clip yet, run the clip pipeline for " + id + "): " + string.Join(", ", todo.ToArray()) : ", none missing"));
        return added;
    }

    // ---------------------------------------------------------------- 6 enemy counterpart of JoanSwap.ApplyPlayer
    [MenuItem("Hades/Characters/Apply roster enemy to scene")]
    public static void ApplyEnemy()
    {
        Roster.Reload();
        var d = Load();
        var sc = EditorSceneManager.OpenScene(d.scene);
        var enemy = JoanSwap.FindRoot(sc, Roster.EnemyObject);
        if (enemy == null) { Debug.LogError("CHARSETUP roster enemy object '" + Roster.EnemyObject + "' is not in the scene (Set up enemy builds it)"); return; }
        int refs = 0, off = 0;
        foreach (var r in Roster.All)
        {
            if (r == null || r.id == Roster.PlayerId || r.id == Roster.EnemyId) continue;
            var g = JoanSwap.FindRoot(sc, r.objectName); if (g == null || g == enemy) continue;
            refs += JoanSwap.Repoint(g, enemy, sc);
            if (g.activeSelf) { g.SetActive(false); off++; }
        }
        enemy.SetActive(true);
        EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
        Debug.Log("CHARSETUP enemy = " + Roster.EnemyId + " (object " + enemy.name + "), scene references re-pointed " + refs + ", other roster characters switched off " + off);
    }
}

// HCKIT5 v95 (HC4 K8): light-layer groups from the roster. hdrp_char_layer.json may list "@lit" (characters that take the character light layer) and "@restore"
// (characters that go back on the Default layer and keep the door lights: the enemy look). A roster row says which with "lightLayerGroup": "lit" | "restore";
// no key = the enemy is "restore", everyone else "lit" (= the old hand-kept lists Dracula, Joan / Pitborn).
public static class RosterGroups
{
    public static string GroupOf(RosterCharacter r, Dictionary<string, CharacterSetup.RowExt> ext)
    {
        CharacterSetup.RowExt e; if (ext.TryGetValue(r.id, out e) && e.lightLayerGroup.Length > 0) return e.lightLayerGroup;
        return r.id == Roster.EnemyId ? "restore" : "lit";
    }

    public static string[] Expand(string[] names)
    {
        if (names == null) return null;
        var ext = CharacterSetup.Ext(); var o = new List<string>();
        foreach (var n in names)
        {
            if (n == "@lit" || n == "@restore")
            {
                string g = n.Substring(1);
                foreach (var r in Roster.All) if (r != null && r.id.Length > 0 && GroupOf(r, ext) == g) o.Add(r.objectName);
            }
            else o.Add(n);
        }
        return o.ToArray();
    }
}
