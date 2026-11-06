using System.Collections.Generic;
using UnityEngine;

// Time.timeScale dips on unscaled time (finisher slow-mo). Several requests merge: the lowest scale wins while any is running. Always restored.
public static class FinisherTime
{
    class Dip { public float scale, dur, easeIn, easeOut, age; public SlowPoint[] pts; }   // pts (V62): a time-scale curve
    static readonly List<Dip> dips = new List<Dip>();
    static float baseScale = 1f, baseFixed = 0.02f; static bool captured;
    static int lastFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { dips.Clear(); captured = false; lastFrame = -1; }

    public static void SlowMo(float scale, float durationUnscaled, float easeIn = 0.05f, float easeOut = 0.25f)
    {
        if (!captured) { captured = true; baseScale = Time.timeScale <= 0f ? 1f : Time.timeScale; baseFixed = Time.fixedDeltaTime; }
        dips.Add(new Dip { scale = Mathf.Clamp(scale, 0.02f, 1f), dur = Mathf.Max(0.01f, durationUnscaled), easeIn = Mathf.Max(0.001f, easeIn), easeOut = Mathf.Max(0.001f, easeOut) });
    }
    /// <summary>V62: a time-scale curve (data: finishers.json slowmo[].curve). Points t = unscaled seconds from now, scale = Time.timeScale factor, smoothstep between points; before the first point it ramps from 1,
    /// after the last it eases back to 1 over easeOut. Merges with other dips by the lowest-wins rule.</summary>
    public static void Curve(SlowPoint[] pts, float easeOut = 0.3f)
    {
        if (pts == null || pts.Length == 0) return;
        if (!captured) { captured = true; baseScale = Time.timeScale <= 0f ? 1f : Time.timeScale; baseFixed = Time.fixedDeltaTime; }
        var c = new SlowPoint[pts.Length]; for (int i = 0; i < pts.Length; i++) c[i] = new SlowPoint { t = Mathf.Max(0f, pts[i].t), scale = Mathf.Clamp(pts[i].scale, 0.02f, 1f) };
        System.Array.Sort(c, (a, b) => a.t.CompareTo(b.t));
        dips.Add(new Dip { pts = c, dur = Mathf.Max(0.01f, c[c.Length - 1].t), easeIn = 0.001f, easeOut = Mathf.Max(0.001f, easeOut), scale = 1f });
    }
    static float EvalCurve(SlowPoint[] p, float a)
    {
        if (a <= p[0].t) { if (p[0].t <= 1e-4f) return p[0].scale; float u0 = Mathf.Clamp01(a / p[0].t); return Mathf.Lerp(1f, p[0].scale, u0 * u0 * (3f - 2f * u0)); }
        for (int i = 1; i < p.Length; i++)
            if (a <= p[i].t) { float span = Mathf.Max(1e-4f, p[i].t - p[i - 1].t), u = Mathf.Clamp01((a - p[i - 1].t) / span); return Mathf.Lerp(p[i - 1].scale, p[i].scale, u * u * (3f - 2f * u)); }
        return p[p.Length - 1].scale;
    }
    public static bool Slowed { get { return dips.Count > 0; } }

    /// <summary>FINFIX v93: a frame step clamped to finishers.json rt.maxStepS (0.05 s): the finisher clocks (scene T, camera blends, shake, slow-mo dips, holds) advance by at most this per frame,
    /// so one long frame (a JIT / asset hitch of seconds) cannot skip a beat or expire a whole shot. maxStepS 0 = no clamp.</summary>
    public static float Step(float dt)
    {
        float m = 0f; try { m = FinisherConfig.Get().rt.maxStepS; } catch (System.Exception) { }
        return m > 0f && dt > m ? m : dt;
    }

    // call once per frame (CameraRig.Update and the playback coroutines both may call it; it runs once per frame)
    public static void Tick()
    {
        if (lastFrame == Time.frameCount) return; lastFrame = Time.frameCount;
        if (dips.Count == 0) return;
        float dt = Step(Time.unscaledDeltaTime), lowest = 1f;   // FINFIX v93: clamped step
        for (int i = dips.Count - 1; i >= 0; i--)
        {
            var d = dips[i]; d.age += dt;
            if (d.age >= d.dur + d.easeOut) { dips.RemoveAt(i); continue; }
            float w = d.age < d.easeIn ? d.age / d.easeIn : (d.age <= d.dur ? 1f : 1f - (d.age - d.dur) / d.easeOut);
            float v;
            if (d.pts != null) { v = EvalCurve(d.pts, Mathf.Min(d.age, d.dur)); if (d.age > d.dur) v = Mathf.Lerp(1f, v, Mathf.Clamp01(w)); }
            else v = Mathf.Lerp(1f, d.scale, Mathf.Clamp01(w));
            lowest = Mathf.Min(lowest, v);
        }
        if (dips.Count == 0) { Restore(); return; }
        Time.timeScale = baseScale * lowest; if (!PhysicsClock.Scripted) Time.fixedDeltaTime = baseFixed * Time.timeScale;   // PHYS1 v1
    }

    public static void Restore()
    {
        dips.Clear();
        if (captured) { Time.timeScale = baseScale; if (!PhysicsClock.Scripted) Time.fixedDeltaTime = baseFixed; captured = false; }
    }
}
