using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Alpha step 1, lane C: the finisher rotation. After the killing beat ADDuelController calls FinisherPlayer(beat) = RunAfterBeat; ADPlayback.PlayBeat skips a kill beat that
// has a finisher, so this class plays the whole killing blow: pair placement, attacker (a DuelFighter move OR a paired clip), victim (paired clip or the normal death/ragdoll),
// timed camera shots, slow-mo and gore events. Everything is data: duel_rules.json "finishers" (ADFinishersCfg / ADFinisherEntry in ADCConfig.cs).
// Adding a paired clip pair = adding one entry (attackerClip, victimClip, shots, gore, slowmo); no code.
//
// Gore events call lane B by REFLECTION, so this file compiles with or without it:
//   type "cut" (or head_detach -> unit "head", skull_split -> unit "head_split")  ->  ADMeshCut.Cut(Transform victimRoot, string unitId, int seed, Vector3 awayDir, float push, float pieceImpulse)
//   type "blood_burst" / fallback -> a CombatEvent "Hit" at the wound point (BloodBurst etc. react)
// and always raises the static event ADFinisherPlayer.OnGore (anything can subscribe).
public struct ADGoreCall { public string type, unit, param; public DuelFighter victim, attacker; public Vector3 point, away; public float strength; }

public static class ADFinisherPlayer
{
    public static event Action<ADGoreCall> OnGore;
    public static event Action<string, ADBeat> OnFinisherStart;   // entry id, beat (UI / audio / stats)

    static ADBeat pickedFor; static ADFinisherEntry picked; static int lastIndex = -1, kills; static string lastId;

