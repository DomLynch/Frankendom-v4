using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Addons;
using GLTFast.Schema;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// TEXDEDUPE v2 (kit fk_texdedupe_kit, 18 Nov). ALL CODE IS IN Assembly-CSharp: no glTFast package edit (glTFast.asmdef is its own assembly, a compile-only take does not carry it). Only PUBLIC glTFast API is used.
// Idea: glTFast decodes every runtime glb image into its own Texture2D (64 MB for a 4K ARGB32). The bytes of the same image in two glbs (or one glb loaded twice) are identical. v2 does not hand an importer a shared object
// (its Dispose would destroy it). Instead:
//  1. IDENTITY at decode: an ITextureImageLoader import addon (public API) sees the encoded PNG/JPEG bytes, decodes them EXACTLY as glTFast does (same flags, same LoadImage) and tags the new Texture2D with a content key
//     (128-bit hash + byte length + sRGB/linear + mip flag). The importer owns that texture as before; nothing is shared yet.
//  2. SETTLE: a texture counts as settled when some Material references it and that has been true for settleS seconds (the import bound it and is not mid-instantiate).
//  3. COLLAPSE (from MemLog.Update, an existing tick): for a key with 2+ settled textures (or one when a canonical already exists) the canonical copy C is made ONCE: new Texture2D with the SAME TextureFormat / mip count /
//     colour space as the source (no format remap), Apply(false,true) first (uploads, drops the CPU buffer: non-readable like glTFast's, no zero CPU twin), then Graphics.CopyTexture(source, C). C is verified on the GPU
//     (256x256 blit of both, byte compare) before anything is swapped; a mismatch aborts the key and nothing changes. Then EVERY Material in the process that holds one of the duplicates is re-pointed at C (one scan,
//     covers material clones made later by gear / NPC code), and only then are the importer's duplicates destroyed. C is owned here, never by a GltfImport, so GltfImport.Dispose cannot touch it; it is destroyed when no
//     Material references it any more (idle for idleS).
// Switch: texture_dedupe.json (enabled false = the addon claims nothing, Tick returns at once, glTFast runs its own path bit for bit). -texdedupe:on / -texdedupe:off. tiers [] = every tier.
// Hooks (compile-only safe): MemLog.Boot -> TexDedupe.Boot(), GlbPreload.ResetStatics -> TexDedupe.Boot() (earlier than any import), MemLog.Update -> TexDedupe.Tick(). No serialized field, no new RuntimeInitializeOnLoadMethod.
public static class TexDedupe
{
    [Serializable] class Cfg { public bool enabled = false; public string[] tiers = new string[0]; public int minBytes = 262144; public float settleS = 6f; public float scanS = 2f; public float idleS = 10f; public int maxGroupsPerTick = 2; public bool verify = true; public bool log = true; public int logFirst = 40; public float tickS = 10f; }
    static Cfg cfg; static bool loaded, booted;

