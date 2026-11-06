using System;
using UnityEngine;

// ZONEFLAG v1 (laneINT107): the one launcher flag for the zone build. `-zones` = zones + chase + zone span on (the data switches stay false in the files), `-nozones` = all off even if a file says true.
// Neither flag = the data files decide, exactly as shipped. Read once from the command line. Harness runs never create the zone host, so the flag cannot move a sim hash.
public static class ZoneFlag
{
    static int state = -1;   // -1 not read, 0 none, 1 -zones, 2 -nozones
    public static bool Forced { get { Read(); return state == 1; } }
    public static bool Allowed(bool dataOn) { Read(); return state == 2 ? false : state == 1 ? true : dataOn; }
    static void Read()
    {
        if (state >= 0) return; state = 0;
        try { foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-nozones") { state = 2; break; } if (a == "-zones") state = 1; } } catch (Exception) { }
        if (state != 0) Debug.Log("[Zones] launcher flag: " + (state == 1 ? "-zones (zones, chase and zone span ON)" : "-nozones (all OFF)"));
    }
}
