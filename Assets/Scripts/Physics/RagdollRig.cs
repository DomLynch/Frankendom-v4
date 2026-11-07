using System;
using System.Collections.Generic;
using UnityEngine;

// PHYS1 v1 (PH3 P1): the powered ragdoll rig of one fighter. Built ONCE at scene load (RagdollDeath.PrebuildAll), converted at death.
//  ALIVE : every part has a Rigidbody (kinematic, follows the Animator) + a CapsuleCollider (trigger on the Hurtbox layer: never blocks the CharacterController, rays that ignore triggers
//          skip it, the layer matrix gives it no contacts) + a ConfigurableJoint to its parent part built in the REST pose (limits are measured from rest, like V3's role table).
//          A cheap sampler (LateUpdate, after the Animator / IK) keeps the last few poses so the conversion knows each bone's velocity.
//  DEAD  : Convert() = Animator off, capsules solid on the RagdollBody layer, bodies dynamic with the inherited bone + knock velocity, the joint drive holds the pose the death started in
//          and ramps 1 -> 0.01 over death.rampSeconds (damper x limpDamper), limits start wide enough to contain that pose and tighten to V3's limits, the blow impulse is applied
//          along the attacker's facing at the zone part (split over parts by data), and the body freezes when every part is slow for freezeTicksBelow steps.
// Stepping: PhysicsClock.OnStep (sim time, hit-stop aware) when Scripted, FixedUpdate otherwise. Nothing here writes sim state. All numbers: ragdoll.json (+ physics.json for layers).
[DefaultExecutionOrder(500)]
public class RagdollRig : MonoBehaviour
{
    public class Part
    {
        public RagdollDeath.PartCfg cfg; public Transform bone; public Rigidbody rb; public ConfigurableJoint joint; public CapsuleCollider cap;
        public float mass; public Quaternion restLocal = Quaternion.identity, basis = Quaternion.identity;
        public float lowX, highX, limY, limZ;         // final limits (deg)
        public float curLowX, curHighX, curY, curZ;   // start-of-ramp limits (widened to contain the pose the death starts in)
        public bool hinge; public Part parent; public Vector3 longLocal = Vector3.up;
        public Vector3 inheritV, inheritW;            // what the conversion gave this body (proof)
        public Vector3 convertPos;                    // world position at the conversion (proof: pop check)
    }

    static readonly List<RagdollRig> All = new List<RagdollRig>();
    static int live;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { All.Clear(); live = 0; }

    public DuelFighter df; public Fighter fighter;
    public readonly List<Part> parts = new List<Part>();
    readonly Dictionary<string, Part> byName = new Dictionary<string, Part>();
    public bool Built, Converted, Frozen, BuildFailed;
    public float SimTime;                 // sim seconds since the conversion
    public Vector3 BlowDir; public string BlowZone = ""; public Vector3 KnockUsed, RootVelUsed;
    public float BuildMs;                 // how long Build() took (paid at scene load, not at death)
    string restWorst = "";            // FIXPHYS v99: the part with the largest rest flexion (named in the warning)
    public bool RestShifted;              // FIXPHYS2 v101: at least one hinge window was moved by the rest flexion (RestHingeMaxDeg stays the raw pose fact; the proof must not fail a compensated rig)
    public float RestHingeMaxDeg;         // largest |hinge flexion| found at build (should be ~0: rest pose)
    // FINPHYS v97: finisher blow (FinPhys.OnContact) + cut parts leaving the ragdoll (FinPhys.AfterCut / ReleaseHalves) + the sampled velocity of one part (pieces inherit it)
    public float FinPushMul = 1f; public string FinZone = ""; public Vector3 FinDir = Vector3.zero;
    readonly List<string[]> finCuts = new List<string[]>();
    public void CutOff(string[] carries) { if (carries == null || carries.Length == 0) return; finCuts.Add(carries); if (Converted) ApplyCut(carries); }   // before the conversion it is applied at the end of Convert()
    void ApplyCut(string[] carries)
    {
        for (int i = 0; i < carries.Length; i++)
        {
            var p = PartByName(carries[i]); if (p == null) continue;
            if (p.joint != null) { Destroy(p.joint); p.joint = null; }
            if (p.cap != null) p.cap.enabled = false;
            if (p.rb != null) { if (!p.rb.isKinematic) { p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero; } p.rb.isKinematic = true; }
        }
    }
    public bool PartVelocity(string name, out Vector3 v, out Vector3 w)
    {
        v = w = Vector3.zero; var p = PartByName(name); if (p == null) return false;
        int i = parts.IndexOf(p); if (i < 0) return false;
        Vector3 vr; return SampledVelocity(i, RagdollDeath.Cfg.inherit.windowFrames, out v, out w, out vr);
    }
    int slowTicks;
    // sampler ring
    Vector3[][] ringP; Quaternion[][] ringQ; Vector3[] ringRoot; float[] ringDt; int ringHead = -1, ringCount;

    public Part PartByName(string n) { Part p; return byName.TryGetValue(n, out p) ? p : null; }
    public static int LiveCount { get { return live; } }

