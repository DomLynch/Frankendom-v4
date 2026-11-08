#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// SHADOW DIAGNOSTIC (lane SHADOW, v75): -shadowdiag [-out:<dir>]   (run at 1920x1080 with the default runtime_flags.json: -screen-width 1920 -screen-height 1080 -screen-fullscreen 0)
// Finds the floating dark floor shadow blobs in one run. All numbers come from Assets/Data/shadow_fixes.json "diag" (floor rectangle, pose, captures, thresholds).
//  1. Places both fighters at the v66 wide pose in front of the REAL lock-on CameraRig (brains off, flicker scripts frozen so captures are comparable).
//  2. INVENTORY (shadowdiag_log.txt): every Light with shadows on (type, range, intensity, position, update mode), and for each one the renderers with shadowCastingMode != Off that
//     are enabled + active and (point/spot) inside the light range: name, hierarchy path, materials, bounds, isVisible, flags (TRANSPARENT / ALPHACLIP / ALPHA0 / INVISIBLE / SHADOWSONLY /
//     PARTICLE / TRAIL), and the FOOTPRINT of the bounds projected from the light onto the floor plane. Listed: every caster whose footprint overlaps the floor rectangle (largest overlap first)
//     and the topN largest by bounds volume. STRETCHED = a bounds corner is above the light (point light) so the shadow runs out to the light range.
//  3. CAPTURES (diag.captures, default 7): baseline; DoorShadowWarm off; DoorwayFlickerLight off; both off; all point+spot shadows off; Key_ColdSun off; Key_ColdSun EveryFrame (tests the cached map).
//     Each capture: settleFrames frames, shadowdiag_<id>.png, luma stats of the floor region, and the pixels that got BRIGHTER than the baseline by brightenDelta (count, bbox, centroid in screen
//     fractions): the capture whose "brightened" count is large and sits where the blobs are, names the cause.
//  4. shadowdiag_summary.txt with a VERDICT line per capture and a RANKING by brightened pixels. Exit 0 always on a completed run (it is a diagnostic), 2 exception / setup failure, 3 timeout.
// Nothing on disk besides <out> is changed. Time.captureFramerate = 60.
[DefaultExecutionOrder(-60)]
public class ShadowDiag : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F2", IC); }
    static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-shadowdiag")) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<ShadowDiag>() != null) return;
        var go = new GameObject("ShadowDiag"); DontDestroyOnLoad(go); go.AddComponent<ShadowDiag>();
    }

    string dir, logPath; float t0, timeoutS = 240f; bool exiting; int exitCode; float exitAt;
    DuelFighter D, P; CameraRig rig; ShadowFixes.Diag cfg;
    readonly List<string> summary = new List<string>();
    class LightState { public Light l; public LightShadows s; public HDAdditionalLightData hd; public ShadowUpdateMode um; public bool hasHd; }
    readonly List<LightState> lights = new List<LightState>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(6) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[shadowdiag] " + s);
    }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "shadowdiag"); } catch { dir = "shadowdiag"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "shadowdiag_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        cfg = ShadowFixes.Get().diag;
    }
    void Start() { StartCoroutine(Guarded(Main())); }
    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > timeoutS) { Log("TIMEOUT after " + timeoutS.ToString("F0", IC) + " s"); Finish(3, "timeout"); }
    }
    IEnumerator Guarded(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Log("EXCEPTION: " + ex); Finish(2, "exception"); yield break; }
            yield return cur;
        }
    }
    IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    void Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; if (facing.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }

    // ---------------------------------------------------------------- inventory
    class Foot { public Renderer r; public float overlap, volume; public Vector4 rect; public bool stretched; public string flags; }

    static Vector4 FloorRect(float[] a) { return a != null && a.Length >= 4 ? new Vector4(a[0], a[1], a[2], a[3]) : new Vector4(-6f, 6f, 4f, 24f); }
    static float Overlap(Vector4 a, Vector4 b)   // x0 x1 z0 z1
    {
        float x = Mathf.Max(0f, Mathf.Min(a.y, b.y) - Mathf.Max(a.x, b.x)), z = Mathf.Max(0f, Mathf.Min(a.w, b.w) - Mathf.Max(a.z, b.z)); return x * z;
    }
    static string MatInfo(Renderer r, out string flags)
    {
        var sb = new StringBuilder(); var fl = new StringBuilder();
        Material[] ms; try { ms = r.sharedMaterials; } catch { ms = new Material[0]; }
        foreach (var m in ms)
        {
            if (m == null) { sb.Append("<null>;"); continue; }
            sb.Append(m.name).Append('[').Append(m.shader != null ? m.shader.name : "?").Append(" q").Append(m.renderQueue).Append("];");
            if (m.renderQueue >= 3000) fl.Append("TRANSPARENT ");
            if (m.HasProperty("_AlphaCutoffEnable") && m.GetFloat("_AlphaCutoffEnable") > 0.5f) fl.Append("ALPHACLIP ");
            Color c; bool has = false; c = Color.white;
            if (m.HasProperty("_BaseColor")) { c = m.GetColor("_BaseColor"); has = true; } else if (m.HasProperty("_UnlitColor")) { c = m.GetColor("_UnlitColor"); has = true; }
            if (has && c.a < 0.02f) fl.Append("ALPHA0 ");
        }
        flags = fl.ToString(); return sb.ToString();
    }
    string RendererLine(Foot f)
    {
        var r = f.r; string flags; string mats = MatInfo(r, out flags);
        if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) flags += "SHADOWSONLY ";
        if (r is ParticleSystemRenderer) flags += "PARTICLE ";
        if (r is TrailRenderer) flags += "TRAIL ";
        if (!r.isVisible) flags += "NOT_VISIBLE_TO_CAMERA ";
        if (f.stretched) flags += "STRETCHED ";
        if (r.GetComponentInParent<LODGroup>() != null) flags += "LOD ";
        if (r.staticShadowCaster) flags += "STATICSHADOWCASTER ";
        var b = r.bounds;
        return "  " + r.name + "  path=" + ShadowFixes.PathOf(r.transform) + "  mode=" + r.shadowCastingMode + "  vis=" + r.isVisible + "  layer=" + r.gameObject.layer + " rmask=" + r.renderingLayerMask
            + "  bounds c" + V(b.center) + " size" + V(b.size) + "  footprint x" + F(f.rect.x) + ".." + F(f.rect.y) + " z" + F(f.rect.z) + ".." + F(f.rect.w) + " overlapM2=" + F(f.overlap)
            + "  mats=" + mats + "  flags=" + flags.Trim();
    }
    Foot Project(Renderer r, Light l, Vector4 floor)
    {
        var b = r.bounds; var f = new Foot { r = r, volume = b.size.x * b.size.y * b.size.z };
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue; int n = 0;
        float fy = cfg.floorY;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
            Vector3 p;
            if (l.type == LightType.Directional)
            {
                Vector3 d = l.transform.forward; if (d.y > -0.01f) continue;
                p = c + d * ((fy - c.y) / d.y);
            }
            else
            {
                Vector3 lp = l.transform.position; Vector3 d = c - lp;
                if (c.y < lp.y - 0.01f) p = lp + d * ((fy - lp.y) / d.y);
                else { Vector3 h = new Vector3(d.x, 0f, d.z); if (h.sqrMagnitude < 1e-6f) continue; p = lp + h.normalized * l.range; p.y = fy; f.stretched = true; }
                if (new Vector2(p.x - lp.x, p.z - lp.z).magnitude > l.range) { Vector3 h = new Vector3(p.x - lp.x, 0f, p.z - lp.z).normalized * l.range; p = new Vector3(lp.x + h.x, fy, lp.z + h.z); }
            }
            x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z); n++;
        }
        if (n == 0) { f.rect = Vector4.zero; f.overlap = 0f; return f; }
        f.rect = new Vector4(x0, x1, z0, z1); f.overlap = Overlap(f.rect, floor);
        return f;
    }
    void Inventory()
    {
        Vector4 floor = FloorRect(cfg.floorRect);
        var all = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        Log("INVENTORY renderers active=" + all.Length + " floor rect x" + F(floor.x) + ".." + F(floor.y) + " z" + F(floor.z) + ".." + F(floor.w) + " floorY " + F(cfg.floorY));
        foreach (var ls in lights)
        {
            var l = ls.l;
            if (!l.enabled || !l.gameObject.activeInHierarchy || l.shadows == LightShadows.None) continue;
            Log("LIGHT " + l.name + " type=" + l.type + " range=" + F(l.range) + " intensity=" + F(l.intensity) + " pos" + V(l.transform.position) + " fwd" + V(l.transform.forward) + " shadows=" + l.shadows
                + (ls.hasHd ? " res=" + ls.hd.shadowResolution.@override + " update=" + ls.hd.shadowUpdateMode + " alwaysDrawDynamic=" + ls.hd.alwaysDrawDynamicShadows : ""));
            var foots = new List<Foot>();
            foreach (var r in all)
            {
                if (!r.enabled || r.shadowCastingMode == ShadowCastingMode.Off || r is SpriteRenderer) continue;
                if (l.type != LightType.Directional)
                {
                    var bb = r.bounds; if (Vector3.Distance(bb.ClosestPoint(l.transform.position), l.transform.position) > l.range) continue;
                }
                foots.Add(Project(r, l, floor));
            }
            foots.Sort((a, b) => b.overlap.CompareTo(a.overlap));
            int nOv = 0; foreach (var f in foots) if (f.overlap > 0f) nOv++;
            Log("  casters (mode != Off, enabled, active" + (l.type != LightType.Directional ? ", inside range" : "") + ") = " + foots.Count + "; footprint overlaps the floor rect = " + nOv + " (listed largest first, max " + cfg.maxOverlapRows + ")");
            int shown = 0; foreach (var f in foots) { if (f.overlap <= 0f || shown >= cfg.maxOverlapRows) break; Log(RendererLine(f)); shown++; }
            foots.Sort((a, b) => b.volume.CompareTo(a.volume));
            Log("  top " + cfg.topN + " casters by bounds volume:");
            for (int i = 0; i < foots.Count && i < cfg.topN; i++) Log(RendererLine(foots[i]));
            // fighters: their own shadow is expected; log the per-fighter distance so the point-light shadow length can be read against the screenshot
            if (l.type != LightType.Directional)
                foreach (var fh in new[] { D, P }) { if (fh == null) continue; Vector3 d = fh.transform.position - l.transform.position; Log("  fighter " + fh.name + " at " + V(fh.transform.position) + " is " + F(new Vector2(d.x, d.z).magnitude) + " m (flat) from this light, light height " + F(l.transform.position.y) + " m: a point at height h casts to flat distance d*Lh/(Lh-h) beyond the light (h=0.5 -> x" + F(l.transform.position.y / Mathf.Max(0.01f, l.transform.position.y - 0.5f)) + ", h=1.0 -> x" + F(l.transform.position.y / Mathf.Max(0.01f, l.transform.position.y - 1.0f)) + ")"); }
        }
    }

    // ---------------------------------------------------------------- captures
    void RestoreLights()
    {
        foreach (var ls in lights)
        {
            if (ls.l == null) continue;
            if (ls.hasHd) { ls.hd.EnableShadows(ls.s != LightShadows.None); ls.hd.shadowUpdateMode = ls.um; }
            ls.l.shadows = ls.s;
        }
    }
    void ShadowsOff(LightState ls) { ls.l.shadows = LightShadows.None; if (ls.hasHd) ls.hd.EnableShadows(false); }
    void ApplyCapture(ShadowFixes.Capture c)
    {
        RestoreLights();
        foreach (var ls in lights)
        {
            if (ls.l == null) continue;
            bool off = false;
            if (c.lightsOff != null) foreach (var n in c.lightsOff) if (ls.l.name == n) off = true;
            if (c.allPointSpotOff && (ls.l.type == LightType.Point || ls.l.type == LightType.Spot)) off = true;
            if (off) ShadowsOff(ls);
            if (c.keyEveryFrame && ls.l.type == LightType.Directional && ls.hasHd && ls.l.shadows != LightShadows.None) ls.hd.shadowUpdateMode = ShadowUpdateMode.EveryFrame;
        }
    }
    class Shot { public string id; public Color32[] px; public int w, h; public float regionMean, darkFrac; public int brightened, darkened; public Vector2 centroid; public Vector4 bbox; public string lightsOff; }
    static float Luma(Color32 c) { return (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f; }
    IEnumerator Grab(string id, Shot baseline, Action<Shot> done)
    {
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        CaptureUtil.HideConsole(); try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("SHOT failed " + id + ": " + e.Message); }
        if (tex == null) { done(null); yield break; }
        var s = new Shot { id = id, px = tex.GetPixels32(), w = tex.width, h = tex.height };
        try { File.WriteAllBytes(Path.Combine(dir, "shadowdiag_" + id + ".png"), tex.EncodeToPNG()); } catch (Exception e) { Log("SHOT write failed " + id + ": " + e.Message); }
        Destroy(tex);
        float[] rg = cfg.screenRegion != null && cfg.screenRegion.Length >= 4 ? cfg.screenRegion : new float[] { 0f, 0f, 1f, 0.55f };
        int x0 = Mathf.Clamp((int)(rg[0] * s.w), 0, s.w - 1), x1 = Mathf.Clamp((int)(rg[2] * s.w), 1, s.w), y0 = Mathf.Clamp((int)(rg[1] * s.h), 0, s.h - 1), y1 = Mathf.Clamp((int)(rg[3] * s.h), 1, s.h);
        double sum = 0; int dark = 0, cnt = 0; double cx = 0, cy = 0; int bx0 = int.MaxValue, bx1 = -1, by0 = int.MaxValue, by1 = -1;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * s.w + x; float l = Luma(s.px[i]); sum += l; cnt++; if (l < cfg.darkLuma) dark++;
                if (baseline != null && baseline.px != null && baseline.px.Length == s.px.Length)
                {
                    float d = l - Luma(baseline.px[i]);
                    if (d > cfg.brightenDelta) { s.brightened++; cx += x; cy += y; if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y; }
                    else if (d < -cfg.brightenDelta) s.darkened++;
                }
            }
        s.regionMean = cnt > 0 ? (float)(sum / cnt) : 0f; s.darkFrac = cnt > 0 ? (float)dark / cnt : 0f;
        if (s.brightened > 0) { s.centroid = new Vector2((float)(cx / s.brightened) / s.w, (float)(cy / s.brightened) / s.h); s.bbox = new Vector4((float)bx0 / s.w, (float)by0 / s.h, (float)(bx1 + 1) / s.w, (float)(by1 + 1) / s.h); }
        done(s);
    }

    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup; CameraRig rig0 = null;
        while ((D = Find(Roster.PlayerObject)) == null || (P = Find(Roster.EnemyObject)) == null || (rig0 = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > 20f) { Log("fighters / CameraRig not found"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        rig = rig0;
        for (int i = 0; i < 30; i++) yield return null;
        Time.captureFramerate = 60; QualitySettings.vSyncCount = 0;
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (b is PlayerBrain || b is AIBrain || b is TouchControls || b is DoorwayFlicker || b is AtmosFlicker || b is FurnaceFlicker || b is CrackFlicker || b is Flicker) b.enabled = false;   // brains off, flicker frozen at its current value
        D.opponent = P; P.opponent = D; P.maxHealth = 1000f; P.health = 1000f; D.maxHealth = 1000f; D.health = 1000f;
        rig.lockEnabled = true; rig.lockRange = 80f;
        Vector3 anchor = new Vector3(cfg.enemyAnchor[0], cfg.enemyAnchor[1], cfg.enemyAnchor[2]), axis = new Vector3(cfg.axis[0], 0f, cfg.axis[2]).normalized;
        Place(P, anchor, -axis); Place(D, anchor - axis * cfg.sepM, axis);
        D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; D.guardHeld = false; P.guardHeld = false;
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var ls = new LightState { l = l, s = l.shadows, hd = l.GetComponent<HDAdditionalLightData>() }; ls.hasHd = ls.hd != null; if (ls.hasHd) ls.um = ls.hd.shadowUpdateMode; lights.Add(ls);
        }
        Log("start res " + Screen.width + "x" + Screen.height + " lights=" + lights.Count + " settleFrames=" + cfg.settleFrames + " fast=" + RuntimeQuality.Fast + " flags=" + RuntimeQuality.FlagsPath);
        yield return Frames(cfg.settleFrames);
        Inventory();

        var caps = ShadowFixes.Get().diag.captures;
        if (caps == null || caps.Length == 0) { Log("no captures in shadow_fixes.json diag.captures"); Finish(2, "no captures"); yield break; }
        Shot baseShot = null; var shots = new List<Shot>();
        foreach (var c in caps)
        {
            ApplyCapture(c);
            yield return Frames(cfg.settleFrames);
            Shot s = null; yield return Grab(c.id, baseShot, r => s = r);
            if (s == null) { summary.Add(c.id + ": capture failed"); continue; }
            s.lightsOff = (c.lightsOff != null ? string.Join("+", c.lightsOff) : "") + (c.allPointSpotOff ? "ALL_POINT_SPOT" : "") + (c.keyEveryFrame ? " key=EveryFrame" : "");
            if (baseShot == null) baseShot = s;
            shots.Add(s);
            string row = c.id + " [" + (string.IsNullOrEmpty(s.lightsOff) ? "baseline" : s.lightsOff) + "]: floor-region mean luma " + s.regionMean.ToString("F4", IC) + " dark<" + cfg.darkLuma.ToString("F3", IC) + " frac " + s.darkFrac.ToString("F4", IC)
                + (s == baseShot ? "" : "  BRIGHTENED_vs_baseline " + s.brightened + " px (darkened " + s.darkened + ")" + (s.brightened > 0 ? " centroid (" + F(s.centroid.x) + "," + F(s.centroid.y) + ") bbox x" + F(s.bbox.x) + ".." + F(s.bbox.z) + " y" + F(s.bbox.y) + ".." + F(s.bbox.w) : ""));
            summary.Add(row); Log(row);
        }
        RestoreLights();
        // ranking: which arm removed the most dark pixels
        var rank = new List<Shot>(shots); rank.RemoveAll(x => x == baseShot); rank.Sort((a, b) => b.brightened.CompareTo(a.brightened));
        summary.Add("RANKING (most pixels brightened = the shadow that arm removed; a blob cause removes a compact region, see bbox):");
        foreach (var r in rank) summary.Add("  " + r.id + ": " + r.brightened + " px");
        foreach (var r in rank) Log("RANK " + r.id + " " + r.brightened);
        Finish(0, "done");
    }

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; Time.captureFramerate = 0; Time.timeScale = 1f;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("shadowdiag_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height);
            sb.AppendLine("VERDICT: " + (code == 0 ? "COMPLETE" : "FAIL") + "   (exit code " + code + ", " + why + ")   floor rect + captures: Assets/Data/shadow_fixes.json diag");
            foreach (var r in summary) sb.AppendLine("  " + r);
            File.WriteAllText(Path.Combine(dir, "shadowdiag_summary.txt"), sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[shadowdiag] summary write failed: " + e.Message); }
        Log("FINISH " + why + " exit " + code);
    }
}
#else // FIXHARNESS v1 nl=0
#endif // FIXHARNESS v1
