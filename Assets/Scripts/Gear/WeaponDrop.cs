using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// WEAPONDROP v1 (lane laneWEAPONDROP). When an armed fighter dies, the weapon in its hand leaves the hand at a DATA time after the kill and falls to the ground with weight: a real Rigidbody
// (compound collider: shaft capsule + head box, or one box) on the physProp layer, mass and centre of mass from data (the head is heavy), the hand's own velocity inherited, a small kick away
// from the killer and a tumble. It rests where it lands (flat as the colliders let it), the body and colliders are then destroyed (a resting weapon is a plain prop: no physics cost), and it stays.
// It is collected with the loot: LootUI anchors the pillar of a listed loot item on the weapon (PillarAt) and, when the player picks that item up, the weapon sinks away (OnLootCollected), so one
// maul shows, never two. It also goes when its owner is respawned (same object name alive again), after maxLifeS, or lingerAfterOwnerS after the corpse was removed (no loot link).
// Data: weapon_drop.json (Assets/Data, copy in StreamingAssets). No row for a character, or enabled=false, or no file = nothing happens (today). Sobek's claws (WeaponSwap.Unarmed) and a hand with no
// visible weapon do nothing. Retry / scene reload: everything is removed on the Single scene load. -weapondrop:off switches the whole thing off.
// PRESENTATION ONLY: it reads DuelFighter and the Animator pose and writes nothing of the sim; no UnityEngine.Random; the colliders ignore every CharacterController and the victim's own colliders.
// Nothing runs until a row-matching fighter dies (one event subscription); the runner only exists while a drop is alive.
public static class WeaponDrop
{
    // ---------------- data (Assets/Data/weapon_drop.json) ----------------
    [Serializable]
    public class BodyCfg
    {
        public float friction = 0.8f, restitution = 0.15f, linearDamping = 0.05f, angularDamping = 0.3f, maxDepenetration = 2f, maxAngularSpeed = 30f;
        public float restSpeed = 0.06f, restAngularSpeed = 0.25f, restSeconds = 0.3f, maxFallSeconds = 8f, rescueBelowM = 4f, floorGapM = 0.004f, settleSeconds = 0.25f, groundRayUpM = 1f, groundRayDownM = 12f, handSmooth = 0.5f;
        public int solverIterations = 12, solverVelocityIterations = 4;
        public string detection = "ContinuousDynamic";
    }
    [Serializable]
    public class Shape
    {
        public string weapon = "", type = "headed", axis = "y", note = "";   // type: headed (shaft capsule + head box from headFrom of the mesh length along axis) | box (the whole mesh bounds x boxShrink)
        public float headFrom = 0.6f, shaftRadiusM = 0.04f;
        public float[] boxShrink = { 1f, 1f, 1f }, headShrink = { 1f, 1f, 1f };
    }
    [Serializable]
    public class Row
    {
        public string character = "", weapon = "", note = "";   // roster id; weapon "" = whatever the hand holds
        public bool enabled = true, duringFinisher = true, vanishOnRespawn = true;
        public float delayS = 0.35f, inheritVelocity = 0.6f, maxInheritMps = 8f, kickMps = 0.6f, liftMps = 0.4f, spinRadS = 4f, spinYawRadS = 0.5f;
        public float massKg = 7f, headMassShare = 0.8f, maxLifeS = 280f, lingerAfterOwnerS = 20f, sinkS = 1.5f;
        public string[] lootItems = new string[0];   // gear.json item ids whose loot pillar belongs to this weapon
    }
    [Serializable]
    public class Root
    {
        public string note = "";
        public bool enabled = true, log = true, bestLodOnly = true;
        public float pollS = 0.5f;
        public string[] sockets = { "WeaponSocket_R", "WeaponSocket_L" }, keepChildren = { "TrailTip" };
        public BodyCfg body = new BodyCfg();
        public Shape[] shapes = new Shape[0];
        public Row[] characters = new Row[0];
    }

