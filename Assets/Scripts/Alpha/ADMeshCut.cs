using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ALPHA lane B: runtime mesh cuts for finishers (decapitation, split crown, opened, dismember, hamstring; runThrough = blood only).
// Port of the Armagedom finisher cuts (finisher-cuts.js + pitbornCuts.json), but cut at RUNTIME instead of swapping to a pre-cut GLB:
//   1. a "unit" (data: wounds.cuts.units) names a root bone; every vertex skinned >= weightMin to that bone's subtree belongs to the piece;
//   2. each triangle goes to the piece when >= 2 of its 3 vertices do (head: neck seam; waist; shoulder);
//   3. the piece mesh is baked from the CURRENT pose (SkinnedMeshRenderer.BakeMesh) into a rigid mesh with a Rigidbody (it falls/rolls);
//   4. the body mesh is rebuilt without those triangles (still skinned, so the ragdoll/animation keeps working);
//   5. the open boundary of BOTH sides is closed with a cap (centroid fan per connected boundary loop, welded by position) using a flesh material;
//   6. "split" units (split crown) cut the piece's triangles down a bone-local plane into two halves that stay attached to the bone, hinge open a few degrees, with cut faces capped;
//   7. blood: jet on the body stump, a smaller one on the severed piece, decals via ADWoundsRuntime.
// REQUIRES the victim model's meshes to be Read/Write enabled (Mesh.isReadable). If not, falls back to: scale the unit's root bone to 0 and spawn a placeholder sphere piece. See handoff.

public struct ADCutBloodEvent { public Vector3 pos, dir; public string kind; public int loc; }
public class ADCutResult
{
    public bool ok; public string note = ""; public string unit = "";
    public readonly List<GameObject> pieces = new List<GameObject>();
    public readonly List<ADCutBloodEvent> blood = new List<ADCutBloodEvent>();
}

public static class ADMeshCut
{
    struct Edge { public int l1, a1, b1, l2; public int count; }

    // ------------------------------------------------------------------ public API
    /// <summary>True when the body has the unit's bone (used as the `has` predicate of the plan).</summary>
    public static bool CanCut(Transform victimRoot, string unitId)
    {
        var u = FindUnit(unitId); if (u == null) return false;
        return u.kind == "wound" ? FindBone(victimRoot, u.bones) != null : FindBone(victimRoot, u.bones) != null;
    }

