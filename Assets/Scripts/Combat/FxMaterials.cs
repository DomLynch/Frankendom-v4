using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Runtime particle materials for HDRP + a guard against default (flat white) materials.
// Why: HDRP/Unlit ignores Shuriken vertex colour and vertex alpha (see FurnaceParticle.shader), so a CombatVFX / BloodBurst system on an HDRP/Unlit material
// ignores colour-over-lifetime and startColor: every dust puff / blood billboard stays a full-strength grey-white card until its lifetime ends, then pops off.
// FxMaterials.Particle() rebuilds those materials on Furnace/Particle (tex * vertex colour * tint, premultiplied alpha or additive).
// FxMaterialGuard scans renderers at start and every few seconds and replaces a null / non-HDRP material on a ParticleSystemRenderer with an invisible one.
// All numbers and shader names live in Assets/Data/fx_materials.json (fx_materials.json missing = built-in defaults below).

public static class FxMaterialTable
{
    [Serializable] public class Role { public string role = ""; public int additive = 0; public float intensity = 1f, softDistance = 0f; public float[] tint = new float[0]; }
    [Serializable]
    public class Data
    {
        public int version = 1, enabled = 1, guard = 1, convertCombatVfx = 1, replaceBadParticleMaterials = 1, fillNullSlots = 1;
        public string particleShader = "Furnace/Particle";
        public string[] okPrefixes = new string[] { "HDRP/", "Shader Graphs/", "Furnace/", "Hidden/" };
        public string[] keepShaders = new string[] { "Sprites/Default" };
        public float scanSeconds = 4f, recheckSeconds = 20f;
        public int logMax = 40;
        public int softDotSize = 32;
        public Role[] roles = new Role[0];
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    public static Role GetRole(string role)
    {
        var d = Get();
        for (int i = 0; i < d.roles.Length; i++) if (d.roles[i] != null && d.roles[i].role == role) return d.roles[i];
        return new Role();
    }

    static void Load()
    {
        data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("fx_materials");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "fx_materials.json");
                if (File.Exists(p)) json = DataPath.ReadFile(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/fx_materials.json");
                if (File.Exists(p)) json = DataPath.ReadFile(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("FxMaterialTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) return;   // defaults
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.okPrefixes == null) d.okPrefixes = new string[0];
            if (d.keepShaders == null) d.keepShaders = new string[0];
            if (d.roles == null) d.roles = new Role[0];
            if (string.IsNullOrEmpty(d.particleShader)) d.particleShader = "Furnace/Particle";
            data = d;
        }
        catch (Exception ex) { Debug.LogError("FxMaterialTable: parse failed: " + ex.Message); }
    }
}

public static class FxMaterials
{
    static Texture2D softDot;
    static Material fallback;
    static bool fallbackTried;

    public static Shader FindParticleShader()
    {
        var d = FxMaterialTable.Get();
        return Shader.Find(d.particleShader);
    }

    // round soft white dot (alpha falls off to the edge): the stand-in texture so a particle never samples Unity's white default (a square card)
    public static Texture2D SoftDot()
    {
        if (softDot != null) return softDot;
        int n = Mathf.Clamp(FxMaterialTable.Get().softDotSize, 8, 256);
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp; tex.name = "FxSoftDot";
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Clamp01(1f - (u * u + v * v));
                a = a * a;
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
            }
        tex.SetPixels32(px); tex.Apply(false, true);
        softDot = tex;
        return softDot;
    }

