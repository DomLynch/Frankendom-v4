using UnityEngine;

// Stance damage modifiers. Hooked from Duel.Resolve (clean hit + guard chip): dmg *= STSteer.DamageMul(attacker, defender).
// Dracula attacking uses the stance's dealtMul, Dracula defending uses takenMul. Trickster rolls per hit with a seeded LCG (separate from the AI stream).
public static class STStanceMods
{
    static uint rng = 1;
    public static STCfg Cfg;
    public static float LastMul = 1f;          // last multiplier handed out (UI / log)
    public static string LastNote = "";        // e.g. "trickster +30% dealt"
    public static int Hits, Boosts;

    public static void Seed(int seed) { rng = unchecked((uint)seed * 2654435761u + 12345u); if (rng == 0) rng = 1; }
    static float Roll() { rng = unchecked(rng * 1664525u + 1013904223u); return (float)(rng / 4294967296.0); }

    public static float Mul(DuelFighter a, DuelFighter d)
    {
        var dr = STSteer.Dracula;
        if (dr == null || Cfg == null) return 1f;
        bool dealt = a == dr, taken = d == dr;
        if (!dealt && !taken) return 1f;
        var st = Cfg.Stance(STSteer.Stance);
        if (st == null) return 1f;
        float m = 1f; LastNote = "";
        if (dealt) m *= st.dealtMul;
        if (taken) m *= st.takenMul;
        var tr = st.trickster;
        if (tr != null && tr.enabled)
        {
            float r = Roll(); Hits++;
            if (dealt && r < tr.pDealt) { m *= tr.dealtMul; LastNote = "trickster dealt x" + tr.dealtMul.ToString("0.00"); Boosts++; }
            else if (taken && r < tr.pTaken) { m *= tr.takenMul; LastNote = "trickster taken x" + tr.takenMul.ToString("0.00"); Boosts++; }
        }
        LastMul = m;
        return m;
    }
}
