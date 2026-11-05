using System;
using System.Collections.Generic;
using UnityEngine;

// LOOKOPT: look changes waiting for Dom's LOOK OK, each ONE switch in look_options.json (StreamingAssets, then Assets/Data). All defaults = off = today's look.
//   deathWash / combatCamCap / finisherBlood.wetDecal are read by one-line hooks (GameLoop.DrawEnd, CamFraming.Fit, GoreFx.Splash).
//   doorwaySpikeStrip / floorNormal are applied here at runtime after every scene load (nothing in the scene or the material assets is edited).
public static class LookOptions
{
    [Serializable] public class Wash { public bool on; public float[] tint = { 0.06f, 0.025f, 0.025f, 1f }; }
    [Serializable] public class Spike { public bool on; public string[] hideObjects = { "DoorwayEmberGlow" }; }
    [Serializable] public class Floor { public bool on; public float scale = 0.35f; public string[] materials = { "ground_paving_LOD0_plate_top", "ground_paving_LOD1_plate_top", "ground_paving_LOD2_plate_top" }; }
    [Serializable] public class Blood { public bool wetDecal; public float smoothness = 0.8f, depth = 0.12f; }
    [Serializable] public class Cap { public bool on; public float followM = 7.5f; }
    [Serializable] public class Root { public Wash deathWash = new Wash(); public Spike doorwaySpikeStrip = new Spike(); public Floor floorNormal = new Floor(); public Blood finisherBlood = new Blood(); public Cap combatCamCap = new Cap(); }

    static Root cfg;
    public static Root Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Root();
        foreach (var p in new[] { System.IO.Path.Combine(Application.streamingAssetsPath, "look_options.json"), System.IO.Path.Combine(Application.dataPath, "Data/look_options.json") })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null) cfg = r; } catch (Exception e) { Debug.LogWarning("[LookOptions] " + p + ": " + e.Message); }
            break;
        }
        if (cfg.deathWash == null) cfg.deathWash = new Wash();
        if (cfg.doorwaySpikeStrip == null) cfg.doorwaySpikeStrip = new Spike();
        if (cfg.floorNormal == null) cfg.floorNormal = new Floor();
        if (cfg.finisherBlood == null) cfg.finisherBlood = new Blood();
        if (cfg.combatCamCap == null) cfg.combatCamCap = new Cap();
        return cfg;
    }

    /// <summary>End-screen vignette tint: the hud.json colour unless deathWash is on (defeat screen only).</summary>
    public static Color DeathTint(bool win, Color hud)
    {
        var w = Cfg().deathWash; if (win || !w.on || w.tint == null || w.tint.Length < 3) return hud;
        return new Color(w.tint[0], w.tint[1], w.tint[2], w.tint.Length > 3 ? w.tint[3] : 1f);
    }

    /// <summary>Max camera-to-look distance for the lock-on pull-back; 0 = no cap.</summary>
    public static float CamCapM() { var c = Cfg().combatCamCap; return c.on && c.followM > 0f ? c.followM : 0f; }

    static readonly Dictionary<string, Material> clones = new Dictionary<string, Material>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; clones.Clear(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var c = Cfg(); if (!c.doorwaySpikeStrip.on && !c.floorNormal.on) return;
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
                    if (t.name == c.doorwaySpikeStrip.hideObjects[k] && t.gameObject.activeSelf) { t.gameObject.SetActive(false); Debug.Log("[LookOptions] doorway spike strip hidden: " + t.name); }
            }
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
