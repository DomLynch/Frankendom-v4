using UnityEngine;

// Duelist AI: port of Frankendom V3 src/fight/ai.ts decide() (+ AiRead / AiDial / AiTactic, numbers in Assets/Data/ai_rows.json and difficulty.json).
// It reads only committed duel state (never the other side's pending input), notices a fresh action `reaction` ticks late, and feeds the same inputs
// the player has (DuelFighter.Press / moveDir / guardHeld / heavyHeld / Feint). Decisions run once per sim tick advance (Duel.TickCount), never on
// Time.time, and every random draw comes from a seeded LCG (V3 lcg), so a run with the same seed and the same frame timing reproduces.
public class AIBrain : MonoBehaviour
{
    // legacy inspector fields (kept so serialized scenes still load; the V3 profile replaces them)
    public float aggression = 1f, guardChance = 0.45f, parryChance = 0.4f, rollChance = 0.2f;
    public DifficultyRow diff;   // V3 difficulty row: nine knobs (reaction, accuracy, parry, dodge, aggression, pressure, discipline, lapse, read)
    public DuelFighter df;
    public int seedOffset;                 // per-brain seed salt (tests)
    public static int GlobalSeed;          // -aiseed N on the command line
    public AiProfile profile;              // resolved profile (difficulty row over the Pitborn base, or the player-bot row)
    public AiState ai;                     // V3 AiState (mode, plan, habits...)
    public string lastScores = "";         // debug: last offence scores

    class Intent
    {
        public string action, guardDir;
        public bool guard, held, run, feintNow, closing;
        public float mx, mz;
    }

    bool inited, auto, pendingHit, defenceMet, oWasGuard, wasHurt, oPrevValid, selfWasGuard;
    int lastTick, guardEntryTick = -100000;
    Vector3 oPrevPos;
    Intent last = new Intent();

