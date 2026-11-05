using UnityEngine;

// Tactic windows (strategic, ~3 s): each overlays the player's AI profile and forces a few AiState fields (mode, next attack, wait, retreat/disengage timers)
// from AIBrain's per-frame pre-hook. The AI's own reactive logic (parry / dodge / block of a noticed swing) keeps running, biased by the overlay.
// Cooldowns count sim ticks (Duel.TickCount), never Time.time.
public class STTactics
{
    public STCfg cfg;
    public DuelFighter d, o;
    public AIBrain brain;
    public int Running = -1;                         // (int)STTactic or -1
    public STTacticDef Def;
    public int StartTick, EndTick;
    readonly int[] cdStart = new int[7], cdLen = new int[7], uses = new int[7];
    uint rng = 7;
    int pendingKind;                                  // 0 none, 1 backstep, 2 sidestep
    int pendingUntil, nextSpecialTick;
    // per-tactic outcome stats (for the log / feed)
    public float dmgDealt, dmgTaken; public int parries, blocks, dodges, hitsDealt, hitsTaken;

    static int Tps { get { var u = AiRows.I != null ? AiRows.I.unity : null; return u != null && u.ticksPerSecond > 0 ? (int)u.ticksPerSecond : 60; } }
    public void Seed(int s) { rng = unchecked((uint)s * 2246822519u + 99u); if (rng == 0) rng = 1; }
    float Roll() { rng = unchecked(rng * 1664525u + 1013904223u); return (float)(rng / 4294967296.0); }

    public void Reset()
    {
        if (Running >= 0) End("fight reset");
        for (int i = 0; i < 7; i++) { cdStart[i] = 0; cdLen[i] = 0; uses[i] = 0; }
        pendingKind = 0;
    }

    public float Cooldown01(STTactic t)
    {
        int i = (int)t; var def = cfg != null ? cfg.Tactic(t) : null;
        if (def != null && def.usesPerFight > 0 && uses[i] >= def.usesPerFight) return 1f;
        if (cdLen[i] <= 0) return 0f;
        int left = cdStart[i] + cdLen[i] - Duel.TickCount;
        return left <= 0 ? 0f : Mathf.Clamp01(left / (float)cdLen[i]);
    }
    public bool Enabled(STTactic t)
    {
        if (cfg == null || d == null || o == null || d.Dead || o.Dead) return false;
        var def = cfg.Tactic(t); if (def == null) return false;
        int i = (int)t;
        if (def.usesPerFight > 0 && uses[i] >= def.usesPerFight) return false;
        if (def.foeHealthFracMax > 0f && o.health / Mathf.Max(1f, o.maxHealth) > def.foeHealthFracMax) return false;
        if (def.staminaMin > 0f && d.stamina < def.staminaMin) return false;
        if (t == STTactic.GoForKill && d.special1Cd > 0) return false;
        return true;
    }
    public float TimeLeft(STTactic t)
    {
        if (Running != (int)t) return 0f;
        return Mathf.Max(0f, (EndTick - Duel.TickCount) / (float)Tps);
    }

    public bool Start(STTactic t)
    {
        if (!Enabled(t) || Cooldown01(t) > 0f || Running == (int)t) return false;
        var def = cfg.Tactic(t);
        if (Running >= 0) End("replaced by " + t);
        int now = Duel.TickCount, i = (int)t;
        Running = i; Def = def; StartTick = now; EndTick = now + Mathf.Max(1, (int)(def.maxSeconds * Tps));
        cdStart[i] = now; cdLen[i] = (int)(def.cooldownSeconds * Tps); uses[i]++;
        dmgDealt = dmgTaken = 0f; parries = blocks = dodges = hitsDealt = hitsTaken = 0;
        var s = brain != null ? brain.ai : null;
        if (s != null)
        {
            if (def.flipSide) s.side = -s.side;
            if (def.retreatUntilTicks > 0) { s.retreatUntil = now + def.retreatUntilTicks; s.mode = AiMode.Retreat; s.decision = def.retreatUntilTicks; s.plan = AiPlan.None; s.next = AiNext.None; }
            if (def.disengageTicks > 0) s.disengageUntil = now + def.disengageTicks;
            if (!string.IsNullOrEmpty(def.forceMode)) s.decision = 0f;   // re-pick at once, then Pre holds the forced mode
        }
        pendingKind = def.backstepOnStart ? 1 : (def.sidestepOnStart ? 2 : 0);
        pendingUntil = now + Mathf.Max(1, def.retryTicks);
        STAiBias.Refresh(cfg, cfg.Stance(STSteer.Stance), def);
        STSteer.Log("tactic:" + t, Describe(), "started cd=" + def.cooldownSeconds + "s max=" + def.maxSeconds + "s");
        STSteer.Feed(Nice(t));
        STSteer.RaiseTacticStart(t);
        return true;
    }

