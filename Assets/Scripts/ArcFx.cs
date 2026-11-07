using System;
using System.Collections.Generic;
using UnityEngine;

// ARCS v105 (laneARCS): dark-by-default look switches for the three things Goose scored 6/10 on v103 (fight_15 / fight_19 / v102 fight_9):
//   guard   = the pale blue-white crescent arcs round Joan. They are the GUARD-DIRECTION HUD (GameHUD.DrawGuardDir, hud.json guardDir), NOT a weapon trail: WeaponTrail is off unless -trails.
//             While on: a soft-edged, end-faded, darker and thinner arc (own generated texture), low alpha, muted tint. hud.json is not touched.
//   weapon  = the soft curved weapon arc the AGENTS.md direction asks for. WeaponTrail stays off by default (Dom 2026-11-04: trails removed, cheesy); while on the existing TrailRenderer
//             (TrailTip) draws a short, tapered, alpha-blended steel-smoke ribbon (no white, no additive) on the Furnace/Particle material with a generated soft cross-profile texture.
//   sparks  = the cartoon spark fan. It is the Unreal-baked 6x4 flipbook (clash_fx.json sheets.sparks) anchored on the DEFENDER'S BODY, not on the weapon contact. While on: the flipbook
//             is skipped, the procedural streaks (ClashFx) and the HDR streaks (HitFeel) are re-anchored on the real contact point (the weapon tips, ContactPoint) and narrowed.
// Every number is arc_fx.json (StreamingAssets first, then Assets/Data). Class defaults = arc_fx.json (HCFIX); the file decides which switches are on (guard on, weapon / sparks dark); every "on" false = today's look. Presentation only: no sim state, no sim RNG.
public static class ArcFx
{
    [Serializable]
    public class Guard
    {
        public bool on = true;   // HCFIX: = arc_fx.json guard.on (Dom wants the soft guard arc on). The file is the switch: "on": false there = today's hud.json look
        public float[] tint = { 0.58f, 0.56f, 0.52f, 1f }, parryTint = { 0.78f, 0.6f, 0.24f, 1f };   // HCFIX: class defaults = arc_fx.json guard (colour / alpha stay data, Dom may darken them there)
        public float arcDeg = 56f, thickness = 0.10f, edgeSoft = 0.55f, endFade = 0.5f, idleAlpha = 0.1f, activeAlpha = 0.62f, parryAlpha = 0.7f;
        public int textureSize = 256;
    }
    [Serializable]
    public class Weapon
    {
        public bool on;
        public float timeS = 0.16f, minVertexDistanceM = 0.04f, widthM = 0.12f, shoulderAt = 0.15f, shoulderWidth = 0.35f, edgeSoft = 0.9f;
        public float[] headColor = { 0.72f, 0.74f, 0.78f, 1f }, tailColor = { 0.3f, 0.3f, 0.33f, 1f };
        public float headAlpha = 0.26f, midAlpha = 0.12f, midAt = 0.4f;
        public int cornerVertices = 4, capVertices = 2, profileSize = 64;
        public string[] emberColors = { "#FFD0A0", "#FF4A1A", "#7A1200" }; public float[] emberAt = { 0f, 0.4f, 1f }, emberAlpha = { 0.9f, 0.5f, 0f }; /* DATALAYER b12 */ // WeaponTrail ember palette (maul)
        public float[] plainColor = { 0.91f, 0.875f, 0.78f }, plainAlpha = { 0.6f, 0f }; /* DATALAYER b12 */ // WeaponTrail plain (sword) colour and head / tail alpha
        public string role = "dust";   // fx_materials.json role of the alpha-blend material (dust = non-additive)
    }
    [Serializable]
    public class Sparks
    {
        public bool on;
        public bool flipbook = false;                       // false = the baked fan is skipped (sheets.sparks), the procedural streaks carry the hit
        public float proceduralCountScale = 1f, countScale = 1.6f, sideSpreadMul = 0.5f, upRangeMul = 0.6f;
        public bool useContactPoint = true;
        public float contactBlend = 0.5f, maxFromBodyM = 1.3f, minHeightM = 0.35f, maxHeightM = 2.2f, tipRefreshS = 1f;   // blend 0 = attacker's tip, 1 = defender's tip; limits keep the burst on the fight
        public float[] hotColor = { 1f, 0.8f, 0.45f, 1f }, coolColor = { 0.55f, 0.28f, 0.12f, 1f };   // procedural streak colour over life (clash_fx.json colorHot / colorCool = white -> grey-blue); [] = keep
        public string tipNode = "TrailTip";
        public bool clashOnly = true;                       // contact point only for Blocked / Parried / GuardBroken (and a Hit on metal): flesh hits keep the body anchor
    }
    [Serializable] public class Root { public Guard guard = new Guard(); public Weapon weapon = new Weapon(); public Sparks sparks = new Sparks(); }