    /// <summary>Cut one unit off/open on the victim. awayDir = direction the blow pushes (world). push/pieceImpulse come from the finisher row. Pieces are returned (not parented to the victim).</summary>
    public static ADCutResult Cut(Transform victimRoot, string unitId, int seed, Vector3 awayDir, float push = 1f, float pieceImpulse = 60f)
    {
        var res = new ADCutResult { unit = unitId };
        var cfg = ADWoundsData.Get(); var cc = cfg.cuts;
        var unit = FindUnit(unitId);
        if (unit == null) { res.note = "unknown unit " + unitId; return res; }
        Transform root = FindBone(victimRoot, unit.bones);
        if (root == null) { res.note = "bone not found for " + unitId; return res; }
        Transform parentBone = FindBone(victimRoot, unit.parentBones) ?? root.parent;
        if (parentBone == null) parentBone = root;
        Vector3 jetDir = (root.position - parentBone.position); if (jetDir.sqrMagnitude < 1e-6f) jetDir = Vector3.up; jetDir.Normalize();
        var victimRuntime = victimRoot.GetComponentInParent<ADWoundsRuntime>() ?? victimRoot.GetComponent<ADWoundsRuntime>();
        awayDir.y = 0f; if (awayDir.sqrMagnitude < 1e-4f) awayDir = victimRoot.forward * -1f; awayDir.Normalize();

        if (unit.kind == "wound")
        {
            res.ok = true; res.note = "wound only";
            Blood(res, victimRoot, parentBone, root.position, jetDir, unit, cfg, victimRuntime, false);
            return res;
        }

        var set = new HashSet<Transform>(root.GetComponentsInChildren<Transform>(true));
        var rnd = new System.Random(seed * 7919 + unitId.GetHashCode());
        var smrs = victimRoot.GetComponentsInChildren<SkinnedMeshRenderer>(false);
        GameObject physRoot = null; Vector3 firstCap = root.position; bool haveCap = false; int unreadable = 0, touched = 0;
        bool split = unit.kind == "split";

        for (int s = 0; s < smrs.Length; s++)
        {
            var smr = smrs[s];
            if (smr == null || smr.sharedMesh == null || !smr.enabled) continue;
            if (!smr.sharedMesh.isReadable) { unreadable++; continue; }
            Vector3 cap;
            if (!CutSkinned(smr, set, root, parentBone, unit, cc, split, ref physRoot, res, out cap)) continue;
            touched++;
            if (!haveCap) { firstCap = cap; haveCap = true; }
        }

        if (touched == 0)
        {
            if (unreadable > 0 && !split) { Fallback(victimRoot, root, unit, res); }
            else res.note = unreadable > 0 ? "meshes not readable (enable Read/Write on the model import)" : "no vertices skinned to " + unitId;
            if (!res.ok) return res;
        }
        else
        {
            res.ok = true;
            if (physRoot != null) Launch(physRoot, victimRoot, unit, cc, awayDir, push, pieceImpulse, rnd);
        }
        Blood(res, victimRoot, parentBone, haveCap ? firstCap : root.position, jetDir, unit, cfg, victimRuntime, physRoot != null);
        return res;
    }

    // ------------------------------------------------------------------ helpers
    static ADCutUnit FindUnit(string id)
    {
        var u = ADWoundsData.Get().cuts.units;
        for (int i = 0; i < u.Length; i++) if (u[i] != null && u[i].id == id) return u[i];
        return null;
    }

    static Transform FindBone(Transform root, string[] names)
    {
        if (names == null || names.Length == 0 || root == null) return null;
        var all = root.GetComponentsInChildren<Transform>(true);
        for (int n = 0; n < names.Length; n++)
        {
            string k = ADWoundsRuntime.Key(names[n]);
            for (int i = 0; i < all.Length; i++) if (ADWoundsRuntime.Key(all[i].name) == k) return all[i];
        }
        return null;
    }

    struct Ctx
    {
        public Vector3[] pos, nrm; public Vector2[] uv; public BoneWeight[] bw; public Vector3[] bpos, bnrm;
        public int[][] tris; public int[][] label; public int[] weld;
    }

    static int Weld(Dictionary<(int, int, int), int> map, Vector3 p, float q, int index)
    {
        var key = (Mathf.RoundToInt(p.x / q), Mathf.RoundToInt(p.y / q), Mathf.RoundToInt(p.z / q));
        int id; if (map.TryGetValue(key, out id)) return id;
        map[key] = index; return index;
    }

