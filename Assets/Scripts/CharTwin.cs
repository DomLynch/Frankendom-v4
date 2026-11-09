using System;
using System.Collections.Generic;
using UnityEngine;

// CHARTWIN v1 (laneVIS4, 2026-11-09). UNPROVEN until compiled and run by the Unity dev. PRESENTATION ONLY. NEW FILE: no existing script calls it, nothing existing is edited.
// What: swaps a character's material for a prebuilt "twin" of it (a Resources material authored in the Editor by CharTwinAuthor: same HDRP/Lit, same textures, but Material Type = Subsurface Scattering + a diffusion
//   profile (item 15), or Material Type = Anisotropy (item 16 hair)). The twin is an ASSET in Resources, so its shader variants are compiled into the player; nothing here flips shader keywords at runtime.
// Why twins, not a runtime flip: the SSS / Aniso Lit variants are shader_feature variants that the player build only keeps when some material in the build uses them. A runtime HDMaterial.SetMaterialType on a build
//   whose materials are all Standard would ask for a variant that was stripped.
// Data (one json per item, same schema, each its own switch; Data + StreamingAssets identical): char_sss.json (item 15), char_hair.json (item 16). enabled=false (the shipped state) or a missing file = nothing happens, the
//   driver restores the original materials. A hot edit of the json (the built game's folder) applies while the game runs, on and off.
//   rows[] { character (roster id, any case), enabled, materials[] { match (part of the material name, like character_materials.json), twin (Resources path, no extension) } }.
//   copyTextures / copyColors / copyFloats = property names mirrored from the ORIGINAL material onto the twin every poll, so CharMatOverride's graded albedo blob and colour (it edits the original) still show on the twin.
//   twinMarker = a part of every twin's name; a slot whose material name contains it is never swapped again.
// It only reads Renderer.sharedMaterials and writes them back; no transforms, animators, colliders, Rigidbodies, DuelFighter state, sim, RNG or other json. Render time only.
// No per-frame allocation: polling is timed (pollSeconds, spawnPollSeconds right after a scene load), reused lists, cached property ids. One FindObjectsByType array per poll (2 per second by default).
public static class CharTwin
{
    [Serializable] public class MatRow { public string match = ""; public string twin = ""; }
    [Serializable] public class CharRow { public string character = ""; public bool enabled = true; public MatRow[] materials = new MatRow[0]; }
    [Serializable]
    public class Cfg
    {
        public bool enabled; public bool log = true; public float pollSeconds = 0.5f, spawnPollSeconds = 0.1f, spawnFastSeconds = 2f;
        public string instanceSuffix = "", cloneSuffix = "", twinMarker = "";
        public string[] copyTextures = new string[0], copyColors = new string[0], copyFloats = new string[0];
        public CharRow[] rows = new CharRow[0];
    }

    static readonly string[] Files = { "char_sss.json", "char_hair.json" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (UnityEngine.Object.FindFirstObjectByType<CharTwinDriver>() != null) return;
        bool any = false; foreach (var f in Files) if (DataPath.Find(f) != null) any = true;
        if (!any) return;   // no json shipped: not even a driver object
        var go = new GameObject(typeof(CharTwinDriver).Name); go.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(go);
        var d = go.AddComponent<CharTwinDriver>(); foreach (var f in Files) d.Groups.Add(new CharTwinGroup(f));
    }
}

// one json = one group = one switch
public class CharTwinGroup
{
    class Slot { public Renderer r; public int index; public Material original, twin; }

    readonly string file; CharTwin.Cfg cfg; DateTime stamp = DateTime.MinValue; bool loadedOnce;
    readonly List<Slot> swapped = new List<Slot>();
    readonly Dictionary<string, Material> twins = new Dictionary<string, Material>();
    readonly HashSet<string> warned = new HashSet<string>();
    readonly List<Renderer> rbuf = new List<Renderer>(); readonly List<Material> mbuf = new List<Material>();
    int[] texIds = new int[0], colIds = new int[0], fltIds = new int[0];

    public CharTwinGroup(string f) { file = f; }
    public bool Wants { get { return cfg != null && cfg.enabled; } }

    bool Reload()
    {
        string p = DataPath.Find(file);
        if (p == null) { bool had = cfg != null; cfg = null; loadedOnce = false; stamp = DateTime.MinValue; return had; }
        DateTime t = System.IO.File.GetLastWriteTimeUtc(p);
        if (loadedOnce && t == stamp) return false;
        loadedOnce = true; stamp = t;
        try { cfg = JsonUtility.FromJson<CharTwin.Cfg>(DataPath.ReadFile(p)); }
        catch (Exception e) { Debug.LogWarning("[CharTwin] bad " + file + ": " + e.Message); cfg = null; }
        if (cfg != null) { texIds = Ids(cfg.copyTextures); colIds = Ids(cfg.copyColors); fltIds = Ids(cfg.copyFloats); }
        return true;
    }

    static int[] Ids(string[] names) { if (names == null) return new int[0]; var a = new int[names.Length]; for (int i = 0; i < a.Length; i++) a[i] = Shader.PropertyToID(names[i] ?? ""); return a; }

    public float PollSeconds { get { return cfg != null ? cfg.pollSeconds : 1f; } }
    public CharTwin.Cfg Cfg { get { return cfg; } }

    // phase 1 of a poll: reload the json if it changed (a change restores everything first, then phase 2 re-swaps); true = phase 2 is wanted
    public bool Prepare()
    {
        if (Reload()) RevertAll();
        if (cfg == null || !cfg.enabled) { if (swapped.Count > 0) RevertAll(); return false; }
        return true;
    }

