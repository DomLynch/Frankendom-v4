using System;
using System.Collections.Generic;
using System.Text;

// ALPHA STEP 1 rules engine (lane A). Pure C#, no UnityEngine. All numbers/matrix/stances come from ADRulesData (duel_rules.json).
// Round state machine: Plan (timer, blind, simultaneous) -> Resolve (3 beats computed at once, OnBeat per beat) -> Replay -> Result -> next Plan.
// Win only at 0 HP: the beat that kills ends the round and the duel (no ring-out, no stamina win).
//   var rules = new ADRules(data); rules.StartDuel();            // Phase = Plan, OnPlan(1)
//   rules.Submit(ADSide.Dracula, plan); rules.Submit(ADSide.Pitborn, plan);   // both in -> resolves immediately
//   rules.Tick(dt) every frame during Plan (timer; on timeout the last plan repeats)
//   playback consumes rules.LastResult.beats, then rules.BeginReplay(); rules.EndRound(); rules.NextRound();
public class ADRules
{
    public readonly ADRulesData Data;
    public IADRng Rng;
    public IADWounds Wounds;
    public ADFighter Dracula, Pitborn;
    public ADPhase Phase = ADPhase.Idle;
    public int Round;
    public float PlanTimeLeft;
    public bool Over;
    public ADSide Winner;
    public ADRoundResult LastResult;
    public List<ADRoundResult> History = new List<ADRoundResult>();

    public event Action<int> OnPlan;
    public event Action<ADBeat> OnBeat;
    public event Action<ADRoundResult> OnRoundEnd;
    public event Action<ADSide, ADBeat> OnKill;     // (winner, killing beat)

    class CRow
    {
        public bool[] a = new bool[9], b = new bool[9];
        public ADOutcome oa, ob; public float ah, bh; public bool sa, sb, ra, rb; public string reason;
    }
    readonly List<CRow> rows = new List<CRow>();
    readonly Dictionary<ADMove, ADMoveDef> moveDefs = new Dictionary<ADMove, ADMoveDef>();
    readonly Dictionary<ADStance, ADStanceDef> stanceDefs = new Dictionary<ADStance, ADStanceDef>();
    ADPlan subA, subB;
    bool timedA, timedB;

    public ADRulesCfg Cfg { get { return Data.rules; } }

    public ADRules(ADRulesData data, IADRng rng = null, IADWounds wounds = null)
    {
        Data = data;
        Rng = rng ?? new ADRng(data.seed);
        Wounds = wounds ?? new ADBasicWounds(data.rules);
        foreach (var m in data.rules.moves) { ADMove id; if (Enum.TryParse(m.id, true, out id)) moveDefs[id] = m; }
        foreach (var s in data.stances.rows) { ADStance id; if (Enum.TryParse(s.id, true, out id)) stanceDefs[id] = s; }
        foreach (var r in data.matrix.rows)
        {
            var c = new CRow { ah = r.aHits, bh = r.bHits, sa = r.staggerA, sb = r.staggerB, ra = r.riposteA, rb = r.riposteB, reason = r.reason ?? "" };
            ParseMoves(r.a, c.a); ParseMoves(r.b, c.b);
            if (!Enum.TryParse(r.outA, true, out c.oa)) c.oa = ADOutcome.Whiff;
            if (!Enum.TryParse(r.outB, true, out c.ob)) c.ob = ADOutcome.Whiff;
            rows.Add(c);
        }
    }
    static void ParseMoves(string list, bool[] into)
    {
        if (string.IsNullOrEmpty(list) || list.Trim() == "*") { for (int i = 0; i < into.Length; i++) into[i] = true; return; }
        foreach (var p in list.Split(',')) { ADMove m; if (Enum.TryParse(p.Trim(), true, out m)) into[(int)m] = true; }
    }

    public ADFighter Get(ADSide s) { return s == ADSide.Dracula ? Dracula : Pitborn; }
    public ADFighter Other(ADSide s) { return s == ADSide.Dracula ? Pitborn : Dracula; }
    public bool IsAttack(ADMove m) { ADMoveDef d; return moveDefs.TryGetValue(m, out d) && d.attack; }

