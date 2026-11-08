using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// SHIELD v1 (lane SHIELD). Equipping an OFF-HAND item that has a gear.json "shield" block puts its glb on the wearer's left hand and gives the combat code a SHIELD COVER rule.
//  Visual   the glb (GearAssetLoader, same lookup / HDRP-Lit conversion / material rows as every gear glb) hangs under a socket child of the hand bone (shield_mount.json: hand, socket name).
//           The socket is re-posed every LateUpdate from the character's row for the animator state (offset in real metres in the HAND frame, Euler on the socket: the WeaponGrip.cs maths),
//           the world scale is row.scale (the glb is authored in metres, grip bar = origin, +Z the face, +Y up). Rows can tuck the shield (another pose for a state) or hide it (visible 0)
//           per Animator state, blended over blendSeconds. A two-handed mainHand weapon (WeaponSwap.TwoHanded) hides it, and so does a running finisher scene (RTFinisher.Busy, shield_mount.json hideDuringFinishers). The left fingers close on the grip bar
//           (row.curl, GripPose axes) with the same weight as the shield shows.
//  Combat   IGearShieldSource.Shield = the item's ShieldDef (gear.json items[].shield). ShieldHooks.* are the seams Duel.Resolve asks: no shield worn = no component / null = every hook returns the
//           neutral value (false / 1) and the sim is exactly today's. Nothing here names a character or an item: the numbers are the item's row, the mount rows are data.
//  Ragdoll  the socket stays on the hand bone and follows it (no collider, nothing to drop): the simple correct thing, it can never be left floating.
// QA: `-shield:off` = the shield is never equipped (A arm of an A/B run), `-shield:log` = log lines.

public interface IGearShieldSource { GearTable.ShieldDef Shield { get; } }

public static class ShieldHooks
{
    public static GearTable.ShieldDef Of(Component fighter)
    {
        if (fighter == null || !GearTable.Get().enabled) return null;
        var src = fighter.GetComponent<IGearShieldSource>(); return src != null ? src.Shield : null;
    }
    static int SideId(string guard) { return DirectionalGuard.SideIdFor(guard); }
    static int GraceTicks(bool heavy)   // the same block-ease numbers DirectionalGuard.SideOk uses (controls.json guard)
    {
        var G = ControlsTable.Get().guard; float ms = G.sideGraceMs + (heavy ? G.heavyBlockWindowBonusMs : 0f);
        string cls = heavy ? "heavy" : "light";
        if (G.blockEase != null) for (int i = 0; i < G.blockEase.Length; i++) if (G.blockEase[i] != null && G.blockEase[i].cls == cls) { ms += G.blockEase[i].bonusMs; break; }
        return Mathf.Max(0, Mathf.RoundToInt(ms * 60f / 1000f));
    }
    // Duel.Resolve, only after DirectionalGuard.SideOk said NO: true when the defender's guard side is one of the shield's cover rows for this swing's direction (extra cover, never a parry)
    public static bool Covers(DuelFighter d, MoveDef m, bool heavy)
    {
        var s = Of(d); if (s == null || m == null || s.cover == null || string.IsNullOrEmpty(m.direction)) return false;
        for (int i = 0; i < s.cover.Length; i++)
        {
            var c = s.cover[i]; if (c == null || c.attacks == null || Array.IndexOf(c.attacks, m.direction) < 0) continue;
            int id = SideId(c.guard);
            if (d.guardSide == id) return true;
            int g = GraceTicks(heavy); if (id > 0 && g > 0 && d.DirSideHeldWithin(id, g)) return true;
        }
        return false;
    }
    public static float ExtraCostMul(DuelFighter d) { var s = Of(d); return s != null ? s.blockStaminaMul : 1f; }   // stamina of a block the shield covered and the base guard would not have
    public static float BlockPostureMul(DuelFighter d)   // posture put on the wearer by a block made with the shield-side guard (covered or not)
    {
        var s = Of(d); if (s == null || string.IsNullOrEmpty(s.sideGuard)) return 1f;
        return d.guardSide == SideId(s.sideGuard) ? s.blockPostureMul : 1f;
    }
    public static string CueEvent(DuelFighter d) { var s = Of(d); return s != null ? s.cueEvent : ""; }
}

