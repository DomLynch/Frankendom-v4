using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// laneVIS2 v1, audit item 8 (STAGED for Assets/Scripts/Zones/ZoneSky.cs; NEVER COMPILED IN UNITY, written CPU-only; Roslyn-checked against the project assemblies only).
// What it is: a dark, slow HDRP CLOUD LAYER (the cheap one: a baked lat-long texture composited in the sky pass; NOT the Volumetric Clouds raymarch) under the black-sun eclipse sky, from DATA (zone_sky.json,
// StreamingAssets first, same DataPath rule as CampLights / ZoneLook). One global runtime Volume (priority 21, above ZoneLook's 20 and the baked Global 10) carries VisualEnvironment.cloudType = CloudLayer and the
// CloudLayer overrides; only opacity / tint / scroll speed vary per zone (they are render-time parameters, NOT part of HDRP's cloud bake hash, so a zone blend never re-bakes the cloud texture).
// Why a runtime volume: the baked HdrpScene path (hdrp_lighting_ultra.json "clouds") builds VolumetricClouds only, runs only in an Editor re-bake, and is enable=false today; Global.asset has no cloud component, so the existing
// RuntimeQuality "clouds" flag (it only toggles an existing VolumetricClouds component) has nothing to switch.
// Exposure: the cloud colour is tint x 2^exposureEV / 1.2 and the HDRI sky multiplier is the same function of HDRISky.exposure, so the cloud exposure is READ from the live HDRISky override (exposureFromHdri) plus a
// data offset: a tint of 0.1 then means "as bright as a sky texel of linear 0.1". The absolute match to the black-sun texture is NOT measured: tune tintScale / exposureOffsetEV on the first frame.
// Dark by default: absent file, enabled=false or -zonesky:off = this class does nothing. Switch: look in zone_sky.json "enabled"; same-exe A/B: -zonesky:off.
[Serializable] public class ZoneSkyRow { public string id = ""; public float zFrom = -100000f, zTo = 100000f; public float opacity = -999f, scrollSpeed = -999f; public float[] tint; }
[Serializable] public class ZoneSkyData
{
    public bool enabled = false, log = true, exposureFromHdri = true, lighting = true, upperHemisphereOnly = true, castShadows = false;
    public int waitFrames = 60, steps = 6, resolution = 1024; public float pollSeconds = 0.25f, blendM = 14f, smoothSeconds = 2f, rescanSeconds = 5f, priority = 21f;
    public float exposureEV = 0f, exposureOffsetEV = 0f, tintScale = 1f, altitude = 6000f, thickness = 0.5f, rotation = 0f, scrollOrientation = 0f;
    public string cloudMapPath = "";   // "" = HDRP's own DefaultCloudMap (runtime resource); else a Resources path of a lat-long RGBA texture (R channel used)
    public ZoneSkyRow defaults = new ZoneSkyRow(); public ZoneSkyRow[] rows = new ZoneSkyRow[0];
}

public sealed class ZoneSky : MonoBehaviour
{
    static readonly float Eps = 0.01f, MinSmooth = 0.05f, MinPoll = 0.02f;
    struct P { public Color tint; public float opacity, speed; }
    static ZoneSky inst; ZoneSkyData D; P[] rowP; P cur; bool curInit; float lastT, nextRescan; float hdriEv; bool hdriFound;
    Volume vol; VolumeProfile prof; VisualEnvironment ve; CloudLayer cl;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-zonesky:", StringComparison.OrdinalIgnoreCase) && (a.EndsWith(":off", StringComparison.OrdinalIgnoreCase) || a.EndsWith(":0"))) return;
        string txt = DataPath.ReadText("zone_sky.json"); if (txt == null) return;
        ZoneSkyData d; try { d = JsonUtility.FromJson<ZoneSkyData>(txt); } catch (Exception e) { Debug.LogWarning("[ZoneSky] zone_sky.json unreadable, no cloud layer: " + e.Message); return; }
        if (d == null || !d.enabled || d.rows == null || d.rows.Length == 0) return;
        var go = new GameObject("ZoneSky"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneSky>(); inst.D = d;
    }

