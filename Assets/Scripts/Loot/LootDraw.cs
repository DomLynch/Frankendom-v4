using System.Collections.Generic;
using UnityEngine;

// LOOTUI v85: the small IMGUI drawing kit of the loot UI (OS serif fonts, shadowed text, fills, frames, arrows). Same look as the HUD (hud.json): charcoal panels, thin gold frame, crimson accents.
// Self-contained on purpose (the HUD lane's HudDraw is not required); fonts / colours / sizes all come from loot_ui.json. Call from OnGUI only. Alpha multiplies everything drawn.
public static class LootDraw
{
    static MenuLayoutCfg.LootScreenC ML { get { return MenuLayoutCfg.LS; } }   // UIDATAB v1
    static readonly GUIContent measure = new GUIContent();   // UIDATAB v1: reused by Height (was a new GUIContent per call)
    public static float Alpha = 1f;
    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    static readonly Dictionary<string, GUIStyle> styles = new Dictionary<string, GUIStyle>();
    static Texture2D triUp, triDown, dash;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { fonts.Clear(); styles.Clear(); triUp = triDown = dash = null; Alpha = 1f; }

    public static Font FontFor(string kind)
    {
        Font f; if (fonts.TryGetValue(kind, out f)) return f;
        var F = LootCfg.Get().fonts; var names = kind == "ui" ? F.ui : F.serif; f = null;
        if (names != null && names.Length > 0) { try { f = Font.CreateDynamicFontFromOSFont(names, Mathf.Max(ML.draw.fontMin, F.osSize)); } catch (System.Exception e) { Debug.LogWarning("[LootUI] font " + kind + ": " + e.Message); f = null; } }
        fonts[kind] = f; return f;
    }

    public static GUIStyle Style(string kind, int size, TextAnchor a, bool bold, bool italic, bool wrap = false)
    {
        string key = kind + "|" + size + "|" + (int)a + "|" + (bold ? 1 : 0) + (italic ? 1 : 0) + (wrap ? 1 : 0); GUIStyle s;
        if (!styles.TryGetValue(key, out s) || s == null)
        {
            s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = a, wordWrap = wrap, clipping = TextClipping.Overflow, richText = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            s.fontStyle = bold ? (italic ? FontStyle.BoldAndItalic : FontStyle.Bold) : (italic ? FontStyle.Italic : FontStyle.Normal);
            var f = FontFor(kind); if (f != null) s.font = f;
            styles[key] = s;
        }
        return s;
    }

    static Color Fade(Color c) { c.a *= Alpha; return c; }
    public static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = Fade(c); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
    public static void Frame(Rect r, Color c, float t)
    {
        Fill(new Rect(r.x, r.y, r.width, t), c); Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
        Fill(new Rect(r.x, r.y + t, t, r.height - 2f * t), c); Fill(new Rect(r.xMax - t, r.y + t, t, r.height - 2f * t), c);
    }
    public static void Rule(float x, float y, float w, Color c) { Fill(new Rect(x, y, w, ML.draw.ruleH), c); }
    public static void Tex(Rect r, Texture2D t, Color c) { if (t == null) return; if (ResGateProbe.On) ResGateProbe.Box(r); /* RESGATE v1 */ var o = GUI.color; GUI.color = Fade(c); GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true); GUI.color = o; }

    /// <summary>Shadowed label.</summary>
    public static void Text(Rect r, string s, string kind, int size, TextAnchor a, bool bold, Color c, bool italic = false, bool wrap = false)
    {
        if (string.IsNullOrEmpty(s) || c.a * Alpha <= 0.003f) return;
        var st = Style(kind, size, a, bold, italic, wrap); var sh = LootCfg.Col(LootCfg.Get().colors.shadow); float d = Mathf.Max(1f, size / ML.draw.shadowDiv); if (ResGateProbe.On) ResGateProbe.Text(r, s, kind, size, a, bold, wrap); /* RESGATE v1 */
        st.normal.textColor = Fade(new Color(sh.r, sh.g, sh.b, sh.a * c.a)); GUI.Label(new Rect(r.x + d, r.y + d, r.width, r.height), s, st);
        st.normal.textColor = Fade(c); GUI.Label(r, s, st);
    }
    public static float Height(string s, string kind, int size, float width, bool bold = false, bool italic = false)
    {
        if (string.IsNullOrEmpty(s)) return 0f; measure.text = s; return Style(kind, size, TextAnchor.UpperLeft, bold, italic, true).CalcHeight(measure, width);   // UIDATAB v1: reused GUIContent
    }

    // small procedural arrows (white, anti-aliased by the texture size): up / down triangle and a dash
    public static Texture2D Arrow(bool up)
    {
        if (up) { if (triUp == null) triUp = MakeTri(true); return triUp; }
        if (triDown == null) triDown = MakeTri(false); return triDown;
    }
    public static Texture2D Dash()
    {
        if (dash != null) return dash;
        dash = new Texture2D(4, 4, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[16]; for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        dash.SetPixels32(px); dash.Apply(false, true); return dash;
    }
    static Texture2D MakeTri(bool up)
    {
        int n = LootCfg.Get().screen.triTex; float pad = LootCfg.Get().screen.triPad * n / ML.draw.triRefSize;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float fy = up ? y + 0.5f : n - (y + 0.5f);                     // 0 at the flat base, n at the tip
                float span = (n - 2f * pad); float h = Mathf.Clamp01((fy - pad) / span);   // 0 base .. 1 tip
                float half = (1f - h) * (n * 0.5f - pad);                        // half width at this height
                float dx = Mathf.Abs(x + 0.5f - n * 0.5f);
                float inside = fy < pad || fy > n - pad ? 0f : Mathf.Clamp01(half - dx + 0.5f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(inside * 255f));
            }
        t.SetPixels32(px); t.Apply(false, true); return t;
    }
}
