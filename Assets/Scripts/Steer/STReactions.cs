using System;
using System.Collections.Generic;
using UnityEngine;

// STEER lane S2 (re-scoped, no tells / no wind-up changes): smart natural reactions for BOTH fighters, all numbers in steer.json block "reactions".
//  - per incoming swing type (light/slash/stab/kick/heavy/special) a seeded draw of the answer: guard / parry attempt / roll / sidestep / trade / ignore, probabilities from
//    reactions.byMove x skill x stance x running tactic; the draw is executed by the EXISTING AIBrain (its profile knobs parry/dodge/lapse/guard/reaction are overridden for that one swing,
//    then restored) or by DuelFighter.PressSidestep / Press (sidestep, trade). Reaction delay = skill ms +- jitter ms, per swing.
//  - punish whiffs: after a miss or a blocked heavy the other fighter attacks the recovery with probability by skill (and stance), after a human-ish delay.
//  - no robotic loops: per fighter a short history of swing types; a repeated run / short cycle shifts the AI's light-vs-heavy share (profile.pressure) the other way, plus seeded mix-ups
//    and slow pressure/patience phases on profile.aggression.
//  - FEINT bait: the player's feint (cancelled wind-up) while the Feint tactic is on: the enemy bites with probability reactions.feintBiteChance, starts a counter attack, and the player's riposte beats it.
// Inert unless STSteer.Active. No hooks into existing files: only public members (AIBrain.profile / ai, DuelFighter.Press / PressSidestep / ParrySuccess / Stagger, ContactRelay events).
[Serializable] public class STRProb { public float guard, parry, roll, sidestep, trade; }
[Serializable] public class STRMoveRow { public string type = ""; public STRProb player = new STRProb(); public STRProb enemy = new STRProb(); }
[Serializable] public class STRMoveMap { public string move = "", type = "light", label = "cut"; }
[Serializable] public class STRMul { public string key = ""; public float guard = 1f, parry = 1f, roll = 1f, sidestep = 1f, trade = 1f, punish = 1f; }
[Serializable]
public class STRSide
{
    public bool enabled = true;
    public float reactionMs = 260f, jitterMs = 90f, skill = 1f;                       // delay before he notices a swing; skill scales guard/parry/roll/sidestep
    public float punish = 0.5f, punishHeavyBonus = 0.2f, punishDelayMs = 200f, punishJitterMs = 90f, punishHeavyShare = 0.3f;   // whiff / blocked-heavy punish
    public float mixup = 0.35f, repeatPenalty = 0.22f, patienceAmp = 0.25f, patienceMinS = 3f, patienceMaxS = 7f;               // anti-pattern layer
    public int historyLen = 6;
}
[Serializable] public class STRFeint { public string biteMove = "light_right"; public float armSeconds = 1.6f, maxGapMeters = 3.4f; public int biteStaggerTicks = 100, leadTicks = 3, feintMaxTick = 12; public bool requireTactic = true; }
[Serializable]
public class STRCfg
{
    public bool enabled = true; public int seed = 20261104; public float feintBiteChance = 0.55f, sidestepLeadTicks = 6f, feedMinGapS = 0.5f;
    public STRSide player = new STRSide(), enemy = new STRSide(); public STRFeint feint = new STRFeint();
    public STRMoveRow[] byMove = new STRMoveRow[0]; public STRMoveMap[] moveTypes = new STRMoveMap[0];
    public STRMul[] stanceMul = new STRMul[0]; public STRMul[] tacticMul = new STRMul[0];
}

[DefaultExecutionOrder(-100)]
public class STReactions : MonoBehaviour
{
    // a knob that other writers (S1 stance bias) may also change: we remember what we wrote and treat any other value as the new base
    class Knob
    {
        float last = float.NaN, baseV;
        public float Base(float cur) { if (float.IsNaN(last) || Mathf.Abs(cur - last) > 1e-6f) baseV = cur; return baseV; }
        public void Set(ref float field, float baseValue, float v) { field = v; last = v; baseV = baseValue; }
    }
    class Act { public int tick; public string kind; public int dir; public DuelFighter who; public int serial; }
    class Ctx
    {
        public DuelFighter df; public AIBrain brain; public STRSide cfg; public bool isPlayer;
        public Knob kReaction = new Knob(), kParry = new Knob(), kDodge = new Knob(), kLapse = new Knob(), kGuard = new Knob(), kPressure = new Knob(), kAgg = new Knob();
        public bool hasOv; public float ovReaction, ovParry, ovDodge, ovLapse, ovGuard; public int ovSerial = -1;
        public float pressureDelta, aggMul = 1f, patienceUntil; public List<int> hist = new List<int>();
    }

