using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// FN2: imports the paired Mixamo finisher clips (Assets/Data/finishers.json) and adds them as Animator states to the Dracula / Pitborn controllers.
//   menu Tools > Combat > Add Finisher Clips        batch:  -executeMethod FinisherClipSetup.RunBatch   (only the dev opens the Editor; exit code 0 = unmatched 0 and every pair resolved, 2 otherwise)
// Modelled on AttackClipSetup (same leaf-name path remap onto the controller's own `referenceState` clip, same state recipe) but the source is the raw Mixamo stand-in GLB, not a baked one, so the retarget that
// Tools/bake_attack_clips.py does in Python is done here on the curves, with the numbers read from the data:
//   Hips rotation  q' = hipsCorrection * q          (corr = q_rig * inverse(q_mixamo_rest), measured per character from <Name>_combat.glb; (1,0,0,0) = 180 deg about X for both today)
//   Hips position  p' = hipsCorrection * (p * hipsRatio)   hipsRatio = character hipsRestZ / clip hipsRestZ; then rootXZ: "inplace" holds the horizontal hips travel at frame 0 (killers), "keep" leaves the clip travel (victims
//                  that are flung or fall away), "limited" scales it to <= maxStepM; hipsYOffsetM lifts/lowers the hips (rig-local up axis) if a clip floats or sinks. Other bones: rotation only (translation tracks dropped, as in the bake).
// Steps per run (idempotent): 1) copy <sourceDir>/<file>.glb to <glbDir>/ when missing/changed and import it (glTFast, animationMethod = Mecanim if the importer exposes it);
//   2) per character, per clip whose `targets` include it: build <outClipDir>/<id>.anim with every curve path remapped (by bone leaf name) to the paths of the controller's reference clip, retarget the Hips, add/re-point the Base Layer
//   state named <id>; 3) log one line per clip incl. "unmatched N" (must be 0) and verify every pair in `pairs` has its killer state in the killer's controller and its victim state in the victim's.
// Controllers are backed up once to Tools/_backup_clips_finishersetup/. Nothing outside the data file's paths is touched.
public static class FinisherClipSetup
{
    [Serializable] class CharCfg { public string name, controller, referenceState, outClipDir; public float hipsRestZ = 1f; public float[] hipsCorrection; }
    [Serializable] class ClipCfg { public string id, file, role, rootXZ = "inplace"; public string[] targets; public float maxStepM, hipsRestZ = 1f, hipsYOffsetM, expectedDurationS; }
    [Serializable] class EntryCfg { public string id, attackerSide = "any", attackerClip, victimClip; public bool enabled; }
    [Serializable] class ImportCfg { public string sourceDir, glbDir; public CharCfg[] characters; public ClipCfg[] clips; }
    [Serializable] class Root { public ImportCfg clipImport; public EntryCfg[] entries; }
    // flattened view used by the code below (cfg.sourceDir ... cfg.pairs)
    class Cfg { public string sourceDir, glbDir; public CharCfg[] characters; public ClipCfg[] clips; public EntryCfg[] pairs; }

    const string DataPath = "Assets/Data/finishers.json";
    static int totalUnmatched, totalStates, totalClips;

    [MenuItem("Tools/Combat/Add Finisher Clips")]
    public static void Run() { RunInternal(); }

    public static void RunBatch()
    {
        bool ok = RunInternal();
        EditorApplication.Exit(ok ? 0 : 2);
    }

