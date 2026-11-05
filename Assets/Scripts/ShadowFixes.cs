using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// laneSHADOW v75: data-driven runtime shadow fixes. Assets/Data/shadow_fixes.json (StreamingAssets copy in the player) lists lights whose shadows are switched off and
// renderers whose shadowCastingMode is overridden, applied a few frames after the scene loads (no scene rebuild needed; doorway.json light.shadows is only read by the editor builder).
// The same file carries the "diag" block read by ShadowDiag (-shadowdiag). Skip everything with -noshadowfixes (A/B check).
public static class ShadowFixes
{
    [Serializable] public class LightRow { public string name; public bool shadows = true; public string updateMode; public string note; }   // shadows=false -> off; updateMode = EveryFrame | OnEnable | OnDemand (HDAdditionalLightData.shadowUpdateMode)
    [Serializable] public class RendRow { public string match; public string mode = "Off"; public string note; }
    [Serializable] public class Capture { public string id; public string[] lightsOff; public bool allPointSpotOff; public bool keyEveryFrame; public string note; }
    [Serializable] public class Diag
    {
        public float[] floorRect = { -6f, 6f, 4f, 24f };   // x0, x1, z0, z1 of the arena floor that counts (arena_limits.json is x -3.8..3.8, z 8..22; the margin is the visible apron)
        public float floorY = 0f;
        public int settleFrames = 120, topN = 30, maxOverlapRows = 200;
        public float[] enemyAnchor = { 0f, 0f, 17f }; public float[] axis = { 0f, 0f, 1f }; public float sepM = 2.5f;   // v66 wide pose: Dracula x 0 z 14.5, Pitborn 2.5 m in front
        public float[] screenRegion = { 0f, 0f, 1f, 0.55f };   // x0, y0, x1, y1 as screen fractions (y from the bottom): the floor part of the frame
        public float darkLuma = 0.02f, brightenDelta = 0.03f;
        public Capture[] captures;
    }
    [Serializable] public class Cfg { public bool enabled = true; public LightRow[] lights; public RendRow[] renderers; public Diag diag; }

    static Cfg cfg;
    public static Cfg Get()
    {
        if (cfg != null) return cfg;
        cfg = new Cfg();
        var txt = HudCfg.ReadData("shadow_fixes.json");
        if (txt != null) { try { var c = JsonUtility.FromJson<Cfg>(txt); if (c != null) cfg = c; } catch (Exception e) { Debug.LogWarning("[ShadowFixes] shadow_fixes.json parse failed: " + e.Message); } }
        if (cfg.diag == null) cfg.diag = new Diag();
        return cfg;
    }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }

    public static string PathOf(Transform t) { string p = t.name; while (t.parent != null) { t = t.parent; p = t.name + "/" + p; } return p; }

    /// <summary>Applies the lights and renderers lists. Returns a one-line summary.</summary>
    public static string Apply()
    {
        var c = Get(); if (!c.enabled) return "disabled in shadow_fixes.json";
        int nl = 0, nr = 0, nu = 0;
        if (c.lights != null)
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var row in c.lights)
                    if (row != null && !string.IsNullOrEmpty(row.name) && l.name == row.name)
                    {
                        var hd = l.GetComponent<HDAdditionalLightData>();
                        if (!row.shadows) { if (hd != null) hd.EnableShadows(false); l.shadows = LightShadows.None; nl++; }
                        if (!string.IsNullOrEmpty(row.updateMode) && hd != null)
                        {
                            try { hd.shadowUpdateMode = (ShadowUpdateMode)Enum.Parse(typeof(ShadowUpdateMode), row.updateMode, true); nu++; } catch { Debug.LogWarning("[ShadowFixes] bad updateMode '" + row.updateMode + "' for " + row.name); }
                        }
                    }
        if (c.renderers != null && c.renderers.Length > 0)
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string path = null;
                foreach (var row in c.renderers)
                {
                    if (row == null || string.IsNullOrEmpty(row.match)) continue;
                    if (path == null) path = PathOf(r.transform);
                    if (path.IndexOf(row.match, StringComparison.Ordinal) < 0) continue;
                    ShadowCastingMode m;
                    try { m = (ShadowCastingMode)Enum.Parse(typeof(ShadowCastingMode), row.mode, true); } catch { continue; }
                    if (r.shadowCastingMode != m) { r.shadowCastingMode = m; nr++; }
                }
            }
        return "lights shadows off=" + nl + " update modes set=" + nu + " renderers changed=" + nr;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Flag("-noshadowfixes") || Flag("-shadowdiag")) return;   // the diag applies its own states
        var go = new GameObject("ShadowFixes"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<ShadowFixesRunner>();
    }
}

public class ShadowFixesRunner : MonoBehaviour
{
    IEnumerator Start()
    {
        // twice: once after RuntimeQuality / scene setup has run, once after the fighters are spawned (roster swaps re-create renderers)
        for (int i = 0; i < 3; i++) yield return null;
        Debug.Log("[ShadowFixes] " + ShadowFixes.Apply());
        for (int i = 0; i < 60; i++) yield return null;
        Debug.Log("[ShadowFixes] second pass: " + ShadowFixes.Apply());
        Destroy(gameObject);
    }
}
