using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Death-only ragdoll (V3_TO_UNITY rows 3, 4, 8; V3 data/physics.json states.death + kill + motorTuning, data/hero-death.json ragdoll).
// OPT-IN and default OFF: Assets/Data/ragdoll.json "enabled": false. With it off nothing is subscribed and nothing changes (the death clip + holdLast stay).
// When on, the first time a fighter is Killed (ContactRelay.OnKilled) this builds a ~18-part ragdoll on the fighter's existing Mixamo bones:
//   capsule Collider + Rigidbody per part, ConfigurableJoint (slerp drive, locked linear, limited angular) to the parent part.
// The pose at the moment of conversion is the joint's rest pose (identity targetRotation holds it), the Animator is switched off, then the
// slerp strength ramps from rampFrom to rampTo over rampSeconds (V3 "ramp from current to 0.01 over 1.0 s"), a kill impulse is applied to the
// zone part, and the body is frozen (all kinematic) once the hips stay below freezeSpeedSq for freezeTicksBelow physics ticks (or maxSeconds).
// Presentation only: reads DuelFighter/Fighter, never writes the sim. Build happens once at death, so there is no per-frame cost before it.
// Manual use: add this component to a fighter in the editor to force the ragdoll for that fighter even when the JSON flag is off.
public class RagdollDeath : MonoBehaviour
{
    // ---------------- data (Assets/Data/ragdoll.json) ----------------
    [Serializable] public class MassRow { public string contains = ""; public float kg = 80f; }
    [Serializable] public class DeathCfg { public float rampFrom = 1f, rampTo = 0.01f, rampSeconds = 1f, limpDamper = 2f, freezeSpeedSq = 0.02f, maxSeconds = 8f; public int freezeTicksBelow = 4; }
    [Serializable] public class ZoneParts { public string head = "head", torso = "spine2", legs = "thighL"; }
    [Serializable] public class KillCfg { public float impulse = 70f, lift = 6f, impulseScale = 1f; public ZoneParts zoneParts = new ZoneParts(); }
    [Serializable] public class DriveCfg { public float springPerKg = 300f, damperPerKg = 12f; }
    [Serializable] public class BodyCfg { public float linearDamping = 0.05f, angularDamping = 0.4f; public string collisionDetection = "ContinuousSpeculative"; public bool ignoreSelfCollision = true, updateSkinnedWhenOffscreen = true; }
    [Serializable] public class PartCfg { public string name = "", parent = "", end = ""; public string[] bones = new string[0]; public float massFrac = 0.05f, radiusM = 0.06f, swing = 40f, twist = 20f, gain = 1f, damping = 1f; }
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
            var d = JsonUtility.FromJson<Data>(json);
            if (d == null) return;
            if (d.nameContains == null) d.nameContains = new string[0];
            if (d.massKg == null) d.massKg = new MassRow[0];
            if (d.death == null) d.death = new DeathCfg();
            if (d.kill == null) d.kill = new KillCfg();
            if (d.kill.zoneParts == null) d.kill.zoneParts = new ZoneParts();
            if (d.drive == null) d.drive = new DriveCfg();
            if (d.body == null) d.body = new BodyCfg();
            if (d.parts == null) d.parts = new PartCfg[0];
            data = d;
        }
        catch (Exception ex) { Debug.LogError("RagdollDeath: parse failed: " + ex.Message); data = new Data(); }
    }

    // ---------------- global hook (only installed when the JSON flag is on) ----------------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        ContactRelay.OnKilled -= OnKilledGlobal;   // no domain reload in the editor: never double-subscribe
        var d = Cfg;
        if (d == null || !d.enabled || d.parts.Length == 0) return;
        ContactRelay.OnKilled += OnKilledGlobal;
    }

    static void OnKilledGlobal(DuelFighter f, MoveDef m)
    {
        if (f == null) return;
        if (f.GetComponent<RagdollDeath>() != null) return;   // a manually added component handles its own fighter
        var d = Cfg;
        if (d.nameContains.Length > 0)
        {
            bool match = false;
            for (int i = 0; i < d.nameContains.Length; i++) if (!string.IsNullOrEmpty(d.nameContains[i]) && f.name.Contains(d.nameContains[i])) { match = true; break; }
            if (!match) return;
        }
        f.gameObject.AddComponent<RagdollDeath>().Begin(m);
    }

    // ---------------- per-fighter ----------------
    class Part { public PartCfg cfg; public Transform bone; public Rigidbody rb; public ConfigurableJoint joint; public float mass; }

    DuelFighter df; Fighter fighter;
    readonly List<Part> parts = new List<Part>();
    readonly Dictionary<string, Part> byName = new Dictionary<string, Part>();
    bool begun, built, frozen, subscribed;
    float armAt, builtAt; int slowTicks;
    MoveDef killMove;

    void OnEnable()
    {
        df = GetComponent<DuelFighter>(); fighter = GetComponent<Fighter>();
        if (df == null) return;
        // when added by hand (not by the global hook) listen for this fighter's own death
        ContactRelay.OnKilled += OnKilledSelf; subscribed = true;
    }
    void OnDisable() { if (subscribed) { ContactRelay.OnKilled -= OnKilledSelf; subscribed = false; } }
    void OnKilledSelf(DuelFighter f, MoveDef m) { if (f == df) Begin(m); }

    public bool IsRagdoll { get { return built; } }

    /// <summary>Start the death sequence (idempotent). The conversion runs startDelaySeconds later so the death clip's first pose leads in.</summary>
    public void Begin(MoveDef killingMove)
    {
        if (begun) return;
        begun = true; killMove = killingMove;
        if (df == null) df = GetComponent<DuelFighter>();
        if (fighter == null) fighter = GetComponent<Fighter>();
        armAt = Time.time + Mathf.Max(0f, Cfg.startDelaySeconds);
        if (Cfg.startDelaySeconds <= 0f) Build();
    }

    void Update()
    {
        if (begun && !built && !frozen && Time.time >= armAt) Build();
        if (!built || frozen) return;
        ApplyDrives(Time.time - builtAt);
    }

    void FixedUpdate()
    {
        if (!built || frozen) return;
        var dc = Cfg.death;
        float t = Time.time - builtAt;
        if (t >= dc.maxSeconds) { Freeze(); return; }
        if (t < dc.rampSeconds) { slowTicks = 0; return; }
        Part root = parts.Count > 0 ? parts[0] : null;
        if (root == null || root.rb == null) { Freeze(); return; }
        if (root.rb.linearVelocity.sqrMagnitude < dc.freezeSpeedSq) slowTicks++; else slowTicks = 0;
        if (slowTicks >= Mathf.Max(1, dc.freezeTicksBelow)) Freeze();
    }

    // ---------------- build ----------------
    static string Key(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int c = n.LastIndexOf(':'); if (c >= 0) n = n.Substring(c + 1);
        return n.ToLowerInvariant();
    }

    float MassFor()
    {
        var d = Cfg;
        { var rrp = RosterResolver.Pick(d.massKg, rrx => rrx.contains, name, false); if (rrp != null) return rrp.kg; }   // HCKIT2 v93
        return d.defaultMassKg;
    }

    void Build()
    {
        if (built) return;
        var d = Cfg;
        Transform root = (fighter != null && fighter.model != null) ? fighter.model : transform;
        var all = root.GetComponentsInChildren<Transform>(true);
        var lookup = new Dictionary<string, Transform>();
        for (int i = 0; i < all.Length; i++) { string k = Key(all[i].name); if (k.Length > 0 && !lookup.ContainsKey(k)) lookup[k] = all[i]; }

        float massSum = 0f;
        for (int i = 0; i < d.parts.Length; i++)
        {
            var pc = d.parts[i]; if (pc == null || pc.bones == null) continue;
            Transform bone = null;
            for (int b = 0; b < pc.bones.Length && bone == null; b++) lookup.TryGetValue(Key(pc.bones[b]), out bone);
            if (bone == null) continue;
            var p = new Part { cfg = pc, bone = bone };
            parts.Add(p); byName[pc.name] = p; massSum += Mathf.Max(0.0001f, pc.massFrac);
        }
        if (parts.Count < d.minParts || parts.Count == 0 || parts[0].cfg.parent != "")
        {
            Debug.LogWarning("RagdollDeath: only " + parts.Count + " of " + d.parts.Length + " bones resolved on " + name + " (or the root part is missing); keeping the death clip.");
            parts.Clear(); byName.Clear(); begun = true; frozen = true; return;
        }

        // physics: the Animator stops and the bones keep the pose they have now
        if (fighter != null) { fighter.holdLast = false; if (fighter.anim != null) fighter.anim.enabled = false; }
        float totalKg = MassFor();
        float sizeScale = df != null ? Mathf.Max(0.01f, df.scale) : Mathf.Max(0.01f, transform.lossyScale.y);

        CollisionDetectionMode cdm = CollisionDetectionMode.Discrete;
        try { cdm = (CollisionDetectionMode)Enum.Parse(typeof(CollisionDetectionMode), d.body.collisionDetection, true); } catch (Exception) { }

        // 1) colliders + bodies for every part first (joints need the parent body to exist)
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            p.mass = totalKg * Mathf.Max(0.0001f, p.cfg.massFrac) / massSum;
            AddCapsule(p, sizeScale, d.leafLengthM);
            var rb = p.bone.GetComponent<Rigidbody>();
            if (rb == null) rb = p.bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = p.mass; rb.useGravity = true; rb.isKinematic = false;
            rb.linearDamping = d.body.linearDamping; rb.angularDamping = d.body.angularDamping;
            rb.collisionDetectionMode = cdm;
            p.rb = rb;
        }
        // 2) joints, parent first (the JSON lists parents before children)
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i];
            Part parent;
            if (!byName.TryGetValue(p.cfg.parent, out parent)) parent = parts[0];
            var j = p.bone.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = parent.rb;
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
            float sw = Mathf.Clamp(p.cfg.swing, 1f, 177f), tw = Mathf.Clamp(p.cfg.twist, 1f, 177f);
            j.lowAngularXLimit = new SoftJointLimit { limit = -sw };
            j.highAngularXLimit = new SoftJointLimit { limit = sw };
            j.angularYLimit = new SoftJointLimit { limit = tw };
            j.angularZLimit = new SoftJointLimit { limit = sw };
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.enableCollision = false;
            p.joint = j;
        }
        // 3) the ragdoll must not fight itself
        if (d.body.ignoreSelfCollision)
        {
            var cols = new List<Collider>();
            for (int i = 0; i < parts.Count; i++) { var c = parts[i].bone.GetComponent<Collider>(); if (c != null) cols.Add(c); }
            for (int a = 0; a < cols.Count; a++) for (int b = a + 1; b < cols.Count; b++) Physics.IgnoreCollision(cols[a], cols[b], true);
        }
        if (d.body.updateSkinnedWhenOffscreen)
        {
            var sk = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < sk.Length; i++) sk[i].updateWhenOffscreen = true;
        }

        built = true; builtAt = Time.time; slowTicks = 0;
        ApplyDrives(0f);
        ApplyKillImpulse();
    }

    // capsule from the bone to its "end" (another part's bone, else the first child bone, else a short stub), all in the bone's local space so any import scale works
    void AddCapsule(Part p, float sizeScale, float leafM)
    {
        var bone = p.bone;
        Transform end = null;
        Part ep;
        if (!string.IsNullOrEmpty(p.cfg.end) && byName.TryGetValue(p.cfg.end, out ep)) end = ep.bone;
        if (end == null || end == bone) end = bone.childCount > 0 ? bone.GetChild(0) : null;
        float ls = Mathf.Max(0.0001f, bone.lossyScale.y);
        Vector3 v = end != null ? bone.InverseTransformPoint(end.position) : Vector3.up * (leafM * sizeScale / ls);
        if (v.sqrMagnitude < 1e-8f) v = Vector3.up * (leafM * sizeScale / ls);
        float len = v.magnitude;
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        var cap = bone.GetComponent<CapsuleCollider>();
        if (cap == null) cap = bone.gameObject.AddComponent<CapsuleCollider>();
        cap.direction = (ax >= ay && ax >= az) ? 0 : (ay >= az ? 1 : 2);
        float r = Mathf.Max(0.001f, p.cfg.radiusM * sizeScale / ls);
        cap.radius = r; cap.height = Mathf.Max(len, r * 2.02f); cap.center = v * 0.5f;
        cap.isTrigger = false;
    }

    // ---------------- motion ----------------
    void ApplyDrives(float t)
    {
        var dc = Cfg.death; var dr = Cfg.drive;
        float u = dc.rampSeconds > 0.0001f ? Mathf.Clamp01(t / dc.rampSeconds) : 1f;
        float strength = Mathf.Lerp(dc.rampFrom, dc.rampTo, u);
        float damperMul = Mathf.Lerp(1f, Mathf.Max(0.01f, dc.limpDamper), u);   // V3 limp_damper: the limp body is damped harder
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i]; if (p.joint == null) continue;
            var drive = new JointDrive();
            drive.positionSpring = dr.springPerKg * p.mass * p.cfg.gain * strength;
            drive.positionDamper = dr.damperPerKg * p.mass * p.cfg.damping * damperMul;
            drive.maximumForce = float.MaxValue;
            p.joint.slerpDrive = drive;
        }
    }

    Part PartForZone()
    {
        var z = Cfg.kill.zoneParts;
        string id = killMove != null && killMove.id != null ? killMove.id : "";
        string want = (id.Contains("overhead") || id.StartsWith("heavy")) ? z.head : (id == "kick" ? z.legs : z.torso);
        Part p;
        if (!string.IsNullOrEmpty(want) && byName.TryGetValue(want, out p)) return p;
        return parts[0];
    }

    void ApplyKillImpulse()
    {
        var k = Cfg.kill;
        Vector3 away = transform.forward * -1f;
        if (df != null && df.opponent != null) { away = transform.position - df.opponent.transform.position; }
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f) away = -transform.forward;
        away.Normalize();
        Vector3 imp = (away * k.impulse + Vector3.up * k.lift) * k.impulseScale;
        var target = PartForZone();
        if (target.rb != null) target.rb.AddForce(imp, ForceMode.Impulse);
    }

    void Freeze()
    {
        if (frozen) return;
        frozen = true;
        for (int i = 0; i < parts.Count; i++) if (parts[i].rb != null) parts[i].rb.isKinematic = true;
    }
}
