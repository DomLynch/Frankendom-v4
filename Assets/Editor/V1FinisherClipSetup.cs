using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// V1FINC v94: adds the 3 V1 finisher clips (laneV1FIN "Unity step C") as Animator states on the roster characters, from data, idempotent.
//   menu Tools > Combat > Add V1 Finisher Clips      chain:  V1FinisherClipSetup.Run()      batch:  -executeMethod V1FinisherClipSetup.RunBatch   (exit 0 = RESULT OK, 2 otherwise)
// Data: Assets/Data/v1_finisher_clips.json (clip file -> state id -> targets as roles "@player" / "@enemy" or a roster id). No character names in code:
//   controller + output folder come from roster.json (Roster.Get(id).controller / finisherClipFolder), the per-character retarget numbers (referenceState, hipsRestZ, hipsCorrection) from
//   finishers.json clipImport.characters (single source, same as FinisherClipSetup), the expected rig root per character from the json `rigRoots` rows (an assertion, the real paths come from the controller).
// Same recipe as FinisherClipSetup (read that file for the reasoning): GLB imported by glTFast with animationMethod Mecanim(2) -> source AnimationClip -> every Transform curve path remapped by bone LEAF NAME onto the paths of the controller's
// own reference clip (so Joan gets Armature/root/..., Pitborn Rig/root/...), Hips rotation q' = corr*q, Hips position p' = corr*(p*ratio) with ratio = character hipsRestZ / clip hipsRestZ, rootXZ inplace/keep/limited, other bones rotation only.
// GENERIC rig on purpose (transform-path curves, the controllers have no Humanoid avatar): this is what the live fin_*.anim clips are, and FinisherPlayer's HasState/Play work on state names only.
// Idempotent: GLB bytes are not touched (laneV1FIN copies them), a re-run rewrites the same .anim (ClearCurves + same curves) and re-points the existing state; controllers are backed up once to Tools/_backup_clips_v1finc/.
public static class V1FinisherClipSetup
{
    [Serializable] class ClipRow { public string id, file, role, rootXZ = "inplace"; public string[] targets; public float maxStepM, hipsRestZ = 1f, hipsYOffsetM, expectedDurationS; }
    [Serializable] class RigRootRow { public string id, rigRoot; }
    [Serializable] class Tol { public float durationS = 0.06f, hipsStartHeightM = 0.2f; }
    [Serializable] class Cfg { public string glbDir, finishersJson; public Tol stateTolerance; public RigRootRow[] rigRoots; public ClipRow[] clips; }
    [Serializable] class CharRow { public string name, referenceState; public float hipsRestZ = 1f; public float[] hipsCorrection; }
    [Serializable] class ImportBlock { public CharRow[] characters; }
    [Serializable] class FinRoot { public ImportBlock clipImport; }