    static bool RunInternal()
    {
        totalUnmatched = 0; totalStates = 0; totalClips = 0;
        if (!File.Exists(DataPath)) { Debug.LogError("FinisherClipSetup: " + DataPath + " missing"); return false; }
        var root = JsonUtility.FromJson<Root>(File.ReadAllText(DataPath));
        if (root == null || root.clipImport == null) { Debug.LogError("FinisherClipSetup: no clipImport block in " + DataPath); return false; }
        var cfg = new Cfg { sourceDir = root.clipImport.sourceDir, glbDir = root.clipImport.glbDir, characters = root.clipImport.characters, clips = root.clipImport.clips, pairs = root.entries };
        if (cfg == null || cfg.clips == null || cfg.characters == null || cfg.clips.Length == 0) { Debug.LogError("FinisherClipSetup: no clips/characters in " + DataPath); return false; }

        // 1) copy + import the GLBs
        EnsureFolder(cfg.glbDir);
        int copied = 0, missingSrc = 0;
        for (int i = 0; i < cfg.clips.Length; i++)
        {
            var c = cfg.clips[i];
            string src = Path.Combine(cfg.sourceDir, c.file), dst = cfg.glbDir + "/" + c.file;
            if (!File.Exists(src)) { if (!File.Exists(dst)) { Debug.LogWarning("FinisherClipSetup: source GLB missing " + src); missingSrc++; } continue; }
            // FN3: the opentrident Mixamo-library GLBs list scene.nodes=[0] (the LeftToeBase leaf) instead of the Armature root, which makes glTFast throw
            // "Node at index 52 has no name" and import NO clip. FixSceneRoot rewrites the scene root while copying; content (not size) decides whether to rewrite dst.
            byte[] want = FixSceneRoot(File.ReadAllBytes(src), c.file);
            bool changed = !File.Exists(dst) || !BytesEqual(File.ReadAllBytes(dst), want);
            if (changed) { File.WriteAllBytes(dst, want); copied++; }
            AssetDatabase.ImportAsset(dst, changed ? ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport : ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(dst);
            if (imp != null)
            {
                var so = new SerializedObject(imp); var p = so.FindProperty("importSettings.animationMethod");   // glTFast: 0 none, 1 legacy, 2 mecanim (same as the character GLB metas)
                if (p != null && p.intValue != 2) { p.intValue = 2; so.ApplyModifiedPropertiesWithoutUndo(); imp.SaveAndReimport(); }
            }
        }
        Debug.Log("FinisherClipSetup: GLBs copied " + copied + ", source missing " + missingSrc + " (dir " + cfg.glbDir + ")");

        // 2) per character
        for (int i = 0; i < cfg.characters.Length; i++) SetupCharacter(cfg, cfg.characters[i]);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 3) verify the pairs
        int pairsOk = 0, pairsBad = 0;
        if (cfg.pairs != null)
            for (int i = 0; i < cfg.pairs.Length; i++)
            {
                if (PairOk(cfg, cfg.pairs[i])) pairsOk++; else pairsBad++;
            }
        bool ok = totalUnmatched == 0 && pairsBad == 0 && missingSrc == 0 && totalStates > 0;
        Debug.Log("FinisherClipSetup: done. clips built " + totalClips + ", states added/updated " + totalStates + ", unmatched " + totalUnmatched + (totalUnmatched == 0 ? " (ok)" : " (WARNING: unmatched bones will not animate)") + ", pairs ok " + pairsOk + "/" + (pairsOk + pairsBad) + (ok ? "  RESULT OK" : "  RESULT NOT OK"));
        return ok;
    }

    // ---------------------------------------------------------------- helpers
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

