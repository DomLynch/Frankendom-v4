using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// BANDITS v1 (laneBANDITS). ONE Editor call that imports the hooded bandit bodies end to end, for the ids in Assets/Data/bandits.json types[] that are not "off" ONLY.
// Install as Assets/Editor/BanditsBatch.cs (Editor-only folder). No existing file is edited: the per-id work is done by the EXISTING code, reached like this:
//   - EnemySwap.ImportOne(Cfg, Row), EnemySwap.BuildOne(Cfg, Row) and EnemySwap.CheckSides(Scene,string,string,bool) are private static: bound ONCE by reflection (Delegate.CreateDelegate, signature
//     checked, a clear FAILED line if EnemySwap ever changes). They are the very code behind Hades > Enemy > 1 / 2 / 4, called per row of a FILTERED copy of enemy_swap.json (same public Cfg class, bandit rows only).
//   - GuardLayerSetup.RunFor(id) is public (one roster id).
//   - Hades > Zones > Save creature prefabs has no id filter (SaveAll loops every zones.json creature), so its few lines are mirrored here for the bandit creatures only (ZoneData is public).
// Batch:  Unity.exe -batchmode -quit -projectPath <p> -executeMethod BanditsBatch.Run -logFile <f>      Menu: Hades/Bandits/Import all (batch)
// Optional arg  -banditsIds BanditMelee,BanditArcher  restricts further (subset of the non-off ids).
// Idempotent: every called step is copy-only-if-newer / controller only if absent / scene rebuilt from a fresh base copy / prefab overwritten / guard layer refreshed.
public static class BanditsBatch
{
    const string BanditsJson = "Assets/Data/bandits.json";
    const string SwapJson = "Assets/Data/enemy_swap.json";
    // OpenTrident delivery (camp_npcs_clipbuild/out). Used ONLY when the enemy_swap row has no readable sourceGlb / sourceAnimGlb (apply_bandits writes them from bandits.json "delivered", absent today), and only in memory.
    const string DeliveryDir = @"C:\Users\dom12\Documents\kits_out\opentrident_for_frankendom\camp_npcs_clipbuild\out_plus\";
    static readonly string[,] Delivered = { { "BanditAssassin", "akai" }, { "BanditMelee", "arissa" }, { "BanditArcher", "erika_bow" } };

    [Serializable] class BType { public string id = ""; public bool off = false; }
    [Serializable] class BFile { public BType[] types = new BType[0]; }

    delegate void ImportOneFn(EnemySwap.Cfg cfg, EnemySwap.Row row);
    delegate bool BuildOneFn(EnemySwap.Cfg cfg, EnemySwap.Row row);
    delegate bool CheckSidesFn(Scene sc, string playerObject, string enemyObject, bool repair);

    // ------------------------------------------------------------------ error capture (a LogError / exception inside one step = that step FAILED)
    static int errCount; static readonly List<string> errText = new List<string>();
    static void OnLog(string msg, string stack, LogType t)
    {
        if (t != LogType.Error && t != LogType.Exception && t != LogType.Assert) return;
        lock (errText) { errCount++; if (errText.Count < 6) errText.Add(msg.Length > 220 ? msg.Substring(0, 220) : msg); }
    }

    /// <summary>Runs a, returns the number of errors it logged (an exception counts as one).</summary>
    static int Guarded(Action a, out string firstErrors)
    {
        lock (errText) { errCount = 0; errText.Clear(); }
        Application.logMessageReceivedThreaded += OnLog;
        try { a(); }
        catch (Exception e) { Debug.LogError("BANDITS exception: " + e); }
        finally { Application.logMessageReceivedThreaded -= OnLog; }
        lock (errText) { firstErrors = string.Join(" | ", errText); return errCount; }
    }

    static bool failed;
    static void Fail(string line) { failed = true; Debug.LogError("BANDITS " + line); }

