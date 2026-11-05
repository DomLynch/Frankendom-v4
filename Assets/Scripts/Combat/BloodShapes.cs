using System;
using UnityEngine;

// HCKIT6 v100 (HC3 K10b/c): the SHAPE generators of the blood and wound art, as data. Assets/Data/blood_shapes.json (Data == StreamingAssets, read through DataPath).
// Before this file BloodBurst (spray / drop ranges, V3 width / length factors), WoundDecals.MakeMask (streak + puncture blob layout), BloodGround.MakeSplat (splat blobs) and
// BloodEdge.MakeStrip (edge strip curves) held those numbers as literals. The class defaults below ARE the old literals (no file = the v95 look), the json carries the same numbers
// (tools selftest hckit6_selftest.py proves json == old literals). Presentation only: nothing here touches the sim. Ranges are [lo, hi]; Between(range) = lo + (hi - lo) * Rnd().
public static class BloodShapes
{
    [Serializable] public class Drop   // the heavy drops at the front of a burst
    {
        public float[] life = { 0.5f, 0.72f }, size = { 0.07f, 0.12f }, stretch = { 1.1f, 1.5f }, radial = { 0.3f, 1.1f }, along = { 0.3f, 1.0f }, up = { 1.6f, 3.0f };
    }
    [Serializable] public class Spray   // the thin spray behind them
    {
        public float[] life = { 0.14f, 0.4f }, size = { 0.018f, 0.05f }, stretch = { 1.3f, 2.4f }, speed = { 1.2f, 3.6f }, along = { 0.5f, 2.4f }, up = { 0.2f, 2.6f };
        public float radialMul = 0.6f;
    }
    [Serializable] public class Burst
    {
        public float[] thinFactors = { 0.7f, 0.65f, 0.9f }, longNumer = { 1.2f, 1.2f }, longDenom = { 0.65f, 0.9f };   // V3 width x THIN, length x LONG (blood-style.ts)
        public float front = 0.35f, minCam = 1.5f, growMax = 3f, gravity = 8f, gravityRef = 9.81f, pullHeight = 1.2f, nearEps = 0.01f, startLifetime = 0.6f, maxParticleSize = 0.5f, cameraVelocityScale = 0f;
        public int maxParticles = 400, hitCount = 15, killCount = 25, hitDrops = 2, killDrops = 3, seed = 731;
        public float[] alphaKeys = { 1f, 0f, 0.9f, 0.7f, 0f, 1f };   // alpha, time pairs of the colour-over-lifetime gradient
        public Drop drop = new Drop(); public Spray spray = new Spray();
        public float Thin() { return thinFactors[0] * thinFactors[1] * thinFactors[2]; }
        public float Long() { return longNumer[0] * longNumer[1] / (longDenom[0] * longDenom[1]); }
    }

    [Serializable] public class Wound   // WoundDecals.MakeMask: streak (slash / overhead) and puncture (stab) masks
    {
        public int texMin = 16, texMax = 256, seedMul = 131, variantMul = 7919, punctureSeedAdd = 17;
        public float gain = 1.5f, blobReach = 1.2f, centre = 0.5f;
        public float coreR = 0.1f;                                                                     // puncture: round hole
        public int runCount = 6; public float runStart = 0.1f, runStep = 0.045f, runR = 0.04f, runRStep = 0.004f;   // puncture: short radial run
        public int satCount = 9; public float satDistMin = 0.16f, satDistRange = 0.25f, satRMin = 0.012f, satRRange = 0.025f;   // puncture: satellite drops
        public int seg = 28; public float segX0 = 0.06f, segXSpan = 0.88f, wobble = 0.04f, sineFreq = 5f, sineAmp = 0.03f, segR = 0.03f, segTaper = 0.07f, segJitterLo = 0.75f, segJitterRange = 0.5f, segSx = 1.6f;   // streak: tapered gash
        public int speckCount = 10; public float speckX0 = 0.1f, speckXSpan = 0.8f, speckYSpan = 0.4f, speckRMin = 0.01f, speckRRange = 0.02f;
        public int dripCount = 3; public float dripX0 = 0.2f, dripXSpan = 0.6f, dripDrop = 0.08f, dripDropRange = 0.12f, dripR = 0.012f, dripSx = 0.6f, dripSy = 2.4f;   // short runs below the cut
    }

    [Serializable] public class Ground   // BloodGround.MakeSplat + placement
    {
        public int variants = 3, blobBase = 4, blobRand = 4, pins = 4;
        public float[] dist = { 0.1f, 0.38f }, mainR = { 0.16f, 0.24f }, midR = { 0.04f, 0.1f }, pinR = { 0.012f, 0.03f }, sx = { 0.7f, 1.4f }, stretchY = { 0.8f, 1.25f };
        public float reach = 2f, hitScatter = 0.4f, hitOffset = 0.6f, dripMin = 0.05f;
    }

    [Serializable] public class Strip   // BloodEdge.MakeStrip: one edge strip kind
    {
        public float reachBase = 0.45f, reachAmp = 0.55f, reachFreq = 3f, reachY = 0f, rowBase = 1f, rowAmp = 0f, rowFreq = 0f, rowY = 0f, pow = 1.3f;
        public bool reachSquare = false;
    }
    [Serializable] public class Edge
    {
        public Strip[] kinds = {
            new Strip(),
            new Strip { reachBase = 0.85f, reachAmp = 0f, rowBase = 0.6f, rowAmp = 0.4f, rowFreq = 5f, rowY = 7f, pow = 2.2f },
            new Strip { reachBase = 0.2f, reachAmp = 0.8f, reachFreq = 14f, reachY = 3f, reachSquare = true, rowBase = 0.5f, rowAmp = 0.5f, rowFreq = 40f, rowY = 9f, pow = 1f } };
        public float seedMul = 1.37f, indexMul = 11.3f, vFade = 0.18f, gain = 0.95f, shadeBase = 0.8f, shadeAmp = 0.4f, shadeFreq = 6f;
    }

    [Serializable] public class All { public Burst burst = new Burst(); public Wound wound = new Wound(); public Ground ground = new Ground(); public Edge edge = new Edge(); }

    static All cfg;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; }
    public static All Get()
    {
        if (cfg != null) return cfg;
        cfg = new All();
        try { string json = DataPath.ReadText("blood_shapes.json"); if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, cfg); }
        catch (Exception ex) { Debug.LogWarning("BloodShapes: blood_shapes.json unreadable (" + ex.Message + "), built-in defaults"); }
        return cfg;
    }
    public static void Reload() { cfg = null; }
}