    // A Furnace/Particle copy of `src` (texture taken from it) for a Shuriken system. Returns `src` unchanged when the feature is off or the shader is missing.
    public static Material Particle(Material src, string role)
    {
        var d = FxMaterialTable.Get();
        if (d.enabled == 0 || d.convertCombatVfx == 0) return src;
        Shader sh = FindParticleShader();
        if (sh == null)
        {
            Debug.LogWarning("FxMaterials: shader '" + d.particleShader + "' not found (add it to Always Included Shaders); keeping the original material for '" + role + "'.");
            return src;
        }
        var r = FxMaterialTable.GetRole(role);
        Texture tex = null;
        if (src != null)
        {
            tex = src.mainTexture;
            if (tex == null && src.HasProperty("_UnlitColorMap")) tex = src.GetTexture("_UnlitColorMap");
        }
        if (tex == null) tex = SoftDot();
        var m = new Material(sh);
        m.name = "FxMat_" + role;
        m.SetTexture("_MainTex", tex);
        Color tint = Color.white;
        if (r.tint != null && r.tint.Length >= 4) tint = new Color(r.tint[0], r.tint[1], r.tint[2], r.tint[3]);
        m.SetColor("_Tint", tint);
        m.SetFloat("_Intensity", r.intensity);
        m.SetFloat("_ExposureWeight", 0f);
        m.SetFloat("_SoftDistance", r.softDistance);
        m.SetFloat("_SrcBlend", 1f);                              // BlendMode.One
        m.SetFloat("_DstBlend", r.additive != 0 ? 1f : 10f);     // One (additive) or OneMinusSrcAlpha (premultiplied alpha)
        m.renderQueue = 3000;
        return m;
    }

    // Invisible material: Furnace/Particle, additive, black, alpha 0 (adds nothing). Null when the shader is missing (callers then disable the renderer).
    public static Material Fallback()
    {
        if (fallback != null) return fallback;
        if (fallbackTried) return null;
        fallbackTried = true;
        Shader sh = FindParticleShader();
        if (sh == null) return null;
        var m = new Material(sh);
        m.name = "FxMat_InvisibleFallback";
        m.SetTexture("_MainTex", SoftDot());
        m.SetColor("_Tint", new Color(0f, 0f, 0f, 0f));
        m.SetFloat("_Intensity", 0f);
        m.SetFloat("_ExposureWeight", 0f);
        m.SetFloat("_SoftDistance", 0f);
        m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f);
        m.renderQueue = 3000;
        fallback = m;
        return fallback;
    }
}

