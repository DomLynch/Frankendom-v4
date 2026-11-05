using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Debug = UnityEngine.Debug;

// PREWARM v92 (lanePREWARM). FxPrewarm (PERF3 C1) already covers: GoreFx splats/decals/spray, BloodGround, ClashFx, CombatVFX, BloodBurst, the MeshCut cap materials and a severed-piece physics stub; GlbPreload (C3) the gear GLBs.
// What was still built on first use, in the middle of the first finisher / the first loot drop, and is warmed here (one step every prewarm92.json framesBetweenSteps frames, each step timed):
//   cleave        FinisherCleave halo + core materials and the generated line texture             (first head split)
//   woundDecals   one HDRP/Decal material per wound shape on a sub-pixel DecalProjector            (first landed hit that wounds)
//   volumes       a weight-0 global Volume with MotionBlur + Vignette + the finisher exposure override (FinisherCamera.SetNoBlur / FinisherVignette.Begin create exactly these)
//   clips         every Animator clip of every fighter touched (clip data is loaded), finisher clip states checked and logged if missing
//   ragdollStub   a kinematic root + chained dynamic capsules with ConfigurableJoint slerp drives, far below the arena, destroyed after the draw frames (RagdollDeath.Build's component set)
//   lootMaterials the loot pillar + ground textures, meshes and materials drawn sub-pixel          (first drop)
//   lootFonts     the loot OS font, the label/toast styles at their real sizes, glyphs requested    (first drop label in OnGUI: font raster + style creation)
// Plus a hitch watcher: any frame longer than hitch.hitchMs is logged with the game state, so the verify/loop logs show where a stall still is.
// Nothing here touches the sim: no tick, no RNG, no gameplay state. Everything is sub-pixel / weight 0 / far away and destroyed. Off: prewarm92.json enabled=false or -noprewarm92. Skipped in batch mode and (unless inHarness / -prewarm92harness) in harness runs.
// Log lines: "[Prewarm92] step <name> <ms> ms", "[Prewarm92] done total <s> s ..." and "[Prewarm92] HITCH <ms> ms ...".
public class Prewarm92 : MonoBehaviour
{
    [Serializable] public class StepsC { public bool cleave = true, woundDecals = true, lootMaterials = true, lootFonts = true, volumes = true, clips = true, ragdollStub = true; }
    [Serializable] public class VolC { public int frames = 3; public float vignetteIntensity = 0.5f, priority = 9000f; }
    [Serializable] public class ClipsC { public bool touchAllControllerClips = true, reportMissingFinisherClips = true; }
    [Serializable] public class RagC { public int parts = 18, frames = 2; public float distance = 5000f, radius = 0.06f, length = 0.3f, mass = 4f, spring = 300f, damper = 12f, swing = 40f, twist = 20f; }
    [Serializable] public class FontsC { public string[] kinds = { "serif" }; public string chars = "ABCabc123"; public bool[] bold = { true, false }; }
    [Serializable] public class HitchC { public bool enabled = true; public float hitchMs = 250f; public int maxLogs = 20; }
    [Serializable]
    public class Cfg
    {
        public bool enabled = true, inHarness = false, ragdollStubAlways = false; public float startDelaySeconds = 1.5f; public int framesBetweenSteps = 3, drawFrames = 3;
        public StepsC steps = new StepsC(); public VolC volumes = new VolC(); public ClipsC clips = new ClipsC(); public RagC ragdollStub = new RagC(); public FontsC fonts = new FontsC(); public HitchC hitch = new HitchC();
    }

