using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// FINPHYS v97: physics in the finishers, the V3 way (Frankendom V3 data/physics.json `finisherCuts` + `cut` + `kill`, physics package role table / world.json / states.json), ported as C# on Unity PhysX.
// PRESENTATION ONLY. The 60 Hz sim stays authoritative: nothing here is read by the Duel, all stepping rides PhysicsClock (Script mode, one Physics.Simulate per sim tick), no Time.timeScale.
// What V3 adds on top of V1's finishers ("only the physics ragdoll cuts", FA3), and where it lives here:
//   * the victim's body goes into the PHYS1 death ragdoll at the finisher's beat (row.ragdollAtS after the contact) with the blow's momentum (V3 kill 70 N s x row.push, zone part, lift) - FinPhys.DieAt / OnContact -> RagdollRig.Fin* fields;
//   * a cut removes the carried parts from the ragdoll (V3 rd.cut = the cut role's joint removed) - FinPhys.AfterCut -> RagdollRig.CutOff;
//   * the cut piece is a physics body: mass = the carried parts' mass (V3 role table), V3 push (cut.kill 14 N s + lift 5 along the blow), V3 world damping / friction / restitution, 2 sub-steps for 2 s after the cut
//     (V3 cut_high_rate_ticks 120), rolling resistance on the ground (V3 0.8) - PieceBody;
//   * the split-crown halves become two bodies after they opened (V3 step 2: independent halves) - FinPhys.ReleaseHalves;
//   * PHYS2's hit-reaction partial ragdoll is off while a finisher scene runs - FinPhys.SuppressHit.
// V1FIN's scripted head arc stays the DEFAULT for the head (units[head].mode "arc"); "physics" is the json option (or -finphys:head=physics) so VERIFY can compare both.
// Data: physics.json `finphys` (copy in StreamingAssets). Missing block / enabled=false / -finphys:off = exactly the V1FIN + PHYS1 + PHYS2 behaviour.
[Serializable] public class FinKick { public float impulse = 14f, lift = 5f; }
[Serializable]
public class FinUnitRow
{
    public string unit = "", v3 = "";              // finishers.json cuts.units id; the physics package's severable id (provenance)
    public string cutPart = "";                    // ragdoll.json part whose joint the cut removes ("" = nothing leaves the ragdoll)
    public string[] carries = new string[0];       // ragdoll.json parts the piece carries (V3 carries_roles): their mass is the piece's, their capsules its colliders
    public string mode = "physics";                // physics | arc (V1FIN scripted head arc) | script (V1FIN OpenedHalf) | off
    public float impulse = -1f, lift = -1f;        // piece push (N s) -1 = piece.kill
    public float massScale = 1f;                   // x the carried parts' mass
    public bool inheritVelocity = true;            // the piece keeps the velocity of the part it was cut from
    public float restMaxM = -1f;                   // > 0: never rests farther than this from the cut point (the head: Dom's FINFIX rule). -1 = none
    public string note = "";
}
[Serializable]
public class FinRow
{
    public string id = "", v3 = "";                // finishers.json entry id; V3 finisherCuts row name
    public bool ragdoll = true;                    // victim body -> death ragdoll at the beat
    public float ragdollAtS = 0f;                  // seconds after the contact (V3 cut.atS 0 = with the killing blow)
    public string zone = "torso";                  // head | torso | legs: the part that takes the blow
    public float push = 1f;                        // V3 finisherCuts.rows.*.push: multiplies the kill impulse (run through 0.25: the blade holds him up)
    public string note = "";
}
[Serializable]
public class FinPieceCfg
{
    public FinKick kill = new FinKick();           // V3 cut.kill {impulse 14, lift 5}
    public float friction = 0.8f, restitution = 0.2f, linearDamping = 0.05f, angularDamping = 0.2f;   // V3 world.json body / physics package p.restitution
    public int solverIterations = 16, solverVelocityIterations = 4;
    public float maxDepenetration = 2f, maxLaunchMps = 8f, minMassKg = 0.3f;
    public string detection = "ContinuousDynamic";  // V3: continuous detection for pieces
    public float highRateS = 2f; public int substeps = 2;   // V3 cut_high_rate_ticks 120 at 60 Hz = 2 s of 2 sub-steps
    public float rollCoeff = 0.8f;                 // V3 ground-rows rolling resistance: decel = coeff x g / radius while the piece lies on the ground
    public float restSpeed = 0.05f, restAngularSpeed = 0.2f; public int restTicks = 12;
    public float freezeAfterRestS = 0.5f, maxSeconds = 20f, rescueBelowM = 6f;   // frozen kinematic after resting this long (it STAYS, Dom's rule; V3 hid pieces after 12 s); fallen out of the world = put back once
    public float capsuleShrink = 1f, fallbackRadiusK = 0.38f, fallbackRadiusMinM = 0.07f, fallbackRadiusMaxM = 0.24f;
    public bool ignoreVictimColliders = true;
    public string colliderProxy = "capsules";      // capsules = the ragdoll part capsules | bounds = one sphere from Renderer.bounds. NEVER a MeshCollider / the render mesh (Joan's head is a 400k-tri mesh)
}
[Serializable]
public class FinCrownCfg
{
    public bool independent = true;                // the two skull halves become bodies once they opened
    public float releaseAtS = 0.384f;              // crown age (s) at which they leave the head (V1 open time)
    public string part = "head";                   // ragdoll part the halves were riding (it leaves the ragdoll)
    public float massShare = 0.5f, apartMps = 0.4f, upMps = 0f, boxShrink = 0.9f;   // V3 halves apart 0.14 m over 0.35 s ~ 0.4 m/s each
}
[Serializable]
public class FinPhysCfg
{
    public bool enabled = true;
    public bool suppressHitReact = true;           // PHYS2 hit reaction + clash recoil off while a finisher scene runs
    public bool bodyRagdoll = true;                // false = the victim keeps V1FIN's clip to the end (rows.ragdoll ignored)
    public FinPieceCfg piece = new FinPieceCfg();
    public FinCrownCfg crown = new FinCrownCfg();
    public FinRow[] rows = new FinRow[0];
    public FinUnitRow[] units = new FinUnitRow[0];
}
[Serializable] class FinPhysFile { public FinPhysCfg finphys = new FinPhysCfg(); }

