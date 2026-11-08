using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// SOBEKUNITY v1 (laneSOBEKUNITY). ONE batch entry for the second opponent:
//   Unity.exe -batchmode -nographics -projectPath <proj> -executeMethod SobekUnityBatch.RunAll -sobekCfg <cfg.json> -sobekOut <dir> -logFile <log>
// Order inside the single run (every step logs "SOBEKUNITY ..."; any failure = EditorApplication.Exit(code) with a reason line "SOBEKUNITY FAIL <step> <reason>"):
//   1 preflight   roster row, files, config            exit 2
//   2 import      combat / anim / attacks / dodges glb, clips counted                      exit 3
//   3 controller  null-motion template -> <Id>.controller, states filled by clip name from the character's OWN glb, attack + dodge bakes
//                 (AttackClipSetup / DodgeClipSetup restricted to this id), guard layer + mask, EnsureStates          exit 3
//   4 scene       EnemySwap.ImportModels + BuildScenesMenu (fresh copy of the base scene every run, never a second root) + CheckScenes   exit 4
//   5 materials   HdrpCharMats.Run (mask maps, materialId) and a check that every skinned renderer uses a material asset of this character   exit 5
//   6 grip        WeaponGrip rows from the staged hand-frame fit: socketOffset / socketEuler computed from the REAL socket frame (pos0 / rot0 as built in the scene) and written to <out>\sobek_grip_rows.json   exit 6
//   7 verify      roster row, controller, renderer count, socket, states (with motions of this character), one active fighter per side, grip round trip   exit 7
// Nothing here names a character: the id and every number come from the cfg json (made by run_sobek_unity.ps1 / sobek_prep.py from roster.json and the staged fit).
public static class SobekUnityBatch
{
    // ------------------------------------------------------------------ cfg
    [Serializable] public class Fit { public string id = "", bone = "mixamorig:RightHand"; public float[] pos = { 0f, 0f, 0f }, euler = { 0f, 0f, 0f }, shaftAxis = { 0f, 1f, 0f }; }
    [Serializable] public class Group { public string id = "", fit = "", weapon = ""; public string[] statePrefixes = new string[0], stateExact = new string[0]; public bool isDefault; public float slide, blendSeconds = 0.1f; }
    [Serializable]
    public class Cfg
    {
        public string character = "";
        public string hand = "RightHand", socket = "WeaponSocket_R", middle = "mixamorig:RightHandMiddle1";
        public int skinnedRenderers = 2, minControllerStates = 30, minClips = 30;
        public string[] glbs = new string[0];                 // asset paths imported in step 2 (combat first)
        public string[] requiredStateSources = new string[0]; // "clip_build.base", "attack_clips.rows"
        public float[] mirrorX = { -1f, 1f, 1f }, mirrorId = { 1f, 1f, 1f }, mirrorZ = { 1f, 1f, -1f };   // glTF -> Unity candidates (glTFast mirrors X; the others are tried only if the tip does not point forward)
        public float minTipForwardDot = 0.3f, maxGripToSegmentM = 0.12f, maxAxisErrDeg = 1.0f, maxRoundTripPosM = 0.002f, maxRoundTripDeg = 0.2f, maxSocketOffsetM = 0.6f;
        public Fit[] fits = new Fit[0];
        public Group[] groups = new Group[0];
        public string baseScene = "Assets/Scenes/HadesSlice.unity";
        public string[] deactivateRoots = new string[0];      // laneMONSTER3: root names switched off in the built enemy scene (inherited template / other enemies)
        public bool deactivateOtherEnemies = false;           // true = also every other active roster-enemy root (the same test scene.oneEnemy uses)
    }

    static Cfg cfg; static string outDir = "", id = "";
    static readonly List<string> fails = new List<string>();
    static readonly List<string> logErrors = new List<string>();
    static int checks;

