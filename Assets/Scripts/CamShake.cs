using System;
using System.Collections.Generic;
using UnityEngine;

// V65CAM: camera shake scaled by hit weight. Data: the "shake" block of camera_impact.json (StreamingAssets, then Assets/Data); this file holds no numbers.
//  A contact (ContactRelay.OnEvent) is classed hitLight / hitHeavy / kick / blockLight / blockHeavy / parry / guardBreak / special / crit / deathblow and adds ONE pulse:
//  a damped smooth-noise wobble (Perlin, continuous, no jitter) of the camera position (m, in the camera's own right / up) and roll (deg), amplitude x ImpactTable.MoveMul(move).
//  Comfort: every pulse starts with a smoothstep attack, the sum goes through a soft saturation (tanh) at maxPosM / maxRollDeg, frequencies are capped at maxFreqHz, at most maxPulses
//  run at once (the weakest is dropped), and `scale` (0 = off) is the one accessibility knob. Real time (unscaledDeltaTime): a hit-stop or a slow-mo does not stretch the shake.
//  CameraRig calls CamShake.Apply(transform) once per frame after CameraImpact.Apply, before the ground floor.
public static class CamShake
{
    [Serializable] public class ClassRow { public string id; public float amp = 0.02f, rollDeg = 0.2f, freq = 9f, decay = 0.15f; }
    [Serializable]
    public class Row
    {
        public bool enabled = true;
        public float scale = 1f;               // accessibility: global multiplier, 0 = no shake
        public float maxPosM = 0.07f, maxRollDeg = 0.9f, maxFreqHz = 14f;
        public int maxPulses = 6;
        public float attackS = 0.025f;         // smoothstep attack of every pulse
        public float endAt = 0.02f;            // a pulse ends when its envelope is below this
        public float victimMul = 1f, attackerMul = 0.6f, bystanderMul = 0.4f;   // the player is hit / the player lands the blow / neither
        public bool useMoveWeight = true;
        public ClassRow[] classes = new ClassRow[0];
    }
    [Serializable] class Root { public Row shake; }
    class Pulse { public float amp, roll, freq, decay, age, sx, sy, sr; }

