using System;
using System.Text;

// Engine-free self-test for ADPitbornAI (no UnityEngine): ADPitbornAISelfTest.Run() -> true when all checks pass, log in `report`.
// The fixture config below is a TEST FIXTURE only; the shipped numbers live in duel_rules.json "ai".
public static class ADPitbornAISelfTest
{
    public static string report = "";

    static ADAiBlock Fixture()
    {
        ADAiBlock b = new ADAiBlock();
        b.earlyRounds = 2; b.lowHpFrac = 0.4f; b.repeatMoveMul = 1f; b.specialWeight = 3f;
        b.stances = new[] { W("Aggressive", 1), W("Defensive", 1), W("Neutral", 1), W("Trickster", 1) };
        b.moves = new[] { W("Light", 4), W("Slash", 2), W("Stab", 1), W("Heavy", 1), W("Kick", 1), W("Guard", 1), W("Roll", 0), W("Sidestep", 0), W("Special", 3) };
        ADAiDifficulty e = new ADAiDifficulty { id = "easy" };
        ADAiDifficulty n = new ADAiDifficulty { id = "normal", specialMinRound = 3, specialLowHpFrac = 0.35f, guardLowHpMul = 8f, heavyEarlyMul = 4f };
        ADAiDifficulty h = new ADAiDifficulty { id = "hard", attackMul = 1.2f };
        b.difficulty = new[] { e, n, h };
        return b;
    }
    static ADWeightRow W(string id, float w) { return new ADWeightRow { id = id, w = w }; }

    static int fails;
    static StringBuilder sb;
    static void Check(bool ok, string what) { if (!ok) fails++; sb.AppendLine((ok ? "PASS " : "FAIL ") + what); }