    public static STReactions Instance;
    public STRCfg cfg; Ctx dra, pit;
    uint rng = 1u; readonly List<Act> acts = new List<Act>();
    float lastFeed = -10f; bool pitBiteWanted, pitBiteIssued, biteAttack, biteDone; float biteArmUntil, feintArmUntil; int nextPressTick;
    FState draPrevState; int draPrevT; bool draPrevValid; string[] typeNames = { "light", "slash", "stab", "kick", "heavy", "special" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { var go = new GameObject("STReactionsBoot"); DontDestroyOnLoad(go); go.AddComponent<STReactionsWatcher>(); }

    public static STReactions Ensure()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("STReactions"); return go.AddComponent<STReactions>();
    }

    void OnEnable()
    {
        Instance = this; cfg = STSteer.Load<STRCfg>("reactions"); rng = (uint)Mathf.Max(1, cfg.seed);
        ContactRelay.OnAttackStarted += OnAttackStarted; ContactRelay.OnAttackMissed += OnAttackMissed; ContactRelay.OnEvent += OnEvent;
        STSteer.OnTactic += OnTactic; STSteer.OnFightReset += OnReset;
    }
    void OnDisable()
    {
        ContactRelay.OnAttackStarted -= OnAttackStarted; ContactRelay.OnAttackMissed -= OnAttackMissed; ContactRelay.OnEvent -= OnEvent;
        STSteer.OnTactic -= OnTactic; STSteer.OnFightReset -= OnReset;
        Restore(dra); Restore(pit);
        if (Instance == this) Instance = null;
    }
    void OnReset() { Restore(dra); Restore(pit); dra = pit = null; acts.Clear(); pitBiteWanted = pitBiteIssued = biteAttack = false; draPrevValid = false; }

    float Rand() { unchecked { rng = rng * 1664525u + 1013904223u; return ((rng >> 8) & 0xFFFFFF) / 16777216f; } }
    bool Live() { return STSteer.Active && cfg != null && cfg.enabled; }

    void Bind()
    {
        var d = STSteer.Player; var p = STSteer.Enemy;
        if (d == null || p == null) foreach (var f in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) { string fid = Roster.IdOf(f); if (fid == Roster.EnemyId) p = p ?? f; else if (fid == Roster.PlayerId) d = d ?? f; }
        if (d != null && (dra == null || dra.df != d)) dra = MakeCtx(d, true);
        if (p != null && (pit == null || pit.df != p)) pit = MakeCtx(p, false);
    }
    Ctx MakeCtx(DuelFighter f, bool isDra)
    {
        var c = new Ctx { df = f, brain = f.GetComponent<AIBrain>(), isPlayer = isDra, cfg = isDra ? cfg.player : cfg.enemy };
        c.patienceUntil = Time.time + 2f; return c;
    }
    Ctx CtxOf(DuelFighter f) { return dra != null && dra.df == f ? dra : (pit != null && pit.df == f ? pit : null); }
    static bool BrainOn(Ctx c) { return c != null && c.brain != null && c.brain.enabled && c.brain.profile != null && c.cfg.enabled; }

    // ---------------- move typing ----------------
    STRMoveMap MapOf(string id)
    {
        foreach (var m in cfg.moveTypes) if (m.move == id) return m;
        if (id != null && id.StartsWith("heavy")) return new STRMoveMap { move = id, type = "heavy", label = "heavy" };
        if (id != null && (id.StartsWith("skill_") || id == "critical")) return new STRMoveMap { move = id, type = "special", label = "special" };
        return new STRMoveMap { move = id, type = "light", label = "cut" };
    }
    int TypeIdx(string t) { return Mathf.Max(0, Array.IndexOf(typeNames, t)); }
    STRProb ProbFor(string type, bool isDra)
    {
        foreach (var r in cfg.byMove) if (r.type == type) return isDra ? r.player : r.enemy;
        return null;
    }
    STRMul Find(STRMul[] a, string key) { foreach (var m in a) if (m.key == key) return m; return null; }

