using System;
using System.Collections;
using UnityEngine;

// Killing-blow finishers in the REAL-TIME / steer duel. Duel.Resolve calls RTFinisher.TryBegin(attacker, victim, move) at the lethal hit, before the plain Die().
//   true  -> a finisher scene was started (Duel.Frozen = true, victim kept alive until the scene's own Die()); Duel.Resolve returns without killing.
//   false -> nothing happened, Duel.Resolve runs the plain death exactly as before (flag off, no eligible entry, any error).
// The scene itself is FinisherPlayer.Run (data-driven) through RTFinisherHost: CameraRig yields, shots via FinisherCamera, slow-mo via FinisherTime (Time.timeScale, unscaled waits).
// Data: finishers.json (flag enableFinishers, default FALSE, and the "rt" block = FinisherRtCfg). A build ships with finishers off and tests them with the flag or -finisherauto.
public static class RTFinisher
{
    public static bool Busy { get; internal set; }
    public static bool ForceEnable;          // FinisherAuto: on regardless of the data flag
    public static string ForceId = "";       // FinisherAuto: play exactly this entry (filters ignored)
    public static string LastRefusal = "";
    public static DuelFighter Killer, Victim;
    public static FinisherEntry Current;

    public static event Action<FinisherEntry, DuelFighter, DuelFighter> OnStart;   // entry, killer, victim (scene queued, Frozen already true)
    public static event Action<string, string> OnStage;                                // "contact" | "shot" | "slowmo" | "notify", detail (FinisherAuto reads these)
    public static event Action<bool, string> OnEnd;                                    // ok, message (all state is restored when this fires)

    public static FinisherRtCfg Cfg { get { return FinisherConfig.Get().rt; } }
    public static void ReloadConfig() { FinisherConfig.Reload(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Busy = false; ForceEnable = false; ForceId = ""; LastRefusal = ""; Killer = Victim = null; Current = null; OnStart = null; OnStage = null; OnEnd = null; }

    public static bool Enabled { get { return ForceEnable || (FinisherConfig.EnableFinishers && !(Cfg.offInBalanceAuto && BalanceRun) && (!Cfg.onlyWhenSteer || STSteer.Active)); } }
    static bool balanceChecked, balanceRun;
    static bool BalanceRun { get { if (!balanceChecked) { balanceChecked = true; { var a = Environment.GetCommandLineArgs(); balanceRun = Array.IndexOf(a, "-balanceauto") >= 0 || Array.IndexOf(a, "-verifyGround") >= 0 || Array.IndexOf(a, "-postureproof") >= 0; } } return balanceRun; } }   // harness runs (balanceauto, verifyGround, postureproof) keep the plain death: the finisher teleports would break verifyGround's fight-Y band
    public static bool DeathblowFinishes { get { return Enabled && Cfg.deathblowFinishes; } }   // V61B: Duel.Resolve asks before the lethal check

    internal static void Stage(string what, string detail) { if (OnStage != null) { try { OnStage(what, detail); } catch (Exception e) { Debug.LogWarning("[finisher] stage handler: " + e.Message); } } }

    public static int SideOf(DuelFighter f) { string en = Roster.EnemyObject; return f.name.Contains(en) || f.transform.root.name.Contains(en) ? 1 : 0; }   // 1 = roster enemy, 0 = roster player (FinisherSides)
    static FinisherMove MoveOf(MoveDef m)
    {
        string id = m != null ? m.id : "";
        if (id == "light_left" || id == "light_right") return FinisherMove.Light;
        if (id.StartsWith("heavy")) return FinisherMove.Heavy;
        if (id == "thrust" || id == "riposte") return FinisherMove.Stab;
        if (id == "kick") return FinisherMove.Kick;
        if (id.StartsWith("skill_") || id.StartsWith("special")) return FinisherMove.Special;
        return FinisherMove.Light;
    }
    static FinisherLoc LocOf(MoveDef m)
    {
        string id = m != null ? m.id : "";
        if (id.Contains("overhead") || id.StartsWith("heavy")) return FinisherLoc.Head;
        if (id == "kick") return FinisherLoc.Leg;
        if (id == "light_left" || id == "light_right") return FinisherLoc.Arm;
        return FinisherLoc.Torso;
    }

    // Called by Duel.Resolve at the lethal blow. Never throws.
    public static bool TryBegin(DuelFighter a, DuelFighter d, MoveDef m) { return TryBegin(a, d, m, false); }
    // deathblow = a posture-break deathblow (V61B): the scene starts even when the hit did not take the HP to zero; the scene's own Die() is the kill
    public static bool TryBegin(DuelFighter a, DuelFighter d, MoveDef m, bool deathblow)
    {
        try
        {
            if (!Enabled || Busy || a == null || d == null) return false;
            int ks = SideOf(a), vs = SideOf(d);
            if (ks == vs) { LastRefusal = "same side"; return false; }
            if (vs == 0 && !Cfg.allowPlayerVictim) { LastRefusal = "player victim disabled"; return false; }
            var fc = FinisherConfig.Get();
            if (fc.entries == null || fc.entries.Length == 0) { LastRefusal = "no entries in finishers.json"; return false; }
            var mv = MoveOf(m); var loc = LocOf(m);
            var ctx = new FinisherContext { killer = a, victim = d, killerSide = ks, victimSide = vs, move = mv, loc = loc, deathblow = deathblow };
            string force = !string.IsNullOrEmpty(ForceId) ? ForceId : Cfg.forceId;
            FinisherEntry e = !string.IsNullOrEmpty(force) ? FinisherPlayer.ById(force) : FinisherPlayer.Pick(ctx);
            if (e == null) { LastRefusal = !string.IsNullOrEmpty(force) ? "forced id '" + force + "' not found" : "no eligible entry for " + FinisherSides.IdOf(ks) + " " + mv + "/" + loc; return false; }
            var runner = FinisherRTRunner.Ensure(); if (runner == null) { LastRefusal = "no runner"; return false; }

            // commit: from here the scene owns both fighters
            Busy = true; Duel.Frozen = true; Killer = a; Victim = d; Current = e; LastRefusal = "";
            d.health = Mathf.Max(d.health, 1f); var hp = d.GetComponent<Health>(); if (hp != null) hp.current = d.health;   // the HUD must not call the kill before the scene does
            runner.Launch(e, ctx);
            if (OnStart != null) { try { OnStart(e, a, d); } catch (Exception ex) { Debug.LogWarning("[finisher] OnStart handler: " + ex.Message); } }
            return true;
        }
        catch (Exception ex)
        {
            LastRefusal = "exception " + ex.Message; Debug.LogWarning("[finisher] TryBegin failed, plain death: " + ex);
            Busy = false; Duel.Frozen = false;
            return false;
        }
    }

    internal static void End(bool ok, string msg)
    {
        Busy = false; Duel.Frozen = false;
        if (OnEnd != null) { try { OnEnd(ok, msg); } catch (Exception e) { Debug.LogWarning("[finisher] OnEnd handler: " + e.Message); } }
    }
}

// Scene-bound driver (a reload / Retry destroys it and OnDestroy releases everything).
public sealed class FinisherRTRunner : MonoBehaviour
{
    static FinisherRTRunner inst;
    RTFinisherHost host; DuelFighter killer, victim;

