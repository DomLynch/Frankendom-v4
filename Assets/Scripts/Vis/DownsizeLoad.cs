using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// DOWNSIZELOAD v1 (kit fk_downsize_load_kit, 18 Nov). Default OFF (downsize_load.json enabled false). Compile-only: no MonoBehaviour, no serialized field, no RuntimeInitializeOnLoadMethod, no Burst attribute (jobs run on Mono JIT).
// Problem (MEASURED by Strategy on the RTX 3060): PerfMem.GlbTick shrinks runtime glb textures AFTER glTFast uploaded them at full size (4096x4096 ARGB32 = 64 MB, RGB24 = 48 MB, 1 mip), a few per second ("152 textures, saved -0.15 GB" after 94 s),
// so the VRAM peak is already paid. Here the image is created at the reduced size and the full-size texture never survives the call that decoded it.
// Where it hooks (nothing duplicated): the glTFast import addon that TexDedupe already registers (TexDedupe.Claims / TexDedupe.Load, 2 anchored edits) and, for ZoneDecode's externalised images, Prov.RequestTexture (1 line).
//  Path A (embedded PNG/JPEG, glTFast calls ITextureImageLoader): Decode(): temp Texture2D + LoadImage (glTFast's own creation flags) -> k halving blits on the GPU (bilinear at exact half scale = a true 2x2 box filter per step,
//   in linear light for sRGB textures because the RT is sRGB) -> ReadPixels into the FINAL texture created at (w>>k, h>>k) -> DestroyImmediate(temp). Synchronous: no await between create and destroy, so at most ONE full-size image is resident (counter below).
//  Path B (ZoneDecode externalised image, decoded on Unity's worker thread, arrives as a full-size texture): Reduce() = the same chain, then DestroyImmediate of the arrival.
//  CPU fallback (method "cpu", or no GPU): temp readable + IJobParallelFor box average of 2^k x 2^k blocks (sRGB-space average on plain bytes) written into a same-format texture. Any other failure: the full texture is returned as glTFast would have made it (old behaviour, a warning line).
// Normal maps (linear, mean R,G about 128 and B high): the box average shortens vectors; renormalizeNormals re-normalises the reduced result (parallel job). Alpha is averaged like colour (no premultiplication: the usual mip behaviour).
// Optional compress (data, default false): Texture2D.Compress(false) on the REDUCED texture (CPU DXT1/DXT5, 4-6x fewer bytes than RGBA32/RGB24), only when w*h <= compressMaxPixels (cost scales with pixels), never for detected normal maps.
// Preset coupling: k = PerfMem.GlbMipDrop (memory_tiers.json glbMipDrop of the active preset: Low 2, Medium 1, High 0 = this class does nothing), or dropOverride >= 0 in the data row, or -dlmip:N. Switch: -downsizeload:on / -downsizeload:off.
// Hooks: TexDedupe.Claims / TexDedupe.Load (decode), ZoneDecode.Prov.RequestTexture (download), PerfMem.GlbTick (Tick() for the log line + IsReduced() so the post-upload pass never reduces an image twice).
public static class DownsizeLoad
{
    [Serializable] class Cfg
    {
        public bool enabled = false; public string[] presets = new string[0]; public int dropOverride = -1; public int minSize = 2048; public int minOut = 256; public string method = "gpu";
        public bool renormalizeNormals = true; public bool compress = false; public int compressMaxPixels = 1048576; public bool compressNormals = false; public bool log = true; public int logFirst = 12; public float tickS = 10f;
    }
    static Cfg cfg; static bool loaded;
    sealed class RefCmp : System.Collections.Generic.IEqualityComparer<Texture2D> { public bool Equals(Texture2D a, Texture2D b) { return object.ReferenceEquals(a, b); } public int GetHashCode(Texture2D o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); } }
    static readonly System.Collections.Generic.HashSet<Texture2D> reduced = new System.Collections.Generic.HashSet<Texture2D>(new RefCmp());
    static int nTex, lastK, residentFull, peakFull, fallbacks, normalsFixed, compressedN, errors; static long fullB, outB; static double msSum, msMax; static float nextLog; static int lastSig = -1; static bool announced;

    static void LoadCfg()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            foreach (var p in DataPath.Candidates("downsize_load.json"))
            {
                if (!System.IO.File.Exists(p)) continue;
                cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); break;
            }
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (a == "-downsizeload:on") { if (cfg == null) cfg = new Cfg(); cfg.enabled = true; }
                else if (a == "-downsizeload:off" && cfg != null) cfg.enabled = false;
                else if (a.StartsWith("-dlmip:")) { int v; if (cfg == null) cfg = new Cfg(); if (int.TryParse(a.Substring(7), out v)) cfg.dropOverride = v; }
            }
        }
        catch (Exception e) { Debug.LogWarning("[DownsizeLoad] cfg: " + e.Message); }
        Debug.Log("[DownsizeLoad] " + (cfg != null && cfg.enabled ? "on method=" + cfg.method + " minSize=" + cfg.minSize + " minOut=" + cfg.minOut + " dropOverride=" + cfg.dropOverride + " compress=" + cfg.compress + " renormalizeNormals=" + cfg.renormalizeNormals : "off (data)"));
    }

    /// <summary>The drop k (output = size >> k per axis) for the active preset, 0 = nothing happens.</summary>
    static int Drop()
    {
        LoadCfg();
        if (cfg == null || !cfg.enabled) return 0;
        if (cfg.presets != null && cfg.presets.Length > 0)
        {
            bool ok = false; string n = PerfMem.MemTierName; foreach (var p in cfg.presets) if (string.Equals(p, n, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            if (!ok) return 0;
        }
        return Mathf.Max(0, cfg.dropOverride >= 0 ? cfg.dropOverride : PerfMem.GlbMipDrop);
    }

    /// <summary>True while the switch is on and the preset has a drop (Low / Medium). Cheap.</summary>
    public static bool Active { get { return Drop() > 0; } }

    /// <summary>True for a texture made here (PerfMem.GlbTick must not downsize it a second time: Medium makes 2048 which is still >= glb.minSize).</summary>
    public static bool IsReduced(UnityEngine.Texture t) { var t2 = t as Texture2D; return !object.ReferenceEquals(t2, null) && reduced.Contains(t2); }

    static int EffDrop(int w, int h)
    {
        int k = Drop(); int m = Mathf.Min(w, h);
        if (k <= 0 || m < cfg.minSize) return 0;
        while (k > 0 && (m >> k) < Mathf.Max(4, cfg.minOut)) k--;
        return k;
    }

    // header only: PNG IHDR or JPEG SOF. false = unknown (left to glTFast).
    static bool Dims(ReadOnlySpan<byte> b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length > 33 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return false;
            w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19]; h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0;
        }
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                int m = b[i + 1]; if (m == 0xFF) { i++; continue; }
                if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) { h = (b[i + 5] << 8) | b[i + 6]; w = (b[i + 7] << 8) | b[i + 8]; return w > 0 && h > 0; }
                if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { i += 2; continue; }
                i += 2 + ((b[i + 2] << 8) | b[i + 3]);
            }
        }
        return false;
    }

    /// <summary>TexDedupe.Claims hook: true when this encoded PNG/JPEG will be decoded at reduced size.</summary>
    internal static bool Wants(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8 || Drop() <= 0) return false;
        int w, h; if (!Dims(d, out w, out h)) return false;
        return EffDrop(w, h) > 0;
    }

    /// <summary>TexDedupe.Load hook. Returns the reduced texture (same colour space, mips flag and readability as glTFast's), or null = the caller decodes at full size as before.</summary>
    internal static Texture2D Decode(ReadOnlySpan<byte> span, bool linear, bool readable, bool mips)
    {
        int w, h; if (Drop() <= 0 || !Dims(span, out w, out h)) return null;
        int k = EffDrop(w, h); if (k <= 0) return null;
        float t0 = Time.realtimeSinceStartup; Texture2D full = null;
        try
        {
            var flags = TextureCreationFlags.DontUploadUponCreate | TextureCreationFlags.DontInitializePixels;
            bool cpu = string.Equals(cfg.method, "cpu", StringComparison.OrdinalIgnoreCase) || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
            full = new Texture2D(4, 4, linear ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R8G8B8A8_SRGB, flags);
            residentFull++; if (residentFull > peakFull) peakFull = residentFull;
            full.LoadImage(span, cpu ? false : !readable);   // GPU path: the CPU copy is dropped at once (unless the importer wants a readable image); the CPU path needs the pixels
            k = EffDrop(full.width, full.height);
            if (k <= 0) { residentFull--; Finish(full, readable); return full; }   // header lied: give glTFast what it would have made
            var res = cpu ? ReduceCpu(full, linear, readable, mips, k) : ReduceGpu(full, linear, readable, mips, k);
            if (res == null) { fallbacks++; residentFull--; Finish(full, readable); return full; }
            Account(full, res, k, (Time.realtimeSinceStartup - t0) * 1000.0);
            UnityEngine.Object.DestroyImmediate(full); residentFull--;
            return res;
        }
        catch (Exception e)
        {
            errors++; Debug.LogWarning("[DownsizeLoad] decode failed, full-size path: " + e.Message);
            if (full != null) { residentFull = Mathf.Max(0, residentFull - 1); try { Finish(full, readable); return full; } catch (Exception) { } }
            return null;
        }
    }

    // a CPU-path texture still holds its CPU copy: drop it unless the importer asked for a readable image
    static void Finish(Texture2D t, bool readable) { if (!readable && t.isReadable) t.Apply(false, true); }

    /// <summary>ZoneDecode.Prov hook (path B): the worker-thread decode delivered a full-size texture. Returns the reduced texture and destroys the arrival, or returns the arrival untouched.</summary>
    internal static Texture2D Reduce(Texture2D full, bool nonReadable)
    {
        if (object.ReferenceEquals(full, null) || full == null || Drop() <= 0) return full;
        int k = EffDrop(full.width, full.height); if (k <= 0) return full;
        float t0 = Time.realtimeSinceStartup;
        try
        {
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(full.graphicsFormat);
            residentFull++; if (residentFull > peakFull) peakFull = residentFull;
            var res = ReduceGpu(full, linear, !nonReadable, full.mipmapCount > 1, k);
            if (res == null) { fallbacks++; residentFull--; return full; }
            Account(full, res, k, (Time.realtimeSinceStartup - t0) * 1000.0);
            UnityEngine.Object.DestroyImmediate(full); residentFull--;
            return res;
        }
        catch (Exception e) { errors++; residentFull = Mathf.Max(0, residentFull - 1); Debug.LogWarning("[DownsizeLoad] reduce failed, arrival kept: " + e.Message); return full; }
    }

    static void Account(Texture2D full, Texture2D res, int k, double ms)
    {
        nTex++; lastK = k; reduced.Add(res); msSum += ms; if (ms > msMax) msMax = ms;
        long fb = BytesOf(full.format, full.width, full.height), ob = BytesOf(res.format, res.width, res.height);
        if (res.mipmapCount > 1) ob = ob * 4 / 3;
        fullB += fb; outB += ob;
        if (cfg.log && nTex <= cfg.logFirst) Debug.Log("[DownsizeLoad] " + full.name + " " + full.width + "x" + full.height + " " + full.format + " -> " + res.width + "x" + res.height + " " + res.format + " k=" + k + " " + (fb / 1048576.0).ToString("F1") + " -> " + (ob / 1048576.0).ToString("F1") + " MB, " + ms.ToString("F0") + " ms (decode + reduce)");
    }

    static long BytesOf(TextureFormat f, int w, int h)
    {
        switch (f)
        {
            case TextureFormat.RGB24: return (long)w * h * 3;
            case TextureFormat.DXT1: return (long)((w + 3) / 4) * ((h + 3) / 4) * 8;
            case TextureFormat.DXT5: case TextureFormat.BC7: return (long)((w + 3) / 4) * ((h + 3) / 4) * 16;
            default: return (long)w * h * 4;
        }
    }

    // channel byte offsets for the formats LoadImage / ReadPixels produce
    static bool Offs(TextureFormat f, out int bpp, out int r, out int g, out int b, out int a)
    {
        switch (f)
        {
            case TextureFormat.RGB24: bpp = 3; r = 0; g = 1; b = 2; a = -1; return true;
            case TextureFormat.RGBA32: bpp = 4; r = 0; g = 1; b = 2; a = 3; return true;
            case TextureFormat.ARGB32: bpp = 4; r = 1; g = 2; b = 3; a = 0; return true;
            case TextureFormat.BGRA32: bpp = 4; r = 2; g = 1; b = 0; a = 3; return true;
            default: bpp = r = g = b = a = 0; return false;
        }
    }

    // ---------------------------------------------------------------- GPU path
    static Texture2D ReduceGpu(Texture2D full, bool linear, bool readable, bool mips, int k)
    {
        var prev = RenderTexture.active; RenderTexture cur = null; Texture2D res = null;
        try
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            var rw = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            int w = full.width, h = full.height; Texture src = full;
            for (int i = 0; i < k; i++)
            {
                w = Mathf.Max(1, w >> 1); h = Mathf.Max(1, h >> 1);
                var nxt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, rw); nxt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, nxt);
                if (cur != null) RenderTexture.ReleaseTemporary(cur);
                cur = nxt; src = nxt;
            }
            bool rgb = full.format == TextureFormat.RGB24;
            res = new Texture2D(w, h, rgb ? TextureFormat.RGB24 : TextureFormat.RGBA32, mips, linear) { name = full.name, wrapMode = full.wrapMode, filterMode = full.filterMode, anisoLevel = full.anisoLevel };
            RenderTexture.active = cur;
            res.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            Post(res, linear, readable, mips);
            return res;
        }
        catch (Exception e) { errors++; Debug.LogWarning("[DownsizeLoad] gpu reduce " + full.name + ": " + e.Message); if (res != null) UnityEngine.Object.DestroyImmediate(res); return null; }
        finally { RenderTexture.active = prev; if (cur != null) RenderTexture.ReleaseTemporary(cur); }
    }

    // normal-map renormalise, optional compress, final Apply (mips generated from the reduced level 0)
    static void Post(Texture2D res, bool linear, bool readable, bool mips)
    {
        int bpp, ro, go, bo, ao;
        bool known = Offs(res.format, out bpp, out ro, out go, out bo, out ao);
        bool isNormal = false;
        if (known && linear && cfg.renormalizeNormals)
        {
            var d = res.GetRawTextureData<byte>();
            isNormal = LooksLikeNormal(d, res.width, res.height, bpp, ro, go, bo);
            if (isNormal) { new RenormJob { d = d, w = res.width, bpp = bpp, ro = ro, go = go, bo = bo }.Schedule(res.height, 16).Complete(); normalsFixed++; }
        }
        bool comp = cfg.compress && known && (res.format == TextureFormat.RGB24 || res.format == TextureFormat.RGBA32) && (long)res.width * res.height <= cfg.compressMaxPixels && (res.width % 4) == 0 && (res.height % 4) == 0 && (!isNormal || cfg.compressNormals);
        if (comp) { res.Apply(mips, false); res.Compress(false); compressedN++; res.Apply(false, !readable); }
        else res.Apply(mips, !readable);
    }

    static bool LooksLikeNormal(NativeArray<byte> d, int w, int h, int bpp, int ro, int go, int bo)
    {
        long n = (long)w * h; int step = (int)Mathf.Max(1, n / 4096); long sr = 0, sg = 0, sb = 0; int c = 0;
        for (long p = 0; p < n; p += step) { long o = p * bpp; sr += d[(int)(o + ro)]; sg += d[(int)(o + go)]; sb += d[(int)(o + bo)]; c++; }
        if (c == 0) return false;
        float mr = sr / (float)c, mg = sg / (float)c, mb = sb / (float)c;
        return Mathf.Abs(mr - 128f) < 28f && Mathf.Abs(mg - 128f) < 28f && mb > 200f;
    }

    struct RenormJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<byte> d; public int w, bpp, ro, go, bo;
        public void Execute(int y)
        {
            int row = y * w * bpp;
            for (int x = 0; x < w; x++)
            {
                int o = row + x * bpp; float nx = (d[o + ro] - 127.5f) / 127.5f, ny = (d[o + go] - 127.5f) / 127.5f, nz = (d[o + bo] - 127.5f) / 127.5f;
                float l = Mathf.Sqrt(nx * nx + ny * ny + nz * nz); if (l < 1e-4f) continue; l = 1f / l;
                d[o + ro] = (byte)Mathf.Clamp(Mathf.RoundToInt((nx * l * 0.5f + 0.5f) * 255f), 0, 255);
                d[o + go] = (byte)Mathf.Clamp(Mathf.RoundToInt((ny * l * 0.5f + 0.5f) * 255f), 0, 255);
                d[o + bo] = (byte)Mathf.Clamp(Mathf.RoundToInt((nz * l * 0.5f + 0.5f) * 255f), 0, 255);
            }
        }
    }

    // ---------------------------------------------------------------- CPU path (fallback): box average of 2^k x 2^k blocks, same format as the decode produced
    static Texture2D ReduceCpu(Texture2D full, bool linear, bool readable, bool mips, int k)
    {
        Texture2D res = null;
        try
        {
            int bpp, ro, go, bo, ao; if (!full.isReadable || !Offs(full.format, out bpp, out ro, out go, out bo, out ao)) return null;
            int ow = full.width >> k, oh = full.height >> k;
            res = new Texture2D(ow, oh, full.format, mips, linear) { name = full.name, wrapMode = full.wrapMode, filterMode = full.filterMode, anisoLevel = full.anisoLevel };
            var src = full.GetRawTextureData<byte>(); var dst = res.GetRawTextureData<byte>();
            new BoxJob { src = src, dst = dst, sw = full.width, ow = ow, bpp = bpp, k = k }.Schedule(oh, 8).Complete();
            Post(res, linear, readable, mips);
            return res;
        }
        catch (Exception e) { errors++; Debug.LogWarning("[DownsizeLoad] cpu reduce " + full.name + ": " + e.Message); if (res != null) UnityEngine.Object.DestroyImmediate(res); return null; }
    }

    struct BoxJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> src; [NativeDisableParallelForRestriction] public NativeArray<byte> dst; public int sw, ow, bpp, k;
        public void Execute(int oy)
        {
            int n = 1 << k, cnt = n * n, half = cnt / 2;
            for (int ox = 0; ox < ow; ox++)
                for (int c = 0; c < bpp; c++)
                {
                    int s = 0;
                    for (int j = 0; j < n; j++) { int row = ((oy << k) + j) * sw; for (int i = 0; i < n; i++) s += src[(row + (ox << k) + i) * bpp + c]; }
                    dst[(oy * ow + ox) * bpp + c] = (byte)((s + half) / cnt);
                }
        }
    }

    // ---------------------------------------------------------------- log (PerfMem.GlbTick calls this every frame; work only every tickS and only when something changed)
    public static void Tick()
    {
        if (cfg == null || !cfg.enabled || !cfg.log) return;
        float now = Time.realtimeSinceStartup; if (now < nextLog) return;
        nextLog = now + Mathf.Max(1f, cfg.tickS);
        if (!announced && Drop() > 0) { announced = true; Debug.Log("[DownsizeLoad] active preset=" + PerfMem.MemTierName + " k=" + Drop() + " (glb images >= " + cfg.minSize + " px are created at 1/2^k per axis, area 1/4^k)"); }
        int sig = nTex * 1000003 + fallbacks * 1009 + errors; if (sig == lastSig) return; lastSig = sig;
        Debug.Log("[DownsizeLoad] " + nTex + " textures decoded at 1/2^" + lastK + ", " + ((fullB - outB) / 1073741824.0).ToString("F2") + " GB not uploaded (peak resident full-size = " + peakFull + ")"
            + " | full " + (fullB / 1073741824.0).ToString("F2") + " GB -> out " + (outB / 1073741824.0).ToString("F2") + " GB, avg " + (nTex > 0 ? msSum / nTex : 0).ToString("F0") + " ms max " + msMax.ToString("F0") + " ms per image, normalsRenormalized=" + normalsFixed + " compressed=" + compressedN + " fallbacks=" + fallbacks + " errors=" + errors + " preset=" + PerfMem.MemTierName);
    }
}