    // ---------------------------------------------------------------- FN3: glTF scene-root repair
    [Serializable] class GNode { public int[] children; }
    [Serializable] class GScene { public int[] nodes; }
    [Serializable] class GDoc { public GNode[] nodes; public GScene[] scenes; }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // Returns the GLB with scenes[0].nodes = the nodes that have no parent. Unchanged bytes when the file is already right or is not a plain GLB 2.
    static byte[] FixSceneRoot(byte[] glb, string name)
    {
        try
        {
            if (glb.Length < 28 || glb[0] != 0x67 || glb[1] != 0x6C || glb[2] != 0x54 || glb[3] != 0x46) return glb;      // "glTF"
            int jl = BitConverter.ToInt32(glb, 12);
            if (BitConverter.ToUInt32(glb, 16) != 0x4E4F534Au || jl <= 0 || 20 + jl > glb.Length) return glb;            // chunk type "JSON"
            string json = System.Text.Encoding.UTF8.GetString(glb, 20, jl);
            var doc = JsonUtility.FromJson<GDoc>(json);
            if (doc == null || doc.nodes == null || doc.scenes == null || doc.scenes.Length == 0 || doc.scenes[0].nodes == null) return glb;
            var isChild = new bool[doc.nodes.Length];
            for (int i = 0; i < doc.nodes.Length; i++)
                if (doc.nodes[i] != null && doc.nodes[i].children != null)
                    foreach (int c in doc.nodes[i].children) if (c >= 0 && c < isChild.Length) isChild[c] = true;
            var roots = new List<int>();
            for (int i = 0; i < isChild.Length; i++) if (!isChild[i]) roots.Add(i);
            var cur = doc.scenes[0].nodes;
            bool same = cur.Length == roots.Count;
            for (int i = 0; same && i < cur.Length; i++) if (cur[i] != roots[i]) same = false;
            if (same || roots.Count == 0) return glb;
            var rx = new System.Text.RegularExpressions.Regex(@"(""scenes""\s*:\s*\[\s*\{[^\]]*?""nodes""\s*:\s*)\[[^\]]*\]");
            string json2 = rx.Replace(json, "$1[" + string.Join(",", roots.ConvertAll(r => r.ToString()).ToArray()) + "]", 1);
            if (json2 == json) { Debug.LogWarning("FinisherClipSetup: " + name + ": scene root looks wrong but the JSON pattern was not found, left unchanged"); return glb; }
            byte[] jb = System.Text.Encoding.UTF8.GetBytes(json2);
            int padded = (jb.Length + 3) & ~3;
            int restStart = 20 + jl, restLen = glb.Length - restStart;
            var outp = new byte[20 + padded + restLen];
            Buffer.BlockCopy(glb, 0, outp, 0, 20);
            Buffer.BlockCopy(jb, 0, outp, 20, jb.Length);
            for (int i = jb.Length; i < padded; i++) outp[20 + i] = 0x20;
            Buffer.BlockCopy(glb, restStart, outp, 20 + padded, restLen);
            Buffer.BlockCopy(BitConverter.GetBytes(outp.Length), 0, outp, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(padded), 0, outp, 12, 4);
            Debug.Log("FinisherClipSetup: " + name + ": scene root fixed [" + string.Join(",", Array.ConvertAll(cur, x => x.ToString())) + "] -> [" + string.Join(",", roots.ConvertAll(r => r.ToString()).ToArray()) + "]");
            return outp;
        }
        catch (Exception ex) { Debug.LogWarning("FinisherClipSetup: FixSceneRoot " + name + " failed: " + ex.Message); return glb; }
    }

    static string Leaf(string path) { int k = path.LastIndexOf('/'); return k >= 0 ? path.Substring(k + 1) : path; }

