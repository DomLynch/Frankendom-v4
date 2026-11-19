using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

// ZONEDECODE v1 (fk_loadscope_kit, COO order 18 Nov: the late-tier GLB decodes are the arrival hitches). NOT APPLIED / NOT RUN: Roslyn only. Default OFF (zone_scope.json decode.mode "off").
// Problem (MEASURED, README 5): glTFast decodes an EMBEDDED GLB image with Texture2D.LoadImage on the main thread: one 4096x4096 PNG/JPG = one 100-340 ms frame (14 of them in the late tier). That call cannot be moved to a worker.
// Fix = make the images NOT embedded: the GLB's JSON chunk is rewritten in memory on a worker thread so every big image (>= decode.minPixels) has a uri ("zsimg_N.png") instead of a bufferView, the image bytes are
// written once to <temporaryCachePath>/<decode.cacheDir>/ and glTFast is given an IDownloadProvider that answers RequestTexture for those uris by delegating to glTFast's own DefaultDownloadProvider with the cache file
// (UnityWebRequestTexture: Unity decodes the file on a worker thread; the main thread only creates / uploads the texture). Same decoders as LoadImage = same pixels; same glTFast material / sampler / linear logic.
// Guards (data, reuse the hitch keys): a request starts only when imageSpacingFrames passed since the last one, the previous frame was < hitch.calmFrameMs, no fight (hitch.holdWhileEngaged), each wait capped by hitch.maxWaitS;
// decode.maxParallel requests in flight. Failure of any kind (rewrite, file write, request, texture) = the old path for that image / file (LoadImage on the main thread, or GltfImport.LoadFile), never a missing texture.
// Look-neutral by construction EXCEPT two things the pair must check (UNMEASURED): (1) mip chain: textures today have 1 mip (4096x4096 ARGB32 = 64 MB); if UnityWebRequestTexture returns a chain and decode.stripMips is true the mip 0 is
// GPU-copied into a 1-mip texture of the same graphics format (transient memory), (2) readability: decode.nonReadable false (default) keeps today's behaviour; true drops the CPU copy (saves memory, breaks any GetPixels on gear textures).
// No MonoBehaviour, no serialized field, no RuntimeInitializeOnLoadMethod (called from GlbPreload.LoadAsync). Log lines: "[ZoneDecode] <file>: n images externalised ... ms", "[ZoneDecode] img <name> WxH <fmt> mips A->B ... ms", "[ZoneDecode] FALLBACK ...".
public static class ZoneDecode
{
    static int lastReqFrame = -100000, inFlight; static long cachedBytes, imgCount; static double workerMs;

    public static bool Wants(string file) { return ZoneScope.DecodeWants(file); }

    /// <summary>Parse + import this GLB with its big images decoded by Unity's worker thread. Null = could not (caller uses the old LoadFile path).</summary>
    public static async Task<GLTFast.GltfImport> Load(string file, GLTFast.IDeferAgent da)
    {
        var cfg = ZoneScope.Cfg; if (cfg == null) return null; var d = cfg.decode; GLTFast.GltfImport g = null;
        try
        {
            float t0 = Time.realtimeSinceStartup; var images = new Dictionary<string, byte[]>(); int minPx = d.minPixels; string fp = file;
            byte[] glb2 = await Task.Run(() => { byte[] raw = File.ReadAllBytes(fp); return Rewrite(raw, minPx, images); });
            if (glb2 == null || images.Count == 0) return null;   // nothing big to externalise: the old path is as good
            string dir = Path.Combine(Application.temporaryCachePath, d.cacheDir); Directory.CreateDirectory(dir);
            var prov = new Prov(images, dir, Hash(file), d, cfg.hitch);
            g = new GLTFast.GltfImport(downloadProvider: prov, deferAgent: da);
            bool ok = await g.LoadGltfBinary(glb2, new Uri(file));
            if (!ok) { g.Dispose(); Debug.LogWarning("[ZoneDecode] FALLBACK " + Path.GetFileName(file) + ": glTFast could not import the rewritten GLB, old path"); return null; }
            if (cfg.log) Debug.Log("[ZoneDecode] " + Path.GetFileName(file) + ": " + images.Count + " images externalised (" + (Sum(images) / 1048576f).ToString("0.0") + " MB of bytes), " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms wall, cache writes " + prov.Writes + ", fallbacks " + prov.Fallbacks + ", mips stripped " + prov.Stripped);
            return g;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ZoneDecode] FALLBACK " + Path.GetFileName(file) + ": " + e.Message); try { if (g != null) g.Dispose(); } catch (Exception) { }
            return null;
        }
    }

