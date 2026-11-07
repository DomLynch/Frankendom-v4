using System;
using UnityEngine;

// LOCODIR v1 (laneMONSTERLOCO): per-character locomotion by the REAL direction of travel + Animator speed matched to the body speed. Data: Assets/Data/loco_dir.json (StreamingAssets in the player). OFF by default.
// Presentation only: reads a transform position, returns an Animator state name and an Animator speed. It never writes a sim field, moveDir, a speed, a position or a rotation; nothing in the sim reads the result.
// Why: DuelFighter.Update picks strafe vs walk with one test (|x| > 0.9 |z|, never while run is held) and plays every clip at animWeight, so a fighter that moves BACK or diagonally plays the FORWARD
// walk / run clip, and every clip plays at speed 1 whatever the body does (feet slide, worst on a big monster whose walk is slower than the clip).
// A character with NO row in loco_dir.json (or enabled false anywhere) gets exactly today's choice: Choose returns the clip it was given and Speed returns the speed it was given.
// Standalone: needs only HudCfg.ReadData and Roster.IdOf; the STRAFE (StrafeLock) and RUNSLIDE (LocoMatch) kits are NOT required and do not conflict (LocoDir runs last and owns the speed of a row character).
public static class LocoDir
{
    [Serializable] public class PickCfg { public float forwardMaxDeg = 48f, backMinDeg = 132f, hysteresisDeg = 8f; }
    // state = Animator state name ("" = no clip of its own: today's choice, and no speed override). stride = world m/s the clip moves a planted foot at Animator speed 1 and model scale 1.
    // cycleS = clip length of one full step cycle in seconds at speed 1 (information for the proof, the math does not use it).
    [Serializable] public class Gait { public string state = ""; public float stride = 0f, cycleS = 1f, minAnim = 0.5f, maxAnim = 1.6f; }
    [Serializable] public class Gaits { public Gait walk = new Gait(), run = new Gait(), back = new Gait(), strafeL = new Gait(), strafeR = new Gait(); }
    [Serializable] public class CharRow { public string id = ""; public bool enabled = true; public string modelScaleSource = "anim"; public Gaits gaits = new Gaits(); }   // modelScaleSource: "anim" = Animator transform lossyScale.y, "root" = DuelFighter.scale
    [Serializable] class Root { public bool enabled = false, debug = false, pickWhileGuarding = false; public float sampleS = 0.1f, maxChangePerS = 3f, idleBelowMps = 0.05f; public PickCfg pick = new PickCfg(); public CharRow[] characters = new CharRow[0]; }

    public const int Forward = 0, Right = 1, Left = 2, Back = 3;
    public const string GWalk = "walk", GRun = "run", GBack = "back", GStrafeL = "strafeL", GStrafeR = "strafeR", ScaleRoot = "root";   // keys of the json "gaits" object / modelScaleSource
    const float MinStride = 0.01f, MinScale = 0.01f;   // hc:ok divide guards (a stride or a model scale below 1 cm is "not set")
    const float GapDtMul = 2f, TeleportM = 3f, ReResolveS = 2f, MinWindowS = 0.02f, LogEveryS = 1f;   // hc:ok measuring guards: a skipped frame (x dt) restarts the window, a jump over 3 m is a teleport, the roster id is re-read every 2 s, a sample window is never shorter than 20 ms, the debug line is printed once per second

    static Root f; static bool loaded;
    static void Load()
    {
        if (loaded) return; loaded = true;
        LoadText(HudCfg.ReadData("loco_dir.json"));
    }
    /// <summary>Parse a loco_dir.json text (also used by proofs). Null / bad text = file absent = LocoDir off.</summary>
    public static void LoadText(string txt)
    {
        loaded = true; f = null;
        if (txt == null) return;
        try { f = JsonUtility.FromJson<Root>(txt); } catch (Exception e) { Debug.LogWarning("[LocoDir] loco_dir.json parse failed: " + e.Message); f = null; }
        if (f == null) return;
        if (f.pick == null) f.pick = new PickCfg();
        if (f.characters == null) f.characters = new CharRow[0];
    }
    public static bool Enabled { get { Load(); return f != null && f.enabled; } }

