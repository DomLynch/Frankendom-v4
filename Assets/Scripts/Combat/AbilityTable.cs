using System;
using System.Collections.Generic;
using UnityEngine;

// ABILITIES v1 (laneABILITIES, STAGED, UNPROVEN): abilities as data rows. Data: Assets/Data/abilities.json (twin in StreamingAssets). Pattern of ShoveTable / GearTable.
// MASTER `enabled` false, no file, or an empty abilities[] = today's fight bit for bit: For() returns null, ExtraMove() returns null, EvadeWindow() returns 0, CostOf() / ClipOf() hand back the caller's own value,
// and every DuelFighter / HUD hook below falls to the old roster path. Nothing here names a character or a weapon: characters[] and weapons[] are row data (Joan only: no abilities on monsters).
// Slots: 0, 1 = the weapon specials (keys 1 / 2), 2 = shield bash (C), 3 = riposte / dodge strike (B; the row with trigger "window" is the HUD face, the row with trigger "evade" is the dodge strike).
// Hot reload: data_watcher.json row { "file": "abilities.json", "calls": [ { "type": "AbilityTable", "method": "Reload" } ] }.
public static class AbilityTable
{
    [Serializable] public class VsGuard { public float stagger, staminaDamage; }
    [Serializable]
    public class MoveJ   // inline move block: merged as a MoveDef when the row's moveId is not in Moves.asset / move-defs.json (MoveTable.Get falls back to ExtraMove)
    {
        public string direction = "";
        public int windup, active, recovery;
        public float damage, stamina, staminaDamage, stagger, poise, poiseFrom, chip, knockback, stepIn, reach, posture, arcDeg;
        public bool breaksGuard, parryable = true, charges;
        public VsGuard vsGuard = new VsGuard();
    }
    [Serializable]
    public class Row
    {
        public string id = "", name = "", moveId = "", glyph = "slash", key = "", clip = "", trigger = "press";   // trigger: press | window | evade
        public int slot;
        public string[] weapons = new string[0];      // WeaponStats.id; "" = default sword (no weapon item), "*" = any weapon
        public string[] characters = new string[0];   // roster ids; empty = nobody
        public float cooldownS, staminaCost;
        public bool exactToday;                       // true: the move's own stamina path stays (today's free special)
        public MoveJ move = new MoveJ();
        [NonSerialized] public MoveDef def;           // built from move{} on load (null when the row has no inline move)
    }
    [Serializable] public class Evade { public int roll = 80, sidestep = 60, backstep = 45; public string moveId = "skill_dodgestrike"; }
    [Serializable]
    public class Data
    {
        public bool enabled, debugLog;                // shipped / missing = off
        public string fallbackWeapon = "knight_sword";   // a weapon id no row lists shows this weapon's bar
        public int initialCooldownTicks = 120, swapLockTicks = 120;   // initialCooldownTicks: documentation of today's DuelFighter field initialiser (120), not read; swapLockTicks: a weapon change puts slots 0 / 1 on at least this cooldown
        public Evade evade = new Evade();
        public Row[] abilities = new Row[0];
    }

    // per fighter: the resolved bar for the weapon it holds right now (rebuilt when the WeaponStats reference or the data generation changes)
    class FCache { public WeaponStats ws; public string wid = ""; public int gen = -1; public Row[] rows = new Row[4]; public Row evade; }

