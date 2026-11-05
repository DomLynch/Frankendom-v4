using System;
using System.Collections.Generic;
using UnityEngine;

// Port of Frankendom V3 src/fight/ai-read-rows.ts + the Habits / readOpponent half of ai.ts, plus AiWorld: the committed-state observer that gives the
// brain V3's view of a duel (phase, age, elapsed, landed, rolls/steps) from the public DuelFighter state and the ContactRelay events.
// The AI reads only what a player could see: both fighters' committed sim state, never the other side's pending input buffer.

[Serializable]
public class AiReadRows
{
    public float feint = 0.16666667f, after = 2, parry = 0.5f, guardTicks = 180, guardShare = 0.45f, roll = 0.4f, swings = 11, lightShare = 0.7f, baitHold = 12;
    public float parryBoost = 2, parryCap = 0.85f, chargeBoost = 0.4f, kickBoost = 0.3f, anticipate = 8, baitShare = 0.7f, parkShare = 0.5f, lateNotice = 6;
    public float holdTicks = 6, latch = 600, plus = 2, decay = 0, pokerShare = 0.5f, kickerShare = 0.5f, spammerLapseShare = 0.5f, guardedKickBase = 0.5f;
}

// V3 AiProfile: the nine brain knobs (from a DifficultyRow) plus the fight-identity knobs. A value of -1 on read / anticipate / tellReaction means "absent"
// (V3 `undefined`: no roll is drawn and the default applies).
[Serializable]
public class AiProfile
{
    public float reaction, accuracy, parry, dodge, aggression, pressure, discipline, lapse;
    public float read = -1, feint, guard = 1, disengage, circle, step, interrupt, kick, dash, stab;
    public float anticipate = -1, tellReaction = -1, spamRun, spamBoth, braceHeavy;
    public bool softNotice;
    public string tactic = "";

    public AiProfile Clone() { return (AiProfile)MemberwiseClone(); }

    // V3 difficulty.ts: a difficulty row overlays exactly the nine knobs on the opponent's base profile.
    public static AiProfile FromRow(DifficultyRow r, AiProfile baseProfile)
    {
        var p = baseProfile.Clone();
        p.reaction = r.reaction; p.accuracy = r.accuracy; p.parry = r.parry; p.dodge = r.dodge; p.aggression = r.aggression;
        p.pressure = r.pressure; p.discipline = r.discipline; p.lapse = r.lapse; p.read = r.read;
        return p;
    }
}

// What the opponent has done this match, counted from committed state edges only.
public class Habits
{
    public int ticks, guard, parries, rolls, steps, lights, heavies, thrusts, kicks, attacks, parks;
    public int latch, hold, run;
}

public struct Reads { public bool parryHappy, turtle, roller, stepper, spammer, parker, poker, kicker; }

public static class AiRead
{
    // V3 readOpponent.
    public static Reads Read(Habits h, AiProfile p, AiReadRows R)
    {
        var r = new Reads();
        float swings = h.lights + h.heavies + h.thrusts;
        bool oldSpam = swings >= R.swings && h.lights / swings >= R.lightShare;
        // spamRun: read a masher EARLY (consecutive lights with no defence that met a blow); the gate replaces the old read unless spamBoth
        bool spam = p.spamRun > 0f
            ? (h.run >= p.spamRun + (h.ticks < h.latch ? R.plus : 0f)) || (p.spamBoth > 0f && oldSpam)
            : oldSpam;
        r.parryHappy = h.attacks >= R.after && h.parries / (float)h.attacks >= R.parry;
        r.turtle = h.ticks >= R.guardTicks && h.guard / (float)h.ticks >= R.guardShare;
        r.roller = h.attacks >= R.after && h.rolls / (float)h.attacks >= R.roll;
        r.stepper = h.attacks >= R.after && h.steps / (float)h.attacks >= R.roll;
        r.spammer = spam;
        r.parker = swings >= R.after && h.parks / swings >= R.parkShare;
        r.poker = swings >= R.after && h.thrusts / swings >= R.pokerShare;
        r.kicker = h.kicks >= R.after && h.kicks / (swings + h.kicks) >= R.kickerShare;
        return r;
    }
}

// V3 phase names mapped from FState.
public enum Ph { Ready, Guard, Attack, Hurt, Roll, Backstep, Other, Dead }

// Per-fighter facts the public DuelFighter does not expose (chained flag, landed, attack start tick, last action, riposte window).
public class FTrack
{
    public int serial, windupTot, activeTot, actionTick, riposteUntil, parkSerial = -1;
    public int startTick = -100000;
    public bool chained, landed;
    public string actionKind = "";
}

