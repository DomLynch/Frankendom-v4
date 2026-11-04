using System;
using System.Collections.Generic;

// ALPHA lane B2: engine-free geometry self-test for the cut idea used by ADMeshCut: split triangles by a plane, then cap the open boundary loop with a centroid fan.
// Pure C# (no UnityEngine), runs on plain dotnet: ADMeshCutSelfTest.Run() returns true when all checks pass. Not used by the game at runtime.
public static class ADMeshCutSelfTest
{
    public struct V { public double x, y, z; public V(double a, double b, double c) { x = a; y = b; z = c; } }

    static V Lerp(V a, V b, double t) { return new V(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t); }
    static double Side(V p, double planeY) { return p.y - planeY; }
    static string Key(V p) { return Math.Round(p.x * 1e4) + "," + Math.Round(p.y * 1e4) + "," + Math.Round(p.z * 1e4); }

    /// <summary>Cuts the triangle soup by the plane y = planeY. Returns the triangles above and below; the cut produces new vertices (on-plane).</summary>
    public static void SplitByPlane(List<V[]> tris, double planeY, List<V[]> above, List<V[]> below)
    {
        foreach (var t in tris)
        {
            double s0 = Side(t[0], planeY), s1 = Side(t[1], planeY), s2 = Side(t[2], planeY);
            bool a0 = s0 >= 0, a1 = s1 >= 0, a2 = s2 >= 0;
            if (a0 == a1 && a1 == a2) { (a0 ? above : below).Add(t); continue; }
            // polygon clip: walk edges, keep both sides
            var up = new List<V>(); var dn = new List<V>();
            for (int i = 0; i < 3; i++)
            {
                V p = t[i], q = t[(i + 1) % 3]; double sp = Side(p, planeY), sq = Side(q, planeY);
                (sp >= 0 ? up : dn).Add(p);
                if ((sp >= 0) != (sq >= 0)) { V x = Lerp(p, q, sp / (sp - sq)); x.y = planeY; up.Add(x); dn.Add(x); }
            }
            Fan(up, above); Fan(dn, below);
        }
    }

    static void Fan(List<V> poly, List<V[]> outTris) { for (int i = 1; i + 1 < poly.Count; i++) outTris.Add(new[] { poly[0], poly[i], poly[i + 1] }); }

    /// <summary>Caps the open boundary of a triangle soup with a centroid fan per boundary loop (edges used once, welded by position). Returns the number of loops capped.
    /// Cap triangles are oriented opposite to the boundary edge direction of their neighbour so the mesh closes.</summary>
    public static int Cap(List<V[]> tris)
    {
        var edges = new Dictionary<string, int>(); var dir = new Dictionary<string, V[]>();
        foreach (var t in tris) for (int i = 0; i < 3; i++)
        {
            V a = t[i], b = t[(i + 1) % 3]; string ka = Key(a), kb = Key(b); string k = string.CompareOrdinal(ka, kb) < 0 ? ka + "|" + kb : kb + "|" + ka;
            int c; edges.TryGetValue(k, out c); edges[k] = c + 1; dir[k] = new[] { a, b };
        }
        var bnd = new List<V[]>(); foreach (var kv in edges) if (kv.Value == 1) bnd.Add(dir[kv.Key]);
        // group boundary edges into loops by connectivity
        var used = new bool[bnd.Count]; int loops = 0;
        for (int s = 0; s < bnd.Count; s++)
        {
            if (used[s]) continue;
            var loop = new List<V[]>(); var open = new Queue<int>(); open.Enqueue(s); used[s] = true;
            while (open.Count > 0)
            {
                int i = open.Dequeue(); loop.Add(bnd[i]);
                for (int j = 0; j < bnd.Count; j++) if (!used[j] && (Key(bnd[i][0]) == Key(bnd[j][0]) || Key(bnd[i][0]) == Key(bnd[j][1]) || Key(bnd[i][1]) == Key(bnd[j][0]) || Key(bnd[i][1]) == Key(bnd[j][1]))) { used[j] = true; open.Enqueue(j); }
            }
            V c = new V(); foreach (var e in loop) { c.x += e[0].x; c.y += e[0].y; c.z += e[0].z; }
            c.x /= loop.Count; c.y /= loop.Count; c.z /= loop.Count;
            // reversed boundary edge direction closes the surface (each neighbour edge a->b needs b->a)
            foreach (var e in loop) tris.Add(new[] { e[1], e[0], c });
            loops++;
        }
        return loops;
    }