    const string DataPath = "Assets/Data/v1_finisher_clips.json";
    static int unmatched, states, clipsBuilt, bad;
    // FINISHER5 v1: -bakeOnly A,B on the command line (or BakeOnly set from code) restricts the bake to those roster ids (the monster finisher victims + Joan's killer clip); empty = every target = today.
    //   command: Unity.exe -batchmode -quit -projectPath <p> -executeMethod V1FinisherClipSetup.RunBatchOnly -bakeOnly Grendel,Humbaba,Erlking,Sobek,Joan
    public static string[] BakeOnly = new string[0];
    static bool Wanted(string id) { if (BakeOnly == null || BakeOnly.Length == 0) return true; foreach (var s in BakeOnly) if (string.Equals(s.Trim(), id, StringComparison.OrdinalIgnoreCase)) return true; return false; }
    public static void RunBatchOnly() { var a = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "-bakeOnly") BakeOnly = a[i + 1].Split(','); EditorApplication.Exit(RunChecked() ? 0 : 2); }

    [MenuItem("Tools/Combat/Add V1 Finisher Clips")]
    public static void Run() { RunChecked(); }

    public static void RunBatch() { EditorApplication.Exit(RunChecked() ? 0 : 2); }

    public static bool RunChecked()
    {
        unmatched = 0; states = 0; clipsBuilt = 0; bad = 0;
        if (!File.Exists(DataPath)) { Debug.LogError("V1FinisherClipSetup: " + DataPath + " missing"); return false; }
        var cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(DataPath));
        if (cfg == null || cfg.clips == null || cfg.clips.Length == 0 || string.IsNullOrEmpty(cfg.glbDir)) { Debug.LogError("V1FinisherClipSetup: no clips / glbDir in " + DataPath); return false; }
        if (cfg.stateTolerance == null) cfg.stateTolerance = new Tol();
        if (string.IsNullOrEmpty(cfg.finishersJson) || !File.Exists(cfg.finishersJson)) { Debug.LogError("V1FinisherClipSetup: finishersJson missing: " + cfg.finishersJson); return false; }
        var fin = JsonUtility.FromJson<FinRoot>(File.ReadAllText(cfg.finishersJson));
        if (fin == null || fin.clipImport == null || fin.clipImport.characters == null) { Debug.LogError("V1FinisherClipSetup: no clipImport.characters in " + cfg.finishersJson); return false; }
        Roster.Reload();

        // 1) import the GLBs (they sit in glbDir already: laneV1FIN copies them + the .meta files; here only the import settings are made sure)
        int missing = 0;
        foreach (var c in cfg.clips)
        {
            string glb = cfg.glbDir + "/" + c.file;
            if (!File.Exists(glb)) { Debug.LogError("V1FinisherClipSetup: GLB missing " + glb); missing++; continue; }
            AssetDatabase.ImportAsset(glb, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(glb);
            if (imp != null)
            {
                var so = new SerializedObject(imp); var p = so.FindProperty("importSettings.animationMethod");   // glTFast: 0 none, 1 legacy, 2 mecanim (as the live finisher GLB metas)
                if (p != null && p.intValue != 2) { p.intValue = 2; so.ApplyModifiedPropertiesWithoutUndo(); imp.SaveAndReimport(); }
            }
        }

        // 2) per roster character that any clip targets
        var done = new HashSet<string>();
        foreach (var row in fin.clipImport.characters)
        {
            if (row == null || string.IsNullOrEmpty(row.name) || !done.Add(row.name)) continue;
            bool any = false; foreach (var c in cfg.clips) if (Targets(c, row.name)) any = true;
            if (any && Wanted(row.name)) SetupCharacter(cfg, row);   // FINISHER5 v1
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 3) every target has every state, right length, right root
        foreach (var id in done) if (Wanted(id)) Verify(cfg, id);   // FINISHER5 v1
        bool ok = missing == 0 && unmatched == 0 && bad == 0 && states > 0;
        Debug.Log("V1FinisherClipSetup: done. clips built " + clipsBuilt + ", states added/updated " + states + ", unmatched " + unmatched + ", verify problems " + bad + ", GLBs missing " + missing + (ok ? "  RESULT OK" : "  RESULT NOT OK"));
        return ok;
    }

    // ---------------------------------------------------------------- helpers
    static bool Targets(ClipRow c, string id)
    {
        if (c.targets == null || string.IsNullOrEmpty(id)) return false;
        foreach (var t in c.targets) if (RosterResolver.Resolve(t) == id) return true;
        return false;
    }

    static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        foreach (var s in sm.states) if (s.state != null && s.state.name == name) return s.state;
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
    static string FirstSeg(string path) { int k = path.IndexOf('/'); return k >= 0 ? path.Substring(0, k) : path; }

    // bone key: letters and digits only, lower case ("mixamorig:Hips" == "mixamorig_Hips")
    static string Key(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
    const string HipsKey = "mixamorighips";

    static string ExpectedRoot(Cfg cfg, string id)
    {
        if (cfg.rigRoots != null) foreach (var r in cfg.rigRoots) if (r != null && r.id == id) return r.rigRoot;
        return "";
    }

    static AnimationClip LoadSource(string glbPath, out string found)
    {
        found = ""; AnimationClip best = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(glbPath))
        {
            var c = o as AnimationClip;
            if (c == null || c.name.StartsWith("__preview__")) continue;
            found += (found.Length > 0 ? ", " : "") + c.name;
            if (best == null) best = c;
        }
        return best;
    }

    // ---------------------------------------------------------------- one character
    static void SetupCharacter(Cfg cfg, CharRow ch)
    {
        var rc = Roster.Get(ch.name);
        if (string.IsNullOrEmpty(rc.controller) || string.IsNullOrEmpty(rc.finisherClipFolder)) { Debug.LogWarning("V1FinisherClipSetup: " + ch.name + ": no controller / finisherClipFolder in roster.json"); unmatched++; return; }
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(rc.controller);
        if (ctrl == null) { Debug.LogWarning("V1FinisherClipSetup: controller not found " + rc.controller); unmatched++; return; }
        var baseSm = ctrl.layers[0].stateMachine;
        var refState = FindState(baseSm, ch.referenceState);
        var refClip = refState != null ? refState.motion as AnimationClip : null;
        if (refClip == null) { Debug.LogWarning("V1FinisherClipSetup: reference state '" + ch.referenceState + "' has no clip in " + rc.controller); unmatched++; return; }

        var map = new Dictionary<string, string>();
        string hipsPathRef = null;
        foreach (var b in AnimationUtility.GetCurveBindings(refClip))
        {
            string p = b.path;
            if (b.type == typeof(Transform) && Key(Leaf(b.path)) == HipsKey && hipsPathRef == null) hipsPathRef = b.path;
            while (!string.IsNullOrEmpty(p))
            {
                string k = Key(Leaf(p));
                if (!map.ContainsKey(k)) map[k] = p;
                int s = p.LastIndexOf('/');
                p = s > 0 ? p.Substring(0, s) : "";
            }
        }
        string root = hipsPathRef != null ? FirstSeg(hipsPathRef) : "";
        string want = ExpectedRoot(cfg, ch.name);
        if (root.Length == 0 || (want.Length > 0 && root != want)) { Debug.LogError("V1FinisherClipSetup: " + ch.name + " rig root is '" + root + "' in " + rc.controller + ", json rigRoots says '" + want + "'"); bad++; return; }
        Debug.Log("V1FinisherClipSetup: " + ch.name + " rig root '" + root + "' (hips " + hipsPathRef + ")");

        try
        {
            string dir = "Tools/_backup_clips_v1finc";
            Directory.CreateDirectory(dir);
            string dstB = Path.Combine(dir, Path.GetFileName(rc.controller));
            if (!File.Exists(dstB)) File.Copy(rc.controller, dstB);
        }
        catch (Exception ex) { Debug.LogWarning("V1FinisherClipSetup: backup failed " + ex.Message); }

        EnsureFolder(rc.finisherClipFolder);
        Quaternion corr = Quaternion.identity;
        if (ch.hipsCorrection != null && ch.hipsCorrection.Length == 4) corr = new Quaternion(ch.hipsCorrection[0], ch.hipsCorrection[1], ch.hipsCorrection[2], ch.hipsCorrection[3]);

        int added = 0;
        foreach (var cc in cfg.clips)
        {
            if (!Targets(cc, ch.name)) continue;
            string glb = cfg.glbDir + "/" + cc.file, found = "";
            var s = File.Exists(glb) ? LoadSource(glb, out found) : null;
            if (s == null && File.Exists(glb)) { AssetDatabase.ImportAsset(glb, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport); s = LoadSource(glb, out found); }
            if (s == null) { Debug.LogWarning("V1FinisherClipSetup: " + ch.name + " clip '" + cc.id + "': no AnimationClip in " + glb + " (found: " + found + ")"); unmatched++; continue; }

            string apath = rc.finisherClipFolder + "/" + cc.id + ".anim";
            var dstClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(apath);
            bool isNew = dstClip == null;
            if (isNew) dstClip = new AnimationClip(); else dstClip.ClearCurves();
            dstClip.name = cc.id; dstClip.frameRate = s.frameRate > 0f ? s.frameRate : 30f; dstClip.legacy = false;

            float ratio = Mathf.Clamp(ch.hipsRestZ / Mathf.Max(0.05f, cc.hipsRestZ), 0.33f, 3f);
            int hit = 0, miss = 0, dropped = 0; var missNames = new List<string>();
            var hipsRot = new Dictionary<string, AnimationCurve>(); var hipsPos = new Dictionary<string, AnimationCurve>();
            string hipsPath = null;
            foreach (var b in AnimationUtility.GetCurveBindings(s))
            {
                if (b.type != typeof(Transform)) { dropped++; continue; }
                bool rot = b.propertyName.StartsWith("m_LocalRotation."), pos = b.propertyName.StartsWith("m_LocalPosition.");
                if (!rot && !pos) { dropped++; continue; }
                string key = Key(Leaf(b.path)); bool isHips = key == HipsKey;
                if (pos && !isHips) { dropped++; continue; }
                string np;
                if (!map.TryGetValue(key, out np)) { miss++; if (missNames.Count < 6) missNames.Add(Leaf(b.path)); continue; }
                var curve = AnimationUtility.GetEditorCurve(s, b);
                if (isHips)
                {
                    hipsPath = np;
                    string comp = b.propertyName.Substring(b.propertyName.IndexOf('.') + 1);
                    if (rot) hipsRot[comp] = curve; else hipsPos[comp] = curve;
                    hit++;
                    continue;
                }
                var nb = b; nb.path = np;
                AnimationUtility.SetEditorCurve(dstClip, nb, curve);
                hit++;
            }

            float z0Clip = float.NaN;
            if (hipsPath != null) z0Clip = WriteHips(dstClip, hipsPath, hipsRot, hipsPos, corr, ratio, cc);
            else { Debug.LogWarning("V1FinisherClipSetup: " + ch.name + " clip '" + cc.id + "' has no Hips curves"); miss++; }

            var set = AnimationUtility.GetAnimationClipSettings(s);
            set.loopTime = false; set.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(dstClip, set);
            if (isNew) AssetDatabase.CreateAsset(dstClip, apath); else EditorUtility.SetDirty(dstClip);

            var state = FindState(baseSm, cc.id);
            if (state == null) state = baseSm.AddState(cc.id);
            state.motion = dstClip; state.writeDefaultValues = true; state.speed = 1f;
            added++; clipsBuilt++; states++; unmatched += miss;
            string hipsMsg = float.IsNaN(z0Clip) ? "" : ", hips up t0 " + z0Clip.ToString("0.000") + (Mathf.Abs(ch.hipsRestZ - z0Clip) > cfg.stateTolerance.hipsStartHeightM ? "  (WARNING: differs from the character's standing hipsRestZ " + ch.hipsRestZ.ToString("0.000") + ")" : "");
            Debug.Log("V1FinisherClipSetup: " + ch.name + " state '" + cc.id + "' len " + dstClip.length.ToString("0.000") + " s (expected " + cc.expectedDurationS.ToString("0.000") + "), curves remapped " + hit + ", dropped " + dropped + ", unmatched " + miss + (miss > 0 ? "  (WARNING: unmatched bones will not animate: " + string.Join(", ", missNames.ToArray()) + ")" : "") + ", hipsRatio " + ratio.ToString("0.000") + ", rootXZ " + cc.rootXZ + hipsMsg);
        }
        EditorUtility.SetDirty(ctrl);
        Debug.Log("V1FinisherClipSetup: " + rc.controller + " +" + added + " V1 finisher states");
    }

    // Retargets and writes the Hips curves (same maths as FinisherClipSetup.WriteHips). Returns the hips up value at the first key.
    static float WriteHips(AnimationClip dst, string path, Dictionary<string, AnimationCurve> rot, Dictionary<string, AnimationCurve> pos, Quaternion corr, float ratio, ClipRow cc)
    {
        float z0 = float.NaN;
        AnimationCurve rx, ry, rz, rw;
        if (rot.TryGetValue("x", out rx) && rot.TryGetValue("y", out ry) && rot.TryGetValue("z", out rz) && rot.TryGetValue("w", out rw))
        {
            var kx = new Keyframe[rx.length]; var ky = new Keyframe[rx.length]; var kz = new Keyframe[rx.length]; var kw = new Keyframe[rx.length];
            Quaternion prev = Quaternion.identity; bool first = true;
            for (int i = 0; i < rx.length; i++)
            {
                float t = rx.keys[i].time;
                var q = corr * new Quaternion(rx.Evaluate(t), ry.Evaluate(t), rz.Evaluate(t), rw.Evaluate(t));
                float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); if (n > 1e-6f) q = new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
                if (!first && (prev.x * q.x + prev.y * q.y + prev.z * q.z + prev.w * q.w) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                prev = q; first = false;
                kx[i] = new Keyframe(t, q.x); ky[i] = new Keyframe(t, q.y); kz[i] = new Keyframe(t, q.z); kw[i] = new Keyframe(t, q.w);
            }
            SetLinear(dst, path, "m_LocalRotation.x", kx); SetLinear(dst, path, "m_LocalRotation.y", ky); SetLinear(dst, path, "m_LocalRotation.z", kz); SetLinear(dst, path, "m_LocalRotation.w", kw);
        }
        AnimationCurve px, py, pz;
        if (pos.TryGetValue("x", out px) && pos.TryGetValue("y", out py) && pos.TryGetValue("z", out pz))
        {
            int n = px.length; var ox = new Keyframe[n]; var oy = new Keyframe[n]; var oz = new Keyframe[n];
            var v = new Vector3[n]; var tt = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = px.keys[i].time; tt[i] = t;
                v[i] = corr * (new Vector3(px.Evaluate(t), py.Evaluate(t), pz.Evaluate(t)) * ratio);
            }
            float h0x = n > 0 ? v[0].x : 0f, h0y = n > 0 ? v[0].y : 0f, travel = 0f;
            for (int i = 0; i < n; i++) travel = Mathf.Max(travel, new Vector2(v[i].x - h0x, v[i].y - h0y).magnitude);
            float k = 1f;
            if (cc.rootXZ == "limited" && cc.maxStepM > 0f && travel > 1e-6f) k = Mathf.Min(1f, cc.maxStepM / travel);
            for (int i = 0; i < n; i++)
            {
                float x = v[i].x, y = v[i].y;
                if (cc.rootXZ == "inplace") { x = h0x; y = h0y; }
                else if (cc.rootXZ == "limited") { x = h0x + (x - h0x) * k; y = h0y + (y - h0y) * k; }
                ox[i] = new Keyframe(tt[i], x); oy[i] = new Keyframe(tt[i], y); oz[i] = new Keyframe(tt[i], v[i].z + cc.hipsYOffsetM);
            }
            z0 = n > 0 ? v[0].z + cc.hipsYOffsetM : float.NaN;
            SetLinear(dst, path, "m_LocalPosition.x", ox); SetLinear(dst, path, "m_LocalPosition.y", oy); SetLinear(dst, path, "m_LocalPosition.z", oz);
        }
        return z0;
    }

    static void SetLinear(AnimationClip clip, string path, string prop, Keyframe[] keys)
    {
        var curve = new AnimationCurve(keys);
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), prop), curve);
    }

    // ---------------------------------------------------------------- verify: the state is on the controller, the clip has the right length and every curve path starts at the rig root
    static void Verify(Cfg cfg, string id)
    {
        bool any = false; foreach (var c in cfg.clips) if (Targets(c, id)) any = true;
        if (!any) return;
        var rc = Roster.Get(id);
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(rc.controller);
        if (ctrl == null) { Debug.LogError("V1FinisherClipSetup: verify: controller missing for " + id); bad++; return; }
        var sm = ctrl.layers[0].stateMachine;
        foreach (var c in cfg.clips)
        {
            if (!Targets(c, id)) continue;
            var st = FindState(sm, c.id); var clip = st != null ? st.motion as AnimationClip : null;
            if (clip == null) { Debug.LogError("V1FinisherClipSetup: verify: " + id + " has no state '" + c.id + "'"); bad++; continue; }
            string root = ExpectedRoot(cfg, id); int off = 0, total = 0;
            foreach (var b in AnimationUtility.GetCurveBindings(clip)) { total++; if (root.Length > 0 && FirstSeg(b.path) != root) off++; }
            bool lenOk = c.expectedDurationS <= 0f || Mathf.Abs(clip.length - c.expectedDurationS) <= cfg.stateTolerance.durationS;
            bool okc = off == 0 && total > 0 && lenOk;
            if (!okc) bad++;
            Debug.Log("V1FinisherClipSetup: verify " + id + " '" + c.id + "': " + total + " curves, " + off + " outside root '" + root + "', len " + clip.length.ToString("0.000") + (lenOk ? "" : " (expected " + c.expectedDurationS.ToString("0.000") + ")") + (okc ? " OK" : " PROBLEM"));
        }
    }
}
