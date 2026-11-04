using System;
using System.Collections.Generic;

// Offline self-test for ADRules. Engine-free: ADRulesSelfTest.Run(loader) returns the failures (empty list = all pass).
// In Unity: ADRulesSelfTest.Run() (UNITY only) loads duel_rules.json fresh for every case; ADDuelController runs it with -alphaselftest.
public static class ADRulesSelfTest
{
    class ScriptRng : IADRng
    {
        readonly Queue<double> q = new Queue<double>(); readonly double fill;
        public ScriptRng(double fill, params double[] first) { this.fill = fill; foreach (var d in first) q.Enqueue(d); }
        public double Next01() { return q.Count > 0 ? q.Dequeue() : fill; }
    }

    static Func<ADRulesData> load;
    static List<string> fails;
    static int checks;

    static void Check(bool ok, string what) { checks++; if (!ok) fails.Add(what); }

    // Fresh rules with deterministic knobs: no variance, no crit, no riposte unless asked; rng returns 0.5 unless scripted.
    static ADRules Make(bool riposte = false, double crit = 0.0, ScriptRng rng = null)
    {
        var d = load();
        d.rules.damageVariance = 0f; d.crit.chance = (float)crit; d.rules.riposteChance = riposte ? 1f : 0f;
        var r = new ADRules(d, rng ?? new ScriptRng(0.5));
        r.StartDuel();
        return r;
    }
    static ADPlan P(ADMove m1, ADStance s = ADStance.Neutral) { return new ADPlan(s, m1, ADMove.Guard, ADMove.Guard); }
    static ADBeat Beat0(ADRules r, ADMove a, ADMove b, ADStance sa = ADStance.Neutral, ADStance sb = ADStance.Neutral)
    {
        var rr = r.ResolveRound(P(a, sa), P(b, sb)); return rr.beats[0];
    }

