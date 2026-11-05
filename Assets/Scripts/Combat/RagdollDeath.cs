using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// Death ragdoll (V3_TO_UNITY rows 3, 4, 8; V3 data/physics.json states.death + kill + motorTuning, data/hero-death.json ragdoll).
// PHYS1 v1 (PH3 P1): this class is now the DATA + HOOK facade; the rig itself lives in Scripts/Physics/RagdollRig.cs.
//   * the rig (capsule + Rigidbody + ConfigurableJoint per part) is PRE-BUILT at scene load, kinematic, capsules as Hurtbox-layer triggers, so death costs no hitch;
//   * at Killed the rig converts: bones inherit the velocity they had (finite difference of the last frames + the sim knock velocity), the blow impulse goes along the
//     attacker's facing at the zone part (70 N s + 6 lift, split over zone / spine2 / hips by ragdoll.json kill.split), the motor ramps 1 -> 0.01 over 1 s with damper x2,
//     the body settles when every part's speed^2 < 0.02 for 4 physics steps. Joint limits are V3's role table (spine 20/15 ... forearm / shin hinges).
//   * stepping is the PhysicsClock (Physics.Simulate once per 60 Hz sim tick); hit-stop and slow-mo reach the ragdoll through it.
// Data: Assets/Data/ragdoll.json (copy in StreamingAssets). enabled=false / missing file = nothing subscribed, nothing built, the death clip + holdLast stay.
// Presentation only: reads DuelFighter/Fighter, never writes the sim. Manual use: add this component to a fighter in the editor to force the ragdoll for that fighter even when the JSON flag is off.
public class RagdollDeath : MonoBehaviour
{
    // ---------------- data (Assets/Data/ragdoll.json) ----------------
    [Serializable] public class MassRow { public string contains = ""; public float kg = 80f; }
    [Serializable] public class DeathCfg { public float rampFrom = 1f, rampTo = 0.01f, rampSeconds = 1f, limpDamper = 2f, freezeSpeedSq = 0.02f, maxSeconds = 8f; public int freezeTicksBelow = 4; }
    [Serializable] public class ZoneParts { public string head = "head", torso = "spine2", legs = "thighL"; }
    [Serializable] public class ZoneRule { public string match = "equals", value = "", zone = "torso"; }   // match: equals | prefix | contains, on the attacker's move id
    [Serializable] public class SplitRow { public string part = "zone", note = ""; public float share = 1f; }   // part: "zone" = the zone part, else a ragdoll part name
    [Serializable] public class KillCfg { public float impulse = 70f, lift = 6f, impulseScale = 1f; public bool attackerFacing = true; public string defaultZone = "torso"; public ZoneParts zoneParts = new ZoneParts(); public ZoneRule[] zoneRules = new ZoneRule[0]; public SplitRow[] split = new SplitRow[0]; }
    [Serializable] public class DriveCfg { public float springPerKg = 300f, damperPerKg = 12f; }
    [Serializable] public class BodyCfg { public float linearDamping = 0.05f, angularDamping = 0.4f, maxAngularVelocity = 50f, maxDepenetrationVelocity = 4f; public int solverIterations = 6, solverVelocityIterations = 1; public string collisionDetection = "ContinuousSpeculative"; public bool ignoreSelfCollision = true, updateSkinnedWhenOffscreen = true; }
    [Serializable] public class RigCfg { public bool prebuild = true, hurtboxTrigger = true; public int maxLive = 3, sampleFrames = 6; public float restCheckMaxDeg = 25f; }
    [Serializable] public class InheritCfg { public int windowFrames = 3; public float animScale = 1f, rootScale = 1f, knockScale = 1f, angularScale = 1f, maxSpeed = 12f, maxAngularSpeed = 25f; public bool knockReplacesRoot = true; }
    [Serializable] public class ConvertCfg { public string[] disableComponents = new string[0]; public float tinyScale = 0.001f; }   // behaviours (by type name) that write bones in LateUpdate and would fight the ragdoll once it is dynamic
    [Serializable] public class LimitCfg { public float convertMarginDeg = 8f, tightenSeconds = 0.35f; }
    [Serializable] public class HingeCfg { public bool enabled; public float min, max, offAxisDeg = 10f, bend = 1f; public bool flip = true; }   // min..max degrees about the hinge axis; bend +1 = flexion moves the limb toward the fighter's forward, -1 = backward; offAxisDeg = swing / twist left on the other axes
    [Serializable] public class PartCfg { public string name = "", parent = "", end = ""; public string[] bones = new string[0]; public float massFrac = 0.05f, radiusM = 0.06f, swing = 40f, twist = 20f, gain = 1f, damping = 1f, inertiaMul = 1f; public HingeCfg hinge = new HingeCfg(); }
    [Serializable]
    public class Data
    {
        public bool enabled;
        public string[] nameContains = new string[0];
        public float startDelaySeconds, defaultMassKg = 80f, leafLengthM = 0.12f;
        public int minParts = 12;
        public MassRow[] massKg = new MassRow[0];
        public DeathCfg death = new DeathCfg();
        public KillCfg kill = new KillCfg();
        public DriveCfg drive = new DriveCfg();
        public BodyCfg body = new BodyCfg();
        public RigCfg rig = new RigCfg();
        public InheritCfg inherit = new InheritCfg();
        public LimitCfg limits = new LimitCfg();
        public ConvertCfg convert = new ConvertCfg();
        public PartCfg[] parts = new PartCfg[0];
    }

    static Data data;
    public static Data Cfg { get { if (data == null) Load(); return data; } }
    public static void Reload() { data = null; Load(); }

