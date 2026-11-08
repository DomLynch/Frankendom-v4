// ENDALPHA v1: NEW FILE, copy to Assets\Scripts\Zones\ZoneEndScreen.cs. UNPROVEN: compile-safe by reading, the Unity dev compiles at merge.
// Data: zones.json "endScreens" (EndScreenRow) + gate.endScreen (id). Everything on screen comes from the row; no literal text, size, key or time here.
using System;
using UnityEngine;

[Serializable] public class EndScreenRow
{
    public string id = "", title = "", hint = "", campZone = "", campSpot = ""; public string[] lines = new string[0], continueKeys = new string[0], campKeys = new string[0];
    public float fadeInS = 1.5f, minShowS = 1f, backdropAlpha = 0.8f, titleY = 0.3f, lineGap = 1.5f, titleGapMul = 1.6f, nudgeBackM = 8f; public int titleSize = 46, lineSize = 22, hintSize = 18;
    public float[] color = new float[0], dimColor = new float[0]; public bool onlyWhenNoEnemyAlive = true;
}

/// <summary>Modal overlay for a gate that carries an endScreen id. ZoneRuntime calls Begin(), Draw() from OnGUI and Poll() from Update.</summary>
public static class ZoneEndScreen
{
    public enum Result { None, Continue, Camp }
    static EndScreenRow row; static float t0;
    public static bool Active { get { return row != null; } }
    public static EndScreenRow Find(EndScreenRow[] rows, string id) { if (rows != null) foreach (var r in rows) if (r != null && r.id == id) return r; return null; }
    public static void Begin(EndScreenRow r) { row = r; t0 = Time.unscaledTime; }
    static bool Any(string[] keys) { if (keys != null) foreach (var k in keys) { KeyCode kc; if (Enum.TryParse(k, out kc) && Input.GetKeyDown(kc)) return true; } return false; }
    public static Result Poll()
    {
        if (row == null || Time.unscaledTime - t0 < row.minShowS) return Result.None;
        Result r = Any(row.campKeys) ? Result.Camp : Any(row.continueKeys) ? Result.Continue : Result.None;
        return r;
    }
    public static EndScreenRow Row { get { return row; } }
    public static void End() { row = null; }
    public static void Draw()
    {
        if (row == null) return;
        float a = Mathf.Clamp01((Time.unscaledTime - t0) / Mathf.Max(0.01f, row.fadeInS)), vw = Screen.width, vh = Screen.height;   // hc:ok 0.01 = divide guard
        GUI.depth = -10001;   // hc:ok IMGUI draw order
        var veil = Color.black; veil.a = row.backdropAlpha * a; GameLoop.Fill(new Rect(0f, 0f, vw, vh), veil);
        Color ink = HudCfg.Col(row.color, a), dim = HudCfg.Col(row.dimColor, a);
        float y = vh * row.titleY;
        HudDraw.Caps(vw * 0.5f, y, 0.5f, row.title, "title", row.titleSize, row.titleSize, 0f, ink);
        y += row.titleSize * row.lineGap * row.titleGapMul;
        foreach (var l in row.lines) { HudDraw.Text(new Rect(0f, y, vw, row.lineSize * row.lineGap), l, "serif", row.lineSize, TextAnchor.UpperCenter, false, ink); y += row.lineSize * row.lineGap; }
        y += row.lineSize * row.lineGap;
        HudDraw.Text(new Rect(0f, y, vw, row.hintSize * row.lineGap), row.hint, "serif", row.hintSize, TextAnchor.UpperCenter, false, dim);
    }
}
