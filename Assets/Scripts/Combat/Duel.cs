using System.Collections.Generic;
using UnityEngine;

// Fixed 60 Hz deterministic fight clock + hit resolution (ported from V3 duel.ts stepDuel rules: guard arc, parry window, posture, rear/counter bonuses).
public class Duel : MonoBehaviour
{
    static readonly List<DuelFighter> fighters = new List<DuelFighter>();
    public static int TickCount;
    public static bool Frozen;   // FN1: true while a finisher scene runs: no brain decides, no new hit resolves (reset on scene load)
    public static bool FrozenFor(DuelFighter f) { if (!Frozen) return false; if (!RTFinisher.Busy) return true; var r = FinisherConfig.Get().rt.realtime; if (r == null || !r.enabled || !r.pairOnlyFreeze) return true; return f == null || f == RTFinisher.Killer || f == RTFinisher.Victim; }   // FINREALTIME v1 H2: Frozen for THIS fighter. A finisher scene with rt.realtime.pairOnlyFreeze freezes only the killer and the victim; menus / loot / market (no finisher) freeze everyone as before
    float acc;
    static Duel inst;
    public static float Alpha { get { return inst == null || PresentationClock.Frozen ? 1f : Mathf.Clamp01(inst.acc * 60f); } }   // PHYSAUDIT: 0..1 inside the current tick, ViewInterp only (never read by the sim)
    public static void Register(DuelFighter f) { if (!fighters.Contains(f)) fighters.Add(f); }
    public static void Unregister(DuelFighter f) { fighters.Remove(f); }

    // FPSSIM v1: the AI brains decide once per 60 Hz tick (they used to run in Update: once per RENDERED frame with n = ticks since the last frame, so the decision cadence, the RNG draws and
    // the state each decision saw depended on the display rate). State after tick j -> intent for tick j+1, the same latency as a 60 fps frame. Data: ai_rows.json unity.brainsOnTick (default false).
    public static bool BrainsOnTick { get { return AiRows.I.unity.brainsOnTick; } }
    static void SimBrains()
    {
        for (int i = 0; i < fighters.Count; i++) { var b = fighters[i].GetComponent<AIBrain>(); if (b != null && b.isActiveAndEnabled) b.SimStep(); }
        for (int i = 0; i < fighters.Count; i++) { var fb = fighters[i].GetComponent<FlowBot>(); if (fb != null && fb.isActiveAndEnabled) fb.SimStep(); }
    }

    void Awake() { TickCount = 0; Frozen = false; inst = this; }
    void OnDestroy() { fighters.Clear(); Frozen = false; }

    void Update()
    {
        acc += Mathf.Min(PresentationClock.SimDelta(Time.deltaTime), PhysicsCfg.Get().clock.maxCatchupS); /* DATALAYER b11 */   // hit-stop: no accumulation while the presentation clock is frozen
        while (acc >= 1f / 60f)
        {
            acc -= 1f / 60f; TickCount++;
            for (int i = 0; i < fighters.Count; i++) fighters[i].Tick();
            for (int i = 0; i < fighters.Count; i++) fighters[i].MoveTick();   // PHYSAUDIT: bodies move on the sim tick (after every decision of this tick)
            BowHooks.SimTick(fighters);   // BOW v2: bow mode switch + arrows landing (master off = returns at once)
            if (BrainsOnTick) SimBrains();   // FPSSIM v1
            PhysicsClock.OnSimTick();   // PHYS1 v1: ONE Physics.Simulate per 60 Hz sim tick, after the fighters ticked (hit-stop = no tick = no step)
            if (PresentationClock.Frozen) { acc = 0f; break; }   // a contact this tick requested a stop (V3 main.ts: accumulator = 0)
        }
        // DEATHFIX v92: safety net. HP 0 with the state not Dead (any damage path that skipped Resolve's Die(), a finisher that ended without its own Die()) must still kill the fighter.
        // A running finisher scene owns the kill (it holds the victim at >= 1 HP until its own Die()), so it is skipped while Busy / Frozen. death.json enforceZeroHp.
        if (DeathTable.Get().enforceZeroHp && !Frozen && !RTFinisher.Busy)
            for (int i = 0; i < fighters.Count; i++) { var z = fighters[i]; if (z != null && !z.Dead && z.maxHealth > 0f && z.health <= 0f) z.Die(); }
    }

