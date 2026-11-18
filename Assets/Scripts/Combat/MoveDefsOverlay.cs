using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// laneGUARD2 v82 (NEW). Adds the move rows that exist in Assets/Data/move-defs.json (StreamingAssets copy in the player) but not in the baked Moves.asset
// (heavy_left / heavy_right / heavy_thrust / light_overhead: the asset was imported from the V3 file once and nobody re-imports it, so the v70 JSON rows never reached the game
// and controls.json attack.map fell back to heavy_overhead). Rows already in the asset are NEVER touched unless the row says "overrides": true.
// Called once by MoveTable.Get when it builds its lookup; the asset's own list is not modified (nothing to save / dirty).
//
// FIELD CONTRACT for existing-move overrides (v2, README in _agents/laneLG/fix_moveoverride):
//   "overrides": true and NO "set"  -> legacy behaviour, bit for bit: windup, active (if >0), recovery, damage (if >0), chained timings (if chained.windup >0); needs windup > 0.
//   "overrides": true and "set": ["reach","stagger",...] -> the default set above (when windup > 0) PLUS exactly the named fields, applied literally (0 / false / empty are values).
//   Overridable names: windup active recovery damage chained stamina staminaDamage stagger poise poiseFrom chip knockback stepIn reach posture chamber arcDeg
//                      breaksGuard parryable charges chain (window + follow) vsGuard (stagger + staminaDamage).
//   NOT overridable (identity / animation routing / not in the JSON class): id direction path weaponStaminaMul hasChained hasVsGuard. Any other name in "set" logs a warning and is ignored.
public static class MoveDefsOverlay
{
    [Serializable] class Chained { public int windup, active, recovery; }
    [Serializable] class Chain { public int window; public string[] follow; }
    [Serializable] class VsGuard { public float stagger, staminaDamage; }
    [Serializable] class MoveJ
    {
        public string id, direction, path;
        public int windup, active, recovery;
        public Chained chained; public Chain chain; public VsGuard vsGuard;
        public float damage, stamina, staminaDamage, stagger, poise, poiseFrom, chip, knockback, stepIn, reach, posture, chamber, arcDeg;   // ABILITIES v1: arcDeg
        public bool breaksGuard, parryable, charges;
        public bool overrides;   // WEIGHT v1: true = this row's fields replace the baked Moves.asset row of the same id (a copy; everything else of the asset row stays)
        public string[] set;     // FIELD CONTRACT: names of the fields to apply (absent = the default timing/damage set). JsonUtility cannot tell absent from 0, so the row names what it means.
    }
    [Serializable] class MovesJ { public MoveJ[] moves; }

    // one entry per overridable field: read from a MoveDef (for the log), write from the json row, and the legacy rule for the default set
    class Field { public Func<MoveDef, string> read; public Action<MoveDef, MoveJ> write; public Func<MoveJ, bool> legacyApplies; }
    static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    static string B(bool v) { return v ? "true" : "false"; }
    static Field Fl(Func<MoveDef, float> g, Action<MoveDef, float> s, Func<MoveJ, float> j) { return new Field { read = d => F(g(d)), write = (d, m) => s(d, j(m)) }; }
    static Field Bo(Func<MoveDef, bool> g, Action<MoveDef, bool> s, Func<MoveJ, bool> j) { return new Field { read = d => B(g(d)), write = (d, m) => s(d, j(m)) }; }

    static readonly string[] DefaultSet = { "windup", "active", "recovery", "damage", "chained" };   // what a row without "set" has always applied
    static readonly Dictionary<string, Field> Fields = BuildFields();

