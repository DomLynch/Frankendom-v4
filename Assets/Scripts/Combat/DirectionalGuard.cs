using UnityEngine;

// Directional guard (lane CTRL, v68): which guard side covers a swing, and the side-match test Duel.Resolve adds to its guard check (controls.json guard{}).
// The parry window itself is untouched (parry.json / stamina.json); a parry now also needs the right side when guardRequiresSide is true.
public static class DirectionalGuard
{
    // guard side id (DuelFighter.guardSide) for a side name or alias; centre / unknown / null = 0
    public static int SideIdFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        var G = ControlsTable.Get().guard;
        if (name == G.centreName) return 0;
        for (int i = 0; i < G.sides.Length; i++)
        {
            var s = G.sides[i]; if (s == null) continue;
            if (s.name == name) return s.id;
            if (s.aliases != null) for (int k = 0; k < s.aliases.Length; k++) if (s.aliases[k] == name) return s.id;
        }
        return 0;
    }
    // -1 = not a guard side name at all (used by the match table: a typo must not silently mean centre)
    static int SideIdStrict(string name)
    {
        var G = ControlsTable.Get().guard;
        if (name == G.centreName) return 0;
        if (G.centreAliases != null) for (int k = 0; k < G.centreAliases.Length; k++) if (G.centreAliases[k] == name) return 0;
        for (int i = 0; i < G.sides.Length; i++)
        {
            var s = G.sides[i]; if (s == null) continue;
            if (s.name == name) return s.id;
            if (s.aliases != null) for (int k = 0; k < s.aliases.Length; k++) if (s.aliases[k] == name) return s.id;
        }
        return -1;
    }
    public static string NameOf(int id)
    {
        var G = ControlsTable.Get().guard;
        if (id == 0) return G.centreName;
        for (int i = 0; i < G.sides.Length; i++) if (G.sides[i] != null && G.sides[i].id == id) return G.sides[i].name;
        return G.centreName;
    }

    // GUARDLOW v1: Animator state (pose clip) of a guard side id: controls.json guard.sides[].pose; no row / empty pose = the legacy ids (1 left, 2 right, 3 up), else guard_idle
    public static string PoseOf(int id)
    {
        var G = ControlsTable.Get().guard;
        if (G.sides != null) for (int i = 0; i < G.sides.Length; i++) { var s = G.sides[i]; if (s != null && s.id == id && !string.IsNullOrEmpty(s.pose)) return s.pose; }
        return id == 1 ? "guard_left" : id == 2 ? "guard_right" : id == 3 ? "guard_up" : "guard_idle";
    }

    // guard names that cover an attack direction (MoveDef.direction); null = no row = any guard covers it
    public static string[] Covering(string attackDir)
    {
        var M = ControlsTable.Get().guard.match;
        if (string.IsNullOrEmpty(attackDir) || M == null) return null;
        for (int i = 0; i < M.Length; i++) if (M[i] != null && M[i].attack == attackDir) return M[i].guards;
        return null;
    }

    // first guard side id that covers the move (what a perfect reader holds); 0 when the move needs no side
    public static int BestSideFor(MoveDef m)
    {
        var g = m != null ? Covering(m.direction) : null;
        if (g == null) return 0;
        for (int i = 0; i < g.Length; i++) { if (g[i] == "*") return 0; int id = SideIdStrict(g[i]); if (id >= 0) return id; }
        return 0;
    }

    // Duel.Resolve: true when the defender's guard side covers the swing. heavy = Duel's heavy flag (block ease: heavyBlockWindowBonusMs + blockEase row).
    public static bool SideOk(DuelFighter a, DuelFighter d, MoveDef m, bool heavy)
    {
        if (!ControlsTable.SideRequired || d == null || m == null) return true;
        if (d.FlowHuman && !ControlsTable.Get().guard.classicRequiresSide && !DirectionalInput.IsSwiping(d)) return true;   // a human with no way to pick a side right now
        var g = Covering(m.direction);
        if (g == null || g.Length == 0) return true;
        var G = ControlsTable.Get().guard;
        float ms = G.sideGraceMs + (heavy ? G.heavyBlockWindowBonusMs : 0f);
        string cls = heavy ? "heavy" : "light";
        if (G.blockEase != null) for (int i = 0; i < G.blockEase.Length; i++) if (G.blockEase[i] != null && G.blockEase[i].cls == cls) { ms += G.blockEase[i].bonusMs; break; }
        int grace = Mathf.Max(0, Mathf.RoundToInt(ms * 60f / 1000f));   // sim ticks (60 Hz)
        for (int i = 0; i < g.Length; i++)
        {
            if (g[i] == "*") return true;
            int id = SideIdStrict(g[i]); if (id < 0) continue;
            if (d.guardSide == id) return true;
            if (grace > 0 && d.DirSideHeldWithin(id, grace)) return true;
        }
        if (ControlsTable.Get().debugLog) Debug.Log("[DirGuard] " + d.name + " guard " + NameOf(d.guardSide) + " does not cover " + m.id + " (" + m.direction + ")");
        return false;
    }

    // v69: AIBrain's guard side for a flowBotOn fighter (the balance bot): the guard side that covers the foe's current swing with chance bot.sideReadChance
    // (one deterministic roll per half second of sim time, so a side is not re-rolled every tick), else a wrong side; no swing = keep the side it holds
    public static int BotSide(DuelFighter df, DuelFighter foe)
    {
        var m = foe != null ? foe.CurrentMove : null;
        if (m == null) return df.guardSide;
        int best = BestSideFor(m);
        uint h = (uint)(Duel.TickCount / 30) * 2654435761u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        float roll = (h & 0xFFFFu) / 65535f;
        if (roll < ControlsTable.Get().bot.sideReadChance) return best;
        var S = ControlsTable.Get().guard.sides; int n = S.Length + 1;
        for (int k = 1; k <= n; k++) { int idx = (best + k) % n; int id = idx == 0 ? 0 : (S[idx - 1] != null ? S[idx - 1].id : 0); if (id != best) return id; }
        return best;
    }

    // FlowBot: aim the guard at the incoming swing with chance sideReadChance (roll, pick = two 0..1 rolls from the bot's own deterministic RNG), else a wrong side
    public static void BotAim(DuelFighter df, MoveDef incoming, float roll, float pick, float chance = -1f)   // BALANCE2 v96: chance >= 0 overrides controls.json bot.sideReadChance (deliberate parry / guard-cancel)
    {
        if (df == null || !ControlsTable.SideRequired) return;
        int best = BestSideFor(incoming);
        if (roll < (chance >= 0f ? chance : ControlsTable.Get().bot.sideReadChance)) { df.guardSide = best; return; }   // BALANCE2 v96
        var S = ControlsTable.Get().guard.sides;
        int n = S.Length + 1;   // centre + every side
        int start = Mathf.Min(n - 1, (int)(pick * n));
        for (int k = 0; k < n; k++)
        {
            int idx = (start + k) % n; int id = idx == 0 ? 0 : (S[idx - 1] != null ? S[idx - 1].id : 0);
            if (id != best) { df.guardSide = id; return; }
        }
    }
}
