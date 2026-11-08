using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

// CHARMAT v2 (laneASSETAUDIT, 2026-11-08). UNPROVEN until compiled and run by the Unity dev. PRESENTATION ONLY.
// What: per-character material overrides read from character_materials.json (StreamingAssets, Data mirror), so a colour or albedo tweak is a data deploy, not a build.
//   rows[] { character (roster id, any case), materials[] { match, baseColor [r,g,b] multiplier, smoothness / metallic (-1 = leave), albedoBlob (pre-compressed BC7 blob, preferred), albedoTexture (jpg/png fallback) } }.
//   EVERY name, suffix, property name, texture setting and timing is a key in the json (props{}, texture{}, instanceSuffix, cloneSuffix, pollSeconds, spawnPollSeconds, spawnFastSeconds): nothing hard-coded here except the file name, read through DataPath like the other tables.
// When: a hidden driver (created once after the first scene load) polls for DuelFighter objects it has not processed. For spawnFastSeconds after a scene load or a new fighter it polls at spawnPollSeconds (0 = every frame), so a character
//   is re-coloured in the frame it appears; otherwise at pollSeconds. A changed json OR a changed texture file (write time) re-applies to every character, with no material re-clone (editor clones are reused) and the replaced texture destroyed.
// Textures: albedoBlob = "BC7B" blob from bc7blob/make_bc7.py (BC7 + full mips, about 21 MB for 4096), loaded with LoadRawTextureData: no image decode, no runtime compress, no hitch. albedoTexture (RGBA32 decode) only if the blob is missing or BC7 is unsupported.
// What it writes: ONLY Material properties named in props{} on materials found on the character's Renderers (editor: Renderer.sharedMaterials swaps to clones, so no .mat asset is dirtied; player: the scene's material instances in place).
//   It never touches transforms, animators, colliders, Rigidbodies, DuelFighter, Health, Fighter, rules or any json other than character_materials.json. No existing script calls it.
public static class CharMatOverride
{
    [Serializable] public class MatRow { public string match = ""; public float[] baseColor; public float smoothness = -1f, metallic = -1f; public string albedoBlob = "", albedoTexture = ""; }
    [Serializable] public class CharRow { public string character = ""; public MatRow[] materials = new MatRow[0]; }
    [Serializable] public class Props { public string[] baseColor = new string[0], baseMap = new string[0], smoothness = new string[0], metallic = new string[0]; }
    [Serializable] public class TexCfg { public int anisoLevel = 1; public string wrap = "", filter = "", blobMagic = "", namePrefix = ""; }
    [Serializable] public class Cfg
    {
        public bool enabled = true; public float pollSeconds = 0.25f, spawnPollSeconds = 0f, spawnFastSeconds = 2f;
        public string instanceSuffix = "", cloneSuffix = ""; public Props props = new Props(); public TexCfg texture = new TexCfg(); public CharRow[] rows = new CharRow[0];
    }

