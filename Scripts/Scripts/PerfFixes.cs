using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// lanePERF3 v100: the LOOK-SAFE performance fixes as DATA, each one switchable on its own so the look gate can A/B them one at a time.
// Data: perf_fixes.json (StreamingAssets in the player, Assets/Data in the editor). Missing file = every fix OFF (the shipped look is untouched).
// Toggle without a rebuild, on the command line (same exe, same data), for perfbench A/B runs:
//   -perffix:A1,A4        only these fixes ON (everything else OFF, whatever the json says)
//   -perffix:none         all OFF (baseline arm)        -perffix:json  (default) = the json "enabled" flags
//   -noperffix:A4,D1      json flags, but these OFF     -noperffixes = same as -perffix:none
//   -perfharness          C1 / C3 also run in harness runs (-finisherauto ...), which FxPrewarm skips by default
// Fix ids: A1 door point lights shadows OnEnable + alwaysDrawDynamic | A1b mark static MeshRenderers as staticShadowCaster near those lights | A4 ground / terrain / paving shadowCasting Off
//          B3 decal projector draw distance cap (the HDRP-asset half of B3 is in asset_variants) | C1 finisher path prewarm (FxPrewarm) | C3 GLB preload (GlbPreload)
//          D1 flicker scripts write only on change | D1b flicker scripts use a MaterialPropertyBlock instead of Material.SetColor
// A3 (Ultra shadow depth bits 16) and the asset half of B3 (decal atlas 2048) are HDRP-asset edits, NOT runtime: see asset_variants\make_variant.py (documented, not applied).
// A1 / A4 use the same renderer-path matching as laneSHADOW's ShadowFixes (hierarchy path, ordinal substring; PathOf below is a copy so this file does not depend on kitCOMBAT being merged first)
// and they do not touch shadow_fixes.json: both can run together (ShadowFixes rows win nothing here, they set different properties).
public static class PerfFixes
{
    [Serializable] public class LightRow { public string name; public string updateMode = "OnEnable"; public bool alwaysDrawDynamic = true; public string note; }
    [Serializable] public class MatchRow { public string match; public bool enabled = true; public string note; }
    [Serializable] public class A1Cfg { public bool enabled = true; public LightRow[] lights = new LightRow[0]; public bool requestRender = true; }
    [Serializable] public class A1bCfg
    {
        public bool enabled = false;
        public float rangeMargin = 0f;                         // a renderer counts when its bounds are within light.range + this of the light
        public string[] excludePathContains = new string[0];   // moving things (orbiting shards, portal, ...) must NOT be cached: any hierarchy path with one of these substrings is skipped
        public bool excludeRoster = true;                      // fighters (Roster object names) are never marked
        public bool excludeRigidbodies = true, excludeAnimators = true;   // (SkinnedMeshRenderers are never touched: only MeshRenderer is scanned)
    }
    [Serializable] public class A4Cfg { public bool enabled = true; public string mode = "Off"; public MatchRow[] renderers = new MatchRow[0]; }
    [Serializable] public class B3Cfg { public bool enabled = false; public float projectorDrawDistanceMax = 80f; public string note; }   // caps DecalProjector.drawDistance (never raises it)
    [Serializable] public class C1Cfg
    {
        public bool enabled = true;
        public bool inHarness = false;         // true: the finisher prewarm also runs in -finisherauto / proof runs (otherwise FxPrewarm stays off there: PerfFlags.prewarmInHarness)
        public bool capMaterials = true;       // MeshCut cap material (outer + inner) built from each fighter's body renderer and drawn once, sub-pixel
        public bool capCastShadows = true;     // draw with shadows ON so the shadow-caster pass pipeline state exists too
        public bool useBodyMesh = true;        // draw the fighter's own (bind pose) mesh sub-pixel instead of a quad, same vertex layout as the severed piece
        public bool pieceStub = true;          // one throw-away kinematic rigidbody + collider + PhysicsMaterial, far away, destroyed after pieceStubFrames (physics shape creation)
        public int pieceStubFrames = 2;
        public float pieceStubDistance = 5000f;
    }
    [Serializable] public class C3Cfg
    {
        public bool enabled = true;
        public bool fromGearJson = true;                       // every items[].glb of gear.json
        public string[] files = new string[0];                 // extra glb files: StreamingAssets-relative, project-relative or absolute
        public int startDelayFrames = 30;                      // after the scene load
        public int framesBetweenFiles = 20;                    // one file at a time, spread over the intro
        public float maxTotalMb = 128f;                        // parsed imports stay in RAM until the loader takes them: files past this total are not preloaded
        public bool inHarness = false;
    }
    [Serializable] public class D1Cfg { public bool enabled = true; public float epsilonRel = 0.002f; public float epsilonEmissiveRel = 0.002f; public string note; }   // write only when the value moved by more than epsilon x the base (0 = any change)
    [Serializable] public class D1bCfg { public bool enabled = true; public string note; }
    [Serializable] public class Cfg
    {
        public bool enabled = true;
        public int applyAfterFrames = 3, secondPassAfterFrames = 60;
        public A1Cfg A1 = new A1Cfg(); public A1bCfg A1b = new A1bCfg(); public A4Cfg A4 = new A4Cfg(); public B3Cfg B3 = new B3Cfg();
        public C1Cfg C1 = new C1Cfg(); public C3Cfg C3 = new C3Cfg(); public D1Cfg D1 = new D1Cfg(); public D1bCfg D1b = new D1bCfg();
    }