    static Data data; static int gen;
    static readonly Dictionary<DuelFighter, FCache> caches = new Dictionary<DuelFighter, FCache>();
    static readonly Dictionary<string, MoveDef> extra = new Dictionary<string, MoveDef>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { data = null; gen++; caches.Clear(); extra.Clear(); }

    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }   // name starts with Reload: data_watcher.json calls it
    public static bool Off { get { var d = Get(); return !d.enabled || d.abilities.Length == 0; } }

    static void Load()
    {
        data = new Data(); gen++; caches.Clear(); extra.Clear();
        string json = DataPath.ReadText("abilities.json");
        if (string.IsNullOrEmpty(json)) return;   // no file = today
        try
        {
            var d = JsonUtility.FromJson<Data>(json.TrimStart('﻿'));
            if (d == null) return;
            if (d.abilities == null) d.abilities = new Row[0];
            if (d.evade == null) d.evade = new Evade();
            for (int i = 0; i < d.abilities.Length; i++)
            {
                var r = d.abilities[i]; if (r == null) continue;
                if (r.weapons == null) r.weapons = new string[0];
                if (r.characters == null) r.characters = new string[0];
                if (r.name == null) r.name = ""; if (r.key == null) r.key = ""; if (r.glyph == null) r.glyph = "slash"; if (r.trigger == null) r.trigger = "press";
                var m = r.move;
                if (m != null && !string.IsNullOrEmpty(r.moveId) && (m.windup > 0 || m.active > 0))   // an absent / null move block reads as zeros: not a full row, never added
                {
                    var md = new MoveDef
                    {
                        id = r.moveId, direction = m.direction, windup = m.windup, active = m.active, recovery = m.recovery,
                        damage = m.damage, stamina = m.stamina, staminaDamage = m.staminaDamage, stagger = m.stagger, poise = m.poise, poiseFrom = m.poiseFrom,
                        chip = m.chip, knockback = m.knockback, stepIn = m.stepIn, reach = m.reach, posture = m.posture, arcDeg = m.arcDeg,
                        breaksGuard = m.breaksGuard, parryable = m.parryable, charges = m.charges
                    };
                    if (m.vsGuard != null && m.vsGuard.stagger > 0f) { md.hasVsGuard = true; md.vsGuardStagger = m.vsGuard.stagger; md.vsGuardStaminaDamage = m.vsGuard.staminaDamage; }
                    r.def = md; if (!extra.ContainsKey(md.id)) extra[md.id] = md;
                }
            }
            data = d; gen++;
            if (d.debugLog) Debug.Log("[ability] loaded " + d.abilities.Length + " rows, enabled " + d.enabled + ", " + extra.Count + " inline moves");
        }
        catch (Exception ex) { Debug.LogWarning("[ability] abilities.json parse failed, abilities stay off: " + ex.Message); data = new Data(); extra.Clear(); gen++; }
    }

    // MoveTable.Get fallback: the inline move of an ability row whose id is in neither Moves.asset nor move-defs.json (null when off or unknown). Works across a hot reload (the table's own map is built once).
    public static MoveDef ExtraMove(string id)
    {
        if (string.IsNullOrEmpty(id) || Off) return null;
        MoveDef m; return extra.TryGetValue(id, out m) ? m : null;
    }

    static bool Has(string[] a, string v) { for (int i = 0; i < a.Length; i++) if (a[i] == v) return true; return false; }
    static int Score(Row r, string character, string wid)   // 2 exact weapon, 1 "*", 0 no match
    {
        if (r == null || !Has(r.characters, character)) return 0;
        if (Has(r.weapons, wid)) return 2;
        return Has(r.weapons, "*") ? 1 : 0;
    }
    static Row Pick(Row[] all, int slot, bool evade, string character, string wid)
    {
        Row best = null; int bs = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var r = all[i]; if (r == null || r.slot != slot || (r.trigger == "evade") != evade) continue;
            int s = Score(r, character, wid); if (s > bs) { bs = s; best = r; }
        }
        return best;
    }

    static FCache Cache(DuelFighter f)
    {
        var ws = WeaponHooks.Of(f); string wid = ws != null && ws.id != null ? ws.id : "";
        FCache c; caches.TryGetValue(f, out c);
        if (c != null && c.gen == gen && ReferenceEquals(c.ws, ws)) return c;
        if (caches.Count > 64) { var dead = new List<DuelFighter>(); foreach (var kv in caches) if (kv.Key == null) dead.Add(kv.Key); for (int i = 0; i < dead.Count; i++) caches.Remove(dead[i]); }   // destroyed fighters (a Unity null is still a valid key object here)
        bool changed = c != null && c.gen == gen && c.wid != wid;   // a weapon change, not a data reload
        var n = new FCache { ws = ws, wid = wid, gen = gen };
        var d = data; string who = f.CharacterId;
        for (int s = 0; s < 4; s++)
        {
            if (s < 2 && wid == "bow") continue;   // the bow keeps its own specials (BowHooks.SpecialIdFor): no row while it is up
            var r = Pick(d.abilities, s, false, who, wid);
            if (r == null && s < 2) r = Pick(d.abilities, s, false, who, d.fallbackWeapon);   // an unlisted weapon shows the fallback bar
            n.rows[s] = r;
        }
        n.evade = Pick(d.abilities, 3, true, who, wid);
        caches[f] = n;
        if (changed && c.wid != "bow" && wid != "bow" && d.swapLockTicks > 0 && (n.rows[0] != null || n.rows[1] != null))   // swapping is not a free cooldown reset
        {
            f.special1Cd = Mathf.Max(f.special1Cd, d.swapLockTicks); f.special2Cd = Mathf.Max(f.special2Cd, d.swapLockTicks);
            if (d.debugLog) Debug.Log("[ability] " + f.name + " weapon '" + c.wid + "' -> '" + wid + "': slots 1/2 locked " + d.swapLockTicks + " ticks");
        }
        return n;
    }

    /// <summary>The ability row on slot 0..3 for this fighter's current weapon (null = off, not a character with a row, or the bow is up on a weapon slot).</summary>
    public static Row For(DuelFighter f, int slot)
    {
        if (f == null || slot < 0 || slot > 3 || Off) return null;
        return Cache(f).rows[slot];
    }

    /// <summary>Highest bar slot with a row + 1 (0 = no bar: the HUD keeps its own two icons).</summary>
    public static int SlotCount(DuelFighter f)
    {
        if (f == null || Off) return 0;
        var c = Cache(f); for (int s = 3; s >= 0; s--) if (c.rows[s] != null) return s + 1;
        return 0;
    }

    /// <summary>The row this fighter casts for move id (window rows never match: riposte keeps its own cost / clip). The evade row counts as slot 3. null = not an ability for this fighter.</summary>
    public static Row RowOfMove(DuelFighter f, string moveId)
    {
        if (f == null || string.IsNullOrEmpty(moveId) || Off) return null;
        var c = Cache(f);
        for (int s = 0; s < 4; s++) { var r = c.rows[s]; if (r != null && r.trigger != "window" && r.moveId == moveId) return r; }
        return c.evade != null && c.evade.moveId == moveId ? c.evade : null;
    }
    public static int SlotOfMove(DuelFighter f, string moveId) { var r = RowOfMove(f, moveId); return r != null ? r.slot : -1; }

    public static int CooldownTicks(Row r, int fallback) { return r != null ? Mathf.Max(0, Mathf.RoundToInt(r.cooldownS * 60f)) : fallback; }

    /// <summary>Stamina the ability charges (before the stance multiplier). fallback for a move that is not an ability or an exactToday row (today's cost path).</summary>
    public static float CostOf(DuelFighter f, string moveId, float fallback)
    {
        var r = RowOfMove(f, moveId); return r == null || r.exactToday ? fallback : r.staminaCost;
    }

    /// <summary>Animator state of the ability's move (null = none: ClipFor decides).</summary>
    public static string ClipOf(DuelFighter f, string moveId)
    {
        var r = RowOfMove(f, moveId); return r != null && !string.IsNullOrEmpty(r.clip) ? r.clip : null;
    }

    /// <summary>Move id of the dodge strike (the trigger "evade" row), null when off / no row.</summary>
    public static string EvadeMove(DuelFighter f)
    {
        if (f == null || Off) return null;
        var e = Cache(f).evade; return e != null && !string.IsNullOrEmpty(e.moveId) ? e.moveId : null;
    }

    /// <summary>Ticks the dodge strike stays available after an i-frame swallowed a swing (dodgeKind 0 roll, 1 backstep, 2 sidestep). 0 = off / no evade row.</summary>
    public static int EvadeWindow(DuelFighter f, int dodgeKind)
    {
        if (f == null || Off || EvadeMove(f) == null) return 0;
        var e = data.evade; return dodgeKind == 0 ? e.roll : dodgeKind == 1 ? e.backstep : e.sidestep;
    }
}