    public static FinisherRTRunner Ensure()
    {
        if (inst != null) return inst;
        var go = new GameObject("FinisherRT"); inst = go.AddComponent<FinisherRTRunner>(); return inst;
    }

    public void Launch(FinisherEntry e, FinisherContext ctx)
    {
        killer = ctx.killer; victim = ctx.victim; host = new RTFinisherHost(ctx.killer, ctx.victim, ctx.killerSide, ctx.victimSide);
        StartCoroutine(Drive(e, ctx));
    }

    IEnumerator Drive(FinisherEntry e, FinisherContext ctx)
    {
        yield return null;                      // never start inside the attacker's own Tick (Duel.Resolve is running)
        bool ok = true; string msg = "";
        IEnumerator it = null;
        try { it = FinisherPlayer.Run(host, e, ctx); } catch (Exception ex) { ok = false; msg = ex.GetType().Name + ": " + ex.Message; Debug.LogError("[finisher] " + ex); }
        float t0 = Time.unscaledTime, cap = Mathf.Max(5f, RTFinisher.Cfg.maxSeconds);
        while (ok && it != null)
        {
            bool more = false;
            try { more = it.MoveNext(); } catch (Exception ex) { ok = false; msg = ex.GetType().Name + ": " + ex.Message; Debug.LogError("[finisher] " + e.id + ": " + ex); }
            if (!ok || !more) break;
            if (Time.unscaledTime - t0 > cap) { ok = false; msg = "watchdog " + cap.ToString("0.#") + " s"; Debug.LogWarning("[finisher] " + e.id + " " + msg); break; }
            yield return it.Current;
        }
        Release(ok, msg);
    }

    // restores everything and makes sure the victim is dead (the plain death is the fallback of a broken scene)
    void Release(bool ok, string msg)
    {
        try
        {
            if (host != null) host.Abort();
            if (killer != null) { if (!killer.enabled) killer.enabled = true; killer.moveDir = Vector3.zero; killer.guardHeld = false; killer.opponent = null; }
            if (victim != null)
            {
                if (!victim.enabled) victim.enabled = true;
                if (!victim.Dead) { victim.Die(); msg = (msg + " victim not dead at the end, plain Die()").Trim(); }
                victim.opponent = null;
            }
        }
        catch (Exception ex) { ok = false; msg = (msg + " release: " + ex.Message).Trim(); Debug.LogWarning("[finisher] release: " + ex); }
        host = null;
        RTFinisher.End(ok, msg);
    }

