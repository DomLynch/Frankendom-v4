using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

// BODY-ZONE MASKING (lane GEAR v79). Hides the parts of the wearer's body that gear covers so nothing pokes through.
// Mode "triangles" (default): every skinned vertex gets its zone from its bone weights (gear.json boneZones: bone name -> zone). A vertex is "inside" the covered zones when at least
//   mask.vertexThreshold of its weight sits on bones of those zones; triangles with all (or the majority / any, mask.triangleRule) of their vertices inside are dropped from a CLONE of the mesh.
//   Works on ANY body on the 106-joint rig with no authored mask: the skinning IS the zone map. Needs a readable mesh; the original mesh is put back when the mask clears.
// Mode "alpha": no mesh change, the zone bit mask is written to a material property block (mask.shaderProp) for a shader that clips on it (no such shader ships yet).
// One GearMask per wearer. Meshes it builds are cached per (source mesh, zone mask) and destroyed with the wearer.
public class GearMask
{
    readonly Dictionary<SkinnedMeshRenderer, Mesh> orig = new Dictionary<SkinnedMeshRenderer, Mesh>();
    readonly Dictionary<(Mesh, ulong), Mesh> cache = new Dictionary<(Mesh, ulong), Mesh>();
    readonly List<Mesh> made = new List<Mesh>();
    readonly Dictionary<Mesh, int> cut = new Dictionary<Mesh, int>();   // triangles removed per built clone
    MaterialPropertyBlock mpb;
    public int lastHiddenTris;   // for logs / proofs: triangles removed by the last Apply across all bodies

    // bodies = the wearer's own skinned meshes (never gear). zones 0 = restore everything.
    public void Apply(IEnumerable<SkinnedMeshRenderer> bodies, ulong zones)
    {
        var C = GearTable.Get().mask; lastHiddenTris = 0;
        foreach (var smr in bodies)
        {
            if (smr == null) continue;
            if (!orig.ContainsKey(smr)) orig[smr] = smr.sharedMesh;
            if (C.mode == "alpha") { SetAlpha(smr, C.shaderProp, zones); continue; }
            Mesh o = orig[smr]; if (o == null) continue;
            if (zones == 0UL || C.mode == "off") { if (smr.sharedMesh != o) smr.sharedMesh = o; continue; }
            Mesh m;
            var key = (o, zones);
            if (!cache.TryGetValue(key, out m)) { m = Build(smr, o, zones, C); cache[key] = m; if (m != null) made.Add(m); }
            if (m != null) { smr.sharedMesh = m; int c; if (cut.TryGetValue(m, out c)) lastHiddenTris += c; }
            else if (smr.sharedMesh != o) smr.sharedMesh = o;
        }
    }

    // put every original mesh back and free the clones
    public void Dispose()
    {
        foreach (var kv in orig) if (kv.Key != null && kv.Value != null && kv.Key.sharedMesh != kv.Value) kv.Key.sharedMesh = kv.Value;
        foreach (var m in made) if (m != null) Object.Destroy(m);
        made.Clear(); cache.Clear(); orig.Clear(); cut.Clear();
    }

    void SetAlpha(SkinnedMeshRenderer smr, string prop, ulong zones)
    {
        if (mpb == null) mpb = new MaterialPropertyBlock();
        smr.GetPropertyBlock(mpb); mpb.SetFloat(prop, (float)(uint)zones); smr.SetPropertyBlock(mpb);   // float holds 24 bits exactly: fine for <= 24 zones
    }

    Mesh Build(SkinnedMeshRenderer smr, Mesh o, ulong zones, GearTable.MaskCfg C)
    {
        if (!o.isReadable) { GearTable.Warn("body mesh '" + o.name + "' is not readable (enable Read/Write on the import): no masking on it"); return null; }
        var bones = smr.bones; int nb = bones.Length, vc = o.vertexCount;
        NativeArray<byte> bpv = o.GetBonesPerVertex(); NativeArray<BoneWeight1> bw = o.GetAllBoneWeights();
        if (bw.Length == 0 || bpv.Length != vc) return null;   // not skinned: nothing to mask by
        var boneIn = new bool[nb];
        for (int i = 0; i < nb; i++) { int z = bones[i] != null ? GearTable.ZoneOfBone(bones[i].name) : -1; boneIn[i] = z >= 0 && ((zones >> z) & 1UL) != 0UL; }

        var inside = new bool[vc]; int k = 0, hidden = 0;
        for (int v = 0; v < vc; v++)
        {
            int n = bpv[v]; float w = 0f;
            for (int j = 0; j < n; j++) { var b = bw[k + j]; if (b.boneIndex >= 0 && b.boneIndex < nb && boneIn[b.boneIndex]) w += b.weight; }
            k += n; inside[v] = w >= C.vertexThreshold; if (inside[v]) hidden++;
        }
        if (hidden == 0) return null;

        int removed = 0;
        int need = C.triangleRule == "any" ? 1 : C.triangleRule == "majority" ? 2 : 3;
        var m = Object.Instantiate(o); m.name = o.name + "_gear" + zones;
        var tri = new List<int>();
        for (int s = 0; s < o.subMeshCount; s++)
        {
            if (o.GetTopology(s) != MeshTopology.Triangles) continue;
            var idx = o.GetIndices(s); tri.Clear();
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                int c = (inside[idx[i]] ? 1 : 0) + (inside[idx[i + 1]] ? 1 : 0) + (inside[idx[i + 2]] ? 1 : 0);
                if (c >= need) { removed++; continue; }
                tri.Add(idx[i]); tri.Add(idx[i + 1]); tri.Add(idx[i + 2]);
            }
            m.SetTriangles(tri, s, false);
        }
        cut[m] = removed;
        GearTable.Log("mask " + o.name + " zones=" + zones + ": " + hidden + " / " + vc + " vertices inside");
        return m;
    }
}
