using System;
using System.Collections.Generic;
using UnityEngine;

// FK16 (unity-dev): the rider pose. While Joan is mounted her locomotion clips (idle / walk / run) are replaced by the baked rider clips (Editor RiderClipSetup: rider_idle, rider_gallop states in Joan.controller)
// through an AnimatorOverrideController (the TwoHandClips method); dismounting puts the controller back. Data: mount.json "rider": { enabled, clips: [ { from = controller clip name, state = baked clip } ] }.
// No block / enabled false / a missing clip = nothing changes. The controller in use when mounting is restored only if it is still ours when she dismounts.
public static class RiderPose
{
    [Serializable] public class Pair { public string from = "", state = ""; }
    [Serializable] public class Shield { public bool enabled = false; public string socket = "ShieldSocket_L", bone = "mixamorig:Spine2"; public float rx = 0f, ry = 0f, rz = 0f, px = 0f, py = 0.05f, pz = -0.22f, scale = 1f; }   // DATA: the shield rides on her back while mounted
    [Serializable] public class Cfg { public bool enabled = false, log = true; public Pair[] clips = new Pair[0]; public Shield shield = new Shield(); }
    [Serializable] class Doc { public Cfg rider = new Cfg(); }
    static Cfg cfg; static RuntimeAnimatorController before; static AnimatorOverrideController mine;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { cfg = null; before = null; mine = null; }
    static Cfg Get()
    {
        if (cfg != null) return cfg; cfg = new Cfg();
        try { string t = DataPath.ReadText("mount.json"); if (!string.IsNullOrEmpty(t)) { var d = JsonUtility.FromJson<Doc>(t.TrimStart('﻿')); if (d != null && d.rider != null) { cfg = d.rider; if (cfg.clips == null) cfg.clips = new Pair[0]; if (cfg.shield == null) cfg.shield = new Shield(); } } }
        catch (Exception e) { Debug.LogWarning("[Rider] mount.json rider block read failed: " + e.Message); }
        return cfg;
    }
    static Animator AnimOf(DuelFighter f) { var fx = f != null ? f.GetComponent<Fighter>() : null; return fx != null ? fx.anim : null; }
    public static void Apply(DuelFighter f)
    {
        var c = Get(); var anim = AnimOf(f); if (!c.enabled || anim == null || anim.runtimeAnimatorController == null) return;
        var cur = anim.runtimeAnimatorController; var ov = cur as AnimatorOverrideController; var baseC = ov != null ? ov.runtimeAnimatorController : cur;
        var byName = new Dictionary<string, AnimationClip>(); foreach (var k in baseC.animationClips) if (k != null && !byName.ContainsKey(k.name)) byName[k.name] = k;
        var oc = new AnimatorOverrideController(baseC); oc.name = baseC.name;
        var list = new List<KeyValuePair<AnimationClip, AnimationClip>>(); oc.GetOverrides(list);
        if (ov != null) { var prev = new List<KeyValuePair<AnimationClip, AnimationClip>>(); ov.GetOverrides(prev); for (int i = 0; i < list.Count; i++) for (int j = 0; j < prev.Count; j++) if (prev[j].Key == list[i].Key && prev[j].Value != null) list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, prev[j].Value); }   // keep a weapon's two-hand override underneath
        int n = 0, miss = 0;
        foreach (var p in c.clips)
        {
            if (p == null) continue; AnimationClip to; if (!byName.TryGetValue(p.state, out to)) { miss++; continue; }
            for (int i = 0; i < list.Count; i++) if (list[i].Key != null && list[i].Key.name == p.from) { list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, to); n++; }
        }
        if (n == 0) { if (c.log) Debug.LogWarning("[Rider] no clip replaced (missing " + miss + "): the rider clips are not in the controller (Editor RiderClipSetup not run?), old pose stays"); return; }
        oc.ApplyOverrides(list); before = cur; mine = oc; anim.runtimeAnimatorController = oc;
        if (c.log) Debug.Log("[Rider] Joan mounted: " + n + " clip(s) replaced" + (miss > 0 ? ", " + miss + " missing" : ""));
    }
    // SHIELD: while mounted the shield socket (ShieldSocket_L) is re-parented to a back bone with the mount.json rider.shield row; dismounting puts it back exactly where it was
    static Transform shieldT, shieldParent; static Vector3 shieldPos, shieldScale; static Quaternion shieldRot;
    static Transform Find(DuelFighter f, string name) { foreach (var t in f.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t; return null; }
    public static void ShieldOn(DuelFighter f)
    {
        var S = Get().shield; if (!Get().enabled || S == null || !S.enabled || f == null || shieldT != null) return;
        var sock = Find(f, S.socket); var bone = Find(f, S.bone); if (sock == null || bone == null) { Debug.LogWarning("[Rider] shield carry: " + (sock == null ? "socket '" + S.socket + "'" : "bone '" + S.bone + "'") + " not found, the shield stays on the arm"); return; }
        shieldT = sock; shieldParent = sock.parent; shieldPos = sock.localPosition; shieldRot = sock.localRotation; shieldScale = sock.localScale;
        sock.SetParent(bone, false); sock.localRotation = Quaternion.Euler(S.rx, S.ry, S.rz); sock.localPosition = new Vector3(S.px, S.py, S.pz); sock.localScale = shieldScale * (S.scale > 0.01f ? S.scale : 1f);
        if (Get().log) Debug.Log("[Rider] shield carried on " + S.bone);
    }
    public static void ShieldOff()
    {
        if (shieldT == null) return;
        if (shieldParent != null) { shieldT.SetParent(shieldParent, false); shieldT.localPosition = shieldPos; shieldT.localRotation = shieldRot; shieldT.localScale = shieldScale; }
        shieldT = null; shieldParent = null;
    }
    public static void Restore(DuelFighter f)
    {
        ShieldOff();
        var anim = AnimOf(f); if (anim == null || mine == null) return;
        if (anim.runtimeAnimatorController == mine && before != null) anim.runtimeAnimatorController = before;
        if (Get().log) Debug.Log("[Rider] Joan dismounted: original controller back");
        mine = null; before = null;
    }
}
