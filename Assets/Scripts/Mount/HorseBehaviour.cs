using System;
using System.Collections.Generic;
using UnityEngine;

// laneHORSEBEHAV v1 (staged DARK, UNPROVEN in Unity). Ambient horses that (1) stroll the MAIN area of their rectangle (not its edges), (2) can be targeted and killed (a few hits, real-time respawn),
// (3) are skittish (the first hit sends them galloping away from the attacker around props and arenas, then they calm down and stroll again) and (4) walk / trot / gallop with hooves planted (stride = speed).
// Data: horse_behaviour.json (Assets\Data + StreamingAssets). enabled=false or no file = nothing here runs and AmbientHorses behaves exactly as before.
// Code shape: a PARTIAL of AmbientHorses (it reads the private Horse / HerdState types). AmbientHorses.cs gets 5 anchored hooks (listed in README): partial, Horse.hb field, File() remap, Step() head, LocalBounds(SMR).
// The horse GLBs have NO skeleton and NO clips (one static mesh node each), so tools\make_rigs.py writes rigged COPIES (9 joints: HB_Body, HB_U_<leg>, HB_L_<leg>, HB_F_<leg> hoof marker) and this file
// drives the joints procedurally: a 2 bone IK in the leg plane puts every hoof on a stance/swing curve in the horse frame, so a planted hoof stays on the ground while the body moves at the real speed.
// Hits: the horses are not DuelFighters (no collider, no sim body). A swing of the player (state Active) is judged here with the SAME reach / arc maths as Duel.Resolve (move.reach x ReachScale + radii + pad, HitArcDeg).
// Nothing in the combat code is edited: Duel.Resolve keeps judging only player.opponent; horses never become player.opponent, so no brain / HUD / finisher / loot / zone-record code ever sees one.
[Serializable] public class HbGait { public string name = ""; public float minMps = 0f, strideM = 1.2f, duty = 0.6f, liftM = 0.12f, bobM = 0f, pitchDeg = 1f, rollDeg = 1f; public float[] offsets = new float[] { 0.25f, 0.75f, 0f, 0.5f }; }   // offsets = phase of FL, FR, HL, HR
[Serializable] public class HbRigMap { public string from = "", to = ""; }
[Serializable] public class HbWeightRow { public string match = ""; public float w = 1f; }
[Serializable] public class HbRoam { public float edgeInsetM = 0f, centerBias = 0f, laneX = 0f, laneBiasM = 0f; }
[Serializable] public class HbLife
{
    public string mode = "hits";   // hits: every connecting swing takes hp / hitsToKill x its weight; damage: the move's own damage x the player's damage scale
    public float limpRate = 6f, hp = 100f, hitsToKill = 4f, heavyWeight = 2f, damageMul = 1f, respawnS = 180f, corpseS = 30f, dieFallS = 0.9f, dieRollDeg = 85f, deathPivotX = 0.35f, respawnMinPlayerM = 25f;
    public HbWeightRow[] weights = new HbWeightRow[0];   // move id contains `match` -> weight w (first match wins); heavy swings use heavyWeight when no row matches
}
[Serializable] public class HbHit { public float halfLengthM = 0.9f, radiusM = 0.45f, reachPadM = 0.45f, assistArcDeg = 25f; public bool whileEngaged = false; }
[Serializable] public class HbSkit
{
    public bool onHit = true; public float fleeMps = 8f, fleeJitter = 0.1f, accelMps2 = 9f, decelMps2 = 7f, turnDegPerS = 240f, startleS = 0.25f, startlePitchDeg = 10f, panicMinS = 3.5f, panicMaxS = 6f, fleeMinM = 16f, calmPauseS = 2.5f, alignDeg = 60f, awayJitterDeg = 25f, spaceM = 3f, spaceMps = 2.2f, mountSinkM = 0.07f;   /* MOUNTWALK v1 */
    public float nearPadM = 6f, lookAheadM = 5f, clearanceM = 1.4f, steerEveryS = 0.1f, fanStepDeg = 20f, fanMaxDeg = 140f, boundsMarginM = 8f, groundCheckStepM = 2.5f;
}
[Serializable] public class HbMove { public float rigScanS = 0.25f, rigRescanS = 1f, walkAccelMps2 = 4f, walkDecelMps2 = 5f, turnStepMpsPerDeg = 0.012f, standMps = 0.05f, settleMps = 0.4f, gaitBlendS = 0.3f, hysteresisMps = 0.4f, reach = 0.985f, bodyDropRateMps = 12f, maxDropM = 0.45f, animFarM = 70f, boundsPadM = 0.6f, resumeGapS = 1.5f, groundSampleM = 0f; }
[Serializable] public class HbTarget { public bool enabled = true, lockCamera = true; public float rangeM = 9f, coneDeg = 80f, keepRangeMul = 1.3f, keepConeMul = 1.4f, retargetMarginM = 1.5f; }
[Serializable] public class HbHud { public bool enabled = true; public float refHeightPx = 1080f, markerPx = 12f, barWidthPx = 90f, barHeightPx = 8f, headUpM = 0.6f; public float[] markerColor = new float[] { 1f, 0.85f, 0.3f, 1f }, barColor = new float[] { 0.8f, 0.15f, 0.1f, 0.95f }; }
[Serializable] public class HbCfg
{
    public bool enabled = false, debugLog = false, logSlip = true;
    public float slipLogEveryS = 3f, debugRangeM = 12f;
    public HbRigMap[] rigMap = new HbRigMap[0]; public HbGait[] gaits = new HbGait[0];
    public HbRoam roam = new HbRoam(); public HbLife life = new HbLife(); public HbHit hit = new HbHit(); public HbSkit skit = new HbSkit(); public HbMove move = new HbMove(); public HbTarget target = new HbTarget(); public HbHud hud = new HbHud();
    public string debugHitKey = "";   // "" = none; the command line flag -horsebehavdebug sets F10 when empty (a virtual light hit on the targeted / nearest horse, for proof captures)
}

