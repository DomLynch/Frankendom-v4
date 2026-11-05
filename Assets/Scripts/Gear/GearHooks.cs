using UnityEngine;

// The small seam between gear and the combat code (lane GEAR v79). Combat never names GearSlots: it asks GearHooks.*(fighter), which returns the NEUTRAL value
// (1 / 0) for any fighter that has no IGearStatSource, and when gear.json is missing or "enabled": false. So Dracula / Pitborn (no loadout) fight exactly as before.
public interface IGearStatSource { GearStats Stats { get; } }

// summed stats of everything a wearer has equipped + the derived multipliers (conversion numbers: gear.json stats{})
public struct GearStats
{
    public float armor, poise, weight;
    public static GearStats operator +(GearStats a, GearStats b) { return new GearStats { armor = a.armor + b.armor, poise = a.poise + b.poise, weight = a.weight + b.weight }; }

    // multiplier on the damage this wearer takes (1 = none)
    public float DamageTakenMul
    {
        get { var r = GearTable.Get().stats; if (armor <= 0f) return 1f; return 1f - Mathf.Min(r.maxReduction, armor / (armor + Mathf.Max(0.001f, r.armorK))); }
    }
    // extra hits the poise meter (poise.json meter.hits) needs before it absorbs
    public int PoiseHitsBonus
    {
        get { var r = GearTable.Get().stats; if (poise <= 0f || r.poisePerMeterHit <= 0f) return 0; return Mathf.FloorToInt(poise / r.poisePerMeterHit); }
    }
    // multiplier on the posture a clean hit adds to this wearer
    public float PostureTakenMul
    {
        get { var r = GearTable.Get().stats; if (poise <= 0f) return 1f; return Mathf.Max(r.minPostureMul, 1f - poise * r.posturePerPoise); }
    }
    // multiplier on stamina regeneration (heavy kit slows the breath)
    public float StaminaRegenMul
    {
        get { var r = GearTable.Get().stats; float over = weight - r.weightFree; if (over <= 0f) return 1f; return Mathf.Max(r.minRegenMul, 1f - over * r.regenPerWeight); }
    }
}

public static class GearHooks
{
    static bool Active { get { return GearTable.Get().enabled; } }
    static bool TryStats(Component who, out GearStats s)
    {
        s = default(GearStats);
        if (who == null || !Active) return false;
        var src = who.GetComponent<IGearStatSource>(); if (src == null) return false;
        s = src.Stats; return true;
    }
    public static float DamageTakenMul(Component fighter) { GearStats s; return TryStats(fighter, out s) ? s.DamageTakenMul : 1f; }       // Duel.Resolve: clean hit + block chip
    public static float PostureTakenMul(Component fighter) { GearStats s; return TryStats(fighter, out s) ? s.PostureTakenMul : 1f; }     // Duel.Resolve: posture from a clean hit
    public static int PoiseHitsBonus(Component fighter) { GearStats s; return TryStats(fighter, out s) ? s.PoiseHitsBonus : 0; }          // PoiseTable.MeterFor: meter.hits + this
    public static float StaminaRegenMul(Component fighter) { GearStats s; return TryStats(fighter, out s) ? s.StaminaRegenMul : 1f; }     // DuelFighter stamina regen (weight)
}
