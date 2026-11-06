using System;
using System.Runtime.InteropServices;
using UnityEngine;

// WOUNDGATE v105: the pure helpers behind the WoundShot gate fixes (WoundShot.cs calls them; nothing here touches the sim, the scene or any asset).
//  1) MemProbe: a real process memory probe. v103 logged "workingSet 0.00 GB" on every line because System.Diagnostics.Process.WorkingSet64 returns 0 in the Mono player, so the 12 GB cap and the 37 GB
//     decal-leak guard measured nothing. Order: kernel32 K32GetProcessMemoryInfo (working set + private bytes) -> Process -> Unity's own reserved memory (approximate, labelled). A probe that reads under
//     woundshot.json memProbeMinGB is a FAIL ("probe dead"), never a silent pass.
//  2) Visibility: geometry proof that a decal is inside a camera's frame AND not behind the victim's own body / a wall (v103: the bounds-only "inView" was true for Joan's head wound while the close camera looked
//     at the back of her head). Pure maths on a candidate camera pose + Physics.RaycastAll from the camera to samples along the decal; used both to pick a close-camera pose and as a gate.
//  3) Diff: the pixel proof. Same frame with the decal projector on and off; the wound is "in frame" only if enough pixels inside the projected decal box change (and a control on-vs-on diff stays small).
// Every threshold is woundshot.json (WoundShot.Cfg). Presentation / harness only.
public static class WoundGate
{
    // ---------------- 1) memory probe ----------------
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential)]
    struct PMC
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)] static extern bool K32GetProcessMemoryInfo(IntPtr process, out PMC counters, uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
#endif
    const double GB = 1024.0 * 1024.0 * 1024.0;
    static string lastSrc = "none";
    public static string Source { get { return lastSrc; } }

    /// <summary>Working set and private bytes of this process in GB. Returns false when no source gave a positive number. src names the source that answered.</summary>
    public static bool ReadMemory(out double wsGB, out double privGB, out string src)
    {
        wsGB = 0; privGB = 0; src = "none";
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            var c = new PMC(); c.cb = (uint)Marshal.SizeOf(typeof(PMC));
            if (K32GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb))
            {
                wsGB = (double)c.WorkingSetSize.ToUInt64() / GB; privGB = (double)c.PrivateUsage.ToUInt64() / GB;
                if (wsGB > 0.0) { src = "kernel32"; lastSrc = src; return true; }
            }
        }
        catch (Exception) { }