    /// <summary>The row of a character id, or null when LocoDir is off, the id has no row, or the row is disabled.</summary>
    public static CharRow Row(string charId)
    {
        Load(); if (f == null || !f.enabled || string.IsNullOrEmpty(charId)) return null;
        for (int i = 0; i < f.characters.Length; i++) { var c = f.characters[i]; if (c != null && c.enabled && c.id == charId) return c; }
        return null;
    }
    public static bool HasRow(string charId) { return Row(charId) != null; }

    public static Gait GaitOf(CharRow r, string gait)
    {
        if (r == null || r.gaits == null || gait == null) return null;
        switch (gait)
        {
            case GWalk: return r.gaits.walk;
            case GRun: return r.gaits.run;
            case GBack: return r.gaits.back;
            case GStrafeL: return r.gaits.strafeL;
            case GStrafeR: return r.gaits.strafeR;
        }
        return null;
    }

    // Signed angle (deg) of a direction in the fighter's local frame: 0 = forward, + = right, +-180 = straight back.
    public static float AngleOf(Vector3 local) { return Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg; }

    // The sector of an angle with hysteresis: `sector` is the previous result (Forward at the start). Pure.
    public static int SectorOf(float angleDeg, int sector, PickCfg P)
    {
        float a = Mathf.Abs(angleDeg), h = Mathf.Max(0f, P.hysteresisDeg);
        float fwdLim = P.forwardMaxDeg + (sector == Forward ? h : -h);   // leave Forward only past the edge + h, enter it only inside the edge - h
        float backLim = P.backMinDeg + (sector == Back ? -h : h);        // enter Back only past the edge + h, leave it only inside the edge - h
        if (a < fwdLim) return Forward;
        if (a > backLim) return Back;
        return angleDeg > 0f ? Right : Left;
    }

    /// <summary>The gait key of a sector: run only for the forward sector (and only while runNow), strafe and back keep their own gait.</summary>
    public static string GaitKey(int sector, bool runNow)
    {
        return sector == Right ? GStrafeR : sector == Left ? GStrafeL : sector == Back ? GBack : (runNow ? GRun : GWalk);
    }

    /// <summary>Animator state for a moving fighter. `local` = direction of travel in the fighter's frame, `locked` = it faces its target (else it faces its move direction: sector Forward),
    /// `fallback` = today's choice (returned for no row / empty state). `gait` = the gait key whose speed rule applies, or null (no speed override).</summary>
    public static string Pick(string charId, Vector3 local, bool locked, bool runNow, string fallback, ref int sector, out string gait)
    {
        return PickRow(Row(charId), local, locked, runNow, fallback, ref sector, out gait);
    }
    static string PickRow(CharRow r, Vector3 local, bool locked, bool runNow, string fallback, ref int sector, out string gait)
    {
        gait = null; if (r == null) return fallback;
        sector = locked ? SectorOf(AngleOf(local), sector, f.pick) : Forward;
        string key = GaitKey(sector, runNow); var g = GaitOf(r, key);
        if (g == null || string.IsNullOrEmpty(g.state)) return fallback;   // no clip of its own for this sector: today's choice, speed untouched
        gait = key; return g.state;
    }

    /// <summary>Animator speed = clamp(bodySpeed / (stride x modelScale), minAnim, maxAnim) for a gait; `current` when off / no row / no stride / unknown speed. Never negative.</summary>
    public static float AnimSpeed(string charId, string gait, float bodySpeed, float modelScale, float current)
    {
        return AnimSpeedRow(Row(charId), gait, bodySpeed, modelScale, current);
    }
    static float AnimSpeedRow(CharRow r, string gait, float bodySpeed, float modelScale, float current)
    {
        var g = GaitOf(r, gait);
        if (g == null || string.IsNullOrEmpty(g.state) || g.stride < MinStride || modelScale < MinScale || bodySpeed < 0f) return current;
        return Mathf.Clamp(bodySpeed / (g.stride * modelScale), Lo(g), Hi(g));
    }
    static float Lo(Gait g) { return Mathf.Max(0f, g.minAnim); }
    static float Hi(Gait g) { return Mathf.Max(Lo(g), g.maxAnim); }

