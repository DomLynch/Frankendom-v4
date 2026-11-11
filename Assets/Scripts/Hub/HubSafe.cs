using UnityEngine;

// laneNPCSPAWN v1: the safe-zone rule, as three small static questions. Each one is a PURE function of the player's position (read at the moment it is asked, in the sim tick or the input call that asks),
// so it replays the same at any frame rate. While hub.json is off, On is false and every answer is false after ONE static bool read: the duel is bit-for-bit what it was.
//   NoHit(d)        : Duel.Resolve skips a strike whose defender is the player standing in the camp (hub.json safe.combat.damageToPlayer = 0: health stays as it is; blocks, parries and chip damage are skipped too).
//   Refuses(press)  : the player's attack presses are refused in the camp (STALL's MarketSafe.Refuses calls it first); rolls / sidesteps / backsteps stay (hub.json safe.combat.allowPress).
//   GuardRefused    : the guard hold is not read in the camp (PlayerBrain hunk).
// Reuse: laneZONESPAN keeps monsters out (destSafe: followers stop 1.5 m before the door face, the gate is not shut by an engaged creature, no spawns in a safe zone); laneSTALL's hunks in DuelFighter /
// PlayerBrain already route presses to MarketSafe; this lane adds the same rule for a camp WITHOUT the market switched on, plus the damage stop. UNPROVEN IN UNITY.
public static class HubSafe
{
    /// <summary>Set once by HubNpcs.Boot when hub.json is enabled and valid.</summary>
    public static bool On;
    public static float RefusedAt = -99f;
    static HubAreaRow A; static HubSafeCombat C; static HubAreaRow[] X = new HubAreaRow[0];   // MARKET2 v1: X = the areas of the enabled extra camps (hub.json camps[])
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { On = false; RefusedAt = -99f; A = null; C = null; X = new HubAreaRow[0]; }

    public static void Arm(HubData k)
    {
        A = k.area; C = k.safe.combat; On = true;
        var l = new System.Collections.Generic.List<HubAreaRow>(); if (k.camps != null) for (int i = 0; i < k.camps.Length; i++) { var c = HubCfg.Camp(i); if (c != null) l.Add(c.area); }   // MARKET2 v1: same rule, same combat rows, in the extra camps too
        X = l.ToArray();
    }
    public static void Disarm() { On = false; }

    /// <summary>The player is standing in the camp rectangle right now.</summary>
    public static bool PlayerInside()
    {
        if (!On) return false;
        if (TraderKnows.AmbushLive) return false; /* S9: the camp is not safe while the impostor lives */ var p = GameLoop.Player; if (p == null) return false;
        var t = p.transform.position; if (HubArea.Contains(t.x, t.z, A.zMin, A.zMax, A.xHalf)) return true;
        for (int i = 0; i < X.Length; i++) if (HubArea.Contains(t.x, t.z, X[i].zMin, X[i].zMax, X[i].xHalf)) return true;   // MARKET2 v1: nothing in X unless a camp is enabled
        return false;
    }

    /// <summary>True when `d` is the player and the camp is safe right now: Duel.Resolve returns without touching health, posture or stamina.</summary>
    public static bool NoHit(DuelFighter d)
    {
        if (!On) return false;
        if (d == null || d != GameLoop.Player || C.damageToPlayer > 0f) return false;
        return PlayerInside();
    }

    public static bool Refuses(string press)
    {
        if (!On || C.playerAttack || !PlayerInside()) return false;
        for (int i = 0; i < C.allowPress.Length; i++) if (C.allowPress[i] == press) return false;
        RefusedAt = Time.unscaledTime; return true;
    }

    public static bool GuardRefused { get { return On && !C.playerGuard && PlayerInside(); } }
}
