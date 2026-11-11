using System;
using UnityEngine;

// DATAWATCH v1 (laneDATA1): CampLights had no Reload(). This adds one (partial class; CampLights.cs only gains the word `partial`): re-reads camp_lights.json and rebuilds the lights from it.
// Same rules as Boot: only with the zones on (ZoneSave.Active). Not running yet (the file was enabled:false at boot) -> Boot() decides from the new file. Running -> the old lights are destroyed and the rows rebuilt
// (Build waits waitFrames, so a change shows about a third of a second later); enabled:false -> the lights go away. A file that does not parse keeps the lights that are on.
public sealed partial class CampLights
{
    public static void Reload()
    {
        if (!ZoneSave.Active) return;
        if (inst == null) { Boot(); return; }
        string txt = DataPath.ReadText("camp_lights.json");
        if (txt == null) { inst.StopAllCoroutines(); inst.Clear(); inst.D = new CampLightData(); return; }
        CampLightData d; try { d = JsonUtility.FromJson<CampLightData>(txt); } catch (Exception e) { Debug.LogWarning("[CampLights] Reload: camp_lights.json unreadable, keeping the current lights: " + e.Message); return; }
        if (d == null) return;
        inst.StopAllCoroutines(); inst.Clear(); inst.D = d;
        if (d.enabled && d.rows != null && d.rows.Length > 0) inst.StartCoroutine(inst.Build());
    }
}
