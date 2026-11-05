using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// V61C: finisher shots must look like the gameplay camera (the v61 sheets were washed out to grey-white). Data: finishers.json rt.look.
//  copyCameraData      snapshot the gameplay camera's HDRP camera data (volume layer mask, volume anchor, anti-aliasing, dithering, physical exposure inputs, culling) at Begin and re-assert it
//                      (only when something changed it) for the whole scene; restored at End.
//  freezeVolumeAnchor  the camera's Volume anchor is a transform frozen at the GAMEPLAY camera position for the scene, so local (box) Volumes that shape the gameplay look (exposure / fog / colour near the gate)
//                      are blended exactly as in gameplay instead of falling off when the finisher camera orbits away.
//  darkenEV            stops the finisher shots are darkened by (added to the Fixed exposure value through the same high-priority Volume that turns motion blur off). 0 = off. Tune from the -finisherauto luma log.
[Serializable]
public class FinisherLookCfg
{
    public bool copyCameraData = true;
    public bool freezeVolumeAnchor = true;
    public bool enforceEachFrame = true;
    public float darkenEV = 0f;
    // V62B: every finisher camera looks toward the gate (the gate glow side is dark, the arena back side is pale grey); per-shot override FinisherShot.gate
    public bool gateFacing = true;
    public float[] gatePoint = { 0f, 4f, 24f };   // world x, y, z of the gate opening (V62C: HdrpDoorway measured the gate bounds frontZ 22.82 / centre z 27.0; gateway.json says "front face z 27"; 24 = just inside the front face)
    public float gateMinDot = 0.25f;               // the flat view direction must have at least this cosine with the direction to the gate (0.25 = within ~75 deg)
    public bool gateMirror = true;                 // V62C: when the view does not face the gate, first MIRROR the azimuth about the pair axis, then rotate in 15 degree steps
    public float evRate = 4f;                      // stops per second the per-shot exposure offset moves
}

public class LookSnap
{
    public HDAdditionalCameraData hd; public Camera cam;
    public LayerMask volumeMask; public Transform volumeAnchor;
    public HDAdditionalCameraData.AntialiasingMode aa; public bool dithering, stopNaNs, allowDyn;
    public int cullingMask; public float near, far; public bool usePhysical; public int iso; public float shutter, aperture;
}

public static class FinisherLook
{
    public static LookSnap Capture(Camera cam)
    {
        if (cam == null) return null;
        var s = new LookSnap { cam = cam, hd = cam.GetComponent<HDAdditionalCameraData>() };
        s.cullingMask = cam.cullingMask; s.near = cam.nearClipPlane; s.far = cam.farClipPlane; s.usePhysical = cam.usePhysicalProperties; s.iso = cam.iso; s.shutter = cam.shutterSpeed; s.aperture = cam.aperture;
        if (s.hd != null)
        {
            s.volumeMask = s.hd.volumeLayerMask; s.volumeAnchor = s.hd.volumeAnchorOverride; s.aa = s.hd.antialiasing; s.dithering = s.hd.dithering; s.stopNaNs = s.hd.stopNaNs; s.allowDyn = s.hd.allowDynamicResolution;
        }
        return s;
    }