    void OnDestroy()
    {
        if (inst == this) inst = null;
        if (RTFinisher.Busy) { FinisherTime.Restore(); RTFinisher.Busy = false; Duel.Frozen = false; var cr = FindFirstObjectByType<CameraRig>(); if (cr != null) cr.Yield = false; }
    }
}

// Host two: the real-time duel. The lock-on CameraRig yields (CameraRig.Yield) while FinisherCamera plays the data shots on the same camera; slow-mo is FinisherTime (Time.timeScale, unscaled waits).
public sealed class RTFinisherHost
{
    readonly DuelFighter[] f = new DuelFighter[2];
    CameraRig crig; FinisherCamera arig; bool startedRig, aborted;

    public RTFinisherHost(DuelFighter killer, DuelFighter victim, int killerSide, int victimSide) { f[killerSide] = killer; f[victimSide] = victim; }

    public bool Begin()
    {
        if (f[0] == null || f[1] == null) return false;
        crig = UnityEngine.Object.FindFirstObjectByType<CameraRig>();
        if (crig != null) crig.Yield = true;
        arig = FinisherCamera.Ensure();
        if (arig != null && !arig.Active) { arig.Begin(f[0].transform, f[1].transform); startedRig = true; }
        return true;
    }
    public DuelFighter Fighter(int side) { return f[side]; }

    public void Prep()
    {
        for (int i = 0; i < 2; i++)
        {
            var x = f[i]; if (x == null) continue;
            x.opponent = null; x.moveDir = Vector3.zero; x.run = false; x.guardHeld = false; x.heavyHeld = false; x.stamina = x.maxStamina; x.posture = 0f;
            if (!x.Dead) { x.state = FState.Loco; x.t = 0; x.SetKnock(Vector3.zero); }     // the killer may be mid-swing: the scene replays the blow from a clean state
            var fi = x.GetComponent<Fighter>(); if (fi != null) fi.holdLast = false;
        }
    }
    public void Cleanup() { for (int i = 0; i < 2; i++) { var x = f[i]; if (x == null) continue; x.moveDir = Vector3.zero; x.guardHeld = false; x.posture = 0f; x.opponent = null; } }

    static string LocName(FinisherLoc l) { return l == FinisherLoc.Head ? "head" : l == FinisherLoc.Leg ? "legs" : l == FinisherLoc.Arm ? "arms" : "torso"; }
    public void FireEvent(DuelFighter a, DuelFighter d, string type, bool heavy, FinisherLoc loc, MoveDef m)
    {
        ContactRelay.Raise(a, d, type, heavy, false);
        string id = m != null ? m.id : "";
        ContactRelay.RaiseEvent(new CombatEvent
        {
            type = type, actor = a, target = d, moveId = id, heavy = heavy, perfect = false, counter = false, rear = false,
            location = LocName(loc), direction = id.EndsWith("left") ? "left" : id.EndsWith("right") ? "right" : "front",
            point = d.transform.position + Vector3.up * (1.1f * FighterScale.Of(d)), tick = Duel.TickCount
        });
        if (type == "Hit") RTFinisher.Stage("contact", id);
    }

    public bool HasCamera { get { return arig != null && arig.Active; } }
    public void CameraCut(string shotId, Transform actor, Transform target, float blend, FinisherShot[] localDefs) { if (HasCamera) arig.Cut(shotId, actor, target, blend, localDefs); RTFinisher.Stage("shot", shotId); }
    public void CameraShake(float amplitude) { if (HasCamera) arig.Shake(amplitude); }
    public void CameraDone() { Abort(); }

    public void TimeTick() { FinisherTime.Tick(); }
    public void SlowMo(float scale, float dur, float easeIn, float easeOut) { FinisherTime.SlowMo(scale, dur, easeIn, easeOut); RTFinisher.Stage("slowmo", scale.ToString("0.00")); }
    public void SlowCurve(SlowPoint[] pts, float easeOut) { FinisherTime.Curve(pts, easeOut); RTFinisher.Stage("slowmo", "curve"); }   // V62
    public void TimeRestore() { FinisherTime.Restore(); }

    public void Notify(string text)
    {
        Debug.Log("[finisher] " + text); RTFinisher.Stage("notify", text);
        if (RTFinisher.Cfg.notify && STSteer.Active) STSteer.Feed(text);
    }

    // idempotent: time back to normal, camera back to the lock-on rig
    public void Abort()
    {
        if (aborted) return; aborted = true;
        FinisherTime.Restore();
        if (arig != null && startedRig) arig.End();
        if (crig != null) crig.Yield = false;
    }
}
