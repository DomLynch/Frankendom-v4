using System;
using System.IO;
using UnityEngine;

// Self-contained finisher data shapes + loader. Nothing here depends on the removed turn-based (Alpha) code.
// Data: Assets/Data/finishers.json (StreamingAssets copy in a build). Schema = FN2's finishers.json; JsonUtility ignores unknown keys (clipImport is editor-only).
// This file: camera shot rows, entry rows, cut/gore shapes, the loader (FinisherConfig) and the real-time settings block "rt".

public enum FinisherMove { Light, Slash, Stab, Heavy, Kick, Guard, Roll, Sidestep, Special }   // names are what finishers.json "moves" filters use
public enum FinisherLoc { Arm, Leg, Torso, Head }                                               // ints match CutUnit.loc and GoreLocRow order

// everything the entry pick needs to know about the killing blow (was Beat)
public struct FinisherContext
{
    public DuelFighter killer, victim; public int killerSide, victimSide;   // 0 = Roster player, 1 = Roster enemy (FinisherSides)
    public FinisherMove move; public FinisherLoc loc; public bool deathblow;   // deathblow = the blow was a posture-break deathblow (V61B)
}

public struct FinisherGoreCall { public string type, unit, param; public DuelFighter victim, attacker; public Vector3 point, away; public float strength; }

// ---------------- camera shots ----------------
// One camera "shot" = an orbit pose around a focus point of the killer/victim pair, evaluated live every frame.
[Serializable]
public class FinisherShot
{
    public string id = "";
    public string focus = "mid";        // "mid" (between the fighters) | "actor" | "target"
    public float angleDeg = 25f;        // camera azimuth around the focus relative to the actor->target line: 0 = behind the actor, 90 = side, 180 = behind the target
    public float side = 1f;             // +1 right of the actor->target line, -1 left, 0 = alternate on every cut
    public float dist = 5f;             // metres from the focus
    public float height = 1.7f;         // camera height above the focus ground
    public float lookHeight = 1.2f;     // look-at height above the focus ground
    public float shoulder = 0f;         // extra lateral offset of the camera (over-the-shoulder)
    public float lookAhead = 0.2f;      // 0..1 slides the look-at from the focus toward the target
    public float fov = 38f;
    public float blend = 0.25f;         // seconds (unscaled) to move here from wherever the camera is; 0 = hard cut
    public float shake = 0f;            // metres of camera shake on arrival (decays in ~0.25 s)
    // fit: dist is DERIVED so the fighters fit the frame (both full bodies for fitTarget "pair", the target alone for "target"), from the real separation, fov and aspect
    public bool fit = false;
    public string fitTarget = "pair";   // "pair" | "target"
    public float bodyHalfWidth = 0.9f;  // metres each side of a fighter's centre that must stay in frame (weapon arm included)
    public float bodyHeight = 2.6f;     // metres from the feet that must stay in frame (the tallest fighter, horns included)
    public float fitMargin = 1.15f;     // > 1 = air around the fighters
    public float minDist = 4f, maxDist = 7.5f;
    public float pushIn = 0f, pushSeconds = 0f;   // slow push-in: dist shrinks by pushIn metres over pushSeconds (unscaled) after the cut
    // V61B: frame the victim's REAL body (SkinnedMeshRenderer bounds), not the root transform that stays where the fighter stood after a death clip
    public string track = "body";       // "body" (default: live body bounds) | "root" (the old transform-based look)
    public bool frameBody = false;      // focus "target": dist grows until the body fits the frame (see frameTop / frameMargin)
    public float frameTop = 0f;         // > 0: only the top N metres of the body must fit (head + chest close-up); 0 = the whole body
    public float frameMargin = 1.2f, frameMaxDist = 9f;   // air around the body; the distance never grows past max(dist, frameMaxDist)
    public float lookMin = 0.2f;        // the look-at stays at least this far above the bottom of the framed part (a prone body is looked at, not the floor)
    public float bodyFollow = 10f;      // smoothing rate (1/s) of the body bounds
    // V62B: camera side + exposure per shot (data)
    public bool gate = true;            // true: the camera is placed so it looks TOWARD the gate (rt.look.gatePoint); the azimuth is rotated (nearest angle, mirrored side first) until the view direction is within rt.look.gateMinDot of the gate direction
    public float gateMinDot = -2f;      // V62C: per-shot minimum cosine between the view direction and the gate direction (-2 = rt.look.gateMinDot); the wide / fall / settle shots use 0.8
    public float darkenEV = 0f;         // extra stops this shot is darker (+) / brighter (-) than the gameplay exposure, on top of rt.look.darkenEV (Fixed exposure only; blended at rt.look.evRate stops / s)
    // V1FIN v94 (V1 finisherSidePose / decapitation front-hold): the shoulder slide and the look turn are RAMPED over slideRampS (V1 finishPush: 1.3 / 0.75 s) instead of being there from the first frame;
    // pieceUnit != "" = the look-at moves toward the midpoint of the victim's body and that unit's severed piece (V1: 'the look centres between corpse and head'), by pieceTurn (0..1) x the ramp
    public float slideRampS = 0f;       // 0 = no ramp (old behaviour)
    public string pieceUnit = "";       // e.g. "head"; "" = off
    public float pieceTurn = 0f;
}
[Serializable] public class FinisherShotKey { public float t = 0f; public string rel = "start"; public string shot = ""; public float blend = -1f; }       // rel: "start" | "contact"
// V62: one point of a per-finisher time-scale curve (t = unscaled seconds after the key fires, scale = Time.timeScale factor; smoothstep between points)
[Serializable] public class SlowPoint { public float t = 0f; public float scale = 1f; }
[Serializable] public class FinisherSlowKey { public float t = 0f; public string rel = "contact"; public float scale = 0.25f, duration = 0.6f, easeIn = 0.05f, easeOut = 0.25f; public SlowPoint[] curve = new SlowPoint[0]; }   // duration in UNSCALED seconds. V62: curve non-empty = a time-scale CURVE instead of the single dip (scale/duration/easeIn ignored; easeOut = the tail back to 1 after the last point)