#endif
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess(); p.Refresh();
            double w = p.WorkingSet64 / GB, v = p.PrivateMemorySize64 / GB;
            if (w > 0.0) { wsGB = w; privGB = v; src = "process"; lastSrc = src; return true; }
        }
        catch (Exception) { }
        try
        {
            double r = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / GB;   // Unity's own reserved memory: a lower bound of the real working set, still moves with a leak
            if (r > 0.0) { wsGB = r; privGB = r; src = "unity-reserved"; lastSrc = src; return true; }
        }
        catch (Exception) { }
        lastSrc = src; return false;
    }

    // ---------------- 2) visibility ----------------
    public class VisResult { public int samples, inFrame, clear; public string blocker = ""; public float Frac { get { return samples > 0 ? Mathf.Min(inFrame, clear) / (float)samples : 0f; } } }

    /// <summary>Sample points along the decal's long axis (local x), at fractions of its length (along, e.g. -0.4, 0, 0.4). No decal: the single point `fallback`.</summary>
    public static Vector3[] SamplePoints(Transform decal, Vector3 fallback, float[] along)
    {
        if (decal == null || along == null || along.Length == 0) return new[] { fallback };
        float len = 0f; var pj = decal.GetComponent<UnityEngine.Rendering.HighDefinition.DecalProjector>();
        if (pj != null) len = pj.size.x * Mathf.Abs(decal.lossyScale.x); else len = Mathf.Abs(decal.lossyScale.x);
        var pts = new Vector3[along.Length];
        for (int i = 0; i < pts.Length; i++) pts[i] = decal.position + decal.right * (len * along[i]);
        return pts;
    }

    /// <summary>Is point p inside a camera of this pose / vertical fov / aspect, with `margin` (fraction of the half-frame) kept free at the edges.</summary>
    public static bool InFrustum(Vector3 camPos, Quaternion camRot, float fovDeg, float aspect, Vector3 p, float near, float margin)
    {
        Vector3 v = Quaternion.Inverse(camRot) * (p - camPos);
        if (v.z <= near) return false;
        float th = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad), nx = v.x / (v.z * th * Mathf.Max(0.01f, aspect)), ny = v.y / (v.z * th), lim = 1f - Mathf.Clamp01(margin);
        return Mathf.Abs(nx) <= lim && Mathf.Abs(ny) <= lim;
    }

    /// <summary>Fraction of the sample points that are in the frame AND not blocked: a collider (any, the victim's own body included) hit earlier than epsM before the point blocks it.
    /// `mask` = the layers the harness already uses for its camera rays. The camera pose is given, not read, so candidate poses can be scored before the camera moves.</summary>
    public static VisResult Check(Vector3 camPos, Quaternion camRot, float fovDeg, float aspect, float near, Vector3[] pts, int mask, float epsM, float margin)
    {
        var r = new VisResult { samples = pts.Length };
        for (int i = 0; i < pts.Length; i++)
        {
            bool fr = InFrustum(camPos, camRot, fovDeg, aspect, pts[i], near, margin); if (fr) r.inFrame++;
            Vector3 d = pts[i] - camPos; float dist = d.magnitude; bool clear = true;
            if (dist > 1e-3f)
            {
                var hits = Physics.RaycastAll(camPos, d / dist, dist, mask, QueryTriggerInteraction.Ignore);
                for (int h = 0; h < hits.Length; h++) if (hits[h].distance < dist - epsM) { clear = false; if (r.blocker.Length == 0) r.blocker = hits[h].collider != null ? hits[h].collider.name : "?"; break; }
            }
            if (clear) r.clear++;
        }
        return r;
    }

    // ---------------- 3) pixel proof ----------------
    public class DiffResult
    {
        public int count, noise, rectArea, fullRectArea, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        public float CentreX(int w) { return maxX < 0 ? -1f : (minX + maxX) * 0.5f / w; }
        public float CentreY(int h) { return maxY < 0 ? -1f : (minY + maxY) * 0.5f / h; }
    }

    /// <summary>Screen rectangle (pixels, y up, like ReadPixels) of the decal's projector box seen by `cam`, padded by padFrac of its size (at least padMinPx). fullArea = the unclamped area, area = clamped to the frame.</summary>
    public static void ProjectBox(Camera cam, Transform decal, int w, int h, float padFrac, int padMinPx, out int x0, out int y0, out int x1, out int y1, out int fullArea)
    {
        x0 = y0 = 0; x1 = w - 1; y1 = h - 1; fullArea = 0; if (decal == null) return;
        var pj = decal.GetComponent<UnityEngine.Rendering.HighDefinition.DecalProjector>();
        Vector3 s = pj != null ? Vector3.Scale(pj.size, decal.lossyScale) : decal.lossyScale; s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue; bool any = false;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = decal.position + decal.right * (((i & 1) == 0 ? -0.5f : 0.5f) * s.x) + decal.up * (((i & 2) == 0 ? -0.5f : 0.5f) * s.y) + decal.forward * (((i & 4) == 0 ? -0.5f : 0.5f) * s.z);
            Vector3 vp = cam.WorldToViewportPoint(c); if (vp.z <= 0f) continue; any = true;
            minx = Mathf.Min(minx, vp.x * w); maxx = Mathf.Max(maxx, vp.x * w); miny = Mathf.Min(miny, vp.y * h); maxy = Mathf.Max(maxy, vp.y * h);
        }
        if (!any) { x0 = y0 = 0; x1 = y1 = -1; return; }
        float padX = Mathf.Max(padMinPx, (maxx - minx) * padFrac), padY = Mathf.Max(padMinPx, (maxy - miny) * padFrac);
        fullArea = Mathf.Max(0, Mathf.RoundToInt((maxx - minx) * (maxy - miny)));
        x0 = Mathf.Clamp(Mathf.FloorToInt(minx - padX), 0, w - 1); x1 = Mathf.Clamp(Mathf.CeilToInt(maxx + padX), 0, w - 1);
        y0 = Mathf.Clamp(Mathf.FloorToInt(miny - padY), 0, h - 1); y1 = Mathf.Clamp(Mathf.CeilToInt(maxy + padY), 0, h - 1);
    }

    /// <summary>Count the pixels of an RGB24 frame `b` (w*h*3 bytes, ReadPixels row order) that differ from `a` by more than thr (0..255) in any channel, inside the rectangle. Sets the bounding box of the changed pixels.</summary>
    public static int CountChanged(byte[] a, Unity.Collections.NativeArray<byte> b, int w, int x0, int y0, int x1, int y1, int thr, DiffResult res)
    {
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * w * 3;
            for (int x = x0; x <= x1; x++)
            {
                int i = row + x * 3;
                int dr = a[i] - b[i], dg = a[i + 1] - b[i + 1], db = a[i + 2] - b[i + 2];
                if (dr < 0) dr = -dr; if (dg < 0) dg = -dg; if (db < 0) db = -db;
                if (dr > thr || dg > thr || db > thr)
                {
                    n++;
                    if (res != null) { if (x < res.minX) res.minX = x; if (x > res.maxX) res.maxX = x; if (y < res.minY) res.minY = y; if (y > res.maxY) res.maxY = y; }
                }
            }
        }
        return n;
    }
}
