using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// BOW v2 (laneBOW, STAGED DARK, UNPROVEN). The seams the combat code asks (like WeaponHooks / GearHooks) plus the mode logic: manual toggle, AUTO-SWITCH by range with hysteresis, guard/dodge/lock-on rules.
// Master off (bow.json enabled false, or no file) = every entry point returns neutral on its first line and SimTick returns at once: today's game bit for bit.
//
//  Mode      ON = the fighter's strikes (bow.json lanes[] move ids) become auto-aimed arrows, resolved by BowShot through the ordinary Duel.Resolve pipeline (guard side, i-frames, posture, hit reaction),
//            the two specials are the bow's (bow.json slots), WeaponHooks reports the bow stats, attack_clips.json rows with weapon "bow" apply, the bow glb is on the left hand and the sword is dark.
//  Seams     Duel.Resolve -> Handles / Release (the swing throws the arrow instead of striking); DuelFighter.SpecialId -> SpecialIdFor; WeaponHooks.Of -> StatsFor; AttackClipTable.Find -> WeaponKey;
//            GameHUD.DrawSpecials -> SpecialLabel; Duel.Update -> SimTick (one call per 60 Hz tick, after the fighters moved).
//  Switching never cancels anything: it happens only in FState.Loco with the guard not held (guard HELD while the bow is up drops to the sword at once).
public static class BowHooks
{
    public class State
    { public bool manualOff;
        public bool none, bow; public string charId = "";
        public int sinceSwitch = 9999, farTicks, lockout, manualHold, ammo, lastToggleFrame = -1;
        public DuelFighter foe; public BowCfg.Foe foeRow; public bool foeResolved; public WeaponSwap ws; public BowVisual vis; public bool init;
    }

