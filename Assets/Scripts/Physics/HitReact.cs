using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// PHYS2 v1 (PH3 phase P2): hit-reaction PARTIAL ragdoll for the LIVING fighters. PRESENTATION ONLY.
//  The 60 Hz Duel sim stays authoritative: this class never writes DuelFighter / Fighter / CharacterController state or an Animator parameter, uses no UnityEngine.Random and creates no
//  collider (its bodies are invisible to every physics query), so -physproof:simhash must stay byte-identical. The only things it writes are bone ROTATIONS in LateUpdate and its own proxy bodies.
//
//  HOW: next to the pre-built RagdollRig (RagdollDeath.PrebuildAll) every fighter gets a collider-less POWERED COPY of the rig ("proxy": one Rigidbody + ConfigurableJoint per part, built in the rest
//  pose from the rig's own joint axes, limits and masses, parked kinematic). The real bones are never touched by PhysX, so Convert (death), the Animator and FootGround see a normal skeleton.
//  On a landed Hit (ContactRelay) a REACTION starts: the proxies wake at the animated pose with the animated velocity, the slerp drives chase the animated pose (spring / damper per kg from
//  ragdoll.json drive x part gain / damping: body 2 / 0.25, neck + head 1 / 1) and V3's curve dips the motors of the row's parts (ease in, hold, recover) while V3's impulse hits the zone part
//  (head / spine2 / thigh). Every frame, in LateUpdate BEFORE FootGround (order -140: the foot IK and the head clamp still run on top), each part with a blend share gets
//  localRotation(rel. to its part parent) = Slerp(clip, proxy, share x depth): the body sways and comes back; a part with share 0 follows its parent's deviation through the transform hierarchy.
//  Classes: light 0.12 impulse / 0.12 dip / 0.2 blend (V3 'tone it down'), heavy + counter 1. Blocks / parries: feel-clash recoil on the arms (recoil blocked 0.6, perfect 0.9, parried 1.5).
//  Reactions are a list (max maxReactions): per part the LOWEST strength and the HIGHEST share win, each on its own timeline, so a second blow never pops the pose.
// Data: Assets/Data/physics.json "hit" + "clash" (copy in StreamingAssets). hit.enabled=false / missing = nothing is attached. Stops for dead fighters (the rig converts) and when the Animator is off.

// ---------------- data ----------------
[Serializable] public class HitKV { public string key = ""; public float value; }                    // key: a role ("arm"), a part name ("armL") or role$near / role$far (the side the blow came from)
[Serializable] public class HitPair { public string key = "", value = ""; }
[Serializable] public class HitSplit { public string part = "zone"; public float share = 1f; }       // part: "zone" = the zone part, else role / role$near / part name; share is signed (negative = pushed the other way)
[Serializable] public class HitDirRow { public string dir = "front"; public float impulse = 1f, dip = 1f, twist = 1f; }
[Serializable] public class HitTiming { public float easeInS = 0.05f, holdS = 0.5f, backS = 0.35f; }
[Serializable] public class HitRow { public string id = "", about = ""; public HitTiming timing = new HitTiming(); public HitKV[] dips = new HitKV[0], blend = new HitKV[0]; public float impulse, lift, twist; public HitSplit[] split = new HitSplit[0]; public HitDirRow[] dirs = new HitDirRow[0]; }
[Serializable] public class HitClassRow { public string name = ""; public float impulse = 1f, twist = 1f, dip = 1f, blend = 1f, holdS = 1f, backS = 1f; }
[Serializable] public class HitRoleRow { public string part = "", role = "", side = ""; }
[Serializable]
public class HitCfg
{
    public bool enabled; public string mode = "stagger", defaultSide = "R"; public int maxReactions = 4;
    public float frontCos = 0.7071f, limitMarginDeg = 30f, swingCapMarginDeg = 10f, swingCapRampDeg = 10f,   /*INTPHYS v99 swing cap*/ maxDeltaV = 6f, maxLinearSpeed = 12f, maxAngularSpeed = 25f;
    public bool gravity = true, inheritVelocity = true;
    public float linearDamping = 0.05f, angularDamping = 0.4f, maxAngularVelocity = 50f, inertiaFloor = 1e-4f, flinchLeanScale = 1f, springScale = 1f, damperScale = 1f;
    public int solverIterations = 16, solverVelocityIterations = 4;
    public HitRoleRow[] roles = new HitRoleRow[0]; public HitPair[] zoneParts = new HitPair[0]; public string[] heavyMoves = new string[0];
    public HitClassRow[] classes = new HitClassRow[0]; public HitRow[] rows = new HitRow[0];
}
[Serializable] public class ClashKindRow { public string name = ""; public float recoil, absorbed, dip = 1f; }
[Serializable] public class ClashPartRow { public string part = ""; public float weight = 1f, share = 1f; }
[Serializable] public class ClashSide { public string impulsePart = ""; public ClashPartRow[] parts = new ClashPartRow[0]; }
[Serializable] public class ClashSwing { public float light = 1f, heavy = 1.92f, charged = 1.5f; }
[Serializable]
public class ClashCfg
{
    public bool enabled; public float baseImpulse = 7f, lift = 0.8f, minImpulse = 1f, maxImpulse = 24f, weaponRatio = 1f, minScale = 0.25f, maxStrength = 1.92f, lightScale = 0.7f;
    public ClashSwing swing = new ClashSwing(); public HitTiming timing = new HitTiming { easeInS = 0.04f, holdS = 0.12f, backS = 0.3f };
    public ClashKindRow[] kinds = new ClashKindRow[0]; public ClashSide attacker = new ClashSide(), defender = new ClashSide();
}
[Serializable] public class HitProofClass { public string name = "", moveId = ""; public bool heavy, counter; public float minPeakDeg, maxPeakDeg = 90f; }
[Serializable] public class HitProofClash { public string kind = "", type = ""; public bool perfect, heavy; }
[Serializable] public class HitProofTrack { public string row = "track", move = "heavy"; public float separationM = 6f, seconds = 1.4f, maxDeg = 5f, idleSeconds = 1f; public string[] attackParts = new string[0]; }   // PHYSAUDIT: attackParts = parts the shipped rows blend (empty = all)
[Serializable]
public class HitProofCfg
{
    public float separationM = 1.7f, observeExtraS = 0.35f, heavyOverLightMin = 2f, recoverDeg = 1f, recoverExtraS = 0.1f, maxHipsDeltaM = 0.001f, maxFootDriftM = 0.03f, maxAvgStepMs = 0.5f, maxP95StepMs = 1f, clashMinPeakDeg = 0.5f, clashMaxPeakDeg = 60f, sheetCropFraction = 0.5f;
    public int settleTicks = 40, sheetCellWidth = 320, sheetCellHeight = 180;
    public string[] victims = new string[] { "enemy" }, zones = new string[] { "torso" }, sheetZones = new string[] { "torso" }, minPeakZones = new string[0];
    public HitProofClass[] classes = new HitProofClass[0]; public HitProofClash[] clash = new HitProofClash[0]; public HitProofTrack track = new HitProofTrack(); public int[] sheetTicks = new int[] { 0, 10, 20 };
}
[Serializable] public class HitProofWrap { public HitProofCfg hit = new HitProofCfg(); }
[Serializable] public class HitFile { public HitCfg hit = new HitCfg(); public ClashCfg clash = new ClashCfg(); public HitProofWrap proof = new HitProofWrap(); public SwingCfg swing = new SwingCfg(); /* PHYSSWING v1 */ }

