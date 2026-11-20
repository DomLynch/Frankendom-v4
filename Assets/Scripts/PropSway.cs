using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

// PROPSWAY v1 (fk_propsway, 20 Nov 2026). NOT APPLIED. UNPROVEN until compiled and run. PRESENTATION ONLY. COMPILE-ONLY: a static class, no MonoBehaviour, no scene object, no [SerializeField], no new boot hook.
// Hooks (two one-line calls in MatAdjustRuntime.cs, same pattern as DetailPackRuntime.Tick): PropSway.Tick(force) inside MatAdjustRuntime.Scan (find props, reload propsway.json),
//   PropSway.Frame() inside MatAdjustDriver.Update (the one per-frame driver; no per-object Update). Both ride the MatAdjustDriver that MatAdjustRuntime.Boot already registered in the live player.
// What: a very small, slow rigid sway on BAKED prop wrappers picked by GameObject name (the wrapper named by the props_layout id, with a LODGroup). The mesh is not bent: the wrapper transform is rotated.
//   kind chain  = swing about the TOP of the renderer bounds, perpendicular to the wind, tip downwind, lagged (chains, hanging lamps)
//   kind cloth  = yaw twist about the vertical axis through the wrapper origin (a banner pole stays upright, the cloth hanging off the axis swings)
//   kind branch = lean about the BASE, perpendicular to the wind (trees: a fraction of a degree, a rigid trunk must not visibly bend)
// Motion: angle = amp x (0.2 x windScale + 0.8 x osc), osc = 0.72 sin(2 pi t / P + phase) + 0.28 sin(2 pi t / (0.41 P) + 1.7 phase), then a first order lag (lagS). Phase and period (+-periodJitter) come from a hash of the object name,
//   so neighbours never swing in step. amp = maxDeg x ((1 - windGain) + windGain x windScale). windScale = |Wind.Vec| / refSpeedMps (Wind.cs, zone by z) when wind.json is enabled, else a slow own gust around 1 and a fixed direction.
// Cost cap: only items within rangeM (40) of the camera, at most maxMovers (64) at once, nearest first, selection every selectSeconds (0.5), one pass per frame over a preallocated list, no allocation per frame.
// Static batching: an item with any Renderer.isPartOfStaticBatch is skipped (a combined mesh cannot move) and counted. Items under an Animator are skipped. If another script moves the wrapper (PropsOverlay), the new pose becomes the base.
// Switch: propsway.json mode "off" (shipped) or a missing file = nothing runs and every pose is put back exactly. "on" = rows. Editor: does nothing. Writes only Transform position/rotation of matched wrappers; never a material, never a property block.
public static class PropSway
{
    [Serializable] public class Row
    {
        public string match = "";                  // part of the GameObject name (ordinal, ignore case)
        public string[] exclude = new string[0];   // any of these in the name = skip
        public string zone = "";                   // optional: part of the scene root / zone root name the prop sits under ("" = any)
        public float zMin = -100000f, zMax = 100000f;   // optional world z band of the prop origin, [zMin, zMax)
        public string kind = "chain";              // chain | cloth | branch
        public float maxDeg = -1f;                 // peak angle; -1 = kind default (chain 3, cloth 4.5, branch 0.35)
        public float periodS = -1f;                // main period in seconds; -1 = kind default (chain 4.2, cloth 5.5, branch 8)
        public float windGain = -1f;               // 0 = amplitude ignores wind speed, 1 = proportional; -1 = kind default
        public float lagS = -1f;                   // first order lag of the angle; -1 = kind default (chain 0.35, cloth 0.15, branch 0.6)
        public float phaseJitter = 1f;             // 0..1 share of a full cycle taken from the name hash
        public float periodJitter = 0.12f;         // +- share of periodS taken from the name hash
        public string pivot = "";                  // "" = kind default (chain top, cloth origin, branch base); top | base | origin
        public string axis = "";                   // "" = kind default (chain wind, cloth yaw, branch wind); wind | yaw
        public bool enabled = true;
    }
    [Serializable] public class Cfg
    {
        public string note = "";
        public string mode = "off";                // off | on
        public float rangeM = 40f, selectSeconds = 0.5f, rescanSeconds = 5f, refSpeedMps = 1f, dirDeg = 70f, gustPeriodS = 9f, logSampleS = 10f;
        public int maxMovers = 64, maxCandidates = 1024;
        public bool log = true;
        public Row[] rows = new Row[0];
    }

