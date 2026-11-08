using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// OFFHAND v2 (laneOFFHAND, from laneMONSTER3's v1). A data-driven LEFT-HAND weapon: a second blade for a fighter that dual-wields (Grendel). Data: Assets/Data/off_hand.json (master, bone, socket, per-fighter grip rows, per-move blade rows).
//  Visual   listens to GearSlots.OnEquipped / OnUnequipped (like WeaponSwap) for an item in off_hand.json slot ("offHand") that has a gear.json weapon block and NO shield block (a shield stays ShieldMount's, Joan is untouched).
//           The item's glb (GearAssetLoader) hangs under the socket on the hand bone (prefix + bone); the socket is made when the skeleton has none. The grip is the fighter's row (name, item): socketOffset (metres, hand-bone frame)
//           / socketEuler (degrees) / slide (metres along the blade), eased over blendSeconds, optional Animator states per row (same maths as WeaponGrip). It rides the hand bone, so it moves with the animation and the ragdoll.
//  Sim      NONE. Duel.Resolve decides a hit from the attacker/defender positions, the move's reach x DuelFighter.ReachScale (= scale x WeaponHooks = the ONE IGearWeaponSource on the root, WeaponSwap) and the move's own ticks; no
//           collider is read. This component implements no IGearWeaponSource, deletes every Collider of its glb, writes no DuelFighter / Animator / CharacterController state and uses no Random. Blade-to-hit assignment is
//           presentation only (below).
//  Presentation  PhysWeapon.Prims adds this blade as a capsule (socket -> glb tip node) to the contact maths, so contact sparks (ClashFx / HitFeel SparkAnchor) and the clash impulse point (HitReact) treat it like the main
//           weapon. off_hand.json attacks[] says which blade(s) a move swings (BladesFor): an attacker's contact point comes from those blades only; as a defender every blade counts.
public class OffHandWeapon : MonoBehaviour
{
    [Serializable] public class Row
    {
        public string name = "", item = ""; public bool enabled = false;
        public float[] socketOffset = { 0f, 0f, 0f }, socketEuler = { 0f, 0f, 0f }; public float slide = 0f, blendSeconds = 0.1f;
        public string[] states = new string[0];   // Animator states this row applies to (empty = the default row)
    }
    [Serializable] public class AttackRow { public string name = "", move = ""; public string[] blades = new string[0]; }   // "R" = the main-hand weapon, "L" = the off-hand weapon
    [Serializable] public class Cfg { public bool enabled = false, debugLog = false; public string slot = "offHand", bone = "LeftHand", socket = "WeaponSocket_L"; public Row[] rows = new Row[0]; public AttackRow[] attacks = new AttackRow[0]; }

