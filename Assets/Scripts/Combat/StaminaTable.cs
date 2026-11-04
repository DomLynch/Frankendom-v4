using System;
using System.IO;
using UnityEngine;

// Data layer for stamina, guard hold, guard break, plain block, exhausted state and the combat HUD bars: Assets/Data/stamina.json,
// and for the parry window / riposte: Assets/Data/parry.json (copies in StreamingAssets for the player).
// Lookup order for each file (same as DodgeTable): Resources/<name>, StreamingAssets/<name>.json, Application.dataPath/Data/<name>.json.
// A missing or unparsable file keeps the code defaults below. They reproduce the V3 behaviour (rules-defs.json values), so the game plays as before.
// "enabled": false in stamina.json = pure V3 regen / costs (RegenDelay, MoveCost, BlockCost fall back to the rules table).
// All timings are whole 60 Hz sim ticks; costs are stamina points on a 0..maxStamina bar.
public static class StaminaTable
{
    [Serializable] public class CostRow { public string id = ""; public float cost = 0f; }
    [Serializable] public class FighterRow { public string key = ""; public float maxStamina = 0f; public float regenMul = 1f; public int parryWindowTicks = 0; }
    [Serializable] public class RegenRow
    {
        public int delayTicks = 50;            // no regen for this long after ANY stamina use (attack, dodge, block)
        public float perSecond = 22f;          // stamina per second once the delay is over (before the multipliers below)
        public float guardMul = 0f;            // multiplier while the guard is up (0 = no regen while guarding)
        public int exhaustedDelayTicks = 100;  // delay after the bar hit empty
        public float exhaustedMul = 0.8f;      // regen multiplier while exhausted
    }
    [Serializable] public class DodgeCostRow { public float roll = 24f, backstep = 10f, sidestep = 8f; }   // -1 = keep the dodge.json value
    [Serializable] public class GuardRow
    {
        public int holdFreeTicks = 30;         // guard hold is free for this long, then it drains
        public float holdDrainPerSecond = 4f;  // slow drain while the guard stays up (0 = off)
        public bool allowAtZero = true;        // the guard can still be raised at 0 stamina (never a hard lock); a block at 0 breaks it
        public int raiseDelayExhausted = 8;    // exhausted: the guard only counts as raised after this many ticks (slow guard)
    }
    [Serializable] public class ExhaustedRow
    {
        public float recoverAt = 25f;          // exhausted from 0 stamina until the bar is back to this value (>= the dearest dodge, so a roll is possible again)
        public float walkMul = 0.85f;          // move speed multiplier
        public float damageMul = 0.75f;        // damage dealt multiplier (light attacks still work, at a penalty)
        public float heavyMinStamina = 18f;    // no heavy below this: the heavy key throws a light instead
        public bool noParry = true;            // no parry window while exhausted (a plain block only)
        public float blockCostMul = 1.3f;      // block stamina cost multiplier
        public float chipMul = 2f;             // chip damage multiplier on a block
    }
    [Serializable] public class BlockRow
    {
        public float baseLight = 12f;          // stamina per blocked light / slash / stab
        public float baseHeavy = 24f;          // stamina per blocked heavy
        public float staminaDamageMul = 0.3f;  // plus the move's staminaDamage * this
        public float chipMin = 0.08f;          // chip = damage * max(move.chip, chipMin) taken through a plain block (never kills: leaves 1 hp)
    }
    [Serializable] public class GuardBreakRow
    {
        public float heavyBreakBelow = 20f;    // a blocked heavy breaks the guard when stamina is at or below this after the block cost (0 = only at empty)
        public int staggerTicks = 54;          // the stagger window after a break: a free hit
        public float staminaAfter = 0f;        // stamina left after a break
        public int flashTicks = 24;            // HUD flash length
    }
    [Serializable] public class HudRow
    {
        public bool enabled = true;
        public float barHeight = 6f, gap = 4f, borderPx = 1f;
        public float postureHeight = 3f, postureShowAbove = 12f;   // the posture bar shows only when it is not empty
        public bool showForEnemy = true;
        public float[] staminaColor = { 0.30f, 0.55f, 0.30f }, lowColor = { 0.78f, 0.55f, 0.16f }, exhaustedColor = { 0.60f, 0.10f, 0.08f };
        public float[] guardColor = { 0.62f, 0.66f, 0.74f }, parryColor = { 0.98f, 0.80f, 0.28f }, breakColor = { 0.95f, 0.18f, 0.12f }, postureColor = { 0.80f, 0.72f, 0.30f };
        public float lowFraction = 0.30f;      // below this fraction the bar turns amber
        public float exhaustedPulseHz = 3f;
        public int parryFlashTicks = 20;
        public int labelFontSize = 11;
        public string breakText = "GUARD BROKEN", parryText = "PARRY";
    }
    [Serializable] public class StaminaData
    {
        public bool enabled = true;
        public bool debugLog = false;          // logs [Stamina] break / exhausted lines
        public float defaultMax = 100f;
        public FighterRow[] fighters = new FighterRow[0];
        public RegenRow regen = new RegenRow();
        public CostRow[] costs = new CostRow[0];   // by MoveDef id; ids not listed keep MoveDef.stamina (V3); skills stay neutral
        public DodgeCostRow dodge = new DodgeCostRow();
        public GuardRow guard = new GuardRow();
        public ExhaustedRow exhausted = new ExhaustedRow();
        public BlockRow block = new BlockRow();
        public GuardBreakRow guardBreak = new GuardBreakRow();
        public HudRow hud = new HudRow();
    }
    [Serializable] public class ParryData
    {
        public bool enabled = true;
        public int windowTicks = 10;               // parry window from the first tick of the guard (V3 rule "parry" = 10)
        public int cooldownTicks = 30;             // re-raising the guard inside this time after the last raise gets no window (anti-spam; V3 "parryCooldown")
        public int attackerStaggerTicks = 80;      // the parried attacker's stagger (V3 "parryStun" = 90)
        public int attackerStaggerHeavyTicks = 100;
        public int riposteWindowTicks = 90;        // the parrier may riposte inside this time
        public int selfRecoveryTicks = 8;          // the parrier's own recovery (V3 "parryRecovery")
        public float attackerPosture = 25f;        // V3 "posture.parry"
        public float staminaCost = 4f;             // small cost of a successful parry (a plain block costs far more)
        public bool parryHeavies = true;           // false: a heavy cannot be parried, only blocked
    }

