using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// WALKPROBE v1 (laneWALKAUDIT): a walkability grid probe for zones 4-7. DEFAULT OFF: runs only with the launch flag -walkProbe (or walk_probe.json "enabled": true). Data: walk_probe.json (all numbers).
// Once, startAfterS seconds after the scene load, it samples a grid (x xMin..xMax, z zMin..zMax) and writes <out>/walk_probe.csv + a summary in the player log ("[WalkProbe] ..."):
//   live   = a physics ray from rayFromY straight down (fighters / triggers ignored): the first collider, its name and y (none = HOLE: nothing to stand on right now);
//   static = the cell lies inside a BoxCollider whose object name starts with groundNamePrefix (active or NOT: rings that are switched off are tested by their transform), top y and name.
//            live=NO and static=YES is a RING_OFF_HOLE (wake margins); static=NO is a plain HOLE (no collider box at all);
//   gap    = live y - walkY, flagged GAP when beyond walkTolM (floor not at the walking plane);
//   wall   = a capsule cast of the fighter size one step toward +z hits a non-fighter collider within stepM (blocked-forward);
//   clamp  = what the movement clamps do to that point: PlayArea.Row / Project, the x +-3.8 lane / stair clamp where no play area row exists, ArenaLimits.ZoneZMax (CLAMPED = the cell is not walkable).
// The probe never moves the player and never edits a collider. Switch off = leave the flag out (the json ships enabled=false).
public sealed class WalkProbe : MonoBehaviour
{
    [Serializable] class Cfg
    {
        public bool enabled = false; public float startAfterS = 25f, rayFromY = 60f, rayLenM = 120f, walkY = 0f, walkTolM = 0.15f;
        public float xMin = -45f, xMax = 45f, xStep = 3f, zMin = 98f, zMax = 380f, zStep = 3f;
        public float capsuleRadius = 0.35f, capsuleBottomY = 0.4f, capsuleTopY = 1.6f, stepM = 2f;
        public string groundNamePrefix = "GroundCollider"; public string outDir = ""; public bool quitWhenDone = false;
    }
    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.Ordinal)) return a.Substring(p.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Cfg c = null; string t = DataPath.ReadText("walk_probe.json"); if (t != null) { try { c = JsonUtility.FromJson<Cfg>(t); } catch { } }
        if (c == null) c = new Cfg(); if (Flag("-walkProbe")) c.enabled = true; if (!c.enabled) return;
        string o = Arg("-walkProbeOut:"); if (!string.IsNullOrEmpty(o)) c.outDir = o;
        var go = new GameObject("WalkProbe"); DontDestroyOnLoad(go); go.AddComponent<WalkProbe>().C = c;
    }
    Cfg C; float t0; bool done;
    void Start() { t0 = Time.realtimeSinceStartup; }
    void Update() { if (!done && C != null && Time.realtimeSinceStartup - t0 >= C.startAfterS) { done = true; try { Run(); } catch (Exception e) { Debug.LogWarning("[WalkProbe] failed: " + e); } if (C.quitWhenDone) Application.Quit(); } }

    struct Box { public Vector3 c, h; public string name; public bool active; }
    static string F(float v) { return float.IsNaN(v) ? "NaN" : v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }

    void Run()
    {
        var boxes = new List<Box>();
        foreach (var b in FindObjectsByType<BoxCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!b.gameObject.name.StartsWith(C.groundNamePrefix, StringComparison.Ordinal)) continue;
            var t = b.transform; var s = t.lossyScale; var sz = new Vector3(b.size.x * Mathf.Abs(s.x), b.size.y * Mathf.Abs(s.y), b.size.z * Mathf.Abs(s.z));
            boxes.Add(new Box { c = t.TransformPoint(b.center), h = sz * 0.5f, name = b.gameObject.name, active = b.gameObject.activeInHierarchy });
        }
        string dir = string.IsNullOrEmpty(C.outDir) ? Application.persistentDataPath : C.outDir; try { Directory.CreateDirectory(dir); } catch { }
        var sb = new StringBuilder("x,z,liveHit,liveY,liveName,staticHit,staticY,staticName,staticActive,gapM,wall,wallName,clampMoved,clampDx,clampDz,verdict\n");
        int cells = 0, holes = 0, ringOff = 0, plain = 0, gaps = 0, walls = 0, clamped = 0;
        var perZone = new SortedDictionary<int, int[]>();   // z band of 10 m -> [cells, noLiveGround, walls, clamped]
        var hits = new RaycastHit[32];
        for (float z = C.zMin; z <= C.zMax + 1e-3f; z += C.zStep)
            for (float x = C.xMin; x <= C.xMax + 1e-3f; x += C.xStep)
            {
                cells++; int n = Physics.RaycastNonAlloc(new Vector3(x, C.rayFromY, z), Vector3.down, hits, C.rayLenM, ~0, QueryTriggerInteraction.Ignore);
                float ly = float.NaN, best = float.MaxValue; string ln = "";
                for (int i = 0; i < n; i++) { var col = hits[i].collider; if (col == null || col.GetComponentInParent<DuelFighter>() != null) continue; if (hits[i].distance < best) { best = hits[i].distance; ly = hits[i].point.y; ln = col.name; } }
                bool live = best < float.MaxValue; float sy = float.NaN; string sn = ""; bool sa = false, st = false;
                foreach (var b in boxes) if (Mathf.Abs(x - b.c.x) <= b.h.x && Mathf.Abs(z - b.c.z) <= b.h.z) { float top = b.c.y + b.h.y; if (!st || top > sy) { st = true; sy = top; sn = b.name; sa = b.active; } }
                float gap = live ? ly - C.walkY : float.NaN; bool wall = false; string wn = "";
                if (live)
                {
                    var p1 = new Vector3(x, ly + C.capsuleBottomY, z); var p2 = new Vector3(x, ly + C.capsuleTopY, z);
                    RaycastHit h; if (Physics.CapsuleCast(p1, p2, C.capsuleRadius, Vector3.forward, out h, C.stepM, ~0, QueryTriggerInteraction.Ignore) && h.collider.GetComponentInParent<DuelFighter>() == null) { wall = true; wn = h.collider.name; }
                }
                float cx = x, cz = z;
                var pa = PlayArea.Row(z);
                if (pa != null) PlayArea.Project(pa, ref cx, ref cz);
                else { cx = Mathf.Clamp(x, -3.8f, 3.8f); if (Mathf.Abs(x) > ArenaLimits.StairHalfWidthX && z > ArenaLimits.StairZMin && z < ArenaLimits.StairZMax) cx = Mathf.Sign(x) * ArenaLimits.StairHalfWidthX; if (Mathf.Abs(x) < Mathf.Abs(cx)) cx = x; }
                if (!float.IsNaN(ArenaLimits.ZoneZMax) && cz > ArenaLimits.ZoneZMax) cz = ArenaLimits.ZoneZMax;
                bool moved = Mathf.Abs(cx - x) > 1e-3f || Mathf.Abs(cz - z) > 1e-3f;
                string v = !live && st ? "RING_OFF_HOLE" : !live ? "HOLE" : Mathf.Abs(gap) > C.walkTolM ? "GAP" : wall ? "WALL" : "ok"; if (moved && v == "ok") v = "CLAMPED";
                if (v == "RING_OFF_HOLE") { ringOff++; holes++; } else if (v == "HOLE") { plain++; holes++; } else if (v == "GAP") gaps++;
                if (wall) walls++; if (moved) clamped++;
                int band = Mathf.FloorToInt(z / 10f) * 10; int[] bz; if (!perZone.TryGetValue(band, out bz)) { bz = new int[4]; perZone[band] = bz; }
                bz[0]++; if (!live) bz[1]++; if (wall) bz[2]++; if (moved) bz[3]++;
                sb.Append(F(x)).Append(',').Append(F(z)).Append(',').Append(live ? 1 : 0).Append(',').Append(F(ly)).Append(',').Append(ln).Append(',').Append(st ? 1 : 0).Append(',').Append(F(sy)).Append(',').Append(sn).Append(',').Append(sa ? 1 : 0).Append(',').Append(F(gap)).Append(',').Append(wall ? 1 : 0).Append(',').Append(wn).Append(',').Append(moved ? 1 : 0).Append(',').Append(F(cx - x)).Append(',').Append(F(cz - z)).Append(',').Append(v).Append('\n');
            }
        string path = Path.Combine(dir, "walk_probe.csv"); try { File.WriteAllText(path, sb.ToString()); } catch (Exception e) { Debug.LogWarning("[WalkProbe] cannot write " + path + ": " + e.Message); }
        Debug.Log("[WalkProbe] " + cells + " cells, holes " + holes + " (ring-off " + ringOff + ", no collider box " + plain + "), floor gaps " + gaps + ", blocked-forward " + walls + ", moved by the clamps " + clamped + ", ground boxes " + boxes.Count + ", ArenaLimits.ZoneZMax " + F(ArenaLimits.ZoneZMax) + " -> " + path);
        foreach (var kv in perZone) Debug.Log("[WalkProbe] z " + kv.Key + ".." + (kv.Key + 10) + ": cells " + kv.Value[0] + " noLiveGround " + kv.Value[1] + " wall " + kv.Value[2] + " clamped " + kv.Value[3]);
        foreach (var b in boxes) Debug.Log("[WalkProbe] box " + b.name + " centre " + b.c.ToString("F1") + " halfsize " + b.h.ToString("F1") + " top y " + F(b.c.y + b.h.y) + " active " + b.active);
    }
}