    static Root cfg; static WeaponDropRunner runner; static PhysicsMaterial mat;
    static readonly List<Drop> drops = new List<Drop>();
    public static int Released, Rested, Vanished, Restored;   // counters for the in-game checks

    public static Root Cfg { get { if (cfg == null) Load(); return cfg; } }
    public static void Reload() { cfg = null; Load(); }
    public static int Alive { get { return drops.Count; } }

    static void Load()
    {
        cfg = new Root();
        string text = DataPath.ReadText("weapon_drop.json");
        if (string.IsNullOrEmpty(text)) { cfg.enabled = false; return; }   // no file = today
        try { var r = JsonUtility.FromJson<Root>(text); if (r != null) cfg = r; }
        catch (Exception ex) { Debug.LogWarning("[weapondrop] weapon_drop.json parse failed: " + ex.Message); cfg = new Root(); cfg.enabled = false; }
        if (cfg.characters == null) cfg.characters = new Row[0];
        if (cfg.shapes == null) cfg.shapes = new Shape[0];
        if (cfg.sockets == null) cfg.sockets = new string[0];
        if (cfg.keepChildren == null) cfg.keepChildren = new string[0];
        if (cfg.body == null) cfg.body = new BodyCfg();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cfg = null; runner = null; mat = null; drops.Clear(); Released = Rested = Vanished = Restored = 0; ContactRelay.OnKilled -= OnKilled; SceneManager.sceneLoaded -= OnSceneLoaded; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        ContactRelay.OnKilled -= OnKilled; SceneManager.sceneLoaded -= OnSceneLoaded;   // no domain reload in the editor: never double-subscribe
        var C = Cfg; if (!C.enabled || C.characters.Length == 0 || Array.IndexOf(Environment.GetCommandLineArgs(), "-weapondrop:off") >= 0) return;
        ContactRelay.OnKilled += OnKilled; SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) Cleanup(); }   // Retry / Next / zone restart reload the scene

    static void Cleanup()
    {
        for (int i = 0; i < drops.Count; i++) if (drops[i].root != null) UnityEngine.Object.Destroy(drops[i].root.gameObject);
        drops.Clear(); if (runner != null) runner.enabled = false;
    }

    // ---------------- the kill ----------------
    sealed class Drop
    {
        public DuelFighter owner; public string ownerName = "", cid = "", wid = ""; public Row row; public Shape shape;
        public Transform main; public MeshFilter mainMf; public Transform root;
        public readonly List<Transform> pieces = new List<Transform>(); public readonly List<Transform> parents = new List<Transform>();
        public readonly List<Vector3> lpos = new List<Vector3>(), lscale = new List<Vector3>(); public readonly List<Quaternion> lrot = new List<Quaternion>(); public readonly List<bool> act = new List<bool>();
        public Rigidbody rb; public readonly List<GameObject> cols = new List<GameObject>();
        public int state; public float t, life, restFor, poll, easeT, sinkT, sinkDepth, groundY, ownerGone;   // state: 0 in the hand, 1 falling, 2 resting, 3 sinking
        public bool scripted, ease;
        public Vector3 prev, handV, vel, ang, sinkFrom, easeFromP, easeToP; public Quaternion easeFromR, easeToR;
        public string link = "";
    }

    static void OnKilled(DuelFighter victim, MoveDef m)
    {
        try { Begin(victim); } catch (Exception ex) { Debug.LogWarning("[weapondrop] kill hook: " + ex.Message); }
    }

    static void Begin(DuelFighter v)
    {
        var C = Cfg; if (v == null || !C.enabled) return;
        for (int i = 0; i < drops.Count; i++) if (drops[i].owner == v) return;   // a kill is handled once (the finisher path and Die() can both fire)
        string cid = v.CharacterId, wid = WeaponSwap.IdOf(v.transform.root);
        if (wid.Length == 0) { var rc = Roster.Get(cid); wid = rc != null ? rc.weapon : ""; }
        var ws = v.GetComponent<WeaponSwap>(); if (ws != null && ws.Unarmed) return;   // claws: nothing to drop
        Row row = FindRow(C, cid, wid); if (row == null || !row.enabled) return;
        if (!row.duringFinisher && RTFinisher.Busy) return;
        var d = new Drop { owner = v, ownerName = v.name, cid = cid, wid = wid, row = row, shape = FindShape(C, wid) };
        Transform sock = null;
        for (int i = 0; i < C.sockets.Length && sock == null; i++) { var s = V1Extras.FindExact(v.transform, C.sockets[i]); if (s != null && Collect(C, s, d)) sock = s; }
        if (sock == null) { if (C.log) Debug.Log("[weapondrop] " + v.name + " (" + cid + "): no visible weapon on " + string.Join("/", C.sockets) + ", nothing to drop"); return; }
        d.prev = d.main.position; drops.Add(d);
        if (runner == null) { var go = new GameObject("WeaponDropRunner"); UnityEngine.Object.DontDestroyOnLoad(go); runner = go.AddComponent<WeaponDropRunner>(); }
        runner.enabled = true;
        if (C.log) Debug.Log("[weapondrop] " + v.name + " (" + cid + ", weapon '" + wid + "') dies: " + d.pieces.Count + " piece(s) leave the hand in " + row.delayS.ToString("0.00") + " s");
    }

    static Row FindRow(Root C, string cid, string wid)
    {
        Row any = null;
        for (int i = 0; i < C.characters.Length; i++)
        {
            var r = C.characters[i]; if (r == null || RosterResolver.Resolve(r.character) != cid) continue;
            if (!string.IsNullOrEmpty(r.weapon)) { if (r.weapon == wid) return r; }
            else if (any == null) any = r;
        }
        return any;
    }

    static Shape FindShape(Root C, string wid)
    {
        for (int i = 0; i < C.shapes.Length; i++) if (C.shapes[i] != null && C.shapes[i].weapon == wid) return C.shapes[i];
        for (int i = 0; i < C.shapes.Length; i++) if (C.shapes[i] != null && C.shapes[i].weapon.Length == 0) return C.shapes[i];
        return new Shape { type = "box" };
    }

    static bool IsVisualRenderer(Renderer r) { return r != null && r.enabled && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer); }

    // the socket's visible weapon pieces (children with an enabled mesh renderer; the trail node and switched-off swap leftovers stay); the piece with the most vertices is the main one
    static bool Collect(Root C, Transform sock, Drop d)
    {
        int best = -1;
        for (int i = 0; i < sock.childCount; i++)
        {
            var ch = sock.GetChild(i); if (Array.IndexOf(C.keepChildren, ch.name) >= 0 || !ch.gameObject.activeInHierarchy) continue;
            MeshFilter bm = null; int bv = -1;
            foreach (var mf in ch.GetComponentsInChildren<MeshFilter>(false))
            {
                var mr = mf.GetComponent<Renderer>(); if (!IsVisualRenderer(mr) || mf.sharedMesh == null) continue;
                if (mf.sharedMesh.vertexCount > bv) { bv = mf.sharedMesh.vertexCount; bm = mf; }
            }
            if (bm == null) continue;
            d.pieces.Add(ch); d.parents.Add(sock); d.lpos.Add(ch.localPosition); d.lrot.Add(ch.localRotation); d.lscale.Add(ch.localScale); d.act.Add(ch.gameObject.activeSelf);
            if (bv > best) { best = bv; d.main = ch; d.mainMf = bm; }
        }
        return d.main != null;
    }

    // ---------------- per frame (runner) ----------------
    internal static void Tick(float dt)
    {
        var C = Cfg;
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            var d = drops[i]; bool done = false;
            try { done = Step(d, C, dt); } catch (Exception ex) { Debug.LogWarning("[weapondrop] " + d.ownerName + ": " + ex.Message); done = true; }
            if (done) { if (d.root != null) UnityEngine.Object.Destroy(d.root.gameObject); drops.RemoveAt(i); }
        }
        if (drops.Count == 0 && runner != null) runner.enabled = false;
    }

    static bool Step(Drop d, Root C, float dt)
    {
        switch (d.state)
        {
            case 0: return InHand(d, C, dt);
            case 1: return Falling(d, C, dt);
            case 2: return Resting(d, C, dt);
            default: return Sinking(d, dt);
        }
    }

    static bool InHand(Drop d, Root C, float dt)
    {
        if (d.main == null) return true;   // the owner went away before the release: its hand took the weapon along
        if (d.owner != null && !d.owner.Dead) { Restore(d, C); return true; }   // the fighter is back on its feet: the weapon stays / returns to the hand
        Vector3 p = d.main.position;
        if (dt > Mathf.Epsilon) d.handV = Vector3.Lerp(d.handV, (p - d.prev) / dt, C.body.handSmooth);
        d.prev = p; d.t += dt;
        if (d.t >= d.row.delayS) Release(d, C);
        return false;
    }

    static void Release(Drop d, Root C)
    {
        // new unit-scale root at the weapon's grip pose; the pieces keep their world pose and scale
        var go = new GameObject("DroppedWeapon_" + d.ownerName); go.transform.SetPositionAndRotation(d.main.position, d.main.rotation); d.root = go.transform;
        for (int i = 0; i < d.pieces.Count; i++)
        {
            var p = d.pieces[i]; if (p == null) continue;
            p.SetParent(d.root, true);
            if (C.bestLodOnly && p != d.main) p.gameObject.SetActive(false);   // the other LODs stay in the hand's list for Restore, but are not shown on the ground
        }
        d.state = 1; Released++;
        Vector3 pos = d.root.position; float gy; bool ground = Ground(C, pos, d.owner, out gy); d.groundY = ground ? gy : (d.owner != null ? d.owner.transform.position.y : pos.y);
        Vector3 away = Away(d.owner);
        Vector3 v = Vector3.ClampMagnitude(d.handV * d.row.inheritVelocity, d.row.maxInheritMps) + away * d.row.kickMps + Vector3.up * d.row.liftMps;
        Vector3 w = Vector3.Cross(Vector3.up, away) * d.row.spinRadS + Vector3.up * d.row.spinYawRadS;
        d.scripted = !ground || !PhysicsClock.PhysicsOn;   // no ground under it, or physics switched off (-physics:off): a scripted fall
        if (d.scripted) { d.vel = v; d.ang = w; }
        else BuildBody(d, C, v, w);
        if (C.log) Debug.Log("[weapondrop] " + d.ownerName + " drops '" + d.wid + "' at " + pos.ToString("0.00") + " v " + v.ToString("0.00") + " (" + (d.scripted ? "scripted fall, ground " + (ground ? "found" : "NOT found") : "physics: " + d.row.massKg.ToString("0.0") + " kg") + "), ground y " + d.groundY.ToString("0.00"));
    }

    static Vector3 Away(DuelFighter owner)
    {
        Vector3 a = Vector3.zero;
        if (owner != null && owner.opponent != null) a = owner.transform.position - owner.opponent.transform.position;
        else if (owner != null) a = -owner.transform.forward;
        a.y = 0f; if (a.sqrMagnitude < Mathf.Epsilon) a = Vector3.forward;
        return a.normalized;
    }

    static readonly RaycastHit[] hits = new RaycastHit[16];
    // the real ground below p: the first hit of the project's ground mask that is not a fighter or one of its ragdoll parts
    static bool Ground(Root C, Vector3 p, DuelFighter owner, out float y)
    {
        y = p.y; var B = C.body;
        int n = Physics.RaycastNonAlloc(p + Vector3.up * B.groundRayUpM, Vector3.down, hits, B.groundRayUpM + B.groundRayDownM, PhysMasks.Ground, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;
            if (h.distance < best) { best = h.distance; y = h.point.y; }
        }
        return best < float.MaxValue;
    }

    static PhysicsMaterial Mat(BodyCfg B)
    {
        if (mat == null) mat = new PhysicsMaterial("WeaponDrop") { dynamicFriction = B.friction, staticFriction = B.friction, bounciness = B.restitution, frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Average };
        return mat;
    }

    static int AxisOf(string a) { return a == "x" ? 0 : a == "z" ? 2 : 1; }

    static GameObject AddCol(Transform frame, bool capsule, int axis, Vector3 center, Vector3 size, float radius, float height, PhysicsMaterial pm)
    {
        var g = new GameObject("DropCol"); g.layer = PhysMasks.PropLayer; g.transform.SetParent(frame, false);
        if (capsule) { var c = g.AddComponent<CapsuleCollider>(); c.direction = axis; c.center = center; c.radius = radius; c.height = height; c.sharedMaterial = pm; }
        else { var b = g.AddComponent<BoxCollider>(); b.center = center; b.size = size; b.sharedMaterial = pm; }
        return g;
    }

    static Vector3 Shrunk(Vector3 s, float[] k) { return k != null && k.Length >= 3 ? new Vector3(s.x * k[0], s.y * k[1], s.z * k[2]) : s; }

    static void BuildBody(Drop d, Root C, Vector3 v, Vector3 w)
    {
        var B = C.body; var pm = Mat(B); var S = d.shape; var R = d.row;
        Transform frame = d.mainMf.transform; Bounds b = d.mainMf.sharedMesh.bounds; int ax = AxisOf(S.axis);
        Vector3 com;
        if (S.type == "headed")
        {
            float lo = b.min[ax], hi = b.max[ax], hs = Mathf.Lerp(lo, hi, S.headFrom);
            Vector3 sc = Vector3.zero; sc[ax] = (lo + hs) * 0.5f;
            Vector3 hc = b.center; hc[ax] = (hs + hi) * 0.5f; Vector3 hz = b.size; hz[ax] = hi - hs;
            d.cols.Add(AddCol(frame, true, ax, sc, Vector3.zero, S.shaftRadiusM, Mathf.Max(2f * S.shaftRadiusM, hs - lo), pm));
            d.cols.Add(AddCol(frame, false, ax, hc, Shrunk(hz, S.headShrink), 0f, 0f, pm));
            com = Vector3.Lerp(sc, hc, R.headMassShare);
        }
        else { d.cols.Add(AddCol(frame, false, ax, b.center, Shrunk(b.size, S.boxShrink), 0f, 0f, pm)); com = b.center; }
        var rb = d.root.gameObject.AddComponent<Rigidbody>(); d.rb = rb;
        rb.isKinematic = false; rb.useGravity = true; rb.mass = Mathf.Max(Mathf.Epsilon, R.massKg);
        rb.linearDamping = B.linearDamping; rb.angularDamping = B.angularDamping; rb.maxDepenetrationVelocity = B.maxDepenetration; rb.maxAngularVelocity = B.maxAngularSpeed;
        rb.solverIterations = Mathf.Max(1, B.solverIterations); rb.solverVelocityIterations = Mathf.Max(1, B.solverVelocityIterations);
        rb.interpolation = RigidbodyInterpolation.None;   // Physics.Simulate (scripted clock) ignores interpolation
        CollisionDetectionMode cdm = CollisionDetectionMode.ContinuousDynamic;
        try { cdm = (CollisionDetectionMode)Enum.Parse(typeof(CollisionDetectionMode), B.detection, true); } catch (Exception) { }
        rb.collisionDetectionMode = cdm;
        rb.centerOfMass = d.root.InverseTransformPoint(frame.TransformPoint(com));
        IgnoreOthers(d);
        rb.linearVelocity = v; rb.angularVelocity = w;
    }

    // the weapon never collides with a CharacterController (the sim moves through them), with the victim's own colliders (hand, ragdoll capsules) or with the killer's
    static void IgnoreOthers(Drop d)
    {
        var mine = new List<Collider>(); for (int i = 0; i < d.cols.Count; i++) mine.AddRange(d.cols[i].GetComponents<Collider>());
        var other = new List<Collider>();
        foreach (var cc in UnityEngine.Object.FindObjectsByType<CharacterController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) other.Add(cc);
        if (d.owner != null) other.AddRange(d.owner.GetComponentsInChildren<Collider>(true));
        if (d.owner != null && d.owner.opponent != null) other.AddRange(d.owner.opponent.GetComponentsInChildren<Collider>(true));
        for (int i = 0; i < other.Count; i++)
        {
            var o = other[i]; if (o == null || !o.enabled || !o.gameObject.activeInHierarchy) continue;   // Physics.IgnoreCollision logs an error for a disabled collider
            for (int j = 0; j < mine.Count; j++) { try { Physics.IgnoreCollision(o, mine[j], true); } catch (Exception) { } }
        }
    }

    static bool Falling(Drop d, Root C, float dt)
    {
        if (d.root == null) return true;
        var B = C.body; d.life += dt; d.t += dt;
        if (d.scripted) { ScriptedFall(d, C, dt); return false; }
        if (d.rb == null) { Settle(d, C, "body lost"); return false; }
        if (d.root.position.y < d.groundY - B.rescueBelowM) { Debug.LogWarning("[weapondrop] " + d.ownerName + ": the weapon fell out of the world, laid on the ground"); Teardown(d); LayFlat(d, C); return false; }
        bool slow = d.rb.linearVelocity.sqrMagnitude < B.restSpeed * B.restSpeed && d.rb.angularVelocity.sqrMagnitude < B.restAngularSpeed * B.restAngularSpeed;
        d.restFor = slow ? d.restFor + dt : 0f;
        if (d.restFor >= B.restSeconds) Settle(d, C, "rest");
        else if (d.t - d.row.delayS >= B.maxFallSeconds) Settle(d, C, "time limit");
        return false;
    }

    static void Teardown(Drop d)
    {
        for (int i = 0; i < d.cols.Count; i++) if (d.cols[i] != null) UnityEngine.Object.Destroy(d.cols[i]);
        d.cols.Clear(); if (d.rb != null) UnityEngine.Object.Destroy(d.rb); d.rb = null;
    }

    // at rest: the body and colliders go (a resting weapon costs nothing), the weapon stays as a prop
    static void Settle(Drop d, Root C, string why)
    {
        Teardown(d); d.state = 2; Rested++; d.poll = 0f;
        if (C.log) Debug.Log("[weapondrop] " + d.ownerName + " '" + d.wid + "' at rest (" + why + ") " + d.life.ToString("0.0") + " s after the release, at " + d.root.position.ToString("0.00") + ", ground y " + d.groundY.ToString("0.00") + ", up.y " + RestUpY(d).ToString("0.00"));
    }

    static float RestUpY(Drop d)
    {
        if (d.mainMf == null || d.mainMf.sharedMesh == null) return 0f;
        Vector3 s = d.mainMf.sharedMesh.bounds.size; int hi = 0; for (int i = 1; i < 3; i++) if (s[i] > s[hi]) hi = i;
        Vector3 ax = Vector3.zero; ax[hi] = 1f; return d.mainMf.transform.TransformDirection(ax).normalized.y;   // 0 = the long axis lies horizontal
    }

    static void ScriptedFall(Drop d, Root C, float dt)
    {
        var B = C.body;
        d.vel += Physics.gravity * dt; d.root.position += d.vel * dt;
        float a = d.ang.magnitude; if (a > Mathf.Epsilon) d.root.rotation = Quaternion.AngleAxis(a * Mathf.Rad2Deg * dt, d.ang / a) * d.root.rotation;
        Bounds b;
        if (!VisualBounds(d.root, out b)) { LayFlat(d, C); return; }
        if (b.min.y <= d.groundY + B.floorGapM || d.t - d.row.delayS >= B.maxFallSeconds) LayFlat(d, C);
    }

    // the long axis horizontal, the thinnest axis up (the same rule as the opened-finisher DroppedWeapon), eased in over settleSeconds, the lowest point on the ground
    static void LayFlat(Drop d, Root C)
    {
        var B = C.body; d.easeFromP = d.root.position; d.easeFromR = d.root.rotation;
        Quaternion r0 = d.root.rotation, rRest = r0;
        var mf = d.mainMf;
        if (mf != null && mf.sharedMesh != null)
        {
            Vector3 sz = mf.sharedMesh.bounds.size; int lo = 0, hi = 0; for (int i = 1; i < 3; i++) { if (sz[i] > sz[hi]) hi = i; if (sz[i] < sz[lo]) lo = i; }
            if (lo == hi) lo = (hi + 1) % 3;
            Vector3 lAx = Vector3.zero, tAx = Vector3.zero; lAx[hi] = 1f; tAx[lo] = 1f;
            Vector3 L = mf.transform.TransformDirection(lAx).normalized, T = mf.transform.TransformDirection(tAx).normalized;
            Vector3 H = L; H.y = 0f; if (H.sqrMagnitude < Mathf.Epsilon) H = Away(d.owner); H.Normalize();
            if (Mathf.Abs(Vector3.Dot(L, T)) < 1f - Mathf.Epsilon)
            {
                Vector3 Tp = Vector3.ProjectOnPlane(T, L); if (Tp.sqrMagnitude < Mathf.Epsilon) Tp = Vector3.up; else Tp.Normalize();
                rRest = Quaternion.LookRotation(H, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(L, Tp)) * r0;
            }
        }
        d.root.rotation = rRest; Bounds b;
        Vector3 p = d.root.position; if (VisualBounds(d.root, out b)) p.y += d.groundY + B.floorGapM - b.min.y;
        d.easeToP = p; d.easeToR = rRest; d.root.SetPositionAndRotation(d.easeFromP, d.easeFromR);
        d.ease = true; d.easeT = 0f; d.state = 2; d.poll = 0f; Rested++;
        if (C.log) Debug.Log("[weapondrop] " + d.ownerName + " '" + d.wid + "' laid flat at " + p.ToString("0.00") + " (ground y " + d.groundY.ToString("0.00") + ")");
    }

    static bool VisualBounds(Transform t, out Bounds b)
    {
        b = new Bounds(t.position, Vector3.zero); bool any = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>(false)) { if (!IsVisualRenderer(r)) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
        return any;
    }

    // ---------------- lying on the ground ----------------
    static bool Resting(Drop d, Root C, float dt)
    {
        if (d.root == null) return true;
        if (d.ease)
        {
            d.easeT += dt; float k = Mathf.Clamp01(d.easeT / Mathf.Max(Mathf.Epsilon, C.body.settleSeconds)); k = k * k * (3f - 2f * k);
            d.root.SetPositionAndRotation(Vector3.Lerp(d.easeFromP, d.easeToP, k), Quaternion.Slerp(d.easeFromR, d.easeToR, k)); if (k >= 1f) d.ease = false;
        }
        d.life += dt; d.poll += dt; if (d.owner != null) d.ownerGone = 0f; else d.ownerGone += dt;
        var R = d.row; bool linked = d.link.Length > 0;
        if (R.maxLifeS > 0f && d.life >= R.maxLifeS) return StartSink(d, C, "lifetime");
        if (d.owner == null && !linked && R.lingerAfterOwnerS >= 0f && d.ownerGone >= R.lingerAfterOwnerS) return StartSink(d, C, "owner removed");
        if (d.poll >= C.pollS)
        {
            d.poll = 0f;
            if (d.owner == null && R.vanishOnRespawn && Respawned(d)) return StartSink(d, C, "owner respawned");
        }
        return false;
    }

    // the owner object was destroyed (corpse removed); a live fighter with the same object name (a zone respawn) means the weapon is back in its hand: this one goes
    static bool Respawned(Drop d)
    {
        foreach (var f in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f != null && !f.Dead && f.name == d.ownerName) return true;
        return false;
    }

    static bool StartSink(Drop d, Root C, string why)
    {
        Teardown(d); Bounds b; d.sinkDepth = VisualBounds(d.root, out b) ? b.size.y + C.body.floorGapM + b.extents.y : 1f; d.sinkFrom = d.root.position; d.sinkT = 0f; d.state = 3; d.ease = false;
        if (C.log) Debug.Log("[weapondrop] " + d.ownerName + " '" + d.wid + "' removed (" + why + ")");
        return false;
    }

    static bool Sinking(Drop d, float dt)
    {
        if (d.root == null) return true;
        d.sinkT += dt; float s = Mathf.Max(Mathf.Epsilon, d.row.sinkS), k = Mathf.Clamp01(d.sinkT / s); k = k * k * (3f - 2f * k);
        d.root.position = d.sinkFrom + Vector3.down * (d.sinkDepth * k);
        if (d.sinkT < s) return false;
        Vanished++; return true;
    }

    // the fighter is alive again (a revive path, if one ever exists): pieces go back to the hand exactly where they were
    static void Restore(Drop d, Root C)
    {
        Teardown(d);
        for (int i = 0; i < d.pieces.Count; i++)
        {
            var p = d.pieces[i]; if (p == null || d.parents[i] == null) continue;
            p.SetParent(d.parents[i], false); p.localPosition = d.lpos[i]; p.localRotation = d.lrot[i]; p.localScale = d.lscale[i]; p.gameObject.SetActive(d.act[i]);
        }
        Restored++; if (C.log) Debug.Log("[weapondrop] " + d.ownerName + " is alive again: the weapon is back in the hand");
    }

    // ---------------- the loot link (two one-line calls in LootUI) ----------------
    /// <summary>LootUI.SpawnQueued: where the pillar of this loot item stands. The dropped weapon that lists the item (weapon_drop.json lootItems) and is not linked yet; else p unchanged.</summary>
    public static Vector3 PillarAt(string itemId, Vector3 p)
    {
        if (string.IsNullOrEmpty(itemId)) return p;
        for (int i = 0; i < drops.Count; i++)
        {
            var d = drops[i]; if (d.link.Length > 0 || d.state == 3 || d.root == null || Array.IndexOf(d.row.lootItems, itemId) < 0) continue;
            d.link = itemId; return d.root.position;
        }
        return p;
    }

    /// <summary>LootUI.Collect: the player took the item up; the weapon that stood for it sinks away (one weapon, in the carry list, not also on the ground).</summary>
    public static void OnLootCollected(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        for (int i = 0; i < drops.Count; i++)
        {
            var d = drops[i]; if (d.state == 3 || d.state == 0 || d.root == null) continue;
            if (d.link == itemId || (d.link.Length == 0 && Array.IndexOf(d.row.lootItems, itemId) >= 0)) StartSink(d, Cfg, "collected as loot '" + itemId + "'");
        }
    }
}

// the only per-frame cost: exists after the first matching death, disabled again when the last dropped weapon is gone
public sealed class WeaponDropRunner : MonoBehaviour
{
    void Update() { WeaponDrop.Tick(Time.deltaTime); }
}
