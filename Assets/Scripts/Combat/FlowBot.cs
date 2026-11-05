using UnityEngine;

// The human's flow features for the BALANCE BOT (lane V64P, reworked by lane V65BOT). BalanceAuto adds this to Dracula (AIBrain "auto" playerBot profile, PlayerBrain off) when flow.json "applyToBot"
// is true and sets DuelFighter.flowBotOn, so FlowTable.Applies() is true for him and the SAME DuelFighterFlow code paths the human uses run: attack-to-attack chain rows (FlowChain), magnetism + turn cap
// (FlowAttackStart / FlowFace), directional dodge overrides (FlowDodgeBegin), the riposte key (PressRiposte -> FlowRiposteFree) and the cancel table (TryCancel).
// This component only supplies the key presses a human would make; the AIBrain is untouched and keeps deciding movement, guard, parry and its own attacks. Every number: flow.json "bot".
// Seeded from AIBrain.GlobalSeed so a seed gives the same choices. Inert unless flowBotOn is set (never in the shipped game, only -balanceauto).
// V65BOT changes (all numbers in flow.json "bot"): the chain stamina gate is the cost of the chosen follow-up (+ chainReserve) instead of a flat 30; reach counts the follow-up's step-in;
// a queued AI dodge no longer blocks a chain the bot chose; heavy / special wind-ups of the foe inside cancelHeavyDist get a dodge cancel with their own chance and lead window;
// the riposte key is pressed 2..7 ticks after the parry (inside the 8-tick parry recovery, so it beats the AI's own strike press); riposte reach counts its step-in.
// Every decision is counted (Diag()) so a run shows WHICH gate stopped a chain / cancel / riposte.
[DefaultExecutionOrder(20)]
public class FlowBot : MonoBehaviour
{
    DuelFighter df; uint rng = 1u; int lastTick = -1;
    // chain
    int recStart = -1, pressAt = -1, links, chainsSeen; string pressType = "";
    // cancel
    int cancelKey = -1; bool cancelTake, cancelGuard, cancelHeavy;
    // riposte
    int ripWin = -1, ripDue; bool ripTake;
    // diagnostics (BalanceAuto prints Diag() per fight)
    int dChain, dLinks, dExh, dChance, dQueued, dNoRow, dNoReach, dNoStam, dPressed, dPressReach, dPressDodge, dDropped;
    int dCOpp, dCOppHeavy, dCTake, dCDodge, dCGuard, dCStam, dCCd, dCQueued, dCDist;
    int dRWin, dRTake, dRKey, dRAi, dRReach, dRBusy;
    int dPOpp, dPTake, dPTry, dPFresh, dPStam, dPCd, dPFar;   // active parry
    int parKey = -1; bool parTake;