    static float Angle(Vector3 fwd, Vector3 to) { fwd.y = 0; to.y = 0; return Vector3.Angle(fwd, to); }

    // returns true when the strike made contact (hit, block, parry): the move then stops checking
    public static bool Resolve(DuelFighter a, DuelFighter d, MoveDef m, float chargeMul)
    {
        if (d.Dead || FrozenFor(d) || FrozenFor(a) /* FINREALTIME v1 H2b */ || HubSafe.NoHit(d)) return false;   // laneNPCSPAWN: the player in a safe camp takes no strike (health, posture, stamina stay)
        if (!RangedNow && BowHooks.Handles(a, m)) return BowHooks.Release(a, d, m, chargeMul);   // BOW v2: a bow-mode swing throws the arrow (BowShot judges it on arrival through this same method)
        Vector3 ad = d.transform.position - a.transform.position; ad.y = 0f;
        float dist = ad.magnitude;
        float arc = m.id == "kick" ? a.Rule("kickArc", Mathf.PI / 4f) * Mathf.Rad2Deg : a.HitArcDeg; /* DATALAYER b11 */
        if (!RangedNow /* BOW v2 */ && dist > m.reach * a.ReachScale /* WEAPONSWAP v88: weapon reach */ + a.radius + d.radius + 0.45f) return false;
        if (!RangedNow /* BOW v2 */ && Angle(a.transform.forward, ad) > arc) return false;
        if (d.IFrames && !d.SidestepIgnoresIFrames(m)) return false;   // rolled through it (lanePARRY: a sidestep does not beat an overhead, parry.json sidestep)
        HitReactionTable.Last = null;   // P2: set again below only for a clean hit
        if (BellyBite.Handles(a, m)) { var bgR = BellyBite.Resolve(a, d); if (bgR == BellyBite.Result.Landed) { Fire(a, d, "Hit", true, false); return true; } if (bgR == BellyBite.Result.Scene) return true; /* BELLYGRAB v1 */ }   // BELLYBITE v1: unblockable grab (data belly_bite.json, master switch off = never true)

        bool heavy = m.charges || m.damage >= a.HeavyDamage; /* DATALAYER b11 */
        string evt;
        Vector3 away = ad.normalized;
        bool defGuardArc = d.IsGuarding && Angle(d.transform.forward, -ad) <= a.Rule("guardArc", Mathf.PI / 3f) * Mathf.Rad2Deg * StanceLayer.GuardArcMul(d) /* STANCE2 v81 */; bool defGuard = defGuardArc && DirectionalGuard.SideOk(a, d, m, heavy); bool shieldCov = !defGuard && defGuardArc && ShieldHooks.Covers(d, m, heavy); if (shieldCov) defGuard = true; /* SHIELD v1: the wearer's shield cover rows (gear.json items[].shield) count a swing the guard side alone would not stop as a BLOCK; never a parry */    // CTRL v68: the guard side must cover the swing (controls.json guard.guardRequiresSide)

        if (defGuard && !shieldCov /* SHIELD v1 */ && d.InParryWindow && m.parryable && (!heavy || !StaminaTable.Parry().enabled || StaminaTable.Parry().parryHeavies))
        {
            d.ParrySuccess();
            a.Stagger(StaminaTable.ParryStagger(heavy, (int)a.Rule("parryStun", 90)), "stagger_loop"); a.AddPosture(a.ParryAttackerPosture(heavy));   /* lanePARRY: DuelFighterParry.cs */ if (PostureTable.Get().enabled) d.RelievePosture(PostureTable.Get().parry.relief); bool pbk = a.TryPostureBreak(d);
            Fire(a, d, "Parried", heavy, false); if (pbk) Fire(d, a, "GuardBroken", true, false); return true;
        }
        if (defGuard && m.id == "kick" && m.hasVsGuard)
        {
            d.SpendStamina(m.vsGuardStaminaDamage); d.GuardBreakStun((int)m.vsGuardStagger); if (PostureTable.Get().enabled) { d.AddPosture(PostureTable.Get().block.kick * StanceLayer.PostureMul(a, d, false) /* STANCE2 v81 */); d.TryPostureBreak(a); }
            Fire(a, d, "GuardBroken", true, false); return true;
        }
        if (defGuard)
        {
            d.SpendStamina(StaminaTable.BlockCost(m, heavy, d.Exhausted, a.Rule("blockCost", 25f)) * StanceLayer.BlockCostMul(d) * (shieldCov ? ShieldHooks.ExtraCostMul(d) : 1f) /* SHIELD v1 */);   // lanePARRY: stance block cost (stances.json)
            d.AddPosture(PostureTable.BlockGain(m, heavy) * ShieldHooks.BlockPostureMul(d) /* SHIELD v1 */ * WeaponHooks.PostureDealtMul(a) /* WEAPONSWAP v88 */ * StanceLayer.PostureMul(a, d, false) /* STANCE2 v81 */); if (PostureTable.Get().enabled && PostureTable.Get().block.attackerBlocked > 0f) a.AddPosture(PostureTable.Get().block.attackerBlocked); bool pbb = d.TryPostureBreak(a);
            d.health = Mathf.Max(1f, d.health - m.damage * StanceLayer.BlockChipMul(d) /* STANCE2 v81 */ * StaminaTable.ChipFrac(m, d.Exhausted) * (STSteer.Active ? STSteer.DamageMul(a, d) : 1f) * GearHooks.DamageTakenMul(d));   /* kitGEAR v79: armour stat on block chip */
            if (pbb) { Fire(a, d, "GuardBroken", true, false); }
            else if (StaminaTable.BreaksGuard(d.stamina, m, heavy))
            {
                d.GuardBreakStun(); Fire(a, d, "GuardBroken", true, false);
            }
            else { d.NoteBlock(); d.BlockHit(heavy); d.SetKnock(away * m.knockback * a.scale * 0.5f); Fire(a, d, "Blocked", heavy, d.InParryWindow); if (shieldCov) ContactRelay.Raise(a, d, "ShieldBlocked", heavy, false); /* SHIELD v1: shield cue + sparks (cues.json block_shield, clash_fx.json ShieldBlocked) */ }
            return true;
        }

        // clean hit
        bool rear = Angle(d.transform.forward, -ad) > 90f;                 // attacker behind the defender
        bool counter = d.InWindup; lastRear = rear; lastCounter = counter;
        var hr = HitReactionTable.Pick(a, d, m, heavy, counter, rear, -1f);   // P2: location / direction / weight + the animator state (null = legacy three states)
        HitReactionTable.Last = hr;
        float dmg = m.damage * a.DamageScaleNow * chargeMul * (rear ? a.Rule("rear.damage", 1.25f) : 1f) * (counter ? a.Rule("counter.damage", 1.25f) : 1f);
        if (STSteer.Active) dmg *= STSteer.DamageMul(a, d); bool db = PostureTable.Get().enabled && d.DeathblowOpen; if (db) dmg = PostureTable.DeathblowDamage(dmg, d);   // P6 deathblow: the next hit on a posture-broken fighter   // steer stance modifiers (dealt / taken, Trickster per-hit roll)
        bool dbLethal = db && StanceLayer.KeepLethal && dmg > d.health;   // STANCE2 v81: DeathblowDamage returned health + 1 (lethal); it must stay lethal under any damage multiplier < 1
        if (FightBalance.Active) dmg *= FightBalance.HitMul(a, d, m);   // B1: fight_balance.json
        float stg = m.stagger * chargeMul * (rear ? a.Rule("rear.stagger", 1.25f) : 1f) * (counter ? a.Rule("counter.stagger", 1.5f) : 1f);
        var pv = PoiseTable.Evaluate(a, d, m, db); dmg *= pv.damageMul * GearHooks.DamageTakenMul(d); d.health -= dmg; /* kitGEAR v79: armour stat */   // V64P: hyper-armour, poise meter, riposte damage multiplier (poise.json)
        if (ShoveTable.NoKill(m) && d.health <= 0f) d.health = 1f;   // SHOVE v1: a shove (shove.json nonLethal) never kills: health stays at 1 at the least
        if (dbLethal && d.health > 0f) d.health = 0f;   // STANCE2 v81: after the last damage multiplier (stance taken, balance, poise, gear armour)
        if (db) d.ConsumeDeathblow(a); else d.AddPosture(pv.ClampPosture(d, StanceLayer.PostureDealtMul(a, d, true) /* STANCE2 v81: attacker postureDealtMul x Trickster proc; the defender half is the PostureTakenMul(d) factor of this line */ * PostureTable.HitGain(m) * WeaponHooks.PostureDealtMul(a) /* WEAPONSWAP v88 */ * (rear ? a.Rule("rear.posture", 1.5f) : 1f) * StanceLayer.PostureTakenMul(d) * GearHooks.PostureTakenMul(d)));   /* kitGEAR v79: poise stat */   // lanePARRY: stance posture resist
        d.SetKnock(away * m.knockback * a.scale * 0.9f * pv.knockMul);
        if (ShoveTable.IsMove(m.id)) ShoveTable.ApplyKnock(a, d, m, stg, hr, pv.knockMul);   // SHOVE v1: the knock velocity that puts the foe at shove.json pushToM (replaces the line above for a shove id only)
        HitKnockback.Apply(a, d, m, away, heavy, counter, stg, hr != null ? hr.staggerScale : 1f, pv.knockMul);   // KNOCKBACK v1: per-class / mass / boss-resist push of the victim root, clamped by arena + colliders (dark: knockback.enabled=false returns at once)
        if (db && d.health > 0f && RTFinisher.DeathblowFinishes && RTFinisher.TryBegin(a, d, m, true)) return true;   // V61B: a posture-break deathblow ALWAYS starts a finisher (finishers.json rt.deathblowFinishes), lethal or not; the scene's Die() is the kill
        if (d.health <= 0f)
        {
            if (RTFinisher.TryBegin(a, d, m, db)) return true;   // FN1: killing-blow finisher scene (finishers.json enableFinishers, default false); the plain death below is the fallback
            d.Die(); Fire(a, d, "Hit", heavy, false); return true;
        }
        if (pv.armoured) { Fire(a, d, "Hit", heavy, false); return true; }   /* V64P */ if (PostureTable.Get().enabled) { if (d.TryPostureBreak(a)) { Fire(a, d, "GuardBroken", true, false); return true; } }   // P6: posture break = long stagger + deathblow window (posture.json)
        else if (d.posture >= a.Rule("posture.max", 100f)) { d.KnockDown(); Fire(a, d, "GuardBroken", true, false); return true; }
        if (hr != null && hr.knockdown && !pv.absorb && !pv.grace) { d.KnockDown(); Fire(a, d, "Hit", heavy, false); return true; }   // data: allowKnockdown + a knockdown row (sim change, default off)
        string clip = hr != null ? hr.state : (heavy ? "hit_heavy" : (rear ? "hit_light_back" : "hit_light_front"));
        if (pv.absorb) d.PoiseBreakOut(pv.breakOutTicks); else if (!pv.grace) d.Stagger(Mathf.Max(db ? PostureTable.Get().deathblow.staggerTicks : 0, (int)(stg * (hr != null ? hr.staggerScale : 1f))), clip, hr != null ? hr.startS : 0f /* PITMAULFIX v1 */);   // V64P: poise absorb = break-out, grace = no interrupt
        Fire(a, d, "Hit", heavy, false);
        return true;
    }