[DefaultExecutionOrder(-140)]
public partial class HitReact : MonoBehaviour   // PHYSSWING v1: partial (Scripts/Physics/HitReactSwing.cs)
{
    // ---------------- config loading (own parse of physics.json: unknown keys are ignored by JsonUtility, so PhysicsCfg stays as PHYS1 wrote it) ----------------
    static HitFile file;
    public static HitFile Cfg { get { if (file == null) file = Load(); return file; } }
    public static void Reload() { file = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { file = null; }

    static HitFile Load()
    {
        var d = new HitFile();   // class defaults = disabled
        string json = null;
        string[] paths = { Path.Combine(Application.streamingAssetsPath, "physics.json"), Path.Combine(Application.dataPath, "Data/physics.json") };
        for (int i = 0; i < paths.Length && json == null; i++)
        {
            try { if (File.Exists(paths[i])) json = DataPath.ReadFile(paths[i]).TrimStart('﻿'); }
            catch (Exception ex) { Debug.LogWarning("[HitReact] cannot read " + paths[i] + ": " + ex.Message); }
        }
        if (string.IsNullOrEmpty(json)) return d;
        try
        {
            var r = JsonUtility.FromJson<HitFile>(json);
            if (r == null) return d;
            if (r.hit == null) r.hit = new HitCfg(); if (r.clash == null) r.clash = new ClashCfg(); if (r.proof == null) r.proof = new HitProofWrap(); if (r.proof.hit == null) r.proof.hit = new HitProofCfg();
            if (r.swing == null) r.swing = new SwingCfg(); SwingCfg.Fix(r.swing);   // PHYSSWING v1
            var h = r.hit;
            if (h.roles == null) h.roles = new HitRoleRow[0]; if (h.zoneParts == null) h.zoneParts = new HitPair[0]; if (h.heavyMoves == null) h.heavyMoves = new string[0]; if (h.classes == null) h.classes = new HitClassRow[0]; if (h.rows == null) h.rows = new HitRow[0];
            for (int i = 0; i < h.rows.Length; i++)
            {
                var w = h.rows[i]; if (w == null) { h.rows[i] = new HitRow(); continue; }
                if (w.timing == null) w.timing = new HitTiming(); if (w.dips == null) w.dips = new HitKV[0]; if (w.blend == null) w.blend = new HitKV[0]; if (w.split == null) w.split = new HitSplit[0]; if (w.dirs == null) w.dirs = new HitDirRow[0];
            }
            var c = r.clash;
            if (c.swing == null) c.swing = new ClashSwing(); if (c.timing == null) c.timing = new HitTiming(); if (c.kinds == null) c.kinds = new ClashKindRow[0];
            if (c.attacker == null) c.attacker = new ClashSide(); if (c.defender == null) c.defender = new ClashSide();
            if (c.attacker.parts == null) c.attacker.parts = new ClashPartRow[0]; if (c.defender.parts == null) c.defender.parts = new ClashPartRow[0];
            var p = r.proof.hit;
            if (p.minPeakZones == null) p.minPeakZones = new string[0]; if (p.victims == null) p.victims = new string[0]; if (p.zones == null) p.zones = new string[0]; if (p.sheetZones == null) p.sheetZones = new string[0]; if (p.classes == null) p.classes = new HitProofClass[0];
            if (p.clash == null) p.clash = new HitProofClash[0]; if (p.track == null) p.track = new HitProofTrack(); if (p.sheetTicks == null) p.sheetTicks = new int[] { 0 };
            return r;
        }
        catch (Exception ex) { Debug.LogError("[HitReact] parse failed: " + ex.Message); return d; }
    }

    // ---------------- attach ----------------
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { SceneManager.sceneLoaded -= OnSceneLoaded; SceneManager.sceneLoaded += OnSceneLoaded; AttachAll(); }
    static void OnSceneLoaded(Scene s, LoadSceneMode m) { AttachAll(); }

    static void AttachAll() { AttachAll(null); }   // ZONES-ATTACH v1 (INT107)
    public static void AttachTo(GameObject root) { AttachAll(root); }   // same want-check, one object (ZoneRuntime.SpawnOne for a prefab creature)
    static void AttachAll(GameObject root){
        var c = Cfg;
        if (c == null || !(c.hit.enabled || c.swing.enabled) || !PhysicsClock.PhysicsOn || !PhysicsClock.Scripted) return;   /* PHYSSWING v1: swing sub-switch also attaches the proxy */   // INTPHYS v99: master / clock switch off = no proxies
        var all = (root != null ? root.GetComponentsInChildren<DuelFighter>(true) : FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
        for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].GetComponent<HitReact>() == null) all[i].gameObject.AddComponent<HitReact>();
    }

