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
    public DuelFighter killer, victim; public int killerSide, victimSide;   // 0 = Dracula, 1 = Pitborn
    public FinisherMove move; public FinisherLoc loc;
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
    public float bodyHeight = 2.6f;     // metres from the feet that must stay in frame (the Pitborn with horns)
    public float fitMargin = 1.15f;     // > 1 = air around the fighters
    public float minDist = 4f, maxDist = 7.5f;
    public float pushIn = 0f, pushSeconds = 0f;   // slow push-in: dist shrinks by pushIn metres over pushSeconds (unscaled) after the cut
}
[Serializable] public class FinisherShotKey { public float t = 0f; public string rel = "start"; public string shot = ""; public float blend = -1f; }       // rel: "start" | "contact"
[Serializable] public class FinisherSlowKey { public float t = 0f; public string rel = "contact"; public float scale = 0.25f, duration = 0.6f, easeIn = 0.05f, easeOut = 0.25f; }   // duration in UNSCALED seconds

[Serializable] public class FinisherGoreKey { public float t = 0f; public string rel = "contact"; public string type = ""; public string cut = ""; public string bone = ""; public float strength = 1f; public string param = ""; }   // type: head_detach | skull_split | cut | blood_burst | custom
[Serializable]
public class FinisherEntry
{
    public string id = "";
    public bool enabled = true;
    public bool placeholder = false;        // true: uses stand-in clips (no paired clip yet); logged
    public float weight = 1f;
    public string attackerSide = "any";     // "any" | "Dracula" | "Pitborn"
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
    public float dieAt = -2f;               // seconds when DuelFighter.Die() runs (ragdoll hook): -2 = at the contact when no victimClip, else at the clip end; -1 = never
    public bool holdLast = true;
    // placement (centre distance of the pair and who faces whom)
    public float distance = 1.7f;
    public float victimYawDeg = 0f;         // victim yaw relative to "facing the attacker"
    public bool moveWhole = false;          // attacker root motion allowed to slide the pair (not used yet)
    // FN2 pair sync for paired Mixamo clips (all stand-in metres, tuned in data; FinisherPlayer multiplies by the killer's DuelFighter.scale):
    public float[] pairOffset = new float[0];   // [right, up, forward] of the victim centre relative to the killer, in the killer's frame; empty = use `distance` straight ahead
    public float killerYawDeg = 0f;         // extra yaw of the killer GameObject so its clip's own facing points at the victim (clip facing offset)
    public bool distanceScaleByKiller = true;   // multiply distance / pairOffset by the killer's scale (Pitborn 1.19)
    public FinisherShot[] shotDefs = new FinisherShot[0];
    public FinisherShotKey[] shots = new FinisherShotKey[0];
    public FinisherGoreKey[] gore = new FinisherGoreKey[0];
    public FinisherSlowKey[] slowmo = new FinisherSlowKey[0];
    public float holdAfter = 1.6f;          // seconds (unscaled) the final frame stays before control returns
    public string note = "";
}
[Serializable] public class SprayCfg { public float seconds = 1.0f, rate = 90f, coneDeg = 16f, gravity = 8f; public float[] speed = { 2.5f, 4.5f }, size = { 0.03f, 0.06f }, life = { 0.5f, 0.9f }; }
[Serializable] public class GoreLocRow { public string name = "torso"; public string[] bones = new string[0]; public float radiusM = 0.12f; public float frontArcDeg = 360f; public float[] along = { 0.2f, 0.8f }; }
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
    public int extraDecals = 8;
}

[Serializable]
public class CutsCfg
{
    public bool enabled = true;
    public float weightMin = 0.5f, pieceImpulseScale = 0.2f, weldM = 0.0002f;
    public float[] capColor = { 0.435f, 0.114f, 0.114f, 1f };   // flesh
    public float[] innerColor = { 0.165f, 0.043f, 0.043f, 1f };
    public CutUnit[] units = new CutUnit[0];
}

// ---------------- rotation + real-time settings ----------------
[Serializable] public class RotWeapons { public string Dracula = "sword", Pitborn = "maul"; }
[Serializable]
public class FinisherRotation
{
    public string mode = "sequence";        // "sequence" (entry order, wrapping) | "random" (seeded, weighted)
    public int seed = 20261104;
    public bool noImmediateRepeat = true;
    public string fallbackEntry = "";       // entry id used when nothing matches ("" = the normal death)
    public RotWeapons weapons = new RotWeapons();
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
    public float aggression = 1.3f, pressure = 1f, parry = 0f, dodge = 0f;   // Dracula's AI profile (the aggressive steer)
    public string shotMode = "auto";         // auto | screen | camera
}

[Serializable]
public class FinisherRtCfg
{
    public bool onlyWhenSteer = false;       // true: only attempted when steer mode is active (-steer)
    public bool allowPlayerVictim = true;    // Pitborn finishing Dracula is allowed
    public bool persistRotation = true;      // finisher_rotation.json in persistentDataPath
    public bool notify = true;               // feed line "Finisher: <id>" in steer mode
    public bool disableMotionBlur = true;    // a global high-priority Volume forces MotionBlur off while a scene runs
    public float maxSeconds = 25f;           // wall-clock watchdog of one scene (unscaled)
    public string forceId = "";              // debug: always play this entry id (empty = rotation)
    public string fallbackShot = "kill";     // shot id when an entry has no shot at t <= 0 (unknown id = built-in orbit)
    public FinisherAutoCfg auto = new FinisherAutoCfg();   // -finisherauto harness data (FinisherAuto.cs)
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
        if (data.rotation.weapons == null) data.rotation.weapons = new RotWeapons();
        if (data.entries == null) data.entries = new FinisherEntry[0];
        if (data.cuts == null) data.cuts = new CutsCfg();
        if (data.cuts.units == null) data.cuts.units = new CutUnit[0];
        if (data.gore == null) data.gore = new GoreCfg();
        if (data.gore.locs == null) data.gore.locs = new GoreLocRow[0];
        if (data.gore.woundSpray == null) data.gore.woundSpray = new SprayCfg();
        if (data.gore.neckSpray == null) data.gore.neckSpray = new SprayCfg();
        if (data.rt == null) data.rt = new FinisherRtCfg();
        if (data.rt.auto == null) data.rt.auto = new FinisherAutoCfg();
    }
}