[Serializable] public class FinisherGoreKey { public float t = 0f; public string rel = "contact"; public string type = ""; public string cut = ""; public string bone = ""; public float strength = 1f; public string param = ""; }   // type: head_detach | skull_split | cut | blood_burst | custom
[Serializable]
public class FinisherEntry
{
    public string id = "";
    public bool enabled = true;
    public bool placeholder = false;        // true: uses stand-in clips (no paired clip yet); logged
    public float weight = 1f;
    public string attackerSide = "any";     // "any" | "player" | "enemy" | a roster character id (FinisherSides.Matches)
    public string[] weapons = new string[0];    // attacker weapon ids ("sword","maul", ...); empty = any
    public string[] directions = new string[0]; // kill direction "front"|"back"|"side"; empty = any
    public string[] moves = new string[0];      // Move names of the killing blow; empty = any
    public string[] locations = new string[0];  // Loc names where the blow landed; empty = any
    // attacker: either a DuelFighter move (real sim animation) or a paired clip (played directly on the Animator)
    public string attackerMove = "heavy_overhead";
    public string attackerClip = "";        // Animator state; "" = use attackerMove
    public float attackerClipSpeed = 1f, attackerFade = 0.1f, attackerClipStart = 0f;
    public float contactAt = -1f;           // seconds after the start when the blow lands; -1 = from the move's wind-up (or 0.6 s for a clip)
    public float duration = 2.4f;           // length of the scene in seconds (anim time); the attacker returns to idle after it
    // victim
    public string victimClip = "";          // Animator state; "" = the normal death (DuelFighter.Die)
    public float victimClipSpeed = 1f, victimFade = 0.08f, victimStart = -1f;   // victimStart -1 = at the contact
    public BladeAimCfg bladeAim;            // V1CLIPS v98: V1 aimBladeAt (blade through the chest) for this entry; null / disabled = the clip as authored
    public V1Clips clipsV1;                 // V1FIN v94: V1's victim (and killer) clip for this entry; null / states missing = the entry as it is
    public FinisherShotKey[] shotsV1;       // V1FIN v94: V1's camera for this entry (used instead of `shots` while rt.v1.enabled && rt.v1.camera); null / empty = `shots`
    public FinisherGoreKey[] goreV1;        // V1FIN v94: V1's gore timing (used instead of `gore` while rt.v1.enabled && rt.v1.timing); null / empty = `gore`
    public float victimClipStart = 0f;      // FINFIX v93: second INSIDE the victim clip where it starts (skips a standing lead-in, e.g. the Mixamo death clip's first ~0.5 s)
    public float dieAt = -2f;               // seconds when DuelFighter.Die() runs (ragdoll hook): -2 = at the contact when no victimClip, else at the clip end; -1 = never
    public bool holdLast = true;
    // placement (centre distance of the pair and who faces whom)
    public float distance = 1.7f;
    public float victimYawDeg = 0f;         // victim yaw relative to "facing the attacker"
    public bool moveWhole = false;          // attacker root motion allowed to slide the pair (not used yet)
    // FN2 pair sync for paired Mixamo clips (all stand-in metres, tuned in data; FinisherPlayer multiplies by the killer's DuelFighter.scale):
    public float[] pairOffset = new float[0];   // [right, up, forward] of the victim centre relative to the killer, in the killer's frame; empty = use `distance` straight ahead
    public float killerYawDeg = 0f;         // extra yaw of the killer GameObject so its clip's own facing points at the victim (clip facing offset)
    public bool distanceScaleByKiller = true;   // multiply distance / pairOffset by the killer's scale (DuelFighter.scale)
    public FinisherShot[] shotDefs = new FinisherShot[0];
    public FinisherShotKey[] shots = new FinisherShotKey[0];
    public FinisherGoreKey[] gore = new FinisherGoreKey[0];
    public FinisherSlowKey[] slowmo = new FinisherSlowKey[0];
    public float holdAfter = 1.6f;          // seconds (unscaled) the final frame stays before control returns
    public float contactHoldMs = -1f;       // FINFIX v93: animation-only hold at the contact (both Animators stop for this many real ms, the scene clock / camera / blood keep running; NO Time.timeScale). -1 = rt.contactHoldMs
    public float contactShake = -1f;        // FINFIX v93: camera shake amplitude at the contact (m); -1 = rt.contactShake
    // V62 pair probe (-finisherauto logs where the killer's hand is relative to the victim's body at contact and suggests pairOffset numbers; tune in data, never in code)
    public float bladeReach = 0.6f;                       // wanted horizontal distance (stand-in metres, x killer scale) from the killer's hand to the victim's probe bone at contact
    public string probeKillerBone = "RightHand", probeVictimBone = "Spine2";   // bone-name suffixes
    public ContactFrame[] sheetFrames = new ContactFrame[0];   // non-empty = this entry's own -finisherauto contact-sheet frames (else rt.auto.sheetFrames)
    public string note = "";
    public FinisherOverride[] overrides = new FinisherOverride[0];   // JOANFIN v77: per killer / victim roster-id rows that replace pair numbers, shots or availability (FinisherPair)
    public FinisherEntry Clone() { return (FinisherEntry)MemberwiseClone(); }
}
// JOANFIN v77: a row of entry.overrides. killer / victim = roster id ("*" = any; an exact id beats "*"). Unset = -999 / -1 / empty (the entry's own value stays). pairOffset values are stand-in metres like the entry's
// (multiplied by the killer's rt.killers pairScale and DuelFighter.scale), shotDefs rows replace the entry's shots with the same id (or are appended).
[Serializable]
public class FinisherOverride
{
    public string killer = "*", victim = "*";
    public bool unavailable = false;        // true: this entry is never picked for that pair (the rotation takes the next one, or a plain death)
    public bool requireDeathblow = false;   // true: only picked when the blow was a posture-break deathblow (the victim is hunched / staggering, head and neck low enough for a short killer)
    public float[] pairOffset = new float[0];
    public float distance = -1f, bladeReach = -1f, killerYawDeg = -999f, victimYawDeg = -999f, contactAt = -1f;
    public FinisherShot[] shotDefs = new FinisherShot[0];
    public string note = "";
}
// JOANFIN v77: rt.killers row: per roster id scale of the pair distance / offsets / bladeReach (stand-in metres are Dracula-sized; 1 = no change). Multiplies DuelFighter.scale.
[Serializable] public class FinisherKillerRow { public string id = ""; public float pairScale = 1f; public string note = ""; }
[Serializable] public class SprayCfg { public float seconds = 1.0f, rate = 90f, coneDeg = 16f, gravity = 8f; public float[] speed = { 2.5f, 4.5f }, size = { 0.03f, 0.06f }, life = { 0.5f, 0.9f }; public float[] phaseUntilS, phaseRate, phaseSpeed; /* V1FIN v94: non-empty = stepped emission (phaseRate[i] per second until phaseUntilS[i]; phaseSpeed[i] = the start speed then) */ }
// V1FIN v94: V1 src/finisher-blood.ts as data. One profile per anatomy (decapitation, splitCrown, runThrough, opened): the sources and the emission phases (burst 78/s x strength for 1.7 s, 32/s to 3.5 s, 14/s to 7 s).
[Serializable] public class V1BloodSource { public string site = ""; public float strength = 1f; public float upOffsetM = 0f, fwdOffsetM = 0f; public float dirUp = 1f, dirFwd = 0f; public bool onPiece = false; public bool fromTop = false; }
[Serializable] public class V1BloodProfile { public string id = ""; public SprayCfg spray = new SprayCfg(); public V1BloodSource[] sources = new V1BloodSource[0]; }

