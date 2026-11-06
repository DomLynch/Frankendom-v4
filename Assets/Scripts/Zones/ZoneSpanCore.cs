using System;

// ZONESPAN v1: SpanChain = the same rule for ANY number of zones (data rows); SpanCore below is the original two-zone rule, kept as the reference the self-test compares SpanChain against.
// ZONE2DUP v1: the PURE part (no UnityEngine, no Time, no Random, no files) so it can be run headless (selftest\SpanCoreTest.cs) and, like ZoneCore, reused by a server later.
// SpanCore  = which of the two physical zone roots is wanted for a player at height z, with hysteresis (no flicker at the edge), plus which zone the player counts as standing in.
// XingStats = the frame-time verdict for the crossing test (ZoneXing): worst frame, mean, soft hitches against a baseline taken in the same run.
public sealed class SpanCore
{
    public float z1Min, z1Max, z2Min, z2Max, ahead, behind, hyst, boundary;
    public bool want1 = true, want2;   // zone 1 is the loaded scene: wanted from the start
    int zone = 1;
    public int Zone { get { return zone; } }

    /// <summary>Wanted range of a lane [min,max]: widened by 'behind' toward the near end and 'ahead' toward the far end; 'slack' is the hysteresis added when the zone is already wanted.</summary>
    static bool Within(float z, float min, float max, float behind, float ahead, float slack) { return z >= min - behind - slack && z <= max + ahead + slack; }

    /// <summary>One poll. gateOpen=false keeps zone 2 unwanted. keep1/keep2 = a living monster stands in that zone (it stays wanted under it). Returns true when want1/want2/Zone changed.</summary>
    public bool Step(float z, bool gateOpen, bool keep1, bool keep2)
    {
        bool w1 = Within(z, z1Min, z1Max, behind, ahead, want1 ? hyst : 0f) || keep1;
        bool w2 = gateOpen && (Within(z, z2Min, z2Max, behind, ahead, want2 ? hyst : 0f) || keep2);
        if (!w1 && !w2) { w1 = want1; w2 = want2; }   // never both off (a player outside every range keeps what it had)
        int zn = zone;   // the zone the player counts as standing in: the boundary plane, with hysteresis on both sides
        if (!gateOpen) zn = 1; else if (zone == 1 && z >= boundary + hyst) zn = 2; else if (zone == 2 && z < boundary - hyst) zn = 1;
        bool ch = w1 != want1 || w2 != want2 || zn != zone; want1 = w1; want2 = w2; zone = zn; return ch;
    }

    /// <summary>True while both roots are wanted (the doorway walk): the only time two zones are in the frame.</summary>
    public bool Both { get { return want1 && want2; } }

    /// <summary>Ring index (0 = nearest the entry) for a distance, thresholds ascending; the last ring takes everything beyond.</summary>
    public static int RingOf(float dist, float[] cuts) { for (int i = 0; i < cuts.Length; i++) if (dist <= cuts[i]) return i; return cuts.Length; }

    /// <summary>Skyline offset along z: follow * clamp(z - fromZ, 0, offset).</summary>
    public static float SkylineOffset(float z, float fromZ, float offset, float follow) { return follow * Math.Max(0f, Math.Min(offset, z - fromZ)); }
}