    void Awake() { df = GetComponent<DuelFighter>(); Seed(); }
    void OnEnable() { if (df != null) df.flowBotDir = PickDir; }
    void OnDisable() { if (df != null && df.flowBotDir == (System.Func<string>)PickDir) df.flowBotDir = null; }
    public void Seed()
    {
        rng = unchecked((uint)AIBrain.GlobalSeed * 2654435761u + 0x9E3779B9u) ^ 0xA5A5F00Du; if (rng == 0u) rng = 1u;
        if (df != null) df.flowBotDir = PickDir;
    }
    float R() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (rng & 0xFFFFFFu) / 16777216f; }

    public string Diag()
    {
        return "bot chain decisions " + dChain + " (stopped: links " + dLinks + " exhausted " + dExh + " chance " + dChance + " ai-queued " + dQueued + " no-row " + dNoRow + " out-of-reach " + dNoReach + " stamina " + dNoStam + ")"
            + " pressed " + dPressed + " (late out-of-reach " + dPressReach + " dodge-queued " + dPressDodge + ", dropped ai-dodge " + dDropped + ")"
            + " | cancel foe-swings " + dCOpp + " (heavy " + dCOppHeavy + ") taken " + dCTake + " pressed dodge " + dCDodge + " guard " + dCGuard + " (skipped: stamina " + dCStam + " dodge-cd " + dCCd + " queued " + dCQueued + " far " + dCDist + ")"
            + " | riposte windows " + dRWin + " take " + dRTake + " key " + dRKey + " (ai first " + dRAi + ", out-of-reach ticks " + dRReach + ", busy ticks " + dRBusy + ")"
            + " | parry foe-swings " + dPOpp + " take " + dPTake + " raised " + dPTry + " (skipped: fresh " + dPFresh + " stamina " + dPStam + " cooldown " + dPCd + " far " + dPFar + ")";
    }

    // direction label of a bot dodge (called from DuelFighter.PoiseTick when AIBrain's plain roll is about to be consumed)
    string PickDir()
    {
        var B = FlowTable.Get().bot; float wl = Mathf.Max(0f, B.dirLeft), wr = Mathf.Max(0f, B.dirRight), wb = Mathf.Max(0f, B.dirBack), wf = Mathf.Max(0f, B.dirForward);
        float tot = wl + wr + wb + wf; if (tot <= 0f) return "forward";
        float x = R() * tot;
        if ((x -= wl) < 0f) return "left";
        if ((x -= wr) < 0f) return "right";
        if ((x -= wb) < 0f) return "back";
        return "forward";
    }

    // centre distance the move can reach: reach x scale + both radii + the contact slack of Duel.Resolve (0.45) + the share of the move's step-in that closes the gap during the wind-up - margin
    bool InReach(MoveDef m, DuelFighter o, float margin, float stepShare)
    {
        if (m == null || o == null) return false;
        Vector3 v = o.transform.position - transform.position; v.y = 0f;
        return v.magnitude <= m.reach * df.scale + df.radius + o.radius + 0.45f + m.stepIn * stepShare - margin;
    }

    static bool MatchAny(string[] pre, string id)
    {
        if (pre == null || string.IsNullOrEmpty(id)) return false;
        for (int i = 0; i < pre.Length; i++) if (!string.IsNullOrEmpty(pre[i]) && id.StartsWith(pre[i], System.StringComparison.Ordinal)) return true;
        return false;
    }

    void Update()
    {
        if (df == null) return;
        var C = FlowTable.Get();
        if (!C.enabled || !df.flowBotOn || !FlowTable.Applies(df) || Duel.Frozen || df.Dead) return;
        var o = df.opponent; if (o == null || o.Dead) return;
        int tick = Duel.TickCount; if (tick == lastTick) return; lastTick = tick;
        var B = C.bot;
        if (B.chain) ChainLogic(o, B, tick);
        if (B.cancel) CancelLogic(o, B, tick);
        if (B.riposte && C.riposte.enabled) RiposteLogic(o, B, C, tick);
        if (B.parry) ParryLogic(o, B, tick);
    }

    // ---- active parry: the AI profile holds the guard (guard 1), so the first-ticks parry window is almost never fresh. On a parryable foe wind-up the bot re-raises the guard so the window covers contact ----
    void ParryLogic(DuelFighter o, FlowTable.BotCfg B, int tick)
    {
        if (df.state != FState.Loco && df.state != FState.Guard) return;
        if (o.state != FState.Windup || o.CurrentMove == null || !o.CurrentMove.parryable) return;
        int key = tick - o.t;
        if (key != parKey) { parKey = key; dPOpp++; parTake = R() < B.parryTryChance; if (parTake) dPTake++; }
        if (!parTake) return;
        int left = o.WindupLeft;
        if (left > B.parryLeadMax || left < B.parryLeadMin) return;
        { Vector3 v = df.transform.position - o.transform.position; v.y = 0f; if (v.magnitude > o.CurrentMove.reach * o.scale + o.radius + df.radius + 0.75f) { dPFar++; parTake = false; return; } }
        parTake = false;
        if (df.Exhausted || df.stamina <= 0f) { dPStam++; return; }
        if (df.state == FState.Guard && df.InParryWindow) { dPFresh++; return; }   // the guard just went up: the window is already open
        if (!df.FlowParryReady) { dPCd++; return; }
        df.FlowParryRaise(df.state == FState.Guard, B.parryHoldTicks); dPTry++;
    }

    // ---- attack chains: a human string = press the next attack inside the recovery window of flow.json chain.rows ----
    void ChainLogic(DuelFighter o, FlowTable.BotCfg B, int tick)
    {
        if (df.state != FState.Recovery || df.move == null) { if (df.state != FState.Recovery && df.state != FState.Windup && df.state != FState.Active) { links = 0; pressAt = -1; recStart = -1; } return; }
        int rs = tick - df.t;
        if (rs != recStart)   // first frame of this recovery: decide once
        {
            recStart = rs; pressAt = -1; pressType = "";
            if (df.flowChains != chainsSeen) { chainsSeen = df.flowChains; links++; } else links = 0;
            bool hit = df.HitDone;
            float p = (hit ? B.chainOnHit : B.chainOnWhiff) * Mathf.Pow(Mathf.Clamp01(B.chainDecay), links);
            dChain++;
            if (B.maxLinks > 0 && links >= B.maxLinks) dLinks++;
            else if (df.Exhausted) dExh++;
            else if (R() >= p) dChance++;
            else if (df.FlowAttackQueued) dQueued++;   // the AI already pressed an attack: FlowChain runs it through the same rows
            else
            {
                float wl = B.wLight, wh = B.wHeavy, wt = B.wThrust, wk = B.wKick;
                var rl = FlowTable.ChainRowFor(df.move.id, "light", hit); var rh = FlowTable.ChainRowFor(df.move.id, "heavy", hit);
                var rt = FlowTable.ChainRowFor(df.move.id, "thrust", hit); var rk = FlowTable.ChainRowFor(df.move.id, "kick", hit);
                if (rl == null) wl = 0f; if (rh == null) wh = 0f; if (rt == null) wt = 0f; if (rk == null) wk = 0f;
                bool anyRow = wl + wh + wt + wk > 0f;
                MoveDef ml = df.moves.Get("light_right"), mh = df.moves.Get("heavy_overhead"), mt = df.moves.Get("thrust"), mk = df.moves.Get("kick");
                if (!InReach(ml, o, B.reachMargin, B.stepInShare)) wl = 0f;
                if (!InReach(mh, o, B.reachMargin, B.stepInShare)) wh = 0f;
                if (!InReach(mt, o, B.reachMargin, B.stepInShare)) wt = 0f;
                if (!InReach(mk, o, B.reachMargin, B.stepInShare)) wk = 0f;
                bool anyReach = wl + wh + wt + wk > 0f;
                // stamina: the follow-up must be paid for (stamina.json cost) and leave chainReserve
                float floor = Mathf.Max(B.minStamina, 0f);
                if (df.stamina < floor || df.stamina < df.FlowMoveCost(ml) + B.chainReserve) wl = 0f;
                if (df.stamina < floor || df.stamina < df.FlowMoveCost(mh) + B.chainReserve) wh = 0f;
                if (df.stamina < floor || df.stamina < df.FlowMoveCost(mt) + B.chainReserve) wt = 0f;
                if (df.stamina < floor || df.stamina < df.FlowMoveCost(mk) + B.chainReserve) wk = 0f;
                float tot = wl + wh + wt + wk;
                if (tot > 0f)
                {
                    float x = R() * tot; FlowTable.ChainRow row;
                    if ((x -= wl) < 0f) { pressType = "light"; row = rl; }
                    else if ((x -= wh) < 0f) { pressType = "heavy"; row = rh; }
                    else if ((x -= wt) < 0f) { pressType = "thrust"; row = rt; }
                    else { pressType = "kick"; row = rk; }
                    pressAt = rs + Mathf.Max(1, row.earliest - 1) + (int)(R() * (B.jitterTicks + 1));
                    if (row.latest > 0) pressAt = Mathf.Min(pressAt, rs + row.latest - 1);
                }
                else if (!anyRow) dNoRow++; else if (!anyReach) dNoReach++; else dNoStam++;
            }
        }
        if (pressAt >= 0 && tick >= pressAt && pressType.Length > 0)
        {
            string id = pressType == "heavy" ? "heavy_overhead" : pressType == "light" ? "light_right" : pressType;
            bool threat = o.state == FState.Windup;   // the foe is swinging: a dodge the bot pressed on purpose (cancel) wins over the chain
            if (df.FlowAttackQueued) { }
            else if (!InReach(df.moves.Get(id), o, B.reachMargin * 0.5f, B.stepInShare)) dPressReach++;
            else if (df.FlowDodgeQueued && (threat || !B.chainDropsDodge)) dPressDodge++;
            else
            {
                if (df.FlowDodgeQueued) { df.FlowDropQueuedDodges(); dDropped++; }
                df.Press(pressType); dPressed++;
            }
            pressAt = -1; pressType = "";
        }
    }

    // ---- attack cancel: an incoming swing while the bot is still in recovery -> dodge (directional) or guard at the cancel.json tick ----
    // A heavy / critical / special wind-up of the foe (flow.json bot.cancelFoeHeavy prefixes) inside cancelHeavyDist gets its own chance and lead window and is always answered with a dodge.
    void CancelLogic(DuelFighter o, FlowTable.BotCfg B, int tick)
    {
        if (df.state != FState.Recovery || o.state != FState.Windup || o.CurrentMove == null) return;
        int key = tick - o.t;
        if (key != cancelKey)
        {
            cancelKey = key; dCOpp++;
            cancelHeavy = MatchAny(B.cancelFoeHeavy, o.CurrentMove.id); if (cancelHeavy) dCOppHeavy++;
            cancelTake = R() < (cancelHeavy ? B.cancelHeavyChance : B.cancelChance); cancelGuard = !cancelHeavy && R() < B.cancelGuardShare;
            if (cancelTake) dCTake++;
        }
        if (!cancelTake) return;
        int left = o.WindupLeft;
        int lo = cancelHeavy ? B.cancelHeavyLeadMin : B.cancelLeadMin, hi = cancelHeavy ? B.cancelHeavyLeadMax : B.cancelLeadMax;
        if (left > hi || left < lo) return;
        { Vector3 v = df.transform.position - o.transform.position; v.y = 0f; float lim = cancelHeavy ? B.cancelHeavyDist : o.CurrentMove.reach * o.scale + o.radius + df.radius + 0.75f; if (v.magnitude > lim) { dCDist++; cancelTake = false; return; } }   // the swing cannot reach him anyway
        cancelTake = false;
        if (cancelGuard) { if (!df.FlowDodgeQueued) { df.FlowForceGuard(B.guardHoldTicks); dCGuard++; } else dCQueued++; }
        else if (df.stamina < B.cancelMinStamina) dCStam++;
        else if (!df.FlowDodgeReady) dCCd++;
        else if (df.FlowDodgeQueued) dCQueued++;
        else { FlowTable.DodgePress(df, PickDir()); dCDodge++; }
    }

    // ---- riposte key: after a parry take the riposte with the dedicated key a few ticks later (inside the parry recovery the press just waits) ----
    void RiposteLogic(DuelFighter o, FlowTable.BotCfg B, FlowTable.Data C, int tick)
    {
        if (df.RiposteLeft <= 0) return;
        int win = tick + df.RiposteLeft;
        if (win != ripWin) { ripWin = win; ripPressed = false; dRWin++; ripTake = R() < B.riposteChance; if (ripTake) dRTake++; ripDue = tick + B.riposteReactMin + (int)(R() * (Mathf.Max(B.riposteReactMin, B.riposteReactMax) - B.riposteReactMin + 1)); }
        if (!ripTake || tick < ripDue) return;
        if ((df.state == FState.Windup || df.state == FState.Active || df.state == FState.Recovery) && df.CurrentMove != null && PoiseTable.IsRiposte(df.CurrentMove)) { ripTake = false; if (!ripPressed) dRAi++; return; }   // the AI already took it
        if (df.state != FState.Loco && df.state != FState.Guard && df.state != FState.ParryRecover) { dRBusy++; return; }
        var rm = df.moves.Get(string.IsNullOrEmpty(C.riposte.move) ? "riposte" : C.riposte.move);
        if (!InReach(rm, o, -B.riposteSlack, B.stepInShare)) { dRReach++; return; }
        df.PressRiposte(); ripTake = false; ripPressed = true; dRKey++;
    }
    bool ripPressed;
}
