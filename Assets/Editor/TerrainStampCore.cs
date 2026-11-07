using System;
using System.Collections.Generic;

// TERRAINSTAMP v2 core (env-dev): PURE C#, no UnityEngine, so it compiles in the Unity Editor assembly AND in a standalone console test (test\StampTest.cs) that runs it on the real terrain GLB.
// Input: world-space terrain chunks + stamp windows. Output: new vertex heights + welded normals, or ok=false with the reason and NOTHING else (no side effects, no scene access).
// Rule per window: for every vertex with |x| < xHalf and zFrom < z - offsetZ < zTo and weight w > 0:  y += w * (zone1Height(x, z - offsetZ) + yBias - y); w = smoothstep falloff 1 -> 0 over blend m at the window edge.
// zone1Height comes from the PRISTINE triangles (heights are never read from already moved vertices), so windows are order independent per vertex apart from the sequential lerp where two windows overlap.
public static class TerrainStampCore
{
    public class Chunk { public string name; public float[] x, y, z; public int[][] tris; }
    public class Win { public string id; public float offsetZ, xHalf, zFrom, zTo, blend, yBias; }
    public class Result
    {
        public bool ok; public string error = "";
        public float[][] newY; public bool[][] moved, touched; public float[][] nx, ny, nz;   // per chunk, per vertex
        public Dictionary<string, int> movedPerWin = new Dictionary<string, int>(); public Dictionary<string, float> coverage = new Dictionary<string, float>();
        public float maxDelta; public int chunksChanged, vertsRewritten;
    }

    public static float Smooth(float t) { if (t < 0f) t = 0f; if (t > 1f) t = 1f; return t * t * (3f - 2f * t); }
    public static float Weight(Win w, float x, float zl)
    {
        float b = w.blend < 0.01f ? 0.01f : w.blend;
        return Smooth((w.xHalf - Math.Abs(x)) / b) * Smooth((zl - w.zFrom) / b) * Smooth((w.zTo - zl) / b);
    }

    static Result Fail(string why) { return new Result { ok = false, error = why }; }
    static long Key(float x, float y, float z)
    {
        long a = (long)Math.Round(x * 100.0) + 1048576, b = (long)Math.Round(y * 100.0) + 1048576, c = (long)Math.Round(z * 100.0) + 1048576;
        return ((a & 0x1FFFFF) << 42) | ((b & 0x1FFFFF) << 21) | (c & 0x1FFFFF);
    }