    static bool lastRear, lastCounter;
    public static bool RangedNow; public static MoveDef RangedMove;   // BOW v2: an arrow is being judged (BowShot.Land): no reach / arc test, no finisher; events carry the shooter's swing
    static MoveDef FM(DuelFighter a) { return RangedMove ?? a.CurrentMove; }   // BOW v2
    // hit zone from the move (V3 hit-react zones): overhead/heavy -> head, thrust -> torso, kick -> legs, side cuts -> arms
    static string LocationOf(MoveDef m, DuelFighter d) { if (m == null) return "torso"; string id = m.id; if (id.Contains("overhead") || id.StartsWith("heavy")) return "head"; if (id == "kick") return "legs"; if (id.Contains("thrust") || id == "riposte") return "torso"; return id == "light_left" || id == "light_right" ? "arms" : "torso"; }
    static string DirectionOf(MoveDef m) { if (m == null) return "front"; if (m.id.EndsWith("left")) return "left"; if (m.id.EndsWith("right")) return "right"; return "front"; }
    static void Fire(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        ContactRelay.Raise(a, d, type, heavy, perfect);
        ContactRelay.RaiseEvent(new CombatEvent { type = type, actor = a, target = d, moveId = FM(a) != null ? FM(a).id : "" /* BOW v2 */, heavy = heavy, perfect = perfect, counter = lastCounter, rear = lastRear, location = LocationOf(FM(a), d), direction = DirectionOf(FM(a)), weight = HitReactionTable.Last != null ? HitReactionTable.Last.weight : "", hitLocation = HitReactionTable.Last != null ? HitReactionTable.Last.location : "", hitDirection = HitReactionTable.Last != null ? HitReactionTable.Last.direction : "", point = HitAnchor.Point(a, d) /* KITHIT v1 */, tick = TickCount });
    }
}