    static Cfg cfg;
    public static float TotalSeconds = -1f;   // < 0 until the prewarm finished
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; TotalSeconds = -1f; }

    static Cfg Load()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        try { var txt = HudCfg.ReadData("prewarm92.json"); if (txt != null) { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } }
        catch (Exception e) { Debug.LogWarning("[Prewarm92] prewarm92.json: " + e.Message); }
        if (cfg.steps == null) cfg.steps = new StepsC(); if (cfg.volumes == null) cfg.volumes = new VolC(); if (cfg.clips == null) cfg.clips = new ClipsC(); if (cfg.ragdollStub == null) cfg.ragdollStub = new RagC();
        if (cfg.fonts == null) cfg.fonts = new FontsC(); if (cfg.fonts.kinds == null) cfg.fonts.kinds = new string[0]; if (cfg.fonts.bold == null || cfg.fonts.bold.Length == 0) cfg.fonts.bold = new[] { false }; if (cfg.hitch == null) cfg.hitch = new HitchC();
        cfg.framesBetweenSteps = Mathf.Max(1, cfg.framesBetweenSteps); cfg.drawFrames = Mathf.Max(1, cfg.drawFrames);
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.isBatchMode) return;
        bool force = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("-noprewarm92", StringComparison.OrdinalIgnoreCase)) return;
            if (a.Equals("-prewarm92harness", StringComparison.OrdinalIgnoreCase)) force = true;
        }
        var c = Load(); if (!c.enabled) return;
        bool runSteps = !PerfFlags.HarnessRun() || c.inHarness || force;
        if (!runSteps && !c.hitch.enabled) return;
        if (FindFirstObjectByType<Prewarm92>() != null) return;
        var go = new GameObject("Prewarm92"); DontDestroyOnLoad(go); go.AddComponent<Prewarm92>().runSteps = runSteps;
    }

    bool runSteps, fontsArmed; int hitchLogs; float maxFrameMs;
    readonly List<Material> shared = new List<Material>();    // materials that belong to a static cache: drawn, never destroyed
    readonly List<Material> temp = new List<Material>();      // throw-away materials (loot): destroyed after the draw
    readonly List<Mesh> meshes = new List<Mesh>();
    readonly List<GameObject> junk = new List<GameObject>();
    VolumeProfile volProf;

    // ---------------- hitch watcher ----------------
    void Update()
    {
        var h = cfg.hitch; if (!h.enabled) return;
        float ms = Time.unscaledDeltaTime * 1000f; if (Time.frameCount > 2 && TotalSeconds < 0f) maxFrameMs = Mathf.Max(maxFrameMs, ms);
        if (hitchLogs < h.maxLogs && ms > h.hitchMs && Time.frameCount > 2)
        {
            hitchLogs++;
            Debug.Log("[Prewarm92] HITCH " + ms.ToString("0") + " ms at t=" + Time.realtimeSinceStartup.ToString("0.0") + " s, frame " + Time.frameCount + ", outcome " + GameLoop.Outcome + ", slowMo " + FinisherTime.Slowed + ", finisherVignette " + FinisherVignette.Alive + ", prewarmDone " + (TotalSeconds >= 0f));
        }
    }

    // ---------------- the steps ----------------
    IEnumerator Start()
    {
        if (!runSteps) yield break;
        var c = cfg; var PF = PerfFlags.C; float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < c.startDelaySeconds) yield return null;
        Camera cam = null; for (int i = 0; i < 300 && (cam = Camera.main) == null; i++) yield return null;
        if (cam == null) { Destroy(gameObject); yield break; }

        var report = new System.Text.StringBuilder(); var sw = new Stopwatch(); int stepsRun = 0;
        Action<string, Action> step = (name, body) =>
        {
            sw.Restart(); try { body(); } catch (Exception e) { Debug.LogWarning("[Prewarm92] step " + name + ": " + e.Message); }
            sw.Stop(); stepsRun++; report.Append(' ').Append(name).Append('=').Append(sw.ElapsedMilliseconds).Append("ms");
            Debug.Log("[Prewarm92] step " + name + " " + sw.ElapsedMilliseconds + " ms");
        };

        // ---- build (each is the first-use cost, paid here, one step every few frames) ----
        var S = c.steps;
        if (S.cleave && FinisherConfig.EnableGore) { step("cleave", () => FinisherCleave.CollectPrewarm(shared)); yield return Frames(c.framesBetweenSteps); }
        if (S.woundDecals) { step("woundDecals", () => WoundDecals.CollectPrewarm(shared)); yield return Frames(c.framesBetweenSteps); }
        if (S.lootMaterials && LootCfg.Get().enabled) { step("lootMaterials", () => LootPickup.CollectPrewarm(temp, meshes)); yield return Frames(c.framesBetweenSteps); }
        if (S.clips) { step("clips", TouchClips); yield return Frames(c.framesBetweenSteps); }
        if (S.volumes) { step("volumes", () => { volProf = MakeVolume(); }); yield return Frames(c.framesBetweenSteps); }
        if (S.ragdollStub && (c.ragdollStubAlways || RagdollDeath.Cfg.enabled)) { step("ragdollStub", () => MakeRagdollStub(Camera.main != null ? Camera.main.transform.position : Vector3.zero)); yield return Frames(c.framesBetweenSteps); }
        if (S.lootFonts && LootCfg.Get().enabled) { step("lootFonts", LootFonts); fontsArmed = true; yield return Frames(c.framesBetweenSteps); }

        // ---- draw: sub-pixel meshes in front of the camera, decal projectors, the volume at weight 0 ----
        var quad = GoreFx.Quad(); GameObject decalGo = null; int frames = Mathf.Max(c.drawFrames, Mathf.Max(c.volumes.frames, c.ragdollStub.frames)); float maxDraw = 0f;
        for (int f = 0; f < frames; f++)
        {
            cam = Camera.main; if (cam == null) break; Transform ct = cam.transform; float t1 = Time.realtimeSinceStartup;
            Vector3 pos = ct.position + ct.forward * Mathf.Max(0.5f, PF.prewarmDistance); float size = Mathf.Max(1e-6f, PF.prewarmSize);
            if (f < c.drawFrames)
            {
                var m = Matrix4x4.TRS(pos, ct.rotation, Vector3.one * size);
                for (int i = 0; i < shared.Count; i++) if (shared[i] != null && !IsDecal(shared[i])) Graphics.DrawMesh(quad, m, shared[i], 0, (Camera)null, 0, (MaterialPropertyBlock)null, ShadowCastingMode.Off, false);
                for (int i = 0; i < temp.Count; i++)
                {
                    if (temp[i] == null) continue; Mesh pm = meshes.Count > 0 ? meshes[i < meshes.Count ? i : meshes.Count - 1] : quad;
                    float ext = Mathf.Max(1e-4f, Mathf.Max(pm.bounds.size.x, Mathf.Max(pm.bounds.size.y, pm.bounds.size.z)));
                    Graphics.DrawMesh(pm, Matrix4x4.TRS(pos, ct.rotation, Vector3.one * (size / ext)), temp[i], 0, (Camera)null, 0, (MaterialPropertyBlock)null, ShadowCastingMode.Off, false);
                }
            }
            if (f == 0)
            {
                decalGo = MakeDecals(pos, ct.rotation, size);
                if (volProf != null) { var go = new GameObject("Prewarm92Volume"); var v = go.AddComponent<Volume>(); v.isGlobal = true; v.priority = c.volumes.priority; v.weight = 0f; v.sharedProfile = volProf; junk.Add(go); }
            }
            maxDraw = Mathf.Max(maxDraw, (Time.realtimeSinceStartup - t1) * 1000f);
            yield return null;
        }
        if (decalGo != null) Destroy(decalGo);
        for (int i = 0; i < junk.Count; i++) if (junk[i] != null) Destroy(junk[i]);
        if (volProf != null) Destroy(volProf);
        for (int i = 0; i < temp.Count; i++) if (temp[i] != null) Destroy(temp[i]);   // only the loot clones; shared (static cache) materials stay
        yield return null;
        TotalSeconds = Time.realtimeSinceStartup - t0;
        Debug.Log("[Prewarm92] done total " + TotalSeconds.ToString("0.00") + " s after scene load (start delay " + c.startDelaySeconds.ToString("0.0") + " s), steps " + stepsRun + ":" + report + ", shared mats " + shared.Count + ", loot mats " + temp.Count + ", draw max " + maxDraw.ToString("0") + " ms, max frame " + maxFrameMs.ToString("0") + " ms");
        // this component stays alive: Update keeps the hitch watcher running for the rest of the session
    }

    static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }
    static bool IsDecal(Material m) { return m.shader != null && m.shader.name == "HDRP/Decal"; }

    // ---- clips: load the clip data of every controller, check the finisher states exist ----
    void TouchClips()
    {
        int clips = 0, ctrls = 0, missing = 0; var seen = new HashSet<RuntimeAnimatorController>(); var miss = new System.Text.StringBuilder();
        foreach (var fi in FindObjectsByType<Fighter>(FindObjectsSortMode.None))
        {
            if (fi == null || fi.anim == null || fi.anim.runtimeAnimatorController == null) continue;
            var rc = fi.anim.runtimeAnimatorController;
            if (cfg.clips.touchAllControllerClips && seen.Add(rc)) { ctrls++; foreach (var cl in rc.animationClips) if (cl != null && cl.length >= 0f) clips++; }
            if (cfg.clips.reportMissingFinisherClips && FinisherConfig.EnableFinishers)
                foreach (var e in FinisherConfig.Get().entries)
                {
                    if (e == null || !e.enabled) continue;
                    if (!string.IsNullOrEmpty(e.attackerClip) && !FinisherPlayer.HasState(fi, e.attackerClip)) { missing++; if (missing <= 6) miss.Append(' ').Append(fi.name).Append(':').Append(e.attackerClip); }
                    if (!string.IsNullOrEmpty(e.victimClip) && !FinisherPlayer.HasState(fi, e.victimClip)) { missing++; if (missing <= 6) miss.Append(' ').Append(fi.name).Append(':').Append(e.victimClip); }
                }
        }
        Debug.Log("[Prewarm92] clips touched " + clips + " in " + ctrls + " controllers; finisher states not on a fighter (a killer-only clip is expected missing on the victim and the reverse) " + missing + miss);
    }

    // ---- volumes: the same override types as FinisherCamera.SetNoBlur + FinisherVignette.Begin ----
    VolumeProfile MakeVolume()
    {
        var prof = ScriptableObject.CreateInstance<VolumeProfile>();
        var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0f); mb.active = true;
        var vg = prof.Add<Vignette>(true); vg.intensity.Override(Mathf.Clamp01(cfg.volumes.vignetteIntensity)); vg.active = true;
        FinisherLook.DarkenExposure(prof);
        return prof;
    }

    // ---- decals: one sub-pixel projector per wound decal material ----
    GameObject MakeDecals(Vector3 pos, Quaternion rot, float size)
    {
        GameObject root = null;
        foreach (var m in shared)
        {
            if (m == null || !IsDecal(m)) continue;
            if (root == null) root = new GameObject("Prewarm92Decals");
            var g = new GameObject("d"); g.transform.SetParent(root.transform, false); g.transform.SetPositionAndRotation(pos, rot);
            var p = g.AddComponent<DecalProjector>(); p.material = m; p.size = new Vector3(size, size, size); p.fadeFactor = 1f;
        }
        return root;
    }

    // ---- ragdoll: RagdollDeath.Build's component set on a throw-away chain (destroyed with the draw frames) ----
    void MakeRagdollStub(Vector3 near)
    {
        var R = cfg.ragdollStub; var root = new GameObject("Prewarm92Ragdoll"); root.transform.position = near + Vector3.down * Mathf.Max(100f, R.distance); junk.Add(root);
        Rigidbody prev = null;
        for (int i = 0; i < Mathf.Max(2, R.parts); i++)
        {
            var go = new GameObject("p" + i); go.transform.SetParent(root.transform, false); go.transform.localPosition = Vector3.up * (-R.length * i);
            var rb = go.AddComponent<Rigidbody>(); rb.mass = R.mass; rb.useGravity = false; rb.detectCollisions = false; rb.isKinematic = prev == null;
            var cap = go.AddComponent<CapsuleCollider>(); cap.radius = R.radius; cap.height = R.length; cap.direction = 1;
            if (prev != null)
            {
                var j = go.AddComponent<ConfigurableJoint>(); j.connectedBody = prev; j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
                j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
                j.lowAngularXLimit = new SoftJointLimit { limit = -R.swing }; j.highAngularXLimit = new SoftJointLimit { limit = R.swing };
                j.angularYLimit = new SoftJointLimit { limit = R.swing }; j.angularZLimit = new SoftJointLimit { limit = R.twist };
                j.rotationDriveMode = RotationDriveMode.Slerp; j.slerpDrive = new JointDrive { positionSpring = R.spring * R.mass, positionDamper = R.damper * R.mass, maximumForce = float.MaxValue };
            }
            prev = rb;
        }
    }

    // ---- loot fonts: create the real OS font and request the glyphs at the real sizes; the styles are built in OnGUI ----
    static int[] LootSizes() { var C = LootCfg.Get(); return new[] { C.label.nameSize, C.label.subSize, C.label.promptSize, C.pickup.toastSize, C.pickup.toastSubSize }; }

    void LootFonts()
    {
        var sizes = LootSizes();
        foreach (var kind in cfg.fonts.kinds)
        {
            var f = LootDraw.FontFor(kind); if (f == null) continue;
            foreach (int s in sizes) foreach (bool b in cfg.fonts.bold) f.RequestCharactersInTexture(cfg.fonts.chars, s, b ? FontStyle.Bold : FontStyle.Normal);
        }
    }

    void OnGUI()
    {
        if (!fontsArmed || TotalSeconds >= 0f || Event.current.type != EventType.Layout) return;
        var sizes = LootSizes(); var content = new GUIContent(cfg.fonts.chars);
        foreach (var kind in cfg.fonts.kinds)
            foreach (int s in sizes) foreach (bool b in cfg.fonts.bold)
                LootDraw.Style(kind, s, TextAnchor.UpperCenter, b, false).CalcSize(content);   // creates + caches the style (same key family as LootUI.DrawLabels) and rasterises its glyphs
    }
}
