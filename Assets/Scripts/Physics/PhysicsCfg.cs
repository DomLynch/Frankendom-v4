using System;
using System.IO;
using UnityEngine;

// PHYS1 v1: Assets/Data/physics.json (copy in StreamingAssets). World solver numbers, the physics clock, named layers + collision matrix and the -physproof thresholds.
// Missing / unreadable file = Defaults() below (clock OFF), so a build without the file behaves exactly like before. No gameplay number lives in code.
[Serializable] public class PhysClockCfg { public bool enabled; public int hz = 60, maxStepsPerFrame = 4, duelCheckEveryFrames = 30, statsRing = 512; public bool fallbackWhenNoDuel = true; }
[Serializable] public class PhysWorldCfg { public int solverIterations = 6, solverVelocityIterations = 1; public float maxAngularVelocity = 50f, gravityY = -9.81f; }
[Serializable] public class PhysLayerRow { public string name = ""; public int index; }
[Serializable] public class PhysPairRow { public string a = "", b = ""; }
[Serializable] public class PhysMatrixCfg { public string[] queryOnly = new string[0]; public PhysPairRow[] ignore = new PhysPairRow[0]; }
[Serializable] public class PhysProofClock { public int frames = 120; public float hitStopMs = 250f; public string slowMoClass = "parry"; }
[Serializable] public class PhysProofMove { public string move = "", zone = ""; }
[Serializable]
public class PhysProofDeath
{
    public float victimHealth = 1f, separationM = 1.7f;
    public float[] approachDegrees = new float[] { 0f };
    public PhysProofMove[] moves = new PhysProofMove[0];
    public int killWaitTicks = 150;
    public float hingeSlackDeg = 15f, observeSeconds = 9f, firstWindowSeconds = 0.5f, minTravelAlongBlowM = 0.2f, maxConvertJumpM = 0.02f, momentumTolerance = 0.1f, restToleranceM = 0.03f, maxRestSeconds = 8f, maxAvgStepMs = 0.5f, maxP95StepMs = 1f, minRagdollSpeedSq = 0.0004f;
    public float moveDirX, moveDirZ = 1f; public bool run = true; public int runUpTicks = 45; public float momentumImpulseScale = 0f;
    public float[] sheetFrames = new float[] { 0f, 0.5f, 1f, 2f };
    public int sheetCellWidth = 320, sheetCellHeight = 180;
}
[Serializable] public class PhysProofSimHash { public int seed = 1, maxTicks = 5400, postDeathTicks = 300; public float victimHealth = 40f; public string[] runs = new string[] { "on", "off", "off" }; }
[Serializable] public class PhysProofCfg { public int settleFrames = 30; public float timeoutSeconds = 420f; public PhysProofClock clock = new PhysProofClock(); public PhysProofDeath death = new PhysProofDeath(); public PhysProofSimHash simHash = new PhysProofSimHash(); }

[Serializable]
public class PhysicsCfg
{
    public int version = 1;
    public bool enabled = true;   // INTPHYS v99 master switch (physics.json "enabled"): false = the pre-physics behaviour (no scripted clock, no hit / clash / finisher physics)
    public PhysClockCfg clock = new PhysClockCfg();
    public PhysWorldCfg world = new PhysWorldCfg();
    public PhysLayerRow[] layers = new PhysLayerRow[0];
    public PhysMatrixCfg matrix = new PhysMatrixCfg();
    public string[] groundIgnore = new string[0];
    public PhysProofCfg proof = new PhysProofCfg();

    static PhysicsCfg cur;
    public static PhysicsCfg Get() { if (cur == null) cur = Load(); return cur; }
    public static void Reload() { cur = null; }

    public int LayerIndex(string name, int fallback = -1)
    {
        if (layers != null) for (int i = 0; i < layers.Length; i++) if (layers[i] != null && layers[i].name == name) return layers[i].index;
        return fallback;
    }

    static PhysicsCfg Load()
    {
        string json = null;
        string[] paths = { Path.Combine(Application.streamingAssetsPath, "physics.json"), Path.Combine(Application.dataPath, "Data/physics.json") };
        for (int i = 0; i < paths.Length && json == null; i++)
        {
            try { if (File.Exists(paths[i])) json = File.ReadAllText(paths[i]).TrimStart('﻿'); }
            catch (Exception ex) { Debug.LogWarning("[PhysicsCfg] cannot read " + paths[i] + ": " + ex.Message); }
        }
        var d = new PhysicsCfg();   // class defaults = clock disabled, no layers
        if (string.IsNullOrEmpty(json)) return d;   // missing file = the feature is off
        try
        {
            var r = JsonUtility.FromJson<PhysicsCfg>(json);
            if (r == null) return d;
            if (r.clock == null) r.clock = new PhysClockCfg(); if (r.world == null) r.world = new PhysWorldCfg(); if (r.layers == null) r.layers = new PhysLayerRow[0];
            if (r.matrix == null) r.matrix = new PhysMatrixCfg(); if (r.matrix.queryOnly == null) r.matrix.queryOnly = new string[0]; if (r.matrix.ignore == null) r.matrix.ignore = new PhysPairRow[0];
            if (r.groundIgnore == null) r.groundIgnore = new string[0];
            if (r.proof == null) r.proof = new PhysProofCfg(); if (r.proof.clock == null) r.proof.clock = new PhysProofClock(); if (r.proof.death == null) r.proof.death = new PhysProofDeath(); if (r.proof.simHash == null) r.proof.simHash = new PhysProofSimHash();
            if (r.proof.death.moves == null) r.proof.death.moves = new PhysProofMove[0]; if (r.proof.death.approachDegrees == null) r.proof.death.approachDegrees = new float[] { 0f };
            if (r.proof.death.sheetFrames == null) r.proof.death.sheetFrames = new float[] { 0f }; if (r.proof.simHash.runs == null) r.proof.simHash.runs = new string[] { "on", "off", "off" };
            return r;
        }
        catch (Exception ex) { Debug.LogError("[PhysicsCfg] parse failed: " + ex.Message); return d; }
    }
}