    static Cfg cfg;
    public static Cfg Config()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        foreach (var p in DataPath.Candidates("off_hand.json"))
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); if (r != null) cfg = r; } catch (Exception) { }
            break;
        }
        if (cfg.slot == null) cfg.slot = ""; if (cfg.bone == null) cfg.bone = ""; if (cfg.socket == null) cfg.socket = "";
        if (cfg.rows == null) cfg.rows = new Row[0]; if (cfg.attacks == null) cfg.attacks = new AttackRow[0];
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq; }

    static bool IsOurs(GearTable.Item it, string slotId)
    {
        var C = Config(); if (!C.enabled || it == null || C.slot.Length == 0 || slotId != C.slot) return false;
        if (it.weapon == null || string.IsNullOrEmpty(it.weapon.id)) return false;              // armour and the like: not a weapon item
        if (it.shield != null && !string.IsNullOrEmpty(it.shield.id)) return false;            // a shield stays ShieldMount's
        return true;
    }
    static void OnEq(GearSlots gs, GearTable.Item it, string slotId)
    {
        if (gs == null || !GearTable.Get().enabled || !IsOurs(it, slotId)) return;
        var ow = gs.GetComponent<OffHandWeapon>(); if (ow == null) ow = gs.gameObject.AddComponent<OffHandWeapon>();
        ow.gs = gs; ow.Equip(it);
    }
    static void OnUneq(GearSlots gs, GearTable.Item it, string slotId)
    {
        if (gs == null) return; var C = Config(); if (slotId != C.slot) return;
        var ow = gs.GetComponent<OffHandWeapon>(); if (ow != null) ow.Clear();
    }

    /// <summary>The off-hand weapon of a fighter (null = none). Presentation readers only (PhysWeapon).</summary>
    public static OffHandWeapon Of(Component fighter) { return fighter != null ? fighter.GetComponent<OffHandWeapon>() : null; }

    /// <summary>Which blades the attacker's current move swings (off_hand.json attacks[]); no row for the move = both. Presentation only.</summary>
    public static void BladesFor(DuelFighter a, out bool main, out bool off)
    {
        main = off = true; if (a == null || a.CurrentMove == null) return;
        var A = Config().attacks; if (A.Length == 0) return;
        string rn = a.transform.root.name, mid = a.CurrentMove.id;
        for (int i = 0; i < A.Length; i++)
        {
            var r = A[i]; if (r == null || r.move != mid || r.blades == null || r.blades.Length == 0) continue;
            if (RosterResolver.Tier(r.name, rn) <= 0) continue;
            main = Array.IndexOf(r.blades, "R") >= 0; off = Array.IndexOf(r.blades, "L") >= 0; return;
        }
    }

    GearSlots gs; GearTable.Item item; int serial; bool wantLoad;
    Transform hand, socket, instance, tip, root; Animator anim; bool madeSocket;
    readonly List<Row> all = new List<Row>(); Row row; Vector3 pos0, instBase, curOff; Quaternion rot0 = Quaternion.identity, curRot = Quaternion.identity; float curSlide; bool curInit;

    public bool VisualShown { get { return instance != null; } }
    public string WeaponId { get { return instance != null && item != null ? item.weapon.id : ""; } }
    public Transform Socket { get { return socket; } }
    public Transform Tip { get { return tip; } }      // the glb's tip node (null = no tip node: the blade has no contact capsule)

    void OnEnable() { if (wantLoad && instance == null && item != null) StartLoad(); }
    void OnDestroy() { Clear(); }

    public void Equip(GearTable.Item it) { Clear(); item = it; wantLoad = true; StartLoad(); }
    void StartLoad() { if (!isActiveAndEnabled || item == null) return; wantLoad = false; StartCoroutine(Load(serial, item)); }

    public void Clear()
    {
        serial++; wantLoad = false;
        if (instance != null) { instance.gameObject.SetActive(false); Destroy(instance.gameObject); }   // GearPiece.OnDestroy disposes the glTFast import and the HDRP/Lit copies
        if (madeSocket && socket != null) Destroy(socket.gameObject);
        instance = null; socket = null; tip = null; hand = null; madeSocket = false; row = null; item = null; all.Clear(); curInit = false;
    }

    static Transform FindNamed(Transform top, string n)
    {
        if (top == null || string.IsNullOrEmpty(n)) return null;
        foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
        return null;
    }

    IEnumerator Load(int my, GearTable.Item it)
    {
        var C = Config(); Transform sk = gs != null ? gs.Skeleton : transform; root = transform.root;
        hand = FindNamed(sk, Roster.BonePrefix(this) + C.bone);
        if (hand == null) { GearTable.Warn("off-hand weapon '" + it.weapon.id + "': no bone '" + Roster.BonePrefix(this) + C.bone + "' on " + name); yield break; }
        socket = FindNamed(hand, C.socket); madeSocket = false;
        if (socket == null) { var g = new GameObject(C.socket); socket = g.transform; socket.SetParent(hand, false); socket.localPosition = Vector3.zero; socket.localRotation = Quaternion.identity; socket.localScale = Vector3.one; madeSocket = true; }
        pos0 = socket.localPosition; rot0 = socket.localRotation;
        GameObject inst = null; IDisposable res = null; string info = "";
        yield return GearAssetLoader.Load(it, it.glb, true, socket, (g, r, i) => { inst = g; res = r; info = i; });
        if (this == null || my != serial) { if (res != null) res.Dispose(); if (inst != null) Destroy(inst); yield break; }   // unequipped / replaced while loading
        if (inst == null) { GearTable.Warn("off-hand weapon '" + it.weapon.id + "' has no visual (" + info + "): nothing is shown"); yield break; }

        Renderer refR = null;
        foreach (var r in sk.GetComponentsInChildren<Renderer>(true)) if (!r.transform.IsChildOf(socket) && !(r is TrailRenderer)) { refR = r; break; }   // the wearer's own renderer: layer / shadow settings must match
        inst.name = "OffHand_" + it.id; var t = inst.transform; t.SetParent(socket, false); t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; t.localScale = Vector3.one;
        foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Destroy(c);                  // no collider: the sim reads none
        var tag = inst.GetComponent<GearPiece>(); if (tag == null) tag = inst.AddComponent<GearPiece>();
        tag.itemId = it.id; tag.slotId = "offHandWeapon"; tag.resources = res;
        var rends = new List<Renderer>(inst.GetComponentsInChildren<Renderer>(true));
        tag.madeMaterials = GearMaterials.Convert(rends, it);
        int layer = socket.gameObject.layer; foreach (var x in inst.GetComponentsInChildren<Transform>(true)) x.gameObject.layer = layer;
        foreach (var r in rends)
        {
            r.shadowCastingMode = refR != null ? refR.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = refR != null ? refR.receiveShadows : true;
            if (refR != null) r.renderingLayerMask = refR.renderingLayerMask;
        }
        var grip = FindNamed(t, it.weapon.gripNode);
        if (grip != null && grip != t) t.localPosition -= socket.InverseTransformPoint(grip.position);
        instBase = t.localPosition; tip = FindNamed(t, it.weapon.tipNode);
        anim = sk.GetComponentInParent<Animator>(); if (anim == null) anim = GetComponentInChildren<Animator>();
        all.Clear(); string rn = root.name;
        foreach (var c in C.rows) if (c != null && c.enabled && RosterResolver.Tier(c.name, rn) > 0 && (string.IsNullOrEmpty(c.item) || c.item == it.id)) all.Add(c);
        row = PickRow(); curInit = false; instance = t;
        if (C.debugLog) Debug.Log("[OffHand] " + name + " " + it.id + " on " + hand.name + ": " + info + ", rows " + all.Count + ", tip " + (tip != null ? tip.name : "none"));
        GearTable.Log("off-hand weapon " + it.weapon.id + " on " + name + ": " + info + (all.Count > 0 ? ", grip row found" : ", NO off_hand.json row (socket at the bone)"));
    }

    Row PickRow()
    {
        if (all.Count == 0) return null;
        if (all.Count > 1 && anim != null)
        {
            int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash; if (anim.IsInTransition(0) && anim.GetAnimatorTransitionInfo(0).normalizedTime > 0.5f) h = anim.GetNextAnimatorStateInfo(0).shortNameHash;
            for (int i = 0; i < all.Count; i++) { var r = all[i]; if (r.states == null) continue; for (int j = 0; j < r.states.Length; j++) if (Animator.StringToHash(r.states[j]) == h) return r; }
        }
        for (int i = 0; i < all.Count; i++) if (all[i].states == null || all[i].states.Length == 0) return all[i];
        return all[0];
    }

    static Vector3 V(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }

    void LateUpdate()
    {
        if (instance == null || socket == null || hand == null) return;
        row = PickRow();
        if (row == null) { socket.localPosition = pos0; socket.localRotation = rot0; return; }
        float hs = Mathf.Max(0.0001f, hand.lossyScale.x);   // hc:ok divide guard; same maths as WeaponGrip: metres converted by the hand's lossy scale
        Vector3 tOff = V(row.socketOffset); Quaternion tRot = Quaternion.Euler(V(row.socketEuler));
        if (!curInit) { curInit = true; curOff = tOff; curRot = tRot; curSlide = row.slide; }
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.005f, row.blendSeconds));   // hc:ok divide guard
        curOff = Vector3.Lerp(curOff, tOff, k); curRot = Quaternion.Slerp(curRot, tRot, k); curSlide = Mathf.Lerp(curSlide, row.slide, k);
        socket.localPosition = pos0 + curOff / hs; socket.localRotation = curRot * rot0;
        float ss = Mathf.Max(0.0001f, socket.lossyScale.y);   // hc:ok divide guard
        instance.localPosition = instBase + Vector3.up * (-curSlide / ss);
    }
}