[Serializable] public class GoreLocRow { public string name = "torso"; public string[] bones = new string[0]; public float radiusM = 0.12f; public float frontArcDeg = 360f; public float[] along = { 0.2f, 0.8f }; }
[Serializable]
public class PieceCfg   // V62C: the severed piece (head) physics; finishers.json gore.piece. Read by FinisherPiece.
{
    public string shape = "sphere";                 // sphere | capsule | box (fitted to the piece's renderer bounds)
    public float fitK = 0.5f;                       // sphere / capsule radius = min extent + (max - min) * fitK (0 = tightest, 1 = the horns' reach)
    public float radiusScale = 1f, minRadiusM = 0.08f;
    public float dynamicFriction = 0.6f, staticFriction = 0.6f, bounciness = 0.1f;   // low friction / bounce: it rolls a little and stops
    public string collisionMode = "ContinuousSpeculative";   // ContinuousSpeculative | Continuous | ContinuousDynamic | Discrete
    public bool interpolate = true;
    public float sleepThreshold = 0.05f, maxDepenetrationVelocity = 2f, linearDamping = 0.05f;   // linearDamping < 0 = leave the MeshCut value
    public float spawnClampM = 0.01f;               // at spawn the lowest point of the mesh is lifted to at least ground + this
    public float restMaxM = 0f;                     // FINFIX v93: > 0 = the piece never rests (or travels) farther than this from where it was cut (horizontal metres); beyond it the position is pulled back and the horizontal speed killed. 0 = off
    public float restMaxDamp = 0.5f;                // FINFIX v93: horizontal speed multiplier applied each physics step while the clamp is active (0 = dead stop)
    public float rescueBelowM = 0.3f;               // ONE safety rescue only when the collider bottom is more than this under the ground (not every frame)
    public int maxRescues = 1;
    public float settleSeconds = 0.5f;
    // V64F2: a resting piece whose VISIBLE mesh (horns, hair) sinks into the floor (the collider is a smaller sphere) is lifted once to the floor and frozen (kinematic), logged "SettleLift"
    public bool settleLift = true, settleFreeze = true; public float settleTolM = 0.02f, settleMaxLiftM = 0.4f, settleStillS = 0.2f;
}
[Serializable] public class GoreCfg
{
    public bool enabled = true;
    public int maxDecalsPerLoc = 14, seed = 99, texSize = 64;
    public int[] decalsBySeverity = { 0, 3, 6, 10 };   // decals on a location when its severity rises to index
    public float critMul = 1.6f;
    public float[] sizeM = { 0.07f, 0.16f };           // decal quad edge, min..max, metres
    public float[] color = { 0.30f, 0.02f, 0.02f, 1f };
    public float quadLift = 0.004f;
    public GoreLocRow[] locs = new GoreLocRow[0];
    public SprayCfg woundSpray = new SprayCfg { seconds = 0.35f, rate = 60f };
    public SprayCfg neckSpray = new SprayCfg();
    public V1BloodProfile[] v1blood = new V1BloodProfile[0];   // V1FIN v94
    public PieceCfg piece = new PieceCfg();   // V62C
    public CleaveRow[] cleaves = new CleaveRow[0];   // V63G
    public BurstRow[] bursts = new BurstRow[0];      // V63G
    public FinVisCfg finisherVfx = new FinVisCfg();   // FINVIS v1: finisher blood v2 (pools, lit droplets, cut reveal); v2 = false is today's look
}