    // ---------------- state machine ----------------
    public void StartDuel()
    {
        Dracula = new ADFighter { side = ADSide.Dracula, hp = Cfg.hpDracula, hpMax = Cfg.hpDracula };
        Pitborn = new ADFighter { side = ADSide.Pitborn, hp = Cfg.hpPitborn, hpMax = Cfg.hpPitborn };
        Round = 0; Over = false; History.Clear(); LastResult = null; Phase = ADPhase.Idle;
        BeginPlan();
    }

    public void BeginPlan()
    {
        if (Over) return;
        Round++; Phase = ADPhase.Plan; PlanTimeLeft = Cfg.planSeconds;
        subA = subB = null; timedA = timedB = false;
        Dracula.stagger = Pitborn.stagger = 0;
        if (OnPlan != null) OnPlan(Round);
    }

    public bool HasSubmitted(ADSide s) { return (s == ADSide.Dracula ? subA : subB) != null; }

    // Blind: nothing about the other plan is exposed until the round resolves. Re-submitting before the other side is in replaces the plan.
    public bool Submit(ADSide side, ADPlan plan)
    {
        if (Phase != ADPhase.Plan || Over || plan == null) return false;
        var p = Sanitize(plan, Get(side));
        if (side == ADSide.Dracula) subA = p; else subB = p;
        if (subA != null && subB != null) ResolveSubmitted();
        return true;
    }

    public void Tick(float dt)
    {
        if (Phase != ADPhase.Plan || Over || Cfg.planSeconds <= 0f) return;
        PlanTimeLeft -= dt;
        if (PlanTimeLeft > 0f) return;
        PlanTimeLeft = 0f;
        if (subA == null) { subA = Sanitize(Fallback(ADSide.Dracula), Dracula); timedA = true; }
        if (subB == null) { subB = Sanitize(Fallback(ADSide.Pitborn), Pitborn); timedB = true; }
        ResolveSubmitted();
    }

    ADPlan Fallback(ADSide s)
    {
        if (Cfg.repeatPlanOnTimeout)
            for (int i = History.Count - 1; i >= 0; i--) return (s == ADSide.Dracula ? History[i].planA : History[i].planB).Clone();
        return Cfg.defaultPlan.ToPlan();
    }

    void ResolveSubmitted()
    {
        var a = subA; var b = subB; var ta = timedA; var tb = timedB;
        Phase = ADPhase.Resolve;
        var rr = ResolveRound(a, b);
        rr.timedOutA = ta; rr.timedOutB = tb;
    }

    public void BeginReplay() { if (Phase == ADPhase.Resolve) Phase = ADPhase.Replay; }
    public void EndRound()
    {
        if (Phase != ADPhase.Resolve && Phase != ADPhase.Replay) return;
        Phase = Over ? ADPhase.Ended : ADPhase.Result;
        if (OnRoundEnd != null) OnRoundEnd(LastResult);
    }
    public void NextRound() { if (Phase == ADPhase.Result) BeginPlan(); }

    // ---------------- plans ----------------
    // Fixes length, allows ONE Special per fight per fighter (extra/spent ones become cfg.usedSpecialFallback).
    public ADPlan Sanitize(ADPlan plan, ADFighter f)
    {
        var p = new ADPlan { stance = plan.stance, moves = new ADMove[Cfg.beats] };
        ADMove fb; if (!Enum.TryParse(Cfg.usedSpecialFallback, true, out fb)) fb = ADMove.Guard;
        bool spent = f.specialUsed;
        for (int i = 0; i < p.moves.Length; i++)
        {
            ADMove m = (plan.moves != null && i < plan.moves.Length) ? plan.moves[i] : fb;
            if (m == ADMove.Special) { if (spent || !Cfg.enableSpecials) m = fb; else spent = true; }
            p.moves[i] = m;
        }
        return p;
    }

    // ---------------- resolution ----------------
    public ADRoundResult ResolveRound(ADPlan planDracula, ADPlan planPitborn)
    {
        var pa = Sanitize(planDracula, Dracula); var pb = Sanitize(planPitborn, Pitborn);
        var rr = new ADRoundResult { round = Round, planA = pa, planB = pb };
        if (Phase == ADPhase.Plan || Phase == ADPhase.Idle) Phase = ADPhase.Resolve;
        BeginStance(Dracula, pa.stance, out rr.stanceNoteA);
        BeginStance(Pitborn, pb.stance, out rr.stanceNoteB);
        foreach (var m in pa.moves) if (m == ADMove.Special) Dracula.specialUsed = true;
        foreach (var m in pb.moves) if (m == ADMove.Special) Pitborn.specialUsed = true;
        for (int i = 0; i < pa.moves.Length && !Over; i++)
        {
            var beat = ResolveBeat(i, pa.moves[i], pb.moves[i]);
            rr.beats.Add(beat);
            if (OnBeat != null) OnBeat(beat);
            if (beat.killed)
            {
                Over = true; Winner = beat.victim == ADSide.Dracula ? ADSide.Pitborn : ADSide.Dracula;
                rr.killed = true; rr.winner = Winner;
                if (OnKill != null) OnKill(Winner, beat);
            }
        }
        LastResult = rr; History.Add(rr);
        return rr;
    }

