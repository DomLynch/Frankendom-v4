using System;

// MARKET2 v1 (pool-laneMARKET2CODE): which stall of which camp the player is standing in. PURE (no UnityEngine), so the selftest compiles it with plain dotnet next to MarketCore.cs.
// One MarketArea per stall (the main stall first, then market.json stalls[]), every area keeps its own enter / leave hysteresis. Of the areas the player is inside, the stall whose anchor is nearest wins
// (ties go to the lower index = the main stall). With ONE area this is exactly MarketArea.Update: 0 when inside, -1 when not (the selftest compares it with the old call over a random walk).
public static class MarketStallPick
{
    /// <summary>Updates every area (all of them, every call: no early exit, so each keeps its hysteresis) and returns the picked index, -1 = inside none. ax / az = the stall anchors (may be null with one area).</summary>
    public static int Update(MarketArea[] areas, float[] ax, float[] az, float x, float z)
    {
        int pick = -1; float best = float.MaxValue; if (areas == null) return pick;
        for (int i = 0; i < areas.Length; i++)
        {
            var a = areas[i]; if (a == null || !a.Update(x, z)) continue;
            float d = (ax != null && az != null && i < ax.Length && i < az.Length) ? MarketArea.Dist(x, z, ax[i], az[i]) : 0f;
            if (pick < 0 || d < best) { pick = i; best = d; }
        }
        return pick;
    }
}
