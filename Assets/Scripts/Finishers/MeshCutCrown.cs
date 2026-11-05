using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// V1FIN v94: Frankendom V1's split crown (src/skull.ts splitSkull + characters.ts splitCrown), as a MeshCut unit of kind "crown".
//   * HEAD ONLY: the triangles skinned to the head subtree (the same rule as the other units: >= 2 of 3 vertices with weight >= cuts.weightMin) are baked ONCE from the pose, expressed in a metre-sized HEAD FRAME
//     (origin = head bone, up = neck -> head, forward = the victim's facing, so the cut plane is the sagittal plane for any rig); the body mesh is NOT rebuilt (the old split path welded and edge-mapped the whole body).
//   * every baked triangle is clipped at the frame's centre plane (x = 0) into a left and a right half; the cut edges are closed with a centroid fan per source mesh, textured with a small mottled flesh map (skull.ts).
//   * the real head is hidden the V1 way: the Head bone is scaled to crown.hideScale, so the corpse reads headless while the BODY STAYS INTACT (no mesh swap). CrownRig keeps it hidden and puts it back when destroyed.
//   * the halves hang from a hinge at the bottom of the skull and OPEN over time on the head bone (smoothstep, openDeg about the frame's z, openShiftM outward): the opening is animated, not baked.
// Same staged-job contract as every cut (Prepare / Step / Commit): all the work is done in Run (sliced), Commit only activates the container.
public static partial class MeshCut
{
    sealed class CrownPart
    {
        public List<Vector3> P = new List<Vector3>(), N = new List<Vector3>(); public List<Vector2> U = new List<Vector2>();
        public List<int>[] T; public Material[] mats; public Material src;
    }

    static IEnumerator CrownSteps(CutJob j)
    {
        var unit = j.unit; var cu = unit.crown ?? new CrownCfg(); Transform head = j.root, neck = j.parentBone, victim = j.victimRoot;
        j.bakePos = head.position; j.bakeRot = head.rotation;
        var set = new HashSet<Transform>(head.GetComponentsInChildren<Transform>(true));
        Vector3 up = head.position - neck.position; if (up.sqrMagnitude < 1e-6f) up = head.up; up.Normalize();
        Quaternion frameRot;
        if (cu.frame == "bone") frameRot = head.rotation;
        else
        {
            Vector3 fwd = Vector3.ProjectOnPlane(victim.forward, up); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.forward, up); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
            frameRot = Quaternion.LookRotation(fwd.normalized, up);
        }
        Vector3 origin = head.position; Matrix4x4 w2f = Matrix4x4.TRS(origin, frameRot, Vector3.one).inverse;
        var parts = new List<CrownPart>(); SkinnedMeshRenderer firstSmr = null; int n = 0, every = Mathf.Max(16, j.every);

