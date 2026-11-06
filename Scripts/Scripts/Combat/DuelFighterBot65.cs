using UnityEngine;

// Lane V65BOT: small accessors FlowBot needs (third part of the partial DuelFighter, see DuelFighterFlow.cs / DuelFighterPoise.cs). No rules here, no state, no hooks.
public partial class DuelFighter
{
    public bool FlowDodgeReady => dodgeCd <= 0;                                   // a queued dodge can start (the dodge cooldown is over)
    public float FlowMoveCost(MoveDef m) { return m != null ? MoveCost(m) : 0f; }  // stamina the move costs (stamina.json costs), for the bot's "can I afford the follow-up" test
    // the bot decided to chain: forget a plain dodge press that is still queued (a human who presses the next attack does not also hold the dodge key)
    public void FlowDropQueuedDodges() { pRoll = 0; pBack = 0; pSide = 0; }
    // V65BOT2: active parry. A guard held since long ago has no parry window (the window is the first parry.json windowTicks after the guard goes up, and only when the parry cooldown is over);
    // FlowBot releases a stale guard for one tick and raises it again so the window covers the foe's contact.
    int flowGuardDrop;
    public bool FlowParryReady => parryCd <= 0;
    public void FlowParryRaise(bool dropFirst, int holdTicks) { if (dropFirst) flowGuardDrop = 1; FlowForceGuard(holdTicks); }
}
