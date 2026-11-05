using System;
using UnityEngine;

// S1 data classes for steer.json blocks "steer", "stances", "tactics", "profileClamp". Other lanes keep their own classes for "reactions" (S2), "ui" (S3), "auto" (S4).
// Profile ops follow Strategy's K draft: per AiProfile knob { set (absolute, NaN = none), mul, add }, applied in that order, then clamped by "profileClamp".
[Serializable] public class STGeneral
{
    public int seed = 1337;
    public string defaultStance = "Neutral";
    public float checkEverySeconds = 0.5f;     // how often the controller re-resolves fighters / re-asserts the player's AI
    public bool feedLines = true;              // S1 emits basic feed lines (parry, guard break, dodge, kill, tactic names)
    public bool feedHits = false;              // also one line per landed hit
    public float fightResetDeadSeconds = 0.5f; // the enemy must be dead this long before a revive counts as a new fight
    public bool disablePlayerBrain = true;
}

[Serializable] public class STOp { public float set = float.NaN, mul = 1f, add = 0f; }
[Serializable] public class STOps
{
    public STOp aggression = new STOp(), pressure = new STOp(), parry = new STOp(), dodge = new STOp(), lapse = new STOp(), guard = new STOp(), circle = new STOp(),
                feint = new STOp(), step = new STOp(), kick = new STOp(), dash = new STOp(), disengage = new STOp(), interrupt = new STOp(), discipline = new STOp(),
                read = new STOp(), reaction = new STOp(), accuracy = new STOp();
}
[Serializable] public class STClampRow { public string knob; public float lo = -1e9f, hi = 1e9f; }
[Serializable] public class STClampBlock { public STClampRow[] list = new STClampRow[0]; }

[Serializable] public class STTrickster { public bool enabled = false; public float pDealt = 0.25f, dealtMul = 1.3f, pTaken = 0.25f, takenMul = 1.3f; }
[Serializable] public class STStanceDef
{
    public string id = "Neutral";
    public float dealtMul = 1f, takenMul = 1f;     // damage multipliers (the player as attacker / as defender)
    public STTrickster trickster = new STTrickster();   // per-hit seeded roll (only when enabled)
    public STOps profile = new STOps();             // AI bias on the player's AiProfile
}
[Serializable] public class STStancesBlock { public STStanceDef[] list = new STStanceDef[0]; }

[Serializable] public class STTacticDef
{
    public string id = "Press";
    public float cooldownSeconds = 6f, maxSeconds = 3f;
    public int usesPerFight = 0;              // 0 = unlimited
    public float foeHealthFracMax = 0f;       // > 0: enabled only when the enemy health / max <= this
    public float staminaMin = 0f;             // enabled only above this stamina
    public string forceMode = "";             // Approach | Circle | Retreat | Guard (AiMode), held while the tactic runs
    public bool waitZero = false;             // AiState.wait = 0: attack as soon as legal
    public string[] forceNext = new string[0];// Light | Heavy | Thrust, drawn by forceNextWeights whenever the AI has no next attack
    public float[] forceNextWeights = new float[0];
    public bool suppressOffence = false;      // clears AiState.next and keeps wait up (openings / counters still fire)
    public bool flipSide = false;             // flip the circling side at start
    public bool backstepOnStart = false;      // Press("backstep"), retried until the dodge starts or retryTicks passed
    public bool sidestepOnStart = false;      // PressSidestep(side), same retry
    public int retryTicks = 20;
    public int retreatUntilTicks = 0, disengageTicks = 0;   // AiState.retreatUntil / disengageUntil = now + n
    public string special = "";               // "special1" / "special2": pressed once in reach (GoForKill)
    public float specialReach = 3f;           // world metres gate (min with the move reach)
    public float reachInset = 0.25f;
    public STOps profile = new STOps();       // overlay on top of the stance
}
[Serializable] public class STTacticsBlock { public STTacticDef[] list = new STTacticDef[0]; }

public class STCfg
{
    public STGeneral general = new STGeneral();
    public STStancesBlock stances = new STStancesBlock();
    public STTacticsBlock tactics = new STTacticsBlock();
    public STClampBlock clamp = new STClampBlock();
    public static STCfg Load()
    {
        var c = new STCfg();
        c.general = STSteer.Load<STGeneral>("steer");
        c.stances = STSteer.Load<STStancesBlock>("stances");
        c.tactics = STSteer.Load<STTacticsBlock>("tactics");
        c.clamp = STSteer.Load<STClampBlock>("profileClamp");
        if (c.stances.list == null) c.stances.list = new STStanceDef[0];
        if (c.tactics.list == null) c.tactics.list = new STTacticDef[0];
        return c;
    }
    public STStanceDef Stance(STStance s)
    {
        string id = s.ToString();
        for (int i = 0; i < stances.list.Length; i++) if (stances.list[i] != null && stances.list[i].id == id) return stances.list[i];
        return null;
    }
    public STTacticDef Tactic(STTactic t)
    {
        string id = t.ToString();
        for (int i = 0; i < tactics.list.Length; i++) if (tactics.list[i] != null && tactics.list[i].id == id) return tactics.list[i];
        return null;
    }
}
