using System;
using UnityEngine;

// BOW v2 (laneBOW, STAGED DARK, UNPROVEN). Data: Assets/Data/bow.json (copy in StreamingAssets). Master `enabled` false or no file = the exact old game (BowHooks answers neutral on its first line).
// Ported from Frankendom V3 sandbox-rules (missile / missileLanes / bowSpecial / specialPick). Nothing here names a character or a weapon: bow.json `characters` lists roster ids, `weaponId` is the key attack_clips.json rows use.
// Command line: -bow:on / -bow:off force the master, -bow:auto forces auto.enabled, -bow:log prints one line per switch and shot.
public static class BowCfg
{
    [Serializable] public class Lane { public string move = ""; public int lane; public int multPerMille; }
    [Serializable] public class LaneDir { public int lane; public string direction = ""; }
    [Serializable] public class Special
    {
        public string id = "", label = "", glyph = "", v3Skill = "", meleeTwin = "", moveId = "", clipRole = "", tint = "";
        public int unlockAfterBossKills, arrows, multPerMille, windupTicks, activeTicks, speedCmS, pinTicks;
        public int[] lanes = new int[0];
        public float posture, stagger, knockback;
    }
    [Serializable] public class Slots { public string special1 = "", special2 = ""; }
    [Serializable] public class Auto
    {
        public bool enabled, requireLockOn, blockWhenGearWeapon;
        public float rangeEnterM, rangeExitM, maxRangeM;
        public int enterHoldTicks, minDwellTicks, lockoutAfterGuardTicks, manualHoldTicks;
    }
    [Serializable] public class Foe { public string id = ""; public bool allow = true; public float enterOffsetM, exitOffsetM, pinMul = 1f; }
    [Serializable] public class Keys { public string[] toggleKeys = new string[0], padToggleKeys = new string[0]; }
    [Serializable] public class GearWeapon { public float damageMul = 1f, speedMul = 1f, reachMul = 1f, postureMul = 1f; public bool twoHanded; }
    [Serializable] public class Carry { public bool enabled; public string parentRole = "Spine2"; public float rxDeg, ryDeg, rzDeg, px, py, pz; public float scale = 1f; public int releaseDelayTicks = 45; }   // K24: where the bow rests when it is not being drawn (null/disabled = it stays on the hand, as before)
    [Serializable] public class Nock { public bool enabled = true; public float px = 0f, py = 0f, pz = 0f, rxDeg = 0f, ryDeg = 0f, rzDeg = 0f; }   // the arrow on the string during Windup, bow-local (BowVisual)
    [Serializable] public class Draw { public bool enabled = true; public float dropM = 0.08f, arrowBackM = 0.03f, maxPitchDeg = 10f, bendDeg = 40f; public float[] stringA = new float[] { 0f, -0.645f, 0.137f }, stringB = new float[] { 0f, 0.671f, 0.137f }; public float stringRadiusM = 0.014f, minSpanT = 0f, tipClampT = 0.05f, widthM = 0.0035f, drawSeconds = 0.18f, handPx = 0f, handPy = 0.04f, handPz = 0.02f; public string color = "#e6dcc2"; public string handRole = "RightHand"; }   // DRAW v1: the string (baked in the bow glb, removed at load) is redrawn as a line from the limb tips to the right hand while the fighter draws (Windup); snaps back on the loose
    [Serializable] public class NpcVisual { public bool enabled = true; public string[] ids = new string[] { "NpcSlagCrossbowman" }; }   // NPCARCHER v1: archer ids (bandits.json role archer) that get a BowVisual
    [Serializable] public class Visual { public Nock nock = new Nock(); public Draw draw = new Draw(); public NpcVisual npc = new NpcVisual(); public string handRole = "LeftHand"; public float holdRxDeg, holdRyDeg, holdRzDeg, holdPx, holdPy, holdPz; public Carry carry = new Carry(); }
    // K24: aim assist. Joan turns to the foe (or, with retarget, the best foe inside the cone) while she draws and looses; the cone is measured from the camera heading (the way the player is looking), the foe the duel already has is accepted outside the cone when lockedAlwaysFaces.
    [Serializable] public class Aim { public bool enabled, playerOnly = true, lockedAlwaysFaces = true, retarget, snapOnRelease = true, faceInLoco; public float coneDeg = 70f, rangeM = 24f, turnSpeedDegS = 720f, snapAboveDeg = 3f; }
    [Serializable] public class Arrow { public float nockedRadiusM = 0f, nockedFinHeightM = 0f, nockedFinLenM = 0f; /* DATA: the arrow ON the string: shaft radius (0 = radiusM), fin height / length (0 = the flight values) */ public string fletchColor = ""; /* DATA: fin colour override (pale fletching), "" = the fin row */ public float streakLenM = 0f; /* DATA: short pale motion streak behind the flying arrow, 0 = off */ public string streakColor = "#e9e5d3"; public int maxStuckPerBody = 0; /* DATA: stuck arrows kept per body, oldest goes first, 0 = only the global maxStuck */ public float stuckLevel = 0f; /* DATA: 0..1 how much of the landing pitch is levelled so a stuck arrow points back along the shot, not up or down */ public float unlitMul = 0.3f; /* DATA: colour scale when only an unlit shader exists (a tracer-like glow otherwise); 0 = off */ public bool stickToPlayer = false; /* DATA: false = an arrow that hits the hero ends (no rod left on her); true = it sticks like on any other body */ public float gravityMS2, arcMaxM = 99f, dragPerS, embedM = 0.12f, stuckSeconds, stuckFadeS = 1f, stuckScatterM = 0.05f, headLenM = 0.07f, headRadiusM = 0.016f, finLenM = 0.12f, finHeightM = 0.028f, finInsetM = 0.02f, shaftSmoothness = 0.35f, headSmoothness = 0.6f, headMetallic = 0.8f, thudGain, thudRate = 1f, looseGain, looseRate = 1f; public int maxStuck = 24, finCount = 3, radialSegments = 8; public bool castShadow = true; public string thudCue = "", looseCue = ""; public float tintMix = 0.35f /* FIX2 v1: was a literal in BowShot */, lengthM = 0.95f, radiusM = 0.014f, releaseHeightM = 1.35f, aimHeightM = 1.2f, releaseForwardM = 0.4f, volleySpreadM = 0.12f; public string shaft = "#8a6a43", head = "#8d9399", fin = "#7a6a4c"; public string[] shaders = new string[0]; }
    [Serializable] public class Hud { public bool enabled; public int bracketPx, thickPx, gapPx, chipRightPx, chipBottomPx, chipFontPx, chipWidthPx = 140; public float chipLineH = 1.6f; public string colour = "#e8d9a8", chipBow = "BOW", chipSword = "SWORD", chipAmmoSep = "  "; }
    [Serializable]
    public class Data
    {
        public bool enabled, debugLog, quiverOn, guardExitsBow;
        public string[] characters = new string[0];
        public bool equipKeepNoFoe = true; /* DATA: with the bow item in the main hand the bow mode stays up (bow in hand, sword hidden) with or without a live foe; false = the no-foe drop of today */ public bool equipRearm = true; /* DATA: while the bow item sits in the main hand the bow mode comes back up whenever the player has a live foe (the no-foe drop only lasts until the next engage); false = old: equip sets the mode once, the first no-foe tick drops it */ public string weaponId = "bow", gearItemId = "", equipItemId = "";   /* K3b: gear item id whose main-hand equip turns the bow mode on ("" = off, today) */
        public int ammo, speedCmS = 900, sharePerMille = 1000, specialRecoveryTicks = 31;
        public float minGapM = 3f, specialBaseDamage = 22f;
        public Lane[] lanes = new Lane[0];
        public LaneDir[] laneDirection = new LaneDir[0];
        public Slots slots = new Slots();
        public Special[] specials = new Special[0];
        public Auto auto = new Auto();
        public Foe[] foes = new Foe[0];
        public Keys input = new Keys();
        public GearWeapon gearWeapon = new GearWeapon();
        public Visual visual = new Visual();
        public Aim aim = new Aim();
        public Arrow arrow = new Arrow();
        public Hud hud = new Hud();
    }

