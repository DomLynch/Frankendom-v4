using System;
using System.IO;
using UnityEngine;

// Alpha step 1, lane C: data shapes + loader for the "camera" and "finishers" blocks of Assets/Data/duel_rules.json.
// Lookup order like Combat\DodgeTable.cs: Resources/duel_rules, StreamingAssets/duel_rules.json, Application.dataPath/Data/duel_rules.json.
// If the file has no usable block, a standalone Assets/Data/ad_camera.json / finishers.json (same top-level key) is tried. Code holds only fallbacks.

// ---------------- camera ----------------
// One camera "shot" = an orbit pose around a focus point of the actor/target pair, evaluated live every frame.
[Serializable]
public class ADShot
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
}
[Serializable] public class ADShotKey { public float t = 0f; public string rel = "start"; public string shot = ""; public float blend = -1f; }       // rel: "start" | "contact"
[Serializable] public class ADSlowKey { public float t = 0f; public string rel = "contact"; public float scale = 0.25f, duration = 0.6f, easeIn = 0.05f, easeOut = 0.25f; }   // duration in UNSCALED seconds
[Serializable] public class ADOutcomeShot { public string kind = ""; public string shot = ""; public string critShot = ""; }                               // kind: Land Blocked Parried Dodged GuardBreak Clash Whiff Riposte

[Serializable]
public class ADPlaybackCfg
{
    public float engageDist = 2.1f;              // centre distance of the pair between beats (m), along the lane axis
    public float repositionSeconds = 1.6f;       // max time spent walking back to the staging spots
    public float repositionTolerance = 0.18f;    // m
    public float snapTolerance = 0.5f;           // anything closer than this after the timeout is snapped (CharacterController off/on)
    public int leadTicks = 8;                    // sim ticks before the first press of a beat
    public int tradeGapTicks = 5;                // the second landing hit of a beat comes this many ticks after the first
    public int clashGapTicks = 0;
    public int blockLeadTicks = 22;              // a blocking guard goes up this long before the contact (above the parry window, so it reads as a block)
    public int parryLeadTicks = 4;               // a parrying guard goes up this long before the contact (inside the parry window)
    public int dodgeLeadExtra = 3;               // dodge starts at contact - iframeStart - this
    public int parryStaggerTicks = 55, blockKnockTicks = 12, guardBreakTicks = 60, hitStaggerLight = 22, hitStaggerHeavy = 40, critStaggerExtra = 20, clashStaggerTicks = 40, kickStaggerTicks = 45, whiffStaggerTicks = 0;
    public int riposteDelayTicks = 14;           // after a parry the defender presses the riposte this late
    public float knockLight = 0.5f, knockHeavy = 1.1f, knockCrit = 1.6f;   // metres of shove on a hit (scaled by the move)
    public float settleSeconds = 0.35f;          // after everything is idle, before the camera returns to the plan view
    public float maxBeatSeconds = 7f;            // timeout of one beat
    public float critSlowLeadTicks = 10f;        // crit slow-mo starts this many sim ticks before the contact
    public float healthPad = 100000f;            // DuelFighter.health during a beat (the old damage code never kills; the rules own the real HP)
    public string heavyMove = "heavy_overhead", lightRightMove = "light_right", lightLeftMove = "light_left", stabMove = "thrust", kickMove = "kick", special1Move = "special1", special2Move = "special2";
    public string slashSide = "left";            // which light clip Slash uses ("left"|"right")
    public string hitLightClip = "hit_light_front", hitBackClip = "hit_light_back", hitHeavyClip = "hit_heavy", staggerClip = "stagger_loop";
    public bool useFinishers = true;
    public float idleWaitSeconds = 3f;           // max wait for both fighters to be free before a beat's presses (a knocked-down fighter needs 1.5 s to get up)
    public float finisherMaxSeconds = 20f;       // watchdog of the whole finisher scene
}

[Serializable]
public class ADCameraCfg
{
    public string planShot = "plan";             // over-the-shoulder planning view behind Dracula looking at the Pitborn
    public string startShot = "wide34";          // first cut of every beat
    public string endShot = "plan";              // after the beat
    public string critShot = "crit";
    public string killShot = "kill";
    public ADShot[] shots = new ADShot[0];
    public ADOutcomeShot[] outcomeShots = new ADOutcomeShot[0];
    public ADSlowKey critSlow = new ADSlowKey { rel = "contact", scale = 0.2f, duration = 0.7f };
    public ADSlowKey parrySlow = new ADSlowKey { rel = "contact", scale = 0.35f, duration = 0.45f };
    public ADPlaybackCfg playback = new ADPlaybackCfg();
}