    /// <summary>Flinch.cs asks how much of its visual-pivot lean to keep while this fighter's partial ragdoll is showing (hit.flinchLeanScale at full depth, 1 when idle).</summary>
    public static float LeanScale(DuelFighter f)
    {
        if (f == null) return 1f;
        var h = f.GetComponent<HitReact>(); if (h == null || !h.active) return 1f;
        return 1f - (1f - Mathf.Clamp01(Cfg.hit.flinchLeanScale)) * Mathf.Clamp01(h.DepthMax);
    }

    // ---------------- reactions ----------------
    class Reaction
    {
        public float t, ease, hold, back, dipMul = 1f, blendMul = 1f; public float[] dip, share; public string tag = "";
        public float Total { get { return ease + hold + back; } }
        public float Depth()
        {
            if (t < 0f) return 0f;
            if (t < ease) return ease > 1e-5f ? t / ease : 1f;
            if (t < ease + hold) return 1f;
            if (t < ease + hold + back) return back > 1e-5f ? 1f - (t - ease - hold) / back : 0f;
            return 0f;
        }
    }
    struct Pending { public int i; public Vector3 imp; public Vector3 at; public bool hasAt; }   // PHYSWEAPON v106

    // ---------------- state ----------------
    DuelFighter df; RagdollRig rig;
    bool ready, active, hadPrev, warned;
    public bool Ready { get { return ready; } }
    public bool ProxiesActive { get { return active; } }
    public int ActiveCount { get { return reactions.Count - SwingCounted; } }   // PHYSSWING v1: the persistent situation row is not a hit reaction
    public float DepthMax { get; private set; }
    public float BuildMs { get; private set; }
    public float LastDuration { get; private set; }
    public float[] DevNowDeg, DevPeakDeg; public float HipsDeltaM, HipsDeltaPeakM;
    public int PartCount { get { return n; } }
    public string PartName(int i) { return pname[i]; }
    public Transform PartBone(int i) { return rig.parts[i].bone; }
    public Vector3 PartAnimPos(int i) { return animPos[i]; }   // INTPHYS v99: the clip's own world position of the part this frame (recorded before the blend)
    public int PartIndex(string name) { int k; return byName.TryGetValue(name, out k) ? k : -1; }
    public string LastRow = "", LastClass = "", LastDir = "";
    readonly List<Reaction> reactions = new List<Reaction>(8);
    readonly List<Pending> pending = new List<Pending>(8);
    readonly Dictionary<string, int> byName = new Dictionary<string, int>();
    int n; GameObject root;
    Rigidbody[] rb; ConfigurableJoint[] jt; Quaternion[] toJoint, restLocal; int[] pi; float[] mass, springBase, damperBase, strength, share;
    string[] pname, prole; char[] pside;
    Quaternion[] animRot, animRel, prevRot; Vector3[] animPos, prevPos;
    float[] swingCap;   // INTPHYS v99 (lanePITCUT): per part the widest joint limit the proxy has (deg)