    const string FileName = "character_materials.json";
    static Cfg cfg; static DateTime cfgStamp = DateTime.MinValue; static bool loadedOnce;
    static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
    static readonly Dictionary<string, DateTime> texStamp = new Dictionary<string, DateTime>();
    static readonly Dictionary<string, DateTime> texFail = new Dictionary<string, DateTime>();   // key -> file stamp of a load that failed: not retried (and not logged again) until the file changes
    static readonly List<Texture2D> trash = new List<Texture2D>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (UnityEngine.Object.FindFirstObjectByType<CharMatOverrideDriver>() != null) return;
        var go = new GameObject(typeof(CharMatOverrideDriver).Name); go.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<CharMatOverrideDriver>();
    }

    internal static Cfg Current { get { return cfg; } }
    static string SaPath(string rel) { return Path.Combine(Application.streamingAssetsPath, rel.Replace('/', Path.DirectorySeparatorChar)); }

    // true when the json changed since the last call (or loaded for the first time)
    internal static bool Reload()
    {
        string p = DataPath.Find(FileName);
        if (p == null) { if (cfg != null) { cfg = null; return true; } return false; }
        DateTime t = File.GetLastWriteTimeUtc(p);
        if (loadedOnce && t == cfgStamp) return false;
        loadedOnce = true; cfgStamp = t;
        try { cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(p)); }
        catch (Exception e) { Debug.LogWarning("[CharMat] bad " + FileName + ": " + e.Message); cfg = null; }
        return true;
    }

    // a signature of every texture file the rows name (write times): a changed texture alone re-applies
    internal static int TextureSignature()
    {
        int sig = 0; if (cfg == null || cfg.rows == null) return sig;
        foreach (var r in cfg.rows) { if (r == null || r.materials == null) continue; foreach (var m in r.materials) { if (m == null) continue; sig = HashCode.Combine(sig, Stamp(m.albedoBlob), Stamp(m.albedoTexture)); } }
        return sig;
    }
    static long Stamp(string rel) { if (string.IsNullOrEmpty(rel)) return 0L; string p = SaPath(rel); return File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0L; }

    static CharRow RowFor(string id)
    {
        if (cfg == null || !cfg.enabled || cfg.rows == null || string.IsNullOrEmpty(id)) return null;
        foreach (var r in cfg.rows) if (r != null && string.Equals(r.character, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    static T ParseEnum<T>(string s, T dflt) where T : struct { T v; return Enum.TryParse(s, true, out v) ? v : dflt; }

    static Texture2D Make(MatRow r)
    {
        Texture2D t = null;
        if (!string.IsNullOrEmpty(r.albedoBlob)) t = LoadBlob(r.albedoBlob);
        if (t == null && !string.IsNullOrEmpty(r.albedoTexture)) t = LoadImageFile(r.albedoTexture);
        return t;
    }

    static void Finish(Texture2D t, string rel)
    {
        t.name = cfg.texture.namePrefix + Path.GetFileNameWithoutExtension(rel);
        t.wrapMode = ParseEnum(cfg.texture.wrap, TextureWrapMode.Repeat); t.filterMode = ParseEnum(cfg.texture.filter, FilterMode.Trilinear); t.anisoLevel = cfg.texture.anisoLevel;
    }

    // pre-compressed BC7 blob: 32-byte header written by make_bc7.py (the loader reads dataOffset from the header), then the raw mips
    static Texture2D LoadBlob(string rel)
    {
        string p = SaPath(rel); if (!File.Exists(p)) return null;
        if (!SystemInfo.SupportsTextureFormat(TextureFormat.BC7)) { Debug.LogWarning("[CharMat] BC7 not supported, falling back for " + rel); return null; }
        Texture2D t = null;
        try
        {
            byte[] all = File.ReadAllBytes(p); int magic, ver, w, h, mips, flags, off;
            using (var br = new BinaryReader(new MemoryStream(all))) { magic = br.ReadInt32(); ver = br.ReadInt32(); w = br.ReadInt32(); h = br.ReadInt32(); mips = br.ReadInt32(); flags = br.ReadInt32(); off = br.ReadInt32(); }
            if (magic != BitConverter.ToInt32(Encoding.ASCII.GetBytes(cfg.texture.blobMagic), 0)) { Debug.LogWarning("[CharMat] not a blob: " + rel); return null; }
            t = new Texture2D(w, h, TextureFormat.BC7, mips, (flags & 1) != 0);
            var raw = t.GetRawTextureData<byte>();
            if (raw.Length != all.Length - off) { Debug.LogWarning("[CharMat] blob size mismatch " + rel + " (" + raw.Length + " vs " + (all.Length - off) + ")"); UnityEngine.Object.Destroy(t); t = null; return null; }
            NativeArray<byte>.Copy(all, off, raw, 0, raw.Length);
            t.Apply(false, true); Finish(t, rel); return t;
        }
        catch (Exception e) { Debug.LogWarning("[CharMat] blob failed " + rel + ": " + e.Message); if (t != null) UnityEngine.Object.Destroy(t); return null; }
    }

    // fallback: decoded image file, uncompressed (large, one-off hitch)
    static Texture2D LoadImageFile(string rel)
    {
        string p = SaPath(rel); if (!File.Exists(p)) { Debug.LogWarning("[CharMat] texture missing " + rel); return null; }
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, true, false);
        if (!t.LoadImage(File.ReadAllBytes(p), true)) { Debug.LogWarning("[CharMat] cannot decode " + rel); UnityEngine.Object.Destroy(t); return null; }
        Finish(t, rel); return t;
    }

    // cached by the row's blob / texture path; a newer file replaces the entry and the old texture is destroyed by the driver after the re-apply
    static Texture2D Tex(MatRow r)
    {
        string key = !string.IsNullOrEmpty(r.albedoBlob) ? r.albedoBlob : r.albedoTexture; if (string.IsNullOrEmpty(key)) return null;
        long st = Math.Max(Stamp(r.albedoBlob), Stamp(r.albedoTexture)); DateTime t = new DateTime(st, DateTimeKind.Utc); Texture2D tex;
        if (texCache.TryGetValue(key, out tex) && tex != null && texStamp[key] == t) return tex;
        DateTime ft; if (texFail.TryGetValue(key, out ft) && ft == t) return tex;   // this file version already failed once
        var n = Make(r); if (n == null) { texFail[key] = t; return tex; }   // keep the old one if the new file cannot be read
        texFail.Remove(key);
        if (tex != null) trash.Add(tex);
        texCache[key] = n; texStamp[key] = t; return n;
    }

    internal static void EmptyTrash() { foreach (var t in trash) if (t != null) UnityEngine.Object.Destroy(t); trash.Clear(); }

    static string BaseName(string n)
    {
        if (n == null) return "";
        foreach (var suf in new[] { cfg.cloneSuffix, cfg.instanceSuffix }) if (!string.IsNullOrEmpty(suf) && n.EndsWith(suf, StringComparison.Ordinal)) n = n.Substring(0, n.Length - suf.Length);
        return n;
    }

    // apply one character row to every renderer under root; returns the number of materials changed
    public static int Apply(Transform root, string id)
    {
        var row = RowFor(id); if (row == null || row.materials == null || root == null) return 0;
        int n = 0;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials; bool replaced = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue; string nm = BaseName(m.name);
                foreach (var mr in row.materials)
                {
                    if (mr == null || string.IsNullOrEmpty(mr.match) || nm.IndexOf(mr.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var target = m;
                    if (Application.isEditor && !string.IsNullOrEmpty(cfg.cloneSuffix) && !m.name.EndsWith(cfg.cloneSuffix, StringComparison.Ordinal)) { target = new Material(m); target.name = m.name + cfg.cloneSuffix; mats[i] = target; replaced = true; }   // editor: clone once, never dirty the .mat asset
                    Set(target, mr); n++; break;
                }
            }
            if (replaced) r.sharedMaterials = mats;
        }
        return n;
    }

    static void SetAll(Material m, string[] props, float v) { if (props == null) return; foreach (var p in props) if (!string.IsNullOrEmpty(p) && m.HasProperty(p)) m.SetFloat(p, v); }

    static void Set(Material m, MatRow r)
    {
        var P = cfg.props;
        if (r.baseColor != null && r.baseColor.Length > 2 && P.baseColor != null) foreach (var p in P.baseColor) if (!string.IsNullOrEmpty(p) && m.HasProperty(p)) m.SetColor(p, new Color(r.baseColor[0], r.baseColor[1], r.baseColor[2], 1f));
        var t = Tex(r); if (t != null && P.baseMap != null) foreach (var p in P.baseMap) if (!string.IsNullOrEmpty(p) && m.HasProperty(p)) m.SetTexture(p, t);
        if (r.smoothness >= 0f) SetAll(m, P.smoothness, Mathf.Clamp01(r.smoothness));
        if (r.metallic >= 0f) SetAll(m, P.metallic, Mathf.Clamp01(r.metallic));
    }
}

// survives scene loads. Fast polling (spawnPollSeconds) for spawnFastSeconds after a scene load or a new fighter, normal polling (pollSeconds) otherwise.
public class CharMatOverrideDriver : MonoBehaviour
{
    readonly HashSet<GameObject> done = new HashSet<GameObject>(); float next, nextSig, fastUntil; int sig; bool sigSet;
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void OnLoaded(Scene s, LoadSceneMode m) { var c = CharMatOverride.Current; fastUntil = Time.unscaledTime + (c != null ? c.spawnFastSeconds : 0f); next = 0f; }

    void LateUpdate()   // after every script's Update of the frame, before the frame renders
    {
        float now = Time.unscaledTime; if (now < next) return;
        bool changed = CharMatOverride.Reload(); var c = CharMatOverride.Current;
        if (c == null || !c.enabled) { next = now + (c != null ? c.pollSeconds : 0f); return; }
        if (now >= nextSig) { nextSig = now + c.pollSeconds; int s = CharMatOverride.TextureSignature(); if (sigSet && s != sig) changed = true; sig = s; sigSet = true; }
        next = now + (now < fastUntil ? c.spawnPollSeconds : c.pollSeconds);
        if (changed) done.Clear(); else done.RemoveWhere(g => g == null);
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (f == null) continue; var key = f.gameObject; if (done.Contains(key)) continue;
            done.Add(key); fastUntil = now + c.spawnFastSeconds;
            string id = Roster.IdOf(f.transform);
            try { int n = CharMatOverride.Apply(f.transform.root, id); if (n > 0) Debug.Log("[CharMat] " + id + ": " + n + " material(s) overridden from character_materials.json"); }
            catch (Exception e) { Debug.LogWarning("[CharMat] " + id + " skipped: " + e.Message); }
        }
        CharMatOverride.EmptyTrash();
    }
}