    /// <summary>Write the snapshot back, but only the values that differ (assigning camera data every frame would reset TAA history).</summary>
    public static int Apply(LookSnap s)
    {
        if (s == null || s.cam == null) return 0;
        int n = 0; var cam = s.cam;
        if (cam.cullingMask != s.cullingMask) { cam.cullingMask = s.cullingMask; n++; }
        if (!Mathf.Approximately(cam.nearClipPlane, s.near)) { cam.nearClipPlane = s.near; n++; }
        if (!Mathf.Approximately(cam.farClipPlane, s.far)) { cam.farClipPlane = s.far; n++; }
        if (cam.usePhysicalProperties != s.usePhysical) { cam.usePhysicalProperties = s.usePhysical; n++; }
        if (cam.iso != s.iso) { cam.iso = s.iso; n++; }
        if (!Mathf.Approximately(cam.shutterSpeed, s.shutter)) { cam.shutterSpeed = s.shutter; n++; }
        if (!Mathf.Approximately(cam.aperture, s.aperture)) { cam.aperture = s.aperture; n++; }
        var hd = s.hd; if (hd == null) return n;
        if (hd.volumeLayerMask != s.volumeMask) { hd.volumeLayerMask = s.volumeMask; n++; }
        if (hd.antialiasing != s.aa) { hd.antialiasing = s.aa; n++; }
        if (hd.dithering != s.dithering) { hd.dithering = s.dithering; n++; }
        if (hd.stopNaNs != s.stopNaNs) { hd.stopNaNs = s.stopNaNs; n++; }
        if (hd.allowDynamicResolution != s.allowDyn) { hd.allowDynamicResolution = s.allowDyn; n++; }
        return n;
    }

    /// <summary>rt.look.darkenEV: darken the Fixed exposure by that many stops inside the given (high priority, global) profile. No-op when the scene exposure is not Fixed.</summary>
    public static void DarkenExposure(VolumeProfile prof)
    {
        try
        {
            float ev = FinisherConfig.Get().rt.look.darkenEV; if (prof == null || Mathf.Abs(ev) < 1e-4f) return;
            var st = VolumeManager.instance.stack; var cur = st != null ? st.GetComponent<Exposure>() : null;
            if (cur == null || cur.mode.value != ExposureMode.Fixed) { Debug.LogWarning("[FinisherLook] darkenEV set but the scene exposure is not Fixed: ignored"); return; }
            var ex = prof.Add<Exposure>(true); ex.mode.Override(ExposureMode.Fixed); ex.fixedExposure.Override(cur.fixedExposure.value + ev); ex.active = true;
        }
        catch (Exception e) { Debug.LogWarning("[FinisherLook] darkenEV: " + e.Message); }
    }

    /// <summary>V62B: the Exposure override of a (high priority) profile, created when it is not there yet (Fixed mode).</summary>
    public static Exposure ExposureOverride(VolumeProfile prof)
    {
        if (prof == null) return null;
        Exposure ex; if (prof.TryGet(out ex)) return ex;
        ex = prof.Add<Exposure>(true); ex.mode.Override(ExposureMode.Fixed); ex.active = true; return ex;
    }
    /// <summary>V62B: the scene's Fixed exposure value (EV100) from the live Volume stack; NaN when the scene exposure is not Fixed.</summary>
    public static float StackFixedEV()
    {
        try { var st = VolumeManager.instance.stack; var cur = st != null ? st.GetComponent<Exposure>() : null; if (cur != null && cur.mode.value == ExposureMode.Fixed) return cur.fixedExposure.value; } catch (Exception) { }
        return float.NaN;
    }
    /// <summary>Mean sRGB luma (Rec.709 weights, 0..1) of the picture's middle band: the HUD bars at the top and the console / HUD strip at the bottom are left out. 48 x 27 samples.</summary>
    public static float MeanLuma(Texture2D tex)
    {
        if (tex == null || tex.width < 4 || tex.height < 4) return 0f;
        double sum = 0; int n = 0;
        for (int yy = 0; yy < 27; yy++)
            for (int xx = 0; xx < 48; xx++)
            {
                float u = 0.04f + 0.92f * (xx + 0.5f) / 48f, v = 0.14f + 0.72f * (yy + 0.5f) / 27f;   // y from the bottom: 14 % .. 86 %
                Color c = tex.GetPixel(Mathf.Clamp((int)(u * tex.width), 0, tex.width - 1), Mathf.Clamp((int)(v * tex.height), 0, tex.height - 1));
                sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; n++;
            }
        return (float)(sum / Math.Max(1, n));
    }
}