    static Root cfg;
    public static Root Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Root();
        try
        {
            string s = DataPath.ReadText("arc_fx.json");
            if (!string.IsNullOrEmpty(s)) { var r = JsonUtility.FromJson<Root>(s); if (r != null) cfg = r; }
        }
        catch (Exception ex) { Debug.LogWarning("ArcFx: arc_fx.json read failed: " + ex.Message); }
        if (cfg.guard == null) cfg.guard = new Guard();
        if (cfg.weapon == null) cfg.weapon = new Weapon();
        if (cfg.sparks == null) cfg.sparks = new Sparks();
        return cfg;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; tex.Clear(); tips.Clear(); trailTex = null; }

    static Color C(float[] a, Color fb) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f) : fb; }

    // ---------------- guard arcs (GameHUD.DrawGuardDir) ----------------
    /// <summary>The guard config while the switch is on, else null (callers keep the hud.json look).</summary>
    public static Guard GuardOn() { var g = Cfg().guard; return g != null && g.on ? g : null; }

    static readonly Dictionary<string, Texture2D> tex = new Dictionary<string, Texture2D>();
    /// <summary>Soft arc pointing up (same layout as HudDraw.Arc: band centre at HudDraw.ArcCentre of the half-size): smooth cross-section, tapered and faded ends, no hard edge.</summary>
    public static Texture2D GuardArc(Guard g)
    {
        int n = Mathf.Clamp(g.textureSize, 64, 1024);
        string key = "g|" + n + "|" + g.arcDeg + "|" + g.thickness + "|" + g.edgeSoft + "|" + g.endFade;
        Texture2D t; if (tex.TryGetValue(key, out t) && t != null) return t;
        t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave, name = "ArcFxGuard" };
        var px = new Color32[n * n]; float h = n * 0.5f, half = g.arcDeg * 0.5f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f - h) / h, dy = (y + 0.5f - h) / h, r = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Abs(Mathf.Atan2(dx, dy) * Mathf.Rad2Deg), a = 0f;
                if (ang < half)
                {
                    float u = ang / half, taper = 1f - u * u, w = g.thickness * taper * 0.5f;                    // half width of the band at this angle (tapers to a point)
                    float d = Mathf.Abs(r - HudDraw.ArcCentre);
                    float cross = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(w * (1f - Mathf.Clamp01(g.edgeSoft)), w, d));   // soft shoulder across the band
                    float along = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f - Mathf.Clamp01(g.endFade), 1f, u));     // fades out towards both tips
                    a = Mathf.Clamp01(cross * along);
                }
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        t.SetPixels32(px); t.Apply(false, false); tex[key] = t; return t;
    }
    public static Color GuardColor(Guard g, bool active, bool parry) { return active && parry ? C(g.parryTint, Color.white) : C(g.tint, Color.white); }
    public static float GuardAlpha(Guard g, bool active, bool parry) { return active ? (parry ? g.parryAlpha : g.activeAlpha) : g.idleAlpha; }

    // ---------------- weapon arc (WeaponTrail) ----------------
    /// <summary>True while the weapon arc switch is on (WeaponTrail then renders without the -trails launch flag).</summary>
    public static bool WeaponOn { get { var w = Cfg().weapon; return w != null && w.on; } }

    static Texture2D trailTex;
    static Texture2D TrailProfile(Weapon w)
    {
        if (trailTex != null) return trailTex;
        int n = Mathf.Clamp(w.profileSize, 8, 256);
        var t = new Texture2D(1, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave, name = "ArcFxTrailProfile" };
        var px = new Color32[n];
        for (int y = 0; y < n; y++)
        {
            float d = Mathf.Abs((y + 0.5f) / n * 2f - 1f);                                   // 0 = ribbon centre, 1 = edge
            float a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f - Mathf.Clamp01(w.edgeSoft), 1f, d));
            px[y] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
        }
        t.SetPixels32(px); t.Apply(false, false); trailTex = t; return t;
    }
    /// <summary>Restyle a weapon TrailRenderer as the soft curved arc. No-op while the switch is off.</summary>
    public static void StyleTrail(TrailRenderer tr)
    {
        var w = Cfg().weapon; if (tr == null || w == null || !w.on) return;
        tr.time = w.timeS; tr.minVertexDistance = w.minVertexDistanceM; tr.widthMultiplier = w.widthM;
        tr.numCornerVertices = w.cornerVertices; tr.numCapVertices = w.capVertices;
        tr.alignment = LineAlignment.View; tr.textureMode = LineTextureMode.Stretch;
        tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; tr.receiveShadows = false;
        tr.widthCurve = new AnimationCurve(new Keyframe(0f, w.shoulderWidth), new Keyframe(Mathf.Clamp01(w.shoulderAt), 1f), new Keyframe(1f, 0f));
        Color hc = C(w.headColor, Color.white), tc = C(w.tailColor, Color.gray);
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(hc, 0f), new GradientColorKey(Color.Lerp(hc, tc, w.midAt), w.midAt), new GradientColorKey(tc, 1f) },
                  new[] { new GradientAlphaKey(w.headAlpha, 0f), new GradientAlphaKey(w.midAlpha, w.midAt), new GradientAlphaKey(0f, 1f) });
        tr.colorGradient = g;
        if (tr.sharedMaterial != null && tr.sharedMaterial.name == "FxMat_" + w.role) return;   // already restyled (Apply() can run again)
        var m = FxMaterials.Particle(tr.sharedMaterial, w.role);
        if (m != null && m != tr.sharedMaterial) { m.SetTexture("_MainTex", TrailProfile(w)); tr.sharedMaterial = m; }
    }

    // ---------------- sparks (ClashFx / HitFeel) ----------------
    public static Sparks SparksOn() { var s = Cfg().sparks; return s != null && s.on ? s : null; }
    public static float SideMul() { var s = SparksOn(); return s != null ? s.sideSpreadMul : 1f; }
    public static Color SparkHot(Color d) { var s = SparksOn(); return s != null && s.hotColor != null && s.hotColor.Length >= 3 ? C(s.hotColor, d) : d; }
    public static Color SparkCool(Color d) { var s = SparksOn(); return s != null && s.coolColor != null && s.coolColor.Length >= 3 ? C(s.coolColor, d) : d; }
    public static float CountMul() { var s = SparksOn(); return s != null ? s.countScale : 1f; }
    public static float UpMul() { var s = SparksOn(); return s != null ? s.upRangeMul : 1f; }

    struct Tip { public Transform t; public float at; }
    static readonly Dictionary<DuelFighter, Tip> tips = new Dictionary<DuelFighter, Tip>();
    static Transform TipOf(DuelFighter f, Sparks s)
    {
        if (f == null) return null;
        Tip c; if (tips.TryGetValue(f, out c) && c.t != null && Time.unscaledTime - c.at < s.tipRefreshS) return c.t;
        Transform found = null;
        foreach (var wt in f.GetComponentsInChildren<WeaponTrail>(true)) { if (wt != null) { found = wt.transform; break; } }
        if (found == null) { foreach (var tt in f.GetComponentsInChildren<Transform>(true)) { if (tt != null && tt.name == s.tipNode) { found = tt; break; } } }
        tips[f] = new Tip { t = found, at = Time.unscaledTime };
        return found;
    }
    /// <summary>The spark origin for a contact: between the two weapon tips (blend), kept within maxFromBodyM of the old body anchor and inside the height band; the old anchor when off or a tip is missing.</summary>
    public static Vector3 ContactPoint(DuelFighter attacker, DuelFighter defender, string type, bool metalHit, Vector3 bodyAnchor)
    {
        var s = SparksOn(); if (s == null || !s.useContactPoint || attacker == null || defender == null) return bodyAnchor;
        bool clash = type == CombatClass.Blocked || type == CombatClass.Parried || type == CombatClass.GuardBroken;
        if (s.clashOnly && !clash && !metalHit) return bodyAnchor;
        var ta = TipOf(attacker, s); if (ta == null) return bodyAnchor;
        Vector3 p = ta.position;
        if (clash) { var td = TipOf(defender, s); if (td != null) p = Vector3.Lerp(ta.position, td.position, Mathf.Clamp01(s.contactBlend)); }
        float sc = FighterScale.Of(defender), floorY = defender.transform.position.y;
        p.y = Mathf.Clamp(p.y, floorY + s.minHeightM * sc, floorY + s.maxHeightM * sc);
        Vector3 off = p - bodyAnchor; float lim = s.maxFromBodyM * sc;
        if (off.magnitude > lim) p = bodyAnchor + off.normalized * lim;
        return p;
    }
}
