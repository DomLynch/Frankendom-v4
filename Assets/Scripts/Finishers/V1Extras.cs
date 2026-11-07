using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// V1CLIPS v98: the three V1 finisher pieces laneV1FIN did not port (its MANIFEST "Not ported"), data driven (finishers.json), roster ids only, no character names:
//   BladeAim       = V1 characters.ts aimBladeAt() (scene.ts:882-883): during run through the middle of the killer's blade is put THROUGH the victim's chest, not the shoulder->tip ray. The sword arm is turned about
//                    the shoulder (and, with stepMaxM > 0, the killer is slid along the lane first: V1's "grounded presentation step" for reach), the off hand is re-solved onto the hilt a hand's width behind the
//                    sword hand (two-bone reach, the clip's own elbow bend). World space, after the Animator / WeaponGrip, so no bone or weapon stretches.
//   DroppedWeapon  = V1 opened.ts weapon group: the victim's weapon is released to the sand on the waist cut (slides .25 sideways / .35 back x scale, rolls flat over smooth(.12,.62) of the 3.2 s clock).
//   OpenedLegs     = V1 opened.ts lower group: the legs slide -.35 / -.12 x scale and fall over smooth(.36,.84). Only when the V3 physics ragdoll is NOT taking the hips + legs (FinPhys.Off or row legs.onlyWithoutFinPhys false),
//                    because FINPHYS v97 already lets hips + legs fall as the ragdoll.
//   V1Rematch      = CrownRig.RestoreAll() on every scene load (Retry / Next / STSteer.RestartFight all reload the scene) so a restored head bone, the halves, the dropped weapon and the scripted pieces never outlive a fight.
// All timing uses FinisherTime.Step (real time, clamped), never Time.timeScale.

[Serializable]
public class OffHandCfg
{
    public bool enabled = true;
    public string[] bones = { "LeftArm", "LeftForeArm", "LeftHand" };   // bone-name suffixes: upper arm, lower arm, hand of the off hand
    public float pommelM = 0.07f;                                          // goal = sword hand + this far towards the pommel (V1: .07)
}

[Serializable]
public class BladeAimCfg
{
    public bool enabled = false;
    public string armBone = "RightArm", handBone = "RightHand";            // bone-name suffixes (the sword arm's upper arm; the hand holding the weapon)
    public string[] weaponSockets = { "WeaponSocket_R" };                  // exact transform names under the hand; the first MeshFilter below is the blade
    public string victimBone = "Spine1";                                   // V1 spine_02
    public float segFrom = 0.24f, segTo = 0.85f;                           // V1 segment [.24,.85] along the blade, here as fractions hilt (0) -> tip (1); the middle goes through the chest
    public string startRel = "contact";                                    // "contact" | "start": what startS is measured from
    public float startS = -0.35f, rampS = 0.25f, holdS = 2.6f;             // aim ramps 0 -> 1 over rampS from (rel + startS), holds holdS after the ramp, then lets go (V1: min(1, finishClock / .25))
    public float stepMaxM = 0f;                                            // > 0: the killer is slid along the lane by up to this to give the arm reach (V1 anchor step); 0 = off (pairOffset gives the reach)
    public OffHandCfg offHand = new OffHandCfg();
    public ClawCfg claw;                                                   // SOBEKCLAWS: used instead of a weapon mesh while the killer is unarmed (WeaponSwap.Unarmed); null = never
}

// SOBEKCLAWS: the "blade" of an unarmed killer = the hand bone -> the claw tip (tipM, metres in the hand frame like weapon.tipOffsetM). segFrom / segTo / stepMaxM replace the entry's while the claws aim; no off hand re-solve.
[Serializable]
public class ClawCfg
{
    public bool enabled = false;
    public float[] tipM = new float[0];
    public float segFrom = 0.6f, segTo = 1f, stepMaxM = 0f;
    public string note = "";
}