    static StaminaData sd; static ParryData pd;
    public static StaminaData Get() { if (sd == null) sd = Load<StaminaData>("stamina") ?? new StaminaData(); return sd; }
    public static ParryData Parry() { if (pd == null) pd = Load<ParryData>("parry") ?? new ParryData(); return pd; }
    public static void Reload() { sd = null; pd = null; }

    static T Load<T>(string name) where T : class
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>(name);
            if (ta != null) json = ta.text;
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, name + ".json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/" + name + ".json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("StaminaTable: read failed " + name + ": " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("StaminaTable: " + name + ".json not found; code defaults in use."); return null; }
        try { return JsonUtility.FromJson<T>(json); }
        catch (Exception ex) { Debug.LogError("StaminaTable: parse failed " + name + ": " + ex.Message); return null; }
    }

    static FighterRow Row(string key) { var f = Get().fighters; if (f != null) for (int i = 0; i < f.Length; i++) if (f[i] != null && f[i].key == key) return f[i]; return null; }

    // ---- per-fighter values ----
    public static float MaxStamina(string key, float fallback) { var r = Row(key); return r != null && r.maxStamina > 0f ? r.maxStamina : (Get().enabled && Get().defaultMax > 0f ? Get().defaultMax : fallback); }
    public static float RegenMul(string key) { var r = Row(key); return r != null ? r.regenMul : 1f; }
    public static int ParryWindow(string key, int fallback) { var r = Row(key); if (r != null && r.parryWindowTicks > 0) return r.parryWindowTicks; return Parry().enabled && Parry().windowTicks > 0 ? Parry().windowTicks : fallback; }

    // ---- costs ----
    public static float MoveCost(MoveDef m)
    {
        var s = Get();
        if (s.enabled && s.costs != null) for (int i = 0; i < s.costs.Length; i++) if (s.costs[i] != null && s.costs[i].id == m.id) return s.costs[i].cost;
        return m.stamina;
    }
    public static float DodgeCost(string kind, float dodgeJsonValue)
    {
        var s = Get(); if (!s.enabled) return dodgeJsonValue;
        float v = kind == "roll" ? s.dodge.roll : kind == "backstep" ? s.dodge.backstep : s.dodge.sidestep;
        return v >= 0f ? v : dodgeJsonValue;
    }
    // stamina a plain block costs the defender (parry cost is parry.json; kick vs guard is the move's own vsGuard row)
    public static float BlockCost(MoveDef m, bool heavy, bool defenderExhausted, float v3Block)
    {
        var s = Get(); if (!s.enabled) return v3Block * (heavy ? 1.2f : 1f) + m.staminaDamage * 0.3f;
        float c = (heavy ? s.block.baseHeavy : s.block.baseLight) + m.staminaDamage * s.block.staminaDamageMul;
        return defenderExhausted ? c * s.exhausted.blockCostMul : c;
    }
    public static float ChipFrac(MoveDef m, bool defenderExhausted)
    {
        var s = Get(); if (!s.enabled) return m.chip;
        float c = Mathf.Max(m.chip, s.block.chipMin);
        return defenderExhausted ? c * s.exhausted.chipMul : c;
    }
    // staminaAfter = the defender's stamina AFTER the block cost was taken (it can be below 0)
    public static bool BreaksGuard(float staminaAfter, MoveDef m, bool heavy)
    {
        if (staminaAfter <= 0f || m.breaksGuard) return true;
        var s = Get(); return s.enabled && heavy && s.guardBreak.heavyBreakBelow > 0f && staminaAfter <= s.guardBreak.heavyBreakBelow;
    }
    public static int ParryStagger(bool heavy, int v3) { var p = Parry(); return !p.enabled ? v3 : (heavy ? p.attackerStaggerHeavyTicks : p.attackerStaggerTicks); }

    public static Color Col(float[] a, float alpha = 1f) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], alpha) : new Color(1f, 0f, 1f, alpha); }
}