    void Awake() { df = GetComponent<DuelFighter>(); }
    void OnEnable() { ContactRelay.OnEvent -= OnEvent; ContactRelay.OnEvent += OnEvent; if (df == null) df = GetComponent<DuelFighter>(); }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; if (ready) { reactions.Clear(); Park(); } }
    void OnDestroy() { ContactRelay.OnEvent -= OnEvent; if (root != null) Destroy(root); }

    // ---------------- build ----------------
    bool TryInit()
    {
        if (ready) return true;
        if (rig == null) rig = GetComponent<RagdollRig>();
        if (rig == null || !rig.Built || rig.parts.Count < 3) return false;
        try { Build(); }
        catch (Exception ex) { Debug.LogError("[HitReact] build failed on " + name + ": " + ex); ready = false; enabled = false; if (root != null) Destroy(root); }
        return ready;
    }

    void Build()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var h = Cfg.hit; var parts = rig.parts; n = parts.Count;
        rb = new Rigidbody[n]; jt = new ConfigurableJoint[n]; toJoint = new Quaternion[n]; restLocal = new Quaternion[n]; pi = new int[n]; mass = new float[n]; springBase = new float[n]; damperBase = new float[n]; strength = new float[n]; share = new float[n];
        pname = new string[n]; prole = new string[n]; pside = new char[n];
        animRot = new Quaternion[n]; animRel = new Quaternion[n]; prevRot = new Quaternion[n]; animPos = new Vector3[n]; prevPos = new Vector3[n]; swingCap = new float[n];   // INTPHYS v99
        DevNowDeg = new float[n]; DevPeakDeg = new float[n];
        byName.Clear();
        for (int i = 0; i < n; i++) { pname[i] = parts[i].cfg.name; byName[pname[i]] = i; }
        for (int i = 0; i < n; i++)
        {
            pi[i] = i == 0 ? -1 : (parts[i].parent != null ? Mathf.Max(0, parts.IndexOf(parts[i].parent)) : 0);
            prole[i] = pname[i]; pside[i] = '\0';
            for (int r = 0; r < h.roles.Length; r++) { var rr = h.roles[r]; if (rr != null && rr.part == pname[i]) { prole[i] = rr.role; pside[i] = string.IsNullOrEmpty(rr.side) ? '\0' : rr.side[0]; break; } }
        }
        var d = RagdollDeath.Cfg; var dr = d.drive;
        float sizeScale = df != null ? Mathf.Max(0.01f, df.scale) : 1f;
        root = new GameObject("HitReactProxy_" + name); root.hideFlags = HideFlags.DontSave;
        var wpos = new Vector3[n]; var wrot = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            var p = parts[i];
            restLocal[i] = p.restLocal; mass[i] = Mathf.Max(0.05f, p.mass);
            if (i == 0) { wrot[i] = p.bone.rotation; wpos[i] = p.bone.position; }
            else
            {
                var par = parts[pi[i]].bone;
                wrot[i] = wrot[pi[i]] * restLocal[i];
                wpos[i] = wpos[pi[i]] + wrot[pi[i]] * (Quaternion.Inverse(par.rotation) * (p.bone.position - par.position));
            }
            var go = new GameObject(pname[i]); go.transform.SetParent(root.transform, false); go.transform.SetPositionAndRotation(wpos[i], wrot[i]);
            var body = go.AddComponent<Rigidbody>();
            body.mass = mass[i]; body.useGravity = false; body.isKinematic = true; body.interpolation = RigidbodyInterpolation.None; body.detectCollisions = false;
            body.linearDamping = h.linearDamping; body.angularDamping = h.angularDamping; body.maxAngularVelocity = Mathf.Max(1f, h.maxAngularVelocity);
            body.solverIterations = Mathf.Max(1, h.solverIterations); body.solverVelocityIterations = Mathf.Max(1, h.solverVelocityIterations); body.sleepThreshold = 0f;
            Vector3 lw = p.bone.TransformVector(p.longLocal); float len = Mathf.Max(0.02f, lw.magnitude);
            float rad = Mathf.Max(0.005f, p.cfg.radiusM * sizeScale);
            body.centerOfMass = (p.longLocal.sqrMagnitude > 1e-8f ? p.longLocal.normalized : Vector3.up) * (len * 0.5f);
            float inertia = Mathf.Max(h.inertiaFloor, mass[i] * (rad * rad * 0.25f + len * len / 12f)) * Mathf.Max(1f, p.cfg.inertiaMul);   // no collider shape here: set the tensor by hand (isotropic = stable, twist is over-stiff on purpose)
            body.inertiaTensor = Vector3.one * inertia; body.inertiaTensorRotation = Quaternion.identity;
            rb[i] = body;
            springBase[i] = dr.springPerKg * p.mass * p.cfg.gain * Mathf.Max(0f, h.springScale); damperBase[i] = dr.damperPerKg * p.mass * p.cfg.damping * Mathf.Max(0f, h.damperScale);   // the death ramp shares drive / gain / damping; these two scale the living rig only
        }
        float mg = Mathf.Max(0f, h.limitMarginDeg);
        for (int i = 1; i < n; i++)
        {
            var p = parts[i]; if (p.joint == null) continue;
            var j = rb[i].gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = rb[pi[i]];
            j.autoConfigureConnectedAnchor = false; j.anchor = Vector3.zero;
            Quaternion rp = Quaternion.Inverse(parts[pi[i]].bone.rotation);
            j.connectedAnchor = rp * (p.bone.position - parts[pi[i]].bone.position);
            j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
            j.axis = p.joint.axis; j.secondaryAxis = p.joint.secondaryAxis;
            j.lowAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(p.lowX - mg, -177f, 177f) };
            j.highAngularXLimit = new SoftJointLimit { limit = Mathf.Clamp(p.highX + mg, -177f, 177f) };
            j.angularYLimit = new SoftJointLimit { limit = Mathf.Clamp(p.limY + mg, 0f, 177f) };
            j.angularZLimit = new SoftJointLimit { limit = Mathf.Clamp(p.limZ + mg, 0f, 177f) };
            swingCap[i] = Mathf.Max(Mathf.Max(Mathf.Abs(j.lowAngularXLimit.limit), Mathf.Abs(j.highAngularXLimit.limit)), Mathf.Max(j.angularYLimit.limit, j.angularZLimit.limit));   // INTPHYS v99
            j.rotationDriveMode = RotationDriveMode.Slerp; j.projectionMode = JointProjectionMode.PositionAndRotation; j.enableCollision = false; j.enablePreprocessing = false;
            Vector3 right = j.axis, fwd = Vector3.Cross(j.axis, j.secondaryAxis).normalized, up = Vector3.Cross(fwd, right).normalized;
            toJoint[i] = Quaternion.LookRotation(fwd, up);
            jt[i] = j;
        }
        SwingBuild();   // PHYSSWING v1
        BuildMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        ready = true;
    }

    // ---------------- events ----------------
    bool Live()
    {
        if (FinPhys.SuppressHit) return false;   /* FINPHYS v97: no partial ragdoll while a finisher scene runs */ if (!ready || df == null || df.Dead || rig == null || rig.Converted) return false;
        var f = rig.fighter; if (f != null && f.anim != null && !f.anim.enabled) return false;
        return isActiveAndEnabled && PhysicsClock.PhysicsOn && PhysicsClock.Scripted;   // INTPHYS v99
    }

    void OnEvent(CombatEvent e)
    {
        try
        {
            if (!ready || !Live()) return;
            if (e.type == "Hit") { if (e.target == df && Cfg.hit.enabled) TriggerHit(e); }   // PHYSSWING v1: a swing-only attach starts no hit reaction
            else if (e.type == "Blocked" || e.type == "Parried" || e.type == "GuardBroken") { if (e.actor == df || e.target == df) TriggerClash(e); }
        }
        catch (Exception ex) { if (!warned) { warned = true; Debug.LogError("[HitReact] event failed (further errors muted): " + ex); } }
    }

    HitClassRow ClassByName(string nm)
    {
        var cs = Cfg.hit.classes;
        for (int i = 0; i < cs.Length; i++) if (cs[i] != null && cs[i].name == nm) return cs[i];
        for (int i = 0; i < cs.Length; i++) if (cs[i] != null && cs[i].name == "heavy") return cs[i];
        return new HitClassRow();
    }

    string ClassName(CombatEvent e)
    {
        if (e.counter) return "counter";
        if (e.heavy) return "heavy";
        var hm = Cfg.hit.heavyMoves;
        for (int i = 0; i < hm.Length; i++) if (hm[i] == e.moveId) return "heavy";
        return "light";
    }

    HitRow RowById(string id)
    {
        var rs = Cfg.hit.rows;
        for (int i = 0; i < rs.Length; i++) if (rs[i] != null && rs[i].id == id) return rs[i];
        return null;
    }

    string ZoneSpec(string loc)
    {
        var zp = Cfg.hit.zoneParts;
        for (int i = 0; i < zp.Length; i++) if (zp[i] != null && zp[i].key == loc) return zp[i].value;
        return "spine2";
    }

    Vector3 BlowDir(DuelFighter attacker)
    {
        Vector3 d = attacker != null ? attacker.transform.forward : -transform.forward;
        d.y = 0f; if (d.sqrMagnitude < 1e-4f) { d = -transform.forward; d.y = 0f; }
        if (d.sqrMagnitude < 1e-4f) d = Vector3.forward;
        return d.normalized;
    }

    string DirName(Vector3 dir)
    {
        Vector3 f = transform.forward; f.y = 0f; f.Normalize();
        float c = Vector3.Dot(dir, f);
        if (c <= -Cfg.hit.frontCos) return "front";   // travels into his chest
        if (c >= Cfg.hit.frontCos) return "back";
        return Vector3.Dot(dir, transform.right) > 0f ? "left" : "right";   // pushed toward his right = it came from his left
    }

    char NearSide(string dirName)
    {
        if (dirName == "left") return 'L';
        if (dirName == "right") return 'R';
        string ds = Cfg.hit.defaultSide; return string.IsNullOrEmpty(ds) ? 'R' : ds[0];
    }

    static bool SideOk(char side, string suffix, char near)
    {
        if (side == '\0') return true;
        bool isNear = side == near;
        return suffix == "far" ? !isNear : isNear;
    }

    bool Matches(string key, int i, char near)
    {
        if (string.IsNullOrEmpty(key)) return false;
        int d = key.IndexOf('$'); string role = d >= 0 ? key.Substring(0, d) : key, suf = d >= 0 ? key.Substring(d + 1) : "";
        if (role != prole[i] && role != pname[i]) return false;
        return suf.Length == 0 || SideOk(pside[i], suf, near);
    }

    int ResolvePart(string spec, char near)
    {
        if (string.IsNullOrEmpty(spec)) return -1;
        for (int i = 0; i < n; i++) if (Matches(spec, i, near)) return i;
        int k; return byName.TryGetValue(spec, out k) ? k : -1;
    }

    /// <summary>Start the V3 reaction for a landed blow (called by the ContactRelay handler; the proof calls it with synthetic events).</summary>
    public void TriggerHit(CombatEvent e)
    {
        if (!ready || !Live()) return;
        if (!Cfg.hit.enabled) return;   // PHYSKNOCK v1: a knock-only attach (hit.enabled false) must not start stagger reactions
        var h = Cfg.hit;
        string loc = string.IsNullOrEmpty(e.location) ? "torso" : e.location;
        HitRow row = (h.mode == "zone" || (HitPhysics.On && HitPhysics.Cfg.zoneRows)) ? (RowById(loc) ?? RowById("stagger")) : RowById("stagger");   // HITPHYS v1: per-zone rows when hit_physics.json is on
        if (row == null) return;
        string cn = ClassName(e);
        { Vector3 hbd = BlowDir(e.actor); var hcl = PhysTune(e, row, ClassByName(cn), cn, hbd); StartRow(row, hcl, hbd, ZoneSpec(loc)); PhysAfter(e, hbd); HitDirStart(e); /* HITDIR v1 */ }   // HITPHYS v1: per hit type x fighter (HitReactPhys.cs); identical to StartRow(row, ClassByName(cn), BlowDir(e.actor), ZoneSpec(loc)) when off
        LastClass = cn;
    }

    /// <summary>Proof / tools: start a named row directly (blow direction in world space, zone part spec like "spine2").</summary>
    public bool TriggerRow(string rowId, string className, Vector3 blowDir, string zoneSpec)
    {
        if (!ready || !Live()) return false;
        var row = RowById(rowId); if (row == null) return false;
        blowDir.y = 0f; if (blowDir.sqrMagnitude < 1e-6f) blowDir = Vector3.forward;
        StartRow(row, ClassByName(className), blowDir.normalized, zoneSpec);
        return true;
    }

    void StartRow(HitRow row, HitClassRow cls, Vector3 dir, string zoneSpec)
    {
        var h = Cfg.hit;
        string dn = DirName(dir); char near = NearSide(dn);
        HitDirRow dd = null;
        for (int i = 0; i < row.dirs.Length; i++) if (row.dirs[i] != null && row.dirs[i].dir == dn) { dd = row.dirs[i]; break; }
        float dImp = dd != null ? dd.impulse : 1f, dDip = dd != null ? dd.dip : 1f, dTw = dd != null ? dd.twist : 1f;
        var r = new Reaction { tag = row.id, ease = Mathf.Max(0f, row.timing.easeInS), hold = Mathf.Max(0f, row.timing.holdS * cls.holdS), back = Mathf.Max(0f, row.timing.backS * cls.backS), dipMul = cls.dip * dDip, blendMul = cls.blend };
        r.dip = new float[n]; r.share = new float[n];
        for (int i = 0; i < n; i++) r.dip[i] = 1f;
        for (int k = 0; k < row.dips.Length; k++) { var kv = row.dips[k]; if (kv == null) continue; for (int i = 0; i < n; i++) if (Matches(kv.key, i, near)) r.dip[i] = Mathf.Min(r.dip[i], Mathf.Clamp01(kv.value)); }
        for (int k = 0; k < row.blend.Length; k++) { var kv = row.blend[k]; if (kv == null) continue; for (int i = 0; i < n; i++) if (Matches(kv.key, i, near)) r.share[i] = Mathf.Max(r.share[i], Mathf.Clamp01(kv.value)); }
        AddReaction(r);
        LastRow = row.id; LastDir = dn;
        // the blow: signed shares of the row's total impulse along the attacker's facing, lift up
        float imp = row.impulse * cls.impulse * dImp, lift = row.lift * cls.impulse;
        if (imp > 1e-5f || Mathf.Abs(lift) > 1e-5f)
            for (int s = 0; s < row.split.Length; s++)
            {
                var sp = row.split[s]; if (sp == null) continue;
                int idx = ResolvePart(sp.part == "zone" ? zoneSpec : sp.part, near); if (idx < 0) continue;
                PushPhys(idx, dir * (imp * sp.share) + Vector3.up * (lift * sp.share));   // HITPHYS v1: at the blow point when the type says so, else Push
            }
        float tw = row.twist * cls.twist * dTw;
        if (tw > 1e-5f && (dn == "left" || dn == "right"))
        {
            Vector3 perp = Vector3.Cross(Vector3.up, dir) * (dn == "left" ? 1f : -1f);   // a couple: chest one way, belly the other
            int c = ResolvePart("spine2", near), b = ResolvePart("spine", near);
            if (c >= 0) Push(c, perp * tw); if (b >= 0) Push(b, -perp * tw);
        }
    }

    void PushAt(int i, Vector3 imp, Vector3 at) { float m = mass[i] * Mathf.Max(0.1f, Cfg.hit.maxDeltaV); if (imp.sqrMagnitude > m * m) imp = imp.normalized * m; if (float.IsNaN(imp.x) || float.IsNaN(imp.y) || float.IsNaN(imp.z) || float.IsNaN(at.x) || float.IsNaN(at.y) || float.IsNaN(at.z)) return; if (active && !rb[i].isKinematic) rb[i].AddForceAtPosition(imp, at, ForceMode.Impulse); else pending.Add(new Pending { i = i, imp = imp, at = at, hasAt = true }); }   // PHYSWEAPON v106   // hc:ok same min-deltaV guard as Push
    void AddReaction(Reaction r)
    {
        int cap = Mathf.Max(1, Cfg.hit.maxReactions);
        while (reactions.Count >= cap) reactions.RemoveAt(0);
        reactions.Add(r);
        LastDuration = Mathf.Max(LastDuration, r.Total);
    }

    void Push(int i, Vector3 imp)
    {
        float m = mass[i] * Mathf.Max(0.1f, Cfg.hit.maxDeltaV);
        if (imp.sqrMagnitude > m * m) imp = imp.normalized * m;
        if (float.IsNaN(imp.x) || float.IsNaN(imp.y) || float.IsNaN(imp.z)) return;
        if (active && !rb[i].isKinematic) rb[i].AddForce(imp, ForceMode.Impulse); else pending.Add(new Pending { i = i, imp = imp });
    }

    /// <summary>Block / parry / broken guard: feel-clash recoil on the arms of the attacker (event.actor) and the defender (event.target).</summary>
    public void TriggerClash(CombatEvent e)
    {
        var c = Cfg.clash;
        if (!c.enabled || !ready || !Live()) return;
        string kind = e.type == "Parried" ? "parried" : (e.type == "GuardBroken" ? "broken" : (e.perfect ? "perfect" : "blocked"));
        ClashKindRow k = null; for (int i = 0; i < c.kinds.Length; i++) if (c.kinds[i] != null && c.kinds[i].name == kind) { k = c.kinds[i]; break; }
        if (k == null) return;
        bool attacker = e.actor == df, defender = e.target == df;
        if (attacker == defender) return;
        var side = attacker ? c.attacker : c.defender;
        float strength = e.heavy ? c.swing.heavy : c.swing.light;
        float scale = Mathf.Clamp(strength / Mathf.Max(0.01f, c.maxStrength), c.minScale, 1f);
        float ts = e.heavy ? 1f : Mathf.Max(0.05f, c.lightScale);
        var r = new Reaction { tag = "clash_" + kind, ease = c.timing.easeInS * ts, hold = c.timing.holdS * ts, back = c.timing.backS * ts, dipMul = 1f, blendMul = 1f };
        r.dip = new float[n]; r.share = new float[n];
        for (int i = 0; i < n; i++) r.dip[i] = 1f;
        for (int q = 0; q < side.parts.Length; q++)
        {
            var pr = side.parts[q]; if (pr == null) continue;
            int idx = ResolvePart(pr.part, 'R'); if (idx < 0) continue;
            r.dip[idx] = Mathf.Min(r.dip[idx], Mathf.Clamp(1f - (1f - k.dip) * pr.weight * scale, 0.01f, 1f));
            r.share[idx] = Mathf.Max(r.share[idx], Mathf.Clamp01(pr.share));
        }
        AddReaction(r);
        LastRow = r.tag; LastClass = e.heavy ? "heavy" : "light"; LastDir = attacker ? "attacker" : "defender";
        Vector3 dir = BlowDir(attacker ? df : e.actor);   // the blow's line is the ATTACKER's facing
        float mag = Mathf.Clamp(c.baseImpulse * c.weaponRatio * strength * (attacker ? k.recoil : k.absorbed), c.minImpulse, c.maxImpulse);
        if ((attacker ? k.recoil : k.absorbed) <= 0f) mag = 0f;
        mag *= ClashMul(kind, attacker, e);   // HITPHYS v1: x kind x fighter x mass (1 when off)
        int ip = ResolvePart(side.impulsePart, 'R');
        if (ip >= 0 && mag > 0f) { Vector3 cimp = (attacker ? -dir : dir) * mag + Vector3.up * c.lift, cpt; if (PhysWeapon.ClashPoint(e, rb[ip], out cpt)) PushAt(ip, cimp, cpt); else Push(ip, cimp); /*PHYSWEAPON v106*/ }   // the weapon arm is thrown back, the guard arm driven back along the blow
    }

    // ---------------- per frame ----------------
    void LateUpdate()
    {
        if (!ready) { if ((Time.frameCount & 15) == 0) TryInit(); return; }
        if (!Live())
        {
            if (active || reactions.Count > 0) { reactions.Clear(); Park(); }
            hadPrev = false;   // the next live frame has no valid previous pose (no inherited velocity from a stale sample)
            return;
        }
        if (!BonesOk())   // the model was swapped / a bone destroyed after the rig was built (the rig keeps its death clip in that case too): stop for good
        {
            Debug.LogWarning("[HitReact] a bone of " + name + " is gone; the partial hit ragdoll is off for this fighter.");
            reactions.Clear(); Park(); ready = false; enabled = false; return;
        }
        float dt = PresentationClock.SimDelta(Time.deltaTime);
        Record();
        for (int k = reactions.Count - 1; k >= 0; k--) { var r = reactions[k]; r.t += dt; if (r.t >= r.Total) reactions.RemoveAt(k); }
        SwingTick(dt);   // PHYSSWING v1: situation row + lead sampling
        KnockTick(dt);   // PHYSKNOCK v1: knockdown / get-up blend, a pure function of the sim tick (returns at once when knock.enabled is false)
        if (reactions.Count == 0)
        {
            if (active) Park();
            DepthMax = 0f; for (int i = 0; i < n; i++) DevNowDeg[i] = 0f; HipsDeltaM = 0f;
            Remember();
            return;
        }
        if (!active) Activate(dt);
        Feed();
        Compose();
        if (!Blend()) { reactions.Clear(); Park(); }
        else Measure();
        Remember();
    }

    bool BonesOk()
    {
        var parts = rig.parts;
        for (int i = 0; i < n; i++) if (parts[i].bone == null) return false;
        return true;
    }

    void Record()
    {
        var parts = rig.parts;
        for (int i = 0; i < n; i++) { var b = parts[i].bone; animRot[i] = b.rotation; animPos[i] = b.position; }
        animRel[0] = Quaternion.identity;
        for (int i = 1; i < n; i++) animRel[i] = Quaternion.Inverse(animRot[pi[i]]) * animRot[i];
    }

    void Remember() { for (int i = 0; i < n; i++) { prevRot[i] = animRot[i]; prevPos[i] = animPos[i]; } hadPrev = true; }

    void Activate(float dt)
    {
        var h = Cfg.hit;
        for (int i = 0; i < n; i++)
        {
            rb[i].isKinematic = true;
            rb[i].transform.SetPositionAndRotation(animPos[i], animRot[i]); rb[i].position = animPos[i]; rb[i].rotation = animRot[i];
        }
        for (int i = 1; i < n; i++)
        {
            if (jt[i] == null) continue;
            jt[i].connectedAnchor = Quaternion.Inverse(animRot[pi[i]]) * (animPos[i] - animPos[pi[i]]);
            rb[i].isKinematic = false; rb[i].useGravity = h.gravity;
            Vector3 v = Vector3.zero, w = Vector3.zero;
            if (h.inheritVelocity && hadPrev && dt > 1e-5f)
            {
                v = (animPos[i] - prevPos[i]) / dt;
                Quaternion dq = animRot[i] * Quaternion.Inverse(prevRot[i]); float ang; Vector3 axis; dq.ToAngleAxis(out ang, out axis);
                if (ang > 180f) ang -= 360f;
                if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x) && axis.sqrMagnitude > 1e-8f) w = axis.normalized * (ang * Mathf.Deg2Rad / dt);
                float sp = v.magnitude; if (sp > h.maxLinearSpeed && sp > 1e-5f) v *= h.maxLinearSpeed / sp;
                float asp = w.magnitude; if (asp > h.maxAngularSpeed && asp > 1e-5f) w *= h.maxAngularSpeed / asp;
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) v = Vector3.zero;
                if (float.IsNaN(w.x) || float.IsNaN(w.y) || float.IsNaN(w.z)) w = Vector3.zero;
            }
            rb[i].linearVelocity = v; rb[i].angularVelocity = w; rb[i].WakeUp();
        }
        for (int k = 0; k < pending.Count; k++) { var p = pending[k]; if (p.i > 0 && !rb[p.i].isKinematic) { if (p.hasAt) rb[p.i].AddForceAtPosition(p.imp, p.at, ForceMode.Impulse); else rb[p.i].AddForce(p.imp, ForceMode.Impulse); /*PHYSWEAPON v106*/ } }
        pending.Clear();
        active = true; SwingActivated();   // PHYSSWING v1
    }

    void Park()
    {
        if (ready) for (int i = 1; i < n; i++) if (rb[i] != null && !rb[i].isKinematic) rb[i].isKinematic = true;
        active = false; pending.Clear(); DepthMax = 0f;
        if (ready) { for (int i = 0; i < n; i++) DevNowDeg[i] = 0f; HipsDeltaM = 0f; }
    }

    // proxy hips follow the animated hips; every joint target = the animated pose of its part relative to its part parent (the well-known joint-space conversion), anchors follow the clip
    void Feed()
    {
        rb[0].MovePosition(animPos[0]); rb[0].MoveRotation(animRot[0]);
        for (int i = 1; i < n; i++)
        {
            var j = jt[i]; if (j == null) continue;
            j.connectedAnchor = Quaternion.Inverse(animRot[pi[i]]) * (animPos[i] - animPos[pi[i]]);
            j.targetRotation = Quaternion.Inverse(toJoint[i]) * Quaternion.Inverse(HitDirRel(i, SwingRel(i))) * restLocal[i] * toJoint[i];   // PHYSSWING v1: SwingRel = animRel unless the lead is on
        }
    }

    void Compose()
    {
        for (int i = 0; i < n; i++) { strength[i] = 1f; share[i] = 0f; }
        float dm = 0f;
        for (int k = 0; k < reactions.Count; k++)
        {
            var r = reactions[k]; float dep = r.Depth(); if (dep <= 0f) continue;
            if (dep > dm && r != swingR) dm = dep;   // PHYSSWING v1: the situation row does not count as a hit depth (Flinch lean)
            for (int i = 0; i < n; i++)
            {
                float s = Mathf.Clamp(1f - (1f - r.dip[i]) * r.dipMul * dep, 0.01f, 1f);
                if (s < strength[i]) strength[i] = s;
                float b = Mathf.Clamp01(r.share[i] * r.blendMul * dep);
                if (b > share[i]) share[i] = b;
            }
        }
        // INTPHYS v99 (lanePITCUT): a part whose CLIP pose is already near its joint limit (overhead arm ~175 of 177 deg) is not driven: the target flips to the far side of the cone (q vs -q). Ramps share to 0 over swingCapRampDeg;
        // swingCapMarginDeg < 0 switches it off. A part with share 0 follows its parent through the hierarchy, i.e. shows the clip.
        { var hc = Cfg.hit; float cm = hc.swingCapMarginDeg, rw = Mathf.Max(0.01f, hc.swingCapRampDeg);
          if (cm >= 0f) for (int i = 1; i < n; i++) { if (share[i] <= 0f || jt[i] == null) continue; float sw = Quaternion.Angle(restLocal[i], animRel[i]); share[i] *= Mathf.Clamp01((swingCap[i] - cm - sw) / rw); } }
        SwingReach();   // PHYSSWING v1: limit-aware share
        DepthMax = dm;
        for (int i = 1; i < n; i++)
        {
            var j = jt[i]; if (j == null) continue;
            var drive = new JointDrive { positionSpring = springBase[i] * strength[i], positionDamper = damperBase[i] * SwingDamp(i), maximumForce = float.MaxValue };
            j.slerpDrive = drive;
        }
    }

    bool Blend()
    {
        var parts = rig.parts;
        for (int i = 1; i < n; i++)
        {
            float s = share[i]; if (s <= 1e-4f) continue;
            Quaternion phys = PhysRel(i);   // PHYSSWING v1
            if (float.IsNaN(phys.x) || float.IsNaN(phys.y) || float.IsNaN(phys.z) || float.IsNaN(phys.w)) { Debug.LogWarning("[HitReact] NaN in the proxy of " + pname[i] + " on " + name + "; reaction dropped."); return false; }
            Quaternion rel = Quaternion.Slerp(animRel[i], phys, SwingShare(i, phys, Mathf.Min(1f, s)));   // PHYSSWING v1
            rel = KnockRel(i, rel); parts[i].bone.rotation = parts[pi[i]].bone.rotation * rel;   // PHYSKNOCK v1: floor guard + deviation cap + rate limit (returns rel at once when no knock shows)
        }
        return true;
    }

    void Measure()
    {
        var parts = rig.parts;
        for (int i = 0; i < n; i++) { float a = Quaternion.Angle(parts[i].bone.rotation, animRot[i]); DevNowDeg[i] = a; if (a > DevPeakDeg[i]) DevPeakDeg[i] = a; }
        HipsDeltaM = (parts[0].bone.position - animPos[0]).magnitude; if (HipsDeltaM > HipsDeltaPeakM) HipsDeltaPeakM = HipsDeltaM;
    }

    /// <summary>Proof: forget the peaks (call right before a blow).</summary>
    public void ResetPeaks() { if (DevPeakDeg == null) return; for (int i = 0; i < n; i++) DevPeakDeg[i] = 0f; HipsDeltaPeakM = 0f; LastDuration = 0f; }
    public float PeakMaxDeg { get { float m = 0f; if (DevPeakDeg != null) for (int i = 0; i < n; i++) m = Mathf.Max(m, DevPeakDeg[i]); return m; } }
    public float NowMaxDeg { get { float m = 0f; if (DevNowDeg != null) for (int i = 0; i < n; i++) m = Mathf.Max(m, DevNowDeg[i]); return m; } }
    public string PeakPartName { get { float m = -1f; int k = 0; if (DevPeakDeg != null) for (int i = 0; i < n; i++) if (DevPeakDeg[i] > m) { m = DevPeakDeg[i]; k = i; } return n > 0 ? pname[k] : ""; } }
    /// <summary>Proof: drop every reaction and park the proxies (between cases).</summary>
    public void ClearAll() { reactions.Clear(); if (ready) Park(); }
}
