using System;
using System.Collections.Generic;
using UnityEngine;

// TEXSHARE v1 (Lead, Strategy GO 17 Nov): every runtime glb load (gear, NPC bodies, mounts, ambient horses, monsters) decodes its own copy of the same images (TexTop: image_0 x10, image_2 x7 ... = ~1.5 GB of duplicates at z1 +60 s).
// After a glb is instantiated, Apply(root, file) swaps each runtime texture on the root's materials for ONE shared copy per (file, name, size, format); the importer's own copy is destroyed 2 s later (second pass catches materials swapped after the instantiate).
// The shared copy is a GPU copy (Graphics.CopyTexture, no CPU readback) owned here and destroyed when its last root is gone. Textures are never edited in place; tints live on the material. Data row texture_share.json (enabled false = off, hot).
// Driven from MemLog.Update (existing boot path: a compile-only take cannot add a new RuntimeInitializeOnLoadMethod).
public static class TexShare
{
    [Serializable] class Cfg { public bool enabled = false; public int minSize = 1024; public float settleS = 2f; public bool log = true; }
    static Cfg cfg;
    class Entry { public Texture2D shared; public HashSet<GameObject> users = new HashSet<GameObject>(); }
    class Job { public GameObject root; public string file; public float at; public List<Texture2D> originals; }
    static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    static readonly List<Job> jobs = new List<Job>();
    static int swaps, reused, created, destroyedOrig; static long savedB; static float nextPrune;

    public static void LoadCfg()
    {
        try
        {
            foreach (var p in DataPath.Candidates("texture_share.json"))
            {
                if (!System.IO.File.Exists(p)) continue;
                cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); break;
            }
            foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-texshare:on") { if (cfg == null) cfg = new Cfg(); cfg.enabled = true; } else if (a == "-texshare:off" && cfg != null) cfg.enabled = false; }
        }
        catch (Exception e) { Debug.LogWarning("[TexShare] cfg: " + e.Message); }
        Debug.Log("[TexShare] " + (cfg != null && cfg.enabled ? "on" : "off (data)"));
    }

    public static bool On { get { return cfg != null && cfg.enabled; } }

    // call right after InstantiateMainSceneAsync(root) succeeded; file = the glb path (key)
    public static void Apply(GameObject root, string file)
    {
        if (!On || root == null || string.IsNullOrEmpty(file)) return;
        try { var orig = Pass(root, file); jobs.Add(new Job { root = root, file = file, at = Time.realtimeSinceStartup + Mathf.Max(0.5f, cfg.settleS), originals = orig }); }
        catch (Exception e) { Debug.LogWarning("[TexShare] apply " + file + ": " + e.ToString()); }
    }

    static List<Texture2D> Pass(GameObject root, string file)
    {
        var originals = new List<Texture2D>();
        var done = new Dictionary<Texture2D, Texture2D>();   // original -> shared, per pass
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                foreach (int id in m.GetTexturePropertyNameIDs())
                {
                    var t = m.GetTexture(id) as Texture2D;
                    if (t == null || t.width < cfg.minSize) continue;
                    Texture2D s;
                    if (!done.TryGetValue(t, out s))
                    {
                        s = Shared(root, file, t);
                        done[t] = s;
                        if (s != null && s != t && !originals.Contains(t)) originals.Add(t);
                    }
                    if (s != null && s != t) { m.SetTexture(id, s); swaps++; }
                }
            }
        }
        return originals;
    }

    static Texture2D Shared(GameObject root, string file, Texture2D t)
    {
        // an asset texture (not created at runtime) is already shared by Unity; only glTFast-style runtime textures carry no asset path
        if (t.name != null && (t.name.StartsWith("zgm_") || t.name.StartsWith("Cookie"))) return t;
        string key = file + "|" + t.name + "|" + t.width + "x" + t.height + "|" + t.format + "|" + t.mipmapCount;
        Entry e;
        if (entries.TryGetValue(key, out e) && e.shared != null) { e.users.Add(root); if (e.shared != t) reused++; return e.shared; }
        if (SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None) return t;
        var gf = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat) ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
        if (t.format != TextureFormat.ARGB32 && t.format != TextureFormat.RGBA32 && t.format != TextureFormat.RGB24 && t.format != TextureFormat.BGRA32) gf = SystemInfo.GetCompatibleFormat(t.graphicsFormat, UnityEngine.Experimental.Rendering.FormatUsage.Sample);   // compressed (BC7/DXT/BC5): same block format
        if (gf == UnityEngine.Experimental.Rendering.GraphicsFormat.None) return t;
        var c = new Texture2D(t.width, t.height, gf, t.mipmapCount, UnityEngine.Experimental.Rendering.TextureCreationFlags.None) { name = t.name, wrapMode = t.wrapMode, filterMode = t.filterMode, anisoLevel = t.anisoLevel, mipMapBias = t.mipMapBias };
        Graphics.CopyTexture(t, c);
        e = new Entry { shared = c }; e.users.Add(root); entries[key] = e; created++;
        return c;
    }

    // called every frame from MemLog.Update
    public static void Tick()
    {
        if (!On) return;
        float now = Time.realtimeSinceStartup;
        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            var j = jobs[i];
            if (now < j.at) continue;
            jobs.RemoveAt(i);
            if (j.root == null) { foreach (var t in j.originals) KillOrig(t); continue; }
            try
            {
                var more = Pass(j.root, j.file);   // late material swaps (gear materials, tints) re-point at the shared copy
                foreach (var t in more) if (!j.originals.Contains(t)) j.originals.Add(t);
                foreach (var t in j.originals) KillOrig(t);
            }
            catch (Exception e) { Debug.LogWarning("[TexShare] settle " + j.file + ": " + e.Message); }
        }
        if (now >= nextPrune)
        {
            nextPrune = now + 5f;
            var dead = new List<string>();
            foreach (var kv in entries)
            {
                kv.Value.users.RemoveWhere(g => g == null);
                if (kv.Value.users.Count == 0 && jobsHold(kv.Key) == false) dead.Add(kv.Key);
            }
            foreach (var k in dead) { if (entries[k].shared != null) UnityEngine.Object.Destroy(entries[k].shared); entries.Remove(k); }
            if (cfg.log && (swaps > 0)) Debug.Log("[TexShare] shared=" + entries.Count + " created=" + created + " reused=" + reused + " swaps=" + swaps + " origDestroyed=" + destroyedOrig + " savedGB=" + (savedB / 1073741824.0).ToString("F2"));
        }
    }

    static bool jobsHold(string key) { return false; }

    static void KillOrig(Texture2D t)
    {
        if (t == null) return;
        long sz = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t);
        UnityEngine.Object.Destroy(t); destroyedOrig++; savedB += sz;
    }
}
