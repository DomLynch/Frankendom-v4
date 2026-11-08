using System;
using System.IO;
using UnityEngine;

// Data layer for the Sekiro-style posture system (P6): Assets/Data/posture.json (copy in StreamingAssets for the player).
// Lookup order (same as StaminaTable): Resources/posture, StreamingAssets/posture.json, Application.dataPath/Data/posture.json.
// Missing / unparsable file, or "enabled": false  =  exactly the old V3 behaviour (posture >= posture.max -> KnockDown, rules-defs posture.* values).
// Timings are whole 60 Hz sim ticks. Posture is a 0..max bar per fighter:
//   gains   : a clean hit adds hit.<class> to the receiver, a block adds block.<class> to the defender, a parry adds parry.attacker to the attacker (and removes parry.relief from the parrier);
//   decay   : after a hold delay with no new posture, it falls at decay.perSecond;
//   break   : posture reaching max = POSTURE BREAK: staggered for posBreak.stunTicks, and for posBreak.windowTicks the next hit on that fighter is a DEATHBLOW
//             (damage = max(dmg * damageMul, maxHealth * minHealthFrac), lethal when it would leave <= killBelowFrac; a lethal hit goes through the finisher hook as before).
public static class PostureTable
{
    [Serializable] public class FighterRow { public string key = ""; public float max = 0f; public int holdTicks = 0; public float decayPerSecond = -1f; public float deathblowMinHealthFrac = -1f; }
    [Serializable] public class GainRow { public float light = 14f, thrust = 12f, heavy = 26f, kick = 20f, other = -1f; }   // any value < 0 = the MoveDef.posture of the move (V3 data)
    [Serializable] public class BlockRow { public float light = 6f, heavy = 14f, kick = 10f, attackerBlocked = 0f; }       // attackerBlocked: posture the ATTACKER gains when its swing is blocked
    [Serializable] public class ParryRow { public float attacker = 30f, relief = 12f; }                                    // relief: posture the parrier loses
    [Serializable] public class DecayRow { public int holdTicks = 60; public float perSecond = 14f; }
    [Serializable] public class BreakRow { public int stunTicks = 100; public int windowTicks = 120; public float resetFraction = 0.25f; public string clip = "stagger_loop"; public int flashTicks = 30; }
    [Serializable] public class DeathblowRow { public float damageMul = 4f; public float minHealthFrac = 0.5f; public float killBelowFrac = 0.25f; public int staggerTicks = 60; }
    [Serializable] public class AiRow { public bool enabled = true; public float score = 2f; public int reactionTicks = 12; public float chanceBase = 0.7f; public float chanceAggression = 0.3f; public bool approach = true; public string[] order = { "light", "thrust", "heavy" }; }
    [Serializable] public class HudRow
    {
        public bool enabled = true, alwaysShow = true; public float height = 6f, gap = 2f, highFrom = 0.6f, pulseHz = 4f; public int labelFontSize = 12;
        public float[] bgColor = { 0f, 0f, 0f, 0.65f }, color = { 0.80f, 0.72f, 0.30f }, highColor = { 0.95f, 0.50f, 0.12f }, breakColor = { 0.95f, 0.18f, 0.12f }, deathblowColor = { 1f, 0.92f, 0.85f };
        public string breakText = "POSTURE BROKEN", deathblowText = "DEATHBLOW";
    }
    [Serializable] public class PostureData
    {
        public bool enabled = false, debugLog = false;
        public float defaultMax = 100f;
        public FighterRow[] fighters = new FighterRow[0];
        public GainRow hit = new GainRow(); public BlockRow block = new BlockRow(); public ParryRow parry = new ParryRow(); public DecayRow decay = new DecayRow();
        public BreakRow posBreak = new BreakRow(); public DeathblowRow deathblow = new DeathblowRow(); public AiRow ai = new AiRow(); public HudRow hud = new HudRow();
    }

    static PostureData pd;
    public static PostureData Get() { if (pd == null) pd = Load() ?? new PostureData(); return pd; }
    public static void Reload() { pd = null; }

