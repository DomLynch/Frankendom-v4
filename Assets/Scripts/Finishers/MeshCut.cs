using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Runtime mesh cuts for finishers (decapitation, split crown, opened, dismember, hamstring; runThrough = blood only).
// Port of the Armagedom finisher cuts (finisher-cuts.js + pitbornCuts.json), but cut at RUNTIME instead of swapping to a pre-cut GLB:
//   1. a "unit" (data: wounds.cuts.units) names a root bone; every vertex skinned >= weightMin to that bone's subtree belongs to the piece;
//   2. each triangle goes to the piece when >= 2 of its 3 vertices do (head: neck seam; waist; shoulder);
//   3. the piece mesh is baked from the CURRENT pose (SkinnedMeshRenderer.BakeMesh) into a rigid mesh with a Rigidbody (it falls/rolls);
//   4. the body mesh is rebuilt without those triangles (still skinned, so the ragdoll/animation keeps working);
//   5. the open boundary of BOTH sides is closed with a cap (centroid fan per connected boundary loop, welded by position) using a flesh material;
//   6. "split" units (split crown) cut the piece's triangles down a bone-local plane into two halves that stay attached to the bone, hinge open a few degrees, with cut faces capped;
//   7. blood: jet on the body stump, a smaller one on the severed piece, decals via GoreFx.Splash.
// REQUIRES the victim model's meshes to be Read/Write enabled (Mesh.isReadable). If not, falls back to: scale the unit's root bone to 0 and spawn a placeholder sphere piece. See handoff.

public struct CutBloodEvent { public Vector3 pos, dir; public string kind; public int loc; }
public class CutResult
{
    public bool ok; public string note = ""; public string unit = "";
    public bool budget;   // CUTLOD v96: the cut was aborted because the job exceeded cut_lod.json totalBudgetMs (the caller runs the gore-only + plain death fallback, it must not retry another cut)
    public readonly List<GameObject> pieces = new List<GameObject>();
    public readonly List<CutBloodEvent> blood = new List<CutBloodEvent>();
}

public static partial class MeshCut   // V1FIN v94: partial (MeshCutCrown.cs)
{
    struct Edge { public int l1, a1, b1, l2; public int count; }

    // ------------------------------------------------------------------ public API
    /// <summary>True when the body has the unit's bone (used as the `has` predicate of the plan).</summary>
    public static bool CanCut(Transform victimRoot, string unitId)
    {
        if (victimRoot == null) return false;
        var u = FindUnit(unitId); if (u == null) return false;
        return u.kind == "wound" ? FindBone(victimRoot, u.bones) != null : FindBone(victimRoot, u.bones) != null;
    }

    /// <summary>FINFIX v93: true when the unit changes the victim's mesh (subtree / split), false for blood-only "wound" units and unknown ids.</summary>
    public static bool IsMeshUnit(string unitId) { var u = FindUnit(unitId); return u != null && u.kind != "wound"; }

    /// <summary>Cut one unit off/open on the victim. awayDir = direction the blow pushes (world). push/pieceImpulse come from the finisher row. Pieces are returned (not parented to the victim).
    /// FINFIX v93: now Prepare + finish + Commit in one call (same result as before). The finisher player uses Prepare (time-sliced under the wind-up) and Commit at the contact instead.</summary>
    public static CutResult Cut(Transform victimRoot, string unitId, int seed, Vector3 awayDir, float push = 1f, float pieceImpulse = 60f)
    {
        // FN2: never let a cut take the kill down with it (real-time path: Die() already ran, colliders are off, the ragdoll may exist). Any exception -> ok=false + one warning; the finisher player then runs its head-hide fallback.
        try
        {
            var job = Prepare(victimRoot, unitId, seed);
            job.Step(0f, 0);   // budget 0 = run to the end in this call
            return Commit(job, awayDir, push, pieceImpulse);
        }
        catch (Exception ex)
        {
            WarnOnce("cut-ex:" + unitId, "Cut('" + unitId + "') threw " + ex.GetType().Name + ": " + ex.Message + " | " + ex.StackTrace);
            return new CutResult { unit = unitId, ok = false, note = "cut exception: " + ex.Message };
        }
    }