// Startup + periodic scan. One log line per offending renderer (deduplicated, capped by fx_materials.json logMax).
public class FxMaterialGuard : MonoBehaviour
{
    readonly HashSet<Renderer> seen = new HashSet<Renderer>();
    readonly HashSet<string> logged = new HashSet<string>();
    readonly List<Material> tmp = new List<Material>();
    float nextScan, nextClear; int logCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var d = FxMaterialTable.Get();
        if (d.enabled == 0 || d.guard == 0) return;
        if (FindFirstObjectByType<FxMaterialGuard>() != null) return;
        var go = new GameObject("FxMaterialGuard");
        DontDestroyOnLoad(go);
        go.AddComponent<FxMaterialGuard>();
    }

    void Start()
    {
        var d = FxMaterialTable.Get();
        Scan();
        nextScan = Time.unscaledTime + Mathf.Max(0.5f, d.scanSeconds);
        nextClear = Time.unscaledTime + Mathf.Max(2f, d.recheckSeconds);
    }

    // PERF: the old Update checked every renderer of the scene in one frame (and re-checked them all every recheckSeconds): a periodic hitch that grew with the particle / decal / piece count.
    // The renderer list is still gathered at the same moments, but at most perf.json fxScanPerFrame renderers are checked per frame; the next scan waits until the previous one is done.
    Renderer[] pending; int pendingIdx;

    void Update()
    {
        float now = Time.unscaledTime;
        if (pending != null) { Drain(PerfFlags.C.fxScanPerFrame); return; }
        if (now < nextScan) return;
        var d = FxMaterialTable.Get();
        nextScan = now + Mathf.Max(0.5f, d.scanSeconds);
        if (now >= nextClear) { seen.Clear(); nextClear = now + Mathf.Max(2f, d.recheckSeconds); }
        pending = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None); pendingIdx = 0;
        Drain(PerfFlags.C.fxScanPerFrame);
    }

    void Drain(int budget)
    {
        var d = FxMaterialTable.Get();
        int done = 0;
        while (pending != null && pendingIdx < pending.Length && done < budget)
        {
            var r = pending[pendingIdx++];
            if (r == null || !seen.Add(r)) continue;
            done++;
            try { Check(r, d); } catch (Exception ex) { Debug.LogWarning("[FxGuard] check failed on " + r.name + ": " + ex.Message); }
        }
        if (pending != null && pendingIdx >= pending.Length) pending = null;
    }

    static bool StartsWithAny(string s, string[] prefixes)
    {
        for (int i = 0; i < prefixes.Length; i++) if (!string.IsNullOrEmpty(prefixes[i]) && s.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
        return false;
    }
    static bool EqualsAny(string s, string[] names)
    {
        for (int i = 0; i < names.Length; i++) if (names[i] == s) return true;
        return false;
    }

    static string PathOf(Transform t)
    {
        string p = t.name;
        int guardN = 0;
        while (t.parent != null && guardN++ < 24) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    void Log(Renderer r, int slot, string what, string action)
    {
        if (logCount >= FxMaterialTable.Get().logMax) return;
        string key = r.GetType().Name + "|" + r.name + "|" + slot + "|" + what;
        if (!logged.Add(key)) return;
        logCount++;
        Debug.LogWarning("[FxGuard] " + PathOf(r.transform) + " (" + r.GetType().Name + ") slot " + slot + ": " + what + " -> " + action);
    }

    void Scan()
    {
        pending = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None); pendingIdx = 0;
        Drain(int.MaxValue);   // the start-up scan stays complete and immediate
    }

    void Check(Renderer r, FxMaterialTable.Data d)
    {
        var psr = r as ParticleSystemRenderer;
        tmp.Clear();
        r.GetSharedMaterials(tmp);

        if (psr != null)
        {
            // a particle renderer in Mesh mode with no mesh draws nothing; everything else needs exactly the first material
            Material m = tmp.Count > 0 ? tmp[0] : null;
            string what = null; bool replace = false;
            if (m == null) { what = "null material"; replace = true; }
            else if (m.shader == null) { what = "material '" + m.name + "' has no shader"; replace = true; }
            else
            {
                string sn = m.shader.name;
                bool ok = StartsWithAny(sn, d.okPrefixes);
                bool keep = EqualsAny(sn, d.keepShaders);
                if (!ok) { what = "non-HDRP shader '" + sn + "'"; replace = !keep; if (keep) what += " (kept: keepShaders)"; }
            }
            if (what == null) return;
            if (!replace || d.replaceBadParticleMaterials == 0) { Log(r, 0, what, "logged only"); return; }
            var fb = FxMaterials.Fallback();
            if (fb != null) { psr.sharedMaterial = fb; Log(r, 0, what, "replaced with invisible fallback"); }
            else { psr.enabled = false; Log(r, 0, what, "renderer disabled (fallback shader missing)"); }
            return;
        }

        // other renderers (mesh, skinned, sprite, trail, line): log; fill null slots with the invisible fallback so nothing renders as flat white
        bool changed = false;
        for (int s = 0; s < tmp.Count; s++)
        {
            var m = tmp[s];
            if (m == null)
            {
                if (d.fillNullSlots != 0)
                {
                    var fb = FxMaterials.Fallback();
                    if (fb != null) { tmp[s] = fb; changed = true; Log(r, s, "null material", "slot filled with invisible fallback"); continue; }
                }
                Log(r, s, "null material", "logged only");
            }
            else if (m.shader != null && !StartsWithAny(m.shader.name, d.okPrefixes))
            {
                Log(r, s, "non-HDRP shader '" + m.shader.name + "'", "logged only");
            }
        }
        if (changed) r.sharedMaterials = tmp.ToArray();
    }
}
