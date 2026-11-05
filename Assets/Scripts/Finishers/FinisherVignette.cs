using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// V63G: the finisher's own vignette lever + the guarantee that it is GONE after the scene.
//
// Finding (V63G): the finisher code never adds a vignette. The dark corners seen on every finisher frame (and on the gameplay frames) are the SCENE's global Volume
// (hdrp_lighting_ultra.json post.vignette 0.48 -> HdrpScene.cs -> Vignette override in the baked scene profile). This file adds a finisher-owned override on top of it:
//   rt.look.vignette.slowIntensity   vignette intensity WHILE time is slowed (FinisherTime.Slowed); < 0 = no override at all (the scene value, nothing is created)
//   fadeInS / fadeOutS               blend toward / away from slowIntensity when the slow-mo starts / ends (unscaled seconds)
//   releaseFadeS                     after RTFinisher.End (FinisherCamera.End) the override fades to weight 0 over this long and the Volume + profile are DESTROYED
//   maxLifeS                         hard cap: the override is removed after this long whatever happens
// The override is ONE global Volume (priority 10001) whose WEIGHT is animated (HDRP lerps scene value -> override by weight), so weight 0 = exactly the scene value. Driven by its own
// component on UNSCALED time (the camera rig is disabled after the scene; slow-mo does not slow the fade).
// Verify() is the -finisherauto check: after the scene the effective vignette (and the Fixed exposure) read from a FRESH Volume stack must equal the pre-scene value, and no Finisher* Volume may be left.
[Serializable]
public class FinisherVignetteCfg
{
    public bool enabled = true;
    public float slowIntensity = -1f;       // vignette intensity during slow-mo; < 0 = leave the scene's vignette alone (no Volume is created)
    public float fadeInS = 0.12f, fadeOutS = 0.5f;
    public float releaseFadeS = 0.25f;
    public float maxLifeS = 40f;
}

