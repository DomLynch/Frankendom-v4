using System;
using UnityEngine;

// CAMZONE v1 (laneCAMZONE, UNPROVEN in Unity): the lock-on camera in a ZONES run. Data: camera_pose.json "zone" row (CameraRig reads it with the rest of the pose).
// The v108b report: "sobek and pitborn fight messes up, the camera is gliding, birds eye, and gets stuck". The cause is NOT proven until a -camtrace clip exists; this file removes every way I found from code
// that lets the zone camera go high or freeze with two enemies (see _agents\laneCAMZONE\MANIFEST.md, section 2, for the ranked list with file:line):
//   Pick()      validates CameraRig.enemy every frame: the lock target must be an enemy that is FIGHTING Joan (opponent == Joan, not giving up / walking home), alive, active, and within targetMaxGapM
//               (kept until targetReleaseGapM, so there is no flapping at the edge); otherwise the nearest fighting enemy, otherwise NONE (the free camera). A change made by ZoneRuntime (Engage / Retarget)
//               is held back for minHoldS while the old target is still good. A corpse is kept for the death camera, handed over at once to a living fighting enemy, and released when Joan is
//               deathReleaseGapM away or after deathReleaseS.
//   CapGap()    the pose (CameraRig back / height) grows with the gap; in zones the gap it uses is capped at gapCapM (the framing then still fits both fighters, CamFraming / CamReframe are untouched).
//   YawStep()   the lock yaw may turn at most yawMaxDegPerS (a target hand-over is a swing, not a snap); SnapYaw() lets it re-aim while the lock is fully off (invisible).
//   FreePitch / PitchMax / XClamp / YieldStuck   the free camera released from a lock starts at freePitchDeg and never goes above freePitchMaxDeg (a mouse drift to 60 deg = birds eye); the camera x clamp of the
//               short arena (11.5) widens to the zone width; a finisher yield that never released lets go after yieldMaxS.
// Active() is false with zones off (ZoneRuntime.Owns) or enabled=false: every method then returns its input unchanged, so the single duel camera is the same as before.
public static class CamZone
{
    [Serializable]
    public class Row
    {
        public bool enabled = false;
        public float targetMaxGapM = 9f, targetReleaseGapM = 11f, gapCapM = 6f, minHoldS = 0.8f, yawMaxDegPerS = 150f, scanEveryS = 0.15f;
        public bool skipGivingUp = true;
        public float freePitchDeg = 8f, freePitchMaxDeg = 35f, xClamp = 40f;
        public float deathReleaseGapM = 8f, deathReleaseS = 8f, yieldMaxS = 45f;
        public bool log = true;
    }

    static DuelFighter pdf; static Transform pdfFor, accTr, heldTr, deadTr, dfTr; static DuelFighter dfCache;
    static float accAt, deadSince, nextScan, yieldT; static bool snapPending; static int yieldFrame = -9; static DuelFighter[] fighters = new DuelFighter[0];
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { pdf = null; pdfFor = accTr = heldTr = deadTr = dfTr = null; dfCache = null; accAt = deadSince = nextScan = yieldT = 0f; snapPending = false; yieldFrame = -9; fighters = new DuelFighter[0]; }

    public static bool Active(Row z) { return z != null && z.enabled && ZoneRuntime.Owns; }

    // ---- pose / lens hooks (CameraRig) ------------------------------------------------------------------------------
    public static float CapGap(Row z, float gap) { if (!Active(z) || z.gapCapM <= 0f) return gap; return Mathf.Min(gap, z.gapCapM); }
    public static float YawStep(Row z, float step, float dt) { if (!Active(z) || z.yawMaxDegPerS <= 0f) return step; float m = z.yawMaxDegPerS * dt; return Mathf.Clamp(step, -m, m); }
    public static bool SnapYaw(Row z, float lockW) { if (!Active(z)) return false; if (snapPending) { snapPending = false; return lockW < 0.5f; } return lockW < 0.02f; }   // the lock yaw re-aims at once only while the lock is (nearly) off, so nobody sees it
    public static float FreePitch(Row z, float pitch) { return Active(z) ? z.freePitchDeg : pitch; }
    public static float PitchMax(Row z, float max) { return Active(z) ? Mathf.Min(max, z.freePitchMaxDeg) : max; }
    public static float XClamp(Row z, float v) { return Active(z) && z.xClamp > v ? z.xClamp : v; }
    public static bool YieldStuck(Row z, float dtUnscaled)
    {
        if (!Active(z) || z.yieldMaxS <= 0f) return false;
        if (RTFinisher.Busy || Time.frameCount != yieldFrame + 1) yieldT = 0f;
        yieldFrame = Time.frameCount; if (RTFinisher.Busy) return false;
        yieldT += dtUnscaled; if (yieldT < z.yieldMaxS) return false;
        yieldT = 0f; if (z.log) Debug.LogWarning("[CamZone] camera yielded for " + z.yieldMaxS.ToString("F0") + " s with no finisher running: released"); return true;
    }