public static class AiWorld
{
    static readonly Dictionary<DuelFighter, FTrack> tracks = new Dictionary<DuelFighter, FTrack>();
    static readonly List<AIBrain> brains = new List<AIBrain>();
    static bool subscribed;

    public static FTrack Track(DuelFighter f)
    {
        FTrack t;
        if (!tracks.TryGetValue(f, out t)) { t = new FTrack(); tracks[f] = t; }
        return t;
    }

    public static void Register(AIBrain b)
    {
        if (!brains.Contains(b)) brains.Add(b);
        if (!subscribed)
        {
            ContactRelay.OnAttackStarted += AttackStarted;
            ContactRelay.OnAttackMissed += AttackMissed;
            ContactRelay.OnAction += ActionDone;
            ContactRelay.OnEvent += Contact;
            subscribed = true;
        }
    }
    public static void Unregister(AIBrain b)
    {
        brains.Remove(b);
        if (brains.Count == 0 && subscribed)
        {
            ContactRelay.OnAttackStarted -= AttackStarted;
            ContactRelay.OnAttackMissed -= AttackMissed;
            ContactRelay.OnAction -= ActionDone;
            ContactRelay.OnEvent -= Contact;
            subscribed = false;
            tracks.Clear();
        }
    }

    public static Ph Phase(DuelFighter f)
    {
        switch (f.state)
        {
            case FState.Loco: return Ph.Ready;
            case FState.Guard: return Ph.Guard;
            case FState.Windup: case FState.Active: case FState.Recovery: return Ph.Attack;
            case FState.Stagger: case FState.Down: return Ph.Hurt;
            case FState.Roll: return Track(f).actionKind == "backstep" ? Ph.Backstep : Ph.Roll;
            case FState.Dead: return Ph.Dead;
            default: return Ph.Other;
        }
    }

    // V3 `age` of an attack: the animation clock (rewound to the chamber while a charge is parked).
    public static int AttackAge(DuelFighter f, FTrack t)
    {
        switch (f.state)
        {
            case FState.Windup: return f.t;
            case FState.Active: return t.windupTot + f.t;
            case FState.Recovery: return t.windupTot + t.activeTot + f.t;
            default: return f.t;
        }
    }

    // ---- event handlers (fire inside the 60 Hz tick) ----
    static void AttackStarted(DuelFighter f, MoveDef m)
    {
        var t = Track(f);
        t.serial++; t.startTick = Duel.TickCount; t.landed = false;
        int w = f.state == FState.Windup ? f.WindupLeft : m.windup;
        t.windupTot = w;
        t.chained = m.hasChained && m.chainedWindup != m.windup && w == m.chainedWindup;
        t.activeTot = t.chained ? m.chainedActive : m.active;
        for (int i = 0; i < brains.Count; i++)
        {
            var b = brains[i]; if (b.df == null) continue;
            if (b.df == f) b.NoteOwnAttack(m);
            else if (b.df.opponent == f) b.NoteFoeAttack(m, t);
        }
    }
    static void AttackMissed(DuelFighter f, MoveDef m)
    {
        for (int i = 0; i < brains.Count; i++)
        {
            var b = brains[i]; if (b.df != f || f.opponent == null) continue;
            // a swing that finds the foe mid-roll or mid-backstep is a defence that met a blow (V3 Dodged / AttackMissed vs backstep)
            if (f.opponent.IFrames || Phase(f.opponent) == Ph.Backstep) b.NoteDefence();
        }
    }
    static void ActionDone(DuelFighter f, string a)
    {
        var t = Track(f); t.actionKind = a; t.actionTick = Duel.TickCount;
        for (int i = 0; i < brains.Count; i++)
        {
            var b = brains[i]; if (b.df != null && b.df.opponent == f) b.NoteFoeAction(a);
        }
    }
    static void Contact(CombatEvent e)
    {
        if (e.actor == null || e.target == null) return;
        bool contact = e.type == "Hit" || e.type == "Blocked" || e.type == "Parried" || e.type == "GuardBroken";
        if (contact) Track(e.actor).landed = true;
        if (e.type == "Parried") Track(e.target).riposteUntil = Duel.TickCount + StaminaTable.RiposteWindow((int)e.target.Rule("parryStun", AiRows.I.unity.rules.parryStun));   // lanePARRY: parry.json riposteWindowTicks
        for (int i = 0; i < brains.Count; i++)
        {
            var b = brains[i]; if (b.df == null) continue;
            if (b.df == e.actor && e.type == "Hit") b.NoteOwnHit();
            if (b.df.opponent == e.target && (e.type == "Blocked" || e.type == "Parried")) b.NoteDefence();
        }
    }
}
