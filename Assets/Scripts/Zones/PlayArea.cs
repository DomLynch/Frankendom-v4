using System;
using UnityEngine;

// PLAYAREA v1 (laneZONE6): the shared per-zone play area for boss zones. ArenaLimits clamps every fighter to the 7.6 m lane (arena_limits.json x -3.8..3.8) and DuelFighter's stair clamp
// to 2.45 m, in EVERY zone, which would squeeze a 4 m boss and Joan into a corridor inside a 30 m ring. Data: play_areas.json (StreamingAssets, copy in Assets/Data), { enabled, rows[] }.
// A row covers the world z range zFrom..zTo of one zone; inside it the allowed ground = the lane (|x - laneX| <= laneHalfX, any z) UNION the area (disc: centre [x,z] + radius; rect: centre [x,z] + half [hx,hz]).
// A fighter outside both is moved to the nearest allowed point. Pure function of (x, z) and data: no time, no random, no state. enabled=false (the shipped value) or no row for that z = Row() is null = the old arithmetic exactly.
// Rows exist only for zones 4+ (world z >= 104), which the 8..22 duel harness never visits. Units metres.
[Serializable] public class PaRow
{
    public string zone = "", shape = "disc", note = "";
    public bool enabled = true;
    public float zFrom, zTo, laneX, laneHalfX = 2.45f, radius;
    public float[] centre = new float[0], half = new float[0];
}
[Serializable] public class PaFile { public bool enabled = false; public int version = 1; public PaRow[] rows = new PaRow[0]; }

public static class PlayArea
{
    static PaFile file; static bool loaded;
    static PaFile Load()
    {
        if (loaded) return file; loaded = true;
        string txt = DataPath.ReadText("play_areas.json");
        if (txt != null) { try { file = JsonUtility.FromJson<PaFile>(txt); } catch { } }
        return file;
    }
    public static PaRow Row(float z)
    {
        var d = Load(); if (d == null || !d.enabled || d.rows == null) return null;
        for (int i = 0; i < d.rows.Length; i++) { var r = d.rows[i]; if (r != null && r.enabled && z >= r.zFrom && z <= r.zTo) return r; }
        return null;
    }
    // moves (x, z) to the nearest point of lane U area; unchanged when already inside either
    public static void Project(PaRow r, ref float x, ref float z)
    {
        if (Mathf.Abs(x - r.laneX) <= r.laneHalfX) return;
        float ax = x, az = z; bool has = false;
        if (r.centre != null && r.centre.Length >= 2)
        {
            float cx = r.centre[0], cz = r.centre[1];
            if (r.shape == "rect" && r.half != null && r.half.Length >= 2) { has = true; ax = Mathf.Clamp(x, cx - r.half[0], cx + r.half[0]); az = Mathf.Clamp(z, cz - r.half[1], cz + r.half[1]); }
            else if (r.shape == "disc")
            {
                has = true; float dx = x - cx, dz = z - cz, d2 = dx * dx + dz * dz;
                if (d2 > r.radius * r.radius) { float k = r.radius / Mathf.Sqrt(d2); ax = cx + dx * k; az = cz + dz * k; }
            }
            if (has && ax == x && az == z) return;
        }
        float lx = Mathf.Clamp(x, r.laneX - r.laneHalfX, r.laneX + r.laneHalfX), dl = (x - lx) * (x - lx), da = (x - ax) * (x - ax) + (z - az) * (z - az);
        if (has && da < dl) { x = ax; z = az; } else x = lx;
    }
}
