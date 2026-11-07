using System;

// CHASE v1 (laneCHASE): the pure part of "a monster follows you and gives up". No UnityEngine, no Time, no Random: a function of (state, inputs, ticks), so it behaves the same at any frame rate
// and a server could run it. All numbers come from chase.json (ChaseRow). UNPROVEN IN UNITY.
[Serializable] public class ChaseRow
{
    public string id;
    public float speedMulOfPlayerRun, startGap, stopGap, slowZoneM, accelMps2, giveUpGap, outOfReachGap, giveUpOutOfReachS, giveUpPauseS;
    public float homeSpeedMulOfWalk, homeArriveDist, followGateMaxGap, followArriveBehindM, runClipFromMps, walkClipBelowMps, animMin, animMax;
    public bool resetOnHome, followThroughGate;
    public float handoverSlackM;   // FOESEP v1: hand back to the AI at gap <= stopGap + this (the speed ramp reaches stopGap only asymptotically: v108c stalled at 2.62 / 3.91 m)
}

public enum ChaseMode { Fight, Chase, Pause, Home, Done }

public struct ChaseState { public ChaseMode mode; public float speed; public int outTicks, pauseTicks; }

public static class ChaseCore
{
    /// <summary>One update covering n sim ticks. gap = metres between bodies, homeDist = metres from the creature to home, leash = spawn row leash (0 = no limit),
    /// playerRun = the player's current run speed in m/s. Returns the speed to move at (m/s, toward the player in Chase, toward home in Home).</summary>
    public static float Step(ref ChaseState s, ChaseRow r, int n, float gap, float homeDist, float leash, float playerRun, float ownWalk)
    {
        float dt = n / 60f;
        if (s.mode == ChaseMode.Fight)
        {
            if (gap > r.startGap) { s.mode = ChaseMode.Chase; s.outTicks = 0; }
            else { s.speed = 0f; return 0f; }
        }
        if (s.mode == ChaseMode.Chase)
        {
            if (gap <= r.stopGap + r.handoverSlackM) { s.mode = ChaseMode.Fight; s.speed = 0f; s.outTicks = 0; return 0f; }
            s.outTicks = gap > r.outOfReachGap ? s.outTicks + n : 0;
            if (gap > r.giveUpGap || s.outTicks / 60f > r.giveUpOutOfReachS || (leash > 0f && homeDist > leash)) { s.mode = ChaseMode.Pause; s.pauseTicks = 0; s.speed = 0f; return 0f; }
            float ramp = r.slowZoneM > 0f ? Math.Max(0f, Math.Min(1f, (gap - r.stopGap) / r.slowZoneM)) : 1f;
            float want = r.speedMulOfPlayerRun * playerRun * ramp;
            s.speed = MoveTowards(s.speed, want, r.accelMps2 * dt);
            return s.speed;
        }
        if (s.mode == ChaseMode.Pause)
        {
            s.speed = 0f; s.pauseTicks += n;
            if (s.pauseTicks / 60f >= r.giveUpPauseS) s.mode = ChaseMode.Home;
            return 0f;
        }
        if (s.mode == ChaseMode.Home)
        {
            if (homeDist <= r.homeArriveDist) { s.mode = ChaseMode.Done; s.speed = 0f; return 0f; }
            s.speed = MoveTowards(s.speed, r.homeSpeedMulOfWalk * ownWalk, r.accelMps2 * dt);
            return s.speed;
        }
        return 0f;
    }
    static float MoveTowards(float a, float b, float d) { return Math.Abs(b - a) <= d ? b : a + Math.Sign(b - a) * d; }

    /// <summary>Run or walk clip with hysteresis: run from runFrom m/s up, walk at walkBelow and under, in between keep what it was.</summary>
    public static bool UseRun(bool wasRun, float speed, float runFrom, float walkBelow) { return speed >= runFrom ? true : (speed <= walkBelow ? false : wasRun); }
    public static float AnimSpeed(float speed, float nominal, float lo, float hi) { return nominal <= 0f ? 1f : Math.Max(lo, Math.Min(hi, speed / nominal)); }
}