public sealed partial class AmbientHorses
{
    const float HbEps = 0.01f;   // divide / epsilon guard
    enum HbS { Stroll, Startle, Flee, Calm, Dying, Corpse, Gone }
    static readonly string[] HbLegs = { "FL", "FR", "HL", "HR" };
    class HbRig
    {
        public Transform body; public Transform[] U = new Transform[4], L = new Transform[4], F = new Transform[4]; public SkinnedMeshRenderer smr; public string tag = "";
        public Vector3[] pivot = new Vector3[4], knee = new Vector3[4], foot = new Vector3[4]; public float[] l1 = new float[4], l2 = new float[4], r1 = new float[4], r2 = new float[4];   // rest, in the horse Body space
    }
    class HbLeg { public bool stance; public Vector3 plantW; public float slip = -1f, floatM; }
    class HbHorse
    {
        public string key = ""; public HbS st = HbS.Stroll; public float hp, hpMax, v, vAnim, yawRate, lastTick, stT, panicT, fleeV, fleeBase, steerAt, calmAt, bodyY, phase, gaitBlend = 1f, deadAt, fleeYaw, nextSlipLog, slipMax, floatMax, hitFlash, dieSide = 1f;
        public Vector3 hitFrom, fleeStart; public int rigCount, hits, seenSwing, gait = -1; public float rigScanAt; public float[] off = new float[4], offT = new float[4]; public float strideC, dutyC, liftC, bobC, pitchC, rollC;
        public HbLeg[] legs = new HbLeg[] { new HbLeg(), new HbLeg(), new HbLeg(), new HbLeg() }; public List<HbRig> rigs = new List<HbRig>(); public Health health; public bool corpseHidden; public string gaitName = "";
    }

    static HbCfg HBC; static int hbFrame = -1, hbSwingSeq; static bool hbSwingPrev, hbSwingLanded; static MoveDef hbSwingMove; static DuelFighter hbSwingBy;
    static readonly Dictionary<string, long> hbKilled = new Dictionary<string, long>(); static readonly Dictionary<string, string> hbRemap = new Dictionary<string, string>();
    static readonly List<AhCircle> hbNear = new List<AhCircle>(); static CameraRig hbRig; static float hbRigAt; static Transform hbLockTr; static HbHorse hbTarget; static Horse hbTargetH; static bool hbDebug;
    static readonly List<KeyValuePair<Horse, HbHorse>> hbAll = new List<KeyValuePair<Horse, HbHorse>>();