public static class FinPhys
{
    static FinPhysCfg cfg;
    public static bool SceneActive; public static string SceneId = "";
    static FinRow sceneRow; static int highRateTicks; static float sceneT0;
    public static int PiecesLaunched, HalvesReleased, BodiesCued;
    public static readonly List<GameObject> Spawned = new List<GameObject>();   // halves (and anything else that is not under the victim) removed on a rematch

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; SceneActive = false; SceneId = ""; sceneRow = null; highRateTicks = 0; sceneT0 = 0f; PiecesLaunched = HalvesReleased = BodiesCued = 0; Spawned.Clear(); }

    static string Arg(string prefix)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length; i++) if (a[i] != null && a[i].StartsWith(prefix, StringComparison.Ordinal)) return a[i];
        return null;
    }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }

    public static FinPhysCfg Cfg
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new FinPhysCfg { enabled = false };   // no file / no block = off
            string json = null;
            string[] paths = { Path.Combine(Application.streamingAssetsPath, "physics.json"), Path.Combine(Application.dataPath, "Data/physics.json") };
            for (int i = 0; i < paths.Length && json == null; i++)
            {
                try { if (File.Exists(paths[i])) json = File.ReadAllText(paths[i]).TrimStart((char)0xFEFF); }
                catch (Exception ex) { Debug.LogWarning("[finphys] cannot read " + paths[i] + ": " + ex.Message); }
            }
            if (string.IsNullOrEmpty(json) || json.IndexOf("\"finphys\"", StringComparison.Ordinal) < 0) return cfg;
            try
            {
                var f = JsonUtility.FromJson<FinPhysFile>(json);
                if (f != null && f.finphys != null)
                {
                    var c = f.finphys;
                    if (c.piece == null) c.piece = new FinPieceCfg(); if (c.piece.kill == null) c.piece.kill = new FinKick(); if (c.crown == null) c.crown = new FinCrownCfg();
                    if (c.rows == null) c.rows = new FinRow[0]; if (c.units == null) c.units = new FinUnitRow[0];
                    for (int i = 0; i < c.units.Length; i++) if (c.units[i] != null && c.units[i].carries == null) c.units[i].carries = new string[0];
                    cfg = c;
                }
            }
            catch (Exception ex) { Debug.LogError("[finphys] physics.json finphys parse failed, feature off: " + ex.Message); }
            return cfg;
        }
    }
    public static void Reload() { cfg = null; }

    /// <summary>The master switch: data, PhysicsClock.PhysicsOn (-physics:off), and -finphys:off.</summary>
    public static bool On { get { return Cfg.enabled && PhysicsClock.Scripted && PhysicsClock.PhysicsOn && !Flag("-finphys:off"); } }
    public static bool SuppressHit { get { return SceneActive && Cfg.suppressHitReact && Time.unscaledTime - sceneT0 < 90f; } }   // 90 s watchdog: a scene that threw never leaves the hit reaction off

    public static FinRow RowFor(string id)
    {
        var r = Cfg.rows; for (int i = 0; i < r.Length; i++) if (r[i] != null && r[i].id == id) return r[i];
        return null;
    }
    public static FinUnitRow UnitRow(string unit)
    {
        var u = Cfg.units; for (int i = 0; i < u.Length; i++) if (u[i] != null && u[i].unit == unit) return u[i];
        return null;
    }
    /// <summary>Piece mode of a unit: the json value, overridden by -finphys:&lt;unit&gt;=&lt;mode&gt; (e.g. -finphys:head=physics).</summary>
    public static string ModeOf(FinUnitRow u)
    {
        if (u == null) return "off";
        var a = Arg("-finphys:" + u.unit + "=");
        if (a != null) { int e = a.IndexOf('='); if (e > 0 && e + 1 < a.Length) return a.Substring(e + 1); }
        return string.IsNullOrEmpty(u.mode) ? "physics" : u.mode;
    }
    static bool BodyRagdoll(FinRow r) { return r != null && r.ragdoll && Cfg.bodyRagdoll && Arg("-finphys:body=clip") == null; }

    // ---------------- scene hooks (FinisherPlayer.Run) ----------------
    public static void BeginScene(string entryId, DuelFighter killer, DuelFighter victim)
    {
        SceneActive = On; SceneId = entryId; sceneT0 = Time.unscaledTime; sceneRow = SceneActive ? RowFor(entryId) : null;
        if (!SceneActive) return;
        Debug.Log("[finphys] scene '" + entryId + "' " + (sceneRow == null ? "(no row: V1FIN as is, hit reaction off)" : "row ragdoll=" + BodyRagdoll(sceneRow) + " at +" + sceneRow.ragdollAtS.ToString("0.00") + " s zone " + sceneRow.zone + " push " + sceneRow.push.ToString("0.00")));
    }
    public static void EndScene() { SceneActive = false; sceneRow = null; }

    /// <summary>Seconds (scene clock) when the victim's Die() runs: the earlier of the entry's own time and contact + row.ragdollAtS. The conversion follows Die() through the Killed event (RagdollDeath).</summary>
    public static float DieAt(float contactT, float own)
    {
        if (!SceneActive || !BodyRagdoll(sceneRow)) return own;
        return Mathf.Min(own, contactT + Mathf.Max(0f, sceneRow.ragdollAtS));
    }

    /// <summary>At the contact: tell the victim's ragdoll rig how this blow pushes (V3 kill x row.push, the row's zone, along the killer -> victim lane).</summary>
    public static void OnContact(DuelFighter killer, DuelFighter victim, Vector3 axis)
    {
        if (!SceneActive || victim == null) return;
        var rig = victim.GetComponent<RagdollRig>(); if (rig == null) return;
        axis.y = 0f; if (axis.sqrMagnitude > 1e-6f) rig.FinDir = axis.normalized;
        if (sceneRow != null) { rig.FinPushMul = sceneRow.push; rig.FinZone = sceneRow.zone; BodiesCued++; }
        Debug.Log("[finphys] contact: blow dir " + rig.FinDir.ToString("0.00") + " zone " + (string.IsNullOrEmpty(rig.FinZone) ? "(move rule)" : rig.FinZone) + " push x" + rig.FinPushMul.ToString("0.00") + " (rig built " + rig.Built + ", converted " + rig.Converted + ")");
    }

    /// <summary>After MeshCut made a cut (FinisherPlayer.Gore): the carried parts leave the victim's ragdoll (V3: rd.cut removes the cut role's joint).</summary>
    public static void AfterCut(DuelFighter victim, string unit, CutResult res)
    {
        if (!On || victim == null || res == null || !res.ok) return;
        var u = UnitRow(unit); if (u == null || string.IsNullOrEmpty(u.cutPart) || u.carries.Length == 0) return;
        var rig = victim.GetComponent<RagdollRig>(); if (rig == null) return;
        rig.CutOff(u.carries);
        Debug.Log("[finphys] cut '" + unit + "': " + u.carries.Length + " parts leave the ragdoll (cut part " + u.cutPart + ", rig converted " + rig.Converted + ")");
    }

    // ---------------- stepping ----------------
    /// <summary>PhysicsClock.StepOnce calls this instead of Physics.Simulate: V3 steps cut pieces at 2 sub-steps for 2 s after a cut.</summary>
    public static void Simulate(float dt)
    {
        int n = highRateTicks > 0 ? Mathf.Max(1, Cfg.piece.substeps) : 1;
        if (highRateTicks > 0) highRateTicks--;
        if (n <= 1) { Physics.Simulate(dt); return; }
        float h = dt / n; for (int i = 0; i < n; i++) Physics.Simulate(h);
    }
    static void StartHighRate() { highRateTicks = Mathf.Max(highRateTicks, Mathf.RoundToInt(Mathf.Max(0f, Cfg.piece.highRateS) * PhysicsCfg.Get().clock.hz)); }

    // ---------------- pieces ----------------
    /// <summary>MeshCut.Launch hook: true = the piece is a physics body (PieceBody); false = V1FIN's scripted arc / the old Rigidbody launch.</summary>
    public static bool LaunchPiece(GameObject root, Transform victim, CutUnit unit, Vector3 away, float push)
    {
        if (!On || root == null || unit == null) return false;
        var u = UnitRow(unit.id); if (u == null || ModeOf(u) != "physics") return false;
        try { var pb = root.AddComponent<PieceBody>(); pb.Init(victim, unit, u, away, push); PiecesLaunched++; StartHighRate(); return true; }
        catch (Exception ex) { Debug.LogWarning("[finphys] piece '" + unit.id + "' physics failed (" + ex.Message + "): V1FIN launch instead"); var pb2 = root.GetComponent<PieceBody>(); if (pb2 != null) UnityEngine.Object.Destroy(pb2); return false; }
    }

    internal static void IgnoreVictim(Collider[] mine, Transform victim)
    {
        if (victim == null || mine == null) return;
        var cols = victim.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
        {
            var vc = cols[i]; if (vc == null || !vc.enabled || !vc.gameObject.activeInHierarchy) continue;   // Physics.IgnoreCollision logs an error for a disabled collider
            for (int j = 0; j < mine.Length; j++) { if (mine[j] == null || mine[j] == vc) continue; try { Physics.IgnoreCollision(vc, mine[j], true); } catch (Exception) { } }
        }
    }

    internal static void ConfigureBody(Rigidbody rb, FinPieceCfg p, float mass)
    {
        rb.isKinematic = false; rb.useGravity = true; rb.mass = Mathf.Max(p.minMassKg, mass);
        rb.linearDamping = p.linearDamping; rb.angularDamping = p.angularDamping;
        rb.maxDepenetrationVelocity = p.maxDepenetration; rb.maxAngularVelocity = Mathf.Max(rb.maxAngularVelocity, 50f);
        rb.solverIterations = Mathf.Max(1, p.solverIterations); rb.solverVelocityIterations = Mathf.Max(1, p.solverVelocityIterations);
        rb.interpolation = RigidbodyInterpolation.None;   // Physics.Simulate ignores interpolation
        CollisionDetectionMode cdm = CollisionDetectionMode.ContinuousDynamic;
        try { cdm = (CollisionDetectionMode)Enum.Parse(typeof(CollisionDetectionMode), p.detection, true); } catch (Exception) { }
        rb.collisionDetectionMode = cdm;
    }
    internal static PhysicsMaterial Material(FinPieceCfg p) { return new PhysicsMaterial("FinPhysPiece") { dynamicFriction = p.friction, staticFriction = p.friction, bounciness = p.restitution, frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Average }; }

    // ---------------- split crown halves ----------------
    /// <summary>CrownRig.LateUpdate: once the halves opened (crown age >= crown.releaseAtS) they leave the head and fly as two bodies. Returns true once released (the caller stops posing them).</summary>
    public static bool ReleaseHalves(Transform[] halves, Transform headBone, float age)
    {
        var c = Cfg.crown; if (!On || !c.independent || halves == null || halves.Length < 2 || headBone == null || age < c.releaseAtS) return false;
        for (int i = 0; i < halves.Length; i++) if (halves[i] == null) return false;
        var df = headBone.GetComponentInParent<DuelFighter>(); var rig = df != null ? df.GetComponent<RagdollRig>() : null;
        var hp = rig != null ? rig.PartByName(c.part) : null;
        float headMass = hp != null ? hp.mass : 5f; Vector3 v0 = Vector3.zero, w0 = Vector3.zero;
        if (hp != null && hp.rb != null && !hp.rb.isKinematic) { v0 = hp.rb.linearVelocity; w0 = hp.rb.angularVelocity; }
        else if (rig != null) rig.PartVelocity(c.part, out v0, out w0);
        var p = Cfg.piece; var mat = Material(p); var bodies = new Rigidbody[halves.Length]; var cols = new Collider[halves.Length];
        for (int i = 0; i < halves.Length; i++)
        {
            Transform h = halves[i]; Transform frame = h.parent;
            Vector3 outward = (frame != null ? frame.right : headBone.right) * (i == 0 ? -1f : 1f);   // CrownRig.LateUpdate: half 0 opens to -x, half 1 to +x of the head frame
            h.SetParent(null, true);
            var go = h.gameObject; var rb = go.GetComponent<Rigidbody>(); if (rb == null) rb = go.AddComponent<Rigidbody>();
            Bounds b = new Bounds(h.position, Vector3.zero); bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { if (r == null || r is ParticleSystemRenderer) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            var bc = go.AddComponent<BoxCollider>(); bc.center = h.InverseTransformPoint(b.center); Vector3 sz = h.InverseTransformVector(b.size); bc.size = new Vector3(Mathf.Abs(sz.x), Mathf.Abs(sz.y), Mathf.Abs(sz.z)) * Mathf.Clamp(c.boxShrink, 0.2f, 1f); bc.sharedMaterial = mat;
            go.layer = PhysMasks.PropLayer;
            ConfigureBody(rb, p, headMass * Mathf.Max(0.05f, c.massShare));
            rb.linearVelocity = v0 + outward * c.apartMps + Vector3.up * c.upMps; rb.angularVelocity = w0;
            bodies[i] = rb; cols[i] = bc; Spawned.Add(go);
        }
        try { Physics.IgnoreCollision(cols[0], cols[1], true); } catch (Exception) { }
        for (int i = 0; i < cols.Length; i++) IgnoreVictim(new[] { cols[i] }, df != null ? df.transform : headBone.root);
        if (rig != null) rig.CutOff(new[] { c.part });
        HalvesReleased++; StartHighRate();
        Debug.Log("[finphys] crown halves released at age " + age.ToString("0.00") + " s: 2 x " + (headMass * c.massShare).ToString("0.0") + " kg, v0 " + v0.ToString("0.00") + ", apart " + c.apartMps.ToString("0.00") + " m/s");
        return true;
    }

    /// <summary>Rematch: destroy everything FinPhys spawned outside the victim (CrownRig.RestoreAll calls this).</summary>
    public static void ClearSpawned() { for (int i = Spawned.Count - 1; i >= 0; i--) if (Spawned[i] != null) UnityEngine.Object.Destroy(Spawned[i]); Spawned.Clear(); }
}