    void Awake()
    {
        df = GetComponent<DuelFighter>();
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-autoplay") auto = true;
            int sv; if (args[i] == "-aiseed" && i + 1 < args.Length && int.TryParse(args[i + 1], out sv)) GlobalSeed = sv;
        }
        if (name == "Dracula" && !auto) enabled = false;
    }
    void OnEnable() { AiWorld.Register(this); }
    void OnDisable() { AiWorld.Unregister(this); inited = false; }

    static uint Hash(string s)
    {
        uint h = 2166136261u;
        for (int i = 0; i < s.Length; i++) h = unchecked((h ^ s[i]) * 16777619u);
        return h;
    }

    void Init()
    {
        var A = AiRows.I;
        if (diff != null) profile = AiProfile.FromRow(diff, A.profileBase);
        else profile = A.playerBot.Clone();
        uint seed = unchecked((uint)A.dial.startSeed ^ Hash(name) ^ (uint)GlobalSeed ^ (uint)seedOffset);
        ai = AiState.Initial(seed, A.dial);
        lastTick = Duel.TickCount; inited = true;
    }

    // ---- notifications from AiWorld (event-fed habit counters) ----
    public void NoteOwnHit() { pendingHit = true; }
    public void NoteDefence() { defenceMet = true; }
    public void NoteOwnAttack(MoveDef m)
    {
        if (!inited) return;
        if (m.id != "kick") ai.habits.attacks++;
    }
    public void NoteFoeAttack(MoveDef m, FTrack ot)
    {
        if (!inited) return;
        var h = ai.habits; var R = AiRows.I.read; var D = AiRows.I.dial;
        var st = AiWorld.Track(df);
        // a light that PUNISHES my whiff (starts while my swing is past its active frames, or I am hurt) is the honest answer, not a mash
        bool punishes = AiWorld.Phase(df) == Ph.Hurt || (AiWorld.Phase(df) == Ph.Attack && df.move != null && AiWorld.AttackAge(df, st) >= st.windupTot + st.activeTot);
        bool light = m.id == "light_left" || m.id == "light_right";
        if (light) { if (!punishes) h.run++; } else h.run = 0;
        if (m.id == "heavy_overhead") h.heavies++;
        else if (m.id == "thrust") h.thrusts++;
        else if (light) h.lights++;
        else if (m.id == "kick") h.kicks++;
    }
    public void NoteFoeAction(string a)
    {
        if (!inited) return;
        if (a == "roll") ai.habits.rolls++; else if (a == "backstep") ai.habits.steps++;
    }

    static MoveDef Mv(DuelFighter f, string id) { return f != null && f.moves != null ? f.moves.Get(id) : null; }
    static string Mirror(string d) { return d == "right" ? "left" : d == "left" ? "right" : d; }
    static int Rnd(float x) { return (int)Mathf.Floor(x + 0.5f); }   // JS Math.round
    static readonly string[] Sides = new string[] { "left", "right", "overhead", "thrust", "low" };

    void Update()
    {
        if (df == null) return;
        if (!inited) Init();
        var o = df.opponent;
        if (df.Dead || o == null || o.Dead) { df.moveDir = Vector3.zero; df.guardHeld = false; df.run = false; df.heavyHeld = false; return; }
        int now = Duel.TickCount;
        if (now < lastTick) lastTick = now;   // sim restarted
        int n = now - lastTick;
        if (n <= 0) return;
        lastTick = now;
        int cap = Mathf.Max(1, AiRows.I.unity.maxCatchUpTicks);
        if (n > cap) n = cap;
        last = Decide(n);
        Apply(last);
    }

    // ---------------- V3 decide() ----------------
    Intent Decide(int n)
    {
        var A = AiRows.I; var R = A.read; var D = A.dial; var U = A.unity; var p = profile; var s = ai;
        var o = df.opponent; var intent = new Intent();
        int tick = Duel.TickCount;
        FTrack st = AiWorld.Track(df), ot = AiWorld.Track(o);
        Ph sp = AiWorld.Phase(df), op = AiWorld.Phase(o);

        Vector3 to = o.transform.position - transform.position; to.y = 0f;
        float dist = to.magnitude;
        Vector3 dir = dist > 0.01f ? to / dist : transform.forward; dir.y = 0f;
        // Unity geometry: Duel.Resolve reaches reach*scale + both radii + slack, so V3's gap is measured from that edge, in the attacker's reach units
        float gapW = Mathf.Max(0f, dist - (df.radius + o.radius + U.contactSlack));
        float gap = gapW / Mathf.Max(0.01f, df.scale), gapT = gapW / Mathf.Max(0.01f, o.scale);
        bool oMoved = oPrevValid && (o.transform.position - oPrevPos).sqrMagnitude > 0.000001f;
        float oldLastGap = s.lastGap;
        s.lastGap = gap; oPrevPos = o.transform.position; oPrevValid = true;

        float rollCost = df.Rule("rollCost", U.rules.rollCost);
        float safeStart = df.Rule("safeStart", U.rules.safeStart), safeEnd = df.Rule("safeEnd", U.rules.safeEnd);
        float parryWindow = df.Rule("parry", U.rules.parry);

        MoveDef mLight = Mv(df, "light_right"), mHeavy = Mv(df, "heavy_overhead"), mThrust = Mv(df, "thrust"), mKick = Mv(df, "kick");
        MoveDef omv = o.CurrentMove;
        bool canAct = sp == Ph.Ready || sp == Ph.Guard;
        bool exhausted = df.stamina <= 0f;
        bool chainOpen = df.state == FState.Recovery && df.move != null && df.move.chainFollow != null && df.move.chainFollow.Length > 0 && df.t <= df.move.chainWindow;
        float guardShare = p.guard;
        bool threat = false, noticed = false;

        // self guard bookkeeping (parry cooldown estimate)
        bool selfGuard = sp == Ph.Guard;
        if (selfGuard && !selfWasGuard) guardEntryTick = tick;
        selfWasGuard = selfGuard;
        bool parryCooldown = tick - guardEntryTick < df.Rule("parryCooldown", U.rules.parryCooldown);

        bool Legal(string act)
        {
            if (exhausted) return false;
            if (act == "backstep") return canAct && df.stamina >= df.Rule("backstep.cost", U.rules.backstepCost);
            if (act == "light" || act == "heavy") return canAct || chainOpen;
            return canAct;
        }
        bool InReach(MoveDef m, string act)
        {
            return m != null && gap <= m.reach - D.reachInset && gap >= MinReach(m) + D.reachInset && Legal(act);
        }
        bool Charging(DuelFighter f)
        {
            return f.state == FState.Windup && f.move != null && f.move.charges && f.heavyHeld && f.t >= (int)f.move.chamber;
        }
        Intent Done()
        {
            if (intent.guard || intent.action == "parry") intent.guardDir = (threat && noticed && s.readSide != null) ? s.readSide : null;
            return intent;
        }
        AiPlan StepOrRoll(bool clears)
        {
            return df.stamina >= rollCost && !(p.step > 0f && clears && s.Roll() < p.step) ? AiPlan.Dodge : AiPlan.Evade;
        }

        // Special moves (off unless unity.useSpecials): cast whenever ready and in reach
        if (U.useSpecials && canAct && !exhausted && U.specialMoves != null)
        {
            for (int i = 0; i < U.specialMoves.Length && i < 2 && i < U.specialPress.Length; i++)
            {
                var sm = Mv(df, U.specialMoves[i]); int cd = i == 0 ? df.special1Cd : df.special2Cd;
                if (sm != null && cd <= 0 && gap <= Mathf.Min(U.specialReach, sm.reach) - D.reachInset) { intent.action = U.specialPress[i]; return intent; }
            }
        }

        // Hit and run: a blow that landed earns a hop back out of range
        if (pendingHit && p.disengage > 0f && s.Roll() < p.disengage) s.disengageUntil = tick + (int)D.disengageTicks;
        pendingHit = false;

        // ---- observe the opponent's habits (state edges) ----
        var h = s.habits; h.ticks += n;
        if (op == Ph.Guard) h.guard += n;
        bool oGuard = o.IsGuarding;
        if (oGuard && !oWasGuard && o.InParryWindow) h.parries++;
        oWasGuard = oGuard;
        h.hold = op == Ph.Guard ? h.hold + n : 0;
        // a guard held through my windup is a commitment: it ends the masher's run with no contact needed
        if (sp == Ph.Attack && df.state == FState.Active && st.serial != s.holdCheckedSerial)
        {
            s.holdCheckedSerial = st.serial;
            if (h.hold >= R.holdTicks) { h.run = R.decay > 0f ? Mathf.Max(0, h.run - (int)D.readDecayStep) : 0; h.latch = h.ticks + (int)R.latch; }
        }
        // a defence that actually meets a blow ends the run and latches the early gate off
        if (defenceMet) { h.run = R.decay > 0f ? Mathf.Max(0, h.run - (int)D.readDecayStep) : 0; h.latch = h.ticks + (int)R.latch; defenceMet = false; }
        // the first tick a swing sat at its chamber
        if (op == Ph.Attack && Charging(o) && ot.parkSerial != ot.serial) { ot.parkSerial = ot.serial; h.parks++; }
        Reads reads = AiRead.Read(h, p, R);

        if (sp != Ph.Attack) { s.hold = false; s.feint = false; }
        // A feinted swing: cancelled into a guard on the last feintable tick
        if (s.feint && sp == Ph.Attack && df.state == FState.Windup && df.t >= (int)U.feintUntil - 1)
        {
            s.feint = false;
            intent.action = "parry"; intent.guard = true; intent.feintNow = true;
            return intent;
        }
        if (s.hold && sp == Ph.Attack && df.move != null)
        {
            int myCharge = Mathf.Max(0, (tick - st.startTick) - AiWorld.AttackAge(df, st));
            intent.held = df.move.id == "heavy_overhead" ? myCharge < (int)U.chargeMin : myCharge < R.baitHold;
        }

        // Being hit: back off briefly, then decide afresh
        bool hurtNow = sp == Ph.Hurt, hurtEdge = hurtNow && !wasHurt; wasHurt = hurtNow;
        if (hurtEdge && guardShare == 0f && reads.poker && op == Ph.Attack && omv != null && omv.id == "thrust")
        {
            s.decision = 0f; s.plan = AiPlan.None; s.next = AiNext.None;
        }
        else if (hurtEdge)
        {
            s.retreatUntil = tick + (int)D.retreatTicks; s.decision = D.retreatTicks; s.mode = AiMode.Retreat; s.plan = AiPlan.None; s.next = AiNext.None;
            s.wait = Rnd((D.waitBase + s.Roll() * D.waitSpread) * (D.waitAggrCeil - p.aggression));
        }

        // ---- perception ----
        int oage = op == Ph.Attack ? AiWorld.AttackAge(o, ot) : o.t;
        int elapsedO = op == Ph.Attack ? tick - ot.startTick : o.t;
        threat = op == Ph.Attack && !ot.landed && omv != null && (o.state == FState.Windup || o.state == FState.Active);
        bool oLight = omv != null && (omv.id == "light_left" || omv.id == "light_right");
        bool oTell = omv != null && IsTell(A, omv.id);
        float reaction = reads.spammer && oLight ? Mathf.Min(p.reaction, p.anticipate >= 0f ? p.anticipate : R.anticipate)
            : p.tellReaction >= 0f && oTell ? Mathf.Min(p.reaction, p.tellReaction) : p.reaction;
        noticed = threat && elapsedO >= reaction;
        if (!threat) { s.plan = AiPlan.None; s.readSide = null; s.brace = false; }
        else if (ot.serial != s.plannedSerial && elapsedO >= reaction)
        {
            s.plannedSerial = ot.serial;
            MoveDef mv = omv;
            string want = Mirror(mv.direction);
            if (guardShare == 0f) s.readSide = null;
            else if (p.read < 0f || s.Roll() < p.read) s.readSide = want;
            else
            {
                string[] others = new string[4]; int k = 0;
                for (int i = 0; i < Sides.Length; i++) if (Sides[i] != want) others[k++] = Sides[i];
                s.readSide = others[Mathf.Min(3, (int)Mathf.Floor(s.Roll() * 4f))];
            }
            float r = s.Roll(); bool inRange = gapT <= mv.reach + D.rangeSlack;
            s.brace = false;   // needs a heavy-stopping guard (scutum); the Unity slice has none
            bool unblockable = mv.breaksGuard || Charging(o);
            bool affordable = df.stamina >= mv.staminaDamage;
            float backTravel = df.Rule("backstep.ticks", U.rules.backstepTicks) * df.Rule("backstep.speed", U.rules.backstepSpeed) * D.backstepTravelMul / U.ticksPerSecond * D.backstepSpeedShare;
            bool clears = gapT + backTravel > mv.reach + D.reachInset;
            if (inRange && !mv.parryable)
            {
                s.plan = r < p.dodge ? StepOrRoll(clears) : (s.readSide == "low" && guardShare > 0f && affordable ? AiPlan.Block : AiPlan.Ignore);
                s.jitter = Rnd((1f - p.accuracy) * D.jitterAmp * (s.Roll() * 2f - 1f));
            }
            else
            {
                float lapse = reads.spammer ? p.lapse * R.spammerLapseShare : p.lapse;
                float parryChance2 = reads.spammer ? Mathf.Min(R.parryCap, Mathf.Min(1f - p.dodge, p.parry * R.parryBoost)) : p.parry;
                if (!inRange || s.Roll() < lapse) s.plan = AiPlan.Ignore;
                else if (r < parryChance2 && !parryCooldown) s.plan = AiPlan.Parry;
                else if (r < parryChance2 + p.dodge && df.stamina >= rollCost) s.plan = AiPlan.Dodge;
                else if (unblockable)
                    s.plan = df.stamina >= rollCost && !(p.step > 0f && clears && s.Roll() < p.step) ? AiPlan.Dodge : (!parryCooldown && guardShare > 0f ? AiPlan.Parry : AiPlan.Evade);
                else s.plan = affordable && (guardShare >= 1f || s.Roll() < guardShare) ? AiPlan.Block : AiPlan.Evade;

                // tactic table reaction slot (only for a profile that names a row)
                var reactions = AiTactic.HeroWinding(p.tactic);
                if (reactions != null && s.plan != AiPlan.Ignore)
                {
                    string pick = AiTactic.Pick(reactions, s.Roll());
                    if (pick == "backstep") s.plan = AiPlan.Evade;
                    else if (guardShare > 0f && pick == "parry" && !parryCooldown) s.plan = AiPlan.Parry;
                    else if (guardShare > 0f && pick == "brace" && affordable) s.plan = AiPlan.Block;
                }
                // late notice (COMBAT-001): only for a profile with softNotice
                float spare = ot.windupTot - reaction;
                if (s.plan != AiPlan.Ignore && p.softNotice && !oTell && !ot.chained && spare > 0f && spare < R.lateNotice && s.Roll() >= (spare - 1f) / (R.lateNotice - 1f)) s.plan = AiPlan.Ignore;
                s.jitter = Rnd((1f - p.accuracy) * D.jitterAmp * (s.Roll() * 2f - 1f));
            }
        }
        else if (noticed && s.plan == AiPlan.Block && Charging(o) && !s.brace)
        {
            s.plan = df.stamina >= rollCost ? AiPlan.Dodge : (!parryCooldown && guardShare > 0f ? AiPlan.Parry : AiPlan.Evade);
        }

        // ---- openings ----
        bool opening = (op == Ph.Hurt && o.t >= p.reaction) || o.stamina <= 0f
            || (op == Ph.Attack && !ot.landed && oage - ot.windupTot - ot.activeTot >= p.reaction)
            || (reads.roller && op == Ph.Roll && o.t >= safeEnd);
        bool guarded = op == Ph.Guard && o.t >= p.reaction;
        float thrustReach = mThrust != null ? mThrust.reach : 0f, lightReach = mLight != null ? mLight.reach : 0f;
        bool advancing = (op == Ph.Ready || op == Ph.Guard) && oMoved && oldLastGap > gap + D.advanceEps
            && gap < thrustReach - D.reachInset && gap > lightReach - D.reachInset;
        MoveDef oLightDef = Mv(o, "light_right");
        bool pressured = reads.spammer && op == Ph.Ready && oLightDef != null && gapT <= oLightDef.reach + D.reachInset;

        // ---- movement mode (seeded, bounded) ----
        if (sp != Ph.Hurt) { s.decision = Mathf.Max(0f, s.decision - n); s.wait = Mathf.Max(0f, s.wait - n); }
        if (s.wait <= 0f && s.next == AiNext.None && canAct)
        {
            float lightShare = reads.parryHappy ? Mathf.Max(p.pressure, R.baitShare) : p.pressure;
            s.next = s.Roll() < lightShare ? AiNext.Light : (h.attacks > 0 && s.Roll() < A.fight.thrustShare ? AiNext.Thrust : AiNext.Heavy);
        }
        // an opener the stamina bar can no longer pay for becomes a cut
        if (s.next == AiNext.Heavy && mHeavy != null && mHeavy.stamina > df.maxStamina) s.next = AiNext.Light;
        if (s.next == AiNext.Thrust && mThrust != null && mThrust.stamina > df.maxStamina) s.next = AiNext.Light;
        if (s.next == AiNext.Light && mLight != null && gap < MinReach(mLight) + D.reachInset)
        {
            if (InReachFrom(mHeavy, gap, D, true) && Legal("heavy")) s.next = AiNext.Heavy;
            else if (InReachFrom(mThrust, gap, D, true) && Legal("thrust")) s.next = AiNext.Thrust;
        }
        if (pressured && s.next == AiNext.Heavy) s.next = AiNext.Light;

        float floor = p.discipline * df.maxStamina / 100f;
        bool low = df.stamina < floor;
        bool shaky = df.posture >= df.Rule("posture.max", U.rules.postureMax) * D.shakyShare;
        if (s.decision <= 0f)
        {
            float r = s.Roll();
            s.decision = D.guardHoldBase + Mathf.Floor(r * D.guardHoldSpread); s.side = (s.seed & 1u) != 0u ? 1 : -1;
            s.mode = (low || shaky) ? (gap < D.lowRetreatGap || df.stamina < rollCost ? AiMode.Retreat : AiMode.Circle)
                : gap > D.farApproachGap ? AiMode.Approach : gap < 1f ? AiMode.Retreat : (r < D.guardRollShare * guardShare ? AiMode.Guard : AiMode.Circle);
        }
        if (gap > D.hardApproachGap || (!low && !shaky && gap > D.midApproachGap)) s.mode = AiMode.Approach;
        if (tick < s.retreatUntil || ((low || shaky) && gap < D.lowCloseRetreatGap)) s.mode = AiMode.Retreat;
        else if ((low || shaky) && s.mode == AiMode.Retreat && gap >= D.circleReturnGap) s.mode = AiMode.Circle;

        // ---- reaction to a noticed swing ----
        if (canAct && noticed && s.plan != AiPlan.None && s.plan != AiPlan.Ignore)
        {
            float estimate = ot.windupTot - oage + s.jitter;
            if (s.plan == AiPlan.Parry)
            {
                if (sp == Ph.Ready) { if (estimate <= parryWindow - D.parryWindowMargin && !parryCooldown) { intent.action = "parry"; intent.guard = true; } }
                else intent.guard = df.t < parryWindow || estimate <= D.estimateMax;
            }
            else if (s.plan == AiPlan.Dodge && df.stamina >= rollCost && estimate <= safeEnd - D.dodgeSafeEndMargin && estimate >= safeStart) intent.action = "dodge";
            else if (s.plan == AiPlan.Evade)
            {
                MoveDef bm = omv;
                if (Legal("backstep") && gapT <= bm.reach + D.backstepSlack) intent.action = "backstep";
                else { intent.mx = -dir.x; intent.mz = -dir.z; }
            }
            else if (guardShare > 0f) intent.guard = true;
            return Done();
        }

        // ---- offence: utility scores over what the opponent is committed to ----
        if ((canAct || chainOpen) && !exhausted)
        {
            float r = s.Roll();
            bool kickNow = ((guarded || reads.parryHappy) && r < R.guardedKickBase + (reads.turtle || reads.parryHappy ? R.kickBoost : 0f))
                || (p.kick > 0f && !threat && (reads.roller || reads.stepper) && r < p.kick);
            float sCritical = 0f;
            float sCounter = tick < st.riposteUntil && InReach(mHeavy, "heavy") ? 1.4f : 0f;
            float sPunish = opening && (InReach(mLight, "light") || (p.stab > 0f && InReach(mThrust, "thrust") && r < p.stab)) ? 1.5f : 0f;
            bool followThrust = df.move != null && df.move.chainFollow != null && System.Array.IndexOf(df.move.chainFollow, "thrust") >= 0;
            float sChain = chainOpen && (InReach(mLight, "light") || (followThrust && InReach(mThrust, "thrust"))) && r < p.aggression ? 1.2f : 0f;
            float sKick = InReach(mKick, "kick") && kickNow ? D.kickWeight + (reads.turtle ? R.kickBoost : 0f) : 0f;
            float sHeavy = guarded && InReach(mHeavy, "heavy") ? 1f : (s.next == AiNext.Heavy && !threat && !pressured && InReach(mHeavy, "heavy") ? 0.8f : 0f);
            float sLight = s.next == AiNext.Light && !threat && !guarded && InReach(mLight, "light") ? 0.8f : 0f;
            float sInterrupt = p.interrupt > 0f && threat && omv != null && !ot.landed && mLight != null
                && ot.windupTot - oage > mLight.Windup(false) + D.interruptWindupMargin && InReach(mLight, "light") && r < p.interrupt ? 1.3f : 0f;
            float sThrust = !threat && !guarded && InReach(mThrust, "thrust") && s.next != AiNext.None && (advancing || s.next == AiNext.Thrust) ? (advancing ? 0.9f : 0.8f) : 0f;
            if (low) { sChain = 0f; sKick = 0f; sHeavy = 0f; sLight = 0f; sThrust = 0f; sInterrupt = 0f; }   // stamina discipline: only punishes below the floor
            if (!canAct) { sCritical = 0f; sCounter = 0f; sPunish = 0f; sKick = 0f; sHeavy = 0f; sLight = 0f; sInterrupt = 0f; sThrust = 0f; }   // mid-recovery: only the chain follow-up
            // first strictly-greater wins in V3's insertion order (stable sort)
            string best = "none"; float score = 0f;
            if (sCritical > score) { score = sCritical; best = "critical"; }
            if (sCounter > score) { score = sCounter; best = "counter"; }
            if (sPunish > score) { score = sPunish; best = "punish"; }
            if (sChain > score) { score = sChain; best = "chain"; }
            if (sKick > score) { score = sKick; best = "kick"; }
            if (sHeavy > score) { score = sHeavy; best = "heavy"; }
            if (sLight > score) { score = sLight; best = "light"; }
            if (sInterrupt > score) { score = sInterrupt; best = "interrupt"; }
            if (sThrust > score) { score = sThrust; best = "thrust"; }
            lastScores = best + " " + score.ToString("0.0");
            if (score > 0f)
            {
                string action = best == "kick" ? "kick"
                    : (best == "heavy" || best == "critical" || best == "counter") ? "heavy"
                    : (best == "thrust" || ((best == "chain" || best == "punish") && !InReach(mLight, "light"))) ? "thrust" : "light";
                s.wait = Rnd((D.waitBase + s.Roll() * D.waitSpread) * (D.waitAggrCeil - p.aggression)); s.next = AiNext.None;
                // a less aggressive warden sometimes baits instead: a visible guard the player must open
                bool baitable = best == "heavy" || best == "light" || (best == "thrust" && !advancing);
                if (baitable && !guarded && s.Roll() < (1f - p.aggression) * D.baitChance * guardShare)
                {
                    s.mode = AiMode.Guard; s.decision = D.guardHoldBase + Mathf.Floor(s.Roll() * D.guardHoldSpread); intent.guard = true;
                    return Done();
                }
                float chargeChance = p.aggression - D.chargeOffset + (reads.turtle || reads.roller || reads.parryHappy ? R.chargeBoost : 0f);
                if (best == "heavy") s.hold = (guarded || reads.roller || reads.parryHappy) && s.Roll() < chargeChance;
                else s.hold = best == "light" && reads.parryHappy && s.Roll() < R.baitShare;
                float feintChance = Mathf.Max(p.feint, reads.parryHappy ? R.feint : 0f);
                s.feint = feintChance > 0f && (action == "heavy" || action == "light") && !s.hold && s.Roll() < feintChance;
                intent.action = action; intent.held = s.hold;
                return intent;
            }
        }
        if (!canAct) return Done();   // mid-attack / hurt / rolling: nothing to steer

        // against a read cut-spammer the warden walks in guard inside cutting range when it is not swinging
        if (pressured && canAct && !exhausted && !threat && !low && guardShare > 0f) intent.guard = true;
        if (s.mode == AiMode.Guard && canAct && !exhausted) { intent.guard = true; return Done(); }
        // hit and run: the hop back out after a landed blow
        if (tick < s.disengageUntil && canAct && !threat && Legal("backstep"))
        {
            s.disengageUntil = 0; intent.action = "backstep"; return intent;
        }

        // ---- closing distance ----
        bool cramped = s.mode == AiMode.Approach && mThrust != null && gap < MinReach(mThrust) + D.reachInset;
        float closeTo = s.next == AiNext.Thrust ? thrustReach - D.thrustCloseInset : A.fight.close;
        float forward = s.mode == AiMode.Approach && gap > closeTo ? D.forwardSpeed
            : s.mode == AiMode.Retreat && gap < (low ? D.retreatGapLow : D.retreatGapNormal) ? D.backSpeed
            : cramped ? D.backSpeed : 0f;
        // (hover logic for a guardless fighter vs a read poker/kicker is not ported: the Pitborn always guards)
        float lateral = s.mode == AiMode.Circle ? s.side * D.lateralStep : (s.mode == AiMode.Approach && forward != 0f ? s.side * D.lateralStep * p.circle : 0f);
        bool dash = p.dash > 0f && opening && forward > 0f && gap > A.fight.close + D.dashSlack && df.stamina > rollCost && s.Roll() < p.dash;
        Vector3 lat = new Vector3(dir.z, 0f, -dir.x);   // V3 (cos f, -sin f)
        Vector3 mvec = dir * forward + lat * lateral;
        intent.mx = mvec.x; intent.mz = mvec.z; intent.run = dash; intent.closing = forward > 0f;
        return Done();
    }

    // V3 moves carry an optional minReach (pole point); the Unity MoveDef table has none, so 0
    static float MinReach(MoveDef m) { return 0f; }
    static bool InReachFrom(MoveDef m, float gap, AiDialRows D, bool ignoreLegal)
    {
        return m != null && gap >= MinReach(m) + D.reachInset && gap <= m.reach - D.reachInset;
    }
    static bool IsTell(AiRowsFile A, string id)
    {
        if (A.tells == null) return false;
        for (int i = 0; i < A.tells.Length; i++) if (A.tells[i] == id) return true;
        return false;
    }

    // ---------------- intent -> DuelFighter inputs ----------------
    void Apply(Intent it)
    {
        var U = AiRows.I.unity; var o = df.opponent;
        df.heavyHeld = it.held;
        if (it.feintNow) df.Feint();
        bool free = df.state == FState.Loco || df.state == FState.Guard;
        if (!free) { df.moveDir = Vector3.zero; df.run = false; }
        else
        {
            Vector3 v = new Vector3(it.mx, 0f, it.mz) * U.moveGain;
            // soft bias to hold the uphill / downhill side of the stairs (scene geometry; data in ai_rows.json unity.zBias)
            Vector3 to = o.transform.position - transform.position; to.y = 0f;
            if (U.zBias != null && to.magnitude <= U.zBiasBaseDist + U.zBiasScaleDist * df.scale)
            {
                for (int i = 0; i < U.zBias.Length; i++)
                {
                    var zb = U.zBias[i]; if (zb == null || zb.name != name) continue;
                    float z = transform.position.z, lim = o.transform.position.z + zb.offset;
                    if (zb.cmp == "gt" ? z > lim : z < lim) v += new Vector3(0f, 0f, zb.push);
                }
            }
            df.moveDir = Vector3.ClampMagnitude(v, 1f);
            df.run = it.run || (it.closing && to.magnitude > U.runBeyondDist);
        }
        bool wantGuard = (it.guard || it.action == "parry") && df.state != FState.Roll;
        df.guardHeld = wantGuard;
        if (wantGuard) df.guardSide = it.guardDir == "left" ? 1 : it.guardDir == "right" ? 2 : it.guardDir == "overhead" ? 3 : 0;
        if (it.action == null || it.action == "parry") return;
        if (it.action == "dodge") df.Press("roll");
        else df.Press(it.action);   // backstep, light, heavy, thrust, kick, special1/2
    }
}