    public static List<string> Run(Func<ADRulesData> loader)
    {
        load = loader; fails = new List<string>(); checks = 0;
        ADBeat b; ADRules r;

        // --- counters ---
        b = Beat0(Make(), ADMove.Guard, ADMove.Light);
        Check(b.outA == ADOutcome.Parried && b.outB == ADOutcome.Blocked && b.dmgToA == 0 && b.dmgToB == 0, "guard parries light");
        b = Beat0(Make(), ADMove.Slash, ADMove.Guard);
        Check(b.outA == ADOutcome.Blocked && b.outB == ADOutcome.Parried && b.dmgToB == 0, "guard parries slash (mirrored)");
        b = Beat0(Make(riposte: true), ADMove.Guard, ADMove.Light);
        Check(b.outA == ADOutcome.Riposte && b.riposteA && b.dmgToB == 8 && b.dmgToA == 0, "parry -> riposte 8 dmg (got " + b.dmgToB + ")");
        b = Beat0(Make(), ADMove.Heavy, ADMove.Guard);
        Check(b.outA == ADOutcome.Hit && b.outB == ADOutcome.GuardBreak && b.dmgToB == 20 && b.staggerB, "heavy breaks guard, 20 dmg, guard staggered");
        b = Beat0(Make(), ADMove.Guard, ADMove.Kick);
        Check(b.outA == ADOutcome.Staggered && b.outB == ADOutcome.Hit && b.dmgToA == 5 && b.staggerA, "kick staggers guard");
        b = Beat0(Make(), ADMove.Roll, ADMove.Heavy);
        Check(b.outA == ADOutcome.Dodged && b.outB == ADOutcome.Whiff && b.dmgToA == 0 && b.staggerB, "roll dodges heavy, heavy user staggered (recovery)");
        b = Beat0(Make(), ADMove.Sidestep, ADMove.Stab);
        Check(b.outA == ADOutcome.Dodged && b.outB == ADOutcome.Whiff && b.dmgToA == 0, "sidestep dodges stab");
        b = Beat0(Make(), ADMove.Roll, ADMove.Stab);
        Check(b.outA == ADOutcome.Dodged && b.dmgToA == 0, "roll dodges stab");
        b = Beat0(Make(), ADMove.Sidestep, ADMove.Slash);
        Check(b.outA == ADOutcome.Struck && b.outB == ADOutcome.Hit && b.dmgToA == 10, "slash catches sidestep");
        b = Beat0(Make(), ADMove.Roll, ADMove.Slash);
        Check(b.outA == ADOutcome.Dodged && b.dmgToA == 0, "roll still dodges slash");
        b = Beat0(Make(), ADMove.Stab, ADMove.Heavy);
        Check(b.outA == ADOutcome.Hit && b.outB == ADOutcome.Interrupted && b.dmgToB == 9 && b.dmgToA == 0, "stab interrupts heavy");
        b = Beat0(Make(), ADMove.Heavy, ADMove.Stab);
        Check(b.outB == ADOutcome.Hit && b.outA == ADOutcome.Interrupted && b.dmgToA == 9 && b.dmgToB == 0, "stab interrupts heavy (mirrored)");
        b = Beat0(Make(), ADMove.Guard, ADMove.Roll);
        Check(b.outA == ADOutcome.Whiff && b.outB == ADOutcome.Whiff, "defensive vs defensive whiffs");

        // --- speed tiers, trade, clash ---
        b = Beat0(Make(), ADMove.Light, ADMove.Slash);
        Check(b.firstA && !b.tie && b.dmgToB == 7 && b.dmgToA == 10, "light (tier 2) lands before slash (3), both trade");
        b = Beat0(Make(), ADMove.Slash, ADMove.Stab);
        Check(!b.firstA, "stab (tier 1) is first vs slash");
        b = Beat0(Make(), ADMove.Slash, ADMove.Kick);
        Check(b.tie && b.outA == ADOutcome.Clash && b.outB == ADOutcome.Clash && b.dmgToA == 0 && b.dmgToB == 0 && b.staggerA && b.staggerB, "equal tiers clash, both stagger, no damage");
        r = Make(); r.Dracula.speedTierPenalty = 1.5f;     // leg wound: light 2 -> 3.5, slower than slash 3
        b = Beat0(r, ADMove.Light, ADMove.Slash);
        Check(!b.firstA, "leg wound slows the tier");
        r = Make(); r.Dracula.hp = 1;
        var rr = r.ResolveRound(P(ADMove.Slash), P(ADMove.Light));
        b = rr.beats[0];
        Check(b.killed && b.victim == ADSide.Dracula && b.outB == ADOutcome.Killed && b.outA == ADOutcome.Interrupted && b.dmgToB == 0, "faster hit kills first; slower attack is voided");
        Check(rr.beats.Count == 1 && r.Over && r.Winner == ADSide.Pitborn, "kill ends round and duel, winner Pitborn");

        // --- stances ---
        b = Beat0(Make(), ADMove.Light, ADMove.Roll, ADStance.Aggressive);
        Check(b.dmgToB == 9, "aggressive +25% dealt (7 -> 9), got " + b.dmgToB);
        b = Beat0(Make(), ADMove.Light, ADMove.Roll, ADStance.Neutral, ADStance.Defensive);
        Check(b.dmgToB == 5, "defensive -25% taken (7 -> 5), got " + b.dmgToB);
        b = Beat0(Make(), ADMove.Roll, ADMove.Light, ADStance.Aggressive);
        Check(b.dmgToA == 8, "aggressive +20% taken (7 -> 8), got " + b.dmgToA);
        b = Beat0(Make(), ADMove.Light, ADMove.Roll, ADStance.Defensive);
        Check(b.dmgToB == 6, "defensive -20% dealt (7 -> 6), got " + b.dmgToB);
        b = Beat0(Make(rng: new ScriptRng(0.5, 0.1)), ADMove.Light, ADMove.Roll, ADStance.Trickster);
        Check(b.dmgToB == 9, "trickster 25% bucket: +30% dealt (7 -> 9), got " + b.dmgToB);
        b = Beat0(Make(rng: new ScriptRng(0.5, 0.3)), ADMove.Roll, ADMove.Light, ADStance.Trickster);
        Check(b.dmgToA == 9, "trickster 25% bucket: +30% taken (7 -> 9), got " + b.dmgToA);
        b = Beat0(Make(rng: new ScriptRng(0.5, 0.9)), ADMove.Light, ADMove.Roll, ADStance.Trickster);
        Check(b.dmgToB == 7, "trickster 50% bucket: nothing, got " + b.dmgToB);

        // --- crit ---
        b = Beat0(Make(crit: 0.08, rng: new ScriptRng(0.5, 0.5, 0.0)), ADMove.Light, ADMove.Roll);   // variance draw, then crit draw
        Check(b.crit && b.critA && b.dmgToB == 12, "crit x1.75 (7 -> 12), got " + b.dmgToB);
        b = Beat0(Make(crit: 0.08), ADMove.Light, ADMove.Roll);
        Check(!b.crit && b.dmgToB == 7, "no crit at roll 0.5");

        // --- kill / win at 0 HP only ---
        r = Make(); r.Pitborn.hp = 5; bool killEvent = false; r.OnKill += (w, kb) => killEvent = w == ADSide.Dracula && kb.killed;
        rr = r.ResolveRound(P(ADMove.Light), P(ADMove.Roll));
        b = rr.beats[0];
        Check(b.killed && b.victim == ADSide.Pitborn && b.dmgToB == 5 && b.outA == ADOutcome.Killed && r.Over && r.Winner == ADSide.Dracula && killEvent && rr.beats.Count == 1, "kill at 0 HP: winner Dracula, damage clamped to hp, OnKill fired");
        r = Make(); r.ResolveRound(new ADPlan(ADStance.Neutral, ADMove.Light, ADMove.Light, ADMove.Light), new ADPlan(ADStance.Neutral, ADMove.Roll, ADMove.Roll, ADMove.Roll));
        Check(!r.Over && r.Pitborn.hp == 120 - 21, "no kill while HP > 0 (no ring-out or stamina win)");

        // --- special: once per fight, drain heals, slam knocks down ---
        r = Make(); r.Dracula.hp = 60;
        rr = r.ResolveRound(new ADPlan(ADStance.Neutral, ADMove.Special, ADMove.Special, ADMove.Guard), P(ADMove.Sidestep));
        Check(rr.planA.moves[1] == ADMove.Guard, "second special in one plan is replaced");
        Check(rr.beats[0].outA == ADOutcome.Hit && rr.beats[0].outB == ADOutcome.Struck, "special catches the sidestep");
        Check(rr.beats[0].dmgToB == 16 && rr.beats[0].healA == 10 && r.Dracula.hp == 70, "blood drain 16 dmg, heals 60% (10), got heal " + rr.beats[0].healA);
        Check(r.Dracula.specialUsed, "special spent");
        var p2 = r.Sanitize(new ADPlan(ADStance.Neutral, ADMove.Special, ADMove.Light, ADMove.Light), r.Dracula);
        Check(p2.moves[0] == ADMove.Guard, "spent special becomes guard");
        r = Make();
        rr = r.ResolveRound(new ADPlan(ADStance.Neutral, ADMove.Light, ADMove.Light, ADMove.Light), new ADPlan(ADStance.Neutral, ADMove.Special, ADMove.Guard, ADMove.Guard));
        Check(rr.beats[0].knockdownA && rr.beats[0].dmgToA == 26 && rr.beats[0].dmgToB == 7, "light vs slam: both land (light first), Dracula knocked down");
        Check(rr.beats[1].lostA && rr.beats[1].outA == ADOutcome.Staggered, "knocked-down fighter loses the next beat's move");

        // --- special definitions from the 'special' block (lane E): guardMode reduced, avoidedBy Roll, no crit, forced location, stagger ---
        b = Beat0(Make(), ADMove.Special, ADMove.Guard);
        Check(b.outA == ADOutcome.Hit && b.outB == ADOutcome.Struck && b.dmgToB == 8, "guard only halves the special (16 -> 8), no guard break, got " + b.dmgToB);
        b = Beat0(Make(), ADMove.Special, ADMove.Roll);
        Check(b.outB == ADOutcome.Dodged && b.outA == ADOutcome.Whiff && b.dmgToB == 0 && b.staggerA, "roll avoids the special; the user is staggered");
        b = Beat0(Make(), ADMove.Special, ADMove.Sidestep);
        Check(b.outA == ADOutcome.Hit && b.outB == ADOutcome.Struck && b.dmgToB == 16 && b.staggerB && b.locB == ADLoc.Torso, "sidestep does not avoid it; Dracula special: torso, victim staggered");
        b = Beat0(Make(), ADMove.Sidestep, ADMove.Special);
        Check(b.outB == ADOutcome.Hit && b.dmgToA == 26 && b.knockdownA, "pitborn slam catches the sidestep, knockdown");
        b = Beat0(Make(), ADMove.Roll, ADMove.Special);
        Check(b.outA == ADOutcome.Dodged && b.dmgToA == 0, "roll avoids the slam");
        b = Beat0(Make(crit: 1.0), ADMove.Special, ADMove.Sidestep);
        Check(b.crit, "special can crit when critAllowed (data)");
        { var d = load(); d.rules.damageVariance = 0f; d.crit.chance = 1f; d.special.dracula.critAllowed = false; r = new ADRules(d, new ScriptRng(0.5)); r.StartDuel(); b = Beat0(r, ADMove.Special, ADMove.Sidestep); }
        Check(!b.crit && b.dmgToB == 16, "critAllowed=false suppresses special crits");

        // --- wounds (default provider) ---
        r = Make(); var w = new ADBasicWounds(r.Cfg); var f = new ADFighter { hp = 10, hpMax = 10 };
        w.Apply(f, ADLoc.Leg, 8, false); w.Apply(f, ADLoc.Leg, 8, false); w.Apply(f, ADLoc.Arm, 8, false); w.Apply(f, ADLoc.Arm, 8, false);
        Check(Math.Abs(f.speedTierPenalty - 1f) < 0.001f && Math.Abs(w.DamageMul(f, ADMove.Heavy) - 0.7f) < 0.001f && w.DamageMul(f, ADMove.Light) == 1f, "leg wound slows, arm wound weakens heavy only");

        // --- state machine: blind submit, timer, repeat plan ---
        r = Make(); int plans = 0; r.OnPlan += n => plans++;
        Check(r.Phase == ADPhase.Plan && r.Round == 1, "duel starts in Plan");
        Check(r.Submit(ADSide.Dracula, P(ADMove.Light)) && r.Phase == ADPhase.Plan && r.HasSubmitted(ADSide.Dracula) && r.LastResult == null, "one plan in: still planning, nothing revealed");
        r.Tick(5f); Check(r.Phase == ADPhase.Plan, "timer not out at 5s");
        r.Tick(5.1f);
        Check(r.Phase == ADPhase.Resolve && r.LastResult != null && r.LastResult.timedOutB && !r.LastResult.timedOutA, "timeout: missing plan filled, round resolves");
        r.BeginReplay(); Check(r.Phase == ADPhase.Replay, "replay phase");
        r.EndRound(); Check(r.Phase == ADPhase.Result, "result phase");
        r.NextRound(); Check(r.Phase == ADPhase.Plan && r.Round == 2 && plans == 1, "next round plans again");
        r.Submit(ADSide.Dracula, new ADPlan(ADStance.Defensive, ADMove.Heavy, ADMove.Heavy, ADMove.Heavy)); r.Tick(11f);
        Check(r.History[1].planB.moves[0] == r.History[0].planB.moves[0], "last plan repeats on timeout");

        // --- determinism + full duel ends only by kill ---
        int hp1 = RunRandomDuel(777), hp2 = RunRandomDuel(777);
        Check(hp1 == hp2 && hp1 != int.MinValue, "same seed, same duel (" + hp1 + " vs " + hp2 + ")");

        fails.Insert(0, "ADRulesSelfTest: " + checks + " checks, " + fails.Count + " failed");
        return fails;
    }

