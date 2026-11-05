using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

// PHYS1 v1 (PH3 P0): ONE physics clock. Physics.simulationMode = Script and Physics.Simulate(1/hz) once per 60 Hz sim tick (Duel.Update calls OnSimTick after the fighters tick),
// so PhysX runs on the SIM clock: hit-stop (PresentationClock stops Duel from ticking) freezes ragdolls and pieces, slow-mo (timeScale) slows them together with the sim,
// and a harness with Time.captureFramerate = 60 gets exactly one step per frame. CombatSlowMo / FinisherTime no longer rewrite Time.fixedDeltaTime while Scripted.
// Without a Duel in the scene (menus, tools) a fallback accumulator in PhysicsClockDriver steps on the same PresentationClock.SimDelta, so nothing is left unstepped.
// Data: Assets/Data/physics.json (clock / world / layers / matrix). clock.enabled=false or a missing file = nothing here does anything (Scripted stays false, OnSimTick returns at once).
// PHYSICS IS PRESENTATION: nothing in the sim reads a PhysX result. Proof: -physproof:simhash (byte-identical sim with physics on vs off) and -physproof:clock.
public static class PhysicsClock
{
    public static bool Scripted { get; private set; }
    /// <summary>false = no stepping and no ragdoll rig at all (proof "off" runs and -physics:off); the sim must not notice.</summary>
    public static bool PhysicsOn = true;
    public static int StepCount;
    public static float StepDt = 1f / 60f;
    public static event Action<float> OnStep;      // after every Simulate (sim time dt)
    public static double TotalMs; public static float MaxMs; public static int StatSteps;
    static float[] ring = new float[512]; static int ringPos, ringCount;
    static SimulationMode origMode; static bool haveOrig;
    static float acc; static int frameN, duelTickFrame = -100; static bool duelPresent; static bool applied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Scripted = false; PhysicsOn = true; StepCount = 0; OnStep = null; TotalMs = 0; MaxMs = 0; StatSteps = 0; ringPos = 0; ringCount = 0; acc = 0f; frameN = 0; duelPresent = false; applied = false; haveOrig = false; PhysicsCfg.Reload(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { Apply(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void BootDriver()
    {
        if (UnityEngine.Object.FindFirstObjectByType<PhysicsClockDriver>() != null) return;
        var go = new GameObject("PhysicsClock"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<PhysicsClockDriver>();
    }

    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }

    public static void Apply()
    {
        if (applied) return;
        applied = true;
        var c = PhysicsCfg.Get();
        ApplyLayers(c);
        if (Flag("-physics:off")) PhysicsOn = false;
        if (!c.clock.enabled) return;
        if (c.world.solverIterations > 0) { Physics.defaultSolverIterations = c.world.solverIterations; Physics.defaultSolverVelocityIterations = Mathf.Max(1, c.world.solverVelocityIterations); }
        if (c.world.maxAngularVelocity > 0f) Physics.defaultMaxAngularSpeed = c.world.maxAngularVelocity;
        if (c.world.gravityY < 0f) Physics.gravity = new Vector3(Physics.gravity.x, c.world.gravityY, Physics.gravity.z);
        StepDt = 1f / Mathf.Max(1, c.clock.hz);
        int n = Mathf.Max(16, c.clock.statsRing); if (ring.Length != n) ring = new float[n];
        if (!haveOrig) { origMode = Physics.simulationMode; haveOrig = true; Application.quitting += RestoreMode; }
        Physics.simulationMode = SimulationMode.Script;
        Scripted = true;
    }
    static void RestoreMode() { if (haveOrig) { try { Physics.simulationMode = origMode; } catch (Exception) { } } Scripted = false; }

    // collision matrix from physics.json: queryOnly layers collide with nothing, listed pairs are ignored, everything else keeps the project default (collide)
    static void ApplyLayers(PhysicsCfg c)
    {
        for (int q = 0; q < c.matrix.queryOnly.Length; q++)
        {
            int li = c.LayerIndex(c.matrix.queryOnly[q], -1); if (li < 0 || li > 31) continue;
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(li, i, true);
        }
        for (int p = 0; p < c.matrix.ignore.Length; p++)
        {
            var r = c.matrix.ignore[p]; if (r == null) continue;
            int a = c.LayerIndex(r.a, -1), b = c.LayerIndex(r.b, -1);
            if (a >= 0 && a < 32 && b >= 0 && b < 32) Physics.IgnoreLayerCollision(a, b, true);
        }
    }

    /// <summary>Duel.Update calls this once per 60 Hz sim tick, after the fighters ticked.</summary>
    public static void OnSimTick() { duelTickFrame = Time.frameCount; StepOnce(); }

    static void StepOnce()
    {
        if (!Scripted) return;
        if (!PhysicsOn) { Physics.SyncTransforms(); return; }   // proof 'off' runs: no stepping, but colliders still follow their transforms like on a stepped tick
        long t0 = Stopwatch.GetTimestamp();
        FinPhys.Simulate(StepDt);   /* FINPHYS v97: 2 sub-steps for 2 s after a cut (V3 cut_high_rate_ticks) */
        StepCount++;
        var h = OnStep; if (h != null) h(StepDt);
        float ms = (float)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        TotalMs += ms; StatSteps++; if (ms > MaxMs) MaxMs = ms;
        ring[ringPos] = ms; ringPos = (ringPos + 1) % ring.Length; if (ringCount < ring.Length) ringCount++;
    }

    public static float AvgMs { get { return StatSteps > 0 ? (float)(TotalMs / StatSteps) : 0f; } }
    public static float P95Ms
    {
        get
        {
            if (ringCount == 0) return 0f;
            var tmp = new float[ringCount]; Array.Copy(ring, tmp, ringCount); Array.Sort(tmp);
            return tmp[Mathf.Clamp((int)Mathf.Ceil(ringCount * 0.95f) - 1, 0, ringCount - 1)];
        }
    }
    public static void ResetStats() { TotalMs = 0; MaxMs = 0; StatSteps = 0; ringPos = 0; ringCount = 0; }

    // fallback for scenes without a Duel (LateUpdate of the driver)
    internal static void FallbackTick(float dt)
    {
        if (!Scripted || !PhysicsOn) return;
        var c = PhysicsCfg.Get();
        if (!c.clock.fallbackWhenNoDuel) return;
        if (Time.frameCount - duelTickFrame <= 2) { duelPresent = true; acc = 0f; return; }   // the Duel ticked a moment ago: it owns the stepping
        if (frameN++ % Mathf.Max(1, c.clock.duelCheckEveryFrames) == 0) { var du = UnityEngine.Object.FindFirstObjectByType<Duel>(); duelPresent = du != null && du.enabled; }   // a Duel that AutoShot switched off does not tick, so the fallback must step
        if (duelPresent) { acc = 0f; return; }
        acc += Mathf.Min(PresentationClock.SimDelta(dt), 0.1f);
        int n = 0, max = Mathf.Max(1, c.clock.maxStepsPerFrame);
        while (acc >= StepDt && n < max) { acc -= StepDt; StepOnce(); n++; }
        if (n >= max) acc = 0f;
    }
}

[DefaultExecutionOrder(1000)]
public class PhysicsClockDriver : MonoBehaviour
{
    void LateUpdate() { PhysicsClock.FallbackTick(Time.deltaTime); }
}
