using System;
using System.Collections.Generic;

// Alpha step 1, lane E: the Pitborn's planner. Pure C# (no UnityEngine) so it can be self-tested outside the editor.
// One call = one blind round plan: a stance + 3 moves, chosen by a seeded weighted random pick.
// NO reading of the player's habits, NO rising stakes. Light sanity rules only (all numbers in duel_rules.json "ai" block):
//   heavy more likely in the early rounds, guard more likely at low HP, the Special not before round N unless HP is low, and only once.
// Enum ordinals mirror the shared contract (ADTypes.cs, lane A): ADStance{Aggressive,Defensive,Neutral,Trickster},
// ADMove{Light,Slash,Stab,Heavy,Kick,Guard,Roll,Sidestep,Special}. Convert with (ADMove)(int)m once ADTypes.cs exists.
public static class ADAiConst
{
    public const int MoveCount = 9;
    public const int StanceCount = 4;
    public const int PlanMoves = 3;      // contract: a plan is always exactly 3 moves
}

public enum AiStance { Aggressive = 0, Defensive = 1, Neutral = 2, Trickster = 3 }
public enum AiMove { Light = 0, Slash = 1, Stab = 2, Heavy = 3, Kick = 4, Guard = 5, Roll = 6, Sidestep = 7, Special = 8 }

[Serializable] public class ADWeightRow { public string id = ""; public float w = 1f; }

[Serializable]
public class ADAiDifficulty
{
    public string id = "normal";
    public float attackMul = 1f;          // multiplies light/slash/stab/heavy/kick weights
    public float defendMul = 1f;          // multiplies guard/roll/sidestep weights
    public float temperature = 1f;        // weights are raised to 1/temperature: < 1 sharper (favours likely picks), > 1 flatter
    public int specialMinRound = 3;       // Special not offered before this round unless HP is low
    public float specialLowHpFrac = 0.35f;// ... or own hp/hpMax at or below this
    public float guardLowHpMul = 2f;      // guard weight multiplier when hp/hpMax <= ai.lowHpFrac
    public float heavyEarlyMul = 1.5f;    // heavy weight multiplier in rounds <= ai.earlyRounds
}

[Serializable]
public class ADAiBlock
{
    public int earlyRounds = 2;
    public float lowHpFrac = 0.4f;
    public float lowHpDefensiveStanceMul = 2f;   // Defensive stance weight multiplier at low HP
    public float repeatMoveMul = 0.5f;           // weight multiplier for a move equal to the previous slot (variety)
    public float specialWeight = 1.5f;           // Special weight when it is allowed (moves[] has no Special row, or use the row)
    public ADWeightRow[] stances = new ADWeightRow[0];
    public ADWeightRow[] moves = new ADWeightRow[0];
    public ADAiDifficulty[] difficulty = new ADAiDifficulty[0];
}

public class ADAiState
{
    public int hp, hpMax;
    public bool specialUsed;              // set by the rules when the Pitborn has spent its Special
}

public class ADAiPlan
{
    public AiStance stance;
    public AiMove[] moves = new AiMove[ADAiConst.PlanMoves];
    public bool UsesSpecial { get { for (int i = 0; i < moves.Length; i++) if (moves[i] == AiMove.Special) return true; return false; } }
    public override string ToString() { return stance + " [" + moves[0] + ", " + moves[1] + ", " + moves[2] + "]"; }
}

public class ADPitbornAI
{
    readonly ADAiBlock cfg;
    ulong rngState;
    bool specialSpent;                    // own bookkeeping: a plan that contained the Special spends it
    public bool SpecialSpent { get { return specialSpent; } }

    public ADPitbornAI(ADAiBlock config, int seed)
    {
        cfg = config ?? new ADAiBlock();
        Reseed(seed);
    }

    // New fight: fresh seed, Special available again.
    public void Reseed(int seed)
    {
        rngState = (ulong)(uint)seed * 2654435761UL + 0x9E3779B97F4A7C15UL;
        if (rngState == 0) rngState = 1;
        specialSpent = false;
    }

    float Next01()                        // xorshift64*
    {
        rngState ^= rngState >> 12; rngState ^= rngState << 25; rngState ^= rngState >> 27;
        ulong r = rngState * 2685821657736338717UL;
        return (float)((r >> 40) / 16777216.0);
    }

    ADAiDifficulty Diff(string id)
    {
        ADAiDifficulty first = null;
        if (cfg.difficulty != null)
            for (int i = 0; i < cfg.difficulty.Length; i++)
            {
                if (cfg.difficulty[i] == null) continue;
                if (first == null) first = cfg.difficulty[i];
                if (string.Equals(cfg.difficulty[i].id, id, StringComparison.OrdinalIgnoreCase)) return cfg.difficulty[i];
            }
        return first ?? new ADAiDifficulty();
    }

