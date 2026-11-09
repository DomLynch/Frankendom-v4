using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// laneVIS1 v1 (STAGED for Assets/Scripts/Zones/ZoneLook.cs; NEVER COMPILED, written CPU-only: unity-dev2 must compile it and fix typos).
// What it is: per-zone look from DATA (look_zones.json, StreamingAssets first, same DataPath rule as CampLights). One global runtime Volume (priority 20, created here, no scene edit) carries the per-zone
// grade (ColorAdjustments, WhiteBalance), bloom / vignette and the height-fog / fog-albedo / fog-tint overrides; the existing key / fill / moon-rim / rim lights are edited BY NAME (intensity, colour, aim) and
// local ground-haze boxes are built at runtime near the player. Everything is blended by the player's world z across the zone boundaries (zones.json limits chain), eased in time.
// Why a runtime Volume: HdrpScene.Run (the only writer of the baked Global.asset) is NOT part of the zone build chain, so baked-key edits need an Editor re-bake; this path needs no re-bake and can be tuned with a hot data release once this one file is in a player build.
// Cost: every parameter here is on a pass that already runs (colour grade LUT = one 3D LUT build whatever is enabled, bloom, vignette, volumetric fog); the lights and the volume are the existing ones; the haze boxes are extra local
// volumetric fog volumes (NOT free: measure). Nothing allocates per frame. Dark by default: absent file, enabled=false or -zonelook:off = this class does nothing.
// Switches (look_zones.json i2 i3 i5 i6 i9 i18) or the argument -zonelook:2,3,9 (item list; wins over the json) so one exe can render every before/after arm. -zonelook:off disables everything.
[Serializable] public class ZoneLookRow
{
    public string id = ""; public float zFrom = -100000f, zTo = 100000f;
    public float[] colorFilter; public float postExposure = -999f, temperature = -999f, tint = -999f, saturation = -999f, contrast = -999f, vignette = -999f, bloomIntensity = -999f, bloomThreshold = -999f;
    public float[] fogAlbedo, fogTint; public float fogMeanFreePath = -999f, fogMaxHeight = -999f;
    public float keyElevationDeg = -999f, keyYawOffsetDeg = -999f; public float[] keyColor;
    public float[] bounceColor; public float bounceLux = -999f, bouncePitchDeg = -999f, bounceYawDeg = -999f; public float[] moonRimColor;
}
[Serializable] public class ZoneLookHero { public float rimCharacterLux = 400f; }
[Serializable] public class ZoneLookHaze { public string id = ""; public string zone = ""; public float[] center, size, albedo, positiveFade, negativeFade; public float groundY = 0f, meanFreePath = 5f, activeRadiusM = 45f; public bool raycastGround = true; }
[Serializable] public class ZoneLookData
{
    public bool enabled = false, log = true, i2, i3, i5, i6, i9, i18;
    public float pollSeconds = 0.1f, blendM = 14f, smoothSeconds = 1.5f, keyGroundMul = 0.95f, keyMaxBoost = 1.8f, keyMinElevationDeg = 12f; public int waitFrames = 30;
    public ZoneLookHero hero = new ZoneLookHero(); public ZoneLookRow defaults = new ZoneLookRow(); public ZoneLookRow[] rows = new ZoneLookRow[0]; public ZoneLookHaze[] haze = new ZoneLookHaze[0];
}

public sealed class ZoneLook : MonoBehaviour
{
    struct P { public Color filter, fogAlbedo, fogTint, keyColor, bounceColor, moonRim; public float postExp, temp, tint, sat, con, vig, bloomI, bloomT, mfp, top, keyElev, keyYaw, bounceLux, bPitch, bYaw; }
    class LRef { public Light l; public float baseLux, basePitch, baseYaw, baseRoll; public Color baseCol; }

