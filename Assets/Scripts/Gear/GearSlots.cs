using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// GEAR SLOTS (lane GEAR v79). One per wearer (add it to the fighter root; it finds the skeleton through Fighter.anim). Data: Assets/Data/gear.json, loadout: roster.json row "loadout".
//  Equip(itemId[, slotId]) / Unequip(slotId): by id, from code, the loot pickup or a UI. Equipping always works logically (stats apply, events fire);
//  the VISUAL loads in the background (layer mesh already on the wearer, Resources prefab, imported asset, or a glb through glTFast), its skinned meshes are rebound to the wearer's bones by
//  bone name, and only THEN is the covered body zone masked. A missing / broken asset = no visual, no masking, one warning: the wearer never ends up with a hole.
//  Stats: IGearStatSource, read by GearHooks from Duel.Resolve / PoiseTable / stamina regen.
//  GEAR2 (v79-80): an item with glbMirror holds a PAIR in its one slot (right glb + left glb: two pieces, one slot, equipped / removed together; masking only when both pieces resolved, so a missing half never leaves a hole).
//  Materials: every glb piece gets gear.json material rows (GearMaterials.Convert with the item), and so does a wearer Layer_* mesh the first time its item is equipped (Steel / Leather / Heraldry never render flat or pink).
//  hidesLayers: an item that resolved a visual switches the named wearer layer meshes off (the tabard hides Layer_Tunic); when it goes, a layer that another equipped item shows comes back.
public class GearSlots : MonoBehaviour, IGearStatSource
{
    public string characterId = "";          // "" = Roster.IdOf(transform)
    public bool applyLoadout = true;         // equip the roster.json loadout in Start
    public Transform skeletonRoot;           // "" = Fighter.anim.transform, else this transform

    class SlotState { public string slotId; public GearTable.Item item; public int serial; public bool loading, visualOk, maskOk, wasTried; public List<GameObject> pieces = new List<GameObject>(); public List<Renderer> renderers = new List<Renderer>(); public List<GameObject> owned = new List<GameObject>(); public bool layerMode; public Renderer layerRenderer; }
    // one glb of an item (the primary, or the mirror of a pair) while it loads
    class PieceResult { public bool ok, abort; public string info = ""; public GameObject root; public List<Renderer> renderers = new List<Renderer>(); public List<GameObject> owned = new List<GameObject>(); }

    readonly Dictionary<string, SlotState> slots = new Dictionary<string, SlotState>();
    readonly HashSet<Renderer> styledLayers = new HashSet<Renderer>();      // wearer layer meshes whose materials already went through GearMaterials
    readonly List<Material> layerMaterials = new List<Material>();          // the clones made for them (destroyed with the wearer)
    readonly GearMask mask = new GearMask();
    int serialCounter; bool managesLayers;

    // events: wearer, item, slot id. OnVisual fires once per equip when the visual resolved (ok false = no visual: missing asset or unbindable)
    public static event Action<GearSlots, GearTable.Item, string> OnEquipped, OnUnequipped;
    public static event Action<GearSlots, GearTable.Item, string, bool> OnVisual;

    public string CharacterId { get { return characterId.Length > 0 ? characterId : Roster.IdOf(transform); } }
    public Transform Skeleton
    {
        get
        {
            if (skeletonRoot != null) return skeletonRoot;
            var f = GetComponent<Fighter>(); if (f != null && f.anim != null) return f.anim.transform;
            var a = GetComponentInChildren<Animator>(); return a != null ? a.transform : transform;
        }
    }

