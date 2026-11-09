using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

// laneSHADOWPROXY v104 (SHADOWPROXY): SHADOW-ONLY STAND-IN for placed props, one DARK switch (shadow_proxy.json, "enabled": false).
//   Every placed prop keeps its visible LODGroup unchanged (visible mesh, LOD fade, materials). Its renderers stop casting shadows (ShadowCastingMode.Off) and ONE extra
//   renderer per source mesh, outside the LODGroup, casts instead: the mesh of an EXISTING lower LOD of the same prop (ShadowsOnly, no main pass, no receive).
//   Which LOD is a per-asset, per-instance-scale DATA decision made from the GLB geometry (silhouette.py: shadow-silhouette error along the sun on the floor and from the sun,
//   contact footprint error, lowest-point gap). Two zones: FAR (beyond nearM from the camera, the cascade-1 texel is the yardstick) and NEAR (inside nearM, the cascade-0 texel
//   is the yardstick): where the stand-in would be off by more than maxErrTexels the full mesh (LOD0, or LOD1) is the caster again. The swap is two prebuilt renderers
//   toggled with Renderer.enabled, no mesh assignment per frame.
// Combination with OPT3 G1/G3 (GeoCull.cs): this runs first (startDelayFrames 30 < 90), so GeoCull snapshots the LOD renderers as "not casting" (it never touches them)
//   and the stand-ins as "casting" (it removes them when the shadow cannot reach any camera, restores them later). G1 therefore removes the stand-in, not the full mesh.
//   Both write only shadowCastingMode / forceRenderingOff; this one writes shadowCastingMode of the LOD renderers and Renderer.enabled of the stand-ins.
//   If GeoCull restores a LOD renderer to "casting" anyway (data order changed), the slice scan below puts it back to Off.
// Command line (same exe, same data, for A/B arms): -shadowproxy:on | -shadowproxy:none | -shadowproxy:far (no near zone) | -shadowproxyreport | -shadowproxyab[:periodS]
//   (in-process ON/OFF alternation, writes shadowproxy_ab.csv to -benchout:<dir>; same csv format as ShadowCache, read it with laneSHADOWCACHE\ab_report.py. Run it with -geocull:none).
// The sim never reads any of this (render flags and extra shadow-only renderers). The ground is never touched (excludeNameContains).
public static class ShadowProxy
{
    [Serializable] public class Calc
    {
        public float texelNearM = 0.0076f, texelFarM = 0.026f;   // CALC (make_proxy_data.py): shadow texel size of cascade 0 / cascade 1 at the lock-cam FOV and the HDRP shadow settings
        public float nearRangeM = 12f;                           // cascade 0 reaches this far from the camera (split0 x maxDistance)
        public float maxErrTexels = 1f, contactTolM = 0.02f, gapTolM = 0.01f, nearMarginM = 3f;
        public float shadowLenPerHeight = 0.9f;                  // horizontal shadow length per metre of prop height, from the sun vector
    }
    [Serializable] public class AssetRow
    {
        public string key = "", asset = "", library = "";
        public float heightUnit;                                 // LOD0 height at scale 1 (m)
        public float[] errUnit = new float[0];                   // [lod] max(floor, perp) shadow-silhouette error vs LOD0 at scale 1 (m); index 0 = 0
        public float[] contactUnit = new float[0];               // [lod] contact footprint error at scale 1 (m)
        public float[] gapUnit = new float[0];                   // [lod] lowest-point lift above LOD0 at scale 1 (m), > 0 = light leak under the prop
        public int[] tris = new int[0];
    }
    [Serializable] public class Cfg
    {
        public string note = "";
        public bool enabled = false;
        public string arenaRoot = "Arena";
        public string[] extraRoots = new string[0], extraGroups = new string[0];   // FPSX_D2: more scene roots (zone roots Zone2, HubZone, ...) and the ring groups under each; empty = only Arena as before
        public string[] groups = new string[0];
        public string[] excludeNameContains = new string[0];
        public string[] layoutFiles = new string[0];             // prop id -> (library, asset)
        public int startDelayFrames = 30, evalEveryFrames = 6;
        public float hysteresisM = 2f;
        public bool nearEnabled = true;
        public bool log = true; public float logEveryS = 10f;
        public string[] disableWhenArgs = new string[0];
        public float abPeriodS = 20f, abSettleS = 4f; public string abOutFile = "shadowproxy_ab.csv";
        public Calc calc = new Calc();
        public AssetRow[] assets = new AssetRow[0];
    }
    [Serializable] class LayProp { public string id = "", library = "", asset = ""; public bool disabled; }
    [Serializable] class Lay { public LayProp[] props = new LayProp[0]; }