    static ZoneLook inst; ZoneLookData D; P[] rowP; P defP, cur; bool curInit; float lastT, nextHaze, nextLog, nextFind;
    Volume vol; VolumeProfile prof; ColorAdjustments ca; WhiteBalance wb; Vignette vg; Bloom bl; Fog fg;
    LRef key = new LRef(), fill = new LRef(), moon = new LRef(), rim = new LRef(); bool lightsReady, rimDone;
    readonly List<GameObject> hazeGo = new List<GameObject>(); readonly List<ZoneLookHaze> hazeRow = new List<ZoneLookHaze>(); readonly List<float> hazeY = new List<float>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        string only = null;
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-zonelook:", StringComparison.OrdinalIgnoreCase)) only = a.Substring(10).Trim();
        if (only == "off" || only == "0") return;
        string txt = DataPath.ReadText("look_zones.json"); if (txt == null) return;
        ZoneLookData d; try { d = JsonUtility.FromJson<ZoneLookData>(txt); } catch (Exception e) { Debug.LogWarning("[ZoneLook] look_zones.json unreadable, no zone look: " + e.Message); return; }
        if (d == null || !d.enabled || d.rows == null || d.rows.Length == 0) return;
        if (!string.IsNullOrEmpty(only))   // -zonelook:2,3,9 = exactly these items, whatever the json switches say
        {
            var s = "," + only + ","; d.i2 = s.Contains(",2,"); d.i3 = s.Contains(",3,"); d.i5 = s.Contains(",5,"); d.i6 = s.Contains(",6,"); d.i9 = s.Contains(",9,"); d.i18 = s.Contains(",18,");
        }
        var go = new GameObject("ZoneLook"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneLook>(); inst.D = d;
    }