    static long Sum(Dictionary<string, byte[]> m) { long s = 0; foreach (var kv in m) s += kv.Value.Length; return s; }
    static string Hash(string s) { unchecked { uint h = 2166136261; foreach (char c in s.ToLowerInvariant().Replace('\\', '/')) { h ^= c; h *= 16777619; } return h.ToString("x8"); } }

    // ------------------------------------------------------------------ the gate (main thread)
    static async Task Gate(ZscCfg c)
    {
        float t0 = Time.realtimeSinceStartup;
        while (true)
        {
            bool wait = Time.frameCount - lastReqFrame < Mathf.Max(1, c.decode.imageSpacingFrames) || inFlight >= Mathf.Max(1, c.decode.maxParallel)
                || Time.unscaledDeltaTime * 1000f > c.hitch.calmFrameMs || (c.hitch.holdWhileEngaged && ZoneScope.Engaged());
            if (!wait || Time.realtimeSinceStartup - t0 > c.hitch.maxWaitS) break;
            await Task.Yield();
        }
        lastReqFrame = Time.frameCount; inFlight++;
    }

    // ------------------------------------------------------------------ the download provider glTFast asks for the externalised images
    sealed class Prov : GLTFast.Loading.IDownloadProvider
    {
        readonly Dictionary<string, byte[]> imgs; readonly string dir, tag; readonly ZscDecode d; readonly ZscHitch h; readonly GLTFast.Loading.DefaultDownloadProvider inner = new GLTFast.Loading.DefaultDownloadProvider();
        public int Writes, Fallbacks, Stripped;
        public Prov(Dictionary<string, byte[]> i, string dir_, string tag_, ZscDecode d_, ZscHitch h_) { imgs = i; dir = dir_; tag = tag_; d = d_; h = h_; }

        public Task<GLTFast.Loading.IDownload> Request(Uri url) { return inner.Request(url); }

