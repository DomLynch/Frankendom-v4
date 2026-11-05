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
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; only = null; off = null; none = false; }

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
        if (On("E4"))
        {
            QualitySettings.streamingMipmapsActive = c.E4.streamingMipmapsActive; QualitySettings.streamingMipmapsMemoryBudget = c.E4.memoryBudgetMB;
            QualitySettings.streamingMipmapsMaxLevelReduction = Mathf.Max(0, c.E4.maxLevelReduction); QualitySettings.streamingMipmapsAddAllCameras = c.E4.addAllCameras;
            Debug.Log("[PerfMem] E4 streaming active=" + QualitySettings.streamingMipmapsActive + " budgetMB=" + QualitySettings.streamingMipmapsMemoryBudget);
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!On("E1") && !On("A2")) return;
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
        Destroy(gameObject);
    }
}
