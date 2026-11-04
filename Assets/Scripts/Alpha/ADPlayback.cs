using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Alpha step 1, lane C: plays one resolved ADBeat as a real animation clash, BOTH fighters at once.
// The existing real-time combat (DuelFighter, Duel, Flinch, RagdollDeath, BloodBurst, ClashFx, CueBus, PresentationClock) is only DRIVEN through its public API:
//  - brains (PlayerBrain, AIBrain, TouchControls) are off; each fighter's `opponent` is null, so Duel.Resolve never decides anything (the dice already did);
//  - moves are started with DuelFighter.Press(..) at ticks computed so the contacts land in speed-tier order;
//  - when an attacker reaches its contact tick this class applies the outcome by hand through the public reaction calls
//    (Stagger / BlockHit / ParrySuccess / KnockDown / Die) and raises ContactRelay events, so audio, blood, hit-stop, flinch and sparks react as in the real game;
//  - defensive moves (guard, roll, sidestep) are timed against the contact they are meant to beat (or miss).
// ADBeat semantics (ADTypes.cs): out* is the result for that fighter's own move; a = Dracula, b = Pitborn.
// Every number is in duel_rules.json "camera" -> "playback" (ADPlaybackCfg).
public class ADPlayback : MonoBehaviour
{
    public static ADPlayback Instance { get; private set; }
    public DuelFighter[] fighters = new DuelFighter[2];   // [0] Dracula, [1] Pitborn
    public bool Ready { get { return fighters[0] != null && fighters[1] != null; } }
    public bool Busy { get; private set; }

    ADPlaybackCfg C { get { return ADCConfig.Camera.playback; } }
    Vector3 laneMid, laneAxis; bool laneSet;
    readonly MonoBehaviour[] brainsOff = new MonoBehaviour[0];
    readonly bool[] lightRight = new bool[2];
    readonly float[] savedHealth = new float[2];

