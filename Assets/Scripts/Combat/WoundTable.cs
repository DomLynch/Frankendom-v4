using System;
using System.IO;
using UnityEngine;

// Wounds as DATA (Assets/Data/wounds.json, copy in StreamingAssets): wound decals that build up on the body, HP-threshold damage states (limp, breathing, guard sag, drips),
// a leg-hit slow, a stamina-ceiling loss per wound and the player's low-HP screen-edge vignette. Rows are keyed by roster character id; the row id "*" is the default
// that any character without its own row (a new one, e.g. Joan) gets. A missing / broken file = built-in defaults below (same numbers as the shipped "*" row), "enabled": false = everything off.
// Rows are standalone: a character row only inherits the C# field defaults for fields it leaves out, so copy the "*" row and change what differs.
public static class WoundTable
{
    // one wound shape: length is along the streak, width across it; rollDeg is added to the swing roll (slash = horizontal, overhead = vertical, stab = round)
    [Serializable] public class Shape { public string tex = "streak"; public float length = 0.34f, width = 0.07f, baseRollDeg = 0f, tiltDeg = 18f; }
    // an HP-threshold damage state; the lowest hpFrac that frac is below wins. dripEverySec 0 = no drips
    [Serializable] public class Stage { public string id = ""; public float hpFrac = 0.5f, moveMul = 1f, regenMul = 1f, guardLayerMul = 1f, dripEverySec = 0f; public int guardRaiseExtraTicks = 0, dripSplats = 1; public float dripSizeMul = 0.6f; }
    [Serializable] public class Row
    {
        public string id = "*";
        public int maxDecals = 10;                        // cap per fighter; the oldest fades out when exceeded
        public float fadeOutSec = 2.5f, lifeSec = 0f;     // fade length of an evicted decal; lifeSec > 0 = decals also fade out that long after landing (0 = stay for the fight)
        public float opacity = 0.95f, sizeJitter = 0.2f, heavySizeMul = 1.3f, killSizeMul = 1.5f, counterSizeMul = 1.15f;
        public float depth = 0.5f, bodyRadius = 0.2f, lateral = 0.14f, lateralJitter = 0.06f;   // projector box depth (m), distance from body axis to the surface the blow lands on, sideways offset for left/right swings
        public float headY = 1.55f, torsoY = 1.15f, armY = 1.2f, legsY = 0.5f, heightJitter = 0.08f;   // hit heights (m at scale 1)
        public Shape slash = new Shape { tex = "streak", length = 0.34f, width = 0.07f, baseRollDeg = 0f, tiltDeg = 18f };
        public Shape stab = new Shape { tex = "puncture", length = 0.1f, width = 0.1f, baseRollDeg = 0f, tiltDeg = 180f };
        public Shape overhead = new Shape { tex = "streak", length = 0.36f, width = 0.07f, baseRollDeg = 90f, tiltDeg = 8f };
        public float legSlowSec = 4f, legSlowMul = 0.85f;                    // V3: a leg hit slows the walk for 4 s
        public float staminaCeilPerWound = 0.02f, staminaCeilMin = 0.7f;      // each landed hit lowers the stamina ceiling (fraction of max) down to this floor
        public float blendSec = 0.6f;                                         // damage-state multipliers ease in over this long
        public Stage[] stages = new Stage[]
        {
            new Stage { id = "hurt",  hpFrac = 0.5f,  moveMul = 0.92f, regenMul = 0.85f, guardLayerMul = 0.9f, guardRaiseExtraTicks = 4,  dripEverySec = 0f },
            new Stage { id = "dying", hpFrac = 0.25f, moveMul = 0.78f, regenMul = 0.6f,  guardLayerMul = 0.5f, guardRaiseExtraTicks = 10, dripEverySec = 1.6f, dripSplats = 1, dripSizeMul = 0.6f }
        };
    }
    [Serializable] public class DecalCfg
    {
        public float[] color = new float[] { 0.25f, 0.02f, 0.02f }; public float smoothness = 0.8f;
        public int texSize = 64, variants = 3, seed = 41; public float drawDistance = 25f, fadeScale = 0.8f, startAngleFade = 0f, endAngleFade = 180f;
        public bool quadFallback = true;   // no HDRP/Decal shader in the player = a bone-parented blood quad instead (BloodGround's material)
    }
    [Serializable] public class VignetteCfg
    {
        public bool enabled = true; public float startFrac = 0.35f, maxAlpha = 0.55f, inner = 0.45f, pulseHz = 0.9f, pulseDepth = 0.2f, fadeInSec = 0.6f; public int texSize = 128, guiDepth = -900;
        public float[] color = new float[] { 0.18f, 0f, 0.01f };
    }
    [Serializable] public class Data
    {
        public int version = 1; public bool enabled = true, decals = true, states = true, debugLog = false; public float refreshSec = 0.5f;
        public DecalCfg decal = new DecalCfg(); public VignetteCfg vignette = new VignetteCfg(); public Row[] characters = new Row[0];
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    static readonly Row builtin = new Row();
    // row for a roster character id: its own row, else "*", else the built-in defaults (never null)
    public static Row For(string id)
    {
        var d = Get(); Row star = null;
        if (d.characters != null)
            for (int i = 0; i < d.characters.Length; i++)
            {
                var r = d.characters[i]; if (r == null) continue;
                if (!string.IsNullOrEmpty(id) && r.id == id) return r;
                if (r.id == "*") star = r;
            }
        return star ?? builtin;
    }

    static void Load()
    {
        data = new Data(); string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("wounds");
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "wounds.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/wounds.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("WoundTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) return;   // no file = built-in defaults (feature on)
        try
        {
            var dd = JsonUtility.FromJson<Data>(json); if (dd == null) return;
            if (dd.decal == null) dd.decal = new DecalCfg();
            if (dd.vignette == null) dd.vignette = new VignetteCfg();
            if (dd.characters == null) dd.characters = new Row[0];
            if (dd.decal.color == null || dd.decal.color.Length < 3) dd.decal.color = new float[] { 0.25f, 0.02f, 0.02f };
            if (dd.vignette.color == null || dd.vignette.color.Length < 3) dd.vignette.color = new float[] { 0.18f, 0f, 0.01f };
            foreach (var r in dd.characters)
            {
                if (r == null) continue;
                if (r.stages == null) r.stages = new Stage[0];
                if (r.slash == null) r.slash = new Shape();
                if (r.stab == null) r.stab = new Shape();
                if (r.overhead == null) r.overhead = new Shape();
            }
            data = dd;
        }
        catch (Exception ex) { Debug.LogError("WoundTable: parse failed: " + ex.Message); }
    }
}