// V1FIN v94: V1 src/severed-head.ts, every number there. Used by SeveredHeadArc.
[Serializable]
public class ArcCfg
{
    public bool enabled = false;
    public float sideMps = 1.1f, upMps = 1.8f, spinRadS = 9f;             // launchSeveredHead: velocity (axis-perpendicular 1.1, up 1.8), spin 9 rad/s
    public float sideSign = 1f, spinSign = 1f;                            // +1 = the right of the killer->victim axis (the side the V1 camera slides to); see MANIFEST on handedness
    public float gravity = 12f;                                           // stepSeveredHead: 'a touch heavier than life'
    public float bounceMinVy = 1f, bounceVy = 0.28f, bounceXz = 0.68f;    // a real bounce: vy < -1 -> vy * -0.28, xz * 0.68
    public float rollDecayPerS = 2.1f, restSpeed = 0.05f;                 // rolling friction 1 - 2.1 dt; rests at <= 0.05 m/s
    public float radiusK = 0.38f, radiusMinM = 0.07f, radiusMaxM = 0.24f; // radius = clamp(max(size.x, size.z) * .38, .07, .24) (characters.ts sever)
    public float maxSubstepS = 0.0166667f;                                // V1 steps once per frame (~1/60); longer frames are split
    public float restLiftMaxM = 0.4f, restLiftTolM = 0.02f;              // once at rest: lift the visible mesh out of the floor if the V1 sphere radius left it under (<= restLiftMaxM)
}

// V1FIN v94: V1 src/opened.ts place(): the upper half slides .5 m (x scale) sideways and .12 m along the blow over smooth(.045,.3) of the 3.2 s clock and tips over smooth(.2,.66); seconds = progress x 3.2.
[Serializable]
public class ScriptCfg
{
    public bool enabled = false;
    public float slideSideM = 0.5f, slideAlongM = 0.12f, slideStartS = 0.144f, slideEndS = 0.96f, fallStartS = 0.64f, fallEndS = 2.112f;
    public float fallDeg = 90f, sideSign = 1f, floorM = 0.008f;
    public DropCfg weapon = new DropCfg();   // V1CLIPS v98: opened.ts dropped weapon (V1Extras.cs)
    public LegsCfg legs = new LegsCfg();     // V1CLIPS v98: opened.ts scripted leg topple (only without the physics ragdoll)
}

// V1FIN v94: V1's own clips (OpenTrident finishers_vps\clips-authored, 106-joint Mixamo-named). Applied over an entry only when every named Animator state exists on the fighter (else the entry plays as before).
[Serializable]
public class V1Clips
{
    public string victimClip = "", attackerClip = "";
    public float victimClipSpeed = 0.75f, victimClipStart = 0f, victimStart = -1f, dieAt = -2f;
    public float attackerClipSpeed = -1f, attackerClipStart = -1f, contactAt = -1f, duration = -1f, holdAfter = -1f;   // -1 = keep the entry's
    public float[] pairOffset = new float[0];   // V1CLIPS v98: [right, up, forward] replaces the entry's pairOffset while the V1 clips play (placement for the V1 clip poses; up lifts the victim)
    public string source = "";
}