    public static bool Run()
    {
        fails = 0; sb = new StringBuilder();
        ADAiBlock cfg = Fixture();
        const int N = 6000;

        // 1. weights respected over many seeds (round 1, full HP, Special not allowed): Light ~ 4/9, Guard ~ 1/9 of picks, Roll/Sidestep never.
        int[] cnt = new int[ADAiConst.MoveCount]; int total = 0;
        for (int seed = 0; seed < N; seed++)
        {
            ADPitbornAI ai = new ADPitbornAI(cfg, seed);
            ADAiPlan p = ai.ChoosePlan(new ADAiState { hp = 90, hpMax = 90 }, 1, "easy");
            foreach (AiMove m in p.moves) { cnt[(int)m]++; total++; }
        }
        float light = cnt[(int)AiMove.Light] / (float)total, guard = cnt[(int)AiMove.Guard] / (float)total, heavy = cnt[(int)AiMove.Heavy] / (float)total;
        Check(Math.Abs(light - 4f / 10f) < 0.03f, "easy r1 Light share " + light.ToString("0.000") + " ~ 0.400 (4/10; Roll,Sidestep,Special weight 0)");
        Check(Math.Abs(guard - 1f / 10f) < 0.03f, "easy r1 Guard share " + guard.ToString("0.000") + " ~ 0.100");
        Check(cnt[(int)AiMove.Roll] == 0 && cnt[(int)AiMove.Sidestep] == 0, "zero-weight moves never picked");
        Check(cnt[(int)AiMove.Special] == 0, "Special never picked in round 1 at full HP");

        // 2. heavy more likely early (normal, round 1 vs round 5).
        int heavyEarly = 0, heavyLate = 0, n3 = 0;
        for (int seed = 0; seed < N; seed++)
        {
            ADPitbornAI ai = new ADPitbornAI(cfg, seed);
            foreach (AiMove m in ai.ChoosePlan(new ADAiState { hp = 90, hpMax = 90 }, 1, "normal").moves) if (m == AiMove.Heavy) heavyEarly++;
            ADPitbornAI ai2 = new ADPitbornAI(cfg, seed + 100000);
            // round 5 with special already used so only the heavy multiplier differs
            foreach (AiMove m in ai2.ChoosePlan(new ADAiState { hp = 90, hpMax = 90, specialUsed = true }, 5, "normal").moves) if (m == AiMove.Heavy) heavyLate++;
            n3 += 3;
        }
        Check(heavyEarly > heavyLate * 1.5f, "heavy more likely early: " + heavyEarly + " vs late " + heavyLate + " of " + n3);

        // 3. guard more likely at low HP.
        int gLow = 0, gHigh = 0;
        for (int seed = 0; seed < N; seed++)
        {
            ADPitbornAI a = new ADPitbornAI(cfg, seed);
            foreach (AiMove m in a.ChoosePlan(new ADAiState { hp = 20, hpMax = 90, specialUsed = true }, 5, "normal").moves) if (m == AiMove.Guard) gLow++;
            ADPitbornAI b = new ADPitbornAI(cfg, seed);
            foreach (AiMove m in b.ChoosePlan(new ADAiState { hp = 90, hpMax = 90, specialUsed = true }, 5, "normal").moves) if (m == AiMove.Guard) gHigh++;
        }
        Check(gLow > gHigh * 2f, "guard more likely at low HP: " + gLow + " vs " + gHigh);

        // 4. Special not before round N (normal: 3) at full HP; allowed at low HP any round.
        int spEarly = 0, spLowHp = 0, spRound3 = 0;
        for (int seed = 0; seed < N; seed++)
        {
            ADPitbornAI a = new ADPitbornAI(cfg, seed);
            if (a.ChoosePlan(new ADAiState { hp = 90, hpMax = 90 }, 2, "normal").UsesSpecial) spEarly++;
            ADPitbornAI b = new ADPitbornAI(cfg, seed);
            if (b.ChoosePlan(new ADAiState { hp = 20, hpMax = 90 }, 1, "normal").UsesSpecial) spLowHp++;
            ADPitbornAI c = new ADPitbornAI(cfg, seed);
            if (c.ChoosePlan(new ADAiState { hp = 90, hpMax = 90 }, 3, "normal").UsesSpecial) spRound3++;
        }
        Check(spEarly == 0, "no Special in round 2 at full HP");
        Check(spLowHp > 0, "Special possible at low HP in round 1 (" + spLowHp + " of " + N + ")");
        Check(spRound3 > 0, "Special possible from round 3 (" + spRound3 + " of " + N + ")");

        // 5. Special at most once per plan and once per fight; plan always 3 moves; stance valid.
        bool onceOk = true, threeOk = true, perPlanOk = true; int fightsWithSpecial = 0;
        for (int seed = 0; seed < 2000; seed++)
        {
            ADPitbornAI ai = new ADPitbornAI(cfg, seed);
            int used = 0;
            for (int round = 1; round <= 12; round++)
            {
                ADAiPlan p = ai.ChoosePlan(new ADAiState { hp = 15, hpMax = 90 }, round, seed % 3 == 0 ? "easy" : seed % 3 == 1 ? "normal" : "hard");
                if (p.moves.Length != 3) threeOk = false;
                int inPlan = 0; foreach (AiMove m in p.moves) if (m == AiMove.Special) inPlan++;
                if (inPlan > 1) perPlanOk = false;
                used += inPlan;
                if ((int)p.stance < 0 || (int)p.stance >= ADAiConst.StanceCount) threeOk = false;
            }
            if (used > 1) onceOk = false;
            if (used == 1) fightsWithSpecial++;
        }
        Check(threeOk, "plan always 3 moves, valid stance");
        Check(perPlanOk, "Special at most once per plan");
        Check(onceOk, "Special at most once per fight (" + fightsWithSpecial + " of 2000 fights used it at low HP)");

        // 6. caller-side flag respected.
        bool flagOk = true;
        for (int seed = 0; seed < 500; seed++)
        {
            ADPitbornAI ai = new ADPitbornAI(cfg, seed);
            if (ai.ChoosePlan(new ADAiState { hp = 15, hpMax = 90, specialUsed = true }, 6, "hard").UsesSpecial) flagOk = false;
        }
        Check(flagOk, "state.specialUsed blocks the Special");

        // 7. determinism: same seed, same plan.
        ADAiPlan x = new ADPitbornAI(cfg, 77).ChoosePlan(new ADAiState { hp = 50, hpMax = 90 }, 4, "normal");
        ADAiPlan y = new ADPitbornAI(cfg, 77).ChoosePlan(new ADAiState { hp = 50, hpMax = 90 }, 4, "normal");
        Check(x.ToString() == y.ToString(), "seeded: same seed same plan (" + x + ")");

        sb.AppendLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        report = sb.ToString();
        return fails == 0;
    }
}