        // 1. skinned renderers: head-dominated triangles, from the CURRENT pose (BakeMesh), into the head frame
        foreach (var smr0 in victim.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            var smr = smr0;
            if (smr == null || smr.sharedMesh == null) continue;
            if (!smr.enabled) { j.disabledSkipped++; continue; }
            smr = LodPick(j, smr, set, false); if (smr == null) continue;   // CUTLOD v96: bake the ~10k-triangle proxy of the heavy head, never the 400k render mesh
            var mesh = smr.sharedMesh; if (!mesh.isReadable) { j.unreadable++; WarnUnreadable(smr); continue; }
            var bw = mesh.boneWeights; int vc = mesh.vertexCount; var bones = smr.bones; if (bw.Length != vc || bones == null || bones.Length == 0) continue;
            var baked = new Mesh(); smr.BakeMesh(baked, false);
            try
            {
                if (baked.vertexCount != vc) continue;
                var inA = new bool[vc]; int nA = 0;
                for (int v = 0; v < vc; v++)
                {
                    float w = 0f; var b = bw[v];
                    w += InSet(bones, b.boneIndex0, set) ? b.weight0 : 0f; w += InSet(bones, b.boneIndex1, set) ? b.weight1 : 0f; w += InSet(bones, b.boneIndex2, set) ? b.weight2 : 0f; w += InSet(bones, b.boneIndex3, set) ? b.weight3 : 0f;
                    if (w >= j.cc.weightMin) { inA[v] = true; nA++; }
                    if (++n >= every) { n = 0; if (j.Over()) yield return null; }
                }
                if (nA == 0) continue;
                Matrix4x4 l2f = w2f * smr.transform.localToWorldMatrix; var bp = baked.vertices; var bn = baked.normals; var uv = mesh.uv; bool hasN = bn.Length == vc, hasUv = uv.Length == vc;
                var part = new CrownPart { src = smr.sharedMaterial, mats = smr.sharedMaterials }; int subs = mesh.subMeshCount; part.T = new List<int>[subs]; for (int s = 0; s < subs; s++) part.T[s] = new List<int>();
                var remap = new Dictionary<int, int>();
                for (int s = 0; s < subs; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                    var idx = mesh.GetIndices(s);
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                    {
                        int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                        if (((inA[a] ? 1 : 0) + (inA[b] ? 1 : 0) + (inA[c] ? 1 : 0)) < 2) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int o = idx[t + k], m;
                            if (!remap.TryGetValue(o, out m))
                            {
                                m = part.P.Count; remap[o] = m; part.P.Add(l2f.MultiplyPoint3x4(bp[o]));
                                part.N.Add(hasN ? l2f.MultiplyVector(bn[o]).normalized : Vector3.up); part.U.Add(hasUv ? uv[o] : Vector2.zero);
                            }
                            part.T[s].Add(m);
                        }
                        if (++n >= every) { n = 0; if (j.Over()) yield return null; }
                    }
                }
                if (part.P.Count > 0) { parts.Add(part); if (firstSmr == null) firstSmr = smr; }
            }
            finally { UnityEngine.Object.Destroy(baked); }
            if (j.Over()) yield return null;
        }
        // 2. plain meshes under the head bone (helmet, hair cards, horns): all their triangles
        if (cu.includeBoneMeshes)
            foreach (var mf in head.GetComponentsInChildren<MeshFilter>(false))
            {
                var mr = mf.GetComponent<MeshRenderer>(); if (mr == null || !mr.enabled || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                var mesh = mf.sharedMesh; Matrix4x4 l2f = w2f * mf.transform.localToWorldMatrix; var vs = mesh.vertices; var ns = mesh.normals; var us = mesh.uv; bool hasN = ns.Length == vs.Length, hasUv = us.Length == vs.Length;
                var part = new CrownPart { src = mr.sharedMaterial, mats = mr.sharedMaterials }; int subs = mesh.subMeshCount; part.T = new List<int>[subs];
                for (int v = 0; v < vs.Length; v++) { part.P.Add(l2f.MultiplyPoint3x4(vs[v])); part.N.Add(hasN ? l2f.MultiplyVector(ns[v]).normalized : Vector3.up); part.U.Add(hasUv ? us[v] : Vector2.zero); }
                for (int s = 0; s < subs; s++) { part.T[s] = new List<int>(); if (mesh.GetTopology(s) == MeshTopology.Triangles) part.T[s].AddRange(mesh.GetIndices(s)); }
                parts.Add(part);
            }
        if (parts.Count == 0) { j.note = "crown: no head triangles (renderers disabled or not readable)"; yield break; }

        // 3. bounds, hinge, cap material (V1 skull.ts: hinge = max(bounds.min.y, -.045); cap uv from the bounds of everything)
        Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), bmax = -bmin;
        foreach (var pt in parts) foreach (var p in pt.P) { bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); }
        float hinge = Mathf.Max(bmin.y, cu.hingeMinYM);
        Material capMat = firstSmr != null ? CapMaterial(firstSmr, j.cc, false) : new Material(Shader.Find("Standard")); j.owned.Add(capMat);
        var tex = CapTexture(cu); j.owned.Add(tex);
        foreach (var lg in j.lodGo) if (lg != null) UnityEngine.Object.Destroy(lg); j.lodGo.Clear();   // CUTLOD v96: the proxies were only a bake source (firstSmr is not read after the cap material)
        Color tint = new Color(cu.capColor[0], cu.capColor[1], cu.capColor[2], cu.capColor.Length > 3 ? cu.capColor[3] : 1f);
        foreach (var p in new[] { "_BaseColor", "_Color" }) if (capMat.HasProperty(p)) capMat.SetColor(p, tint);
        foreach (var p in new[] { "_BaseColorMap", "_MainTex" }) if (capMat.HasProperty(p)) capMat.SetTexture(p, tex);

        // 4. the container (inactive until Commit), two half pivots, one mesh per source mesh per half
        var container = new GameObject("SplitCrown"); container.transform.SetParent(victim, false); container.SetActive(false); j.hidden.Add(container);
        var halves = new Transform[2]; int built = 0;
        for (int h = 0; h < 2; h++)
        {
            float side = h == 0 ? -1f : 1f;
            var half = new GameObject(h == 0 ? "SkullLeft" : "SkullRight"); half.transform.SetParent(container.transform, false); half.transform.localPosition = new Vector3(0f, hinge, 0f); halves[h] = half.transform;
            foreach (var pt in parts)
            {
                var m = CrownHalf(pt, side, hinge, bmin, bmax, out int capTris); if (m == null) continue;
                j.owned.Add(m); built++;
                var go = new GameObject("Mesh"); go.transform.SetParent(half.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var mats = new Material[m.subMeshCount]; for (int s = 0; s < mats.Length - 1; s++) mats[s] = s < pt.mats.Length ? pt.mats[s] : pt.src; mats[mats.Length - 1] = capMat;
                var mrr = go.AddComponent<MeshRenderer>(); mrr.sharedMaterials = mats; mrr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (j.Over()) yield return null;
            }
        }
        if (built == 0) { j.note = "crown: both halves empty"; yield break; }
        var rig = container.AddComponent<CrownRig>(); rig.Setup(head, halves, hinge, Quaternion.Inverse(head.rotation) * frameRot, cu);
        j.res.pieces.Add(container);
        j.firstCap = origin + frameRot * new Vector3(0f, bmax.y + cu.seamUpM, 0f); j.haveCap = true; j.touched++;
    }

    // one half of one source mesh: V1 skull.ts clipping at x = 0 (the half keeps x * side >= 0), cut edges closed with a centroid fan
    static Mesh CrownHalf(CrownPart pt, float side, float hinge, Vector3 bmin, Vector3 bmax, out int capTris)
    {
        capTris = 0; int count = 0; int subs = pt.T.Length; var P = new List<Vector3>(); var N = new List<Vector3>(); var U = new List<Vector2>(); var T = new List<int>[subs + 1]; for (int i = 0; i <= subs; i++) T[i] = new List<int>();
        var edges = new List<Vector3[]>(); var poly = new List<int>(); var vp = new Vector3[3]; var vn = new Vector3[3]; var vu = new Vector2[3];
        for (int s = 0; s < subs; s++)
        {
            var idx = pt.T[s];
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                for (int k = 0; k < 3; k++) { vp[k] = pt.P[idx[t + k]]; vn[k] = pt.N[idx[t + k]]; vu[k] = pt.U[idx[t + k]]; }
                var cross = new List<Vector3>(2);
                var pp = new List<Vector3>(4); var nn = new List<Vector3>(4); var uu = new List<Vector2>(4);
                for (int k = 0; k < 3; k++)
                {
                    int k2 = (k + 1) % 3; bool ina = vp[k].x * side >= 0f, inb = vp[k2].x * side >= 0f;
                    if (ina) { pp.Add(vp[k]); nn.Add(vn[k]); uu.Add(vu[k]); }
                    if (ina == inb) continue;
                    float tt = -vp[k].x / (vp[k2].x - vp[k].x); Vector3 p = Vector3.Lerp(vp[k], vp[k2], tt); p.x = 0f;
                    pp.Add(p); nn.Add(Vector3.Lerp(vn[k], vn[k2], tt).normalized); uu.Add(Vector2.Lerp(vu[k], vu[k2], tt)); cross.Add(p);
                }
                if (cross.Count == 2) edges.Add(new[] { cross[0], cross[1] });
                for (int k = 1; k < pp.Count - 1; k++)
                    foreach (int q in new[] { 0, k, k + 1 }) { T[s].Add(P.Count); P.Add(new Vector3(pp[q].x, pp[q].y - hinge, pp[q].z)); N.Add(nn[q]); U.Add(uu[q]); count++; }
            }
        }
        if (edges.Count > 0)
        {
            Vector3 cen = Vector3.zero; foreach (var e in edges) cen += e[0] + e[1]; cen /= edges.Count * 2f;
            Vector3 face = new Vector3(-side, 0f, 0f); float rz = Mathf.Max(1e-5f, bmax.z - bmin.z), ry = Mathf.Max(1e-5f, bmax.y - bmin.y);
            foreach (var e in edges)
            {
                Vector3 a = e[0], b = e[1]; if (Vector3.Dot(Vector3.Cross(a - cen, b - cen), face) < 0f) { var tmp = a; a = b; b = tmp; }
                if (Vector3.Cross(a - cen, b - cen).sqrMagnitude < 1e-14f) continue;
                foreach (var pnt in new[] { cen, a, b }) { T[subs].Add(P.Count); P.Add(new Vector3(pnt.x, pnt.y - hinge, pnt.z)); N.Add(face); U.Add(new Vector2((pnt.z - bmin.z) / rz, (pnt.y - bmin.y) / ry)); }
                capTris++; count++;
            }
        }
        if (count == 0) return null;
        var m = new Mesh { name = "FinisherCrownHalf", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(P); m.SetNormals(N); m.SetUVs(0, U); m.subMeshCount = subs + 1;
        for (int i = 0; i <= subs; i++) m.SetTriangles(T[i], i, false);
        m.RecalculateBounds(); return m;
    }

    // skull.ts: 64 x 64 mottled tissue map: shade = (min + |grain| * span) * vein
    static Texture2D CapTexture(CrownCfg cu)
    {
        int sz = Mathf.Clamp(cu.capTexSize, 4, 512); var px = new Color32[sz * sz]; var tn = cu.capTint != null && cu.capTint.Length >= 3 ? cu.capTint : new[] { 200f, 175f, 165f };
        for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                double grain = (Math.Sin(x * 127.1 + y * 311.7) * 43758.5453) % 1.0;
                float vein = Math.Sin(x * 0.6 + Math.Sin(y * 0.35) * 3.0) > cu.capVeinStep ? cu.capVein : 1f;
                float shade = (cu.capShadeMin + (float)Math.Abs(grain) * cu.capShadeSpan) * vein;
                px[y * sz + x] = new Color32((byte)Mathf.Clamp(tn[0] * shade, 0, 255), (byte)Mathf.Clamp(tn[1] * shade, 0, 255), (byte)Mathf.Clamp(tn[2] * shade, 0, 255), 255);
            }
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, true, false) { name = "FinisherCrownCap", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
        tex.SetPixels32(px); tex.Apply(true); return tex;
    }

    // V1 profile blood (finisher-blood.ts): sources + stepped emission phases from gore.v1blood. Returns false when the unit has no profile / the switch is off (the caller then does the old blood).
    static bool V1Blood(CutResult res, Transform victim, Transform parentBone, Vector3 pos, Vector3 dir, CutUnit unit, FinisherFile cfg, bool hasPiece)
    {
        var v1 = cfg.rt != null ? cfg.rt.v1 : null; if (v1 == null || !v1.enabled || !v1.blood || string.IsNullOrEmpty(unit.bloodProfile) || cfg.gore.v1blood == null) return false;
        V1BloodProfile prof = null; foreach (var p in cfg.gore.v1blood) if (p != null && p.id == unit.bloodProfile) { prof = p; break; }
        if (prof == null || prof.sources == null || prof.sources.Length == 0) return false;
        Vector3 fwd = Vector3.ProjectOnPlane(victim.forward, dir); if (fwd.sqrMagnitude < 1e-4f) fwd = victim.forward; fwd.Normalize();
        foreach (var s in prof.sources)
        {
            if (s == null) continue;
            var sp = new SprayCfg { seconds = prof.spray.seconds, rate = prof.spray.rate * s.strength, coneDeg = prof.spray.coneDeg, gravity = prof.spray.gravity, speed = prof.spray.speed, size = prof.spray.size, life = prof.spray.life, phaseUntilS = prof.spray.phaseUntilS, phaseSpeed = prof.spray.phaseSpeed };
            if (prof.spray.phaseRate != null) { sp.phaseRate = new float[prof.spray.phaseRate.Length]; for (int i = 0; i < sp.phaseRate.Length; i++) sp.phaseRate[i] = prof.spray.phaseRate[i] * s.strength; }
            Vector3 d = (dir * s.dirUp + fwd * s.dirFwd).normalized; Vector3 at = pos + dir * s.upOffsetM + fwd * s.fwdOffsetM;
            Transform parent = parentBone;
            if (s.onPiece && hasPiece && res.pieces.Count > 0 && res.pieces[0] != null) { parent = res.pieces[0].transform; d = -dir; }
            else if (s.onPiece) continue;
            res.blood.Add(new CutBloodEvent { pos = at, dir = d, kind = s.site, loc = unit.loc });
            GoreFx.Spray(parent, at, d, sp, 0f);
        }
        return true;
    }
}

