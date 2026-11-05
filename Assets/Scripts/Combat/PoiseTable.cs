using System;
using System.IO;
using UnityEngine;

// POISE / HYPER-ARMOUR data layer (lane V64P). Every number lives in Assets/Data/poise.json (copy in StreamingAssets for the player).
// Lookup order (same as FlowTable): Resources/poise, Editor Data/poise.json, StreamingAssets/poise.json, Application.dataPath/Data/poise.json.
// A missing / unparsable file or "enabled": false = the exact old behaviour (Evaluate returns a neutral verdict).
//  1. HYPER-ARMOUR (armour.fighters[].moves[]): while a fighter is inside the armoured window of a move (state Windup from fromTick, all of Active, optionally the first recoveryTicks of
//     Recovery) a clean hit still damages (x damageMul) but does not stagger / knock down / posture-break him and the swing goes on. First matching row wins; "armour": false excludes a move.
//  2. POISE METER (meter): every clean, interrupting hit on a fighter is counted; hits older than windowTicks are forgotten. After `hits` of them the NEXT hit is absorbed: it damages
//     (x absorbDamageMul) but does not stagger, a current stagger is cut short (break-out) and a grace window of breakOutTicks starts in which hits damage (x graceDamageMul) but never
//     interrupt. Anti stun-lock for both fighters. Per fighter overrides in meter.fighters[].
//  3. RIPOSTE damage multiplier (riposte.damageMul on riposte.moves[] ids), applied in Duel.Resolve to the clean-hit damage.
// Hooks: Duel.Resolve (apply_v64p.py) -> PoiseTable.Evaluate; DuelFighter.Tick -> PoiseTick (DuelFighterPoise.cs).
public static class PoiseTable
{
    [Serializable] public class ArmourRow { public string move = "*"; public bool armour = true; public int fromTick = 1, recoveryTicks = 0; public float damageMul = 1f, knockMul = 0.25f, postureMul = 0.5f; }
    [Serializable] public class ArmourFighter { public string key = ""; public ArmourRow[] moves = new ArmourRow[0]; }
    [Serializable] public class ArmourCfg { public bool enabled = true; public ArmourFighter[] fighters = new ArmourFighter[0]; }

    [Serializable] public class MeterFighter { public string key = ""; public bool enabled = true; public int hits = -1, windowTicks = -1, breakOutTicks = -1; public float absorbDamageMul = -1f, graceDamageMul = -1f; }
    [Serializable]
    public class MeterCfg
    {
        public bool enabled = true, countArmoured = false; public int hits = 3, windowTicks = 150, breakOutTicks = 45; public float absorbDamageMul = 0.75f, graceDamageMul = 0.6f;
        public MeterFighter[] fighters = new MeterFighter[0];
    }
    [Serializable] public class RiposteCfg { public float damageMul = 0.8f; public string[] moves = { "riposte", "slash_riposte", "heavy_riposte" }; }

    [Serializable]
    public class Data
    {
        public bool enabled = true, debugLog;
        public ArmourCfg armour = new ArmourCfg(); public MeterCfg meter = new MeterCfg(); public RiposteCfg riposte = new RiposteCfg();
    }

    // what Duel.Resolve needs for one clean hit
    public struct Verdict
    {
        public bool armoured, absorb, grace; public float damageMul, knockMul, postureMul; public int breakOutTicks;
        // posture the victim gets from this hit: an armoured fighter can never be posture-broken (his posture stops just below max), others unchanged
        public float ClampPosture(DuelFighter d, float gain)
        {
            if (!armoured) return gain;
            float g = gain * postureMul, room = d.PostureMax - 0.5f - d.posture;
            return Mathf.Max(0f, Mathf.Min(g, room));
        }
    }

    static Data data; static bool loaded;
    public static Data Get() { if (!loaded) Load(); return data; }
    public static void Reload() { loaded = false; Load(); }

    static bool MatchMove(string pat, string id)
    {
        if (string.IsNullOrEmpty(pat) || pat == "*" || pat == id) return true;
        return pat.EndsWith("*", StringComparison.Ordinal) && id.StartsWith(pat.Substring(0, pat.Length - 1), StringComparison.Ordinal);
    }
    public static bool IsRiposte(MoveDef m)
    {
        var r = Get().riposte; if (m == null || r.moves == null) return false;
        for (int i = 0; i < r.moves.Length; i++) if (MatchMove(r.moves[i], m.id)) return true;
        return false;
    }

