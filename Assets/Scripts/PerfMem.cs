using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// lanePERF4 v100: memory + shadow switches as DATA (perf_mem.json, StreamingAssets in the player / Assets/Data in the editor). Missing file = everything OFF.
// Command line (same exe for every A/B arm):  -permem:E1,E3  only these ON | -permem:none  all OFF | -nopermem:E3  json minus these.
// E1 destroy the unused MetaHumanHead object | E3 QualitySettings.globalTextureMipmapLimit | E4 mip streaming | A2 sun shadowUpdateMode OnEnable + alwaysDrawDynamic.
// A1 / A1b / A4 (door lights, static casters, ground shadowCasting Off) are lanePERF3's PerfFixes (-perffix:); A3 (16 bit shadow depth) and the A2 asset flag are HDRP asset values (make_variant4.py).
public static class PerfMem
{
    [Serializable] public class E1Cfg { public bool enabled = false; public string[] names = new string[0]; public bool unloadUnused = true; }
    [Serializable] public class E3Cfg { public bool enabled = false; public int globalTextureMipmapLimit = 0; }
    [Serializable] public class E4Cfg { public bool enabled = false; public bool streamingMipmapsActive = true; public float memoryBudgetMB = 512f; public int maxLevelReduction = 2; public bool addAllCameras = true; }
    [Serializable] public class A2Cfg { public bool enabled = false; public string[] lightTypes = new string[0]; public string updateMode = "OnEnable"; public bool alwaysDrawDynamic = true; public bool requestRender = true; }
    [Serializable] public class Cfg { public bool enabled = true; public int applyAfterFrames = 2; public E1Cfg E1 = new E1Cfg(); public E3Cfg E3 = new E3Cfg(); public E4Cfg E4 = new E4Cfg(); public A2Cfg A2 = new A2Cfg(); }

