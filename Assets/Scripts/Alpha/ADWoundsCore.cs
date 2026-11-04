using System;

// ALPHA lane B: wounds. PURE C# (no UnityEngine), so the rules engine and the self-test can use it without the engine.
// Location ints match lane A's ADLoc order: 0 Arm, 1 Leg, 2 Torso, 3 Head (cast (int)ADLoc.X).
// All numbers come from the "wounds" block of Assets/Data/duel_rules.json (classes below are the JSON shape; the defaults here mirror alpha_wounds.json).

[Serializable] public class ADWoundWeightRow { public string move = "*"; public float arm = 1f, leg = 1f, torso = 2f, head = 0.5f; }
[Serializable] public class ADWoundLocRow { public string name = "torso"; public float damageMul = 1f; public float[] thresholds = new float[] { 8f, 18f, 32f }; }
[Serializable] public class ADWoundEffects
{
    public float[] heavyMulByArmSeverity = new float[] { 1f, 0.9f, 0.75f, 0.55f };   // x damage of Heavy (and Special) by the arm severity 0..3
    public float[] speedPenaltyByLegSeverity = new float[] { 0f, 0.5f, 1f, 1.5f };   // speed-tier penalty (higher = slower, same unit as ADFighter.speedTierPenalty) by leg severity 0..3
    public float critWoundMul = 2f;                                                  // a crit counts this much damage toward the severity thresholds
}
[Serializable] public class ADWoundsCfg
{
    public ADWoundWeightRow[] hitWeights = new ADWoundWeightRow[] {
        new ADWoundWeightRow { move = "light", arm = 2f, leg = 1f, torso = 2f, head = 0.5f },
        new ADWoundWeightRow { move = "slash", arm = 2f, leg = 1f, torso = 2f, head = 1f },
        new ADWoundWeightRow { move = "stab", arm = 1f, leg = 0.5f, torso = 3f, head = 1f },
        new ADWoundWeightRow { move = "heavy", arm = 1f, leg = 1f, torso = 2f, head = 2f },
        new ADWoundWeightRow { move = "kick", arm = 0.2f, leg = 3f, torso = 1f, head = 0.2f },
        new ADWoundWeightRow { move = "*", arm = 1f, leg = 1f, torso = 2f, head = 0.5f } };
    public ADWoundLocRow[] locations = new ADWoundLocRow[] {
        new ADWoundLocRow { name = "arm", damageMul = 0.85f }, new ADWoundLocRow { name = "leg", damageMul = 0.9f },
        new ADWoundLocRow { name = "torso", damageMul = 1f }, new ADWoundLocRow { name = "head", damageMul = 1f, thresholds = new float[] { 10f, 20f, 30f } } };
    public ADWoundEffects effects = new ADWoundEffects();
    public int seed = 4242;
    public ADGoreCfg gore = new ADGoreCfg();
    public ADCutsCfg cuts = new ADCutsCfg();
}

public struct ADWoundEvent { public object owner; public int loc, severity; public bool increased, crit; public float damage; }

public static class ADWoundRules
{
    public const int Arm = 0, Leg = 1, Torso = 2, Head = 3, Count = 4;
    public static readonly string[] Names = { "arm", "leg", "torso", "head" };

    static ADWoundWeightRow RowFor(ADWoundsCfg c, string move)
    {
        string m = string.IsNullOrEmpty(move) ? "*" : move.ToLowerInvariant();
        ADWoundWeightRow star = null;
        for (int i = 0; i < c.hitWeights.Length; i++)
        {
            var r = c.hitWeights[i]; if (r == null) continue;
            if (r.move == m) return r;
            if (r.move == "*") star = r;
        }
        return star ?? new ADWoundWeightRow();
    }

    /// <summary>Weighted location roll for one landed hit. rng returns [0,1).</summary>
    public static int RollLocation(ADWoundsCfg c, string move, Func<float> rng)
    {
        var r = RowFor(c, move);
        float a = Math.Max(0f, r.arm), l = Math.Max(0f, r.leg), t = Math.Max(0f, r.torso), h = Math.Max(0f, r.head);
        float sum = a + l + t + h; if (sum <= 0f) return Torso;
        float x = rng() * sum;
        if ((x -= a) < 0f) return Arm;
        if ((x -= l) < 0f) return Leg;
        if ((x -= t) < 0f) return Torso;
        return Head;
    }

    public static float LocationDamageMul(ADWoundsCfg c, int loc) { return loc >= 0 && loc < c.locations.Length && c.locations[loc] != null ? c.locations[loc].damageMul : 1f; }
}

/// <summary>Wound state of one fighter: accumulated damage and severity (0 none, 1 light, 2 serious, 3 crippled) per location.</summary>
public class ADWoundSet
{
    public static event Action<ADWoundEvent> OnWound;
    public readonly float[] damage = new float[ADWoundRules.Count];
    public readonly int[] severity = new int[ADWoundRules.Count];
    public object owner;
    readonly ADWoundsCfg cfg;
    public ADWoundSet(ADWoundsCfg c, object owner = null) { cfg = c ?? new ADWoundsCfg(); this.owner = owner; }

    public void Reset() { for (int i = 0; i < damage.Length; i++) { damage[i] = 0f; severity[i] = 0; } }

    /// <summary>Register a landed hit at loc. Returns true when the location's severity went up.</summary>
    public bool Apply(int loc, float dmg, bool crit)
    {
        if (loc < 0 || loc >= ADWoundRules.Count || dmg <= 0f) return false;
        damage[loc] += dmg * (crit ? Math.Max(1f, cfg.effects.critWoundMul) : 1f);
        var th = cfg.locations[loc] != null ? cfg.locations[loc].thresholds : null;
        int s = 0;
        if (th != null) for (int i = 0; i < th.Length; i++) if (damage[loc] >= th[i]) s = i + 1;
        bool up = s > severity[loc];
        if (up) severity[loc] = s;
        var h = OnWound; if (h != null) h(new ADWoundEvent { owner = owner, loc = loc, severity = severity[loc], increased = up, crit = crit, damage = dmg });
        return up;
    }

    /// <summary>Effect for the rules: damage multiplier on Heavy / Special from the arm wound.</summary>
    public float HeavyDamageMul() { var a = cfg.effects.heavyMulByArmSeverity; int s = severity[ADWoundRules.Arm]; return a != null && a.Length > 0 ? a[Math.Min(s, a.Length - 1)] : 1f; }
    /// <summary>Effect for the rules: speed tiers lost to the leg wound.</summary>
    public float SpeedTierPenalty() { var a = cfg.effects.speedPenaltyByLegSeverity; int s = severity[ADWoundRules.Leg]; return a != null && a.Length > 0 ? a[Math.Min(s, a.Length - 1)] : 0f; }
    /// <summary>Roll + register in one call; dmgOut is the damage after the location multiplier.</summary>
    public int RollAndApply(string move, float dmg, bool crit, Func<float> rng, out float dmgOut)
    {
        int loc = ADWoundRules.RollLocation(cfg, move, rng);
        dmgOut = dmg * ADWoundRules.LocationDamageMul(cfg, loc);
        Apply(loc, dmgOut, crit);
        return loc;
    }
}