    // the armour row that protects fighter d right now (null = none)
    public static ArmourRow ArmourNow(DuelFighter d)
    {
        var C = Get().armour; if (!C.enabled || d == null || d.CurrentMove == null) return null;
        ArmourFighter af = null; string key = d.PostureKey;
        af = RosterResolver.ByIdOrDefault(C.fighters, rrx => rrx.key, key);   // HCKIT2 v93
        if (af == null || af.moves == null) return null;
        string id = d.CurrentMove.id; FState s = d.state;
        for (int i = 0; i < af.moves.Length; i++)
        {
            var r = af.moves[i]; if (r == null || !MatchMove(r.move, id)) continue;
            if (!r.armour) return null;
            bool inside = s == FState.Active || (s == FState.Windup && d.t >= r.fromTick) || (s == FState.Recovery && d.t <= r.recoveryTicks);
            return inside ? r : null;
        }
        return null;
    }

    struct MeterNow { public bool on; public int hits, window, breakOut; public float absorb, grace; }
    static MeterNow MeterFor(DuelFighter d)
    {
        var M = Get().meter; var n = new MeterNow { on = M.enabled, hits = M.hits, window = M.windowTicks, breakOut = M.breakOutTicks, absorb = M.absorbDamageMul, grace = M.graceDamageMul };
        string key = d.PostureKey;
        if (M.fighters != null) for (int i = 0; i < M.fighters.Length; i++)
        {
            var f = M.fighters[i]; if (f == null || f.key != key) continue;
            if (!f.enabled) n.on = false;
            if (f.hits >= 0) n.hits = f.hits; if (f.windowTicks >= 0) n.window = f.windowTicks; if (f.breakOutTicks >= 0) n.breakOut = f.breakOutTicks;
            if (f.absorbDamageMul >= 0f) n.absorb = f.absorbDamageMul; if (f.graceDamageMul >= 0f) n.grace = f.graceDamageMul;
            break;
        }
        n.hits += GearHooks.PoiseHitsBonus(d);   // kitGEAR v79: poise stat = extra meter hits
        return n;
    }

    // Duel.Resolve, clean-hit branch, before the damage is applied. Mutates the victim's poise counters (a hit is counted exactly once).
    public static Verdict Evaluate(DuelFighter a, DuelFighter d, MoveDef m, bool deathblow)
    {
        var v = new Verdict { damageMul = 1f, knockMul = 1f, postureMul = 1f };
        var D = Get(); if (!D.enabled || d == null) return v;
        if (D.riposte != null && D.riposte.damageMul > 0f && Mathf.Abs(D.riposte.damageMul - 1f) > 1e-4f && IsRiposte(m)) v.damageMul *= D.riposte.damageMul;
        if (deathblow || d.PostureBroken) return v;   // a broken fighter is meant to take the hit (deathblow window)
        int tick = Duel.TickCount;
        var M = MeterFor(d);
        var ar = ArmourNow(d);
        if (ar != null)
        {
            v.armoured = true; v.damageMul *= ar.damageMul; v.knockMul = ar.knockMul; v.postureMul = ar.postureMul; d.statArmourHits++;
            if (M.on && D.meter.countArmoured) d.PoiseCount(tick, M.window);
            if (D.debugLog) Debug.Log("[Poise] " + d.name + " ARMOUR absorbs " + m.id + " during " + d.CurrentMove.id + " (" + d.state + " t=" + d.t + ") tick=" + tick);
            return v;
        }
        if (!M.on) return v;
        if (d.PoiseGrace > 0)
        {
            v.grace = true; v.damageMul *= M.grace; v.knockMul = 0f; v.postureMul = 1f;
            if (D.debugLog) Debug.Log("[Poise] " + d.name + " GRACE hit tick=" + tick + " left=" + d.PoiseGrace);
            return v;
        }
        if (d.PoiseCountNow(tick, M.window) >= M.hits)
        {
            v.absorb = true; v.damageMul *= M.absorb; v.knockMul = 0f; v.breakOutTicks = Mathf.Max(1, M.breakOut);
            d.statPoiseAbsorbs++; d.PoiseReset();
            if (D.debugLog) Debug.Log("[Poise] " + d.name + " ABSORB (after " + M.hits + " hits) tick=" + tick + " break-out " + v.breakOutTicks);
            return v;
        }
        d.PoiseCount(tick, M.window);
        return v;
    }

    // ---------------- load ----------------
    static void Load()
    {
        loaded = true; data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("poise");
            if (ta != null) json = ta.text;
#if UNITY_EDITOR
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/poise.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "poise.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/poise.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("PoiseTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { data.enabled = false; return; }   // no file: old behaviour
        try
        {
            var d = JsonUtility.FromJson<Data>(json.TrimStart('﻿'));
            if (d == null) { data.enabled = false; return; }
            if (d.armour == null) d.armour = new ArmourCfg(); if (d.armour.fighters == null) d.armour.fighters = new ArmourFighter[0];
            if (d.meter == null) d.meter = new MeterCfg(); if (d.meter.fighters == null) d.meter.fighters = new MeterFighter[0];
            if (d.riposte == null) d.riposte = new RiposteCfg();
            data = d;
        }
        catch (Exception ex) { Debug.LogError("PoiseTable: parse failed: " + ex.Message); data = new Data { enabled = false }; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded = false; data = null; }
}
