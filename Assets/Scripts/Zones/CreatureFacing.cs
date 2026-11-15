using System;
using System.Collections.Generic;
using UnityEngine;

// FK15 item 5 (Lead): per-creature visual yaw by data. StreamingAssets/Data creature_facing.json { enabled, log, rows:[{creature,yawDeg}] } (twins in Assets\Data and Assets\StreamingAssets), read once.
// Default 0 / no row / disabled / missing file = nothing changes. The offset turns the creature's VISUAL (the Animator's own transform when it is a child, else every direct child of the root
// about the root's Y axis); the root transform that carries the collider, AI heading, aggro and hit directions is never rotated.
[Serializable] public class CreatureFacingRow { public string creature = ""; public float yawDeg = 0f; }
[Serializable] public class CreatureFacingData { public bool enabled = false, log = true; public CreatureFacingRow[] rows = new CreatureFacingRow[0]; }
public static class CreatureFacing
{
    static CreatureFacingData d; static bool loaded; static readonly HashSet<string> logged = new HashSet<string>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { d = null; loaded = false; logged.Clear(); }
    static CreatureFacingData Get()
    {
        if (loaded) return d; loaded = true;
        try { string t = DataPath.ReadText("creature_facing.json"); if (!string.IsNullOrEmpty(t)) d = JsonUtility.FromJson<CreatureFacingData>(t); } catch (Exception e) { Debug.LogWarning("[CreatureFacing] read failed: " + e.Message); }
        return d;
    }
    public static void Apply(GameObject go, string creature)
    {
        var c = Get(); if (c == null || !c.enabled || c.rows == null || go == null) return;
        float deg = 0f; bool hit = false;
        string on = go.name; int hx = on.IndexOf('#'); if (hx > 0) on = on.Substring(0, hx);   // rows match the roster id or the object name
        foreach (var r in c.rows) if (r != null && (r.creature == creature || r.creature == on)) { deg = r.yawDeg; hit = true; break; }
        if (!hit || Mathf.Abs(deg) < 0.001f) return;
        var anim = go.GetComponentInChildren<Animator>(true); string how;
        if (anim != null && anim.transform != go.transform) { anim.transform.Rotate(0f, deg, 0f, Space.World); how = "animator child " + anim.name; }
        else { foreach (Transform ch in go.transform) ch.RotateAround(go.transform.position, Vector3.up, deg); how = "all root children"; }
        if (c.log && logged.Add(creature)) Debug.Log("[CreatureFacing] " + creature + " +" + deg.ToString("F0") + " (" + how + ")");
    }
}