    static Data data; static int force; static bool forceAuto;   // force: 1 = -bow:on, -1 = -bow:off
    static bool Flag(string f) { foreach (var a in Environment.GetCommandLineArgs()) if (a == f) return true; return false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { data = null; force = 0; forceAuto = false; }

    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }
    public static bool Master { get { if (data == null) Load(); return force > 0 || (force == 0 && data.enabled); } }
    public static bool AutoOn { get { if (data == null) Load(); return Master && (forceAuto || data.auto.enabled); } }
    public static bool Log { get { return Get().debugLog || Flag("-bow:log"); } }

    static void Load()
    {
        data = new Data(); force = Flag("-bow:off") ? -1 : Flag("-bow:on") ? 1 : 0; forceAuto = Flag("-bow:auto");
        string json = DataPath.ReadText("bow.json");
        if (string.IsNullOrEmpty(json)) return;   // no file = today
        try
        {
            var d = JsonUtility.FromJson<Data>(json.TrimStart('﻿'));
            if (d == null) return;
            if (d.lanes == null) d.lanes = new Lane[0]; if (d.laneDirection == null) d.laneDirection = new LaneDir[0]; if (d.specials == null) d.specials = new Special[0];
            if (d.foes == null) d.foes = new Foe[0]; if (d.characters == null) d.characters = new string[0];
            if (d.slots == null) d.slots = new Slots(); if (d.auto == null) d.auto = new Auto(); if (d.input == null) d.input = new Keys(); if (d.gearWeapon == null) d.gearWeapon = new GearWeapon();
            if (d.visual == null) d.visual = new Visual(); if (d.visual.carry == null) d.visual.carry = new Carry(); if (d.aim == null) d.aim = new Aim(); if (d.arrow == null) d.arrow = new Arrow(); if (d.hud == null) d.hud = new Hud();
            data = d;
        }
        catch (Exception ex) { Debug.LogWarning("[bow] bow.json parse failed, the bow stays off: " + ex.Message); data = new Data(); }
    }