// SHIELDGUARD v1: the shield ARM. A row with arm.weight > 0 puts the left arm in a real sword-and-board hold while its states play: the shield (grip = the row's socketOffset / socketEuler)
// is carried at shieldPos / shieldEuler (metres / degrees, in the FIGHTER's axes x right, y up, z toward the foe, measured from the chest bone), the forearm comes across the body, the elbow bends toward `pole`.
[Serializable]
public class ShieldArmCfg
{
    public float weight = 0f;                                  // 0 = no arm override (the animated arm is untouched)
    public float[] shieldPos = { 0f, 0f, 0f }, shieldEuler = { 0f, 0f, 0f }, pole = { 0f, -1f, 0f };
    public float blendSeconds = 0.1f, reachFadeM = 0.15f, forearmTwist = 0.5f;   // pose ease / the IK fades out when the shield point is further than the arm reaches / share of the wrist roll the forearm takes
    public float punchM = 0f, punchOutS = 0.1f, punchBackS = 0.25f;   // SHIELDGUARD v3: a punch-out of the shield toward the foe (metres along the fighter's z) when the row's state starts: out in punchOutS, back in punchBackS (0 = none)
    public string chest = "mixamorig:Spine2", arm = "mixamorig:LeftArm", forearm = "mixamorig:LeftForeArm";
}
[Serializable]
public class ShieldMountRow
{
    public ShieldArmCfg arm = new ShieldArmCfg();   // SHIELDGUARD v1
    public string name = "", item = "";                  // character (Roster name / id / "*") and the gear item id ("" = any shield)
    public string hand = "LeftHand", socket = "ShieldSocket_L";
    public float[] socketOffset = { 0f, 0f, 0f }, socketEuler = { 0f, 0f, 0f };   // metres in the hand frame / degrees on the socket
    public float scale = 1f, blendSeconds = 0.1f, visible = 1f;
    public string[] states = new string[0];             // Animator states this row applies to (empty = the default row)
    public bool curl = true; public string curlRow = "";   // close the left fingers on the grip bar while the shield shows
    public float fingerDeg = 72f, thumbDeg = 35f, thumbWeight = 0.7f, falloff = 0.85f; public float[] fingerAxis = { 0f, 0f, 1f }, thumbAxis = { 0f, 1f, 0f };
    public string id = "";
}
[Serializable] public class ShieldMountFile { public bool enabled = true, debugLog = false; public bool hideWithTwoHanded = true, hideDuringFinishers = true; public float curlSeconds = 0.1f; /* DATALAYER b8 */ public ShieldMountRow[] rows = new ShieldMountRow[0]; }

