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
    [Serializable] public class Cfg { public bool enabled = false, log = true; public bool riderClipsRuntime = false; /* DATA: true = the rider clips are built at runtime from StreamingAssets\Rider\Joan_rider_clips.glb (RiderClipRuntime); false = the baked controller clips */ public Pair[] clips = new Pair[0]; public Shield shield = new Shield(); }
    [Serializable] class Doc { public Cfg rider = new Cfg(); }
    static Cfg cfg; static RuntimeAnimatorController before; static AnimatorOverrideController mine;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { cfg = null; before = null; mine = null; rt = null; rtTried = false; }
    static Cfg Get()
    {
        if (cfg != null) return cfg; cfg = new Cfg();
        try { string t = DataPath.ReadText("mount.json"); if (!string.IsNullOrEmpty(t)) { var d = JsonUtility.FromJson<Doc>(t.TrimStart('﻿')); if (d != null && d.rider != null) { cfg = d.rider; if (cfg.clips == null) cfg.clips = new Pair[0]; if (cfg.shield == null) cfg.shield = new Shield(); } } }
        catch (Exception e) { Debug.LogWarning("[Rider] mount.json rider block read failed: " + e.Message); }
        return cfg;
    }
    static Animator AnimOf(DuelFighter f) { var fx = f != null ? f.GetComponent<Fighter>() : null; return fx != null ? fx.anim : null; }
    /// <summary>ARMDIAG (QA, mount.json debugLog true): what the Animator is playing while mounted: per-layer weight and the clip names with their weights on layer 0, so a raised arm can be traced to a layer / state (Unity-side override) or to the rider clip itself.</summary>
    public static void LogAnim(DuelFighter f)
    {
        var anim = AnimOf(f); if (anim == null) return; var sb = new System.Text.StringBuilder("[RiderDiag] t=" + Time.time.ToString("0.0") + " layers:");
        for (int i = 0; i < anim.layerCount; i++) sb.Append(" " + anim.GetLayerName(i) + "=" + anim.GetLayerWeight(i).ToString("0.00"));
        sb.Append(" | L0 clips:"); foreach (var ci in anim.GetCurrentAnimatorClipInfo(0)) sb.Append(" " + (ci.clip != null ? ci.clip.name : "?") + "=" + ci.weight.ToString("0.00"));
        var nx = anim.GetNextAnimatorClipInfo(0); if (nx.Length > 0) { sb.Append(" next:"); foreach (var ci in nx) sb.Append(" " + (ci.clip != null ? ci.clip.name : "?") + "=" + ci.weight.ToString("0.00")); }
        sb.Append(" speed=" + anim.speed.ToString("0.00")); Debug.Log(sb.ToString());
    }
    static Dictionary<string, AnimationClip> rt; static Animator rtAnim; static bool rtTried;
    /// <summary>RUNTIMECLIPS: the rider clips built from StreamingAssets\Rider\Joan_rider_clips.glb (own glTF reader, no Unity asset); null when the file is absent.</summary>
    static AnimationClip RuntimeClip(Animator anim, string name)
    {
        if (anim == null) return null;
        if (!rtTried || rtAnim != anim)
        {
            rtTried = true; rtAnim = anim; rt = null; string rep = "no file";
            foreach (var pth in DataPath.Candidates("Rider/Joan_rider_clips.glb")) if (System.IO.File.Exists(pth)) { rt = RiderClipRuntime.Load(pth, anim.transform, out rep); break; }
            if (Get().log) Debug.Log("[Rider] runtime clips: " + (rt != null ? rep : "none (" + rep + "), the controller / Resources clips are used"));
        }
        AnimationClip c; return rt != null && rt.TryGetValue(name, out c) ? c : null;
    }
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
            if (p == null) continue; AnimationClip to = c.riderClipsRuntime ? RuntimeClip(anim, p.state) : null; if (to == null && !byName.TryGetValue(p.state, out to)) { miss++; continue; }   // RUNTIMECLIPS: with the data switch riderClipsRuntime (default OFF) the clip comes from StreamingAssets\Rider\Joan_rider_clips.glb (own glTF reader, plays through the same Animator override); OFF or no file = the baked controller clip as before   // RUNTIMECLIPS: StreamingAssets\Rider\Joan_rider_clips.glb read at runtime first (a pose change = a file copy), then Resources, then the controller state   // RESOURCESBAKE: Resources first (v3c), the controller state is only the fallback
            for (int i = 0; i < list.Count; i++) if (list[i].Key != null && list[i].Key.name == p.from) { list[i] = new KeyValuePair<AnimationClip, AnimationClip>(list[i].Key, to); n++; }
        }
        if (n == 0) { if (c.log) Debug.LogWarning("[Rider] no clip replaced (missing " + miss + "): the rider clips are not in the controller (Editor RiderClipSetup not run?), old pose stays"); return; }
        oc.ApplyOverrides(list); before = cur; mine = oc; anim.runtimeAnimatorController = oc;
        if (c.log) Debug.Log("[Rider] Joan mounted: " + n + " clip(s) replaced" + (miss > 0 ? ", " + miss + " missing" : ""));
    }
    // SHIELD: while mounted the shield socket (ShieldSocket_L) is re-parented to a back bone with the mount.json rider.shield row; dismounting puts it back exactly where it was
    static bool shieldWarned; static Transform shieldT, shieldParent; static Vector3 carryPos; static Quaternion carryRot = Quaternion.identity; static float carryScale = 1f;
    /// <summary>SHIELDPOSE: while the shield socket is carried on a rider bone, ShieldMount keeps this pose (mount.json rider.shield px/py/pz, rx/ry/rz, scale) instead of overwriting it with the hand pose every frame.</summary>
    public static bool Carrying(Transform t) { return shieldT != null && shieldT == t; }
    public static Vector3 CarryPos { get { return carryPos; } } public static Quaternion CarryRot { get { return carryRot; } } public static float CarryScale { get { return carryScale; } } static Vector3 shieldPos, shieldScale; static Quaternion shieldRot;
    static Transform Find(DuelFighter f, string name) { foreach (var t in f.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t; return null; }
    public static void ShieldOn(DuelFighter f)
    {
        var S = Get().shield; if (!Get().enabled || S == null || !S.enabled || f == null || shieldT != null) return;   // SHIELDRETRY: called again from MountRider every 0.5 s while mounted (the shield glb can finish loading after the summon), the not-found warning prints once per mount
        var sock = Find(f, S.socket); var bone = Find(f, S.bone); if (sock == null || bone == null) { if (!shieldWarned) { shieldWarned = true; Debug.LogWarning("[Rider] shield carry: " + (sock == null ? "socket '" + S.socket + "'" : "bone '" + S.bone + "'") + " not found (yet), retrying every 0.5 s while mounted"); } return; }
        shieldT = sock; shieldParent = sock.parent; shieldPos = sock.localPosition; shieldRot = sock.localRotation; shieldScale = sock.localScale;
        carryPos = new Vector3(S.px, S.py, S.pz); carryRot = Quaternion.Euler(S.rx, S.ry, S.rz); carryScale = S.scale > 0.01f ? S.scale : 1f; sock.SetParent(bone, false); sock.localRotation = Quaternion.Euler(S.rx, S.ry, S.rz); sock.localPosition = new Vector3(S.px, S.py, S.pz); sock.localScale = shieldScale * (S.scale > 0.01f ? S.scale : 1f);
        if (Get().log) Debug.Log("[Rider] shield carried on " + S.bone);
    }
    public static void ShieldOff()
    {
        shieldWarned = false; if (shieldT == null) return;
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
