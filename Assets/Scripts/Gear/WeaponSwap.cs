using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// WEAPONSWAP v88 (lane WEAPONSWAP). Equipping a mainHand item that has a gear.json "weapon" block REPLACES the wearer's sword on WeaponSocket_R, visually and in combat; unequipping brings the sword back.
//  Visual   the item's glb (GearAssetLoader, same lookup as every gear glb) is parented under the weapon socket (grip node = the glb origin = the socket origin, +Y along the shaft). The sockets' other children
//           (the default sword LODs) are switched off, except the names in weaponSwap.keepChildren (the TrailTip is re-parented under the new weapon's `tip` node and goes back on unequip).
//           The mainHand slot stays a DATA-ONLY slot in GearSlots (visual false): GearSlots keeps counting the item's armor / poise / weight stats, this component does the weapon part and listens to
//           GearSlots.OnEquipped / OnUnequipped (static events), so GearSlots / the loot UI / the drop code need no edit. Loot save / load re-equips through the same events.
//  Combat   IGearWeaponSource.Weapon = damageMul / speedMul / reachMul / postureMul of the item's gear.json "weapon" block (neutral = the sword: 1 / 1 / 1 / 1). WeaponHooks.* are the seams the combat code asks
//           (Duel.Resolve damage, posture and reach, the move tick lengths of DuelFighter): 1 / 1 / 1 / 1 for a fighter without a WeaponSwap (Pitborn, Dracula), exactly as before.
//  Grip     WeaponSwap.IdOf(root) = the weapon id whose VISUAL is on the socket ("" = the default sword): WeaponGrip.cs and GripPose.cs pick their rows by it (rows carry "weapon"; "" = every weapon).
//  Two-hand twoHanded weapons attach a TwoHandIK to the Animator object, aimed at the weapon's offHandNode (grip_off).
// Stats apply at once on equip (like GearSlots stats without an asset), the visual / grip rows / IK follow when the glb is on the socket. A glb that is missing or does not load = a warning and the sword stays.
// Data: gear.json "weaponSwap" (socket, keepChildren, ik) and items[].weapon. Nothing here names a character or a weapon.

public class WeaponStats
{
    public string id = "";
    public float damageMul = 1f, speedMul = 1f, reachMul = 1f, postureMul = 1f;
    public bool twoHanded;
}

// the seam: any component on a fighter root that can say which weapon stats apply (WeaponSwap does)
public interface IGearWeaponSource { WeaponStats Weapon { get; } }

// What the combat code asks (like GearHooks): neutral (1) for a fighter that has no weapon source or with the gear system off.
public static class WeaponHooks
{
    static readonly WeaponStats neutral = new WeaponStats();
    public static WeaponStats Of(Component fighter)
    {
        if (fighter == null || !GearTable.Get().enabled) return neutral;
        var src = fighter.GetComponent<IGearWeaponSource>(); if (src == null) return neutral;
        return src.Weapon ?? neutral;
    }
    public static float DamageMul(Component fighter) { return Of(fighter).damageMul; }        // DuelFighter.DamageScaleNow
    public static float PostureDealtMul(Component fighter) { return Of(fighter).postureMul; }  // Duel.Resolve: posture a clean hit / a blocked hit puts on the defender
    public static float ReachMul(Component fighter) { return Of(fighter).reachMul; }           // DuelFighter.ReachScale: Duel.Resolve reach (and the AI reach reads)
    // multiplier on the TICK length of a move's windup / active / recovery: slower weapon (speedMul 0.75) = 1.33x the ticks. Exactly 1 for a neutral weapon.
    public static float TimeMul(Component fighter) { float s = Of(fighter).speedMul; return s > 0.05f && s != 1f ? 1f / s : 1f; }
}