    static Row cfg; static readonly List<Pulse> pulses = new List<Pulse>(); static readonly Dictionary<DuelFighter, int> seenDb = new Dictionary<DuelFighter, int>();
    static CameraRig rig; static CamShakeDriver driver; static int lastFrame = -1;
    public static Vector2 LastOffset { get; private set; }
    public static float LastRoll { get; private set; }
    public static float PeakOffset, PeakRoll; public static int Fired; public static string LastClass = "";   // proof counters

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; pulses.Clear(); seenDb.Clear(); rig = null; driver = null; lastFrame = -1; PeakOffset = 0f; PeakRoll = 0f; Fired = 0; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (driver != null || UnityEngine.Object.FindFirstObjectByType<CamShakeDriver>() != null) return;
        var go = new GameObject("CamShake"); UnityEngine.Object.DontDestroyOnLoad(go); driver = go.AddComponent<CamShakeDriver>();
    }

    public static Row Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/camera_impact.json", Application.dataPath + "/Data/camera_impact.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null && r.shake != null) { cfg = r.shake; if (cfg.classes == null) cfg.classes = new ClassRow[0]; } } catch (Exception e) { Debug.LogWarning("[CamShake] " + p + ": " + e.Message); }
            break;
        }
        return cfg;
    }
    static ClassRow Row_(string id) { var c = Cfg().classes; for (int i = 0; i < c.Length; i++) if (c[i] != null && c[i].id == id) return c[i]; return null; }

    static bool IsPlayer(DuelFighter f)
    {
        if (rig == null) rig = UnityEngine.Object.FindFirstObjectByType<CameraRig>();
        if (f == null || rig == null || rig.target == null) return false;
        return f.transform == rig.target || f.transform.IsChildOf(rig.target);
    }

    /// <summary>The weight class of a contact (null = no shake).</summary>
    public static string Classify(CombatEvent e)
    {
        string mid = e.moveId ?? "";
        switch (e.type)
        {
            case CombatClass.GuardBroken: return CombatClass.GuardBreak;   // HCKIT4 v95
            case CombatClass.Parried: return CombatClass.Parry;   // HCKIT4 v95
            case CombatClass.Blocked: return CombatClass.Of(e.type, e.heavy);   // HCKIT4 v95
            case "Hit":
                if (e.actor != null)
                {
                    int seen; seenDb.TryGetValue(e.actor, out seen);
                    int now = e.actor.statDeathblows; seenDb[e.actor] = now;
                    if (now > seen) return "deathblow";
                }
                if (CombatClass.IsKick(mid)) return CombatClass.Kick;   // HCKIT4 v95
                if (CombatClass.IsSpecial(mid)) return CombatClass.Special;   // HCKIT4 v95
                if (CombatClass.IsCrit(e)) return CombatClass.Crit;   // HCKIT4 v95
                return CombatClass.Of(e.type, e.heavy);   // HCKIT4 v95
        }
        return null;
    }

    public static void OnEvent(CombatEvent e)
    {
        var r = Cfg(); if (!r.enabled || r.scale <= 0f) return;
        string cls = Classify(e); if (cls == null) return;
        float who = IsPlayer(e.target) ? r.victimMul : (IsPlayer(e.actor) ? r.attackerMul : r.bystanderMul);
        Trigger(cls, who * (r.useMoveWeight ? ImpactTable.MoveMul(e.moveId) : 1f) * HitFx.ShakeMul(e) /*HITFX2*/);
    }

    public static void Trigger(string cls, float mul)
    {
        var r = Cfg(); var c = Row_(cls); if (!r.enabled || c == null || r.scale <= 0f) return;
        var p = new Pulse { amp = c.amp * mul * r.scale, roll = c.rollDeg * mul * r.scale, freq = Mathf.Min(r.maxFreqHz, Mathf.Max(0.5f, c.freq)), decay = Mathf.Max(0.02f, c.decay), sx = UnityEngine.Random.value * 100f, sy = UnityEngine.Random.value * 100f, sr = UnityEngine.Random.value * 100f };
        if (pulses.Count >= Mathf.Max(1, r.maxPulses))
        {
            int weakest = 0; float w = float.MaxValue;
            for (int i = 0; i < pulses.Count; i++) { float v = pulses[i].amp * Mathf.Exp(-pulses[i].age / pulses[i].decay); if (v < w) { w = v; weakest = i; } }
            if (w >= p.amp) return;   // the new one is weaker than everything running
            pulses.RemoveAt(weakest);
        }
        pulses.Add(p); Fired++; LastClass = cls;
    }

    public static void Clear() { pulses.Clear(); LastOffset = Vector2.zero; LastRoll = 0f; }

    /// <summary>Draw the shake on the camera and age it (once per rendered frame, real time).</summary>
    public static void Apply(Transform cam)
    {
        if (lastFrame == Time.frameCount) return; lastFrame = Time.frameCount;
        var r = Cfg(); float dt = Time.unscaledDeltaTime;
        if (!r.enabled || pulses.Count == 0) { LastOffset = Vector2.zero; LastRoll = 0f; return; }
        float x = 0f, y = 0f, roll = 0f;
        for (int i = pulses.Count - 1; i >= 0; i--)
        {
            var p = pulses[i];
            float env = Mathf.Exp(-p.age / p.decay) * Mathf.SmoothStep(0f, 1f, r.attackS > 1e-4f ? p.age / r.attackS : 1f);
            if (p.age > r.attackS && env < r.endAt) { pulses.RemoveAt(i); continue; }
            float t = p.age * p.freq;
            x += p.amp * env * (Mathf.PerlinNoise(p.sx, t) * 2f - 1f);
            y += p.amp * env * (Mathf.PerlinNoise(p.sy, t) * 2f - 1f);
            roll += p.roll * env * (Mathf.PerlinNoise(p.sr, t) * 2f - 1f);
            p.age += dt;
        }
        // soft saturation: never beyond the comfort caps, no flat kink
        float m = Mathf.Sqrt(x * x + y * y);
        if (m > 1e-6f) { float s = r.maxPosM * (float)Math.Tanh(m / r.maxPosM) / m; x *= s; y *= s; }
        roll = r.maxRollDeg * (float)Math.Tanh(roll / Mathf.Max(1e-4f, r.maxRollDeg));
        LastOffset = new Vector2(x, y); LastRoll = roll;
        PeakOffset = Mathf.Max(PeakOffset, Mathf.Sqrt(x * x + y * y)); PeakRoll = Mathf.Max(PeakRoll, Mathf.Abs(roll));
        cam.position += cam.right * x + cam.up * y;
        if (Mathf.Abs(roll) > 1e-4f) cam.rotation = cam.rotation * Quaternion.Euler(0f, 0f, roll);
    }
}

public class CamShakeDriver : MonoBehaviour
{
    void OnEnable() { ContactRelay.OnEvent += CamShake.OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= CamShake.OnEvent; CamShake.Clear(); }
}