    static PostureData Load()
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("posture");
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "posture.json"); if (File.Exists(p)) json = DataPath.ReadFile(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/posture.json"); if (File.Exists(p)) json = DataPath.ReadFile(p); }
        }
        catch (Exception ex) { Debug.LogWarning("PostureTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("PostureTable: posture.json not found; V3 posture rules in use."); return null; }
        try { return JsonUtility.FromJson<PostureData>(json); }
        catch (Exception ex) { Debug.LogError("PostureTable: parse failed: " + ex.Message); return null; }
    }

    static FighterRow Row(string key) { var f = Get().fighters; if (f != null) for (int i = 0; i < f.Length; i++) if (f[i] != null && f[i].key == key) return f[i]; return null; }

    // ---- per-fighter ----
    public static float Max(string key, float fallback) { if (!Get().enabled) return fallback; var r = Row(key); return r != null && r.max > 0f ? r.max : (Get().defaultMax > 0f ? Get().defaultMax : fallback); }
    public static int HoldTicks(string key, int fallback) { if (!Get().enabled) return fallback; var r = Row(key); return r != null && r.holdTicks > 0 ? r.holdTicks : Get().decay.holdTicks; }
    public static float DecayPerTick(string key, float fallbackPerTick) { if (!Get().enabled) return fallbackPerTick; var r = Row(key); float ps = r != null && r.decayPerSecond >= 0f ? r.decayPerSecond : Get().decay.perSecond; return ps / 60f; }
    public static float MinHealthFrac(string key) { var r = Row(key); return r != null && r.deathblowMinHealthFrac >= 0f ? r.deathblowMinHealthFrac : Get().deathblow.minHealthFrac; }

    // ---- gains ----
    public static string ClassOf(MoveDef m)
    {
        string id = m != null ? m.id : "";
        if (id == "kick") return "kick";
        if (id == "thrust" || id == "riposte") return "thrust";
        if (id.StartsWith("heavy")) return "heavy";
        if (id == "light_left" || id == "light_right" || id == "slash_riposte") return "light";
        return "other";
    }
    // posture a clean hit adds to the receiver (before the rear multiplier)
    public static float HitGain(MoveDef m)
    {
        var P = Get(); if (!P.enabled) return m.posture;
        float v;
        switch (ClassOf(m)) { case "light": v = P.hit.light; break; case "thrust": v = P.hit.thrust; break; case "heavy": v = P.hit.heavy; break; case "kick": v = P.hit.kick; break; default: v = P.hit.other; break; }
        if (m.posture <= 0f) return 0f;   // moves that carry no posture in the move data (the riposte family) stay at 0
        return v < 0f ? m.posture : v;
    }
    // posture a plain block adds to the defender
    public static float BlockGain(MoveDef m, bool heavy)
    {
        var P = Get(); if (!P.enabled) return m.posture * 0.5f;
        string c = ClassOf(m); return c == "kick" ? P.block.kick : (heavy || c == "heavy") ? P.block.heavy : P.block.light;
    }
    public static float ParryAttackerGain(float legacy) { var P = Get(); return P.enabled ? P.parry.attacker : legacy; }

    // ---- deathblow ----
    public static float DeathblowDamage(float dmg, DuelFighter victim)
    {
        var D = Get().deathblow;
        float v = Mathf.Max(dmg * D.damageMul, victim.maxHealth * MinHealthFrac(victim.PostureKey));
        if (victim.health - v <= victim.maxHealth * D.killBelowFrac) v = victim.health + 1f;   // lethal: Duel.Resolve then runs RTFinisher.TryBegin, or the plain death when finishers are off
        return v;
    }

    // ---- AI ----
    public static bool AiGo(DuelFighter self, DuelFighter opp)
    {
        var P = Get(); if (!P.enabled || !P.ai.enabled || self == null || opp == null || self.Dead) return false;
        return opp.DeathblowOpen && opp.DeathblowAge >= P.ai.reactionTicks;
    }
    public static float AiChance(float aggression) { var A = Get().ai; return Mathf.Clamp01(A.chanceBase + A.chanceAggression * aggression); }

    public static Color Col(float[] a, float alpha = -1f) { if (a == null || a.Length < 3) return new Color(1f, 0f, 1f, alpha < 0f ? 1f : alpha); return new Color(a[0], a[1], a[2], alpha >= 0f ? alpha : (a.Length > 3 ? a[3] : 1f)); }
}