    static readonly Dictionary<DuelFighter, State> states = new Dictionary<DuelFighter, State>();
    static readonly Dictionary<string, bool> bowByChar = new Dictionary<string, bool>();
    static int bowUp;                 // number of fighters in bow mode: 0 = every seam returns neutral without a dictionary lookup
    static WeaponStats stats;
    static KeyCode[] kToggle, kPad; static CameraRig rig;
    static List<DuelFighter> allFighters;   // K24: the list SimTick last saw (BowAim retarget)
    public static List<DuelFighter> Fighters { get { return allFighters; } }   // FREEAIM v1

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { states.Clear(); bowByChar.Clear(); bowUp = 0; stats = null; kToggle = kPad = null; rig = null; allFighters = null; BowWalk.Reset(); SceneManager.sceneLoaded -= OnScene; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { SceneManager.sceneLoaded -= OnScene; SceneManager.sceneLoaded += OnScene; }
    static void OnScene(Scene s, LoadSceneMode m) { states.Clear(); bowByChar.Clear(); bowUp = 0; rig = null; BowWalk.Reset(); }

    // ---------------- seams ----------------
    public static bool Active(DuelFighter f) { if (bowUp == 0 || f == null) return false; State st; return states.TryGetValue(f, out st) && st.bow; }

    /// <summary>Duel.Resolve: true when this swing is thrown as an arrow by Release (the shooter is in bow mode and the move is a lane move or a bow special).</summary>
    public static bool Handles(DuelFighter a, MoveDef m)
    {
        if (bowUp == 0 || a == null || m == null || !Active(a)) return false;
        return BowCfg.SpecialByMove(m.id) != null || BowCfg.LaneOf(m.id) != null;
    }

    /// <summary>Duel.Resolve, instead of the reach test: launch the arrow(s) at the foe. Returns true once launched (the swing is spent, no AttackMissed), false inside minGapM / out of arrows (the swing whiffs).</summary>
    public static bool Release(DuelFighter a, DuelFighter d, MoveDef m, float chargeMul)
    {
        var C = BowCfg.Get(); State st; if (!states.TryGetValue(a, out st) || !st.bow) return false;
        var sp = BowCfg.SpecialByMove(m.id); var lane = sp == null ? BowCfg.LaneOf(m.id) : null; if (sp == null && lane == null) return false;
        bool pointShot = false; Vector3 fp = Vector3.zero;
        if (BowFree.Wants(a)) { DuelFighter ft; if (BowFree.Resolve(a, out ft, out fp)) { if (ft != null) d = ft; else pointShot = true; } else if (d == a) return false; }   // FREEAIM v1: the camera ray picks the fighter it passes through (soft snap) or the point it hits; d == a = the no-foe caller's placeholder
        else { var pick = BowAim.Pick(a, d, allFighters); if (pick != null) d = pick; }   // K24: aim assist (identical to d unless aim.retarget finds a better foe in the cone; null = assist off)
        float gap; if (pointShot) { Vector3 gv = fp - a.transform.position; gv.y = 0f; gap = gv.magnitude; } else gap = Gap(a, d);
        if (gap < C.minGapM) return false;
        if (pointShot) BowAim.SnapPoint(a, fp); else BowAim.Snap(a, d);   // K24: the arrow leaves from the front
        int cost = sp != null ? Mathf.Max(1, sp.arrows) : 1;
        if (C.quiverOn) { if (st.ammo < cost) return false; st.ammo -= cost; }
        int[] lanes = sp != null && sp.lanes != null && sp.lanes.Length > 0 ? sp.lanes : new[] { lane != null ? lane.lane : 0 };
        float mul = sp != null ? 1f : C.sharePerMille / 1000f * (lane.multPerMille > 0 ? lane.multPerMille : 1000) / 1000f;   // a special's move row already carries the per-arrow damage
        int speed = sp != null && sp.speedCmS > 0 ? sp.speedCmS : C.speedCmS;
        float pinMul = st.foeRow != null ? st.foeRow.pinMul : 1f;
        int pin = sp != null ? Mathf.RoundToInt(sp.pinTicks * pinMul) : 0;
        string tint = sp != null && !string.IsNullOrEmpty(sp.tint) ? sp.tint : C.arrow.defaultTint; DM03.LogS("arrow.defaultTint", 1, C.arrow.defaultTint);   // DATALAYER 18 Nov: bow.json arrow.defaultTint (default #e8d9a8)
        if (BowFree.Wants(a)) { var FF = C.aim.free; if (FF.speedCmS > 0f) speed = Mathf.Max(speed, Mathf.RoundToInt(FF.speedCmS)); }   // FREEAIM v1: a long shot flies fast (data); the 7.8 m/s of V3 would take 6 s over 45 m
        BowShot.Launch(a, pointShot ? null : d, m, lanes, sp != null, mul * chargeMul, speed, pin, tint, pointShot, fp);
        if (BowCfg.Log) Debug.Log("[bow] " + a.name + " " + m.id + " -> " + (pointShot ? "point " + fp.ToString("F1") : d.name) + " gap " + gap.ToString("0.0") + " m, " + lanes.Length + " arrow(s), " + (C.quiverOn ? st.ammo + " left" : "quiver off"));
        return true;
    }

    /// <summary>DuelFighter.TickActive with no duel foe, once per swing: launch the arrow straight at the best un-engaged fighter in the aim cone. An arrow ignores melee reach, so this calls Release directly (the arrival is judged by Duel.Resolve as for any arrow). Logs why when nothing flew.</summary>
    public static bool NoFoeShot(DuelFighter a, MoveDef m)
    {
        if (!Handles(a, m)) return false;
        State st; states.TryGetValue(a, out st); bool free = BowFree.Wants(a); DuelFighter t = null; bool shot;   // FREEAIM v1: free aim fires along the camera ray (Release resolves it; a = placeholder foe)
        if (free) shot = Release(a, a, m, 1f); else { t = BowAim.Pick(a, null, allFighters); shot = t != null && !t.Dead && Release(a, t, m, 1f); }
        if (BowCfg.Log) Debug.Log("[bow] no-foe swing: target " + (free ? "free aim (camera ray)" : t != null ? t.name + " gap " + Gap(a, t).ToString("F1") + " m" : "none in the aim cone") + ", ammo " + (st != null ? st.ammo.ToString() : "?") + ", arrow launched " + shot);
        return shot;
    }

    /// <summary>DuelFighter.SpecialId: the bow's move id for this slot while the bow is up, else null (the caller keeps the roster special).</summary>
    public static string SpecialIdFor(DuelFighter f, int slot)
    {
        if (bowUp == 0 || !Active(f)) return null;
        var sp = BowCfg.SpecialBySlot(slot); return sp != null && !string.IsNullOrEmpty(sp.moveId) ? sp.moveId : null;
    }

    /// <summary>WeaponHooks.Of: the bow's weapon stats while the fighter's bow is up, else null (WeaponHooks keeps its own answer).</summary>
    public static WeaponStats StatsFor(Component fighter)
    {
        if (bowUp == 0 || fighter == null) return null;
        var df = fighter as DuelFighter; if (df == null) df = fighter.GetComponent<DuelFighter>();
        if (!Active(df)) return null;
        if (stats == null) { var g = BowCfg.Get().gearWeapon; stats = new WeaponStats { id = BowCfg.Get().weaponId, damageMul = g.damageMul, speedMul = g.speedMul, reachMul = g.reachMul, postureMul = g.postureMul, twoHanded = g.twoHanded }; }
        return stats;
    }

    /// <summary>AttackClipTable.Find: the weapon key rows are matched by ("bow" while this character's bow is up, else "" = keep the roster weapon).</summary>
    public static string WeaponKey(string characterId)
    {
        if (bowUp == 0 || string.IsNullOrEmpty(characterId)) return "";
        bool on; return bowByChar.TryGetValue(characterId, out on) && on ? BowCfg.Get().weaponId : "";
    }

    /// <summary>GameHUD.DrawSpecials: the special icon's label in bow mode (e.g. "Power Shot"), else the caller's own.</summary>
    public static string SpecialLabel(DuelFighter f, int slot, string fallback)
    {
        if (bowUp == 0 || !Active(f)) return fallback;
        var sp = BowCfg.SpecialBySlot(slot); return sp != null && !string.IsNullOrEmpty(sp.label) ? sp.label : fallback;
    }

    // HUD reads
    public static bool AnyBowUp { get { return bowUp > 0; } }
    public static bool TryHud(DuelFighter f, out bool bow, out int ammo, out DuelFighter foe)
    {
        bow = false; ammo = 0; foe = null; State st; if (f == null || !states.TryGetValue(f, out st) || st.none) return false;
        bow = st.bow; ammo = st.ammo; foe = st.foe; return true;
    }
    public static DuelFighter HudFighter()
    {
        foreach (var kv in states) if (kv.Key != null && !kv.Value.none && kv.Key.IsPlayer) return kv.Key;
        return null;
    }

    // ---------------- the 60 Hz tick (Duel.Update, after the fighters ticked and moved) ----------------
    public static void SimTick(List<DuelFighter> fighters)
    {
        if (!BowCfg.Master) return;
        allFighters = fighters;
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i]; if (f == null) continue;
            State st; if (!states.TryGetValue(f, out st)) { st = new State(); states[f] = st; }
            if (!st.init) { st.init = true; st.charId = Roster.IdOf(f.transform); st.none = !BowCfg.IsBowCharacter(st.charId); st.ammo = BowCfg.Get().ammo; }
            if (st.none) continue;
            Evaluate(f, st);
            if (st.bow) BowAim.Tick(f, st.foe, fighters);   // K24: turn to the foe while drawing / loosing (aim.enabled false = returns at once)
        }
        BowShot.Step(Duel.TickCount);
    }

    static float Gap(DuelFighter a, DuelFighter d) { Vector3 v = d.transform.position - a.transform.position; v.y = 0f; return v.magnitude; }

    static void Evaluate(DuelFighter f, State st)
    {
        var C = BowCfg.Get(); var A = C.auto;
        st.sinceSwitch++; if (st.lockout > 0) st.lockout--; if (st.manualHold > 0) st.manualHold--;
        if (st.foe == null || st.foe.Dead) { st.foe = f.opponent; st.foeResolved = false; }
        var foe = st.foe;
        if (foe != null && !st.foeResolved) { st.foeResolved = true; st.foeRow = BowCfg.FoeRow(Roster.IdOf(foe.transform)); }
        bool dead = f.Dead || foe == null || foe.Dead;
        bool free = f.state == FState.Loco && !f.guardHeld && !Duel.FrozenFor(f) && !MarketSafe.NoCombat;

        // manual toggle (player only; one toggle per rendered frame even when two sim ticks run in it)
        if (f.IsPlayer && st.lastToggleFrame != Time.frameCount && ToggleDown())
        {
            st.lastToggleFrame = Time.frameCount;
            if (st.bow) { SetMode(f, st, false, "manual"); st.manualHold = A.manualHoldTicks; st.manualOff = true; }
            else if (!dead && free && GearWeaponBlocks(f, st, A)) { }
            else if (!dead && free) { SetMode(f, st, true, "manual"); st.manualHold = A.manualHoldTicks; st.manualOff = false; }
            return;
        }

        if (st.bow)
        {
            if (dead && f.IsPlayer && !f.Dead && BowEquip.KeepNoFoe(f)) { if (f.guardHeld && C.guardExitsBow) { SetMode(f, st, false, "guard"); st.lockout = A.lockoutAfterGuardTicks; } return; }   // equipKeepNoFoe: no foe = no target, the bow stays in her hand
            if (dead) { SetMode(f, st, false, "no foe"); return; }
            if (f.guardHeld && C.guardExitsBow) { SetMode(f, st, false, "guard"); st.lockout = A.lockoutAfterGuardTicks; return; }   // the guard is a sword guard: instant, even mid-recovery of nothing (guardHeld only)
            if (BowCfg.AutoOn && st.manualHold <= 0 && free && (st.foeRow == null || st.foeRow.allow))
            {
                float exitM = A.rangeExitM + (st.foeRow != null ? st.foeRow.exitOffsetM : 0f);
                if (Gap(f, foe) <= exitM) SetMode(f, st, false, "range");
            }
            return;
        }

        // equipRearm (bow.json): the bow item is in the main hand and a foe is engaged: the bow mode comes (back) up; the manual toggle / guard lockout still win
        bool locoNow = f.state == FState.Loco && !f.guardHeld && !Duel.FrozenFor(f);
        if (f.IsPlayer && !f.Dead && !st.manualOff && (dead ? (locoNow && BowEquip.KeepNoFoe(f)) : free) && st.lockout <= 0 && st.manualHold <= 0 && BowEquip.WantsBow(f) && !(C.quiverOn && st.ammo <= 0)) { SetMode(f, st, true, "equip rearm"); return; }
        // melee -> bow, auto only
        if (!BowCfg.AutoOn || dead || !free || st.lockout > 0 || st.manualHold > 0 || st.sinceSwitch < A.minDwellTicks || (st.foeRow != null && !st.foeRow.allow) || GearWeaponBlocks(f, st, A) || (C.quiverOn && st.ammo <= 0) || (A.requireLockOn && !LockedOn(f))) { st.farTicks = 0; return; }
        float enterM = A.rangeEnterM + (st.foeRow != null ? st.foeRow.enterOffsetM : 0f);
        float maxM = A.maxRangeM > 0f ? A.maxRangeM : ControlsTable.Get().camera.exitRange;
        float g = Gap(f, foe);
        if (g >= enterM && g <= maxM) { if (++st.farTicks >= A.enterHoldTicks) SetMode(f, st, true, "range"); } else st.farTicks = 0;
    }

    static bool GearWeaponBlocks(DuelFighter f, State st, BowCfg.Auto A) { if (!A.blockWhenGearWeapon) return false; if (st.ws == null) st.ws = f.GetComponent<WeaponSwap>(); return st.ws != null && st.ws.VisualShown; }   // WeaponSwap is added on the first weapon equip, so it is looked up lazily

    static bool LockedOn(DuelFighter f)
    {
        if (rig == null) rig = Object.FindFirstObjectByType<CameraRig>();
        return rig != null && rig.lockEnabled && rig.dbgLockOn;
    }

    static bool ToggleDown()
    {
        var I = BowCfg.Get().input;
        if (kToggle == null) kToggle = Parse(I.toggleKeys); if (kPad == null) kPad = Parse(I.padToggleKeys);
        for (int i = 0; i < kToggle.Length; i++) if (Input.GetKeyDown(kToggle[i])) return true;
        for (int i = 0; i < kPad.Length; i++) if (Input.GetKeyDown(kPad[i])) return true;
        return false;
    }
    static KeyCode[] Parse(string[] names)
    {
        var l = new List<KeyCode>(); if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (System.Enum.TryParse(names[i], true, out k)) l.Add(k); }
        return l.ToArray();
    }

    /// <summary>K3b (BowEquip.cs): equip / unequip of the bow item sets the mode directly. Default path: bow.json equipItemId "" = never called.</summary>
    public static void SetFor(DuelFighter f, bool on, string why)
    {
        if (f == null || !BowCfg.Master) return; State st; if (!states.TryGetValue(f, out st)) { st = new State(); states[f] = st; }
        if (!st.init) { st.init = true; st.charId = Roster.IdOf(f.transform); st.none = !BowCfg.IsBowCharacter(st.charId); st.ammo = BowCfg.Get().ammo; }
        if (st.none) return; if (on) st.manualOff = false; SetMode(f, st, on, why);
    }

    /// <summary>K10 (AmmoRefill): add arrows to the fighter's quiver, capped at bow.json ammo. False when no bow state / quiver off / already full.</summary>
    public static bool AddAmmo(DuelFighter f, int n, out int added)
    {
        added = 0; var C = BowCfg.Get(); State st; if (f == null || !BowCfg.Master || !C.quiverOn || n < 1) return false;
        if (!states.TryGetValue(f, out st)) { st = new State(); states[f] = st; }
        if (!st.init) { st.init = true; st.charId = Roster.IdOf(f.transform); st.none = !BowCfg.IsBowCharacter(st.charId); st.ammo = C.ammo; }
        if (st.none || st.ammo >= C.ammo) return false;
        added = Mathf.Min(n, C.ammo - st.ammo); st.ammo += added; return true;
    }

    static void SetMode(DuelFighter f, State st, bool on, string why)
    {
        if (st.bow == on) return;
        st.bow = on; st.sinceSwitch = 0; st.farTicks = 0; bowUp += on ? 1 : -1; if (bowUp < 0) bowUp = 0;
        if (!string.IsNullOrEmpty(st.charId)) bowByChar[st.charId] = on;
        if (st.vis == null) st.vis = f.GetComponent<BowVisual>() ?? f.gameObject.AddComponent<BowVisual>();
        st.vis.Show(on);
        if (BowCfg.Log) Debug.Log("[bow] " + f.name + (on ? " BOW up" : " SWORD back") + " (" + why + ") tick " + Duel.TickCount);
    }
}
