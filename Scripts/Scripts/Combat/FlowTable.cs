using System;
using System.IO;
using UnityEngine;

// Player MOVESET FLOW data layer (lane V64FLOW). Every number lives in Assets/Data/flow.json (copy in StreamingAssets for the player).
// Lookup order (same as CombatFeel): Resources/flow, Editor Data/flow.json, StreamingAssets/flow.json, Application.dataPath/Data/flow.json.
// A missing / unparsable file or "enabled": false leaves DuelFighter and PlayerBrain on the exact old behaviour. scope "player" = only a fighter whose PlayerBrain is ENABLED
// (a human); the AI, the playerBot player of BalanceAuto (PlayerBrain off) and the MoveSheet / PostureProof harnesses are untouched. "all" = every fighter.
// Sections: chain (attack-to-attack cancel windows), lock (target lock-on toggle + HUD marker), attack (magnetism snap + limited turn speed), dodge (directional dodge overrides), riposte.
public static class FlowTable
{
    // ---------------- chain: attack-to-attack ----------------
    // from: move id | "*" | prefix pattern "light*"; to: light | heavy | thrust | kick. earliest/latest = Recovery tick t (1 = first tick after the active window), latest <= 0 = until the recovery ends.
    // First matching row (file order) wins. onHitOnly = only when the swing connected (hit, block or parry). chained = use the move's chained (shorter wind-up) timing where the move has one.
    [Serializable] public class ChainRow { public string from = "*", to = "light"; public int earliest = 1, latest = 0; public bool onHitOnly, chained = true; }
    [Serializable] public class Combo { public string name = ""; public string[] steps = new string[0]; }
    [Serializable] public class ChainCfg { public bool replaceBuiltin = true; public int maxLinks = 5; public ChainRow[] rows = new ChainRow[0]; public Combo[] combos = new Combo[0]; }

    // ---------------- lock-on ----------------
    [Serializable]
    public class LockHud
    {
        public bool enabled = true, dot = true, toast = true; public float dotPx = 7f, yOffset = 1.15f, toastSeconds = 0.9f, toastY = 0.12f; public int fontSize = 11, toastFontSize = 15;
        public string text = "LOCK", onText = "LOCK ON", offText = "LOCK OFF"; public float[] color = { 0.96f, 0.93f, 0.85f, 0.92f };
    }
    [Serializable] public class LockCfg { public bool enabled = true, startLocked = true, pushAwayBreaks = false, attackRelocks = false, guardFacesTarget = true; public float range = 14f; public float breakAngleDeg = 120f, breakSeconds = 0.25f; public string[] keys = { "Tab", "Mouse2", "JoystickButton9" }; public LockHud hud = new LockHud(); }   // HCKIT4 v95

    // ---------------- attack magnetism + turn tracking ----------------
    [Serializable] public class MagnetCfg { public bool enabled = true; public float maxAngleDeg = 50f, maxDistance = 3.2f, snapMs = 90f, botMaxDistance = 1.5f; }   // V64P: botMaxDistance = magnetism range cap for a fighter that is not the human (AI / bot), 0 = no cap
    [Serializable] public class TurnCfg { public bool enabled = true; public float windupDegPerSec = 480f, activeDegPerSec = 80f, recoveryDegPerSec = 200f; }
    [Serializable] public class MoveRow { public string move = "*"; public float maxAngleDeg = -1f, maxDistance = -1f, snapMs = -1f, windupDegPerSec = -1f, activeDegPerSec = -1f, recoveryDegPerSec = -1f; }   // -1 = inherit
    [Serializable] public class AttackCfg { public MagnetCfg magnet = new MagnetCfg(); public TurnCfg turn = new TurnCfg(); public MoveRow[] moves = new MoveRow[0]; }

    // ---------------- directional dodge ----------------
    // dir: forward | back | left | right (relative to the locked target) | free (lock off). -1 = keep the dodge.json value of the kind that plays.
    [Serializable] public class DodgeRow { public string dir = ""; public int iframeStart = -1, iframeEnd = -1; public float distance = -1f, stamina = -1f; }
    [Serializable] public class DodgeCfg { public bool enabled = true, snapToAxes = true; public DodgeRow[] dirs = new DodgeRow[0]; }

    // ---------------- riposte ----------------
    [Serializable] public class RiposteCfg { public bool enabled = true, auto = true, consume = false, hint = true; public string[] keys = { "B", "JoystickButton1" }; public string move = "riposte", hintText = "RIPOSTE"; public int fontSize = 16; }