    // ---------------- reaction draw for a fresh swing ----------------
    void OnAttackStarted(DuelFighter f, MoveDef m)
    {
        if (!Live() || m == null) return;
        Bind(); var atk = CtxOf(f); if (atk == null) return;
        var def = atk == dra ? pit : dra; if (def == null) return;
        var map = MapOf(m.id); int ti = TypeIdx(map.type);

        // anti-pattern history of the attacker
        atk.hist.Add(ti); while (atk.hist.Count > Mathf.Max(3, atk.cfg.historyLen)) atk.hist.RemoveAt(0);
        UpdatePressure(atk, ti);
        ApplyKnobs(atk);

        // bite attack bookkeeping
        if (f == pit.df && pitBiteIssued && m.id == cfg.feint.biteMove) { biteAttack = true; biteDone = false; pitBiteIssued = false; pitBiteWanted = false; }

        if (!BrainOn(def)) return;
        var P = ProbFor(map.type, def.isPlayer); if (P == null) return;
        float skill = def.cfg.skill, g = P.guard * skill, pa = P.parry * skill, ro = P.roll * skill, si = P.sidestep * skill, tr = P.trade;
        if (def.isPlayer)
        {
            Mul(ref g, ref pa, ref ro, ref si, ref tr, Find(cfg.stanceMul, STSteer.Stance.ToString()));
            var run = STSteer.Running; if (run.HasValue) Mul(ref g, ref pa, ref ro, ref si, ref tr, Find(cfg.tacticMul, run.Value.ToString()));
        }
        float sum = g + pa + ro + si + tr; if (sum > 1f) { g /= sum; pa /= sum; ro /= sum; si /= sum; tr /= sum; }
        float u = Rand(); string pick = "ignore";
        if (u < g) pick = "guard"; else if ((u -= g) < pa) pick = "parry"; else if ((u -= pa) < ro) pick = "roll"; else if ((u -= ro) < si) pick = "sidestep"; else if ((u -= si) < tr) pick = "trade";

        int delay = Mathf.Max(4, Mathf.RoundToInt((def.cfg.reactionMs + (Rand() * 2f - 1f) * def.cfg.jitterMs) * 0.06f));
        var p = def.brain.profile;
        def.hasOv = true; def.ovSerial = -2; def.ovReaction = delay;
        def.ovParry = 0f; def.ovDodge = 0f; def.ovLapse = 0f; def.ovGuard = 1f;
        switch (pick)
        {
            case "parry": def.ovParry = 1f; break;
            case "roll": def.ovDodge = 1f; break;
            case "guard": break;
            default: def.ovLapse = 1f; break;   // sidestep / trade / ignore: the brain stands down, we act (or nothing)
        }
        int start = Duel.TickCount, contact = start + (f.state == FState.Windup ? f.WindupLeft : m.windup);
        if (pick == "sidestep") acts.Add(new Act { tick = Mathf.Max(start + delay, contact - Mathf.RoundToInt(cfg.sidestepLeadTicks)), kind = "sidestep", dir = Rand() < 0.5f ? -1 : 1, who = def.df, serial = def.ovSerial });
        else if (pick == "trade") acts.Add(new Act { tick = start + delay, kind = "trade", who = def.df, serial = def.ovSerial });
        ApplyKnobs(def);
        STSteer.Log("react:" + (def.isPlayer ? Roster.PlayerName.ToLowerInvariant() : Roster.EnemyName.ToLowerInvariant()), map.type + " -> " + pick + " delay " + (delay * 1000 / 60) + " ms", "g" + g.ToString("0.00") + " pa" + pa.ToString("0.00") + " ro" + ro.ToString("0.00") + " si" + si.ToString("0.00") + " tr" + tr.ToString("0.00"));
    }
    static void Mul(ref float g, ref float pa, ref float ro, ref float si, ref float tr, STRMul m)
    {
        if (m == null) return; g *= m.guard; pa *= m.parry; ro *= m.roll; si *= m.sidestep; tr *= m.trade;
    }

