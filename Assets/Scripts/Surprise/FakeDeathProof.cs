#if !FRANKEN_NO_HARNESS // harness code, compiled unless the release build passes FRANKEN_NO_HARNESS
using System;
using UnityEngine;

// FAKEDEATH proof switches (pool-laneS7). Command-line only; a release build (FRANKEN_NO_HARNESS) has none of them, so nothing can force a surprise there.
//   -s7:0              the whole feature off (even if fake_deaths.json says enabled)
//   -s7:1              both mechanics forced on: no chance roll, no per-run cap, no SurpriseGate (the boss falls and rises at its first lethal blow, the next loot beside a bandit corpse is grabbed)
//   -s7corpse:1        only the corpse grab forced
//   -s7corpse:auto     as -s7corpse:1 plus: after proof.autoAfterS the nearest living bandit is killed and the grab follows (no fight / loot needed)
//   -s7boss:1          only the boss rise forced (any boss in boss.pickIds, its first lethal blow)
//   -s7boss:<Id>       the boss rise forced for ONE roster id (Pitborn, Sobek, Grendel, Humbaba, Erlking)
// Forced runs work inside harness flags too (the data master switch is ignored while a force flag is set).
static class FakeDeathProof
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Parse()
    {
        FakeDeath.ForceOff = FakeDeath.ForceAll = FakeDeath.ForceCorpse = FakeDeath.ForceBoss = FakeDeath.AutoCorpse = false; FakeDeath.ForceBossId = "";
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a == "-s7:0") FakeDeath.ForceOff = true;
            else if (a == "-s7:1") FakeDeath.ForceAll = true;
            else if (a == "-s7corpse:1") FakeDeath.ForceCorpse = true;
            else if (a == "-s7corpse:auto") { FakeDeath.ForceCorpse = true; FakeDeath.AutoCorpse = true; }
            else if (a == "-s7boss:1") FakeDeath.ForceBoss = true;
            else if (a.StartsWith("-s7boss:", StringComparison.Ordinal)) { FakeDeath.ForceBoss = true; FakeDeath.ForceBossId = a.Substring(a.IndexOf(':') + 1); }
        }
        if (FakeDeath.ForceOff) FakeDeath.ForceAll = FakeDeath.ForceCorpse = FakeDeath.ForceBoss = false;
    }
}
#endif
