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
    [Serializable] public class Draw { public bool enabled = true; public float dropM = 0.08f, arrowBackM = 0.03f, maxPitchDeg = 10f, bendDeg = 40f; public float[] stringA = new float[] { 0f, -0.645f, 0.137f }, stringB = new float[] { 0f, 0.671f, 0.137f }; public float stringRadiusM = 0.014f, minSpanT = 0f, tipClampT = 0.05f, widthM = 0.0035f, drawSeconds = 0.18f, handPx = 0f, handPy = 0.04f, handPz = 0.02f; public string color = "#e6dcc2"; public string handRole = "RightHand"; public bool npcIk = true, bowArmIk = true, spineLevel = true, groundHips = true; public float leanTargetDeg = 6f, headPitchDeg = 0f, spineLevelW = 1f; public float bowArmRaiseM = 0.05f; public float cheekRightM = 0.06f, cheekDownM = 0.09f, cheekFwdM = 0.10f; public float holdAfterReleaseS = 0f, easeOutS = 0.2f; /* RELEASEHOLD v1 (W5): the NPC draw IK (right arm to the cheek, bow arm, torso, head) stays at full weight this long after the loose, then eases out over easeOutS; 0 / 0.2 = today */ public DrawBody[] bodies = new DrawBody[0]; /* K18: per-body overrides of the NPC draw pose, matched on the NPC id (the fighter name before '#') */ public Draw Clone() { return (Draw)MemberwiseClone(); } }   // DRAW v1: the string (baked in the bow glb, removed at load) is redrawn as a line from the limb tips to the right hand while the fighter draws (Windup); snaps back on the loose
    [Serializable] public class DrawBody { public const float Unset = -999f; public string id = ""; public float cheekRightM = Unset, cheekDownM = Unset, cheekFwdM = Unset, leanTargetDeg = Unset, headPitchDeg = Unset, spineLevelW = Unset, bowArmRaiseM = Unset, holdAfterReleaseS = Unset, easeOutS = Unset; }   // K18: any field left at -999 (or absent in the json) inherits the global visual.draw value
    [Serializable] public class NpcVisual { public bool enabled = true; public string[] ids = new string[] { "NpcSlagCrossbowman" }; public float looseStartS = 0f; /* RELEASEHOLD v1 (W5): the NPC loose clip starts this many seconds in (0 = from its first frame, today). The clips re-draw from a half-drawn hand for ~0.45 s and release at ~0.5 s, but the arrow has already left at t 0: 0.48 starts the clip at the release */ }   // NPCARCHER v1: archer ids (bandits.json role archer) that get a BowVisual
    [Serializable] public class Visual { public Nock nock = new Nock(); public Draw draw = new Draw(); public NpcVisual npc = new NpcVisual(); public string handRole = "LeftHand"; public float holdRxDeg, holdRyDeg, holdRzDeg, holdPx, holdPy, holdPz; public Carry carry = new Carry(); }
    // K24: aim assist. Joan turns to the foe (or, with retarget, the best foe inside the cone) while she draws and looses; the cone is measured from the camera heading (the way the player is looking), the foe the duel already has is accepted outside the cone when lockedAlwaysFaces.
    // FREEAIM v1 (BowFree.cs): crosshair / camera-ray aim. aim.free in bow.json; JSON class only. enabled false = today.
    [Serializable] public class Free { public bool enabled, whenNoFoe = true, faceWhileHolding = true, log; public string aimHoldKey = "LeftAlt", colour = "#e8d9a8", colourOnTarget = "#ffffff"; public float maxRangeM = 45f, minRangeM = 5f /* DATALAYER 18 Nov: floor of the free-aim ray length (was Mathf.Max(5f, maxRangeM) in BowFree.Resolve); row absent = 5 */, softSnapDeg = 4f, reticleUpMaxFrac = 0.4f /* DATALAYER 18 Nov: clamp of reticleUpFrac to +-this (was +-0.4 literals in BowFree, ray and reticle); row absent = 0.4 */, reticleUpFrac = 0f /* RETICLEUP v1: the reticle and the aim ray sit this fraction of the screen height above the centre (0 = centre, today); the play camera pitches down, so 0.12 lets a 30-40 m target sit under the reticle */, aimFov = 42f, easeInS = 0.18f, easeOutS = 0.25f, speedCmS = 2400f, tightenTo = 0.55f; public float[] shoulderOffset = new float[] { 0.45f, 0.15f, -0.6f }; public int dotPx = 2, tickLenPx = 8, tickThickPx = 1, gapPx = 16; }
    [Serializable] public class Aim { public bool enabled, playerOnly = true, lockedAlwaysFaces = true, retarget, snapOnRelease = true, faceInLoco; public float coneDeg = 70f, rangeM = 24f, turnSpeedDegS = 720f, snapAboveDeg = 3f; public Free free = new Free(); }
    [Serializable] public class Arrow { public float nockedRadiusM = 0f, nockedFinHeightM = 0f, nockedFinLenM = 0f; /* DATA: the arrow ON the string: shaft radius (0 = radiusM), fin height / length (0 = the flight values) */ public string fletchColor = ""; /* DATA: fin colour override (pale fletching), "" = the fin row */ public float streakLenM = 0f; /* DATA: short pale motion streak behind the flying arrow, 0 = off */ public string streakColor = "#e9e5d3"; public int maxStuckPerBody = 0; /* DATA: stuck arrows kept per body, oldest goes first, 0 = only the global maxStuck */ public float stuckLevel = 0f; /* DATA: 0..1 how much of the landing pitch is levelled so a stuck arrow points back along the shot, not up or down */ public float unlitMul = 0.3f; /* DATA: colour scale when only an unlit shader exists (a tracer-like glow otherwise); 0 = off */ public bool stickToPlayer = false; /* DATA: false = an arrow that hits the hero ends (no rod left on her); true = it sticks like on any other body */ public float gravityMS2, arcMaxM = 99f, dragPerS, embedM = 0.12f, stuckSeconds, stuckFadeS = 1f, stuckScatterM = 0.05f, headLenM = 0.07f, headRadiusM = 0.016f, finLenM = 0.12f, finHeightM = 0.028f, finInsetM = 0.02f, shaftSmoothness = 0.35f, headSmoothness = 0.6f, headMetallic = 0.8f, thudGain, thudRate = 1f, looseGain, looseRate = 1f; public int maxStuck = 24, finCount = 3, radialSegments = 8; public bool castShadow = true; public string thudCue = "", looseCue = ""; public float tintMix = 0.35f /* FIX2 v1: was a literal in BowShot */, lengthM = 0.95f, radiusM = 0.014f, releaseHeightM = 1.35f, aimHeightM = 1.2f, releaseForwardM = 0.4f, volleySpreadM = 0.12f; public float flightSampleFrom = 0.96f, flightSampleSpan = 0.04f, overflyPadS = 1.5f, overflyStuckFrac = 0.1f, overflyMinS = 1f, streakSmoothness = 0.1f, streakWidthFrac = 0.7f; public string defaultTint = "#e8d9a8"; /* DATALAYER 18 Nov (datamove_03): overflyStuckFrac / overflyMinS = a dodged arrow flies on max(overflyMinS, stuckSeconds * overflyStuckFrac + overflyPadS) s; streakSmoothness / streakWidthFrac = the motion streak material smoothness and its width as a share of radiusM; defaultTint = arrow tint when the spec row has none (were 0.1 / 1 / 0.1 / 0.7 / "#e8d9a8" literals) */ /* DATALAYER 18 Nov: BowShot arrow flight: the tangent is sampled between flightSampleFrom and 1 of the move span (Embed + Overfly), flightSampleSpan is the matching divisor (keep = 1 - from), overflyPadS = extra seconds a dodged arrow flies on (was 0.96 / 0.04 / 1.5 literals) */ public string shaft = "#8a6a43", head = "#8d9399", fin = "#7a6a4c"; public string[] shaders = new string[0]; }
    // WALKFIRE v1: walk while drawing / loosing (BowWalk.cs). JSON class only, no scene field. bow.json "move".
    [Serializable] public class Move { public bool whileShooting = false, allowSprint = false, specialsToo = true, playerOnly = true, upperLayer = true, log = false; public float speedMul = 0.5f, legAnimSpeed = 0.55f, strafeRatio = 1f, legFadeS = 0.12f /* DATALAYER 18 Nov: the leg crossfade (idle / walk / strafe) while walking through a shot, was a literal in BowWalk; row absent = 0.12 */; public string[] upperTokens = new string[] { "Spine", "Neck", "Head", "Shoulder", "Arm", "Hand", "Thumb", "Index", "Middle", "Ring", "Pinky" }, lowerTokens = new string[] { "Leg", "Foot", "Toe" }; }
    [Serializable] public class Hud { public bool enabled; public int bracketPx, thickPx, gapPx, chipRightPx, chipBottomPx, chipFontPx, chipWidthPx = 140; public float chipLineH = 1.6f, chipShowS = 0f, chipFadeS = 0.5f; public string colour = "#e8d9a8", chipBow = "BOW", chipSword = "SWORD", chipAmmoSep = "  "; }
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
        public Move move = new Move();   // WALKFIRE v1
    }

    static Data data; static int force; static bool forceAuto; static System.Collections.Generic.Dictionary<string, Draw> drawFor;   // force: 1 = -bow:on, -1 = -bow:off
    static bool Flag(string f) { foreach (var a in Environment.GetCommandLineArgs()) if (a == f) return true; return false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { data = null; force = 0; forceAuto = false; drawFor = null; }

    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; drawFor = null; Load(); }
    /// <summary>K18: the draw settings for one NPC body: visual.draw with the matching visual.draw.bodies[] row laid over it (cached per id). No row = the global object itself.</summary>
    public static Draw DrawFor(string npcId)
    {
        var D = Get().visual.draw; if (D == null || D.bodies == null || D.bodies.Length == 0 || string.IsNullOrEmpty(npcId)) return D;
        if (drawFor == null) drawFor = new System.Collections.Generic.Dictionary<string, Draw>(); Draw c; if (drawFor.TryGetValue(npcId, out c)) return c;
        c = D; for (int i = 0; i < D.bodies.Length; i++) { var b = D.bodies[i]; if (b == null || b.id != npcId) continue; c = D.Clone(); const float U = DrawBody.Unset;
            if (b.cheekRightM > U) c.cheekRightM = b.cheekRightM; if (b.cheekDownM > U) c.cheekDownM = b.cheekDownM; if (b.cheekFwdM > U) c.cheekFwdM = b.cheekFwdM; if (b.leanTargetDeg > U) c.leanTargetDeg = b.leanTargetDeg;
            if (b.headPitchDeg > U) c.headPitchDeg = b.headPitchDeg; if (b.spineLevelW > U) c.spineLevelW = b.spineLevelW; if (b.bowArmRaiseM > U) c.bowArmRaiseM = b.bowArmRaiseM; if (b.holdAfterReleaseS > U) c.holdAfterReleaseS = b.holdAfterReleaseS; if (b.easeOutS > U) c.easeOutS = b.easeOutS; break; }
        if (Log) Debug.Log("[bow] draw row for " + npcId + ": cheek R/D/F " + c.cheekRightM.ToString("F3") + "/" + c.cheekDownM.ToString("F3") + "/" + c.cheekFwdM.ToString("F3") + " lean " + c.leanTargetDeg.ToString("F1") + " headPitch " + c.headPitchDeg.ToString("F1") + " spineW " + c.spineLevelW.ToString("F2") + " bowArmRaise " + c.bowArmRaiseM.ToString("F3") + (ReferenceEquals(c, D) ? " (global, no row)" : " (row applied)"));
        drawFor[npcId] = c; return c;
    }
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
            if (d.visual == null) d.visual = new Visual(); if (d.visual.carry == null) d.visual.carry = new Carry(); if (d.aim == null) d.aim = new Aim(); if (d.aim.free == null) d.aim.free = new Free(); if (d.arrow == null) d.arrow = new Arrow(); if (d.hud == null) d.hud = new Hud(); if (d.move == null) d.move = new Move();
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
