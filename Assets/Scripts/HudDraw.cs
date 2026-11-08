using System.Collections.Generic;
using UnityEngine;

// HUD2 (lane laneHUD2): procedural drawing kit for the dark-gothic HUD. IMGUI only, no art assets, no TextMeshPro / uGUI (neither package is in the project).
//  - fonts: OS serif faces through Font.CreateDynamicFontFromOSFont (names in hud.json fonts{}); missing = Unity default font
//  - text: shadowed labels, and "small caps" / tracked caps drawn glyph by glyph (first letter of each word at the big size, the rest at the small size, tracking px between glyphs)
//  - shapes: convex-polygon signed-distance textures (tapered bar silhouette, outline ring, diamond, arc, glyphs), generated once and cached
// Every size / colour / name comes from HudCfg (hud.json). Call from OnGUI only. Alpha multiplies every Fill / Text / Tex (set it around a draw pass, reset to 1).
public static class HudDraw
{
    public static float Alpha = 1f;
    public static float Outline = 0f, OutlineAlpha = 0.9f;   // HUDFIX v92: glyph outline radius in px (0 = off) in the shadow colour, set around a draw pass like Alpha (the end screen)

    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    static readonly Dictionary<int, GUIStyle> styles = new Dictionary<int, GUIStyle>();
    static readonly Dictionary<string, Texture2D> texs = new Dictionary<string, Texture2D>();
    static readonly GUIContent tmp = new GUIContent();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { fonts.Clear(); styles.Clear(); texs.Clear(); capsCache.Clear(); lastRingTex = null; lastArcTex = null; Alpha = 1f; Outline = 0f; pies = null; }