    // restart keeps the rotation (lastIndex/kills/lastId) so the next kill picks the NEXT entry; only the pick of the finished fight is cleared
    public static void ResetFight() { pickedFor = null; picked = null; }
    static void ResetAll() { ResetFight(); lastIndex = -1; kills = 0; lastId = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { ResetAll(); OnGore = null; OnFinisherStart = null; }

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

    // True when this killing beat will be played by a finisher (the choice is made once per beat and kept for Run).
    public static bool Wants(ADBeat b)
    {
        if (b == null || !b.killed || !ADFlags.Finishers) return false;
        if (pickedFor == b) return picked != null;
        pickedFor = b; picked = Pick(b);
        return picked != null;
    }

    static ADFinisherEntry Pick(ADBeat b)
    {
        var cfg = ADCConfig.Finishers; if (cfg == null || !cfg.enabled || cfg.entries == null || cfg.entries.Length == 0) return null;
        int killer = 1 - (int)b.victim; int victim = (int)b.victim;
        var pb = ADPlayback.Instance; DuelFighter A = pb != null ? pb.fighters[killer] : null, V = pb != null ? pb.fighters[victim] : null;
        string weapon = killer == 0 ? cfg.playerWeapon : cfg.pitbornWeapon, dir = DirectionOf(A, V);
        ADMove mv = killer == 0 ? b.a : b.b; ADLoc loc = victim == 0 ? b.locA : b.locB; string side = ((ADSide)killer).ToString();
        var ok = new List<int>();
        for (int i = 0; i < cfg.entries.Length; i++)
        {
            var e = cfg.entries[i]; if (e == null || !e.enabled) continue;
            if (e.attackerSide != "any" && !string.Equals(e.attackerSide, side, StringComparison.OrdinalIgnoreCase)) continue;
            if (!In(e.weapons, weapon) || !In(e.directions, dir) || !In(e.moves, mv.ToString()) || !In(e.locations, loc.ToString())) continue;
            ok.Add(i);
        }
        if (ok.Count == 0)
        {
            if (!string.IsNullOrEmpty(cfg.fallbackEntry)) foreach (var e in cfg.entries) if (e != null && e.id == cfg.fallbackEntry) return e;
            return null;
        }
        int pickIdx;
        if (cfg.rotation == "random")
        {
            var rng = new ADRng(cfg.seed ^ Hash(weapon) ^ (Hash(dir) * 7) ^ (kills * 7919));
            var pool = new List<int>(ok);
            if (cfg.noImmediateRepeat && pool.Count > 1) pool.RemoveAll(i => cfg.entries[i].id == lastId);
            float sum = 0f; foreach (int i in pool) sum += Mathf.Max(0f, cfg.entries[i].weight);
            double r = rng.Next01() * Math.Max(sum, 1e-6f); pickIdx = pool[pool.Count - 1];
            foreach (int i in pool) { r -= Mathf.Max(0f, cfg.entries[i].weight); if (r < 0) { pickIdx = i; break; } }
        }
        else
        {
            pickIdx = ok[0]; foreach (int i in ok) if (i > lastIndex) { pickIdx = i; break; }   // next eligible entry after the last one, wrapping
        }
        lastIndex = pickIdx; lastId = cfg.entries[pickIdx].id; kills++;
        return cfg.entries[pickIdx];
    }

    // ---------------- playback ----------------
    // ADDuelController.FinisherPlayer
    public static IEnumerator RunAfterBeat(ADPlayback pb, ADBeat b)
    {
        if (!Wants(b)) yield break;                 // the plain beat (with the normal death) was already played by PlayBeat
        var e = picked; pickedFor = null; picked = null;
        yield return Run(pb, e, b);
    }

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
    static bool HasState(Fighter f, string clip) { return f != null && f.anim != null && !string.IsNullOrEmpty(clip) && f.anim.HasState(0, Animator.StringToHash(clip)); }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    public static IEnumerator Run(ADPlayback pb, ADFinisherEntry e, ADBeat b)
    {
        if (!pb.Ready && !pb.Begin()) yield break;
        var cfg = ADCConfig.Finishers; var rig = ADCameraRig.Ensure();
        int ki = 1 - (int)b.victim, vi = (int)b.victim;
        DuelFighter A = pb.fighters[ki], V = pb.fighters[vi];
        Fighter fa = A.GetComponent<Fighter>(), fv = V.GetComponent<Fighter>();
        ADLoc loc = vi == 0 ? b.locA : b.locB;
        if (e.placeholder) Debug.Log("[finisher] " + e.id + " uses placeholder clips");
        if (OnFinisherStart != null) OnFinisherStart(e.id, b);
        pb.PrepPublic();

        // placement: pair on the lane axis, e.distance apart, facing each other (hidden by the hard first cut)
        Vector3 mid = (A.transform.position + V.transform.position) * 0.5f, axis = Flat(V.transform.position - A.transform.position);
        if (axis.sqrMagnitude < 1e-4f) axis = A.transform.forward; axis.Normalize();
        float d = Mathf.Max(0.6f, e.distance);
        ADPlayback.Teleport(A, new Vector3(mid.x, A.transform.position.y, mid.z) - axis * (d * 0.5f), axis);
        ADPlayback.Teleport(V, new Vector3(mid.x, V.transform.position.y, mid.z) + axis * (d * 0.5f), Quaternion.Euler(0f, e.victimYawDeg, 0f) * -axis);
        A.opponent = null; V.opponent = null; A.guardHeld = false; V.guardHeld = false; A.moveDir = V.moveDir = Vector3.zero;

        bool clipPath = !string.IsNullOrEmpty(e.attackerClip) && HasState(fa, e.attackerClip);
        string moveKey = MoveKey(e.attackerMove);
        var md = A.moves != null ? A.moves.Get(moveKey) : null;
        var ac = AttackClipTable.Get(); float frac = ac != null && ac.enabled && ac.defaults != null ? ac.defaults.contactActiveFrac : 0.5f;
        int off = Mathf.Max(1, Mathf.RoundToInt(frac * (md != null ? md.Active(false) : 4)));
        float planned = e.contactAt >= 0f ? e.contactAt : clipPath ? 0.6f : (1 + (md != null ? md.Windup(false) : 14) + off) / 60f;
        bool vClip = !string.IsNullOrEmpty(e.victimClip) && HasState(fv, e.victimClip);
        float vLen = vClip ? ClipLength(fv, e.victimClip) / Mathf.Max(0.05f, e.victimClipSpeed) : 0f;

        // the DuelFighter state machine must not touch a body that plays a paired clip
        if (clipPath) A.enabled = false;
        if (vClip) V.enabled = false;

        // timelines
        var shots = new List<ADShotKey>(e.shots ?? new ADShotKey[0]); shots.Sort((p, q) => p.t.CompareTo(q.t));
        var slows = new List<ADSlowKey>(e.slowmo ?? new ADSlowKey[0]);
        var gores = new List<ADGoreKey>(e.gore ?? new ADGoreKey[0]);
        bool[] shotDone = new bool[shots.Count], slowDone = new bool[slows.Count], goreDone = new bool[gores.Count];
        bool contactSeen = false, vStarted = false, dead = false, started = false;
        float T = 0f, contactT = planned, wall0 = Time.unscaledTime;

        if (clipPath) { fa.holdLast = false; fa.ForceExact(e.attackerClip, e.attackerFade, e.attackerClipStart); fa.anim.speed = e.attackerClipSpeed * fa.animWeight; }
        else { PressById(A, e.attackerMove); }
        started = true;

        // first shot is a hard cut (the pair was just placed)
        if (rig != null)
        {
            bool any = false;
            for (int i = 0; i < shots.Count; i++) if (shots[i].t <= 0f && shots[i].rel != "contact") { rig.Cut(shots[i].shot, A.transform, V.transform, 0f, e.shotDefs); shotDone[i] = true; any = true; break; }
            if (!any) rig.Cut(ADCConfig.Camera.killShot, A.transform, V.transform, 0f, e.shotDefs);
        }

        float total = Mathf.Max(e.duration, planned + 0.5f);
        while (T < total)
        {
            T += Time.deltaTime;
            ADTime.Tick();
            // contact
            if (!contactSeen)
            {
                bool hit = clipPath ? T >= planned : (A.state == FState.Active && A.CurrentMove != null && A.CurrentMove.id == moveKey && A.t >= off);
                if (hit)
                {
                    contactSeen = true; contactT = T;
                    pb.FireEvent(A, V, "Hit", true, loc, A.CurrentMove);
                    if (rig != null) rig.Shake(0.03f);
                    if (!vClip && e.dieAt <= -1.5f && !dead) { V.Die(); dead = true; }
                }
            }
            // victim clip
            if (vClip && !vStarted)
            {
                float vs = e.victimStart >= 0f ? e.victimStart : (contactSeen ? contactT : planned);
                if (T >= vs && (e.victimStart >= 0f || contactSeen)) { vStarted = true; fv.holdLast = false; fv.ForceExact(e.victimClip, e.victimFade, 0f); fv.anim.speed = e.victimClipSpeed * fv.animWeight; vs = T; vStart = T; }
            }
            if (vClip && vStarted && !dead && e.dieAt != -1f)
            {
                float at = e.dieAt >= 0f ? e.dieAt : vStart + vLen;
                if (T >= at)
                {
                    dead = true; V.enabled = true; V.Die();
                    if (e.holdLast && fv.anim != null && fv.anim.enabled) { fv.holdLast = true; fv.ForceExact(e.victimClip, 0f, Mathf.Max(0f, ClipLength(fv, e.victimClip) - 0.02f)); }
                }
            }
            // shots
            for (int i = 0; i < shots.Count; i++)
            {
                if (shotDone[i]) continue; var k = shots[i];
                float at = k.rel == "contact" ? (contactSeen ? contactT : planned) + k.t : k.t;
                if (T >= at) { shotDone[i] = true; if (rig != null) rig.Cut(k.shot, A.transform, V.transform, k.blend, e.shotDefs); }
            }
            for (int i = 0; i < slows.Count; i++)
            {
                if (slowDone[i]) continue; var k = slows[i];
                float at = k.rel == "contact" ? (contactSeen ? contactT : planned) + k.t : k.t;
                if (T >= at) { slowDone[i] = true; ADTime.SlowMo(k.scale, k.duration, k.easeIn, k.easeOut); }
            }
            for (int i = 0; i < gores.Count; i++)
            {
                if (goreDone[i]) continue; var k = gores[i];
                float at = k.rel == "contact" ? (contactSeen ? contactT : planned) + k.t : k.t;
                if (T >= at && (k.rel != "contact" || contactSeen || at <= planned + 0.001f)) { goreDone[i] = true; Gore(k, V, A, loc); }
            }
            if (Time.unscaledTime - wall0 > total * 8f + 6f) { Debug.LogWarning("[finisher] timeout " + e.id); break; }
            yield return null;
        }
        if (!dead) { V.enabled = true; V.Die(); }
        // hold the last frame (unscaled), then give control back
        ADTime.Restore();
        float hold = Mathf.Max(0f, e.holdAfter);
        for (float t = 0f; t < hold; t += Time.unscaledDeltaTime) yield return null;
        if (clipPath) { A.enabled = true; if (fa != null) { fa.holdLast = false; fa.Force("idle", 0.15f); if (fa.anim) fa.anim.speed = fa.animWeight; } }
        A.state = FState.Loco; A.t = 0;
        pb.CleanupPublic();
        if (rig != null) rig.Plan();
    }
    static float vStart;   // single scene at a time

    // ---------------- gore ----------------
    static Type meshCutType; static MethodInfo meshCut; static bool meshCutLooked;
    static MethodInfo FindMeshCut()
    {
        if (meshCutLooked) return meshCut; meshCutLooked = true;
        meshCutType = Type.GetType("ADMeshCut");
        if (meshCutType != null) meshCut = meshCutType.GetMethod("Cut", BindingFlags.Public | BindingFlags.Static);
        return meshCut;
    }

    static void Gore(ADGoreKey k, DuelFighter victim, DuelFighter attacker, ADLoc loc)
    {
        Vector3 away = Flat(victim.transform.position - attacker.transform.position); if (away.sqrMagnitude < 1e-4f) away = attacker.transform.forward; away.Normalize();
        string unit = !string.IsNullOrEmpty(k.cut) ? k.cut : k.type == "head_detach" ? "head" : k.type == "skull_split" ? "head_split" : "";
        var call = new ADGoreCall { type = k.type, unit = unit, param = k.param, victim = victim, attacker = attacker, away = away, strength = k.strength, point = victim.transform.position + Vector3.up * 1.4f * Mathf.Max(1f, victim.scale) };
        if (OnGore != null) OnGore(call);
        bool handled = false;
        if (unit.Length > 0)
        {
            var m = FindMeshCut();
            if (m != null)
            {
                try
                {
                    var ps = m.GetParameters(); var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var pt = ps[i].ParameterType;
                        args[i] = pt == typeof(Transform) ? (object)victim.transform : pt == typeof(string) ? unit : pt == typeof(int) ? (object)(ADCConfig.Finishers.seed + Mathf.Abs(Hash(unit)) % 1000) : pt == typeof(Vector3) ? (object)away : pt == typeof(float) ? (object)(i == 4 ? k.strength : 60f * k.strength) : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                    }
                    var res = m.Invoke(null, args); handled = true;
                    if (res != null)   // ADMeshCut.Cut reports ok=false (flag off, unknown unit, unreadable mesh) without throwing: then the head-hide / blood fallback must run
                    {
                        var okf = res.GetType().GetField("ok"); var okp = res.GetType().GetProperty("ok");
                        if (okf != null && okf.FieldType == typeof(bool)) handled = (bool)okf.GetValue(res);
                        else if (okp != null && okp.PropertyType == typeof(bool)) handled = (bool)okp.GetValue(res);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[finisher] ADMeshCut.Cut failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            }
        }
        if (!handled)
        {
            // stand-in until the mesh cut lands: head_detach hides the head bone so the beat still reads, plus a blood event at the wound
            if (k.type == "head_detach") foreach (var t in victim.GetComponentsInChildren<Transform>()) if (t.name.EndsWith("Head")) { t.localScale = Vector3.one * 0.0001f; break; }
            ContactRelay.RaiseEvent(new CombatEvent { type = "Hit", actor = attacker, target = victim, moveId = attacker.CurrentMove != null ? attacker.CurrentMove.id : "", heavy = true, location = loc == ADLoc.Head ? "head" : "torso", direction = "front", point = call.point, tick = Duel.TickCount });
        }
        if (k.type == "blood_burst") ContactRelay.RaiseEvent(new CombatEvent { type = "Hit", actor = attacker, target = victim, heavy = true, location = "torso", direction = "front", point = call.point, tick = Duel.TickCount });
    }
}