// V1FIN v94: V1 src/skull.ts (splitSkull) + characters.ts splitCrown(). Used by MeshCut kind "crown".
[Serializable]
public class CrownCfg
{
    public string frame = "auto";            // "auto": up = neck->head, forward = the victim's facing (a sagittal plane for any rig) | "bone": the head bone's own axes
    public float openDeg = 10.8858f;         // each half: 0.19 rad about the hinge (z of the head frame)
    public float openShiftM = 0.008f;        // each half also slides 0.008 m outward
    public float openDelayS = 0f, openS = 0.384f;   // V1: open = smoothstep((progress - .045) / .12) of the 3.2 s scene = starts with the split, takes 0.384 s
    public float hingeMinYM = -0.045f;       // hinge height in the head frame: max(bounds.min.y, -0.045)
    public float hideScale = 0.0001f;        // the real Head bone is scaled to this (V1: bone.scale .0001) so the body stays intact and the corpse reads headless
    public bool includeBoneMeshes = true;    // plain MeshRenderers under the head bone (helmet, hair cards, horns) are split too
    public int capTexSize = 64;              // the mottled cut-face texture (V1 skull.ts: 64 x 64)
    public float[] capTint = { 200f, 175f, 165f };   // texel base colour x shade (0..255)
    public float capShadeMin = 0.75f, capShadeSpan = 0.25f, capVein = 0.45f, capVeinStep = 0.8f;
    public float[] capColor = { 0.2667f, 0.0863f, 0.0980f, 1f };   // #441619, multiplies the map
    public float seamUpM = 0.0f;             // blood origin above the top of the skull (V1: crown bone + .045 up)
}

[Serializable]
public class CutUnit
{
    public string id = "";
    public string kind = "subtree";          // subtree: detach everything skinned to the bone subtree; split: split the subtree's mesh down a plane, halves stay attached; wound: no cut, blood only
    public string[] bones = new string[0];   // root bone of the cut (first one found)
    public string[] parentBones = new string[0];   // bone the piece hangs from (jet emitter + stump); "" = the bone's own parent
    public int loc = 3;                      // Loc of the wound that the gore shows
    public float massKg = 5f, colliderRadiusM = 0.1f, openDeg = 14f, splitOffsetM = 0f;
    public string splitAxis = "x";           // bone-local axis the split plane is perpendicular to
    public float jetSeconds = 1.0f;
    // gib physics + blood (all data): torque scale, upward bias and sideways scatter of the throw, angular drag, piece lifetime (0 = stays), extra decals splashed on the body at the cut
    public float torqueMul = 0.5f, upBias = 0.45f, scatter = 0.3f, angularDrag = 0.5f, lifeSeconds = 0f;
    // FINFIX v93 (V3 severed-head numbers): > 0 = the piece is thrown with these VELOCITIES instead of the impulse (launchSideMps horizontally away from the killer, launchUpMps up, spinRadS about the blow heading); 0 = the old impulse / torque
    public float launchSideMps = 0f, launchUpMps = 0f, spinRadS = 0f;
    public int extraDecals = 8;
    public ArcCfg arc = new ArcCfg();         // V1FIN v94: V1's scripted severed-head arc (severed-head.ts); used instead of the Rigidbody while rt.v1.enabled && rt.v1.headArc and arc.enabled
    public ScriptCfg script = new ScriptCfg();   // V1FIN v94: V1's opened.ts schedule for the upper half of a waist cut (kind subtree)
    public CrownCfg crown = new CrownCfg();   // V1FIN v94: kind "crown" = V1's head-only split (skull.ts)
    public string bloodProfile = "";          // V1FIN v94: id in gore.v1blood (V1 finisher-blood.ts timing / sources); "" = the old neckSpray / woundSpray
    public string cleave = "", burst = "";   // V63G: ids in gore.cleaves / gore.bursts (a dark wound line + patches + drips on the head, and a bigger blood burst, on top of the cut; FinisherCleave)
}

[Serializable]
public class CutsCfg
{
    public bool enabled = true;
    public float weightMin = 0.5f, pieceImpulseScale = 0.2f, weldM = 0.0002f;
    public float[] capColor = { 0.435f, 0.114f, 0.114f, 1f };   // flesh
    public float[] innerColor = { 0.165f, 0.043f, 0.043f, 1f };
    public CutUnit[] units = new CutUnit[0];
    public CapMaterialCfg capMaterial = new CapMaterialCfg();   // V64F: the material of the cut faces (red, wet, dark, non-glowing); enabled = false keeps the legacy capColor / innerColor clone
}

// V64F: gore cap material. shader "" = clone of the cut renderer's own material (pipeline keywords kept) with every texture removed, subsurface / transmission switched off, emission black,
// colour + smoothness from here, double sided (a flipped cap face never shows the inside of the mesh); a shader name (e.g. "HDRP/Lit") builds a fresh material instead.
[Serializable]
public class CapMaterialCfg
{
    public bool enabled = true;
    public float[] color = { 0.2f, 0.025f, 0.03f, 1f }, innerColor = { 0.09f, 0.012f, 0.015f, 1f };
    public float smoothness = 0.78f, metallic = 0f;
    public bool doubleSided = true, clearSubsurface = true;
    public string shader = "";
}