    // ---------------- fonts / styles ----------------
    public static Font FontFor(string kind)
    {
        Font f; if (fonts.TryGetValue(kind, out f)) return f;
        var F = HudCfg.Get().fonts; string[] names = kind == "title" ? F.title : kind == "ui" ? F.ui : F.serif;
        f = null;
        if (names != null && names.Length > 0) { try { f = Font.CreateDynamicFontFromOSFont(names, Mathf.Max(HudLayout.Get().draw.minOsSize, F.osSize)); } catch (System.Exception e) { Debug.LogWarning("[HudDraw] font " + kind + ": " + e.Message); f = null; } }
        fonts[kind] = f; return f;
    }
    public static GUIStyle Style(string kind, int size, TextAnchor a, bool bold, bool italic)
    {
        int key = (((kind == "title" ? 1 : kind == "ui" ? 2 : 0) * 512 + Mathf.Clamp(size, 0, 511)) * 16 + (int)a) * 4 + (bold ? 2 : 0) + (italic ? 1 : 0); GUIStyle s;   // int key: no string garbage per label per frame
        if (!styles.TryGetValue(key, out s) || s == null)
        {
            s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = a, wordWrap = false, clipping = TextClipping.Overflow, richText = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            s.fontStyle = bold ? (italic ? FontStyle.BoldAndItalic : FontStyle.Bold) : (italic ? FontStyle.Italic : FontStyle.Normal);
            var f = FontFor(kind); if (f != null) s.font = f;
            styles[key] = s;
        }
        return s;
    }
    static Color Fade(Color c) { c.a *= Alpha; return c; }
    static readonly Vector2[] ring8 = { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1), new Vector2(0.7071f, 0.7071f), new Vector2(-0.7071f, 0.7071f), new Vector2(0.7071f, -0.7071f), new Vector2(-0.7071f, -0.7071f) };   // HUDFIX v92
    // HUDFIX v92: 8 offset copies in the shadow colour under a label when Outline > 0
    static void OutlineLabel(GUIStyle st, Rect r, string s, float ca)
    {
        if (Outline <= 0f) return; var sh = HudCfg.Col(HudCfg.Get().colors.shadow);
        st.normal.textColor = Fade(new Color(sh.r, sh.g, sh.b, OutlineAlpha * ca));
        for (int i = 0; i < ring8.Length; i++) GUI.Label(new Rect(r.x + ring8[i].x * Outline, r.y + ring8[i].y * Outline, r.width, r.height), s, st);
    }

    // ---------------- basic fills ----------------
    public static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = Fade(c); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
    public static void Tex(Rect r, Texture2D t, Color c) { if (t == null) return; if (ResGateProbe.On) ResGateProbe.Box(r); /* RESGATE v1 */ var o = GUI.color; GUI.color = Fade(c); GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true); GUI.color = o; }
    public static void TexUV(Rect r, Texture2D t, Rect uv, Color c) { if (t == null) return; var o = GUI.color; GUI.color = Fade(c); GUI.DrawTextureWithTexCoords(r, t, uv, true); GUI.color = o; }

    // ---------------- text ----------------
    /// <summary>Shadowed single label.</summary>
    public static void Text(Rect r, string s, string kind, int size, TextAnchor a, bool bold, Color c, bool italic = false)
    {
        if (string.IsNullOrEmpty(s) || c.a * Alpha <= HudLayout.Get().draw.alphaCutoffText) return;
        var st = Style(kind, size, a, bold, italic); var sh = HudCfg.Col(HudCfg.Get().colors.shadow); float d = Mathf.Max(HudLayout.Get().draw.shadowMinText, size / HudLayout.Get().draw.shadowDivText); if (ResGateProbe.On) ResGateProbe.Text(r, s, kind, size, a, bold); /* RESGATE v1 */
        OutlineLabel(st, r, s, c.a);   // HUDFIX v92
        st.normal.textColor = Fade(new Color(sh.r, sh.g, sh.b, sh.a * c.a)); GUI.Label(new Rect(r.x + d, r.y + d, r.width, r.height), s, st);
        st.normal.textColor = Fade(c); GUI.Label(r, s, st);
    }

    class Run { public string ch; public float x; public int size; public float w; }
    class CapsLayout { public Run[] runs; public float width; public int big; }
    // UIDATA_A v1: the key was a string built per Caps() call (kind|big|small|tracking|bold|text = garbage every label every frame); now a struct, same cache semantics
    struct CapsKey : System.IEquatable<CapsKey>
    {
        public int kind, big, small; public float tracking; public bool bold; public string text;
        public bool Equals(CapsKey o) { return kind == o.kind && big == o.big && small == o.small && tracking == o.tracking && bold == o.bold && string.Equals(text, o.text); }
        public override bool Equals(object o) { return o is CapsKey && Equals((CapsKey)o); }
        public override int GetHashCode() { unchecked { int h = kind * 31 + big; h = h * 31 + small; h = h * 31 + tracking.GetHashCode(); h = h * 31 + (bold ? 1 : 0); return h * 31 + (text != null ? text.GetHashCode() : 0); } }
    }
    static readonly Dictionary<CapsKey, CapsLayout> capsCache = new Dictionary<CapsKey, CapsLayout>();
    static float lastRingPx = -1f, lastArcDeg = -1f, lastArcThick = -1f; static Texture2D lastRingTex, lastArcTex;   // UIDATA_A v1: front caches, the string-keyed texs dictionary stays the owner

    static bool MinorWord(string t, int i) { if (i <= 0) return false; foreach (var w in HudLayout.Get().draw.minorWords) if (string.CompareOrdinal(t, i, w, 0, w.Length) == 0) return true; return false; }
    static CapsLayout Layout(string text, string kind, int big, int small, float tracking, bool bold)
    {
        var key = new CapsKey { kind = kind == "title" ? 1 : kind == "ui" ? 2 : 0, big = big, small = small, tracking = tracking, bold = bold, text = text }; CapsLayout c;
        if (capsCache.TryGetValue(key, out c)) return c;
        if (capsCache.Count > HudCfg.Get().layout.capsCache) capsCache.Clear();   /* HCKIT6 v100 */
        string up = text.ToUpperInvariant(); var runs = new List<Run>(); float x = 0f; bool wordStart = true;
        for (int i = 0; i < up.Length; i++)
        {
            char ch = up[i]; int sz = ((wordStart && !MinorWord(text, i)) || big == small) ? big : small; wordStart = ch == ' ';   // v77 minor: 'of' / 'the' keep the small cap size
            var st = Style(kind, sz, TextAnchor.LowerLeft, bold, false); tmp.text = ch.ToString();
            float w = ch == ' ' ? st.CalcSize(new GUIContent("a")).x * HudCfg.Get().layout.spaceW : st.CalcSize(tmp).x;
            runs.Add(new Run { ch = ch.ToString(), x = x, size = sz, w = w });
            x += w + tracking;
        }
        c = new CapsLayout { runs = runs.ToArray(), width = Mathf.Max(0f, x - tracking), big = big }; capsCache[key] = c; return c;
    }
    /// <summary>Small-caps / tracked caps line. pivot 0 = x is the left edge, 0.5 = centre, 1 = right edge. y = top of the line box; returns the line width. big == small gives plain tracked capitals.</summary>
    public static float Caps(float x, float y, float pivot, string text, string kind, int big, int small, float tracking, Color c, bool bold = true, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var L = Layout(text, kind, big, small, tracking, bold); if (c.a * Alpha <= HudLayout.Get().draw.alphaCutoffCaps) return L.width; if (ResGateProbe.On) ResGateProbe.Caps(x - L.width * pivot, y, L.width, big * HudCfg.Get().layout.lineH, text, big); /* RESGATE v1 */
        float x0 = x - L.width * pivot, h = big * HudCfg.Get().layout.lineH; var sh = HudCfg.Col(HudCfg.Get().colors.shadow); float d = Mathf.Max(HudLayout.Get().draw.shadowMinCaps, big / HudCfg.Get().layout.shadowDiv);
        for (int i = 0; i < L.runs.Length; i++)
        {
            var r = L.runs[i]; if (r.ch == " ") continue;
            var st = Style(kind, r.size, TextAnchor.LowerLeft, bold, false); var rc = new Rect(x0 + r.x, y, r.w + HudCfg.Get().layout.runPad, h);
            if (shadow) OutlineLabel(st, rc, r.ch, c.a);   // HUDFIX v92
            if (shadow) { st.normal.textColor = Fade(new Color(sh.r, sh.g, sh.b, sh.a * c.a)); GUI.Label(new Rect(rc.x + d, rc.y + d, rc.width, rc.height), r.ch, st); }
            st.normal.textColor = Fade(c); GUI.Label(rc, r.ch, st);
        }
        return L.width;
    }
    public static float CapsWidth(string text, string kind, int big, int small, float tracking, bool bold = true) { return string.IsNullOrEmpty(text) ? 0f : Layout(text, kind, big, small, tracking, bold).width; }

    // ---------------- procedural textures ----------------
    static Texture2D Make(string key, int w, int h, System.Func<float, float, float> alphaAt, FilterMode fm = FilterMode.Bilinear)
    {
        Texture2D t;
        if (texs.TryGetValue(key, out t) && t != null) return t;
        t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = fm, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { float a = Mathf.Clamp01(alphaAt(x + 0.5f, y + 0.5f)); px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f)); }
        t.SetPixels32(px); t.Apply(false, false); texs[key] = t; return t;
    }
    static float Cov(float d) { return Mathf.Clamp01(0.5f - d); }

    // convex polygon (CCW, y up) signed distance in texels: > 0 outside
    static float PolyD(Vector2[] p, Vector2[] n, float x, float y)
    {
        float m = -1e9f; for (int i = 0; i < p.Length; i++) { float d = (x - p[i].x) * n[i].x + (y - p[i].y) * n[i].y; if (d > m) m = d; }
        return m;
    }
    static void PolyNormals(Vector2[] p, out Vector2[] n)
    {
        n = new Vector2[p.Length];
        for (int i = 0; i < p.Length; i++) { var e = p[(i + 1) % p.Length] - p[i]; n[i] = new Vector2(e.y, -e.x).normalized; }
    }
    static Vector2[] Hex(float w, float h, float t) { return new[] { new Vector2(0f, h * 0.5f), new Vector2(t, 0f), new Vector2(w - t, 0f), new Vector2(w, h * 0.5f), new Vector2(w - t, h), new Vector2(t, h) }; }

    /// <summary>Tapered bar silhouette (a long hexagon: pointed ends), white with anti-aliased alpha. ring &gt; 0 = only the outline of that thickness (texels).</summary>
    public static Texture2D BarShape(float w, float h, float taper, float ringTexels)
    {
        float ts = HudCfg.Get().bars.texScale; int tw = Mathf.Max(8, Mathf.RoundToInt(w * ts)), th = Mathf.Max(4, Mathf.RoundToInt(h * ts)); float tt = Mathf.Min(taper * ts, tw * 0.45f);
        string key = "bar|" + tw + "|" + th + "|" + Mathf.RoundToInt(tt) + "|" + ringTexels.ToString("F1");
        Texture2D t; if (texs.TryGetValue(key, out t) && t != null) return t;
        var P = Hex(tw, th, tt); Vector2[] N; PolyNormals(P, out N); float ring = ringTexels;
        return Make(key, tw, th, (x, y) => { float d = PolyD(P, N, x, y); return ring <= 0f ? Cov(d) : Cov(d) - Cov(d + ring); });
    }
    public static Texture2D Diamond()
    {
        var Q = new[] { new Vector2(2f, 32f), new Vector2(32f, 2f), new Vector2(62f, 32f), new Vector2(32f, 62f) }; Vector2[] N; PolyNormals(Q, out N);   // CCW: left, bottom, right, top
        return Make("diamond", 64, 64, (x, y) => Cov(PolyD(Q, N, x, y)));
    }
    public static Texture2D DiamondRing()
    {
        var Q = new[] { new Vector2(2f, 32f), new Vector2(32f, 2f), new Vector2(62f, 32f), new Vector2(32f, 62f) }; Vector2[] N; PolyNormals(Q, out N);
        return Make("diamondRing", 64, 64, (x, y) => { float d = PolyD(Q, N, x, y); return Cov(d) - Cov(d + HudLayout.Get().draw.diamondRingPx); });
    }
    /// <summary>Horizontal gradient: alpha 1 at the left edge to 0 at the right edge (flip with a negative-width uv).</summary>
    public static Texture2D GradH() { return Make("gradH", 128, 2, (x, y) => 1f - x / 128f, FilterMode.Bilinear); }
    /// <summary>Vertical soft band: alpha 0 at both edges, 1 in the middle (smooth).</summary>
    public static Texture2D BandV() { return Make("bandV", 2, 128, (x, y) => { float k = Mathf.Sin(y / 128f * Mathf.PI); return k * k; }); }
    /// <summary>Radial vignette: transparent in the centre, opaque at the corners.</summary>
    public static Texture2D Vignette() { return Make("vignette", 128, 128, (x, y) => { float dx = (x - 64f) / 64f, dy = (y - 64f) / 64f; float r = Mathf.Sqrt(dx * dx + dy * dy) / HudLayout.Get().draw.vignetteDiv; return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HudLayout.Get().draw.vignetteStart, 1f, r)); }); }
    public static Texture2D Disc() { return Make("disc", 128, 128, (x, y) => Cov((Mathf.Sqrt((x - 64f) * (x - 64f) + (y - 64f) * (y - 64f)) - HudLayout.Get().draw.discRadius))); }
    public static Texture2D Ring(float px) { if (lastRingTex != null && px == lastRingPx) return lastRingTex; lastRingPx = px; lastRingTex = RingMake(px); return lastRingTex; }
    static Texture2D RingMake(float px) { string k = "ring" + px; return Make(k, 128, 128, (x, y) => { float d = Mathf.Sqrt((x - 64f) * (x - 64f) + (y - 64f) * (y - 64f)) - HudLayout.Get().draw.ringRadius; return Cov(d) - Cov(d + px * 2f); }); }

    /// <summary>Thin crescent arc pointing up (rotate with GUIUtility.RotateAroundPivot): drawn in a square whose half-size = radius / 0.85; the band's centre radius is 0.85 of that.</summary>
    public const float ArcCentre = 0.85f;
    public static Texture2D Arc(float arcDeg, float thick) { if (lastArcTex != null && arcDeg == lastArcDeg && thick == lastArcThick) return lastArcTex; lastArcDeg = arcDeg; lastArcThick = thick; lastArcTex = ArcMake(arcDeg, thick); return lastArcTex; }
    static Texture2D ArcMake(float arcDeg, float thick)
    {
        string key = "arc|" + arcDeg.ToString("F0") + "|" + thick.ToString("F2");
        return Make(key, 192, 192, (x, y) =>
        {
            float dx = (x - 96f) / 96f, dy = (y - 96f) / 96f; float r = Mathf.Sqrt(dx * dx + dy * dy);
            float ang = Mathf.Abs(Mathf.Atan2(dx, dy) * Mathf.Rad2Deg); float half = arcDeg * 0.5f; if (ang >= half) return 0f;
            float k = 1f - (ang / half) * (ang / half); float t = thick * k;       // tapers to a point at both ends
            return Mathf.Clamp01((t * 0.5f - Mathf.Abs(r - ArcCentre)) * 96f + 0.5f);
        });
    }

    static Texture2D[] pies;
    /// <summary>Cooldown pie: the NOT yet ready part (frac 1 = full disc, 0 = nothing), clockwise-shrinking from 12 o'clock.</summary>
    public static Texture2D Pie(float frac, int steps)
    {
        int i = Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(frac) * steps), 0, steps); if (i <= 0) return null;
        if (pies == null || pies.Length != steps + 1) pies = new Texture2D[steps + 1];
        if (pies[i] != null) return pies[i];
        float cover = i / (float)steps; // fraction of the circle still dark, from the end of the sweep back to 12 o'clock going clockwise
        var t = new Texture2D(96, 96, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[96 * 96];
        for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++)
        {
            float dx = x + 0.5f - 48f, dy = y + 0.5f - 48f; float d = Mathf.Sqrt(dx * dx + dy * dy) - HudLayout.Get().draw.pieRadius; float a = Cov(d);
            float ang = Mathf.Atan2(dx, dy); if (ang < 0f) ang += Mathf.PI * 2f; float u = ang / (Mathf.PI * 2f);   // 0 at 12 o'clock, clockwise
            if (u < 1f - cover) a = 0f;                                                                              // the ready part is cleared first, from 12 o'clock clockwise
            px[y * 96 + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
        }
        t.SetPixels32(px); t.Apply(false, false); pies[i] = t; return t;
    }

    /// <summary>Glyph for a special icon, white on transparent. Known ids: slash, crescent, cross; anything else is a diamond.</summary>
    public static Texture2D Glyph(string id)
    {
        switch (id)
        {
            case "slash":
                {
                    // a long diagonal blade (lozenge, bottom-left to top-right) and a shorter second cut beside it
                    var Bl = new[] { new Vector2(8f, 8f), new Vector2(68f, 40f), new Vector2(122f, 122f), new Vector2(40f, 68f) };
                    var Cut = new[] { new Vector2(56f, 6f), new Vector2(95.4f, 32.6f), new Vector2(122f, 72f), new Vector2(82.6f, 45.4f) };
                    Vector2[] NBl, NCut; PolyNormals(Bl, out NBl); PolyNormals(Cut, out NCut);
                    return Make("g_slash", 128, 128, (x, y) => Mathf.Max(Cov(PolyD(Bl, NBl, x, y)), Cov(PolyD(Cut, NCut, x, y))));
                }
            case "crescent":
                return Make("g_crescent", 128, 128, (x, y) =>
                {
                    float d1 = Mathf.Sqrt((x - 64f) * (x - 64f) + (y - 64f) * (y - 64f)) - 52f;
                    float d2 = Mathf.Sqrt((x - 84f) * (x - 84f) + (y - 70f) * (y - 70f)) - 46f;
                    return Mathf.Min(Cov(d1), 1f - Cov(d2));
                });
            case "cross":
                {
                    var A = new[] { new Vector2(10f, 24f), new Vector2(24f, 10f), new Vector2(118f, 104f), new Vector2(104f, 118f) };
                    var B = new[] { new Vector2(104f, 10f), new Vector2(118f, 24f), new Vector2(24f, 118f), new Vector2(10f, 104f) }; Vector2[] NA, NB; PolyNormals(A, out NA); PolyNormals(B, out NB);
                    return Make("g_cross", 128, 128, (x, y) => Mathf.Max(Cov(PolyD(A, NA, x, y)), Cov(PolyD(B, NB, x, y))));
                }
            default:
                {
                    var Q = new[] { new Vector2(10f, 64f), new Vector2(64f, 10f), new Vector2(118f, 64f), new Vector2(64f, 118f) }; Vector2[] N; PolyNormals(Q, out N);
                    return Make("g_diamond", 128, 128, (x, y) => Cov(PolyD(Q, N, x, y)));
                }
        }
    }
}