// V1FIN v94: runtime of the split crown: follows the head bone, keeps the real head hidden, opens the halves (V1 characters.ts splitCrown: crown.open(smoothstep(...))). Destroyed with the victim; puts the bone back.
public sealed class CrownRig : MonoBehaviour
{
    public static readonly List<CrownRig> All = new List<CrownRig>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { All.Clear(); }
    /// <summary>Rematch hook: every crown puts its head bone back and goes away.</summary>
    public static void RestoreAll() { for (int i = All.Count - 1; i >= 0; i--) if (All[i] != null) Destroy(All[i].gameObject); All.Clear(); }

    Transform head; Transform[] halves; float hinge, age; Quaternion boneToFrame; CrownCfg cu; Vector3 savedScale = Vector3.one; bool hidden; public float Open;
    public void Setup(Transform headBone, Transform[] halfPivots, float hingeY, Quaternion b2f, CrownCfg cfg) { head = headBone; halves = halfPivots; hinge = hingeY; boneToFrame = b2f; cu = cfg; }
    void OnEnable() { All.Add(this); if (head != null && !hidden) { savedScale = head.localScale; hidden = true; } Follow(); Hide(); }
    void OnDestroy() { All.Remove(this); if (hidden && head != null) head.localScale = savedScale; }
    void Hide() { if (head != null) head.localScale = Vector3.one * cu.hideScale; }
    void Follow()
    {
        if (head == null) return;
        transform.SetPositionAndRotation(head.position, head.rotation * boneToFrame);
        var p = transform.parent; if (p != null) { Vector3 ls = p.lossyScale; transform.localScale = new Vector3(1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x)), 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.z))); }
    }
    void LateUpdate()
    {
        if (head == null) return;
        age += FinisherTime.Step(Time.unscaledDeltaTime);
        Follow(); Hide();   // after the Animator: nothing may put the head back while the corpse is on screen
        float t = Mathf.Clamp01((age - cu.openDelayS) / Mathf.Max(1e-4f, cu.openS)); Open = t * t * (3f - 2f * t);
        for (int i = 0; i < halves.Length; i++)
        {
            float side = i == 0 ? -1f : 1f;
            halves[i].localRotation = Quaternion.Euler(0f, 0f, -side * cu.openDeg * Open);
            halves[i].localPosition = new Vector3(side * cu.openShiftM * Open, hinge, 0f);
        }
    }
}