    static Color C(float[] a, Color d) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], 1f) : d; }
    static float F(float v, float d) { return v <= -998f ? d : v; }
    static float Pitch(float x) { x = Mathf.Repeat(x, 360f); return x > 180f ? x - 360f : x; }

    P Resolve(ZoneLookRow r, P d)
    {
        P p; p.filter = C(r.colorFilter, d.filter); p.postExp = F(r.postExposure, d.postExp); p.temp = F(r.temperature, d.temp); p.tint = F(r.tint, d.tint); p.sat = F(r.saturation, d.sat); p.con = F(r.contrast, d.con);
        p.vig = F(r.vignette, d.vig); p.bloomI = F(r.bloomIntensity, d.bloomI); p.bloomT = F(r.bloomThreshold, d.bloomT);
        p.fogAlbedo = C(r.fogAlbedo, d.fogAlbedo); p.fogTint = C(r.fogTint, d.fogTint); p.mfp = F(r.fogMeanFreePath, d.mfp); p.top = F(r.fogMaxHeight, d.top);
        p.keyElev = F(r.keyElevationDeg, d.keyElev); p.keyYaw = F(r.keyYawOffsetDeg, d.keyYaw); p.keyColor = C(r.keyColor, d.keyColor);
        p.bounceColor = C(r.bounceColor, d.bounceColor); p.bounceLux = F(r.bounceLux, d.bounceLux); p.bPitch = F(r.bouncePitchDeg, d.bPitch); p.bYaw = F(r.bounceYawDeg, d.bYaw); p.moonRim = C(r.moonRimColor, d.moonRim);
        return p;
    }
    P Neutral()
    {
        P p = new P(); p.filter = Color.white; p.fogAlbedo = new Color(0.55f, 0.62f, 0.72f); p.fogTint = Color.white; p.keyColor = Color.white; p.bounceColor = Color.white; p.moonRim = Color.white;
        p.con = 33f; p.sat = -30f; p.vig = 0.48f; p.bloomI = 0.22f; p.bloomT = 0.92f; p.mfp = 75f; p.top = 25f; p.keyElev = -1f; p.keyYaw = 0f; p.bounceLux = 900f; p.bPitch = -999f; p.bYaw = -999f; return p;
    }
    // concrete values for the "leave as authored" sentinels once the lights are known
    P Concrete(P p)
    {
        if (p.keyElev < 0f) p.keyElev = key.l != null ? key.basePitch : 45f;
        if (p.bPitch <= -998f) p.bPitch = fill.l != null ? fill.basePitch : 35f;
        if (p.bYaw <= -998f) p.bYaw = fill.l != null ? fill.baseYaw : 15f;
        return p;
    }
    static P Lerp(P a, P b, float t)
    {
        P p;
        p.filter = Color.Lerp(a.filter, b.filter, t); p.fogAlbedo = Color.Lerp(a.fogAlbedo, b.fogAlbedo, t); p.fogTint = Color.Lerp(a.fogTint, b.fogTint, t); p.keyColor = Color.Lerp(a.keyColor, b.keyColor, t);
        p.bounceColor = Color.Lerp(a.bounceColor, b.bounceColor, t); p.moonRim = Color.Lerp(a.moonRim, b.moonRim, t);
        p.postExp = Mathf.Lerp(a.postExp, b.postExp, t); p.temp = Mathf.Lerp(a.temp, b.temp, t); p.tint = Mathf.Lerp(a.tint, b.tint, t); p.sat = Mathf.Lerp(a.sat, b.sat, t); p.con = Mathf.Lerp(a.con, b.con, t);
        p.vig = Mathf.Lerp(a.vig, b.vig, t); p.bloomI = Mathf.Lerp(a.bloomI, b.bloomI, t); p.bloomT = Mathf.Lerp(a.bloomT, b.bloomT, t); p.mfp = Mathf.Lerp(a.mfp, b.mfp, t); p.top = Mathf.Lerp(a.top, b.top, t);
        p.keyElev = Mathf.Lerp(a.keyElev, b.keyElev, t); p.keyYaw = Mathf.Lerp(a.keyYaw, b.keyYaw, t); p.bounceLux = Mathf.Lerp(a.bounceLux, b.bounceLux, t);
        p.bPitch = Mathf.Lerp(a.bPitch, b.bPitch, t); p.bYaw = Mathf.LerpAngle(a.bYaw, b.bYaw, t);
        return p;
    }
    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

    void Rebuild()
    {
        defP = Concrete(Resolve(D.defaults, Neutral()));
        rowP = new P[D.rows.Length];
        for (int i = 0; i < rowP.Length; i++) rowP[i] = Concrete(Resolve(D.rows[i], defP));
    }

    int ZoneIndex(float z) { int n = D.rows.Length, i = 0; for (; i < n - 1; i++) if (z < D.rows[i].zTo) break; return i; }
    P Sample(float z)
    {
        int n = rowP.Length, i = ZoneIndex(z); float half = D.blendM * 0.5f;
        if (i < n - 1 && z > D.rows[i].zTo - half) return Lerp(rowP[i], rowP[i + 1], Smooth((z - (D.rows[i].zTo - half)) / Mathf.Max(0.01f, D.blendM)));
        if (i > 0 && z < D.rows[i].zFrom + half) return Lerp(rowP[i - 1], rowP[i], Smooth((z - (D.rows[i].zFrom - half)) / Mathf.Max(0.01f, D.blendM)));
        return rowP[i];
    }

    void Start() { StartCoroutine(Build()); }

    System.Collections.IEnumerator Build()
    {
        for (int i = 0; i < Mathf.Max(1, D.waitFrames); i++) yield return null;
        // the one runtime Volume. Parameters are only OVERRIDDEN for the items that are on, so an item that is off leaves the baked Global.asset value alone.
        var vg0 = new GameObject("ZoneLookVolume"); vg0.transform.SetParent(transform, false);
        vol = vg0.AddComponent<Volume>(); vol.isGlobal = true; vol.priority = 20f; vol.weight = 0f;
        prof = ScriptableObject.CreateInstance<VolumeProfile>(); vol.sharedProfile = prof;
        ca = prof.Add<ColorAdjustments>(false); wb = prof.Add<WhiteBalance>(false); vg = prof.Add<Vignette>(false); bl = prof.Add<Bloom>(false); fg = prof.Add<Fog>(false);
        ca.colorFilter.overrideState = D.i2; ca.postExposure.overrideState = D.i2; ca.saturation.overrideState = D.i2; ca.contrast.overrideState = D.i2; wb.temperature.overrideState = D.i2; wb.tint.overrideState = D.i2;
        vg.intensity.overrideState = D.i9; bl.intensity.overrideState = D.i9; bl.threshold.overrideState = D.i9;
        fg.albedo.overrideState = D.i5; fg.tint.overrideState = D.i5; fg.meanFreePath.overrideState = D.i5; fg.maximumHeight.overrideState = D.i5;
        FindLights(); Rebuild();
        foreach (var h in D.haze)
        {
            if (h == null || h.center == null || h.center.Length < 3 || h.size == null || h.size.Length < 3) continue;
            var g = new GameObject("ZoneLookHaze_" + h.id); g.SetActive(false); g.transform.SetParent(transform, false); g.transform.position = new Vector3(h.center[0], h.groundY, h.center[2]);
            var lv = g.AddComponent<LocalVolumetricFog>();
            lv.parameters.size = new Vector3(h.size[0], h.size[1], h.size[2]); lv.parameters.albedo = C(h.albedo, new Color(0.10f, 0.10f, 0.12f)); lv.parameters.meanFreePath = h.meanFreePath;
            lv.parameters.positiveFade = h.positiveFade != null && h.positiveFade.Length >= 3 ? new Vector3(h.positiveFade[0], h.positiveFade[1], h.positiveFade[2]) : new Vector3(0.35f, 1f, 0.35f);
            lv.parameters.negativeFade = h.negativeFade != null && h.negativeFade.Length >= 3 ? new Vector3(h.negativeFade[0], h.negativeFade[1], h.negativeFade[2]) : new Vector3(0.35f, 0f, 0.35f);
            hazeGo.Add(g); hazeRow.Add(h); hazeY.Add(h.groundY);
        }
        if (D.log) Debug.Log("[ZoneLook] built: items i2=" + D.i2 + " i3=" + D.i3 + " i5=" + D.i5 + " i6=" + D.i6 + " i9=" + D.i9 + " i18=" + D.i18 + ", " + rowP.Length + " zone rows, " + hazeGo.Count + " haze boxes; key base pitch " + (key.l != null ? key.basePitch.ToString("F1") : "not found") + " yaw " + (key.l != null ? key.baseYaw.ToString("F1") : "-") + " lux " + (key.l != null ? key.baseLux.ToString("F0") : "-"));
    }

    static void Grab(LRef r, string name, Light[] all)
    {
        r.l = null;
        for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].name == name) { r.l = all[i]; break; }
        if (r.l == null) return;
        var e = r.l.transform.eulerAngles; r.basePitch = Pitch(e.x); r.baseYaw = e.y; r.baseRoll = e.z; r.baseLux = r.l.intensity; r.baseCol = r.l.color;
    }
    void FindLights()
    {
        var all = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Grab(key, "Key_ColdSun", all); Grab(fill, "FillCool", all); Grab(moon, "MoonRim", all); Grab(rim, "Rim_Character", all);
        lightsReady = key.l != null || fill.l != null; rimDone = false;
        if (D.log && !lightsReady) Debug.LogWarning("[ZoneLook] Key_ColdSun / FillCool not found in the scene: light edits skipped");
    }

    float GroundAt(Transform pt, ZoneLookHaze h, float last)
    {
        RaycastHit hit;
        if (h.raycastGround && pt != null && Physics.Raycast(pt.position + Vector3.up * 1.5f, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore)) return hit.point.y;   // the arena floor under the player (stamped flat)
        return last;
    }

    void Update()
    {
        if (vol == null || rowP == null) return;
        var p = GameLoop.Player; bool live = p != null && ZoneRuntime.Owns && ZoneSpan.Live;
        float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
        if (!live) { vol.weight = Mathf.MoveTowards(vol.weight, 0f, dt / 1.0f); SetHaze(false, null); return; }
        if (Time.unscaledTime >= nextFind && (key.l == null || fill.l == null)) { nextFind = Time.unscaledTime + 5f; FindLights(); Rebuild(); }
        float z = p.transform.position.z;
        P tgt = Sample(z);
        if (!curInit) { cur = tgt; curInit = true; }
        else cur = Lerp(cur, tgt, 1f - Mathf.Exp(-dt / Mathf.Max(0.05f, D.smoothSeconds)));
        vol.weight = Mathf.MoveTowards(vol.weight, 1f, dt / 1.0f);

        if (D.i2) { ca.colorFilter.value = cur.filter; ca.postExposure.value = cur.postExp; ca.saturation.value = cur.sat; ca.contrast.value = cur.con; wb.temperature.value = cur.temp; wb.tint.value = cur.tint; }
        if (D.i9) { vg.intensity.value = cur.vig; bl.intensity.value = cur.bloomI; bl.threshold.value = cur.bloomT; }
        if (D.i5) { fg.albedo.value = cur.fogAlbedo; fg.tint.value = cur.fogTint; fg.meanFreePath.value = cur.mfp; fg.maximumHeight.value = cur.top; }
        if (D.i3 && lightsReady) ApplyLights(cur);
        if (D.i18 && !rimDone && rim.l != null) { rimDone = true; if (rim.l.lightUnit == UnityEngine.Rendering.LightUnit.Lux) rim.l.intensity = D.hero.rimCharacterLux; else Debug.LogWarning("[ZoneLook] Rim_Character unit is " + rim.l.lightUnit + ", not Lux: item 18 skipped"); }
        if (Time.unscaledTime >= nextHaze) { nextHaze = Time.unscaledTime + Mathf.Max(0.05f, D.pollSeconds * 5f); SetHaze(D.i6, p.transform); }
        if (D.log && Time.unscaledTime >= nextLog)
        {
            nextLog = Time.unscaledTime + 10f; var zi = ZoneIndex(z);
            Debug.Log("[ZoneLook] z=" + z.ToString("F1") + " zone=" + D.rows[zi].id + " sat=" + cur.sat.ToString("F1") + " filter=" + cur.filter + " fogTop=" + cur.top.ToString("F0") + " mfp=" + cur.mfp.ToString("F1") + " keyElev=" + cur.keyElev.ToString("F1") + " keyLux=" + (key.l != null ? key.l.intensity.ToString("F0") : "-"));
        }
    }

    void ApplyLights(P c)
    {
        if (key.l != null)
        {
            float elev = Mathf.Max(D.keyMinElevationDeg, c.keyElev);
            float boost = Mathf.Clamp(Mathf.Sin(Mathf.Max(1f, key.basePitch) * Mathf.Deg2Rad) / Mathf.Sin(elev * Mathf.Deg2Rad) * D.keyGroundMul, 0.5f, D.keyMaxBoost);   // keeps the lux on the GROUND at keyGroundMul of today (capped), walls facing the sun get brighter
            key.l.transform.rotation = Quaternion.Euler(elev, key.baseYaw + c.keyYaw, key.baseRoll);
            key.l.intensity = key.baseLux * boost; key.l.color = c.keyColor;
        }
        if (fill.l != null) { fill.l.transform.rotation = Quaternion.Euler(c.bPitch, c.bYaw, 0f); fill.l.intensity = c.bounceLux; fill.l.color = c.bounceColor; }
        if (moon.l != null) moon.l.color = c.moonRim;
    }

    void SetHaze(bool want, Transform pt)
    {
        for (int i = 0; i < hazeGo.Count; i++)
        {
            var g = hazeGo[i]; if (g == null) continue;
            var h = hazeRow[i]; bool on = false;
            if (want && pt != null)
            {
                var c = new Vector2(h.center[0], h.center[2]); var q = new Vector2(pt.position.x, pt.position.z); on = (c - q).sqrMagnitude <= h.activeRadiusM * h.activeRadiusM;
                if (on) { float y = GroundAt(pt, h, hazeY[i]); hazeY[i] = y; g.transform.position = new Vector3(h.center[0], y, h.center[2]); }
            }
            if (g.activeSelf != on) g.SetActive(on);
        }
    }
}
