using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

// CAMPFIX v1: the camp trader (Arissa) stood under the stall awning with no light of her own: the awning shadows the key sun, and the fill / rim lights of the scene only reach renderers that carry their light-layer bit
// (HdrpFixes sets those on scene renderers; a runtime glb NPC has the Default layer only). This reads camp_lights.json and builds NON-SHADOW point lights from data, in world metres, with a light-layer mask that
// reaches every layer (layerMask 65535, so it also lights the player's rim-layer renderers). A row is switched on only while the zones are live and the player is within activeRadiusM of it, so it costs nothing anywhere else.
// Dark: only with the zones on (ZoneSave.Active), only when camp_lights.json enabled (default false, absent file = nothing). The scene, the other lights and every shadow setting are untouched.
[Serializable] public class CampLightRow { public string name = ""; public float[] pos; public float kelvin = 2200f, candela = 3500f, range = 5f, volumetricDimmer = 0f, activeRadiusM = 16f; public int layerMask = 65535; public FurnaceFlicker.Noise flicker; }
[Serializable] public class CampLightData { public bool enabled = false, log = true; public int waitFrames = 20; public float pollSeconds = 0.25f; public CampLightRow[] rows = new CampLightRow[0]; }

public sealed partial class CampLights : MonoBehaviour
{
    static CampLights inst; CampLightData D; readonly List<GameObject> made = new List<GameObject>(); readonly List<CampLightRow> rowOf = new List<CampLightRow>(); float next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        string txt = DataPath.ReadText("camp_lights.json"); if (txt == null) return;
        CampLightData d; try { d = JsonUtility.FromJson<CampLightData>(txt); } catch (Exception e) { Debug.LogWarning("[CampLights] camp_lights.json unreadable, no camp lights: " + e.Message); return; }
        if (d == null || !d.enabled || d.rows == null || d.rows.Length == 0) return;
        var go = new GameObject("CampLights"); DontDestroyOnLoad(go); inst = go.AddComponent<CampLights>(); inst.D = d;
    }
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void Start() { StartCoroutine(Build()); }
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { Clear(); StartCoroutine(Build()); } }
    void Clear() { foreach (var g in made) if (g != null) Destroy(g); made.Clear(); rowOf.Clear(); }

    System.Collections.IEnumerator Build()
    {
        for (int i = 0; i < Mathf.Max(1, D.waitFrames); i++) yield return null;
        foreach (var r in D.rows)
        {
            if (r == null || r.pos == null || r.pos.Length < 3) continue;
            var g = new GameObject(string.IsNullOrEmpty(r.name) ? "CampLight" : r.name); g.SetActive(false); g.transform.SetParent(transform, false); g.transform.position = new Vector3(r.pos[0], r.pos[1], r.pos[2]);
            var l = g.AddComponent<Light>(); var hd = g.AddComponent<HDAdditionalLightData>();
            l.type = LightType.Point; l.lightUnit = UnityEngine.Rendering.LightUnit.Candela; l.intensity = r.candela; l.range = r.range;
            l.useColorTemperature = false; l.color = Mathf.CorrelatedColorTemperatureToRGB(r.kelvin);
            l.shadows = LightShadows.None; hd.EnableShadows(false); hd.volumetricDimmer = r.volumetricDimmer;
            hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)(uint)Mathf.Max(1, r.layerMask);
            var fl = g.AddComponent<AtmosFlicker>(); fl.target = l; fl.noise = r.flicker;   // inactive object: Awake runs on SetActive, after the noise is set
            made.Add(g); rowOf.Add(r);
        }
        if (D.log) Debug.Log("[CampLights] " + made.Count + " non-shadow point light(s) built from camp_lights.json (on only near the camp, zones live)");
    }

    void Update()
    {
        if (made.Count == 0 || Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(0.05f, D.pollSeconds);
        var p = GameLoop.Player; bool live = p != null && ZoneRuntime.Owns && ZoneSpan.Live;
        for (int i = 0; i < made.Count; i++)
        {
            var g = made[i]; if (g == null) continue;
            bool want = live && (p.transform.position - g.transform.position).sqrMagnitude <= rowOf[i].activeRadiusM * rowOf[i].activeRadiusM;
            if (g.activeSelf != want) g.SetActive(want);
        }
    }
}