    static Cfg cfg;
    static HashSet<string> only, off;   // command line
    public static HashSet<string> AbOnly;   // PERFAB: -perfab arm = "only these ids" (same meaning as -perffix:), null = command line / json
    static bool none;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; only = null; off = null; none = false; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new Cfg { enabled = false };   // no file = all OFF
            var txt = HudCfg.ReadData("perf_fixes.json");
            if (txt != null) { try { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } catch (Exception e) { Debug.LogWarning("[PerfFixes] perf_fixes.json parse failed, all fixes OFF: " + e.Message); cfg = new Cfg { enabled = false }; } }
            Fill(cfg); ParseArgs();
            return cfg;
        }
    }
    static void Fill(Cfg c)
    {
        if (c.A1 == null) c.A1 = new A1Cfg(); if (c.A1.lights == null) c.A1.lights = new LightRow[0];
        if (c.A1b == null) c.A1b = new A1bCfg(); if (c.A1b.excludePathContains == null) c.A1b.excludePathContains = new string[0];
        if (c.A4 == null) c.A4 = new A4Cfg(); if (c.A4.renderers == null) c.A4.renderers = new MatchRow[0];
        if (c.B3 == null) c.B3 = new B3Cfg(); if (c.C1 == null) c.C1 = new C1Cfg(); if (c.C3 == null) c.C3 = new C3Cfg(); if (c.C3.files == null) c.C3.files = new string[0];
        if (c.D1 == null) c.D1 = new D1Cfg(); if (c.D1b == null) c.D1b = new D1bCfg();
    }
    static void ParseArgs()
    {
        only = null; off = new HashSet<string>(StringComparer.OrdinalIgnoreCase); none = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("-noperffixes", StringComparison.OrdinalIgnoreCase)) none = true;
            else if (a.Equals("-perfharness", StringComparison.OrdinalIgnoreCase)) { cfg.C1.inHarness = true; cfg.C3.inHarness = true; }   // let C1 / C3 run in -finisherauto / proof runs (A/B of the finisher stall)
            else if (a.StartsWith("-perffix:", StringComparison.OrdinalIgnoreCase))
            {
                string v = a.Substring(9);
                if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) none = true;
                else if (!v.Equals("json", StringComparison.OrdinalIgnoreCase)) { only = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var s in v.Split(',')) if (s.Trim().Length > 0) only.Add(s.Trim()); }
            }
            else if (a.StartsWith("-noperffix:", StringComparison.OrdinalIgnoreCase)) foreach (var s in a.Substring(11).Split(',')) if (s.Trim().Length > 0) off.Add(s.Trim());
        }
    }

    /// <summary>True when fix `id` is on: command line first (-perffix: / -noperffix:), then perf_fixes.json (master "enabled" and the fix's own "enabled").</summary>
    public static bool On(string id)
    {
        var c = C;
        if (AbOnly != null) return AbOnly.Contains(id);   // PERFAB
        if (none) return false;
        if (off.Contains(id)) return false;
        if (only != null) return only.Contains(id);   // an id listed on the command line is ON even if the json flag is false
        if (!c.enabled) return false;
        switch (id)
        {
            case "A1": return c.A1.enabled;
            case "A1b": return c.A1b.enabled;
            case "A4": return c.A4.enabled;
            case "B3": return c.B3.enabled;
            case "C1": return c.C1.enabled;
            case "C3": return c.C3.enabled;
            case "D1": return c.D1.enabled;
            case "D1b": return c.D1b.enabled;
        }
        return false;
    }

    /// <summary>One line naming the fixes that are on (logged once at start, read by the perfbench scripts).</summary>
    public static string Summary()
    {
        var sb = new System.Text.StringBuilder("[PerfFixes] on:");
        foreach (var id in new[] { "A1", "A1b", "A4", "B3", "C1", "C3", "D1", "D1b" }) if (On(id)) sb.Append(' ').Append(id);
        return sb.ToString();
    }

    public static string PathOf(Transform t) { string p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }   // same as ShadowFixes.PathOf

    // ------------------------------------------------------------------ runtime apply (A1, A1b, A4, B3)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Debug.Log(Summary());
        if (!On("A1") && !On("A1b") && !On("A4") && !On("B3")) return;
        var go = new GameObject("PerfFixes"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<PerfFixesRunner>();
    }

    /// <summary>Applies A1 / A1b / A4 / B3. Returns a one-line summary. Safe to call twice (second pass after the fighters exist).</summary>
    public static string Apply()
    {
        var c = C; int nl = 0, ns = 0, nr = 0, nd = 0; var sb = new System.Text.StringBuilder();
        Light[] lights = null;
        if (On("A1") || On("A1b")) lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // A1: shadow update OnEnable + alwaysDrawDynamic on the named point lights (cached static shadows, fighters still drawn every frame)
        var a1Lights = new List<Light>();
        if (On("A1") && lights != null)
            foreach (var l in lights)
                foreach (var row in c.A1.lights)
                {
                    if (row == null || string.IsNullOrEmpty(row.name) || l.name != row.name) continue;
                    var hd = l.GetComponent<HDAdditionalLightData>(); if (hd == null) { sb.Append(" no-HD:" + l.name); continue; }
                    ShadowUpdateMode m = ShadowUpdateMode.OnEnable;
                    if (!string.IsNullOrEmpty(row.updateMode)) { try { m = (ShadowUpdateMode)Enum.Parse(typeof(ShadowUpdateMode), row.updateMode, true); } catch { Debug.LogWarning("[PerfFixes] A1 bad updateMode '" + row.updateMode + "' for " + row.name); continue; } }
                    if (hd.shadowUpdateMode != m) hd.shadowUpdateMode = m;
                    if (hd.alwaysDrawDynamicShadows != row.alwaysDrawDynamic) hd.alwaysDrawDynamicShadows = row.alwaysDrawDynamic;
                    if (c.A1.requestRender && m != ShadowUpdateMode.EveryFrame) { try { hd.RequestShadowMapRendering(); } catch { } }   // fill the cache now (static casters rendered once)
                    a1Lights.Add(l); nl++;
                }

        // A1b: static props near those lights go into the cached map (Renderer.staticShadowCaster); without it every prop is a "dynamic" caster and A1 saves nothing
        if (On("A1b") && a1Lights.Count > 0)
        {
            var skip = new List<string>(); if (c.A1b.excludeRoster) { try { foreach (var ch in Roster.All) { if (ch != null && !string.IsNullOrEmpty(ch.objectName)) skip.Add(ch.objectName); } } catch { } }
            foreach (var s in c.A1b.excludePathContains) if (!string.IsNullOrEmpty(s)) skip.Add(s);
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (r.staticShadowCaster || r.shadowCastingMode == ShadowCastingMode.Off) continue;
                bool near = false;
                foreach (var l in a1Lights) if (Vector3.Distance(r.bounds.ClosestPoint(l.transform.position), l.transform.position) <= l.range + c.A1b.rangeMargin) { near = true; break; }
                if (!near) continue;
                if (c.A1b.excludeRigidbodies && r.GetComponentInParent<Rigidbody>() != null) continue;
                if (c.A1b.excludeAnimators && r.GetComponentInParent<Animator>() != null) continue;
                string path = PathOf(r.transform); bool bad = false;
                for (int i = 0; i < skip.Count && !bad; i++) if (path.IndexOf(skip[i], StringComparison.Ordinal) >= 0) bad = true;
                if (bad) continue;
                r.staticShadowCaster = true; ns++;
            }
        }

        // A4: ground / terrain / paving renderers stop casting shadows (a floor never shadows anything that is visible)
        if (On("A4") && c.A4.renderers.Length > 0)
        {
            ShadowCastingMode mode = ShadowCastingMode.Off;
            try { mode = (ShadowCastingMode)Enum.Parse(typeof(ShadowCastingMode), c.A4.mode, true); } catch { }
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r is ParticleSystemRenderer || r is SkinnedMeshRenderer || r.shadowCastingMode == mode) continue;
                string path = null;
                foreach (var row in c.A4.renderers)
                {
                    if (row == null || !row.enabled || string.IsNullOrEmpty(row.match)) continue;
                    if (path == null) path = PathOf(r.transform);
                    if (path.IndexOf(row.match, StringComparison.Ordinal) < 0) continue;
                    r.shadowCastingMode = mode; nr++; break;
                }
            }
        }

        // B3 (runtime half): cap every DecalProjector's draw distance; never raises it. The HDRP-asset half (decalSettings.drawDistance / atlas 2048) is in asset_variants.
        if (On("B3") && c.B3.projectorDrawDistanceMax > 0f)
            foreach (var d in UnityEngine.Object.FindObjectsByType<DecalProjector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (d.drawDistance > c.B3.projectorDrawDistanceMax) { d.drawDistance = c.B3.projectorDrawDistanceMax; nd++; }

        return "A1 lights=" + nl + " A1b staticCasters=" + ns + " A4 renderers off=" + nr + " B3 projectors capped=" + nd + sb;
    }
}