    void BeginStance(ADFighter f, ADStance s, out string note)
    {
        f.stance = s; f.stanceDealt = 1f; f.stanceTaken = 1f; note = s.ToString();
        ADStanceDef d;
        if (!stanceDefs.TryGetValue(s, out d)) return;
        f.stanceDealt = d.dealtMul; f.stanceTaken = d.takenMul;
        if (d.chances != null && d.chances.Length > 0)
        {
            double r = Rng.Next01(), acc = 0; ADChance pick = d.chances[d.chances.Length - 1];
            foreach (var c in d.chances) { acc += c.chance; if (r < acc) { pick = c; break; } }
            f.stanceDealt *= pick.dealtMul; f.stanceTaken *= pick.takenMul; note = pick.label;
        }
    }

    float Tier(ADFighter f, ADMove m, bool staggered)
    {
        ADMoveDef d; float t = moveDefs.TryGetValue(m, out d) ? d.tier : 0f;
        if (m == ADMove.Special) { var sp = SpecialOf(f.side); if (sp != null) t = sp.tier; }
        ADStanceDef sd; if (stanceDefs.TryGetValue(f.stance, out sd)) t += sd.speedDelta;
        return t + f.speedTierPenalty + (staggered ? Cfg.staggerTierPenalty : 0f);
    }
    ADSpecialDef SpecialOf(ADSide s) { return s == ADSide.Dracula ? Data.special.dracula : Data.special.pitborn; }
    public float BaseDamage(ADFighter f, ADMove m)
    {
        if (m == ADMove.Special) { var sp = SpecialOf(f.side); if (sp != null) return sp.damage; }
        ADMoveDef d; return moveDefs.TryGetValue(m, out d) ? d.damage : 0f;
    }

    static string Phrase(ADSide s, ADMove m) { return (s == ADSide.Dracula ? "your " : "his ") + m.ToString().ToLowerInvariant(); }

