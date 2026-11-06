using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// CUTLOD v96: per-character "cut LOD". Every runtime cut (MeshCut, MeshCutCrown, the severed-head bake) must work on a cheap proxy, never on Joan's 400k-triangle Tripo head.
//   * data: cut_lod.json rows { character (roster id), renderer (the heavy SkinnedMeshRenderer's object name; "" = roster headRenderer), anchorBone, file (StreamingAssets\cutlod\*.cutlod.json) }.
//     The file is the offline-decimated mesh (Blender, ~10k triangles, same UVs, skin weights to the same Head / Neck bones, Unity space). Material slots are the heavy renderer's own materials.
//   * MeshCut.LodPick swaps the heavy renderer for a hidden proxy SkinnedMeshRenderer (same bones / bindposes / materials) for the duration of the cut; after a committed cut the proxy stays as the visible rest of the mesh
//     and the heavy renderer is disabled (crown: the Head bone is scaled away as before, the proxy is destroyed after the bake).
//   * a heavy renderer WITHOUT a row that has more than maxTrisNoProxy triangles is not cut at all (logged), so a new heavy model can never freeze the game again.
//   * budgets: perFrameMaxMs caps one Step slice, totalBudgetMs aborts the whole job (the finisher player then runs its gore-only + plain death fallback), syncSliceMs makes the synchronous Cut()/Commit() path interruptible.
//   * one log line per cut: [cutlod] ... tris ... ms.  watchdogS: FinisherPlayer falls back to a plain death when no gore key has committed after that many real seconds.
public static class CutLod
{
    [Serializable] public class Row { public string character = ""; public string renderer = ""; public string anchorBone = "mixamorig:Head"; public string file = ""; }
    [Serializable] public class Cfg
    {
        public string note = ""; public bool enabled = true;
        public float perFrameMaxMs = 4f;        // one Step() slice never asks for more than this (the finisher's precut budget is min()ed with it)
        public float totalBudgetMs = 600f;      // all slices of one job; exceeded -> job.failed ("cut budget exceeded") -> gore-only + plain death
        public float syncSliceMs = 20f;         // slice length of the synchronous path (Cut() / Commit() of an unfinished job), the total budget is checked between slices
        public int maxTrisNoProxy = 300000;     // a renderer above this with no row is skipped by the cut (Pitborn body 249,999 tris is the biggest that must still be cut: its v94 cuts took 160-250 ms cpu)
        public float watchdogS = 8f;            // finisher scene: real seconds without a committed gore key -> plain death (0 = off)
        public bool log = true;
        public Row[] rows = new Row[0];
    }
    [Serializable] public class Sub { public int[] t = new int[0]; }
    [Serializable] public class ProxyFile
    {
        public int version; public string id = ""; public string renderer = ""; public int srcTris, tris, verts;
        public string[] bones = new string[0]; public float[] pos = new float[0], nrm = new float[0], uv = new float[0]; public int[] boneIdx = new int[0]; public float[] boneW = new float[0]; public Sub[] subs = new Sub[0];
    }

