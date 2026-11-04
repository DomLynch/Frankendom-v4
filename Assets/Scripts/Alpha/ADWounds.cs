using System;
using System.Collections.Generic;

// ALPHA lane B: the IADWounds provider for lane A's ADRules (assign: rules.Wounds = new ADWounds();).
// Location weights, severity thresholds and effects come from duel_rules.json "wounds" (ADWoundsData). Severity is written to ADFighter.wounds[] and
// ADFighter.speedTierPenalty, as the interface requires. If a Unity ADWoundsRuntime is bound for the victim's side (ADWounds.Bind), the gore (decals, spray) is shown too.
// Note: IADWounds has no hook for a per-location damage multiplier; locations[].damageMul is only used by ADWoundsRuntime.RegisterHit (default 1.0 in data).
public class ADWounds : IADWounds
{
    readonly ADWoundsCfg cfg;
    readonly Dictionary<ADFighter, ADWoundSet> sets = new Dictionary<ADFighter, ADWoundSet>();
    static readonly Dictionary<ADSide, ADWoundsRuntime> bound = new Dictionary<ADSide, ADWoundsRuntime>();

    public ADWounds() : this(ADGoreGate.Wounds ? ADWoundsData.Get() : null) { }
    public ADWounds(ADWoundsCfg c) { cfg = c ?? new ADWoundsCfg(); }

    /// <summary>Hook a fighter's ADWoundsRuntime (gore) to its side.</summary>
    public static void Bind(ADSide side, ADWoundsRuntime rt) { if (rt == null) bound.Remove(side); else bound[side] = rt; }

    ADWoundSet SetFor(ADFighter f)
    {
        ADWoundsRuntime rt; bound.TryGetValue(f.side, out rt);
        ADWoundSet s;
        if (rt != null && rt.wounds != null) s = rt.wounds;
        else if (!sets.TryGetValue(f, out s)) { s = new ADWoundSet(cfg, f); sets[f] = s; }
        // a new fight starts with a clean ADFighter (severity 0 everywhere): drop the old accumulation and the old gore
        bool clean = true; for (int i = 0; i < f.wounds.Length; i++) if (f.wounds[i] != 0) clean = false;
        if (clean) { bool any = false; for (int i = 0; i < s.damage.Length; i++) if (s.damage[i] > 0f) any = true; if (any) { s.Reset(); if (rt != null) rt.ResetGore(); } }
        return s;
    }

    public ADLoc RollLocation(ADMove move, IADRng rng)
    {
        if (!ADGoreGate.Wounds) return ADLoc.Torso;   // disabled: neutral, no rng drawn
        return (ADLoc)ADWoundRules.RollLocation(cfg, move.ToString(), () => (float)rng.Next01());
    }

    public float DamageMul(ADFighter attacker, ADMove move)
    {
        if (!ADGoreGate.Wounds) return 1f;            // disabled: no wound damage multiplier
        if (move != ADMove.Heavy && move != ADMove.Special) return 1f;
        return SetFor(attacker).HeavyDamageMul();
    }

    public void Apply(ADFighter victim, ADLoc loc, int damage, bool crit)
    {
        if (!ADGoreGate.Wounds) return;               // disabled: no severity, no speed penalty, no gore
        var s = SetFor(victim);
        ADWoundsRuntime rt; bound.TryGetValue(victim.side, out rt);
        if (rt != null) rt.ApplyAt((int)loc, damage, crit); else s.Apply((int)loc, damage, crit);
        for (int i = 0; i < ADWoundRules.Count; i++) victim.wounds[i] = s.severity[i];
        victim.speedTierPenalty = s.SpeedTierPenalty();
    }
}