    static Cfg cfg; static HashSet<string> only, off; static bool none;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; only = null; off = null; none = false; mt = null; mtName = null; mtSource = null; mtRow = null; mtExplicit = false; mtMipBase = -1; diagStage = 0; levStage = 0; levT0 = -1f; mtFlagsAll = null; glbSeen = null; glbDone = null; glbRetire = null; glbN = 0; glbSavedB = 0; glbNext = 0f; }   // FK16GB_A v1: + MemTier statics

    public static Cfg Get()
    {
        if (cfg != null) return cfg;
        var c = new Cfg();
        try
        {
            var txt = HudCfg.ReadData("perf_mem.json");
            if (!string.IsNullOrEmpty(txt)) { var r = JsonUtility.FromJson<Cfg>(txt); if (r != null) c = r; if (c.E1 == null) c.E1 = new E1Cfg(); if (c.E3 == null) c.E3 = new E3Cfg(); if (c.E4 == null) c.E4 = new E4Cfg(); if (c.A2 == null) c.A2 = new A2Cfg(); }
            else c.enabled = false;
        }
        catch (Exception e) { Debug.LogWarning("[PerfMem] perf_mem.json: " + e.Message); c.enabled = false; }
        cfg = c;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-permem:", StringComparison.OrdinalIgnoreCase)) { var v = a.Substring(8); if (v.Equals("none", StringComparison.OrdinalIgnoreCase)) none = true; else if (!v.Equals("json", StringComparison.OrdinalIgnoreCase)) only = Split(v); }
            else if (a.StartsWith("-nopermem:", StringComparison.OrdinalIgnoreCase)) off = Split(a.Substring(10));
        }
        return cfg;
    }
    static HashSet<string> Split(string s) { var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var p in s.Split(',')) if (p.Trim().Length > 0) h.Add(p.Trim()); return h; }

    /// <summary>True when switch `id` is on: command line first, then perf_mem.json (master "enabled" and the switch's own "enabled").</summary>
    public static bool On(string id)
    {
        var c = Get(); if (none) return false;
        if (only != null) return only.Contains(id);
        if (off != null && off.Contains(id)) return false;
        if (!c.enabled) return false;
        switch (id) { case "E1": return c.E1.enabled; case "E3": return c.E3.enabled; case "E4": return c.E4.enabled; case "A2": return c.A2.enabled; }
        return false;
    }
    public static string Summary()
    {
        var sb = new System.Text.StringBuilder("[PerfMem] on:"); bool any = false;
        foreach (var id in new[] { "E1", "E3", "E4", "A2" }) if (On(id)) { sb.Append(' ').Append(id); any = true; }
        if (!any) sb.Append(" none"); return sb.ToString();
    }

    // E3 / E4 are quality settings: set them BEFORE the scene textures load (BeforeSceneLoad), so the first upload already uses the limit.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Early()
    {
        var c = Get(); Debug.Log(Summary());
        if (On("E3")) { QualitySettings.globalTextureMipmapLimit = Mathf.Max(0, c.E3.globalTextureMipmapLimit); Debug.Log("[PerfMem] E3 globalTextureMipmapLimit=" + QualitySettings.globalTextureMipmapLimit); }
        MemTierApply();   // FK16GB_A v1: the Low / Medium / High preset (no-op on High). QualityTiers.Select calls it again after every quality level switch, which resets the limit.
        if (On("E4"))
        {
            QualitySettings.streamingMipmapsActive = c.E4.streamingMipmapsActive; QualitySettings.streamingMipmapsMemoryBudget = c.E4.memoryBudgetMB;
            QualitySettings.streamingMipmapsMaxLevelReduction = Mathf.Max(0, c.E4.maxLevelReduction); QualitySettings.streamingMipmapsAddAllCameras = c.E4.addAllCameras;
            Debug.Log("[PerfMem] E4 streaming active=" + QualitySettings.streamingMipmapsActive + " budgetMB=" + QualitySettings.streamingMipmapsMemoryBudget);
        }
    }


    // ======================= FK16GB_A v1: Graphics preset Low / Medium / High (memory_tiers.json) =======================
    // ONE presets file. No new boot hook: Early() above and QualityTiers.Early/Select call into this; no serialized MonoBehaviour fields (plain JsonUtility data classes).
    // Pick order: -qa_tier:low|medium|high  >  the player's saved choice (PlayerPrefs prefsKey; ignored in harness runs)  >  autoPick by RAM + VRAM (-qa_ram:N GB replaces the real RAM)  >  defaultPreset (high).
    // High changes NOTHING (mipLimit 0, quality ""), so a High start is today's behaviour bit for bit.
    [Serializable] public class MtRow { public string name = "", label = "", note = "", quality = "", level = ""; public int mipLimit = 0, glbMipDrop = 0, maskMipDrop = 0; public MtQs qs = new MtQs(); }   // FK_PRESETS v1: + level (QualitySettings level = HDRP asset), qs; the flags overlay object is cut out of the json text by MtExtractFlags
    [Serializable] public class MtAuto { public bool enabled = true; public int ramMbLow = 18000, ramMbMedium = 26000, vramMbLow = 4500, vramMbMedium = 7000; }
    [Serializable] public class MtGlb { public int minSize = 2048, maxPerTick = 2; public float tickS = 1f, settleS = 8f, retireS = 8f; public bool normalsToo = true, log = true, diag = false; public string maskSuffix = "_mask"; public int maskMinSize = 1024; }
    [Serializable] public class MtCfg { public bool enabled = true; public string defaultPreset = "high", prefsKey = "gfx.preset"; public MtAuto autoPick = new MtAuto(); public MtGlb glb = new MtGlb(); public MtRow[] presets = new MtRow[0]; }
    static MtCfg mt; static string mtName, mtSource; static MtRow mtRow; static bool mtExplicit; static int mtMipBase = -1;

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length); return null; }
    static MtRow MtFind(string n) { if (mt == null || mt.presets == null || string.IsNullOrEmpty(n)) return null; foreach (var r in mt.presets) if (r != null && string.Equals(r.name, n, StringComparison.OrdinalIgnoreCase)) return r; return null; }
    static int MtRank(string n) { return n == "low" ? 0 : n == "medium" ? 1 : 2; }

    /// <summary>System RAM in MB used for the autoPick: -qa_ram:N (GB) when given, else SystemInfo.systemMemorySize.</summary>
    public static int RamMb { get { string q = Arg("-qa_ram:"); int g; if (q != null && int.TryParse(q, out g) && g > 0) return g * 1024; return SystemInfo.systemMemorySize; } }

    public static string MemTierName { get { MtResolve(); return mtName; } }                       // "low" | "medium" | "high"
    public static string MemTierLabel { get { MtResolve(); return mtRow != null && !string.IsNullOrEmpty(mtRow.label) ? mtRow.label : (mtName == "low" ? "Low" : mtName == "medium" ? "Medium" : "High"); } }
    public static string MemTierSource { get { MtResolve(); return mtSource; } }                    // flag | saved | autoPick | default | off
    public static MtRow[] MemTierRows() { MtResolve(); return mt != null && mt.enabled && mt.presets != null ? mt.presets : new MtRow[0]; }

    static void MtResolve()
    {
        if (mtName != null) return;
        mtName = "high"; mtSource = "off"; mtRow = null;
        try
        {
            var txt = HudCfg.ReadData("memory_tiers.json");
            if (string.IsNullOrEmpty(txt)) { Debug.Log("[MemTier] preset=High source=off (no memory_tiers.json)"); return; }
            var c = JsonUtility.FromJson<MtCfg>(txt); if (c == null || !c.enabled) { Debug.Log("[MemTier] preset=High source=off (memory_tiers.json enabled=false)"); return; }
            if (c.autoPick == null) c.autoPick = new MtAuto(); if (c.glb == null) c.glb = new MtGlb(); if (string.IsNullOrEmpty(c.prefsKey)) c.prefsKey = "gfx.preset"; if (string.IsNullOrEmpty(c.defaultPreset)) c.defaultPreset = "high";
            mt = c; string n = null; string src = "default"; int ram = RamMb, vram = SystemInfo.graphicsMemorySize;
            string flag = Arg("-qa_tier:");
            mtExplicit = flag != null || Arg("-qa_ram:") != null;
            if (flag != null) { n = flag.Trim().ToLowerInvariant(); src = "flag"; }
            if (MtFind(n) == null && !RuntimeQuality.IsHarness()) { string sv = PlayerPrefs.GetString(c.prefsKey, ""); if (MtFind(sv) != null) { n = sv.ToLowerInvariant(); src = "saved"; } }
            if (MtFind(n) == null && c.autoPick.enabled)
            {
                string byRam = ram <= c.autoPick.ramMbLow ? "low" : ram <= c.autoPick.ramMbMedium ? "medium" : "high";
                string byVram = vram > 0 && vram <= c.autoPick.vramMbLow ? "low" : vram > 0 && vram <= c.autoPick.vramMbMedium ? "medium" : "high";
                n = MtRank(byRam) <= MtRank(byVram) ? byRam : byVram; src = "autoPick";
            }
            if (MtFind(n) == null) { n = c.defaultPreset.ToLowerInvariant(); src = "default"; }
            if (MtFind(n) == null) { Debug.LogWarning("[MemTier] preset '" + n + "' not in memory_tiers.json, High behaviour kept"); return; }
            mtName = n; mtSource = src; mtRow = MtFind(n);
            mtFlagsAll = new Dictionary<string, string>(); foreach (var pr in c.presets) if (pr != null && !string.IsNullOrEmpty(pr.name)) mtFlagsAll[pr.name.ToLowerInvariant()] = MtExtractFlags(txt, pr.name);   // FK_PRESETS v1
            Debug.Log("[MemTier] preset=" + MemTierLabel + " source=" + src + (src == "autoPick" || src == "flag" ? " ram=" + ram + "MB" + (Arg("-qa_ram:") != null ? "(forced -qa_ram)" : "") + " vram=" + vram + "MB" : "")
                + " mipLimit=" + mtRow.mipLimit + " glbMipDrop=" + mtRow.glbMipDrop + " maskMipDrop=" + mtRow.maskMipDrop + " limitNow=" + QualitySettings.globalTextureMipmapLimit + " level=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + " effects=" + (string.IsNullOrEmpty(mtRow.quality) ? "(unchanged)" : mtRow.quality) + (mtName == "high" ? " (High: nothing changed)" : ""));
            if (mtName != "high") Debug.Log("[MemTier] levers " + MemTierLabel + ": level=" + (string.IsNullOrEmpty(mtRow.level) ? "(tier row's own)" : mtRow.level) + " tierRow=" + mtRow.quality + " qs.skinWeights=" + mtRow.qs.skinWeights + " qs.aniso=" + mtRow.qs.aniso + " flagsOverlay=" + (mtFlagsAll[mtName].Length == 0 ? "(none)" : mtFlagsAll[mtName]));   // FK_PRESETS v1: the intent, one line (the effective values are in the levers-now lines)
        }
        catch (Exception e) { Debug.LogWarning("[MemTier] memory_tiers.json: " + e.Message); mtName = "high"; mtSource = "off"; mtRow = null; }
    }

    /// <summary>The effects row (quality_tiers.json tier name) the preset wants at boot, or "" for none (High, off, or a harness run without -qa_tier / -qa_ram, so gates keep their tier).</summary>
    public static string MemTierQuality(bool harness)
    {
        MtResolve(); if (mtRow == null || string.IsNullOrEmpty(mtRow.quality)) return "";
        if (harness && !mtExplicit) return "";
        return mtRow.quality.ToLowerInvariant();
    }

    /// <summary>Sets QualitySettings.globalTextureMipmapLimit for the preset. High (mipLimit 0) touches nothing, except that it puts back the value a Low/Medium preset replaced (menu switch back).</summary>
    public static void MemTierApply() { MtResolve(); if (mtRow == null) return; MtApplyMip(); MtApplyQs(); }   // FK_PRESETS v1: mip limit (kit A) + QualitySettings overrides
    static void MtApplyMip()
    {
        int cur = QualitySettings.globalTextureMipmapLimit, want = Mathf.Max(0, mtRow.mipLimit);
        if (want > 0)
        {
            if (cur >= want) return;
            if (mtMipBase < 0) mtMipBase = cur;
            QualitySettings.globalTextureMipmapLimit = want;
            Debug.Log("[MemTier] applied " + MemTierLabel + ": globalTextureMipmapLimit " + cur + " -> " + want + " (top mip of every texture dropped; textures already loaded change at the next start)");
        }
        else if (mtMipBase >= 0)
        {
            QualitySettings.globalTextureMipmapLimit = mtMipBase; Debug.Log("[MemTier] applied " + MemTierLabel + ": globalTextureMipmapLimit " + cur + " -> " + mtMipBase + " (restored)"); mtMipBase = -1;
        }
    }


    // ---- glTFast runtime textures (gear, NPC, mount, boss weapons): created with ONE mip level, so QualitySettings.globalTextureMipmapLimit never changes them (a limit never drops the last mip).
    // Preset row glbMipDrop N: every uncompressed single-mip texture >= glb.minSize is blitted on the GPU to (w >> N, h >> N) (no CPU readback), swapped on every material that holds it, the original destroyed glb.retireS later
    // (a preload may still hold it: the 17 Nov GlbPreload NullReference). Ticked from PerfMemRunner.Update (existing boot path). High: glbMipDrop 0 = nothing runs.
    static Dictionary<Texture2D, float> glbSeen; static HashSet<Texture2D> glbDone; static List<KeyValuePair<float, Texture2D>> glbRetire; static float glbNext; static int glbN; static long glbSavedB;
    public static int GlbMipDrop { get { MtResolve(); return mtRow != null ? Mathf.Max(0, mtRow.glbMipDrop) : 0; } }
    public static int MaskMipDrop { get { MtResolve(); return mtRow != null ? Mathf.Max(0, mtRow.maskMipDrop) : 0; } }
    static bool DiagOn { get { MtResolve(); return mt != null && mt.glb != null && (mt.glb.diag || Arg("-qa_mipdiag") != null); } }
    /// <summary>True when PerfMemRunner has to stay alive: glb downsize, mask downsize or the diag line (-qa_mipdiag / glb.diag). High with no diag = false.</summary>
    public static bool RunnerWanted { get { return GlbMipDrop > 0 || MaskMipDrop > 0 || DiagOn || LevOn; } }   // FK_PRESETS v1: + the lever dump
    static int diagStage;

    // v3 DIAG (Strategy 18 Nov, kit only): TexTop's per-format totals use t.width/height/mipmapCount, which do NOT change with QualitySettings.globalTextureMipmapLimit (Low log: a 4096 BC7 still prints 21.3 MB), so a Low-vs-Ultra TexTop total cannot show the limit.
    // This prints the limit at the first tick and at 60 s, how many of the top 200 textures by nominal size have mips=1 per group, and Texture.currentTextureMemory (the real GPU number) next to the nominal total.
    static void DiagTick(float now)
    {
        if (!DiagOn) return;
        if (diagStage == 0) { diagStage = 1; Debug.Log("[MemTier] diag t=" + now.ToString("F1") + "s globalTextureMipmapLimit=" + QualitySettings.globalTextureMipmapLimit + " qualityLevel=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + " preset=" + MemTierLabel); }
        if (diagStage == 1 && now >= 60f) { diagStage = 2; DiagDump(); }
    }
    static void DiagDump()
    {
        try
        {
            var list = new List<KeyValuePair<long, Texture2D>>(); long nominal = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (t == null) continue; long sz = 0; int w = t.width, h = t.height;
                try { var gf = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(t.format, false); for (int m = 0; m < Mathf.Max(1, t.mipmapCount); m++) { sz += (long)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(Mathf.Max(1, w), Mathf.Max(1, h), gf); w >>= 1; h >>= 1; } } catch (Exception) { }
                nominal += sz; list.Add(new KeyValuePair<long, Texture2D>(sz, t));
            }
            list.Sort((a, b) => b.Key.CompareTo(a.Key));
            string[] grp = { "glbRuntime", "mask", "imported", "other" }; var one = new int[4]; var all = new int[4]; var mb = new long[4];
            for (int i = 0; i < list.Count && i < 200; i++)
            {
                var t = list[i].Value; string n = (t.name ?? "").ToLowerInvariant(); var f = t.format;
                bool unc = f == TextureFormat.ARGB32 || f == TextureFormat.RGBA32 || f == TextureFormat.RGB24 || f == TextureFormat.BGRA32;
                int gi = n.EndsWith("_mask") ? 1 : (t.mipmapCount == 1 && unc) ? 0 : t.mipmapCount > 1 ? 2 : 3;
                all[gi]++; mb[gi] += list[i].Key; if (t.mipmapCount == 1) one[gi]++;
            }
            int ones = 0; for (int k = 0; k < 4; k++) ones += one[k];
            var sb = new System.Text.StringBuilder("[MemTier] diag t=60s " + ones + " of top 200 have no mips (mips=1); globalTextureMipmapLimit now=" + QualitySettings.globalTextureMipmapLimit
                + " nominalTexGB=" + (nominal / 1073741824.0).ToString("F2") + " currentTextureMemoryGB=" + (((long)Texture.currentTextureMemory) / 1073741824.0).ToString("F2") + " (the nominal figure ignores the limit; the real one does not)");
            for (int k = 0; k < 4; k++) sb.Append(" | " + grp[k] + " n=" + all[k] + " mips1=" + one[k] + " " + (mb[k] / 1048576.0).ToString("F0") + "MB");
            Debug.Log(sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[MemTier] diag failed: " + e.Message); }
    }

    // v3 MASK / IMPORTED-CHAIN downsize (fallback when an importer-side limit cannot be used): a texture WITH a mip chain (BC7/BC5/DXT5, name ends with glb.maskSuffix, e.g. the generated HDRP _MaskMap pngs) is rebuilt without its top N mips:
    // new Texture2D(w>>N, h>>N, same compressed GraphicsFormat, mips-N) and Graphics.CopyTexture(src, 0, m+N, dst, 0, m) for every mip: GPU only, no readback, no re-encode. Swapped on every material, original destroyed retireS later.
    // If globalTextureMipmapLimit already reaches such a texture the top mips are not on the GPU and the copy fails (caught, texture left alone); then set maskMipDrop 0.
    static Texture2D ChainDrop(Texture2D t, int drop)
    {
        try
        {
            int w = t.width >> drop, h = t.height >> drop, mips = t.mipmapCount - drop;
            if (mips < 1 || w < 4 || h < 4 || (w % 4) != 0 || (h % 4) != 0) return null;
            var nt = new Texture2D(w, h, t.graphicsFormat, mips, UnityEngine.Experimental.Rendering.TextureCreationFlags.None) { name = t.name, wrapMode = t.wrapMode, filterMode = t.filterMode, anisoLevel = t.anisoLevel, mipMapBias = t.mipMapBias };
            for (int m = 0; m < mips; m++) Graphics.CopyTexture(t, 0, m + drop, nt, 0, m);
            return nt;
        }
        catch (Exception e) { Debug.LogWarning("[MemTier] mask downsize " + t.name + ": " + e.Message); return null; }
    }

    public static void GlbTick()
    {
        DownsizeLoad.Tick(); int drop = GlbMipDrop, mdrop = MaskMipDrop; if (mt == null || (drop <= 0 && mdrop <= 0 && !DiagOn && !LevOn)) return;
        DiagTick(Time.realtimeSinceStartup); LevTick(Time.realtimeSinceStartup); if (drop <= 0 && mdrop <= 0) return;
        drop = Mathf.Max(0, drop);
        var g = mt.glb ?? new MtGlb(); float now = Time.realtimeSinceStartup;
        if (glbSeen == null) { glbSeen = new Dictionary<Texture2D, float>(); glbDone = new HashSet<Texture2D>(); glbRetire = new List<KeyValuePair<float, Texture2D>>(); Debug.Log("[MemTier] " + MemTierLabel + " glb downsize active: runtime textures >= " + g.minSize + " with 1 mip drop " + drop + " level(s) (globalTextureMipmapLimit cannot reach them); mask downsize " + (mdrop > 0 && QualitySettings.globalTextureMipmapLimit > 0 ? "SKIPPED (mip limit " + QualitySettings.globalTextureMipmapLimit + " already reaches the masks)" : mdrop.ToString())); }
        for (int i = glbRetire.Count - 1; i >= 0; i--) if (now >= glbRetire[i].Key) { if (glbRetire[i].Value != null) UnityEngine.Object.Destroy(glbRetire[i].Value); glbRetire.RemoveAt(i); }
        if (now < glbNext) return; glbNext = now + Mathf.Max(0.2f, g.tickS);
        try
        {
            if (SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None) return;
            var map = new Dictionary<Texture2D, Texture2D>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (t == null || map.Count >= Mathf.Max(1, g.maxPerTick)) break;
                if (glbDone.Contains(t) || DownsizeLoad.IsReduced(t)) continue;   // DOWNSIZELOAD v1: made at the reduced size at load, never reduced twice
                if (mdrop > 0 && QualitySettings.globalTextureMipmapLimit == 0 && t.mipmapCount > 1 && t.width >= g.maskMinSize && !string.IsNullOrEmpty(g.maskSuffix) && (t.name ?? "").EndsWith(g.maskSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    float fm; if (!glbSeen.TryGetValue(t, out fm)) { glbSeen[t] = now; continue; }
                    if (now - fm < g.settleS) continue;
                    glbDone.Add(t); var mt2 = ChainDrop(t, mdrop); if (mt2 != null) map[t] = mt2; continue;
                }
                if (drop <= 0) continue;
                var f = t.format; if (f != TextureFormat.ARGB32 && f != TextureFormat.RGBA32 && f != TextureFormat.RGB24 && f != TextureFormat.BGRA32) continue;
                if (t.mipmapCount != 1 || t.width < g.minSize || t.height < g.minSize) continue;
                string n = t.name == null ? "" : t.name; if (n.StartsWith("Cookie") || n.StartsWith("zgm_")) continue;
                if (!g.normalsToo && n.ToLowerInvariant().Contains("normal")) continue;
                float first; if (!glbSeen.TryGetValue(t, out first)) { glbSeen[t] = now; continue; }
                if (now - first < g.settleS) continue;
                glbDone.Add(t);
                var nt = GlbDownsize(t, drop); if (nt != null) map[t] = nt;
            }
            if (map.Count == 0) return;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                foreach (int pid in m.GetTexturePropertyNameIDs()) { var cur = m.GetTexture(pid) as Texture2D; Texture2D to; if (cur != null && map.TryGetValue(cur, out to)) m.SetTexture(pid, to); }
            }
            foreach (var kv in map) { glbSavedB += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(kv.Key) - UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(kv.Value); glbRetire.Add(new KeyValuePair<float, Texture2D>(now + Mathf.Max(1f, g.retireS), kv.Key)); glbN++; }
            if (g.log) Debug.Log("[MemTier] glb/mask downsize +" + map.Count + " total " + glbN + " textures, saved " + (glbSavedB / 1073741824.0).ToString("F2") + " GB (estimate by runtime size, originals freed " + g.retireS + " s later)");
        }
        catch (Exception e) { Debug.LogWarning("[MemTier] glb downsize failed: " + e.Message); glbNext = float.MaxValue; }
    }

    static Texture2D GlbDownsize(Texture2D t, int drop)
    {
        RenderTexture rt = null;
        try
        {
            int w = Mathf.Max(4, t.width >> drop), h = Mathf.Max(4, t.height >> drop);
            bool srgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);
            var gf = srgb ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            Graphics.Blit(t, rt);
            var nt = new Texture2D(w, h, gf, 1, UnityEngine.Experimental.Rendering.TextureCreationFlags.None) { name = t.name, wrapMode = t.wrapMode, filterMode = t.filterMode, anisoLevel = t.anisoLevel, mipMapBias = t.mipMapBias };
            Graphics.CopyTexture(rt, nt);
            return nt;
        }
        catch (Exception e) { Debug.LogWarning("[MemTier] glb downsize " + t.name + ": " + e.Message); return null; }
        finally { if (rt != null) RenderTexture.ReleaseTemporary(rt); }
    }

    // ======================= FK_PRESETS v1: the per-preset lever table (level + qs + flags overlay), all runtime, no HDRP asset edit =======================
    // level  : QualitySettings level name = which existing HDRP asset is used (shadow atlas, decal atlas, SSS, depth bits, DRS floor). QualityTiers.LevelOf asks MemTierLevelFor.
    // qs     : QualitySettings values set after every level switch (SetQualityLevel resets them; every preset change also changes the level, so no restore code is needed).
    // flags  : RuntimeQuality.Flags keys overlaid on the effects row by QualityTiers.Overlay (renderScale, shadowDistance, cascades, fogQuality, ssrQuality, ssaoQuality, bloomQuality, lodBiasMul, particleScale, motionBlur ...).
    // All three apply only while the active effects row == the preset's quality row (MtGate) and never in a harness run without -qa_tier / -qa_ram. High has none of them.
    [Serializable] public class MtQs { public int skinWeights = -1, aniso = -1; }
    static Dictionary<string, string> mtFlagsAll; static int levStage; static float levT0 = -1f;

    static bool MtGate(string tier)
    {
        MtResolve(); if (mtRow == null || string.IsNullOrEmpty(mtRow.quality)) return false;
        if (RuntimeQuality.IsHarness() && !mtExplicit) return false;
        return string.Equals(tier, mtRow.quality, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The preset's flags overlay (raw json object) when it applies to this effects row, else "".</summary>
    public static string MemTierFlagsFor(string tier) { string f; return MtGate(tier) && mtFlagsAll != null && mtFlagsAll.TryGetValue(mtName, out f) && f != null ? f : ""; }
    /// <summary>The QualitySettings level name the preset wants for this effects row, else "" (the row's own level).</summary>
    public static string MemTierLevelFor(string tier) { return MtGate(tier) && !string.IsNullOrEmpty(mtRow.level) ? mtRow.level : ""; }

    // cuts the "flags" object of the preset whose "name" is `preset` out of the json text (brace matching inside that preset's own braces; json strings hold no braces: check_presets.py checks that). "" when the preset has none.
    static string MtExtractFlags(string text, string preset)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, "\"name\"\\s*:\\s*\"" + System.Text.RegularExpressions.Regex.Escape(preset) + "\"");
        if (!m.Success) return "";
        int open = text.LastIndexOf('{', m.Index); if (open < 0) return "";
        int depth = 0, end = -1;
        for (int i = open; i < text.Length; i++) { if (text[i] == '{') depth++; else if (text[i] == '}' && --depth == 0) { end = i; break; } }
        if (end < 0) return "";
        int f = text.IndexOf("\"flags\"", open, end - open, StringComparison.Ordinal); if (f < 0) return "";
        int o = text.IndexOf('{', f, end - f); if (o < 0) return "";
        depth = 0;
        for (int i = o; i <= end; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return System.Text.RegularExpressions.Regex.Replace(text.Substring(o, i - o + 1), "\\s+", "");   // whitespace-free = one grep-able log token
        }
        return "";
    }

    static void MtApplyQs()
    {
        if (mtRow == null || mtRow.qs == null) return;
        if (mtRow.qs.skinWeights >= 0 && (int)QualitySettings.skinWeights != mtRow.qs.skinWeights) { var o = QualitySettings.skinWeights; QualitySettings.skinWeights = (SkinWeights)mtRow.qs.skinWeights; Debug.Log("[MemTier] applied " + MemTierLabel + ": skinWeights " + o + " -> " + QualitySettings.skinWeights); }
        if (mtRow.qs.aniso >= 0 && (int)QualitySettings.anisotropicFiltering != mtRow.qs.aniso) { var o = QualitySettings.anisotropicFiltering; QualitySettings.anisotropicFiltering = (AnisotropicFiltering)mtRow.qs.aniso; Debug.Log("[MemTier] applied " + MemTierLabel + ": anisotropicFiltering " + o + " -> " + QualitySettings.anisotropicFiltering); }
    }

    // lever dump: the EFFECTIVE values the renderer holds (volume stack, HDRP asset, QualitySettings), at the first tick + 4 s and at 60 s. Automatic on Low / Medium, High only with -qa_levers (so a plain High start logs nothing extra).
    static bool LevOn { get { MtResolve(); return mt != null && mt.enabled && (Arg("-qa_levers") != null || (mtRow != null && mtName != "high")); } }
    static void LevTick(float now)
    {
        if (!LevOn) return;
        if (levT0 < 0f) levT0 = now;
        if (levStage == 0 && now - levT0 >= 4f) { levStage = 1; LevDump("t=" + now.ToString("F0") + "s"); }
        else if (levStage == 1 && now >= 60f) { levStage = 2; LevDump("t=60s"); }
    }

    static T LevGet<T>(VolumeStack st, VolumeProfile pr) where T : VolumeComponent { if (st != null) return st.GetComponent<T>(); T c; return pr != null && pr.TryGet(out c) ? c : null; }
    static string LevDescribe(string pfx, VolumeStack st, VolumeProfile pr)
    {
        string o = "";
        var hs = LevGet<HDShadowSettings>(st, pr); if (hs != null) o += " " + pfx + ".sunDist=" + hs.maxShadowDistance.value.ToString("F0") + " " + pfx + ".cascades=" + hs.cascadeShadowSplitCount.value;
        var fg = LevGet<Fog>(st, pr); if (fg != null) o += " " + pfx + ".fogVol=" + (fg.enableVolumetricFog.value ? 1 : 0) + " " + pfx + ".fogQ=" + fg.quality.value;
        var gi = LevGet<GlobalIllumination>(st, pr); if (gi != null) o += " " + pfx + ".ssgi=" + (gi.active ? 1 : 0) + " " + pfx + ".ssgiQ=" + gi.quality.value;
        var sr = LevGet<ScreenSpaceReflection>(st, pr); if (sr != null) o += " " + pfx + ".ssr=" + (sr.active ? 1 : 0) + " " + pfx + ".ssrQ=" + sr.quality.value;
        var ao = LevGet<ScreenSpaceAmbientOcclusion>(st, pr); if (ao != null) o += " " + pfx + ".ssao=" + (ao.active ? 1 : 0) + " " + pfx + ".ssaoQ=" + ao.quality.value;
        var cs = LevGet<ContactShadows>(st, pr); if (cs != null) o += " " + pfx + ".contact=" + (cs.active ? 1 : 0);
        var bl = LevGet<Bloom>(st, pr); if (bl != null) o += " " + pfx + ".bloomQ=" + bl.quality.value;
        var mb = LevGet<MotionBlur>(st, pr); if (mb != null) o += " " + pfx + ".motionBlur=" + mb.intensity.value.ToString("F2");
        var vc = LevGet<VolumetricClouds>(st, pr); if (vc != null) o += " " + pfx + ".clouds=" + (vc.active ? 1 : 0);
        return o;
    }
    static string LevStacks()
    {
        string o = "";
        try   // the cloned profile of the global Volume (what RuntimeQuality.Apply wrote); only Volumes that already own a clone, so nothing is instantiated here
        {
            foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None)) if (v != null && v.isGlobal && v.HasInstantiatedProfile() && v.profile != null && v.profile.TryGet(out HDShadowSettings _)) { o += " cloneOf=" + v.name + LevDescribe("clone", null, v.profile); break; }
        }
        catch (Exception e) { o += " cloneReadFailed=" + e.Message; }
        try   // what the camera renders with: its own HDCamera volume stack (all Volumes blended: look, zone, local) and the DRS size it really uses
        {
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || !cam.enabled || cam.targetTexture != null || cam.GetComponent<HDAdditionalCameraData>() == null) continue;
                var hd = HDCamera.GetOrCreate(cam);
                o += " cam=" + cam.name + LevDescribe("cam", hd.volumeStack, null) + " cam.px=" + cam.pixelWidth + "x" + cam.pixelHeight + " cam.drs=" + hd.actualWidth + "x" + hd.actualHeight + " cam.drsFrac=" + (hd.actualWidth / (float)Mathf.Max(1, cam.pixelWidth)).ToString("F3");
                break;
            }
        }
        catch (Exception e) { o += " camReadFailed=" + e.Message; }
        return o;
    }
    static void LevDump(string tag)
    {
        string o = "[MemTier] levers-now " + tag + " preset=" + MemTierLabel + " src=" + MemTierSource + " tierRow=" + QualityTiers.Current;
        try { o += " qualityLevel=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + " mipLimit=" + QualitySettings.globalTextureMipmapLimit + " lodBias=" + QualitySettings.lodBias.ToString("F2") + " skinWeights=" + QualitySettings.skinWeights + " aniso=" + QualitySettings.anisotropicFiltering + " screen=" + Screen.width + "x" + Screen.height; } catch (Exception) { }
        try
        {
            var pipe = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (pipe != null)
            {
                var ps = pipe.currentPlatformRenderPipelineSettings;
                o += " asset=" + pipe.name + " punctualAtlas=" + ps.hdShadowInitParams.punctualLightShadowAtlas.shadowAtlasResolution + " sunMapMax=" + ps.hdShadowInitParams.maxDirectionalShadowMapResolution + " shadowDepth=" + ps.hdShadowInitParams.directionalShadowsDepthBits
                    + " decalAtlas=" + ps.decalSettings.atlasWidth + "x" + ps.decalSettings.atlasHeight + " decalDraw=" + ps.decalSettings.drawDistance + " sss=" + (ps.supportSubsurfaceScattering ? 1 : 0) + " drsMin=" + ps.dynamicResolutionSettings.minPercentage;
            }
        }
        catch (Exception e) { o += " assetReadFailed=" + e.Message; }
        o += LevStacks();   // FK_PRESETS v3: effective values from the CAMERA's own volume stack (HDCamera.volumeStack) and from the cloned global Volume profile; the default VolumeManager.instance.stack is never updated by HDRP (it read sunDist=500 cascades=4 = component defaults)
        Debug.Log(o);
    }

    /// <summary>Settings screen: choose a preset. Saves it, applies the mip limit now and returns the effects row the caller should switch to ("" = the data default). null = unknown preset.</summary>
    public static string SetMemTier(string name)
    {
        MtResolve(); var r = MtFind(name); if (r == null) return null;
        mtName = r.name.ToLowerInvariant(); mtRow = r; mtSource = "saved";
        try { PlayerPrefs.SetString(mt.prefsKey, mtName); PlayerPrefs.Save(); } catch (Exception) { }
        Debug.Log("[MemTier] menu: preset set to " + MemTierLabel + " (saved; texture detail fully applies at the next start)");
        MemTierApply();
        return string.IsNullOrEmpty(r.quality) ? "" : r.quality.ToLowerInvariant();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!On("E1") && !On("A2") && !RunnerWanted) return;   // FK16GB_A v2: the preset's glb / mask downsize and the diag line also need the runner
        var go = new GameObject("PerfMemRunner"); go.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<PerfMemRunner>();
    }

    public static string ApplyE1()
    {
        var c = Get().E1; int n = 0; var seen = new HashSet<GameObject>();
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            var g = t.gameObject; if (!g.scene.IsValid()) continue;   // scene objects only (not assets / prefabs)
            if (Array.IndexOf(c.names, g.name) < 0 || !seen.Add(g)) continue;
            UnityEngine.Object.Destroy(g); n++;
        }
        return "E1 destroyed=" + n;
    }

    static LightType ParseType(string s) { try { return (LightType)Enum.Parse(typeof(LightType), s, true); } catch { return LightType.Directional; } }
    static ShadowUpdateMode ParseMode(string s) { try { return (ShadowUpdateMode)Enum.Parse(typeof(ShadowUpdateMode), s, true); } catch { return ShadowUpdateMode.EveryFrame; } }
    public static string ApplyA2()
    {
        var c = Get().A2; int n = 0; var mode = ParseMode(c.updateMode);
        var types = new List<LightType>(); foreach (var s in c.lightTypes) types.Add(ParseType(s));
        foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (!types.Contains(l.type) || l.shadows == LightShadows.None) continue;
            var hd = l.GetComponent<HDAdditionalLightData>(); if (hd == null) continue;
            hd.shadowUpdateMode = mode; hd.alwaysDrawDynamicShadows = c.alwaysDrawDynamic;
            if (c.requestRender && mode != ShadowUpdateMode.EveryFrame) hd.RequestShadowMapRendering();
            n++;
        }
        return "A2 lights=" + n + " mode=" + mode;
    }
}

public class PerfMemRunner : MonoBehaviour
{
    IEnumerator Start()
    {
        int f = Mathf.Max(1, PerfMem.Get().applyAfterFrames); for (int i = 0; i < f; i++) yield return null;
        if (PerfMem.On("E1"))
        {
            Debug.Log("[PerfMem] " + PerfMem.ApplyE1());
            if (PerfMem.Get().E1.unloadUnused) { yield return null; yield return Resources.UnloadUnusedAssets(); Debug.Log("[PerfMem] E1 unloaded unused assets"); }
        }
        if (PerfMem.On("A2")) Debug.Log("[PerfMem] " + PerfMem.ApplyA2());
        if (!PerfMem.RunnerWanted) Destroy(gameObject);   // FK16GB_A v2: stays alive to tick the Low / Medium glb downsize
    }
    void Update() { PerfMem.GlbTick(); }
}