    static float F(float v, float d) { return v <= -998f ? d : v; }
    static Color C(float[] a, Color d) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], 1f) : d; }
    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
    static P Lerp(P a, P b, float t) { P p; p.tint = Color.Lerp(a.tint, b.tint, t); p.opacity = Mathf.Lerp(a.opacity, b.opacity, t); p.speed = Mathf.Lerp(a.speed, b.speed, t); return p; }

    P Resolve(ZoneSkyRow r, P d) { P p; p.tint = C(r.tint, d.tint); p.opacity = F(r.opacity, d.opacity); p.speed = F(r.scrollSpeed, d.speed); return p; }

    int ZoneIndex(float z) { int n = D.rows.Length, i = 0; for (; i < n - 1; i++) if (z < D.rows[i].zTo) break; return i; }
    P Sample(float z)
    {
        int n = rowP.Length, i = ZoneIndex(z); float half = D.blendM * 0.5f;
        if (i < n - 1 && z > D.rows[i].zTo - half) return Lerp(rowP[i], rowP[i + 1], Smooth((z - (D.rows[i].zTo - half)) / Mathf.Max(Eps, D.blendM)));
        if (i > 0 && z < D.rows[i].zFrom + half) return Lerp(rowP[i - 1], rowP[i], Smooth((z - (D.rows[i].zFrom - half)) / Mathf.Max(Eps, D.blendM)));
        return rowP[i];
    }

    void Start() { StartCoroutine(Build()); }

    System.Collections.IEnumerator Build()
    {
        for (int i = 0; i < Mathf.Max(1, D.waitFrames); i++) yield return null;
        P def; def.tint = Color.white; def.opacity = 0f; def.speed = 0f; def = Resolve(D.defaults, def);
        rowP = new P[D.rows.Length]; for (int i = 0; i < rowP.Length; i++) rowP[i] = Resolve(D.rows[i], def);
        var g = new GameObject("ZoneSkyVolume"); g.transform.SetParent(transform, false);
        vol = g.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = D.priority; vol.weight = 0f;
        prof = ScriptableObject.CreateInstance<VolumeProfile>(); vol.sharedProfile = prof;
        ve = prof.Add<VisualEnvironment>(false); ve.cloudType.Override((int)CloudType.CloudLayer);   // only the cloud type: the sky type / ambient mode of the baked Global stay as they are
        cl = prof.Add<CloudLayer>(false);
        cl.layers.Override(CloudMapMode.Single); cl.upperHemisphereOnly.Override(D.upperHemisphereOnly);
        cl.resolution.Override((CloudResolution)Mathf.Clamp(D.resolution, (int)CloudResolution.CloudResolution256, (int)CloudResolution.CloudResolution2048));
        var m = cl.layerA; m.altitude.Override(D.altitude); m.thickness.Override(D.thickness); m.rotation.Override(D.rotation); m.lighting.Override(D.lighting); m.steps.Override(D.steps); m.castShadows.Override(D.castShadows);
        if (!string.IsNullOrEmpty(D.cloudMapPath)) { var tex = Resources.Load<Texture2D>(D.cloudMapPath); if (tex != null) m.cloudMap.Override(tex); else Debug.LogWarning("[ZoneSky] cloudMapPath '" + D.cloudMapPath + "' not found in Resources: using HDRP's default cloud map"); }
        m.scrollOrientation.overrideState = true; m.scrollOrientation.value = new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = D.scrollOrientation };
        m.scrollSpeed.overrideState = true; m.tint.overrideState = true; m.exposure.overrideState = true; cl.opacity.overrideState = true;
        ScanHdri(); Apply(Sample(PlayerZ()), true);
        vol.weight = 1f;
        if (D.log) Debug.Log("[ZoneSky] built: cloud layer, " + rowP.Length + " zone rows, hdri EV " + (hdriFound ? hdriEv.ToString("0.00") : "not found (using exposureEV)") + ", resolution " + D.resolution + ", altitude " + D.altitude);
    }

    float PlayerZ() { var p = GameLoop.Player; return p != null ? p.transform.position.z : 0f; }

    // read the exposure of the HDRISky the other volumes end up with (RuntimeQuality may override it per tier), so the cloud multiplier follows the sky multiplier
    void ScanHdri()
    {
        hdriFound = false; float best = float.MinValue;
        foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
        {
            if (v == vol || !v.isGlobal || v.profile == null) continue;
            if (v.priority >= best && v.profile.TryGet(out HDRISky hs) && hs.exposure.overrideState) { best = v.priority; hdriEv = hs.exposure.value; hdriFound = true; }
        }
    }

    void Apply(P p, bool snap)
    {
        float a = snap || !curInit ? 1f : 1f - Mathf.Exp(-Mathf.Max(0f, Time.unscaledTime - lastT) / Mathf.Max(MinSmooth, D.smoothSeconds));
        cur = curInit ? Lerp(cur, p, a) : p; curInit = true; lastT = Time.unscaledTime;
        var m = cl.layerA; Color t = cur.tint * D.tintScale; t.a = 1f;
        cl.opacity.value = cur.opacity; m.tint.value = t;
        m.exposure.value = (D.exposureFromHdri && hdriFound ? hdriEv : D.exposureEV) + D.exposureOffsetEV;
        m.scrollSpeed.value = new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = cur.speed };
    }

    float next;
    void Update()
    {
        if (cl == null || Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(MinPoll, D.pollSeconds);
        var p = GameLoop.Player; bool live = p != null && ZoneRuntime.Owns && ZoneSpan.Live;
        if (vol.weight > 0f != live) vol.weight = live ? 1f : 0f;
        if (!live) return;
        if (Time.unscaledTime >= nextRescan) { nextRescan = Time.unscaledTime + Mathf.Max(1f, D.rescanSeconds); ScanHdri(); }
        Apply(Sample(p.transform.position.z), false);
    }

    void OnDestroy() { if (prof != null) Destroy(prof); }
}
