using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

// V11 v1 (STAGED, UNCOMPILED, NEW FILE, copy to Assets/Scripts/Zones/AccentLights.cs only when the Unity dev applies it): per-zone complementary light ACCENTS.
// Reads accent_lights.json (DataPath, StreamingAssets first): spot (or point) lights hung on existing props, soft pool cookie (StreamingAssets/accent_pool_cookie.png, loaded at runtime, no import settings needed),
// colour temperature + candela in HDRP physical units, light-layer mask that excludes the player/enemy rim layers. NON-SHADOW by default; at most maxShadowLive shadowed lights live at once and only when allowShadow
// (and -accent:2) is set. A row is on only while the zones are live, the player is within activeRadiusM, and it is one of the maxLive nearest rows (the fps cap). Dark: -accent:0 or enabled=false or no file = nothing.
// Args: -accent:0 off | -accent:1 on (no shadow) | -accent:2 on + shadow candidates. Same structure as CampLights.cs, which stays untouched.
[Serializable] public class AccentRow { public string name = ""; public string zone = ""; public string type = "spot"; public float[] pos; public float aimPitchDeg = 90f, aimYawDeg = 0f, kelvin = 2200f, candela = 8000f, range = 10f, spotAngleDeg = 85f, innerPct = 45f, volumetricDimmer = 0f, activeRadiusM = 34f; public string cookie = ""; public int layerMask = 65439; public bool shadow = false; public FurnaceFlicker.Noise flicker; }
[Serializable] public class AccentData { public bool enabled = false, log = true, allowShadow = false; public int waitFrames = 20, shadowResolution = 1024, maxLive = 6, maxShadowLive = 1; public float pollSeconds = 0.25f; public AccentRow[] rows = new AccentRow[0]; }

public sealed class AccentLights : MonoBehaviour
{
    static AccentLights inst; AccentData D; int mode = 1; Texture2D cookieTex;
    readonly List<GameObject> made = new List<GameObject>(); readonly List<AccentRow> rowOf = new List<AccentRow>(); readonly List<int> order = new List<int>(); float next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        int m = 1; foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-accent:")) int.TryParse(a.Substring(8), out m);
        if (m <= 0) return;
        string txt = DataPath.ReadText("accent_lights.json"); if (txt == null) return;
        AccentData d; try { d = JsonUtility.FromJson<AccentData>(txt); } catch (Exception e) { Debug.LogWarning("[AccentLights] accent_lights.json unreadable, no accents: " + e.Message); return; }
        if (d == null || !d.enabled || d.rows == null || d.rows.Length == 0) return;
        var go = new GameObject("AccentLights"); DontDestroyOnLoad(go); inst = go.AddComponent<AccentLights>(); inst.D = d; inst.mode = m;
    }
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void Start() { StartCoroutine(Build()); }
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { Clear(); StartCoroutine(Build()); } }
    void Clear() { foreach (var g in made) if (g != null) Destroy(g); made.Clear(); rowOf.Clear(); }

    Texture2D Cookie(string n)
    {
        if (string.IsNullOrEmpty(n)) return null; if (cookieTex != null) return cookieTex;
        string p = Path.Combine(Application.streamingAssetsPath, n + ".png"); if (!File.Exists(p)) return null;
        cookieTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp }; cookieTex.LoadImage(File.ReadAllBytes(p), false); return cookieTex;
    }

    System.Collections.IEnumerator Build()
    {
        for (int i = 0; i < Mathf.Max(1, D.waitFrames); i++) yield return null;
        bool shadowOk = D.allowShadow && mode >= 2;
        foreach (var r in D.rows)
        {
            if (r == null || r.pos == null || r.pos.Length < 3) continue;
            var g = new GameObject(string.IsNullOrEmpty(r.name) ? "AccentLight" : r.name); g.SetActive(false); g.transform.SetParent(transform, false);
            g.transform.position = new Vector3(r.pos[0], r.pos[1], r.pos[2]); g.transform.rotation = Quaternion.Euler(r.aimPitchDeg, r.aimYawDeg, 0f);
            var l = g.AddComponent<Light>(); var hd = g.AddComponent<HDAdditionalLightData>();
            bool spot = r.type != "point";
            l.type = spot ? LightType.Spot : LightType.Point; l.lightUnit = UnityEngine.Rendering.LightUnit.Candela; l.intensity = r.candela; l.range = r.range;
            if (spot) { l.spotAngle = r.spotAngleDeg; l.innerSpotAngle = r.spotAngleDeg * Mathf.Clamp01(r.innerPct / 100f); var c = Cookie(r.cookie); if (c != null) hd.SetCookie(c); }
            l.useColorTemperature = false; l.color = Mathf.CorrelatedColorTemperatureToRGB(r.kelvin);
            bool sh = shadowOk && r.shadow; l.shadows = sh ? LightShadows.Soft : LightShadows.None; hd.EnableShadows(sh); if (sh) hd.SetShadowResolution(D.shadowResolution);
            hd.volumetricDimmer = r.volumetricDimmer; hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)(uint)Mathf.Max(1, r.layerMask);
            if (r.flicker.amplitude > 0f)   /* unity-build: Noise is a struct (never null), compile fix */  { var fl = g.AddComponent<AtmosFlicker>(); fl.target = l; fl.noise = r.flicker; }
            made.Add(g); rowOf.Add(r);
        }
        if (D.log) Debug.Log("[AccentLights] mode " + mode + ": " + made.Count + " accent light(s) built from accent_lights.json (maxLive " + D.maxLive + ", shadows " + (shadowOk ? "ON" : "off") + ")");
    }

    void Update()
    {
        if (made.Count == 0 || Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(0.05f, D.pollSeconds);
        var p = GameLoop.Player; bool live = p != null && ZoneRuntime.Owns && ZoneSpan.Live;
        order.Clear(); for (int i = 0; i < made.Count; i++) if (made[i] != null) order.Add(i);
        Vector3 pp = live ? p.transform.position : Vector3.zero;
        order.Sort((a, b) => (made[a].transform.position - pp).sqrMagnitude.CompareTo((made[b].transform.position - pp).sqrMagnitude));
        int on = 0, shOn = 0;
        foreach (int i in order)
        {
            var g = made[i]; var r = rowOf[i];
            bool want = live && on < D.maxLive && (pp - g.transform.position).sqrMagnitude <= r.activeRadiusM * r.activeRadiusM;
            if (want && r.shadow) { var lt = g.GetComponent<Light>(); if (lt != null && lt.shadows != LightShadows.None) { if (shOn >= D.maxShadowLive) want = false; else shOn++; } }
            if (want) on++;
            if (g.activeSelf != want) g.SetActive(want);
        }
    }
}
