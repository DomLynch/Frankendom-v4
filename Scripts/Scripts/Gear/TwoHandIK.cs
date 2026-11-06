using UnityEngine;

// WEAPONSWAP v88: left-arm two-bone IK for two-handed weapons (gear.json items[].weapon.twoHanded). No Animation Rigging package and no Humanoid avatar (Generic mixamorig rig), so this is a plain
// LateUpdate solve on mixamorig:LeftArm / LeftForeArm / LeftHand: the left PALM is brought onto the weapon's off-hand node (glb node `grip_off`, on the shaft axis), the hand is turned so its
// finger-curl axis lies along the shaft with the palm facing it, then GripPose closes the left fingers (grip_pose.json row of the weapon: leftHand true).
// Weight: smoothed 0..1 (item.weapon.ikWeight, ikBlendSeconds); 0 when the wearer is dead, during a finisher (Duel.Frozen, weaponSwap.ik.releaseWhenFrozen) or in a release state; fades out
// smoothly when the target is further than the arm can reach (weaponSwap.ik.reachFadeM). Runs after the Animator, ArmHold, WeaponGrip (-50), GuardTorsoTurn (40) and HeadClamp (50),
// so the weapon socket and the torso are final when the target is read. All numbers: gear.json "weaponSwap.ik".
//   The solve is split in two: TwoBoneIK (pure vector maths, no Unity engine calls: unit tested standalone) and this component (bones -> rotations).
// Self-attached by WeaponSwap when a twoHanded weapon's visual is on the socket; it does nothing without an owner that says TwoHanded.

// Pure two-bone positions: given the chain root R, elbow M, wrist E (current pose) and a wrist target T, return the elbow and wrist that keep both bone lengths, put the wrist on T
// (or as close as the chain reaches along R->T) and bend toward `pole` (any direction; only its part perpendicular to R->T counts).
public static class TwoBoneIK
{
    public static bool Solve(Vector3 R, Vector3 M, Vector3 E, Vector3 T, Vector3 pole, out Vector3 newM, out Vector3 newE, out float reachGap)
    {
        newM = M; newE = E; reachGap = 0f;
        float l1 = (M - R).magnitude, l2 = (E - M).magnitude;
        if (l1 < 1e-5f || l2 < 1e-5f) return false;
        Vector3 rt = T - R; float dRaw = rt.magnitude;
        if (dRaw < 1e-5f) return false;
        Vector3 u = rt / dRaw;
        float dMax = (l1 + l2) * 0.9995f, dMin = Mathf.Abs(l1 - l2) * 1.0005f + 1e-4f;
        float d = Mathf.Clamp(dRaw, dMin, dMax);
        reachGap = Mathf.Max(0f, dRaw - (l1 + l2));
        float a = (l1 * l1 - l2 * l2 + d * d) / (2f * d);                // distance along R->T to the foot of the elbow
        float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - a * a));            // elbow height over the line R->T
        Vector3 p = pole - u * Vector3.Dot(pole, u);
        if (p.sqrMagnitude < 1e-8f) { Vector3 pm = (M - R); p = pm - u * Vector3.Dot(pm, u); }
        if (p.sqrMagnitude < 1e-8f) p = Vector3.Cross(u, Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.right);
        p.Normalize();
        newM = R + u * a + p * h;
        newE = R + u * d;
        return true;
    }
}

[DefaultExecutionOrder(60)]
public class TwoHandIK : MonoBehaviour
{
    public WeaponSwap owner;
    public float Weight { get { return wEff; } }                          // 0..1 actually applied last frame (GripPose closes the left fingers with it)
    public bool Driving { get { return owner != null && owner.TwoHanded; } }

    Transform arm, fore, hand, mid; Animator anim; DuelFighter fighter; bool init, ok;
    Vector3 palmLocal, fH, nH; Quaternion qLocal = Quaternion.identity;   // palm point + finger direction + palm normal, all in the hand bone's frame
    float w, wEff; int frameN; Vector3 curlAxis = Vector3.forward, poleHint = new Vector3(-0.5f, -1f, -0.3f);

