using System;

// ZONES v1 pure core. No UnityEngine, no Time, no UnityEngine.Random, no file access: it is the part a headless test, a server or a later Supabase store can run unchanged.
// Respawn is LAZY (V1 world-spawns / V3 zone_spawns pattern): nothing counts down. A kill stores respawnAtMs (UTC ms); Look(rec, now) says "alive" once now >= respawnAtMs.
// Jitter and the creature choice come from an FNV-1a hash of (zone, spawn, generation), the same family as MoveVariants, so every client computes the same answer.
// The 60 Hz sim (Duel.cs) never calls this; wall-clock time exists only here and in ZoneSave/ZoneRuntime.
[Serializable] public class SpawnRec
{
    public string zone = "", spawn = "", creature = "";
    public int generation;          // kills so far on this spawn; the next life is life number generation (zone_spawns.generation)
    public long killedAtMs;         // UTC ms of the last kill, 0 = never killed
    public long respawnAtMs;        // UTC ms when it reads alive again (zone_spawns.respawn_at), 0 = alive
}
public struct SpawnLook { public bool alive; public long msLeft; public int generation; }

public static class ZoneCore
{
    public static Func<long> Clock = DefaultClock;   // tests replace it; production reads UTC
    static long DefaultClock() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L; }
    public static long NowMs() { return Clock(); }

    public static uint Hash(string zone, string spawn, int generation, string salt)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = Mix(h, zone); h = Mix(h, "|"); h = Mix(h, spawn); h = Mix(h, "|"); h = Mix(h, salt); h = Mix(h, "|");
            h ^= (uint)generation; h *= 16777619u; h ^= (uint)(generation >> 16); h *= 16777619u;
            return h;
        }
    }
    static uint Mix(uint h, string s) { unchecked { if (s != null) for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; } return h; } }

    /// <summary>Weighted index; weights &lt;= 0 never win; -1 when nothing can win.</summary>
    public static int PickIndex(uint h, int[] weights)
    {
        if (weights == null) return -1;
        long total = 0; for (int i = 0; i < weights.Length; i++) if (weights[i] > 0) total += weights[i];
        if (total <= 0) return -1;
        long r = h % (uint)total, acc = 0;
        for (int i = 0; i < weights.Length; i++) { if (weights[i] <= 0) continue; acc += weights[i]; if (r < acc) return i; }
        return -1;
    }
    public static int PickCreature(string zone, string spawn, int generation, int[] weights) { return PickIndex(Hash(zone, spawn, generation, "creature"), weights); }

    /// <summary>Respawn delay for this life: minS..maxS inclusive, decided by the hash (same for everyone, no wall clock, no random).</summary>
    public static int RespawnSeconds(string zone, string spawn, int generation, int minS, int maxS)
    {
        if (minS < 0) minS = 0; if (maxS < minS) maxS = minS;
        uint span = (uint)(maxS - minS + 1);
        return minS + (int)(Hash(zone, spawn, generation, "respawn") % span);
    }

    public static SpawnLook Look(SpawnRec r, long nowMs)
    {
        var l = new SpawnLook();
        if (r == null || r.respawnAtMs <= 0 || r.respawnAtMs <= nowMs) { l.alive = true; l.msLeft = 0; l.generation = r == null ? 0 : r.generation; }
        else { l.alive = false; l.msLeft = r.respawnAtMs - nowMs; l.generation = r.generation; }
        return l;
    }

    /// <summary>Store a kill. Returns the new record (the old one is not changed).</summary>
    public static SpawnRec Kill(SpawnRec prev, string zone, string spawn, string creature, long nowMs, int minS, int maxS)
    {
        var n = new SpawnRec { zone = zone, spawn = spawn, creature = creature, generation = (prev == null ? 0 : prev.generation) + 1, killedAtMs = nowMs };
        n.respawnAtMs = nowMs + 1000L * RespawnSeconds(zone, spawn, n.generation, minS, maxS);
        return n;
    }

    /// <summary>Clock set back: a respawn time further away than maxS + guardS is pulled in to now + maxS. True when it changed.</summary>
    public static bool ClockGuard(SpawnRec r, long nowMs, int maxS, int guardS)
    {
        if (r == null || r.respawnAtMs <= 0) return false;
        long limit = nowMs + 1000L * (maxS + Math.Max(0, guardS));
        if (r.respawnAtMs <= limit) return false;
        r.respawnAtMs = nowMs + 1000L * maxS; return true;
    }

    // ---- hp policy (fractions 0..1 of max) ----
    public static float CarryHp(string policy, float hpFrac, float floorFrac)
    {
        if (policy == "full") return 1f;
        if (policy == "floor") return Math.Max(hpFrac, floorFrac);
        return hpFrac;   // "carry" and anything unknown: the gate heals nothing
    }
    /// <summary>Out-of-combat regen counted in SIM TICKS (60 per second), up to ceilFrac of max; never lowers hp that is already above the ceiling.</summary>
    public static float RegenStep(float hp, float max, int ticks, float perSecondFrac, float ceilFrac)
    {
        if (max <= 0f || ticks <= 0) return hp;
        float ceil = max * ceilFrac; if (hp >= ceil) return hp;
        float n = hp + max * perSecondFrac * ticks / 60f; return n > ceil ? ceil : n;
    }
}
