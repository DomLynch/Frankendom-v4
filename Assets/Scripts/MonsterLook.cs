using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

// MONSTERLOOK v3 (laneMONSTERDATA, 2026-11-08; v3 = DATAREVIEW K1-K4 fixes + prefab-map release without UnloadUnusedAssets). UNPROVEN until compiled and run by the Unity dev. PRESENTATION ONLY, OFF by default (no row has look.enabled true).
// What: a monster's MESH and TEXTURES come from StreamingAssets (a geometry-only glb through glTFast + pre-compressed BC7 blobs), so a model / colour / texture change is a data deploy, not a build.
// Rows live in character_materials.json (laneASSETAUDIT's file, shared, found with DataPath.Find): rows[].look { enabled, model, parts[] { renderer, mesh, slots[] { slot, glbMaterial, baseMap, normalMap, maskMap } } }.
// Property names, keywords and suffixes are data too: the file's props{} (baseMap / normalMap / maskMap lists), texture{} (aniso, wrap, filter, blobMagic, namePrefix), instanceSuffix, lookGlobal{ cloneSuffix, keywords, gates }.
// Swap = IN PLACE on the prefab's own SkinnedMeshRenderer: sharedMesh + bones (rebound BY NAME to the prefab's skeleton, same as GearAttach) + rootBone + sharedMaterials + localBounds. Skeleton, Animator, controller, clips,
// colliders, CharacterController, ragdoll and sockets are never touched, so FighterLod / MeshCut / BellyBite / WoundDecals / HitFlash / RagdollRig keep seeing the same renderer objects.
// Materials = a CLONE of the prefab's material in that slot (shader, keywords, remaps unchanged) with only the base / normal / mask maps replaced; CharMatOverride rows (tint, smoothness) then apply on top.
// GATES before any write (any failure = the prefab look stays, one log line): renderer on the prefab, glb mesh present, readable (MeshCut), sub-mesh count and glb material NAME per slot, every bone found by NAME,
// BINDPOSE per bone name against the prefab mesh's own bindposes (max element difference <= lookGlobal.bindposeEps, and at least bindposeMinFrac of the look's bones compared), every blendshape NAME the prefab mesh
// has also exists on the look mesh (BellyBite / morph drivers), texture maps loaded, material has the property.
// COST WHEN OFF: no driver work beyond one float compare per frame and a file stat every offPollS; no scene scan, no entry, no glb/texture loaded (an entry is retired the moment its row goes off).
// Switches: lookGlobal.enabled, rows[].look.enabled, command line -monsterlook:on (every row that has a model) | off | json (default).
// Hot reload: json or a blob / glb file changed on disk -> fighters are restored to the prefab look and re-applied. Restore also runs when a row is switched off while the game runs.
public static class MonsterLook
{
    [Serializable] public class Slot { public int slot; public string glbMaterial = "", baseMap = "", normalMap = "", maskMap = ""; }
    [Serializable] public class Part { public string renderer = "", mesh = ""; public Slot[] slots = new Slot[0]; }
    [Serializable] public class Look { public bool enabled; public string model = ""; public Part[] parts = new Part[0]; }
    [Serializable] public class Row { public string character = ""; public Look look; }
    [Serializable] public class Keywords { public string[] normalMap = new string[0], maskMap = new string[0]; }
    [Serializable] public class Global
    {
        public bool enabled = true, unloadAfterSwap = true, requireReadable = true, logSwap = true, bindposeGate = true, blendshapeGate = true;
        public float sigCheckS = 1f, retireAfterS = 3f, minPollS = 0.1f, offPollS = 2f, bindposeEps = 0.0001f, bindposeMinFrac = 0.5f;
        public int anisoMax = 16, releasePerFrame = 1; public string cloneSuffix = ""; public Keywords keywords = new Keywords();
    }
    [Serializable] public class Props { public string[] baseMap = new string[0], normalMap = new string[0], maskMap = new string[0]; }
    [Serializable] public class TexCfg { public int anisoLevel = 1; public string wrap = "", filter = "", blobMagic = "", namePrefix = ""; }
    [Serializable] public class Cfg
    {
        public bool enabled = true; public float pollSeconds = 0.5f; public string instanceSuffix = ""; public Props props = new Props(); public TexCfg texture = new TexCfg();
        public Global lookGlobal = new Global(); public Row[] rows = new Row[0];
    }