    static Dictionary<string, Field> BuildFields()
    {
        var t = new Dictionary<string, Field>();
        t["windup"] = new Field { read = d => d.windup.ToString(), write = (d, m) => d.windup = m.windup, legacyApplies = m => true };
        t["active"] = new Field { read = d => d.active.ToString(), write = (d, m) => d.active = m.active, legacyApplies = m => m.active > 0 };
        t["recovery"] = new Field { read = d => d.recovery.ToString(), write = (d, m) => d.recovery = m.recovery, legacyApplies = m => true };
        t["damage"] = new Field { read = d => F(d.damage), write = (d, m) => d.damage = m.damage, legacyApplies = m => m.damage > 0f };
        t["chained"] = new Field
        {
            read = d => d.hasChained ? d.chainedWindup + "/" + d.chainedActive + "/" + d.chainedRecovery : "none",
            write = (d, m) =>
            {
                bool has = m.chained != null && m.chained.windup > 0;   // named in "set": a missing / empty chained block clears the chained timings
                d.hasChained = has; d.chainedWindup = has ? m.chained.windup : 0; d.chainedActive = has ? m.chained.active : 0; d.chainedRecovery = has ? m.chained.recovery : 0;
            },
            legacyApplies = m => m.chained != null && m.chained.windup > 0
        };
        t["stamina"] = Fl(d => d.stamina, (d, v) => d.stamina = v, m => m.stamina);
        t["staminaDamage"] = Fl(d => d.staminaDamage, (d, v) => d.staminaDamage = v, m => m.staminaDamage);
        t["stagger"] = Fl(d => d.stagger, (d, v) => d.stagger = v, m => m.stagger);
        t["poise"] = Fl(d => d.poise, (d, v) => d.poise = v, m => m.poise);
        t["poiseFrom"] = Fl(d => d.poiseFrom, (d, v) => d.poiseFrom = v, m => m.poiseFrom);
        t["chip"] = Fl(d => d.chip, (d, v) => d.chip = v, m => m.chip);
        t["knockback"] = Fl(d => d.knockback, (d, v) => d.knockback = v, m => m.knockback);
        t["stepIn"] = Fl(d => d.stepIn, (d, v) => d.stepIn = v, m => m.stepIn);
        t["reach"] = Fl(d => d.reach, (d, v) => d.reach = v, m => m.reach);
        t["posture"] = Fl(d => d.posture, (d, v) => d.posture = v, m => m.posture);
        t["chamber"] = Fl(d => d.chamber, (d, v) => d.chamber = v, m => m.chamber);
        t["arcDeg"] = Fl(d => d.arcDeg, (d, v) => d.arcDeg = v, m => m.arcDeg);
        t["breaksGuard"] = Bo(d => d.breaksGuard, (d, v) => d.breaksGuard = v, m => m.breaksGuard);
        t["parryable"] = Bo(d => d.parryable, (d, v) => d.parryable = v, m => m.parryable);
        t["charges"] = Bo(d => d.charges, (d, v) => d.charges = v, m => m.charges);
        t["chain"] = new Field
        {
            read = d => d.chainFollow != null && d.chainFollow.Length > 0 ? d.chainWindow + " [" + string.Join(",", d.chainFollow) + "]" : "none",
            write = (d, m) =>
            {
                bool has = m.chain != null && m.chain.follow != null && m.chain.follow.Length > 0;   // empty / missing = clear the chain
                d.chainWindow = has ? m.chain.window : 0; d.chainFollow = has ? m.chain.follow : null;
            }
        };
        t["vsGuard"] = new Field
        {
            read = d => d.hasVsGuard ? F(d.vsGuardStagger) + "/" + F(d.vsGuardStaminaDamage) : "none",
            write = (d, m) =>
            {
                bool has = m.vsGuard != null && (m.vsGuard.stagger > 0f || m.vsGuard.staminaDamage > 0f);   // JsonUtility never yields null for a nested class: presence = a non-zero value
                d.hasVsGuard = has; d.vsGuardStagger = has ? m.vsGuard.stagger : 0f; d.vsGuardStaminaDamage = has ? m.vsGuard.staminaDamage : 0f;
            }
        };
        return t;
    }