    // phase 2: swap / sync every fighter's renderers
    public void Run(DuelFighter[] fighters)
    {
        if (cfg == null || !cfg.enabled) return;
        for (int i = swapped.Count - 1; i >= 0; i--) if (swapped[i].r == null) swapped.RemoveAt(i);
        foreach (var f in fighters)
        {
            if (f == null) continue;
            var row = RowFor(Roster.IdOf(f.transform)); if (row == null || !row.enabled || row.materials == null) continue;
            rbuf.Clear(); f.transform.root.GetComponentsInChildren(true, rbuf);
            foreach (var r in rbuf) { if (r != null) Swap(r, row); }
        }
        foreach (var s in swapped) Sync(s);
    }

    CharTwin.CharRow RowFor(string id)
    {
        if (cfg.rows == null || string.IsNullOrEmpty(id)) return null;
        foreach (var r in cfg.rows) if (r != null && string.Equals(r.character, id, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    string BaseName(string n)
    {
        if (n == null) return "";
        if (!string.IsNullOrEmpty(cfg.cloneSuffix) && n.EndsWith(cfg.cloneSuffix, StringComparison.Ordinal)) n = n.Substring(0, n.Length - cfg.cloneSuffix.Length);
        if (!string.IsNullOrEmpty(cfg.instanceSuffix) && n.EndsWith(cfg.instanceSuffix, StringComparison.Ordinal)) n = n.Substring(0, n.Length - cfg.instanceSuffix.Length);
        return n;
    }

    void Swap(Renderer r, CharTwin.CharRow row)
    {
        r.GetSharedMaterials(mbuf); bool changed = false;
        for (int i = 0; i < mbuf.Count; i++)
        {
            var m = mbuf[i]; if (m == null) continue;
            string nm = BaseName(m.name);
            if (!string.IsNullOrEmpty(cfg.twinMarker) && nm.IndexOf(cfg.twinMarker, StringComparison.OrdinalIgnoreCase) >= 0) continue;   // already a twin
            foreach (var mr in row.materials)
            {
                if (mr == null || string.IsNullOrEmpty(mr.match) || nm.IndexOf(mr.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var tw = Twin(mr.twin); if (tw == null) break;
                swapped.Add(new Slot { r = r, index = i, original = m, twin = tw }); mbuf[i] = tw; changed = true;
                if (cfg.log) Debug.Log("[CharTwin] " + file + " " + row.character + ": " + r.name + " slot " + i + " " + m.name + " -> " + tw.name);
                break;
            }
        }
        if (changed) r.SetSharedMaterials(mbuf);
    }

    Material Twin(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        Material t; if (twins.TryGetValue(path, out t)) return t;
        var asset = Resources.Load<Material>(path);
        if (asset == null) { if (warned.Add(path)) Debug.LogWarning("[CharTwin] " + file + ": twin material not found in Resources: " + path + " (run the CharTwinAuthor step, then rebuild)"); return null; }
        t = new Material(asset); t.name = asset.name; twins[path] = t; return t;
    }

    void Sync(Slot s)
    {
        if (s.original == null || s.twin == null) return;
        foreach (int id in texIds) if (s.original.HasProperty(id) && s.twin.HasProperty(id)) { var tx = s.original.GetTexture(id); if (s.twin.GetTexture(id) != tx) s.twin.SetTexture(id, tx); }
        foreach (int id in colIds) if (s.original.HasProperty(id) && s.twin.HasProperty(id)) { var c = s.original.GetColor(id); if (s.twin.GetColor(id) != c) s.twin.SetColor(id, c); }
        foreach (int id in fltIds) if (s.original.HasProperty(id) && s.twin.HasProperty(id)) { float v = s.original.GetFloat(id); if (s.twin.GetFloat(id) != v) s.twin.SetFloat(id, v); }
    }

    public void RevertAll()
    {
        foreach (var s in swapped)
        {
            if (s.r == null || s.original == null) continue;
            s.r.GetSharedMaterials(mbuf);
            if (s.index < mbuf.Count && mbuf[s.index] == s.twin) { mbuf[s.index] = s.original; s.r.SetSharedMaterials(mbuf); }
        }
        swapped.Clear();
        foreach (var kv in twins) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
        twins.Clear();
    }
}

// survives scene loads; fast polling right after a scene load so a character is swapped in the frame it appears
public class CharTwinDriver : MonoBehaviour
{
    public readonly List<CharTwinGroup> Groups = new List<CharTwinGroup>();
    public float idlePollSeconds = 1f;   // no group wants anything: only the json stamps are checked, this often
    float next, fastUntil;
    void OnEnable() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnLoaded; }
    void OnLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m)
    {
        float fastS = 0f; foreach (var g in Groups) if (g.Cfg != null && g.Cfg.spawnFastSeconds > fastS) fastS = g.Cfg.spawnFastSeconds;
        fastUntil = Time.unscaledTime + fastS; next = 0f;
    }

    void LateUpdate()   // after every script's Update, before the frame renders
    {
        float now = Time.unscaledTime; if (now < next) return;
        bool fast = now < fastUntil, want = false;
        foreach (var g in Groups) { try { if (g.Prepare()) want = true; } catch (Exception e) { Debug.LogWarning("[CharTwin] prepare failed: " + e.Message); } }
        float step = idlePollSeconds;
        if (want)
        {
            var arr = FindObjectsByType<DuelFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None); step = float.PositiveInfinity;
            foreach (var g in Groups)
            {
                if (!g.Wants) continue;
                try { g.Run(arr); } catch (Exception e) { Debug.LogWarning("[CharTwin] run failed: " + e.Message); }
                float p = fast ? g.Cfg.spawnPollSeconds : g.Cfg.pollSeconds; if (p < step) step = p;
            }
        }
        next = now + step;
    }
}
