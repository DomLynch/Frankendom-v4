using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// LOOKOPT: look changes waiting for Dom's LOOK OK, each ONE switch in look_options.json (StreamingAssets, then Assets/Data). All defaults = off = today's look.
//   deathWash / combatCamCap / finisherBlood.wetDecal are read by one-line hooks (GameLoop.DrawEnd, CamFraming.Fit, GoreFx.Splash).
//   doorwaySpikeStrip / floorNormal are applied here at runtime after every scene load (nothing in the scene or the material assets is edited).
public static class LookOptions
{
    [Serializable] public class Wash { public bool on; public float[] tint = { 0.06f, 0.025f, 0.025f, 1f }; }
    [Serializable] public class Spike { public bool on; public string[] hideObjects = { "DoorwayEmberGlow" }; public float emberIntensity = 0f; /* // GOOSEBACKLOG2 v102: > 0 = dim the glow material _Intensity instead of hiding the object */ }
    [Serializable] public class Floor { public bool on; public float scale = 0.35f, detailNormalScale = -1f, smoothnessMax = -1f; /* // GOOSEBACKLOG2 v102: -1 = leave the material value */ public string[] materials = { "ground_paving_LOD0_plate_top", "ground_paving_LOD1_plate_top", "ground_paving_LOD2_plate_top" }; }
    [Serializable] public class Blood { public bool wetDecal; public float smoothness = 0.8f, depth = 0.12f; }
    [Serializable] public class Cap { public bool on; public float followM = 7.5f; }
    [Serializable] public class ReframeRow { public bool on; public float maxDistM = 6.5f, startSepM = 4f, spanSepM = 6f, maxPitchDeg = 4f, fovMaxDeg = 0f; }   // GOOSEBACKLOG2 v102: far-separation framing, Goose 08:30
    [Serializable] public class Door { public bool on; public float lightVolumetricDimmer = 0.5f, fogMeanFreePath = 5f; public string lightName = "DoorwayFlickerLight"; public string[] fogNames = { "DoorwayFog_Inside" }; }   // GOOSEBACKLOG v100
    [Serializable] public class Plate { public bool on; public float plateAlpha = 0f; }
    [Serializable] public class WoundD { public bool on; public float colorMul = 0.5f, sizeMul = 0.7f; public int affectNormal = -1, affectAO = -1; public float endAngleFade = -1f; /* // GOOSEBACKLOG2 v102: -1 = keep wounds.json */ }
    [Serializable] public class Vig { public bool on; public float intensity = 0.34f; }
    [Serializable] public class Root { public Door doorwayDepth = new Door(); public Plate endPlate = new Plate(); public WoundD woundMarks = new WoundD(); public Vig sceneVignette = new Vig(); public Wash deathWash = new Wash(); public Spike doorwaySpikeStrip = new Spike(); public Floor floorNormal = new Floor(); public Blood finisherBlood = new Blood(); public Cap combatCamCap = new Cap(); public ReframeRow camReframe = new ReframeRow(); /* // GOOSEBACKLOG2 v102 */ public PlugFix terrainPlug = new PlugFix(); /* // laneWHIRLFIX */ }