    // no scene edit needed: after the scene loads, the player fighter and every fighter whose roster row has a loadout get a GearSlots (gear.json enabled only)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!GearTable.Get().enabled) return;
        foreach (var f in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (f == null || f.GetComponent<GearSlots>() != null) continue;
            string id = f.CharacterId;
            if (Roster.IsPlayer(id) || GearTable.Loadout(id) != null) f.gameObject.AddComponent<GearSlots>();
        }
    }

    // ---------------- queries ----------------
    public string ItemIn(string slotId) { SlotState s; return slots.TryGetValue(slotId, out s) ? s.item.id : ""; }
    public bool VisualOk(string slotId) { SlotState s; return slots.TryGetValue(slotId, out s) && s.visualOk; }
    public bool Has(string itemId) { foreach (var kv in slots) if (kv.Value.item.id == itemId) return true; return false; }
    public List<string> EquippedItemIds() { var l = new List<string>(); foreach (var kv in slots) l.Add(kv.Value.item.id); return l; }
    public ulong CoveredZones { get; private set; }
    public int LastMaskedTriangles { get { return mask.lastHiddenTris; } }

    public GearStats Stats
    {
        get
        {
            var G = GearTable.Get(); var t = new GearStats(); if (!G.enabled) return t;
            foreach (var kv in slots)
            {
                var s = kv.Value; if (!s.visualOk && !G.statsWithoutAsset) continue;
                t.armor += s.item.stats.armor; t.poise += s.item.stats.poise; t.weight += s.item.stats.weight;
            }
            return t;
        }
    }

    // ---------------- equip / unequip ----------------
    // slotId optional: an exact slot id or a group; "" = the item's own slot. Returns false when the item or a slot for it does not exist (nothing changes).
    public bool Equip(string itemId, string slotId = "")
    {
        var G = GearTable.Get(); if (!G.enabled) return false;
        var it = GearTable.Find(itemId); if (it == null) { GearTable.Warn("Equip: unknown item '" + itemId + "'"); return false; }
        var cands = GearTable.SlotsFor(string.IsNullOrEmpty(slotId) ? it.slot : slotId);
        if (cands.Count == 0) { GearTable.Warn("Equip: item '" + itemId + "' has no valid slot ('" + (string.IsNullOrEmpty(slotId) ? it.slot : slotId) + "')"); return false; }
        string sid = cands[0];
        for (int i = 0; i < cands.Count; i++) if (!slots.ContainsKey(cands[i])) { sid = cands[i]; break; }   // first empty slot of the group, else replace the first
        if (slots.ContainsKey(sid) && slots[sid].item.id == itemId) return true;   // already worn
        RemoveSlot(sid, false);

        var s = new SlotState { slotId = sid, item = it, serial = ++serialCounter };
        slots[sid] = s;
        GearTable.Log(name + " equips " + it.id + " in " + sid);
        if (OnEquipped != null) OnEquipped(this, it, sid);
        var def = GearTable.SlotOf(sid);
        if (def != null && def.visual && it.HasVisualSource) TryStartLoad(s); else Resolved(s, false, def != null && !def.visual ? "data-only slot" : "item has no visual", false);
        return true;
    }

    public bool Unequip(string slotId)
    {
        if (!slots.ContainsKey(slotId)) return false;
        RemoveSlot(slotId, true); return true;
    }

    void RemoveSlot(string sid, bool refresh)
    {
        SlotState s; if (!slots.TryGetValue(sid, out s)) return;
        slots.Remove(sid);
        if (s.layerMode) { if (s.layerRenderer != null) s.layerRenderer.gameObject.SetActive(false); }
        else { foreach (var p in s.pieces) if (p != null) Destroy(p); foreach (var g in s.owned) if (g != null) Destroy(g); s.pieces.Clear(); s.owned.Clear(); }
        if (OnUnequipped != null) OnUnequipped(this, s.item, sid);
        if (refresh) RefreshVisuals();
    }

    // ---------------- visual ----------------
    void Start()
    {
        if (!GearTable.Get().enabled || !applyLoadout) return;
        var lo = GearTable.Loadout(CharacterId); if (lo == null) return;
        managesLayers = GearTable.Get().hideUnequippedLayers;   // this wearer has a loadout: its layer meshes now follow the slots
        for (int i = 0; i < lo.Length; i++) Equip(lo[i]);
        RefreshVisuals();
    }
    void OnEnable() { foreach (var kv in slots) if (!kv.Value.visualOk && !kv.Value.loading && !kv.Value.wasTried) { var def = GearTable.SlotOf(kv.Key); if (def != null && def.visual && kv.Value.item.HasVisualSource) TryStartLoad(kv.Value); } }
    void OnDestroy() { mask.Dispose(); foreach (var m in layerMaterials) if (m != null) Destroy(m); layerMaterials.Clear(); }

    void TryStartLoad(SlotState s)
    {
        if (!isActiveAndEnabled) return;   // OnEnable starts it later
        s.loading = true; s.wasTried = true; StartCoroutine(LoadVisual(s));
    }

    bool IsCurrent(SlotState s) { SlotState c; return slots.TryGetValue(s.slotId, out c) && c == s; }

    IEnumerator LoadVisual(SlotState s)
    {
        var it = s.item;
        if (it.layer.Length > 0)
        {
            // a layer mesh that already sits on the wearer (Layer_Helmet ...): equipping = showing it
            Renderer lr = FindLayer(it.layer);
            s.loading = false;
            if (lr == null) { Resolved(s, false, "wearer has no layer mesh '" + it.layer + "'"); yield break; }
            s.layerMode = true; s.layerRenderer = lr; StyleLayer(lr, it); lr.gameObject.SetActive(true); s.renderers.Add(lr);
            s.maskOk = true;
            Resolved(s, true, "layer " + it.layer); yield break;
        }

        // GEAR2: one piece per glb. A pair loads the primary first, then the mirror (each missing / broken half is reported separately).
        var results = new List<PieceResult>();
        var r1 = new PieceResult(); results.Add(r1);
        yield return LoadPiece(s, it.glb, it.part, "", true, r1);
        if (r1.abort) { Cleanup(results); yield break; }
        if (it.IsPair)
        {
            var r2 = new PieceResult(); results.Add(r2);
            yield return LoadPiece(s, it.glbMirror, it.partMirror.Length > 0 ? it.partMirror : it.part, "_mirror", false, r2);
            if (r2.abort) { Cleanup(results); yield break; }
        }
        s.loading = false;

        int ok = 0; var why = new List<string>(); var bad = new List<string>();
        foreach (var r in results)
        {
            if (r.ok) { ok++; s.pieces.Add(r.root); s.owned.AddRange(r.owned); s.renderers.AddRange(r.renderers); why.Add(r.info); } else bad.Add(r.info);
        }
        if (ok == 0) { s.maskOk = false; Resolved(s, false, string.Join("; ", bad.ToArray())); yield break; }
        s.maskOk = bad.Count == 0;   // a pair with a missing half is shown (one side) but its zones are NOT masked: no hole on the side that has no plate
        if (bad.Count > 0) GearTable.Warn("'" + it.id + "' pair incomplete (" + string.Join("; ", bad.ToArray()) + "): the other half is shown alone, no body masking");
        Resolved(s, true, string.Join(" | ", why.ToArray()));
    }

    // loads, binds and styles ONE glb of an item. res.abort = the slot was unequipped / replaced while loading (the piece made here is already destroyed).
    IEnumerator LoadPiece(SlotState s, string glb, string part, string suffix, bool primary, PieceResult res)
    {
        var it = s.item; Transform sk = Skeleton;
        var piece = new GameObject("Gear_" + it.id + suffix);
        piece.transform.SetParent(sk, false);
        var tag = piece.AddComponent<GearPiece>(); tag.itemId = it.id; tag.slotId = s.slotId;
        GameObject inst = null; IDisposable resources = null; string info = "";
        yield return GearAssetLoader.Load(it, glb, primary, piece.transform, (g, r, i) => { inst = g; resources = r; info = i; });
        if (this == null || !IsCurrent(s)) { if (resources != null) resources.Dispose(); if (piece != null) Destroy(piece); res.abort = true; yield break; }   // unequipped / replaced while loading
        tag.resources = resources;
        if (inst == null) { Destroy(piece); res.info = info; yield break; }

        var rep = GearAttach.Bind(inst, piece.transform, GearAttach.BoneMap(sk), it, s.slotId, part);
        if (rep.skinned + rep.rigid == 0) { Destroy(piece); res.info = "no usable mesh in " + info + (part.Length > 0 ? " (part '" + part + "')" : ""); yield break; }
        if (rep.MissingFrac > GearTable.Get().maxMissingBoneFrac)
        {
            foreach (var g in rep.owned) if (g != null) Destroy(g);
            Destroy(piece); res.info = "rig mismatch: " + rep.missing + " of " + rep.bones + " bones not on the wearer (" + string.Join(",", rep.missingNames.ToArray()) + ")"; yield break;
        }
        tag.madeMaterials = GearMaterials.Convert(rep.renderers, it);   // glTFast shader-graph materials -> HDRP/Lit, then gear.json material rows
        res.ok = true; res.root = piece; res.owned = rep.owned; res.renderers = rep.renderers;
        res.info = info + " (" + rep.skinned + " skinned, " + rep.rigid + " rigid, " + rep.missing + "/" + rep.bones + " bones off-rig)";
    }

    void Cleanup(List<PieceResult> results)
    {
        foreach (var r in results) { if (r.root != null) Destroy(r.root); foreach (var g in r.owned) if (g != null) Destroy(g); }
    }

    // wearer layer mesh (Layer_Tunic / Greaves / Bracers / Helmet): once per renderer, its materials get the swap (if glTFast) and the gear.json rows by material name. Clones are owned here.
    void StyleLayer(Renderer lr, GearTable.Item it)
    {
        if (lr == null || !GearTable.Get().materials.styleLayers || !styledLayers.Add(lr)) return;
        layerMaterials.AddRange(GearMaterials.Convert(new List<Renderer> { lr }, it));
    }

    void Resolved(SlotState s, bool ok, string why, bool warn = true)
    {
        s.visualOk = ok; if (!ok) s.maskOk = false;
        if (!ok && warn) GearTable.Warn("'" + s.item.id + "' has no visual (" + why + "): equipped for stats only, no body masking");
        else if (ok) GearTable.Log(s.item.id + " visual ok: " + why);
        if (OnVisual != null) OnVisual(this, s.item, s.slotId, ok);
        RefreshVisuals();
    }

    Renderer FindLayer(string layerName)
    {
        foreach (var r in Skeleton.GetComponentsInChildren<Renderer>(true))
            if (r.name == layerName && r.GetComponentInParent<GearPiece>() == null) return r;
        return null;
    }

    // recompute (a) which wearer layer meshes show, (b) the covered body zones, (c) the body mask. Cheap; runs on every equip / unequip / visual result.
    readonly HashSet<Renderer> hiddenByGear = new HashSet<Renderer>();   // JOANHAIR v1: renderers an equipped item hid by name (hidesLayers) come back when no item hides them any more
    void RefreshVisuals()
    {
        var G = GearTable.Get(); if (!G.enabled) return;
        // (a) layers: owned layer meshes follow their slot; any layer an equipped, visible item hides is off
        var hide = new HashSet<string>(); ulong zones = 0UL; var gearRenderers = new HashSet<Renderer>();
        foreach (var kv in slots)
        {
            var s = kv.Value; if (!s.visualOk) continue;
            foreach (var h in s.item.hidesLayers) hide.Add(h);
            if (s.maskOk) zones |= GearTable.MaskOf(s.item);   // a pair with a missing half masks nothing
            foreach (var r in s.renderers) if (r != null) gearRenderers.Add(r);
        }
        var ownedLayers = new HashSet<string>(); foreach (var it in G.items) if (it != null && it.layer.Length > 0) ownedLayers.Add(it.layer);
        foreach (var r in Skeleton.GetComponentsInChildren<Renderer>(true))
        {
            if (r.GetComponentInParent<GearPiece>() != null || r.GetComponent<GearPiece>() != null) continue;
            bool isLayer = ownedLayers.Contains(r.name), equipped = gearRenderers.Contains(r);
            if (hide.Contains(r.name)) { r.gameObject.SetActive(false); hiddenByGear.Add(r); }   // JOANHAIR v1
            else if (isLayer && managesLayers && !equipped) { r.gameObject.SetActive(false); hiddenByGear.Remove(r); }
            else if (hiddenByGear.Remove(r)) r.gameObject.SetActive(true);   // JOANHAIR v1: Joan_Hair returns on unequip
            else if (isLayer && equipped && !r.gameObject.activeSelf) r.gameObject.SetActive(true);   // GEAR2: a layer another item had hidden comes back when that item goes
        }
        // (b)(c) body = the wearer's skinned meshes that are not gear and not an equipped layer
        CoveredZones = zones;
        var bodies = new List<SkinnedMeshRenderer>();
        foreach (var r in Skeleton.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r.GetComponentInParent<GearPiece>() != null || gearRenderers.Contains(r)) continue;
            bodies.Add(r);
        }
        mask.Apply(bodies, zones);
    }
}