    // repeated run / short cycle of the attacker's swing types: push the next opener the other way (light-ish repeated -> heavier, heavy-ish repeated -> lighter), plus a seeded mix-up
    void UpdatePressure(Ctx c, int lastType)
    {
        var h = c.hist; int n = h.Count, run = 1;
        for (int i = n - 2; i >= 0 && h[i] == h[n - 1]; i--) run++;
        int rep = run - 1;
        for (int per = 2; per <= 3 && rep < 2; per++)   // ABAB / ABCABC
            if (n >= per * 2) { bool cyc = true; for (int i = 0; i < per; i++) if (h[n - 1 - i] != h[n - 1 - i - per]) { cyc = false; break; } if (cyc) rep = Mathf.Max(rep, 2); }
        bool lightish = lastType <= 1;
        float d = (lightish ? -1f : 1f) * c.cfg.repeatPenalty * Mathf.Min(3, rep);
        if (Rand() < c.cfg.mixup) d += (Rand() * 2f - 1f) * 0.3f;
        c.pressureDelta = d;
    }

    // ---------------- punish whiffs ----------------
    void OnAttackMissed(DuelFighter f, MoveDef m)
    {
        if (!Live() || m == null) return;
        Bind(); var atk = CtxOf(f); var def = atk == dra ? pit : dra; if (atk == null || def == null) return;
        bool heavy = MapOf(m.id).type == "heavy" || MapOf(m.id).type == "special";
        SchedulePunish(def, f, heavy, "whiff");
        if (def.isPlayer && (def.df.state == FState.Roll || def.df.IFrames)) Feed("You dodged his " + MapOf(m.id).label);
    }
    void OnEvent(CombatEvent e)
    {
        if (!Live() || e.actor == null || e.target == null) return;
        Bind();
        if (e.type == "Blocked" && e.heavy) { var def = CtxOf(e.target); if (def != null) SchedulePunish(def, e.actor, true, "blocked heavy"); }
        else if (e.type == "Parried" && e.target == dra?.df && e.actor == pit?.df && !biteAttack) Feed("You parried his " + MapOf(e.moveId).label);
    }
    void SchedulePunish(Ctx def, DuelFighter attacker, bool heavy, string why)
    {
        if (!BrainOn(def)) return;
        float p = def.cfg.punish + (heavy ? def.cfg.punishHeavyBonus : 0f);
        if (def.isPlayer)
        {
            var sm = Find(cfg.stanceMul, STSteer.Stance.ToString()); if (sm != null) p *= sm.punish;
            var run = STSteer.Running; if (run.HasValue) { var tm = Find(cfg.tacticMul, run.Value.ToString()); if (tm != null) p *= tm.punish; }
        }
        if (Rand() >= Mathf.Clamp01(p)) return;
        int delay = Mathf.Max(3, Mathf.RoundToInt((def.cfg.punishDelayMs + (Rand() * 2f - 1f) * def.cfg.punishJitterMs) * 0.06f));
        acts.Add(new Act { tick = Duel.TickCount + delay, kind = "punish", who = def.df, serial = -1 });
        STSteer.Log("react:" + (def.isPlayer ? Roster.PlayerName.ToLowerInvariant() : Roster.EnemyName.ToLowerInvariant()), "punish " + why + " in " + (delay * 1000 / 60) + " ms", "p=" + p.ToString("0.00"));
    }

    // ---------------- feint bait ----------------
    void OnTactic(STTactic t, bool accepted)
    {
        if (!Live() || !accepted || t != STTactic.Feint) return;
        feintArmUntil = Time.time + 3.5f;   // the feint tactic window: a cancelled wind-up inside it baits
    }

