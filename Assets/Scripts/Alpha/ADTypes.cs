using System;
using System.Collections.Generic;

// ALPHA STEP 1 shared contract (lane A). Engine-free on purpose (no UnityEngine) so ADRules + the self-test also run offline.
// Every number lives in Assets/Data/duel_rules.json (StreamingAssets copy in the player). This file only holds shapes.
// Outcome semantics: out* is the result FOR THAT FIGHTER'S OWN MOVE. Hit = my attack landed, Blocked = my attack was blocked,
// Parried = I parried, Riposte = I parried and counter-hit, Dodged = I dodged, Clash = tied attacks, Interrupted = my move was cut off
// (or voided because the beat was already decided by a kill), GuardBreak = my guard was broken, Whiff = my move found nothing,
// Staggered = I was staggered / knocked down, Killed = my hit killed the opponent, Struck = my defence/move failed and I was hit (appended).
public enum ADSide { Dracula, Pitborn }
public enum ADStance { Aggressive, Defensive, Neutral, Trickster }
public enum ADMove { Light, Slash, Stab, Heavy, Kick, Guard, Roll, Sidestep, Special }
public enum ADLoc { Arm, Leg, Torso, Head }
public enum ADOutcome { Hit, Blocked, Parried, Riposte, Dodged, Clash, Interrupted, GuardBreak, Whiff, Staggered, Killed, Struck }
public enum ADPhase { Idle, Plan, Resolve, Replay, Result, Ended }

[Serializable]
public class ADPlan
{
    public ADStance stance = ADStance.Neutral;
    public ADMove[] moves = new ADMove[3];
    public ADPlan() { }
    public ADPlan(ADStance s, ADMove m1, ADMove m2, ADMove m3) { stance = s; moves = new[] { m1, m2, m3 }; }
    public ADPlan Clone() { var p = new ADPlan(); p.stance = stance; p.moves = moves != null ? (ADMove[])moves.Clone() : new ADMove[3]; return p; }
    public override string ToString() { return stance + ": " + moves[0] + ", " + moves[1] + ", " + moves[2]; }
}

public class ADFighter
{
    public ADSide side;
    public int hp, hpMax;
    public bool specialUsed;
    public int[] wounds = new int[4];          // severity per ADLoc (index = (int)ADLoc), written by the wounds provider
    public float speedTierPenalty;             // added to the move's speed tier (higher = slower); set by the wounds provider
    public int stagger;                        // carried into the NEXT beat: 0 none, 1 staggered (slower), 2 knocked down (loses the move)
    public float critBonus;                    // added to crit chance (later: Cunning)
    public ADStance stance;                    // stance of the current round
    public float stanceDealt = 1f, stanceTaken = 1f;   // current-round multipliers (Trickster rolls them once per round)
    public string stanceNote = "";             // e.g. "Trickster: +30% dealt"
    public bool Dead { get { return hp <= 0; } }
    public int Wound(ADLoc l) { return wounds[(int)l]; }
}

public class ADBeat
{
    public int beat;                           // 0-based inside the round
    public int round;
    public ADMove a, b;                        // a = Dracula's move, b = Pitborn's move (after sanitising)
    public ADOutcome outA, outB;
    public int dmgToA, dmgToB;                 // damage actually applied
    public ADLoc locA, locB;                   // where each was hit (only meaningful when dmgTo* > 0)
    public bool crit, critA, critB;            // critA = Dracula's hit was a crit
    public bool firstA;                        // Dracula's move lands first (ties: true)
    public bool tie;                           // equal speed tier
    public int healA, healB;                   // blood drain etc.
    public bool staggerA, staggerB;            // fighter is staggered going into the next beat
    public bool knockdownA, knockdownB;
    public bool riposteA, riposteB;
    public bool specialA, specialB;            // the special was spent in this beat
    public bool lostA, lostB;                  // that fighter lost this beat's move (knocked down by the previous beat)
    public bool killed;
    public ADSide victim;                      // valid when killed
    public string reason;                      // from Dracula's point of view: 'your guard beat his light'
    public override string ToString() { return "beat " + beat + " " + a + " vs " + b + " -> " + outA + "/" + outB + " (" + dmgToB + " to Pitborn, " + dmgToA + " to Dracula) " + reason; }
}

public class ADRoundResult
{
    public int round;
    public ADPlan planA, planB;                // sanitised plans actually played
    public List<ADBeat> beats = new List<ADBeat>();
    public bool killed;
    public ADSide winner;                      // valid when killed
    public string stanceNoteA = "", stanceNoteB = "";
    public bool timedOutA, timedOutB;          // plan repeated because the timer ran out
}

public interface IADRng { double Next01(); }

// Deterministic, platform independent (splitmix64).
public class ADRng : IADRng
{
    ulong s;
    public ADRng(int seed) { s = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x1234567UL; }
    public double Next01()
    {
        s += 0x9E3779B97F4A7C15UL; ulong z = s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }
}