    static float RowWeight(ADWeightRow[] rows, string id, float dflt)
    {
        if (rows != null)
            for (int i = 0; i < rows.Length; i++)
                if (rows[i] != null && string.Equals(rows[i].id, id, StringComparison.OrdinalIgnoreCase)) return rows[i].w;
        return dflt;
    }

    static bool IsAttack(AiMove m) { return m == AiMove.Light || m == AiMove.Slash || m == AiMove.Stab || m == AiMove.Heavy || m == AiMove.Kick; }
    static bool IsDefend(AiMove m) { return m == AiMove.Guard || m == AiMove.Roll || m == AiMove.Sidestep; }

    int Pick(float[] w)
    {
        float sum = 0f;
        for (int i = 0; i < w.Length; i++) sum += w[i];
        if (sum <= 0f) return 0;
        float r = Next01() * sum;
        for (int i = 0; i < w.Length; i++)
        {
            if (w[i] <= 0f) continue;
            r -= w[i];
            if (r < 0f) return i;
        }
        for (int i = w.Length - 1; i >= 0; i--) if (w[i] > 0f) return i;
        return 0;
    }

    // Final weight per move for this state/round (index = (int)AiMove). Special is 0 unless allowed.
    public float[] MoveWeights(ADAiState st, int round, string difficulty)
    {
        ADAiDifficulty d = Diff(difficulty);
        float hpFrac = st.hpMax > 0 ? (float)st.hp / st.hpMax : 1f;
        bool low = hpFrac <= cfg.lowHpFrac;
        float inv = d.temperature > 0.01f ? 1f / d.temperature : 1f;
        float[] w = new float[ADAiConst.MoveCount];
        for (int i = 0; i < w.Length; i++)
        {
            AiMove m = (AiMove)i;
            float v = RowWeight(cfg.moves, m.ToString(), m == AiMove.Special ? cfg.specialWeight : 1f);
            if (m == AiMove.Special)
            {
                bool allowed = !st.specialUsed && !specialSpent &&
                               (round >= d.specialMinRound || hpFrac <= d.specialLowHpFrac);
                if (!allowed) { w[i] = 0f; continue; }
            }
            else if (IsAttack(m)) v *= d.attackMul;
            else if (IsDefend(m)) v *= d.defendMul;
            if (m == AiMove.Heavy && round <= cfg.earlyRounds) v *= d.heavyEarlyMul;
            if (m == AiMove.Guard && low) v *= d.guardLowHpMul;
            w[i] = v > 0f ? (float)Math.Pow(v, inv) : 0f;
        }
        return w;
    }

    public float[] StanceWeights(ADAiState st, string difficulty)
    {
        ADAiDifficulty d = Diff(difficulty);
        float hpFrac = st.hpMax > 0 ? (float)st.hp / st.hpMax : 1f;
        float inv = d.temperature > 0.01f ? 1f / d.temperature : 1f;
        float[] w = new float[ADAiConst.StanceCount];
        for (int i = 0; i < w.Length; i++)
        {
            AiStance s = (AiStance)i;
            float v = RowWeight(cfg.stances, s.ToString(), 1f);
            if (s == AiStance.Defensive && hpFrac <= cfg.lowHpFrac) v *= cfg.lowHpDefensiveStanceMul;
            w[i] = v > 0f ? (float)Math.Pow(v, inv) : 0f;
        }
        return w;
    }

    // The one entry point. round is 1-based. Always returns 3 moves; the Special appears at most once per plan and once per fight.
    public ADAiPlan ChoosePlan(ADAiState st, int round, string difficulty)
    {
        ADAiPlan plan = new ADAiPlan();
        plan.stance = (AiStance)Pick(StanceWeights(st, difficulty));
        float[] baseW = MoveWeights(st, round, difficulty);
        bool placedSpecial = false;
        for (int slot = 0; slot < ADAiConst.PlanMoves; slot++)
        {
            float[] w = (float[])baseW.Clone();
            if (placedSpecial) w[(int)AiMove.Special] = 0f;
            if (slot > 0)
            {
                int prev = (int)plan.moves[slot - 1];
                w[prev] *= cfg.repeatMoveMul;
            }
            AiMove m = (AiMove)Pick(w);
            plan.moves[slot] = m;
            if (m == AiMove.Special) placedSpecial = true;
        }
        if (placedSpecial) specialSpent = true;
        return plan;
    }
}