// ---------------- rotation + real-time settings ----------------
// Roster sides of a finisher: side 0 = the roster player, side 1 = the roster enemy. The killer weapon comes from roster.json ("weapon" of that character row).
public static class FinisherSides
{
    public static string IdOf(int side) { return side == 0 ? Roster.PlayerId : (ZoneRuntime.FoeId.Length > 0 ? ZoneRuntime.FoeId : Roster.EnemyId); }   // TWOFIGHT v1: in a zone run the enemy side is the player's current target
    public static string WeaponOf(int side) { return Roster.Get(IdOf(side)).weapon; }
    // finishers.json attackerSide: "any" / "" = both sides, "player" / "enemy" = that roster side, anything else = a character id (old files named the character)
    public static bool Matches(string attackerSide, int side)
    {
        if (string.IsNullOrEmpty(attackerSide) || attackerSide == "any") return true;
        if (string.Equals(attackerSide, "player", StringComparison.OrdinalIgnoreCase)) return side == 0;
        if (string.Equals(attackerSide, "enemy", StringComparison.OrdinalIgnoreCase)) return side == 1;
        return string.Equals(attackerSide, IdOf(side), StringComparison.OrdinalIgnoreCase);
    }
    // the only side an attackerSide allows, or -1 when both sides may attack
    public static int OnlySide(string attackerSide)
    {
        bool p = Matches(attackerSide, 0), e = Matches(attackerSide, 1);
        return p == e ? -1 : (p ? 0 : 1);
    }
}
// JOANFIN v77: per killer / victim overrides of an entry (finishers.json entries[].overrides, rt.killers). Everything keyed by roster id, no names in code.
public static class FinisherPair
{
    static bool Is(string pat, string id) { return string.IsNullOrEmpty(pat) || pat == "*" || string.Equals(pat, id, StringComparison.OrdinalIgnoreCase); }
    static bool Exact(string pat, string id) { return !string.IsNullOrEmpty(pat) && pat != "*" && string.Equals(pat, id, StringComparison.OrdinalIgnoreCase); }
    /// <summary>The best row for this pair: exact killer+victim, then exact killer, then exact victim, then "*" / "*"; null = none.</summary>
    public static FinisherOverride Find(FinisherEntry e, string killerId, string victimId)
    {
        if (e == null || e.overrides == null) return null;
        FinisherOverride best = null; int bs = -1;
        foreach (var o in e.overrides)
        {
            if (o == null || !Is(o.killer, killerId) || !Is(o.victim, victimId)) continue;
            int s = (Exact(o.killer, killerId) ? 2 : 0) + (Exact(o.victim, victimId) ? 1 : 0);
            if (s > bs) { bs = s; best = o; }
        }
        return best;
    }
    public static bool Available(FinisherEntry e, string killerId, string victimId, bool deathblow)
    {
        var o = Find(e, killerId, victimId); if (o == null) return true;
        return !o.unavailable && (!o.requireDeathblow || deathblow);
    }
    public static float KillerScale(string killerId)
    {
        var rows = FinisherConfig.Get().rt.killers; if (rows == null) return 1f;
        foreach (var r in rows) if (r != null && string.Equals(r.id, killerId, StringComparison.OrdinalIgnoreCase)) return Mathf.Max(0.3f, r.pairScale);
        foreach (var r2 in rows) if (r2 != null && r2.id == "*") return Mathf.Max(0.3f, r2.pairScale);   // HCKIT2 v93 "*" default killer
        return 1f;
    }
    /// <summary>A copy of the entry with the matching override applied (the loaded entry is never changed); the entry itself when nothing matches.</summary>
    public static FinisherEntry Resolve(FinisherEntry e, string killerId, string victimId)
    {
        var o = Find(e, killerId, victimId); if (o == null) return e;
        var r = e.Clone();
        if (o.pairOffset != null && o.pairOffset.Length >= 3) r.pairOffset = o.pairOffset;
        if (o.distance >= 0f) r.distance = o.distance;
        if (o.bladeReach >= 0f) r.bladeReach = o.bladeReach;
        if (o.contactAt >= 0f) r.contactAt = o.contactAt;
        if (o.killerYawDeg > -998f) r.killerYawDeg = o.killerYawDeg;
        if (o.victimYawDeg > -998f) r.victimYawDeg = o.victimYawDeg;
        if (o.shotDefs != null && o.shotDefs.Length > 0)
        {
            var list = new System.Collections.Generic.List<FinisherShot>(e.shotDefs ?? new FinisherShot[0]);
            foreach (var s in o.shotDefs) { if (s == null) continue; int i = list.FindIndex(x => x != null && x.id == s.id); if (i >= 0) list[i] = s; else list.Add(s); }
            r.shotDefs = list.ToArray();
        }
        return r;
    }
}

[Serializable]
public class FinisherRotation
{
    public string mode = "sequence";        // "sequence" (entry order, wrapping) | "random" (seeded, weighted)
    public int seed = 20261104;
    public bool noImmediateRepeat = true;
    public string fallbackEntry = "";       // entry id used when nothing matches ("" = the normal death)
    public string[] enabledIds = new string[0];   // V61B: non-empty = ONLY these entry ids may be picked by the rotation (the other entries stay in the file for ForceId / -finisherauto / ById)
    public bool perWeapon = true;           // V62: the rotation position is kept per killer weapon (roster.json "weapon" of the killer), persisted in finisher_rotation.json
}