    // ---- the lock target --------------------------------------------------------------------------------------------
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }
    static DuelFighter Df(Transform t) { if (t == null) return null; if (t != dfTr) { dfTr = t; dfCache = t.GetComponent<DuelFighter>(); } return dfCache; }
    static bool GivingUp(DuelFighter f) { var a = f.GetComponent<ChaseAgent>(); return a != null && (a.GivingUpNow || a.Done); }
    static bool Fighting(Row z, DuelFighter f) { return f != null && !f.Dead && f.isActiveAndEnabled && f.opponent == pdf && !(z.skipGivingUp && GivingUp(f)); }
    static bool Valid(Row z, Transform t, bool held)
    {
        var f = Df(t); if (f == null || !Fighting(z, f)) return false;
        return Flat(pdfFor.position, t.position) <= (held ? z.targetReleaseGapM : z.targetMaxGapM);
    }
    static Transform Best(Row z, Transform prefer)
    {
        float now = Time.unscaledTime;
        if (now >= nextScan) { nextScan = now + Mathf.Max(0.02f, z.scanEveryS); fighters = UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None); }
        Transform best = null; float bd = float.MaxValue;
        if (prefer != null) { var pf = Df(prefer); if (pf != null && Fighting(z, pf) && Flat(pdfFor.position, prefer.position) <= z.targetMaxGapM) return prefer; }
        for (int i = 0; i < fighters.Length; i++)
        {
            var f = fighters[i]; if (f == null || f == pdf || !Fighting(z, f)) continue;
            float d = Flat(pdfFor.position, f.transform.position); if (d <= z.targetMaxGapM && d < bd) { bd = d; best = f.transform; }
        }
        return best;
    }
    static void SetEnemy(Row z, CameraRig rig, Transform t, string why)
    {
        if (rig.enemy == t) return;
        if (z.log) Debug.Log("[CamZone] camera target " + (rig.enemy != null ? rig.enemy.name : "none") + " -> " + (t != null ? t.name : "none (free camera)") + ": " + why);
        if (rig.enemy == null && t != null) snapPending = true;
        rig.enemy = t; rig.enemyHp = t != null ? t.GetComponent<Health>() : null;
    }
    static void Accept(Transform t) { accTr = t; accAt = Time.unscaledTime; heldTr = t; } /* LOCKON v1 */ public static void ForceAccept(Transform t) { accTr = t; accAt = Time.unscaledTime; heldTr = t; deadTr = null; }

    /// <summary>CameraRig.LateUpdate, before the lock test. Never touches the player's own target (hits, HUD): only which enemy the CAMERA frames.</summary>
    public static void Pick(Row z, CameraRig rig, Transform player)
    {
        if (RTFinisher.Busy || rig.Yield || player == null) return;
        float now = Time.unscaledTime;
        if (pdfFor != player) { pdfFor = player; pdf = player.GetComponent<DuelFighter>(); accTr = heldTr = deadTr = null; nextScan = 0f; }
        if (pdf == null) return;
        Transform cur = rig.enemy;
        if (cur != accTr)   // somebody else (ZoneRuntime Engage / Retarget) moved the camera target
        {
            if (cur != null && accTr != null && now - accAt < z.minHoldS && Valid(z, accTr, true) && Valid(z, cur, false)) { SetEnemy(z, rig, accTr, "hold " + z.minHoldS.ToString("F1") + " s before a hand-over"); cur = accTr; }
            else { if (accTr == null && cur != null) snapPending = true; accTr = cur; accAt = now; heldTr = null; }
        }
        if (cur != null)
        {
            var cf = Df(cur);
            if (cf != null && cf.Dead)   // a corpse: a living fighter takes over at once; else the death camera keeps it for a while
            {
                var alt = Best(z, null); if (alt != null) { SetEnemy(z, rig, alt, "target died, another is fighting"); Accept(alt); return; }
                if (deadTr != cur) { deadTr = cur; deadSince = now; }
                if (Flat(player.position, cur.position) > z.deathReleaseGapM || now - deadSince > z.deathReleaseS) { SetEnemy(z, rig, null, "dead target left behind"); Accept(null); }
                return;
            }
            if (cf == null) return;
            if (Valid(z, cur, heldTr == cur)) { heldTr = cur; return; }
        }
        var nb = Best(z, pdf.opponent != null ? pdf.opponent.transform : null);
        SetEnemy(z, rig, nb, cur == null ? "none fighting before" : "not fighting Joan, giving up or out of range"); Accept(nb);
    }
}
