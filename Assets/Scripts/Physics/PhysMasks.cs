using UnityEngine;

// PHYS1 v1: the ONE place that knows the physics layers and the shared ground mask (replaces the dead ~((1 << 8) | (1 << 9)) literals).
// Layer indexes come from physics.json "layers"; groundIgnore lists the layer names a ground ray must not hit (legacy character layers 8 / 9 + the three new ones).
// Ground = every layer except those. NotPhys = every layer except Hurtbox / RagdollBody / PhysProp (for data-driven masks that already carry their own ignoreLayers: mask & PhysMasks.NotPhys).
public static class PhysMasks
{
    static bool ready;
    static int hurtbox = 10, ragdoll = 11, prop = 12, ground = ~0, notPhys = ~0;

    static void Ensure()
    {
        if (ready) return;
        ready = true;
        var c = PhysicsCfg.Get();
        hurtbox = c.LayerIndex("hurtbox", hurtbox); ragdoll = c.LayerIndex("ragdollBody", ragdoll); prop = c.LayerIndex("physProp", prop);
        notPhys = ~((1 << hurtbox) | (1 << ragdoll) | (1 << prop));
        int g = ~0;
        for (int i = 0; i < c.groundIgnore.Length; i++) { int li = c.LayerIndex(c.groundIgnore[i], -1); if (li >= 0 && li < 32) g &= ~(1 << li); }
        ground = c.groundIgnore.Length == 0 ? notPhys & ~FighterLayers.Mask : g;   // no data = the old literal plus the new layers
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { ready = false; PhysicsCfg.Reload(); }

    public static int HurtboxLayer { get { Ensure(); return hurtbox; } }
    public static int RagdollLayer { get { Ensure(); return ragdoll; } }
    public static int PropLayer { get { Ensure(); return prop; } }
    /// <summary>Layers a ground / foot / floor ray may hit.</summary>
    public static int Ground { get { Ensure(); return ground; } }
    /// <summary>Everything except Hurtbox / RagdollBody / PhysProp.</summary>
    public static int NotPhys { get { Ensure(); return notPhys; } }
    public static int Bit(string layerName) { int i = PhysicsCfg.Get().LayerIndex(layerName, -1); return i >= 0 && i < 32 ? 1 << i : 0; }
}