    ADBeat ResolveBeat(int i, ADMove mA, ADMove mB)
    {
        var fa = Dracula; var fb = Pitborn;
        var b = new ADBeat { beat = i, round = Round, a = mA, b = mB, locA = ADLoc.Torso, locB = ADLoc.Torso };
        b.lostA = fa.stagger == 2; b.lostB = fb.stagger == 2;
        bool penA = fa.stagger == 1, penB = fb.stagger == 1;
        fa.stagger = 0; fb.stagger = 0;
        b.specialA = mA == ADMove.Special && !b.lostA; b.specialB = mB == ADMove.Special && !b.lostB;
        float tA = Tier(fa, mA, penA), tB = Tier(fb, mB, penB);
        b.firstA = tA <= tB;
        float hitsA = 0f, hitsB = 0f; bool stA = false, stB = false, ripA = false, ripB = false;
        string reason = "";

        if (b.lostA || b.lostB)
        {
            b.outA = b.lostA ? ADOutcome.Staggered : (IsAttack(mA) && !b.lostB ? ADOutcome.Hit : ADOutcome.Whiff);
            b.outB = b.lostB ? ADOutcome.Staggered : (IsAttack(mB) && !b.lostA ? ADOutcome.Hit : ADOutcome.Whiff);
            if (!b.lostA && IsAttack(mA)) { hitsA = 1f; b.outA = ADOutcome.Hit; }
            if (!b.lostB && IsAttack(mB)) { hitsB = 1f; b.outB = ADOutcome.Hit; }
            reason = b.lostA && b.lostB ? "both fighters are down" : (b.lostA ? "you are down and lose the move" : "he is down and loses the move");
        }
        else
        {
            CRow row = null; bool mir = false;
            foreach (var r in rows)
            {
                if (r.a[(int)mA] && r.b[(int)mB]) { row = r; mir = false; break; }
                if (r.a[(int)mB] && r.b[(int)mA]) { row = r; mir = true; break; }
            }
            if (row != null)
            {
                b.outA = mir ? row.ob : row.oa; b.outB = mir ? row.oa : row.ob;
                hitsA = mir ? row.bh : row.ah; hitsB = mir ? row.ah : row.bh;
                stA = mir ? row.sb : row.sa; stB = mir ? row.sa : row.sb;
                ripA = mir ? row.rb : row.ra; ripB = mir ? row.ra : row.rb;
                string pa = mir ? Phrase(ADSide.Dracula, mA) : Phrase(ADSide.Dracula, mA), pb = Phrase(ADSide.Pitborn, mB);
                string ra = mir ? pb : pa, rb = mir ? pa : pb;     // {a}/{b} in the row's own orientation
                reason = row.reason.Replace("{a}", ra).Replace("{b}", rb);
            }
            else if (IsAttack(mA) && IsAttack(mB))
            {
                if (Math.Abs(tA - tB) < Cfg.tierEpsilon)
                {
                    b.tie = true; b.outA = b.outB = ADOutcome.Clash; hitsA = hitsB = Cfg.clashDamageMul; stA = stB = true;
                    reason = Phrase(ADSide.Dracula, mA) + " and " + Phrase(ADSide.Pitborn, mB) + " clash";
                }
                else
                {
                    bool aFirst = tA < tB;
                    if (Cfg.tradeOnAttackVsAttack) { hitsA = hitsB = 1f; b.outA = b.outB = ADOutcome.Hit; }
                    else { if (aFirst) { hitsA = 1f; b.outA = ADOutcome.Hit; b.outB = ADOutcome.Interrupted; } else { hitsB = 1f; b.outB = ADOutcome.Hit; b.outA = ADOutcome.Interrupted; } }
                    reason = (aFirst ? Phrase(ADSide.Dracula, mA) + " was faster than " + Phrase(ADSide.Pitborn, mB) : Phrase(ADSide.Pitborn, mB) + " was faster than " + Phrase(ADSide.Dracula, mA));
                }
            }
            else if (IsAttack(mA)) { hitsA = 1f; b.outA = ADOutcome.Hit; b.outB = ADOutcome.Struck; reason = Phrase(ADSide.Dracula, mA) + " found its mark"; }
            else if (IsAttack(mB)) { hitsB = 1f; b.outB = ADOutcome.Hit; b.outA = ADOutcome.Struck; reason = Phrase(ADSide.Pitborn, mB) + " found its mark"; }
            else { b.outA = b.outB = ADOutcome.Whiff; reason = "nothing landed"; }
            // data-driven special vs a defensive move (lane E 'special' block): guard reduce / avoidedBy override the matrix rows
            if (mA == ADMove.Special && !IsAttack(mB)) SpecialVsDefence(Data.special.dracula, mB, true, b, ref hitsA, ref hitsB, ref stA, ref stB, ref reason);
            else if (mB == ADMove.Special && !IsAttack(mA)) SpecialVsDefence(Data.special.pitborn, mA, false, b, ref hitsA, ref hitsB, ref stA, ref stB, ref reason);
        }

        // Riposte after a parry (chance from data)
        bool ripAGo = ripA && Rng.Next01() < Cfg.riposteChance;
        bool ripBGo = ripB && Rng.Next01() < Cfg.riposteChance;
        if (ripAGo) { b.riposteA = true; b.outA = ADOutcome.Riposte; reason += " and riposted"; }
        if (ripBGo) { b.riposteB = true; b.outB = ADOutcome.Riposte; reason += " and riposted"; }

        // strikes in speed order; riposte strikes always come after the parry
        bool pendKnockA = false, pendKnockB = false, pendStaggerA = stA, pendStaggerB = stB;
        var strikes = new List<int>();   // 0 = Dracula hits Pitborn, 1 = Pitborn hits Dracula, 2/3 = riposte by A / B
        bool aStrike = hitsA > 0f || ripAGo, bStrike = hitsB > 0f || ripBGo;
        if (b.firstA) { if (aStrike) strikes.Add(ripAGo && hitsA <= 0f ? 2 : 0); if (bStrike) strikes.Add(ripBGo && hitsB <= 0f ? 3 : 1); }
        else { if (bStrike) strikes.Add(ripBGo && hitsB <= 0f ? 3 : 1); if (aStrike) strikes.Add(ripAGo && hitsA <= 0f ? 2 : 0); }

        foreach (int s in strikes)
        {
            bool byA = (s == 0 || s == 2); bool rip = s >= 2;
            var atk = byA ? fa : fb; var def = byA ? fb : fa; var mv = byA ? mA : mB;
            if (atk.Dead) { if (byA) b.outA = ADOutcome.Interrupted; else b.outB = ADOutcome.Interrupted; continue; }   // killed earlier in this beat
            float frac = rip ? 1f : (byA ? hitsA : hitsB);
            bool crit; ADLoc loc; int heal; bool knock;
            int dmg = Strike(atk, def, rip ? ADMove.Slash : mv, frac, rip, out crit, out loc, out heal, out knock);
            if (byA) { b.dmgToB += dmg; b.locB = loc; b.critA |= crit; b.healA += heal; if (knock) pendKnockB = true; }
            else { b.dmgToA += dmg; b.locA = loc; b.critB |= crit; b.healB += heal; if (knock) pendKnockA = true; }
            if (!rip && mv == ADMove.Special) { var spx = SpecialOf(atk.side); if (spx != null && spx.staggerBeats > 0 && dmg > 0) { if (byA) pendStaggerB = true; else pendStaggerA = true; } }
            if (!rip && mv != ADMove.Special) { ADMoveDef md; if (moveDefs.TryGetValue(mv, out md) && md.staggers && dmg > 0) { if (byA) pendStaggerB = true; else pendStaggerA = true; } }
            if (def.Dead)
            {
                b.killed = true; b.victim = def.side;
                if (byA) b.outA = ADOutcome.Killed; else b.outB = ADOutcome.Killed;
                reason += " - killing blow";
            }
        }
        b.crit = b.critA || b.critB;
        if (b.crit) reason += " (crit)";
        if (!fa.Dead && !fb.Dead)
        {
            fa.stagger = pendKnockA ? 2 : (pendStaggerA ? 1 : 0); fb.stagger = pendKnockB ? 2 : (pendStaggerB ? 1 : 0);
        }
        b.staggerA = fa.stagger == 1; b.staggerB = fb.stagger == 1; b.knockdownA = fa.stagger == 2; b.knockdownB = fb.stagger == 2;
        b.reason = reason;
        return b;
    }