    static int RunRandomDuel(int seed)
    {
        var d = load(); d.seed = seed;
        var r = new ADRules(d); r.StartDuel();
        var pick = new ADRng(seed + 1);
        int guard = 0;
        while (!r.Over && guard++ < 500)
        {
            ADPlan MakePlan() { return new ADPlan((ADStance)(int)(pick.Next01() * 4), (ADMove)(int)(pick.Next01() * 9), (ADMove)(int)(pick.Next01() * 9), (ADMove)(int)(pick.Next01() * 9)); }
            r.Submit(ADSide.Dracula, MakePlan()); r.Submit(ADSide.Pitborn, MakePlan());
            r.BeginReplay(); r.EndRound(); r.NextRound();
        }
        if (!r.Over) return int.MinValue;
        if (!(r.Dracula.hp <= 0 ^ r.Pitborn.hp <= 0)) return int.MinValue;
        return r.Round * 10000 + r.Dracula.hp * 100 + r.Pitborn.hp + (r.Winner == ADSide.Dracula ? 7 : 0);
    }

#if UNITY_5_3_OR_NEWER
    public static string Run()
    {
        var fl = Run(() => { var x = ADData.LoadRules(); x.rules.enableWounds = x.rules.enableSpecials = true; return x; });
        string s = string.Join("\n", fl.ToArray());
        if (fl.Count > 1) UnityEngine.Debug.LogError(s); else UnityEngine.Debug.Log(s);
        return s;
    }
#endif
}