// lanePERF3 D1 helpers for the flicker scripts: write a value only when it moved more than eps x its basis since the last WRITE (eps 0 = any change). Every flicker script caches On("D1") / On("D1b") once in Awake/Start.
public static class FlickerGuard
{
    public static float Eps { get { return Mathf.Max(0f, PerfFixes.C.D1.epsilonRel); } }
    public static float EpsEmissive { get { return Mathf.Max(0f, PerfFixes.C.D1.epsilonEmissiveRel); } }
    public static bool Moved(float last, float now, float basis, float eps) { return Mathf.Abs(now - last) > eps * Mathf.Max(1e-6f, Mathf.Abs(basis)); }
}

public class PerfFixesRunner : MonoBehaviour
{
    IEnumerator Start()
    {
        var c = PerfFixes.C;
        for (int i = 0; i < Mathf.Max(1, c.applyAfterFrames); i++) yield return null;   // after RuntimeQuality / ShadowFixes first pass
        Debug.Log("[PerfFixes] " + PerfFixes.Apply());
        for (int i = 0; i < Mathf.Max(1, c.secondPassAfterFrames); i++) yield return null;   // after the fighters are spawned / roster swaps
        Debug.Log("[PerfFixes] second pass: " + PerfFixes.Apply());
        Destroy(gameObject);
    }
}