    void SpecialVsDefence(ADSpecialDef sp, ADMove d, bool spIsA, ADBeat b, ref float hitsA, ref float hitsB, ref bool stA, ref bool stB, ref string reason)
    {
        if (sp == null) return;
        bool hasAvoid = sp.avoidedBy != null && sp.avoidedBy.Length > 0;
        if (!hasAvoid && string.IsNullOrEmpty(sp.guardMode) && !sp.unblockable) return;   // no data: keep the matrix row
        ADSide spSide = spIsA ? ADSide.Dracula : ADSide.Pitborn;
        string pSp = Phrase(spSide, ADMove.Special), pDef = Phrase(spIsA ? ADSide.Pitborn : ADSide.Dracula, d);
        ADOutcome oSp, oDef; float hits; bool stSp = false, stDef = false;
        bool avoided = false;
        if (hasAvoid) foreach (var n in sp.avoidedBy) if (string.Equals(n, d.ToString(), StringComparison.OrdinalIgnoreCase)) avoided = true;
        if (avoided && d != ADMove.Guard) { oDef = ADOutcome.Dodged; oSp = ADOutcome.Whiff; hits = 0f; stSp = true; reason = pDef + " avoided " + pSp; }
        else if (d == ADMove.Guard)
        {
            if (sp.unblockable || string.Equals(sp.guardMode, "break", StringComparison.OrdinalIgnoreCase)) { oSp = ADOutcome.Hit; oDef = ADOutcome.GuardBreak; hits = 1f; stDef = true; reason = pSp + " broke " + pDef; }
            else if (string.Equals(sp.guardMode, "reduced", StringComparison.OrdinalIgnoreCase)) { oSp = ADOutcome.Hit; oDef = ADOutcome.Struck; hits = sp.guardReduce; reason = pDef + " only halved " + pSp; }
            else { oSp = ADOutcome.Blocked; oDef = ADOutcome.Parried; hits = 0f; reason = pDef + " stopped " + pSp; }
        }
        else { oSp = ADOutcome.Hit; oDef = ADOutcome.Struck; hits = 1f; reason = pSp + " caught " + pDef; }
        if (spIsA) { b.outA = oSp; b.outB = oDef; hitsA = hits; hitsB = 0f; stA = stSp; stB = stDef; }
        else { b.outB = oSp; b.outA = oDef; hitsB = hits; hitsA = 0f; stB = stSp; stA = stDef; }
    }

