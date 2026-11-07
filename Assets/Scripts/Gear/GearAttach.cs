using System;
using System.Collections.Generic;
using UnityEngine;

// Marks everything gear created on a wearer (the piece root and any rigid part parented to a bone), so bone lookups and masking never mistake it for the wearer's own rig.
public class GearPiece : MonoBehaviour
{
    public string itemId = "", slotId = "";
    public IDisposable resources;   // glTFast import (meshes, textures) of a runtime-loaded glb: disposed with the piece
    public List<Material> madeMaterials;   // HDRP/Lit copies made by GearMaterials: destroyed with the piece
    void OnDestroy() { if (resources != null) { try { resources.Dispose(); } catch (Exception) { } resources = null; } if (madeMaterials != null) { foreach (var m in madeMaterials) if (m != null) Destroy(m); madeMaterials = null; } }
}

// Rebinds a gear item's skinned meshes to the WEARER's skeleton by bone name (every character is on the shared 106-joint psyche-106 rig, mixamorig:* names).
public static class GearAttach
{
    public class Report
    {
        public int skinned, rigid, bones, missing; public List<string> missingNames = new List<string>();
        public List<Renderer> renderers = new List<Renderer>(); public List<GameObject> owned = new List<GameObject>();
        public float MissingFrac { get { return bones <= 0 ? 0f : (float)missing / bones; } }
    }

    // every bone of the wearer, by exact name and by normalised name (GearTable.Normalize). Skips gear pieces.
    public static Dictionary<string, Transform> BoneMap(Transform root)
    {
        var map = new Dictionary<string, Transform>(); if (root == null) return map;
        var stack = new Stack<Transform>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var t = stack.Pop(); if (t != root && t.GetComponent<GearPiece>() != null) continue;
            if (t != root) { map.TryAdd(t.name, t); map.TryAdd(GearTable.Normalize(t.name), t); }
            for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
        }
        return map;
    }
    public static Transform Find(Dictionary<string, Transform> map, string name)
    {
        if (map == null || string.IsNullOrEmpty(name)) return null;
        Transform t; if (map.TryGetValue(name, out t)) return t;
        return map.TryGetValue(GearTable.Normalize(name), out t) ? t : null;
    }

    // inst = the freshly instantiated gear prefab / glb scene (a temporary hierarchy under pieceRoot). On return its skinned meshes hang under pieceRoot, bound to the wearer's bones,
    // rigid meshes hang under the matching wearer bone, and inst is destroyed (its own copy of the skeleton goes with it).
    // partFilter (GEAR2): the node name to keep for THIS piece; null = item.part (the mirror glb of a pair may name its node differently: item.partMirror)
    public static Report Bind(GameObject inst, Transform pieceRoot, Dictionary<string, Transform> map, GearTable.Item item, string slotId, string partFilter = null)
    {
        var rep = new Report(); if (inst == null) return rep;
        Transform hips = Find(map, Roster.BoneName(pieceRoot, "Hips")) ?? Find(map, "Hips"); /* HCAUDIT1 v104 */
        string part = partFilter ?? item.part;
        bool all = string.IsNullOrEmpty(part);
        var skinned = new List<SkinnedMeshRenderer>(inst.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        var rigid = new List<MeshRenderer>(inst.GetComponentsInChildren<MeshRenderer>(true));
        skinned.RemoveAll(s => !all && s.name != part);
        rigid.RemoveAll(m => !all && m.name != part);
        var oldBones = new HashSet<Transform>();

        foreach (var smr in skinned)
        {
            var src = smr.bones; var dst = new Transform[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var b = src[i]; if (b == null) continue;
                oldBones.Add(b); rep.bones++;
                Transform t = Find(map, b.name);
                if (t == null)
                {
                    rep.missing++; if (rep.missingNames.Count < 8) rep.missingNames.Add(b.name);
                    for (var p = b.parent; p != null && t == null; p = p.parent) t = Find(map, p.name);   // nearest ancestor the wearer has
                }
                dst[i] = t != null ? t : hips;
            }
            Transform root = smr.rootBone != null ? Find(map, smr.rootBone.name) : null;
            smr.bones = dst; smr.rootBone = root != null ? root : hips;
            smr.transform.SetParent(pieceRoot, false);
            smr.transform.localPosition = Vector3.zero; smr.transform.localRotation = Quaternion.identity; smr.transform.localScale = Vector3.one;   // skinning is bone driven: the mesh node's own transform does not move the vertices
            rep.skinned++; rep.renderers.Add(smr);
        }

        foreach (var mr in rigid)
        {
            Transform anc = null, wb = null;
            for (var p = mr.transform.parent; p != null && p != inst.transform.parent; p = p.parent)
            {
                var t = Find(map, p.name); if (t != null) { anc = p; wb = t; break; }
            }
            if (wb == null) { GearTable.Warn("rigid part '" + mr.name + "' of '" + item.id + "' has no matching parent bone on the wearer: dropped"); continue; }
            Vector3 pos = anc.InverseTransformPoint(mr.transform.position);
            Quaternion rot = Quaternion.Inverse(anc.rotation) * mr.transform.rotation;
            Vector3 ls = mr.transform.lossyScale, la = anc.lossyScale;
            Vector3 scl = new Vector3(Mathf.Abs(la.x) > 1e-6f ? ls.x / la.x : 1f, Mathf.Abs(la.y) > 1e-6f ? ls.y / la.y : 1f, Mathf.Abs(la.z) > 1e-6f ? ls.z / la.z : 1f);
            var go = mr.gameObject; go.transform.SetParent(wb, false);
            go.transform.localPosition = pos; go.transform.localRotation = rot; go.transform.localScale = scl;
            var tag = go.GetComponent<GearPiece>() ?? go.AddComponent<GearPiece>(); tag.itemId = item.id; tag.slotId = slotId;
            rep.rigid++; rep.renderers.Add(mr); rep.owned.Add(go);
        }

        // leftover bone transforms that were moved out together with a skinned mesh node: remove the ones without a renderer
        foreach (var b in oldBones)
            if (b != null && b.IsChildOf(pieceRoot) && b != pieceRoot && b.GetComponentInChildren<Renderer>(true) == null) UnityEngine.Object.Destroy(b.gameObject);
        UnityEngine.Object.Destroy(inst);
        return rep;
    }
}
