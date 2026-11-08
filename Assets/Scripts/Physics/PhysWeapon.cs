using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// PHYSWEAPON v106: blade contact and finisher physics, PRESENTATION ONLY, ALL DARK (Data\physweapon.json: enabled false and every sub-switch false = today's behaviour, no object created, no event subscribed).
// The 60 Hz sim stays authoritative. Nothing here is read by the Duel; nothing writes DuelFighter / Fighter / CharacterController / Animator state; there is no UnityEngine.Random (own xorshift seeded from the
// PhysicsClock step counter); every body steps on PhysicsClock (Scripted, one Physics.Simulate per sim tick), on layer physProp which ignores both fighter layers (physics.json matrix), and nothing here is a trigger the sim polls.
// What it adds (each behind its own switch, all need the physics master + clock to be ON, same as FinPhys):
//   clash     weapon-on-weapon recoil at the REAL contact point: the two blades are capsules (segment socket -> tip + extension, radius) taken from the live transforms, the closest points of the two segments
//             give the contact point; HitReact applies the clash impulse at that point (AddForceAtPosition on the weapon / guard arm part, lever arm clamped) instead of at the part centre.
//   pin       run_through: the killer's blade tip carries a kinematic pin; a drive joint holds the victim's torso part on it while the blade is in, released when the blade withdraws (stretch) or after maxHoldS.
//   severed   decapitation head + split_crown halves: spin from the blow, centre of mass dropped, bounce / friction / damping rows; optional head mode physics (the V1FIN scripted arc stays the default).
//   debris    floor chips knocked up by a heavy hit / guard break / finisher contact (pooled, capped, shrink-recycled).
//   props     scene props named in the data (loose dressing near the arena) become bodies when a contact lands within radiusM, then freeze where they stop. A scene reload (rematch) restores them.
// Not physics under the simhash rule: see MANIFEST.md section 4.
[Serializable] public class PwWeapon { public string id = ""; public string match = ""; public float extendM = 0f, radiusM = 0.04f, headRadiusM = 0f; public string note = ""; }   // match = lower-case token found in the weapon id / the socket's node names; extendM = metres beyond TrailTip along the shaft; headRadiusM > 0 = a sphere of that radius at TrailTip (a maul head)
[Serializable] public class PwClash { public bool on = false; public bool sparks = false; public float sparkMaxFromBodyM = 1.3f; public float blend = 0.5f, maxGapM = 0.45f, leverMaxM = 0.35f, leverScale = 0.6f, minHeightM = 0.3f, maxHeightM = 2.3f; public string socketNode = "WeaponSocket_R", tipNode = "TrailTip"; public float refreshS = 2f; }
[Serializable] public class PwPin { public bool on = false; public string[] entries = new string[0]; public string part = "spine1"; public float startTimeoutS = 1.5f, maxHoldS = 1.4f, releaseDistM = 0.45f, woundDepthM = 0.12f, spring = 4000f, damper = 200f, maxForceN = 1500f; }
[Serializable] public class PwBodyTune { public string unit = ""; public float spinDegPerS = 0f, comDropM = 0f, bounciness = -1f, friction = -1f, linearDamping = -1f, angularDamping = -1f, maxAngularDegPerS = 1400f; public string spinAxis = "cross"; }
[Serializable] public class PwSevered { public bool on = false; public bool headPhysics = false; public float fallbackFriction = 0.8f, fallbackBounciness = 0.2f; /* PHYS2 v109h */ public PwBodyTune[] tunes = new PwBodyTune[0]; }
[Serializable] public class PwDebrisRow { public string type = ""; public bool heavyOnly = true; public int count = 8; public float minMps = 1.2f, maxMps = 3.4f, upMps = 2.2f, spreadM = 0.35f; }
[Serializable] public class PwDebris
{
    public bool on = false; public int maxLive = 24; public float lifeS = 4.5f, shrinkS = 0.6f, minSizeM = 0.015f, maxSizeM = 0.05f, massKg = 0.05f, smoothness = 0.2f, restSpeed = 0.05f, freezeAfterRestS = 0.4f;
    public float[] color = new float[] { 0.16f, 0.145f, 0.125f }; public bool castShadows = true; public string shader = "HDRP/Lit";
    // PHYS2 v109h: former in-logic literals, same values: chip shape (6 corner points, xyz), collider box, damping, ground probe, spawn lift, launch jitter, spin, rest and kill-plane thresholds, light-hit strength
    public float[] chipVerts = new float[] { 0.5f, 0.02f, 0.05f, -0.45f, 0f, -0.08f, 0.04f, 0.32f, 0f, -0.02f, -0.26f, 0.06f, 0.05f, 0.04f, 0.42f, -0.06f, -0.02f, -0.38f };
    public float[] colliderSize = new float[] { 0.7f, 0.5f, 0.7f }; public float linearDamping = 0.05f, angularDamping = 0.15f, groundProbeUpM = 1.5f, groundProbeLenM = 4f, spawnLiftM = 0.03f, dirBias = 0.4f, strengthFloor = 0.6f, upJitterMin = 0.5f, spinRadPerS = 30f, restAngSpeed = 0.5f, killY = -3f, lightStrength = 0.6f;
    public PwDebrisRow finisher = new PwDebrisRow { type = "finisher", heavyOnly = false, count = 14, minMps = 1.5f, maxMps = 4.2f, upMps = 2.8f, spreadM = 0.5f };
    public PwDebrisRow[] rows = new PwDebrisRow[0];
}
[Serializable] public class PwPropRow { public string name = ""; public float massKg = 4f, radiusM = 3f, minMps = 1.5f, maxMps = 3.5f, upMps = 1.5f, spinDegPerS = 220f; public bool heavyOnly = true; }
[Serializable] public class PwProps { public bool on = false; public float maxSizeM = 1.6f, restSpeed = 0.06f, freezeAfterRestS = 0.6f, maxSeconds = 12f, restAngSpeed = 0.25f, killY = -3f, lightStrength = 0.6f, strengthFloor = 0.6f, bodyLinearDamping = 0.05f, bodyAngularDamping = 0.2f; public int solverIterations = 12; /* PHYS2 v109h */ public string[] onTypes = new string[0]; public PwPropRow[] rows = new PwPropRow[0]; }
[Serializable] public class PwCfg
{
    public bool enabled = false;
    public PwWeapon[] weapons = new PwWeapon[0]; public PwWeapon fallback = new PwWeapon { id = "default", match = "", extendM = 0f, radiusM = 0.04f };
    public PwClash clash = new PwClash(); public PwPin pin = new PwPin(); public PwSevered severed = new PwSevered(); public PwDebris debris = new PwDebris(); public PwProps props = new PwProps();
}
[Serializable] class PwFile { public PwCfg physweapon = new PwCfg(); }