    [Serializable] public class PlugFix { public bool on; public string[] names = { "TerrainCentrePlug" }; public float dropM = 0.05f; }   // laneWHIRLFIX
    static Root cfg;
    public static Root Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Root();
        foreach (var p in new[] { System.IO.Path.Combine(Application.streamingAssetsPath, "look_options.json"), System.IO.Path.Combine(Application.dataPath, "Data/look_options.json") })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(DataPath.ReadFile(p)); if (r != null) cfg = r; } catch (Exception e) { Debug.LogWarning("[LookOptions] " + p + ": " + e.Message); }
            break;
        }
        if (cfg.deathWash == null) cfg.deathWash = new Wash();
        if (cfg.doorwaySpikeStrip == null) cfg.doorwaySpikeStrip = new Spike();
        if (cfg.floorNormal == null) cfg.floorNormal = new Floor();
        if (cfg.finisherBlood == null) cfg.finisherBlood = new Blood();
        if (cfg.combatCamCap == null) cfg.combatCamCap = new Cap();
        if (cfg.camReframe == null) cfg.camReframe = new ReframeRow();   // GOOSEBACKLOG2 v102
        if (cfg.doorwayDepth == null) cfg.doorwayDepth = new Door();   // GOOSEBACKLOG v100
        if (cfg.endPlate == null) cfg.endPlate = new Plate();
        if (cfg.woundMarks == null) cfg.woundMarks = new WoundD();
        if (cfg.sceneVignette == null) cfg.sceneVignette = new Vig();
        if (cfg.terrainPlug == null) cfg.terrainPlug = new PlugFix();   // laneWHIRLFIX
        return cfg;
    }

    /// <summary>End-screen vignette tint: the hud.json colour unless deathWash is on (defeat screen only).</summary>
    public static Color DeathTint(bool win, Color hud)
    {
        var w = Cfg().deathWash; if (win || !w.on || w.tint == null || w.tint.Length < 3) return hud;
        return new Color(w.tint[0], w.tint[1], w.tint[2], w.tint.Length > 3 ? w.tint[3] : 1f);
    }

    /// <summary>Max camera-to-look distance for the lock-on pull-back; 0 = no cap.</summary>
    /// <summary>// GOOSEBACKLOG2 v102: far-separation lock-cam framing (look_options.json camReframe); on=false returns the switch row untouched, callers test .on.</summary>
    public static ReframeRow Reframe() { return Cfg().camReframe; }
    public static float CamCapM() { var c = Cfg().combatCamCap; return c.on && c.followM > 0f ? c.followM : 0f; }

    /// <summary>End-screen backing plate alpha (hud.json plateAlpha unless endPlate is on; its hard top/bottom edge is the 'panel band').</summary>
    public static float PlateAlpha(float hud) { var p = Cfg().endPlate; return p.on ? p.plateAlpha : hud; }   // GOOSEBACKLOG v100
    /// <summary>Wound decal _BaseColor multiplier (1 = today).</summary>
    public static float WoundColorMul() { var w = Cfg().woundMarks; return w.on ? w.colorMul : 1f; }
    /// <summary>Wound decal length/width multiplier (1 = today).</summary>
    /// <summary>// GOOSEBACKLOG2 v102: decal Affect Normal / AO / end angle fade while woundMarks is on (-1 = wounds.json value). Goose 08:10: BaseColor only, angle fade 60..80.</summary>
    public static bool WoundAffectNormal(bool def) { var w = Cfg().woundMarks; return w.on && w.affectNormal >= 0 ? w.affectNormal > 0 : def; }
    public static bool WoundAffectAO(bool def) { var w = Cfg().woundMarks; return w.on && w.affectAO >= 0 ? w.affectAO > 0 : def; }
    public static float WoundEndAngle(float def) { var w = Cfg().woundMarks; return w.on && w.endAngleFade >= 0f ? w.endAngleFade : def; }
    public static float WoundSizeMul() { var w = Cfg().woundMarks; return w.on ? w.sizeMul : 1f; }

    static readonly Dictionary<string, Material> clones = new Dictionary<string, Material>();
    static readonly Dictionary<Transform, float> plugY0 = new Dictionary<Transform, float>();   // laneWHIRLFIX

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; clones.Clear(); plugY0.Clear(); /* laneWHIRLFIX */ }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = Cfg(); if (!c.doorwaySpikeStrip.on && !c.floorNormal.on && !c.doorwayDepth.on && !c.sceneVignette.on && !c.terrainPlug.on) return;   /* laneWHIRLFIX */   // GOOSEBACKLOG v100: + doorwayDepth, sceneVignette
        var go = new GameObject("LookOptionsApply"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Applier>();
    }

    // 5 frames after load (the world builders finish in Start), again at 2 s for late spawns; restarts after every scene load
    class Applier : MonoBehaviour
    {
        int frames; float t; bool second;
        void OnEnable() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnLoaded; }
        void OnDisable() { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnLoaded; }
        void OnLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) { frames = 0; t = 0f; second = false; }
        void Update()
        {
            frames++; t += Time.unscaledDeltaTime;
            if (frames == 5) ApplyAll();
            else if (!second && t > 2f) { second = true; ApplyAll(); }
        }
    }

    static void ApplyAll()
    {
        var c = Cfg();
        if (c.doorwaySpikeStrip.on && c.doorwaySpikeStrip.hideObjects != null)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]; if (t == null || !t.gameObject.scene.IsValid()) continue;   // scene objects only, no prefab assets
                for (int k = 0; k < c.doorwaySpikeStrip.hideObjects.Length; k++)
                    if (t.name == c.doorwaySpikeStrip.hideObjects[k] && c.doorwaySpikeStrip.emberIntensity > 0f)   // GOOSEBACKLOG2 v102: dim ember, no hide (MaterialPropertyBlock, idempotent)
                    {
                        var rr = t.GetComponentsInChildren<Renderer>(true); var mb = new MaterialPropertyBlock();
                        for (int q = 0; q < rr.Length; q++) { if (rr[q] == null) continue; rr[q].GetPropertyBlock(mb); mb.SetFloat("_Intensity", c.doorwaySpikeStrip.emberIntensity); rr[q].SetPropertyBlock(mb); }
                        Debug.Log("[LookOptions] doorway spike strip dimmed to " + c.doorwaySpikeStrip.emberIntensity.ToString("F2") + ": " + t.name);
                    }
                    else if (t.name == c.doorwaySpikeStrip.hideObjects[k] && t.gameObject.activeSelf) { t.gameObject.SetActive(false); Debug.Log("[LookOptions] doorway spike strip hidden: " + t.name); }
            }
        }
        if (c.terrainPlug.on && c.terrainPlug.names != null)   // laneWHIRLFIX: TerrainCentrePlug (HdrpGround.EnsureTerrainCentrePlug) is coplanar with the terrain cap (y 0), same material, other UV and no tangents: it z-fights under TAA jitter = the circling flicker at the feet. Sunk dropM under the cap it still fills a hole but can never win the depth test. Idempotent (absolute y from the first-seen y).
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i]; if (t == null || !t.gameObject.scene.IsValid() || Array.IndexOf(c.terrainPlug.names, t.name) < 0) continue;
                float y0; if (!plugY0.TryGetValue(t, out y0)) { y0 = t.position.y; plugY0[t] = y0; }
                float want = y0 - Mathf.Max(0f, c.terrainPlug.dropM); var p = t.position;
                if (Mathf.Abs(p.y - want) > 1e-4f) { t.position = new Vector3(p.x, want, p.z); Debug.Log("[LookOptions] terrain plug sunk to y " + want.ToString("F3") + ": " + t.name); }
            }
        }
        if (c.doorwayDepth.on)   // GOOSEBACKLOG v100: faint red haze behind the threshold = depth cue; only the light's volumetric dimmer and the inside fog density change
        {
            var d = c.doorwayDepth;
            var lights = UnityEngine.Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++) if (lights[i] != null && lights[i].name == d.lightName) { lights[i].volumetricDimmer = d.lightVolumetricDimmer; Debug.Log("[LookOptions] doorway light volumetricDimmer " + d.lightVolumetricDimmer.ToString("F2")); }
            var fogs = UnityEngine.Object.FindObjectsByType<LocalVolumetricFog>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < fogs.Length; i++) if (fogs[i] != null && d.fogNames != null && Array.IndexOf(d.fogNames, fogs[i].name) >= 0) { var fp = fogs[i].parameters; fp.meanFreePath = d.fogMeanFreePath; fogs[i].parameters = fp; Debug.Log("[LookOptions] doorway fog meanFreePath " + d.fogMeanFreePath.ToString("F1") + " on " + fogs[i].name); }
        }
        if (c.sceneVignette.on)   // GOOSEBACKLOG v100: hdrp_lighting_ultra.json post.vignette 0.48 is baked into the scene Volume at build; override it at runtime
        {
            var vols = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < vols.Length; i++) { var v = vols[i]; if (v == null || v.sharedProfile == null) continue; Vignette vg; if (v.profile.TryGet(out vg)) { vg.intensity.Override(c.sceneVignette.intensity); Debug.Log("[LookOptions] scene vignette " + c.sceneVignette.intensity.ToString("F2") + " on " + v.name); } }
        }
        if (c.floorNormal.on && c.floorNormal.materials != null)
        {
            var rs = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None); int n = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]; if (r == null) continue;
                var ms = r.sharedMaterials; bool ch = false;
                for (int j = 0; j < ms.Length; j++)
                {
                    var m = ms[j]; if (m == null || !m.HasProperty("_NormalScale") || Array.IndexOf(c.floorNormal.materials, Base(m.name)) < 0) continue;
                    Material cl;   // one private copy per material, so the asset (and the editor project) keeps its 0.75
                    if (!clones.TryGetValue(Base(m.name), out cl) || cl == null) { cl = new Material(m) { name = Base(m.name) + " (lookopt)" }; clones[Base(m.name)] = cl; }
                    if (Mathf.Abs(cl.GetFloat("_NormalScale") - c.floorNormal.scale) > 1e-4f) cl.SetFloat("_NormalScale", c.floorNormal.scale);
                    if (c.floorNormal.detailNormalScale >= 0f && cl.HasProperty("_DetailNormalScale")) cl.SetFloat("_DetailNormalScale", c.floorNormal.detailNormalScale);   // GOOSEBACKLOG2 v102
                    if (c.floorNormal.smoothnessMax >= 0f && cl.HasProperty("_SmoothnessRemapMax")) cl.SetFloat("_SmoothnessRemapMax", c.floorNormal.smoothnessMax);   // GOOSEBACKLOG2 v102
                    if (ms[j] != cl) { ms[j] = cl; ch = true; n++; }
                }
                if (ch) r.sharedMaterials = ms;
            }
            if (n > 0) Debug.Log("[LookOptions] floor normal scale " + c.floorNormal.scale.ToString("F2") + " on " + n + " material slots");
        }
    }

    static string Base(string n)
    {
        if (n == null) return "";
        if (n.EndsWith(" (Instance)")) return n.Substring(0, n.Length - 11);
        if (n.EndsWith(" (lookopt)")) return n.Substring(0, n.Length - 10);
        return n;
    }
}
