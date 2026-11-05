using UnityEngine;

// Directional presses + guard-side history (lane CTRL, v68). Partial of DuelFighter.
// Hooks into DuelFighter: Tick -> DirTick; TickFree (heavy / light / thrust start) and DuelFighterFlow.FlowChain (chain start) -> DirPick.
public partial class DuelFighter
{
    string pDirMove, pDirType; int pDirTick = -9999, pDirTtl;
    int[] dirSideTick;   // last sim tick each guard side id was held in the Guard state (block ease, DirectionalGuard.SideOk)

    // press type the buffers know for a move: charging moves = heavy, thrust = thrust, kick = kick, the rest = light
    static string PressTypeOf(MoveDef m)
    {
        if (m == null) return "light";
        if (m.charges) return "heavy";
        if (m.id == "thrust") return "thrust";
        if (m.id == "kick") return "kick";
        return "light";
    }

    // first id of the list that exists in this fighter's MoveTable (controls.json attack.map fallbacks); null = none
    public string FirstMove(string[] ids)
    {
        if (ids == null || moves == null) return null;
        for (int i = 0; i < ids.Length; i++) if (!string.IsNullOrEmpty(ids[i]) && moves.Get(ids[i]) != null) return ids[i];
        return null;
    }

    // a press that names the exact move: it rides the normal light / heavy / thrust buffer (same queue, chain rows, cancels, stamina) and replaces the default pick when that buffer starts a move
    public void PressDirected(string moveId)
    {
        var m = moves != null && !string.IsNullOrEmpty(moveId) ? moves.Get(moveId) : null;
        if (m == null) { Press("light"); return; }
        string type = PressTypeOf(m);
        pDirMove = moveId; pDirType = type; pDirTick = Duel.TickCount; pDirTtl = CombatFeel.QueueTtl(this, (int)R("bufferTtl", 11));
        Press(type);
        if (ControlsTable.Get().debugLog) Debug.Log("[DirInput] " + name + " press " + moveId + " (" + type + ") ttl " + pDirTtl);
    }

    // the move id a buffer of this type starts: the directed one when it is still live, else the old default
    // v99 (QC5): the undirected fallback goes through MoveVariants (move_variants.json weights, only moves that exist); no row = the old fallback
    string VariantFallback(string type, string fallback) { string v = MoveVariants.Pick(this, type, fallback); return v != fallback && moves != null && moves.Get(v) != null ? v : fallback; }
    string DirPick(string type, string fallback)
    {
        if (pDirMove == null) return VariantFallback(type, fallback);
        bool live = Duel.TickCount - pDirTick <= pDirTtl + 1;
        if (!live) { pDirMove = null; return VariantFallback(type, fallback); }
        if (pDirType != type) return VariantFallback(type, fallback);
        string id = pDirMove; pDirMove = null;
        return moves != null && moves.Get(id) != null ? id : fallback;
    }

    // hold-to-heavy upgrade / heavy re-aim (heavyGraceMs): restart the current wind-up as another move while t <= maxTick (the old move's stamina is given back)
    public bool RestartDirected(string id, int maxTick)
    {
        if (state != FState.Windup || move == null || string.IsNullOrEmpty(id) || move.id == id || t > maxTick) return false;
        var m = moves.Get(id); if (m == null) return false;
        if (m.charges && !HeavyAllowed()) return false;
        stamina = Mathf.Min(maxStamina, stamina + MoveCost(move));
        StartMove(id, false);
        return true;
    }

    void DirTick()
    {
        if (state != FState.Guard) return;
        if (dirSideTick == null) dirSideTick = new int[8];
        int s = Mathf.Clamp(guardSide, 0, dirSideTick.Length - 1);
        dirSideTick[s] = Duel.TickCount;
    }
    // true when guard side id was held (Guard state) within the last `ticks` sim ticks
    public bool DirSideHeldWithin(int id, int ticks)
    {
        if (dirSideTick == null || id < 0 || id >= dirSideTick.Length || dirSideTick[id] <= 0) return false;
        return Duel.TickCount - dirSideTick[id] <= ticks;
    }
}