// Lane B plugs in here (ADWounds). ADRules falls back to ADBasicWounds (small, from rules.fallbackWounds) when nothing is set.
public interface IADWounds
{
    ADLoc RollLocation(ADMove move, IADRng rng);          // where a hit by this move lands
    float DamageMul(ADFighter attacker, ADMove move);     // e.g. arm wound weakens heavies
    void Apply(ADFighter victim, ADLoc loc, int damage, bool crit);   // add severity, refresh victim.speedTierPenalty
}

// Tiny helper for lanes B-E: pull one top-level block out of duel_rules.json text so they can JsonUtility.FromJson<TheirClass>(block).
public static class ADJson
{
    public static string Block(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        while (k >= 0)
        {
            int c = k + key.Length + 2; while (c < json.Length && char.IsWhiteSpace(json[c])) c++;
            if (c < json.Length && json[c] == ':')
            {
                c++; while (c < json.Length && char.IsWhiteSpace(json[c])) c++;
                if (c >= json.Length || (json[c] != '{' && json[c] != '[')) return null;
                char open = json[c], close = open == '{' ? '}' : ']'; int depth = 0; bool str = false;
                for (int i = c; i < json.Length; i++)
                {
                    char ch = json[i];
                    if (str) { if (ch == '\\') i++; else if (ch == '"') str = false; continue; }
                    if (ch == '"') str = true; else if (ch == open) depth++; else if (ch == close && --depth == 0) return json.Substring(c, i - c + 1);
                }
                return null;
            }
            k = json.IndexOf("\"" + key + "\"", k + 1, StringComparison.Ordinal);
        }
        return null;
    }
}

// ---- data shapes for the blocks lane A owns (rules, matrix, stances, crit, special). Other lanes define their own classes for their blocks. ----
// JsonUtility cannot read enums by name, so plans in data use strings.
[Serializable]
public class ADPlanDef
{
    public string stance = "Neutral"; public string[] moves;
    public ADPlan ToPlan()
    {
        ADStance s; if (!Enum.TryParse(stance, true, out s)) s = ADStance.Neutral;
        var p = new ADPlan { stance = s, moves = new ADMove[3] };
        for (int i = 0; i < 3; i++) { ADMove m = ADMove.Guard; if (moves != null && i < moves.Length) Enum.TryParse(moves[i], true, out m); p.moves[i] = m; }
        return p;
    }
}
[Serializable] public class ADMoveDef { public string id; public bool attack; public float damage; public float tier; public bool staggers; }
[Serializable] public class ADWoundWeights { public string move; public float arm = 0.25f, leg = 0.25f, torso = 0.4f, head = 0.1f; }
[Serializable]
public class ADRulesCfg
{
    public float planSeconds = 10f;
    public float fallbackBeatSeconds = 1.6f;       // controller: how long a beat lasts when no playback lane is present
    public float replaySeconds = 2.5f;             // controller: replay strip time after a clash
    public float resultSeconds = 0.5f;             // controller: pause before the next plan phase
    public string aiDifficulty = "normal";        // key into ai.difficulty (lane E)
    public float autoConfirmSeconds = 1.2f;        // -alphaauto: delay before Dracula's random plan is confirmed (the planning UI shows for this long)
    public float autoRestartSeconds = 0f;          // -alphaauto: > 0 restarts the duel this long after the kill screen (0 = stay)
    public bool autoPlayDracula = false;           // test/capture: the controller also picks Dracula's plan (also -alphaauto)
    public int beats = 3;
    // v56 MVP feature flags (all default OFF: wounds/gore/mesh cut, crit cam + slow-mo, specials, finishers). Read through ADFlags.
    public bool enableWounds = false, enableGore = false, enableCritCam = false, enableSpecials = false, enableFinishers = false;
    public float alphaTimeoutSeconds = 300f;       // -alphaauto hard stop (command line -alphatimeout:<s> overrides)
    public float autoEndScreenSeconds = 3f;        // -alphaauto: seconds the end screen is shown before exit 0
    public float reviveWaitSeconds = 0.4f;         // restart: pause after the revive before the next plan phase
    public float bootWaitSeconds = 10f;            // bootstrap: max seconds to wait for the two fighters
    public bool repeatPlanOnTimeout = true;
    public int hpDracula = 80, hpPitborn = 120;
    public string usedSpecialFallback = "Guard";   // replaces a second Special in a plan / a Special already spent
    public float damageVariance = 0.1f;            // +- fraction
    public int minDamage = 1;
    public float staggerTierPenalty = 1.5f;        // added to the tier of a staggered fighter's next beat
    public float tierEpsilon = 0.01f;              // tiers closer than this are a tie (clash)
    public float clashDamageMul = 0f;              // damage each side takes in a clash (x its attacker's damage)
    public bool tradeOnAttackVsAttack = true;      // slower attack still lands after the faster one (unless a matrix row says Interrupted)
    public float riposteChance = 0.5f;
    public float riposteDamage = 8f;               // flat base damage of a riposte (goes through stance/crit/wound mults)
    public ADMoveDef[] moves;
    public ADWoundWeights[] fallbackWounds;
    public int woundMinDamage = 5;                 // fallback: a hit this big adds 1 wound severity
    public int maxSeverity = 3;
    public float armHeavyMulPerSeverity = 0.15f;   // fallback: heavy damage x (1 - n*sev), floor 0.4
    public float legTierPerSeverity = 0.5f;        // fallback: speed tier penalty per leg severity
    public ADPlanDef defaultPlan;                  // used when a side never submitted a plan
}
[Serializable]
public class ADMatrixRow
{
    public string a, b;            // move names, comma lists, or "*". Row is tried as (a,b) then mirrored as (b,a).
    public string outA, outB;      // ADOutcome names (for a's move / b's move)
    public float aHits, bHits;     // fraction of a's / b's base damage that lands on the other (0 = none)
    public bool staggerA, staggerB;// that fighter is staggered going into the next beat
    public bool riposteA, riposteB;// that fighter may riposte (riposteChance)
    public bool voidOther;         // a's move cancels b's (and vice versa for the mirrored row): used for stab interrupting heavy
    public string reason;          // '{a} beat {b}' : {a}/{b} become 'your guard' / 'his light'
}
[Serializable] public class ADMatrixCfg { public ADMatrixRow[] rows; }
[Serializable] public class ADChance { public float chance; public float dealtMul = 1f, takenMul = 1f; public string label = ""; }
[Serializable] public class ADStanceDef { public string id; public float dealtMul = 1f, takenMul = 1f, speedDelta; public ADChance[] chances; }
[Serializable] public class ADStancesCfg { public ADStanceDef[] rows; }
[Serializable] public class ADCritCfg { public float chance = 0.08f; public float mul = 1.75f; public float locationHead = 0f; }
[Serializable]
public class ADSpecialDef
{
    public float damage = 20f; public float tier = 5f; public float healFraction; public bool knockdown;
    // lane E fields (same JSON block), adopted by ADRules:
    public bool unblockable;                       // guard cannot hold it: GuardBreak, full damage
    public string guardMode = "";                  // "reduced": Guard only lets guardReduce of the damage through (no guard break); "break": like unblockable; "" = matrix row
    public float guardReduce = 0.5f;
    public string[] avoidedBy;                     // defensive moves that dodge it (e.g. Roll). Defensive moves not listed (Sidestep for an area slam) are caught.
    public bool critAllowed = true;
    public string hitLocation = "";                // "Torso" etc. forces the wound location; "" / "any" = rolled
    public int staggerBeats;                       // > 0: the victim of a landed special is staggered going into the next beat
}
[Serializable] public class ADSpecialCfg { public ADSpecialDef dracula, pitborn; }