[DefaultExecutionOrder(65)]   // SHIELDGUARD v1: after TwoHandIK (60) / HeadClamp (50) / GuardTorsoTurn (40): the chest and hand are final when the arm is solved
public class ShieldMount : MonoBehaviour, IGearShieldSource
{
    static ShieldMountFile data;
    static ShieldMountFile Load()
    {
        if (data != null) return data; data = new ShieldMountFile();
        foreach (var p in DataPath.Candidates("shield_mount.json"))
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<ShieldMountFile>(DataPath.ReadFile(p)); if (r != null && r.rows != null) data = r; } catch (Exception) { }
            break;
        }
        return data;
    }
    static bool Flag(string f) { foreach (var a in Environment.GetCommandLineArgs()) if (a == f) return true; return false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; ExtraCoverBlocks = 0; ContactRelay.OnContact -= OnContactLog; GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq; ContactRelay.OnContact -= OnContactLog; ContactRelay.OnContact += OnContactLog; }
    public static int ExtraCoverBlocks;   // blocks the shield rule added this session (QA: -shield:log prints each one, the balance harness proof counts the lines)
    static void OnContactLog(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (type != "ShieldBlocked") return; ExtraCoverBlocks++;
        if (Flag("-shield:log") || Load().debugLog) Debug.Log("[Shield] extra-cover block #" + ExtraCoverBlocks + ": " + (d != null ? d.name : "?") + " guard " + (d != null ? DirectionalGuard.NameOf(d.guardSide) : "?") + " vs " + (a != null && a.CurrentMove != null ? a.CurrentMove.id + " (" + a.CurrentMove.direction + ")" : "?") + (heavy ? " heavy" : " light"));
    }

    static void OnEq(GearSlots gs, GearTable.Item it, string slotId)
    {
        if (gs == null || it == null || it.shield == null || string.IsNullOrEmpty(it.shield.id) || !GearTable.Get().enabled || !Load().enabled) return;
        if (Flag("-shield:off")) return;   // A arm: no shield, exactly today's fight
        var sm = gs.GetComponent<ShieldMount>(); if (sm == null) sm = gs.gameObject.AddComponent<ShieldMount>();
        sm.gs = gs; sm.Equip(it);
    }
    static void OnUneq(GearSlots gs, GearTable.Item it, string slotId)
    {
        if (gs == null || it == null || it.shield == null || string.IsNullOrEmpty(it.shield.id)) return;
        var sm = gs.GetComponent<ShieldMount>(); if (sm != null) sm.Clear();
    }

    GearSlots gs; GearTable.Item item; GearTable.ShieldDef def; int serial; bool wantLoad;
    Transform hand, socket, inst; Animator anim; WeaponSwap weaponSwap; float diagT; int diagN;
    readonly List<ShieldMountRow> rows = new List<ShieldMountRow>(); ShieldMountRow row;
    Transform[][] lchains; Quaternion[][] lrest;
    Vector3 curOff; Quaternion curRot = Quaternion.identity; float curVis, curScale = 1f; bool curInit; float curl;
    List<Renderer> rends = new List<Renderer>();
    // SHIELDGUARD v1: arm override state
    Transform armChest, armArm, armFore; DuelFighter armFighter; bool armBonesTried; float armW, armWEff; Vector3 armPos, armPole; Quaternion armRot = Quaternion.identity;
    ShieldMountRow armLastRow; float armRowT;   // SHIELDGUARD v3: seconds since the picked row changed (drives the punch)
    public float ArmWeight { get { return armWEff; } }
    public GearTable.ShieldDef Shield { get { return def; } }

    void OnEnable() { if (wantLoad && inst == null) StartLoad(); }
    void OnDestroy() { if (Flag("-shield:log") || Load().debugLog) Debug.Log("[Shield] OnDestroy " + name); Clear(); }

    public void Equip(GearTable.Item it)
    {
        Clear(); item = it; def = it.shield; wantLoad = true;
        if (Load().debugLog || Flag("-shield:log")) Debug.Log("[Shield] " + name + " equips " + it.id + " cover rows " + (def.cover != null ? def.cover.Length : 0));
        StartLoad();
    }
    void StartLoad() { if (!isActiveAndEnabled || item == null) return; wantLoad = false; StartCoroutine(LoadRoutine(serial, item)); }

    public void Clear()
    {
        if (Flag("-shield:log") || Load().debugLog) Debug.Log("[Shield] Clear " + name + " inst " + (inst != null) + " socket " + (socket != null) + (inst != null ? " STACK " + System.Environment.StackTrace.Replace("\n", " | ") : ""));
        serial++; wantLoad = false;
        if (inst != null) { inst.gameObject.SetActive(false); Destroy(inst.gameObject); }
        if (socket != null) Destroy(socket.gameObject);
        inst = null; socket = null; hand = null; item = null; def = null; rows.Clear(); row = null; rends.Clear(); curInit = false; lchains = null; armChest = armArm = armFore = null; armBonesTried = false; armW = armWEff = 0f; armLastRow = null; /* SHIELDGUARD v1 */
    }

    static Transform FindNamed(Transform top, string n)
    {
        if (top == null || string.IsNullOrEmpty(n)) return null;
        foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
        return null;
    }
    static Vector3 V(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }

    IEnumerator LoadRoutine(int my, GearTable.Item it)
    {
        Transform sk = gs != null ? gs.Skeleton : transform; string root = transform.root.name;
        rows.Clear();
        var F = Load(); int best = RosterResolver.Best(F.rows, r => r.name, root);
        foreach (var r in F.rows) if (r != null && best > 0 && RosterResolver.Tier(r.name, root) == best && (string.IsNullOrEmpty(r.item) || r.item == it.id)) rows.Add(r);
        if (rows.Count == 0) { GearTable.Warn("shield '" + it.id + "': no shield_mount.json row for " + root + " (stats apply, no visual)"); yield break; }
        hand = FindNamed(sk, "mixamorig:" + rows[0].hand);
        if (hand == null) { GearTable.Warn("shield '" + it.id + "': no hand bone mixamorig:" + rows[0].hand + " on " + name); yield break; }
        GameObject g = null; IDisposable res = null; string info = "";
        yield return GearAssetLoader.Load(it, it.glb, true, hand, (go, r, i) => { g = go; res = r; info = i; });
        if (this == null || my != serial) { if (res != null) res.Dispose(); if (g != null) Destroy(g); yield break; }   // unequipped / replaced while loading
        if (g == null) { GearTable.Warn("shield '" + it.id + "' has no visual (" + info + "): stats apply"); yield break; }

        var sockGo = new GameObject(rows[0].socket); socket = sockGo.transform; socket.SetParent(hand, false); socket.localPosition = Vector3.zero; socket.localRotation = Quaternion.identity;
        Renderer refR = null;
        foreach (var r in sk.GetComponentsInChildren<Renderer>(true)) if (r is SkinnedMeshRenderer && r.GetComponentInParent<GearPiece>() == null) { refR = r; break; }
        g.name = "Shield_" + it.id; inst = g.transform; inst.SetParent(socket, false); inst.localPosition = Vector3.zero; inst.localRotation = Quaternion.identity; inst.localScale = Vector3.one;
        foreach (var c in g.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var tag = g.GetComponent<GearPiece>(); if (tag == null) tag = g.AddComponent<GearPiece>();
        tag.itemId = it.id; tag.slotId = "shield"; tag.resources = res;
        rends = new List<Renderer>(g.GetComponentsInChildren<Renderer>(true));
        tag.madeMaterials = GearMaterials.Convert(rends, it);   // glTFast -> HDRP/Lit + gear.json material rows (ShieldMetal row)
        int layer = hand.gameObject.layer; foreach (var x in g.GetComponentsInChildren<Transform>(true)) x.gameObject.layer = layer;
        foreach (var r in rends) { r.shadowCastingMode = refR != null ? refR.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = refR != null ? refR.receiveShadows : true; if (refR != null) r.renderingLayerMask = refR.renderingLayerMask; }
        anim = sk.GetComponentInParent<Animator>(); if (anim == null) anim = GetComponentInChildren<Animator>(); weaponSwap = GetComponent<WeaponSwap>();
        lchains = Chains(sk); lrest = Rest(lchains); row = DefaultRow(); curInit = false; curVis = 0f;
        if (Load().debugLog || Flag("-shield:log")) Debug.Log("[Shield] " + name + " shield " + it.id + " on " + hand.name + ": " + info + ", " + rends.Count + " renderers, " + rows.Count + " mount rows");
    }

    ShieldMountRow DefaultRow() { foreach (var r in rows) if (r.states == null || r.states.Length == 0) return r; return rows[0]; }
    ShieldMountRow PickRow()
    {
        if (rows.Count < 2 || anim == null) return DefaultRow();
        for (int li = anim.layerCount - 1; li >= 0; li--)   // the upper (GuardUpper) layer first: while it carries the arm, ITS state decides; layer 0 always counts
        {
            if (li > 0 && anim.GetLayerWeight(li) < 0.5f) continue;
            int h = anim.GetCurrentAnimatorStateInfo(li).shortNameHash; if (anim.IsInTransition(li) && anim.GetAnimatorTransitionInfo(li).normalizedTime > 0.5f) h = anim.GetNextAnimatorStateInfo(li).shortNameHash;
            for (int i = 0; i < rows.Count; i++) { var r = rows[i]; if (r.states == null) continue; for (int j = 0; j < r.states.Length; j++) if (Animator.StringToHash(r.states[j]) == h) return r; }
        }
        return DefaultRow();
    }

    static Transform[][] Chains(Transform sk)
    {
        string[] fingers = { "Index", "Middle", "Ring", "Pinky" }; var all = sk.GetComponentsInChildren<Transform>(true); var c = new Transform[5][];
        for (int f = 0; f < 5; f++) { string nm = f < 4 ? fingers[f] : "Thumb"; var list = new List<Transform>(); for (int j = 1; j <= 3; j++) foreach (var t in all) if (t.name == "mixamorig:LeftHand" + nm + j) { list.Add(t); break; } c[f] = list.ToArray(); }
        return c;
    }
    static Quaternion[][] Rest(Transform[][] ch) { var r = new Quaternion[ch.Length][]; for (int f = 0; f < ch.Length; f++) { r[f] = new Quaternion[ch[f].Length]; for (int j = 0; j < ch[f].Length; j++) r[f][j] = ch[f][j].localRotation; } return r; }

    void LateUpdate()
    {
        if (socket == null || hand == null || inst == null || row == null) { if (diagN < 40 && diagT < Time.unscaledTime && (Flag("-shield:log") || Load().debugLog)) { diagT = Time.unscaledTime + 2f; diagN++; Debug.Log("[Shield] diag#" + diagN + " " + name + " NOT UPDATING: socket " + (socket != null) + " hand " + (hand != null) + " inst " + (inst != null) + " row " + (row != null)); } return; }
        var tr = PickRow();
        bool hideTwo = Load().hideWithTwoHanded && weaponSwap != null && weaponSwap.TwoHanded;   // a two-handed mainHand weapon owns the left hand
        bool hideFin = Load().hideDuringFinishers && RTFinisher.Busy;   // finisher scenes play their own clips (not Animator states): the shield leaves the frame for them
        float tVis = hideTwo || hideFin ? 0f : Mathf.Clamp01(tr.visible);
        Vector3 tOff = V(tr.socketOffset); Quaternion tRot = Quaternion.Euler(V(tr.socketEuler));
        if (!curInit) { curInit = true; curOff = tOff; curRot = tRot; curVis = tVis; curScale = tr.scale; }
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.005f, tr.blendSeconds));
        curOff = Vector3.Lerp(curOff, tOff, k); curRot = Quaternion.Slerp(curRot, tRot, k); curVis = Mathf.Lerp(curVis, tVis, k); curScale = Mathf.Lerp(curScale, tr.scale, k);
        ArmIK(tr, tVis);   // SHIELDGUARD v1: the shield arm (before the socket reads the hand)
        float hs = Mathf.Max(0.0001f, hand.lossyScale.x);
        socket.localPosition = curOff / hs; socket.localRotation = curRot;
        float s = Mathf.Max(0.0001f, curScale * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(curVis)) / hs);   // hide / show = the shield shrinks into the fist over blendSeconds (no pop)
        socket.localScale = new Vector3(s, s, s);   // world scale = row.scale: the glb is authored in metres
        if (diagN < 40 && diagT < Time.unscaledTime && (Flag("-shield:log") || Load().debugLog))
        {
            diagT = Time.unscaledTime + 2f; diagN++; var r0 = rends.Count > 0 ? rends[0] : null; string rs = "none";
            if (r0 != null) { var m0 = r0.sharedMaterial; rs = "enabled " + r0.enabled + " isVisible " + r0.isVisible + " activeInHierarchy " + r0.gameObject.activeInHierarchy + " layer " + r0.gameObject.layer + " bounds " + r0.bounds.center.ToString("F2") + " size " + r0.bounds.size.ToString("F2") + " shader " + (m0 != null && m0.shader != null ? m0.shader.name : "null") + " mats " + r0.sharedMaterials.Length + " tris-mesh " + (r0 is MeshRenderer ? "mesh" : r0.GetType().Name) + " lodgroup " + (r0.GetComponentInParent<LODGroup>() != null); }
            Debug.Log("[Shield] diag#" + diagN + " " + name + " hand lossyScale " + hand.lossyScale.x.ToString("F4") + " socket pos " + socket.position.ToString("F2") + " socket scale " + socket.lossyScale.x.ToString("F3") + " curVis " + curVis.ToString("F2") + " tVis " + tVis.ToString("F2") + " row " + (tr != null ? tr.id : "-") + " hideTwo " + hideTwo + " hideFin " + hideFin + " | rend " + rs + " | cam " + (Camera.main != null ? Camera.main.transform.position.ToString("F1") + " cullMask " + Camera.main.cullingMask : "none"));
        }
        bool show = curVis > 0.02f; for (int i = 0; i < rends.Count; i++) if (rends[i] != null && rends[i].enabled != show) rends[i].enabled = show;
        float want = show && tr.curl ? Mathf.Clamp01(curVis) : 0f;
        curl = Mathf.MoveTowards(curl, want, Time.deltaTime / Mathf.Max(0.0001f, Load().curlSeconds)); /* DATALAYER b8 */
        if (curl > 0f && lchains != null) Curl(tr, curl);
    }
    // SHIELDGUARD v1: left-arm two-bone solve (TwoBoneIK, the same pure maths as TwoHandIK) that carries the shield at the row's arm pose.
    // hand rotation Hr = shield rotation * inverse(grip rotation), wrist target = shield point - Hr * grip offset, so the shield lands exactly on armPos / armEuler.
    void ArmIK(ShieldMountRow tr, float tVis)
    {
        var C = tr.arm; if (C == null) C = new ShieldArmCfg();
        if (!armBonesTried) { armBonesTried = true; Transform sk = gs != null ? gs.Skeleton : transform; armChest = FindNamed(sk, C.chest); armArm = FindNamed(sk, C.arm); armFore = FindNamed(sk, C.forearm); armFighter = GetComponentInParent<DuelFighter>(); }
        if (armChest == null || armArm == null || armFore == null) { armWEff = 0f; return; }
        float tgt = C.weight > 0f && !Flag("-shield:armoff") ? C.weight * Mathf.Clamp01(tVis) : 0f;   // hc:ok QA command-line flag name
        if (armFighter != null && armFighter.Dead) tgt = 0f;
        Vector3 tPos = V(C.shieldPos), tPole = V(C.pole); Quaternion tRot = Quaternion.Euler(V(C.shieldEuler));
        if (tr != armLastRow) { armLastRow = tr; armRowT = 0f; } else armRowT += Time.deltaTime;   // SHIELDGUARD v3: the punch clock restarts whenever the state's row changes
        if (C.punchM > 0f)
        {
            float ph = armRowT < C.punchOutS ? armRowT / Mathf.Max(0.005f, C.punchOutS) : 1f - (armRowT - C.punchOutS) / Mathf.Max(0.005f, C.punchBackS);   // hc:ok epsilon: floor on the phase time
            tPos += Vector3.forward * (C.punchM * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ph)));
        }
        if (armW <= 0.001f && tgt > 0f) { armPos = tPos; armRot = tRot; armPole = tPole; }   // coming from no override: start on the target (no sweep through the body)   // hc:ok epsilon: arm weight is zero
        else if (tgt > 0f) { float ka = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.005f, C.blendSeconds)); armPos = Vector3.Lerp(armPos, tPos, ka); armRot = Quaternion.Slerp(armRot, tRot, ka); armPole = Vector3.Lerp(armPole, tPole, ka); }   // hc:ok epsilon: floor on the blend time
        armW = Mathf.MoveTowards(armW, tgt, Time.deltaTime / Mathf.Max(0.005f, C.blendSeconds));   // hc:ok epsilon: floor on the blend time
        armWEff = 0f;
        if (armW <= 0.001f) return;   // hc:ok epsilon: arm weight is zero
        Quaternion fr = (armFighter != null ? armFighter.transform : transform).rotation;   // the fighter's axes (facing the foe)
        Vector3 Sp = armChest.position + fr * armPos; Quaternion Sr = fr * armRot;
        Quaternion Hr = Sr * Quaternion.Inverse(curRot); Vector3 Hp = Sp - Hr * curOff;      // grip rotation / offset = the smoothed socket row (hand frame, metres)
        Vector3 R = armArm.position, M = armFore.position, E = hand.position, pole = fr * armPole;
        Vector3 nM, nE; float gap;
        if (!TwoBoneIK.Solve(R, M, E, Hp, pole, out nM, out nE, out gap)) return;
        armWEff = armW * (1f - Mathf.Clamp01(C.reachFadeM > 1e-4f ? gap / C.reachFadeM : (gap > 0f ? 1f : 0f)));   // hc:ok epsilon: division guard
        if (armWEff <= 0.001f) return;   // hc:ok epsilon: effective weight is zero
        Quaternion a0 = armArm.localRotation, f0 = armFore.localRotation, h0 = hand.localRotation;
        armArm.rotation = Quaternion.FromToRotation(M - R, nM - R) * armArm.rotation;                                    // elbow onto nM
        armFore.rotation = Quaternion.FromToRotation(hand.position - armFore.position, nE - armFore.position) * armFore.rotation;   // wrist onto nE
        Vector3 ax = (nE - armFore.position).normalized; Quaternion q = Hr * Quaternion.Inverse(armFore.rotation); if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
        float twist = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(q.x, q.y, q.z), ax), q.w) * Mathf.Rad2Deg;                // the roll of the hand about the forearm: the forearm takes its share   // hc:ok quaternion angle = 2 atan2, not a tunable
        armFore.rotation = Quaternion.AngleAxis(twist * C.forearmTwist, ax) * armFore.rotation;
        hand.rotation = Hr;
        armArm.localRotation = Quaternion.Slerp(a0, armArm.localRotation, armWEff); armFore.localRotation = Quaternion.Slerp(f0, armFore.localRotation, armWEff); hand.localRotation = Quaternion.Slerp(h0, hand.localRotation, armWEff);
        if ((Flag("-shield:log") || Load().debugLog) && diagN < 40 && diagT < Time.unscaledTime) Debug.Log("[Shield] arm row " + tr.id + " w " + armWEff.ToString("F2") + " reachGap " + (gap * 100f).ToString("F1") + "cm twist " + twist.ToString("F0"));   // hc:ok debug log, cm conversion
    }
    void Curl(ShieldMountRow r, float w)
    {
        for (int f = 0; f < 5; f++)
            for (int j = 0; j < lchains[f].Length; j++)
            {
                float a = f < 4 ? r.fingerDeg : r.thumbDeg * r.thumbWeight; a *= w * Mathf.Pow(r.falloff, j);
                var axis = f < 4 ? V(r.fingerAxis) : V(r.thumbAxis);
                lchains[f][j].localRotation = Quaternion.Slerp(lchains[f][j].localRotation, lrest[f][j] * Quaternion.AngleAxis(a, axis), w);   // absolute (GripPose absolute mode): never cumulative
            }
    }
}