    static Cfg cfg; static bool forceOn, none, farOnly, report, ab;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; forceOn = false; none = false; farOnly = false; report = false; ab = false; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new Cfg();
            var txt = HudCfg.ReadData("shadow_proxy.json");
            if (txt != null) { try { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } catch (Exception e) { Debug.LogWarning("[ShadowProxy] shadow_proxy.json parse failed, OFF: " + e.Message); cfg = new Cfg(); } }
            if (cfg.extraRoots == null) cfg.extraRoots = new string[0]; if (cfg.extraGroups == null) cfg.extraGroups = new string[0]; if (cfg.groups == null) cfg.groups = new string[0]; if (cfg.excludeNameContains == null) cfg.excludeNameContains = new string[0];
            if (cfg.layoutFiles == null) cfg.layoutFiles = new string[0]; if (cfg.disableWhenArgs == null) cfg.disableWhenArgs = new string[0];
            if (cfg.calc == null) cfg.calc = new Calc(); if (cfg.assets == null) cfg.assets = new AssetRow[0];
            ParseArgs();
            return cfg;
        }
    }
    static void ParseArgs()
    {
        forceOn = false; none = false; farOnly = false; report = false; ab = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.Equals("-shadowproxyreport", StringComparison.OrdinalIgnoreCase)) report = true;
            else if (a.StartsWith("-shadowproxyab", StringComparison.OrdinalIgnoreCase))
            {
                ab = true; int ix = a.IndexOf(':'); float per;
                if (ix > 0 && float.TryParse(a.Substring(ix + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out per) && per > 1f) cfg.abPeriodS = per;
            }
            else if (a.StartsWith("-shadowproxy:", StringComparison.OrdinalIgnoreCase))
            {
                string v = a.Substring(13);
                if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) none = true;
                else if (v.Equals("on", StringComparison.OrdinalIgnoreCase)) forceOn = true;
                else if (v.Equals("far", StringComparison.OrdinalIgnoreCase)) { forceOn = true; farOnly = true; }
            }
        }
    }
    public static bool On { get { var c = C; if (none) return false; if (forceOn || ab) return true; return c.enabled; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Debug.Log("[ShadowProxy] on=" + On + " far-only=" + farOnly + " ab=" + ab);
        if (!On) return;
        foreach (var a in Environment.GetCommandLineArgs()) foreach (var d in C.disableWhenArgs) if (!string.IsNullOrEmpty(d) && a.Equals(d, StringComparison.OrdinalIgnoreCase)) { Debug.Log("[ShadowProxy] disabled by argument " + d); return; }
        var go = new GameObject("ShadowProxy"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<ShadowProxyRunner>();
    }

    /// <summary>Deepest LOD (2, then 1) whose shadow stand-in stays inside the error budget for a prop of uniform scale s in a zone with the given texel size; 0 = keep the full mesh.</summary>
    public static int PickLod(AssetRow row, float s, float texelM, Calc c)
    {
        for (int k = Mathf.Min(2, row.errUnit.Length - 1); k >= 1; k--)
        {
            if (k >= row.tris.Length || row.tris[k] <= 0 || row.tris[k] >= row.tris[0]) continue;
            float contact = k < row.contactUnit.Length ? row.contactUnit[k] : 0f, gap = k < row.gapUnit.Length ? row.gapUnit[k] : 0f;
            if (row.errUnit[k] * s <= c.maxErrTexels * texelM && contact * s <= c.contactTolM && gap * s <= c.gapTolM) return k;
        }
        return 0;
    }

    [DefaultExecutionOrder(900)]
    public sealed class ShadowProxyRunner : MonoBehaviour
    {
        sealed class Entry
        {
            public Transform root; public Renderer[] lodRs; public ShadowCastingMode[] orig;
            public Renderer[] farSet, nearSet; public int farLod, nearLod;
            public Vector3 c; public float r, nearM; public bool near;
        }
        readonly List<Entry> entries = new List<Entry>(); readonly Camera[] camBuf = new Camera[32]; readonly List<Camera> cams = new List<Camera>();
        bool inited, active; int frame, cursor, nProps, nNoRow, nNoCast, nSkipped, nSwapped; float lastLog;
        long trisFull, trisProxy;
        // A/B
        bool abOn = true; float abStartT, abSwitchT; readonly List<string> abRows = new List<string>(); string abPath; float abFlushT;

        void Update()
        {
            frame++;
            if (!inited) { if (frame < C.startDelayFrames) return; Init(); inited = true; }
            if (ab) AbStep();
            if (!active) return;
            EvalSlice();
            if (C.log && report && Time.unscaledTime - lastLog > C.logEveryS) { lastLog = Time.unscaledTime; Debug.Log(Stat()); }
        }
        string Stat() { return "[ShadowProxy] stat entries=" + entries.Count + " props=" + nProps + " noRow=" + nNoRow + " noCaster=" + nNoCast + " nearNow=" + CountNear() + " active=" + active + " casterTrisFull=" + trisFull + " casterTrisProxyFar=" + trisProxy; }
        int CountNear() { int n = 0; for (int i = 0; i < entries.Count; i++) if (entries[i].near) n++; return n; }
        void OnDestroy() { SetActive(false); if (ab) AbFlush(); }
        void OnApplicationQuit() { SetActive(false); if (ab) AbFlush(); }

        void Init()
        {
            var ids = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var f in C.layoutFiles)
            {
                if (string.IsNullOrEmpty(f)) continue; var txt = HudCfg.ReadData(f); if (txt == null) continue;
                Lay L = null; try { L = JsonUtility.FromJson<Lay>(txt); } catch (Exception e) { Debug.LogWarning("[ShadowProxy] " + f + " parse failed: " + e.Message); }
                if (L == null || L.props == null) continue;
                foreach (var p in L.props) if (p != null && !string.IsNullOrEmpty(p.id) && !ids.ContainsKey(p.id)) ids[p.id] = new[] { p.library, p.asset };
            }
            var rows = new Dictionary<string, AssetRow>(StringComparer.Ordinal);
            foreach (var r in C.assets) if (r != null && !string.IsNullOrEmpty(r.key)) rows[r.key] = r;
            var arena = GameObject.Find(C.arenaRoot); if (arena == null) { Debug.LogWarning("[ShadowProxy] arena root '" + C.arenaRoot + "' not found, off"); return; }
            var cal = C.calc;
            var gl = new List<Transform>();   // FPSX_D2: Arena groups first (unchanged order), then the ring groups of the extra roots
            foreach (var gname in C.groups) { var g0 = arena.transform.Find(gname); if (g0 != null) gl.Add(g0); }
            if (C.extraRoots.Length > 0) foreach (var sr in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var rn in C.extraRoots) if (!string.IsNullOrEmpty(rn) && sr.name == rn) foreach (var gn in C.extraGroups) { var g1 = sr.transform.Find(gn); if (g1 != null) gl.Add(g1); }   // roots found among the scene roots, so an inactive root works
            foreach (var g in gl)
            {
                for (int i = 0; i < g.childCount; i++)
                {
                    var t = g.GetChild(i); bool skip = false;
                    foreach (var ex in C.excludeNameContains) if (!string.IsNullOrEmpty(ex) && t.name.IndexOf(ex, StringComparison.OrdinalIgnoreCase) >= 0) { skip = true; break; }
                    if (skip) continue;
                    var lg = t.GetComponent<LODGroup>(); if (lg == null) continue;
                    nProps++;
                    string[] la; AssetRow row = null;
                    if (ids.TryGetValue(t.name, out la)) rows.TryGetValue(la[0] + "/" + la[1], out row);
                    if (row == null) { nNoRow++; continue; }
                    var e = Build(t, row, cal); if (e != null) entries.Add(e); else nSkipped++;
                }
            }
            Debug.Log("[ShadowProxy] init props=" + nProps + " withProxy=" + entries.Count + " noRow=" + nNoRow + " noCaster=" + nNoCast + " skipped=" + nSkipped + " fullCasterTris=" + trisFull + " farProxyTris=" + trisProxy + " nearZone=" + (C.nearEnabled && !farOnly));
            if (ab) AbOpen();
            SetActive(true);
        }

        Entry Build(Transform root, AssetRow row, Calc cal)
        {
            float s = Mathf.Abs(root.lossyScale.x); if (s < 1e-4f) return null;
            var all = root.GetComponentsInChildren<MeshRenderer>(true); if (all.Length == 0) return null;
            bool casts = false; for (int i = 0; i < all.Length; i++) if (all[i].shadowCastingMode != ShadowCastingMode.Off) { casts = true; break; }
            if (!casts) { nNoCast++; return null; }                                  // shadowsOff props stay as they are
            int farLod = PickLod(row, s, cal.texelFarM, cal), nearLod = (C.nearEnabled && !farOnly) ? PickLod(row, s, cal.texelNearM, cal) : farLod;
            if (farLod == 0) return null;                                            // the stand-in would be visible even far away: keep the full mesh
            if (nearLod > farLod) nearLod = farLod;
            var e = new Entry { root = root, farLod = farLod, nearLod = nearLod };
            e.farSet = BuildSet(root, farLod); if (e.farSet == null) return null;
            if (nearLod == farLod) e.nearSet = e.farSet;
            else { e.nearSet = BuildSet(root, nearLod); if (e.nearSet == null) e.nearSet = e.farSet; }
            e.lodRs = new Renderer[all.Length]; e.orig = new ShadowCastingMode[all.Length];
            var proxies = new HashSet<Renderer>(e.farSet); foreach (var r in e.nearSet) proxies.Add(r);
            int n = 0; Bounds b = default(Bounds); bool first = true;
            for (int i = 0; i < all.Length; i++)
            {
                if (proxies.Contains(all[i])) continue;
                e.lodRs[n] = all[i]; e.orig[n] = all[i].shadowCastingMode; n++;
                if (first) { b = RBounds(all[i]); first = false; } else b.Encapsulate(RBounds(all[i]));
            }
            Array.Resize(ref e.lodRs, n); Array.Resize(ref e.orig, n);
            e.c = b.center; e.r = b.extents.magnitude;
            e.nearM = cal.nearRangeM + cal.nearMarginM + row.heightUnit * s * cal.shadowLenPerHeight;
            trisFull += FarTris(row, 0); trisProxy += FarTris(row, farLod);
            return e;
        }

        // FPSX_D2: Renderer.bounds under an INACTIVE ring is not trusted; build the world bounds from the mesh bounds then. Active renderers: r.bounds exactly as before.
        static Bounds RBounds(Renderer r)
        {
            if (r.gameObject.activeInHierarchy) return r.bounds;
            var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) return r.bounds;
            var m = mf.transform.localToWorldMatrix; var mb = mf.sharedMesh.bounds; var res = new Bounds(m.MultiplyPoint3x4(mb.center), Vector3.zero);
            for (int i = 0; i < 8; i++) res.Encapsulate(m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));   // hc:ok box corner signs
            return res;
        }

        Renderer[] BuildSet(Transform root, int lod)
        {
            var lt = root.Find("LOD" + lod); if (lt == null) return null;
            var made = new List<Renderer>();
            foreach (var mf in lt.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.GetComponent<MeshRenderer>(); if (src == null || mf.sharedMesh == null) continue;
                var go = new GameObject("SP_" + mf.name); go.layer = mf.gameObject.layer;
                var tr = go.transform; tr.SetParent(mf.transform.parent, false);
                tr.localPosition = mf.transform.localPosition; tr.localRotation = mf.transform.localRotation; tr.localScale = mf.transform.localScale;
                var nf = go.AddComponent<MeshFilter>(); nf.sharedMesh = mf.sharedMesh;
                var nr = go.AddComponent<MeshRenderer>();
                nr.sharedMaterials = src.sharedMaterials; nr.shadowCastingMode = ShadowCastingMode.ShadowsOnly; nr.receiveShadows = false;
                nr.lightProbeUsage = LightProbeUsage.Off; nr.reflectionProbeUsage = ReflectionProbeUsage.Off; nr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                nr.renderingLayerMask = src.renderingLayerMask; nr.staticShadowCaster = src.staticShadowCaster;
                nr.enabled = false; made.Add(nr);
            }
            return made.Count > 0 ? made.ToArray() : null;
        }

        void CollectCams()
        {
            cams.Clear(); int n = Camera.GetAllCameras(camBuf);
            for (int i = 0; i < n; i++) { var c = camBuf[i]; if (c != null && c.isActiveAndEnabled && c.cameraType == CameraType.Game) cams.Add(c); }
        }

        void EvalSlice()
        {
            int n = entries.Count; if (n == 0) return;
            int slice = Mathf.Max(1, Mathf.CeilToInt(n / (float)Mathf.Max(1, C.evalEveryFrames)));
            CollectCams();
            for (int k = 0; k < slice; k++)
            {
                if (cursor >= n) cursor = 0;
                var e = entries[cursor++]; if (e.root == null) continue;
                for (int i = 0; i < e.lodRs.Length; i++) { var r = e.lodRs[i]; if (r != null && r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off; }   // someone restored it
                bool want = e.near;
                if (e.nearSet != e.farSet)
                {
                    float d = float.MaxValue; for (int j = 0; j < cams.Count; j++) d = Mathf.Min(d, (cams[j].transform.position - e.c).magnitude - e.r);
                    float lim = e.nearM + (e.near ? C.hysteresisM : 0f);
                    want = cams.Count > 0 && d < lim;
                }
                SetZone(e, want, true);
            }
        }

        static void SetZone(Entry e, bool near, bool force)
        {
            if (!force && near == e.near) return;
            bool changed = near != e.near; e.near = near;
            if (!changed && !force) return;
            var on = (near && e.nearSet != null) ? e.nearSet : e.farSet; var off = (on == e.farSet) ? e.nearSet : e.farSet;
            if (off != null && off != on) for (int i = 0; i < off.Length; i++) if (off[i] != null && off[i].enabled) off[i].enabled = false;
            for (int i = 0; i < on.Length; i++) if (on[i] != null && !on[i].enabled) on[i].enabled = true;
        }

        void SetActive(bool a)
        {
            active = a;
            foreach (var e in entries)
            {
                if (e.root == null) continue;
                if (a) { for (int i = 0; i < e.lodRs.Length; i++) if (e.lodRs[i] != null) e.lodRs[i].shadowCastingMode = ShadowCastingMode.Off; SetZone(e, e.near, true); }
                else
                {
                    for (int i = 0; i < e.lodRs.Length; i++) if (e.lodRs[i] != null) e.lodRs[i].shadowCastingMode = e.orig[i];
                    if (e.farSet != null) foreach (var r in e.farSet) if (r != null) r.enabled = false;
                    if (e.nearSet != null) foreach (var r in e.nearSet) if (r != null) r.enabled = false;
                }
            }
        }

        // ---------------------------------------------------------------- in-process A/B (same csv as ShadowCache: t_s,frame_ms,state,settle,mode)
        void AbOpen()
        {
            string dir = Application.persistentDataPath;
            foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-benchout:", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(10).Trim('"');
            try { Directory.CreateDirectory(dir); abPath = Path.Combine(dir, C.abOutFile); File.WriteAllText(abPath, "t_s,frame_ms,state,settle,mode\n"); }
            catch (Exception e) { Debug.LogWarning("[ShadowProxy] ab file: " + e.Message); abPath = null; }
            abStartT = abSwitchT = Time.unscaledTime; abOn = true; abFlushT = abStartT;
            Debug.Log("[ShadowProxy] ab recorder -> " + abPath + " period=" + C.abPeriodS + "s settle=" + C.abSettleS + "s startOn=true");
        }
        void AbStep()
        {
            float now = Time.unscaledTime;
            if (now - abSwitchT >= C.abPeriodS) { abOn = !abOn; abSwitchT = now; SetActive(abOn); Debug.Log("[ShadowProxy] ab -> " + (abOn ? "ON" : "OFF") + " t=" + (now - abStartT).ToString("F1")); }
            abRows.Add((now - abStartT).ToString("F3", CultureInfo.InvariantCulture) + "," + (Time.unscaledDeltaTime * 1000f).ToString("F3", CultureInfo.InvariantCulture) + "," + (abOn ? 1 : 0) + "," + (((now - abSwitchT) < C.abSettleS) ? 1 : 0) + ",SP");
            if (now - abFlushT > 5f) AbFlush();
        }
        void AbFlush()
        {
            abFlushT = Time.unscaledTime; if (abPath == null || abRows.Count == 0) return;
            try { File.AppendAllText(abPath, string.Join("\n", abRows.ToArray()) + "\n"); abRows.Clear(); } catch { }
        }
    }

    static int FarTris(AssetRow row, int lod) { return lod >= 0 && lod < row.tris.Length ? row.tris[lod] : 0; }
}
