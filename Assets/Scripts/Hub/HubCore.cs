using System;
using System.Collections.Generic;

// laneNPCSPAWN v1: the PURE rules of the camp (no UnityEngine, so dotnet can run them headless).
//   HubArea     : the camp rectangle in world x / z (data: hub.json area). Stateless, so the safe-zone test is a pure function of the player's position (sim-deterministic).
//   HubResident : the spawn / despawn rule for the camp's NPCs (hysteresis on the player's distance to the camp, capped, one load at a time).
//   HubClip     : where an idle loop starts (phase offset) and the wrapped play time.
// DARK: nothing here runs unless hub.json enabled=true (HubNpcs boots only then; HubSafe stays false). UNPROVEN IN UNITY.

public static class HubArea
{
    /// <summary>Inside the camp rectangle (edges included). xHalf is measured from x = 0.</summary>
    public static bool Contains(float x, float z, float zMin, float zMax, float xHalf) { return z >= zMin && z <= zMax && x >= -xHalf && x <= xHalf; }

    /// <summary>Distance along z from the camp to the player: 0 inside [zMin, zMax], else the gap to the nearest end.</summary>
    public static float GapZ(float z, float zMin, float zMax) { return z < zMin ? zMin - z : z > zMax ? z - zMax : 0f; }
}

public sealed class HubResident
{
    public float spawnMargin, despawnMargin;
    bool want;
    public bool Want { get { return want; } }
    public HubResident(float spawnMargin, float despawnMargin) { this.spawnMargin = spawnMargin; this.despawnMargin = Math.Max(despawnMargin, spawnMargin); }

    /// <summary>One poll. The NPCs are wanted once the player is within spawnMargin of the camp and kept until the player is further than despawnMargin (no flicker at the edge). `zonesOn` false (zones off) = never wanted.</summary>
    public bool Step(float playerZ, float zMin, float zMax, bool zonesOn)
    {
        if (!zonesOn) { want = false; return false; }
        float gap = HubArea.GapZ(playerZ, zMin, zMax);
        if (!want && gap <= spawnMargin) want = true;
        else if (want && gap > despawnMargin) want = false;
        return want;
    }
    public void Reset() { want = false; }
}

public static class HubClip
{
    /// <summary>Start time of a looping clip of `length` seconds for a phase offset (seconds). Negative or zero length = 0.</summary>
    public static float StartTime(float phaseSeconds, float length)
    {
        if (length <= 1e-4f) return 0f;
        float t = phaseSeconds % length; if (t < 0f) t += length; return t;
    }

    /// <summary>The first `max` ids of `rows` whose `enabled` flag is true; keeps the file order, so which NPC is dropped by a cap is data, not chance.</summary>
    public static List<int> Pick(bool[] enabled, int max)
    {
        var r = new List<int>(); if (enabled == null) return r;
        for (int i = 0; i < enabled.Length && r.Count < Math.Max(0, max); i++) if (enabled[i]) r.Add(i);
        return r;
    }
}