    const string File_ = "character_materials.json";
    // BC7B format facts (not tunables): 4x4 blocks of 16 bytes, 32-byte header; FNV-1a constants and the fingerprint quantum of the skeleton check; bytes per MB
    const int BlockBytes = 16, BlockDim = 4, HeaderBytes = 32, MatElems = 16;   // MatElems = floats in a Matrix4x4
    const float FpQuant = 100000f, MiB = 1048576f; const ulong FnvOffset = 1469598103934665603UL, FnvPrime = 1099511628211UL;
    internal static Cfg cfg; static DateTime cfgStamp = DateTime.MinValue; static bool loadedOnce;
    static int cli = -1;   // -1 unread, 0 json, 1 on, 2 off
    public static int Swapped, Kept, Restored, Tagged;   // counters for the log line / proof; Tagged = fighters that currently wear the data look
    internal static bool AnyLook;                         // cached at every Reload: some row asks for a look (so the driver has work)
    internal static bool Loading;                         // some entry is still loading (the driver steps it every frame only then)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; cfgStamp = DateTime.MinValue; loadedOnce = false; cli = -1; Swapped = Kept = Restored = Tagged = 0; AnyLook = Loading = false; entries.Clear(); retiring.Clear(); seen.Clear(); toRelease.Clear(); releaseQueue.Clear(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (CliMode() == 2) return;
        if (UnityEngine.Object.FindFirstObjectByType<MonsterLookDriver>() != null) return;
        var go = new GameObject("MonsterLookDriver"); go.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<MonsterLookDriver>();
    }

    static int CliMode()
    {
        if (cli >= 0) return cli; cli = 0;
        try { foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-monsterlook:on") cli = 1; else if (a == "-monsterlook:off") cli = 2; else if (a == "-monsterlook:json") cli = 0; } } catch (Exception) { }
        return cli;
    }

    internal static Global Gl() { return cfg != null && cfg.lookGlobal != null ? cfg.lookGlobal : new Global(); }

    static string SA(string rel) { return Path.Combine(Application.streamingAssetsPath, rel.Replace('/', Path.DirectorySeparatorChar)); }

    // true when the json changed since the last call (or loaded the first time). The file is found with DataPath.Find (editor: Assets/Data first, player: StreamingAssets first).
    internal static bool Reload()
    {
        string p = DataPath.Find(File_);
        if (p == null) { if (cfg != null) { cfg = null; AnyLook = false; return true; } return false; }
        DateTime t = File.GetLastWriteTimeUtc(p);
        if (loadedOnce && t == cfgStamp) return false;
        loadedOnce = true; cfgStamp = t;
        try
        {
            cfg = JsonUtility.FromJson<Cfg>(File.ReadAllText(p));
            if (cfg != null) { if (cfg.lookGlobal == null) cfg.lookGlobal = new Global(); if (cfg.props == null) cfg.props = new Props(); if (cfg.texture == null) cfg.texture = new TexCfg(); if (cfg.lookGlobal.keywords == null) cfg.lookGlobal.keywords = new Keywords(); }
        }
        catch (Exception e) { Debug.LogWarning("[MonsterLook] bad " + File_ + ": " + e.Message); cfg = null; }
        AnyLook = ComputeAny();
        return true;
    }

    static bool ComputeAny()
    {
        if (!GlobalOn || cfg.rows == null) return false;
        foreach (var r in cfg.rows) if (r != null && r.look != null && (r.look.enabled || CliMode() == 1) && !string.IsNullOrEmpty(r.look.model)) return true;
        return false;
    }

    internal static bool GlobalOn { get { return CliMode() != 2 && cfg != null && cfg.enabled && cfg.lookGlobal != null && (cfg.lookGlobal.enabled || CliMode() == 1); } }
    internal static bool EverLoaded { get { return loadedOnce; } }

    // the active look of a character id, or null (switch off, no row, no model)
    internal static Look LookFor(string id)
    {
        if (!GlobalOn || cfg.rows == null || string.IsNullOrEmpty(id)) return null;
        foreach (var r in cfg.rows)
            if (r != null && r.look != null && string.Equals(r.character, id, StringComparison.OrdinalIgnoreCase))
                return (r.look.enabled || CliMode() == 1) && !string.IsNullOrEmpty(r.look.model) ? r.look : null;
        return null;
    }

    /// <summary>GlbPreload.Wanted hook: the model glbs (relative to StreamingAssets) of every active look, so they are parsed during the intro. Empty (and no work) when nothing is switched on.</summary>
    public static List<string> PreloadFiles()
    {
        var list = new List<string>();
        try
        {
            Reload(); if (!AnyLook) return list;
            foreach (var r in cfg.rows) if (r != null && r.look != null && (r.look.enabled || CliMode() == 1) && !string.IsNullOrEmpty(r.look.model)) list.Add(r.look.model);
        }
        catch (Exception) { }
        return list;
    }

    // ---------------------------------------------------------------------------------------------- per-model cache
    internal sealed class Entry
    {
        public string id, sig, model; public Look look; public string state = "start", reason = ""; public float sigChecked, t0;
        public Task<GLTFast.GltfImport> glbTask; public GLTFast.GltfImport gltf; public Task<bool> instTask; public GameObject holder;
        public readonly Dictionary<string, Task<byte[]>> blobTasks = new Dictionary<string, Task<byte[]>>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Texture2D> tex = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, SkinnedMeshRenderer> templates = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.OrdinalIgnoreCase);
        public readonly Queue<string> texQueue = new Queue<string>(); public bool failedLogged;
        public bool Ready { get { return state == "ready"; } }
        public bool Failed { get { return state == "failed"; } }
        public void Fail(string why) { state = "failed"; reason = why; if (!failedLogged) { failedLogged = true; Debug.LogWarning("[MonsterLook] " + id + ": look unavailable, prefab look kept (" + why + ")"); } }
    }
    static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
    static readonly List<KeyValuePair<float, Entry>> retiring = new List<KeyValuePair<float, Entry>>();
    internal static int EntryCount { get { return entries.Count; } }
    internal static int RetiringCount { get { return retiring.Count; } }

    static string Signature(Look l)
    {
        var sb = new System.Text.StringBuilder();
        Action<string> add = s =>
        {
            if (string.IsNullOrEmpty(s)) return; string t = "";
            try { var f = new FileInfo(SA(s)); t = f.Exists ? f.LastWriteTimeUtc.Ticks + ":" + f.Length : "missing"; } catch (Exception) { }
            sb.Append(s).Append('@').Append(t).Append(';');
        };
        add(l.model);
        if (l.parts != null) foreach (var p in l.parts) { sb.Append(p.renderer).Append('/').Append(p.mesh).Append(';'); if (p.slots != null) foreach (var s in p.slots) { sb.Append(s.slot).Append(s.glbMaterial).Append(';'); add(s.baseMap); add(s.normalMap); add(s.maskMap); } }
        return sb.ToString();
    }

    // the entry for this character, created on first need; a changed look or file signature replaces it (re-checked at most every lookGlobal.sigCheckS)
    internal static Entry EntryFor(string id, Look l)
    {
        Entry e; entries.TryGetValue(id, out e);
        if (e != null && ReferenceEquals(e.look, l) && Time.unscaledTime - e.sigChecked < Gl().sigCheckS) return e;
        string sig = Signature(l);
        if (e != null && e.sig == sig) { e.look = l; e.sigChecked = Time.unscaledTime; return e; }
        if (e != null) retiring.Add(new KeyValuePair<float, Entry>(Time.unscaledTime, e));   // fighters still wear its mesh until the driver restores them: destroy it a few seconds later
        e = new Entry { id = id, sig = sig, model = l.model, look = l, sigChecked = Time.unscaledTime }; entries[id] = e; Loading = true; return e;
    }

    /// <summary>A row went off (or its file went away): retire its cached mesh + textures. Called by the driver AFTER it restored the fighters; destruction is still delayed by retireAfterS.</summary>
    internal static void DropIdle()
    {
        List<string> gone = null;
        foreach (var kv in entries) if (LookFor(kv.Key) == null) { if (gone == null) gone = new List<string>(); gone.Add(kv.Key); }
        if (gone == null) return;
        foreach (var k in gone) { retiring.Add(new KeyValuePair<float, Entry>(Time.unscaledTime, entries[k])); entries.Remove(k); Debug.Log("[MonsterLook] " + k + ": row is off, cached mesh + textures retired"); }
    }

    internal static void PurgeRetired()
    {
        for (int i = retiring.Count - 1; i >= 0; i--) if (Time.unscaledTime - retiring[i].Key > Gl().retireAfterS) { Retire(retiring[i].Value); retiring.RemoveAt(i); }
    }

    static void Retire(Entry e)
    {
        foreach (var t in e.tex.Values) if (t != null) UnityEngine.Object.Destroy(t);
        e.tex.Clear();
        if (e.holder != null) UnityEngine.Object.Destroy(e.holder);
        if (e.gltf != null) { try { e.gltf.Dispose(); } catch (Exception) { } e.gltf = null; }
    }

    internal static void StepAll()
    {
        bool any = false;
        foreach (var e in entries.Values) { if (!e.Ready && !e.Failed) StepEntry(e); if (!e.Ready && !e.Failed) any = true; }
        Loading = any;
    }

    static void StepEntry(Entry e)
    {
        try
        {
            switch (e.state)
            {
                case "start":
                {
                    e.t0 = Time.realtimeSinceStartup; string file = SA(e.model); if (!File.Exists(file)) { e.Fail("model file missing: " + e.model); return; }
                    var pre = GlbPreload.Take(file);   // parsed during the intro when perf_fixes C3 is on; a miss = load it here
                    e.glbTask = pre ?? LoadGlb(file);
                    var seenF = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in e.look.parts) foreach (var s in p.slots) foreach (var f in new[] { s.baseMap, s.normalMap, s.maskMap })
                    {
                        if (string.IsNullOrEmpty(f) || !seenF.Add(f)) continue;
                        string bp = SA(f); if (!File.Exists(bp)) { e.Fail("texture file missing: " + f); return; }
                        e.blobTasks[f] = Task.Run(() => File.ReadAllBytes(bp));
                    }
                    e.state = "loading"; return;
                }
                case "loading":
                {
                    if (!e.glbTask.IsCompleted) return;
                    foreach (var t in e.blobTasks.Values) if (!t.IsCompleted) return;
                    if (e.glbTask.IsFaulted) { e.Fail("glb task faulted"); return; }
                    e.gltf = e.glbTask.Result; if (e.gltf == null) { e.Fail("glTFast could not load " + e.model); return; }
                    foreach (var t in e.blobTasks.Values) if (t.IsFaulted) { e.Fail("texture read failed: " + t.Exception.GetBaseException().Message); return; }
                    e.holder = new GameObject("MonsterLook_" + e.id); e.holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(e.holder);
                    e.instTask = e.gltf.InstantiateMainSceneAsync(e.holder.transform); e.state = "instantiating"; return;
                }
                case "instantiating":
                {
                    if (!e.instTask.IsCompleted) return;
                    if (e.instTask.IsFaulted || !e.instTask.Result) { e.Fail("glTFast could not instantiate " + e.model); return; }
                    foreach (var smr in e.holder.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        e.templates[smr.name] = smr;
                        if (smr.sharedMesh != null && !e.templates.ContainsKey(smr.sharedMesh.name)) e.templates[smr.sharedMesh.name] = smr;
                    }
                    if (e.templates.Count == 0) { e.Fail("model has no skinned mesh"); return; }
                    foreach (var k in e.blobTasks.Keys) e.texQueue.Enqueue(k);
                    e.state = "textures"; return;
                }
                case "textures":
                {
                    if (e.texQueue.Count > 0)
                    {
                        string f = e.texQueue.Dequeue(); string why;
                        var t = Blob.Make(e.blobTasks[f].Result, f, out why);
                        if (t == null) { e.Fail("texture " + f + ": " + why); return; }
                        e.tex[f] = t; e.blobTasks[f] = null; return;   // one texture per frame: raw copy + Apply are a few ms each
                    }
                    e.state = "ready";
                    Debug.Log("[MonsterLook] " + e.id + ": model + " + e.tex.Count + " texture(s) ready in " + (Time.realtimeSinceStartup - e.t0).ToString("0.00") + " s wall, texture memory now " + (Texture.currentTextureMemory / MiB).ToString("0") + " MB (" + e.model + ")");
                    return;
                }
            }
        }
        catch (Exception ex) { e.Fail(ex.GetType().Name + ": " + ex.Message); }
    }

    static async Task<GLTFast.GltfImport> LoadGlb(string file)
    {
        GLTFast.GltfImport g = null;
        try { g = new GLTFast.GltfImport(); bool ok = await g.LoadFile(file); if (!ok) { g.Dispose(); return null; } return g; }
        catch (Exception ex) { Debug.LogWarning("[MonsterLook] glb load failed (" + file + "): " + ex.Message); try { if (g != null) g.Dispose(); } catch (Exception) { } return null; }
    }

    // ---------------------------------------------------------------------------------------------- BC7 blob: laneASSETAUDIT's "BC7B" v1 (kit/tools/make_look.py writes the same header as bc7blob/make_bc7.py)
    // header, 32 bytes little endian: magic (texture.blobMagic), version 1, width, height, mip count, flags (bit 0 = LINEAR, 0 = sRGB), dataOffset (32), 0. Data = every mip, largest first, 16 bytes per 4x4 block
    // (mips under 4x4 padded to one block), the image stored BOTTOM ROW FIRST (Unity's raw layout).
    static class Blob
    {
        const int FVer = 1, FW = 2, FH = 3, FMips = 4, FFlags = 5, FOff = 6;   // header int index (after the 4-byte magic)
        static int I(byte[] d, int field) { return BitConverter.ToInt32(d, sizeof(int) * field); }

        public static long ChainBytes(int w, int h, int mips)
        {
            long n = 0; for (int m = 0; m < mips; m++) n += (long)((Mathf.Max(1, w >> m) + BlockDim - 1) / BlockDim) * ((Mathf.Max(1, h >> m) + BlockDim - 1) / BlockDim) * BlockBytes; return n;
        }

        static T Parse<T>(string s, T fallback) where T : struct { T v; return Enum.TryParse(s, true, out v) ? v : fallback; }

        public static Texture2D Make(byte[] d, string name, out string why)
        {
            why = ""; var T = cfg != null && cfg.texture != null ? cfg.texture : new TexCfg(); var G = Gl();
            if (d == null || d.Length < HeaderBytes) { why = "too short"; return null; }
            if (System.Text.Encoding.ASCII.GetString(d, 0, BlockDim) != T.blobMagic) { why = "bad magic (texture.blobMagic is '" + T.blobMagic + "')"; return null; }
            int ver = I(d, FVer), w = I(d, FW), h = I(d, FH), mips = I(d, FMips), flags = I(d, FFlags), off = I(d, FOff);
            if (ver != 1 || w < BlockDim || h < BlockDim || mips < 1 || off < HeaderBytes || off > d.Length) { why = "bad header (ver " + ver + " " + w + "x" + h + " mips " + mips + " offset " + off + ")"; return null; }
            long len = d.Length - off;
            if (ChainBytes(w, h, mips) != len) { why = "mip chain size " + ChainBytes(w, h, mips) + " != " + len; return null; }
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.BC7)) { why = "BC7 not supported on this GPU"; return null; }
            bool linear = (flags & 1) != 0;
            var t = new Texture2D(w, h, TextureFormat.BC7, mips, linear);
            var raw = t.GetRawTextureData<byte>();
            if (raw.Length != len) { UnityEngine.Object.Destroy(t); why = "texture size " + raw.Length + " != " + len; return null; }
            Unity.Collections.NativeArray<byte>.Copy(d, off, raw, 0, (int)len);
            t.wrapMode = Parse(T.wrap, TextureWrapMode.Repeat); t.filterMode = Parse(T.filter, FilterMode.Trilinear); t.anisoLevel = Mathf.Clamp(T.anisoLevel, 1, G.anisoMax); t.name = T.namePrefix + Path.GetFileNameWithoutExtension(name);
            t.Apply(false, true);   // upload, drop the CPU copy
            return t;
        }
    }

    // ---------------------------------------------------------------------------------------------- applying to a fighter
    internal sealed class Saved { public SkinnedMeshRenderer smr; public Mesh mesh; public Transform[] bones; public Transform root; public Material[] mats; public Bounds bounds; }

    static Transform[] Mapped(SkinnedMeshRenderer tmpl, Dictionary<string, Transform> map, out string missing)
    {
        missing = ""; var src = tmpl.bones; var dst = new Transform[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            if (src[i] == null) { missing = "null bone " + i; return null; }
            var t = GearAttach.Find(map, src[i].name); if (t == null) { missing = src[i].name; return null; }
            dst[i] = t;
        }
        return dst;
    }

    // GATE: the look mesh's bindpose of every bone, by bone NAME, must equal the prefab mesh's bindpose of the same bone (the prefab mesh is the import of the same rig). A different rest pose / axis / scale
    // would deform the monster, and no later check could see it.
    static bool BindposeGate(SkinnedMeshRenderer prefab, SkinnedMeshRenderer look, Global G, out string why)
    {
        why = ""; if (!G.bindposeGate) return true;
        Matrix4x4[] pbp, lbp; Mesh pm = prefab.sharedMesh, lm = look.sharedMesh;
        try { pbp = pm != null ? pm.bindposes : null; lbp = lm.bindposes; } catch (Exception ex) { why = "bindposes unreadable (" + ex.Message + ")"; return false; }
        var pb = prefab.bones; var lb = look.bones;
        if (pbp == null || pb == null || pbp.Length != pb.Length) { why = "prefab bindposes unavailable (" + (pbp == null ? 0 : pbp.Length) + " poses, " + (pb == null ? 0 : pb.Length) + " bones)"; return false; }
        if (lbp == null || lb == null || lbp.Length != lb.Length) { why = "look bindposes unavailable"; return false; }
        var byName = new Dictionary<string, Matrix4x4>();
        for (int i = 0; i < pb.Length; i++) if (pb[i] != null) { byName[pb[i].name] = pbp[i]; byName[GearTable.Normalize(pb[i].name)] = pbp[i]; }
        int compared = 0; float worst = 0f; string worstBone = "";
        for (int i = 0; i < lb.Length; i++)
        {
            if (lb[i] == null) { why = "null bone " + i + " on the look mesh"; return false; }
            Matrix4x4 pp; if (!byName.TryGetValue(lb[i].name, out pp) && !byName.TryGetValue(GearTable.Normalize(lb[i].name), out pp)) continue;   // bone the prefab mesh is not skinned to: nothing to compare
            compared++;
            for (int k = 0; k < MatElems; k++) { float dlt = Mathf.Abs(pp[k] - lbp[i][k]); if (dlt > worst) { worst = dlt; worstBone = lb[i].name; } }
        }
        if (worst > G.bindposeEps) { why = "bindpose of bone '" + worstBone + "' differs from the prefab mesh by " + worst.ToString("0.#####") + " (bindposeEps " + G.bindposeEps + "): a different rest pose / scale"; return false; }
        if (lb.Length == 0 || compared < G.bindposeMinFrac * lb.Length) { why = "only " + compared + " of " + lb.Length + " look bones could be compared with the prefab bindposes (bindposeMinFrac " + G.bindposeMinFrac + ")"; return false; }
        return true;
    }

    // GATE: every blendshape name of the prefab mesh must exist on the look mesh (BellyBite and any morph driver look them up by name); extra shapes on the look mesh are fine
    static bool BlendshapeGate(SkinnedMeshRenderer prefab, SkinnedMeshRenderer look, Global G, out string why)
    {
        why = ""; if (!G.blendshapeGate || prefab.sharedMesh == null) return true;
        int n = prefab.sharedMesh.blendShapeCount; if (n == 0) return true;
        var have = new HashSet<string>(); for (int i = 0; i < look.sharedMesh.blendShapeCount; i++) have.Add(look.sharedMesh.GetBlendShapeName(i));
        for (int i = 0; i < n; i++) { string nm = prefab.sharedMesh.GetBlendShapeName(i); if (!have.Contains(nm)) { why = "prefab mesh has blendshape '" + nm + "' that the look mesh lacks (" + look.sharedMesh.blendShapeCount + " shape(s) there)"; return false; } }
        return true;
    }

    // fingerprint of the fighter's own transform tree (names + local pose): the swap must leave it exactly as it was
    static ulong SkelFp(Transform root)
    {
        ulong h = FnvOffset;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.GetComponentInParent<GearPiece>() != null) continue;
            var p = t.localPosition; var q = t.localRotation; var s = t.localScale;
            h = (h ^ (ulong)(uint)t.name.GetHashCode()) * FnvPrime;
            h = (h ^ (ulong)(long)Mathf.RoundToInt(p.x * FpQuant)) * FnvPrime; h = (h ^ (ulong)(long)Mathf.RoundToInt(p.y * FpQuant)) * FnvPrime; h = (h ^ (ulong)(long)Mathf.RoundToInt(p.z * FpQuant)) * FnvPrime;
            h = (h ^ (ulong)(long)Mathf.RoundToInt(q.x * FpQuant)) * FnvPrime; h = (h ^ (ulong)(long)Mathf.RoundToInt(q.y * FpQuant)) * FnvPrime; h = (h ^ (ulong)(long)Mathf.RoundToInt(q.z * FpQuant)) * FnvPrime;
            h = (h ^ (ulong)(long)Mathf.RoundToInt(s.x * FpQuant)) * FnvPrime;
        }
        return h;
    }

    static int Tris(Mesh m) { if (m == null) return 0; long n = 0; for (int i = 0; i < m.subMeshCount; i++) n += m.GetIndexCount(i); return (int)(n / 3); }

    sealed class Plan { public SkinnedMeshRenderer smr, tmpl; public Transform[] bones; public Material[] mats; public Transform rootBone; }

    // pop time: when a fighter was first seen without its look, to the frame it got it (a zone clone swapped in the spawn frame reports 0 frames)
    static readonly Dictionary<GameObject, KeyValuePair<float, int>> seen = new Dictionary<GameObject, KeyValuePair<float, int>>();   // keyed by the object itself (GetInstanceID is obsolete in 6000.6)
    internal static void Seen(GameObject root) { if (root != null && !seen.ContainsKey(root)) seen[root] = new KeyValuePair<float, int>(Time.realtimeSinceStartup, Time.frameCount); }
    internal static void ClearSeen() { seen.Clear(); }

    // PREFAB MAP RELEASE (replaces Resources.UnloadUnusedAssets, which scans every asset and can hitch for 100s of ms near a scene load): the prefab's own base / normal / mask textures that a look replaced stay
    // referenced by the saved prefab materials (needed to switch the look off again), so they would sit in memory next to the blobs. Resources.UnloadAsset frees just those textures; the reference stays valid and
    // Unity reloads the asset from disk if a prefab material is ever drawn again (look switched off). A few per frame (lookGlobal.releasePerFrame), only once the whole batch of fighters has been swapped.
    static readonly HashSet<Texture> toRelease = new HashSet<Texture>();
    static readonly Queue<Texture> releaseQueue = new Queue<Texture>();
    internal static int PendingRelease { get { return toRelease.Count; } }
    internal static int QueuedRelease { get { return releaseQueue.Count; } }
    internal static void QueueRelease() { foreach (var t in toRelease) if (t != null) releaseQueue.Enqueue(t); toRelease.Clear(); }
    internal static void StepRelease()
    {
        var G = Gl(); int n = Mathf.Max(1, G.releasePerFrame), freed = 0;
        while (n-- > 0 && releaseQueue.Count > 0)
        {
            var t = releaseQueue.Dequeue(); if (t == null) continue;
            try { Resources.UnloadAsset(t); freed++; } catch (Exception) { }
        }
        if (freed > 0 && releaseQueue.Count == 0 && G.logSwap) Debug.Log("[MonsterLook] prefab maps released (Resources.UnloadAsset, no UnloadUnusedAssets), texture memory now " + (Texture.currentTextureMemory / MiB).ToString("0") + " MB");
    }

    // all checks first, then all writes. Returns true when the look is on the fighter afterwards.
    internal static bool Apply(GameObject fighterRoot, string id, Entry e, out string why)
    {
        why = ""; var G = Gl(); Transform root = fighterRoot.transform; Seen(fighterRoot);
        var made = new List<Material>(); var plans = new List<Plan>(); var replaced = new List<Texture>();
        if (!BuildPlans(root, e, G, plans, made, replaced, out why)) { foreach (var m in made) if (m != null) UnityEngine.Object.Destroy(m); return false; }
        var tag = fighterRoot.GetComponent<MonsterLookTag>() ?? fighterRoot.AddComponent<MonsterLookTag>(); tag.id = id; tag.sig = e.sig;
        var sw = System.Diagnostics.Stopwatch.StartNew(); ulong fp0 = SkelFp(root); int trisOld = 0, trisNew = 0;
        if (tag.saved.Count == 0) Tagged++;
        foreach (var p in plans)
        {
            tag.saved.Add(new Saved { smr = p.smr, mesh = p.smr.sharedMesh, bones = p.smr.bones, root = p.smr.rootBone, mats = p.smr.sharedMaterials, bounds = p.smr.localBounds });
            trisOld += Tris(p.smr.sharedMesh); trisNew += Tris(p.tmpl.sharedMesh);
            p.smr.sharedMesh = p.tmpl.sharedMesh; p.smr.bones = p.bones; if (p.rootBone != null) p.smr.rootBone = p.rootBone; p.smr.sharedMaterials = p.mats; p.smr.localBounds = p.tmpl.localBounds;
        }
        tag.made.AddRange(made);
        foreach (var rt in replaced) if (!(rt is RenderTexture) && !e.tex.ContainsValue(rt as Texture2D)) toRelease.Add(rt);   // the prefab maps this look replaced: released after the batch
        ulong fp1 = SkelFp(root);
        KeyValuePair<float, int> s0; string pop = "";
        if (seen.TryGetValue(fighterRoot, out s0)) { pop = ", pop " + (Time.realtimeSinceStartup - s0.Key).ToString("0.000") + " s / " + (Time.frameCount - s0.Value) + " frame(s) from first sight to swap"; seen.Remove(fighterRoot); }
        if (G.logSwap) Debug.Log("[MonsterLook] " + id + ": look ON (" + plans.Count + " renderer(s), tris " + trisOld + " -> " + trisNew + ", " + made.Count + " material(s) cloned, skeleton fingerprint " + (fp0 == fp1 ? "SAME" : "CHANGED " + fp0 + " -> " + fp1) + ", model " + e.model + ", swap " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms" + pop + ", texture memory " + (Texture.currentTextureMemory / MiB).ToString("0") + " MB)");
        Swapped++; return true;
    }

    static bool BuildPlans(Transform root, Entry e, Global G, List<Plan> plans, List<Material> made, List<Texture> replaced, out string why)
    {
        why = ""; var map = GearAttach.BoneMap(root); var C = cfg;
        var prefabSmrs = new List<SkinnedMeshRenderer>();
        foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (s.GetComponentInParent<GearPiece>() == null) prefabSmrs.Add(s);
        foreach (var part in e.look.parts)
        {
            SkinnedMeshRenderer smr = null;
            foreach (var s in prefabSmrs) if (string.Equals(s.name, part.renderer, StringComparison.OrdinalIgnoreCase)) { smr = s; break; }
            if (smr == null) { why = "renderer '" + part.renderer + "' not on the prefab"; return false; }
            string key = string.IsNullOrEmpty(part.mesh) ? part.renderer : part.mesh; SkinnedMeshRenderer tm;
            if (!e.templates.TryGetValue(key, out tm) || tm == null || tm.sharedMesh == null) { why = "mesh '" + key + "' not in the model"; return false; }
            if (G.requireReadable && !tm.sharedMesh.isReadable) { why = "mesh '" + key + "' is not readable (MeshCut needs it)"; return false; }
            int sub = tm.sharedMesh.subMeshCount;
            if (part.slots == null || part.slots.Length != sub) { why = "'" + key + "' has " + sub + " sub-mesh(es), the row has " + (part.slots == null ? 0 : part.slots.Length) + " slot(s)"; return false; }
            var cur = smr.sharedMaterials; if (cur.Length < sub) { why = "prefab renderer '" + smr.name + "' has " + cur.Length + " material(s) for " + sub + " sub-mesh(es)"; return false; }
            if (!BlendshapeGate(smr, tm, G, out why)) return false;
            if (!BindposeGate(smr, tm, G, out why)) return false;
            var tmats = tm.sharedMaterials; var nm = new Material[cur.Length]; Array.Copy(cur, nm, cur.Length);
            foreach (var sl in part.slots)
            {
                if (sl.slot < 0 || sl.slot >= sub) { why = "slot " + sl.slot + " out of range"; return false; }
                // sub-mesh order BY NAME: the glb material in this sub-mesh slot must be the one the row names
                string gm = sl.slot < tmats.Length && tmats[sl.slot] != null ? tmats[sl.slot].name : "";
                if (!string.IsNullOrEmpty(sl.glbMaterial) && gm.IndexOf(sl.glbMaterial, StringComparison.OrdinalIgnoreCase) < 0) { why = "sub-mesh order: slot " + sl.slot + " of '" + key + "' is glb material '" + gm + "', the row expects '" + sl.glbMaterial + "'"; return false; }
                var pm = cur[sl.slot]; if (pm == null) { why = "prefab slot " + sl.slot + " has no material"; return false; }
                var c = UnityEngine.Object.Instantiate(pm); c.name = (string.IsNullOrEmpty(C.instanceSuffix) ? pm.name : pm.name.Replace(C.instanceSuffix, "")) + G.cloneSuffix; made.Add(c);
                if (!SetTex(c, C.props.baseMap, null, e, sl.baseMap, replaced, out why) || !SetTex(c, C.props.normalMap, G.keywords.normalMap, e, sl.normalMap, replaced, out why) || !SetTex(c, C.props.maskMap, G.keywords.maskMap, e, sl.maskMap, replaced, out why)) return false;
                nm[sl.slot] = c;
            }
            string miss; var bones = Mapped(tm, map, out miss);
            if (bones == null) { why = "bone '" + miss + "' of the model is not on the prefab skeleton"; return false; }
            plans.Add(new Plan { smr = smr, tmpl = tm, bones = bones, mats = nm, rootBone = tm.rootBone != null ? GearAttach.Find(map, tm.rootBone.name) : null });
        }
        return true;
    }

    // sets the texture on every property of the list the material has (props{} of the json); keywords from lookGlobal.keywords
    static bool SetTex(Material m, string[] props, string[] keywords, Entry e, string file, List<Texture> replaced, out string why)
    {
        why = ""; if (string.IsNullOrEmpty(file)) return true;   // empty = keep the prefab's own map
        Texture2D t; if (!e.tex.TryGetValue(file, out t) || t == null) { why = "texture '" + file + "' not loaded"; return false; }
        int hit = 0; if (props != null) foreach (var p in props) if (!string.IsNullOrEmpty(p) && m.HasProperty(p)) { var old = m.GetTexture(p); if (old != null && old != t && !replaced.Contains(old)) replaced.Add(old); m.SetTexture(p, t); hit++; }
        if (hit == 0) { why = "material '" + m.name + "' has none of the properties [" + (props == null ? "" : string.Join(",", props)) + "] (props{} in the json)"; return false; }
        if (keywords != null) foreach (var k in keywords) if (!string.IsNullOrEmpty(k)) m.EnableKeyword(k);
        return true;
    }

    internal static void Restore(GameObject fighterRoot)
    {
        var tag = fighterRoot.GetComponent<MonsterLookTag>(); if (tag == null || tag.saved.Count == 0) return;
        foreach (var s in tag.saved) if (s.smr != null) { s.smr.sharedMesh = s.mesh; s.smr.bones = s.bones; s.smr.rootBone = s.root; s.smr.sharedMaterials = s.mats; s.smr.localBounds = s.bounds; }
        foreach (var m in tag.made) if (m != null) UnityEngine.Object.Destroy(m);
        tag.made.Clear(); tag.saved.Clear(); tag.sig = ""; Restored++; Tagged--;
        Debug.Log("[MonsterLook] " + tag.id + ": prefab look restored");
    }

    internal static bool IsApplied(GameObject root, string sig) { var t = root.GetComponent<MonsterLookTag>(); return t != null && t.saved.Count > 0 && t.sig == sig; }
    internal static bool HasTag(GameObject root) { var t = root.GetComponent<MonsterLookTag>(); return t != null && t.saved.Count > 0; }

    /// <summary>ZoneRuntime.AttachRuntime hook: swap this freshly spawned clone in the SAME frame when its look is ready (the driver does the rest). Costs one bool when nothing is switched on. Never throws.</summary>
    public static void AttachTo(GameObject go)
    {
        try
        {
            if (!loadedOnce) Reload();
            if (go == null || CliMode() == 2 || !AnyLook) return;
            string id = Roster.IdOf(go.transform); var l = LookFor(id); if (l == null) return;
            Seen(go); var e = EntryFor(id, l); if (!e.Ready) return;
            string why;
            if (!Apply(go, id, e, out why)) { Kept++; Debug.LogWarning("[MonsterLook] " + id + ": prefab look kept (" + why + ")"); return; }
            try { CharMatOverride.Apply(go.transform, id); } catch (Exception) { }   // tint / smoothness rows of the same json on top of the new materials
        }
        catch (Exception ex) { Debug.LogWarning("[MonsterLook] AttachTo: " + ex.Message); }
    }
}