public sealed class FinisherVignette : MonoBehaviour
{
    public static float Baseline = float.NaN, ExposureBaseline = float.NaN;   // effective values read from a fresh stack at Begin, before any finisher Volume exists
    static FinisherVignette inst;
    public static bool Alive { get { return inst != null; } }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; Baseline = ExposureBaseline = float.NaN; }

    Volume vol; VolumeProfile prof; float slowW, relW = 1f, life; bool releasing;

    static FinisherVignetteCfg Cfg()
    {
        var f = FinisherConfig.Get(); var l = f != null && f.rt != null ? f.rt.look : null;
        return l != null && l.vignette != null ? l.vignette : new FinisherVignetteCfg();
    }

    // ---------------- public API (FinisherCamera.Begin / End) ----------------
    public static void Begin()
    {
        try
        {
            if (inst != null) { inst.releasing = false; inst.relW = 1f; inst.life = 0f; return; }   // a second Begin without End: keep the one override
            Baseline = ReadIntensity(); ExposureBaseline = ReadFixedExposure();
            var c = Cfg(); if (!c.enabled || c.slowIntensity < 0f) return;
            var go = new GameObject("FinisherVignette"); var d = go.AddComponent<FinisherVignette>();
            d.prof = ScriptableObject.CreateInstance<VolumeProfile>();
            var vg = d.prof.Add<Vignette>(true); vg.intensity.Override(Mathf.Clamp01(c.slowIntensity)); vg.active = true;
            d.vol = go.AddComponent<Volume>(); d.vol.isGlobal = true; d.vol.priority = 10001f; d.vol.weight = 0f; d.vol.sharedProfile = d.prof;
            inst = d;
        }
        catch (Exception e) { Debug.LogWarning("[FinisherVignette] begin: " + e.Message); }
    }

    /// <summary>The scene is over: fade the override out and remove it (also called from FinisherCamera.OnDestroy).</summary>
    public static void End() { if (inst != null) inst.releasing = true; }

    void Update()
    {
        float dt = Time.unscaledDeltaTime; life += dt; var c = Cfg();
        float target = (!releasing && FinisherTime.Slowed) ? 1f : 0f;
        slowW = Mathf.MoveTowards(slowW, target, dt / Mathf.Max(0.01f, target > slowW ? c.fadeInS : c.fadeOutS));
        if (releasing) relW = Mathf.MoveTowards(relW, 0f, dt / Mathf.Max(0.01f, c.releaseFadeS));
        float w = slowW * relW;
        if (vol != null) vol.weight = w;
        if ((releasing && w <= 0.001f) || life > c.maxLifeS) Remove();
    }

    void Remove()
    {
        if (vol != null) { vol.weight = 0f; vol.enabled = false; }
        gameObject.SetActive(false);   // out of every Volume query this frame (Destroy is deferred to the end of the frame)
        if (inst == this) inst = null;
        if (prof != null) Destroy(prof);
        Destroy(gameObject);
    }
    void OnDestroy() { if (inst == this) inst = null; if (prof != null) Destroy(prof); }

    // ---------------- reading the effective post values ----------------
    static void Trigger(out Transform trig, out LayerMask mask)
    {
        trig = null; mask = ~0;
        var cam = Camera.main; if (cam == null) cam = FindFirstObjectByType<Camera>();
        if (cam == null) return;
        trig = cam.transform; var hd = cam.GetComponent<HDAdditionalCameraData>();
        if (hd != null) { mask = hd.volumeLayerMask; if (hd.volumeAnchorOverride != null) trig = hd.volumeAnchorOverride; }
    }
    /// <summary>The effective vignette intensity at the camera, from a FRESH Volume stack (no stale per-camera stack); NaN when it cannot be read.</summary>
    public static float ReadIntensity()
    {
        try
        {
            Transform trig; LayerMask mask; Trigger(out trig, out mask); if (trig == null) return float.NaN;
            var vm = VolumeManager.instance; var st = vm.CreateStack();
            try { vm.Update(st, trig, mask); var vg = st.GetComponent<Vignette>(); return vg != null ? vg.intensity.value : float.NaN; }
            finally { vm.DestroyStack(st); }
        }
        catch (Exception) { return float.NaN; }
    }
    /// <summary>The effective Fixed exposure (EV100) from a fresh stack; NaN when the scene exposure is not Fixed or unreadable.</summary>
    public static float ReadFixedExposure()
    {
        try
        {
            Transform trig; LayerMask mask; Trigger(out trig, out mask); if (trig == null) return float.NaN;
            var vm = VolumeManager.instance; var st = vm.CreateStack();
            try { vm.Update(st, trig, mask); var ex = st.GetComponent<Exposure>(); return ex != null && ex.mode.value == ExposureMode.Fixed ? ex.fixedExposure.value : float.NaN; }
            finally { vm.DestroyStack(st); }
        }
        catch (Exception) { return float.NaN; }
    }

    // ---------------- -finisherauto check ----------------
    /// <summary>After the scene (and the release fade): no Finisher* Volume left, the effective vignette back at the pre-scene value (and, with vignetteAbsolute, below vignetteMaxAfter), the Fixed exposure back.</summary>
    public static void Verify(FinisherAutoCfg a, out string line, List<string> errors, List<string> warns)
    {
        var IC = System.Globalization.CultureInfo.InvariantCulture;
        float after = ReadIntensity(), exAfter = ReadFixedExposure(), before = Baseline, exBefore = ExposureBaseline;
        int alive = 0; string names = "";
        foreach (var v in FindObjectsByType<Volume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (v != null && v.name.StartsWith("Finisher") && v.enabled && v.weight > 0.001f) { alive++; names += (names.Length > 0 ? ", " : "") + v.name + " w=" + v.weight.ToString("0.00", IC); }
        string F(float x) { return float.IsNaN(x) ? "?" : x.ToString("0.000", IC); }
        float delta = float.IsNaN(after) || float.IsNaN(before) ? float.NaN : after - before;
        float tol = Mathf.Max(0f, a.vignetteMaxAfter);
        line = "scene value " + F(before) + " -> after the finisher " + F(after) + " (finisher-owned change " + F(delta) + ", limit " + tol.ToString("0.000", IC) + (a.vignetteAbsolute ? ", ABSOLUTE mode: after must be <= limit" : "") + ")"
             + "; Finisher Volumes still active " + alive + (alive > 0 ? " [" + names + "]" : "") + "; override alive " + Alive + "; Fixed exposure " + F(exBefore) + " -> " + F(exAfter);
        if (alive > 0) errors.Add("finisher Volume(s) still active after the scene: " + names + " (weight must be 0 and the Volume destroyed on release)");
        if (Alive) errors.Add("FinisherVignette override still alive after the release fade (" + a.vignetteWaitS.ToString("0.0", IC) + " s)");
        if (!float.IsNaN(delta) && Mathf.Abs(delta) > tol) errors.Add("vignette after the finisher differs from the scene value by " + delta.ToString("+0.000;-0.000", IC) + " (limit " + tol.ToString("0.000", IC) + "): the finisher override did not fade out");
        if (a.vignetteAbsolute && !float.IsNaN(after) && after > tol) errors.Add("vignette after the finisher is " + F(after) + " (> " + tol.ToString("0.000", IC) + "): absolute mode");
        if (!a.vignetteAbsolute && !float.IsNaN(after) && after > tol) warns.Add("the SCENE vignette is " + F(after) + " in gameplay too (hdrp_lighting_ultra.json post.vignette, baked by HdrpScene); it is not finisher-owned. Lower post.vignette there (editor scene rebuild) or set rt.auto.vignetteAbsolute true to fail on it");
        if (float.IsNaN(after) || float.IsNaN(before)) warns.Add("vignette could not be read from a fresh Volume stack (no camera / API): only the Volume-alive check was judged");
        if (!float.IsNaN(exBefore) && !float.IsNaN(exAfter) && Mathf.Abs(exAfter - exBefore) > 0.05f) errors.Add("Fixed exposure after the finisher " + F(exAfter) + " differs from before " + F(exBefore) + ": the exposure Volume override was not removed");
    }
}