    public static ADPlayback Ensure()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("ADPlayback"); DontDestroyOnLoad(go);
        return go.AddComponent<ADPlayback>();
    }
    void Awake()
    {
        Instance = this;
        ADDuelController.BeatPlayer = b => PlayBeat(b);
        ADDuelController.FinisherPlayer = b => ADFinisherPlayer.RunAfterBeat(this, b);
        ADDuelController.OnRestart += OnRestartDuel;
    }
    void OnRestartDuel()
    {
        StopAllCoroutines(); Busy = false; ADTime.Restore(); ADFinisherPlayer.ResetFight();
        if (!Ready) return;
        Revive(ADSide.Dracula); Revive(ADSide.Pitborn);
        lightRight[0] = lightRight[1] = false;
        if (laneSet) { Teleport(fighters[0], laneMid - laneAxis * (C.engageDist * 0.5f), laneAxis); Teleport(fighters[1], laneMid + laneAxis * (C.engageDist * 0.5f), -laneAxis); }
        if (ADCameraRig.Instance != null) ADCameraRig.Instance.Plan();
    }
    void OnDestroy() { ADDuelController.OnRestart -= OnRestartDuel; if (Instance == this) Instance = null; }

    // ---------------- setup ----------------
    // Finds the two DuelFighters, switches the brains off, claims the camera. Call once when the alpha duel starts.
    public bool Begin()
    {
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
        {
            bool pit = f.name.Contains("Pitborn") || f.transform.root.name.Contains("Pitborn");
            fighters[pit ? 1 : 0] = f;
        }
        if (!Ready) { Debug.LogWarning("[ADPlayback] need two DuelFighters (Dracula + Pitborn)"); return false; }
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        for (int i = 0; i < 2; i++) { fighters[i].opponent = null; fighters[i].moveDir = Vector3.zero; fighters[i].guardHeld = false; fighters[i].run = false; }
        Vector3 d = Flat(fighters[1].transform.position - fighters[0].transform.position); if (d.sqrMagnitude < 1e-4f) d = Vector3.forward;
        laneAxis = d.normalized; laneMid = (fighters[0].transform.position + fighters[1].transform.position) * 0.5f; laneSet = true;
        var rig = ADCameraRig.Ensure(); if (rig != null) rig.Begin(fighters[0].transform, fighters[1].transform);
        return true;
    }

    public void End()
    {
        if (ADCameraRig.Instance != null) ADCameraRig.Instance.End();
        ADTime.Restore();
    }

    // HP shown by the old HUD: fraction of the DuelFighter's own max. Presentation only (the rules own the real HP).
    public void SetHp(ADSide side, int hp, int hpMax)
    {
        var f = fighters[(int)side]; if (f == null || f.Dead) return;
        f.health = f.maxHealth * Mathf.Clamp01(hpMax > 0 ? (float)hp / hpMax : 1f);
    }

    // Brings a dead fighter back to idle for the next opponent (best effort: a ragdolled body needs a scene reload instead).
    public void Revive(ADSide side)
    {
        var f = fighters[(int)side]; if (f == null) return;
        var fi = f.GetComponent<Fighter>(); var col = f.GetComponent<Collider>(); if (col) col.enabled = true;
        f.health = f.maxHealth; f.state = FState.Loco; f.t = 0; f.stamina = f.maxStamina; f.posture = 0f;
        if (fi != null) { fi.holdLast = false; if (fi.anim) { fi.anim.speed = 1f; fi.Force("idle", 0.1f); } }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static int Idx(ADSide s) { return (int)s; }

    // ---------------- staging ----------------
    // Walks both fighters back to their spots on the lane axis, engageDist apart (smooth, with the walk clip), snaps leftovers.
    public IEnumerator Reposition(float dist = -1f)
    {
        if (!Ready || !laneSet) yield break;
        if (dist <= 0f) dist = C.engageDist;
        Vector3[] goal = { laneMid - laneAxis * (dist * 0.5f), laneMid + laneAxis * (dist * 0.5f) };
        float t = 0f;
        while (t < C.repositionSeconds)
        {
            bool all = true;
            for (int i = 0; i < 2; i++)
            {
                var f = fighters[i]; if (f.Dead) continue;
                Vector3 dl = Flat(goal[i] - f.transform.position);
                if (dl.magnitude > C.repositionTolerance && (f.state == FState.Loco || f.state == FState.Guard)) { f.moveDir = dl.normalized; f.guardHeld = false; f.run = false; all = false; }
                else { f.moveDir = Vector3.zero; if (dl.magnitude > C.repositionTolerance) all = false; }
            }
            if (all) break;
            t += Time.deltaTime; yield return null;
        }
        for (int i = 0; i < 2; i++)
        {
            var f = fighters[i]; f.moveDir = Vector3.zero;
            if (f.Dead) continue;
            Vector3 dl = Flat(goal[i] - f.transform.position);
            if (dl.magnitude > 0.02f && dl.magnitude <= Mathf.Max(C.snapTolerance, 0.02f)) Teleport(f, new Vector3(goal[i].x, f.transform.position.y, goal[i].z), laneAxis * (i == 0 ? 1f : -1f));
        }
        yield return null;
    }

    public static void Teleport(DuelFighter f, Vector3 pos, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (cc) cc.enabled = false;
        f.transform.position = pos;
        facing.y = 0f; if (facing.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
        if (cc) cc.enabled = true;
    }

    // ---------------- beat data ----------------
    enum Cls { None, Land, GuardBreak, Blocked, Parried, Dodged, Whiff, Clash, Interrupted }
    class Atk   // one attack of the beat
    {
        public int me, foe; public ADMove move; public string moveId; public Cls cls; public ADLoc loc; public int dmg; public bool crit, kills, knockdown, staggerPost, riposte;
        public int windup, off; public int startTick, contactTick; public bool fired, dead, applied; public bool heavy; public bool isRiposte;
        public int triggerTick;
    }
    class Sched { public int tick; public Action act; public bool done; }

    static bool IsAttack(ADMove m) { return m == ADMove.Light || m == ADMove.Slash || m == ADMove.Stab || m == ADMove.Heavy || m == ADMove.Kick || m == ADMove.Special; }
    static string LocName(ADLoc l) { return l == ADLoc.Head ? "head" : l == ADLoc.Leg ? "legs" : l == ADLoc.Arm ? "arms" : "torso"; }

    ADOutcome Out(ADBeat b, int i) { return i == 0 ? b.outA : b.outB; }
    ADMove Mv(ADBeat b, int i) { return i == 0 ? b.a : b.b; }
    bool Lost(ADBeat b, int i) { return i == 0 ? b.lostA : b.lostB; }
    int DmgTo(ADBeat b, int i) { return i == 0 ? b.dmgToA : b.dmgToB; }
    ADLoc LocOf(ADBeat b, int i) { return i == 0 ? b.locA : b.locB; }
    bool CritBy(ADBeat b, int i) { return i == 0 ? b.critA : b.critB; }
    bool Stag(ADBeat b, int i) { return i == 0 ? b.staggerA : b.staggerB; }
    bool Knock(ADBeat b, int i) { return i == 0 ? b.knockdownA : b.knockdownB; }

    string MoveId(ADMove m, int i)
    {
        switch (m)
        {
            case ADMove.Light: return lightRight[i] ? C.lightRightMove : C.lightLeftMove;
            case ADMove.Slash: return C.slashSide == "right" ? C.lightRightMove : C.lightLeftMove;
            case ADMove.Stab: return C.stabMove;
            case ADMove.Heavy: return C.heavyMove;
            case ADMove.Kick: return C.kickMove;
            case ADMove.Special: return i == 0 ? C.special1Move : C.special2Move;
        }
        return "";
    }
    void PressMove(DuelFighter f, ADMove m, string id)
    {
        switch (m)
        {
            case ADMove.Stab: f.Press("thrust"); break;
            case ADMove.Heavy: f.Press("heavy"); break;
            case ADMove.Kick: f.Press("kick"); break;
            case ADMove.Special: f.special1Cd = 0; f.special2Cd = 0; f.Press(id == C.special1Move ? "special1" : "special2"); break;
            default: f.Press(id); break;   // light_left / light_right
        }
    }
    int OffTicks(MoveDef md)
    {
        var ac = AttackClipTable.Get(); float fr = ac != null && ac.enabled && ac.defaults != null ? ac.defaults.contactActiveFrac : 0.5f;
        return Mathf.Max(1, Mathf.RoundToInt(fr * (md != null ? md.Active(false) : 4)));
    }

    Cls Classify(ADBeat b, int x, int y)
    {
        var m = Mv(b, x); if (!IsAttack(m) || Lost(b, x)) return Cls.None;
        var ox = Out(b, x); var oy = Out(b, y); var my = Mv(b, y);
        if (ox == ADOutcome.Interrupted) return Cls.Interrupted;
        if (ox == ADOutcome.Clash) return Cls.Clash;
        if (ox == ADOutcome.Hit || ox == ADOutcome.Killed)
        {
            if (my == ADMove.Guard && (oy == ADOutcome.GuardBreak || oy == ADOutcome.Staggered)) return Cls.GuardBreak;
            return Cls.Land;
        }
        if (ox == ADOutcome.Blocked) return (oy == ADOutcome.Parried || oy == ADOutcome.Riposte) ? Cls.Parried : Cls.Blocked;
        if (ox == ADOutcome.Whiff) return (my == ADMove.Roll || my == ADMove.Sidestep || oy == ADOutcome.Dodged) ? Cls.Dodged : Cls.Whiff;
        return Cls.Whiff;
    }

    // ---------------- the beat ----------------
    // Play one resolved beat. Start with StartCoroutine(PlayBeat(beat)); it returns when both fighters are idle again and the camera is back on the plan view.
    public IEnumerator PlayBeat(ADBeat b)
    {
        if (!Ready && !Begin()) yield break;
        Busy = true;
        var rig = ADCameraRig.Ensure();
        // killing blow: ADDuelController runs FinisherPlayer(beat) right after this beat, so when a finisher is wanted the whole scene is played there
        if (b.killed && C.useFinishers && ADFinisherPlayer.Wants(b)) { Busy = false; yield break; }
        yield return PlayBeatPlain(b);
    }

    // The beat as the sim shows it, no finisher (also the fallback of ADFinisherPlayer when it picks nothing).
    public IEnumerator PlayBeatPlain(ADBeat b)
    {
        if (!Ready && !Begin()) yield break;
        Busy = true;
        var rig = ADCameraRig.Ensure();
        yield return Reposition();
        Prep();
        var fa = fighters; var atk = new List<Atk>(); var sched = new List<Sched>();
        int T0 = Duel.TickCount + Mathf.Max(2, C.leadTicks);

        // 1. classify both attacks
        var ax = new Atk[2];
        for (int x = 0; x < 2; x++)
        {
            int y = 1 - x; var m = Mv(b, x); Cls cl = Classify(b, x, y);
            if (!IsAttack(m) || Lost(b, x)) continue;
            var a = new Atk { me = x, foe = y, move = m, cls = cl, moveId = MoveId(m, x), dmg = DmgTo(b, y), loc = LocOf(b, y) };
            a.crit = CritBy(b, x) && cl == Cls.Land; a.heavy = m == ADMove.Heavy || m == ADMove.Special || a.crit;
            a.kills = b.killed && (int)b.victim == y && (cl == Cls.Land || cl == Cls.GuardBreak);
            a.knockdown = Knock(b, y) && (cl == Cls.Land || cl == Cls.GuardBreak);
            a.staggerPost = Stag(b, x) && (cl == Cls.Whiff || cl == Cls.Dodged);
            a.riposte = false;
            var md = fa[x].moves != null ? fa[x].moves.Get(a.moveId) : null;
            a.windup = md != null ? md.Windup(false) : 12; a.off = OffTicks(md);
            ax[x] = a; atk.Add(a);
            if (m == ADMove.Light) lightRight[x] = !lightRight[x];
        }
        // a riposte of the parrying fighter is a second, dynamic attack (below)

        // 2. contact ticks in speed-tier order. c(X) = ticks from the press to the contact
        int first = b.firstA ? 0 : 1;
        if (ax[first] == null || ax[first].cls == Cls.Interrupted) first = 1 - first;
        int gap = C.tradeGapTicks;
        bool bothAtk = ax[0] != null && ax[1] != null;
        if (bothAtk && ax[0].cls == Cls.Clash && ax[1].cls == Cls.Clash) gap = C.clashGapTicks;
        int Tc1 = T0;
        foreach (var a in atk) { int c = 1 + a.windup + a.off; int need = c - (bothAtk && a.me != first ? gap : 0); Tc1 = Mathf.Max(Tc1, T0 + need); }
        foreach (var a in atk)
        {
            a.contactTick = Tc1 + (bothAtk && a.me != first ? gap : 0);
            a.startTick = a.contactTick - (1 + a.windup + a.off);
        }

        // 3. schedule: presses, defences, crit slow-mo
        var camCfg = ADCConfig.Camera;
        foreach (var a in atk)
        {
            var A = a;
            sched.Add(new Sched { tick = A.startTick, act = () => { fa[A.me].guardHeld = false; PressMove(fa[A.me], A.move, A.moveId); } });
            if (A.crit && A.cls == Cls.Land) sched.Add(new Sched { tick = A.contactTick - Mathf.RoundToInt(C.critSlowLeadTicks), act = () => Slow(camCfg.critSlow) });
        }
        for (int y = 0; y < 2; y++)
        {
            var m = Mv(b, y); if (IsAttack(m) || Lost(b, y)) continue;
            int x = 1 - y; var A = ax[x]; var Y = fa[y]; int yy = y;
            int ct = A != null ? A.contactTick : T0 + 8;
            if (m == ADMove.Guard)
            {
                bool parry = A != null && A.cls == Cls.Parried;
                int up = ct - (parry ? C.parryLeadTicks : C.blockLeadTicks);
                sched.Add(new Sched { tick = Mathf.Max(T0 - 1, up), act = () => { Y.guardHeld = true; } });
            }
            else
            {
                bool succeed = A == null || A.cls == Cls.Dodged || A.cls == Cls.Whiff || A.cls == Cls.Interrupted;
                var dd = DodgeTable.Get(); int isf = m == ADMove.Roll ? dd.roll.iframeStart : dd.sidestep.iframeStart;
                int at = succeed ? ct - isf - C.dodgeLeadExtra : ct - 1;
                int dir = (b.beat + y) % 2 == 0 ? -1 : 1;
                sched.Add(new Sched { tick = Mathf.Max(T0 - 1, at), act = () => { Y.guardHeld = false; if (m == ADMove.Roll) { Y.moveDir = -Y.transform.forward; Y.Press("roll"); } else Y.PressSidestep(dir); } });
                sched.Add(new Sched { tick = Mathf.Max(T0 - 1, at) + 2, act = () => { Y.moveDir = Vector3.zero; } });
            }
        }

        // 4. camera: opening wide cut
        if (rig != null) rig.Cut(camCfg.startShot, fa[0].transform, fa[1].transform);
        sched.Sort((p, q) => p.tick.CompareTo(q.tick));

        // 5. run
        float t0 = Time.unscaledTime, settle = 0f; var riposteQueue = new List<Atk>();
        while (true)
        {
            int tick = Duel.TickCount;
            foreach (var s in sched) if (!s.done && tick >= s.tick) { s.done = true; s.act(); }
            FaceEach();
            // contacts
            var all = new List<Atk>(atk); all.AddRange(riposteQueue);
            foreach (var a in all)
            {
                if (a.fired || a.dead) continue;
                var me = fa[a.me]; var md = me.CurrentMove;
                bool inMove = (me.state == FState.Windup || me.state == FState.Active) && md != null && md.id == a.moveId;
                if (me.state == FState.Active && md != null && md.id == a.moveId && me.t >= a.off) { a.fired = true; OnContact(b, a, all, riposteQueue, sched); }
                else if (!inMove && a.contactTick > 0 && tick > a.contactTick + 12 && a.cls != Cls.Interrupted) { a.dead = true; }   // never reached the contact (cancelled)
                else if (!inMove && a.cls == Cls.Interrupted && tick > a.contactTick + 12) a.dead = true;
            }
            bool pending = false;
            foreach (var s in sched) if (!s.done) pending = true;
            foreach (var a in atk) if (!a.fired && !a.dead && a.cls != Cls.Interrupted) pending = true;
            foreach (var a in riposteQueue) if (!a.fired && !a.dead) pending = true;
            bool idle = Idle(fa[0]) && Idle(fa[1]);
            if (!pending && idle) { settle += Time.unscaledDeltaTime; if (settle >= C.settleSeconds) break; } else settle = 0f;
            if (Time.unscaledTime - t0 > C.maxBeatSeconds) { Debug.LogWarning("[ADPlayback] beat timeout " + b); break; }
            yield return null;
        }
        Cleanup();
        if (rig != null) rig.Cut(camCfg.endShot, fa[0].transform, fa[1].transform);
        Busy = false;
    }

    static bool Idle(DuelFighter f) { return f.state == FState.Loco || f.state == FState.Guard || f.state == FState.Dead; }

    void Slow(ADSlowKey k) { if (k != null) ADTime.SlowMo(k.scale, k.duration, k.easeIn, k.easeOut); }

    void Prep()
    {
        for (int i = 0; i < 2; i++)
        {
            var f = fighters[i]; savedHealth[i] = f.health;
            f.opponent = null; f.moveDir = Vector3.zero; f.run = false; f.guardHeld = false; f.heavyHeld = false;
            f.stamina = f.maxStamina; f.posture = 0f;
            if (!f.Dead) f.health = Mathf.Max(f.health, C.healthPad);
        }
    }
    void Cleanup()
    {
        for (int i = 0; i < 2; i++)
        {
            var f = fighters[i]; f.moveDir = Vector3.zero; f.guardHeld = false; f.posture = 0f; f.stamina = f.maxStamina; f.opponent = null;
            if (!f.Dead) f.health = savedHealth[i];
        }
    }

    void FaceEach()
    {
        for (int i = 0; i < 2; i++)
        {
            var f = fighters[i]; var o = fighters[1 - i];
            if (f.Dead || f.state == FState.Stagger || f.state == FState.Down || (f.state == FState.Roll)) continue;
            if (f.moveDir.sqrMagnitude > 0.01f && f.state == FState.Loco) continue;   // walking: the sim turns him toward the walk
            Vector3 d = Flat(o.transform.position - f.transform.position); if (d.sqrMagnitude < 1e-4f) continue;
            f.transform.rotation = Quaternion.Slerp(f.transform.rotation, Quaternion.LookRotation(d.normalized, Vector3.up), Mathf.Min(1f, 10f * Time.deltaTime));
        }
    }

    // ---------------- contact ----------------
    void OnContact(ADBeat b, Atk a, List<Atk> all, List<Atk> riposteQ, List<Sched> sched)
    {
        var rig = ADCameraRig.Instance; var cfg = ADCConfig.Camera;
        // trade: the first hit's reaction waits for the other landing hit of the beat so the slower swing is not cancelled before it connects
        if ((a.cls == Cls.Land || a.cls == Cls.GuardBreak) && !a.isRiposte)
        {
            foreach (var o in all)
                if (o != a && !o.fired && !o.dead && (o.cls == Cls.Land || o.cls == Cls.GuardBreak) && o.me == a.foe) { pendingTrade = a; return; }
        }
        ApplyContact(b, a, riposteQ, sched);
        if (pendingTrade != null && pendingTrade != a) { var p = pendingTrade; pendingTrade = null; ApplyContact(b, p, riposteQ, sched, true); }
    }
    Atk pendingTrade;

    string KindOf(Atk a)
    {
        switch (a.cls)
        {
            case Cls.Land: return a.isRiposte ? "Riposte" : "Land";
            case Cls.GuardBreak: return "GuardBreak"; case Cls.Blocked: return "Blocked"; case Cls.Parried: return "Parried";
            case Cls.Dodged: return "Dodged"; case Cls.Clash: return "Clash"; default: return "Whiff";
        }
    }

    void ApplyContact(ADBeat b, Atk a, List<Atk> riposteQ, List<Sched> sched, bool late = false)
    {
        var me = fighters[a.me]; var foe = fighters[a.foe]; var md = me.CurrentMove;
        Vector3 away = Flat(foe.transform.position - me.transform.position).normalized;
        float kb = (md != null ? md.knockback : 1f) * me.scale * 0.9f;
        var rig = ADCameraRig.Instance; var cfg = ADCConfig.Camera;
        var os = ADCConfig.OutcomeShot(KindOf(a));
        string shotId = os != null ? ((a.crit && !string.IsNullOrEmpty(os.critShot)) ? os.critShot : os.shot) : null;
        if (a.kills && !string.IsNullOrEmpty(cfg.killShot)) shotId = cfg.killShot;
        if (rig != null && !string.IsNullOrEmpty(shotId)) rig.Cut(shotId, me.transform, foe.transform);

        switch (a.cls)
        {
            case Cls.Land:
            {
                int st = a.heavy ? C.hitStaggerHeavy : C.hitStaggerLight; if (a.crit) st += C.critStaggerExtra; if (a.move == ADMove.Kick) st = C.kickStaggerTicks;
                string clip = a.heavy ? C.hitHeavyClip : (Angle(foe.transform.forward, -away) > 90f ? C.hitBackClip : C.hitLightClip);
                foe.SetKnock(away * kb * (a.crit ? C.knockCrit / Mathf.Max(0.1f, C.knockHeavy) : 1f));
                if (a.kills) { foe.Die(); }
                else if (a.knockdown) foe.KnockDown();
                else foe.Stagger(st, clip);
                Fire(me, foe, "Hit", a.heavy, false, a.loc, md);
                foe.posture = 0f;
                break;
            }
            case Cls.GuardBreak:
            {
                foe.stamina = 0f; foe.SetKnock(away * kb * 0.6f);
                if (a.kills) foe.Die(); else if (a.knockdown) foe.KnockDown(); else foe.Stagger(C.guardBreakTicks, C.staggerClip);
                Fire(me, foe, "GuardBroken", true, false, a.loc, md);
                break;
            }
            case Cls.Blocked:
                foe.BlockHit(a.heavy); foe.SetKnock(away * kb * 0.5f);
                Fire(me, foe, "Blocked", a.heavy, false, ADLoc.Arm, md);
                break;
            case Cls.Parried:
            {
                foe.ParrySuccess(); me.Stagger(C.parryStaggerTicks, C.staggerClip);
                Fire(me, foe, "Parried", a.heavy, true, ADLoc.Arm, md);
                Slow(cfg.parrySlow);
                bool rip = Out(b, a.foe) == ADOutcome.Riposte || (a.foe == 0 ? b.riposteA : b.riposteB);
                if (rip && DmgTo(b, a.me) > 0)
                {
                    var r = new Atk { me = a.foe, foe = a.me, move = ADMove.Light, moveId = "riposte", cls = Cls.Land, dmg = DmgTo(b, a.me), loc = LocOf(b, a.me), isRiposte = true, heavy = false, off = 0, crit = false };
                    r.kills = b.killed && (int)b.victim == a.me; var rmd = foe.moves != null ? foe.moves.Get("riposte") : null; r.windup = rmd != null ? rmd.Windup(false) : 10; r.off = OffTicks(rmd);
                    r.contactTick = Duel.TickCount + C.riposteDelayTicks + 1 + r.windup + r.off;
                    riposteQ.Add(r);
                    var Foe = foe;
                    sched.Add(new Sched { tick = Duel.TickCount + C.riposteDelayTicks, act = () => { Foe.guardHeld = false; Foe.Press("light"); } });
                }
                break;
            }
            case Cls.Clash:
                me.Stagger(C.clashStaggerTicks, C.staggerClip);
                if (!foe.Dead && foe.state != FState.Stagger) foe.Stagger(C.clashStaggerTicks, C.staggerClip);
                Fire(me, foe, "Blocked", true, false, ADLoc.Arm, md);
                Fire(foe, me, "Blocked", true, false, ADLoc.Arm, foe.CurrentMove);
                if (rig != null) rig.Shake(0.05f);
                break;
            case Cls.Dodged: case Cls.Whiff:
                if (a.staggerPost) { var Me = me; int ft = C.whiffStaggerTicks > 0 ? C.whiffStaggerTicks : 30; sched.Add(new Sched { tick = Duel.TickCount + 10, act = () => { if (!Me.Dead) Me.Stagger(ft, C.staggerClip); } }); }
                break;
        }
        a.applied = true;
    }

    static float Angle(Vector3 f, Vector3 t) { f.y = 0; t.y = 0; return Vector3.Angle(f, t); }

    // raises what Duel.Fire raises, so Flinch, BloodBurst, ClashFx, CueBus, CameraImpact and PresentationClock react exactly as in the real fight
    void Fire(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect, ADLoc loc, MoveDef m)
    {
        ContactRelay.Raise(a, d, type, heavy, perfect);
        string id = m != null ? m.id : "";
        ContactRelay.RaiseEvent(new CombatEvent
        {
            type = type, actor = a, target = d, moveId = id, heavy = heavy, perfect = perfect, counter = false, rear = false,
            location = LocName(loc), direction = id.EndsWith("left") ? "left" : id.EndsWith("right") ? "right" : "front",
            point = d.transform.position + Vector3.up * (1.1f * Mathf.Max(1f, d.scale)), tick = Duel.TickCount
        });
    }

    // exposed for ADFinisherPlayer
    public void PrepPublic() { Prep(); }
    public void CleanupPublic() { Cleanup(); }
    public void FireEvent(DuelFighter a, DuelFighter d, string type, bool heavy, ADLoc loc, MoveDef m) { Fire(a, d, type, heavy, false, loc, m); }
}
