using System.Collections.Generic;
using UnityEngine;

// laneSTANCE2: the AI chooses its own stance. Data: stances.json "ai" (StanceLayer.AiRow); nothing here but the rule.
// Priority Defensive > Aggressive > Trickster > Neutral, decided every ai.decideEveryS sim seconds and never before ai.minDwellS has passed since the last change.
//   Defensive  : own HP <= lowHpEnter, or own posture >= selfPostureEnter      -> kept until HP > lowHpExit AND posture <= selfPostureExit
//   Aggressive : foe posture >= oppPostureEnter, or HP lead >= leadEnter       -> kept until foe posture <= oppPostureExit AND lead <= leadExit
//   Trickster  : while Neutral, tricksterChance per decision; held tricksterHoldS (a random time in the range) unless Defensive / Aggressive take over
// Exit thresholds sit on the far side of the enter ones (hysteresis), so a bar hovering at a threshold cannot flicker the stance.
// Own seeded LCG (stances.json seed ^ name hash): it never touches AIBrain's roll sequence, so every other AI decision of a seed is unchanged. Ticks = Duel.TickCount (60 Hz sim).
// The model of this rule is tools\stance_sim.py ai_pick (same thresholds, same order).
public static class AiStance
{
    class St { public string cur; public int since, next, trickUntil, lastNow; public uint rng; }
    static readonly Dictionary<DuelFighter, St> map = new Dictionary<DuelFighter, St>();

    public static void Reset() { map.Clear(); }
    public static bool Enabled { get { return StanceLayer.Active && StanceLayer.Get().ai != null && StanceLayer.Get().ai.enabled; } }

    static uint Hash(string s) { uint h = 2166136261u; for (int i = 0; i < s.Length; i++) h = unchecked((h ^ s[i]) * 16777619u); return h; }
    static float Roll(St s) { s.rng = unchecked(s.rng * 1664525u + 1013904223u); return (float)(s.rng / 4294967296.0); }
    static int Ticks(float seconds) { return Mathf.Max(1, Mathf.RoundToInt(seconds * Mathf.Max(1f, AiRows.I.unity.ticksPerSecond))); }

    // called from AIBrain.Update every frame, after the dead / frozen guards; does the work once per decideEveryS
    public static void Tick(DuelFighter f)
    {
        if (!Enabled || f == null || f.Dead) return;
        var A = StanceLayer.Get().ai; var o = f.opponent;
        if (o == null || o.Dead || Duel.Frozen) return;
        if (f.IsPlayer && !A.forAutoplayPlayer) return;
        int now = Duel.TickCount;
        St s;
        if (!map.TryGetValue(f, out s)) { s = new St(); s.cur = StanceLayer.StanceOf(f); s.since = now - Ticks(A.minDwellS); s.next = now; s.rng = unchecked((uint)StanceLayer.Get().seed * 2654435761u ^ Hash(f.name)); if (s.rng == 0) s.rng = 1; map[f] = s; }
        if (now < s.lastNow) { s.since = now - Ticks(A.minDwellS); s.next = now; s.trickUntil = 0; }   // the sim restarted
        s.lastNow = now;
        if (now < s.next) return;
        s.next = now + Ticks(A.decideEveryS);
        string want = Pick(A, s, now, f, o);
        if (want != s.cur && StanceLayer.Row(want) != null) { s.cur = want; s.since = now; StanceLayer.Set(f, want, true); }
    }

    static string Pick(StanceLayer.AiRow A, St s, int now, DuelFighter f, DuelFighter o)
    {
        string cur = s.cur;
        if (now - s.since < Ticks(A.minDwellS)) return cur;   // minimum dwell
        float selfHp = f.health / Mathf.Max(1f, f.maxHealth), oppHp = o.health / Mathf.Max(1f, o.maxHealth);
        float selfPost = f.posture / Mathf.Max(1f, f.PostureMax), oppPost = o.posture / Mathf.Max(1f, o.PostureMax);
        float lead = selfHp - oppHp;
        bool wantDef = selfHp <= A.lowHpEnter || selfPost >= A.selfPostureEnter;
        bool keepDef = cur == A.defensiveId && (selfHp < A.lowHpExit || selfPost > A.selfPostureExit);
        bool wantAgg = oppPost >= A.oppPostureEnter || lead >= A.leadEnter;
        bool keepAgg = cur == A.aggressiveId && (oppPost > A.oppPostureExit || lead > A.leadExit);
        if (wantDef || keepDef) return A.defensiveId;
        if (wantAgg || keepAgg) return A.aggressiveId;
        if (cur == A.tricksterId) return now < s.trickUntil ? A.tricksterId : A.neutralId;
        if (A.tricksterChance > 0f && Roll(s) < A.tricksterChance)
        {
            float lo = A.tricksterHoldS[0], hi = Mathf.Max(lo, A.tricksterHoldS[1]);
            s.trickUntil = now + Ticks(lo + Roll(s) * (hi - lo));
            return A.tricksterId;
        }
        return A.neutralId;
    }
}