    [MenuItem("Hades/Bandits/Import all (batch)")]
    static void Menu()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { Debug.Log("BANDITS cancelled (unsaved scenes)"); return; }
        Core();
    }

    /// <summary>-executeMethod entry. Exits the process (0 ok / 1 failed) ONLY in batch mode, so calling it from a live Editor never closes the Editor.</summary>
    public static void Run() { int code = Core(); if (Application.isBatchMode) EditorApplication.Exit(code); }

    static int Core()
    {
        failed = false; int code;
        try { code = Work() ? 0 : 1; }
        catch (Exception e) { Debug.LogError("BANDITS exception: " + e); code = 1; }
        if (code == 0) Debug.Log("BANDITS BATCH OK"); else Debug.LogError("BANDITS BATCH FAILED");
        return code;
    }

    static bool Work()
    {
        bool batch = Application.isBatchMode;
        // ---- gate: refuse when not in batch mode AND bandits.json is missing; with no file there are no ids at all, so batch mode refuses too
        if (!File.Exists(BanditsJson)) { Fail("refused: " + BanditsJson + " is missing" + (batch ? "" : " and Unity is not in batch mode") + " (run apply_bandits.py --apply first)"); return false; }
        BFile bf;
        try { bf = JsonUtility.FromJson<BFile>(File.ReadAllText(BanditsJson).TrimStart('\uFEFF')); } catch (Exception e) { Fail("refused: " + BanditsJson + " unreadable: " + e.Message); return false; }
        var ids = new List<string>();
        if (bf != null && bf.types != null) foreach (var t in bf.types) if (t != null && !string.IsNullOrEmpty(t.id) && !t.off && !ids.Contains(t.id)) ids.Add(t.id);
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-banditsIds")
        {
            var want = new List<string>(args[i + 1].Split(','));
            for (int k = ids.Count - 1; k >= 0; k--) if (!want.Contains(ids[k])) ids.RemoveAt(k);
            foreach (var w in want) if (!ids.Contains(w)) Debug.LogWarning("BANDITS -banditsIds '" + w + "' is not an active type in bandits.json: ignored");
        }
        if (ids.Count == 0) { Fail("refused: no non-off types[] ids in " + BanditsJson); return false; }
        Debug.Log("BANDITS start: ids=[" + string.Join(",", ids) + "] batchMode=" + batch + " (only these ids are processed; every other enemy asset is snapshot-checked at the end)");

        // ---- bind the private EnemySwap steps once
        ImportOneFn importOne; BuildOneFn buildOne; CheckSidesFn checkSides;
        try { importOne = Bind<ImportOneFn>("ImportOne"); buildOne = Bind<BuildOneFn>("BuildOne"); checkSides = Bind<CheckSidesFn>("CheckSides"); }
        catch (Exception e) { Fail("refused: EnemySwap changed shape, cannot bind (" + e.Message + ")"); return false; }

        // ---- step 0 preflight (data the apply script must already have written)
        Roster.Reload(); ZoneData.Reload();
        if (!File.Exists(SwapJson)) { Fail("step 0 preflight FAILED: " + SwapJson + " missing"); return false; }
        EnemySwap.Cfg full;
        try { full = JsonUtility.FromJson<EnemySwap.Cfg>(File.ReadAllText(SwapJson).TrimStart('\uFEFF')); } catch (Exception e) { Fail("step 0 preflight FAILED: enemy_swap.json unreadable: " + e.Message); return false; }
        if (full == null || full.enemies == null) { Fail("step 0 preflight FAILED: enemy_swap.json has no enemies[]"); return false; }
        var rows = new Dictionary<string, EnemySwap.Row>(); var others = new List<EnemySwap.Row>();
        foreach (var r in full.enemies)
        {
            if (r == null || string.IsNullOrEmpty(r.id)) continue;
            if (ids.Contains(r.id)) { if (rows.ContainsKey(r.id)) Debug.LogWarning("BANDITS duplicate enemy_swap row '" + r.id + "': first one used"); else rows[r.id] = r; }
            else others.Add(r);
        }
        var T = ZoneData.Get(); var live = new List<string>(ids);
        foreach (var id in ids)
        {
            var miss = new List<string>();
            if (!rows.ContainsKey(id)) miss.Add("enemy_swap.json row");
            if (!Roster.Has(id)) miss.Add("roster.json row");
            bool cre = false; foreach (var c in T.creatures) if (c != null && c.id == id && c.source == "resources" && c.path.Length > 0) cre = true;
            if (!cre) miss.Add("zones.json creature (source resources)");
            if (miss.Count == 0)
            {
                var er = Roster.Get(id); var row = rows[id]; string tid = TemplateId(full, row);
                if (!Roster.Has(tid) || !File.Exists(Roster.Get(tid).controller)) miss.Add("template '" + tid + "' roster row / controller");
                if (er.modelGlb.Length == 0 || er.controller.Length == 0) miss.Add("roster modelGlb/controller path");
                // bandits-dev2 fix: PLACEHOLDER mode points the roster modelGlb at Dracula's shared glb (not under Assets/Characters/<id>/): never fill a source then, or CopyIfNewer would overwrite his glb with a bandit body
                if (er.modelGlb.Replace('\\', '/').Contains("/" + id + "/")) FillSource(id, row); else Debug.Log("BANDITS " + id + ": roster modelGlb " + er.modelGlb + " is not this id's own folder (placeholder mode): no source fill");
                if (!File.Exists(er.modelGlb) && !File.Exists(row.sourceGlb)) miss.Add("combat glb (neither " + er.modelGlb + " nor a readable sourceGlb)");
                if (er.animGlb.Length > 0 && !File.Exists(er.animGlb) && !File.Exists(row.sourceAnimGlb)) Debug.LogWarning("BANDITS " + id + ": anim glb " + er.animGlb + " absent and no sourceAnimGlb (import continues)");
            }
            if (miss.Count > 0) { Fail("step 0 preflight: " + id + " missing " + string.Join("; ", miss)); live.Remove(id); }
        }
        Debug.Log("BANDITS step 0 preflight " + (live.Count == ids.Count ? "OK" : "FAILED") + ": " + live.Count + "/" + ids.Count + " ids ready");
        if (live.Count == 0) return false;

        var sub = new EnemySwap.Cfg { baseScene = full.baseScene, scenePattern = full.scenePattern, templateId = full.templateId };
        var subRows = new List<EnemySwap.Row>(); foreach (var id in live) subRows.Add(rows[id]); sub.enemies = subRows.ToArray();

        // snapshot of everything that belongs to OTHER enemies (checked again at the end)
        var snap = new Dictionary<string, DateTime>();
        Action<string> take = p => { if (!string.IsNullOrEmpty(p) && File.Exists(p) && !snap.ContainsKey(p)) snap[p] = File.GetLastWriteTimeUtc(p); };
        take(full.baseScene);
        foreach (var r in others)
        {
            if (!r.inPlace) take(EnemySwap.ScenePathOf(full, r));
            if (Roster.Has(r.id)) { var q = Roster.Get(r.id); take(q.controller); take(q.modelGlb); take(q.animGlb); take(q.guardMask); }
            foreach (var c in T.creatures) if (c != null && c.id == r.id && c.path.Length > 0) take("Assets/Resources/" + c.path + ".prefab");
        }
        foreach (var id in live) { string tid = TemplateId(full, rows[id]); if (Roster.Has(tid)) { var q = Roster.Get(tid); take(q.controller); take(q.modelGlb); take(q.guardMask); } }

        // ---- step 1 copy + import + controller (= Hades > Enemy > 1, bandit rows only)
        foreach (var id in new List<string>(live))
        {
            var row = rows[id]; var er = Roster.Get(id); string fe;
            bool had = File.Exists(er.modelGlb), hadCtl = File.Exists(er.controller);
            // bandits-dev2 T-POSE FIX: a controller left over from an earlier pass (the placeholder pass re-pointed its states to DRACULA's glb) is never re-pointed by EnemySwap (it only touches clips from the TEMPLATE's glb): the states then play clips of another rig = T-pose
            string tplGlb = Roster.Get(TemplateId(full, row)).modelGlb;
            if (hadCtl)
            {
                var old = AssetDatabase.LoadAssetAtPath<AnimatorController>(er.controller); int stale = old != null ? StaleClips(old, tplGlb, er.modelGlb) : 0;
                if (stale > 0) { AssetDatabase.DeleteAsset(er.controller); hadCtl = false; Debug.Log("BANDITS step 1 " + id + ": controller " + er.controller + " had " + stale + " state clip(s) from another character's combat glb (placeholder leftover): deleted, EnemySwap copies the template controller again"); }
            }
            int e = Guarded(() => { importOne(sub, row); RepointToOwn(er.controller, er.modelGlb, id); AssetDatabase.SaveAssets(); }, out fe);
            string why = e > 0 ? "errors=" + e + " [" + fe + "]" : "";
            if (e == 0 && !File.Exists(er.modelGlb)) why = "combat glb still missing";
            var ctl = e == 0 ? AssetDatabase.LoadAssetAtPath<AnimatorController>(er.controller) : null;
            if (e == 0 && why.Length == 0 && ctl == null) why = "controller not loadable " + er.controller;
            if (why.Length > 0) { Fail("step 1 import " + id + " FAILED: " + why); live.Remove(id); continue; }
            int clips = 0; foreach (var o in AssetDatabase.LoadAllAssetsAtPath(er.modelGlb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview")) clips++; }
            int left = TemplateClipsLeft(ctl, Roster.Get(TemplateId(full, row)).modelGlb);
            if (clips == 0) { Fail("step 1 import " + id + " FAILED: 0 AnimationClips in " + er.modelGlb); live.Remove(id); continue; }
            Debug.Log("BANDITS step 1 import " + id + " OK: glb " + er.modelGlb + (had ? " (was present)" : " (new)") + ", clips=" + clips + ", controller " + er.controller + (hadCtl ? " (was present)" : " (new copy of template)") + ", states still on TEMPLATE clips=" + left + (left > 0 ? " (WARN: attack/dodge/finisher bakes are not done by this tool)" : ""));
        }
        // ---- step 2 guard upper-body layer (= Tools > Combat > Add Guard Upper-Body Layer, this id only)
        foreach (var id in new List<string>(live))
        {
            var er = Roster.Get(id); string fe;
            int e = Guarded(() => GuardLayerSetup.RunFor(id), out fe);
            var ctl = AssetDatabase.LoadAssetAtPath<AnimatorController>(er.controller); int idx = -1;
            if (ctl != null) for (int i = 0; i < ctl.layers.Length; i++) if (ctl.layers[i].name == "GuardUpper") idx = i;
            string why = e > 0 ? "errors=" + e + " [" + fe + "]" : idx < 0 ? "no GuardUpper layer afterwards (no guard_idle state/clip in the controller?)" : "";
            if (why.Length == 0)
            {
                var gi = FindState(ctl.layers[0].stateMachine, "guard_idle"); string gp = gi != null && gi.motion != null ? AssetDatabase.GetAssetPath(gi.motion) : "";
                var mask = ctl.layers[idx].avatarMask; int act = 0, tot = mask != null ? mask.transformCount : 0;
                for (int i = 0; i < tot; i++) if (mask.GetTransformActive(i)) act++;
                if (act == 0) why = "GuardUpper mask has 0 active paths";
                else Debug.Log("BANDITS step 2 guard " + id + " OK: layer index " + idx + ", mask " + act + "/" + tot + " paths active, guard_idle clip from " + gp + (gp == er.modelGlb ? "" : " (WARN: not the new combat glb, mask paths may not match this rig)"));
            }
            if (why.Length > 0) { Fail("step 2 guard " + id + " FAILED: " + why); live.Remove(id); }
        }
        // ---- step 3 scenes (= Hades > Enemy > 2, bandit rows only; each scene a fresh copy of the base scene, added to the build settings)
        foreach (var id in new List<string>(live))
        {
            var row = rows[id]; string fe, path = EnemySwap.ScenePathOf(full, row); bool ok = false;
            int e = Guarded(() => { Roster.Reload(); ok = buildOne(sub, row); }, out fe);
            if (e > 0 || !ok || !File.Exists(path)) { Fail("step 3 scene " + id + " FAILED: " + path + " ok=" + ok + " errors=" + e + (fe.Length > 0 ? " [" + fe + "]" : "")); live.Remove(id); }
            else Debug.Log("BANDITS step 3 scene " + id + " OK: " + path);
        }
        // ---- step 3b ground: the cloned template keeps ITS model local Y; put the lowest skinned vertex of the new body on the root pivot (frames showed the bodies 0.5-1 m in the air)
        foreach (var id in new List<string>(live))
        {
            var row = rows[id]; var er = Roster.Get(id); string fe, path = EnemySwap.ScenePathOf(full, row);
            int e = Guarded(() => GroundFix(id, path, er.objectName), out fe);
            if (e > 0) { Fail("step 3b ground " + id + " FAILED: errors=" + e + " [" + fe + "]"); live.Remove(id); }
        }
        // ---- step 4 creature prefabs (= Hades > Zones > Save creature prefabs, bandit creatures only)
        foreach (var id in new List<string>(live))
        {
            string fe; int saved = 0, missing = 0;
            int e = Guarded(() => SavePrefabs(id, T, ref saved, ref missing), out fe);
            if (e > 0 || missing > 0 || saved == 0) { Fail("step 4 prefab " + id + " FAILED: saved=" + saved + " missing=" + missing + " errors=" + e + (fe.Length > 0 ? " [" + fe + "]" : "")); live.Remove(id); }
            else Debug.Log("BANDITS step 4 prefab " + id + " OK: " + saved + " prefab(s) saved");
        }
        // ---- step 5 check (= Hades > Enemy > 4 logic, read-only: one active player root and one active enemy root per scene; plus the prefab file)
        foreach (var id in new List<string>(live))
        {
            var row = rows[id]; string path = EnemySwap.ScenePathOf(full, row); string fe; bool ok = false;
            int e = Guarded(() =>
            {
                var sc = EditorSceneManager.OpenScene(path);
                ok = checkSides(sc, Roster.PlayerObject, Roster.Get(id).objectName, false);
            }, out fe);
            bool pf = true; foreach (var c in T.creatures) if (c != null && c.id == id && c.source == "resources" && !File.Exists("Assets/Resources/" + c.path + ".prefab")) pf = false;
            if (e > 0 || !ok || !pf) { Fail("step 5 check " + id + " FAILED: sides=" + ok + " prefabExists=" + pf + " errors=" + e + (fe.Length > 0 ? " [" + fe + "]" : "")); live.Remove(id); }
            else Debug.Log("BANDITS step 5 check " + id + " OK: " + path + " has one active " + Roster.PlayerObject + " and one active " + Roster.Get(id).objectName + "; prefab present");
        }
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        // ---- step 6 other enemies untouched
        int moved = 0;
        foreach (var kv in snap) if (!File.Exists(kv.Key) || File.GetLastWriteTimeUtc(kv.Key) != kv.Value) { moved++; Fail("step 6 untouched FAILED: " + kv.Key + " changed or vanished"); }
        Debug.Log("BANDITS step 6 untouched " + (moved == 0 ? "OK" : "FAILED") + ": " + (snap.Count - moved) + " of " + snap.Count + " files of other enemies / base scene / template unchanged");
        Debug.Log("BANDITS summary: requested=" + ids.Count + " fully done=" + live.Count + " [" + string.Join(",", live) + "]");
        return !failed && live.Count == ids.Count;
    }

    // ------------------------------------------------------------------ helpers
    static T Bind<T>(string name) where T : Delegate
    {
        var m = typeof(EnemySwap).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (m == null) throw new MissingMethodException("EnemySwap." + name);
        return (T)(object)Delegate.CreateDelegate(typeof(T), m);
    }

    // ---------------------------------------------------------------- bandits-dev2: controller hygiene, own-clip re-pointing, ground fix
    static string ShortName(string n) { int i = n.LastIndexOf('|'); return i >= 0 ? n.Substring(i + 1) : n; }

    static void AllStates(AnimatorStateMachine sm, List<AnimatorState> into)
    {
        foreach (var cs in sm.states) into.Add(cs.state);
        foreach (var sub in sm.stateMachines) AllStates(sub.stateMachine, into);
    }

    static void ClipsOf(Motion m, List<AnimationClip> into)
    {
        if (m == null) return;
        var c = m as AnimationClip; if (c != null) { into.Add(c); return; }
        var bt = m as BlendTree; if (bt != null) foreach (var ch in bt.children) ClipsOf(ch.motion, into);
    }

    /// <summary>States whose clip lives in some OTHER character's combat glb (neither the template's nor this id's own).</summary>
    static int StaleClips(AnimatorController ctl, string tplGlb, string ownGlb)
    {
        var sts = new List<AnimatorState>(); foreach (var l in ctl.layers) AllStates(l.stateMachine, sts);
        int n = 0;
        foreach (var st in sts)
        {
            var cl = new List<AnimationClip>(); ClipsOf(st.motion, cl);
            foreach (var c in cl) { string p = AssetDatabase.GetAssetPath(c); if (p.EndsWith("_combat.glb") && p != tplGlb && p != ownGlb) n++; }
        }
        return n;
    }

    static Motion RemapOwn(Motion m, Dictionary<string, AnimationClip> own, string ownGlb, ref int same, ref int byName, ref int fb, List<string> fbNames)
    {
        if (m == null) return null;
        var c = m as AnimationClip;
        if (c != null)
        {
            if (AssetDatabase.GetAssetPath(c) == ownGlb) { same++; return c; }
            string n = ShortName(c.name); AnimationClip j;
            if (own.TryGetValue(n, out j)) { byName++; return j; }
            string l = n.ToLowerInvariant(); bool atk = l.Contains("atk") || l.Contains("attack") || l.Contains("slash") || l.Contains("light") || l.Contains("heavy") || l.Contains("maul") || l.Contains("claw") || l.Contains("swing") || l.Contains("strike") || l.Contains("special") || l.Contains("thrust");
            AnimationClip f; if (!own.TryGetValue(atk ? "light_attack_R" : "idle", out f) && !own.TryGetValue("idle", out f)) return c;
            fb++; if (fbNames.Count < 40) fbNames.Add(n + "->" + f.name); return f;
        }
        var bt = m as BlendTree;
        if (bt != null) { var ch = bt.children; for (int i = 0; i < ch.Length; i++) ch[i].motion = RemapOwn(ch[i].motion, own, ownGlb, ref same, ref byName, ref fb, fbNames); bt.children = ch; }
        return m;
    }

    /// <summary>After EnemySwap's import: EVERY state clip of the controller comes from this id's own combat glb (by clip name; a foreign attack/maul/other clip falls back to light_attack_R / idle). No clip of another rig is left to T-pose the body.</summary>
    static void RepointToOwn(string ctlPath, string ownGlb, string id)
    {
        var ctl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctlPath); if (ctl == null) { Debug.LogError("BANDITS repoint " + id + ": controller not loadable " + ctlPath); return; }
        var own = new Dictionary<string, AnimationClip>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ownGlb)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview")) own[ShortName(c.name)] = c; }
        if (!own.ContainsKey("idle")) { Debug.LogError("BANDITS repoint " + id + ": no 'idle' clip in " + ownGlb + " (clips=" + own.Count + ")"); return; }
        var sts = new List<AnimatorState>(); foreach (var l in ctl.layers) AllStates(l.stateMachine, sts);
        int same = 0, byName = 0, fb = 0, states = 0; var fbNames = new List<string>();
        foreach (var st in sts)
        {
            states++; var m = RemapOwn(st.motion, own, ownGlb, ref same, ref byName, ref fb, fbNames);
            if (m != st.motion) { st.motion = m; EditorUtility.SetDirty(st); }
        }
        EditorUtility.SetDirty(ctl);
        Debug.Log("BANDITS step 1 repoint " + id + ": " + states + " states, clips already own=" + same + ", re-pointed by name=" + byName + ", fallback to own idle/light_attack_R=" + fb + (fb > 0 ? " [" + string.Join(", ", fbNames) + "]" : ""));
    }

    /// <summary>Opens the enemy scene, measures the lowest skinned vertex of the new body (bind pose, BakeMesh like EnemySwap.WorldHeight) against the root pivot and moves the model's local Y so the feet stand on the pivot.</summary>
    static void GroundFix(string id, string scenePath, string objectName)
    {
        var sc = EditorSceneManager.OpenScene(scenePath); GameObject root = null;
        foreach (var g in sc.GetRootGameObjects()) if (g.name == objectName) root = g;
        if (root == null) { Debug.LogWarning("BANDITS step 3b ground " + id + ": no root '" + objectName + "' in " + scenePath); return; }
        var an = root.GetComponentInChildren<Animator>(true); if (an == null) { Debug.LogWarning("BANDITS step 3b ground " + id + ": no Animator"); return; }
        var model = an.transform; float lo = float.MaxValue;
        foreach (var s in model.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (!s.gameObject.activeInHierarchy || s.sharedMesh == null) continue;
            var m = new Mesh(); s.BakeMesh(m); var vs = m.vertices; var tf = s.transform;
            for (int i = 0; i < vs.Length; i++) { float y = tf.TransformPoint(vs[i]).y; if (y < lo) lo = y; }
            UnityEngine.Object.DestroyImmediate(m);
        }
        if (lo == float.MaxValue) { Debug.LogWarning("BANDITS step 3b ground " + id + ": no visible skinned mesh"); return; }
        float d = lo - root.transform.position.y; float py = model.localPosition.y;
        if (Mathf.Abs(d) > 0.02f)
        {
            float ps = model.parent != null ? Mathf.Max(0.0001f, model.parent.lossyScale.y) : 1f; var p = model.localPosition; p.y -= d / ps; model.localPosition = p;
            EditorSceneManager.MarkSceneDirty(sc); EditorSceneManager.SaveScene(sc);
            Debug.Log("BANDITS step 3b ground " + id + " OK: lowest vertex was " + d.ToString("F3") + " m above the root pivot, model local Y " + py.ToString("F3") + " -> " + p.y.ToString("F3") + " (scene saved)");
        }
        else Debug.Log("BANDITS step 3b ground " + id + " OK: lowest vertex " + d.ToString("F3") + " m from the root pivot, no change (model local Y " + py.ToString("F3") + ")");
    }

    static string TemplateId(EnemySwap.Cfg c, EnemySwap.Row r) { return !string.IsNullOrEmpty(r.templateId) ? r.templateId : !string.IsNullOrEmpty(c.templateId) ? c.templateId : Roster.DefaultEnemyId; }

    // in memory only: a row without a readable sourceGlb gets the OpenTrident delivery path (never written back to enemy_swap.json)
    static void FillSource(string id, EnemySwap.Row row)
    {
        for (int i = 0; i < Delivered.GetLength(0); i++)
        {
            if (Delivered[i, 0] != id) continue;
            if (string.IsNullOrEmpty(row.sourceGlb) || !File.Exists(row.sourceGlb)) { string p = DeliveryDir + Delivered[i, 1] + "_combat.glb"; if (File.Exists(p)) { row.sourceGlb = p; Debug.Log("BANDITS " + id + ": sourceGlb (in memory) = " + p); } }
            if (string.IsNullOrEmpty(row.sourceAnimGlb) || !File.Exists(row.sourceAnimGlb)) { string p = DeliveryDir + Delivered[i, 1] + "_anim.glb"; if (File.Exists(p)) { row.sourceAnimGlb = p; Debug.Log("BANDITS " + id + ": sourceAnimGlb (in memory) = " + p); } }
        }
    }

    static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        foreach (var s in sm.states) if (s.state != null && s.state.name == name) return s.state;
        return null;
    }

    static int TemplateClipsLeft(AnimatorController c, string templateGlb)
    {
        int n = 0;
        foreach (var l in c.layers) n += CountSm(l.stateMachine, templateGlb);
        return n;
    }
    static int CountSm(AnimatorStateMachine sm, string glb)
    {
        int n = 0;
        foreach (var s in sm.states) n += CountMotion(s.state != null ? s.state.motion : null, glb);
        foreach (var s in sm.stateMachines) n += CountSm(s.stateMachine, glb);
        return n;
    }
    static int CountMotion(Motion m, string glb)
    {
        if (m == null) return 0;
        var clip = m as AnimationClip; if (clip != null) return AssetDatabase.GetAssetPath(clip) == glb ? 1 : 0;
        var bt = m as BlendTree; int n = 0; if (bt != null) foreach (var ch in bt.children) n += CountMotion(ch.motion, glb);
        return n;
    }

    // mirror of ZoneCreaturePrefab.SaveAll for the creatures whose id == id (same scene lookup, same root match, same destination)
    static void SavePrefabs(string id, ZoneTable T, ref int saved, ref int missing)
    {
        foreach (var c in T.creatures)
        {
            if (c == null || c.id != id || c.source != "resources" || c.path.Length == 0) continue;
            string dst = "Assets/Resources/" + c.path + ".prefab";
            string sceneName = c.scene;
            if (string.IsNullOrEmpty(sceneName)) foreach (var z in T.zones) if (z != null && z.scene.Length > 0) foreach (var s in z.spawns) if (s != null) foreach (var p in s.picks) if (p != null && p.creature == c.id && string.IsNullOrEmpty(sceneName)) sceneName = z.scene;
            string scenePath = string.IsNullOrEmpty(sceneName) ? null : "Assets/Scenes/" + sceneName + ".unity";
            if (scenePath == null || !File.Exists(scenePath)) { Debug.LogError("BANDITS prefab " + c.id + ": enemy scene not found (" + scenePath + ")"); if (!File.Exists(dst)) missing++; continue; }
            Scene sc = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            string obj = Roster.Get(c.id).objectName; GameObject root = null;
            foreach (var r in sc.GetRootGameObjects()) if (r.name.Contains(obj)) { root = r; break; }
            if (root == null) { Debug.LogError("BANDITS prefab " + c.id + ": no root named '" + obj + "' in " + scenePath); EditorSceneManager.CloseScene(sc, true); if (!File.Exists(dst)) missing++; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            PrefabUtility.SaveAsPrefabAsset(root, dst); saved++; Debug.Log("BANDITS saved " + dst);
            EditorSceneManager.CloseScene(sc, true);
            if (!File.Exists(dst)) missing++;
        }
        AssetDatabase.Refresh();
    }
}