[Serializable]
public class DropCfg
{
    public bool enabled = false;
    public string[] sockets = { "WeaponSocket_R", "WeaponSocket_L" };      // exact transform names on the victim; the first child of the first socket that has one is the weapon
    public float sideM = 0.25f, alongM = -0.35f;                           // opened.ts weapon.position x .25, z -.35 (x scale)
    public float startS = 0.384f, endS = 1.984f;                           // smooth(.12,.62) x 3.2 s
    public float sideSign = 1f, floorM = 0.008f;
}

[Serializable]
public class LegsCfg
{
    public bool enabled = false;
    public bool onlyWithoutFinPhys = true;                                 // true: skipped while FinPhys.On (the ragdoll already drops hips + legs)
    public string hipsBone = "Hips";
    public float sideM = -0.35f, alongM = -0.12f, fallDeg = 90f, sideSign = 1f, hipsRestM = 0.12f;   // opened.ts lower.position (-.35, -.12) x scale, legFall Rz(-90 deg); hips settle this high above the sand
    public float startS = 1.152f, endS = 2.688f;                           // smooth(.36,.84) x 3.2 s
}

public static class V1Extras
{
    public static Transform FindExact(Transform root, string name)
    {
        if (root == null || string.IsNullOrEmpty(name)) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }
    public static Transform FindSuffix(Transform root, string suffix)
    {
        if (root == null || string.IsNullOrEmpty(suffix)) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
    public static float Smooth(float t, float a, float b) { float x = Mathf.Clamp01((t - a) / Mathf.Max(1e-4f, b - a)); return x * x * (3f - 2f * x); }

    /// <summary>Waist cut applied (FinisherPlayer.Gore): the dropped weapon and the leg topple of V1 opened.ts.</summary>
    public static void OnCut(DuelFighter victim, DuelFighter attacker, CutUnit unit, Vector3 away)
    {
        if (victim == null || unit == null || unit.script == null || !unit.script.enabled) return;
        var v1 = FinisherConfig.Get().rt.v1; if (v1 == null || !v1.enabled || !v1.openedExtras) return;
        away.y = 0f; if (away.sqrMagnitude < 1e-4f) away = Vector3.forward; away.Normalize();
        if (unit.script.weapon != null && unit.script.weapon.enabled) DroppedWeapon.Begin(victim.transform, unit.script, away);
        if (unit.script.legs != null && unit.script.legs.enabled && !(unit.script.legs.onlyWithoutFinPhys && FinPhysOn())) OpenedLegs.Begin(victim, unit.script, away);
    }

    public static readonly List<GameObject> Spawned = new List<GameObject>();   // detached victim weapons (the scene reload would remove them too; a non-reload reset needs this)
    static System.Reflection.PropertyInfo finPhysOn; static bool finPhysLooked;
    /// <summary>FINPHYS v97 (V3 ragdoll) is optional in this tree: asked by reflection so this file compiles with or without Scripts\Physics\FinPhys.cs.</summary>
    public static bool FinPhysOn()
    {
        if (!finPhysLooked) { finPhysLooked = true; var t = Type.GetType("FinPhys"); if (t != null) finPhysOn = t.GetProperty("On", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); }
        try { return finPhysOn != null && (bool)finPhysOn.GetValue(null, null); } catch (Exception) { return false; }
    }

    /// <summary>Rematch: everything above goes away. CrownRig.RestoreAll does the crown halves + FinPhys.Spawned; this adds the scripted pieces.</summary>
    public static void RestoreAll()
    {
        BladeAim.EndAll();
        foreach (var l in new List<OpenedLegs>(OpenedLegs.All)) if (l != null) UnityEngine.Object.Destroy(l);
        OpenedLegs.All.Clear();
        for (int i = Spawned.Count - 1; i >= 0; i--) if (Spawned[i] != null) UnityEngine.Object.Destroy(Spawned[i]);
        Spawned.Clear();
        CrownRig.RestoreAll();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Hook() { SceneManager.sceneLoaded -= OnLoaded; SceneManager.sceneLoaded += OnLoaded; }
    static void OnLoaded(Scene s, LoadSceneMode m) { try { RestoreAll(); } catch (Exception ex) { Debug.LogWarning("[finisher] V1 rematch restore: " + ex.Message); } }
}

/// <summary>Idempotent post-Animator edit of one bone: if the Animator did not rewrite the bone since our last write (Animator disabled / holding), the pose is put back before the next edit, so edits never accumulate.</summary>
sealed class PoseGuard
{
    readonly Transform t; Quaternion baseR, wroteR; Vector3 baseP, wroteP; bool wrote, marked;
    public PoseGuard(Transform bone) { t = bone; }
    public void Restore() { if (wrote && t != null && t.localRotation == wroteR && t.localPosition == wroteP) { t.localRotation = baseR; t.localPosition = baseP; } wrote = false; marked = false; }
    public void Mark() { if (t == null || marked) return; baseR = t.localRotation; baseP = t.localPosition; marked = true; }
    public void Done() { if (t == null) return; wroteR = t.localRotation; wroteP = t.localPosition; wrote = true; }
}

// ------------------------------------------------------------------------------------------------ run through: the blade goes through the chest
[DefaultExecutionOrder(900)]   // after the Animator, WeaponGrip, GripPose, HitReact
public sealed class BladeAim : MonoBehaviour
{
    static readonly List<BladeAim> all = new List<BladeAim>();
    public static void EndAll() { for (int i = all.Count - 1; i >= 0; i--) if (all[i] != null) Destroy(all[i]); all.Clear(); }

    BladeAimCfg c; DuelFighter victim; Transform arm, hand, vBone, o0, o1, o2, blade; Vector3 hiltL, tipL; bool resolved, failed, hiltIsA; float age, from, end; float segMid, stepMax; Vector3 applied; CharacterController cc; bool logged; PoseGuard gArm, g0, g1;

    public static BladeAim Begin(DuelFighter killer, DuelFighter victim, BladeAimCfg cfg, float contactPlanned)
    {
        if (killer == null || victim == null || cfg == null || !cfg.enabled) return null;
        var b = killer.gameObject.AddComponent<BladeAim>(); b.c = cfg; b.victim = victim; b.cc = killer.GetComponent<CharacterController>();
        b.from = (cfg.startRel == "contact" ? contactPlanned : 0f) + cfg.startS; b.end = b.from + Mathf.Max(1e-3f, cfg.rampS) + Mathf.Max(0f, cfg.holdS);
        all.Add(b); return b;
    }

    void OnDestroy() { all.Remove(this); if (applied != Vector3.zero) Shift(-applied); applied = Vector3.zero; }

    void Shift(Vector3 d)
    {
        bool had = cc != null && cc.enabled; if (had) cc.enabled = false;
        transform.position += d;
        if (had) cc.enabled = true;
    }

    bool Resolve()
    {
        segMid = 0.5f * (c.segFrom + c.segTo); stepMax = c.stepMaxM;   // SOBEKCLAWS
        arm = V1Extras.FindSuffix(transform, c.armBone); hand = V1Extras.FindSuffix(transform, c.handBone); vBone = V1Extras.FindSuffix(victim.transform, c.victimBone);
        if (arm == null || hand == null || vBone == null) { Debug.LogWarning("[finisher] bladeAim: bone not found (arm " + (arm != null) + ", hand " + (hand != null) + ", victim bone " + (vBone != null) + ")"); return false; }
        var cl = c.claw; var wsw = GetComponent<WeaponSwap>();   // SOBEKCLAWS: an unarmed killer aims with the claws (the template weapon on the socket is hidden but its mesh is still there, so this check comes first)
        if (cl != null && cl.enabled && cl.tipM != null && cl.tipM.Length >= 3 && wsw != null && wsw.Unarmed)
        {
            blade = hand; float hs = Mathf.Max(0.0001f, hand.lossyScale.x); hiltL = Vector3.zero; tipL = new Vector3(cl.tipM[0], cl.tipM[1], cl.tipM[2]) / hs; hiltIsA = true;
            segMid = 0.5f * (cl.segFrom + cl.segTo); stepMax = cl.stepMaxM;
            return true;   // no off hand: one claw goes in
        }
        MeshFilter mf = null;
        foreach (var sn in c.weaponSockets) { var s = V1Extras.FindExact(transform, sn); if (s != null) { mf = s.GetComponentInChildren<MeshFilter>(true); if (mf != null && mf.sharedMesh != null) break; mf = null; } }
        if (mf == null) { Debug.LogWarning("[finisher] bladeAim: no weapon mesh under " + string.Join("/", c.weaponSockets)); return false; }
        blade = mf.transform; var bb = mf.sharedMesh.bounds; Vector3 sz = bb.size; int ax = sz.x >= sz.y && sz.x >= sz.z ? 0 : (sz.y >= sz.z ? 1 : 2);
        Vector3 a = Vector3.zero; a[ax] = 0.5f * sz[ax]; Vector3 pa = bb.center - a, pb = bb.center + a;   // the two ends of the long axis, mesh space
        hiltIsA = (blade.TransformPoint(pa) - hand.position).sqrMagnitude <= (blade.TransformPoint(pb) - hand.position).sqrMagnitude;   // the end nearest the hand is the hilt
        hiltL = hiltIsA ? pa : pb; tipL = hiltIsA ? pb : pa;
        if (c.offHand != null && c.offHand.enabled && c.offHand.bones != null && c.offHand.bones.Length >= 3) { o0 = V1Extras.FindSuffix(transform, c.offHand.bones[0]); o1 = V1Extras.FindSuffix(transform, c.offHand.bones[1]); o2 = V1Extras.FindSuffix(transform, c.offHand.bones[2]); }
        return true;
    }
    Vector3 Hilt() { return blade.TransformPoint(hiltL); }
    Vector3 Tip() { return blade.TransformPoint(tipL); }
    Vector3 BladePoint(float f) { return Vector3.Lerp(Hilt(), Tip(), f); }

    void LateUpdate()
    {
        age += FinisherTime.Step(Time.unscaledDeltaTime);
        if (gArm != null) gArm.Restore(); if (g0 != null) g0.Restore(); if (g1 != null) g1.Restore();
        if (applied != Vector3.zero) { Shift(-applied); applied = Vector3.zero; }   // the Animator pose is re-evaluated each frame, the lane shift is re-applied from the unshifted root
        if (failed || age < from || age > end) return;
        if (!resolved) { resolved = true; if (!Resolve()) { failed = true; return; } gArm = new PoseGuard(arm); g0 = o0 != null ? new PoseGuard(o0) : null; g1 = o1 != null ? new PoseGuard(o1) : null; }
        if (victim == null || vBone == null || blade == null) return;
        float a = Mathf.Clamp01((age - from) / Mathf.Max(1e-3f, c.rampS)); if (age > from + c.rampS) a = 1f;
        Vector3 target = vBone.position, shoulder = arm.position, embedded = BladePoint(segMid);
        if (stepMax > 0f)
        {
            Vector3 fwd = target - shoulder; fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-6f)
            {
                fwd.Normalize(); Vector3 to0 = target - shoulder, from0 = embedded - shoulder;
                float b = Vector3.Dot(to0, fwd), disc = b * b - (to0.sqrMagnitude - from0.sqrMagnitude);   // solve |to - step * fwd| = |from| (V1 aimBladeAt)
                if (disc >= 0f) { float step = Mathf.Clamp(b - Mathf.Sqrt(disc), -stepMax, stepMax) * a; applied = fwd * step; Shift(applied); shoulder = arm.position; embedded = BladePoint(segMid); }
            }
        }
        Vector3 v0 = embedded - shoulder, v1 = target - shoulder;
        if (v0.sqrMagnitude > 1e-8f && v1.sqrMagnitude > 1e-8f)
        { gArm.Mark(); arm.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(v0, v1), a) * arm.rotation; gArm.Done(); }
        if (!logged) { logged = true; Debug.Log("[finisher] bladeAim on: blade middle -> victim " + c.victimBone + ", reach step " + applied.magnitude.ToString("0.000") + " m, off hand " + (o2 != null)); }
        SolveOffHand(a);
    }