    /// <summary>FINFIX v93: the staged cut. A job does ALL the expensive CPU work (bake, weld, labels, edges, cap loops, the new piece and body meshes) without touching the scene's look: the piece objects are created
    /// inactive, the body mesh swap is stored. Step(budgetMs) runs it in slices (one per frame); Commit applies it in a few ms (swap the body mesh, show + place + launch the piece, blood). Discard() throws it away.</summary>
    public sealed class CutJob
    {
        public readonly string unitId; public CutResult res; public bool done, committed, failed; public int slices; public float cpuMs, maxSliceMs; public string note = "";
        internal Transform victimRoot, root, parentBone; internal CutUnit unit; internal CutsCfg cc; internal FinisherFile cfg; internal int seed; internal System.Random rnd;
        internal IEnumerator it; internal float budgetMs; internal int every = 512; internal readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        internal Vector3 bakePos; internal Quaternion bakeRot;             // the unit's root bone at the bake: the pose delta to the bone at the commit moves the piece
        internal readonly List<Action> apply = new List<Action>();         // body mesh + material swaps, run by Commit
        internal readonly List<GameObject> hidden = new List<GameObject>();   // pieces, inactive until Commit
        internal readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();   // meshes / materials created, destroyed by Discard
        internal GameObject physRoot; internal Vector3 physPos0; internal Quaternion physRot0;
        internal Vector3 firstCap; internal bool haveCap, isWound, needFallback, applied; internal int unreadable, touched, disabledSkipped;
        // CUTLOD v96: cut-LOD bookkeeping (proxy renderers, triangle counts for the one-line log, budget flag)
        internal readonly List<GameObject> lodGo = new List<GameObject>(); internal readonly List<LodPair> lodPairs = new List<LodPair>(); internal readonly HashSet<GameObject> lodKeep = new HashSet<GameObject>();
        internal int lodTris, lodSubst, lodSkipped; internal string lodNote = ""; internal bool budgetHit, lodLogged;
        internal CutJob(string id) { unitId = id; }
        internal void Abort(string why) { failed = true; done = true; budgetHit = true; note = why; if (res != null) { res.budget = true; res.note = why; } }
        internal bool Over() { return budgetMs > 0f && sw.Elapsed.TotalMilliseconds > budgetMs; }
        /// <summary>Runs the job until the budget (ms of CPU in this call; 0 = to the end) is used or it is done. Returns true when done. Never throws (a failure sets failed + note and done).</summary>
        public bool Step(float budget, int checkEvery)
        {
            if (done) return true;
            // CUTLOD v96: hard budgets. perFrameMaxMs caps a slice; the synchronous path (budget 0) runs in syncSliceMs slices so the total budget can stop it; totalBudgetMs aborts the whole job (res.budget, failed)
            var lc = CutLod.C; bool lod = lc.enabled; bool sync = budget <= 0f;
            if (lod && !sync && lc.perFrameMaxMs > 0f && budget > lc.perFrameMaxMs) budget = lc.perFrameMaxMs;
            bool sliced = sync && lod && lc.syncSliceMs > 0f; if (sliced) budget = lc.syncSliceMs;
            budgetMs = budget; if (checkEvery > 0) every = checkEvery;
            var swAll = System.Diagnostics.Stopwatch.StartNew(); float inner = 0f;
            sw.Restart();
            try {
                if (it == null) it = Run(this);
                if (sync && !sliced) { while (it.MoveNext()) {} done = true; }
                else if (sliced)
                {
                    while (true)
                    {
                        sw.Restart(); bool more = it.MoveNext(); float si = (float)sw.Elapsed.TotalMilliseconds; if (si > inner) inner = si;
                        if (!more) { done = true; break; }
                        if (lc.totalBudgetMs > 0f && cpuMs + swAll.Elapsed.TotalMilliseconds > lc.totalBudgetMs) { Abort("cut budget exceeded (" + (cpuMs + swAll.Elapsed.TotalMilliseconds).ToString("0") + " ms > " + lc.totalBudgetMs.ToString("0") + " ms)"); break; }
                    }
                }
                else { if (!it.MoveNext()) done = true; }
            }
            catch (Exception ex) { failed = true; done = true; note = "cut exception: " + ex.Message; WarnOnce("job-ex:" + unitId, "cut job '" + unitId + "' threw " + ex.GetType().Name + ": " + ex.Message + " | " + ex.StackTrace); }
            sw.Stop(); slices++; float ms = sliced ? (float)swAll.Elapsed.TotalMilliseconds : (float)sw.Elapsed.TotalMilliseconds; cpuMs += ms; float mx = sliced ? inner : ms; if (mx > maxSliceMs) maxSliceMs = mx;
            if (!done && lod && lc.totalBudgetMs > 0f && cpuMs > lc.totalBudgetMs) Abort("cut budget exceeded (" + cpuMs.ToString("0") + " ms > " + lc.totalBudgetMs.ToString("0") + " ms)");
            return done;
        }
        public void Discard()
        {
            if (committed) return;
            foreach (var g in hidden) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var o in owned) if (o != null) UnityEngine.Object.Destroy(o);
            foreach (var g in lodGo) if (g != null) UnityEngine.Object.Destroy(g);   // CUTLOD v96
            lodGo.Clear(); lodPairs.Clear(); lodKeep.Clear();
            hidden.Clear(); owned.Clear(); apply.Clear(); done = true; committed = true;
        }
    }

    /// <summary>Starts a staged cut (no work is done until Step). Never throws.</summary>
    public static CutJob Prepare(Transform victimRoot, string unitId, int seed)
    {
        var j = new CutJob(unitId) { victimRoot = victimRoot, seed = seed, res = new CutResult { unit = unitId } };
        return j;
    }

    /// <summary>Applies a finished (or, if not finished, completes it now) staged cut: body mesh swap, piece shown / placed / launched, blood. Same CutResult as the old Cut().</summary>
    public static CutResult Commit(CutJob j, Vector3 awayDir, float push = 1f, float pieceImpulse = 60f)
    {
        // CUTLOD v96: one log line per cut (tris, ms); proxies that were not adopted are destroyed
        bool was = j.committed; var r = CommitCore(j, awayDir, push, pieceImpulse);
        if (!was)
        {
            foreach (var g in j.lodGo) if (g != null && !j.lodKeep.Contains(g)) UnityEngine.Object.Destroy(g);
            j.lodGo.Clear(); j.lodPairs.Clear();
            if (!j.lodLogged) { j.lodLogged = true; LodLog(j, r, "cut"); }
        }
        return r;
    }

    static CutResult CommitCore(CutJob j, Vector3 awayDir, float push, float pieceImpulse)
    {
        var res = j.res;
        try
        {
            if (!j.done) j.Step(0f, 0);
            if (j.committed) return res;
            j.committed = true;
            if (j.failed || j.root == null || j.unit == null) { if (res.note.Length == 0) res.note = j.note.Length > 0 ? j.note : "cut not prepared"; FailCleanup(j); return res; }
            if (j.res.note.Length > 0 && !j.isWound && j.touched == 0 && !j.needFallback) return res;   // setup refusal (unknown unit, bone not found, gore off ...)
            Transform victimRoot = j.victimRoot, root = j.root, parentBone = j.parentBone; var unit = j.unit;
            Vector3 jetDir = (root.position - parentBone.position); if (jetDir.sqrMagnitude < 1e-6f) jetDir = Vector3.up; jetDir.Normalize();
            awayDir.y = 0f; if (awayDir.sqrMagnitude < 1e-4f) awayDir = victimRoot.forward * -1f; awayDir.Normalize();
            if (j.isWound)
            {
                res.ok = true; res.note = "wound only";
                Blood(res, victimRoot, parentBone, root.position, jetDir, unit, j.cfg, false);
                return res;
            }
            // the bone moved between the bake and now: carry the baked piece (and the cap centre) with it (rigid, y-up bone pose delta)
            Quaternion dq = root.rotation * Quaternion.Inverse(j.bakeRot); Vector3 bp = j.bakePos, np = root.position;
            if (j.touched == 0)
            {
                if (j.unreadable > 0 && unit.kind != "split") { Fallback(victimRoot, root, unit, res); }
                else res.note = j.unreadable > 0 ? "meshes not readable (enable Read/Write on the model import)" : (j.note.Length > 0 ? j.note : "no vertices skinned to " + unit.id + " [bone " + root.name + ", " + j.disabledSkipped + " disabled and skipped" + (j.disabledSkipped > 0 ? ": the visible head is probably a separate mesh (e.g. a MetaHuman head swap hides the original head renderer); the unit cleave / burst fallback runs" : "") + "]");
                if (!res.ok) return res;
            }
            else
            {
                res.ok = true;
                foreach (var a in j.apply) { try { a(); } catch (Exception ex) { Debug.LogWarning("[MeshCut] apply: " + ex.Message); } }
                j.applied = true;
                if (j.physRoot != null)
                {
                    j.physRoot.transform.SetPositionAndRotation(np + dq * (j.physPos0 - bp), dq * j.physRot0);
                }
                foreach (var g in j.hidden) if (g != null) g.SetActive(true);
                if (j.physRoot != null) Launch(j.physRoot, victimRoot, unit, j.cc, awayDir, push, pieceImpulse, j.rnd);
            }
            Vector3 cap = j.haveCap ? np + dq * (j.firstCap - bp) : root.position;
            Blood(res, victimRoot, parentBone, cap, jetDir, unit, j.cfg, j.physRoot != null);
            j.apply.Clear(); j.hidden.Clear(); j.owned.Clear();
        }
        catch (Exception ex)
        {
            WarnOnce("commit-ex:" + j.unitId, "Commit('" + j.unitId + "') threw " + ex.GetType().Name + ": " + ex.Message + " | " + ex.StackTrace);
            res.ok = false; res.note = "cut exception: " + ex.Message; if (!j.applied) FailCleanup(j);
        }
        return res;
    }

    // a failed job leaves inactive pieces and built meshes behind: destroy them (the body mesh was never swapped)
    static void FailCleanup(CutJob j)
    {
        foreach (var g in j.hidden) if (g != null) UnityEngine.Object.Destroy(g);
        foreach (var o in j.owned) if (o != null) UnityEngine.Object.Destroy(o);
        foreach (var g in j.lodGo) if (g != null) UnityEngine.Object.Destroy(g);   // CUTLOD v96
        j.lodGo.Clear(); j.lodPairs.Clear(); j.lodKeep.Clear();
        j.hidden.Clear(); j.owned.Clear(); j.apply.Clear(); j.res.pieces.Clear();
    }

    // the job body (an iterator: every `yield return null` is a point where Step may stop when its budget is used)
    static IEnumerator Run(CutJob j)
    {
        var res = j.res; Transform victimRoot = j.victimRoot;
        if (victimRoot == null) { res.note = "victimRoot null"; yield break; }
        if (!FinisherConfig.EnableGore) { res.note = "gore disabled (finishers.json enableGore=false)"; yield break; }
        var cfg = FinisherConfig.Get(); var cc = cfg != null ? cfg.cuts : null;
        if (cc == null || cc.units == null || cfg.gore == null) { res.note = "cuts/gore data missing (finishers.json cuts / gore blocks)"; yield break; }
        var unit = FindUnit(j.unitId);
        if (unit == null) { res.note = "unknown unit " + j.unitId; yield break; }
        Transform root = FindBone(victimRoot, unit.bones);
        if (root == null) { res.note = "bone not found for " + j.unitId; yield break; }
        Transform parentBone = FindBone(victimRoot, unit.parentBones) ?? root.parent;
        if (parentBone == null) parentBone = root;
        j.cfg = cfg; j.cc = cc; j.unit = unit; j.root = root; j.parentBone = parentBone;
        j.rnd = new System.Random(j.seed * 7919 + j.unitId.GetHashCode());
        if (unit.kind == "wound") { j.isWound = true; yield break; }
        if (unit.kind == "crown") { var csub = CrownSteps(j); while (csub.MoveNext()) yield return null; yield break; }   // V1FIN v94: V1's head-only split crown (MeshCutCrown.cs)

        var set = new HashSet<Transform>(root.GetComponentsInChildren<Transform>(true));
        var smrsAll = victimRoot.GetComponentsInChildren<SkinnedMeshRenderer>(false);
        bool split = unit.kind == "split";
        // bake every renderer in the same frame (one pose for the whole cut)
        j.bakePos = root.position; j.bakeRot = root.rotation;
        var smrs = new List<SkinnedMeshRenderer>(); var bakedMeshes = new List<Mesh>(); var smrPose = new List<Matrix4x4>(); var smrPos = new List<Vector3>(); var smrRot = new List<Quaternion>(); var smrScale = new List<Vector3>();
        for (int s = 0; s < smrsAll.Length; s++)
        {
            var smr = smrsAll[s];
            if (smr == null || smr.sharedMesh == null) continue;
            if (!smr.enabled) { j.disabledSkipped++; continue; }
            smr = LodPick(j, smr, set, true); if (smr == null) continue;   // CUTLOD v96: the cheap proxy instead of a 400k render mesh (null = not part of this unit / too heavy: skipped)
            if (!smr.sharedMesh.isReadable) { j.unreadable++; WarnUnreadable(smr); continue; }
            var baked = new Mesh(); j.owned.Add(baked); smr.BakeMesh(baked, false);
            smrs.Add(smr); bakedMeshes.Add(baked); smrPose.Add(smr.transform.localToWorldMatrix); smrPos.Add(smr.transform.position); smrRot.Add(smr.transform.rotation); smrScale.Add(smr.transform.lossyScale);
        }
        Matrix4x4 rootW2L = root.worldToLocalMatrix;
        if (smrs.Count == 0 && j.unreadable == 0) j.note = smrsAll.Length == 0 ? "no SkinnedMeshRenderer on the victim" : "";
        for (int s = 0; s < smrs.Count; s++)
        {
            if (j.Over()) yield return null;
            int touchedBefore = j.touched;   // CUTLOD v96
            var sub = CutSkinnedSteps(j, smrs[s], bakedMeshes[s], smrPose[s], smrPos[s], smrRot[s], smrScale[s], rootW2L, set, root, parentBone, unit, cc, split);
            while (sub.MoveNext()) yield return null;
            if (j.touched > touchedBefore)   // CUTLOD v96: the proxy took the cut -> at the commit it becomes the visible rest of the mesh and the heavy render mesh goes off (the cut pieces came from the proxy)
                foreach (var lp in j.lodPairs) if (lp.proxy == smrs[s]) { var lq = lp; j.lodKeep.Add(lq.proxy.gameObject); j.apply.Add(() => { if (lq.proxy != null) lq.proxy.forceRenderingOff = false; if (lq.heavy != null) lq.heavy.enabled = false; }); }
        }
        foreach (var b in bakedMeshes) if (b != null) UnityEngine.Object.Destroy(b);
        for (int i = j.owned.Count - 1; i >= 0; i--) if (j.owned[i] is Mesh && bakedMeshes.Contains((Mesh)j.owned[i])) j.owned.RemoveAt(i);
    }

    // ------------------------------------------------------------------ helpers
    static CutUnit FindUnit(string id)
    {
        var w = FinisherConfig.Get(); var u = w != null && w.cuts != null ? w.cuts.units : null; if (u == null) return null;
        for (int i = 0; i < u.Length; i++) if (u[i] != null && u[i].id == id) return u[i];
        return null;
    }

    static Transform FindBone(Transform root, string[] names)
    {
        if (names == null || names.Length == 0 || root == null) return null;
        var all = root.GetComponentsInChildren<Transform>(true);
        for (int n = 0; n < names.Length; n++)
        {
            string k = GoreFx.Key(names[n]);
            for (int i = 0; i < all.Length; i++) if (GoreFx.Key(all[i].name) == k) return all[i];
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

    // V64F: cap (cut face) material. With cuts.capMaterial.enabled: dark wet red, non-glowing, no textures, no subsurface / transmission (the skin clone rendered the cap as a flat brown slab),
    // double sided; otherwise the legacy clone with capColor / innerColor.
    static Material CapMaterial(SkinnedMeshRenderer smr, CutsCfg cc, bool inner)
    {
        var cm = cc.capMaterial; bool nw = cm != null && cm.enabled;
        Material src = null; var mats = smr.sharedMaterials;
        for (int i = 0; i < mats.Length && src == null; i++) src = mats[i];
        Shader sh = nw && !string.IsNullOrEmpty(cm.shader) ? Shader.Find(cm.shader) : null;
        Material m = sh != null ? new Material(sh) : (src != null ? new Material(src) : new Material(Shader.Find("Standard")));
        m.name = "FinisherCapFlesh";
        var col = Col(nw ? (inner ? cm.innerColor : cm.color) : (inner ? cc.innerColor : cc.capColor));
        foreach (var p in new[] { "_BaseColor", "_Color" }) if (m.HasProperty(p)) m.SetColor(p, col);
        foreach (var p in new[] { "_BaseColorMap", "_MainTex", "_NormalMap", "_BumpMap", "_MaskMap", "_EmissiveColorMap", "_DetailMap" }) if (m.HasProperty(p)) m.SetTexture(p, null);
        float smooth = nw ? cm.smoothness : 0.5f;
        foreach (var p in new[] { "_Smoothness", "_Glossiness" }) if (m.HasProperty(p)) m.SetFloat(p, smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", nw ? cm.metallic : 0f);
        if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", Color.black);
        if (nw)
        {
            foreach (var p in new[] { "_SubsurfaceMaskMap", "_ThicknessMap", "_CoatMaskMap", "_BentNormalMap", "_TangentMap", "_AnisotropyMap", "_IridescenceThicknessMap", "_SpecularColorMap", "_DiffuseScatteringMap" }) if (m.HasProperty(p)) m.SetTexture(p, null);
            foreach (var p in new[] { "_SmoothnessRemapMin", "_AORemapMin" }) if (m.HasProperty(p)) m.SetFloat(p, 0f);
            foreach (var p in new[] { "_SmoothnessRemapMax", "_AORemapMax", "_AO", "_NormalScale" }) if (m.HasProperty(p)) m.SetFloat(p, 1f);
            foreach (var p in new[] { "_EmissiveIntensity", "_CoatMask", "_SubsurfaceMask", "_Anisotropy", "_IridescenceThickness" }) if (m.HasProperty(p)) m.SetFloat(p, 0f);
            if (cm.clearSubsurface)
            {
                if (m.HasProperty("_MaterialID")) m.SetFloat("_MaterialID", 1f);   // HDRP Lit: 1 = Standard (0 = subsurface scattering)
                foreach (var k in new[] { "_MATERIAL_FEATURE_SUBSURFACE_SCATTERING", "_MATERIAL_FEATURE_TRANSMISSION", "_MATERIAL_FEATURE_ANISOTROPY", "_MATERIAL_FEATURE_IRIDESCENCE", "_MATERIAL_FEATURE_CLEAR_COAT", "_MATERIAL_FEATURE_SPECULAR_COLOR" }) m.DisableKeyword(k);
            }
            if (cm.doubleSided)
            {
                if (m.HasProperty("_DoubleSidedEnable")) { m.SetFloat("_DoubleSidedEnable", 1f); m.EnableKeyword("_DOUBLESIDED_ON"); }
                if (m.HasProperty("_DoubleSidedNormalMode")) m.SetFloat("_DoubleSidedNormalMode", 1f);   // mirror
                foreach (var p in new[] { "_CullMode", "_CullModeForward", "_Cull" }) if (m.HasProperty(p)) m.SetFloat(p, 0f);
            }
        }
        return m;
    }

    // PERF3 v100 C1 (FxPrewarm): the cap materials a cut of this renderer would build (outer + inner), built now so FxPrewarm can draw each once, sub-pixel, and the first finisher does not compile the pipeline state.
    // The caller destroys them. Nothing here touches the sim or any RNG.
    public static void CollectPrewarmCaps(SkinnedMeshRenderer smr, CutsCfg cc, List<Material> into)
    {
        if (smr == null || cc == null || into == null) return;
        try { into.Add(CapMaterial(smr, cc, false)); into.Add(CapMaterial(smr, cc, true)); } catch (Exception e) { Debug.LogWarning("[MeshCut] prewarm caps: " + e.Message); }
    }

    // ------------------------------------------------------------------ the cut
    // FINFIX v93: the old synchronous CutSkinned as a sliced iterator. Same maths and the same result meshes; differences: (1) it works on the mesh baked at the job start (all renderers one pose), (2) nothing visible is changed - the body mesh
    // swap is stored in j.apply and the pieces are created inactive (j.hidden), (3) only the triangles near the cut are welded / edge-mapped (exact: every edge between a cut and an uncut triangle has both ends inside the cut's bounding box),
    // (4) the big loops yield when the job's frame budget is used (CutJob.Over).
    static IEnumerator CutSkinnedSteps(CutJob j, SkinnedMeshRenderer smr, Mesh baked, Matrix4x4 smrL2W, Vector3 smrPos, Quaternion smrRot, Vector3 smrScale, Matrix4x4 rootW2L, HashSet<Transform> set, Transform root, Transform parentBone, CutUnit unit, CutsCfg cc, bool split)
    {
        var mesh = smr.sharedMesh; var bones = smr.bones;
        var bw = mesh.boneWeights; int vc = mesh.vertexCount;
        if (bw.Length != vc || bones == null || bones.Length == 0) yield break;
        if (baked.vertexCount != vc) yield break;
        int every = Mathf.Max(16, j.every), n = 0;
        var c = new Ctx { pos = mesh.vertices, nrm = mesh.normals, uv = mesh.uv, bw = bw, bpos = baked.vertices, bnrm = baked.normals };
        if (c.nrm.Length != vc) c.nrm = new Vector3[vc];
        if (c.uv.Length != vc) c.uv = new Vector2[vc];
        if (c.bnrm.Length != vc) c.bnrm = c.nrm;
        if (j.Over()) yield return null;

        // vertex membership by skin weight
        var inA = new bool[vc]; int nA = 0;
        for (int v = 0; v < vc; v++)
        {
            float w = 0f; var b = bw[v];
            w += InSet(bones, b.boneIndex0, set) ? b.weight0 : 0f; w += InSet(bones, b.boneIndex1, set) ? b.weight1 : 0f;
            w += InSet(bones, b.boneIndex2, set) ? b.weight2 : 0f; w += InSet(bones, b.boneIndex3, set) ? b.weight3 : 0f;
            if (w >= cc.weightMin) { inA[v] = true; nA++; }
            if (++n >= every) { n = 0; if (j.Over()) yield return null; }
        }
        if (nA == 0) yield break;

        int subs = mesh.subMeshCount; c.tris = new int[subs][]; c.label = new int[subs][];
        if (subs <= 0) { WarnOnce("nosub:" + mesh.name, "mesh '" + mesh.name + "' has no submeshes"); yield break; }
        for (int s = 0; s < subs; s++)
        {
            var idx = mesh.GetIndices(s);
            bool bad = mesh.GetTopology(s) != MeshTopology.Triangles || idx.Length % 3 != 0;
            for (int q = 0; q < idx.Length && !bad; q++) { if (idx[q] < 0 || idx[q] >= vc) bad = true; if (++n >= every * 8) { n = 0; if (j.Over()) yield return null; } }   // index overflow
            if (bad) { WarnOnce("badidx:" + mesh.name + s, "mesh '" + mesh.name + "' submesh " + s + " is not a clean triangle list (topology/index range): cut skipped"); yield break; }
        }
        Matrix4x4 toBone = rootW2L * smrL2W;
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
                if (k >= 2)
                {
                    int lab = 1;
                    if (split)
                    {
                        Vector3 cen = toBone.MultiplyPoint3x4((c.bpos[i0] + c.bpos[i1] + c.bpos[i2]) / 3f);
                        float d = (axis == 0 ? cen.x : axis == 1 ? cen.y : cen.z) - unit.splitOffsetM;
                        lab = d >= 0f ? 2 : 3;
                    }
                    c.label[s][t] = lab; piecesA++;
                }
                if (++n >= every) { n = 0; if (j.Over()) yield return null; }
            }
        }
        if (piecesA == 0) yield break;

        // FINFIX v93: the cut's bounding box (baked positions of every vertex of a cut triangle, padded): only vertices / triangles touching it can be on a cut boundary
        Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), bmax = -bmin;
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
            {
                if (c.label[s][t] == 0) continue;
                for (int k = 0; k < 3; k++) { Vector3 p = c.bpos[c.tris[s][t * 3 + k]]; bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); }
                if (++n >= every) { n = 0; if (j.Over()) yield return null; }
            }
        float pad = Mathf.Max(0.01f, cc.weldM * 8f); bmin -= new Vector3(pad, pad, pad); bmax += new Vector3(pad, pad, pad);
        var near = new bool[vc];
        for (int v = 0; v < vc; v++)
        {
            Vector3 p = c.bpos[v];
            near[v] = p.x >= bmin.x && p.x <= bmax.x && p.y >= bmin.y && p.y <= bmax.y && p.z >= bmin.z && p.z <= bmax.z;
            if (++n >= every) { n = 0; if (j.Over()) yield return null; }
        }

        // weld by baked position so seam-split vertices (uv/normal splits) match across triangles (only near vertices; the others keep their own index and are never read)
        var wmap = new Dictionary<(int, int, int), int>(); c.weld = new int[vc];
        for (int v = 0; v < vc; v++)
        {
            c.weld[v] = near[v] ? Weld(wmap, c.bpos[v], Mathf.Max(1e-5f, cc.weldM), v) : v;
            if (++n >= every) { n = 0; if (j.Over()) yield return null; }
        }

        // edges between differently labelled triangles (near triangles only)
        var edges = new Dictionary<long, Edge>();
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
            {
                if (!(near[c.tris[s][t * 3]] || near[c.tris[s][t * 3 + 1]] || near[c.tris[s][t * 3 + 2]])) continue;
                for (int e = 0; e < 3; e++)
                {
                    int a = c.tris[s][t * 3 + e], b = c.tris[s][t * 3 + (e + 1) % 3];
                    int wa = c.weld[a], wb = c.weld[b]; if (wa == wb) continue;
                    long key = wa < wb ? ((long)wa << 32) | (uint)wb : ((long)wb << 32) | (uint)wa;
                    Edge ed; if (edges.TryGetValue(key, out ed)) { ed.count++; ed.l2 = c.label[s][t]; edges[key] = ed; }
                    else edges[key] = new Edge { l1 = c.label[s][t], a1 = a, b1 = b, count = 1, l2 = -1 };
                }
                if (++n >= every) { n = 0; if (j.Over()) yield return null; }
            }

        // the two owners of a boundary edge need their own directed copy; keep first owner in the record, find the second owner's direction by a second pass
        var second = new Dictionary<long, int[]>();
        for (int s = 0; s < subs; s++)
            for (int t = 0; t < c.label[s].Length; t++)
            {
                if (!(near[c.tris[s][t * 3]] || near[c.tris[s][t * 3 + 1]] || near[c.tris[s][t * 3 + 2]])) continue;
                for (int e = 0; e < 3; e++)
                {
                    int a = c.tris[s][t * 3 + e], b = c.tris[s][t * 3 + (e + 1) % 3];
                    int wa = c.weld[a], wb = c.weld[b]; if (wa == wb) continue;
                    long key = wa < wb ? ((long)wa << 32) | (uint)wb : ((long)wb << 32) | (uint)wa;
                    Edge ed = edges[key]; if (ed.count != 2) continue;
                    if (ed.a1 == a && ed.b1 == b && ed.l1 == c.label[s][t]) continue;   // the first owner
                    second[key] = new[] { c.label[s][t], a, b };
                }
                if (++n >= every) { n = 0; if (j.Over()) yield return null; }
            }
        if (j.Over()) yield return null;

        var capMatFlesh = CapMaterial(smr, cc, false); j.owned.Add(capMatFlesh);
        int capSub = subs;
        var mats = smr.sharedMaterials;
        var pieceMats = new Material[subs + 1]; for (int i = 0; i < subs; i++) pieceMats[i] = i < mats.Length ? mats[i] : null; pieceMats[subs] = capMatFlesh;

        // ---------- build pieces (inactive until Commit)
        int[] labels = split ? new[] { 2, 3 } : new[] { 1 };
        Transform pieceParent;
        if (!split && j.physRoot == null)
        {
            j.physRoot = new GameObject("Piece_" + unit.id);
            j.physRoot.transform.position = j.bakePos; j.physRoot.transform.rotation = Quaternion.identity;
            j.physPos0 = j.bakePos; j.physRot0 = Quaternion.identity;
            j.physRoot.SetActive(false); j.hidden.Add(j.physRoot);
        }
        pieceParent = split ? root : j.physRoot.transform;
        bool bodyCap = !split;
        foreach (int lab in labels)
        {
            int other = split ? (lab == 2 ? 3 : 2) : 0;
            var pm = BuildPiece(c, subs, lab, other, edges, second, capSub, split ? 1 : 1);
            if (pm == null) continue;
            j.owned.Add(pm);
            var go = new GameObject("Mesh_" + unit.id + "_" + lab);
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = pm;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = pieceMats;
            go.transform.position = smrPos; go.transform.rotation = smrRot; go.transform.localScale = smrScale;
            if (split)
            {
                // halves stay on the bone and hinge open a little around the neck (they are children of the bone from now on: the bone's motion carries them)
                Vector3 pivot = parentBone.position;
                Vector3 hinge = HingeAxis(root, axis);
                go.transform.RotateAround(pivot, hinge, (lab == 2 ? -1f : 1f) * unit.openDeg * 0.5f);
                go.transform.SetParent(root, true);
                go.SetActive(false); j.hidden.Add(go);
            }
            else
            {
                go.transform.SetParent(pieceParent, true);
            }
            j.res.pieces.Add(split ? go : j.physRoot);
            if (j.Over()) yield return null;
        }

        // ---------- rebuild the body without the removed triangles (swapped in by Commit)
        var body = BuildBody(c, mesh, subs, bodyCap, edges, second, capSub); j.owned.Add(body);
        var newMats = new Material[subs + 1]; for (int i = 0; i < subs; i++) newMats[i] = i < mats.Length ? mats[i] : null; newMats[subs] = bodyCap ? capMatFlesh : (mats.Length > 0 && mats[0] != null ? mats[0] : capMatFlesh);
        var smrRef = smr;
        j.apply.Add(() => { if (smrRef != null) { smrRef.sharedMesh = body; smrRef.sharedMaterials = newMats; } });
        // pieces / body / cap material are now kept by the job: they must not be destroyed by Discard after a Commit (Commit clears j.owned)

        // world position of the cap centre for the blood (bake pose)
        Vector3 capWorld = CapCentre(c, edges, second, split ? 2 : 1, split ? 3 : 0, smrL2W);
        j.touched++;
        if (!j.haveCap) { j.firstCap = capWorld; j.haveCap = true; }
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
        Func<int, int> find = x => {
            int root = x;
            while (parent.TryGetValue(root, out int p) && p != root) root = p;
            int curr = x;
            while (parent.TryGetValue(curr, out int p) && p != root) { parent[curr] = root; curr = p; }
            parent[x] = root;
            return root;
        };
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
                Vector3 n = Vector3.Cross(a - b, cen - b); if (n.sqrMagnitude < 1e-14f) continue; n.Normalize();   // winding (b, a, cen); skips zero-area triangles
                int ia = P.Count; P.Add(b); P.Add(a); P.Add(cen); N.Add(n); N.Add(n); N.Add(n); U.Add(Vector2.zero); U.Add(Vector2.zero); U.Add(Vector2.zero);
                T[capSub].Add(ia); T[capSub].Add(ia + 1); T[capSub].Add(ia + 2);
            }
        }
        var m2 = new Mesh { name = "FinisherPieceMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
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
                    Vector3 n = Vector3.Cross(a - b, cen - b); if (n.sqrMagnitude < 1e-14f) continue; n.Normalize();
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

    static Vector3 CapCentre(Ctx c, Dictionary<long, Edge> edges, Dictionary<long, int[]> second, int label, int other, Matrix4x4 smrL2W)   // FINFIX v93: bake-pose matrix instead of the live transform
    {
        var bl = Boundary(edges, second, label, other);
        if (bl.Count == 0) return smrL2W.MultiplyPoint3x4(Vector3.zero);
        Vector3 cen = Vector3.zero; foreach (var e in bl) cen += c.bpos[e[0]]; cen /= bl.Count;
        return smrL2W.MultiplyPoint3x4(cen);
    }

    // ------------------------------------------------------------------ physics, blood, fallback
    static readonly HashSet<string> warned = new HashSet<string>();
    static void WarnOnce(string key, string msg) { if (warned.Add(key)) Debug.LogError("[MeshCut] " + msg); }

    static readonly HashSet<string> warnedUnreadable = new HashSet<string>();
    // LOUD, once per mesh: the cut needs Mesh.isReadable. Fallback = bone scaled to 0 + placeholder sphere (head/limbs); splitCrown does nothing. Fix: Editor menu Tools/Alpha/Make models readable (Assets/Editor/AlphaReadable.cs).
    static void WarnUnreadable(SkinnedMeshRenderer smr)
    {
        string k = smr.sharedMesh.name; if (!warnedUnreadable.Add(k)) return;
        Debug.LogError("[MeshCut] MESH NOT READABLE: '" + k + "' on '" + smr.name + "'. Mesh cut falls back to bone-scale + placeholder piece. Enable Read/Write (see AlphaReadable / alpha_readable.json).");
    }

    // FN2: Physics.IgnoreCollision logs an error / throws when either collider is disabled or on an inactive object (Duel.Die() switches the victim collider off before the cut runs). Skip those pairs; a disabled collider cannot hit the piece anyway.
    static void SafeIgnore(Collider a, Collider b)
    {
        if (a == null || b == null || a == b) return;
        if (!a.enabled || !b.enabled || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) return;
        try { Physics.IgnoreCollision(a, b, true); } catch (Exception) { }
    }

    static void Launch(GameObject root, Transform victim, CutUnit unit, CutsCfg cc, Vector3 away, float push, float impulse, System.Random rnd)
    {
        var rb = root.AddComponent<Rigidbody>(); rb.mass = Mathf.Max(0.1f, unit.massKg);
        { var v1 = FinisherConfig.Get().rt.v1; if (v1 != null && v1.enabled && v1.headArc && unit.arc != null && unit.arc.enabled) { SeveredHeadArc.Begin(root, victim, unit.arc, away); return; } if (v1 != null && v1.enabled && v1.headArc && unit.script != null && unit.script.enabled) { OpenedHalf.Begin(root, victim, unit.script, away); return; } }   // V1FIN v94: V1's scripted head arc instead of the Rigidbody (kinematic; FinisherPiece skips its physics)
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
        for (int i = 0; i < cols.Length; i++) for (int j = 0; j < mine.Length; j++) if (cols[i] != null && !cols[i].transform.IsChildOf(root.transform)) SafeIgnore(cols[i], mine[j]);
        float k = impulse * push * cc.pieceImpulseScale;
        if (unit.launchSideMps > 0f || unit.launchUpMps > 0f)
        {
            // FINFIX v93 (V3 severed head): thrown with VELOCITIES, independent of the piece's mass: launchSideMps away from the killer, launchUpMps up, a little scatter (m/s), spin about the blow heading (rad/s)
            Vector3 vel = away * unit.launchSideMps * push + Vector3.up * unit.launchUpMps + new Vector3((float)rnd.NextDouble() - 0.5f, 0f, (float)rnd.NextDouble() - 0.5f) * unit.scatter;
            rb.angularDamping = unit.angularDrag;
            if (unit.spinRadS > 0f) rb.maxAngularVelocity = Mathf.Max(rb.maxAngularVelocity, unit.spinRadS);
            rb.linearVelocity = vel;
            if (unit.spinRadS > 0f) rb.angularVelocity = away * unit.spinRadS;
        }
        else
        {
        Vector3 imp = away * k + Vector3.up * k * unit.upBias + new Vector3((float)rnd.NextDouble() - 0.5f, 0f, (float)rnd.NextDouble() - 0.5f) * k * unit.scatter;
        rb.AddForce(imp, ForceMode.Impulse);
        rb.AddTorque(new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f) * k * unit.torqueMul, ForceMode.Impulse);
        rb.angularDamping = unit.angularDrag;
        }
        if (unit.lifeSeconds > 0f) UnityEngine.Object.Destroy(root, unit.lifeSeconds);
    }

    static void Blood(CutResult res, Transform victim, Transform parentBone, Vector3 pos, Vector3 dir, CutUnit unit, FinisherFile cfg, bool hasPiece)
    {
        if (V1Blood(res, victim, parentBone, pos, dir, unit, cfg, hasPiece)) { GoreFx.Splash(victim, unit.loc, unit.extraDecals, cfg.gore); FinisherCleave.Apply(victim, unit, pos, dir, Vector3.zero, res); return; }   // V1FIN v94: V1 finisher-blood.ts sources + phases
        res.blood.Add(new CutBloodEvent { pos = pos, dir = dir, kind = "jet", loc = unit.loc });
        var spray = cfg.gore.neckSpray;
        GoreFx.Spray(parentBone, pos, dir, spray, unit.jetSeconds);
        if (hasPiece && res.pieces.Count > 0 && res.pieces[0] != null)
        {
            res.blood.Add(new CutBloodEvent { pos = pos, dir = -dir, kind = "piece", loc = unit.loc });
            GoreFx.Spray(res.pieces[0].transform, pos, -dir, cfg.gore.woundSpray, 0.8f);
        }
        GoreFx.Splash(victim, unit.loc, unit.extraDecals, cfg.gore);
        FinisherCleave.Apply(victim, unit, pos, dir, Vector3.zero, res);   // V63G: wound line + patches + drips on the head and the bigger burst (units with cleave / burst ids; no-op otherwise)
    }

    static void Fallback(Transform victim, Transform root, CutUnit unit, CutResult res)
    {
        if (root.GetComponent<Rigidbody>() != null) { res.note = "meshes not readable and bone has a rigidbody (ragdoll already built)"; return; }
        var smr = victim.GetComponentInChildren<SkinnedMeshRenderer>();
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "Piece_placeholder_" + unit.id;
        go.transform.position = root.position + Vector3.up * 0.1f; go.transform.localScale = Vector3.one * Mathf.Max(0.1f, unit.colliderRadiusM * 2f);
        if (smr != null && smr.sharedMaterial != null) go.GetComponent<Renderer>().sharedMaterial = smr.sharedMaterial;
        var sc = go.GetComponent<Collider>(); if (sc != null) { var cols = victim.GetComponentsInChildren<Collider>(true); for (int i = 0; i < cols.Length; i++) SafeIgnore(cols[i], sc); }
        go.AddComponent<Rigidbody>().mass = unit.massKg;
        root.localScale = Vector3.zero;
        res.pieces.Add(go); res.ok = true; res.note = "FALLBACK: meshes not readable, bone scaled to 0 + placeholder sphere (enable Read/Write on the model import)";
    }
}

