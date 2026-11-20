using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// DETAILPACK v1 (fk_windcode, 20 Nov 2026; adapted from fk_fullbake kit). NOT APPLIED. UNPROVEN until compiled by the Unity dev. PRESENTATION ONLY. COMPILE-ONLY: a new static class + one call from MatAdjustRuntime.Scan.
// No MonoBehaviour, no scene object, no [SerializeField], no new boot hook: Scan is already driven by the MatAdjustDriver that MatAdjustRuntime.Boot registered in the live build.
// What: gives the shared packed detail map (R grey albedo, G normal Y, B smoothness, A normal X, all linear; neutral = 128) to HDRP/Lit materials picked by NAME, at runtime:
//   SetTexture("_DetailMap"), SetTextureScale, _DetailNormalScale / _DetailSmoothnessScale / _DetailAlbedoScale, _LinkDetailsWithBase, EnableKeyword("_DETAIL_MAP")
//   (HDRP Lit.shader lines 50-53, 188; keyword shader_feature_local_fragment _DETAIL_MAP).
// Scope of this patch: the shipped detail_pack.json carries ONE row, the ground clones "M_WorldTerrain (zgm)" made by ZoneGroundMat (the exact name excludes the shared original, so a clone made later cannot inherit
//   the pack). M_WorldTerrain already has the keyword, so the variant exists in the player. Props are NOT in scope (their keyword set has no variant until a FULL carries Resources/DetailKeep).
// Switch: detail_pack.json mode "off" (shipped) or a missing file = nothing runs and everything it applied is put back. "canary" = only names containing canaryMatch. "on" = every row.
// Look warning: swapping the _DetailMap replaces ground_macro_detail.png (the macro albedo blotches that zone_ground_mat.json detailAlbedo 1.2-2.0 drives on z3/z5/z6/z7). The pack's R is neutral (128), so those blotches
//   vanish on any clone that gets it. Row field maxExistingAlbedoScale (>= 0) skips clones whose _DetailAlbedoScale is above it (0.01 = only pit_yard and z4, which have detailAlbedo 0).
// Editor: does nothing. Writes only Material properties named above; never a MaterialPropertyBlock (SRP batcher stays intact), never a transform or scene object.
public static class DetailPackRuntime
{
    [Serializable] public class Row
    {
        public string match = "";                // part of the material name (ordinal, ignore case)
        public string[] exclude = new string[0];  // any of these in the name = skip
        public string pack = "";                  // file in texDir; "" = cfg.defaultPack
        public float tiling = 1f;                 // _DetailMap scale; relativeTiling = multiply the material's own current scale instead of replacing it
        public bool relativeTiling;
        public float normalScale = 0.5f, smoothScale = 0.3f, albedoScale = 0f;   // albedo 0 = no colour change (LOOK OK rule); HDRP Lit range 0..2
        public bool linkWithBase = true;          // _LinkDetailsWithBase
        public bool overrideExisting = true;      // replace an existing _DetailMap (the terrain has the macro blotch)
        public float maxExistingAlbedoScale = -1f; // >= 0: skip materials whose current _DetailAlbedoScale is above this (they carry a macro blotch)
    }
    [Serializable] public class Cfg
    {
        public string note = "";
        public string mode = "off";               // off | canary | on
        public string canaryMatch = "";
        public string texDir = "groundmat";       // under StreamingAssets
        public string defaultPack = "detail_stone_bark.png";
        public float rescanSeconds = 5f;
        public bool log = true, compress = false;
        public Row[] rows = new Row[0];
    }

    sealed class Saved { public Texture tex; public Vector2 scale, offset; public bool kw; public float n, s, a, link; }

    const string FileName = "detail_pack.json", Lit = "HDRP/Lit";
    static Cfg cfg; static long stamp = -1; static float nextScan;
    static readonly Dictionary<string, Texture2D> packs = new Dictionary<string, Texture2D>();
    static readonly Dictionary<Material, Saved> applied = new Dictionary<Material, Saved>();
    static readonly HashSet<int> seen = new HashSet<int>();     // instance ids already looked at (matched or not) for the current cfg
    static int nApplied, nSkipNoProp, nSkipAlbedo;
    public static string LastReport = "";

    static bool Mode(string m) { return cfg != null && string.Equals(cfg.mode, m, StringComparison.OrdinalIgnoreCase); }