    static string Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return "";
    }

    static void Log(string m) { Debug.Log("SOBEKUNITY " + m); }
    static void Check(string name, bool ok, string detail)
    {
        checks++; if (!ok) fails.Add(name + ": " + detail);
        Debug.Log("SOBEKUNITY CHECK " + name + " " + (ok ? "PASS" : "FAIL") + " " + detail);
    }
    static void OnLog(string cond, string stack, LogType t) { if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert) logErrors.Add(cond); }

    // Exit with a reason. code: 2 preflight 3 import/controller 4 scene 5 materials 6 grip 7 verify, 1 unexpected.
    static void Die(int code, string step, string reason)
    {
        Debug.Log("SOBEKUNITY FAIL " + step + " " + reason);
        try { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "sobek_unity_result.txt"), "FAIL " + step + " " + reason + "\n"); } catch (Exception) { }
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    static string Fmt(float v) { return v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture); }
    static string Fmt3(Vector3 v) { return "(" + Fmt(v.x) + "," + Fmt(v.y) + "," + Fmt(v.z) + ")"; }

    // ------------------------------------------------------------------ entry
    public static void RunAll()
    {
        Application.logMessageReceived += OnLog;
        try
        {
            outDir = Arg("-sobekOut"); if (outDir.Length == 0) outDir = "_agents/laneSOBEKUNITY/out";
            Directory.CreateDirectory(outDir);
            string cfgPath = Arg("-sobekCfg");
            if (cfgPath.Length == 0 || !File.Exists(cfgPath)) { Die(2, "preflight", "no -sobekCfg file (" + cfgPath + ")"); return; }
            cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(cfgPath));
            if (cfg == null || cfg.character.Length == 0) { Die(2, "preflight", "cfg has no character"); return; }
            id = cfg.character;
            if (!Preflight()) return;
            if (!StepImport()) return;
            if (!StepController()) return;
            if (!StepScene()) return;
            if (!StepMaterials()) return;
            if (!StepGrip()) return;
            Verify();
        }
        catch (Exception e) { Die(1, "exception", e.GetType().Name + ": " + e.Message + " | " + e.StackTrace.Replace('\n', ' ')); }
        finally { Application.logMessageReceived -= OnLog; }
    }

    // ------------------------------------------------------------------ 1 preflight
    static bool Preflight()
    {
        Roster.Reload();
        if (!Roster.Has(id)) { Die(2, "preflight", "roster.json has no row '" + id + "' (apply the Sobek rows first: run_sobek_unity.ps1 does it)"); return false; }
        var r = Roster.Get(id);
        if (r.id == Roster.PlayerId) { Die(2, "preflight", id + " is the roster player"); return false; }
        foreach (var g in cfg.glbs) if (!File.Exists(g)) { Die(2, "preflight", "missing asset " + g); return false; }
        if (string.IsNullOrEmpty(r.controller) || string.IsNullOrEmpty(r.modelGlb) || string.IsNullOrEmpty(r.objectName)) { Die(2, "preflight", "roster row lacks controller / modelGlb / objectName"); return false; }
        if (!File.Exists(r.modelGlb)) { Die(2, "preflight", "roster modelGlb missing " + r.modelGlb); return false; }
        if (cfg.fits.Length == 0 || cfg.groups.Length == 0) { Die(2, "preflight", "cfg has no grip fits / groups"); return false; }
        if (!File.Exists("Assets/Data/enemy_swap.json")) { Die(2, "preflight", "Assets/Data/enemy_swap.json missing (ladder support not merged: apply_sobek.py --apply part 2)"); return false; }
        var es = JsonUtility.FromJson<EnemySwap.Cfg>(File.ReadAllText("Assets/Data/enemy_swap.json"));
        bool row = false; if (es != null) foreach (var e in es.enemies) if (e != null && e.id == id) row = true;
        if (!row) { Die(2, "preflight", "enemy_swap.json has no row for " + id); return false; }
        foreach (var e in es.enemies) if (e != null && e.id == id && !e.keepWeapon) { Die(2, "preflight", "enemy_swap.json row " + id + " has keepWeapon false: the trident needs the socket"); return false; }
        Log("preflight OK: roster row " + id + " object '" + r.objectName + "' controller " + r.controller + " model " + r.modelGlb + " (roster enemy stays '" + Roster.EnemyId + "', template '" + Roster.DefaultEnemyId + "')");
        return true;
    }

    // ------------------------------------------------------------------ 2 import
    static int ClipCount(string glb)
    {
        int n = 0;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(glb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview")) n++; }
        return n;
    }

    static bool StepImport()
    {
        AssetDatabase.Refresh();
        var r = Roster.Get(id);
        string dir = Path.GetDirectoryName(r.modelGlb).Replace('\\', '/');
        JoanSwap.EnsureFolder(dir); JoanSwap.EnsureFolder(dir + "/Mats"); JoanSwap.EnsureFolder(dir + "/Tex");
        if (!string.IsNullOrEmpty(r.finisherClipFolder)) JoanSwap.EnsureFolder(r.finisherClipFolder);
        foreach (var g in cfg.glbs) AssetDatabase.ImportAsset(g, ImportAssetOptions.ForceSynchronousImport);
        int n = ClipCount(r.modelGlb);
        Check("import.clips", n >= cfg.minClips, n + " clips in " + r.modelGlb + " (need " + cfg.minClips + ")");
        if (n < cfg.minClips) { Die(3, "import", "only " + n + " AnimationClips in " + r.modelGlb + " (glb import animationMethod, or the baked glb was not copied)"); return false; }
        return true;
    }

    // ------------------------------------------------------------------ 3 controller (CharacterSetup steps 1,3,4,5 for THIS id; not step 6, the scene is EnemySwap's)
    static bool StepController()
    {
        var r = Roster.Get(id); var d = CharacterSetup.Load();
        if (!File.Exists(d.controllerTemplate)) CharacterClipBuild.CreateTemplate();
        if (!File.Exists(d.controllerTemplate)) { Die(3, "controller", "null-motion template " + d.controllerTemplate + " could not be created"); return false; }
        if (!CharacterSetup.CreateControllerFromTemplate(d, r)) { Die(3, "controller", "controller " + r.controller + " not created from the template"); return false; }
        if (!string.IsNullOrEmpty(r.guardMask) && !File.Exists(r.guardMask)) AssetDatabase.CreateAsset(new AvatarMask(), r.guardMask);   // GuardLayerSetup fills the character's own bone paths
        AssetDatabase.Refresh();
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
        if (ctrl == null) { Die(3, "controller", "controller not loadable " + r.controller); return false; }
        int filled = CharacterSetup.FillNullStates(ctrl, r.modelGlb);
        EditorUtility.SetDirty(ctrl); AssetDatabase.SaveAssets();
        Log("controller " + r.controller + ": " + filled + " states filled from " + r.modelGlb);
        try
        {
            ClipSets.Only = id;
            AttackClipSetup.Run();
            DodgeClipSetup.Run();
        }
        finally { ClipSets.Only = ""; }
        GuardLayerSetup.RunFor(id);
        CharacterSetup.EnsureStates(id);
        AssetDatabase.SaveAssets();
        return true;
    }

    // ------------------------------------------------------------------ 4 scene (EnemySwap: a fresh copy of the base scene every run)
    static void CleanInherited(string scenePath)
    {
        if ((cfg.deactivateRoots == null || cfg.deactivateRoots.Length == 0) && !cfg.deactivateOtherEnemies) return;
        var sc = EditorSceneManager.OpenScene(scenePath); var r = Roster.Get(id); int off = 0;
        foreach (var g in sc.GetRootGameObjects())
        {
            if (g.name == r.objectName || !g.activeSelf) continue;
            bool hit = cfg.deactivateRoots != null && Array.IndexOf(cfg.deactivateRoots, g.name) >= 0;
            if (!hit && cfg.deactivateOtherEnemies) { var rc = Roster.Get(Roster.IdOfName(g.name)); hit = rc != null && rc.id.Length > 0 && rc.id != id && rc.id != Roster.PlayerId; }
            if (hit) { g.SetActive(false); off++; Log("scene: switched off inherited root '" + g.name + "' in " + scenePath); }
        }
        if (off > 0) { EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc); }
        Log("scene: clean-up switched off " + off + " inherited root(s)");
    }

    static bool StepScene()
    {
        var es = JsonUtility.FromJson<EnemySwap.Cfg>(File.ReadAllText("Assets/Data/enemy_swap.json"));
        EnemySwap.Row row = null; foreach (var e in es.enemies) if (e != null && e.id == id) row = e;
        string path = EnemySwap.ScenePathOf(es, row);
        if (row.inPlace) { Die(4, "scene", "enemy_swap row is inPlace: that edits the base scene HadesSlice.unity; this batch only builds the scene copy"); return false; }
        Log("scene: building " + path + " from " + es.baseScene + " (any existing copy is deleted and rebuilt: one pass, one root)");
        EnemySwap.ImportModels();
        EnemySwap.BuildScenesMenu();
        EnemySwap.CheckScenes();
        CleanInherited(path);
        // the base scene must be untouched: open it and make sure no Sobek root leaked into it
        var bs = EditorSceneManager.OpenScene(es.baseScene);
        var r = Roster.Get(id); bool leaked = false;
        foreach (var g in bs.GetRootGameObjects()) if (g.name == r.objectName || g.name == r.objectName + "_tpl" || g.name == r.objectName + "_old") leaked = true;
        Check("scene.baseUntouched", !leaked, leaked ? "base scene " + es.baseScene + " has a root named " + r.objectName : "no " + r.objectName + " root in the base scene");
        if (leaked) { Die(4, "scene", "base scene was modified (a " + r.objectName + " root is in " + es.baseScene + "): restore it from the rollback backup"); return false; }
        if (!File.Exists(path)) { Die(4, "scene", path + " was not written (see ENEMYSWAP lines above)"); return false; }
        return true;
    }

    // ------------------------------------------------------------------ 5 materials
    static bool StepMaterials()
    {
        HdrpCharMats.Run();
        AssetDatabase.SaveAssets();
        var es = JsonUtility.FromJson<EnemySwap.Cfg>(File.ReadAllText("Assets/Data/enemy_swap.json"));
        EnemySwap.Row row = null; foreach (var e in es.enemies) if (e != null && e.id == id) row = e;
        var sc = EditorSceneManager.OpenScene(EnemySwap.ScenePathOf(es, row));
        var root = FindRoot(sc, Roster.Get(id).objectName);
        if (root == null) { Die(5, "materials", "no root '" + Roster.Get(id).objectName + "' in the built scene"); return false; }
        string matDir = Path.GetDirectoryName(Roster.Get(id).modelGlb).Replace('\\', '/') + "/Mats/";
        int skinned = 0, slots = 0, ours = 0; var names = new SortedSet<string>(); var bad = new List<string>();
        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (UnderSocket(smr.transform)) continue;   // the cloned template weapon is not the character's body
            skinned++;
            foreach (var m in smr.sharedMaterials)
            {
                slots++;
                if (m == null) { bad.Add(smr.name + ": null material"); continue; }
                string p = AssetDatabase.GetAssetPath(m);
                if (p.StartsWith(matDir)) { ours++; names.Add(m.name); } else bad.Add(smr.name + ": '" + m.name + "' is not a material asset under " + matDir + " (" + (p.Length == 0 ? "scene" : p) + ")");
            }
        }
        Check("materials.assets", bad.Count == 0 && slots > 0, ours + "/" + slots + " slots use " + matDir + " [" + string.Join(", ", new List<string>(names).ToArray()) + "]" + (bad.Count > 0 ? " BAD: " + string.Join("; ", bad.ToArray()) : ""));
        Check("materials.distinct", names.Count >= cfg.skinnedRenderers, names.Count + " distinct material names (head and body must not share one)");
        if (bad.Count > 0 || slots == 0) { Die(5, "materials", "renderer slots without a character material asset: " + string.Join("; ", bad.ToArray()) + " (Tex files present? char_tex_install.py ran?)"); return false; }
        return true;
    }

    // ------------------------------------------------------------------ 6 grip: hand-frame fit -> WeaponGrip rows
    // Frames (all read from the code, not guessed):
    //   WeaponSwap puts the weapon glb on the socket with local position 0 (grip node = origin), rotation identity, scale 1, shaft along the socket +Y.
    //   WeaponGrip:  socket.localPosition = pos0 + socketOffset / hand.lossyScale.x ;  socket.localRotation = Quaternion.Euler(socketEuler) * rot0   (pos0 / rot0 = the socket as the scene has it)
    //   so for a wanted socket pose (P, R) in the hand frame:  socketOffset = (P - pos0) * hs ;  socketEuler = (R * inverse(rot0)).eulerAngles.
    //   The staged fit is in the glTF hand-node frame, rotation = Blender Euler XYZ on the trident whose shaft is +Z in Blender (= +Y in glTF):  Rg = Rz*Ry*Rx*C  with C: glTF->Blender (x,-z,y).
    //   glTF -> Unity import is a mirror M (glTFast: X). Rotation in Unity = M*Rg*M, position / axis = M*v. M is tried from the cfg candidates and the first whose tip points forward (rest pose) wins.
    struct M3 { public double a, b, c, d, e, f, g, h, i; }   // row-major 3x3
    static M3 Mul(M3 x, M3 y)
    {
        return new M3
        {
            a = x.a * y.a + x.b * y.d + x.c * y.g, b = x.a * y.b + x.b * y.e + x.c * y.h, c = x.a * y.c + x.b * y.f + x.c * y.i,
            d = x.d * y.a + x.e * y.d + x.f * y.g, e = x.d * y.b + x.e * y.e + x.f * y.h, f = x.d * y.c + x.e * y.f + x.f * y.i,
            g = x.g * y.a + x.h * y.d + x.i * y.g, h = x.g * y.b + x.h * y.e + x.i * y.h, i = x.g * y.c + x.h * y.f + x.i * y.i
        };
    }
    static M3 RotX(double deg) { double r = deg * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r); return new M3 { a = 1, b = 0, c = 0, d = 0, e = c, f = -s, g = 0, h = s, i = c }; }
    static M3 RotY(double deg) { double r = deg * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r); return new M3 { a = c, b = 0, c = s, d = 0, e = 1, f = 0, g = -s, h = 0, i = c }; }
    static M3 RotZ(double deg) { double r = deg * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r); return new M3 { a = c, b = -s, c = 0, d = s, e = c, f = 0, g = 0, h = 0, i = 1 }; }
    static M3 Diag(float[] m) { return new M3 { a = m[0], e = m[1], i = m[2] }; }
    static Vector3 Col(M3 m, int k) { return k == 0 ? new Vector3((float)m.a, (float)m.d, (float)m.g) : k == 1 ? new Vector3((float)m.b, (float)m.e, (float)m.h) : new Vector3((float)m.c, (float)m.f, (float)m.i); }

    // wanted socket rotation in the Unity hand frame for a fit and a mirror
    static Quaternion UnityRot(Fit f, float[] mirror)
    {
        M3 rb = Mul(RotZ(f.euler[2]), Mul(RotY(f.euler[1]), RotX(f.euler[0])));
        M3 c = new M3 { a = 1, b = 0, c = 0, d = 0, e = 0, f = -1, g = 0, h = 1, i = 0 };   // glTF (x,y,z) -> Blender (x,-z,y)
        M3 rg = Mul(rb, c);
        M3 mm = Diag(mirror); M3 ru = Mul(mm, Mul(rg, mm));
        Vector3 y = Col(ru, 1), z = Col(ru, 2);
        return Quaternion.LookRotation(z, y);
    }
    static Vector3 Mir(float[] p, float[] mirror) { return new Vector3(p[0] * mirror[0], p[1] * mirror[1], p[2] * mirror[2]); }

    static bool UnderSocket(Transform t) { for (var p = t; p != null; p = p.parent) if (p.name == cfg.socket) return true; return false; }
    static Transform FindT(Transform root, string n) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t; return null; }
    public static GameObject FindRoot(Scene sc, string objectName) { foreach (var g in sc.GetRootGameObjects()) if (g.name == objectName) return g; return null; }

    static List<string> ControllerStates(AnimatorController ctrl)
    {
        var l = new List<string>(); if (ctrl == null) return l;
        foreach (var layer in ctrl.layers) CollectStates(layer.stateMachine, l);
        return l;
    }
    static void CollectStates(AnimatorStateMachine sm, List<string> into)
    {
        foreach (var cs in sm.states) if (cs.state != null && !into.Contains(cs.state.name)) into.Add(cs.state.name);
        foreach (var sub in sm.stateMachines) CollectStates(sub.stateMachine, into);
    }

    static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    static string N(float v) { return v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture); }
    static string Arr(Vector3 v) { return "[" + N(v.x) + ", " + N(v.y) + ", " + N(v.z) + "]"; }

    static bool StepGrip()
    {
        var es = JsonUtility.FromJson<EnemySwap.Cfg>(File.ReadAllText("Assets/Data/enemy_swap.json"));
        EnemySwap.Row row = null; foreach (var e in es.enemies) if (e != null && e.id == id) row = e;
        var sc = EditorSceneManager.OpenScene(EnemySwap.ScenePathOf(es, row));
        var r = Roster.Get(id); var root = FindRoot(sc, r.objectName);
        if (root == null) { Die(6, "grip", "no root '" + r.objectName + "' in the scene"); return false; }
        var hand = FindT(root.transform, "mixamorig:" + cfg.hand); var mid = FindT(root.transform, cfg.middle); var sock = hand != null ? FindT(hand, cfg.socket) : null;
        Check("grip.socket", sock != null, sock != null ? cfg.socket + " under " + hand.name + " local pos " + Fmt3(sock.localPosition) + " lossyScale " + Fmt3(sock.lossyScale) : "no " + cfg.socket + " under mixamorig:" + cfg.hand + " (keepWeapon / socket name in enemy_swap.json)");
        if (sock == null || mid == null) { Die(6, "grip", "socket or " + cfg.middle + " not found on " + r.objectName); return false; }
        Vector3 pos0 = sock.localPosition; Quaternion rot0 = sock.localRotation; float hs = Mathf.Max(0.0001f, hand.lossyScale.x);
        Vector3 midLocal = hand.InverseTransformPoint(mid.position);
        Vector3 fwd = root.transform.forward;

        // pick the glTF->Unity mirror: the first candidate for which every fit's tip points forward at rest
        float[][] cands = { cfg.mirrorX, cfg.mirrorId, cfg.mirrorZ }; string[] cname = { "mirrorX", "identity", "mirrorZ" };
        int pick = -1; var dots = new StringBuilder();
        for (int c = 0; c < cands.Length && pick < 0; c++)
        {
            bool ok = true; dots.Append(cname[c]).Append(":");
            foreach (var f in cfg.fits)
            {
                Quaternion q = UnityRot(f, cands[c]); Vector3 tip = hand.rotation * (q * Vector3.up);
                float dt = Vector3.Dot(tip.normalized, fwd.normalized); dots.Append(" ").Append(f.id).Append("=").Append(Fmt(dt));
                if (dt < cfg.minTipForwardDot) ok = false;
            }
            dots.Append("; ");
            if (ok) pick = c;
        }
        Check("grip.mirror", pick >= 0, pick >= 0 ? "glTF->Unity mirror '" + cname[pick] + "' (tip.forward dots " + dots + ")" : "no mirror candidate makes the tip point forward at rest (dots " + dots + ")");
        if (pick < 0) { Die(6, "grip", "tip never points forward with any mirror candidate; fit frame wrong? " + dots); return false; }
        float[] mir = cands[pick];

        var sb = new StringBuilder(); sb.Append("{\n \"_gen\": \"SOBEKUNITY v1\",\n \"mirror\": ").Append(Q(cname[pick])).Append(",\n \"pos0\": ").Append(Arr(pos0)).Append(",\n \"rot0Euler\": ").Append(Arr(rot0.eulerAngles)).Append(",\n \"handScale\": ").Append(N(hs)).Append(",\n \"rows\": [\n");
        var states = ControllerStates(AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller));
        bool first = true;
        foreach (var g in cfg.groups)
        {
            Fit fit = null; foreach (var f in cfg.fits) if (f.id == g.fit) fit = f;
            if (fit == null) { Die(6, "grip", "group '" + g.id + "' names fit '" + g.fit + "' which is not in the cfg"); return false; }
            Vector3 P = Mir(fit.pos, mir); Quaternion R = UnityRot(fit, mir);
            Vector3 off = (P - pos0) * hs;                       // metres in the hand frame (WeaponGrip divides by hs)
            Quaternion cur = R * Quaternion.Inverse(rot0); Vector3 eul = cur.eulerAngles;
            // checks: shaft axis vs the staged shaftAxis, grip point near the palm segment, round trip through the WeaponGrip formula
            Vector3 shaftWant = Mir(fit.shaftAxis, mir).normalized; Vector3 shaftGot = (R * Vector3.up).normalized;
            float axisErr = Vector3.Angle(shaftWant, shaftGot);
            float segT = midLocal.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(P, midLocal) / midLocal.sqrMagnitude) : 0f;
            float palm = (P - midLocal * segT).magnitude * hs;
            Vector3 posBack = pos0 + off / hs; Quaternion rotBack = Quaternion.Euler(eul) * rot0;
            float rtPos = (posBack - P).magnitude * hs, rtDeg = Quaternion.Angle(rotBack, R);
            Vector3 tipW = hand.rotation * (R * Vector3.up);
            Check("grip." + g.id + ".axis", axisErr <= cfg.maxAxisErrDeg, "shaft axis error " + Fmt(axisErr) + " deg vs the staged shaftAxis (fit " + fit.id + ")");
            Check("grip." + g.id + ".palm", palm <= cfg.maxGripToSegmentM, "grip point " + Fmt(palm) + " m from the hand->" + cfg.middle + " segment (limit " + Fmt(cfg.maxGripToSegmentM) + ")");
            Check("grip." + g.id + ".roundTrip", rtPos <= cfg.maxRoundTripPosM && rtDeg <= cfg.maxRoundTripDeg && off.magnitude <= cfg.maxSocketOffsetM, "WeaponGrip formula returns pos err " + Fmt(rtPos) + " m, rot err " + Fmt(rtDeg) + " deg; offset " + Fmt3(off) + " euler " + Fmt3(eul));
            Log("grip group " + g.id + ": fit " + fit.id + " socketOffset " + Fmt3(off) + " socketEuler " + Fmt3(eul) + " tip world dir " + Fmt3(tipW) + " (forward dot " + Fmt(Vector3.Dot(tipW.normalized, fwd.normalized)) + ")");

            // state names: the controller's REAL states that match the group (exact names or prefixes); the default row has none
            var sel = new List<string>();
            if (!g.isDefault)
            {
                foreach (var s in states)
                {
                    bool hit = Array.IndexOf(g.stateExact, s) >= 0;
                    foreach (var p in g.statePrefixes) if (p.Length > 0 && s.StartsWith(p)) hit = true;
                    if (hit) sel.Add(s);
                }
                Check("grip." + g.id + ".states", sel.Count > 0, sel.Count + " controller states [" + string.Join(", ", sel.ToArray()) + "]");
                if (sel.Count == 0) { Die(6, "grip", "group '" + g.id + "' matches no state of " + r.controller + " (prefixes " + string.Join("|", g.statePrefixes) + ")"); return false; }
            }
            if (!first) sb.Append(",\n"); first = false;
            sb.Append("  {\"name\": ").Append(Q(id)).Append(", \"weapon\": ").Append(Q(g.weapon)).Append(", \"enabled\": true, \"hand\": ").Append(Q(cfg.hand)).Append(", \"socket\": ").Append(Q(cfg.socket))
              .Append(", \"socketOffset\": ").Append(Arr(off)).Append(", \"socketEuler\": ").Append(Arr(eul)).Append(", \"slide\": ").Append(N(g.slide)).Append(", \"blendSeconds\": ").Append(N(g.blendSeconds)).Append(", \"debugLog\": false");
            if (!g.isDefault) { sb.Append(", \"states\": ["); for (int k = 0; k < sel.Count; k++) { if (k > 0) sb.Append(", "); sb.Append(Q(sel[k])); } sb.Append("]"); }
            sb.Append(", \"_gen\": \"SOBEKUNITY v1\", \"_group\": ").Append(Q(g.id)).Append("}");
        }
        sb.Append("\n ]\n}\n");
        File.WriteAllText(Path.Combine(outDir, "sobek_grip_rows.json"), sb.ToString(), new UTF8Encoding(false));
        Log("grip rows written to " + Path.Combine(outDir, "sobek_grip_rows.json") + " (run_sobek_unity.ps1 merges them into weapon_grip.json, Data and StreamingAssets)");
        return true;
    }

    // ------------------------------------------------------------------ 7 verify (everything by log)
    [Serializable] class BaseRow { public string state = ""; }
    [Serializable] class BaseDoc { public BaseRow[] @base = new BaseRow[0]; }
    [Serializable] class AtkRow { public string character = "", clip = ""; public bool enabled = true; }
    [Serializable] class AtkDoc { public AtkRow[] rows = new AtkRow[0]; }

    static void Verify()
    {
        var r = Roster.Get(id);
        Check("roster.row", Roster.Has(id) && r.objectName.Length > 0 && File.Exists(r.modelGlb), "id " + id + " object '" + r.objectName + "' model " + r.modelGlb);
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(r.controller);
        Check("controller.present", ctrl != null, r.controller);
        var states = ControllerStates(ctrl);
        Check("controller.states", states.Count >= cfg.minControllerStates, states.Count + " states (need " + cfg.minControllerStates + ")");
        Check("controller.mask", !string.IsNullOrEmpty(r.guardMask) && AssetDatabase.LoadAssetAtPath<AvatarMask>(r.guardMask) != null, r.guardMask);

        // required states: clip_build.json base + this character's enabled attack_clips rows; each exists, has a motion, and the motion belongs to this character's folder
        var need = new List<string>();
        foreach (var src in cfg.requiredStateSources)
        {
            if (src == "clip_build.base" && File.Exists("Assets/Data/clip_build.json")) { var b = JsonUtility.FromJson<BaseDoc>(File.ReadAllText("Assets/Data/clip_build.json")); if (b != null) foreach (var x in b.@base) if (x.state.Length > 0 && !need.Contains(x.state)) need.Add(x.state); }
            if (src == "attack_clips.rows" && File.Exists("Assets/Data/attack_clips.json")) { var a = JsonUtility.FromJson<AtkDoc>(File.ReadAllText("Assets/Data/attack_clips.json")); if (a != null) foreach (var x in a.rows) if (x.enabled && x.character == id && x.clip.Length > 0 && !need.Contains(x.clip)) need.Add(x.clip); }
        }
        string own = RosterEditor.Folder(r) + "/"; var missing = new List<string>(); var foreign = new List<string>();
        if (ctrl != null)
        {
            var all = new List<AnimatorState>(); foreach (var l in ctrl.layers) CollectAll(l.stateMachine, all);
            foreach (var n in need)
            {
                var st = all.Find(s => s.name == n);
                if (st == null || st.motion == null) { missing.Add(n); continue; }
                string mp = AssetDatabase.GetAssetPath(st.motion); if (!mp.StartsWith(own)) foreign.Add(n + " -> " + mp);
            }
        }
        Check("states.exist", missing.Count == 0, need.Count + " required states" + (missing.Count > 0 ? ", MISSING/no motion: " + string.Join(", ", missing.ToArray()) : ", all present with motions"));
        Check("states.ownClips", foreign.Count == 0, foreign.Count == 0 ? "every required state plays a clip under " + own : "foreign clips: " + string.Join("; ", foreign.ToArray()));

        // the built scene: renderer count, socket, one active fighter per side
        var es = JsonUtility.FromJson<EnemySwap.Cfg>(File.ReadAllText("Assets/Data/enemy_swap.json"));
        EnemySwap.Row row = null; foreach (var e in es.enemies) if (e != null && e.id == id) row = e;
        string path = EnemySwap.ScenePathOf(es, row);
        var sc = EditorSceneManager.OpenScene(path); var root = FindRoot(sc, r.objectName);
        Check("scene.root", root != null && root.activeInHierarchy, path + " root '" + r.objectName + "'");
        if (root != null)
        {
            int smr = 0; foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>(false)) if (!UnderSocket(s.transform)) smr++;
            Check("scene.renderers", smr == cfg.skinnedRenderers, smr + " active SkinnedMeshRenderers (expected " + cfg.skinnedRenderers + ")");
            var hand = FindT(root.transform, "mixamorig:" + cfg.hand);
            Check("scene.socket", hand != null && FindT(hand, cfg.socket) != null, cfg.socket + " under mixamorig:" + cfg.hand);
            var anim = root.GetComponentInChildren<Animator>(true);
            Check("scene.animator", anim != null && anim.runtimeAnimatorController != null && AssetDatabase.GetAssetPath(anim.runtimeAnimatorController) == r.controller, anim != null && anim.runtimeAnimatorController != null ? AssetDatabase.GetAssetPath(anim.runtimeAnimatorController) : "no controller on the Animator");
            int other = 0; foreach (var g in sc.GetRootGameObjects()) { var rc = Roster.Get(Roster.IdOfName(g.name)); if (g.activeInHierarchy && rc != null && rc.id.Length > 0 && g != root && rc.id != Roster.PlayerId) other++; }
            Check("scene.oneEnemy", other == 0, other + " other active enemy roots next to " + r.objectName);
        }
        Check("log.errors", logErrors.Count == 0, logErrors.Count + " error log lines during the run" + (logErrors.Count > 0 ? ", first: " + logErrors[0] + (logErrors.Count > 1 ? " | " + logErrors[1] : "") + (logErrors.Count > 2 ? " | " + logErrors[2] : "") : ""));
        bool ok = fails.Count == 0;
        string res = (ok ? "PASS" : "FAIL") + " " + checks + " checks, " + fails.Count + " failed";
        Debug.Log("SOBEKUNITY RESULT " + res);
        foreach (var f in fails) Debug.Log("SOBEKUNITY FAILED " + f);
        File.WriteAllText(Path.Combine(outDir, "sobek_unity_result.txt"), res + "\n" + string.Join("\n", fails.ToArray()) + "\n");
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 7);
    }

    static void CollectAll(AnimatorStateMachine sm, List<AnimatorState> into)
    {
        foreach (var cs in sm.states) if (cs.state != null) into.Add(cs.state);
        foreach (var sub in sm.stateMachines) CollectAll(sub.stateMachine, into);
    }
}