[Serializable]
public class FinisherAutoCfg
{
    public float bootWaitSeconds = 20f;      // wait for both fighters after a scene (re)load
    public float killWaitSeconds = 20f;      // seconds of real fighting before the lethal blow is forced
    public float forceExtraSeconds = 8f;     // after the forced blow, wait this long for the scene to start
    public float sceneMaxSeconds = 40f;      // unscaled, one finisher scene
    public float betweenSeconds = 1.0f;      // pause after the end before the restart
    public float perFinisherSeconds = 90f;   // total timeout = bootWait + n * this
    public int shotsPerFinisher = 4;
    public float shotStartDelay = 0.35f;     // shot 1: seconds after the scene starts
    public float shotAfterContact = 0.9f;    // shot 3: seconds after the contact event
    public float shotAfterKill = 0.8f;       // shot 4: seconds after the victim's Die()
    public float aggression = 1.3f, pressure = 1f, parry = 0f, dodge = 0f;   // the attacking fighter's AI profile (the aggressive steer)
    public string shotMode = "auto";         // auto | screen | camera
    // V61B contact sheets (finisher_contact_<id>.png + finisher_contact_all.png in the out dir): frames at start / contact / kill / end of hold / after the release
    public int sheetCols = 3, sheetCellW = 640, sheetCombinedW = 960;
    public float sheetMaxSeconds = 30f;      // unscaled, per finisher incl. the frames after the release
    public bool pairProbe = true;            // V62: log hand-to-victim-body probe lines at the contact (summary NOTE lines)
    // V61C: length / luma / clearance checks of -finisherauto (all data)
    public float maxFinisherSeconds = 7f;       // a finisher that runs longer (paced seconds, capture stalls excluded) FAILS the run; 0 = no limit
    public float lumaTolerance = 0.15f;      // every finisher frame's mean luma must be within this fraction of the gameplay frames' mean luma; 0 = no check
    public bool lumaFails = true;            // false = the luma check only warns
    public int lumaRefFrames = 3;            // gameplay frames (before the scene starts) averaged for the reference
    public string clearKillerBone = "Head", clearVictimBone = "Spine2";   // clearance probe at the contact (bone-name suffixes; Spine and Spine1 of the victim are checked too)
    public float clearChestRadiusM = 0.30f;  // estimated chest half thickness of the victim at scale 1 (x victim scale); clearance = bone distance - this
    public float minClearanceM = 0.10f;      // clearance below this = WARN with a suggested pairOffset
    public ContactFrame[] sheetFrames = ContactFrame.Defaults();
    // V62B: the severed piece (head) must still be there at the end of the scene: alive, visible, resting on the ground (|gap| <= pieceMaxGapM), and the body has no head triangles left
    public bool pieceCheck = true; public float pieceMaxGapM = 0.15f;
    // V63G: after the scene the finisher vignette override must be gone: vignetteMaxAfter = max |effective vignette after - before| (or the absolute value when vignetteAbsolute), vignetteWaitS = how long the run waits for the fade
    public float vignetteMaxAfter = 0.01f, vignetteWaitS = 2f; public bool vignetteAbsolute = false;
    public int pieceMaxRescues = 3;      // V62C: the run fails when a severed piece needed MORE safety rescues than this (the physics should keep it above the floor on its own)
}

[Serializable]
public class FinisherRtCfg
{
    public bool onlyWhenSteer = false;       // true: only attempted when steer mode is active (-steer)
    public bool allowPlayerVictim = true;    // the enemy finishing the player is allowed
    public bool persistRotation = true;      // finisher_rotation.json in persistentDataPath
    public bool notify = true;               // feed line "Finisher: <id>" in steer mode
    public bool disableMotionBlur = true;    // a global high-priority Volume forces MotionBlur off while a scene runs
    public bool deathblowFinishes = false;   // V61B: true = a posture-break deathblow ALWAYS starts a finisher (even when it would not lower HP to zero); the scene then kills. FINFIX v93: default FALSE = V3 behaviour (a finisher only on a real kill; the deathblow is a big hit)
    public float deathblowFinishHpFrac = -1f;   // FINFIX v93: only with deathblowFinishes true: -1 = every deathblow, otherwise only when the HP left after the hit is <= this fraction of max HP
    public float maxStepS = 0.05f;           // FINFIX v93: largest time step (s) the finisher clocks (scene T, camera blends / shake, slow-mo, hold) take in one frame; one long frame cannot skip a beat. 0 = off
    public float contactShake = 0.08f, contactShakeS = 0.25f;   // FINFIX v93: camera shake (m, seconds) at the contact (was a hard-coded 0.03)
    public float contactHoldMs = 130f;       // FINFIX v93: animation-only hold at the contact (real ms, both Animators stopped; the clock, camera and effects keep running; no Time.timeScale). 0 = off
    public bool hpToZeroAtContact = true;    // FINFIX v93: the victim's HP bar goes to 0 at the contact (not at Die()); the kill itself is still the scene's Die()
    public V1FinCfg v1 = new V1FinCfg();         // V1FIN v94: switches + provenance numbers of the V1 finishers port
    public PrecutCfg precut = new PrecutCfg();   // FINFIX v93: the mesh cut is built in slices under the wind-up and only applied at the contact
    public bool offInBalanceAuto = true;     // V61B: -balanceauto runs keep the plain death (no cinematic) unless -finisherauto forces it
    public float maxSeconds = 25f;           // wall-clock watchdog of one scene (unscaled)
    public string forceId = "";              // debug: always play this entry id (empty = rotation)
    public string fallbackShot = "kill";     // shot id when an entry has no shot at t <= 0 (unknown id = built-in orbit)
    public FinisherKillerRow[] killers = new FinisherKillerRow[0];   // JOANFIN v77: per killer roster id pair scale (FinisherPair.KillerScale)
    public FinisherAutoCfg auto = new FinisherAutoCfg();   // -finisherauto harness data (FinisherAuto.cs)
    public FinisherLookCfg look = new FinisherLookCfg();   // V61C: finisher camera keeps the gameplay look (FinisherLook.cs)
    public FinisherPairCfg pair = new FinisherPairCfg();               // V64F: interpenetration probe of the two fighters (FinisherPairProbe.cs)
    public VictimLookCfg victimLook = new VictimLookCfg();             // V64F: victim material luma probe + stuck-flash guard (FinisherVictimLook.cs)
    public FinisherGroundCfg ground = new FinisherGroundCfg();   // V62B: foot-to-ground probe + correction during a scene (FinisherGround.cs)
}