/// <summary>ZONESPAN v1: the SpanCore rule for N zones in a line (zone 0 = the loaded scene, zone i = a duplicate or a safe zone at its own offset). With N = 2 it gives exactly SpanCore's answers (self-test compares them step by step).
/// open[i] = the gate INTO zone i is open (open[0] unused). A zone is reachable only if every gate before it is open. keep[i] = a living fighter stands in zone i.</summary>
public sealed class SpanChain
{
    public readonly int Count; public float[] min, max, boundary; public float ahead, behind, hyst;
    public readonly bool[] want; readonly bool[] tmp; int zone; public int Zone { get { return zone; } }
    public SpanChain(int n) { Count = n; min = new float[n]; max = new float[n]; boundary = new float[n]; want = new bool[n]; tmp = new bool[n]; want[0] = true; }
    static bool Within(float z, float lo, float hi, float behind, float ahead, float slack) { return z >= lo - behind - slack && z <= hi + ahead + slack; }
    public int WantedCount { get { int c = 0; for (int i = 0; i < Count; i++) if (want[i]) c++; return c; } }
    public bool Step(float z, bool[] open, bool[] keep)
    {
        bool changed = false, any = false; bool reach = true; var w = tmp;
        for (int i = 0; i < Count; i++)
        {
            if (i > 0) reach = reach && open[i];
            w[i] = reach && (Within(z, min[i], max[i], behind, ahead, want[i] ? hyst : 0f) || keep[i]); any |= w[i];
        }
        if (!any) for (int i = 0; i < Count; i++) w[i] = want[i];   // never all off
        for (int i = 0; i < Count; i++) { if (w[i] != want[i]) changed = true; want[i] = w[i]; }
        int zn = zone;
        while (zn > 0 && (!open[zn] || z < boundary[zn] - hyst)) zn--;
        if (zn == zone) while (zn + 1 < Count && open[zn + 1] && z >= boundary[zn + 1] + hyst) zn++;
        if (zn != zone) { changed = true; zone = zn; }
        return changed;
    }
    /// <summary>Where a follower must stop in front of a safe zone's door: the door face minus stopBehind. Returns z unchanged when the gate is not a safe destination.</summary>
    public static float FollowerZ(float z, float frontZ, float stopBehind, bool destSafe) { float stop = frontZ - stopBehind; return destSafe && z > stop ? stop : z; }
}

[Serializable] public struct XingVerdict
{
    public bool pass; public string why; public int frames, softHitches; public float baseMedian, baseP99, worst, mean, limitWorst, limitMean;
}

public static class XingStats
{
    /// <summary>Nearest-rank percentile (p in 0..100) of a copy of a, in the units of a.</summary>
    public static float Pct(float[] a, float p)
    {
        if (a == null || a.Length == 0) return 0f; var s = (float[])a.Clone(); Array.Sort(s);
        int i = (int)Math.Ceiling(p / 100.0 * s.Length) - 1; if (i < 0) i = 0; if (i >= s.Length) i = s.Length - 1; return s[i];
    }
    public static float Mean(float[] a) { if (a == null || a.Length == 0) return 0f; double t = 0; foreach (var v in a) t += v; return (float)(t / a.Length); }

    /// <summary>baseline = frame ms standing still in the start zone; window = frame ms across one crossing. Limits (data, zone_span.json test): worst &lt;= baseP99 + extraMs; mean &lt;= baseMedian * meanFactor; frames above baseP99 + softExtraMs at most maxSoft.</summary>
    public static XingVerdict Eval(float[] baseline, float[] window, float extraMs, float meanFactor, float softExtraMs, int maxSoft)
    {
        var v = new XingVerdict(); v.frames = window == null ? 0 : window.Length;
        if (baseline == null || baseline.Length < 5 || window == null || window.Length < 2) { v.pass = false; v.why = "not enough frames (baseline " + (baseline == null ? 0 : baseline.Length) + ", window " + v.frames + ")"; return v; }
        v.baseMedian = Pct(baseline, 50f); v.baseP99 = Pct(baseline, 99f); v.mean = Mean(window);
        v.limitWorst = v.baseP99 + extraMs; v.limitMean = v.baseMedian * meanFactor;
        foreach (var f in window) { if (f > v.worst) v.worst = f; if (f > v.baseP99 + softExtraMs) v.softHitches++; }
        string why = "";
        if (v.worst > v.limitWorst) why += "worst frame " + v.worst.ToString("F1") + " ms > limit " + v.limitWorst.ToString("F1") + "; ";
        if (v.mean > v.limitMean) why += "window mean " + v.mean.ToString("F1") + " ms > limit " + v.limitMean.ToString("F1") + "; ";
        if (v.softHitches > maxSoft) why += v.softHitches + " frames above baseline p99 + " + softExtraMs.ToString("F0") + " ms (max " + maxSoft + "); ";
        v.pass = why.Length == 0; v.why = v.pass ? "ok" : why.TrimEnd(' ', ';'); return v;
    }
}
