using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// The finisher scene player (was FinisherPlayer). Self-contained: entry pick + rotation (persisted), pair placement, paired clips (killer + victim), timed shots / slow-mo / gore,
// the Die() timing. Everything tunable is data: finishers.json entries. Run() talks to the real-time duel only through RTFinisherHost.
//   cut gore: type "cut" (or head_detach -> unit "head", skull_split -> unit "head_split") -> MeshCut.Cut; type "blood_burst" / failed cut -> a CombatEvent "Hit" at the wound.
public static class FinisherPlayer
{
    public static event Action<FinisherGoreCall> OnGore;
    public static event Action<string> OnFinisherStart;   // entry id (UI / audio / stats)
    public static event Action<string> OnPairProbe;       // V62: probe text at the contact (FinisherAuto summary)
    public static bool ProbeEnabled;
    public static float ClearHead = float.NaN, ClearHand = float.NaN;   // V61C: clearance (m) at the last contact: killer head / killer hand to the victim chest (negative = inside the body)

    class RotState { public int lastIndex = -1, kills; public string lastId; }   // V62: one rotation position PER WEAPON (persisted by FinisherRotationStore)
    static readonly Dictionary<string, RotState> rot = new Dictionary<string, RotState>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { rot.Clear(); OnGore = null; OnFinisherStart = null; OnPairProbe = null; ProbeEnabled = false; }

    // the rotation index survives a restart (Application.persistentDataPath\finisher_rotation.json, FinisherRotationStore); read once on the first pick
    static RotState State(string key)
    {
        RotState s; if (rot.TryGetValue(key, out s)) return s;
        s = new RotState(); FinisherRotationStore.Load(key, out s.lastIndex, out s.kills, out s.lastId); rot[key] = s; return s;
    }
    /// <summary>What a game restart does to the in-memory rotation (FinisherRotationCheck): memory dropped, the file is read again on the next pick.</summary>
    internal static void ResetRotationForTest() { rot.Clear(); FinisherRotationStore.DropCache(); }

    // ---------------- selection ----------------
    static int Hash(string s) { unchecked { int h = 17; if (s != null) foreach (char c in s) h = h * 31 + c; return h; } }

    static string DirectionOf(DuelFighter attacker, DuelFighter victim)
    {
        if (attacker == null || victim == null) return "front";
        Vector3 to = attacker.transform.position - victim.transform.position; to.y = 0f; if (to.sqrMagnitude < 1e-4f) return "front";
        float dot = Vector3.Dot(victim.transform.forward, to.normalized);
        return dot > 0.5f ? "front" : dot < -0.5f ? "back" : "side";
    }