    sealed class RefCmp : IEqualityComparer<Texture2D> { public bool Equals(Texture2D a, Texture2D b) { return object.ReferenceEquals(a, b); } public int GetHashCode(Texture2D o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); } }   // by object reference (GetInstanceID is obsolete; a destroyed texture must still be findable)
    sealed class Tag { public string key; public float refSince = -1f; public long bytes; }
    static readonly Dictionary<Texture2D, Tag> tags = new Dictionary<Texture2D, Tag>(new RefCmp());          // importer-owned textures with a content key
    static readonly Dictionary<string, Texture2D> canon = new Dictionary<string, Texture2D>();               // key -> our canonical copy
    static readonly HashSet<Texture2D> owned = new HashSet<Texture2D>(new RefCmp());                         // the canonical copies
    static readonly Dictionary<Texture2D, float> idleSince = new Dictionary<Texture2D, float>(new RefCmp());
    static readonly HashSet<string> bad = new HashSet<string>();                                             // keys whose copy failed the GPU check: never touched again
    static int collapsed, madeCopies, destroyedCanon, tagged, errors, verifyFail; static long grossB, copyB; static float nextScan, nextLog; static int lastSig = -1;

    /// <summary>Reads the row (idempotent) and registers the import addon (idempotent). Called from MemLog.Boot and GlbPreload.ResetStatics.</summary>
    public static void Boot()
    {
        LoadCfg();
        if (booted) return;
        booted = true;
        try { ImportAddonRegistry.RegisterImportAddon(new TexDedupeAddon()); }
        catch (Exception e) { Debug.LogWarning("[TexDedupe] register: " + e.Message); }
    }

    static void LoadCfg()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            foreach (var p in DataPath.Candidates("texture_dedupe.json"))
            {
                if (!System.IO.File.Exists(p)) continue;
                cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); break;
            }
            foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-texdedupe:on") { if (cfg == null) cfg = new Cfg(); cfg.enabled = true; } else if (a == "-texdedupe:off" && cfg != null) cfg.enabled = false; }
        }
        catch (Exception e) { Debug.LogWarning("[TexDedupe] cfg: " + e.Message); }
        Debug.Log("[TexDedupe] " + (cfg != null && cfg.enabled ? "on tiers=" + (cfg.tiers == null || cfg.tiers.Length == 0 ? "all" : string.Join(",", cfg.tiers)) + " minBytes=" + cfg.minBytes + " settleS=" + cfg.settleS + " verify=" + cfg.verify : "off (data)"));
    }

    static bool TierOk()
    {
        if (cfg.tiers == null || cfg.tiers.Length == 0) return true;
        foreach (var t in cfg.tiers) if (string.Equals(t, QualityTiers.Current, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True while the dedupe is on for this tier (checked on every import and every tick, so a hot data flip applies to the next glb).</summary>
    public static bool Active { get { LoadCfg(); return cfg != null && cfg.enabled && TierOk(); } }

    /// <summary>True for a canonical copy owned here (TexCompress must not re-encode or destroy it).</summary>
    public static bool IsShared(UnityEngine.Texture t) { var t2 = t as Texture2D; return !object.ReferenceEquals(t2, null) && owned.Contains(t2); }

    internal static bool Claims(ReadOnlySpan<byte> d)
    {
        if (DownsizeLoad.Wants(d)) return true;   /* DOWNSIZELOAD v1 */ if (!Active || d.Length < cfg.minBytes || d.Length < 8) return false;
        bool png = d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
        bool jpg = d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;
        return png || jpg;   // KTX / WebP / anything else: glTFast's own path
    }

    static string Hash(ReadOnlySpan<byte> s, bool linear, bool mips)
    {
        ulong h1 = 14695981039346656037UL, h2 = 0x9E3779B97F4A7C15UL;
        var w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(s);
        for (int i = 0; i < w.Length; i++)
        {
            ulong v = w[i];
            h1 = (h1 ^ v) * 1099511628211UL; h1 ^= h1 >> 29;
            h2 = (h2 + v) * 0xBF58476D1CE4E5B9UL; h2 ^= h2 >> 31;
        }
        for (int i = w.Length * 8; i < s.Length; i++) { h1 = (h1 ^ s[i]) * 1099511628211UL; h2 = (h2 + s[i]) * 0xBF58476D1CE4E5B9UL; }
        return h1.ToString("x16") + h2.ToString("x16") + "|" + s.Length + "|" + (linear ? "L" : "S") + (mips ? "M" : "m");
    }

    // identical to GLTFast.ImageConversionImageLoader.LoadAsync(bytes): same creation flags, same LoadImage call. The importer still owns the result.
    internal static ImageResult Load(NativeArray<byte>.ReadOnly data, bool linear, bool readable, bool mips)
    {
        var span = data.AsReadOnlySpan();
        var flags = TextureCreationFlags.DontUploadUponCreate | TextureCreationFlags.DontInitializePixels;
        if (mips) flags |= TextureCreationFlags.MipChain;
        var t = DownsizeLoad.Decode(span, linear, readable, mips); bool dsl = t != null;   /* DOWNSIZELOAD v1: reduced at decode, null = glTFast path */ if (!dsl) t = new Texture2D(4, 4, linear ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R8G8B8A8_SRGB, flags);
        if (!dsl) t.LoadImage(span, !readable);
        if (!readable && Active)   // a readable image is glTFast's multi-sampler clone source: never tagged
        {
            try { tags[t] = new Tag { key = Hash(span, linear, mips) }; tagged++; } catch (Exception e) { errors++; Debug.LogWarning("[TexDedupe] tag: " + e.Message); }
        }
        return new ImageResult(t);
    }

    static long Bytes(Texture2D t) { try { return MemLog.Est(t); } catch (Exception) { return (long)t.width * t.height * 4; } }

    /// <summary>Called every frame from MemLog.Update; does real work at most every scanS seconds. Never throws.</summary>
    public static void Tick()
    {
        if (cfg == null || !cfg.enabled) return;
        float now = Time.realtimeSinceStartup; if (now < nextScan) return;
        nextScan = now + Mathf.Max(0.5f, cfg.scanS);
        try { Work(now); } catch (Exception e) { errors++; Debug.LogWarning("[TexDedupe] tick: " + e.Message); }
        Summary(now);
    }

    static void Work(float now)
    {
        // 1. forget destroyed importer textures (their import was disposed) and destroyed canonicals
        List<Texture2D> dead = null;
        foreach (var kv in tags) if (kv.Key == null) { if (dead == null) dead = new List<Texture2D>(); dead.Add(kv.Key); }
        if (dead != null) foreach (var d in dead) tags.Remove(d);
        if (tags.Count == 0 && canon.Count == 0) return;
        // 2. cheap pre-check: is there anything to collapse (a key twice, or a key that already has a canonical)?
        var cnt = new Dictionary<string, int>();
        foreach (var kv in tags) { int c; cnt.TryGetValue(kv.Value.key, out c); cnt[kv.Value.key] = c + 1; }
        bool work = canon.Count > 0;
        if (!work) foreach (var kv in cnt) if (kv.Value >= 2) { work = true; break; }
        if (!work) return;
        // 3. one scan: which of our textures does a Material reference right now
        var refc = new Dictionary<Texture2D, int>(new RefCmp()); var ids = new List<int>();
        var mats = Resources.FindObjectsOfTypeAll<UnityEngine.Material>();
        foreach (var m in mats)
        {
            if (m == null) continue;
            ids.Clear(); m.GetTexturePropertyNameIDs(ids);
            foreach (int id in ids) { var t = m.GetTexture(id) as Texture2D; if (!object.ReferenceEquals(t, null) && t != null && (tags.ContainsKey(t) || owned.Contains(t))) { int c; refc.TryGetValue(t, out c); refc[t] = c + 1; } }
        }
        // 4. settle clocks, ready members per key
        var ready = new Dictionary<string, List<Texture2D>>();
        foreach (var kv in tags)
        {
            if (kv.Key == null) continue;
            int rc; refc.TryGetValue(kv.Key, out rc);
            if (rc == 0) { kv.Value.refSince = -1f; continue; }
            if (kv.Value.refSince < 0f) kv.Value.refSince = now;
            if (now - kv.Value.refSince < cfg.settleS || bad.Contains(kv.Value.key)) continue;
            List<Texture2D> l; if (!ready.TryGetValue(kv.Value.key, out l)) { l = new List<Texture2D>(); ready[kv.Value.key] = l; }
            l.Add(kv.Key);
        }
        // 5. collapse up to maxGroupsPerTick keys (bounds the transient: one extra canonical copy per group)
        var replace = new Dictionary<Texture2D, Texture2D>(new RefCmp()); int groups = 0;
        foreach (var kv in ready)
        {
            if (groups >= Mathf.Max(1, cfg.maxGroupsPerTick)) break;
            Texture2D c; canon.TryGetValue(kv.Key, out c);
            if (!object.ReferenceEquals(c, null) && c == null) { canon.Remove(kv.Key); c = null; }
            if (object.ReferenceEquals(c, null))
            {
                if (kv.Value.Count < 2) continue;
                var src = kv.Value[0];
                c = MakeVerifiedCopy(src, kv.Key);
                if (c == null) { bad.Add(kv.Key); continue; }
                canon[kv.Key] = c; owned.Add(c); madeCopies++; copyB += Bytes(c);
            }
            foreach (var t in kv.Value) replace[t] = c;
            groups++;
        }
        if (replace.Count > 0)
        {
            // 6. re-point EVERY material that holds a duplicate (covers clones made by gear / NPC code), then destroy the duplicates
            int swapped = 0;
            foreach (var m in mats)
            {
                if (m == null) continue;
                ids.Clear(); m.GetTexturePropertyNameIDs(ids);
                foreach (int id in ids) { var t = m.GetTexture(id) as Texture2D; Texture2D c; if (!object.ReferenceEquals(t, null) && t != null && replace.TryGetValue(t, out c)) { m.SetTexture(id, c); swapped++; } }
            }
            foreach (var kv in replace)
            {
                long b = tags.ContainsKey(kv.Key) ? Bytes(kv.Key) : 0; grossB += b; collapsed++;
                if (cfg.log && collapsed <= cfg.logFirst) Debug.Log("[TexDedupe] collapse " + (b / 1048576.0).ToString("F0") + " MB " + kv.Key.width + "x" + kv.Key.height + " " + kv.Key.format + " name=" + kv.Key.name + " -> shared copy (materials re-pointed so far " + swapped + ")");
                tags.Remove(kv.Key); UnityEngine.Object.Destroy(kv.Key);
            }
        }
        // 7. canonical copies nothing references any more are released after idleS
        List<string> drop = null;
        foreach (var kv in canon)
        {
            var c = kv.Value; bool gone = object.ReferenceEquals(c, null) || c == null;
            if (!gone)
            {
                int rc; refc.TryGetValue(c, out rc);
                if (rc > 0 || replace.ContainsValue(c)) { idleSince.Remove(c); continue; }
                float since; if (!idleSince.TryGetValue(c, out since)) { idleSince[c] = now; continue; }
                if (now - since < cfg.idleS) continue;
            }
            if (drop == null) drop = new List<string>(); drop.Add(kv.Key);
        }
        if (drop != null) foreach (var k in drop) { var c = canon[k]; canon.Remove(k); if (!object.ReferenceEquals(c, null)) { owned.Remove(c); idleSince.Remove(c); if (c != null) { UnityEngine.Object.Destroy(c); destroyedCanon++; } } }
    }

    // Same TextureFormat, mip count, colour space as the source (nothing remapped); non-readable BEFORE the GPU copy so no CPU buffer of zeros exists; verified on the GPU before use.
    static Texture2D MakeVerifiedCopy(Texture2D t, string key)
    {
        Texture2D c = null;
        try
        {
            if (SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None) return null;
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);
            c = new Texture2D(t.width, t.height, t.format, t.mipmapCount, linear) { name = t.name, wrapMode = t.wrapMode, filterMode = t.filterMode, anisoLevel = t.anisoLevel, mipMapBias = t.mipMapBias };
            if (c.format != t.format || c.graphicsFormat != t.graphicsFormat || c.mipmapCount != t.mipmapCount || c.width != t.width || c.height != t.height) { UnityEngine.Object.Destroy(c); return null; }
            c.Apply(false, true);                 // upload once, drop the CPU buffer: from here the copy is GPU-only like glTFast's own non-readable textures
            Graphics.CopyTexture(t, c);
            if (cfg.verify && !SameOnGpu(t, c)) { verifyFail++; Debug.LogWarning("[TexDedupe] GPU check failed for " + t.name + " " + t.format + " " + t.width + "x" + t.height + ": key skipped, nothing changed"); UnityEngine.Object.Destroy(c); return null; }
            return c;
        }
        catch (Exception e) { errors++; Debug.LogWarning("[TexDedupe] copy " + t.name + ": " + e.Message); if (c != null) UnityEngine.Object.Destroy(c); return null; }
    }

    static bool SameOnGpu(Texture2D a, Texture2D b)
    {
        RenderTexture rt = null; Texture2D ra = null, rb = null; var prev = RenderTexture.active;
        try
        {
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(a.graphicsFormat);
            rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            ra = Grab(a, rt); rb = Grab(b, rt);
            var pa = ra.GetRawTextureData<byte>(); var pb = rb.GetRawTextureData<byte>();
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) return false;
            return true;
        }
        finally { RenderTexture.active = prev; if (rt != null) RenderTexture.ReleaseTemporary(rt); if (ra != null) UnityEngine.Object.Destroy(ra); if (rb != null) UnityEngine.Object.Destroy(rb); }
    }

    static Texture2D Grab(Texture2D t, RenderTexture rt)
    {
        Graphics.Blit(t, rt); RenderTexture.active = rt;
        var r = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
        r.ReadPixels(new Rect(0, 0, 256, 256), 0, 0, false); r.Apply(false, false);
        return r;
    }

    static void Summary(float now)
    {
        if (!cfg.log || now < nextLog) return;
        nextLog = now + Mathf.Max(1f, cfg.tickS);
        int sig = collapsed * 1000003 + madeCopies * 1009 + destroyedCanon + tagged;
        if (sig == lastSig) return;
        lastSig = sig;
        Debug.Log("[TexDedupe] " + collapsed + " copies collapsed / " + ((grossB - copyB) / 1073741824.0).ToString("F2") + " GB freed net (gross " + (grossB / 1073741824.0).ToString("F2") + " GB minus " + madeCopies + " shared copies " + (copyB / 1073741824.0).ToString("F2")
            + " GB, est raw texture size) | tagged=" + tagged + " pending=" + tags.Count + " sharedLive=" + canon.Count + " sharedDestroyed=" + destroyedCanon + " gpuCheckFail=" + verifyFail + " errors=" + errors + " tierNow=" + QualityTiers.Current);
    }
}

internal sealed class TexDedupeAddon : ImportAddon<TexDedupeInstance> { }

internal sealed class TexDedupeInstance : ImportAddonInstance, ITextureImageLoader
{
    public override void Inject(GltfImportBase gltfImport) { gltfImport.AddImportAddonInstance(this); }
    public override void Inject(IInstantiator instantiator) { }
    public override bool SupportsGltfExtension(string extensionName) { return false; }
    public bool IsAbleToLoad(TextureBase texture, out int imageIndex) { imageIndex = -1; return false; }
    public bool IsAbleToLoad(ReadOnlySpan<byte> data) { return TexDedupe.Claims(data); }
    public Task<ImageResult> LoadImage(NativeArray<byte>.ReadOnly data, bool linear, bool readable, bool generateMipMaps, CancellationToken cancellationToken)
    {
        return Task.FromResult(TexDedupe.Load(data, linear, readable, generateMipMaps));
    }
    public override void Dispose() { }
}
