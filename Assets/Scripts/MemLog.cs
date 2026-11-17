using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

// MEMLOG v1 (unity-build, laneMEM step 0): read-only memory log, no behaviour change. One line at +20 s, +60 s and +180 s after the scene loads:
// "[MemLog] t=60s texNow=x.xx GB texNonStreaming=x.xx GB reserved=x.xx GB allocated=x.xx GB mono=x.xx GB workingSet=x.xx GB private=x.xx GB".
// Never runs in batchmode / harness-free headless runs (no player). Safe: every call is in try/catch and only reads counters.
public class MemLog : MonoBehaviour
{
    static MemLog inst;
    static readonly float[] marks = { 20f, 60f, 180f };   // hc:ok seconds after load for the three log lines
    int next;
    float t0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || Application.isBatchMode) return;
        LoadCfg(); TexShare.LoadCfg();
        var go = new GameObject("MemLog"); DontDestroyOnLoad(go); inst = go.AddComponent<MemLog>();
    }

    void Start() { t0 = Time.realtimeSinceStartup; }

    static string Gb(long bytes) { return (bytes / 1073741824.0).ToString("F2"); }   // hc:ok bytes per GiB

    // TEXCOMPRESS v1 (Lead): runtime-decoded uncompressed textures (glTFast gear/mount/NPC maps, PNG world maps) are 64-85 MB each; Texture2D.Compress -> DXT1/DXT5 makes them 4:1 / 6:1.
    // Data row texture_compress.json (enabled false = off, hot). Hooked here because MemLog.Boot is an existing RuntimeInitializeOnLoadMethod (a compile-only take cannot add a new one).
    [Serializable] class CCfg { public bool enabled = false; public int minSize = 2048; public int maxPerTick = 2; public float tickS = 1f; public bool highQuality = true; public bool normalsToo = false; public string[] excludeContains = new string[0]; }
    static CCfg ccfg; float nextC; int compressedN; long savedB;

    static void LoadCfg()
    {
        try
        {
            foreach (var p in DataPath.Candidates("texture_compress.json"))
            {
                if (!System.IO.File.Exists(p)) continue;
                ccfg = JsonUtility.FromJson<CCfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); break;
            }
            foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-texcompress:on") { if (ccfg == null) ccfg = new CCfg(); ccfg.enabled = true; } else if (a == "-texcompress:off" && ccfg != null) ccfg.enabled = false; }
        }
        catch (Exception e) { Debug.LogWarning("[TexCompress] cfg: " + e.Message); }
        Debug.Log("[TexCompress] " + (ccfg != null && ccfg.enabled ? "on minSize=" + ccfg.minSize + " hq=" + ccfg.highQuality + " normals=" + ccfg.normalsToo : "off (data)"));
    }

    static readonly HashSet<Texture2D> skipped = new HashSet<Texture2D>();

    // non-readable texture (glTFast output): GPU blit to a RenderTexture, read back, build mips, compress, swap it on every material that holds it, destroy the old one.
    static bool Reencode(Texture2D t, bool hq)
    {
        RenderTexture rt = null; Texture2D nt = null; var prev = RenderTexture.active;
        try
        {
            bool srgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);
            rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            Graphics.Blit(t, rt); RenderTexture.active = rt;
            nt = new Texture2D(t.width, t.height, TextureFormat.RGBA32, true, !srgb) { name = t.name, wrapMode = t.wrapMode, filterMode = t.filterMode, anisoLevel = t.anisoLevel };
            nt.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0, false); nt.Apply(true, false);
            nt.Compress(hq); nt.Apply(false, true);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); rt = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null) continue;
                foreach (int id in m.GetTexturePropertyNameIDs()) { if (m.GetTexture(id) == t) m.SetTexture(id, nt); }
            }
            UnityEngine.Object.Destroy(t);
            return true;
        }
        catch (Exception e) { Debug.LogWarning("[TexCompress] reencode " + t.name + ": " + e.Message); if (nt != null) UnityEngine.Object.Destroy(nt); return false; }
        finally { RenderTexture.active = prev; if (rt != null) RenderTexture.ReleaseTemporary(rt); }
    }

    void TickCompress()
    {
        if (ccfg == null || !ccfg.enabled || Time.realtimeSinceStartup < nextC) return;
        nextC = Time.realtimeSinceStartup + Mathf.Max(0.2f, ccfg.tickS);
        try
        {
            int done = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (t == null || done >= ccfg.maxPerTick) break;
                if (skipped.Contains(t)) continue;
                var f = t.format;
                if (f != TextureFormat.ARGB32 && f != TextureFormat.RGBA32 && f != TextureFormat.RGB24 && f != TextureFormat.BGRA32) continue;
                if (t.width < ccfg.minSize || t.width % 4 != 0 || t.height % 4 != 0) continue;
                string n = t.name == null ? "" : t.name; string l = n.ToLowerInvariant();
                if (!ccfg.normalsToo && l.Contains("normal")) continue;
                bool skip = false; foreach (var ex in ccfg.excludeContains) { if (!string.IsNullOrEmpty(ex) && l.Contains(ex.ToLowerInvariant())) { skip = true; break; } }
                if (skip) continue;
                long before = Est(t);
                if (t.isReadable) { t.Compress(ccfg.highQuality); t.Apply(false, true); }
                else if (!Reencode(t, ccfg.highQuality)) { skipped.Add(t); continue; }
                long after = Est(t); savedB += before - after; compressedN++; done++;
            }
            if (done > 0) Debug.Log("[TexCompress] +" + done + " total " + compressedN + " saved " + Gb(savedB) + " GB");
        }
        catch (Exception e) { Debug.LogWarning("[TexCompress] failed: " + e.Message); nextC = float.MaxValue; }
    }

    void Update()
    {
        TickCompress();
        TexShare.Tick();
        if (next >= marks.Length && (ccfg == null || !ccfg.enabled) && !TexShare.On) { enabled = false; return; }
        if (next >= marks.Length) return;
        if (Time.realtimeSinceStartup - t0 < marks[next]) return;
        float mark = marks[next++];
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            Debug.Log("[MemLog] t=" + mark.ToString("F0") + "s texNow=" + Gb((long)Texture.currentTextureMemory) + " GB texNonStreaming=" + Gb((long)Texture.nonStreamingTextureMemory)
                + " GB reserved=" + Gb(Profiler.GetTotalReservedMemoryLong()) + " GB allocated=" + Gb(Profiler.GetTotalAllocatedMemoryLong())
                + " GB mono=" + Gb(Profiler.GetMonoUsedSizeLong()) + " GB gc=" + Gb(GC.GetTotalMemory(false)) + " GB workingSet=" + Gb(p.WorkingSet64) + " GB private=" + Gb(p.PrivateMemorySize64) + " GB");
        }
        catch (Exception e) { Debug.LogWarning("[MemLog] failed: " + e.Message); }
        if (mark == 60f) TexTop();
    }

    // TEXTOP v1: top 40 textures by runtime memory at +60 s plus totals per owner bucket (read-only, one log block).
    static void TexTop()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll<Texture>();
            var list = new System.Collections.Generic.List<KeyValuePair<long, Texture>>(all.Length);
            long total = 0;
            var buckets = new System.Collections.Generic.Dictionary<string, long>();
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var t in all)
            {
                if (t == null) continue;
                long sz = Est(t);
                total += sz; list.Add(new KeyValuePair<long, Texture>(sz, t));
                string n = t.name == null ? "" : t.name;
                string b = Bucket(n);
                long cur; buckets.TryGetValue(b, out cur); buckets[b] = cur + sz;
                int c; counts.TryGetValue(b, out c); counts[b] = c + 1;
            }
            list.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new System.Text.StringBuilder();
            // TABLE: bytes per format, duplicates (same name+size+format), and textures referenced by an ACTIVE renderer's material vs not
            var fmtB = new Dictionary<string, long>(); var fmtN = new Dictionary<string, int>(); var seen = new Dictionary<string, int>(); long dupB = 0; int dupN = 0;
            foreach (var kv in list)
            {
                var t2 = kv.Value as Texture2D; string fm = t2 != null ? t2.format.ToString() : kv.Value.GetType().Name;
                long cb; fmtB.TryGetValue(fm, out cb); fmtB[fm] = cb + kv.Key; int cn; fmtN.TryGetValue(fm, out cn); fmtN[fm] = cn + 1;
                string key = kv.Value.name + "|" + kv.Value.width + "x" + kv.Value.height + "|" + fm; int sc; seen.TryGetValue(key, out sc); seen[key] = sc + 1; if (sc > 0) { dupB += kv.Key; dupN++; }
            }
            foreach (var kv in fmtB) sb.Append("[TexTop] format " + kv.Key + " n=" + fmtN[kv.Key] + " " + Gb(kv.Value) + " GB\n");
            sb.Append("[TexTop] duplicates (same name+size+format, 2nd+ copies) n=" + dupN + " " + Gb(dupB) + " GB\n");
            var used = new HashSet<Texture>(); long usedB = 0; int usedN = 0;
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (r == null || !r.enabled) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (int id in m.GetTexturePropertyNameIDs()) { var tx = m.GetTexture(id); if (tx != null) used.Add(tx); }
                }
            }
            foreach (var kv in list) if (used.Contains(kv.Value)) { usedB += kv.Key; usedN++; }
            // top duplicate groups (copies x size) and top unreferenced textures
            var grp = new List<KeyValuePair<long, string>>();
            var gsz = new Dictionary<string, long>();
            foreach (var kv in list) { var t2 = kv.Value as Texture2D; string key = kv.Value.name + "|" + kv.Value.width + "x" + kv.Value.height + "|" + (t2 != null ? t2.format.ToString() : kv.Value.GetType().Name); gsz[key] = kv.Key; }
            foreach (var kv in seen) if (kv.Value > 1) grp.Add(new KeyValuePair<long, string>((kv.Value - 1) * gsz[kv.Key], kv.Value + " copies " + kv.Key));
            grp.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < grp.Count && i < 25; i++) sb.Append("[TexTop] dupgroup " + (grp[i].Key / 1048576.0).ToString("F0") + " MB extra: " + grp[i].Value + "\n");
            var unF = new Dictionary<string, long>(); var unFn = new Dictionary<string, int>(); var unB = new Dictionary<string, long>();
            foreach (var kv in list)
            {
                if (used.Contains(kv.Value)) continue;
                var t2 = kv.Value as Texture2D; string fm = t2 != null ? t2.format.ToString() : kv.Value.GetType().Name;
                long a; unF.TryGetValue(fm, out a); unF[fm] = a + kv.Key; int an; unFn.TryGetValue(fm, out an); unFn[fm] = an + 1;
                string bk = Bucket(kv.Value.name); long ab; unB.TryGetValue(bk, out ab); unB[bk] = ab + kv.Key;
            }
            foreach (var kv in unF) sb.Append("[TexTop] unref-format " + kv.Key + " n=" + unFn[kv.Key] + " " + Gb(kv.Value) + " GB\n");
            foreach (var kv in unB) sb.Append("[TexTop] unref-bucket " + kv.Key + " " + Gb(kv.Value) + " GB\n");
            int un = 0;
            foreach (var kv in list) { if (used.Contains(kv.Value) || un >= 25) continue; un++; sb.Append("[TexTop] unreferenced " + (kv.Key / 1048576.0).ToString("F1") + " MB " + kv.Value.width + "x" + kv.Value.height + " " + kv.Value.name + "\n"); }
            sb.Append("[TexTop] referenced by an active enabled renderer: n=" + usedN + " " + Gb(usedB) + " GB; NOT referenced: n=" + (list.Count - usedN) + " " + Gb(total - usedB) + " GB\n");
            sb.Append("[TexTop] count=" + list.Count + " total=" + Gb(total) + " GB\n");
            foreach (var kv in buckets) sb.Append("[TexTop] bucket " + kv.Key + " n=" + counts[kv.Key] + " " + Gb(kv.Value) + " GB\n");
            for (int i = 0; i < list.Count && i < 40; i++)   // hc:ok top-40 diagnostic
            {
                var t = list[i].Value; var t2 = t as Texture2D;
                string fmt = t2 != null ? t2.format.ToString() : t.GetType().Name;
                int mips = t2 != null ? t2.mipmapCount : 0;
                sb.Append("[TexTop] " + (list[i].Key / 1048576.0).ToString("F1") + " MB " + t.width + "x" + t.height + " " + fmt + " mips=" + mips + " " + t.name + "\n");
            }
            Debug.Log(sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[TexTop] failed: " + e.Message); }
    }

    // size from format and mip chain for Texture2D (Profiler returned ~0 for most characters and glTF maps), Profiler value otherwise
    static long Est(Texture t)
    {
        long prof = Profiler.GetRuntimeMemorySizeLong(t);
        var t2 = t as Texture2D;
        if (t2 == null) return prof;
        try
        {
            var gf = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetGraphicsFormat(t2.format, false);
            long sum = 0; int w = t2.width, h = t2.height;
            for (int m = 0; m < Mathf.Max(1, t2.mipmapCount); m++)
            {
                sum += (long)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(Mathf.Max(1, w), Mathf.Max(1, h), gf);
                w >>= 1; h >>= 1;
            }
            return Math.Max(prof, sum);
        }
        catch { return prof; }
    }

    static string Bucket(string n)
    {
        string l = n.ToLowerInvariant();
        if (l.StartsWith("zgm_")) return "zgm";
        if (l.StartsWith("image_")) return "glb_image";
        if (l.EndsWith("_4k_d") || l.Contains("4k") || l.Contains("normal") || l.Contains("basecolor") || l.Contains("_base")) return "named_maps";
        if (l.Contains("metahuman") || l.Contains("head") || l.Contains("face")) return "head";
        if (l.Contains("rt_") || l.Contains("camera") || l.Contains("shadow") || l.Contains("hdrp")) return "rt";
        if (l.Contains("joan") || l.Contains("pitborn") || l.Contains("sobek") || l.Contains("grendel") || l.Contains("humbaba") || l.Contains("erlking")) return "character";
        if (l.Contains("image") || l.Contains("texture_") || l.Contains("gltf") || l.Contains("basecolor")) return "gltf";
        if (l.Contains("vfx") || l.Contains("fx") || l.Contains("smoke") || l.Contains("flame")) return "vfx";
        if (l.Contains("ui") || l.Contains("font") || l.Contains("sprite")) return "ui";
        return "other";
    }
}
