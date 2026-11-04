using System.Collections.Generic;
using UnityEngine;

// Fixed 60 Hz deterministic fight clock + hit resolution (ported from V3 duel.ts stepDuel rules: guard arc, parry window, posture, rear/counter bonuses).
public class Duel : MonoBehaviour
{
    static readonly List<DuelFighter> fighters = new List<DuelFighter>();
    public static int TickCount;
    float acc;
    public static void Register(DuelFighter f) { if (!fighters.Contains(f)) fighters.Add(f); }
    public static void Unregister(DuelFighter f) { fighters.Remove(f); }

    void Awake() { TickCount = 0; }
    void OnDestroy() { fighters.Clear(); }

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
        if (d.Dead) return false;
        Vector3 ad = d.transform.position - a.transform.position; ad.y = 0f;
        float dist = ad.magnitude;
        float arc = m.id == "kick" ? a.Rule("kickArc", Mathf.PI / 4f) * Mathf.Rad2Deg : 70f;
        if (dist > m.reach * a.scale + a.radius + d.radius + 0.45f) return false;
        if (Angle(a.transform.forward, ad) > arc) return false;
        if (d.IFrames) return false;   // rolled through it

        bool heavy = m.charges || m.damage >= 18f;
        string evt;
        Vector3 away = ad.normalized;
        bool defGuard = d.IsGuarding && Angle(d.transform.forward, -ad) <= a.Rule("guardArc", Mathf.PI / 3f) * Mathf.Rad2Deg;

        if (defGuard && d.InParryWindow && m.parryable)
        {
            d.ParrySuccess();
            a.Stagger((int)a.Rule("parryStun", 90), "stagger_loop"); a.AddPosture(a.Rule("posture.parry", 25));
            Fire(a, d, "Parried", heavy, false); return true;
        }
        if (defGuard && m.id == "kick" && m.hasVsGuard)
        {
            d.SpendStamina(m.vsGuardStaminaDamage); d.Stagger((int)m.vsGuardStagger, "stagger_loop");
            Fire(a, d, "GuardBroken", true, false); return true;
        }
        if (defGuard)
        {
            d.SpendStamina(a.Rule("blockCost", 25f) * (heavy ? 1.2f : 1f) + m.staminaDamage * 0.3f);
            d.AddPosture(m.posture * 0.5f);
            d.health = Mathf.Max(1f, d.health - m.damage * m.chip * (STSteer.Active ? STSteer.DamageMul(a, d) : 1f));
            if (d.stamina <= 0f || m.breaksGuard)
            {
                d.stamina = 0f; d.Stagger(60, "stagger_loop"); Fire(a, d, "GuardBroken", true, false);
            }
            else { d.BlockHit(heavy); d.SetKnock(away * m.knockback * a.scale * 0.5f); Fire(a, d, "Blocked", heavy, d.InParryWindow); }
            return true;
        }

        // clean hit
        bool rear = Angle(d.transform.forward, -ad) > 90f;                 // attacker behind the defender
        bool counter = d.InWindup; lastRear = rear; lastCounter = counter;
        float dmg = m.damage * a.damageScale * chargeMul * (rear ? a.Rule("rear.damage", 1.25f) : 1f) * (counter ? a.Rule("counter.damage", 1.25f) : 1f);
        if (STSteer.Active) dmg *= STSteer.DamageMul(a, d);   // steer stance modifiers (dealt / taken, Trickster per-hit roll)
        float stg = m.stagger * chargeMul * (rear ? a.Rule("rear.stagger", 1.25f) : 1f) * (counter ? a.Rule("counter.stagger", 1.5f) : 1f);
        d.health -= dmg;
        d.AddPosture(m.posture * (rear ? a.Rule("rear.posture", 1.5f) : 1f));
        d.SetKnock(away * m.knockback * a.scale * 0.9f);
        if (d.health <= 0f) { d.Die(); Fire(a, d, "Hit", heavy, false); return true; }
        if (d.posture >= a.Rule("posture.max", 100f)) { d.KnockDown(); Fire(a, d, "GuardBroken", true, false); return true; }
        string clip = heavy ? "hit_heavy" : (rear ? "hit_light_back" : "hit_light_front");
        d.Stagger((int)stg, clip);
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
        ContactRelay.RaiseEvent(new CombatEvent { type = type, actor = a, target = d, moveId = a.CurrentMove != null ? a.CurrentMove.id : "", heavy = heavy, perfect = perfect, counter = lastCounter, rear = lastRear, location = LocationOf(a.CurrentMove, d), direction = DirectionOf(a.CurrentMove), point = d.transform.position + Vector3.up * (1.1f * Mathf.Max(1f, d.scale)), tick = TickCount });
    }
}

// V3 CombatEvent (minimal): what a contact carries for audio/blood/camera/hit-reaction consumers
public struct CombatEvent { public string type, moveId, location, direction; public DuelFighter actor, target; public bool heavy, perfect, counter, rear; public Vector3 point; public int tick; }
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








