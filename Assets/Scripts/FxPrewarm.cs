using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// FX prewarm (lane PERF-CPU). The blood / spark / dust / decal / finisher-blood materials were first drawn in the middle of a fight, so the first landed hit and the first finisher paid for
// pipeline-state creation, shader upload and (for the finisher) texture + material generation in a single frame. Shortly after the scene loads this component, over a few frames:
//   1. builds the lazily-created assets (GoreFx splat textures + decal / spray materials, the BloodGround pool is built in its own Awake);
//   2. draws a sub-pixel quad with every one of those materials for perf.json prewarmFrames frames (Graphics.DrawMesh, no shadows, 2 m in front of the camera, size perf.json prewarmSize);
//   3. emits one sub-pixel particle through every pooled particle system (ClashFx, CombatVFX, BloodBurst, a spray-style throw-away system).
// Nothing is visible, nothing touches the sim (no tick, no RNG, no gameplay state). Off with perf.json "prewarm": false; skipped in batch mode (no rendering there).
// PERF3 v100 C1 (perf_fixes.json C1, command line -perffix:C1 / -noperffix:C1): the FINISHER path is prewarmed too, during the intro: the MeshCut cap material of each fighter's body renderer (outer + inner, drawn with shadows so the
// shadow-caster pass is built) and a throw-away severed-piece physics stub (Rigidbody + collider + PhysicsMaterial, far below the arena, destroyed after 2 frames). With C1.inHarness a proof run (-finisherauto) runs ONLY this finisher part.
public class FxPrewarm : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = PerfFlags.C;
        if (!c.prewarm || Application.isBatchMode) return;
        bool c1 = PerfFixes.On("C1"), harness = PerfFlags.HarnessRun(), finisherOnly = false;
        if (!c.prewarmInHarness && harness)
        {
            if (!(c1 && PerfFixes.C.C1.inHarness)) return;   // PERF3 v100 C1: a harness run keeps the old behaviour unless C1.inHarness asks for the finisher part
            finisherOnly = true;
        }
        if (FindFirstObjectByType<FxPrewarm>() != null) return;
        var go = new GameObject("FxPrewarm"); DontDestroyOnLoad(go); var fx = go.AddComponent<FxPrewarm>(); fx.doFinisher = c1; fx.finisherOnly = finisherOnly;
    }

    bool doFinisher, finisherOnly;   // PERF3 v100 C1

    IEnumerator Start()
    {
        var c = PerfFlags.C;
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < c.prewarmDelaySeconds) yield return null;   // let the scene's own start-up (FX creation, first frames) finish first

        Camera cam = null;
        for (int i = 0; i < 300 && (cam = Camera.main) == null; i++) yield return null;
        if (cam == null) { Destroy(gameObject); yield break; }

        var mats = new List<Material>();
        try { if (FinisherConfig.EnableGore) GoreFx.CollectPrewarm(FinisherConfig.Get().gore, mats); } catch (System.Exception e) { Debug.LogWarning("[FxPrewarm] gore assets: " + e.Message); }
        yield return null;
        CombatVFX[] vfx = new CombatVFX[0]; BloodBurst[] bursts = new BloodBurst[0];
        if (!finisherOnly)   // PERF3 v100 C1: in a harness run only the finisher part runs
        {
            try { BloodGround.CollectPrewarm(mats); ClashFx.CollectPrewarm(mats); } catch (System.Exception e) { Debug.LogWarning("[FxPrewarm] ground / clash materials: " + e.Message); }
            vfx = FindObjectsByType<CombatVFX>(FindObjectsSortMode.None);
            bursts = FindObjectsByType<BloodBurst>(FindObjectsSortMode.None);
            for (int i = 0; i < vfx.Length; i++) { Add(mats, vfx[i].spark); Add(mats, vfx[i].dust); Add(mats, vfx[i].blood); }
            for (int i = 0; i < bursts.Length; i++) Add(mats, bursts[i].material);
        }
        yield return null;

        // PERF3 v100 C1: the MeshCut cap materials of each fighter's body renderer (what the first finisher builds in one frame)
        var capMats = new List<Material>(); Mesh capMesh = null; var f1 = PerfFixes.C.C1; bool fin = doFinisher && FinisherConfig.EnableFinishers && FinisherConfig.EnableGore;
        if (fin && f1.capMaterials)
        {
            try
            {
                var cc = FinisherConfig.Get().cuts;
                foreach (var df in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
                {
                    SkinnedMeshRenderer best = null;
                    foreach (var smr in df.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (smr.sharedMesh != null && (best == null || smr.sharedMesh.vertexCount > best.sharedMesh.vertexCount)) best = smr;
                    if (best == null) continue;
                    MeshCut.CollectPrewarmCaps(best, cc, capMats);
                    if (capMesh == null && f1.useBodyMesh) capMesh = best.sharedMesh;
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[FxPrewarm] finisher cap materials: " + e.Message); }
        }
        yield return null;
        GameObject stub = null; PhysicsMaterial stubMat = null; int stubLeft = 0;

        var quad = GoreFx.Quad();
        for (int f = 0; f < c.prewarmFrames; f++)
        {
            cam = Camera.main; if (cam == null) break;
            Transform ct = cam.transform;
            Vector3 pos = ct.position + ct.forward * Mathf.Max(0.5f, c.prewarmDistance);
            var m = Matrix4x4.TRS(pos, ct.rotation, Vector3.one * Mathf.Max(1e-6f, c.prewarmSize));
            for (int i = 0; i < mats.Count; i++)
                if (mats[i] != null) Graphics.DrawMesh(quad, m, mats[i], 0, (Camera)null, 0, (MaterialPropertyBlock)null, ShadowCastingMode.Off, false);
            if (capMats.Count > 0)   // PERF3 v100 C1: cap materials on the fighter's own mesh (same vertex layout as a severed piece), shrunk so its longest side is prewarmSize
            {
                Mesh cm = capMesh != null ? capMesh : quad; float ext = Mathf.Max(1e-4f, Mathf.Max(cm.bounds.size.x, Mathf.Max(cm.bounds.size.y, cm.bounds.size.z)));
                var m2 = Matrix4x4.TRS(pos, ct.rotation, Vector3.one * (Mathf.Max(1e-6f, c.prewarmSize) / ext));
                for (int i = 0; i < capMats.Count; i++)
                    if (capMats[i] != null) Graphics.DrawMesh(cm, m2, capMats[i], 0, (Camera)null, 0, (MaterialPropertyBlock)null, f1.capCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off, true);
            }
            if (f == 0)
            {
                float size = Mathf.Max(1e-6f, c.prewarmSize);
                try
                {
                    ClashFx.PrewarmEmit(pos, size);
                    for (int i = 0; i < vfx.Length; i++) if (vfx[i] != null) vfx[i].PrewarmEmit(pos, size);
                    for (int i = 0; i < bursts.Length; i++) if (bursts[i] != null) bursts[i].PrewarmEmit(pos, size);
                    if (FinisherConfig.EnableGore) GoreFx.PrewarmSpray(pos, size);
                }
                catch (System.Exception e) { Debug.LogWarning("[FxPrewarm] particles: " + e.Message); }
                if (fin && f1.pieceStub) { try { stub = MakeStub(cam.transform.position, f1, out stubMat); stubLeft = Mathf.Max(1, f1.pieceStubFrames); } catch (System.Exception e) { Debug.LogWarning("[FxPrewarm] piece stub: " + e.Message); } }
            }
            if (stub != null && --stubLeft <= 0) { Destroy(stub); stub = null; }
            yield return null;
        }
        if (stub != null) Destroy(stub);
        if (stubMat != null) Destroy(stubMat);
        for (int i = 0; i < capMats.Count; i++) if (capMats[i] != null) Destroy(capMats[i]);   // the clones were only for the pipeline state
        if (fin) Debug.Log("[FxPrewarm] finisher path prewarmed: cap materials " + capMats.Count + ", piece stub " + (f1.pieceStub ? "yes" : "no") + (finisherOnly ? " (harness: finisher part only)" : ""));
        Destroy(gameObject);
    }

    // PERF3 v100 C1: what FinisherPiece.Setup builds for a severed head (kinematic here, far below the arena, no gravity, no collisions detected): a Rigidbody, a collider of finishers.json gore.piece.shape and a PhysicsMaterial.
    static GameObject MakeStub(Vector3 near, PerfFixes.C1Cfg f1, out PhysicsMaterial pm)
    {
        var pc = FinisherConfig.Get().gore.piece ?? new PieceCfg();
        var go = new GameObject("FinisherPiecePrewarm"); go.transform.position = near + Vector3.down * Mathf.Max(100f, f1.pieceStubDistance);
        var rb = go.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false; rb.detectCollisions = false;
        Collider col;
        if (pc.shape == "box") col = go.AddComponent<BoxCollider>(); else if (pc.shape == "capsule") col = go.AddComponent<CapsuleCollider>(); else col = go.AddComponent<SphereCollider>();
        pm = new PhysicsMaterial("FinisherPiecePrewarm") { dynamicFriction = pc.dynamicFriction, staticFriction = pc.staticFriction, bounciness = pc.bounciness, frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Minimum };
        col.sharedMaterial = pm;
        return go;
    }

    static void Add(List<Material> into, Material m) { if (m != null && !into.Contains(m)) into.Add(m); }
}
