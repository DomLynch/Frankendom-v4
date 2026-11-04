using System.Collections.Generic;
using UnityEngine;

// Fixed 60 Hz deterministic fight clock + hit resolution (ported from V3 duel.ts stepDuel rules: guard arc, parry window, posture, rear/counter bonuses).
public class Duel : MonoBehaviour
{
    static readonly List<DuelFighter> fighters = new List<DuelFighter>();
    public static int TickCount;
    public static bool Frozen;   // FN1: true while a finisher scene runs: no brain decides, no new hit resolves (reset on scene load)
    float acc;
    public static void Register(DuelFighter f) { if (!fighters.Contains(f)) fighters.Add(f); }
    public static void Unregister(DuelFighter f) { fighters.Remove(f); }

    void Awake() { TickCount = 0; Frozen = false; }
    void OnDestroy() { fighters.Clear(); Frozen = false; }

    void Update()
    {
        acc += Mathf.Min(PresentationClock.SimDelta(Time.deltaTime), 0.1f);   // hit-stop: no accumulation while the presentation clock is frozen
        while (acc >= 1f / 60f)
        {
            acc -= 1f / 60f; TickCount++;
            for (int i = 0; i < fighters.Count; i++) fighters[i].Tick();
            if (PresentationClock.Frozen) { acc = 0f; break; }   // a contact this tick requested a stop (V3 main.ts: accumulator = 0)
        }
    }

    static float Angle(Vector3 fwd, Vector3 to) { fwd.y = 0; to.y = 0; return Vector3.Angle(fwd, to); }

    // returns true when the strike made contact (hit, block, parry): the move then stops checking
    public static bool Resolve(DuelFighter a, DuelFighter d, MoveDef m, float chargeMul)
    {
        if (d.Dead || Frozen) return false;
        Vector3 ad = d.transform.position - a.transform.position; ad.y = 0f;
        float dist = ad.magnitude;
        float arc = m.id == "kick" ? a.Rule("kickArc", Mathf.PI / 4f) * Mathf.Rad2Deg : 70f;
        if (dist > m.reach * a.scale + a.radius + d.radius + 0.45f) return false;
        if (Angle(a.transform.forward, ad) > arc) return false;
        if (d.IFrames) return false;   // rolled through it
        HitReactionTable.Last = null;   // P2: set again below only for a clean hit

        bool heavy = m.charges || m.damage >= 18f;
        string evt;
        Vector3 away = ad.normalized;
        bool defGuard = d.IsGuarding && Angle(d.transform.forward, -ad) <= a.Rule("guardArc", Mathf.PI / 3f) * Mathf.Rad2Deg;

        if (defGuard && d.InParryWindow && m.parryable && (!heavy || !StaminaTable.Parry().enabled || StaminaTable.Parry().parryHeavies))
        {
            d.ParrySuccess();
            a.Stagger(StaminaTable.ParryStagger(heavy, (int)a.Rule("parryStun", 90)), "stagger_loop"); a.AddPosture(PostureTable.ParryAttackerGain(StaminaTable.Parry().enabled ? StaminaTable.Parry().attackerPosture : a.Rule("posture.parry", 25))); if (PostureTable.Get().enabled) d.RelievePosture(PostureTable.Get().parry.relief); bool pbk = a.TryPostureBreak(d);
            Fire(a, d, "Parried", heavy, false); if (pbk) Fire(d, a, "GuardBroken", true, false); return true;
        }
        if (defGuard && m.id == "kick" && m.hasVsGuard)
        {
            d.SpendStamina(m.vsGuardStaminaDamage); d.GuardBreakStun((int)m.vsGuardStagger); if (PostureTable.Get().enabled) { d.AddPosture(PostureTable.Get().block.kick); d.TryPostureBreak(a); }
            Fire(a, d, "GuardBroken", true, false); return true;
        }
        if (defGuard)
        {
            d.SpendStamina(StaminaTable.BlockCost(m, heavy, d.Exhausted, a.Rule("blockCost", 25f)));
            d.AddPosture(PostureTable.BlockGain(m, heavy)); if (PostureTable.Get().enabled && PostureTable.Get().block.attackerBlocked > 0f) a.AddPosture(PostureTable.Get().block.attackerBlocked); bool pbb = d.TryPostureBreak(a);
            d.health = Mathf.Max(1f, d.health - m.damage * StaminaTable.ChipFrac(m, d.Exhausted) * (STSteer.Active ? STSteer.DamageMul(a, d) : 1f));
            if (pbb) { Fire(a, d, "GuardBroken", true, false); }
            else if (StaminaTable.BreaksGuard(d.stamina, m, heavy))
            {
                d.GuardBreakStun(); Fire(a, d, "GuardBroken", true, false);
            }
            else { d.NoteBlock(); d.BlockHit(heavy); d.SetKnock(away * m.knockback * a.scale * 0.5f); Fire(a, d, "Blocked", heavy, d.InParryWindow); }
            return true;
        }

        // clean hit
        bool rear = Angle(d.transform.forward, -ad) > 90f;                 // attacker behind the defender
        bool counter = d.InWindup; lastRear = rear; lastCounter = counter;
        var hr = HitReactionTable.Pick(a, d, m, heavy, counter, rear, -1f);   // P2: location / direction / weight + the animator state (null = legacy three states)
        HitReactionTable.Last = hr;
        float dmg = m.damage * a.DamageScaleNow * chargeMul * (rear ? a.Rule("rear.damage", 1.25f) : 1f) * (counter ? a.Rule("counter.damage", 1.25f) : 1f);
        if (STSteer.Active) dmg *= STSteer.DamageMul(a, d); bool db = PostureTable.Get().enabled && d.DeathblowOpen; if (db) dmg = PostureTable.DeathblowDamage(dmg, d);   // P6 deathblow: the next hit on a posture-broken fighter   // steer stance modifiers (dealt / taken, Trickster per-hit roll)
        if (FightBalance.Active) dmg *= FightBalance.HitMul(a, d, m);   // B1: fight_balance.json
        float stg = m.stagger * chargeMul * (rear ? a.Rule("rear.stagger", 1.25f) : 1f) * (counter ? a.Rule("counter.stagger", 1.5f) : 1f);
        d.health -= dmg;
        if (db) d.ConsumeDeathblow(a); else d.AddPosture(PostureTable.HitGain(m) * (rear ? a.Rule("rear.posture", 1.5f) : 1f));
        d.SetKnock(away * m.knockback * a.scale * 0.9f);
        if (db && d.health > 0f && RTFinisher.DeathblowFinishes && RTFinisher.TryBegin(a, d, m, true)) return true;   // V61B: a posture-break deathblow ALWAYS starts a finisher (finishers.json rt.deathblowFinishes), lethal or not; the scene's Die() is the kill
        if (d.health <= 0f)
        {
            if (RTFinisher.TryBegin(a, d, m, db)) return true;   // FN1: killing-blow finisher scene (finishers.json enableFinishers, default false); the plain death below is the fallback
            d.Die(); Fire(a, d, "Hit", heavy, false); return true;
        }
        if (PostureTable.Get().enabled) { if (d.TryPostureBreak(a)) { Fire(a, d, "GuardBroken", true, false); return true; } }   // P6: posture break = long stagger + deathblow window (posture.json)
        else if (d.posture >= a.Rule("posture.max", 100f)) { d.KnockDown(); Fire(a, d, "GuardBroken", true, false); return true; }
        if (hr != null && hr.knockdown) { d.KnockDown(); Fire(a, d, "Hit", heavy, false); return true; }   // data: allowKnockdown + a knockdown row (sim change, default off)
        string clip = hr != null ? hr.state : (heavy ? "hit_heavy" : (rear ? "hit_light_back" : "hit_light_front"));
        d.Stagger(Mathf.Max(db ? PostureTable.Get().deathblow.staggerTicks : 0, (int)(stg * (hr != null ? hr.staggerScale : 1f))), clip);
        Fire(a, d, "Hit", heavy, false);
        return true;
    }

