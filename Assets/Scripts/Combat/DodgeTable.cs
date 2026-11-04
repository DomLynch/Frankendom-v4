using System;
using System.IO;
using UnityEngine;

// Data layer for the player's evasive moves and the moving guard: Assets/Data/dodge.json (copy in StreamingAssets for the player).
// Lookup order for the file (same as AnimBlend): Resources/dodge, StreamingAssets/dodge.json, Application.dataPath/Data/dodge.json.
// A missing or unparsable file keeps the code defaults below, which reproduce the old hard-coded behaviour (roll 36 ticks, backstep 12, guard speed 0.35 * 1.6).
// "enabled": false switches every row off and DuelFighter falls back to the V3 rules table (rules-defs.json) values.
public static class DodgeTable
{
    // One evasive move. Ticks are whole 60 Hz sim ticks. Distance is metres at fighter scale 1 (DuelFighter multiplies by its own scale).
    [Serializable]
    public class DodgeRow
    {
        public string clip = "";            // Animator state played (sidestep has one per direction below)
        public int ticks = 12;              // duration of the move
        public float distance = 1f;         // total travel in metres
        public int iframeStart = 0;         // invulnerable from this tick of the move (inclusive) ...
        public int iframeEnd = 0;           // ... to this tick (inclusive); end < start = no i-frames
        public float stamina = 0f;          // cost to start; the move is refused while stamina is below it
        public int cooldownTicks = 0;       // after the move ends, before ANY dodge may start again
        public float easePower = 1f;        // 1 = constant speed; 2 = fast start easing to rest (travel = 1-(1-x)^power)
        public float clipSpeed = 0f;        // > 0: fixed Animator speed; 0 = stretch the clip over ticks + clipTailTicks
        public int clipTailTicks = 0;       // extra ticks of clip length beyond the move (backstep plays its clip over 12 + 30)
        public float blendIn = 0f;          // P1: > 0 = crossfade seconds INTO the dodge clip (ForceExact, bypasses anim_blend.json); 0 = the old AnimBlend fade
        public string fxAction = "";        // name raised on ContactRelay.OnAction (audio/dust). "" = the move's own name
    }
    [Serializable]
    public class RollRow : DodgeRow
    {
        public string noInputDir = "back";  // roll with no move input: "back" (V3), "forward" (towards the facing) or "away" (away from the opponent)
        public float turnRate = 30f;        // body turn towards the roll direction (1/s, Slerp factor); the old fixed 12 turned too slowly for a 0.6 s roll
    }
    [Serializable]
    public class SidestepRow : DodgeRow
    {
        public string clipLeft = "strafe_L", clipRight = "strafe_R", clipBack = "backstep";
    }
    [Serializable]
    public class GuardRow
    {
        public float speedMul = 0.5f;                  // move speed while guarding, times walkSpeed (run is always off while guarding)
        public float staminaDrainPerSecond = 0f;       // OFF for now; > 0 drains stamina per second while the guard is up (0 stamina drops the guard)
        public bool upperBodyLayer = true;             // use the masked Animator layer below when it exists
        public string layer = "GuardUpper";            // Animator layer name (Assets/Editor/GuardLayerSetup.cs adds it)
        public float layerBlendIn = 0.12f;             // seconds for the layer weight to rise to 1
        public float poseFade = 0.12f;                 // crossfade seconds when the guard pose changes (centre / left / right / up)
        public bool runDropsGuard = true;              // holding run (Shift) while moving drops the guard (guard works at walk speed only)
        public float legAnimSpeed = 0.55f;             // Animator speed of the walk/strafe legs while guard-walking (body moves at speedMul * walkSpeed, so legs step slower: no foot slide)
        public string moveClipFallback = "guard_idle"; // no layer present: base clip used while moving in guard ("guard_idle" or "locomotion")
    }
    [Serializable]
    public class OnKeyRow { public string none = "roll", forward = "roll", lateral = "sidestep", back = "sidestep"; }   // values: roll | sidestep | backstep
    [Serializable]
    public class DoubleTapRow
    {
        public bool enabled = false;       // double tap of a move key (locked on) also sidesteps
        public float seconds = 0.25f;
        public string[] leftKeys = { "A" }, rightKeys = { "D" }, backKeys = { "S" };
    }
    [Serializable]
    public class InputRow
    {
        public string mode = "immediate";  // "immediate": the roll key decides on key-down; "tapHold": V3 legacy (tap = backstep, hold > holdSeconds = roll)
        public string[] rollKeys = { "E", "Space" };
        public float holdSeconds = 0.15f;  // tapHold mode only
        public float lateralThreshold = 0.5f;
        public bool requireLock = true;    // sidestep only while locked on (otherwise the key rolls)
        public OnKeyRow onKey = new OnKeyRow();
        public DoubleTapRow doubleTap = new DoubleTapRow();
    }
    [Serializable]
    public class Data
    {
        public bool enabled = true;
        public bool debugLog = false;
        public InputRow input = new InputRow();
        public RollRow roll = new RollRow { clip = "roll_forward", ticks = 36, distance = 3.2f, iframeStart = 4, iframeEnd = 20, stamina = 30f, fxAction = "roll" };
        public DodgeRow backstep = new DodgeRow { clip = "backstep", ticks = 12, distance = 1.4f, iframeStart = 4, iframeEnd = 20, stamina = 10f, clipTailTicks = 30, fxAction = "backstep" };
        public SidestepRow sidestep = new SidestepRow { ticks = 14, distance = 2f, iframeStart = 2, iframeEnd = 10, stamina = 8f, cooldownTicks = 18, easePower = 2f, clipSpeed = 2f, fxAction = "backstep" };
        public GuardRow guard = new GuardRow();
        public float flinchMaxLeanDeg = 12f;
        public float minSeparation = 1.2f, separationPad = 0.1f;   // fighters never closer than max(minSeparation, radii + pad) in metres, in every state (Strategy: bodies overlapped, sword through the head)
    }

    static Data data;
    public static Data Get() { if (data == null) Load(); return data; }
    public static void Reload() { data = null; Load(); }

    static void Load()
    {
        data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("dodge");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "dodge.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/dodge.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("DodgeTable: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("DodgeTable: dodge.json not found; code defaults in use."); return; }
        try
        {
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.input == null) d.input = new InputRow();
            if (d.input.onKey == null) d.input.onKey = new OnKeyRow();
            if (d.input.doubleTap == null) d.input.doubleTap = new DoubleTapRow();
            if (d.roll == null) d.roll = new RollRow();
            if (d.backstep == null) d.backstep = new DodgeRow();
            if (d.sidestep == null) d.sidestep = new SidestepRow();
            if (d.guard == null) d.guard = new GuardRow();
            data = d;
        }
        catch (Exception ex) { Debug.LogError("DodgeTable: parse failed: " + ex.Message); data = new Data(); }
    }
}