    static bool In(string[] list, string v) { if (list == null || list.Length == 0) return true; foreach (var s in list) if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase) || s == "*") return true; return false; }

    // V61B: rotation.enabledIds (data) restricts the rotation to those entry ids; empty = every enabled entry (the old behaviour)
    static bool IdAllowed(FinisherRotation rot, string id)
    {
        if (rot == null || rot.enabledIds == null || rot.enabledIds.Length == 0) return true;
        foreach (var s in rot.enabledIds) if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    /// <summary>An entry by id (debug / -finisherauto), filters ignored.</summary>
    public static FinisherEntry ById(string id)
    {
        var cfg = FinisherConfig.Get(); if (cfg.entries == null || string.IsNullOrEmpty(id)) return null;
        foreach (var e in cfg.entries) if (e != null && e.id == id) return e;
        return null;
    }

    // entries that may be picked for this blow: enabled, in rotation.enabledIds, side / weapon / direction / move / location filters
    static List<int> EligibleIdx(FinisherFile file, FinisherContext ctx)
    {
        var cfg = file.rotation; var entries = file.entries; var ok = new List<int>(); if (entries == null) return ok;
        string weapon = FinisherSides.WeaponOf(ctx.killerSide), dir = DirectionOf(ctx.killer, ctx.victim);
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i]; if (e == null || !e.enabled || !IdAllowed(cfg, e.id)) continue;
            if (!FinisherSides.Matches(e.attackerSide, ctx.killerSide)) continue;
            if (!In(e.weapons, weapon) || !In(e.directions, dir) || !In(e.moves, ctx.move.ToString()) || !In(e.locations, ctx.loc.ToString())) continue;
            if (!FinisherPair.Available(e, FinisherSides.IdOf(ctx.killerSide), FinisherSides.IdOf(ctx.victimSide), ctx.deathblow)) continue;   // JOANFIN v77: per killer / victim availability (data)
            ok.Add(i);
        }
        return ok;
    }
    internal static List<string> EligibleIds(FinisherContext ctx)
    {
        var file = FinisherConfig.Get(); var r = new List<string>(); foreach (int i in EligibleIdx(file, ctx)) r.Add(file.entries[i].id); return r;
    }

    /// <summary>The entry for this killing blow (rotation + filters), or null = plain death.
    /// V62: the rotation position is per weapon (rotation.perWeapon), found again through the last id (robust against an edited entry list), never the same id twice in a row while more than one is eligible, persisted.</summary>
    public static FinisherEntry Pick(FinisherContext ctx)
    {
        var file = FinisherConfig.Get(); var cfg = file.rotation; var entries = file.entries;
        if (entries == null || entries.Length == 0) return null;
        string weapon = FinisherSides.WeaponOf(ctx.killerSide);
        var ok = EligibleIdx(file, ctx);
        if (ok.Count == 0)
        {
            if (!string.IsNullOrEmpty(cfg.fallbackEntry)) foreach (var e in entries) if (e != null && e.id == cfg.fallbackEntry) return e;
            return null;
        }
        string key = cfg.perWeapon ? weapon : "*"; var st = State(key);
        int lastIdx = st.lastIndex;
        if (!string.IsNullOrEmpty(st.lastId)) for (int i = 0; i < entries.Length; i++) if (entries[i] != null && entries[i].id == st.lastId) { lastIdx = i; break; }
        int pickIdx;
        if (cfg.mode == "random")
        {
            var rng = new System.Random(cfg.seed ^ Hash(weapon) ^ (Hash(DirectionOf(ctx.killer, ctx.victim)) * 7) ^ (st.kills * 7919));
            var pool = new List<int>(ok);
            if (cfg.noImmediateRepeat && pool.Count > 1) pool.RemoveAll(i => entries[i].id == st.lastId);
            float sum = 0f; foreach (int i in pool) sum += Mathf.Max(0f, entries[i].weight);
            double r = rng.NextDouble() * Math.Max(sum, 1e-6f); pickIdx = pool[pool.Count - 1];
            foreach (int i in pool) { r -= Mathf.Max(0f, entries[i].weight); if (r < 0) { pickIdx = i; break; } }
        }
        else
        {
            pickIdx = ok[0]; foreach (int i in ok) if (i > lastIdx) { pickIdx = i; break; }   // next eligible entry after the last one, wrapping
            if (cfg.noImmediateRepeat && ok.Count > 1 && entries[pickIdx].id == st.lastId) pickIdx = ok[(ok.IndexOf(pickIdx) + 1) % ok.Count];
        }
        st.lastIndex = pickIdx; st.lastId = entries[pickIdx].id; st.kills++;
        FinisherRotationStore.Save(key, st.lastIndex, st.kills, st.lastId);
        return entries[pickIdx];
    }

    // ---------------- helpers ----------------
    static void PressById(DuelFighter f, string id)
    {
        switch (id)
        {
            case "heavy_overhead": f.Press("heavy"); break;
            case "thrust": f.Press("thrust"); break;
            case "kick": f.Press("kick"); break;
            case "special1": f.special1Cd = 0; f.Press("special1"); break;
            case "special2": f.special2Cd = 0; f.Press("special2"); break;
            default: f.Press(id); break;    // light_left / light_right
        }
    }
    static string MoveKey(string id) { return id == "special1" ? "skill_cleave" : id == "special2" ? "skill_reaping" : id; }

    static float ClipLength(Fighter f, string clip)
    {
        if (f == null || f.anim == null || f.anim.runtimeAnimatorController == null) return 0f;
        foreach (var c in f.anim.runtimeAnimatorController.animationClips) if (c.name == clip) return c.length;
        return 0f;
    }
    public static bool HasState(Fighter f, string clip) { return f != null && f.anim != null && !string.IsNullOrEmpty(clip) && f.anim.HasState(0, Animator.StringToHash(clip)); }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    /// <summary>Moves a fighter without the CharacterController fighting the teleport.</summary>
    public static void Teleport(DuelFighter f, Vector3 pos, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (cc) cc.enabled = false;
        f.transform.position = pos;
        facing.y = 0f; if (facing.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
        if (cc) cc.enabled = true;
    }

    // ---------------- playback ----------------
    public static IEnumerator Run(RTFinisherHost host, FinisherEntry e, FinisherContext ctx)
    {
        if (!host.Begin()) yield break;
        e = FinisherPair.Resolve(e, FinisherSides.IdOf(ctx.killerSide), FinisherSides.IdOf(ctx.victimSide));   // JOANFIN v77: per killer / victim pair numbers, shots (data)
        bool rig = host.HasCamera;
        DuelFighter A = host.Fighter(ctx.killerSide), V = host.Fighter(ctx.victimSide);
        Fighter fa = A.GetComponent<Fighter>(), fv = V.GetComponent<Fighter>();
        e = ApplyV1Clips(e, fa, fv);   // V1FIN v94: V1's authored clips when they are imported
        var v1c = FinisherConfig.Get().rt.v1; bool v1on = v1c != null && v1c.enabled;
        FinisherLoc loc = ctx.loc;
        if (e.placeholder) Debug.Log("[finisher] " + e.id + " uses placeholder clips");
        if (OnFinisherStart != null) OnFinisherStart(e.id);
        host.Notify("Finisher: " + e.id);
        host.Prep();
        FinPhys.BeginScene(e.id, A, V);   // FINPHYS v97: hit reaction off, the entry's physics row

        // placement: pair on the lane axis, facing each other (hidden by the hard first cut). distance / pairOffset are stand-in metres scaled by the killer;
        // killerYawDeg turns the killer so its clip's own facing points at the victim, victimYawDeg likewise for the victim.
        Vector3 mid = (A.transform.position + V.transform.position) * 0.5f, axis = Flat(V.transform.position - A.transform.position);
        if (axis.sqrMagnitude < 1e-4f) axis = A.transform.forward; axis.Normalize();
        float ks = e.distanceScaleByKiller ? Mathf.Max(0.5f, A.scale) * FinisherPair.KillerScale(FinisherSides.IdOf(ctx.killerSide)) : 1f;   // JOANFIN v77: x rt.killers pairScale of the killer
        Vector3 rightV = Vector3.Cross(Vector3.up, axis);
        float dz = Mathf.Max(0.6f, e.distance * ks), dx = 0f;
        if (e.pairOffset != null && e.pairOffset.Length >= 3) { dx = e.pairOffset[0] * ks; dz = Mathf.Max(0.6f, e.pairOffset[2] * ks); }
        Vector3 pa = new Vector3(mid.x, A.transform.position.y, mid.z) - axis * (dz * 0.5f) - rightV * (dx * 0.5f);
        Vector3 pv = pa + axis * dz + rightV * dx; pv.y = V.transform.position.y + (e.pairOffset != null && e.pairOffset.Length >= 3 ? e.pairOffset[1] * ks : 0f);   // V1CLIPS v98: pairOffset[1] = lift
        Teleport(A, pa, Quaternion.Euler(0f, e.killerYawDeg, 0f) * axis);
        Teleport(V, pv, Quaternion.Euler(0f, e.victimYawDeg, 0f) * -axis);
        A.opponent = null; V.opponent = null; A.guardHeld = false; V.guardHeld = false; A.moveDir = V.moveDir = Vector3.zero;
        // V62B: ground contact of both bodies during the scene (lift a sunk body, lower a floating one, stats for -finisherauto); removes itself when the scene is over
        FinisherGround.ResetStats(); var gA = FinisherGround.Attach(A, true); var gV = FinisherGround.Attach(V, false); var gcfg = FinisherConfig.Get().rt.ground; float dieT = -1f;
        // V64F: pair interpenetration probe (hips + body capsules over the whole scene) and the victim material luma probe / stuck-flash guard
        FinisherPairProbe.Begin(A, V); FinisherVictimLook.Reset(); FinisherVictimLook.Before = FinisherVictimLook.Sample(V); FinisherVictimLook.Scrub(V, "start"); bool vlDone = false;

        bool clipPath = !string.IsNullOrEmpty(e.attackerClip) && HasState(fa, e.attackerClip);
        string moveKey = MoveKey(e.attackerMove);
        var md = A.moves != null ? A.moves.Get(moveKey) : null;
        var ac = AttackClipTable.Get(); float frac = ac != null && ac.enabled && ac.defaults != null ? ac.defaults.contactActiveFrac : 0.5f;
        int off = Mathf.Max(1, Mathf.RoundToInt(frac * (md != null ? md.Active(false) : 4)));
        float planned = e.contactAt >= 0f ? e.contactAt : clipPath ? 0.6f : (1 + (md != null ? md.Windup(false) : 14) + off) / 60f;
        bool vClip = !string.IsNullOrEmpty(e.victimClip) && HasState(fv, e.victimClip);
        float vLen = vClip ? Mathf.Max(0.05f, ClipLength(fv, e.victimClip) - Mathf.Max(0f, e.victimClipStart)) / Mathf.Max(0.05f, e.victimClipSpeed) : 0f;   // FINFIX v93: minus victimClipStart

        // V1CLIPS v98: V1 aimBladeAt - the blade goes through the chest while the V1 killer clip plays
        if (v1on && v1c.bladeAim && e.bladeAim != null && e.bladeAim.enabled && clipPath && e.clipsV1 != null && e.attackerClip == e.clipsV1.attackerClip) BladeAim.Begin(A, V, e.bladeAim, planned);

        // the DuelFighter state machine must not touch a body that plays a paired clip
        if (clipPath) A.enabled = false;
        if (vClip) V.enabled = false;

        // timelines
        var shots = new List<FinisherShotKey>(v1on && v1c.camera && e.shotsV1 != null && e.shotsV1.Length > 0 ? e.shotsV1 : (e.shots ?? new FinisherShotKey[0]));   // V1FIN v94: V1's camera shots.Sort((p, q) => p.t.CompareTo(q.t));
        var slows = new List<FinisherSlowKey>(e.slowmo ?? new FinisherSlowKey[0]);
        var gores = new List<FinisherGoreKey>(v1on && v1c.timing && e.goreV1 != null && e.goreV1.Length > 0 ? e.goreV1 : (e.gore ?? new FinisherGoreKey[0]));   // V1FIN v94: V1's gore timing
        bool[] shotDone = new bool[shots.Count], slowDone = new bool[slows.Count], goreDone = new bool[gores.Count];
        bool contactSeen = false, vStarted = false, dead = false, anyGore = false;   // CUTLOD v96: anyGore = a gore key (the cut commit) has fired
        float T = 0f, contactT = planned, vStart = 0f, wall0 = Time.unscaledTime;

        if (clipPath) { fa.holdLast = false; fa.ForceExact(e.attackerClip, e.attackerFade, e.attackerClipStart); fa.anim.speed = e.attackerClipSpeed * fa.animWeight; }
        else { PressById(A, e.attackerMove); }

        // first shot is a hard cut (the pair was just placed)
        if (rig)
        {
            bool any = false;
            for (int i = 0; i < shots.Count; i++) if (shots[i].t <= 0f && shots[i].rel != "contact") { host.CameraCut(shots[i].shot, A.transform, V.transform, 0f, e.shotDefs); shotDone[i] = true; any = true; break; }
            if (!any) host.CameraCut(FinisherConfig.Get().rt.fallbackShot, A.transform, V.transform, 0f, e.shotDefs);
        }

        // FINFIX v93: the mesh cut of the first cut-type gore key is BUILT in slices from the scene start (under the wind-up) and only APPLIED at its key (a few ms), so the contact frame does not stall.
        var rtc = FinisherConfig.Get().rt; var pre = rtc.precut ?? new PrecutCfg();
        MeshCut.CutJob[] cutJobs = new MeshCut.CutJob[gores.Count]; int jobIdx = -1; float jobT0 = -1f, jobReadyT = -1f;
        if (pre.enabled && FinisherConfig.EnableGore)
            for (int i = 0; i < gores.Count && jobIdx < 0; i++)
            {
                string pu = GoreUnit(gores[i]);
                if (pu.Length > 0 && MeshCut.IsMeshUnit(pu) && MeshCut.CanCut(V.transform, pu)) { cutJobs[i] = MeshCut.Prepare(V.transform, pu, FinisherConfig.Get().rotation.seed + Mathf.Abs(Hash(pu)) % 1000); jobIdx = i; }
            }
        // FINFIX v93: animation-only contact hold (both Animators stopped for contactHoldMs; the scene clock, camera, blood and the sim are NOT slowed or stopped)
        bool held = false; float holdLeft = 0f, savedA = 1f, savedV = 1f, ratioWas = 1f;
        float total = Mathf.Max(e.duration, planned + 0.5f);
        while (T < total)
        {
            float dt = FinisherTime.Step(Time.deltaTime);   // FINFIX v93: clamped step - one long frame cannot skip a beat
            T += dt;
            host.TimeTick();
            if (jobIdx >= 0 && !cutJobs[jobIdx].done && T >= pre.startAt)
            {
                float toContact = (contactSeen ? 0f : planned - T);
                if (jobT0 < 0f) jobT0 = T;
                cutJobs[jobIdx].Step(toContact < pre.catchUpBeforeContactS ? pre.catchUpBudgetMs : pre.budgetMs, pre.checkEvery);
                if (cutJobs[jobIdx].done) { jobReadyT = T; Debug.Log("[finisher] cut '" + cutJobs[jobIdx].unitId + "' prepared at T=" + T.ToString("0.00") + " s (started " + jobT0.ToString("0.00") + "), " + cutJobs[jobIdx].slices + " slices, cpu " + cutJobs[jobIdx].cpuMs.ToString("0") + " ms, max slice " + cutJobs[jobIdx].maxSliceMs.ToString("0") + " ms" + (cutJobs[jobIdx].failed ? " FAILED: " + cutJobs[jobIdx].note : "")); }
            }
            // contact
            if (!contactSeen)
            {
                bool hit = clipPath ? T >= planned : (A.state == FState.Active && A.CurrentMove != null && A.CurrentMove.id == moveKey && A.t >= off);
                if (hit)
                {
                    contactSeen = true; contactT = T;
                    host.FireEvent(A, V, "Hit", true, loc, A.CurrentMove);
                    FinPhys.OnContact(A, V, axis);   // FINPHYS v97: the blow's momentum / zone for the death ragdoll (before any Die())
                    if (ProbeEnabled) { Probe(e, A, V, axis, rightV, ks); Clearance(e, A, V, ks); }
                    { float cs = e.contactShake >= 0f ? e.contactShake : rtc.contactShake; if (rig && cs > 0f) host.CameraShake(cs, rtc.contactShakeS); }   // FINFIX v93: json (was a hard-coded 0.03)
                    if (rtc.hpToZeroAtContact && !V.Dead) { V.health = 0f; var hpz = V.GetComponent<Health>(); if (hpz != null) hpz.current = 0f; Debug.Log("[finisher] " + e.id + " contact at T=" + T.ToString("0.00") + " s: victim HP -> 0"); }   // FINFIX v93: the bar drains at the contact, not at Die()
                    { float hm = e.contactHoldMs >= 0f ? e.contactHoldMs : rtc.contactHoldMs; if (hm > 0f) { held = true; holdLeft = hm / 1000f; if (fa != null && fa.anim != null) { savedA = fa.anim.speed; fa.anim.speed = 0f; } if (fv != null && fv.anim != null) { savedV = fv.anim.speed; fv.anim.speed = 0f; } } }   // FINFIX v93
                    if (!vClip && e.dieAt <= -1.5f && !dead) { V.Die(); dead = true; }
                }
            }
            // victim clip
            if (vClip && !vStarted)
            {
                float vs = e.victimStart >= 0f ? e.victimStart : (contactSeen ? contactT : planned);
                if (T >= vs && (e.victimStart >= 0f || contactSeen)) { vStarted = true; fv.holdLast = false; fv.ForceExact(e.victimClip, e.victimFade, Mathf.Max(0f, e.victimClipStart)); fv.anim.speed = held ? 0f : e.victimClipSpeed * fv.animWeight; if (held) savedV = e.victimClipSpeed * fv.animWeight; vStart = T; }
            }
            if (vClip && vStarted && !dead && e.dieAt != -1f)
            {
                float at = FinPhys.DieAt(contactT, e.dieAt >= 0f ? e.dieAt : vStart + vLen);   // FINPHYS v97: the ragdoll beat (row.ragdollAtS after the contact)
                if (T >= at)
                {
                    dead = true; V.enabled = true; V.Die();
                    if (e.holdLast && fv.anim != null && fv.anim.enabled) { fv.holdLast = true; fv.ForceExact(e.victimClip, 0f, Mathf.Max(0f, ClipLength(fv, e.victimClip) - 0.02f)); }
                }
            }
            if (dead && dieT < 0f) dieT = T;
            FinisherPairProbe.Sample(dt);
            if (held) { holdLeft -= dt; if (holdLeft <= 0f) { held = false; if (fa != null && fa.anim != null) fa.anim.speed = savedA; if (fv != null && fv.anim != null) fv.anim.speed = savedV; } }   // FINFIX v93: end of the contact hold
            {   // FINFIX v93: the animators follow the clamped scene clock - a frame longer than rt.maxStepS advances them by the same clamped step (the contact stays on the slash, a 7 s hitch moves nothing)
                float raw = Mathf.Max(1e-4f, Time.deltaTime), ratio = Mathf.Clamp01(dt / raw);
                if (ratio < 0.999f || ratioWas < 0.999f)
                {
                    if (clipPath && fa != null && fa.anim != null) fa.anim.speed = (held ? 0f : e.attackerClipSpeed * fa.animWeight) * ratio;
                    if (vClip && vStarted && !dead && fv != null && fv.anim != null) fv.anim.speed = (held ? 0f : e.victimClipSpeed * fv.animWeight) * ratio;
                    ratioWas = ratio;
                }
            }
            if (dead && !vlDone && T >= dieT + 0.3f) { vlDone = true; FinisherVictimLook.Scrub(V, "after death"); }
            if (gA != null) gA.Judge = contactSeen && T >= contactT + gcfg.killerJudgeAfterContactS;
            if (gV != null) { gV.CanLower = dead; gV.Judge = dead && dieT >= 0f && T >= dieT + gcfg.victimJudgeAfterDieS; }
            // shots, slow-mo, gore
            for (int i = 0; i < shots.Count; i++)
            {
                if (shotDone[i]) continue; var k = shots[i];
                float at = k.rel == "contact" ? (contactSeen ? contactT : planned) + k.t : k.t;
                if (T >= at) { shotDone[i] = true; if (rig) host.CameraCut(k.shot, A.transform, V.transform, k.blend, e.shotDefs); }
            }
            for (int i = 0; i < slows.Count; i++)
            {
                if (slowDone[i]) continue; var k = slows[i];
                float at = k.rel == "contact" ? (contactSeen ? contactT : planned) + k.t : k.t;
                if (T >= at) { slowDone[i] = true; if (k.curve != null && k.curve.Length > 0) host.SlowCurve(k.curve, k.easeOut); else host.SlowMo(k.scale, k.duration, k.easeIn, k.easeOut); }   // V62: curve = per-finisher time-scale curve (data)
            }
            for (int i = 0; i < gores.Count; i++)
            {
                if (goreDone[i]) continue; var k = gores[i];
                float at = k.rel == "contact" ? (contactSeen ? contactT : planned) + k.t : k.t;
                if (T >= at && (k.rel != "contact" || contactSeen || at <= planned + 0.001f)) { goreDone[i] = true; Gore(k, V, A, loc, cutJobs[i], T, jobReadyT); anyGore = true; }
            }
            {   // CUTLOD v96: finisher watchdog. No gore key committed after cut_lod.json watchdogS real seconds -> drop the pending cuts and fall back to a plain death, so a failed cut can never hang the game
                var lcw = CutLod.C;
                if (lcw.enabled && lcw.watchdogS > 0f && gores.Count > 0 && !anyGore && Time.unscaledTime - wall0 > lcw.watchdogS)
                {
                    Debug.LogWarning("[finisher] WATCHDOG " + e.id + ": no gore commit after " + (Time.unscaledTime - wall0).ToString("0.0") + " s real (limit " + lcw.watchdogS.ToString("0.0") + " s, T=" + T.ToString("0.00") + "): cuts dropped, plain death");
                    for (int wi = 0; wi < cutJobs.Length; wi++) if (cutJobs[wi] != null && !cutJobs[wi].committed) cutJobs[wi].Discard();
                    break;   // the code after the loop kills the victim (V.Die) and gives control back
                }
            }
            if (Time.unscaledTime - wall0 > total * 8f + 6f) { Debug.LogWarning("[finisher] timeout " + e.id); break; }
            yield return null;
        }
        if (held) { held = false; if (fa != null && fa.anim != null) fa.anim.speed = savedA; if (fv != null && fv.anim != null) fv.anim.speed = savedV; }
        for (int i = 0; i < cutJobs.Length; i++) if (cutJobs[i] != null && !cutJobs[i].committed) cutJobs[i].Discard();   // FINFIX v93: a prepared cut that never fired is thrown away
        if (!dead) { V.enabled = true; V.Die(); }
        // hold the last frame (unscaled), then give control back
        host.TimeRestore();
        float hold = Mathf.Max(0f, e.holdAfter);
        if (gA != null) gA.Judge = true; if (gV != null) { gV.CanLower = true; gV.Judge = true; }   // V62B: the final hold is always judged
        for (float t = 0f; t < hold; ) { float hd = FinisherTime.Step(Time.unscaledDeltaTime); t += hd; FinisherPairProbe.Sample(hd); yield return null; }   // FINFIX v93: clamped step
        FinisherVictimLook.Scrub(V, "final hold"); FinisherVictimLook.After = FinisherVictimLook.Sample(V);   // V64F
        if (clipPath) { A.enabled = true; if (fa != null) { fa.holdLast = false; fa.Force("idle", 0.15f); if (fa.anim) fa.anim.speed = fa.animWeight; } }
        A.state = FState.Loco; A.t = 0;
        BladeAim.EndAll();   // V1CLIPS v98
        FinPhys.EndScene();   // FINPHYS v97
        host.Cleanup();
        if (rig) host.CameraDone();
    }

    // ---------------- pair probe (V62) ----------------
    static Transform FindBone(Transform root, string suffix)
    {
        if (root == null || string.IsNullOrEmpty(suffix)) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>()) if (t.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }
    // At the contact: where is the victim's probe bone relative to the killer's hand, in the pair frame (right / up / along the lane)? The suggestion moves the victim so the bone ends bladeReach ahead of the
    // hand and on its line. Numbers are in stand-in metres (divided by the killer scale), ready to paste into finishers.json pairOffset.
    static void Probe(FinisherEntry e, DuelFighter A, DuelFighter V, Vector3 axis, Vector3 rightV, float ks)
    {
        try
        {
            if (OnPairProbe == null) return;
            var hb = FindBone(A.transform, e.probeKillerBone); var vb = FindBone(V.transform, e.probeVictimBone);
            if (hb == null || vb == null) { OnPairProbe("probe " + e.id + ": bone not found (" + e.probeKillerBone + " " + (hb != null) + ", " + e.probeVictimBone + " " + (vb != null) + ")"); return; }
            Vector3 d = vb.position - hb.position; float mx = Vector3.Dot(d, rightV), mz = Vector3.Dot(d, axis), my = d.y;
            float cx = e.pairOffset != null && e.pairOffset.Length >= 3 ? e.pairOffset[0] : 0f, cz = e.pairOffset != null && e.pairOffset.Length >= 3 ? e.pairOffset[2] : e.distance;
            float nx = cx + (-mx) / ks, nz = cz + (e.bladeReach * ks - mz) / ks;
            string f = "0.00";
            OnPairProbe("probe " + e.id + ": " + e.probeKillerBone + " -> victim " + e.probeVictimBone + " = right " + mx.ToString(f) + " up " + my.ToString(f) + " along " + mz.ToString(f) + " m (want along " + (e.bladeReach * ks).ToString(f)
                + "); pairOffset now [" + cx.ToString("0.000") + ", 0, " + cz.ToString("0.000") + "] suggest [" + nx.ToString("0.000") + ", 0, " + nz.ToString("0.000") + "]");
        }
        catch (Exception ex) { Debug.LogWarning("[finisher] probe: " + ex.Message); }
    }

    // ---------------- clearance probe (V61C) ----------------
    // At the contact: distance from the killer's head and hand to the victim's chest bones (Spine / Spine1 / clearVictimBone) minus the estimated chest radius (clearChestRadiusM x victim scale).
    // Negative = the killer's head or hand is inside the victim's body. Below minClearanceM the line suggests a pairOffset forward value (stand-in metres, killer scale divided out).
    static void Clearance(FinisherEntry e, DuelFighter A, DuelFighter V, float ks)
    {
        try
        {
            ClearHead = ClearHand = float.NaN;
            var cfg = FinisherConfig.Get().rt.auto;
            var head = FindBone(A.transform, cfg.clearKillerBone); var hand = FindBone(A.transform, string.IsNullOrEmpty(e.probeKillerBone) ? "RightHand" : e.probeKillerBone);
            float rad = cfg.clearChestRadiusM * Mathf.Max(0.5f, V.scale), bh = float.MaxValue, bw = float.MaxValue; string nameH = "?", nameW = "?";
            foreach (var sfx in new[] { "Spine", "Spine1", cfg.clearVictimBone })
            {
                var vb = FindBone(V.transform, sfx); if (vb == null) continue;
                if (head != null) { float d = Vector3.Distance(head.position, vb.position) - rad; if (d < bh) { bh = d; nameH = sfx; } }
                if (hand != null) { float d = Vector3.Distance(hand.position, vb.position) - rad; if (d < bw) { bw = d; nameW = sfx; } }
            }
            if (bh == float.MaxValue && bw == float.MaxValue) { if (OnPairProbe != null) OnPairProbe("clearance " + e.id + ": bones not found (" + cfg.clearKillerBone + " " + (head != null) + ", victim chest " + cfg.clearVictimBone + ")"); return; }
            if (bh != float.MaxValue) ClearHead = bh; if (bw != float.MaxValue) ClearHand = bw;
            float worst = Mathf.Min(bh, bw), cz = e.pairOffset != null && e.pairOffset.Length >= 3 ? e.pairOffset[2] : e.distance;
            string sug = worst < cfg.minClearanceM ? "; pairOffset forward now " + cz.ToString("0.000") + " suggest " + (cz + (cfg.minClearanceM - worst) / ks).ToString("0.000") : "; ok";
            if (OnPairProbe != null)
                OnPairProbe("clearance " + e.id + ": head -> victim " + nameH + " " + (bh == float.MaxValue ? "-" : bh.ToString("0.00")) + " m, hand -> victim " + nameW + " " + (bw == float.MaxValue ? "-" : bw.ToString("0.00"))
                    + " m (chest radius est " + rad.ToString("0.00") + " m = " + cfg.clearChestRadiusM.ToString("0.00") + " x scale " + V.scale.ToString("0.00") + "; negative = inside the body; want >= " + cfg.minClearanceM.ToString("0.00") + ")" + sug);
        }
        catch (Exception ex) { Debug.LogWarning("[finisher] clearance: " + ex.Message); }
    }
    // ---------------- gore ----------------
    static string GoreUnit(FinisherGoreKey k)
    {
        string u = !string.IsNullOrEmpty(k.cut) ? k.cut : k.type == "head_detach" ? "head" : k.type == "skull_split" ? "head_split" : "";
        var v1 = FinisherConfig.Get().rt.v1; if (v1 != null && v1.enabled && v1.crown && u.Length > 0 && u == v1.crownReplaces && MeshCut.IsMeshUnit(v1.crownUnit)) u = v1.crownUnit;   // V1FIN v94: V1's head-only split instead of the body-mesh split
        return u;
    }
    // V1FIN v94: V1's authored clips (entry.clipsV1) replace the victim / killer clip only when every named Animator state exists on the fighter
    static FinisherEntry ApplyV1Clips(FinisherEntry e, Fighter fa, Fighter fv)
    {
        var v1 = FinisherConfig.Get().rt.v1; var c = e.clipsV1; if (v1 == null || !v1.enabled || !v1.clips || c == null || string.IsNullOrEmpty(c.victimClip)) return e;
        if (!HasState(fv, c.victimClip) || (!string.IsNullOrEmpty(c.attackerClip) && !HasState(fa, c.attackerClip))) { Debug.Log("[finisher] " + e.id + ": V1 clip state '" + c.victimClip + (string.IsNullOrEmpty(c.attackerClip) ? "" : "' / '" + c.attackerClip) + "' not on the fighter (not imported yet): the entry keeps its own clips"); return e; }
        var r = e.Clone(); r.victimClip = c.victimClip; r.victimClipSpeed = c.victimClipSpeed; r.victimClipStart = c.victimClipStart; r.victimStart = c.victimStart; r.dieAt = c.dieAt;
        if (!string.IsNullOrEmpty(c.attackerClip)) r.attackerClip = c.attackerClip; if (c.attackerClipSpeed > 0f) r.attackerClipSpeed = c.attackerClipSpeed; if (c.attackerClipStart >= 0f) r.attackerClipStart = c.attackerClipStart;
        if (c.pairOffset != null && c.pairOffset.Length >= 3) r.pairOffset = c.pairOffset;   // V1CLIPS v98
        if (c.contactAt >= 0f) r.contactAt = c.contactAt; if (c.duration > 0f) r.duration = c.duration; if (c.holdAfter >= 0f) r.holdAfter = c.holdAfter;
        Debug.Log("[finisher] " + e.id + ": V1 clips '" + c.victimClip + "' x" + c.victimClipSpeed.ToString("0.00") + (string.IsNullOrEmpty(c.attackerClip) ? "" : " + killer '" + c.attackerClip + "'"));
        return r;
    }   // FINFIX v93: the unit a key cuts
    static void Gore(FinisherGoreKey k, DuelFighter victim, DuelFighter attacker, FinisherLoc loc, MeshCut.CutJob job = null, float sceneT = 0f, float readyT = -1f)
    {
        Vector3 away = Flat(victim.transform.position - attacker.transform.position); if (away.sqrMagnitude < 1e-4f) away = attacker.transform.forward; away.Normalize();
        string unit = GoreUnit(k);
        var call = new FinisherGoreCall { type = k.type, unit = unit, param = k.param, victim = victim, attacker = attacker, away = away, strength = k.strength, point = victim.transform.position + Vector3.up * 1.4f * FighterScale.Of(victim) };
        if (OnGore != null) OnGore(call);
        bool handled = false, budgetHit = false;   // CUTLOD v96
        if (unit.Length > 0 && FinisherConfig.EnableGore)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();   // FINFIX v93
                CutResult res;
                if (job != null) { bool wasDone = job.done; res = MeshCut.Commit(job, away, k.strength, 60f * k.strength); Debug.Log("[finisher] cut '" + unit + "' COMMIT at T=" + sceneT.ToString("0.00") + " s took " + sw.ElapsedMilliseconds + " ms (prepared " + (wasDone ? "in time at T=" + readyT.ToString("0.00") : "NOT in time: finished synchronously, cpu " + job.cpuMs.ToString("0") + " ms") + ")"); }
                else { res = MeshCut.Cut(victim.transform, unit, FinisherConfig.Get().rotation.seed + Mathf.Abs(Hash(unit)) % 1000, away, k.strength, 60f * k.strength); Debug.Log("[finisher] cut '" + unit + "' sync (no precut) took " + sw.ElapsedMilliseconds + " ms"); }
                budgetHit = res != null && res.budget; if (budgetHit) Debug.LogWarning("[finisher] cut '" + unit + "' budget exceeded: gore-only + plain death (" + res.note + ")");   // CUTLOD v96
                FinisherPiece.Adopt(res, victim.transform, unit); FinPhys.AfterCut(victim, unit, res); handled = res != null && res.ok; /* FINPHYS v97 (INTPHYS: on live v99 the V1CLIPS trailing // comment swallowed this assignment, restored here) */ if (res != null && res.ok) V1Extras.OnCut(victim, attacker, FinisherConfig.Get().cuts != null ? System.Array.Find(FinisherConfig.Get().cuts.units, uu => uu != null && uu.id == unit) : null, away);   // V1CLIPS v98: opened weapon drop / leg topple
                handled = res != null && res.ok;   // V62B: the severed piece stays as a physics prop   // ok=false (unknown unit, unreadable mesh, exception inside) -> the head-hide / blood fallback below runs
                if (res != null && !res.ok) Debug.LogWarning("[finisher] cut '" + unit + "' not done: " + res.note);
            }
            catch (Exception ex) { Debug.LogWarning("[finisher] MeshCut.Cut failed: " + ex.Message); }
        }
        { var v1f = FinisherConfig.Get().rt.v1; if (!handled && !budgetHit && unit.Length > 0 && v1f != null && v1f.enabled && unit == v1f.crownUnit && FinisherConfig.EnableGore)   // V1FIN v94: the V1 crown could not be built -> the old body-mesh split
          { try { var r2 = MeshCut.Cut(victim.transform, v1f.crownReplaces, FinisherConfig.Get().rotation.seed + Mathf.Abs(Hash(v1f.crownReplaces)) % 1000, away, k.strength, 60f * k.strength); FinisherPiece.Adopt(r2, victim.transform, v1f.crownReplaces); handled = r2 != null && r2.ok; Debug.LogWarning("[finisher] V1 crown not built; old split " + (handled ? "ok" : "failed too")); } catch (Exception ex2) { Debug.LogWarning("[finisher] old split failed: " + ex2.Message); } } }
        if (!handled)
        {
            // stand-in when the mesh cut cannot run: head_detach hides the head bone so the beat still reads, plus a blood event at the wound
            if (unit.Length > 0 && FinisherConfig.EnableGore) FinisherCleave.Fallback(victim.transform, away, unit);   // V63G: wound decals + burst from the head bone (units with cleave / burst ids)
            if (k.type == "head_detach") foreach (var t in victim.GetComponentsInChildren<Transform>()) if (t.name.EndsWith("Head")) { t.localScale = Vector3.one * 0.0001f; break; }
            ContactRelay.RaiseEvent(new CombatEvent { type = "Hit", actor = attacker, target = victim, moveId = attacker.CurrentMove != null ? attacker.CurrentMove.id : "", heavy = true, location = loc == FinisherLoc.Head ? "head" : "torso", direction = "front", point = call.point, tick = Duel.TickCount });
        }
        if (k.type == "blood_burst") ContactRelay.RaiseEvent(new CombatEvent { type = "Hit", actor = attacker, target = victim, heavy = true, location = "torso", direction = "front", point = call.point, tick = Duel.TickCount });
    }
}