    // V1: re-solve the off arm onto the grip a hand's width behind the sword hand, keeping the clip's own elbow bend (two-bone reach).
    void SolveOffHand(float a)
    {
        if (o0 == null || o1 == null || o2 == null || a <= 0f) return;
        Vector3 s = o0.position, e = o1.position, h = o2.position, pommelward = (Hilt() - Tip()).normalized;
        Vector3 goal = hand.position + pommelward * c.offHand.pommelM, dir = goal - s;
        float a2 = (e - s).magnitude, b2 = (h - e).magnitude, d = Mathf.Max(0.03f, Mathf.Min(dir.magnitude, a2 + b2 - 0.001f)); dir.Normalize();
        float along = (a2 * a2 - b2 * b2 + d * d) / (2f * d); Vector3 bend = e - s; bend -= dir * Vector3.Dot(bend, dir); if (bend.sqrMagnitude < 1e-6f) return; bend.Normalize();
        Vector3 elbow = s + dir * along + bend * Mathf.Sqrt(Mathf.Max(0f, a2 * a2 - along * along));
        if (g0 != null) g0.Mark(); if (g1 != null) g1.Mark();
        Aim(o0, o1, elbow, a); Aim(o1, o2, goal, a);
        if (g0 != null) g0.Done(); if (g1 != null) g1.Done();
    }
    static void Aim(Transform bone, Transform child, Vector3 dest, float a)
    {
        Vector3 cur = child.position - bone.position, want = dest - bone.position; if (cur.sqrMagnitude < 1e-8f || want.sqrMagnitude < 1e-8f) return;
        bone.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, want), a) * bone.rotation;
    }
}

