using System;
using System.IO;
using UnityEngine;

// Port of Frankendom V3 src/fight/ai-dial-rows.ts (+ the AiState / lcg part of ai.ts) and the single loader for Assets/Data/ai_rows.json.
// The numbers live in ai_rows.json; the field initialisers below mirror V3's ai-dial-defs.json so a missing file still plays.

[Serializable]
public class AiDialRows
{
    public float startSeed = 731, startLastGap = 99, startDecision = 90, startWait = 90, disengageTicks = 40, retreatTicks = 48;
    public float waitBase = 45, waitSpread = 60, waitAggrCeil = 1.6f, rangeSlack = 0.4f, reachInset = 0.1f, backstepTravelMul = 3, backstepSpeedShare = 0.5f;
    public float advanceEps = 0.01f, loiterShare = 0.75f, backstepSlack = 0.2f, shakyShare = 0.7f, lowRetreatGap = 1.7f, farApproachGap = 1.4f;
    public float guardRollShare = 0.33f, hardApproachGap = 2.5f, midApproachGap = 1.9f, lowCloseRetreatGap = 1.2f, circleReturnGap = 1.9f;
    public float guardHoldBase = 36, guardHoldSpread = 45, estimateMax = 3, kickWeight = 1.1f, baitChance = 0.6f, chargeOffset = 0.25f;
    public float thrustCloseInset = 0.2f, braceSlack = 0.3f, kickHoverSlack = 0.3f, lateralStep = 0.25f, dashSlack = 0.3f, wallSlack = 0.3f;
    public float missileGuardWindow = 24, dodgeSafeEndMargin = 2, parryWindowMargin = 2, readDecayStep = 2, jitterAmp = 8, interruptWindupMargin = 2;
    // EXTRA: literals that were inline in V3 ai.ts (forward .6, back -.4, retreat gaps 1.9 / 2.2)
    public float forwardSpeed = 0.6f, backSpeed = -0.4f, retreatGapLow = 1.9f, retreatGapNormal = 2.2f;
}

public enum AiMode { Approach, Circle, Retreat, Guard }
public enum AiPlan { None, Parry, Dodge, Block, Evade, Ignore }
public enum AiNext { None, Light, Heavy, Thrust }

// V3 AiState. One per brain; mutated in place by AIBrain.Decide.
public class AiState
{
    public uint seed;
    public float lastGap, lastTravel;
    public AiMode mode = AiMode.Approach;
    public int side = 1;
    public float decision, wait;
    public AiNext next = AiNext.None;
    public AiPlan plan = AiPlan.None;
    public string readSide;               // null = no read (guard stands straight)
    public int jitter, retreatUntil, disengageUntil;
    public bool hold, feint, brace;
    public int plannedSerial = -1, holdCheckedSerial = -1;
    public Habits habits = new Habits();

    public static AiState Initial(uint seed, AiDialRows D)
    {
        var s = new AiState();
        s.seed = seed; s.lastGap = D.startLastGap; s.decision = D.startDecision; s.wait = D.startWait;
        return s;
    }
    // V3 lcg: Math.imul(seed, 1664525) + 1013904223 >>> 0 ; seedUnit = seed / 2^32
    public float Roll()
    {
        seed = unchecked(seed * 1664525u + 1013904223u);
        return (float)(seed / 4294967296.0);
    }
}

[Serializable] public class AiFightRow { public float thrustShare = 0.1f, close = 1.15f; }
[Serializable] public class AiZBias { public string name, cmp; public float offset, push; }
[Serializable]
public class AiUnityRules
{
    public float rollCost = 30, safeStart = 4, safeEnd = 20, backstepTicks = 12, backstepSpeed = 1, backstepCost = 10, parry = 10, parryCooldown = 30, parryStun = 90, postureMax = 100;
}
[Serializable]
public class AiUnityRows
{
    public float contactSlack = 0.45f;     // Duel.Resolve reaches reach*scale + radii + 0.45
    public int maxCatchUpTicks = 4;
    public bool brainsOnTick = false;   // FPSSIM v1: true = AIBrain / FlowBot / AiStance decide once per 60 Hz tick from Duel.Update, ArenaLimits clamps in MoveTick
    public float moveGain = 1f, runBeyondDist = 6.5f, feintUntil = 10, chargeMin = 30, ticksPerSecond = 60;
    public bool useSpecials = false;
    public string[] specialMoves = new string[] { "skill_cleave", "skill_reaping" };
    public string[] specialPress = new string[] { "special1", "special2" };
    public float specialReach = 3;
    public float zBiasBaseDist = 0.5f, zBiasScaleDist = 2.3f;
    public AiZBias[] zBias = new AiZBias[0];
    public AiUnityRules rules = new AiUnityRules();
}

[Serializable]
public class AiRowsFile
{
    public AiReadRows read = new AiReadRows();
    public AiDialRows dial = new AiDialRows();
    public AiFightRow fight = new AiFightRow();
    public AiProfile profileBase = new AiProfile();
    public AiProfile playerBot = new AiProfile();
    public string[] tells = new string[] { "thrust" };
    public AiTacticLimits tacticLimits = new AiTacticLimits();
    public AiTacticRow[] tactics = new AiTacticRow[0];
    public AiUnityRows unity = new AiUnityRows();
}

public static class AiRows
{
    static AiRowsFile inst;
    public static AiRowsFile I { get { if (inst == null) Load(); return inst; } }
    public static void Reload() { inst = null; }

    static void Load()
    {
        inst = new AiRowsFile();
        // playerBot defaults (used only if the file lacks the block)
        inst.playerBot.reaction = 14; inst.playerBot.accuracy = 0.75f; inst.playerBot.parry = 0.3f; inst.playerBot.dodge = 0.2f;
        inst.playerBot.aggression = 0.9f; inst.playerBot.pressure = 0.5f; inst.playerBot.discipline = 50; inst.playerBot.lapse = 0.3f; inst.playerBot.read = 0.7f;
        string text = null;
        try
        {
            string[] paths = new string[] { Path.Combine(Application.dataPath, "Data/ai_rows.json"), Path.Combine(Application.streamingAssetsPath, "ai_rows.json") };
            for (int i = 0; i < paths.Length && text == null; i++) if (File.Exists(paths[i])) text = File.ReadAllText(paths[i]);
            if (text == null) { var ta = Resources.Load<TextAsset>("ai_rows"); if (ta != null) text = ta.text; }
        }
        catch (Exception e) { Debug.LogWarning("[AI] ai_rows.json read failed: " + e.Message); }
        if (text == null) { Debug.LogWarning("[AI] ai_rows.json not found; using built-in V3 defaults"); AiTactic.Validate(inst); return; }
        try { JsonUtility.FromJsonOverwrite(text, inst); }
        catch (Exception e) { Debug.LogWarning("[AI] ai_rows.json parse failed: " + e.Message); }
        AiTactic.Validate(inst);
    }
}