    void WatchFeint()
    {
        if (dra == null || pit == null) return;
        var d = dra.df; bool tacticOn = Time.time < feintArmUntil || STSteer.IsRunning(STTactic.Feint);
        if (draPrevValid && draPrevState == FState.Windup && draPrevT <= cfg.feint.feintMaxTick && (d.state == FState.Loco || d.state == FState.Guard) && (tacticOn || !cfg.feint.requireTactic))
        {
            if (Rand() < cfg.feintBiteChance) { pitBiteWanted = true; pitBiteIssued = false; biteArmUntil = Time.time + cfg.feint.armSeconds; nextPressTick = 0; STSteer.Log("feint", Roster.EnemyName.ToLowerInvariant() + " bites", "p=" + cfg.feintBiteChance); }
            else { Feed("He saw through your feint"); STSteer.Log("feint", Roster.EnemyName.ToLowerInvariant() + " ignores", ""); }
        }
        draPrevState = d.state; draPrevT = d.t; draPrevValid = true;

        if (pitBiteWanted)
        {
            if (Time.time > biteArmUntil) { pitBiteWanted = pitBiteIssued = false; return; }
            var p = pit.df; int now = Duel.TickCount;
            if (!pitBiteIssued || now >= nextPressTick)
            {
                Vector3 v = d.transform.position - p.transform.position; v.y = 0f;
                if ((p.state == FState.Loco || p.state == FState.Guard) && v.magnitude <= cfg.feint.maxGapMeters) { p.Press(cfg.feint.biteMove); pitBiteIssued = true; nextPressTick = now + 13; }
            }
        }
        if (biteAttack && !biteDone)
        {
            var p = pit.df;
            if (p.state != FState.Windup && p.state != FState.Active) { biteAttack = false; return; }
            bool near = (p.state == FState.Windup && p.WindupLeft <= cfg.feint.leadTicks) || p.state == FState.Active;
            if (near)
            {
                if (d.state == FState.Roll || d.state == FState.Stagger || d.state == FState.Down || d.state == FState.ParryRecover || d.Dead) { biteAttack = false; return; }
                biteDone = true; biteAttack = false;
                d.ParrySuccess(); p.Stagger(cfg.feint.biteStaggerTicks, "stagger_loop"); p.AddPosture(p.Rule("posture.parry", 25f));
                ContactRelay.Raise(p, d, "Parried", false, true);
                ContactRelay.RaiseEvent(new CombatEvent { type = "Parried", actor = p, target = d, moveId = p.CurrentMove != null ? p.CurrentMove.id : "", perfect = true, location = "torso", direction = "front", point = d.transform.position + Vector3.up * 1.1f, tick = Duel.TickCount });
                Feed("He bit on your feint: free riposte"); STSteer.Log("feint", "bite countered", "parry + riposte window");
            }
        }
    }

    // ---------------- per frame ----------------
    void Update()
    {
        if (!Live()) { if (dra != null) Restore(dra); if (pit != null) Restore(pit); return; }
        Bind(); if (dra == null || pit == null) return;
        WatchFeint();
        RunActs();
        Phase(dra); Phase(pit);
        ReleaseOverrides(dra, pit); ReleaseOverrides(pit, dra);
        ApplyKnobs(dra); ApplyKnobs(pit);
    }

    void Phase(Ctx c)
    {
        if (Time.time < c.patienceUntil) return;
        c.patienceUntil = Time.time + Mathf.Lerp(c.cfg.patienceMinS, c.cfg.patienceMaxS, Rand());
        c.aggMul = 1f + (Rand() * 2f - 1f) * c.cfg.patienceAmp;
    }

    void ReleaseOverrides(Ctx c, Ctx foe)
    {
        if (!c.hasOv) return;
        var ft = AiWorld.Track(foe.df);
        if (c.ovSerial == -2) c.ovSerial = ft.serial;
        bool planned = c.brain != null && c.brain.ai != null && c.brain.ai.plannedSerial == c.ovSerial;
        bool foeAttacking = foe.df.state == FState.Windup || foe.df.state == FState.Active;
        if (planned || !foeAttacking || ft.serial != c.ovSerial) c.hasOv = false;
    }