    /// <summary>One per fighter: measures its horizontal velocity from transform deltas, picks the clip by the direction of travel and rate limits the Animator speed.</summary>
    public sealed class Meter
    {
        Vector3 prev, acc, vel; float t, speed = -1f, anim = -1f, lastNow = -99f, nextResolve = -99f; bool has, haveVel, guard;
        int sector, chosenFrame = -1, appliedFrame = -1; float nextLog; string id = "", gait; CharRow row;
        /// <summary>True when LocoDir set the Animator speed THIS frame (ChaseAgent then leaves its own multiplier off).</summary>
        public bool Applied { get { return appliedFrame == Time.frameCount; } }
        public float BodySpeed { get { return speed; } }
        public int Sector { get { return sector; } }
        public string GaitNow { get { return gait; } }
        public void Reset() { has = false; haveVel = false; acc = Vector3.zero; t = 0f; speed = -1f; anim = -1f; sector = Forward; }

        void Sample(Vector3 pos, float now, float dt)
        {
            if (has && now - lastNow > GapDtMul * dt + 1e-4f) Reset();   // gap (attack, stagger, pause, a frame skipped): do not measure across it
            lastNow = now;
            float win = Mathf.Max(MinWindowS, f.sampleS);   // f is set: Sample runs from Choose only after its null check
            if (has && dt > 1e-4f)
            {
                Vector3 d = pos - prev; d.y = 0f;
                if (d.magnitude < TeleportM) { acc += d; t += dt; if (t >= win) { vel = acc / t; speed = vel.magnitude; haveVel = true; acc = Vector3.zero; t = 0f; } }
                else { acc = Vector3.zero; t = 0f; }
            }
            prev = pos; has = true;
        }

        /// <summary>Call once per rendered frame while the fighter is moving. Returns the Animator state for the real direction of travel, or `fallback` (today's clip) for no row / empty state / guard-walk.</summary>
        public string Choose(Component who, Transform body, Vector3 moveDir, bool locked, bool runNow, bool guarding, string fallback, float now, float dt)
        {
            gait = null; guard = guarding; chosenFrame = Time.frameCount;
            Load(); if (f == null || !f.enabled) return fallback;
            if (now >= nextResolve) { id = Roster.IdOf(who); row = Row(id); nextResolve = now + ReResolveS; }
            if (row == null) { has = false; return fallback; }
            Sample(body.position, now, dt);
            if (guarding && !f.pickWhileGuarding) return fallback;   // guard-walk: today's clip and today's legAnimSpeed
            Vector3 dir = haveVel ? vel : moveDir; dir.y = 0f;        // real velocity; moveDir only until the first window is full
            if (haveVel && speed < f.idleBelowMps)                    // pushed against a wall: the forward gait at its slowest, whatever moveDir says
            {
                sector = Forward; string key = runNow ? GRun : GWalk; var g = GaitOf(row, key);
                if (g == null || string.IsNullOrEmpty(g.state)) return fallback;
                gait = key; return g.state;
            }
            return PickRow(row, body.InverseTransformDirection(dir), locked, runNow, fallback, ref sector, out gait);
        }

        /// <summary>Call once per rendered frame after Choose and after f.Play. `current` = the speed today's code set. Returns the speed to use (rate limited), or `current` unchanged when LocoDir does not own it.</summary>
        public float Speed(float current, float dt, Transform animT, float rootScale)
        {
            if (f != null && f.debug && row != null && chosenFrame == Time.frameCount && Time.time >= nextLog) { nextLog = Time.time + LogEveryS; Debug.Log("[LocoDir] " + id + " sector " + sector + " anim " + anim.ToString("F2") + " bodySpeed " + speed.ToString("F2") + " clip " + (gait != null ? GaitOf(row, gait).state : "(today's)") + (guard ? " (guard-walk)" : "")); }   // data "debug": one line per second for the proof
            if (chosenFrame != Time.frameCount || gait == null || guard || row == null || current <= 0f || speed < 0f) { anim = -1f; return current; }
            var g = GaitOf(row, gait); if (g == null) { anim = -1f; return current; }
            float ms = string.Equals(row.modelScaleSource, ScaleRoot, StringComparison.Ordinal) || animT == null ? rootScale : animT.lossyScale.y;
            float want = haveVel && speed < f.idleBelowMps ? Lo(g) : AnimSpeedRow(row, gait, speed, ms, -1f);
            if (want < 0f) { anim = -1f; return current; }   // no stride for this gait: today's speed
            anim = anim < 0f ? current : Mathf.MoveTowards(anim, want, Mathf.Max(0f, f.maxChangePerS) * dt);
            anim = Mathf.Max(0f, anim); appliedFrame = Time.frameCount;
            return anim;
        }
    }
}