    // bone key: letters and digits only, lower case ("mixamorig:Hips", "mixamorig_Hips", "mixamorigHips" all match)
    static string Key(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++) if (char.IsLetterOrDigit(s[i])) sb.Append(char.ToLowerInvariant(s[i]));
        return sb.ToString();
    }
    const string HipsKey = "mixamorighips";

    static bool Targets(ClipCfg c, string character)
    {
        if (c.targets == null) return false;
        for (int i = 0; i < c.targets.Length; i++) if (c.targets[i] == character) return true;
        return false;
    }

    static AnimationClip LoadSource(string glbPath, out string found)
    {
        found = "";
        AnimationClip best = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(glbPath))
        {
            var c = o as AnimationClip;
            if (c == null || c.name.StartsWith("__preview__")) continue;
            found += (found.Length > 0 ? ", " : "") + c.name;
            if (best == null) best = c;
        }
        return best;
    }

    static float SampleCurve(AnimationClip clip, EditorCurveBinding b, float t)
    {
        var cv = AnimationUtility.GetEditorCurve(clip, b);
        return cv != null ? cv.Evaluate(t) : 0f;
    }

    // ---------------------------------------------------------------- one character
    static void SetupCharacter(Cfg cfg, CharCfg ch)
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ch.controller);
        if (ctrl == null) { Debug.LogWarning("FinisherClipSetup: controller not found " + ch.controller); totalUnmatched++; return; }
        var baseSm = ctrl.layers[0].stateMachine;
        var refState = FindState(baseSm, ch.referenceState);
        var refClip = refState != null ? refState.motion as AnimationClip : null;
        if (refClip == null) { Debug.LogWarning("FinisherClipSetup: reference state '" + ch.referenceState + "' has no clip in " + ch.controller); totalUnmatched++; return; }

        // bone key -> full curve path used by the controller's own clips (+ the reference clip's hips position at t=0 for the frame sanity check)
        var map = new Dictionary<string, string>();
        EditorCurveBinding hipsRefZ = default(EditorCurveBinding); bool haveRefZ = false;
        foreach (var b in AnimationUtility.GetCurveBindings(refClip))
        {
            string p = b.path;
            while (!string.IsNullOrEmpty(p))
            {
                string k = Key(Leaf(p));
                if (!map.ContainsKey(k)) map[k] = p;
                int s = p.LastIndexOf('/');
                p = s > 0 ? p.Substring(0, s) : "";
            }
            if (b.type == typeof(Transform) && b.propertyName == "m_LocalPosition.z" && Key(Leaf(b.path)) == HipsKey) { hipsRefZ = b; haveRefZ = true; }
        }
        float refZ0 = haveRefZ ? SampleCurve(refClip, hipsRefZ, 0f) : float.NaN;

        try
        {
            string dir = "Tools/_backup_clips_finishersetup";
            Directory.CreateDirectory(dir);
            string dstB = Path.Combine(dir, Path.GetFileName(ch.controller));
            if (!File.Exists(dstB)) File.Copy(ch.controller, dstB);
        }
        catch (Exception ex) { Debug.LogWarning("FinisherClipSetup: backup failed " + ex.Message); }

        EnsureFolder(ch.outClipDir);
        Quaternion corr = Quaternion.identity;
        if (ch.hipsCorrection != null && ch.hipsCorrection.Length == 4) corr = new Quaternion(ch.hipsCorrection[0], ch.hipsCorrection[1], ch.hipsCorrection[2], ch.hipsCorrection[3]);

        int added = 0;
        for (int i = 0; i < cfg.clips.Length; i++)
        {
            var cc = cfg.clips[i];
            if (!Targets(cc, ch.name)) continue;
            string glb = cfg.glbDir + "/" + cc.file, found = "";
            var s = File.Exists(glb) ? LoadSource(glb, out found) : null;
            if (s == null && File.Exists(glb)) { AssetDatabase.ImportAsset(glb, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport); s = LoadSource(glb, out found); }   // FN3: one forced reimport before giving up
            if (s == null) { Debug.LogWarning("FinisherClipSetup: " + ch.name + " clip '" + cc.id + "': no AnimationClip in " + glb + " (found: " + found + ")"); totalUnmatched++; continue; }

            string apath = ch.outClipDir + "/" + cc.id + ".anim";
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
                if (!rot && !pos) { dropped++; continue; }                       // scale / euler / anything else
                string key = Key(Leaf(b.path)); bool isHips = key == HipsKey;
                if (pos && !isHips) { dropped++; continue; }                     // translation tracks other than the Hips are dropped (as in the attack bake)
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
            else { Debug.LogWarning("FinisherClipSetup: " + ch.name + " clip '" + cc.id + "' has no Hips curves"); miss++; }

            var set = AnimationUtility.GetAnimationClipSettings(s);
            set.loopTime = false; set.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(dstClip, set);
            if (isNew) AssetDatabase.CreateAsset(dstClip, apath); else EditorUtility.SetDirty(dstClip);

            var state = FindState(baseSm, cc.id);
            if (state == null) state = baseSm.AddState(cc.id);
            state.motion = dstClip; state.writeDefaultValues = true; state.speed = 1f;
            added++; totalClips++; totalStates++; totalUnmatched += miss;
            string frameCheck = float.IsNaN(refZ0) || float.IsNaN(z0Clip) ? "" : ", hips up t0 ref " + refZ0.ToString("0.000") + " vs clip " + z0Clip.ToString("0.000") + (Mathf.Abs(ch.hipsRestZ - z0Clip) > 0.2f ? "  (WARNING: clip start hips height differs from the character's standing hipsRestZ " + ch.hipsRestZ.ToString("0.000") + ", check hipsCorrection/hipsRatio/hipsYOffsetM)" : "  (clip start = standing rest height; the reference is the lower guard_idle crouch, so ref<clip is expected)");
            Debug.Log("FinisherClipSetup: " + ch.name + " state '" + cc.id + "' len " + dstClip.length.ToString("0.000") + " s (expected " + cc.expectedDurationS.ToString("0.000") + "), curves remapped " + hit + ", dropped " + dropped + ", unmatched " + miss + (miss > 0 ? "  (WARNING: unmatched bones will not animate: " + string.Join(", ", missNames.ToArray()) + ")" : "") + ", hipsRatio " + ratio.ToString("0.000") + ", rootXZ " + cc.rootXZ + frameCheck);
            if (cc.expectedDurationS > 0f && Mathf.Abs(dstClip.length - cc.expectedDurationS) > 0.06f) Debug.LogWarning("FinisherClipSetup: " + cc.id + " length " + dstClip.length.ToString("0.000") + " differs from expected " + cc.expectedDurationS.ToString("0.000"));
        }
        EditorUtility.SetDirty(ctrl);
        Debug.Log("FinisherClipSetup: " + ch.controller + " +" + added + " finisher states");
    }

    // Retargets and writes the Hips curves. Key times come from the x curve of each group; the other components are evaluated at those times.
    static float WriteHips(AnimationClip dst, string path, Dictionary<string, AnimationCurve> rot, Dictionary<string, AnimationCurve> pos, Quaternion corr, float ratio, ClipCfg cc)
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
                var q = new Quaternion(rx.Evaluate(t), ry.Evaluate(t), rz.Evaluate(t), rw.Evaluate(t));
                q = corr * q;
                float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w); if (n > 1e-6f) q = new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
                if (!first && (prev.x * q.x + prev.y * q.y + prev.z * q.z + prev.w * q.w) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);   // keep the hemisphere so the linear blend takes the short way
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
            // rig-local axes after the correction: x,y = horizontal, z = up (the Hips of the combat GLBs hang 1.0 up their local z)
            float h0x = v[0].x, h0y = v[0].y, travel = 0f;
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

    // ---------------------------------------------------------------- pair check
    static bool HasState(Cfg cfg, string character, string state)
    {
        CharCfg ch = null;
        for (int i = 0; i < cfg.characters.Length; i++) if (cfg.characters[i].name == character) ch = cfg.characters[i];
        if (ch == null) return false;
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ch.controller);
        if (ctrl == null) return false;
        return FindState(ctrl.layers[0].stateMachine, state) != null;
    }

    static bool PairOk(Cfg cfg, EntryCfg p)
    {
        if (string.IsNullOrEmpty(p.attackerClip)) { Debug.Log("FinisherClipSetup: entry '" + p.id + "' has no attackerClip (plain swing), skipped"); return true; }
        var killers = new List<string>(); if (p.attackerSide == "any" || string.IsNullOrEmpty(p.attackerSide)) { killers.Add("Dracula"); killers.Add("Pitborn"); } else killers.Add(p.attackerSide);
        bool ok = true; string msg = "";
        foreach (var k in killers)
        {
            string v = k == "Dracula" ? "Pitborn" : "Dracula";
            if (!HasState(cfg, k, p.attackerClip)) { ok = false; msg += " killer state '" + p.attackerClip + "' missing on " + k + ";"; }
            if (!string.IsNullOrEmpty(p.victimClip) && !HasState(cfg, v, p.victimClip)) { ok = false; msg += " victim state '" + p.victimClip + "' missing on " + v + ";"; }
        }
        Debug.Log("FinisherClipSetup: entry '" + p.id + "' (" + p.attackerSide + "): " + p.attackerClip + " / " + p.victimClip + (ok ? " OK" : " MISSING:" + msg));
        return ok;
    }
}