    // ---------------- bot: the flow layer on the BalanceAuto bot (lane V64P, FlowBot.cs) ----------------
    // The key presses a human makes, as data: chain a follow-up inside the flow.json chain rows, cancel a recovery into a dodge / guard when a swing comes, take the riposte after a parry, label dodges
    // with a direction. wX = relative weight of the follow-up type. All chances per decision; reaction ticks are sim ticks (60 = 1 s).
    [Serializable]
    public class BotCfg
    {
        public bool chain = true, cancel = true, riposte = true, dodgeDir = true;
        public float chainOnHit = 0.6f, chainOnWhiff = 0.15f, chainDecay = 0.75f, minStamina = 30f, reachMargin = 0.25f;
        public int maxLinks = 4, jitterTicks = 3;
        public float wLight = 1f, wHeavy = 0.35f, wThrust = 0.6f, wKick = 0.25f;
        public float cancelChance = 0.3f, cancelGuardShare = 0.3f, cancelMinStamina = 12f; public int cancelLeadMin = 3, cancelLeadMax = 10, guardHoldTicks = 14;
        public float riposteChance = 0.85f; public int riposteReactMin = 8, riposteReactMax = 22;
        public float dirLeft = 0.35f, dirRight = 0.35f, dirBack = 0.2f, dirForward = 0.1f;
        // V65BOT: chainReserve = stamina that must be left after the follow-up (its cost comes from stamina.json); stepInShare = share of the follow-up's stepIn counted as extra reach;
        // chainDropsDodge = a plain dodge the AI left queued does not block a chain the bot chose; cancelFoeHeavy = id prefixes of foe swings that get the heavy cancel rule (dodge, cancelHeavyChance,
        // cancelHeavyDist metres, lead window cancelHeavyLeadMin..Max ticks of the foe wind-up left); riposteSlack = metres beyond reach the riposte key is still pressed (the riposte steps in)
        public float chainReserve = 6f, stepInShare = 0.7f, cancelHeavyChance = 0.85f, cancelHeavyDist = 2.5f, riposteSlack = 0.6f; public bool chainDropsDodge = true; public int cancelHeavyLeadMin = 3, cancelHeavyLeadMax = 11;
        public string[] cancelFoeHeavy = { "heavy", "critical", "skill_" };
        // V65BOT2: active parry (the AI profile holds the guard, so its parry window is never fresh): on a parryable foe wind-up with parryLead* ticks left the bot re-raises the guard and holds it parryHoldTicks
        public bool parry = true; public float parryTryChance = 0.6f; public int parryLeadMin = 4, parryLeadMax = 8, parryHoldTicks = 12;
        // BALANCE2 v96: the bot uses the whole moveset. parrySideRead / cancelSideRead = chance the deliberate parry / guard-cancel aims the side that covers the swing (-1 = controls.json bot.sideReadChance, unchanged);
        // armourTrade* = on a foe wind-up with armourLeadMin..Max ticks left, start armourMove (an armoured move in poise.json for this fighter) with chance armourTradeChance, so the blow is taken through the armour
        public float parrySideRead = -1f, cancelSideRead = -1f, armourTradeChance = 0f; public int armourLeadMin = 11, armourLeadMax = 22; public string armourMove = "heavy_overhead";   // BALANCE2 v96
    }
    // the first chain row for from -> to, whatever the recovery tick (FlowBot picks a follow-up type and schedules the press from the row's window)
    public static ChainRow ChainRowFor(string fromId, string toType, bool hit)
    {
        var rows = Get().chain.rows;
        for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r == null || r.to != toType || !MatchMove(r.from, fromId)) continue; if (r.onHitOnly && !hit) continue; return r; }
        return null;
    }


    [Serializable]
    public class Data
    {
        public bool enabled = true, debugLog, applyToBot = true; public string scope = "player"; public BotCfg bot = new BotCfg();   // V64P: applyToBot = the BalanceAuto bot (flowBotOn) gets the flow layer
        public ChainCfg chain = new ChainCfg(); public LockCfg lockOn = new LockCfg(); public AttackCfg attack = new AttackCfg(); public DodgeCfg dodge = new DodgeCfg(); public RiposteCfg riposte = new RiposteCfg();
    }

    static Data data; static bool loaded;
    public static bool ForceApply;   // proof harness: treat the (PlayerBrain-less) scripted player as the human
    public static bool ForceHud;
    public static Data Get() { if (!loaded) Load(); return data; }
    public static void Reload() { loaded = false; Load(); }

    public static bool Applies(DuelFighter f)
    {
        var d = Get();
        return d.enabled && f != null && (d.scope == "all" || f.FlowHuman || (d.applyToBot && f.flowBotOn));   // V64P
    }

    // ---------------- lookups ----------------
    static bool MatchMove(string pat, string id)
    {
        if (string.IsNullOrEmpty(pat) || pat == "*" || pat == id) return true;
        return pat.EndsWith("*", StringComparison.Ordinal) && id.StartsWith(pat.Substring(0, pat.Length - 1), StringComparison.Ordinal);
    }

    public static ChainRow FindChain(string fromId, string toType, int t, bool hit)
    {
        var rows = Get().chain.rows;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; if (r == null || r.to != toType || !MatchMove(r.from, fromId)) continue;
            if (t < r.earliest || (r.latest > 0 && t > r.latest)) continue;
            if (r.onHitOnly && !hit) continue;
            return r;
        }
        return null;
    }

    public struct AtkParams { public bool magnet; public float maxAngle, maxDist, snapMs, windup, active, recovery; }
    public static AtkParams Attack(string moveId, DuelFighter who = null)   // V64P: who != null and not the human -> magnetism range capped at botMaxDistance
    {
        var p = AttackBase(moveId);
        if (who != null && !who.FlowHuman) { float cap = Get().attack.magnet.botMaxDistance; if (cap > 0f && p.maxDist > cap) p.maxDist = cap; }
        return p;
    }
    static AtkParams AttackBase(string moveId)
    {
        var a = Get().attack;
        var p = new AtkParams { magnet = a.magnet.enabled, maxAngle = a.magnet.maxAngleDeg, maxDist = a.magnet.maxDistance, snapMs = a.magnet.snapMs, windup = a.turn.windupDegPerSec, active = a.turn.activeDegPerSec, recovery = a.turn.recoveryDegPerSec };
        var rows = a.moves;
        if (rows != null) for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; if (r == null || !MatchMove(r.move, moveId)) continue;
            if (r.maxAngleDeg >= 0f) p.maxAngle = r.maxAngleDeg; if (r.maxDistance >= 0f) p.maxDist = r.maxDistance; if (r.snapMs >= 0f) p.snapMs = r.snapMs;
            if (r.windupDegPerSec >= 0f) p.windup = r.windupDegPerSec; if (r.activeDegPerSec >= 0f) p.active = r.activeDegPerSec; if (r.recoveryDegPerSec >= 0f) p.recovery = r.recoveryDegPerSec;
            break;
        }
        return p;
    }

    public static DodgeRow DodgeDir(string dir)
    {
        var c = Get().dodge; if (!c.enabled || c.dirs == null || string.IsNullOrEmpty(dir)) return null;
        for (int i = 0; i < c.dirs.Length; i++) if (c.dirs[i] != null && c.dirs[i].dir == dir) return c.dirs[i];
        return null;
    }
    // direction name PlayerBrain passes to DuelFighter.SetDodgeDir: relative to the locked target, "free" when the lock is off, "" for a no-input dodge (dodge.json decides)
    public static string DirName(bool locked, string kind, int side)
    {
        if (!locked) return kind == "none" ? "" : "free";
        if (kind == "lateral") return side < 0 ? "left" : "right";
        if (kind == "forward") return "forward";
        if (kind == "back") return "back";
        return "";
    }

    // ---------------- lock-on ----------------
    public static bool PushAwayBreaks { get { var d = Get(); return !d.enabled || !d.lockOn.enabled || d.lockOn.pushAwayBreaks; } }   // true = the old V3 behaviour
    public static float LockBreakAngle { get { return Get().lockOn.breakAngleDeg; } }   // HCKIT4 v95: flow.json lockOn.breakAngleDeg / breakSeconds (pushAwayBreaks: push away from the foe past this angle for this long)
    public static float LockBreakSeconds { get { return Get().lockOn.breakSeconds; } }
    public static bool AttackRelocks { get { var d = Get(); return !d.enabled || !d.lockOn.enabled || d.lockOn.attackRelocks; } }
    public static float lastToggleTime = -99f; public static bool lastToggleOn;
    static bool inited;

    public static bool FacingLocked(CameraRig rig, DuelFighter df) { return rig == null || rig.lockEnabled; }
    public static void ToggleLock(CameraRig rig, DuelFighter df)
    {
        if (rig == null) return;
        rig.lockEnabled = !rig.lockEnabled; lastToggleTime = Time.unscaledTime; lastToggleOn = rig.lockEnabled;
        if (df != null) df.lockFacing = FacingLocked(rig, df);
        if (Get().debugLog) Debug.Log("[Flow] lock " + (rig.lockEnabled ? "ON" : "OFF"));
    }

    // PlayerBrain calls this once per frame (replaces the old Tab line): lock toggle + facing flag + the dedicated riposte key
    public static void PlayerInput(CameraRig rig, DuelFighter df)
    {
        var d = Get();
        if (!d.enabled || !d.lockOn.enabled)
        {
            if (rig && InputMap.Down("lock", KeyCode.Tab)) rig.lockEnabled = !rig.lockEnabled;   // old behaviour
            if (df) df.lockFacing = true;
        }
        else
        {
            if (!inited && rig) { inited = true; rig.lockEnabled = d.lockOn.startLocked && !GameSettings.FreeCameraInCombat; }   // TIERS v96: free camera in combat starts unlocked
            if (rig && InputMap.AnyDown("lock", Keys(ref lockKeys, ref lockKeysSrc, d.lockOn.keys, new[] { KeyCode.Tab }))) ToggleLock(rig, df);
            if (df) df.lockFacing = FacingLocked(rig, df);
        }
        if (d.enabled && d.riposte.enabled && df && InputMap.AnyDown("riposte", Keys(ref ripKeys, ref ripKeysSrc, d.riposte.keys, new[] { KeyCode.B }))) df.PressRiposte();
    }

    static KeyCode[] lockKeys, ripKeys; static string[] lockKeysSrc, ripKeysSrc;
    static KeyCode[] Keys(ref KeyCode[] cache, ref string[] srcCache, string[] names, KeyCode[] fallback)
    {
        if (cache != null && ReferenceEquals(srcCache, names)) return cache;
        var list = new System.Collections.Generic.List<KeyCode>();
        if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (Enum.TryParse(names[i], true, out k)) list.Add(k); }
        srcCache = names; cache = list.Count > 0 ? list.ToArray() : fallback; return cache;
    }
    static bool AnyDown(KeyCode[] ks) { for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }
    public static string FirstKeyLabel(string[] names, string fallback) { return names != null && names.Length > 0 && !string.IsNullOrEmpty(names[0]) ? names[0] : fallback; }

    // same mapping PlayerBrain.DodgeFromKey uses (dodge.json input.onKey), for harnesses with no keys: dir = forward | back | left | right | free
    public static void DodgePress(DuelFighter df, string dir)
    {
        var inp = DodgeTable.Get().input;
        string act = dir == "left" || dir == "right" ? inp.onKey.lateral : dir == "back" ? inp.onKey.back : inp.onKey.forward;
        df.SetDodgeDir(dir);
        if (act == "sidestep") df.PressSidestep(dir == "left" ? -1 : dir == "right" ? 1 : 0);
        else if (act == "backstep") df.Press("backstep");
        else df.Press("roll");
    }

    // ---------------- load ----------------
    static void Load()
    {
        loaded = true; data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("flow");
            if (ta != null) json = ta.text;
#if UNITY_EDITOR
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/flow.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, "flow.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/flow.json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("FlowTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { data.enabled = false; return; }   // no file: old behaviour
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) { data.enabled = false; return; }
            if (d.chain == null) d.chain = new ChainCfg(); if (d.chain.rows == null) d.chain.rows = new ChainRow[0]; if (d.chain.combos == null) d.chain.combos = new Combo[0];
            if (d.lockOn == null) d.lockOn = new LockCfg(); if (d.lockOn.hud == null) d.lockOn.hud = new LockHud(); if (d.lockOn.range <= 0f) d.lockOn.range = 14f;
            if (d.attack == null) d.attack = new AttackCfg(); if (d.attack.magnet == null) d.attack.magnet = new MagnetCfg(); if (d.attack.turn == null) d.attack.turn = new TurnCfg(); if (d.attack.moves == null) d.attack.moves = new MoveRow[0];
            if (d.dodge == null) d.dodge = new DodgeCfg(); if (d.dodge.dirs == null) d.dodge.dirs = new DodgeRow[0];
            if (d.riposte == null) d.riposte = new RiposteCfg(); if (d.bot == null) d.bot = new BotCfg();   // V64P
            if (string.IsNullOrEmpty(d.scope)) d.scope = "player";
            data = d;
        }
        catch (Exception ex) { Debug.LogError("FlowTable: parse failed: " + ex.Message); data = new Data { enabled = false }; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded = false; data = null; inited = false; lastToggleTime = -99f; ForceApply = false; ForceHud = false; lockKeys = null; ripKeys = null; }
}