    static void Load()
    {
        data = new Data();
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("ragdoll");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "ragdoll.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/ragdoll.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("RagdollDeath: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) return;   // missing file = feature off
        try
        {
            var d = JsonUtility.FromJson<Data>(json.TrimStart('﻿'));
            if (d == null) return;
            if (d.nameContains == null) d.nameContains = new string[0];
            if (d.massKg == null) d.massKg = new MassRow[0];
            if (d.death == null) d.death = new DeathCfg();
            if (d.kill == null) d.kill = new KillCfg();
            if (d.kill.zoneParts == null) d.kill.zoneParts = new ZoneParts();
            if (d.kill.zoneRules == null) d.kill.zoneRules = new ZoneRule[0];
            if (d.kill.split == null) d.kill.split = new SplitRow[0];
            if (d.drive == null) d.drive = new DriveCfg();
            if (d.body == null) d.body = new BodyCfg();
            if (d.rig == null) d.rig = new RigCfg();
            if (d.inherit == null) d.inherit = new InheritCfg();
            if (d.limits == null) d.limits = new LimitCfg();
            if (d.convert == null) d.convert = new ConvertCfg(); if (d.convert.disableComponents == null) d.convert.disableComponents = new string[0];
            if (d.parts == null) d.parts = new PartCfg[0];
            for (int i = 0; i < d.parts.Length; i++) if (d.parts[i] != null && d.parts[i].hinge == null) d.parts[i].hinge = new HingeCfg();
            data = d;
        }
        catch (Exception ex) { Debug.LogError("RagdollDeath: parse failed: " + ex.Message); data = new Data(); }
    }

    // ---------------- global hook (only installed when the JSON flag is on) ----------------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        ContactRelay.OnKilled -= OnKilledGlobal;   // no domain reload in the editor: never double-subscribe
        SceneManager.sceneLoaded -= OnSceneLoaded;
        var d = Cfg;
        if (d == null || !d.enabled || d.parts.Length == 0) return;
        ContactRelay.OnKilled += OnKilledGlobal;
        if (d.rig.prebuild) { SceneManager.sceneLoaded += OnSceneLoaded; PrebuildAll(); }   // the first scene is already loaded when this runs; later loads come through the event
    }

    static void OnSceneLoaded(Scene s, LoadSceneMode m) { PrebuildAll(); }

    // pre-build the rigs right after the scene's Awake / OnEnable and BEFORE the first Animator update: the bones still hold the scene (rest / T) pose, which is the pose the joint
    // limits are measured from (V3 role-table limits are relative to rest). The cost is paid here, not at the first death.
    static void PrebuildAll()
    {
        if (!PhysicsClock.PhysicsOn) return;
        var all = FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var f = all[i]; if (f == null || !Matches(f)) continue;
            var rig = f.GetComponent<RagdollRig>();
            if (rig == null) rig = f.gameObject.AddComponent<RagdollRig>();
            rig.Build();
        }
        RagdollRig.LinkAll();
    }

    static bool Matches(DuelFighter f)
    {
        var d = Cfg;
        if (d.nameContains.Length == 0) return true;
        for (int i = 0; i < d.nameContains.Length; i++) if (!string.IsNullOrEmpty(d.nameContains[i]) && f.name.Contains(d.nameContains[i])) return true;
        return false;
    }

    static void OnKilledGlobal(DuelFighter f, MoveDef m)
    {
        if (f == null || !PhysicsClock.PhysicsOn) return;
        if (f.GetComponent<RagdollDeath>() != null) return;   // a manually added component handles its own fighter
        if (!Matches(f)) return;
        f.gameObject.AddComponent<RagdollDeath>().Begin(m);
    }

    // ---------------- per-fighter ----------------
    DuelFighter df; Fighter fighter; RagdollRig rig;
    bool begun, done, subscribed; float armAt; MoveDef killMove;

    void OnEnable()
    {
        df = GetComponent<DuelFighter>(); fighter = GetComponent<Fighter>();
        if (df == null) return;
        // when added by hand (not by the global hook) listen for this fighter's own death
        ContactRelay.OnKilled += OnKilledSelf; subscribed = true;
    }
    void OnDisable() { if (subscribed) { ContactRelay.OnKilled -= OnKilledSelf; subscribed = false; } }
    void OnKilledSelf(DuelFighter f, MoveDef m) { if (f == df && PhysicsClock.PhysicsOn) Begin(m); }

    public bool IsRagdoll { get { return rig != null && rig.Converted; } }
    public RagdollRig Rig { get { return rig; } }

    /// <summary>Start the death sequence (idempotent). The conversion runs startDelaySeconds later so the death clip's first pose leads in.</summary>
    public void Begin(MoveDef killingMove)
    {
        if (begun) return;
        begun = true; killMove = killingMove;
        if (df == null) df = GetComponent<DuelFighter>();
        if (fighter == null) fighter = GetComponent<Fighter>();
        rig = GetComponent<RagdollRig>(); if (rig == null) rig = gameObject.AddComponent<RagdollRig>();
        armAt = Time.time + Mathf.Max(0f, Cfg.startDelaySeconds);
        if (Cfg.startDelaySeconds <= 0f) DoConvert();
    }

    void Update() { if (begun && !done && Time.time >= armAt) DoConvert(); }

    void DoConvert()
    {
        if (done) return;
        done = true;
        RagdollRig.LinkAll();   // a rig that was not pre-built (late spawn) still ignores both CharacterControllers
        if (!rig.Convert(df != null ? df.opponent : null, killMove)) Debug.LogWarning("RagdollDeath: " + name + " kept its death clip (rig not built / too few parts / live cap).");
    }
}