// Root as JsonUtility sees it: unknown blocks (wounds, ai, camera, ui, finishers) are simply ignored here.
[Serializable]
public class ADRulesData
{
    public int version = 1;
    public int seed = 20261104;
    public ADRulesCfg rules;
    public ADMatrixCfg matrix;
    public ADStancesCfg stances;
    public ADCritCfg crit;
    public ADSpecialCfg special;

    // Code defaults for any block that is missing / empty in the json, so a bad data file never throws downstream.
    public void Fill()
    {
        if (rules == null) rules = new ADRulesCfg();
        if (rules.moves == null || rules.moves.Length == 0)
            rules.moves = new[] {
                new ADMoveDef { id = "Light", attack = true, damage = 7, tier = 2 }, new ADMoveDef { id = "Slash", attack = true, damage = 10, tier = 3 },
                new ADMoveDef { id = "Stab", attack = true, damage = 9, tier = 1 }, new ADMoveDef { id = "Heavy", attack = true, damage = 20, tier = 5 },
                new ADMoveDef { id = "Kick", attack = true, damage = 5, tier = 3, staggers = true }, new ADMoveDef { id = "Guard" }, new ADMoveDef { id = "Roll" },
                new ADMoveDef { id = "Sidestep" }, new ADMoveDef { id = "Special", attack = true, damage = 20, tier = 5 } };
        if (rules.beats < 1) rules.beats = 3;
        if (rules.planSeconds <= 0f) rules.planSeconds = 10f;
        if (rules.defaultPlan == null) rules.defaultPlan = new ADPlanDef { stance = "Neutral", moves = new[] { "Light", "Light", "Guard" } };
        if (rules.hpDracula < 1) rules.hpDracula = 80;
        if (rules.hpPitborn < 1) rules.hpPitborn = 120;
        if (matrix == null) matrix = new ADMatrixCfg();
        if (matrix.rows == null) matrix.rows = new ADMatrixRow[0];
        if (stances == null) stances = new ADStancesCfg();
        if (stances.rows == null || stances.rows.Length == 0)
            stances.rows = new[] { new ADStanceDef { id = "Aggressive", dealtMul = 1.25f, takenMul = 1.2f }, new ADStanceDef { id = "Defensive", dealtMul = 0.8f, takenMul = 0.75f }, new ADStanceDef { id = "Neutral" }, new ADStanceDef { id = "Trickster" } };
        if (crit == null) crit = new ADCritCfg();
        if (special == null) special = new ADSpecialCfg();
        if (special.dracula == null) special.dracula = new ADSpecialDef();
        if (special.pitborn == null) special.pitborn = new ADSpecialDef();
    }
}