    // KILLBUG v1: a CLONE (ZoneRuntime Instantiate of the inactive scene template that RagdollDeath.PrebuildAll already built) copies the serialized 'Built = true' but not 'parts' (Part is not
    // Serializable) nor the sampler ring: Built with zero parts, so the first death threw ArgumentOutOfRangeException at ZonePart() parts[0]. Heal = drop the cloned joints, forget the build, rebuild in Start
    // (still the rest pose: before the first Animator update). Never true for a rig that was built in place, so zones off is unchanged.
    bool cloneRebuild;
    bool Heal()
    {
        if (!Built || parts.Count > 0) return false;
        var js = GetComponentsInChildren<ConfigurableJoint>(true);
        for (int i = 0; i < js.Length; i++) if (js[i] != null) DestroyImmediate(js[i]);
        Built = false; BuildFailed = false; ringP = null; ringQ = null; ringHead = -1; ringCount = 0;
        Debug.Log("[killbug] " + name + ": cloned RagdollRig had no parts (Built copied, lists not); rig rebuilt");
        return true;
    }
    void Awake() { if (Heal()) cloneRebuild = true; }
    void Start()
    {
        if (!cloneRebuild) return; cloneRebuild = false;
        try { var d = RagdollDeath.Cfg; if (d.enabled && d.rig.prebuild && PhysicsClock.PhysicsOn && !Built) { Build(); LinkAll(); } }
        catch (Exception ex) { Debug.LogWarning("[killbug] clone rig build failed on " + name + ": " + ex.Message); }
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); PhysicsClock.OnStep += OnClockStep; }
    void OnDisable() { All.Remove(this); PhysicsClock.OnStep -= OnClockStep; }
    void OnDestroy() { if (Converted && !Frozen) live = Mathf.Max(0, live - 1); }

    static string Key(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int c = n.LastIndexOf(':'); if (c >= 0) n = n.Substring(c + 1);
        return n.ToLowerInvariant();
    }

    float MassFor()
    {
        var d = RagdollDeath.Cfg;
        { var rrp = RosterResolver.Pick(d.massKg, rrx => rrx.contains, name, false); if (rrp != null) return rrp.kg; }   // HCKIT2 v93
        return d.defaultMassKg;
    }

    // ---------------- build (kinematic) ----------------
    public bool Build()
    {
        if (Built) return true;
        if (BuildFailed) return false;
        long bt0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var d = RagdollDeath.Cfg;
        df = GetComponent<DuelFighter>(); fighter = GetComponent<Fighter>();
        Transform root = (fighter != null && fighter.model != null) ? fighter.model : transform;
        var all = root.GetComponentsInChildren<Transform>(true);
        var lookup = new Dictionary<string, Transform>();
        for (int i = 0; i < all.Length; i++) { if (all[i].GetComponent<Renderer>() != null) continue; string k = Key(all[i].name); if (k.Length > 0 && !lookup.ContainsKey(k)) lookup[k] = all[i]; }   // mesh nodes (a SkinnedMeshRenderer object called 'head' sits near the model root) are not bones

        float massSum = 0f;
        for (int i = 0; i < d.parts.Length; i++)
        {
            var pc = d.parts[i]; if (pc == null || pc.bones == null) continue;
            Transform bone = null;
            for (int b = 0; b < pc.bones.Length && bone == null; b++) lookup.TryGetValue(Key(pc.bones[b]), out bone);
            if (bone == null) continue;
            if (parts.Count > 0 && !bone.IsChildOf(parts[0].bone)) continue;   // every other part must hang under the root part's bone
            var p = new Part { cfg = pc, bone = bone };
            parts.Add(p); byName[pc.name] = p; massSum += Mathf.Max(0.0001f, pc.massFrac);
        }
        if (parts.Count < d.minParts || parts.Count == 0 || parts[0].cfg.parent != "")
        {
            Debug.LogWarning("RagdollRig: only " + parts.Count + " of " + d.parts.Length + " bones resolved on " + name + " (or the root part is missing); keeping the death clip.");
            parts.Clear(); byName.Clear(); BuildFailed = true; return false;
        }

        float totalKg = MassFor();
        float sizeScale = df != null ? Mathf.Max(0.01f, df.scale) : Mathf.Max(0.01f, transform.lossyScale.y);
        int hurt = PhysMasks.HurtboxLayer;
        Vector3 fwd = (df != null ? df.transform.forward : transform.forward); fwd.y = 0f; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();

        // 1) colliders + kinematic bodies (joints need the parent body to exist)
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            p.mass = totalKg * Mathf.Max(0.0001f, p.cfg.massFrac) / massSum;
            p.parent = (p.cfg.parent != "" && byName.ContainsKey(p.cfg.parent)) ? byName[p.cfg.parent] : (i == 0 ? null : parts[0]);
            p.cap = AddCapsule(p, sizeScale, d.leafLengthM, out p.longLocal);
            p.cap.isTrigger = d.rig.hurtboxTrigger;
            p.bone.gameObject.layer = hurt;
            var rb = p.bone.GetComponent<Rigidbody>();
            if (rb == null) rb = p.bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = p.mass; rb.useGravity = false; rb.isKinematic = true; rb.detectCollisions = true;
            rb.interpolation = RigidbodyInterpolation.None;
            p.rb = rb;
        }
        // 2) joints in the rest pose, parent first (the JSON lists parents before children)
        RestHingeMaxDeg = 0f;
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i];
            var par = p.parent ?? parts[0];
            var j = p.bone.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = par.rb;
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
            p.restLocal = RelRot(p);

            Vector3 longL = p.longLocal.sqrMagnitude > 1e-8f ? p.longLocal.normalized : Vector3.up;
            Vector3 axis, sec;
            var h = p.cfg.hinge;
            bool hingeOk = false;
            if (h != null && h.enabled)
            {
                Vector3 longW = p.bone.TransformDirection(longL);
                Vector3 axW = Vector3.Cross(longW, fwd * (h.bend < 0f ? -1f : 1f));
                if (axW.magnitude >= 0.4f)
                {
                    axW.Normalize();
                    axis = p.bone.InverseTransformDirection(axW).normalized; sec = longL;
                    sec = (sec - axis * Vector3.Dot(sec, axis)).normalized;
                    j.axis = axis; j.secondaryAxis = sec;
                    float oa = Mathf.Clamp(h.offAxisDeg, 1f, 177f);
                    if (h.flip) { p.lowX = Mathf.Clamp(-h.max, -177f, 177f); p.highX = Mathf.Clamp(-h.min, -177f, 177f); } else { p.lowX = Mathf.Clamp(h.min, -177f, 177f); p.highX = Mathf.Clamp(h.max, -177f, 177f); }   // flip: the joint's angle is the connected body relative to this one, i.e. the negative of the flexion about +axis
                    p.limY = oa; p.limZ = oa;
                    hingeOk = true;
                }
            }
            if (!hingeOk)
            {
                axis = longL;                                                     // joint X = the bone's long axis: low / high X = twist, Y / Z = swing cone
                sec = Mathf.Abs(Vector3.Dot(axis, Vector3.right)) < Mathf.Abs(Vector3.Dot(axis, Vector3.forward)) ? Vector3.right : Vector3.forward;
                sec = (sec - axis * Vector3.Dot(sec, axis)).normalized;
                j.axis = axis; j.secondaryAxis = sec;
                float sw = Mathf.Clamp(p.cfg.swing, 1f, 177f), tw = Mathf.Clamp(p.cfg.twist, 1f, 177f);
                p.lowX = -tw; p.highX = tw; p.limY = sw; p.limZ = sw;
            }
            p.hinge = hingeOk;
            // basis of the joint frame in bone-local space (same construction ConfigurableJoint uses): x = axis, y = up, z = cross(axis, secondary)
            Vector3 fw = Vector3.Cross(j.axis, j.secondaryAxis).normalized, up = Vector3.Cross(fw, j.axis).normalized;
            p.basis = Quaternion.LookRotation(fw, up);
            p.curLowX = p.lowX; p.curHighX = p.highX; p.curY = p.limY; p.curZ = p.limZ;
            ApplyLimits(p, p.lowX, p.highX, p.limY, p.limZ);
            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.enableCollision = false;
            j.enablePreprocessing = false;
            p.joint = j;
            if (hingeOk)
            {
                float r0 = HingeAngle(p);
                if (Mathf.Abs(r0) > RestHingeMaxDeg) { RestHingeMaxDeg = Mathf.Abs(r0); restWorst = p.cfg.name; }
                // FIXPHYS v99: a model whose prefab pose is not straight (Pitborn: 69 deg) had its hinge window measured from THAT pose, i.e. shifted by the rest flexion: the death pose violated the window
                // (enemy_momentum.noPop 9.4 cm armR, hingeRange forearmR 118 / shinR 155 deg). Shift the window back so the flexion limits stay [hinge.min, hinge.max] in absolute degrees.
                if (Mathf.Abs(r0) > (d.rig.restShiftMinDeg >= 0f ? d.rig.restShiftMinDeg : d.rig.restCheckMaxDeg))   // PHYSFLINCH v1
                {
                    float off = h.flip ? r0 : -r0;
                    p.lowX = Mathf.Clamp(p.lowX + off, -177f, 177f); p.highX = Mathf.Clamp(p.highX + off, -177f, 177f);
                    p.curLowX = p.lowX; p.curHighX = p.highX; ApplyLimits(p, p.lowX, p.highX, p.limY, p.limZ); RestShifted = true;   // FIXPHYS2 v101
                }
            }
        }
        if (RestHingeMaxDeg > d.rig.restCheckMaxDeg)
            Debug.LogWarning("RagdollRig: " + name + " was NOT in its rest pose at build (hinge flexion " + RestHingeMaxDeg.ToString("F0") + " deg at " + restWorst + " > " + d.rig.restCheckMaxDeg.ToString("F0") + "): joint limits will be offset. Build before the first Animator update.");
        // 3) the ragdoll must not fight itself (pairs are ignored while the capsules are still triggers; GetIgnoreCollision is re-checked at conversion)
        if (d.body.ignoreSelfCollision) IgnoreSelf();
        int n = Mathf.Max(2, d.rig.sampleFrames);
        ringP = new Vector3[n][]; ringQ = new Quaternion[n][]; ringRoot = new Vector3[n]; ringDt = new float[n];
        for (int i = 0; i < n; i++) { ringP[i] = new Vector3[parts.Count]; ringQ[i] = new Quaternion[parts.Count]; }
        ringHead = -1; ringCount = 0;
        Built = true;
        BuildMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - bt0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        return true;
    }

    void IgnoreSelf()
    {
        for (int a = 0; a < parts.Count; a++) for (int b = a + 1; b < parts.Count; b++) SafeIgnore(parts[a].cap, parts[b].cap);
    }

    static void SafeIgnore(Collider a, Collider b)
    {
        if (a == null || b == null || a == b) return;
        if (!a.enabled || !b.enabled || !a.gameObject.activeInHierarchy || !b.gameObject.activeInHierarchy) return;
        try { Physics.IgnoreCollision(a, b, true); } catch (Exception) { }
    }

    /// <summary>Every rig ignores every fighter's CharacterController (a corpse must never push the living fighter: the sim reads that position). Safe to call often.</summary>
    public static void LinkAll()
    {
        var ccs = new List<CharacterController>();
        int rl = PhysMasks.RagdollLayer;
        var fs = FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < fs.Length; i++)
        {
            var cc = fs[i] != null ? fs[i].GetComponent<CharacterController>() : null; if (cc == null) continue;
            ccs.Add(cc);
            int exMask = cc.excludeLayers.value | (1 << rl); var exNames = PhysicsCfg.Get().ccExclude; if (exNames != null) for (int xi = 0; xi < exNames.Length; xi++) exMask |= PhysMasks.Bit(exNames[xi]);   // PHYSON v1: the sim moves with cc.Move, so finisher pieces (physProp) must not block it either
            cc.excludeLayers = exMask;   // layer based, so it survives the cc.enabled toggles (teleports, FinisherGround) that drop per-collider IgnoreCollision pairs
        }
        for (int r = 0; r < All.Count; r++)
        {
            var rig = All[r]; if (rig == null || !rig.Built) continue;
            for (int c = 0; c < ccs.Count; c++) for (int p = 0; p < rig.parts.Count; p++) SafeIgnore(rig.parts[p].cap, ccs[c]);
        }
    }

    // capsule from the bone to its "end" (another part's bone, else the first child bone, else a short stub), all in the bone's local space so any import scale works
    CapsuleCollider AddCapsule(Part p, float sizeScale, float leafM, out Vector3 longLocal)
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
        longLocal = v;
        return cap;
    }

    static void ApplyLimits(Part p, float lowX, float highX, float y, float z)
    {
        var j = p.joint; if (j == null) return;
        j.lowAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(lowX, -177f, 177f) };
        j.highAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(highX, -177f, 177f) };
        j.angularYLimit = new SoftJointLimit { limit = Mathf.Clamp(y, 0f, 177f) };
        j.angularZLimit = new SoftJointLimit { limit = Mathf.Clamp(z, 0f, 177f) };
    }

    // ---------------- sampler (alive) ----------------
    void LateUpdate()
    {
        if (!Built || Converted || ringP == null) return;
        float dt = PresentationClock.SimDelta(Time.deltaTime);
        if (dt <= 1e-6f) return;                       // hit-stop / pause: the pose did not move
        ringHead = (ringHead + 1) % ringP.Length;
        var pp = ringP[ringHead]; var qq = ringQ[ringHead];
        for (int i = 0; i < parts.Count; i++) { pp[i] = parts[i].bone.position; qq[i] = parts[i].bone.rotation; }
        ringRoot[ringHead] = transform.position; ringDt[ringHead] = dt;
        if (ringCount < ringP.Length) ringCount++;
    }

    // velocity of part i over the last `window` samples (bone world velocity, angular velocity, and the root's velocity)
    bool SampledVelocity(int i, int window, out Vector3 v, out Vector3 w, out Vector3 vRoot)
    {
        v = w = vRoot = Vector3.zero;
        int len = ringP.Length, n = Mathf.Min(Mathf.Max(1, window), ringCount - 1);
        if (n < 1 || ringHead < 0) return false;
        int a = ringHead, b = ((ringHead - n) % len + len) % len;
        float T = 0f; for (int k = 0; k < n; k++) T += ringDt[((ringHead - k) % len + len) % len];
        if (T <= 1e-6f) return false;
        v = (ringP[a][i] - ringP[b][i]) / T;
        vRoot = (ringRoot[a] - ringRoot[b]) / T;
        Quaternion dq = ringQ[a][i] * Quaternion.Inverse(ringQ[b][i]);
        float ang; Vector3 axis; dq.ToAngleAxis(out ang, out axis);
        if (ang > 180f) ang -= 360f;
        if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x)) w = axis.normalized * (ang * Mathf.Deg2Rad / T);
        return true;
    }

    /// <summary>The pose the sampler saw last (world positions per part), for the proof's pop check. Null before two samples.</summary>
    public Vector3[] LastSampledPositions() { return ringHead >= 0 ? ringP[ringHead] : null; }

    // ---------------- conversion ----------------
    public bool Convert(DuelFighter killer, MoveDef move)
    {
        if (Converted) return true;
        var d = RagdollDeath.Cfg;
        if (Built && parts.Count == 0) Heal();   // KILLBUG v1: never convert a rig whose lists were not copied
        if (!Built && !Build()) return false;
        for (int i = 0; i < parts.Count; i++) if (parts[i].bone == null || parts[i].rb == null) { Debug.LogWarning("RagdollRig: a bone of " + name + " is gone; keeping the death clip."); return false; }
        if (live >= Mathf.Max(1, d.rig.maxLive)) { Debug.LogWarning("RagdollRig: live ragdoll cap (" + d.rig.maxLive + ") reached; " + name + " keeps its death clip."); return false; }

        Snap();   // REVIVE v1
        // 1) velocities BEFORE the Animator stops: bone velocity relative to the root (the clip) + the root's own motion, the sim knock replaces the root's horizontal part
        var inh = d.inherit;
        Vector3 knock = df != null ? df.KnockVelocity : Vector3.zero;
        Vector3 rootVel = Vector3.zero; bool haveRoot = false;
        for (int i = 0; i < parts.Count; i++)
        {
            Vector3 v, w, vr;
            bool ok = SampledVelocity(i, inh.windowFrames, out v, out w, out vr);
            if (ok && !haveRoot) { rootVel = vr; haveRoot = true; }
            Vector3 rel = ok ? (v - vr) * inh.animScale : Vector3.zero;   // the clip's own motion, relative to the root
            Vector3 rv = ok ? vr : Vector3.zero, add = Vector3.zero;          // the root's motion (run / roll / earlier knock slide)
            if (inh.knockReplacesRoot && knock.sqrMagnitude > 1e-6f) { rv.x = knock.x * inh.knockScale; rv.z = knock.z * inh.knockScale; }   // SetKnock (this blow) replaced the slide the sim moves him with
            else add = knock * inh.knockScale;
            Vector3 total = rel + rv * inh.rootScale + add;
            float sp = total.magnitude; if (sp > inh.maxSpeed && sp > 1e-5f) total *= inh.maxSpeed / sp;
            Vector3 aw = w * inh.angularScale; float asp = aw.magnitude; if (asp > inh.maxAngularSpeed && asp > 1e-5f) aw *= inh.maxAngularSpeed / asp;
            if (float.IsNaN(total.x) || float.IsNaN(total.y) || float.IsNaN(total.z)) total = Vector3.zero;
            if (float.IsNaN(aw.x) || float.IsNaN(aw.y) || float.IsNaN(aw.z)) aw = Vector3.zero;
            parts[i].inheritV = total; parts[i].inheritW = aw;
        }
        if (inh.chainVelocity) ChainInherit(inh);   // PHYSFLINCH v1: child bodies take the rigid velocity of their parent body at the joint anchor (the arms hang on spine2 with an un-bodied clavicle between: sampled bone velocities disagreed with the joint)
        KnockUsed = knock; RootVelUsed = rootVel;

        // 2) the Animator stops; the bones keep the pose they have now
        if (fighter != null) { fighter.holdLast = false; if (fighter.anim != null) fighter.anim.enabled = false; }
        var off = d.convert.disableComponents;
        if (off != null && off.Length > 0)
        {
            var bs = GetComponentsInChildren<Behaviour>(true);
            for (int i = 0; i < bs.Length; i++) { if (bs[i] == null || bs[i] == this) continue; string tn = bs[i].GetType().Name; for (int k = 0; k < off.Length; k++) if (tn == off[k]) { if (bs[i].enabled) offList.Add(bs[i]); bs[i].enabled = false; break; } }   // IK / clamps that write bones in LateUpdate (FootGround moves the hips) would fight the bodies
        }
        if (d.body.updateSkinnedWhenOffscreen)      // only now: a living fighter keeps the cheap bounds, the ragdolled body can leave its bind-pose bounds
        {
            Transform skRoot = (fighter != null && fighter.model != null) ? fighter.model : transform;
            var sk = skRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < sk.Length; i++) sk[i].updateWhenOffscreen = true;
        }
        for (int i = 0; i < parts.Count; i++) parts[i].convertPos = parts[i].bone.position;
        Physics.SyncTransforms();                       // kinematic bodies moved with the animation: bring PhysX to the current pose before they turn dynamic

        // 3) solid, dynamic, moving
        CollisionDetectionMode cdm = CollisionDetectionMode.Discrete;
        try { cdm = (CollisionDetectionMode)Enum.Parse(typeof(CollisionDetectionMode), d.body.collisionDetection, true); } catch (Exception) { }
        int rl = PhysMasks.RagdollLayer;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            p.bone.gameObject.layer = rl;
            p.cap.isTrigger = false;
            var rb = p.rb;
            rb.isKinematic = false; rb.useGravity = true;
            rb.linearDamping = d.body.linearDamping; rb.angularDamping = d.body.angularDamping;
            rb.maxAngularVelocity = d.body.maxAngularVelocity; rb.maxDepenetrationVelocity = d.body.maxDepenetrationVelocity;
            rb.solverIterations = Mathf.Max(1, d.body.solverIterations); rb.solverVelocityIterations = Mathf.Max(1, d.body.solverVelocityIterations);
            rb.collisionDetectionMode = cdm;
            rb.ResetCenterOfMass(); rb.ResetInertiaTensor();   // trigger colliders did not count towards the mass properties
            if (p.cfg.inertiaMul > 1.001f) rb.inertiaTensor = rb.inertiaTensor * p.cfg.inertiaMul;
            if (p.bone.lossyScale.y < d.convert.tinyScale)       // FinisherPlayer scales the cut head bone to ~0: a collider and an inertia of nothing would explode the solver
            {
                float tr = Mathf.Max(0.01f, p.cfg.radiusM * (df != null ? df.scale : 1f));
                p.cap.enabled = false; rb.inertiaTensor = Vector3.one * Mathf.Max(1e-4f, 0.4f * p.mass * tr * tr);
            }   // V3 role table: x8 on the torso capsules was needed for stable joint springs (Jolt); 1 = PhysX default
            // FIXPHYS2 v101: inheritV is the velocity of the BONE ORIGIN (the sampler follows bone positions) but Rigidbody.linearVelocity is the CENTRE OF MASS: a swinging arm moved its origin by w x r x dt on the first step
            // (enemy_momentum.noPop armR 9.3 cm; 25 rad/s x ~0.17 m x 16.7 ms = 7 cm). v_com = v_origin + w x (com - origin) makes the origin follow the clip.
            { Vector3 cOff = rb.worldCenterOfMass - p.bone.position, cv = Vector3.Cross(p.inheritW, cOff);   // PHYSFLINCH v1
              if (d.inherit.chord && p.inheritW.sqrMagnitude > 1e-8f)   // the origin follows the chord of the rotation over the step, not its tangent (a 25 rad/s arm: 1.5 cm)
              { float sdt = Mathf.Max(1e-4f, PhysicsClock.Scripted ? PhysicsClock.StepDt : Time.fixedDeltaTime), wm = p.inheritW.magnitude; cv = (Quaternion.AngleAxis(wm * sdt * Mathf.Rad2Deg, p.inheritW / wm) * cOff - cOff) / sdt; }
              rb.linearVelocity = p.inheritV + cv; rb.angularVelocity = p.inheritW; }
        }
        // the ignore pairs are keyed on the colliders; if a trigger / layer change dropped any, put them back (the proof logs this)
        if (d.body.ignoreSelfCollision && parts.Count > 2 && !Physics.GetIgnoreCollision(parts[0].cap, parts[parts.Count - 1].cap)) IgnoreSelf();
        LinkAll();

        // 4) joints: hold the pose the death starts in, limits wide enough to contain it
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i]; if (p.joint == null) continue;
            if (inh.reanchor) ReAnchor(p);   // PHYSFLINCH v1
            WidenLimits(p, d.limits.convertMarginDeg);
            SetTargetToCurrentPose(p);
        }
        Converted = true; Frozen = false; SimTime = 0f; slowTicks = 0; live++;
        ApplyDrives(0f); TightenLimits(0f);
        ApplyBlow(killer, move); for (int fc = 0; fc < finCuts.Count; fc++) ApplyCut(finCuts[fc]);   /* FINPHYS v97 */
        return true;
    }

    // // PHYSFLINCH v1: the velocity of every body, parent first (ragdoll.json lists parents before children). A child's anchor is held to its parent body by a locked joint, so its origin must start with the parent
    // body's velocity AT THAT POINT: v_parent_origin + w_parent x (anchor - parent origin). The sampled bone velocity differs from that wherever a bone without a body sits between the two (clavicle), by a few m/s
    // for a swinging arm = the 9 cm first-step jump of enemy_momentum.noPop. chainBlend 1 = fully rigid, 0 = the sampled value (old).
    void ChainInherit(RagdollDeath.InheritCfg inh)
    {
        float k = Mathf.Clamp01(inh.chainBlend);
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i]; var par = p.parent; if (par == null || par.bone == null || p.bone == null) continue;
            Vector3 vc = par.inheritV + Vector3.Cross(par.inheritW, p.bone.position - par.bone.position);
            if (float.IsNaN(vc.x) || float.IsNaN(vc.y) || float.IsNaN(vc.z)) continue;
            Vector3 v = Vector3.Lerp(p.inheritV, vc, k);
            float sp = v.magnitude; if (sp > inh.maxSpeed && sp > 1e-5f) v *= inh.maxSpeed / sp;
            p.inheritV = v;
        }
    }

    // // PHYSFLINCH v1: the joints were anchored in the REST pose; the death starts in a clip pose where the un-bodied bones between two parts have moved, so projection would snap the child to the rest offset. Re-measure in the death pose.
    void ReAnchor(Part p)
    {
        var j = p.joint; var par = p.parent ?? (parts.Count > 0 ? parts[0] : null); if (j == null || par == null || par.bone == null || p.bone == null) return;
        j.autoConfigureConnectedAnchor = false; j.anchor = Vector3.zero;
        j.connectedAnchor = par.bone.InverseTransformPoint(p.bone.position);
    }

    // rotation of a part relative to its PARENT PART's bone (not the transform parent: Mixamo has a shoulder / clavicle bone between spine2 and the arm that has no body)
    static Quaternion RelRot(Part p) { return p.parent != null && p.parent.bone != null ? Quaternion.Inverse(p.parent.bone.rotation) * p.bone.rotation : p.bone.localRotation; }

    // joint-space relative rotation of the bone vs its rest pose, and the swing / twist angles it implies (PhysX: twist about joint X, swing in tan(quarter-angle) space about Y and Z)
    static void JointAngles(Part p, out float twistDeg, out float swingYDeg, out float swingZDeg)
    {
        Quaternion q = Quaternion.Inverse(p.restLocal) * RelRot(p);
        Quaternion r = Quaternion.Inverse(p.basis) * q * p.basis;
        if (r.w < 0f) r = new Quaternion(-r.x, -r.y, -r.z, -r.w);
        twistDeg = 2f * Mathf.Atan2(r.x, r.w) * Mathf.Rad2Deg;
        float m = Mathf.Sqrt(r.x * r.x + r.w * r.w);
        Quaternion tq = m > 1e-6f ? new Quaternion(r.x / m, 0f, 0f, r.w / m) : Quaternion.identity;
        Quaternion s = r * Quaternion.Inverse(tq);
        if (s.w < 0f) s = new Quaternion(-s.x, -s.y, -s.z, -s.w);
        swingYDeg = 4f * Mathf.Atan(s.y / (1f + s.w)) * Mathf.Rad2Deg;
        swingZDeg = 4f * Mathf.Atan(s.z / (1f + s.w)) * Mathf.Rad2Deg;
    }

    // symmetric on purpose: if the sign convention of the hinge angle were flipped the pose still sits inside the (wide) start limits, so there is no pop either way
    void WidenLimits(Part p, float marginDeg)
    {
        float tw, sy, sz; JointAngles(p, out tw, out sy, out sz);
        float atw = Mathf.Abs(tw) + marginDeg;
        p.curLowX = Mathf.Min(p.lowX, -atw); p.curHighX = Mathf.Max(p.highX, atw);
        p.curY = Mathf.Max(p.limY, Mathf.Abs(sy) + marginDeg); p.curZ = Mathf.Max(p.limZ, Mathf.Abs(sz) + marginDeg);
        p.curLowX = Mathf.Clamp(p.curLowX, -177f, 177f); p.curHighX = Mathf.Clamp(p.curHighX, -177f, 177f); p.curY = Mathf.Min(p.curY, 177f); p.curZ = Mathf.Min(p.curZ, 177f);
        ApplyLimits(p, p.curLowX, p.curHighX, p.curY, p.curZ);
    }

    void TightenLimits(float t)
    {
        float T = Mathf.Max(0f, RagdollDeath.Cfg.limits.tightenSeconds);
        float u = T > 1e-5f ? Mathf.Clamp01(t / T) : 1f; u = u * u * (3f - 2f * u);
        for (int i = 1; i < parts.Count; i++)
        {
            var p = parts[i]; if (p.joint == null) continue;
            ApplyLimits(p, Mathf.Lerp(p.curLowX, p.lowX, u), Mathf.Lerp(p.curHighX, p.highX, u), Mathf.Lerp(p.curY, p.limY, u), Mathf.Lerp(p.curZ, p.limZ, u));
        }
    }

    // ConfigurableJoint.targetRotation for "hold this local rotation" (the well-known joint-space conversion: Inverse(target) * start, conjugated by the joint frame)
    static void SetTargetToCurrentPose(Part p)
    {
        var j = p.joint;
        Vector3 right = j.axis, fwd = Vector3.Cross(j.axis, j.secondaryAxis).normalized, up = Vector3.Cross(fwd, right).normalized;
        Quaternion toJoint = Quaternion.LookRotation(fwd, up);
        j.targetRotation = Quaternion.Inverse(toJoint) * Quaternion.Inverse(RelRot(p)) * p.restLocal * toJoint;
    }

    void ApplyDrives(float t)
    {
        var d = RagdollDeath.Cfg; var dc = d.death; var dr = d.drive;
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

    // ---------------- the blow ----------------
    string ZoneOf(MoveDef m)
    {
        var k = RagdollDeath.Cfg.kill;
        string id = m != null && m.id != null ? m.id : "";
        for (int i = 0; i < k.zoneRules.Length; i++)
        {
            var r = k.zoneRules[i]; if (r == null || string.IsNullOrEmpty(r.value)) continue;
            bool hit = r.match == "prefix" ? id.StartsWith(r.value, StringComparison.Ordinal) : (r.match == "contains" ? id.Contains(r.value) : id == r.value);
            if (hit) return r.zone;
        }
        return k.defaultZone;
    }

    Part ZonePart(string zone)
    {
        var z = RagdollDeath.Cfg.kill.zoneParts;
        string want = zone == "head" ? z.head : (zone == "legs" ? z.legs : z.torso);
        Part p; if (!string.IsNullOrEmpty(want) && byName.TryGetValue(want, out p)) return p;
        return parts.Count > 0 ? parts[0] : null;   // KILLBUG v1: Push() ignores null
    }

    void ApplyBlow(DuelFighter killer, MoveDef victimMove)
    {
        if (parts.Count == 0) return;   // KILLBUG v1
        var k = RagdollDeath.Cfg.kill;
        MoveDef m = (killer != null && killer.CurrentMove != null) ? killer.CurrentMove : victimMove;   // Killed() hands over the VICTIM's own move; the blow is the attacker's
        Vector3 dir = Vector3.zero;
        if (k.attackerFacing && killer != null) dir = killer.transform.forward;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f && df != null && df.opponent != null) { dir = transform.position - df.opponent.transform.position; dir.y = 0f; }
        if (dir.sqrMagnitude < 1e-4f) { dir = -transform.forward; dir.y = 0f; }
        if (FinDir.sqrMagnitude > 1e-4f) dir = FinDir; dir.Normalize(); BlowDir = dir;   /* FINPHYS v97 */
        BlowZone = string.IsNullOrEmpty(FinZone) ? ZoneOf(m) : FinZone;   /* FINPHYS v97 */
        Part zp = ZonePart(BlowZone);
        Debug.Log("[ragdoll] blow on " + name + ": killer " + (killer != null ? killer.name : "none") + " move '" + (m != null ? m.id : "none") + "' zone " + BlowZone + (string.IsNullOrEmpty(FinZone) ? "" : " (finisher row)") + " dir " + dir.ToString("0.00") + " push x" + FinPushMul.ToString("0.00"));   // FIXPHYS v99: the proof read zone / dir from a finisher row without this line saying so
        if (k.speedMps > 0f)   // FIXPHYS v99: the blow is a VELOCITY (m/s for the whole body) so a 120 kg Pitborn is pushed as far as a 67 kg Joan; the old 70 N s (V3's 65 kg body) gave 0.58 m/s and the collapse beat it (hips -0.08 .. -0.29 m)
        {
            float M = 0f; for (int i = 0; i < parts.Count; i++) M += parts[i].mass;
            if (M > 1e-4f)
            {
                Vector3 I = (dir * k.speedMps + Vector3.up * k.liftMps) * M * k.impulseScale * FinPushMul;
                float zs = Mathf.Clamp01(k.zoneShare);
                for (int i = 0; i < parts.Count; i++) Push(parts[i], I * ((1f - zs) * parts[i].mass / M));   // every body gets the same velocity change: no whip
                Push(zp, I * zs);                                                                                 // the struck zone gets its extra share (head / thigh)
                return;
            }
        }
        Vector3 imp = (dir * k.impulse + Vector3.up * k.lift) * k.impulseScale * FinPushMul;   /* FINPHYS v97 */
        if (k.split == null || k.split.Length == 0) { Push(zp, imp); return; }
        float sum = 0f; for (int i = 0; i < k.split.Length; i++) if (k.split[i] != null) sum += Mathf.Max(0f, k.split[i].share);
        if (sum <= 1e-6f) { Push(zp, imp); return; }
        for (int i = 0; i < k.split.Length; i++)
        {
            var s = k.split[i]; if (s == null || s.share <= 0f) continue;
            Part tp = s.part == "zone" ? zp : PartByName(s.part);
            Push(tp != null ? tp : zp, imp * (s.share / sum));
        }
    }

    static void Push(Part p, Vector3 impulse)
    {
        if (p == null || p.rb == null || p.rb.isKinematic) return;
        Vector3 at = p.cap != null ? p.cap.transform.TransformPoint(p.cap.center) : p.rb.worldCenterOfMass;
        p.rb.AddForceAtPosition(impulse, at, ForceMode.Impulse);
    }

    // ---------------- stepping ----------------
    void OnClockStep(float dt) { if (PhysicsClock.Scripted) Step(dt); }
    void FixedUpdate() { if (!PhysicsClock.Scripted) Step(Time.fixedDeltaTime); }   // flag-off path: PhysX still on FixedUpdate

    void Step(float dt)
    {
        if (!Converted || Frozen) return;
        SimTime += dt;
        var dc = RagdollDeath.Cfg.death;
        ApplyDrives(SimTime);
        if (SimTime < RagdollDeath.Cfg.limits.tightenSeconds + dt) TightenLimits(SimTime);
        if (SimTime >= dc.maxSeconds) { Freeze(); return; }
        if (SimTime < dc.rampSeconds) { slowTicks = 0; return; }
        float maxSq = 0f;
        for (int i = 0; i < parts.Count; i++) { var rb = parts[i].rb; if (rb == null) continue; float s = rb.linearVelocity.sqrMagnitude; if (s > maxSq || float.IsNaN(s)) maxSq = float.IsNaN(s) ? float.MaxValue : s; }
        if (maxSq < dc.freezeSpeedSq) slowTicks++; else slowTicks = 0;
        if (slowTicks >= Mathf.Max(1, dc.freezeTicksBelow)) Freeze();
    }

    public void Freeze()
    {
        if (Frozen) return;
        Frozen = true; live = Mathf.Max(0, live - 1);
        for (int i = 0; i < parts.Count; i++) if (parts[i].rb != null) parts[i].rb.isKinematic = true;
    }

    // REVIVE v1 (laneSEAMLESS): put the rig back to what Build() made, so the fighter can stand up and die again. Snapshot taken once, at the start of the first Convert.
    sealed class JointSnap
    {
        public Vector3 anchor, axis, secondaryAxis, connectedAnchor, targetAngularVelocity; public bool auto, worldSpace, swap, coll, pre; public Quaternion target;
        public ConfigurableJointMotion mx, my, mz, ax, ay, az; public SoftJointLimit lowX, highX, limY, limZ; public SoftJointLimitSpring spX, spYZ; public JointDrive dX, dYZ, dS;
        public RotationDriveMode mode; public JointProjectionMode proj; public float projD, projA;
    }
    sealed class PartSnap { public JointSnap j; public int layer; public bool trig, capOn, kin, grav; public float curLowX, curHighX, curY, curZ; public Vector3 lp; public Quaternion lr; }
    PartSnap[] snaps; readonly List<Behaviour> offList = new List<Behaviour>(); public string RestoreWhy = "";
    public bool CanRestore { get { return snaps != null && finCuts.Count == 0; } }

    void Snap()
    {
        if (snaps != null && snaps.Length == parts.Count) return;
        snaps = new PartSnap[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i]; var s = new PartSnap();
            s.layer = p.bone != null ? p.bone.gameObject.layer : 0; s.lp = p.bone != null ? p.bone.localPosition : Vector3.zero; s.lr = p.bone != null ? p.bone.localRotation : Quaternion.identity;
            s.trig = p.cap != null && p.cap.isTrigger; s.capOn = p.cap != null && p.cap.enabled; s.kin = p.rb == null || p.rb.isKinematic; s.grav = p.rb != null && p.rb.useGravity;
            s.curLowX = p.curLowX; s.curHighX = p.curHighX; s.curY = p.curY; s.curZ = p.curZ;
            var j = p.joint;
            if (j != null)
            {
                var q = new JointSnap();
                q.anchor = j.anchor; q.axis = j.axis; q.secondaryAxis = j.secondaryAxis; q.connectedAnchor = j.connectedAnchor; q.targetAngularVelocity = j.targetAngularVelocity; q.auto = j.autoConfigureConnectedAnchor;
                q.worldSpace = j.configuredInWorldSpace; q.swap = j.swapBodies; q.coll = j.enableCollision; q.pre = j.enablePreprocessing; q.target = j.targetRotation;
                q.mx = j.xMotion; q.my = j.yMotion; q.mz = j.zMotion; q.ax = j.angularXMotion; q.ay = j.angularYMotion; q.az = j.angularZMotion;
                q.lowX = j.lowAngularXLimit; q.highX = j.highAngularXLimit; q.limY = j.angularYLimit; q.limZ = j.angularZLimit; q.spX = j.angularXLimitSpring; q.spYZ = j.angularYZLimitSpring;
                q.dX = j.angularXDrive; q.dYZ = j.angularYZDrive; q.dS = j.slerpDrive; q.mode = j.rotationDriveMode; q.proj = j.projectionMode; q.projD = j.projectionDistance; q.projA = j.projectionAngle;
                s.j = q;
            }
            snaps[i] = s;
        }
    }

    /// <summary>REVIVE v1: undo Convert(): bodies kinematic again, capsules triggers on the hurtbox layer, joints as built, behaviours Convert switched off back on, Animator on. False (and RestoreWhy) = the rig cannot be put back; nothing is half-done then except velocities zeroed.</summary>
    public bool Restore()
    {
        if (!Converted) return true;
        if (snaps == null || snaps.Length != parts.Count) { RestoreWhy = "no snapshot (converted before the kit)"; return false; }
        if (finCuts.Count > 0) { RestoreWhy = "a finisher cut the body"; return false; }
        for (int i = 0; i < parts.Count; i++) if (parts[i].bone == null || parts[i].rb == null) { RestoreWhy = "a bone is gone"; return false; }
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i]; var s = snaps[i];
            if (!p.rb.isKinematic) { p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero; }
            p.rb.isKinematic = s.kin; p.rb.useGravity = s.grav;
            p.bone.gameObject.layer = s.layer;
            if (p.cap != null) { p.cap.isTrigger = s.trig; p.cap.enabled = s.capOn; }
            p.curLowX = s.curLowX; p.curHighX = s.curHighX; p.curY = s.curY; p.curZ = s.curZ;
            var j = p.joint; var q = s.j;
            if (j != null && q != null)
            {
                j.autoConfigureConnectedAnchor = q.auto; j.configuredInWorldSpace = q.worldSpace; j.swapBodies = q.swap;
                j.anchor = q.anchor; j.axis = q.axis; j.secondaryAxis = q.secondaryAxis; j.connectedAnchor = q.connectedAnchor;
                j.xMotion = q.mx; j.yMotion = q.my; j.zMotion = q.mz; j.angularXMotion = q.ax; j.angularYMotion = q.ay; j.angularZMotion = q.az;
                j.lowAngularXLimit = q.lowX; j.highAngularXLimit = q.highX; j.angularYLimit = q.limY; j.angularZLimit = q.limZ; j.angularXLimitSpring = q.spX; j.angularYZLimitSpring = q.spYZ;
                j.angularXDrive = q.dX; j.angularYZDrive = q.dYZ; j.slerpDrive = q.dS; j.rotationDriveMode = q.mode; j.targetRotation = q.target; j.targetAngularVelocity = q.targetAngularVelocity;
                j.projectionMode = q.proj; j.projectionDistance = q.projD; j.projectionAngle = q.projA; j.enableCollision = q.coll; j.enablePreprocessing = q.pre;
            }
            p.bone.localPosition = s.lp; p.bone.localRotation = s.lr;
        }
        if (!Frozen) live = Mathf.Max(0, live - 1);
        Converted = false; Frozen = false; SimTime = 0f; slowTicks = 0; ringHead = -1; ringCount = 0;
        for (int i = 0; i < offList.Count; i++) if (offList[i] != null) offList[i].enabled = true;
        offList.Clear();
        if (fighter != null && fighter.anim != null) fighter.anim.enabled = true;
        RestoreWhy = ""; return true;
    }

    // ---------------- proof helpers ----------------
    /// <summary>Signed flexion of a hinge part about its hinge axis: angle from the parent's long direction to the bone's long direction (0 in the rest pose).</summary>
    public float HingeAngle(Part p)
    {
        if (p == null || p.joint == null || p.parent == null) return 0f;
        Vector3 up = p.bone.position - p.parent.bone.position;
        Vector3 lo = p.bone.TransformDirection(p.longLocal);
        Vector3 ax = p.bone.TransformDirection(p.joint.axis);
        if (up.sqrMagnitude < 1e-8f || lo.sqrMagnitude < 1e-8f) return 0f;
        return Vector3.SignedAngle(up.normalized, lo.normalized, ax.normalized);
    }
}
