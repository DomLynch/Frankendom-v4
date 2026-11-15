using UnityEngine;

// K10 (bandits dev, file-only, NOT applied): ARROW BUNDLE = a stackable consumable of consumables.json with kind "ammo" and `arrows` N.
// It rides the potion rails unchanged (stall Buy tab / PotionTrade / PotionStore / LootSave stacks / use key H, cycle J). Using it adds N arrows to the bow quiver, capped at bow.json ammo.
// Default = old behaviour: with no "ammo" row in consumables.json nothing here is ever called.
public static class AmmoRefill
{
    public const string Kind = "ammo";
    public static bool IsAmmo(PotionCfg.PotionC p) { return p != null && p.kind == Kind; }

    /// <summary>PotionUse.Use: add the bundle's arrows. False (nothing consumed) when the bow state is missing, the quiver is off or already full.</summary>
    public static bool Add(DuelFighter f, PotionCfg.PotionC p, out int added)
    {
        added = 0; if (f == null || p == null) return false;
        return BowHooks.AddAmmo(f, Mathf.Max(1, p.arrows), out added);
    }

    public static string Toast(int added) { return string.Format(System.Globalization.CultureInfo.InvariantCulture, PotionCfg.Get().text.ammo, added); }
}