/// <summary>A severed piece as a PhysX body (see FinPhys header). Added by FinPhys.LaunchPiece to the Rigidbody MeshCut.Launch made.</summary>
public sealed class PieceBody : MonoBehaviour
{
    public static readonly List<PieceBody> All = new List<PieceBody>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { All.Clear(); }

    Rigidbody rb; FinPieceCfg p; FinUnitRow u; Vector3 spawn; float radius = 0.1f, age, restFor, lastGroundT = -10f; int still; bool rest, frozen, rescued, subscribed;
    public float MassKg, LaunchSpeed, RestM = -1f; public string Info = "";
    FinisherPiece fp; float restMax;

    public void Init(Transform victim, CutUnit unit, FinUnitRow row, Vector3 away, float push)
    {
        u = row; p = FinPhys.Cfg.piece; rb = GetComponent<Rigidbody>(); if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        spawn = transform.position;
        var rig = victim != null ? (victim.GetComponent<RagdollRig>() ?? victim.GetComponentInChildren<RagdollRig>()) : null;
        var mat = FinPhys.Material(p);
        // mass + colliders from the carried ragdoll parts (V3: the piece IS those capsules); a rig that is missing / not built = the unit's own mass and a collider fitted to the mesh
        float mass = 0f; int caps = 0; var added = new List<Collider>();
        if (rig != null && rig.Built && p.colliderProxy != "bounds")
            for (int i = 0; i < u.carries.Length; i++)
            {
                var part = rig.PartByName(u.carries[i]); if (part == null || part.bone == null) continue;
                mass += part.mass;
                var cc = part.cap; if (cc == null || part.bone.lossyScale.y < RagdollDeath.Cfg.convert.tinyScale) continue;   // a bone scaled to nothing has no capsule to copy
                added.Add(AddCapsule(cc, mat)); caps++;
            }
        if (mass <= 0f) mass = Mathf.Max(0.1f, unit.massKg);
        mass *= Mathf.Max(0.01f, u.massScale);
        if (caps == 0) added.Add(FitFallback(mat));
        gameObject.layer = PhysMasks.PropLayer;
        FinPhys.ConfigureBody(rb, p, mass); MassKg = rb.mass;
        rb.ResetCenterOfMass(); rb.ResetInertiaTensor();
        // velocity: what the part was doing + the V3 push (impulse N s / mass) along the blow with its lift, capped
        Vector3 v0 = Vector3.zero, w0 = Vector3.zero;
        if (u.inheritVelocity && rig != null)
        {
            string src = !string.IsNullOrEmpty(u.cutPart) ? u.cutPart : (u.carries.Length > 0 ? u.carries[0] : "");
            if (rig.Converted) { var sp = rig.PartByName(src); if (sp != null && sp.rb != null && !sp.rb.isKinematic) { v0 = sp.rb.linearVelocity; w0 = sp.rb.angularVelocity; } }
            else rig.PartVelocity(src, out v0, out w0);
        }
        away.y = 0f; if (away.sqrMagnitude < 1e-4f) away = victim != null ? -victim.forward : Vector3.forward; away.Normalize();
        float imp = (u.impulse >= 0f ? u.impulse : p.kill.impulse) * Mathf.Max(0f, push), lift = (u.lift >= 0f ? u.lift : p.kill.lift) * Mathf.Max(0f, push);
        Vector3 dv = (away * imp + Vector3.up * lift) / rb.mass; float dvm = dv.magnitude; if (dvm > p.maxLaunchMps && dvm > 1e-5f) dv *= p.maxLaunchMps / dvm;
        rb.linearVelocity = v0 + dv; rb.angularVelocity = w0; LaunchSpeed = rb.linearVelocity.magnitude;
        if (p.ignoreVictimColliders) FinPhys.IgnoreVictim(added.ToArray(), victim);
        restMax = u.restMaxM > 0f ? u.restMaxM : -1f;
        Info = "mass " + rb.mass.ToString("0.0") + " kg, " + caps + " capsules, push " + imp.ToString("0.0") + " N s + lift " + lift.ToString("0.0") + " -> dv " + dv.magnitude.ToString("0.00") + " m/s, v0 " + v0.magnitude.ToString("0.00") + " m/s";
        Debug.Log("[finphys] piece '" + unit.id + "' PHYSICS: " + Info + (rig == null ? " (no ragdoll rig: mesh-fitted collider)" : ""));
    }