    // Applies one override row to a COPY of the asset row. Returns the copy, or null when the row applies nothing.
    static MoveDef ApplyOverride(MoveDef asset, MoveJ m, out string summary)
    {
        summary = null;
        var named = new HashSet<string>();
        if (m.set != null)
            foreach (var n in m.set)
            {
                if (string.IsNullOrEmpty(n) || !Fields.ContainsKey(n)) { Debug.LogWarning("[MoveDefsOverlay] " + m.id + ": 'set' names unsupported field '" + n + "' (ignored, the asset value stays). Overridable: " + string.Join(", ", new List<string>(Fields.Keys).ToArray())); continue; }
                named.Add(n);
            }
        bool legacy = m.windup > 0;   // the old gate: a row with no windup never applied its timing/damage defaults
        if (!legacy && named.Count == 0) return null;
        var o = asset.Copy();
        var applied = new List<string>();
        foreach (var kv in Fields)   // fields are independent: order does not matter
        {
            string name = kv.Key; var f = kv.Value;
            bool inSet = named.Contains(name);
            bool apply = inSet || (legacy && Array.IndexOf(DefaultSet, name) >= 0 && f.legacyApplies(m));
            if (!apply) continue;
            string before = f.read(o);
            f.write(o, m);
            string after = f.read(o);
            Debug.Log("[MoveDefsOverlay] " + o.id + "." + name + " " + before + " -> " + after + " (" + (inSet ? "json override, set" : "json override, default timing/damage set") + (before == after ? ", unchanged" : "") + ")");
            if (inSet) applied.Add(name);
        }
        summary = o.id + " " + o.windup + "/" + o.active + "/" + o.recovery + (applied.Count > 0 ? " +set[" + string.Join(",", applied.ToArray()) + "]" : "");
        return o;
    }

    public static void Merge(Dictionary<string, MoveDef> map)
    {
        try
        {
            foreach (var p in new[] { Application.streamingAssetsPath + "/move-defs.json", Application.dataPath + "/Data/move-defs.json" })
            {
                if (!File.Exists(p)) continue;
                var mj = JsonUtility.FromJson<MovesJ>(DataPath.ReadFile(p));
                if (mj == null || mj.moves == null) return;
                string added = "", overrode = "";
                foreach (var m in mj.moves)
                {
                    if (m != null && m.overrides && !string.IsNullOrEmpty(m.id) && map.ContainsKey(m.id))   // WEIGHT v1 + FIELD CONTRACT: data override of a baked row
                    {
                        string sum;
                        var o = ApplyOverride(map[m.id], m, out sum);
                        if (o != null) { map[o.id] = o; overrode += (overrode.Length > 0 ? ", " : "") + sum; continue; }
                    }
                    if (m == null || string.IsNullOrEmpty(m.id) || map.ContainsKey(m.id)) continue;
                    if (m.windup <= 0 && m.active <= 0) continue;   // not a full row (no timings): never add a half-filled move
                    var d = new MoveDef
                    {
                        id = m.id, direction = m.direction, path = m.path, windup = m.windup, active = m.active, recovery = m.recovery,
                        damage = m.damage, stamina = m.stamina, staminaDamage = m.staminaDamage, stagger = m.stagger, poise = m.poise, poiseFrom = m.poiseFrom,
                        chip = m.chip, knockback = m.knockback, stepIn = m.stepIn, reach = m.reach, posture = m.posture, chamber = m.chamber, arcDeg = m.arcDeg,
                        breaksGuard = m.breaksGuard, parryable = m.parryable, charges = m.charges
                    };
                    if (m.chained != null && m.chained.windup > 0) { d.hasChained = true; d.chainedWindup = m.chained.windup; d.chainedActive = m.chained.active; d.chainedRecovery = m.chained.recovery; }
                    if (m.chain != null && m.chain.follow != null && m.chain.follow.Length > 0) { d.chainWindow = m.chain.window; d.chainFollow = m.chain.follow; }
                    if (m.vsGuard != null && m.vsGuard.stagger > 0) { d.hasVsGuard = true; d.vsGuardStagger = m.vsGuard.stagger; d.vsGuardStaminaDamage = m.vsGuard.staminaDamage; }
                    map[d.id] = d; added += (added.Length > 0 ? ", " : "") + d.id;
                }
                if (overrode.Length > 0) Debug.Log("[MoveDefsOverlay] weight overrides applied (windup/active/recovery): " + overrode);
                if (added.Length > 0) Debug.Log("[MoveDefsOverlay] rows added from " + Path.GetFileName(p) + " (not in Moves.asset): " + added);
                return;
            }
        }
        catch (Exception ex) { Debug.LogWarning("[MoveDefsOverlay] skipped: " + ex.Message); }
    }
}
