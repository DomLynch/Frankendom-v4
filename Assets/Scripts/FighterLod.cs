using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// FIGHTERLOD v1 (laneFIGHTERLOD): mid-distance MESH LOD for the skinned fighters (Joan hair 318k tris, Pitborn body 250k tris, ...), switched in DATA, OFF by default.
//   * data: fighter_lod.json (StreamingAssets in the player, Assets/Data in the editor). enabled=false ships. Command line: -fighterlod:on | off | json (default) for A/B runs on one exe.
//   * a row = one skinned renderer of one roster character: { character (roster id), renderer (object name, "" = roster headRenderer), file (fighterlod\*.mid.json, made offline by make_fighterlod.py) }.
//     Adding a character or replacing Joan's head = one new row + one script run, no code.
//   * the mid mesh has the SAME skeleton, bindposes, skin (top-4 weights on the same bones), UVs and material slot as the source, with the normals transferred from the source.
//     It is swapped into the SAME SkinnedMeshRenderer (sharedMesh): no extra renderer, so every other system that finds the fighter's renderers (cuts, decals, bounds, ragdoll) sees one renderer per part as before.
//   * switch = screen-space error: the file carries errModelM (95th percentile distance mid -> source surface incl. a posed-skin test, metres at model scale). Projected error in pixels =
//     errModelM * lossyScale * Screen.height / (2 * depth * tan(fov/2)); mid when it is below enterPx, back to the full mesh above exitPx (hysteresis) and not before minHoldS has passed.
//     It follows the camera's real fov and the real screen height, so close-ups, wide lens and 1080p/1440p/2560x1600 all fall out of one number. A row can instead use fixed distances (enterM/exitM > 0).
//   * ALWAYS full mesh while a finisher runs (RTFinisher.Busy), while the finisher camera is active, for holdAfterFinisherS afterwards, and for afterCutHoldS after any cut asked for the full mesh (MeshCut.LodPick -> ForceHigh).
//   * never touches `enabled`: a renderer a cut disabled stays as it is. If another system owns sharedMesh (GearMask clone, anything foreign) the row yields for that renderer and logs once.
public static class FighterLod
{
    [Serializable] public class Row
    {
        public string character = ""; public string renderer = ""; public string file = ""; public bool enabled = true;
        public float enterPx = 0f, exitPx = 0f;     // 0 = the global value
        public float enterM = 0f, exitM = 0f;       // > 0: switch on camera depth in metres instead of projected error (enterM = go mid beyond this depth, exitM = back to full inside this depth, exitM < enterM)
        public string note = "";
    }
    [Serializable] public class Cfg
    {
        public string note = ""; public bool enabled = false;
        public float tickS = 0.1f;                  // evaluation period (real seconds)
        public float enterPx = 0.7f;                // mid when the projected mid-vs-source error is below this many pixels
        public float exitPx = 0.9f;                 // back to the full mesh above this (> enterPx = hysteresis)
        public float minHoldS = 1.0f;               // no second swap on one renderer inside this time
        public bool offDuringFinisher = true;       // RTFinisher.Busy
        public bool offWithFinisherCamera = true;   // FinisherCamera active
        public float holdAfterFinisherS = 5f;       // stay on the full mesh this long after a finisher / finisher camera ended
        public float afterCutHoldS = 30f;           // stay on the full mesh this long after MeshCut asked for it
        public float rescanS = 1f;                  // look for new fighters / renderers this often while a row is unbound
        public int buildPerTick = 1;                // mid meshes built per tick (parse + tangents are a few ms each, spread over the intro)
        public bool recalcTangents = true;          // RecalculateTangents on the built mesh when the source mesh has tangents
        public bool log = true;
        public Row[] rows = new Row[0];
    }
    [Serializable] public class MidFile : CutLod.ProxyFile { public float errModelM, errMaxModelM, normalErrP95Deg; public string variant = ""; }