// ------------------------------------------------------------------------------------------------ opened: the victim's weapon falls to the sand
public sealed class DroppedWeapon : MonoBehaviour
{
    DropCfg d; Vector3 p0, away, right; Quaternion r0, rRest; float age, scale = 1f, gy0; bool logged; Transform vict;

    public static void Begin(Transform victim, ScriptCfg s, Vector3 away)
    {
        var d = s.weapon; Transform w = null;
        foreach (var sn in d.sockets) { var sock = V1Extras.FindExact(victim, sn); if (sock != null && sock.childCount > 0 && sock.GetChild(0).GetComponentInChildren<Renderer>(true) != null) { w = sock.GetChild(0); break; } }
        if (w == null) { Debug.Log("[finisher] opened: no victim weapon under " + string.Join("/", d.sockets) + " (nothing to drop)"); return; }
        w.SetParent(null, true);
        var h = w.gameObject.AddComponent<DroppedWeapon>(); h.Init(d, away, victim);
        V1Extras.Spawned.Add(w.gameObject);   // removed on a rematch (V1Extras.RestoreAll)
    }

    void Init(DropCfg cfg, Vector3 awayDir, Transform victim)
    {
        d = cfg; vict = victim; p0 = transform.position; r0 = transform.rotation; away = awayDir; right = Vector3.Cross(Vector3.up, away) * d.sideSign; scale = Mathf.Max(0.5f, Mathf.Abs(victim.lossyScale.y));
        gy0 = V1Ground.Y(p0, victim, victim.position.y);
        // rest pose: the long axis of the weapon horizontal, its thinnest axis vertical (V1 searches 64 rolls for the minimum thickness; here the mesh bounds name the axes)
        var mf = GetComponentInChildren<MeshFilter>(true); rRest = r0;
        if (mf != null && mf.sharedMesh != null)
        {
            Vector3 sz = mf.sharedMesh.bounds.size; int lo = 0, hi = 0; for (int i = 1; i < 3; i++) { if (sz[i] > sz[hi]) hi = i; if (sz[i] < sz[lo]) lo = i; }
            if (lo == hi) lo = (hi + 1) % 3;
            Vector3 lAx = Vector3.zero, tAx = Vector3.zero; lAx[hi] = 1f; tAx[lo] = 1f;
            Vector3 L = mf.transform.TransformDirection(lAx).normalized, T = mf.transform.TransformDirection(tAx).normalized;
            Vector3 H = L; H.y = 0f; if (H.sqrMagnitude < 0.01f) H = away; H.Normalize();
            if (Mathf.Abs(Vector3.Dot(L, T)) < 0.99f)
            {
                Vector3 Tp = Vector3.ProjectOnPlane(T, L); if (Tp.sqrMagnitude < 1e-6f) Tp = Vector3.up; else Tp.Normalize();
                rRest = Quaternion.LookRotation(H, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(L, Tp)) * r0;
            }
        }
    }
    void Update()
    {
        if (d == null) return;
        age += FinisherTime.Step(Time.unscaledDeltaTime);
        float k = V1Extras.Smooth(age, d.startS, d.endS);
        Vector3 pos = p0 + right * (d.sideM * scale * k) + away * (d.alongM * scale * k); Quaternion rot = Quaternion.Slerp(r0, rRest, k);
        transform.SetPositionAndRotation(pos, rot);
        Bounds b;
        if (k > 0f && V1Ground.Bounds(transform, out b)) { float gy = V1Ground.Y(b.center, vict, gy0); transform.position += Vector3.up * ((gy + d.floorM - b.min.y) * k); }   // floors[] table: .008 above the sand, blended in with the fall
        if (!logged && age > d.endS) { logged = true; Debug.Log("[finisher] opened: victim weapon '" + name + "' down at " + transform.position.ToString("0.00")); }
    }
}