    Collider AddCapsule(CapsuleCollider src, PhysicsMaterial mat)
    {
        // world capsule of the ragdoll part -> a child collider in the piece's frame (the bone's lossy scale is folded into radius / height)
        Transform bt = src.transform; Vector3 ls = bt.lossyScale;
        Vector3 axisLocal = src.direction == 0 ? Vector3.right : src.direction == 1 ? Vector3.up : Vector3.forward;
        float axScale = Mathf.Abs(src.direction == 0 ? ls.x : src.direction == 1 ? ls.y : ls.z);
        float radScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
        var go = new GameObject("FinPhysCap_" + bt.name); go.layer = PhysMasks.PropLayer; go.transform.SetParent(transform, false);
        Vector3 cw = bt.TransformPoint(src.center); Vector3 aw = bt.TransformDirection(axisLocal);
        go.transform.SetPositionAndRotation(cw, Quaternion.FromToRotation(Vector3.up, aw.sqrMagnitude > 1e-8f ? aw.normalized : Vector3.up));
        var cc = go.AddComponent<CapsuleCollider>(); cc.direction = 1;
        float sh = Mathf.Max(0.1f, p.capsuleShrink);
        Vector3 pls = transform.lossyScale; float ps = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(pls.x), Mathf.Max(Mathf.Abs(pls.y), Mathf.Abs(pls.z))));
        cc.radius = src.radius * radScale * sh / ps; cc.height = Mathf.Max(2f * cc.radius, src.height * axScale * sh / ps); cc.sharedMaterial = mat;
        return cc;
    }

    Collider FitFallback(PhysicsMaterial mat)
    {
        Bounds b = new Bounds(transform.position, Vector3.one * 0.2f); bool any = false;
        foreach (var r in GetComponentsInChildren<Renderer>()) { if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
        radius = Mathf.Clamp(Mathf.Max(b.size.x, b.size.z) * p.fallbackRadiusK, p.fallbackRadiusMinM, p.fallbackRadiusMaxM);
        Vector3 ls = transform.lossyScale; float sc = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z))));
        var sp = gameObject.AddComponent<SphereCollider>(); sp.center = transform.InverseTransformPoint(b.center); sp.radius = radius / sc; sp.sharedMaterial = mat; return sp;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); if (!subscribed) { PhysicsClock.OnStep += OnClockStep; subscribed = true; } }
    void OnDisable() { All.Remove(this); if (subscribed) { PhysicsClock.OnStep -= OnClockStep; subscribed = false; } }
    void OnCollisionStay(Collision c) { lastGroundT = age; }
    void FixedUpdate() { if (!PhysicsClock.Scripted) OnClockStep(Time.fixedDeltaTime); }   // clock off: PhysX is on FixedUpdate again

    void OnClockStep(float dt)
    {
        if (rb == null || frozen) return;
        age += dt;
        if (!rescued && p.rescueBelowM > 0f && rb.position.y < spawn.y - p.rescueBelowM)
        {
            rescued = true; rb.position = spawn + Vector3.up * 0.5f; transform.position = rb.position; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            if (fp == null) fp = GetComponent<FinisherPiece>(); if (fp != null) fp.Rescues++;
            Debug.LogWarning("[finphys] piece '" + name + "' fell out of the world: put back above the cut point once");
        }
        // V3 ground-rows rolling resistance: a round piece on the ground loses coeff x g / r rad/s each second (a capsule or sphere has none in the solver)
        if (p.rollCoeff > 0f && age - lastGroundT < 2.5f * dt + 0.02f)
        {
            Vector3 w = rb.angularVelocity; float m = w.magnitude;
            if (m > 1e-4f) { float dec = p.rollCoeff * Mathf.Abs(Physics.gravity.y) / Mathf.Max(0.03f, radius > 0.01f ? radius : 0.1f) * dt; rb.angularVelocity = w * (Mathf.Max(0f, m - dec) / m); }
        }
        if (restMax > 0f && !rest)
        {
            Vector3 off = rb.position - spawn; off.y = 0f; float dm = off.magnitude;
            if (dm > restMax) { Vector3 dir = off / dm; Vector3 tp = rb.position; tp.x = spawn.x + dir.x * restMax; tp.z = spawn.z + dir.z * restMax; rb.position = tp; transform.position = tp; Vector3 v = rb.linearVelocity; float outv = Mathf.Max(0f, v.x * dir.x + v.z * dir.z); v.x -= dir.x * outv; v.z -= dir.z * outv; rb.linearVelocity = v * 0.4f; }
        }
        bool slow = rb.linearVelocity.sqrMagnitude < p.restSpeed * p.restSpeed && rb.angularVelocity.sqrMagnitude < p.restAngularSpeed * p.restAngularSpeed;
        still = slow ? still + 1 : 0;
        if (!rest && (still >= p.restTicks || age >= p.maxSeconds))
        {
            rest = true; Vector3 ro = rb.position - spawn; ro.y = 0f; RestM = ro.magnitude;
            if (fp == null) fp = GetComponent<FinisherPiece>(); if (fp != null) { fp.Settled = true; fp.RestM = RestM; }
            Debug.Log("[finphys] piece '" + name + "' REST " + RestM.ToString("0.00") + " m from the cut point, " + age.ToString("0.0") + " s after the cut (" + Info + ")");
        }
        if (rest) { restFor += dt; if (restFor >= p.freezeAfterRestS) { frozen = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.isKinematic = true; } }
    }
}