    /// <summary>Validate everything, then compute. minCoverage: share of the window's candidate vertices that must find a zone-1 source height. maxDelta: sanity limit on any vertex move (m).</summary>
    public static Result Compute(List<Chunk> cs, List<Win> ws, float minCoverage, float maxDelta)
    {
        if (cs == null || cs.Count == 0) return Fail("no terrain chunks");
        if (ws == null || ws.Count == 0) return Fail("no stamp windows");
        foreach (var c in cs)
        {
            if (c == null || c.x == null || c.y == null || c.z == null || c.tris == null) return Fail("chunk data missing");
            int n = c.x.Length; if (c.y.Length != n || c.z.Length != n) return Fail("chunk " + c.name + ": vertex arrays differ in length");
            for (int i = 0; i < n; i++) if (float.IsNaN(c.x[i]) || float.IsNaN(c.y[i]) || float.IsNaN(c.z[i]) || float.IsInfinity(c.y[i])) return Fail("chunk " + c.name + ": NaN/Inf vertex " + i);
            foreach (var t in c.tris) { if (t.Length % 3 != 0) return Fail("chunk " + c.name + ": index count not a multiple of 3"); foreach (int q in t) if (q < 0 || q >= n) return Fail("chunk " + c.name + ": index out of range"); }
        }
        foreach (var w in ws)
        {
            if (w == null || !(w.xHalf > 0f) || !(w.zTo > w.zFrom) || w.blend < 0f) return Fail("window " + (w != null ? w.id : "null") + ": bad numbers");
            if (w.zFrom - 0f < -500f || w.zTo > 500f || w.xHalf > 500f) return Fail("window " + w.id + ": out of range");
        }
        // pristine triangles near any SOURCE window (local coordinates), 4 m grid
        const float G = 4f; var tri = new List<float[]>(); var grid = new Dictionary<long, List<int>>();
        foreach (var c in cs)
            foreach (var idx in c.tris)
                for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    int a = idx[k], b = idx[k + 1], d = idx[k + 2];
                    float x0 = Math.Min(c.x[a], Math.Min(c.x[b], c.x[d])), x1 = Math.Max(c.x[a], Math.Max(c.x[b], c.x[d])), z0 = Math.Min(c.z[a], Math.Min(c.z[b], c.z[d])), z1 = Math.Max(c.z[a], Math.Max(c.z[b], c.z[d]));
                    bool any = false; foreach (var w in ws) if (x1 >= -w.xHalf - G && x0 <= w.xHalf + G && z1 >= w.zFrom - G && z0 <= w.zTo + G) { any = true; break; }
                    if (!any) continue;
                    int id = tri.Count; tri.Add(new[] { c.x[a], c.y[a], c.z[a], c.x[b], c.y[b], c.z[b], c.x[d], c.y[d], c.z[d] });
                    for (int gx = (int)Math.Floor(x0 / G); gx <= (int)Math.Floor(x1 / G); gx++)
                        for (int gz = (int)Math.Floor(z0 / G); gz <= (int)Math.Floor(z1 / G); gz++)
                        { long kk = ((long)gx << 32) ^ (uint)gz; List<int> l; if (!grid.TryGetValue(kk, out l)) { l = new List<int>(); grid[kk] = l; } l.Add(id); }
                }
        if (tri.Count == 0) return Fail("no source triangles in any window");
        Func<float, float, float?> H = delegate (float x, float z)
        {
            List<int> l; if (!grid.TryGetValue(((long)(int)Math.Floor(x / G) << 32) ^ (uint)(int)Math.Floor(z / G), out l)) return null; float? best = null;
            foreach (int i in l)
            {
                var t = tri[i]; float dd = (t[5] - t[8]) * (t[0] - t[6]) + (t[6] - t[3]) * (t[2] - t[8]); if (Math.Abs(dd) < 1e-12f) continue;
                float l1 = ((t[5] - t[8]) * (x - t[6]) + (t[6] - t[3]) * (z - t[8])) / dd, l2 = ((t[8] - t[2]) * (x - t[6]) + (t[0] - t[6]) * (z - t[8])) / dd, l3 = 1f - l1 - l2;
                if (l1 < -1e-5f || l2 < -1e-5f || l3 < -1e-5f) continue;
                float h = l1 * t[1] + l2 * t[4] + l3 * t[7]; if (best == null || h > best.Value) best = h;
            }
            return best;
        };
        var R = new Result { ok = true, newY = new float[cs.Count][], moved = new bool[cs.Count][], touched = new bool[cs.Count][], nx = new float[cs.Count][], ny = new float[cs.Count][], nz = new float[cs.Count][] };
        for (int ci = 0; ci < cs.Count; ci++) { R.newY[ci] = (float[])cs[ci].y.Clone(); R.moved[ci] = new bool[cs[ci].y.Length]; R.touched[ci] = new bool[cs[ci].y.Length]; R.nx[ci] = new float[cs[ci].y.Length]; R.ny[ci] = new float[cs[ci].y.Length]; R.nz[ci] = new float[cs[ci].y.Length]; }
        foreach (var w in ws)
        {
            int cand = 0, found = 0, moved = 0;
            for (int ci = 0; ci < cs.Count; ci++)
            {
                var c = cs[ci];
                for (int i = 0; i < c.x.Length; i++)
                {
                    float zl = c.z[i] - w.offsetZ; if (Math.Abs(c.x[i]) >= w.xHalf || zl <= w.zFrom || zl >= w.zTo) continue;
                    float wt = Weight(w, c.x[i], zl); if (wt <= 0f) continue;
                    cand++; var tgt = H(c.x[i], zl); if (tgt == null) continue; found++;
                    float d = wt * (tgt.Value + w.yBias - R.newY[ci][i]); if (Math.Abs(d) > R.maxDelta) R.maxDelta = Math.Abs(d);
                    R.newY[ci][i] += d; R.moved[ci][i] = true; moved++;
                }
            }
            if (cand == 0) return Fail("window " + w.id + ": no terrain vertex inside the window (wrong offset or numbers?)");
            float cov = (float)found / cand; R.coverage[w.id] = cov; R.movedPerWin[w.id] = moved;
            if (cov < minCoverage) return Fail("window " + w.id + ": only " + (cov * 100f).ToString("0.0") + "% of its vertices found a zone-1 source height (need " + (minCoverage * 100f).ToString("0.0") + "%)");
        }
        if (R.maxDelta > maxDelta) return Fail("largest vertex move " + R.maxDelta.ToString("0.00") + " m exceeds the sanity limit " + maxDelta.ToString("0.00") + " m");
        // welded normals over ALL chunks (chunk borders duplicate vertices), for vertices of triangles that touch a moved vertex
        var acc = new Dictionary<long, float[]>();
        for (int ci = 0; ci < cs.Count; ci++)
        {
            var c = cs[ci];
            foreach (var idx in c.tris)
                for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    int a = idx[k], b = idx[k + 1], d = idx[k + 2];
                    float ax = c.x[a], ay = R.newY[ci][a], az = c.z[a], bx = c.x[b], by = R.newY[ci][b], bz = c.z[b], dx = c.x[d], dy = R.newY[ci][d], dz = c.z[d];
                    float ux = bx - ax, uy = by - ay, uz = bz - az, vx = dx - ax, vy = dy - ay, vz = dz - az;
                    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx; if (ny < 0f) { nx = -nx; ny = -ny; nz = -nz; }
                    long[] ks = { Key(ax, ay, az), Key(bx, by, bz), Key(dx, dy, dz) };
                    foreach (long kk in ks) { float[] o; if (!acc.TryGetValue(kk, out o)) { o = new float[3]; acc[kk] = o; } o[0] += nx; o[1] += ny; o[2] += nz; }
                    if (R.moved[ci][a] || R.moved[ci][b] || R.moved[ci][d]) { R.touched[ci][a] = R.touched[ci][b] = R.touched[ci][d] = true; }
                }
        }
        for (int ci = 0; ci < cs.Count; ci++)
        {
            bool any = false; var c = cs[ci];
            for (int i = 0; i < c.x.Length; i++)
            {
                if (!R.moved[ci][i] && !R.touched[ci][i]) continue; any = true; R.vertsRewritten++;
                float[] o; if (!acc.TryGetValue(Key(c.x[i], R.newY[ci][i], c.z[i]), out o)) continue; float l = (float)Math.Sqrt(o[0] * o[0] + o[1] * o[1] + o[2] * o[2]); if (l < 1e-9f) continue;
                R.nx[ci][i] = o[0] / l; R.ny[ci][i] = o[1] / l; R.nz[ci][i] = o[2] / l;
            }
            if (any) R.chunksChanged++;
        }
        return R;
    }
}