// ------------------------------------------------------------------------------------------------ opened: scripted leg topple (only without the physics ragdoll)
public sealed class OpenedLegs : MonoBehaviour
{
    public static readonly List<OpenedLegs> All = new List<OpenedLegs>();
    LegsCfg l; Transform hips; Vector3 away, right; float age, scale = 1f, h0 = -1f; bool logged; PoseGuard g;

    public static void Begin(DuelFighter victim, ScriptCfg s, Vector3 away)
    {
        var hips = V1Extras.FindSuffix(victim.transform, s.legs.hipsBone); if (hips == null) return;
        var o = victim.gameObject.AddComponent<OpenedLegs>(); o.l = s.legs; o.hips = hips; o.away = away; o.right = Vector3.Cross(Vector3.up, away) * s.legs.sideSign; o.scale = Mathf.Max(0.5f, Mathf.Abs(victim.transform.lossyScale.y)); o.g = new PoseGuard(hips); All.Add(o);
    }
    void OnDestroy() { All.Remove(this); }
    void LateUpdate()
    {
        if (l == null || hips == null) return;
        age += FinisherTime.Step(Time.unscaledDeltaTime);
        g.Restore();
        float k = V1Extras.Smooth(age, l.startS, l.endS); if (k <= 0f) return;
        if (h0 < 0f) h0 = Mathf.Max(0f, hips.position.y - V1Ground.Y(hips.position, transform, transform.position.y));
        g.Mark();
        hips.RotateAround(hips.position, away, -l.sideSign * l.fallDeg * k);   // legFall: Rz(-90 deg) about the blow axis, slerped in
        hips.position += right * (l.sideM * scale * k) + away * (l.alongM * scale * k) + Vector3.down * (Mathf.Max(0f, h0 - l.hipsRestM) * k);
        g.Done();
        if (!logged && k >= 1f) { logged = true; Debug.Log("[finisher] opened: legs down at " + hips.position.ToString("0.00")); }
    }
}