    static Vector3 V(float[] a, Vector3 d) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : d; }
    static Transform Find(Transform top, string n) { if (string.IsNullOrEmpty(n)) return null; foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t; return null; }

    void Init()
    {
        init = true; var C = GearTable.Get().weaponSwap.ik;
        arm = Find(transform, C.arm); fore = Find(transform, C.forearm); hand = Find(transform, C.hand); mid = Find(transform, C.middle);
        anim = GetComponent<Animator>(); fighter = GetComponentInParent<DuelFighter>();
        if (arm == null || fore == null || hand == null) { GearTable.Warn("TwoHandIK: left arm bones not found under " + name + " (" + C.arm + " / " + C.forearm + " / " + C.hand + "): off"); return; }
        curlAxis = V(C.curlAxis, Vector3.forward).normalized; poleHint = V(C.poleHintLocal, poleHint);
        ok = true; Vector3 v = Vector3.zero;
        if (mid != null) v = hand.InverseTransformDirection(mid.position - hand.position);
        if (v.sqrMagnitude < 1e-8f) { v = hand.InverseTransformDirection(hand.position - fore.position); }   // no middle finger bone: along the forearm
        fH = v.normalized; palmLocal = v * C.palmFrac;
        // finger-curl axis in the hand frame = the finger bone's own local axis (a curl about it never changes it); the palm normal is where a positive curl swings the fingertip
        Vector3 c = mid != null ? mid.localRotation * curlAxis : curlAxis;
        c -= fH * Vector3.Dot(c, fH);
        if (c.sqrMagnitude < 1e-6f) { c = Vector3.Cross(fH, Vector3.up); if (c.sqrMagnitude < 1e-6f) c = Vector3.right; }
        c.Normalize();
        nH = Vector3.Cross(c, fH).normalized; if (C.flipPalm) nH = -nH;
        qLocal = Quaternion.LookRotation(fH, nH);
    }

    bool InStates(string[] names)
    {
        if (names == null || names.Length == 0 || anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return false;
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash;
        if (anim.IsInTransition(0) && anim.GetAnimatorTransitionInfo(0).normalizedTime > 0.5f) h = anim.GetNextAnimatorStateInfo(0).shortNameHash;
        for (int i = 0; i < names.Length; i++) if (Animator.StringToHash(names[i]) == h) return true;
        return false;
    }

    void LateUpdate()
    {
        if (!init) Init();
        if (!ok) return;
        var C = GearTable.Get().weaponSwap.ik;
        Transform grip = owner != null ? owner.OffHandNode : null;
        float tgt = 0f;
        if (Driving && grip != null && (anim == null || anim.enabled)) tgt = Mathf.Clamp01(owner.IkWeight);
        if (fighter != null && fighter.Dead) tgt = 0f;
        if (C.releaseWhenFrozen && Duel.Frozen) tgt = 0f;
        if (InStates(C.releaseStates)) tgt = 0f;
        if (tgt > 0f && C.states != null && C.states.Length > 0 && !InStates(C.states)) tgt = 0f;
        float dt = Time.deltaTime, blend = owner != null ? owner.IkBlendSeconds : 0.15f;
        w = Mathf.MoveTowards(w, tgt, dt / blend);
        wEff = 0f;
        if (w <= 0.001f || grip == null) return;

        // ---- target: wrist position and hand rotation that put the left palm on the shaft ----
        Quaternion rh0 = hand.rotation; Vector3 G = grip.position, s = grip.up.normalized;
        Vector3 pal0 = hand.position + rh0 * palmLocal;                    // animated palm point
        Vector3 perp = G - pal0; perp -= s * Vector3.Dot(perp, s);
        if (perp.sqrMagnitude < 1e-6f) { perp = fighter != null ? -fighter.transform.right : -transform.right; perp -= s * Vector3.Dot(perp, s); }
        Vector3 nW = perp.normalized;                                      // palm -> shaft: the hand comes from the side it is already on
        if (Mathf.Abs(C.rollDeg) > 0.01f) nW = Quaternion.AngleAxis(C.rollDeg, s) * nW;
        Quaternion best = rh0; float bestAng = float.MaxValue;
        for (int sg = 0; sg < 2; sg++)                                     // curl axis along +s or -s: the one that turns the animated hand less
        {
            Vector3 cW = sg == 0 ? s : -s; Vector3 fW = Vector3.Cross(nW, cW);
            if (fW.sqrMagnitude < 1e-8f) continue;
            Quaternion rh = Quaternion.LookRotation(fW.normalized, nW) * Quaternion.Inverse(qLocal);
            float ang = Quaternion.Angle(rh, rh0); if (ang < bestAng) { bestAng = ang; best = rh; }
        }
        if (C.keepAnimatedRotation) best = rh0;
        Vector3 T = G - nW * C.palmToShaftM - best * palmLocal;           // wrist target

        // ---- two-bone solve (positions), then bone rotations ----
        Vector3 R = arm.position, M = fore.position, E = hand.position;
        Vector3 pole = (M - R); Vector3 hint = (fighter != null ? fighter.transform : transform).TransformDirection(poleHint).normalized;
        Vector3 axis = (T - R).normalized; Vector3 pp = pole - axis * Vector3.Dot(pole, axis);
        pole = pp.sqrMagnitude > 1e-8f ? pp.normalized : hint; pole = Vector3.Slerp(pole, hint, Mathf.Clamp01(C.poleHintMix));
        Vector3 nM, nE; float gap;
        if (!TwoBoneIK.Solve(R, M, E, T, pole, out nM, out nE, out gap)) return;
        wEff = w * (1f - Mathf.Clamp01(C.reachFadeM > 1e-4f ? gap / C.reachFadeM : (gap > 0f ? 1f : 0f)));
        if (wEff <= 0.001f) return;

        Quaternion a0 = arm.localRotation, f0 = fore.localRotation, h0 = hand.localRotation;
        arm.rotation = Quaternion.FromToRotation(M - R, nM - R) * arm.rotation;                        // elbow onto nM (the forearm bone is a child of the arm tip)
        fore.rotation = Quaternion.FromToRotation(hand.position - fore.position, nE - fore.position) * fore.rotation;   // wrist onto nE
        hand.rotation = best;
        Quaternion a1 = arm.localRotation, f1 = fore.localRotation, h1 = hand.localRotation;
        arm.localRotation = Quaternion.Slerp(a0, a1, wEff); fore.localRotation = Quaternion.Slerp(f0, f1, wEff); hand.localRotation = Quaternion.Slerp(h0, h1, wEff);

        if (C.debugLog && ++frameN % Mathf.Max(1, C.debugEvery) == 0)
        {
            Vector3 palmNow = hand.position + hand.rotation * palmLocal; Vector3 d = G - palmNow; float radial = (d - s * Vector3.Dot(d, s)).magnitude;
            Debug.Log("[TwoHandIK] w=" + w.ToString("F2") + " eff=" + wEff.ToString("F2") + " reachGap=" + (gap * 100f).ToString("F1") + "cm palm-to-shaft=" + (radial * 100f).ToString("F1") + "cm (want " + (C.palmToShaftM * 100f).ToString("F1") + ") handTurn=" + bestAng.ToString("F0") + "deg");
        }
    }
}