public class WeaponSwap : MonoBehaviour, IGearWeaponSource
{
    // ---------------- statics: who holds what (read every LateUpdate by WeaponGrip / GripPose, so keep it a dictionary lookup) ----------------
    static readonly Dictionary<Transform, string> idByRoot = new Dictionary<Transform, string>();          // fighter root -> weapon id whose visual is on the socket
    static readonly Dictionary<string, string> cueByCharacter = new Dictionary<string, string>();          // character id -> cues.json weapon key
    public static string IdOf(Transform root) { string s; return root != null && idByRoot.TryGetValue(root, out s) ? s : ""; }
    public static string CueWeaponFor(string characterId) { string s; return !string.IsNullOrEmpty(characterId) && cueByCharacter.TryGetValue(characterId, out s) ? s : ""; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { idByRoot.Clear(); cueByCharacter.Clear(); GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        GearSlots.OnEquipped -= OnEq; GearSlots.OnUnequipped -= OnUneq; GearSlots.OnEquipped += OnEq; GearSlots.OnUnequipped += OnUneq;   // before any GearSlots.Start equips the loadout
        var args = Environment.GetCommandLineArgs();   // QA: `-weapon <gear item id>` equips that item on the player once the fight scene is up (screenshots / headless checks without beating the enemy)
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-weapon") { var go = new GameObject("WeaponSwapQa"); go.AddComponent<WeaponSwapQa>().itemId = args[i + 1]; }
    }

    static bool IsWeaponSlot(string slotId)
    {
        var C = GearTable.Get().weaponSwap; if (!C.enabled || C.slots == null) return false;
        return Array.IndexOf(C.slots, slotId) >= 0;
    }
    static void OnEq(GearSlots gs, GearTable.Item it, string slotId)
    {
        if (gs == null || it == null || !GearTable.Get().enabled || !IsWeaponSlot(slotId)) return;
        var ws = gs.GetComponent<WeaponSwap>(); if (ws == null) ws = gs.gameObject.AddComponent<WeaponSwap>();
        ws.gs = gs; ws.Equip(it);
    }
    static void OnUneq(GearSlots gs, GearTable.Item it, string slotId)
    {
        if (gs == null || !IsWeaponSlot(slotId)) return;
        var ws = gs.GetComponent<WeaponSwap>(); if (ws != null) ws.Clear();
    }

    // ---------------- instance ----------------
    GearSlots gs; GearTable.Item item; GearTable.WeaponDef def; WeaponStats stats;
    int serial; bool wantLoad; Transform root;
    Transform socket, instance, offNode, tipNode, trail, trailParent; Vector3 trailPos, trailScale; Quaternion trailRot;
    readonly List<GameObject> hidden = new List<GameObject>();
    TwoHandIK ik; bool shown; Transform clawNode;   // SOBEKCLAWS: clawNode = the ClawTip made for an unarmed weapon

    public WeaponStats Weapon { get { return stats; } }                  // null = the default sword (WeaponHooks maps that to neutral)
    public bool VisualShown { get { return shown; } }
    public string WeaponId { get { return shown && def != null ? def.id : ""; } }
    public bool Unarmed { get { return shown && def != null && def.unarmed; } }   // SOBEKCLAWS: the claws are the weapon (no glb on the socket)
    public bool TwoHanded { get { return shown && def != null && def.twoHanded; } }
    public float IkWeight { get { return def != null ? def.ikWeight : 0f; } }
    public float IkBlendSeconds { get { return def != null ? Mathf.Max(0.01f, def.ikBlendSeconds) : 0.15f; } }
    public Transform OffHandNode { get { return offNode; } }             // the weapon's off-hand target (grip_off); null until the visual is on the socket
    public Transform WeaponRoot { get { return instance; } }

    void Awake() { root = transform.root; }
    void OnEnable() { if (wantLoad && instance == null) StartLoad(); }
    void OnDestroy() { Clear(); }

    public void Equip(GearTable.Item it)
    {
        Clear();
        if (root == null) root = transform.root;
        if (it == null || it.weapon == null || string.IsNullOrEmpty(it.weapon.id)) return;   // not a weapon item: GearSlots keeps its stats, the sword stays
        item = it; def = it.weapon;
        stats = new WeaponStats { id = def.id, damageMul = def.damageMul, speedMul = def.speedMul, reachMul = def.reachMul, postureMul = def.postureMul, twoHanded = def.twoHanded };
        string cid = gs != null ? gs.CharacterId : ""; if (cid.Length > 0 && def.cueWeapon.Length > 0) cueByCharacter[cid] = def.cueWeapon;
        GearTable.Log(name + " weapon " + def.id + ": damage x" + def.damageMul + " speed x" + def.speedMul + " reach x" + def.reachMul + " posture x" + def.postureMul + (def.twoHanded ? " two-handed" : ""));
        if (def.unarmed) { UnarmedOn(); return; }   // SOBEKCLAWS
        wantLoad = true; StartLoad();
    }

    // SOBEKCLAWS: an item with weapon.unarmed has no glb. The template weapon on the socket goes dark exactly as a swapped-in weapon does (keepChildren stay), a ClawTip node is made on weapon.tipBone
    // at weapon.tipOffsetM (metres in the bone frame) and the swing trail follows it. Nothing loads, so there is nothing to warn about; Clear() puts everything back.
    void UnarmedOn()
    {
        var C = GearTable.Get().weaponSwap; Transform sk = gs != null ? gs.Skeleton : transform;
        if (root == null) root = transform.root;
        Transform sock = FindNamed(sk, C.socket, false);
        hidden.Clear();
        if (sock != null)
            for (int i = 0; i < sock.childCount; i++)
            {
                var ch = sock.GetChild(i); if (InList(C.keepChildren, ch.name)) continue;
                if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); hidden.Add(ch.gameObject); }
            }
        Transform bone = FindNamed(sk, def.tipBone, false);
        if (bone == null) GearTable.Warn("unarmed weapon '" + def.id + "': no bone '" + def.tipBone + "' on " + name + " (no ClawTip node, the trail stays on the socket)");
        else
        {
            float hs = Mathf.Max(0.0001f, bone.lossyScale.x); var o = def.tipOffsetM;
            clawNode = new GameObject("ClawTip").transform; clawNode.SetParent(bone, false);
            clawNode.localPosition = o != null && o.Length >= 3 ? new Vector3(o[0], o[1], o[2]) / hs : Vector3.zero; clawNode.localRotation = Quaternion.identity; clawNode.localScale = Vector3.one;
            tipNode = clawNode;
            trail = sock != null ? FindNamed(sock, C.trailNode, true) : null;
            if (trail != null) { trailParent = trail.parent; trailPos = trail.localPosition; trailRot = trail.localRotation; trailScale = trail.localScale; trail.SetParent(clawNode, false); trail.localPosition = Vector3.zero; trail.localRotation = Quaternion.identity; trail.localScale = Vector3.one; }
        }
        socket = sock; shown = true; idByRoot[root] = def.id;   // from this frame WeaponGrip / GripPose pick the rows of weapon id "claws" (none = the socket as the scene has it)
        GearTable.Log("weapon " + def.id + " on " + name + ": unarmed, template weapon hidden (" + hidden.Count + " children)" + (tipNode != null ? ", claw tip node '" + tipNode.name + "'" : ""));
    }

    void StartLoad() { if (!isActiveAndEnabled || item == null) return; wantLoad = false; StartCoroutine(Load(serial, item)); }

    // back to the default sword: weapon stats off, the weapon glb gone, the hidden sword and the trail restored. Safe to call twice.
    public void Clear()
    {
        serial++; wantLoad = false;
        if (trail != null && trailParent != null && trail.parent != trailParent) { trail.SetParent(trailParent, false); trail.localPosition = trailPos; trail.localRotation = trailRot; trail.localScale = trailScale; }
        if (clawNode != null) { Destroy(clawNode.gameObject); clawNode = null; }   // SOBEKCLAWS
        if (instance != null) { instance.gameObject.SetActive(false); Destroy(instance.gameObject); }   // GearPiece.OnDestroy disposes the glTFast import and the HDRP/Lit copies
        for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null) hidden[i].SetActive(true);
        hidden.Clear();
        if (ik != null) ik.owner = null;
        string cid = gs != null ? gs.CharacterId : ""; if (cid.Length > 0) cueByCharacter.Remove(cid);
        if (root != null) idByRoot.Remove(root);
        instance = null; offNode = null; tipNode = null; trail = null; trailParent = null; socket = null; item = null; def = null; stats = null; shown = false;
    }

    static Transform FindNamed(Transform top, string n, bool directOnly)
    {
        if (top == null || string.IsNullOrEmpty(n)) return null;
        if (directOnly) { for (int i = 0; i < top.childCount; i++) if (top.GetChild(i).name == n) return top.GetChild(i); return null; }
        foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
        return null;
    }
    static bool InList(string[] l, string n) { if (l != null) for (int i = 0; i < l.Length; i++) if (l[i] == n) return true; return false; }

    IEnumerator Load(int my, GearTable.Item it)
    {
        var C = GearTable.Get().weaponSwap; var wd = it.weapon;
        Transform sk = gs != null ? gs.Skeleton : transform;
        Transform sock = FindNamed(sk, C.socket, false);
        if (sock == null) { GearTable.Warn("weapon '" + wd.id + "': no '" + C.socket + "' on " + name + " (sword stays)"); yield break; }
        GameObject inst = null; IDisposable res = null; string info = "";
        yield return GearAssetLoader.Load(it, it.glb, true, sock, (g, r, i) => { inst = g; res = r; info = i; });
        if (this == null || my != serial) { if (res != null) res.Dispose(); if (inst != null) Destroy(inst); yield break; }   // unequipped / replaced while loading
        if (inst == null) { GearTable.Warn("weapon '" + wd.id + "' has no visual (" + info + "): stats apply, the sword stays"); yield break; }

        // reference renderer = the sword that is on the socket now (rendering layer / shadows must match the fighter's other renderers)
        Renderer refR = null;
        foreach (var r in sock.GetComponentsInChildren<Renderer>(true)) if (!r.transform.IsChildOf(inst.transform) && !(r is TrailRenderer)) { refR = r; break; }
        inst.name = "Weapon_" + it.id; var t = inst.transform; t.SetParent(sock, false); t.localPosition = Vector3.zero; t.localRotation = Quaternion.identity; t.localScale = Vector3.one;
        foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var tag = inst.GetComponent<GearPiece>(); if (tag == null) tag = inst.AddComponent<GearPiece>();   // BoneMap / the layer + mask passes of GearSlots skip a GearPiece subtree; OnDestroy frees the import
        tag.itemId = it.id; tag.slotId = "weapon"; tag.resources = res;
        var rends = new List<Renderer>(inst.GetComponentsInChildren<Renderer>(true));
        tag.madeMaterials = GearMaterials.Convert(rends, it);   // glTFast -> HDRP/Lit + gear.json material rows
        int layer = sock.gameObject.layer;
        foreach (var x in inst.GetComponentsInChildren<Transform>(true)) x.gameObject.layer = layer;
        foreach (var r in rends)
        {
            r.shadowCastingMode = refR != null ? refR.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = refR != null ? refR.receiveShadows : true;
            if (refR != null) r.renderingLayerMask = refR.renderingLayerMask;
        }

        // grip node onto the socket origin (a no-op for the pitborn_maul glb, whose grip node IS the origin)
        var grip = FindNamed(t, wd.gripNode, false);
        if (grip != null && grip != t) t.localPosition -= sock.InverseTransformPoint(grip.position);
        offNode = FindNamed(t, wd.offHandNode, false); tipNode = FindNamed(t, wd.tipNode, false);

        // the default weapon goes dark (its LOD children), except keepChildren; the swing trail follows the new weapon's tip
        hidden.Clear();
        for (int i = 0; i < sock.childCount; i++)
        {
            var ch = sock.GetChild(i); if (ch == t || InList(C.keepChildren, ch.name)) continue;
            if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); hidden.Add(ch.gameObject); }
        }
        trail = FindNamed(sock, C.trailNode, true);
        if (trail != null && tipNode != null)
        {
            trailParent = trail.parent; trailPos = trail.localPosition; trailRot = trail.localRotation; trailScale = trail.localScale;
            trail.SetParent(tipNode, false); trail.localPosition = Vector3.zero; trail.localRotation = Quaternion.identity; trail.localScale = Vector3.one;
        }
        else trail = null;

        socket = sock; instance = t; shown = true;
        if (root == null) root = transform.root;
        idByRoot[root] = wd.id;   // from this frame WeaponGrip / GripPose pick this weapon's rows
        if (wd.twoHanded)
        {
            if (ik == null) ik = sk.GetComponent<TwoHandIK>();
            if (ik == null) ik = sk.gameObject.AddComponent<TwoHandIK>();
            ik.owner = this;
        }
        GearTable.Log("weapon " + wd.id + " on " + name + ": " + info + (offNode != null ? ", off-hand node '" + offNode.name + "'" : ", no off-hand node") + (tipNode != null ? ", tip '" + tipNode.name + "'" : ""));
    }
}

// `-weapon <item id>`: waits for the player's GearSlots (up to ~10 s), equips the item once, then removes itself. Off without the flag.
public class WeaponSwapQa : MonoBehaviour
{
    public string itemId = "";
    IEnumerator Start()
    {
        for (int f = 0; f < 600; f++)
        {
            yield return null;
            foreach (var gs in FindObjectsByType<GearSlots>(FindObjectsSortMode.None))
                if (gs != null && Roster.IsPlayer(gs.CharacterId)) { gs.Equip(itemId); Debug.Log("[WeaponSwap] -weapon " + itemId + " equipped on " + gs.name); Destroy(gameObject); yield break; }
        }
        Debug.LogWarning("[WeaponSwap] -weapon " + itemId + ": no player GearSlots found"); Destroy(gameObject);
    }
}