    static Cfg cfg; static readonly Dictionary<string, ProxyFile> files = new Dictionary<string, ProxyFile>(StringComparer.OrdinalIgnoreCase);
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; files.Clear(); }

    public static Cfg C
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new Cfg();
            try { var txt = HudCfg.ReadData("cut_lod.json"); if (txt != null) { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) { cfg = c; if (cfg.rows == null) cfg.rows = new Row[0]; } } else Debug.LogWarning("[cutlod] cut_lod.json not found: defaults (enabled, no rows)"); }
            catch (Exception ex) { Debug.LogWarning("[cutlod] cut_lod.json: " + ex.Message); }
            return cfg;
        }
    }
    public static bool On { get { return C.enabled; } }

    /// <summary>Row for this victim's renderer (roster id of the victim, renderer object name; an empty row.renderer means the roster headRenderer), or null.</summary>
    public static Row RowFor(Transform victim, SkinnedMeshRenderer smr)
    {
        var c = C; if (!c.enabled || c.rows == null || victim == null || smr == null) return null;
        string id = Roster.IdOf(victim); if (string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < c.rows.Length; i++)
        {
            var r = c.rows[i]; if (r == null || r.character != id || string.IsNullOrEmpty(r.file)) continue;
            string want = string.IsNullOrEmpty(r.renderer) ? Roster.Get(id).headRenderer : r.renderer;
            if (!string.IsNullOrEmpty(want) && string.Equals(smr.name, want, StringComparison.OrdinalIgnoreCase)) return r;
        }
        return null;
    }

    public static ProxyFile Load(Row r)
    {
        ProxyFile m; if (files.TryGetValue(r.file, out m)) return m;
        m = null;
        try
        {
            string txt = null; foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, r.file), Path.Combine(Application.dataPath, "Data/" + r.file) }) { if (File.Exists(p)) { txt = File.ReadAllText(p).TrimStart((char)0xFEFF); break; } }
            if (txt == null) Debug.LogWarning("[cutlod] mesh file not found: " + r.file); else m = JsonUtility.FromJson<ProxyFile>(txt);
            if (m != null && (m.pos == null || m.pos.Length < 9 || m.pos.Length % 3 != 0 || m.uv.Length != m.pos.Length / 3 * 2 || m.nrm.Length != m.pos.Length || m.boneIdx.Length != m.pos.Length / 3 * 4 || m.boneW.Length != m.boneIdx.Length || m.subs == null || m.subs.Length == 0)) { Debug.LogWarning("[cutlod] mesh file malformed: " + r.file); m = null; }
        }
        catch (Exception ex) { Debug.LogWarning("[cutlod] " + r.file + ": " + ex.Message); m = null; }
        files[r.file] = m; return m;
    }

    /// <summary>Builds the runtime Mesh for the heavy renderer's skeleton: bone indices are remapped BY NAME into the heavy renderer's own bones array and bindposes are the heavy mesh's, so the proxy skins exactly like the original. Null when a bone is missing.</summary>
    public static Mesh Build(ProxyFile f, SkinnedMeshRenderer heavy)
    {
        var bones = heavy.bones; var map = new int[f.bones.Length];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = -1; string k = GoreFx.Key(f.bones[i]);
            for (int b = 0; b < bones.Length; b++) if (bones[b] != null && (bones[b].name == f.bones[i] || GoreFx.Key(bones[b].name) == k)) { map[i] = b; break; }
            if (map[i] < 0) { Debug.LogWarning("[cutlod] bone '" + f.bones[i] + "' not on renderer '" + heavy.name + "': proxy not built"); return null; }
        }
        var heavyBind = heavy.sharedMesh != null ? heavy.sharedMesh.bindposes : null;
        if (heavyBind == null || heavyBind.Length != bones.Length) { Debug.LogWarning("[cutlod] bindposes of '" + heavy.name + "' do not match its bones"); return null; }
        int vc = f.pos.Length / 3; var P = new Vector3[vc]; var N = new Vector3[vc]; var U = new Vector2[vc]; var BW = new BoneWeight[vc];
        for (int v = 0; v < vc; v++)
        {
            P[v] = new Vector3(f.pos[v * 3], f.pos[v * 3 + 1], f.pos[v * 3 + 2]); N[v] = new Vector3(f.nrm[v * 3], f.nrm[v * 3 + 1], f.nrm[v * 3 + 2]); U[v] = new Vector2(f.uv[v * 2], f.uv[v * 2 + 1]);
            var w = new BoneWeight(); int o = v * 4;
            w.boneIndex0 = Mathf.Max(0, map[Mathf.Clamp(f.boneIdx[o], 0, map.Length - 1)]); w.weight0 = f.boneW[o];
            w.boneIndex1 = Mathf.Max(0, map[Mathf.Clamp(f.boneIdx[o + 1], 0, map.Length - 1)]); w.weight1 = f.boneW[o + 1];
            w.boneIndex2 = Mathf.Max(0, map[Mathf.Clamp(f.boneIdx[o + 2], 0, map.Length - 1)]); w.weight2 = f.boneW[o + 2];
            w.boneIndex3 = Mathf.Max(0, map[Mathf.Clamp(f.boneIdx[o + 3], 0, map.Length - 1)]); w.weight3 = f.boneW[o + 3];
            BW[v] = w;
        }
        var m = new Mesh { name = "CutLod_" + f.id + "_" + f.renderer };
        if (vc > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.SetVertices(P); m.SetNormals(N); m.SetUVs(0, U); m.boneWeights = BW;
        m.bindposes = heavyBind;
        int slots = Mathf.Max(1, heavy.sharedMesh.subMeshCount); m.subMeshCount = slots;
        for (int s = 0; s < slots; s++) m.SetTriangles(s < f.subs.Length && f.subs[s] != null ? f.subs[s].t : new int[0], s, false);
        m.RecalculateBounds(); return m;
    }

    public static int Tris(Mesh m) { if (m == null) return 0; int n = 0; for (int s = 0; s < m.subMeshCount; s++) n += (int)(m.GetIndexCount(s) / 3); return n; }
}