// V3 CombatEvent (minimal): what a contact carries for audio/blood/camera/hit-reaction consumers
public struct CombatEvent { public string type, moveId, location, direction, weight, hitLocation, hitDirection;   // location / direction = the attacker's swing (camera, blood edge use them); hit* = P2 victim-frame result ("" when not a clean hit)
    public DuelFighter actor, target; public bool heavy, perfect, counter, rear; public Vector3 point; public int tick; }
public static class ContactRelay
{
    public static event System.Action<CombatEvent> OnEvent;
    public static void RaiseEvent(CombatEvent e) { if (OnEvent != null) OnEvent(e); }
    public static event System.Action<DuelFighter, DuelFighter, string, bool, bool> OnContact;
    public static void Raise(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect) { if (OnContact != null) OnContact(a, d, type, heavy, perfect); }
    // V3 CombatEvent extras for audio/presentation: AttackStarted (move), AttackMissed, ActionStarted (roll/backstep), Killed (last move)
    public static event System.Action<DuelFighter, MoveDef> OnAttackStarted, OnAttackMissed, OnKilled;
    public static event System.Action<DuelFighter, string> OnAction;
    public static void AttackStarted(DuelFighter f, MoveDef m) { if (OnAttackStarted != null) OnAttackStarted(f, m); }
    public static void AttackMissed(DuelFighter f, MoveDef m) { if (OnAttackMissed != null) OnAttackMissed(f, m); }
    // KILLBUG v1: one subscriber that throws (RagdollDeath on a zone clone) no longer skips the others nor the tail of DuelFighter.Die(); logged, same order, same calls when nothing throws
    public static void Killed(DuelFighter f, MoveDef m)
    {
        if (OnKilled == null) return;
        foreach (var d in OnKilled.GetInvocationList())
        {
            try { ((System.Action<DuelFighter, MoveDef>)d)(f, m); }
            catch (System.Exception ex) { Debug.LogWarning("[killbug] OnKilled subscriber " + d.Method.DeclaringType + "." + d.Method.Name + " threw on " + (f != null ? f.name : "null") + ": " + ex); }
        }
    }
    public static void Action(DuelFighter f, string a) { if (OnAction != null) OnAction(f, a); }
    // Dodge detail for FX/audio/camera consumers (dodge.json): kind = "roll" | "backstep" | "sidestep", dir = world-space travel direction (unit, y = 0)
    public static event System.Action<DuelFighter, string, Vector3> OnDodge;
    public static void Dodge(DuelFighter f, string kind, Vector3 dir) { if (OnDodge != null) OnDodge(f, kind, dir); }
}