    void RunActs()
    {
        int now = Duel.TickCount;
        for (int i = acts.Count - 1; i >= 0; i--)
        {
            var a = acts[i]; if (now < a.tick) continue; acts.RemoveAt(i);
            var who = a.who; if (who == null || who.Dead) continue;
            bool free = who.state == FState.Loco || who.state == FState.Guard;
            var foe = who.opponent; if (foe == null || foe.Dead) continue;
            if (!free) continue;
            Vector3 v = foe.transform.position - who.transform.position; v.y = 0f; float gap = v.magnitude;
            if (a.kind == "sidestep") who.PressSidestep(a.dir);
            else if (a.kind == "trade")
            {
                var lt = who.moves != null ? who.moves.Get("light_right") : null;
                who.Press(lt != null && gap <= lt.reach * who.ReachScale + who.radius + foe.radius ? "light" : "thrust");
            }
            else if (a.kind == "punish")
            {
                if (foe.state != FState.Recovery && foe.state != FState.Stagger) continue;   // he has recovered already
                var c = CtxOf(who); var lt = who.moves != null ? who.moves.Get("light_right") : null; var hv = who.moves != null ? who.moves.Get("heavy_overhead") : null;
                int left = foe.CurrentMove != null && foe.state == FState.Recovery ? foe.WeaponTicks(foe.CurrentMove.Recovery(false)) /* WEAPONSWAP v88 */ - foe.t : 20;
                float lightReach = lt != null ? lt.reach * who.ReachScale + who.radius + foe.radius : 1.5f;
                string mv;
                if (gap > lightReach) mv = "thrust";
                else if (hv != null && left > hv.Windup(false) + 2 && Rand() < (c != null ? c.cfg.punishHeavyShare : 0.3f)) mv = "heavy";
                else mv = "light";
                who.Press(mv);
            }
        }
    }

    // ---------------- write the knobs into the brains ----------------
    public void ReapplyPlayer() { if (dra != null) ApplyKnobs(dra); }
    void ApplyKnobs(Ctx c)
    {
        if (!BrainOn(c)) return;
        var p = c.brain.profile;
        float bR = c.kReaction.Base(p.reaction), bP = c.kParry.Base(p.parry), bD = c.kDodge.Base(p.dodge), bL = c.kLapse.Base(p.lapse), bG = c.kGuard.Base(p.guard), bPr = c.kPressure.Base(p.pressure), bA = c.kAgg.Base(p.aggression);
        c.kReaction.Set(ref p.reaction, bR, c.hasOv ? c.ovReaction : bR);
        c.kParry.Set(ref p.parry, bP, c.hasOv ? c.ovParry : bP);
        c.kDodge.Set(ref p.dodge, bD, c.hasOv ? c.ovDodge : bD);
        c.kLapse.Set(ref p.lapse, bL, c.hasOv ? c.ovLapse : bL);
        c.kGuard.Set(ref p.guard, bG, c.hasOv ? Mathf.Max(bG, c.ovGuard) : bG);
        c.kPressure.Set(ref p.pressure, bPr, Mathf.Clamp01(bPr + c.pressureDelta));
        c.kAgg.Set(ref p.aggression, bA, Mathf.Clamp(bA * c.aggMul, 0.05f, 1f));
    }
    void Restore(Ctx c)
    {
        if (c == null || c.brain == null || c.brain.profile == null) return;
        var p = c.brain.profile;
        c.hasOv = false; c.pressureDelta = 0f; c.aggMul = 1f;
        float v;
        v = c.kReaction.Base(p.reaction); p.reaction = v; v = c.kParry.Base(p.parry); p.parry = v; v = c.kDodge.Base(p.dodge); p.dodge = v;
        v = c.kLapse.Base(p.lapse); p.lapse = v; v = c.kGuard.Base(p.guard); p.guard = v; v = c.kPressure.Base(p.pressure); p.pressure = v; v = c.kAgg.Base(p.aggression); p.aggression = v;
    }

    void Feed(string line)
    {
        if (Time.time - lastFeed < cfg.feedMinGapS) return;
        lastFeed = Time.time; STSteer.Feed(line);
    }
}

// attaches the manager once steer mode is on (idempotent; STController may also call STReactions.Ensure())
public class STReactionsWatcher : MonoBehaviour
{
    float next;
    void Update()
    {
        if (!STSteer.Active || Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        if (STReactions.Instance == null) STReactions.Ensure();
    }
}