    sealed class Item
    {
        public Transform t; public Row row; public string kindName;
        public Vector3 pivotLocal, basePos, pivot, lastPos; public Quaternion baseRot, lastRot;
        public float phase, period, maxDeg, windGain, lagS, angle, dist, ampNow;
        public bool yaw, pivotTop, live, hasPivotLocal;
    }

    const string FileName = "propsway.json";
    const float TwoPi = 6.2831853f;
    static Cfg cfg; static long stamp = -1; static float nextScan, nextSelect, nextSample, nextLiveLog;
    static Vector3 fixedDir = new Vector3(0.94f, 0f, 0.342f);
    static int lastLiveCount = -1, nChain, nCloth, nBranch, nStatic, nAnimator, nNoMesh, nRebased, nRestored;
    static readonly List<Item> items = new List<Item>();
    static readonly List<Item> near = new List<Item>();
    static readonly List<Item> live = new List<Item>();
    static readonly HashSet<int> seen = new HashSet<int>();
    static readonly Camera[] camBuf = new Camera[8];
    static readonly Comparison<Item> byDist = (a, b) => a.dist.CompareTo(b.dist);
    public static string LastReport = "";

    static bool On { get { return cfg != null && string.Equals(cfg.mode, "on", StringComparison.OrdinalIgnoreCase) && cfg.rows != null && cfg.rows.Length > 0; } }