    int Strike(ADFighter atk, ADFighter def, ADMove mv, float frac, bool riposte, out bool crit, out ADLoc loc, out int heal, out bool knock)
    {
        float baseDmg = riposte ? Cfg.riposteDamage : BaseDamage(atk, mv);
        double v = Rng.Next01();
        float variance = 1f + (float)(v * 2.0 - 1.0) * Cfg.damageVariance;
        crit = Rng.Next01() < Data.crit.chance + atk.critBonus;
        ADSpecialDef spd = (!riposte && mv == ADMove.Special) ? SpecialOf(atk.side) : null;
        if (spd != null && !spd.critAllowed) crit = false;
        float mul = frac * atk.stanceDealt * def.stanceTaken * Wounds.DamageMul(atk, mv) * (crit ? Data.crit.mul : 1f);
        int dmg = (int)Math.Round(baseDmg * variance * mul, MidpointRounding.AwayFromZero);
        if (frac > 0f && dmg < Cfg.minDamage) dmg = Cfg.minDamage;
        if (frac <= 0f) dmg = 0;
        dmg = Math.Min(dmg, def.hp);
        loc = Wounds.RollLocation(mv, Rng);
        if (spd != null && !string.IsNullOrEmpty(spd.hitLocation) && !string.Equals(spd.hitLocation, "any", StringComparison.OrdinalIgnoreCase)) { ADLoc fl; if (Enum.TryParse(spd.hitLocation, true, out fl)) loc = fl; }
        def.hp -= dmg;
        if (dmg > 0) Wounds.Apply(def, loc, dmg, crit);
        heal = 0; knock = false;
        if (!riposte && mv == ADMove.Special)
        {
            var sp = SpecialOf(atk.side);
            if (sp != null)
            {
                if (sp.healFraction > 0f && dmg > 0) { heal = Math.Min((int)Math.Round(dmg * sp.healFraction), atk.hpMax - atk.hp); if (heal < 0) heal = 0; atk.hp += heal; }
                knock = sp.knockdown && dmg > 0;
            }
        }
        return dmg;
    }
}

// Default wounds provider (small). Lane B's ADWounds replaces it by assigning ADRules.Wounds.
public class ADBasicWounds : IADWounds
{
    readonly ADRulesCfg cfg;
    public ADBasicWounds(ADRulesCfg c) { cfg = c; }
    public ADLoc RollLocation(ADMove move, IADRng rng)
    {
        ADWoundWeights w = null;
        if (cfg.fallbackWounds != null) foreach (var x in cfg.fallbackWounds) if (string.Equals(x.move, move.ToString(), StringComparison.OrdinalIgnoreCase)) { w = x; break; }
        if (w == null) return ADLoc.Torso;
        double tot = w.arm + w.leg + w.torso + w.head, r = rng.Next01() * tot;
        if ((r -= w.arm) < 0) return ADLoc.Arm;
        if ((r -= w.leg) < 0) return ADLoc.Leg;
        if ((r -= w.torso) < 0) return ADLoc.Torso;
        return ADLoc.Head;
    }
    public float DamageMul(ADFighter attacker, ADMove move)
    {
        if (!cfg.enableWounds || move != ADMove.Heavy) return 1f;
        return Math.Max(0.4f, 1f - cfg.armHeavyMulPerSeverity * attacker.wounds[(int)ADLoc.Arm]);
    }
    public void Apply(ADFighter victim, ADLoc loc, int damage, bool crit)
    {
        if (!cfg.enableWounds) return;
        if (damage >= cfg.woundMinDamage || crit) victim.wounds[(int)loc] = Math.Min(cfg.maxSeverity, victim.wounds[(int)loc] + 1);
        victim.speedTierPenalty = cfg.legTierPerSeverity * victim.wounds[(int)ADLoc.Leg];
    }
}
