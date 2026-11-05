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
public class FxPrewarm : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = PerfFlags.C;
        if (!c.prewarm || Application.isBatchMode) return;
        if (!c.prewarmInHarness && PerfFlags.HarnessRun()) return;
        if (FindFirstObjectByType<FxPrewarm>() != null) return;
        var go = new GameObject("FxPrewarm"); DontDestroyOnLoad(go); go.AddComponent<FxPrewarm>();
    }

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
        try { BloodGround.CollectPrewarm(mats); ClashFx.CollectPrewarm(mats); } catch (System.Exception e) { Debug.LogWarning("[FxPrewarm] ground / clash materials: " + e.Message); }
        var vfx = FindObjectsByType<CombatVFX>(FindObjectsSortMode.None);
        var bursts = FindObjectsByType<BloodBurst>(FindObjectsSortMode.None);
        for (int i = 0; i < vfx.Length; i++) { Add(mats, vfx[i].spark); Add(mats, vfx[i].dust); Add(mats, vfx[i].blood); }
        for (int i = 0; i < bursts.Length; i++) Add(mats, bursts[i].material);
        yield return null;

        var quad = GoreFx.Quad();
        for (int f = 0; f < c.prewarmFrames; f++)
        {
            cam = Camera.main; if (cam == null) break;
            Transform ct = cam.transform;
            Vector3 pos = ct.position + ct.forward * Mathf.Max(0.5f, c.prewarmDistance);
            var m = Matrix4x4.TRS(pos, ct.rotation, Vector3.one * Mathf.Max(1e-6f, c.prewarmSize));
            for (int i = 0; i < mats.Count; i++)
                if (mats[i] != null) Graphics.DrawMesh(quad, m, mats[i], 0, (Camera)null, 0, (MaterialPropertyBlock)null, ShadowCastingMode.Off, false);
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
            }
            yield return null;
        }
        Destroy(gameObject);
    }

    static void Add(List<Material> into, Material m) { if (m != null && !into.Contains(m)) into.Add(m); }
}
