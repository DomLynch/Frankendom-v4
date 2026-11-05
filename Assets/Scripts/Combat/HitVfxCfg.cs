using System;
using UnityEngine;

// HCKIT4 v95 (HC3 K10a): CombatVFX data (hit_vfx.json). Particle system shapes, sizes, lifetimes, colours, burst counts and cones were inline numbers in CombatVFX.cs.
// Every default below = the old literal, so a missing file changes nothing. Colours are [r, g, b, a]. hit_feel.json blood.dropColor / mistColor still colour the drops and the mist (colorFrom).
public static class HitVfxCfg
{
    [Serializable]
    public class Sys
    {
        public string material = "spark";                                  // spark | dust | blood (CombatVFX material slots)
        public float sizeMin = 0.015f, sizeMax = 0.03f, lifeMin = 0.1f, lifeMax = 0.25f, speedMin = 4f, speedMax = 10f, gravity = -1f;   // main.gravityModifier = -gravity
        public float[] color = { 1f, 1f, 1f, 1f };
        public string colorFrom = "";                                      // "dropColor" | "mistColor" = hit_feel.json blood.* instead of color
        public bool stretch = false; public float[] stretchFrom = { 1f, 1f, 1f, 1f }, stretchTo = { 1f, 1f, 1f, 1f };
        public bool billboard = false; public float billboardMaxSize = 0.06f;
    }
    [Serializable] public class Common { public int maxParticles = 200; public float shapeRadius = 0.08f, rendererMaxSize = 0.25f, fadeMidAlpha = 0.8f, fadeMidAt = 0.5f; }
    [Serializable] public class Stretch { public float velocityScale = 0.06f, lengthScale = 5.2f, cameraVelocityScale = 0f, maxParticleSize = 0.5f, midAt = 0.6f, midAlpha = 0.9f, midAlphaAt = 0.5f; }
    [Serializable]
    public class Hit
    {
        public int flashCount = 1; public float flashCone = 5f;
        public int dropsLight = 7, dropsHeavy = 16; public float dropsCone = 40f;
        public int sparksLight = 4, sparksHeavy = 9; public float sparksCone = 35f;
        public int puffLight = 1, puffHeavy = 2; public float puffCone = 60f, puffDirMix = 0.3f;
        public int mistLight = 2, mistHeavy = 5; public float mistCone = 50f;
    }
    [Serializable] public class Block { public int flashCount = 1; public float flashCone = 5f; public int sparks = 12, sparksPerfect = 22; public float sparksCone = 50f, upMix = 0.3f; }
    [Serializable] public class Parried { public int sparks = 30; public float cone = 70f, dirMix = 0.4f; }
    [Serializable] public class GuardBroken { public int sparks = 36, puff = 6; public float sparksCone = 80f, puffCone = 80f; }
    [Serializable]
    public class Data
    {
        public float hitHeightM = 1.1f, puffDropM = 0.4f, burstShapeRadius = 0.05f, hitDirY = 0.15f, prewarmLifeS = 0.1f;
        public Common common = new Common(); public Stretch stretchCfg = new Stretch();
        public Sys sparks = HitVfxCfg.DSparks(), guardSparks = HitVfxCfg.DGuardSparks(), flash = HitVfxCfg.DFlash(), drops = HitVfxCfg.DDrops(), puff = HitVfxCfg.DPuff(), mist = HitVfxCfg.DMist();
        public Hit hit = new Hit(); public Block block = new Block(); public Parried parried = new Parried(); public GuardBroken guardBroken = new GuardBroken();
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; }
    public static Color C(float[] a, Color fallback) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f) : fallback; }

    static Sys S(string mat, float s0, float s1, float l0, float l1, float v0, float v1, float[] c, float g) { return new Sys { material = mat, sizeMin = s0, sizeMax = s1, lifeMin = l0, lifeMax = l1, speedMin = v0, speedMax = v1, color = c, gravity = g }; }
    public static Sys DSparks() { var s = S("spark", 0.015f, 0.03f, 0.1f, 0.25f, 4f, 10f, new[] { 1f, 0.9f, 0.55f, 1f }, -1f); s.stretch = true; s.stretchFrom = new[] { 1f, 0.95f, 0.7f, 1f }; s.stretchTo = new[] { 1f, 0.45f, 0.1f, 1f }; return s; }
    public static Sys DGuardSparks() { var s = S("spark", 0.015f, 0.03f, 0.1f, 0.25f, 4f, 9f, new[] { 1f, 0.97f, 0.8f, 1f }, -1f); s.stretch = true; s.stretchFrom = new[] { 1f, 1f, 0.9f, 1f }; s.stretchTo = new[] { 1f, 0.55f, 0.15f, 1f }; return s; }
    public static Sys DFlash() { return S("spark", 0.25f, 0.35f, 0.05f, 0.08f, 0f, 0f, new[] { 1f, 0.85f, 0.6f, 0.8f }, 0f); }
    public static Sys DDrops() { var s = S("blood", 0.02f, 0.04f, 0.4f, 0.8f, 2f, 5f, new[] { 0.145f, 0.008f, 0.008f, 1f }, -1.2f); s.colorFrom = "dropColor"; s.billboard = true; return s; }
    public static Sys DPuff() { return S("dust", 0.3f, 0.6f, 0.6f, 1f, 0.3f, 0.9f, new[] { 0.4f, 0.4f, 0.42f, 0.28f }, -0.05f); }
    public static Sys DMist() { var s = S("blood", 0.03f, 0.07f, 0.35f, 0.6f, 1.5f, 3.5f, new[] { 0.22f, 0.015f, 0.015f, 0.95f }, -1.2f); s.colorFrom = "mistColor"; return s; }
    static void Fix(Data d)
    {
        var f = new Data();
        if (d.common == null) d.common = f.common; if (d.stretchCfg == null) d.stretchCfg = f.stretchCfg;
        if (d.sparks == null) d.sparks = f.sparks; if (d.guardSparks == null) d.guardSparks = f.guardSparks; if (d.flash == null) d.flash = f.flash;
        if (d.drops == null) d.drops = f.drops; if (d.puff == null) d.puff = f.puff; if (d.mist == null) d.mist = f.mist;
        if (d.hit == null) d.hit = f.hit; if (d.block == null) d.block = f.block; if (d.parried == null) d.parried = f.parried; if (d.guardBroken == null) d.guardBroken = f.guardBroken;
    }
    static void Load()
    {
        var d = new Data();
        try
        {
            string s = DataPath.ReadText("hit_vfx.json");
            if (!string.IsNullOrEmpty(s)) { var r = JsonUtility.FromJson<Data>(s); if (r != null) { Fix(r); d = r; } }
        }
        catch (Exception ex) { Debug.LogWarning("HitVfxCfg: hit_vfx.json read failed: " + ex.Message); }
        data = d;
    }
}