    // called from MatAdjustRuntime.Scan on its cadence (scene load fast polling, then every rescanSeconds). Cheap when off.
    internal static void Tick(bool force)
    {
        if (Application.isEditor) return;
        string p = DataPath.Find(FileName);
        long st = p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0L;
        if (st != stamp)
        {
            stamp = st; cfg = null;
            if (p != null) { try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); } catch (Exception e) { Debug.LogWarning("[DetailPack] bad " + FileName + ": " + e.Message); } }
            seen.Clear(); nextScan = 0f; Restore();
            if (cfg != null && cfg.log) Debug.Log("[DetailPack] loaded mode=" + cfg.mode + " rows=" + (cfg.rows == null ? 0 : cfg.rows.Length));
        }
        if (cfg == null || Mode("off") || cfg.rows == null || cfg.rows.Length == 0) return;
        float now = Time.unscaledTime;
        if (!force && now < nextScan) return;
        nextScan = now + Mathf.Max(1f, cfg.rescanSeconds);
        bool canary = Mode("canary"); int n0 = nApplied;
        foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (m == null || !seen.Add(m.GetHashCode())) continue;
            var row = Pick(m.name, canary); if (row == null) continue;
            if (m.shader == null || m.shader.name != Lit) continue;
            if (!m.HasProperty("_DetailMap")) { nSkipNoProp++; continue; }
            if (row.maxExistingAlbedoScale >= 0f && m.HasProperty("_DetailAlbedoScale") && m.GetFloat("_DetailAlbedoScale") > row.maxExistingAlbedoScale) { nSkipAlbedo++; continue; }
            if (m.GetTexture("_DetailMap") != null && !row.overrideExisting) continue;
            var t = Pack(string.IsNullOrEmpty(row.pack) ? cfg.defaultPack : row.pack); if (t == null) continue;
            Apply(m, row, t);
        }
        LastReport = "mode=" + cfg.mode + " applied=" + nApplied + " noProp=" + nSkipNoProp + " skipAlbedo=" + nSkipAlbedo;
        if (cfg.log && nApplied != n0) Debug.Log("[DetailPack] " + LastReport);
    }

    static Row Pick(string name, bool canary)
    {
        if (canary && (string.IsNullOrEmpty(cfg.canaryMatch) || name.IndexOf(cfg.canaryMatch, StringComparison.OrdinalIgnoreCase) < 0)) return null;
        foreach (var r in cfg.rows)
        {
            if (r == null || string.IsNullOrEmpty(r.match) || name.IndexOf(r.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
            bool ex = false; if (r.exclude != null) foreach (var x in r.exclude) if (!string.IsNullOrEmpty(x) && name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) { ex = true; break; }
            if (!ex) return r;
        }
        return null;
    }

    static Texture2D Pack(string file)
    {
        Texture2D t; if (packs.TryGetValue(file, out t) && t != null) return t;
        string path = Path.Combine(Path.Combine(Application.streamingAssetsPath, cfg.texDir), file);
        if (!File.Exists(path)) { if (!packs.ContainsKey(file)) Debug.LogWarning("[DetailPack] missing " + path); packs[file] = null; return null; }
        try
        {
            t = new Texture2D(4, 4, TextureFormat.RGBA32, true, true) { name = "detailpack_" + file, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };   // linear = true: this is data, not colour
            if (!t.LoadImage(File.ReadAllBytes(path), false)) { UnityEngine.Object.Destroy(t); packs[file] = null; return null; }
            if (cfg.compress) t.Compress(true);
            packs[file] = t; return t;
        }
        catch (Exception e) { Debug.LogWarning("[DetailPack] " + path + ": " + e.Message); return null; }
    }

    static void Apply(Material m, Row r, Texture2D t)
    {
        if (!applied.ContainsKey(m))
            applied[m] = new Saved { tex = m.GetTexture("_DetailMap"), scale = m.GetTextureScale("_DetailMap"), offset = m.GetTextureOffset("_DetailMap"), kw = m.IsKeywordEnabled("_DETAIL_MAP"),
                n = m.GetFloat("_DetailNormalScale"), s = m.GetFloat("_DetailSmoothnessScale"), a = m.GetFloat("_DetailAlbedoScale"), link = m.HasProperty("_LinkDetailsWithBase") ? m.GetFloat("_LinkDetailsWithBase") : 1f };
        m.SetTexture("_DetailMap", t);
        var cur = m.GetTextureScale("_DetailMap");
        m.SetTextureScale("_DetailMap", r.relativeTiling ? new Vector2(cur.x * r.tiling, cur.y * r.tiling) : new Vector2(r.tiling, r.tiling));
        m.SetFloat("_DetailNormalScale", r.normalScale); m.SetFloat("_DetailSmoothnessScale", r.smoothScale); m.SetFloat("_DetailAlbedoScale", r.albedoScale);
        if (m.HasProperty("_LinkDetailsWithBase")) m.SetFloat("_LinkDetailsWithBase", r.linkWithBase ? 1f : 0f);
        m.EnableKeyword("_DETAIL_MAP");
        nApplied++;
    }

    // mode off / file gone / json changed: put every touched material back exactly as it was
    static void Restore()
    {
        if (applied.Count == 0) return;
        foreach (var kv in applied)
        {
            var m = kv.Key; if (m == null) continue; var s = kv.Value;
            m.SetTexture("_DetailMap", s.tex); m.SetTextureScale("_DetailMap", s.scale); m.SetTextureOffset("_DetailMap", s.offset);
            m.SetFloat("_DetailNormalScale", s.n); m.SetFloat("_DetailSmoothnessScale", s.s); m.SetFloat("_DetailAlbedoScale", s.a);
            if (m.HasProperty("_LinkDetailsWithBase")) m.SetFloat("_LinkDetailsWithBase", s.link);
            if (s.kw) m.EnableKeyword("_DETAIL_MAP"); else m.DisableKeyword("_DETAIL_MAP");
        }
        applied.Clear(); nApplied = 0; nSkipNoProp = 0; nSkipAlbedo = 0;
    }
}