    // ---- lookups (all data, no names in code) ----
    public static bool IsBowCharacter(string id) { var d = Get(); if (string.IsNullOrEmpty(id)) return false; for (int i = 0; i < d.characters.Length; i++) if (d.characters[i] == id) return true; return false; }
    public static Special SpecialById(string id) { var d = Get(); for (int i = 0; i < d.specials.Length; i++) if (d.specials[i] != null && d.specials[i].id == id) return d.specials[i]; return null; }
    public static Special SpecialByMove(string moveId) { var d = Get(); if (string.IsNullOrEmpty(moveId)) return null; for (int i = 0; i < d.specials.Length; i++) if (d.specials[i] != null && d.specials[i].moveId == moveId) return d.specials[i]; return null; }
    public static Special SpecialBySlot(int slot) { var d = Get(); return SpecialById(slot == 0 ? d.slots.special1 : d.slots.special2); }
    public static Lane LaneOf(string moveId) { var d = Get(); if (string.IsNullOrEmpty(moveId)) return null; for (int i = 0; i < d.lanes.Length; i++) if (d.lanes[i] != null && d.lanes[i].move == moveId) return d.lanes[i]; return null; }
    public static string DirectionOf(int lane) { var d = Get(); for (int i = 0; i < d.laneDirection.Length; i++) if (d.laneDirection[i] != null && d.laneDirection[i].lane == lane) return d.laneDirection[i].direction; return "thrust"; }
    public static Foe FoeRow(string id) { var d = Get(); if (string.IsNullOrEmpty(id)) return null; for (int i = 0; i < d.foes.Length; i++) if (d.foes[i] != null && d.foes[i].id == id) return d.foes[i]; return null; }
    public static Color Col(string hex, Color fallback) { Color c; return !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out c) ? c : fallback; }
}