    static Color Col(float[] c) { return new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f); }

    static Material CapMaterial(SkinnedMeshRenderer smr, ADCutsCfg cc, bool inner)
    {
        Material src = null; var mats = smr.sharedMaterials;
        for (int i = 0; i < mats.Length && src == null; i++) src = mats[i];
        Material m = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
        m.name = "ADCapFlesh";
        var col = Col(inner ? cc.innerColor : cc.capColor);
        foreach (var p in new[] { "_BaseColor", "_Color" }) if (m.HasProperty(p)) m.SetColor(p, col);
        foreach (var p in new[] { "_BaseColorMap", "_MainTex", "_NormalMap", "_BumpMap", "_MaskMap", "_EmissiveColorMap", "_DetailMap" }) if (m.HasProperty(p)) m.SetTexture(p, null);
        foreach (var p in new[] { "_Smoothness", "_Glossiness" }) if (m.HasProperty(p)) m.SetFloat(p, 0.5f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black);
        return m;
    }

    // ------------------------------------------------------------------ the cut
    static bool CutSkinned(SkinnedMeshRenderer smr, HashSet<Transform> set, Transform root, Transform parentBone, ADCutUnit unit, ADCutsCfg cc, bool split, ref GameObject physRoot, ADCutResult res, out Vector3 capWorld)
    {
        capWorld = Vector3.zero;
        var mesh = smr.sharedMesh; var bones = smr.bones;
        var bw = mesh.boneWeights; int vc = mesh.vertexCount;
        if (bw.Length != vc || bones == null || bones.Length == 0) return false;

        var baked = new Mesh(); smr.BakeMesh(baked, false);
        if (baked.vertexCount != vc) { UnityEngine.Object.Destroy(baked); return false; }
        var c = new Ctx { pos = mesh.vertices, nrm = mesh.normals, uv = mesh.uv, bw = bw, bpos = baked.vertices, bnrm = baked.normals };
        if (c.nrm.Length != vc) c.nrm = new Vector3[vc];
        if (c.uv.Length != vc) c.uv = new Vector2[vc];
        if (c.bnrm.Length != vc) c.bnrm = c.nrm;

        // vertex membership by skin weight
        var inA = new bool[vc]; int nA = 0;
        for (int v = 0; v < vc; v++)
        {
            float w = 0f; var b = bw[v];
            w += InSet(bones, b.boneIndex0, set) ? b.weight0 : 0f; w += InSet(bones, b.boneIndex1, set) ? b.weight1 : 0f;
            w += InSet(bones, b.boneIndex2, set) ? b.weight2 : 0f; w += InSet(bones, b.boneIndex3, set) ? b.weight3 : 0f;
            if (w >= cc.weightMin) { inA[v] = true; nA++; }
        }
        if (nA == 0) { UnityEngine.Object.Destroy(baked); return false; }

        // weld by baked position so seam-split vertices (uv/normal splits) match across triangles
        var wmap = new Dictionary<(int, int, int), int>(); c.weld = new int[vc];
        for (int v = 0; v < vc; v++) c.weld[v] = Weld(wmap, c.bpos[v], Mathf.Max(1e-5f, cc.weldM), v);

        int subs = mesh.subMeshCount; c.tris = new int[subs][]; c.label = new int[subs][];
        Matrix4x4 toBone = root.worldToLocalMatrix * smr.transform.localToWorldMatrix;
        int axis = unit.splitAxis == "y" ? 1 : (unit.splitAxis == "z" ? 2 : 0);
        int piecesA = 0;
        for (int s = 0; s < subs; s++)
        {
            c.tris[s] = mesh.GetTriangles(s);
            c.label[s] = new int[c.tris[s].Length / 3];
            for (int t = 0; t < c.label[s].Length; t++)
            {
                int i0 = c.tris[s][t * 3], i1 = c.tris[s][t * 3 + 1], i2 = c.tris[s][t * 3 + 2];
                int k = (inA[i0] ? 1 : 0) + (inA[i1] ? 1 : 0) + (inA[i2] ? 1 : 0);
                if (k < 2) continue;
                int lab = 1;
                if (split)
                {
                    Vector3 cen = toBone.MultiplyPoint3x4((c.bpos[i0] + c.bpos[i1] + c.bpos[i2]) / 3f);
                    float d = (axis == 0 ? cen.x : axis == 1 ? cen.y : cen.z) - unit.splitOffsetM;
                    lab = d >= 0f ? 2 : 3;
                }
                c.label[s][t] = lab; piecesA++;
            }
        }
        if (piecesA == 0) { UnityEngine.Object.Destroy(baked); return false; }

        // edges between differently labelled triangles
        var edges = new Dictionary<long, Edge>();
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
                for (int e = 0; e < 3; e++)
                {
                    int a = c.tris[s][t * 3 + e], b = c.tris[s][t * 3 + (e + 1) % 3];
                    int wa = c.weld[a], wb = c.weld[b]; if (wa == wb) continue;
                    long key = wa < wb ? ((long)wa << 32) | (uint)wb : ((long)wb << 32) | (uint)wa;
                    Edge ed; if (edges.TryGetValue(key, out ed)) { ed.count++; ed.l2 = c.label[s][t]; edges[key] = ed; }
                    else edges[key] = new Edge { l1 = c.label[s][t], a1 = a, b1 = b, count = 1, l2 = -1 };
                }

        // the two owners of a boundary edge need their own directed copy; keep first owner in the record, find the second owner's direction by a second pass
        var second = new Dictionary<long, int[]>();
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
                for (int e = 0; e < 3; e++)
                {
                    int a = c.tris[s][t * 3 + e], b = c.tris[s][t * 3 + (e + 1) % 3];
                    int wa = c.weld[a], wb = c.weld[b]; if (wa == wb) continue;
                    long key = wa < wb ? ((long)wa << 32) | (uint)wb : ((long)wb << 32) | (uint)wa;
                    Edge ed = edges[key]; if (ed.count != 2) continue;
                    if (ed.a1 == a && ed.b1 == b && ed.l1 == c.label[s][t]) continue;   // the first owner
                    second[key] = new[] { c.label[s][t], a, b };
                }

        var capMatFlesh = CapMaterial(smr, cc, false);
        int capSub = subs;
        var mats = smr.sharedMaterials;
        var pieceMats = new Material[subs + 1]; for (int i = 0; i < subs; i++) pieceMats[i] = i < mats.Length ? mats[i] : null; pieceMats[subs] = capMatFlesh;

        // ---------- build pieces
        int[] labels = split ? new[] { 2, 3 } : new[] { 1 };
        Transform pieceParent;
        if (!split && physRoot == null)
        {
            physRoot = new GameObject("ADPiece_" + unit.id);
            physRoot.transform.position = root.position; physRoot.transform.rotation = Quaternion.identity;
        }
        pieceParent = split ? root : physRoot.transform;
        bool bodyCap = !split;
        foreach (int lab in labels)
        {
            int other = split ? (lab == 2 ? 3 : 2) : 0;
            var pm = BuildPiece(c, subs, lab, other, edges, second, capSub, split ? 1 : 1);
            if (pm == null) continue;
            var go = new GameObject("ADMesh_" + unit.id + "_" + lab);
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = pm;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = pieceMats;
            go.transform.position = smr.transform.position; go.transform.rotation = smr.transform.rotation; go.transform.localScale = smr.transform.lossyScale;
            if (split)
            {
                // halves stay on the bone and hinge open a little around the neck
                Vector3 pivot = parentBone.position;
                Vector3 hinge = HingeAxis(root, axis);
                go.transform.RotateAround(pivot, hinge, (lab == 2 ? -1f : 1f) * unit.openDeg * 0.5f);
                go.transform.SetParent(root, true);
            }
            else
            {
                go.transform.SetParent(pieceParent, true);
            }
            res.pieces.Add(split ? go : physRoot);
        }

        // ---------- rebuild the body without the removed triangles
        var body = BuildBody(c, mesh, subs, bodyCap, edges, second, capSub);
        smr.sharedMesh = body;
        var newMats = new Material[subs + 1]; for (int i = 0; i < subs; i++) newMats[i] = i < mats.Length ? mats[i] : null; newMats[subs] = bodyCap ? capMatFlesh : mats[Mathf.Min(0, mats.Length - 1)];
        smr.sharedMaterials = newMats;

        // world position of the cap centre for the blood
        capWorld = CapCentre(c, edges, second, split ? 2 : 1, split ? 3 : 0, smr.transform);
        UnityEngine.Object.Destroy(baked);
        return true;
    }

    static bool InSet(Transform[] bones, int idx, HashSet<Transform> set) { return idx >= 0 && idx < bones.Length && bones[idx] != null && set.Contains(bones[idx]); }

    static Vector3 HingeAxis(Transform root, int splitAxis)
    {
        // the hinge is the bone-local axis that is neither the split axis nor y (bone length); for the common split axis x that is z
        Vector3 ax = splitAxis == 2 ? root.right : root.forward;
        return ax;
    }

    // boundary edges owned by triangles of `label` whose neighbour label passes (neighbour == other)
    static List<int[]> Boundary(Dictionary<long, Edge> edges, Dictionary<long, int[]> second, int label, int other)
    {
        var list = new List<int[]>();
        foreach (var kv in edges)
        {
            var e = kv.Value; if (e.count != 2) continue;
            int[] s2; if (!second.TryGetValue(kv.Key, out s2)) continue;
            if (e.l1 == label && s2[0] == other) list.Add(new[] { e.a1, e.b1 });
            else if (s2[0] == label && e.l1 == other) list.Add(new[] { s2[1], s2[2] });
        }
        return list;
    }

    // connected loops of boundary edges (by welded vertex id)
    static List<List<int[]>> Loops(List<int[]> edgeList, int[] weld)
    {
        var parent = new Dictionary<int, int>();
        Func<int, int> find = null; find = x => { int p; if (!parent.TryGetValue(x, out p)) { parent[x] = x; return x; } if (p == x) return x; int r = find(p); parent[x] = r; return r; };
        foreach (var e in edgeList) { int a = find(weld[e[0]]), b = find(weld[e[1]]); if (a != b) parent[a] = b; }
        var groups = new Dictionary<int, List<int[]>>();
        foreach (var e in edgeList) { int r = find(weld[e[0]]); List<int[]> g; if (!groups.TryGetValue(r, out g)) { g = new List<int[]>(); groups[r] = g; } g.Add(e); }
        return new List<List<int[]>>(groups.Values);
    }

    static Mesh BuildPiece(Ctx c, int subs, int label, int other, Dictionary<long, Edge> edges, Dictionary<long, int[]> second, int capSub, int unused)
    {
        var vmap = new Dictionary<int, int>(); var P = new List<Vector3>(); var N = new List<Vector3>(); var U = new List<Vector2>();
        var T = new List<int>[subs + 1]; for (int i = 0; i <= subs; i++) T[i] = new List<int>();
        Func<int, int> map = v => { int m; if (vmap.TryGetValue(v, out m)) return m; m = P.Count; vmap[v] = m; P.Add(c.bpos[v]); N.Add(c.bnrm[v]); U.Add(c.uv[v]); return m; };
        int count = 0;
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
                if (c.label[s][t] == label) { for (int k = 0; k < 3; k++) T[s].Add(map(c.tris[s][t * 3 + k])); count++; }
        if (count == 0) return null;
        // cap: for edges owned by this piece: triangle (b, a, centre)
        var bl = Boundary(edges, second, label, other);
        foreach (var loop in Loops(bl, c.weld))
        {
            Vector3 cen = Vector3.zero; foreach (var e in loop) cen += c.bpos[e[0]]; cen /= loop.Count;
            foreach (var e in loop)
            {
                Vector3 a = c.bpos[e[0]], b = c.bpos[e[1]];
                Vector3 n = Vector3.Cross(a - b, cen - b).normalized;   // winding (b, a, cen)
                int ia = P.Count; P.Add(b); P.Add(a); P.Add(cen); N.Add(n); N.Add(n); N.Add(n); U.Add(Vector2.zero); U.Add(Vector2.zero); U.Add(Vector2.zero);
                T[capSub].Add(ia); T[capSub].Add(ia + 1); T[capSub].Add(ia + 2);
            }
        }
        var m2 = new Mesh { name = "ADPieceMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m2.SetVertices(P); m2.SetNormals(N); m2.SetUVs(0, U); m2.subMeshCount = subs + 1;
        for (int i = 0; i <= subs; i++) m2.SetTriangles(T[i], i, false);
        m2.RecalculateBounds();
        return m2;
    }

    static Mesh BuildBody(Ctx c, Mesh src, int subs, bool cap, Dictionary<long, Edge> edges, Dictionary<long, int[]> second, int capSub)
    {
        var P = new List<Vector3>(c.pos); var N = new List<Vector3>(c.nrm); var U = new List<Vector2>(c.uv); var BW = new List<BoneWeight>(c.bw);
        var T = new List<int>[subs + 1]; for (int i = 0; i <= subs; i++) T[i] = new List<int>();
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
                if (c.label[s][t] == 0) for (int k = 0; k < 3; k++) T[s].Add(c.tris[s][t * 3 + k]);
        if (cap)
        {
            var bl = Boundary(edges, second, 0, 1);
            foreach (var loop in Loops(bl, c.weld))
            {
                Vector3 cen = Vector3.zero; foreach (var e in loop) cen += c.pos[e[0]]; cen /= loop.Count;
                BoneWeight cbw = c.bw[loop[0][0]];
                int ci = P.Count; P.Add(cen); N.Add(Vector3.up); U.Add(Vector2.zero); BW.Add(cbw);
                foreach (var e in loop)
                {
                    Vector3 a = c.pos[e[0]], b = c.pos[e[1]];
                    Vector3 n = Vector3.Cross(a - b, cen - b).normalized;
                    int ia = P.Count; P.Add(b); P.Add(a); N.Add(n); N.Add(n); U.Add(Vector2.zero); U.Add(Vector2.zero); BW.Add(c.bw[e[1]]); BW.Add(c.bw[e[0]]);
                    T[capSub].Add(ia); T[capSub].Add(ia + 1); T[capSub].Add(ci);
                }
            }
        }
        var m = new Mesh { name = src.name + "_cut", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(P); m.SetNormals(N); m.SetUVs(0, U); m.boneWeights = BW.ToArray(); m.bindposes = src.bindposes;
        m.subMeshCount = subs + 1;
        for (int i = 0; i <= subs; i++) m.SetTriangles(T[i], i, false);
        m.bounds = src.bounds;
        return m;
    }

    static Vector3 CapCentre(Ctx c, Dictionary<long, Edge> edges, Dictionary<long, int[]> second, int label, int other, Transform smrT)
    {
        var bl = Boundary(edges, second, label, other);
        if (bl.Count == 0) return smrT.position;
        Vector3 cen = Vector3.zero; foreach (var e in bl) cen += c.bpos[e[0]]; cen /= bl.Count;
        return smrT.TransformPoint(cen);
    }

    // ------------------------------------------------------------------ physics, blood, fallback
    static void Launch(GameObject root, Transform victim, ADCutUnit unit, ADCutsCfg cc, Vector3 away, float push, float impulse, System.Random rnd)
    {
        var rb = root.AddComponent<Rigidbody>(); rb.mass = Mathf.Max(0.1f, unit.massKg);
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        var rends = root.GetComponentsInChildren<MeshRenderer>();
        if (rends.Length > 0)
        {
            Bounds b = rends[0].bounds; for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            if (unit.colliderRadiusM > 0f)
            {
                var sc = root.AddComponent<SphereCollider>(); sc.radius = unit.colliderRadiusM; sc.center = root.transform.InverseTransformPoint(b.center);
            }
            else
            {
                var bc = root.AddComponent<BoxCollider>(); bc.center = root.transform.InverseTransformPoint(b.center); bc.size = b.size;
            }
        }
        var cols = victim.GetComponentsInChildren<Collider>(true); var mine = root.GetComponents<Collider>();
        for (int i = 0; i < cols.Length; i++) for (int j = 0; j < mine.Length; j++) if (cols[i] != null && !cols[i].transform.IsChildOf(root.transform)) Physics.IgnoreCollision(cols[i], mine[j], true);
        float k = impulse * push * cc.pieceImpulseScale;
        Vector3 imp = away * k + Vector3.up * k * 0.45f + new Vector3((float)rnd.NextDouble() - 0.5f, 0f, (float)rnd.NextDouble() - 0.5f) * k * 0.3f;
        rb.AddForce(imp, ForceMode.Impulse);
        rb.AddTorque(new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * k * 0.5f, ForceMode.Impulse);
    }

    static void Blood(ADCutResult res, Transform victim, Transform parentBone, Vector3 pos, Vector3 dir, ADCutUnit unit, ADWoundsCfg cfg, ADWoundsRuntime rt, bool hasPiece)
    {
        res.blood.Add(new ADCutBloodEvent { pos = pos, dir = dir, kind = "jet", loc = unit.loc });
        var spray = cfg.gore.neckSpray;
        ADGoreFx.Spray(parentBone, pos, dir, spray, unit.jetSeconds);
        if (hasPiece && res.pieces.Count > 0 && res.pieces[0] != null)
        {
            res.blood.Add(new ADCutBloodEvent { pos = pos, dir = -dir, kind = "piece", loc = unit.loc });
            ADGoreFx.Spray(res.pieces[0].transform, pos, -dir, cfg.gore.woundSpray, 0.8f);
        }
        if (rt != null) rt.Splash(unit.loc, 8);
    }

    static void Fallback(Transform victim, Transform root, ADCutUnit unit, ADCutResult res)
    {
        if (root.GetComponent<Rigidbody>() != null) { res.note = "meshes not readable and bone has a rigidbody (ragdoll already built)"; return; }
        var smr = victim.GetComponentInChildren<SkinnedMeshRenderer>();
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "ADPiece_placeholder_" + unit.id;
        go.transform.position = root.position + Vector3.up * 0.1f; go.transform.localScale = Vector3.one * Mathf.Max(0.1f, unit.colliderRadiusM * 2f);
        if (smr != null && smr.sharedMaterial != null) go.GetComponent<Renderer>().sharedMaterial = smr.sharedMaterial;
        var sc = go.GetComponent<Collider>(); if (sc != null) { var cols = victim.GetComponentsInChildren<Collider>(true); for (int i = 0; i < cols.Length; i++) Physics.IgnoreCollision(cols[i], sc, true); }
        go.AddComponent<Rigidbody>().mass = unit.massKg;
        root.localScale = Vector3.zero;
        res.pieces.Add(go); res.ok = true; res.note = "FALLBACK: meshes not readable, bone scaled to 0 + placeholder sphere (enable Read/Write on the model import)";
    }
}

/// <summary>Runs a finisher's cut plan on the victim at the data times (scaled time, so finisher slow-mo applies). Lane C: ADCutRunner.Run(...) from the finisher player.</summary>
public class ADCutRunner : MonoBehaviour
{
    public event Action<ADCutResult> OnCut;
    public readonly List<ADCutResult> results = new List<ADCutResult>();

    public static ADCutRunner Run(Transform victim, ADCutPlanResult plan, Vector3 away, int seed, Action<ADCutResult> onCut = null)
    {
        var r = victim.gameObject.AddComponent<ADCutRunner>();
        if (onCut != null) r.OnCut += onCut;
        r.StartCoroutine(r.Go(victim, plan, away, seed));
        return r;
    }

    IEnumerator Go(Transform victim, ADCutPlanResult plan, Vector3 away, int seed)
    {
        float t = 0f; var steps = new List<ADCutStep>(plan.steps); steps.Sort((a, b) => a.atS.CompareTo(b.atS));
        for (int i = 0; i < steps.Count; i++)
        {
            while (t < steps[i].atS) { t += Time.deltaTime; yield return null; }
            var res = ADMeshCut.Cut(victim, steps[i].unit, seed + i, away, plan.row.push, plan.row.pieceImpulse);
            results.Add(res); if (OnCut != null) OnCut(res);
        }
    }
}