// ---------------- finishers ----------------
[Serializable] public class ADGoreKey { public float t = 0f; public string rel = "contact"; public string type = ""; public string cut = ""; public string bone = ""; public float strength = 1f; public string param = ""; }   // type: head_detach | skull_split | cut | blood_burst | custom
[Serializable]
public class ADFinisherEntry
{
    public string id = "";
    public bool enabled = true;
    public bool placeholder = false;        // true: uses stand-in clips (no paired clip yet); logged
    public float weight = 1f;
    public string attackerSide = "any";     // "any" | "Dracula" | "Pitborn"
    public string[] weapons = new string[0];    // attacker weapon ids ("sword","maul", ...); empty = any
    public string[] directions = new string[0]; // kill direction "front"|"back"|"side"; empty = any
    public string[] moves = new string[0];      // ADMove names of the killing blow; empty = any
    public string[] locations = new string[0];  // ADLoc names where the blow landed; empty = any
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
    public ADShot[] shotDefs = new ADShot[0];
    public ADShotKey[] shots = new ADShotKey[0];
    public ADGoreKey[] gore = new ADGoreKey[0];
    public ADSlowKey[] slowmo = new ADSlowKey[0];
    public float holdAfter = 1.6f;          // seconds (unscaled) the final frame stays before control returns
    public string note = "";
}
[Serializable]
public class ADFinishersCfg
{
    public bool enabled = true;
    public string rotation = "sequence";    // "sequence" (entry order, wrapping) | "random" (seeded, weighted)
    public int seed = 20261104;
    public bool noImmediateRepeat = true;
    public string playerWeapon = "sword", pitbornWeapon = "maul";
    public string fallbackEntry = "";       // entry id used when nothing matches ("" = the normal death)
    public ADFinisherEntry[] entries = new ADFinisherEntry[0];
}

[Serializable] class ADCRoot { public ADCameraCfg camera; public ADFinishersCfg finishers; }

public static class ADCConfig
{
    static ADCameraCfg cam; static ADFinishersCfg fin;
    public static ADCameraCfg Camera { get { if (cam == null) Load(); return cam; } }
    public static ADFinishersCfg Finishers { get { if (fin == null) Load(); return fin; } }
    public static void Reload() { cam = null; fin = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cam = null; fin = null; }

    static string ReadFirst(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                var ta = Resources.Load<TextAsset>(Path.GetFileNameWithoutExtension(n)); if (ta != null && !string.IsNullOrEmpty(ta.text)) return ta.text;
                string p = Path.Combine(Application.streamingAssetsPath, n); if (File.Exists(p)) return File.ReadAllText(p);
                p = Path.Combine(Application.dataPath, "Data/" + n); if (File.Exists(p)) return File.ReadAllText(p);
            }
            catch (Exception e) { Debug.LogWarning("[ADCConfig] read " + n + ": " + e.Message); }
        }
        return null;
    }

    static void Load()
    {
        cam = null; fin = null;
        foreach (var file in new[] { "duel_rules.json", "ad_camera.json", "finishers.json" })
        {
            string json = ReadFirst(file); if (string.IsNullOrEmpty(json)) continue;
            try
            {
                var r = JsonUtility.FromJson<ADCRoot>(json);
                if (r == null) continue;
                if (cam == null && r.camera != null && r.camera.shots != null && r.camera.shots.Length > 0) cam = r.camera;
                if (fin == null && r.finishers != null && r.finishers.entries != null && r.finishers.entries.Length > 0) fin = r.finishers;
            }
            catch (Exception e) { Debug.LogWarning("[ADCConfig] parse " + file + ": " + e.Message); }
        }
        if (cam == null) cam = new ADCameraCfg();
        if (cam.playback == null) cam.playback = new ADPlaybackCfg();
        if (cam.shots == null) cam.shots = new ADShot[0];
        if (cam.outcomeShots == null) cam.outcomeShots = new ADOutcomeShot[0];
        if (fin == null) fin = new ADFinishersCfg { enabled = false };
        if (fin.entries == null) fin.entries = new ADFinisherEntry[0];
    }

    public static ADShot Shot(string id, ADShot[] extra = null)
    {
        if (extra != null) foreach (var s in extra) if (s != null && s.id == id) return s;
        foreach (var s in Camera.shots) if (s != null && s.id == id) return s;
        return null;
    }
    public static ADOutcomeShot OutcomeShot(string kind)
    {
        foreach (var o in Camera.outcomeShots) if (o != null && o.kind == kind) return o;
        return null;
    }
}