    public static HbCfg HbGet()
    {
        if (HBC != null) return HBC; HBC = new HbCfg(); string txt = DataPath.ReadText("horse_behaviour.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<HbCfg>(txt); if (k != null) HBC = k; } catch (Exception e) { Debug.LogWarning("[HorseBehav] horse_behaviour.json unreadable, off: " + e.Message); HBC = new HbCfg(); } }
        foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith("-horsebehavdebug", StringComparison.OrdinalIgnoreCase)) hbDebug = true;
        if (hbDebug && string.IsNullOrEmpty(HBC.debugHitKey)) HBC.debugHitKey = "F10";
        if (HBC.gaits == null) HBC.gaits = new HbGait[0]; if (HBC.rigMap == null) HBC.rigMap = new HbRigMap[0]; if (HBC.life.weights == null) HBC.life.weights = new HbWeightRow[0];
        if (HBC.enabled || hbDebug) Debug.Log("[HorseBehav] boot enabled=" + HBC.enabled + " gaits=" + HBC.gaits.Length + " rigMap=" + HBC.rigMap.Length + " life=" + HBC.life.mode + " hp=" + HBC.life.hp + " hitsToKill=" + HBC.life.hitsToKill + " respawnS=" + HBC.life.respawnS + " debug=" + hbDebug);
        return HBC;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void HbReset() { HBC = null; hbFrame = -1; hbSwingSeq = 0; hbSwingPrev = false; hbTarget = null; hbTargetH = null; hbLockTr = null; hbRig = null; hbAll.Clear(); hbRemap.Clear(); hbDebug = false; }   // hbKilled survives a zone / scene change on purpose (session only, never saved)

    // AmbientHorses.File(): the rigged copy of a breed glb when horse_behaviour.json lists one and the file exists; the original path otherwise.
    static AhBreed HB_RigOf(AhBreed b)
    {
        var C = HbGet(); if (!C.enabled || b == null || string.IsNullOrEmpty(b.glb) || C.rigMap.Length == 0) return b; string to;
        if (!hbRemap.TryGetValue(b.glb, out to))
        {
            to = null; foreach (var m in C.rigMap) if (m != null && m.from == b.glb && !string.IsNullOrEmpty(m.to)) { try { if (System.IO.File.Exists(System.IO.Path.Combine(Application.streamingAssetsPath, m.to))) to = m.to; } catch (Exception) { } break; }
            hbRemap[b.glb] = to; if (to != null && (C.debugLog || hbDebug)) Debug.Log("[HorseBehav] rig remap " + b.glb + " -> " + to);
        }
        return to == null ? b : new AhBreed { id = b.id, glb = to, heightM = b.heightM, glbHeightM = b.glbHeightM, midGlb = b.midGlb };
    }
    // AmbientHorses.LocalBounds(): a skinned mesh has no MeshFilter.
    static bool HB_MeshOf(Renderer r, out Mesh m, out Transform t)
    {
        m = null; t = r.transform; var mf = r.GetComponent<MeshFilter>(); if (mf != null) { m = mf.sharedMesh; return m != null; }
        var sm = r as SkinnedMeshRenderer; if (sm != null) { m = sm.sharedMesh; return m != null; }
        return false;
    }

    // ------------------------------------------------------------------------------------------------ the per-horse step (replaces AmbientHorses.Step for a managed horse)
    bool HB_Step(Horse h, AhHerd herd, HerdState s, float dt, DuelFighter p)
    {
        var C = HbGet(); if (!C.enabled) return false;
        if (h.hb == null) { h.hb = HbMake(h, C); hbAll.Add(new KeyValuePair<Horse, HbHorse>(h, h.hb)); }
        var b = h.hb; float now = Time.unscaledTime;
        if (Time.frameCount != hbFrame) { hbFrame = Time.frameCount; HbFrame(p, C, now); }
        if (now - b.lastTick > C.move.resumeGapS) HbResume(h, b, C);
        b.lastTick = now; HbFindRigs(h, b, C);
        if (b.st == HbS.Gone) { HbGoneStep(h, b, herd, s, C, p); return true; }
        if (!h.placed) { float gy; if (Ground(h.pos.x, h.pos.z, p != null ? p.transform.position.y : K.fallbackY, out gy)) { h.pos.y = gy; h.placed = true; } else return true; }
        if (b.st == HbS.Dying || b.st == HbS.Corpse) { HbDieStep(h, b, C, dt); HbApply(h, b, p, dt, 0f); return true; }
        float desired = 0f, turn = K.gait.turnDegPerS, wantYaw = h.yaw; bool moveOk = true;
        switch (b.st)
        {
            case HbS.Stroll: HbStroll(h, b, herd, s, C, p, ref desired, ref wantYaw); break;
            case HbS.Startle: b.stT += dt; wantYaw = b.fleeYaw; turn = C.skit.turnDegPerS; moveOk = false; if (b.stT >= C.skit.startleS) { b.st = HbS.Flee; b.stT = 0f; Debug.Log("[HorseBehav] flee " + h.go.name + " yaw=" + b.fleeYaw.ToString("F0") + " speed=" + b.fleeV.ToString("F1") + " panicMin=" + C.skit.panicMinS.ToString("F1") + " s from " + b.hitFrom.ToString("F1")); } break;
            case HbS.Flee: HbFlee(h, b, herd, s, C, dt, ref desired, ref wantYaw); turn = C.skit.turnDegPerS; break;
            case HbS.Calm: if (b.v <= C.move.standMps) { b.calmAt += dt; if (b.calmAt >= C.skit.calmPauseS) { b.st = HbS.Stroll; h.walking = false; h.wait = 0f; Debug.Log("[HorseBehav] calm " + h.go.name + " strolls again, " + (Vector3.Distance(h.pos, b.fleeStart)).ToString("F1") + " m from where it was hit"); } } break;
        }
        float before = h.yaw; h.yaw = Mathf.MoveTowardsAngle(h.yaw, wantYaw, turn * dt); b.yawRate = Mathf.DeltaAngle(before, h.yaw) / Mathf.Max(0.0001f, dt);
        float accel = b.st == HbS.Flee || b.st == HbS.Startle ? C.skit.accelMps2 : C.move.walkAccelMps2, decel = b.st == HbS.Flee || b.st == HbS.Calm ? C.skit.decelMps2 : C.move.walkDecelMps2;
        if (!moveOk) desired = 0f;
        b.v = Mathf.MoveTowards(b.v, desired, (desired > b.v ? accel : decel) * dt);
        if (b.v > 0f)
        {
            var np = h.pos + Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward * (b.v * dt); float gy;
            if (Ground(np.x, np.z, h.pos.y, out gy)) h.pos = new Vector3(np.x, gy, np.z); else { b.v = 0f; if (b.st == HbS.Stroll) { h.walking = false; h.wait = Pause(h); } else if (b.st == HbS.Flee) { b.st = HbS.Calm; b.calmAt = 0f; } }
        }
        float startle = b.st == HbS.Startle ? Mathf.Sin(Mathf.Clamp01(b.stT / Mathf.Max(HbEps, C.skit.startleS)) * Mathf.PI) * C.skit.startlePitchDeg : 0f;
        HbApply(h, b, p, dt, startle); return true;
    }

    // stroll = the AmbientHorses walk (pick a point, turn on the spot, walk, stand) with targets kept off the rectangle edges
    void HbStroll(Horse h, HbHorse b, AhHerd herd, HerdState s, HbCfg C, DuelFighter p, ref float desired, ref float wantYaw)
    {
        var G = K.gait; float dt = Time.deltaTime;
        if (HbSpace(h, p, C, ref desired, ref wantYaw)) return;   /* MOUNTWALK v1: personal space */
        if (h.walking)
        {
            var d = h.target - h.pos; d.y = 0f; float dist = d.magnitude;
            if (dist <= G.arriveM) { h.walking = false; h.wait = Pause(h); }
            else { float want = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; wantYaw = want; float align = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(h.yaw, want)) / Mathf.Max(1f, G.alignDeg)); desired = G.walkMps * align; }
        }
        else { h.wait -= dt; if (h.wait <= 0f) { h.target = HbPick(herd, h, s, p, C); h.walking = true; } }
    }
    Vector3 HbPick(AhHerd h, Horse self, HerdState s, DuelFighter p, HbCfg C)
    {
        var a = h.area; float ins = Mathf.Min(C.roam.edgeInsetM, 0.5f * Mathf.Min(a.xMax - a.xMin, a.zMax - a.zMin) - HbEps); ins = Mathf.Max(0f, ins); float cx = Mathf.Clamp(C.roam.laneX, a.xMin + ins, a.xMax - ins);
        Vector3 best = new Vector3((a.xMin + a.xMax) * 0.5f, 0f, (a.zMin + a.zMax) * 0.5f);
        for (int t = 0; t < Mathf.Max(1, K.maxTargetTries); t++)
        {
            float x = Mathf.Lerp(a.xMin + ins, a.xMax - ins, (float)self.rnd.NextDouble()), z = Mathf.Lerp(a.zMin + ins, a.zMax - ins, (float)self.rnd.NextDouble()); bool ok = true;
            x = Mathf.Lerp(x, cx, Mathf.Clamp01(C.roam.centerBias));
            if (C.roam.laneBiasM > 0f && Mathf.Abs(x - C.roam.laneX) > C.roam.laneBiasM) x = Mathf.Lerp(x, C.roam.laneX, 0.5f);
            if (h.avoid != null) foreach (var c in h.avoid) if (c != null && (x - c.x) * (x - c.x) + (z - c.z) * (z - c.z) < c.r * c.r) { ok = false; break; }
            if (ok && p != null) { var q = p.transform.position; if ((x - q.x) * (x - q.x) + (z - q.z) * (z - q.z) < K.avoidPlayerM * K.avoidPlayerM) ok = false; }
            if (ok) foreach (var o in s.horses) if (o != self && o.go != null && (x - o.pos.x) * (x - o.pos.x) + (z - o.pos.z) * (z - o.pos.z) < K.minSpacingM * K.minSpacingM) { ok = false; break; }
            best = new Vector3(x, 0f, z); if (ok) return best;
        }
        return best;
    }

    // ------------------------------------------------------------------------------------------------ flee
    void HbFlee(Horse h, HbHorse b, AhHerd herd, HerdState s, HbCfg C, float dt, ref float desired, ref float wantYaw)
    {
        var k = C.skit; b.panicT += dt; b.steerAt -= dt;
        float run = Vector3.Distance(h.pos, b.fleeStart);
        if (b.panicT >= k.panicMaxS || (b.panicT >= k.panicMinS && run >= k.fleeMinM)) { b.st = HbS.Calm; b.calmAt = 0f; Debug.Log("[HorseBehav] panic over " + h.go.name + " after " + b.panicT.ToString("F1") + " s, " + run.ToString("F1") + " m"); return; }
        if (b.steerAt <= 0f) { b.steerAt = k.steerEveryS; HbSteer(h, b, herd, C); }
        wantYaw = b.fleeYaw; float align = Mathf.Clamp01(1f - Mathf.Abs(Mathf.DeltaAngle(h.yaw, wantYaw)) / Mathf.Max(1f, k.alignDeg)); desired = b.fleeV * align;
    }
    // pick a heading near the away-from-the-hit direction that is clear of avoid circles (props, arenas, spots of every herd), stays inside the herd rectangle + boundsMarginM and has ground ahead
    void HbSteer(Horse h, HbHorse b, AhHerd herd, HbCfg C)
    {
        var k = C.skit; HbNearCircles(h.pos, k.lookAheadM + k.clearanceM + k.nearPadM); var a = herd.area; float baseYaw = b.fleeBase, step = Mathf.Max(1f, k.fanStepDeg), best = b.fleeYaw; bool found = false;
        int n = Mathf.RoundToInt(k.fanMaxDeg / step);
        for (int i = 0; i <= n * 2 && !found; i++)
        {
            float off = ((i + 1) / 2) * step * ((i & 1) == 1 ? 1f : -1f); if (i == 0) off = 0f; float yaw = baseYaw + off; var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward; var end = h.pos + dir * k.lookAheadM;
            if (end.x < a.xMin - k.boundsMarginM || end.x > a.xMax + k.boundsMarginM || end.z < a.zMin - k.boundsMarginM || end.z > a.zMax + k.boundsMarginM) continue;
            bool clear = true; foreach (var c in hbNear) { if (HbSegCircle(h.pos, end, c.x, c.z, c.r + k.clearanceM)) { clear = false; break; } }
            if (!clear) continue; float gy; bool ground = true; int steps = Mathf.Max(1, Mathf.CeilToInt(k.lookAheadM / Mathf.Max(0.5f, k.groundCheckStepM)));
            for (int q = 1; q <= steps && ground; q++) { var pt = h.pos + dir * (k.lookAheadM * q / steps); if (!Ground(pt.x, pt.z, h.pos.y, out gy)) ground = false; }
            if (!ground) continue; best = yaw; found = true;
        }
        if (!found) { float rb; if (HbSteerRelax(h, b, herd, C, out rb)) { b.fleeYaw = rb; if (C.debugLog || hbDebug) Debug.Log("[HorseBehav] flee " + h.go.name + " no clear heading, best ground heading " + rb.ToString("F0")); return; } b.st = HbS.Calm; b.calmAt = 0f; Debug.Log("[HorseBehav] flee " + h.go.name + " boxed in (no ground ahead on any heading), calming down"); return; }   /* MOUNTWALK v1 */
        if (Mathf.Abs(Mathf.DeltaAngle(best, b.fleeYaw)) > HbEps && (C.debugLog || hbDebug)) Debug.Log("[HorseBehav] steer " + h.go.name + " yaw " + b.fleeYaw.ToString("F0") + " -> " + best.ToString("F0") + " (circles near " + hbNear.Count + ")");
        b.fleeYaw = best;
    }
    static void HbNearCircles(Vector3 p, float r)
    {
        hbNear.Clear(); if (K == null) return;
        foreach (var hd in K.herds) { if (hd == null || hd.avoid == null) continue; foreach (var c in hd.avoid) { if (c == null) continue; float dx = c.x - p.x, dz = c.z - p.z, rr = r + c.r; if (dx * dx + dz * dz <= rr * rr) hbNear.Add(c); } }
    }
    static bool HbSegCircle(Vector3 a, Vector3 e, float cx, float cz, float r)
    {
        float dx = e.x - a.x, dz = e.z - a.z, l2 = dx * dx + dz * dz; float t = l2 > 0f ? Mathf.Clamp01(((cx - a.x) * dx + (cz - a.z) * dz) / l2) : 0f; float px = a.x + dx * t - cx, pz = a.z + dz * t - cz; return px * px + pz * pz < r * r;
    }

    // ------------------------------------------------------------------------------------------------ create / resume / hits / death
    HbHorse HbMake(Horse h, HbCfg C)
    {
        var b = new HbHorse { key = h.go.name, hpMax = C.life.hp, lastTick = Time.unscaledTime }; b.hp = b.hpMax;
        b.health = h.go.GetComponent<Health>(); if (b.health == null) b.health = h.go.AddComponent<Health>(); b.health.max = b.hpMax; b.health.current = b.hpMax;
        long until; if (hbKilled.TryGetValue(b.key, out until) && ZoneCore.NowMs() < until) { b.st = HbS.Gone; b.deadAt = Time.unscaledTime; if (C.debugLog || hbDebug) Debug.Log("[HorseBehav] " + b.key + " is still dead (respawn in " + ((until - ZoneCore.NowMs()) / 1000L) + " s)"); }
        else hbKilled.Remove(b.key);
        return b;
    }
    void HbResume(Horse h, HbHorse b, HbCfg C)
    {
        if (b.st == HbS.Startle || b.st == HbS.Flee || b.st == HbS.Calm) { b.st = HbS.Stroll; h.walking = false; h.wait = 0f; }
        b.v = 0f; b.vAnim = 0f; b.yawRate = 0f; foreach (var l in b.legs) l.stance = false;
        if (b.st == HbS.Dying) { b.st = HbS.Corpse; }
    }
    public static bool ProofHit;   // HORSEPROOF v1 (unity-build): one virtual light hit on the targeted / nearest horse, set by HorseProof.cs (-horseproof) for the scripted proof; false in normal play
    // one pass per frame: the swing watch + the horse that takes the hit, the soft target, the debug key
    void HbFrame(DuelFighter p, HbCfg C, float now)
    {
        if (p != null && !p.Dead)
        {
            bool act = p.state == FState.Active && p.CurrentMove != null;
            if (act && !hbSwingPrev) { hbSwingSeq++; hbSwingLanded = false; hbSwingMove = p.CurrentMove; hbSwingBy = p; }
            hbSwingPrev = act;
            if (act && !hbSwingLanded && (C.hit.whileEngaged || p.opponent == null || p.opponent.Dead)) { Horse bh = null; HbHorse bb = null; float bd = float.MaxValue; foreach (var kv in hbAll) { float d; if (HbCanHit(kv.Key, kv.Value, p, hbSwingMove, C, out d) && d < bd) { bd = d; bh = kv.Key; bb = kv.Value; } } if (bh != null) { hbSwingLanded = true; HbHit_(bh, bb, p, hbSwingMove, 1f, C, "swing"); } }
        }
        else hbSwingPrev = false;
        HbTargetPass(p, C, now);
        if (ProofHit && p != null) { ProofHit = false; HbDebugHit(p, C); }   // HORSEPROOF v1
        if (!string.IsNullOrEmpty(C.debugHitKey) && p != null) { bool down = false; try { KeyCode kc; if (Enum.TryParse(C.debugHitKey, true, out kc)) down = Input.GetKeyDown(kc); } catch (Exception) { } if (down) HbDebugHit(p, C); }
        for (int i = hbAll.Count - 1; i >= 0; i--) if (hbAll[i].Key == null || hbAll[i].Key.go == null) hbAll.RemoveAt(i);
    }
    static Vector3 HbCapsule(Horse h, HbHorse b, Vector3 from, HbCfg C, out Vector3 axis)
    {
        axis = Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward; var a = h.pos - axis * C.hit.halfLengthM; var e = h.pos + axis * C.hit.halfLengthM; var ab = e - a; float l2 = ab.sqrMagnitude; float t = l2 > 0f ? Mathf.Clamp01(Vector3.Dot(from - a, ab) / l2) : 0f; return a + ab * t;
    }
    bool HbCanHit(Horse h, HbHorse b, DuelFighter p, MoveDef m, HbCfg C, out float d)
    {
        d = 0f; if (b == null || h.go == null || !h.go.activeInHierarchy || b.st == HbS.Dying || b.st == HbS.Corpse || b.st == HbS.Gone || !h.placed) return false;
        Vector3 ax; var c = HbCapsule(h, b, p.transform.position, C, out ax); var to = c - p.transform.position; to.y = 0f; d = to.magnitude;
        if (d > m.reach * p.ReachScale + p.radius + C.hit.radiusM + C.hit.reachPadM) return false;
        var fwd = p.transform.forward; fwd.y = 0f; return Vector3.Angle(fwd, to) <= p.HitArcDeg + C.hit.assistArcDeg;
    }
    float HbWeight(MoveDef m, DuelFighter p, HbCfg C)
    {
        if (m == null) return 1f; foreach (var r in C.life.weights) if (r != null && r.match.Length > 0 && m.id != null && m.id.Contains(r.match)) return r.w;
        bool heavy = p != null && (m.charges || m.damage >= p.HeavyDamage); return heavy ? C.life.heavyWeight : 1f;
    }
    void HbHit_(Horse h, HbHorse b, DuelFighter p, MoveDef m, float weightMul, HbCfg C, string how)
    {
        float w = HbWeight(m, p, C) * weightMul, a0 = b.hp, dmg = C.life.mode == "damage" && m != null && p != null ? m.damage * p.DamageScaleNow * C.life.damageMul * weightMul : b.hpMax / Mathf.Max(HbEps, C.life.hitsToKill) * w * C.life.damageMul;
        b.hp = Mathf.Max(0f, b.hp - dmg); b.hits++; if (b.health != null) b.health.current = b.hp; b.hitFrom = p != null ? p.transform.position : h.pos - Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward; b.hitFlash = Time.unscaledTime;
        Debug.Log("[HorseBehav] hit " + h.go.name + " by " + how + " move=" + (m != null ? m.id : "-") + " w=" + w.ToString("F1") + " hp " + a0.ToString("F0") + "->" + b.hp.ToString("F0") + " hits=" + b.hits + " from " + b.hitFrom.ToString("F1") + " state=" + b.st);
        if (b.hp <= 0f) { HbDie(h, b, C); return; }
        if (C.skit.onHit) HbSpook(h, b, C);
    }
    void HbSpook(Horse h, HbHorse b, HbCfg C)
    {
        var k = C.skit; var away = h.pos - b.hitFrom; away.y = 0f; if (away.sqrMagnitude < HbEps) away = Quaternion.Euler(0f, h.yaw, 0f) * Vector3.forward;
        float jit = ((float)h.rnd.NextDouble() * 2f - 1f) * k.awayJitterDeg, yaw = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg + jit;
        bool fresh = b.st != HbS.Flee && b.st != HbS.Startle; b.fleeBase = yaw; b.fleeYaw = yaw; b.fleeV = k.fleeMps * (1f + ((float)h.rnd.NextDouble() * 2f - 1f) * k.fleeJitter); b.panicT = 0f; b.steerAt = 0f; b.stT = 0f;
        if (fresh) { b.fleeStart = h.pos; b.st = k.startleS > 0f ? HbS.Startle : HbS.Flee; } h.walking = false;
    }
    void HbDie(Horse h, HbHorse b, HbCfg C)
    {
        b.st = HbS.Dying; b.stT = 0f; b.v = 0f; b.deadAt = Time.unscaledTime; b.dieSide = h.rnd.NextDouble() < 0.5 ? -1f : 1f; long until = ZoneCore.NowMs() + (long)(C.life.respawnS * 1000f); hbKilled[b.key] = until;
        if (hbTargetH == h) { hbTarget = null; hbTargetH = null; }
        Debug.Log("[HorseBehav] die " + h.go.name + " after " + b.hits + " hits, respawn in " + C.life.respawnS.ToString("F0") + " s (real time, session only: not saved)");
    }
    void HbDieStep(Horse h, HbHorse b, HbCfg C, float dt)
    {
        b.stT += dt; float f = Mathf.Clamp01(b.stT / Mathf.Max(HbEps, C.life.dieFallS)); f *= f;
        if (b.st == HbS.Dying && b.stT >= C.life.dieFallS) { b.st = HbS.Corpse; b.stT = 0f; }
        else if (b.st == HbS.Corpse && b.stT >= C.life.corpseS) { b.st = HbS.Gone; HbBodyActive(h, false); Debug.Log("[HorseBehav] corpse of " + h.go.name + " removed, respawn at " + (hbKilled.ContainsKey(b.key) ? ((hbKilled[b.key] - ZoneCore.NowMs()) / 1000L) + " s" : "?")); }
        b.hitFlash = 0f;
    }
    void HbGoneStep(Horse h, HbHorse b, AhHerd herd, HerdState s, HbCfg C, DuelFighter p)
    {
        HbBodyActive(h, false); long until; if (!hbKilled.TryGetValue(b.key, out until)) until = 0L; if (ZoneCore.NowMs() < until) return;
        float now = Time.unscaledTime; if (now < b.calmAt) return; b.calmAt = now + 1f;   // retry once a second while the player is near the spot
        var np = HbPick(herd, h, s, p, C); var pp = p != null ? p.transform.position : Vector3.zero;
        if (p != null && C.life.respawnMinPlayerM > 0f && Vector3.Distance(new Vector3(pp.x, 0f, pp.z), new Vector3(np.x, 0f, np.z)) < C.life.respawnMinPlayerM) return;   // never pop in front of the player
        h.pos = np; hbKilled.Remove(b.key); b.st = HbS.Stroll; b.hp = b.hpMax; b.hits = 0; b.v = 0f; b.stT = 0f; b.calmAt = 0f; if (b.health != null) b.health.current = b.hpMax; h.placed = false; h.walking = false; h.wait = Pause(h); h.target = h.pos; HbBodyActive(h, true);
        Debug.Log("[HorseBehav] respawn " + h.go.name + " at " + h.pos.ToString("F1") + " hp " + b.hp.ToString("F0"));
    }
    static void HbBodyActive(Horse h, bool on) { if (h.body != null && h.body.gameObject.activeSelf != on) h.body.gameObject.SetActive(on); }
    void HbDebugHit(DuelFighter p, HbCfg C)
    {
        Horse bh = null; HbHorse bb = null; if (hbTargetH != null && hbTarget != null) { bh = hbTargetH; bb = hbTarget; }
        else { float bd = C.debugRangeM; foreach (var kv in hbAll) { var b = kv.Value; if (kv.Key.go == null || !kv.Key.go.activeInHierarchy || b.st >= HbS.Dying) continue; float d = Vector3.Distance(p.transform.position, kv.Key.pos); if (d < bd) { bd = d; bh = kv.Key; bb = b; } } }
        if (bh != null) HbHit_(bh, bb, p, null, 1f, C, "debugkey " + C.debugHitKey); else Debug.Log("[HorseBehav] debug hit: no horse within range");
    }

    // ------------------------------------------------------------------------------------------------ soft target + camera lock + marker
    void HbTargetPass(DuelFighter p, HbCfg C, float now)
    {
        var T = C.target; if (!T.enabled) return;
        bool ok = p != null && !p.Dead && (p.opponent == null || p.opponent.Dead) && !RTFinisher.Busy;
        Horse bh = null; HbHorse bb = null; float bd = float.MaxValue;
        if (ok)
        {
            var cam = Camera.main; var fwd = cam != null ? cam.transform.forward : p.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.0001f) fwd = p.transform.forward; fwd.Normalize();
            foreach (var kv in hbAll)
            {
                var h = kv.Key; var b = kv.Value; if (h.go == null || !h.go.activeInHierarchy || !h.placed || b.st >= HbS.Dying) continue;
                var to = h.pos - p.transform.position; to.y = 0f; float d = to.magnitude, lim = T.rangeM * (h == hbTargetH ? T.keepRangeMul : 1f), cone = T.coneDeg * (h == hbTargetH ? T.keepConeMul : 1f);
                if (d > lim || (d > 0.5f && Vector3.Angle(fwd, to) > cone)) continue;
                if (d < bd) { bd = d; bh = h; bb = b; }
            }
            if (hbTargetH != null && bh != hbTargetH && hbTarget != null && hbTargetH.go != null && hbTarget.st < HbS.Dying) { float cd = Vector3.Distance(p.transform.position, hbTargetH.pos); if (bh != null && bd + T.retargetMarginM > cd && cd <= T.rangeM * T.keepRangeMul) { bh = hbTargetH; bb = hbTarget; } }
        }
        if (bh != hbTargetH) { if (bh != null) Debug.Log("[HorseBehav] target " + bh.go.name + " dist " + bd.ToString("F1") + " m"); else if (hbTargetH != null && hbTargetH.go != null) Debug.Log("[HorseBehav] target released"); hbTargetH = bh; hbTarget = bb; }
        if (!T.lockCamera) return;
        if (now >= hbRigAt || hbRig == null) { hbRigAt = now + 2f; hbRig = FindFirstObjectByType<CameraRig>(); }
        var rig = hbRig; if (rig == null) return;
        if (hbTargetH != null && hbTarget != null) { if (rig.enemy == null || rig.enemy == hbLockTr) { var tr = hbTargetH.go.transform; if (rig.enemy != tr) { rig.enemy = tr; rig.enemyHp = hbTarget.health; if (hbLockTr == null || hbLockTr != tr) Debug.Log("[HorseBehav] camera lock on " + tr.name); } hbLockTr = tr; } else hbLockTr = null; }
        else if (hbLockTr != null) { if (rig.enemy == hbLockTr) { rig.enemy = null; rig.enemyHp = null; Debug.Log("[HorseBehav] camera lock released"); } hbLockTr = null; }
    }
    void OnGUI()
    {
        var C = HBC; if (C == null || !C.enabled || !C.hud.enabled || hbTargetH == null || hbTarget == null || hbTargetH.go == null) return;
        var cam = Camera.main; if (cam == null) return; var H = hbTargetH; var wp = H.pos + Vector3.up * (HbHeight(H) + C.hud.headUpM); var sp = cam.WorldToScreenPoint(wp); if (sp.z <= 0f) return;
        float sc = Screen.height / C.hud.refHeightPx, x = sp.x, y = Screen.height - sp.y, m = C.hud.markerPx * sc, bw = C.hud.barWidthPx * sc, bh = C.hud.barHeightPx * sc; var mc = C.hud.markerColor; var bc = C.hud.barColor; var old = GUI.color;
        GUI.color = new Color(mc[0], mc[1], mc[2], mc[3]); GUI.DrawTexture(new Rect(x - m * 0.5f, y - m * 2f, m, m), Texture2D.whiteTexture);
        var bg = Color.black; bg.a = bc[3]; GUI.color = bg; GUI.DrawTexture(new Rect(x - bw * 0.5f - sc, y - m * 2f + m + sc, bw + 2f * sc, bh + 2f * sc), Texture2D.whiteTexture);
        GUI.color = new Color(bc[0], bc[1], bc[2], bc[3]); GUI.DrawTexture(new Rect(x - bw * 0.5f, y - m * 2f + m + 2f * sc, bw * Mathf.Clamp01(hbTarget.hp / Mathf.Max(HbEps, hbTarget.hpMax)), bh), Texture2D.whiteTexture); GUI.color = old;
    }
    static float HbHeight(Horse h) { return h.body != null ? 2f * h.body.localScale.y : 2f; }

    // ------------------------------------------------------------------------------------------------ rigs + gait + feet
    void HbFindRigs(Horse h, HbHorse b, HbCfg C)
    {
        if (h.body == null) return; float now = Time.unscaledTime; b.rigs.RemoveAll(r => r.body == null);
        if (now < b.rigScanAt) return; b.rigScanAt = now + (b.rigs.Count == 0 ? C.move.rigScanS : C.move.rigRescanS); int cnt = CountDesc(h.body); if (cnt == b.rigCount) return; b.rigCount = cnt;
        var have = new HashSet<Transform>(); foreach (var r in b.rigs) if (r.body != null) have.Add(r.body);
        foreach (var t in h.body.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "HB_Body" || have.Contains(t)) continue; var rg = new HbRig { body = t }; bool ok = true;
            for (int i = 0; i < 4; i++) { rg.U[i] = HbFind(t.parent, "HB_U_" + HbLegs[i]); rg.L[i] = HbFind(t.parent, "HB_L_" + HbLegs[i]); rg.F[i] = HbFind(t.parent, "HB_F_" + HbLegs[i]); if (rg.U[i] == null || rg.L[i] == null || rg.F[i] == null) ok = false; }
            if (!ok) { Debug.LogWarning("[HorseBehav] " + h.go.name + ": rig found but a leg bone is missing, left static"); continue; }
            for (int i = 0; i < 4; i++)
            {
                rg.pivot[i] = h.body.InverseTransformPoint(rg.U[i].position); rg.knee[i] = h.body.InverseTransformPoint(rg.L[i].position); rg.foot[i] = h.body.InverseTransformPoint(rg.F[i].position);
                var u = rg.knee[i] - rg.pivot[i]; var l = rg.foot[i] - rg.knee[i]; rg.l1[i] = new Vector2(u.z, u.y).magnitude; rg.l2[i] = new Vector2(l.z, l.y).magnitude; rg.r1[i] = Mathf.Atan2(u.z, -u.y); rg.r2[i] = Mathf.Atan2(l.z, -l.y);
            }
            var sceneRoot = t.parent != null && t.parent.parent != null ? t.parent.parent : h.body; rg.smr = sceneRoot.GetComponentInChildren<SkinnedMeshRenderer>(true);   // the LOD node that holds this rig (MidLod / FullLod / Body)
            if (rg.smr != null) { var bb = rg.smr.localBounds; bb.Expand(C.move.boundsPadM); rg.smr.localBounds = bb; rg.smr.updateWhenOffscreen = false; }
            rg.tag = t.parent != null && t.parent.parent != null ? t.parent.parent.name : "rig"; b.rigs.Add(rg);
            Debug.Log("[HorseBehav] rig " + h.go.name + " " + rg.tag + " ok legs=4 L1/L2 FL=" + rg.l1[0].ToString("F2") + "/" + rg.l2[0].ToString("F2") + " HL=" + rg.l1[2].ToString("F2") + "/" + rg.l2[2].ToString("F2") + " smr=" + (rg.smr != null));
        }
    }
    static int CountDesc(Transform t) { int n = 0; foreach (Transform c in t) n += 1 + CountDesc(c); return n; }
    static Transform HbFind(Transform root, string name) { if (root == null) return null; foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t; return null; }

    static int HbPickGait(HbCfg C, float v, int cur)
    {
        int g = 0;
        for (int i = 0; i < C.gaits.Length; i++) { float need = C.gaits[i].minMps; if (cur >= 0) { if (i > cur) need += C.move.hysteresisMps; else if (i == cur) need -= C.move.hysteresisMps; } if (v >= need) g = i; }
        return g;
    }
    static float HbCirc(float a, float b, float t) { float d = Mathf.Repeat(b - a + 0.5f, 1f) - 0.5f; return a + d * t; }

    // final transform + rig pose for this frame
    void HbApply(Horse h, HbHorse b, DuelFighter p, float dt, float startlePitch)
    {
        var C = HBC; float bodyY = 0f, pitch = startlePitch, roll = 0f; Vector3 bodyPos = Vector3.zero;
        if (b.st == HbS.Dying || b.st == HbS.Corpse)
        {
            float f = b.st == HbS.Corpse ? 1f : Mathf.Clamp01(b.stT / Mathf.Max(HbEps, C.life.dieFallS)); f *= f; float th = f * C.life.dieRollDeg * -b.dieSide; var R = Quaternion.Euler(0f, 0f, th); var pv = new Vector3(b.dieSide * C.life.deathPivotX, 0f, 0f);
            bodyPos = pv - R * pv; h.go.transform.SetPositionAndRotation(h.pos, Quaternion.Euler(0f, h.yaw, 0f)); h.body.localPosition = bodyPos; h.body.localRotation = R;
            foreach (var rg in b.rigs) for (int i = 0; i < 4; i++) { rg.U[i].localRotation = Quaternion.Slerp(rg.U[i].localRotation, Quaternion.identity, Mathf.Clamp01(dt * C.life.limpRate)); rg.L[i].localRotation = Quaternion.Slerp(rg.L[i].localRotation, Quaternion.identity, Mathf.Clamp01(dt * C.life.limpRate)); }
            return;
        }
        if (dt > 0f && b.rigs.Count > 0)
        {
            var cam = Camera.main; bool far = cam != null && Vector3.Distance(cam.transform.position, h.pos) > C.move.animFarM;
            float veq = Mathf.Max(b.v, Mathf.Abs(b.yawRate) * C.move.turnStepMpsPerDeg); b.vAnim = veq;
            int gi = HbPickGait(C, veq, b.gait); if (C.gaits.Length > 0)
            {
                if (gi != b.gait) { if (b.gait >= 0 && (C.debugLog || hbDebug)) Debug.Log("[HorseBehav] gait " + h.go.name + " " + C.gaits[b.gait].name + " -> " + C.gaits[gi].name + " v=" + veq.ToString("F1")); if (b.gait < 0) { var g0 = C.gaits[gi]; b.strideC = g0.strideM; b.dutyC = g0.duty; b.liftC = g0.liftM; b.bobC = g0.bobM; b.pitchC = g0.pitchDeg; b.rollC = g0.rollDeg; for (int i = 0; i < 4; i++) b.off[i] = g0.offsets[i]; } b.gait = gi; b.gaitName = C.gaits[gi].name; }
                var g = C.gaits[gi]; float k = Mathf.Clamp01(dt / Mathf.Max(HbEps, C.move.gaitBlendS));
                b.strideC = Mathf.Lerp(b.strideC, g.strideM, k); b.dutyC = Mathf.Lerp(b.dutyC, g.duty, k); b.liftC = Mathf.Lerp(b.liftC, g.liftM, k); b.bobC = Mathf.Lerp(b.bobC, g.bobM, k); b.pitchC = Mathf.Lerp(b.pitchC, g.pitchDeg, k); b.rollC = Mathf.Lerp(b.rollC, g.rollDeg, k);
                for (int i = 0; i < 4; i++) b.off[i] = HbCirc(b.off[i], g.offsets[i], k);
            }
            float move = Mathf.Clamp01(veq / Mathf.Max(HbEps, C.move.settleMps)); b.phase = Mathf.Repeat(b.phase + veq / Mathf.Max(HbEps, b.strideC) * dt, 1f);
            var rig0 = b.rigs[b.rigs.Count - 1]; var tgt = new Vector3[4]; float sink = K.sinkM, S = b.dutyC * b.strideC * move, sc = h.body.localScale.y; float byMax = 0f;
            for (int i = 0; i < 4; i++)
            {
                float ph = Mathf.Repeat(b.phase + b.off[i], 1f); var slot = rig0.foot[i] * sc; float z, y;
                if (ph < b.dutyC) { float u = ph / Mathf.Max(HbEps, b.dutyC); z = slot.z + S * 0.5f - S * u; y = 0f; b.legs[i].stance = true; }
                else { float w = (ph - b.dutyC) / Mathf.Max(HbEps, 1f - b.dutyC), e = w * w * (3f - 2f * w); z = slot.z - S * 0.5f + S * e; y = b.liftC * move * Mathf.Sin(w * Mathf.PI); b.legs[i].stance = false; }
                tgt[i] = new Vector3(slot.x, sink + y, z);
                float lr = (rig0.l1[i] + rig0.l2[i]) * sc * C.move.reach, dz = z - rig0.pivot[i].z * sc; if (lr * lr > dz * dz) { float cap = tgt[i].y - rig0.pivot[i].y * sc + Mathf.Sqrt(lr * lr - dz * dz); if (cap < byMax) byMax = cap; }
            }
            byMax = Mathf.Max(byMax, -C.move.maxDropM); b.bodyY = Mathf.MoveTowards(b.bodyY, byMax, C.move.bodyDropRateMps * dt);
            bodyY = b.bodyY + Mathf.Abs(Mathf.Sin(b.phase * Mathf.PI * 2f)) * b.bobC * move; pitch += Mathf.Sin(b.phase * Mathf.PI * 4f) * b.pitchC * move; roll = Mathf.Sin(b.phase * Mathf.PI * 2f) * b.rollC * move;
            h.go.transform.SetPositionAndRotation(h.pos, Quaternion.Euler(0f, h.yaw, 0f)); h.body.localPosition = Vector3.up * bodyY; h.body.localRotation = Quaternion.Euler(pitch, 0f, roll);
            if (!far) foreach (var rg in b.rigs) HbIk(h, rg, tgt);
            HbSlip(h, b, rig0, C, dt);
        }
        else { HbLegacyGait(h, b, dt); h.go.transform.SetPositionAndRotation(h.pos, Quaternion.Euler(0f, h.yaw, 0f)); h.body.localPosition = Vector3.up * h.bob; h.body.localRotation = Quaternion.Euler(h.pitch + pitch, 0f, h.roll); }
        if (h.midR != null) LodStep(h, p);
    }
    // no rig (the rigged glb is missing): the old AmbientHorses procedural bob / pitch / roll, driven by the real speed
    void HbLegacyGait(Horse h, HbHorse b, float dt)
    {
        var G = K.gait; h.speed = b.v; h.amp = Mathf.MoveTowards(h.amp, h.speed > G.standMps ? 1f : 0f, dt / Mathf.Max(G.minBlendS, G.blendS)); float tau = 2f * Mathf.PI;
        h.phase = Mathf.Repeat(h.phase + G.cyclesPerM * h.speed * dt * tau, tau); h.breath = Mathf.Repeat(h.breath + dt * G.breathCyclesPerS * tau, tau);
        h.bob = Mathf.Abs(Mathf.Sin(h.phase)) * G.bobM * h.amp + Mathf.Sin(h.breath) * G.breathM * (1f - h.amp); h.pitch = Mathf.Sin(h.phase * 2f) * G.pitchDeg * h.amp; h.roll = Mathf.Sin(h.phase) * G.rollDeg * h.amp;
    }
    // 2 bone IK in the leg plane (z forward, y up of the Body space). Fore knees bend forward, hind hocks bend back. Bone rotation about x; +deg swings the leg back (Unity Euler x).
    void HbIk(Horse h, HbRig rg, Vector3[] tgtGo)
    {
        for (int i = 0; i < 4; i++)
        {
            var tb = h.body.InverseTransformPoint(h.go.transform.TransformPoint(tgtGo[i])); var pv = rg.pivot[i]; float tz = tb.z - pv.z, ty = tb.y - pv.y; float l1 = rg.l1[i], l2 = rg.l2[i], d = Mathf.Sqrt(tz * tz + ty * ty);
            d = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 0.001f, l1 + l2 - 0.001f); float phi = Mathf.Atan2(tz, -ty), cosA = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f), a = Mathf.Acos(cosA), sgn = i < 2 ? 1f : -1f;
            float a1 = phi + sgn * a; float kz = l1 * Mathf.Sin(a1), ky = -l1 * Mathf.Cos(a1); float a2 = Mathf.Atan2(tz - kz, -(ty - ky));
            float th1 = -(a1 - rg.r1[i]), th2 = -((a2 - rg.r2[i]) - (a1 - rg.r1[i]));
            rg.U[i].localRotation = Quaternion.Euler(th1 * Mathf.Rad2Deg, 0f, 0f); rg.L[i].localRotation = Quaternion.Euler(th2 * Mathf.Rad2Deg, 0f, 0f);
        }
    }
    // proof numbers: in stance the hoof marker must stay where it landed (slip) and on the ground (float); logged every slipLogEveryS while moving
    void HbSlip(Horse h, HbHorse b, HbRig rg, HbCfg C, float dt)
    {
        if (!C.logSlip) return; float now = Time.unscaledTime;
        for (int i = 0; i < 4; i++)
        {
            var leg = b.legs[i]; var fw = rg.F[i].position;
            if (leg.stance) { if (leg.slip < 0f) { leg.plantW = fw; leg.slip = 0f; } else { var dlt = fw - leg.plantW; dlt.y = 0f; leg.slip = Mathf.Max(leg.slip, dlt.magnitude); if (leg.slip > b.slipMax) b.slipMax = leg.slip; float fl = fw.y - (h.pos.y + K.sinkM); if (Mathf.Abs(fl) > Mathf.Abs(b.floatMax)) b.floatMax = fl; } }
            else leg.slip = -1f;
        }
        if (b.vAnim > C.move.settleMps && now >= b.nextSlipLog) { b.nextSlipLog = now + C.slipLogEveryS; Debug.Log("[HorseBehav] feet " + h.go.name + " pos=" + h.pos.x.ToString("F1") + "," + h.pos.z.ToString("F1") + " gait=" + b.gaitName + " v=" + b.v.ToString("F2") + " m/s stride=" + b.strideC.ToString("F2") + " cycle=" + (b.vAnim / Mathf.Max(HbEps, b.strideC)).ToString("F2") + " Hz bodyDrop=" + b.bodyY.ToString("F3") + " maxStanceSlip=" + b.slipMax.ToString("F3") + " m maxHoofHeightInStance=" + b.floatMax.ToString("F3") + " m"); b.slipMax = 0f; b.floatMax = 0f; }
    }
}
