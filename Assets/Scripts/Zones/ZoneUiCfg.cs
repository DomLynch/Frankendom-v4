// UIDATA_C v1: NEW FILE, copy to Assets\Scripts\Zones\ZoneUiCfg.cs. Data: zone_ui.json (Assets\Data == StreamingAssets). Layout / style numbers of the zone cover, end screen, hitch cover,
// camtrace marker, hub tag toast, direction marker label, stance label + toast, capture stamp and the GameLoop end card + prompts. Every default EQUALS the literal it replaced (zero behaviour change);
// a missing file / key / group keeps the default. Loaded once (cached); reset on SubsystemRegistration like the other configs.
using System;
using System.IO;
using UnityEngine;

[Serializable] public class ZoneUiCfg
{
    [Serializable] public class CoverC { public float minAlpha = 0.002f; public int depth = -10000; }
    [Serializable] public class EndScreenC { public int depth = -10001; public float alphaGuard = 0.01f; }
    [Serializable] public class HitchC { public int depth = -10001; }
    [Serializable] public class CamTraceC { public int depth = -32000; public float[] fallbackRgb = new float[] { 1f, 0f, 1f }; public float alpha = 1f, markerTol = 0.3f; }
    [Serializable] public class HubC { public float toastFadeInS = 0.12f, toastFadeOutS = 0.35f, toastLineH = 1.5f; }
    [Serializable] public class DirC { public float labelHalfW = 50f, labelW = 100f, labelHMul = 2f; }
    [Serializable] public class StanceC
    {
        public float shadowOffset = 1f, shadowAlpha = 0.7f, toastHalfW = 160f, toastW = 320f, toastH = 30f, toastYFrac = 0.70f, toastShadowAlpha = 0.7f, toastFadeMinS = 0.05f, toastFadeFrac = 0.5f;
        public int hintSizeDelta = 3, hintMinSize = 8;
    }
    [Serializable] public class CaptureC { public int fontSize = 22; public float rightInset = 260f, bottomInset = 44f, w = 250f, h = 36f; public float[] color = new float[0]; /* empty = Color.yellow */ }
    [Serializable] public class BossC { public int depth = -9000; public float minSeconds = 0.5f, fadeEps = 0.001f; }
    [Serializable] public class EndC
    {
        public int depth = -1000;
        public float fadeEps = 0.001f, vignetteAlpha = 0.9f, fightY = 40f, fightTracking = 3f, titleY = 70f, titleHMul = 1.35f, ruleGap = 8f, ruleDiamond = 9f, ruleThick = 1.3f, ruleCenter = 0.65f, ruleAlpha = 0.8f;
        public float subGap = 18f, subAlpha = 0.95f, subLineH = 1.6f, statsGapMul = 1.7f, statsLineH = 1.6f, nextPreviewGap = 2.1f, nextPreviewLineH = 1.6f, liveAlpha = 0.5f, promptTitleMul = 1.5f, promptHintMul = 1.9f;
        public float hoverK = 0.95f, idleK = 0.8f, promptTracking = 2.5f, promptDiamond = 7f, promptDiamondGap = 18f, promptDiamondY = 0.78f, promptDiamondIdleA = 0.7f, promptRuleY = 1.5f, promptRuleH = 1f, promptRuleA = 0.7f, promptHintY = 1.6f, promptHintLineH = 1.6f;
        public int promptSmallDelta = 4, promptSmallMin = 8;
        public float shadowAlpha = 0.85f, shadowOffset = 1.5f;
    }

    public CoverC cover = new CoverC(); public EndScreenC endScreen = new EndScreenC(); public HitchC hitch = new HitchC(); public CamTraceC camTrace = new CamTraceC(); public HubC hub = new HubC();
    public DirC dir = new DirC(); public StanceC stance = new StanceC(); public CaptureC capture = new CaptureC(); public BossC boss = new BossC(); public EndC end = new EndC();

    static ZoneUiCfg c;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; }
    public static void Reload() { c = null; }
    public static ZoneUiCfg Get()
    {
        if (c != null) return c;
        var k = new ZoneUiCfg();
        try
        {
            foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "zone_ui.json"), Path.Combine(Application.dataPath, "Data/zone_ui.json") })
                if (File.Exists(p)) { var d = JsonUtility.FromJson<ZoneUiCfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (d != null) { k = d; break; } }
        }
        catch (Exception e) { Debug.LogWarning("[ZoneUiCfg] zone_ui.json read failed, defaults kept: " + e.Message); k = new ZoneUiCfg(); }
        if (k.cover == null) k.cover = new CoverC(); if (k.endScreen == null) k.endScreen = new EndScreenC(); if (k.hitch == null) k.hitch = new HitchC(); if (k.camTrace == null) k.camTrace = new CamTraceC();
        if (k.hub == null) k.hub = new HubC(); if (k.dir == null) k.dir = new DirC(); if (k.stance == null) k.stance = new StanceC(); if (k.capture == null) k.capture = new CaptureC(); if (k.boss == null) k.boss = new BossC(); if (k.end == null) k.end = new EndC();
        if (k.camTrace.fallbackRgb == null || k.camTrace.fallbackRgb.Length < 3) k.camTrace.fallbackRgb = new float[] { 1f, 0f, 1f };
        c = k; return c;
    }
    /// <summary>float[] of 3 or 4 -> Color; a short array takes the fallback (the old literal).</summary>
    public static Color Rgba(float[] a, Color fallback) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f) : fallback; }
}
