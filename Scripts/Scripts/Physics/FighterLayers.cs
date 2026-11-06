using UnityEngine;

// HCKIT1 v93 (K5): the two legacy fighter layers by NAME, built on lanePHYS1's physics.json "layers" rows (legacyCharacter = 8, legacyOtherFighter = 9). Replaces the raw
// `1 << 8` / `layer == 8` / `~((1 << 8) | (1 << 9))` literals that lanePHYS1's PhysMasks.Ground did not cover. No physics.json (or no such row) = 8 / 9, exactly the old literals.
// Ground / foot / camera rays use PhysMasks.Ground / PhysMasks.NotPhys (PHYS1); this class is only "which layer IS a fighter".
// REQUIRES lanePHYS1 (PhysicsCfg). apply_hckit1.py skips the K5 hunks while Scripts/Physics/PhysicsCfg.cs is not in the project.
public static class FighterLayers
{
    static bool ready; static int character = 8, other = 9;
    static void Ensure()
    {
        if (ready) return; ready = true;
        var c = PhysicsCfg.Get();
        character = c.LayerIndex("legacyCharacter", character); other = c.LayerIndex("legacyOtherFighter", other);
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { ready = false; }

    /// <summary>Layer index of the fighter's own model (8 today).</summary>
    public static int Character { get { Ensure(); return character; } }
    /// <summary>Layer index of "another fighter" (9 today).</summary>
    public static int OtherFighter { get { Ensure(); return other; } }
    public static int CharacterBit { get { return 1 << Character; } }
    /// <summary>Bit mask of both fighter layers (the old (1 &lt;&lt; 8) | (1 &lt;&lt; 9)).</summary>
    public static int Mask { get { return (1 << Character) | (1 << OtherFighter); } }
    public static bool IsFighterLayer(int layer) { return layer == Character || layer == OtherFighter; }
}