    static bool lastRear, lastCounter;
    // hit zone from the move (V3 hit-react zones): overhead/heavy -> head, thrust -> torso, kick -> legs, side cuts -> arms
    static string LocationOf(MoveDef m, DuelFighter d) { if (m == null) return "torso"; string id = m.id; if (id.Contains("overhead") || id.StartsWith("heavy")) return "head"; if (id == "kick") return "legs"; if (id.Contains("thrust") || id == "riposte") return "torso"; return id == "light_left" || id == "light_right" ? "arms" : "torso"; }
    static string DirectionOf(MoveDef m) { if (m == null) return "front"; if (m.id.EndsWith("left")) return "left"; if (m.id.EndsWith("right")) return "right"; return "front"; }
    static void Fire(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        ContactRelay.Raise(a, d, type, heavy, perfect);
        ContactRelay.RaiseEvent(new CombatEvent { type = type, actor = a, target = d, moveId = a.CurrentMove != null ? a.CurrentMove.id : "", heavy = heavy, perfect = perfect, counter = lastCounter, rear = lastRear, location = LocationOf(a.CurrentMove, d), direction = DirectionOf(a.CurrentMove), weight = HitReactionTable.Last != null ? HitReactionTable.Last.weight : "", hitLocation = HitReactionTable.Last != null ? HitReactionTable.Last.location : "", hitDirection = HitReactionTable.Last != null ? HitReactionTable.Last.direction : "", point = d.transform.position + Vector3.up * (1.1f * Mathf.Max(1f, d.scale)), tick = TickCount });
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
    public static void Killed(DuelFighter f, MoveDef m) { if (OnKilled != null) OnKilled(f, m); }
    public static void Action(DuelFighter f, string a) { if (OnAction != null) OnAction(f, a); }
    // Dodge detail for FX/audio/camera consumers (dodge.json): kind = "roll" | "backstep" | "sidestep", dir = world-space travel direction (unit, y = 0)
    public static event System.Action<DuelFighter, string, Vector3> OnDodge;
    public static void Dodge(DuelFighter f, string kind, Vector3 dir) { if (OnDodge != null) OnDodge(f, kind, dir); }
}








