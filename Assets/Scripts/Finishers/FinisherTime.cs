using System.Collections.Generic;
using UnityEngine;

// Time.timeScale dips on unscaled time (finisher slow-mo). Several requests merge: the lowest scale wins while any is running. Always restored.
public static class FinisherTime
{
    class Dip { public float scale, dur, easeIn, easeOut, age; }
    static readonly List<Dip> dips = new List<Dip>();
    static float baseScale = 1f, baseFixed = 0.02f; static bool captured;
    static int lastFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { dips.Clear(); captured = false; lastFrame = -1; }

    public static void SlowMo(float scale, float durationUnscaled, float easeIn = 0.05f, float easeOut = 0.25f)
    {
        if (!captured) { captured = true; baseScale = Time.timeScale <= 0f ? 1f : Time.timeScale; baseFixed = Time.fixedDeltaTime; }
        dips.Add(new Dip { scale = Mathf.Clamp(scale, 0.02f, 1f), dur = Mathf.Max(0.01f, durationUnscaled), easeIn = Mathf.Max(0.001f, easeIn), easeOut = Mathf.Max(0.001f, easeOut) });
    }
    public static bool Slowed { get { return dips.Count > 0; } }

    // call once per frame (CameraRig.Update and the playback coroutines both may call it; it runs once per frame)
    public static void Tick()
    {
        if (lastFrame == Time.frameCount) return; lastFrame = Time.frameCount;
        if (dips.Count == 0) return;
        float dt = Time.unscaledDeltaTime, lowest = 1f;
        for (int i = dips.Count - 1; i >= 0; i--)
        {
            var d = dips[i]; d.age += dt;
            if (d.age >= d.dur + d.easeOut) { dips.RemoveAt(i); continue; }
            float w = d.age < d.easeIn ? d.age / d.easeIn : (d.age <= d.dur ? 1f : 1f - (d.age - d.dur) / d.easeOut);
            lowest = Mathf.Min(lowest, Mathf.Lerp(1f, d.scale, Mathf.Clamp01(w)));
        }
        if (dips.Count == 0) { Restore(); return; }
        Time.timeScale = baseScale * lowest; Time.fixedDeltaTime = baseFixed * Time.timeScale;
    }

    public static void Restore()
    {
        dips.Clear();
        if (captured) { Time.timeScale = baseScale; Time.fixedDeltaTime = baseFixed; captured = false; }
    }
}