// FINFIX v93: MeshCut.Prepare is time-sliced from the scene start; the cut is applied at its gore key (a cheap swap). Not finished by then = it is finished synchronously (logged).
[Serializable]
public class V1FinCfg
{
    public bool enabled = true;      // master: false = the FINFIX v93 behaviour exactly
    public bool camera = true;       // entry.shotsV1 (front-hold + slide; late 45 degree reveal) instead of entry.shots
    public bool timing = true;       // entry.goreV1 (head pops 5 % into the victim clip) instead of entry.gore
    public bool headArc = true;      // cuts.units[].arc (scripted ballistics) instead of the Rigidbody
    public bool crown = true;        // head-only V1 split (unit head_crown) instead of the body-mesh MeshCut split (unit head_split)
    public bool clips = true;        // entry.clipsV1 (V1's authored victim / killer clips) when the Animator states exist
    public bool blood = true;        // gore.v1blood profiles (finisher-blood.ts timing) on units with a bloodProfile
    public bool bladeAim = true;     // V1CLIPS v98: entry.bladeAim (V1 aimBladeAt: blade through the chest)
    public bool openedExtras = true; // V1CLIPS v98: cuts.units[torso_waist].script.weapon / .legs (dropped weapon, leg topple)
    public string crownUnit = "head_crown", crownReplaces = "head_split";
}

[Serializable]
public class PrecutCfg
{
    public bool enabled = true;
    public float startAt = 0.05f;            // scene seconds when the prepare job starts (after the hard cut to the first shot)
    public float budgetMs = 8f;              // CPU ms per frame the job may use
    public float catchUpBudgetMs = 30f;      // budget once the contact is closer than catchUpBeforeContactS
    public float catchUpBeforeContactS = 0.5f;
    public int checkEvery = 512;             // loop iterations between two stopwatch checks
}

// ---------------- the file ----------------
[Serializable]
public class FinisherFile
{
    public int version = 1; public string note = "";
    public bool enableFinishers = false;     // master flag, default OFF
    public bool enableGore = true;           // mesh cuts + blood at the cut
    public FinisherRotation rotation = new FinisherRotation();
    public FinisherEntry[] entries = new FinisherEntry[0];
    public CutsCfg cuts = new CutsCfg();
    public GoreCfg gore = new GoreCfg();
    public FinisherRtCfg rt = new FinisherRtCfg();
}

public static class FinisherConfig
{
    static FinisherFile data;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; }

    public static FinisherFile Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; }
    public static bool EnableFinishers { get { return Get().enableFinishers; } }
    public static bool EnableGore { get { return Get().enableGore; } }

    static string Read()
    {
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "finishers.json"); if (File.Exists(p)) return File.ReadAllText(p);
            p = Path.Combine(Application.dataPath, "Data/finishers.json"); if (File.Exists(p)) return File.ReadAllText(p);
        }
        catch (Exception e) { Debug.LogWarning("[finisher] finishers.json unreadable: " + e.Message); }
        return null;
    }

    static void Load()
    {
        data = null;
        string json = Read();
        if (!string.IsNullOrEmpty(json))
        {
            try { data = JsonUtility.FromJson<FinisherFile>(json.TrimStart((char)0xFEFF)); }
            catch (Exception e) { Debug.LogError("[finisher] finishers.json parse failed, finishers stay off: " + e.Message); }
        }
        if (data == null) data = new FinisherFile();
        if (data.rotation == null) data.rotation = new FinisherRotation();
        if (data.entries == null) data.entries = new FinisherEntry[0];
        if (data.cuts == null) data.cuts = new CutsCfg();
        if (data.cuts.units == null) data.cuts.units = new CutUnit[0];
        if (data.gore == null) data.gore = new GoreCfg();
        if (data.gore.locs == null) data.gore.locs = new GoreLocRow[0];
        if (data.gore.woundSpray == null) data.gore.woundSpray = new SprayCfg();
        if (data.gore.neckSpray == null) data.gore.neckSpray = new SprayCfg();
        if (data.gore.piece == null) data.gore.piece = new PieceCfg();
        if (data.rt == null) data.rt = new FinisherRtCfg();
        if (data.rt.auto == null) data.rt.auto = new FinisherAutoCfg();
    }
}
