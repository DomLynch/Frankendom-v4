using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// OFFHAND v1 (laneMONSTER3). A small, data-driven LEFT-HAND weapon: a visual-only second blade for a fighter that dual-wields (Grendel). OFF by default: gear.json "offHandWeapon".enabled = false.
//  What it does   listens to GearSlots.OnEquipped / OnUnequipped (like WeaponSwap) for an item in the offHandWeapon.slot (default "offHand") that has a gear.json weapon block (weapon.id) and NO shield block (a shield stays ShieldMount's).
//                 Loads the item's glb (GearAssetLoader, same lookup as every gear glb), parents it under a socket (offHandWeapon.socket, default WeaponSocket_L) on the hand bone (prefix + offHandWeapon.bone, default
//                 mixamorig:LeftHand); the socket is created under the bone when the skeleton has none. The grip is the item's own weapon_grip.json row (name = the fighter, weapon = the item id, hand = the bone,
//                 socketOffset / socketEuler exactly as WeaponGrip.cs reads them: metres in the hand-bone frame, degrees), re-applied every LateUpdate.
//  Sim            NONE. Combat reads ONE weapon per fighter: WeaponHooks.Of(fighter) = the IGearWeaponSource on the root (WeaponSwap, the mainHand item) for damage / posture / reach / tick length, Duel.Resolve decides a hit from
//                 distances and the move's own ticks, and WeaponSwap deletes every Collider of a weapon glb (so does this). The second blade changes no number, no hit, no tick, no sim hash.
//  Unequip / death  Clear() destroys the glb (GearPiece frees the glTFast import and the HDRP/Lit copies) and the socket it made.
// Nothing here names a character or a weapon. Data: gear.json offHandWeapon {enabled, slot, bone, socket}, items[], weapon_grip.json rows.

public class OffHandWeapon : MonoBehaviour
{
    [Serializable] public class Cfg { public bool enabled = false; public string slot = "offHand", bone = "LeftHand", socket = "WeaponSocket_L"; }
    [Serializable] class CfgRoot { public Cfg offHandWeapon = new Cfg(); }
    [Serializable] class GripRow { public string name = ""; public bool enabled = false; public string hand = "LeftHand", socket = "", weapon = ""; public float[] socketOffset = { 0f, 0f, 0f }, socketEuler = { 0f, 0f, 0f }; }
    [Serializable] class GripRoot { public GripRow[] characters = new GripRow[0]; }

    static Cfg cfg; static GripRoot grips;
    static Cfg Config()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        foreach (var p in DataPath.Candidates("gear.json"))
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<CfgRoot>(System.IO.File.ReadAllText(p)); if (r != null && r.offHandWeapon != null) cfg = r.offHandWeapon; } catch (Exception) { }
            break;
        }
        if (cfg.slot == null) cfg.slot = "offHand"; if (cfg.bone == null) cfg.bone = ""; if (cfg.socket == null) cfg.socket = "";
        return cfg;
    }
    static GripRoot Grips()
    {
        if (grips != null) return grips;
        grips = new GripRoot();
        foreach (var p in DataPath.Candidates("weapon_grip.json"))
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<GripRoot>(System.IO.File.ReadAllText(p)); if (r != null && r.characters != null) grips = r; } catch (Exception) { }
            break;
        }
        return grips;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; grips = null; GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq; }

    static bool IsOurs(GearTable.Item it, string slotId)
    {
        var C = Config(); if (!C.enabled || it == null || slotId != C.slot) return false;
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

    GearSlots gs; GearTable.Item item; int serial; bool wantLoad;
    Transform hand, socket, instance; bool madeSocket; GripRow row; Vector3 pos0; Quaternion rot0 = Quaternion.identity;

    public bool VisualShown { get { return instance != null; } }
    public string WeaponId { get { return instance != null && item != null ? item.weapon.id : ""; } }

    void OnEnable() { if (wantLoad && instance == null && item != null) StartLoad(); }
    void OnDestroy() { Clear(); }

    public void Equip(GearTable.Item it)
    {
        Clear(); item = it; wantLoad = true; StartLoad();
    }
    void StartLoad() { if (!isActiveAndEnabled || item == null) return; wantLoad = false; StartCoroutine(Load(serial, item)); }

    public void Clear()
    {
        serial++; wantLoad = false;
        if (instance != null) { instance.gameObject.SetActive(false); Destroy(instance.gameObject); }   // GearPiece.OnDestroy disposes the glTFast import and the HDRP/Lit copies
        if (madeSocket && socket != null) Destroy(socket.gameObject);
        instance = null; socket = null; hand = null; madeSocket = false; row = null; item = null;
    }

    static Transform FindNamed(Transform top, string n)
    {
        if (top == null || string.IsNullOrEmpty(n)) return null;
        foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
        return null;
    }

    IEnumerator Load(int my, GearTable.Item it)
    {
        var C = Config(); Transform sk = gs != null ? gs.Skeleton : transform;
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
        foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Destroy(c);                  // visual only: no collider, like WeaponSwap
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
        row = PickRow(it.weapon.id);
        instance = t;
        GearTable.Log("off-hand weapon " + it.weapon.id + " on " + name + ": " + info + (row != null ? ", grip row found" : ", NO weapon_grip.json row (socket at the bone)"));
    }

    GripRow PickRow(string weaponId)
    {
        var D = Grips(); if (D.characters == null) return null;
        string rn = transform.root.name; int rrb = RosterResolver.Best(D.characters, rrx => rrx.name, rn);
        foreach (var c in D.characters)
        {
            if (c == null || !c.enabled || rrb <= 0 || RosterResolver.Tier(c.name, rn) != rrb) continue;
            if (c.weapon == weaponId) return c;
        }
        return null;
    }

    static Vector3 V(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }

    void LateUpdate()
    {
        if (instance == null || socket == null || hand == null) return;
        if (row == null) { socket.localPosition = pos0; socket.localRotation = rot0; return; }
        float hs = Mathf.Max(0.0001f, hand.lossyScale.x);                                           // same maths as WeaponGrip: metres converted by the hand's lossy scale
        socket.localPosition = pos0 + V(row.socketOffset) / hs; socket.localRotation = Quaternion.Euler(V(row.socketEuler)) * rot0;
    }
}