    // called from MatAdjustRuntime.Scan on its cadence (scene load fast polling, then every 5 s). Reloads propsway.json by write time and finds matching wrappers. Cheap when off.
    internal static void Tick(bool force)
    {
        if (Application.isEditor) return;
        string p = DataPath.Find(FileName);
        long st = p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0L;
        if (st != stamp)
        {
            stamp = st; cfg = null; Release();
            if (p != null) { try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); } catch (Exception e) { UnityEngine.Debug.LogWarning("[PropSway] bad " + FileName + ": " + e.Message); } }
            if (cfg != null && cfg.rows == null) cfg.rows = new Row[0];
            if (cfg != null) { float a = cfg.dirDeg * Mathf.Deg2Rad; fixedDir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)); }
            nextScan = 0f; nextSelect = 0f;
            if (cfg != null && cfg.log) UnityEngine.Debug.Log("[PropSway] loaded mode=" + cfg.mode + " rows=" + cfg.rows.Length + " range=" + cfg.rangeM + " maxMovers=" + cfg.maxMovers);
        }
        if (!On) return;
        float now = Time.unscaledTime;
        if (!force && now < nextScan) return;
        nextScan = now + Mathf.Max(1f, cfg.rescanSeconds);
        Discover();
    }

    // called every frame from MatAdjustDriver.Update: the single per-frame driver
    internal static void Frame()
    {
        if (!On || Application.isEditor) return;
        float now = Time.unscaledTime;
        if (now >= nextSelect) { nextSelect = now + Mathf.Max(0.1f, cfg.selectSeconds); Select(); }
        if (live.Count == 0) return;
        float dt = Time.deltaTime; if (dt <= 0f) return;            // paused / hit-stop: hold the pose (the Wind gust uses Time.time too)
        dt = Mathf.Min(dt, 0.1f); float time = Time.time;
        for (int i = 0; i < live.Count; i++)
        {
            var it = live[i]; var tr = it.t; if (tr == null) continue;
            if ((tr.position - it.lastPos).sqrMagnitude > 2.5e-5f || tr.rotation != it.lastRot) Rebase(it);   // someone else moved it (PropsOverlay): the new pose is the base
            Vector3 w = Wind.Vec(it.basePos); float speed = w.magnitude; float scale; Vector3 dir;
            if (speed > 0.001f) { scale = Mathf.Clamp(speed / Mathf.Max(0.1f, cfg.refSpeedMps), 0f, 2f); dir = w; dir.y = 0f; }
            else { scale = 0.8f + 0.4f * Mathf.PerlinNoise(time / Mathf.Max(1f, cfg.gustPeriodS), 0.61f); dir = fixedDir; }
            if (dir.sqrMagnitude < 1e-6f) dir = fixedDir; dir.Normalize();
            float g = it.windGain; float amp = it.maxDeg * Mathf.Clamp((1f - g) + g * scale, 0f, 1.6f);
            float osc = 0.72f * Mathf.Sin(TwoPi * time / it.period + it.phase) + 0.28f * Mathf.Sin(TwoPi * time / (it.period * 0.41f) + it.phase * 1.7f);
            float target = amp * (0.2f * Mathf.Min(scale, 1f) + 0.8f * osc);
            it.angle += (target - it.angle) * (1f - Mathf.Exp(-dt / it.lagS)); it.ampNow = amp;
            Quaternion q;
            if (it.yaw) q = Quaternion.AngleAxis(it.angle, Vector3.up);
            else q = Quaternion.AngleAxis(it.pivotTop ? -it.angle : it.angle, Vector3.Cross(Vector3.up, dir));   // hanging from the top: the tip goes downwind; standing on the base: the crown goes downwind
            Vector3 np = it.pivot + q * (it.basePos - it.pivot); Quaternion nr = q * it.baseRot;
            tr.SetPositionAndRotation(np, nr); it.lastPos = np; it.lastRot = nr;
        }
        if (cfg.log && cfg.logSampleS > 0f && now >= nextSample && live.Count > 0)
        {
            nextSample = now + cfg.logSampleS; var s = live[0];
            if (s.t != null) UnityEngine.Debug.Log("[PropSway] tick live=" + live.Count + " windActive=" + Wind.Active + " sample " + s.t.name + " kind=" + s.kindName + " angleDeg=" + s.angle.ToString("F2") + " ampDeg=" + s.ampNow.ToString("F2"));
        }
    }

    static void Discover()
    {
        if (items.Count >= cfg.maxCandidates) return;
        var sw = Stopwatch.StartNew(); int found = 0;
        foreach (var lg in Resources.FindObjectsOfTypeAll<LODGroup>())
        {
            if (lg == null) continue; var tr = lg.transform;
            if (!tr.gameObject.scene.IsValid()) continue;                    // a prefab asset, not a scene object
            if (!seen.Add(lg.GetHashCode())) continue;
            var row = Pick(tr); if (row == null) continue;
            if (items.Count >= cfg.maxCandidates) break;
            if (tr.GetComponentInParent<Animator>(true) != null) { nAnimator++; continue; }
            bool batched = false; foreach (var rd in tr.GetComponentsInChildren<Renderer>(true)) if (rd != null && rd.isPartOfStaticBatch) { batched = true; break; }
            if (batched) { nStatic++; continue; }                            // combined static batch: the mesh cannot follow the transform
            var it = new Item { t = tr, row = row };
            string kind = (row.kind ?? "chain").ToLowerInvariant(); if (kind != "cloth" && kind != "branch") kind = "chain"; it.kindName = kind;
            float dMax, dPer, dGain, dLag; string dPivot, dAxis;
            if (kind == "cloth") { dMax = 4.5f; dPer = 5.5f; dGain = 0.6f; dLag = 0.15f; dPivot = "origin"; dAxis = "yaw"; }
            else if (kind == "branch") { dMax = 0.35f; dPer = 8f; dGain = 0.5f; dLag = 0.6f; dPivot = "base"; dAxis = "wind"; }
            else { dMax = 3f; dPer = 4.2f; dGain = 0.6f; dLag = 0.35f; dPivot = "top"; dAxis = "wind"; }
            it.maxDeg = row.maxDeg >= 0f ? row.maxDeg : dMax; it.windGain = Mathf.Clamp(row.windGain >= 0f ? row.windGain : dGain, 0f, 1.5f); it.lagS = Mathf.Max(0.02f, row.lagS >= 0f ? row.lagS : dLag);
            string pv = string.IsNullOrEmpty(row.pivot) ? dPivot : row.pivot.ToLowerInvariant(); string ax = string.IsNullOrEmpty(row.axis) ? dAxis : row.axis.ToLowerInvariant();
            it.yaw = ax == "yaw"; it.pivotTop = pv == "top";
            uint h = Hash(tr.name); float u = (h & 0xFFFFu) / 65535f, v = ((h >> 16) & 0xFFFFu) / 65535f;
            it.phase = TwoPi * u * Mathf.Clamp01(row.phaseJitter);
            float per = row.periodS > 0f ? row.periodS : dPer; it.period = Mathf.Max(0.5f, per * (1f + row.periodJitter * (2f * v - 1f)));
            if (pv == "origin") { it.pivotLocal = Vector3.zero; it.hasPivotLocal = true; }
            else
            {
                Vector3 mn, mx; if (!WorldBounds(tr, out mn, out mx)) { nNoMesh++; continue; }
                Vector3 wp = new Vector3((mn.x + mx.x) * 0.5f, pv == "base" ? mn.y : mx.y, (mn.z + mx.z) * 0.5f);
                it.pivotLocal = tr.InverseTransformPoint(wp); it.hasPivotLocal = true;
            }
            items.Add(it); found++;
            if (kind == "cloth") nCloth++; else if (kind == "branch") nBranch++; else nChain++;
        }
        LastReport = "candidates=" + items.Count + " chain=" + nChain + " cloth=" + nCloth + " branch=" + nBranch + " staticBatched=" + nStatic + " underAnimator=" + nAnimator + " noMesh=" + nNoMesh + " ms=" + sw.ElapsedMilliseconds;
        if (cfg.log && found > 0) UnityEngine.Debug.Log("[PropSway] scan new=" + found + " " + LastReport);
    }

    static Row Pick(Transform tr)
    {
        string name = tr.name; if (string.IsNullOrEmpty(name)) return null;
        foreach (var r in cfg.rows)
        {
            if (r == null || !r.enabled || string.IsNullOrEmpty(r.match) || name.IndexOf(r.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            bool ex = false; if (r.exclude != null) foreach (var x in r.exclude) if (!string.IsNullOrEmpty(x) && name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) { ex = true; break; }
            if (ex) continue;
            if (!string.IsNullOrEmpty(r.zone) && tr.root.name.IndexOf(r.zone, StringComparison.OrdinalIgnoreCase) < 0) continue;
            float z = tr.position.z; if (z < r.zMin || z >= r.zMax) continue;
            return r;
        }
        return null;
    }

    // world AABB of the LOD0 mesh bounds corners (mesh bounds, not Renderer.bounds: valid under an inactive ring, as PropsOverlay.BottomY does)
    static bool WorldBounds(Transform tr, out Vector3 mn, out Vector3 mx)
    {
        mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        var lg = tr.GetComponent<LODGroup>(); Renderer[] rs = null;
        if (lg != null) { var l = lg.GetLODs(); if (l != null && l.Length > 0) rs = l[0].renderers; }
        if (rs == null || rs.Length == 0) rs = tr.GetComponentsInChildren<Renderer>(true);
        bool any = false;
        foreach (var rd in rs)
        {
            if (rd == null) continue; Mesh m = null;
            var mf = rd.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; else { var sk = rd as SkinnedMeshRenderer; if (sk != null) m = sk.sharedMesh; }
            if (m == null) continue; var b = m.bounds; var M = rd.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                var wp = M.MultiplyPoint3x4(c); mn = Vector3.Min(mn, wp); mx = Vector3.Max(mx, wp); any = true;
            }
        }
        return any;
    }

    static uint Hash(string s) { uint h = 2166136261u; for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; } return h; }

    static bool CamPos(out Vector3 p)
    {
        var c = Camera.main;
        if (c == null) { int n = Camera.GetAllCameras(camBuf); for (int i = 0; i < n; i++) if (camBuf[i] != null && camBuf[i].enabled && camBuf[i].cameraType == CameraType.Game) { c = camBuf[i]; break; } }
        if (c == null) { p = Vector3.zero; return false; }
        p = c.transform.position; return true;
    }

    // every selectSeconds: the nearest items inside rangeM (+6 m hysteresis for those already moving), at most maxMovers, become live; the rest are put back at rest
    static void Select()
    {
        Vector3 cam; if (!CamPos(out cam)) return;
        float r = Mathf.Max(5f, cfg.rangeM), rOut = r + 6f; int cap = Mathf.Clamp(cfg.maxMovers, 1, 256);
        near.Clear();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            if (it.t == null) { items.RemoveAt(i); continue; }
            if (!it.t.gameObject.activeInHierarchy) { if (it.live) Rest(it, true); continue; }
            Vector3 at = it.live ? it.basePos : it.t.position; float d = (at - cam).magnitude;
            if (d > (it.live ? rOut : r)) { if (it.live) Rest(it, true); continue; }
            it.dist = it.live ? d * 0.9f : d; near.Add(it);
        }
        near.Sort(byDist);
        live.Clear();
        for (int k = 0; k < near.Count; k++)
        {
            var it = near[k];
            if (k < cap) { if (!it.live) Wake(it); if (it.live) live.Add(it); }
            else if (it.live) Rest(it, true);
        }
        if (cfg.log && live.Count != lastLiveCount && Time.unscaledTime >= nextLiveLog)
        {
            lastLiveCount = live.Count; nextLiveLog = Time.unscaledTime + 2f;
            UnityEngine.Debug.Log("[PropSway] live=" + live.Count + " of " + items.Count + " cam=(" + cam.x.ToString("F0") + "," + cam.z.ToString("F0") + ") windActive=" + Wind.Active);
        }
    }

    static void Wake(Item it)
    {
        var tr = it.t; it.basePos = tr.position; it.baseRot = tr.rotation; it.lastPos = it.basePos; it.lastRot = it.baseRot;
        it.pivot = tr.TransformPoint(it.pivotLocal); it.angle = 0f; it.live = true;
    }

    static void Rebase(Item it)
    {
        var tr = it.t; it.basePos = tr.position; it.baseRot = tr.rotation; it.lastPos = it.basePos; it.lastRot = it.baseRot;
        it.pivot = tr.TransformPoint(it.pivotLocal); it.angle = 0f; nRebased++;
        if (cfg != null && cfg.log && nRebased <= 8) UnityEngine.Debug.Log("[PropSway] rebased " + tr.name + " (moved by another script)");
    }

    // put one item back exactly (unless someone else moved it meanwhile: then it keeps their pose)
    static void Rest(Item it, bool restore)
    {
        if (it.live && restore && it.t != null && (it.t.position - it.lastPos).sqrMagnitude <= 2.5e-5f && it.t.rotation == it.lastRot) { it.t.SetPositionAndRotation(it.basePos, it.baseRot); nRestored++; }
        it.live = false; it.angle = 0f;
    }

    // mode off / file gone / json changed: put every moving prop back exactly as it was, forget everything
    static void Release()
    {
        int n = 0; foreach (var it in items) if (it.live) { Rest(it, true); n++; }
        items.Clear(); near.Clear(); live.Clear(); seen.Clear(); lastLiveCount = -1; nChain = nCloth = nBranch = nStatic = nAnimator = nNoMesh = 0;
        if (n > 0) UnityEngine.Debug.Log("[PropSway] restored " + n + " props to rest");
    }
}