// remembers what the prefab had, so the look can be switched off again while the game runs; destroys the cloned materials with the fighter
public class MonsterLookTag : MonoBehaviour
{
    public string id = "", sig = "";
    internal readonly List<MonsterLook.Saved> saved = new List<MonsterLook.Saved>(); internal readonly List<Material> made = new List<Material>();
    void OnDestroy() { if (saved.Count > 0) MonsterLook.Tagged--; foreach (var m in made) if (m != null) Destroy(m); made.Clear(); }
}

// survives scene loads. When nothing is switched on its Update is one bool and one float compare; a file stat every offPollS watches for the switch.
public class MonsterLookDriver : MonoBehaviour
{
    readonly HashSet<GameObject> failed = new HashSet<GameObject>(); float next;
    void Awake() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDestroy() { SceneManager.sceneLoaded -= OnLoaded; }
    void OnLoaded(Scene s, LoadSceneMode m) { failed.Clear(); MonsterLook.ClearSeen(); }

    void Update()
    {
        if (MonsterLook.Loading) MonsterLook.StepAll();                       // per frame, only while a model / texture is loading
        if (MonsterLook.RetiringCount > 0) MonsterLook.PurgeRetired();
        if (MonsterLook.QueuedRelease > 0) MonsterLook.StepRelease();      // a few prefab textures per frame, only after a batch finished swapping
        if (Time.unscaledTime < next) return;
        bool changed = MonsterLook.Reload(); var c = MonsterLook.cfg; var G = MonsterLook.Gl();
        bool on = MonsterLook.AnyLook;
        if (!on && MonsterLook.Tagged == 0 && MonsterLook.EntryCount == 0) { next = Time.unscaledTime + Mathf.Max(G.minPollS, G.offPollS); return; }   // OFF: no scene scan, nothing loaded
        next = Time.unscaledTime + Mathf.Max(G.minPollS, c != null ? c.pollSeconds : G.minPollS);
        if (changed) failed.Clear();
        bool pending = false;
        if (on || MonsterLook.Tagged > 0)
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (f == null) continue; var go = f.transform.root.gameObject; string id = Roster.IdOf(f.transform);
                var l = on ? MonsterLook.LookFor(id) : null;
                if (l == null) { if (MonsterLook.HasTag(go)) MonsterLook.Restore(go); continue; }   // switched off while running: back to the prefab look
                MonsterLook.Seen(go);
                var e = MonsterLook.EntryFor(id, l);
                if (MonsterLook.IsApplied(go, e.sig) || failed.Contains(go)) continue;
                if (MonsterLook.HasTag(go)) MonsterLook.Restore(go);   // the rows or files changed: start again from the prefab look
                if (e.Failed) { failed.Add(go); MonsterLook.Kept++; continue; }
                if (!e.Ready) { pending = true; continue; }
                string why;
                if (!MonsterLook.Apply(go, id, e, out why)) { failed.Add(go); MonsterLook.Kept++; Debug.LogWarning("[MonsterLook] " + id + ": prefab look kept (" + why + ")"); continue; }
                try { CharMatOverride.Apply(go.transform, id); } catch (Exception) { }   // tint / smoothness rows of the same json on top of the new materials
            }
        MonsterLook.DropIdle();   // after the fighters were restored: a row that went off gives its cached mesh + textures back
        // batch finished swapping (no fighter still waiting for its model): hand the replaced prefab maps to the per-frame release queue. No UnloadUnusedAssets.
        if (!pending && MonsterLook.PendingRelease > 0 && G.unloadAfterSwap) MonsterLook.QueueRelease();   // also covers the AttachTo path (a clone swapped in its spawn frame)
    }
}