public static class PhysWeapon
{
    static PwCfg cfg;
    public static int ClashKicks, PinsMade, PinsReleased, SeveredTuned, ChipsSpawned, PropsKnocked;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; ClashKicks = PinsMade = PinsReleased = SeveredTuned = ChipsSpawned = PropsKnocked = 0; tips.Clear(); Driver.Dead(); }

    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }

    public static PwCfg Cfg
    {
        get
        {
            if (cfg != null) return cfg;
            cfg = new PwCfg();   // enabled false = off
            string json = null;
            string[] paths = { Path.Combine(Application.streamingAssetsPath, "physweapon.json"), Path.Combine(Application.dataPath, "Data/physweapon.json") };
            for (int i = 0; i < paths.Length && json == null; i++)
            {
                try { if (File.Exists(paths[i])) json = DataPath.ReadFile(paths[i]).TrimStart((char)0xFEFF); }
                catch (Exception ex) { Debug.LogWarning("[physweapon] cannot read " + paths[i] + ": " + ex.Message); }
            }
            if (string.IsNullOrEmpty(json)) return cfg;
            try
            {
                var f = JsonUtility.FromJson<PwFile>(json);
                if (f != null && f.physweapon != null)
                {
                    var c = f.physweapon;
                    if (c.weapons == null) c.weapons = new PwWeapon[0]; if (c.fallback == null) c.fallback = new PwWeapon();
                    if (c.clash == null) c.clash = new PwClash(); if (c.pin == null) c.pin = new PwPin(); if (c.pin.entries == null) c.pin.entries = new string[0];
                    if (c.severed == null) c.severed = new PwSevered(); if (c.severed.tunes == null) c.severed.tunes = new PwBodyTune[0];
                    if (c.debris == null) c.debris = new PwDebris(); if (c.debris.rows == null) c.debris.rows = new PwDebrisRow[0]; if (c.debris.finisher == null) c.debris.finisher = new PwDebrisRow(); if (c.debris.color == null || c.debris.color.Length < 3) c.debris.color = new float[] { 0.16f, 0.145f, 0.125f };   // hc:ok default repair, same values as the field defaults
                    if (c.props == null) c.props = new PwProps(); if (c.props.rows == null) c.props.rows = new PwPropRow[0]; if (c.props.onTypes == null) c.props.onTypes = new string[0];
                    cfg = c;
                }
            }
            catch (Exception ex) { Debug.LogError("[physweapon] physweapon.json parse failed, feature off: " + ex.Message); }
            return cfg;
        }
    }
    public static void Reload() { cfg = null; }

    /// <summary>Master: data, the physics clock in Scripted mode (same gate as FinPhys), and no -physweapon:off.</summary>
    public static bool Active { get { return Cfg.enabled && PhysicsClock.Scripted && PhysicsClock.PhysicsOn && !Flag("-physweapon:off"); } }
    public static bool ClashOn { get { return Active && Cfg.clash.on; } }
    public static bool PinOn { get { return Active && Cfg.pin.on; } }
    public static bool SeveredOn { get { return Active && Cfg.severed.on; } }
    public static bool DebrisOn { get { return Active && Cfg.debris.on; } }
    public static bool PropsOn { get { return Active && Cfg.props.on; } }

    // ---------------- weapon geometry (live transforms; nothing read from the sim) ----------------
    class WCache { public Transform sock, tip; public PwWeapon prof; public float at; }
    static readonly Dictionary<DuelFighter, WCache> tips = new Dictionary<DuelFighter, WCache>();

    static Transform FindNamed(Transform root, string n)
    {
        if (root == null || string.IsNullOrEmpty(n)) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t != null && t.name == n) return t;
        return null;
    }
    static PwWeapon ProfileFor(DuelFighter f, Transform sock)
    {
        var c = Cfg; string wid = WeaponSwap.IdOf(f.transform);   // "" = the default weapon on the socket
        string rw = ""; try { var rc = Roster.Get(Roster.IdOf(f)); if (rc != null && rc.weapon != null) rw = rc.weapon; } catch (Exception) { }
        for (int i = 0; i < c.weapons.Length; i++)
        {
            var w = c.weapons[i]; if (w == null || string.IsNullOrEmpty(w.match)) continue;
            string m = w.match.ToLowerInvariant();
            if (wid.ToLowerInvariant().Contains(m)) return w;
            if (string.IsNullOrEmpty(wid) && rw.ToLowerInvariant() == m) return w;
            if (string.IsNullOrEmpty(wid) && sock != null) { foreach (var t in sock.GetComponentsInChildren<Transform>(true)) if (t != null && t.name.ToLowerInvariant().Contains(m)) return w; }
        }
        return c.fallback;
    }
    static WCache Cached(DuelFighter f)
    {
        if (f == null) return null;
        WCache w; if (tips.TryGetValue(f, out w) && w.sock != null && w.tip != null && Time.unscaledTime - w.at < Cfg.clash.refreshS) return w;
        var sock = FindNamed(f.transform, Cfg.clash.socketNode); var tip = FindNamed(f.transform, Cfg.clash.tipNode);
        if (sock == null || tip == null) { tips.Remove(f); return null; }
        w = new WCache { sock = sock, tip = tip, prof = ProfileFor(f, sock), at = Time.unscaledTime }; tips[f] = w; return w;
    }
    /// <summary>The weapon as a capsule: a = the grip (socket origin), b = TrailTip pushed extendM along the shaft, r = radius (metres, fighter scale applied).</summary>
    public static bool Blade(DuelFighter f, out Vector3 a, out Vector3 b, out float r)
    {
        a = b = Vector3.zero; r = 0f; var w = Cached(f); if (w == null) return false;
        float sc = FighterScale.Of(f); a = w.sock.position; Vector3 t = w.tip.position; Vector3 ax = t - a; float len = ax.magnitude; if (len < 1e-4f) return false;
        b = t + ax / len * w.prof.extendM * sc; r = w.prof.radiusM * sc; return true;
    }
    struct Prim { public Vector3 p, q; public float r; }
    static readonly Prim[] primsA = new Prim[3], primsB = new Prim[3];   // OFFHAND v2: shaft, head sphere, off-hand blade
    /// <summary>The weapon as up to two primitives: [0] the shaft capsule, [1] the head sphere at TrailTip (profile headRadiusM > 0). Returns the count (0 = no weapon found).</summary>
    static int Prims(DuelFighter f, Prim[] buf, bool asAttacker = false)
    {
        /* OFFHAND v2: an attacker's contact comes from the blade(s) its move swings (off_hand.json attacks[], no row = both); a defender's blades all count. The off-hand blade is one more capsule, socket -> glb tip node. */
        bool useMain = true, useOff = true; if (asAttacker) OffHandWeapon.BladesFor(f, out useMain, out useOff);
        int n = 0; Vector3 a, b; float r;
        if (useMain && Blade(f, out a, out b, out r))
        {
            buf[n++] = new Prim { p = a, q = b, r = r }; var w = Cached(f);
            if (w != null && w.prof.headRadiusM > 0f) { Vector3 t = w.tip.position; buf[n++] = new Prim { p = t, q = t, r = w.prof.headRadiusM * FighterScale.Of(f) }; }
        }
        Prim op; if (useOff && OffPrim(f, out op)) buf[n++] = op;
        return n;
    }
    /// <summary>OFFHAND v2: the off-hand weapon as a capsule: grip (its socket) -> the glb tip node + the profile's extendM, radius from the profile matched on the off-hand weapon id (same weapons[] rows as the main hand).</summary>
    static bool OffPrim(DuelFighter f, out Prim p)
    {
        p = default(Prim); var oh = OffHandWeapon.Of(f); if (oh == null || !oh.VisualShown || oh.Socket == null || oh.Tip == null) return false;
        var c = Cfg; PwWeapon prof = c.fallback; string wid = oh.WeaponId.ToLowerInvariant();
        for (int i = 0; i < c.weapons.Length; i++) { var w = c.weapons[i]; if (w != null && !string.IsNullOrEmpty(w.match) && wid.Contains(w.match.ToLowerInvariant())) { prof = w; break; } }
        Vector3 a = oh.Socket.position, t = oh.Tip.position, ax = t - a; float len = ax.magnitude; if (len < 1e-4f) return false;
        float sc = FighterScale.Of(f); p = new Prim { p = a, q = t + ax / len * prof.extendM * sc, r = prof.radiusM * sc }; return true;
    }
    static float ClosestSegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, rr = p1 - p2; float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, rr), s, t;
        if (a <= 1e-9f && e <= 1e-9f) { c1 = p1; c2 = p2; return (c1 - c2).magnitude; }
        if (a <= 1e-9f) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, rr);
            if (e <= 1e-9f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                s = den > 1e-9f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); } else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        c1 = p1 + d1 * s; c2 = p2 + d2 * t; return (c1 - c2).magnitude;
    }
    /// <summary>The real contact point of two blades. true = the blades' surfaces are within maxGapM of each other (a true crossing / touch); false = fallback: the blend of the two tips.</summary>
    public static bool ContactPoint(DuelFighter attacker, DuelFighter defender, out Vector3 point)
    {
        point = Vector3.zero; int na = Prims(attacker, primsA, true); if (na == 0) return false;   // OFFHAND v2: attacker blades by move
        int nb = Prims(defender, primsB);
        if (nb == 0) { point = primsA[na - 1].q; Clamp(attacker, defender, ref point); return false; }
        float best = float.MaxValue; Vector3 bp = Vector3.zero;
        for (int i = 0; i < na; i++)
            for (int j = 0; j < nb; j++)
            {
                Vector3 c1, c2; float d = ClosestSegSeg(primsA[i].p, primsA[i].q, primsB[j].p, primsB[j].q, out c1, out c2), gap = d - primsA[i].r - primsB[j].r;
                if (gap >= best) continue;
                best = gap; Vector3 n = d > 1e-5f ? (c2 - c1) / d : Vector3.up;
                bp = (c1 + n * primsA[i].r + c2 - n * primsB[j].r) * 0.5f;   // midway between the two surfaces
            }
        bool touch = best <= Cfg.clash.maxGapM * FighterScale.Of(defender);
        point = touch ? bp : Vector3.Lerp(primsA[na - 1].q, primsB[nb - 1].q, Mathf.Clamp01(Cfg.clash.blend));
        Clamp(attacker, defender, ref point); return touch;
    }
    static bool Clamp(DuelFighter attacker, DuelFighter defender, ref Vector3 p)
    {
        float sc = FighterScale.Of(defender), fy = defender != null ? defender.transform.position.y : 0f;
        p.y = Mathf.Clamp(p.y, fy + Cfg.clash.minHeightM * sc, fy + Cfg.clash.maxHeightM * sc); return true;
    }
    /// <summary>HitReact.TriggerClash hook: the world point where the clash impulse is applied on `part` (the weapon arm / guard arm body). false = apply at the part centre as today.</summary>
    public static bool ClashPoint(CombatEvent e, Rigidbody part, out Vector3 pt)
    {
        pt = Vector3.zero; if (!ClashOn || part == null || e.actor == null || e.target == null) return false;
        Vector3 p; ContactPoint(e.actor, e.target, out p);
        if (p == Vector3.zero) return false;
        Vector3 com = part.worldCenterOfMass, v = p - com; float lim = Cfg.clash.leverMaxM * FighterScale.Of(e.target);
        v *= Mathf.Clamp01(Cfg.clash.leverScale); if (v.magnitude > lim) v = v.normalized * lim;
        pt = com + v; ClashKicks++; return true;
    }

    /// <summary>ClashFx / HitFeel spark origin: the real blade contact point instead of the point on the defender's body (clash.sparks). Flesh hits (no metal) and an off switch return bodyAnchor unchanged.
    /// Composes with ARCS v105 (ArcFx.ContactPoint): enable only ONE of arc_fx sparks.on / physweapon clash.sparks.</summary>
    public static Vector3 SparkAnchor(DuelFighter attacker, DuelFighter defender, string type, bool metal, Vector3 bodyAnchor)
    {
        if (!ClashOn || !Cfg.clash.sparks || attacker == null || defender == null) return bodyAnchor;
        bool clash = type == CombatClass.Blocked || type == CombatClass.Parried || type == CombatClass.GuardBroken;
        if (!clash && !metal) return bodyAnchor;
        Vector3 p; ContactPoint(attacker, defender, out p);
        if (p == Vector3.zero) return bodyAnchor;
        Vector3 off = p - bodyAnchor; float lim = Cfg.clash.sparkMaxFromBodyM * FighterScale.Of(defender);
        if (off.magnitude > lim) p = bodyAnchor + off.normalized * lim;
        return p;
    }

    // ---------------- finisher hooks (FinPhys.OnContact / EndScene) ----------------
    static Pin pin;
    public static void OnFinisherContact(string entryId, DuelFighter killer, DuelFighter victim, Vector3 axis)
    {
        if (!Active || killer == null || victim == null) return;
        Driver.Ensure();
        if (Cfg.pin.on && Array.IndexOf(Cfg.pin.entries, entryId) >= 0) StartPin(killer, victim, axis);
        if (Cfg.debris.on) { Vector3 p = victim.transform.position; Driver.SpawnChips(p, axis, Cfg.debris.finisher, 1f, PhysicsClock.StepCount); }
        if (Cfg.props.on) Driver.KnockProps(victim.transform.position, axis, true, "finisher", 1f);
    }
    public static void OnFinisherEnd() { if (pin != null) pin.Release("scene end"); }

    sealed class Pin
    {
        public GameObject go; public Rigidbody prb; public Transform tip; public Vector3 tipLocal, wound; public RagdollRig rig; public RagdollRig.Part part; public ConfigurableJoint j;
        public float age; public bool joined, done; public Vector3 anchorLocal;
        public void Release(string why)
        {
            if (done) return; done = true;
            if (j != null) UnityEngine.Object.Destroy(j);
            if (go != null) UnityEngine.Object.Destroy(go);
            if (joined) { PinsReleased++; Debug.Log("[physweapon] pin released (" + why + ") after " + age.ToString("0.00") + " s"); }
        }
        public void Step(float dt)
        {
            if (done) return; age += dt; var c = Cfg.pin;
            if (tip == null || rig == null) { Release("lost transform"); return; }
            Vector3 world = tip.TransformPoint(tipLocal);
            if (!joined)
            {
                if (age > c.startTimeoutS) { Release("never converted"); return; }
                if (!rig.Converted || part == null || part.rb == null || part.rb.isKinematic) { if (prb != null) prb.position = world; return; }
                wound = world; prb.position = world; prb.transform.position = world;
                var jt = part.rb.gameObject.AddComponent<ConfigurableJoint>(); jt.connectedBody = prb; jt.autoConfigureConnectedAnchor = false;
                anchorLocal = part.rb.transform.InverseTransformPoint(wound); jt.anchor = anchorLocal; jt.connectedAnchor = Vector3.zero;
                jt.xMotion = jt.yMotion = jt.zMotion = ConfigurableJointMotion.Free; jt.angularXMotion = jt.angularYMotion = jt.angularZMotion = ConfigurableJointMotion.Free;
                var d = new JointDrive { positionSpring = c.spring, positionDamper = c.damper, maximumForce = c.maxForceN };
                jt.xDrive = d; jt.yDrive = d; jt.zDrive = d; jt.targetPosition = Vector3.zero; jt.enableCollision = false;
                j = jt; joined = true; PinsMade++;
                Debug.Log("[physweapon] pin: blade tip holds '" + part.cfg.name + "' (spring " + c.spring.ToString("0") + ", max " + c.maxForceN.ToString("0") + " N)");
                return;
            }
            prb.MovePosition(world);
            Vector3 body = part.rb.transform.TransformPoint(anchorLocal);
            if (age > c.maxHoldS) { Release("max hold"); return; }
            if ((world - body).magnitude > c.releaseDistM * FighterScale.Of(rig.df)) Release("blade withdrew");
        }
    }
    static void StartPin(DuelFighter killer, DuelFighter victim, Vector3 axis)
    {
        if (pin != null) pin.Release("replaced");
        var w = Cached(killer); var rig = victim.GetComponent<RagdollRig>(); if (w == null || rig == null) { Debug.Log("[physweapon] pin skipped: no weapon tip or no ragdoll rig"); return; }
        var part = rig.PartByName(Cfg.pin.part); if (part == null) { Debug.LogWarning("[physweapon] pin part '" + Cfg.pin.part + "' not in the rig"); return; }
        axis.y = 0f; Vector3 ax = axis.sqrMagnitude > 1e-6f ? axis.normalized : (w.tip.position - w.sock.position).normalized;
        Vector3 wound = w.tip.position + ax * Cfg.pin.woundDepthM * FighterScale.Of(victim);
        var go = new GameObject("PhysWeaponPin"); go.transform.position = wound;
        var rb = go.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false; rb.interpolation = RigidbodyInterpolation.None;
        pin = new Pin { go = go, prb = rb, tip = w.tip, tipLocal = w.tip.InverseTransformPoint(wound), wound = wound, rig = rig, part = part };
        Driver.Ensure();
    }

    // ---------------- severed pieces (FinPhys.PieceBody.Init / ReleaseHalves) ----------------
    /// <summary>Unit mode override (FinPhys.ModeOf): head -> physics only when severed.headPhysics is on. The -finphys:head=... command line still wins (it returns before this).</summary>
    public static string ModeOverride(string unit, string mode)
    {
        if (SeveredOn && Cfg.severed.headPhysics && unit == "head" && mode == "arc") return "physics";
        return mode;
    }
    public static void TuneSevered(Rigidbody rb, string unit, Vector3 dir, int index)
    {
        if (!SeveredOn || rb == null) return;
        PwBodyTune t = null; var all = Cfg.severed.tunes; for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].unit == unit) { t = all[i]; break; }
        if (t == null) return;
        dir.y = 0f; if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward; dir.Normalize();
        Vector3 axis;
        // cross = the top of the piece tips along `dir` (the blow for the head, the half's own outward direction for the crown halves: each half already has its own dir, so "outward" takes no sign flip); up = spin about the vertical
        axis = t.spinAxis == "up" ? Vector3.up : Vector3.Cross(Vector3.up, dir);
        float sign = (t.spinAxis == "outward" || (index & 1) == 0) ? 1f : -1f, w = t.spinDegPerS * Mathf.Deg2Rad * sign;
        Vector3 av = rb.angularVelocity + axis.normalized * w; float cap = t.maxAngularDegPerS * Mathf.Deg2Rad; if (av.magnitude > cap) av = av.normalized * cap;
        rb.angularVelocity = av;
        if (Mathf.Abs(t.comDropM) > 1e-5f) rb.centerOfMass = rb.centerOfMass + rb.transform.InverseTransformDirection(Vector3.down) * t.comDropM;   // local space: the piece root is bone-oriented, "down" is the world's
        if (t.linearDamping >= 0f) rb.linearDamping = t.linearDamping; if (t.angularDamping >= 0f) rb.angularDamping = t.angularDamping;
        if (t.bounciness >= 0f || t.friction >= 0f)
        {
            var cols = rb.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i]; if (c == null) continue; var m = c.sharedMaterial;
                var nm = new PhysicsMaterial("PhysWeaponSevered") { dynamicFriction = t.friction >= 0f ? t.friction : (m != null ? m.dynamicFriction : Cfg.severed.fallbackFriction), staticFriction = t.friction >= 0f ? t.friction : (m != null ? m.staticFriction : Cfg.severed.fallbackFriction), bounciness = t.bounciness >= 0f ? t.bounciness : (m != null ? m.bounciness : Cfg.severed.fallbackBounciness), frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Average };
                c.sharedMaterial = nm;
            }
        }
        SeveredTuned++;
        Debug.Log("[physweapon] severed '" + unit + "' #" + index + ": spin " + (t.spinDegPerS * sign).ToString("0") + " deg/s about " + t.spinAxis + ", COM drop " + t.comDropM.ToString("0.000") + " m, bounce " + t.bounciness.ToString("0.00"));
    }

    // ---------------- probe: how the weapon tips sit (run once with -physweapon:probe) ----------------
    public static void Probe(DuelFighter f)
    {
        var w = Cached(f); if (w == null) { Debug.Log("[physweapon] probe " + (f != null ? f.name : "null") + ": no '" + Cfg.clash.socketNode + "' / '" + Cfg.clash.tipNode + "'"); return; }
        Vector3 a, b; float r; Blade(f, out a, out b, out r);
        Bounds bb = new Bounds(w.sock.position, Vector3.zero); bool any = false;
        foreach (var rd in w.sock.GetComponentsInChildren<Renderer>(false)) { if (rd == null || rd is TrailRenderer || rd is ParticleSystemRenderer) continue; if (!any) { bb = rd.bounds; any = true; } else bb.Encapsulate(rd.bounds); }
        Vector3 ax = (w.tip.position - w.sock.position).normalized;
        float lo = 0f, hi = 0f;   // extent of the visible weapon along the shaft (all 8 corners of the axis-aligned bounds projected: an over-estimate when the shaft is diagonal)
        if (any) { lo = float.MaxValue; hi = float.MinValue; for (int k = 0; k < 8; k++) { Vector3 cn = bb.center + Vector3.Scale(bb.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f)); float s = Vector3.Dot(cn - w.sock.position, ax); lo = Mathf.Min(lo, s); hi = Mathf.Max(hi, s); } }   // hc:ok the 8 corners of a bounding box
        Debug.Log("[physweapon] probe " + f.name + ": profile '" + w.prof.id + "', socket " + w.sock.position.ToString("0.000") + ", TrailTip " + w.tip.position.ToString("0.000") + " (local " + w.tip.localPosition.ToString("0.000") + ", lossyScale " + w.tip.lossyScale.x.ToString("0.000") + "), tip along shaft " + (w.tip.position - w.sock.position).magnitude.ToString("0.000") + " m, visible weapon bounds size " + bb.size.ToString("0.000") + " centre " + bb.center.ToString("0.000") + ", bounds along shaft " + lo.ToString("0.00") + " .. " + hi.ToString("0.00") + ", blade capsule a-b " + (b - a).magnitude.ToString("0.000") + " m r " + r.ToString("0.000"));
    }

    // ---------------- driver (created only when something is on) ----------------
    internal sealed class Driver : MonoBehaviour
    {
        static Driver inst;
        public static void Dead() { inst = null; chips.Clear(); props.Clear(); }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Boot() { if (Cfg.enabled && (Cfg.clash.on || Cfg.pin.on || Cfg.severed.on || Cfg.debris.on || Cfg.props.on)) Ensure(); }
        public static void Ensure()
        {
            if (inst != null) return;
            var go = new GameObject("PhysWeaponDriver"); DontDestroyOnLoad(go); inst = go.AddComponent<Driver>();
        }
        bool probed;
        void OnEnable() { ContactRelay.OnEvent += OnEvent; PhysicsClock.OnStep += OnStep; SceneManager.sceneLoaded += OnScene; }
        void OnDisable() { ContactRelay.OnEvent -= OnEvent; PhysicsClock.OnStep -= OnStep; SceneManager.sceneLoaded -= OnScene; if (inst == this) inst = null; }
        void OnScene(Scene s, LoadSceneMode m) { ClearScene(); }

        // ---- events ----
        void OnEvent(CombatEvent e)
        {
            try
            {
                if (!PhysWeapon.Active) return;
                if (!probed && Flag("-physweapon:probe") && e.actor != null && e.target != null) { probed = true; PhysWeapon.Probe(e.actor); PhysWeapon.Probe(e.target); }
                var c = Cfg;
                if (c.debris.on) { var row = RowFor(c.debris.rows, e.type); if (row != null && (!row.heavyOnly || e.heavy)) { Vector3 at = e.target != null ? e.target.transform.position : e.point; Vector3 dir = e.target != null && e.actor != null ? e.target.transform.position - e.actor.transform.position : Vector3.forward; SpawnChips(at, dir, row, e.heavy ? 1f : c.debris.lightStrength, e.tick); } }
                if (c.props.on && Array.IndexOf(c.props.onTypes, e.type) >= 0)
                {
                    Vector3 at = e.target != null ? e.target.transform.position : e.point; Vector3 dir = e.target != null && e.actor != null ? e.target.transform.position - e.actor.transform.position : Vector3.forward;
                    KnockProps(at, dir, e.heavy, e.type, e.heavy ? 1f : c.props.lightStrength);
                }
            }
            catch (Exception ex) { if (!warned) { warned = true; Debug.LogError("[physweapon] event failed (further errors muted): " + ex); } }
        }
        static bool warned;
        static PwDebrisRow RowFor(PwDebrisRow[] rows, string type) { for (int i = 0; i < rows.Length; i++) if (rows[i] != null && rows[i].type == type) return rows[i]; return null; }

        // ---- per sim step ----
        void OnStep(float dt)
        {
            if (pin != null) { pin.Step(dt); if (pin.done) pin = null; }
            StepChips(dt); StepProps(dt);
        }

        // ---- xorshift (no UnityEngine.Random: the sim might use it) ----
        struct Rng { public uint s; public Rng(int seed) { s = (uint)(seed * 747796405 + 2891336453u); if (s == 0) s = 0x9E3779B9u; } public float F() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; } }   // hc:ok xorshift constants, not a tunable

        // ---- debris chips ----
        sealed class Chip { public GameObject go; public Rigidbody rb; public BoxCollider bc; public float age, restFor, size; public bool live, frozen; }
        static readonly List<Chip> chips = new List<Chip>();
        static Mesh chipMesh; static Material chipMat; static Transform root; static bool shaderWarned;

        static Transform Root() { if (root == null) { var g = new GameObject("PhysWeaponDebris"); root = g.transform; } return root; }
        static Mesh ChipMesh()
        {
            if (chipMesh != null) return chipMesh;
            // irregular flat-shaded octahedron (8 triangles, 24 vertices): a rock chip, convex, unit-ish size
            var cv = Cfg.debris.chipVerts; if (cv == null || cv.Length < 18) { Debug.LogWarning("[physweapon] debris.chipVerts needs 18 numbers (6 points xyz): no chips"); return null; }   /* PHYS2 v109h */   // hc:ok chip format: 6 points x 3 numbers
            Vector3[] v = new Vector3[6]; for (int q = 0; q < 6; q++) v[q] = new Vector3(cv[q * 3], cv[q * 3 + 1], cv[q * 3 + 2]);   // hc:ok chip format: 6 points x 3 numbers
            int[,] f = { { 0, 2, 4 }, { 4, 2, 1 }, { 1, 2, 5 }, { 5, 2, 0 }, { 4, 3, 0 }, { 1, 3, 4 }, { 5, 3, 1 }, { 0, 3, 5 } };   // hc:ok octahedron face topology
            var vs = new List<Vector3>(); var ns = new List<Vector3>(); var ix = new List<int>();
            for (int i = 0; i < 8; i++)   // hc:ok octahedron has 8 faces
            {
                Vector3 a = v[f[i, 0]], b = v[f[i, 1]], c = v[f[i, 2]]; Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                if (Vector3.Dot(n, (a + b + c) / 3f) < 0f) { var t = b; b = c; c = t; n = -n; }
                int o = vs.Count; vs.Add(a); vs.Add(b); vs.Add(c); ns.Add(n); ns.Add(n); ns.Add(n); ix.Add(o); ix.Add(o + 1); ix.Add(o + 2);
            }
            chipMesh = new Mesh { name = "PhysWeaponChip" }; chipMesh.SetVertices(vs); chipMesh.SetNormals(ns); chipMesh.SetTriangles(ix, 0); chipMesh.RecalculateBounds();
            return chipMesh;
        }
        static Material ChipMat()
        {
            if (chipMat != null) return chipMat;
            var d = Cfg.debris; var sh = Shader.Find(d.shader);
            if (sh == null) { if (!shaderWarned) { shaderWarned = true; Debug.LogWarning("[physweapon] debris shader '" + d.shader + "' not found: no chips"); } return null; }
            chipMat = new Material(sh) { name = "PhysWeaponChip" };
            var col = new Color(d.color[0], d.color[1], d.color[2], 1f);
            if (chipMat.HasProperty("_BaseColor")) chipMat.SetColor("_BaseColor", col); else chipMat.color = col;
            if (chipMat.HasProperty("_Smoothness")) chipMat.SetFloat("_Smoothness", d.smoothness);
            if (chipMat.HasProperty("_Metallic")) chipMat.SetFloat("_Metallic", 0f);
            return chipMat;
        }
        static Chip NewChip()
        {
            var go = new GameObject("Chip"); go.transform.SetParent(Root(), false); go.layer = PhysMasks.PropLayer;
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = ChipMesh(); var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = ChipMat();
            mr.shadowCastingMode = Cfg.debris.castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            var bc = go.AddComponent<BoxCollider>(); var cz = Cfg.debris.colliderSize; bc.size = (cz != null && cz.Length >= 3) ? new Vector3(cz[0], cz[1], cz[2]) : Vector3.one; var rb = go.AddComponent<Rigidbody>();   /* PHYS2 v109h */
            rb.interpolation = RigidbodyInterpolation.None; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; rb.linearDamping = Cfg.debris.linearDamping; rb.angularDamping = Cfg.debris.angularDamping;
            rb.isKinematic = true; go.SetActive(false);
            return new Chip { go = go, rb = rb, bc = bc };
        }
        public static void SpawnChips(Vector3 at, Vector3 dir, PwDebrisRow row, float strength, int seed)
        {
            var d = Cfg.debris; if (!d.on || row == null || ChipMat() == null) return;
            dir.y = 0f; if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward; dir.Normalize();
            Vector3 g = at; RaycastHit h; if (Physics.Raycast(at + Vector3.up * d.groundProbeUpM, Vector3.down, out h, d.groundProbeLenM, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) g = h.point; else g.y = at.y;
            var rng = new Rng(seed ^ (PhysicsClock.StepCount * 31)); int n = Mathf.Max(0, Mathf.RoundToInt(row.count * Mathf.Clamp01(strength)));   // hc:ok seed mix, not a tunable
            for (int i = 0; i < n; i++)
            {
                Chip c = null;
                for (int k = 0; k < chips.Count; k++) if (!chips[k].live) { c = chips[k]; break; }
                if (c == null)
                {
                    if (chips.Count < d.maxLive) { c = NewChip(); chips.Add(c); }
                    else { c = chips[0]; for (int k = 1; k < chips.Count; k++) if (chips[k].age > c.age) c = chips[k]; }   // recycle the oldest
                }
                float size = Mathf.Lerp(d.minSizeM, d.maxSizeM, rng.F());
                float ang = rng.F() * Mathf.PI * 2f, rad = rng.F() * row.spreadM;
                Vector3 pos = g + new Vector3(Mathf.Cos(ang) * rad, d.spawnLiftM + size, Mathf.Sin(ang) * rad);
                Vector3 sp = (dir * (d.dirBias + rng.F()) + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang))).normalized * Mathf.Lerp(row.minMps, row.maxMps, rng.F()) * Mathf.Lerp(d.strengthFloor, 1f, strength);
                Vector3 vel = sp + Vector3.up * row.upMps * (d.upJitterMin + rng.F());
                c.go.SetActive(true); c.go.transform.SetPositionAndRotation(pos, Quaternion.Euler(rng.F() * 360f, rng.F() * 360f, rng.F() * 360f)); c.go.transform.localScale = Vector3.one * (size * 2f);
                c.rb.isKinematic = false; c.rb.mass = Mathf.Max(0.005f, d.massKg * size / Mathf.Max(0.001f, d.maxSizeM)); c.bc.enabled = true;   // hc:ok divide / min-mass guards
                c.rb.linearVelocity = vel; c.rb.angularVelocity = new Vector3(rng.F() - 0.5f, rng.F() - 0.5f, rng.F() - 0.5f) * d.spinRadPerS;
                c.age = 0f; c.restFor = 0f; c.size = size * 2f; c.live = true; c.frozen = false; ChipsSpawned++;
            }
        }
        static void StepChips(float dt)
        {
            var d = Cfg.debris;
            for (int i = 0; i < chips.Count; i++)
            {
                var c = chips[i]; if (!c.live) continue; c.age += dt;
                if (!c.frozen)
                {
                    bool slow = c.rb.linearVelocity.sqrMagnitude < d.restSpeed * d.restSpeed && c.rb.angularVelocity.sqrMagnitude < d.restAngSpeed * d.restAngSpeed;
                    c.restFor = slow ? c.restFor + dt : 0f;
                    if (c.restFor >= d.freezeAfterRestS || c.rb.position.y < d.killY) { c.frozen = true; c.rb.linearVelocity = Vector3.zero; c.rb.angularVelocity = Vector3.zero; c.rb.isKinematic = true; c.bc.enabled = false; }
                }
                float left = d.lifeS - c.age;
                if (left <= d.shrinkS) c.go.transform.localScale = Vector3.one * (c.size * Mathf.Clamp01(left / Mathf.Max(0.01f, d.shrinkS)));   // hc:ok divide guard
                if (left <= 0f) { c.live = false; c.rb.isKinematic = true; c.go.SetActive(false); }
            }
        }

        // ---- loose props ----
        sealed class Prop { public PwPropRow row; public Transform t; public Rigidbody rb; public BoxCollider bc; public bool moved, frozen, missing; public float age, restFor; }
        static readonly List<Prop> props = new List<Prop>();
        static bool propsScanned;
        static void ScanProps()
        {
            propsScanned = true; props.Clear();
            var rows = Cfg.props.rows;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null || string.IsNullOrEmpty(rows[i].name)) continue;
                var go = GameObject.Find(rows[i].name); props.Add(new Prop { row = rows[i], t = go != null ? go.transform : null, missing = go == null });
            }
        }
        public static void KnockProps(Vector3 at, Vector3 dir, bool heavy, string type, float strength)
        {
            var pc = Cfg.props; if (!pc.on) return;
            if (!propsScanned) ScanProps();
            dir.y = 0f; if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward; dir.Normalize();
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i]; if (p.moved || p.missing || p.t == null || (p.row.heavyOnly && !heavy && type != "finisher")) continue;
                Vector3 off = p.t.position - at; off.y = 0f; if (off.magnitude > p.row.radiusM) continue;
                MakeBody(p, off.sqrMagnitude > 1e-4f ? off.normalized : dir, strength);
            }
        }
        static void MakeBody(Prop p, Vector3 away, float strength)
        {
            var pc = Cfg.props; Renderer[] rs; var lg = p.t.GetComponent<LODGroup>();
            if (lg != null) { var l = lg.GetLODs(); rs = l.Length > 0 ? l[0].renderers : new Renderer[0]; } else rs = p.t.GetComponentsInChildren<Renderer>();
            Bounds b = new Bounds(p.t.position, Vector3.zero); bool any = false;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i]; if (r == null) continue;
                if (r.isPartOfStaticBatch) { p.missing = true; Debug.LogWarning("[physweapon] prop '" + p.row.name + "' is statically batched: left alone"); return; }
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any || Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) > pc.maxSizeM) { p.missing = true; return; }
            if (p.t.GetComponentInChildren<Collider>(true) != null) { p.missing = true; Debug.LogWarning("[physweapon] prop '" + p.row.name + "' already has a collider (a moving obstacle could change the sim's walking): left alone"); return; }
            var cg = new GameObject("PhysWeaponCol"); cg.layer = PhysMasks.PropLayer; cg.transform.SetParent(p.t, false);
            Vector3 ls = p.t.lossyScale; cg.transform.position = b.center; cg.transform.rotation = Quaternion.identity;
            var bc = cg.AddComponent<BoxCollider>(); bc.size = new Vector3(b.size.x / Mathf.Max(1e-4f, Mathf.Abs(ls.x)), b.size.y / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), b.size.z / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
            var rb = p.t.gameObject.AddComponent<Rigidbody>(); rb.mass = p.row.massKg; rb.interpolation = RigidbodyInterpolation.None; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.linearDamping = Cfg.props.bodyLinearDamping; rb.angularDamping = Cfg.props.bodyAngularDamping; rb.solverIterations = Cfg.props.solverIterations;   /* PHYS2 v109h */
            var rng = new Rng(PhysicsClock.StepCount + p.row.name.Length * 17);   // hc:ok seed mix, not a tunable
            rb.linearVelocity = away * Mathf.Lerp(p.row.minMps, p.row.maxMps, rng.F()) * Mathf.Lerp(Cfg.props.strengthFloor, 1f, strength) + Vector3.up * p.row.upMps;
            rb.angularVelocity = new Vector3(rng.F() - 0.5f, rng.F() - 0.5f, rng.F() - 0.5f).normalized * p.row.spinDegPerS * Mathf.Deg2Rad;
            p.rb = rb; p.bc = bc; p.moved = true; PropsKnocked++;
            Debug.Log("[physweapon] prop '" + p.row.name + "' knocked loose: " + rb.mass.ToString("0.0") + " kg, v " + rb.linearVelocity.magnitude.ToString("0.0") + " m/s");
        }
        static void StepProps(float dt)
        {
            var pc = Cfg.props;
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i]; if (!p.moved || p.frozen || p.rb == null) continue; p.age += dt;
                bool slow = p.rb.linearVelocity.sqrMagnitude < pc.restSpeed * pc.restSpeed && p.rb.angularVelocity.sqrMagnitude < pc.restAngSpeed * pc.restAngSpeed;
                p.restFor = slow ? p.restFor + dt : 0f;
                if (p.restFor >= pc.freezeAfterRestS || p.age >= pc.maxSeconds || p.rb.position.y < pc.killY) { p.frozen = true; p.rb.linearVelocity = Vector3.zero; p.rb.angularVelocity = Vector3.zero; p.rb.isKinematic = true; if (p.bc != null) p.bc.enabled = false; }
            }
        }

        // ---- scene reload (rematch) ----
        static void ClearScene()
        {
            if (pin != null) { pin.Release("scene load"); pin = null; }
            for (int i = 0; i < chips.Count; i++) if (chips[i].go != null) Destroy(chips[i].go);
            chips.Clear(); if (root != null) Destroy(root.gameObject); root = null;
            props.Clear(); propsScanned = false; tips.Clear();   // the scene reload put the props back where the scene has them
        }
    }
}