    static string Nice(STTactic t)
    {
        switch (t) { case STTactic.BackOff: return "Back off"; case STTactic.GuardUp: return "Guard up"; case STTactic.GoForKill: return "Go for the kill"; default: return t.ToString(); }
    }
    public string Describe()
    {
        if (brain == null || brain.ai == null) return "no brain";
        var s = brain.ai;
        return "mode=" + s.mode + " plan=" + s.plan + " next=" + s.next + " wait=" + s.wait.ToString("0") + " scores=" + brain.lastScores;
    }

    public void End(string why)
    {
        if (Running < 0) return;
        var t = (STTactic)Running; Running = -1; Def = null; pendingKind = 0;
        STSteer.Log("tactic:" + t + ":end", Describe(), why + " dealt=" + dmgDealt.ToString("0.0") + "(" + hitsDealt + ") taken=" + dmgTaken.ToString("0.0") + "(" + hitsTaken + ") parries=" + parries + " blocks=" + blocks + " dodges=" + dodges);
        STSteer.RaiseTacticEnd(t);
        STAiBias.Refresh(cfg, cfg.Stance(STSteer.Stance), null);
    }

    static AiMode ModeOf(string m)
    {
        switch (m) { case "Circle": return AiMode.Circle; case "Retreat": return AiMode.Retreat; case "Guard": return AiMode.Guard; default: return AiMode.Approach; }
    }

    bool InReach(MoveDef m, float dist, float inset)
    {
        if (m == null) return false;
        return dist <= m.reach * d.scale + d.radius + o.radius + 0.45f - inset;
    }

    // called every frame from AIBrain.Update (before Decide)
    public void Pre()
    {
        if (Running < 0) return;
        if (d == null || o == null || d.Dead || o.Dead) { End("fighter dead"); return; }
        int now = Duel.TickCount;
        if (now >= EndTick) { End("timeout"); return; }
        var def = Def; var s = brain.ai; if (s == null) return;
        if (!string.IsNullOrEmpty(def.forceMode)) { s.mode = ModeOf(def.forceMode); if (s.decision < 3f) s.decision = 3f; }
        if (def.waitZero) s.wait = 0f;
        if (def.suppressOffence) { s.next = AiNext.None; if (s.wait < 20f) s.wait = 20f; }
        else if (def.forceNext != null && def.forceNext.Length > 0 && s.next == AiNext.None && s.wait <= 0f) s.next = PickNext(def);
        // one-shot actions (retried while the fighter is busy)
        if (pendingKind != 0)
        {
            if (d.state == FState.Roll || now > pendingUntil) pendingKind = 0;
            else if (d.state == FState.Loco || d.state == FState.Guard)
            {
                if (pendingKind == 1) d.Press("backstep"); else d.PressSidestep(s.side);
            }
        }
        // special (GoForKill): press once it is ready and in reach
        if (!string.IsNullOrEmpty(def.special) && now >= nextSpecialTick && (d.state == FState.Loco || d.state == FState.Guard))
        {
            string mid = def.special == "special2" ? "skill_reaping" : "skill_cleave";
            int cd = def.special == "special2" ? d.special2Cd : d.special1Cd;
            var mv = d.moves != null ? d.moves.Get(mid) : null;
            Vector3 to = o.transform.position - d.transform.position; to.y = 0f;
            if (mv != null && cd <= 0 && to.magnitude <= Mathf.Min(def.specialReach, mv.reach * d.scale + d.radius + o.radius + 0.45f) - def.reachInset)
            {
                d.Press(def.special); nextSpecialTick = now + 20;
                STSteer.Log("tactic:GoForKill:special", Describe(), "pressed " + def.special);
            }
        }
    }

    AiNext PickNext(STTacticDef def)
    {
        float tot = 0f; int n = def.forceNext.Length;
        for (int i = 0; i < n; i++) tot += (def.forceNextWeights != null && i < def.forceNextWeights.Length) ? def.forceNextWeights[i] : 1f;
        float r = Roll() * Mathf.Max(0.0001f, tot);
        for (int i = 0; i < n; i++)
        {
            r -= (def.forceNextWeights != null && i < def.forceNextWeights.Length) ? def.forceNextWeights[i] : 1f;
            if (r <= 0f || i == n - 1) return def.forceNext[i] == "Heavy" ? AiNext.Heavy : def.forceNext[i] == "Thrust" ? AiNext.Thrust : AiNext.Light;
        }
        return AiNext.Light;
    }
}
