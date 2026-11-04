using System;
using System.Collections.Generic;
using UnityEngine;

// Steer UI (lane S3): procedural vector-style glyphs, used as the fallback when no steer_icons PNG is present.
// Each glyph is a signed-distance function in a unit square (y up) rasterised once into a white 128x128 texture
// (alpha = shape) that STUI tints per state. Also holds the disc / ring / pie textures.
public static class STIcons
{
    const int N = 128;

    static float Seg(float px, float py, float ax, float ay, float bx, float by, float r)
    {
        float pax = px - ax, pay = py - ay, bax = bx - ax, bay = by - ay;
        float h = Mathf.Clamp01((pax * bax + pay * bay) / (bax * bax + bay * bay));
        float dx = pax - bax * h, dy = pay - bay * h;
        return Mathf.Sqrt(dx * dx + dy * dy) - r;
    }
    static float Circ(float px, float py, float cx, float cy, float r) { float dx = px - cx, dy = py - cy; return Mathf.Sqrt(dx * dx + dy * dy) - r; }
    static float RingD(float px, float py, float cx, float cy, float r, float w) { return Mathf.Abs(Circ(px, py, cx, cy, r) + r - r) - w; }
    static float Box(float px, float py, float cx, float cy, float hx, float hy) { return Mathf.Max(Mathf.Abs(px - cx) - hx, Mathf.Abs(py - cy) - hy); }
    // convex polygon, counter-clockwise (y up); negative inside
    static float Poly(float px, float py, params float[] p)
    {
        float d = -1e9f; int n = p.Length / 2;
        for (int i = 0; i < n; i++)
        {
            float ax = p[2 * i], ay = p[2 * i + 1], bx = p[2 * ((i + 1) % n)], by = p[2 * ((i + 1) % n) + 1];
            float ex = bx - ax, ey = by - ay, l = Mathf.Sqrt(ex * ex + ey * ey);
            float s = ((px - ax) * ey - (py - ay) * ex) / l;
            d = Mathf.Max(d, s);
        }
        return d;
    }
    static float Band(float d, float w) { return Mathf.Abs(d + w * 0.5f) - w * 0.5f; }   // outline of the inside of a shape
    static void Rot(ref float x, ref float y, float deg)
    {
        float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a), dx = x - 0.5f, dy = y - 0.5f;
        x = 0.5f + dx * c - dy * s; y = 0.5f + dx * s + dy * c;
    }
    static float Min(float a, float b) { return a < b ? a : b; }
    static float Min(float a, float b, float c) { return Min(Min(a, b), c); }
    static float Min(float a, float b, float c, float d) { return Min(Min(a, b), Min(c, d)); }

    static float SwordShape(float x, float y, bool dashed)
    {
        float d = Seg(x, y, 0.32f, 0.3f, 0.68f, 0.3f, 0.04f);                       // guard
        d = Min(d, Seg(x, y, 0.5f, 0.3f, 0.5f, 0.15f, 0.035f));                     // grip
        d = Min(d, Circ(x, y, 0.5f, 0.12f, 0.05f));                                  // pommel
        if (!dashed)
        {
            d = Min(d, Seg(x, y, 0.5f, 0.3f, 0.5f, 0.74f, 0.05f));
            d = Min(d, Poly(x, y, 0.45f, 0.74f, 0.55f, 0.74f, 0.5f, 0.93f));
        }
        else
        {
            d = Min(d, Seg(x, y, 0.5f, 0.36f, 0.5f, 0.46f, 0.045f));
            d = Min(d, Seg(x, y, 0.5f, 0.54f, 0.5f, 0.64f, 0.045f));
            d = Min(d, Seg(x, y, 0.5f, 0.72f, 0.5f, 0.82f, 0.045f));
        }
        return d;
    }
    static float Chev(float x, float y, float ox, bool right)
    {
        if (!right) x = 1f - x;
        return Min(Seg(x, y, ox, 0.72f, ox + 0.22f, 0.5f, 0.055f), Seg(x, y, ox + 0.22f, 0.5f, ox, 0.28f, 0.055f));
    }
    static float SwordSide(float x, float y, float deg, bool dashed) { Rot(ref x, ref y, deg); return SwordShape(x, y, dashed); }

    static float Shape(string id, float x, float y)
    {
        switch (id)
        {
            case "Aggressive": return SwordSide(x, y, 35f, false);
            case "Defensive":
                {
                    float s = Poly(x, y, 0.24f, 0.8f, 0.24f, 0.52f, 0.5f, 0.12f, 0.76f, 0.52f, 0.76f, 0.8f);
                    return Min(Band(s, 0.1f), Seg(x, y, 0.5f, 0.72f, 0.5f, 0.3f, 0.03f), Seg(x, y, 0.34f, 0.58f, 0.66f, 0.58f, 0.03f));
                }
            case "Neutral":
                {
                    float d = Min(RingD(x, y, 0.5f, 0.5f, 0.27f, 0.05f), Circ(x, y, 0.5f, 0.5f, 0.09f));
                    d = Min(d, Seg(x, y, 0.5f, 0.14f, 0.5f, 0.26f, 0.035f), Seg(x, y, 0.5f, 0.74f, 0.5f, 0.86f, 0.035f));
                    return Min(d, Seg(x, y, 0.14f, 0.5f, 0.26f, 0.5f, 0.035f), Seg(x, y, 0.74f, 0.5f, 0.86f, 0.5f, 0.035f));
                }
            case "Trickster":
                {
                    float lens = Mathf.Max(Circ(x, y, 0.5f, 0.8f, 0.5f), Circ(x, y, 0.5f, 0.2f, 0.5f));
                    return Min(Band(lens, 0.09f), RingD(x, y, 0.5f, 0.5f, 0.13f, 0.03f), Circ(x, y, 0.5f, 0.5f, 0.055f));
                }
            case "Press": return Min(Chev(x, y, 0.24f, true), Chev(x, y, 0.5f, true));
            case "BackOff": return Min(Chev(x, y, 0.24f, false), Chev(x, y, 0.5f, false));
            case "Circle":
                {
                    float dx = x - 0.5f, dy = y - 0.5f;
                    float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    float ring = RingD(x, y, 0.5f, 0.5f, 0.3f, 0.05f);
                    if (ang > -30f && ang < 30f) ring = 1f;
                    float ta = -30f * Mathf.Deg2Rad, px = 0.5f + 0.3f * Mathf.Cos(ta), py = 0.5f + 0.3f * Mathf.Sin(ta);
                    float tx = -Mathf.Sin(ta), ty = Mathf.Cos(ta), nx = Mathf.Cos(ta), ny = Mathf.Sin(ta);
                    float head = Poly(x, y, px + nx * 0.12f, py + ny * 0.12f, px + tx * 0.16f, py + ty * 0.16f, px - nx * 0.12f, py - ny * 0.12f);
                    return Min(ring, head);
                }
            case "GuardUp":
                {
                    float d = Min(Seg(x, y, 0.22f, 0.22f, 0.78f, 0.78f, 0.05f), Seg(x, y, 0.78f, 0.22f, 0.22f, 0.78f, 0.05f));
                    d = Min(d, Seg(x, y, 0.28f, 0.42f, 0.44f, 0.26f, 0.035f), Seg(x, y, 0.72f, 0.42f, 0.56f, 0.26f, 0.035f));
                    return Min(d, Circ(x, y, 0.5f, 0.5f, 0.07f));
                }
            case "Dodge":
                {
                    float d = Seg(x, y, 0.28f, 0.28f, 0.6f, 0.6f, 0.05f);
                    d = Min(d, Poly(x, y, 0.82f, 0.82f, 0.54f, 0.78f, 0.78f, 0.54f));
                    return Min(d, Seg(x, y, 0.13f, 0.42f, 0.3f, 0.59f, 0.03f), Seg(x, y, 0.41f, 0.14f, 0.58f, 0.31f, 0.03f));
                }
            case "Feint": return SwordSide(x, y, -35f, true);
            case "GoForKill":
                {
                    float d = Min(Circ(x, y, 0.5f, 0.58f, 0.3f), Box(x, y, 0.5f, 0.3f, 0.17f, 0.12f));
                    d = Mathf.Max(d, -Circ(x, y, 0.38f, 0.56f, 0.075f));
                    d = Mathf.Max(d, -Circ(x, y, 0.62f, 0.56f, 0.075f));
                    d = Mathf.Max(d, -Poly(x, y, 0.5f, 0.47f, 0.455f, 0.38f, 0.545f, 0.38f));
                    d = Mathf.Max(d, -Seg(x, y, 0.42f, 0.2f, 0.42f, 0.27f, 0.011f));
                    d = Mathf.Max(d, -Seg(x, y, 0.5f, 0.2f, 0.5f, 0.27f, 0.011f));
                    return Mathf.Max(d, -Seg(x, y, 0.58f, 0.2f, 0.58f, 0.27f, 0.011f));
                }
            case "Lock":
                {
                    float body = Box(x, y, 0.5f, 0.32f, 0.26f, 0.2f);
                    float sh = Mathf.Max(RingD(x, y, 0.5f, 0.55f, 0.16f, 0.045f), 0.5f - y);
                    return Mathf.Max(Min(body, sh), -Circ(x, y, 0.5f, 0.34f, 0.055f));
                }
        }
        return Circ(x, y, 0.5f, 0.5f, 0.25f);
    }

    static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
    public static Texture2D Get(string id)
    {
        Texture2D t;
        if (cache.TryGetValue(id, out t) && t != null) return t;
        t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                float d = Shape(id, (i + 0.5f) / N, (j + 0.5f) / N);
                float a = Mathf.Clamp01(0.5f - d * N * 0.75f);
                px[j * N + i] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        t.SetPixels32(px); t.Apply(false, false);
        cache[id] = t; return t;
    }

    static Texture2D disc, ring, thinRing;
    static Texture2D MakeRound(int mode)   // 0 disc, 1 ring (thick edge), 2 thin ring
    {
        const int S = 128; var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[S * S]; float c = (S - 1) * 0.5f, R = S * 0.5f - 1f;
        for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
            {
                float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(R - r + 0.5f);
                if (mode == 1) a *= Mathf.Clamp01(r - (R - 5f) + 0.5f);
                if (mode == 2) a *= Mathf.Clamp01(r - (R - 2.2f) + 0.5f);
                px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        t.SetPixels32(px); t.Apply(false, false); return t;
    }
    public static Texture2D Disc { get { if (disc == null) disc = MakeRound(0); return disc; } }
    public static Texture2D Ring { get { if (ring == null) ring = MakeRound(1); return ring; } }
    public static Texture2D ThinRing { get { if (thinRing == null) thinRing = MakeRound(2); return thinRing; } }

    // pie overlays: pie(i) covers the first i/PieSteps of the circle, clockwise from 12 o'clock (white, tinted by the caller)
    public const int PieSteps = 48;
    static Texture2D[] pies;
    public static Texture2D Pie(float frac)
    {
        if (pies == null) pies = new Texture2D[PieSteps + 1];
        int i = Mathf.Clamp(Mathf.CeilToInt(frac * PieSteps - 0.001f), 0, PieSteps);
        if (i == 0) return null;
        if (pies[i] != null) return pies[i];
        const int S = 96; var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[S * S]; float c = (S - 1) * 0.5f, R = S * 0.5f - 1f, lim = (float)i / PieSteps * 2f * Mathf.PI;
        for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
            {
                float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Atan2(dx, dy); if (ang < 0f) ang += 2f * Mathf.PI;
                float a = ang <= lim ? Mathf.Clamp01(R - r + 0.5f) : 0f;
                px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        t.SetPixels32(px); t.Apply(false, false); pies[i] = t; return t;
    }
}