public static partial class MeshCut
{
    internal sealed class LodPair { public SkinnedMeshRenderer heavy, proxy; }

    /// <summary>CUTLOD v96: the renderer the cut must use instead of `smr`: the proxy (hidden, same skeleton and materials), `smr` itself (small enough), or null (not part of this unit, or too heavy with no proxy: skipped).
    /// keep = the proxy stays as the visible rest of the mesh after the commit (Run); false = transient bake source (CrownSteps destroys it at the end).</summary>
    static SkinnedMeshRenderer LodPick(CutJob j, SkinnedMeshRenderer smr, HashSet<Transform> set, bool keep)
    {
        FighterLod.ForceHigh(smr);   // FIGHTERLOD v1: a mid-distance mesh must never reach a cut: the full render mesh back first (holds afterCutHoldS)
        var c = CutLod.C; if (!c.enabled || smr == null || smr.sharedMesh == null) return smr;
        int tris = CutLod.Tris(smr.sharedMesh);
        var row = CutLod.RowFor(j.victimRoot, smr);
        if (row == null)
        {
            if (c.maxTrisNoProxy > 0 && tris > c.maxTrisNoProxy)
            {
                j.lodSkipped++; j.lodNote += (j.lodNote.Length > 0 ? "; " : "") + "'" + smr.name + "' " + tris + " tris skipped (no cut-LOD row, limit " + c.maxTrisNoProxy + ")";
                WarnOnce("lod-heavy:" + smr.name, "renderer '" + smr.name + "' has " + tris + " triangles and no cut_lod.json row: it is not cut (add a row + a decimated mesh)"); return null;
            }
            j.lodTris += tris; return smr;
        }
        var anchor = FindBone(j.victimRoot, new[] { row.anchorBone });
        if (anchor != null && !set.Contains(anchor)) return null;            // this unit does not contain the mesh's bones: nothing to cut, and no 400k pass
        var pf = CutLod.Load(row); Mesh pm = pf != null ? CutLod.Build(pf, smr) : null;
        if (pm == null) { j.lodSkipped++; j.lodNote += (j.lodNote.Length > 0 ? "; " : "") + "'" + smr.name + "' proxy unavailable, skipped"; return null; }
        var go = new GameObject("CutLodProxy_" + smr.name); go.transform.SetParent(smr.transform.parent, false);
        go.transform.localPosition = smr.transform.localPosition; go.transform.localRotation = smr.transform.localRotation; go.transform.localScale = smr.transform.localScale;
        var p = go.AddComponent<SkinnedMeshRenderer>(); p.sharedMesh = pm; p.bones = smr.bones; p.rootBone = smr.rootBone; p.sharedMaterials = smr.sharedMaterials;
        p.updateWhenOffscreen = true; p.localBounds = smr.localBounds; p.shadowCastingMode = smr.shadowCastingMode; p.forceRenderingOff = true;   // skins (BakeMesh) but draws nothing until the commit
        j.owned.Add(pm); j.lodGo.Add(go); j.lodTris += CutLod.Tris(pm); j.lodSubst++;
        if (keep) j.lodPairs.Add(new LodPair { heavy = smr, proxy = p });
        return p;
    }

    static void LodLog(CutJob j, CutResult res, string how)
    {
        var c = CutLod.C; if (!c.enabled || !c.log || j == null) return;
        Debug.Log("[cutlod] " + how + " unit '" + j.unitId + "' ok=" + (res != null && res.ok) + " tris_in=" + j.lodTris + " proxies=" + j.lodSubst + " skipped=" + j.lodSkipped + " cpu=" + j.cpuMs.ToString("0.0") + " ms slices=" + j.slices + " maxSlice=" + j.maxSliceMs.ToString("0.0") + " ms"
            + (j.budgetHit ? " BUDGET EXCEEDED (limit " + c.totalBudgetMs.ToString("0") + " ms)" : "") + (j.lodNote.Length > 0 ? " | " + j.lodNote : "") + (res != null && !res.ok && res.note.Length > 0 ? " | " + res.note : ""));
    }
}