        public async Task<GLTFast.Loading.ITextureDownload> RequestTexture(Uri url, bool nonReadable)
        {
            string name = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath)); byte[] bytes;
            if (!imgs.TryGetValue(name, out bytes)) return await inner.RequestTexture(url, nonReadable);   // not ours
            var cfg = ZoneScope.Cfg; string path = Path.Combine(dir, tag + "_" + name); float t0 = Time.realtimeSinceStartup;
            await Gate(cfg);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) { await Task.Run(() => File.WriteAllBytes(path, bytes)); Writes++; cachedBytes += bytes.Length; }
                var dl = await inner.RequestTexture(new Uri(path), nonReadable || d.nonReadable);
                var tex = dl != null && dl.Success ? dl.Texture : null;
                if (tex == null) { Fallbacks++; if (dl != null) dl.Dispose(); return Fallback(bytes, name, nonReadable || d.nonReadable, "request failed"); }
                int mips = tex.mipmapCount;
                if (d.stripMips && mips > 1)
                {
                    var one = new Texture2D(tex.width, tex.height, tex.graphicsFormat, 1, UnityEngine.Experimental.Rendering.TextureCreationFlags.None) { name = tex.name, wrapMode = tex.wrapMode, filterMode = tex.filterMode, anisoLevel = tex.anisoLevel };
                    Graphics.CopyTexture(tex, 0, 0, one, 0, 0); UnityEngine.Object.Destroy(tex); tex = one; Stripped++;
                }
                tex = DownsizeLoad.Reduce(tex, nonReadable || d.nonReadable); imgCount++;   /* DOWNSIZELOAD v1 */
                if (d.logImages && ZoneScope.Cfg.log) Debug.Log("[ZoneDecode] img " + tag + "/" + name + " " + tex.width + "x" + tex.height + " " + tex.format + " mips " + mips + "->" + tex.mipmapCount + " readable " + tex.isReadable + " " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms request-to-ready, frame " + Time.frameCount);
                return new Dl(dl, tex);
            }
            catch (Exception e) { Fallbacks++; return Fallback(bytes, name, nonReadable || d.nonReadable, e.Message); }
            finally { inFlight = Math.Max(0, inFlight - 1); }
        }

        // the old path for this one image: LoadImage on the main thread (the hitch we wanted to avoid, but never a missing texture)
        GLTFast.Loading.ITextureDownload Fallback(byte[] bytes, string name, bool nonReadable, string why)
        {
            Debug.LogWarning("[ZoneDecode] FALLBACK img " + name + ": " + why + " (LoadImage on the main thread)");
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false); ImageConversion.LoadImage(t, bytes, nonReadable); return new Dl(null, t);
        }
    }

    sealed class Dl : GLTFast.Loading.ITextureDownload
    {
        readonly GLTFast.Loading.ITextureDownload inner; readonly Texture2D tex;
        public Dl(GLTFast.Loading.ITextureDownload i, Texture2D t) { inner = i; tex = t; }
        public Texture2D Texture { get { return tex; } }
        public bool Success { get { return tex != null; } }
        public string Error { get { return tex != null ? null : "no texture"; } }
        public byte[] Data { get { return null; } }
        public string Text { get { return null; } }
        public bool? IsBinary { get { return true; } }
        public void Dispose() { try { if (inner != null) inner.Dispose(); } catch (Exception) { } }
    }

    // ------------------------------------------------------------------ GLB rewrite (worker thread: no Unity API below this line)
    const uint MagicGlb = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;

    /// <summary>Returns a GLB whose images of >= minPx pixels carry uri "zsimg_N.ext" instead of a bufferView (their bytes go to <paramref name="outImgs"/>), or null when nothing qualifies / the file is not a plain GLB 2.0.</summary>
    internal static byte[] Rewrite(byte[] glb, int minPx, Dictionary<string, byte[]> outImgs)
    {
        if (glb == null || glb.Length < 28 || BitConverter.ToUInt32(glb, 0) != MagicGlb || BitConverter.ToUInt32(glb, 4) != 2u) return null;
        int jl = (int)BitConverter.ToUInt32(glb, 12); if (BitConverter.ToUInt32(glb, 16) != ChunkJson || 20 + jl + 8 > glb.Length) return null;
        int bp = 20 + jl; int binLen = (int)BitConverter.ToUInt32(glb, bp); if (BitConverter.ToUInt32(glb, bp + 4) != ChunkBin || bp + 8 + binLen > glb.Length) return null; int binStart = bp + 8;
        string json = Encoding.UTF8.GetString(glb, 20, jl);
        int io, ic, vo, vc; if (!FindArray(json, "images", out io, out ic) || !FindArray(json, "bufferViews", out vo, out vc)) return null;
        var views = Objects(json, vo, vc); var imgs = Objects(json, io, ic);
        var edits = new List<int[]>(); var texts = new List<string>(); int k = 0;
        for (int i = 0; i < imgs.Count; i++)
        {
            string o = json.Substring(imgs[i][0], imgs[i][1] - imgs[i][0]);
            var mb = Regex.Match(o, "\"bufferView\"\\s*:\\s*(\\d+)"); if (!mb.Success) continue;
            int vi = int.Parse(mb.Groups[1].Value); if (vi < 0 || vi >= views.Count) continue; string v = json.Substring(views[vi][0], views[vi][1] - views[vi][0]);
            var mo = Regex.Match(v, "\"byteOffset\"\\s*:\\s*(\\d+)"); var ml = Regex.Match(v, "\"byteLength\"\\s*:\\s*(\\d+)"); if (!ml.Success) continue;
            int off = mo.Success ? int.Parse(mo.Groups[1].Value) : 0, len = int.Parse(ml.Groups[1].Value); if (off < 0 || len <= 0 || off + len > binLen) continue;
            int w, h; string ext = Dims(glb, binStart + off, len, out w, out h); if (ext == null || (long)w * h < minPx) continue;
            byte[] data = new byte[len]; Buffer.BlockCopy(glb, binStart + off, data, 0, len);
            string name = "zsimg_" + (k++) + "." + ext; outImgs[name] = data;
            edits.Add(new[] { imgs[i][0], imgs[i][1] }); texts.Add(Regex.Replace(o, "\"bufferView\"\\s*:\\s*\\d+", "\"uri\":\"" + name + "\"", RegexOptions.None));
        }
        if (k == 0) return null;
        var sb = new StringBuilder(json.Length + 64); int pos = 0;
        for (int e = 0; e < edits.Count; e++) { sb.Append(json, pos, edits[e][0] - pos).Append(texts[e]); pos = edits[e][1]; }
        sb.Append(json, pos, json.Length - pos);
        byte[] jb = Encoding.UTF8.GetBytes(sb.ToString()); int jpad = (4 - jb.Length % 4) % 4; int jlen = jb.Length + jpad;
        int total = 12 + 8 + jlen + 8 + binLen; byte[] r = new byte[total];
        Buffer.BlockCopy(BitConverter.GetBytes(MagicGlb), 0, r, 0, 4); Buffer.BlockCopy(BitConverter.GetBytes(2u), 0, r, 4, 4); Buffer.BlockCopy(BitConverter.GetBytes((uint)total), 0, r, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((uint)jlen), 0, r, 12, 4); Buffer.BlockCopy(BitConverter.GetBytes(ChunkJson), 0, r, 16, 4); Buffer.BlockCopy(jb, 0, r, 20, jb.Length);
        for (int i = 0; i < jpad; i++) r[20 + jb.Length + i] = 0x20;
        int bo = 20 + jlen; Buffer.BlockCopy(BitConverter.GetBytes((uint)binLen), 0, r, bo, 4); Buffer.BlockCopy(BitConverter.GetBytes(ChunkBin), 0, r, bo + 4, 4); Buffer.BlockCopy(glb, binStart, r, bo + 8, binLen);
        return r;
    }

    /// <summary>"png" / "jpg" and the pixel size read from the header bytes, or null (unknown format: left embedded).</summary>
    static string Dims(byte[] b, int o, int len, out int w, out int h)
    {
        w = h = 0;
        if (len > 24 && b[o] == 0x89 && b[o + 1] == 0x50 && b[o + 2] == 0x4E && b[o + 3] == 0x47)
        {
            if (b[o + 12] != 'I' || b[o + 13] != 'H' || b[o + 14] != 'D' || b[o + 15] != 'R') return null;
            w = (b[o + 16] << 24) | (b[o + 17] << 16) | (b[o + 18] << 8) | b[o + 19]; h = (b[o + 20] << 24) | (b[o + 21] << 16) | (b[o + 22] << 8) | b[o + 23];
            return w > 0 && h > 0 ? "png" : null;
        }
        if (len > 4 && b[o] == 0xFF && b[o + 1] == 0xD8)
        {
            int i = o + 2, end = o + len;
            while (i + 9 < end)
            {
                if (b[i] != 0xFF) { i++; continue; }
                int m = b[i + 1]; if (m == 0xFF) { i++; continue; }
                if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) { h = (b[i + 5] << 8) | b[i + 6]; w = (b[i + 7] << 8) | b[i + 8]; return w > 0 && h > 0 ? "jpg" : null; }
                if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01) { i += 2; continue; }
                i += 2 + ((b[i + 2] << 8) | b[i + 3]);
            }
        }
        return null;
    }

    static bool FindArray(string j, string key, out int open, out int close)
    {
        open = close = -1; int depth = 0;
        for (int i = 0; i < j.Length; i++)
        {
            char c = j[i];
            if (c == '"')
            {
                int s = ++i; while (i < j.Length && j[i] != '"') { if (j[i] == '\\') i++; i++; }
                if (depth == 1 && i - s == key.Length && string.CompareOrdinal(j, s, key, 0, key.Length) == 0)
                {
                    int q = i + 1; while (q < j.Length && char.IsWhiteSpace(j[q])) q++;
                    if (q < j.Length && j[q] == ':') { q++; while (q < j.Length && char.IsWhiteSpace(j[q])) q++; if (q < j.Length && j[q] == '[') { open = q; close = MatchClose(j, q); return close > q; } }
                }
            }
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') depth--;
        }
        return false;
    }

    static int MatchClose(string j, int open)
    {
        int depth = 0;
        for (int i = open; i < j.Length; i++)
        {
            char c = j[i];
            if (c == '"') { i++; while (i < j.Length && j[i] != '"') { if (j[i] == '\\') i++; i++; } }
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>[start, end) of every top-level {object} inside the array j[open..close].</summary>
    static List<int[]> Objects(string j, int open, int close)
    {
        var r = new List<int[]>();
        for (int i = open + 1; i < close; i++)
        {
            if (j[i] == '{') { int e = MatchClose(j, i); if (e < 0) break; r.Add(new[] { i, e + 1 }); i = e; }
        }
        return r;
    }
}