    sealed class Bind
    {
        public SkinnedMeshRenderer smr; public Row row; public Mesh high, mid; public bool isMid, failed, foreign, warned; public float lastSwitch = -999f; public float errWorld; public int highTris, midTris;
    }

    static Cfg cfg; static int cliMode = -1;      // -1 unread, 0 json, 1 on, 2 off
    static readonly Dictionary<string, MidFile> files = new Dictionary<string, MidFile>(StringComparer.OrdinalIgnoreCase);
    static readonly List<Bind> binds = new List<Bind>();
    static float holdUntil;                        // unscaled time before which everything stays on the full mesh
    public static int Swaps, ForcedHigh; public static float LastBuildMs;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; cliMode = -1; files.Clear(); binds.Clear(); holdUntil = 0f; Swaps = ForcedHigh = 0; LastBuildMs = 0f; }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new Cfg();
            try { var txt = HudCfg.ReadData("fighter_lod.json"); if (txt != null) { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) { cfg = c; if (cfg.rows == null) cfg.rows = new Row[0]; } } }
            catch (Exception ex) { Debug.LogWarning("[fighterlod] fighter_lod.json: " + ex.Message); }
            return cfg;
        }
    }
    public static bool On
    {
        get
        {
            if (cliMode < 0) { cliMode = 0; foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-fighterlod:on") cliMode = 1; else if (a == "-fighterlod:off") cliMode = 2; else if (a == "-fighterlod:json") cliMode = 0; } }
            return cliMode == 1 || (cliMode == 0 && C.enabled);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!On || C.rows.Length == 0) return;
        if (UnityEngine.Object.FindFirstObjectByType<FighterLodTicker>() != null) return;
        var go = new GameObject("FighterLod"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<FighterLodTicker>();
        if (C.log) Debug.Log("[fighterlod] on: " + C.rows.Length + " rows, enterPx " + C.enterPx + " exitPx " + C.exitPx + " (cli " + (cliMode == 1 ? "on" : "json") + ")");
    }

    /// <summary>Anything that is about to read or cut the full render mesh of this renderer calls this first (MeshCut.LodPick). Puts the full mesh back at once and holds it for afterCutHoldS.</summary>
    public static void ForceHigh(SkinnedMeshRenderer smr)
    {
        if (cfg == null || binds.Count == 0) return;
        holdUntil = Mathf.Max(holdUntil, Time.unscaledTime + C.afterCutHoldS);
        for (int i = 0; i < binds.Count; i++) { var b = binds[i]; if (b.smr == smr && b.isMid) { Apply(b, false, "forced"); ForcedHigh++; } }
    }
    /// <summary>The finisher scene starts (RTFinisher.OnStart): every renderer back to the full mesh before the first cut.</summary>
    public static void ForceAllHigh(string why)
    {
        holdUntil = Mathf.Max(holdUntil, Time.unscaledTime + C.holdAfterFinisherS);
        for (int i = 0; i < binds.Count; i++) { var b = binds[i]; if (b.smr != null && b.isMid) { Apply(b, false, why); ForcedHigh++; } }
    }

    static void Apply(Bind b, bool mid, string why)
    {
        if (b.smr == null) return;
        var lb = b.smr.localBounds;
        b.smr.sharedMesh = mid ? b.mid : b.high; b.smr.localBounds = lb;
        b.isMid = mid; b.lastSwitch = Time.unscaledTime; Swaps++;
        if (C.log) Debug.Log("[fighterlod] " + b.row.character + "/" + b.smr.name + " -> " + (mid ? "MID " + b.midTris : "FULL " + b.highTris) + " tris (" + why + ")");
    }

    static MidFile Load(Row r)
    {
        MidFile m; if (files.TryGetValue(r.file, out m)) return m;
        m = null;
        try
        {
            string txt = null; foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, r.file), Path.Combine(Application.dataPath, "Data/" + r.file) }) { if (File.Exists(p)) { txt = DataPath.ReadFile(p).TrimStart((char)0xFEFF); break; } }
            if (txt == null) Debug.LogWarning("[fighterlod] mesh file not found: " + r.file); else m = JsonUtility.FromJson<MidFile>(txt);
            if (m != null && (m.pos == null || m.pos.Length < 9 || m.pos.Length % 3 != 0 || m.uv.Length != m.pos.Length / 3 * 2 || m.nrm.Length != m.pos.Length || m.boneIdx.Length != m.pos.Length / 3 * 4 || m.boneW.Length != m.boneIdx.Length || m.subs == null || m.subs.Length == 0)) { Debug.LogWarning("[fighterlod] mesh file malformed: " + r.file); m = null; }
        }
        catch (Exception ex) { Debug.LogWarning("[fighterlod] " + r.file + ": " + ex.Message); m = null; }
        files[r.file] = m; return m;
    }

    internal static void Tick(FighterLodTicker t)
    {
        var c = C; var cam = Camera.main; float now = Time.unscaledTime;
        // 1. bind new renderers
        if (now >= t.nextScan) { t.nextScan = now + Mathf.Max(0.2f, c.rescanS); Scan(c); }
        for (int i = binds.Count - 1; i >= 0; i--) if (binds[i].smr == null) { if (binds[i].mid != null) UnityEngine.Object.Destroy(binds[i].mid); binds.RemoveAt(i); }
        // 2. build a few mid meshes per tick
        int built = 0;
        for (int i = 0; i < binds.Count && built < Mathf.Max(1, c.buildPerTick); i++)
        {
            var b = binds[i]; if (b.mid != null || b.failed || b.smr == null) continue;
            built++; Build(b, c);
        }
        if (cam == null) return;
        // 3. finisher / camera gate: full mesh
        bool fin = (c.offDuringFinisher && RTFinisher.Busy) || (c.offWithFinisherCamera && FinisherCamera.Instance != null && FinisherCamera.Instance.Active);
        if (fin) holdUntil = Mathf.Max(holdUntil, now + c.holdAfterFinisherS);
        bool force = fin || now < holdUntil;
        float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad); if (tanHalf < 1e-4f) return;
        float pxPerM1 = Screen.height / (2f * tanHalf);
        Vector3 cp = cam.transform.position, cf = cam.transform.forward;
        for (int i = 0; i < binds.Count; i++)
        {
            var b = binds[i]; if (b.mid == null || b.failed || b.smr == null) continue;
            if (!b.smr.enabled || !b.smr.gameObject.activeInHierarchy) continue;                  // a cut disabled it, or the fighter is hidden: leave it
            var cur = b.smr.sharedMesh;
            if (cur != b.high && cur != b.mid) { if (!b.warned && c.log) { b.warned = true; Debug.Log("[fighterlod] " + b.row.character + "/" + b.smr.name + " mesh is owned by another system (mask / cut): LOD yields"); } b.foreign = true; continue; }
            b.foreign = false; b.isMid = cur == b.mid;
            if (force) { if (b.isMid) { Apply(b, false, fin ? "finisher" : "hold"); } continue; }
            if (now - b.lastSwitch < c.minHoldS) continue;
            float depth = Vector3.Dot(cf, b.smr.bounds.ClosestPoint(cp) - cp); if (depth < 0.05f) depth = 0.05f;
            bool wantMid;
            if (b.row.enterM > 0f) wantMid = b.isMid ? depth > b.row.exitM : depth > b.row.enterM;
            else
            {
                float err = b.errWorld * pxPerM1 / depth;
                float enter = b.row.enterPx > 0f ? b.row.enterPx : c.enterPx, exit = b.row.exitPx > 0f ? b.row.exitPx : c.exitPx;
                wantMid = b.isMid ? err < exit : err < enter;
            }
            if (wantMid != b.isMid) Apply(b, wantMid, "depth " + depth.ToString("0.0") + " m");
        }
    }

    static void Scan(Cfg c)
    {
        foreach (var df in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            if (df == null) continue;
            foreach (var smr in df.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                bool known = false; for (int i = 0; i < binds.Count; i++) if (binds[i].smr == smr) { known = true; break; }
                if (known) continue;
                string id = Roster.IdOf(smr.transform); if (string.IsNullOrEmpty(id)) continue;
                for (int r = 0; r < c.rows.Length; r++)
                {
                    var row = c.rows[r]; if (row == null || !row.enabled || row.character != id || string.IsNullOrEmpty(row.file)) continue;
                    string want = string.IsNullOrEmpty(row.renderer) ? Roster.Get(id).headRenderer : row.renderer;
                    if (!string.Equals(smr.name, want, StringComparison.OrdinalIgnoreCase)) continue;
                    binds.Add(new Bind { smr = smr, row = row, high = smr.sharedMesh, highTris = CutLod.Tris(smr.sharedMesh) }); break;
                }
            }
        }
    }

    static void Build(Bind b, Cfg c)
    {
        float t0 = Time.realtimeSinceStartup;
        if (b.smr.sharedMesh != b.high) { b.failed = true; return; }              // something swapped it already: never build on a foreign mesh
        var pf = Load(b.row) as MidFile; Mesh m = pf != null ? CutLod.Build(pf, b.smr) : null;
        if (m == null) { b.failed = true; if (c.log) Debug.LogWarning("[fighterlod] " + b.row.character + "/" + b.smr.name + ": mid mesh not built, row skipped"); return; }
        m.name = "FighterLod_" + pf.id + "_" + pf.renderer;
        bool tang = true; try { tang = !b.high.isReadable || b.high.tangents.Length > 0; } catch { }
        if (c.recalcTangents && tang) m.RecalculateTangents();
        b.mid = m; b.midTris = CutLod.Tris(m);
        b.errWorld = pf.errModelM * Mathf.Abs(b.smr.transform.lossyScale.y);
        LastBuildMs = (Time.realtimeSinceStartup - t0) * 1000f;
        if (c.log) Debug.Log("[fighterlod] built " + b.row.character + "/" + b.smr.name + " mid " + b.midTris + " tris / " + m.vertexCount + " verts (full " + b.highTris + " / " + b.high.vertexCount + ") err " + b.errWorld.ToString("0.0000") + " m world, " + LastBuildMs.ToString("0.0") + " ms");
    }

    /// <summary>One line for logs / the profile recorder: renderers on the mid mesh and the skinned triangles / vertices they save right now.</summary>
    public static string StatsLine()
    {
        int mid = 0, n = 0, savedT = 0, savedV = 0;
        for (int i = 0; i < binds.Count; i++) { var b = binds[i]; if (b.mid == null || b.smr == null) continue; n++; if (b.isMid) { mid++; savedT += b.highTris - b.midTris; savedV += b.high.vertexCount - b.mid.vertexCount; } }
        return "[fighterlod] " + mid + "/" + n + " renderers on mid, saved " + savedT + " tris " + savedV + " skinned verts, swaps " + Swaps + ", forced " + ForcedHigh;
    }
}

public class FighterLodTicker : MonoBehaviour
{
    internal float nextScan, nextTick;
    void OnEnable() { RTFinisher.OnStart += OnFinisher; }
    void OnDisable() { RTFinisher.OnStart -= OnFinisher; }
    void OnFinisher(FinisherEntry e, DuelFighter k, DuelFighter v) { FighterLod.ForceAllHigh("finisher start"); }
    void LateUpdate()
    {
        float now = Time.unscaledTime; if (now < nextTick) return;
        nextTick = now + Mathf.Max(0.02f, FighterLod.C.tickS);
        FighterLod.Tick(this);
    }
}
