using UnityEngine;

// STALL v1 (lane laneSTALL): OPTIONAL bridge, installed only when the zone data (laneZONES: Scripts/Zones/ZoneData.cs) exists in the project. It lets the stall find its place from the
// zones.json spot (market.json stall.anchor zone + spot) so the position is not kept in two files. Without this file the stall uses stall.anchor x / z from market.json.
public static class MarketZoneBridge
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void Hook() { MarketWorld.ResolveSpot = Resolve; }

    static bool Resolve(string zone, string spot, out float x, out float z)
    {
        x = z = 0f;
        var zz = ZoneData.Zone(zone); if (zz == null) return false;
        var sp = ZoneData.Spot(zz, spot); if (sp == null || sp.pos == null || sp.pos.Length < 2) return false;
        x = sp.pos[0]; z = sp.pos[1]; return true;
    }
}