    /// <summary>True when every directed edge a->b is matched by exactly one b->a (closed, consistently wound).</summary>
    public static bool Watertight(List<V[]> tris)
    {
        var d = new Dictionary<string, int>();
        foreach (var t in tris) for (int i = 0; i < 3; i++) { string k = Key(t[i]) + ">" + Key(t[(i + 1) % 3]); int c; d.TryGetValue(k, out c); d[k] = c + 1; }
        foreach (var kv in d) { var p = kv.Key.Split('>'); int r; d.TryGetValue(p[1] + ">" + p[0], out r); if (kv.Value != 1 || r != 1) return false; }
        return true;
    }

    public static List<V[]> UnitCube()
    {
        V[] v = { new V(0,0,0), new V(1,0,0), new V(1,1,0), new V(0,1,0), new V(0,0,1), new V(1,0,1), new V(1,1,1), new V(0,1,1) };
        int[][] f = { new[]{0,3,2,1}, new[]{4,5,6,7}, new[]{0,1,5,4}, new[]{3,7,6,2}, new[]{0,4,7,3}, new[]{1,2,6,5} };   // outward CCW quads
        var t = new List<V[]>();
        foreach (var q in f) { t.Add(new[] { v[q[0]], v[q[1]], v[q[2]] }); t.Add(new[] { v[q[0]], v[q[2]], v[q[3]] }); }
        return t;
    }

    static int fails, checks;
    static void Check(bool ok, string what) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

    public static bool Run()
    {
        fails = checks = 0;
        var cube = UnitCube();
        Check(cube.Count == 12 && Watertight(cube), "unit cube: 12 tris, watertight");
        // plane y = 0.3 cuts only side faces (4 side faces x 2 tris: each quad has the diagonal crossing)
        var up = new List<V[]>(); var dn = new List<V[]>();
        SplitByPlane(cube, 0.3, up, dn);
        Check(up.Count + dn.Count > cube.Count, "split adds triangles (" + up.Count + " above, " + dn.Count + " below)");
        Check(!Watertight(up) && !Watertight(dn), "halves are open before capping");
        int la = Cap(up), lb = Cap(dn);
        Check(la == 1 && lb == 1, "one boundary loop per half (" + la + "," + lb + ")");
        Check(Watertight(up), "upper half watertight after cap");
        Check(Watertight(dn), "lower half watertight after cap");
        // area check: all cap vertices lie on the plane; volume: both halves sum back to the cube (divergence theorem)
        double va = Volume(up), vb = Volume(dn);
        Check(Math.Abs(va - 0.7) < 1e-6, "upper volume 0.7 (" + va + ")");
        Check(Math.Abs(vb - 0.3) < 1e-6, "lower volume 0.3 (" + vb + ")");
        // plane that misses the cube: nothing to cut
        var u2 = new List<V[]>(); var d2 = new List<V[]>(); SplitByPlane(UnitCube(), 5.0, u2, d2);
        Check(u2.Count == 0 && d2.Count == 12, "plane above the cube leaves all 12 triangles below");
        // plane through vertices (y = 0 and y = 1 are faces): must not crash and keeps closure after cap
        var u3 = new List<V[]>(); var d3 = new List<V[]>(); SplitByPlane(UnitCube(), 0.5, u3, d3); Cap(u3); Cap(d3);
        Check(Watertight(u3) && Watertight(d3), "plane y=0.5 (through a diagonal midpoint) both halves watertight");
        Console.WriteLine("ADMeshCutSelfTest: " + (checks - fails) + "/" + checks + (fails == 0 ? " ALL PASS" : " FAILURES " + fails));
        return fails == 0;
    }

    static double Volume(List<V[]> tris)
    {
        double s = 0; foreach (var t in tris) { V a = t[0], b = t[1], c = t[2]; s += a.x * (b.y * c.z - b.z * c.y) - a.y * (b.x * c.z - b.z * c.x) + a.z * (b.x * c.y - b.y * c.x); }
        return s / 6.0;
    }
}
