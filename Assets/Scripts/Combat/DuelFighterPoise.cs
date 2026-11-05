using UnityEngine;

// Poise / hyper-armour state, flow-layer balance counters and the small accessors FlowBot needs (lane V64P). Second half of the partial DuelFighter (see DuelFighterFlow.cs).
// Everything here is bookkeeping: the rules are in PoiseTable.cs (poise.json) and FlowTable.cs (flow.json); hooks: Tick -> PoiseTick, BeginDodge / TryCancel -> stat counters,
// DuelFighterFlow -> ComboStart / ComboEnd / flowChainNext / statMagnetSnaps.
public partial class DuelFighter
{
    // ---- poise meter ----
    int poiseHits, poiseLastHit = -100000, poiseGrace;
    public int PoiseGrace => poiseGrace;
    public int PoiseHits => poiseHits;
    public int statPoiseAbsorbs, statArmourHits;     // BalanceAuto: hits absorbed by the meter (break-outs), hits that landed on hyper-armour

    // count one interrupting hit inside the rolling window
    public void PoiseCount(int tick, int window) { if (tick - poiseLastHit > window) poiseHits = 0; poiseHits++; poiseLastHit = tick; }
    // hits still remembered at `tick`
    public int PoiseCountNow(int tick, int window) { if (poiseHits > 0 && tick - poiseLastHit > window) poiseHits = 0; return poiseHits; }
    public void PoiseReset() { poiseHits = 0; poiseLastHit = -100000; }
    // Duel.Resolve, absorbed hit: cut a running stagger short and open the grace window (hits damage but never interrupt)
    public void PoiseBreakOut(int ticks)
    {
        if (state == FState.Dead) return;
        poiseGrace = Mathf.Max(poiseGrace, ticks); PoiseReset();
        if (state == FState.Stagger) ToLoco();
    }
    void PoiseTick()
    {
        if (poiseGrace > 0) { poiseGrace--; if (poiseGrace == 0) PoiseReset(); }
        if (flowBotOn && flowBotDir != null)   // the balance bot's plain roll / backstep press gets a direction label like the human's (FlowTable.DodgePress = the same mapping PlayerBrain uses)
        {
            if (pRoll > 0 && pendDir.Length == 0 && FlowTable.Get().bot.dodgeDir && FlowTable.Get().dodge.enabled) { string dd = flowBotDir(); if (!string.IsNullOrEmpty(dd)) { pRoll = 0; FlowTable.DodgePress(this, dd); statDirDodges++; } }
            else if (pBack > 0 && pendDir.Length == 0 && FlowTable.Get().bot.dodgeDir) { pendDir = "back"; statDirDodges++; }
        }
        if (flowGuardDrop > 0) { flowGuardDrop--; guardHeld = false; }   // V65BOT2: release a stale guard for a tick so the re-raise gets a fresh parry window
        else if (flowGuardForce > 0) { flowGuardForce--; guardHeld = true; }   // FlowBot: a held guard key for a few ticks (the cancel-into-guard window of cancel.json)
    }

    // ---- flow layer on the balance bot ----
    public bool flowBotOn;          // set by BalanceAuto (flow.json applyToBot): FlowTable.Applies treats this fighter like the human
    int flowGuardForce; public System.Func<string> flowBotDir;   // FlowBot sets it: picks a flow.json direction label
    public void FlowForceGuard(int ticks) { flowGuardForce = Mathf.Max(flowGuardForce, ticks); }
    public bool FlowQueuedRoll => pRoll > 0 && pendDir.Length == 0;      // AIBrain pressed a plain roll this frame (no direction label yet)
    public bool FlowQueuedBack => pBack > 0 && pendDir.Length == 0;
    public void FlowDropQueuedRoll() { pRoll = 0; }
    public void FlowLabelBack() { pendDir = "back"; }
    public bool FlowAttackQueued => pLight > 0 || pHeavy > 0 || pThrust > 0 || pKick > 0 || pMove != null;
    public bool FlowDodgeQueued => pRoll > 0 || pBack > 0 || pSide > 0;
    public bool FlowChainBlocked => pMove != null;
    public int RecoveryLen => move != null ? Tk(move.Recovery(chained)) : 0;
    public bool FlowChainedMove => chained;

    // ---- balance counters (BalanceAuto reads these; counted for every fighter) ----
    public int statCancelDodge, statCancelGuard, statCancelAtk;                       // recovery cancelled into a dodge / the guard / a buffered attack at the cancel.json tick
    public int statComboCount, statComboSum, statComboMax, statMagnetSnaps;          // strings of linked attacks (a lone swing is a string of 1), longest, magnetism snaps at swing start
    public int statDirDodges;                                                        // dodges that carried a flow.json direction label
    int comboLen; bool flowChainNext;

    void ComboStart(MoveDef m)
    {
        bool cont = comboLen > 0 && (flowChainNext || chained);
        if (!cont && comboLen > 0) ComboEnd();
        flowChainNext = false; comboLen = cont ? comboLen + 1 : 1;
    }
    void ComboEnd()
    {
        if (comboLen <= 0) return;
        statComboCount++; statComboSum += comboLen; if (comboLen > statComboMax) statComboMax = comboLen; comboLen = 0;
    }
    public void FlowComboFlush() { ComboEnd(); }   // end of a fight: close a string still running
}
