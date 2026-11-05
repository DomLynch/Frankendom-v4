using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// PHYS1 v1 PHYSICS PROOF HARNESS (modelled on PostureProof): -physproof:<clock|masks|death|simhash|all> [-out:<dir>] [-physproofcases:<n>] [-proofsecs:<n>]   (-physproof alone = all)
//   clock   : Physics.simulationMode is Script; exactly one Physics.Simulate per sim tick; zero steps while hit-stop is frozen; CombatSlowMo / FinisherTime no longer rewrite Time.fixedDeltaTime.
//   masks   : the three named layers, the collision matrix, a RagdollBody collider is invisible to a PhysMasks.Ground ray but not to ~0, the rigs are pre-built (kinematic, Hurtbox triggers,
//             no cape / skirt bodies, rest pose).
//   death   : scripted kills (enemy victim: each move/zone x each approach direction, player victim: each move/zone from the first direction) + a pure-momentum case (a running fighter is killed with
//             the blow impulse off). Checks per case: converted the same tick, hips travel along the blow >= data minimum in the first 0.5 s, the conversion does not pop (momentum case: every part
//             within maxConvertJumpM of pre-pose + inherited velocity), hips speed after one step within tolerance of the inherited speed, rest within maxRestSeconds with the lowest collider point
//             within restToleranceM of the floor, no NaN, hinge angles inside their ranges, the survivor was not pushed, physics ms per step (avg / p95). Output: 6-frame contact sheet.
//   simhash : the SAME seeded AI-vs-AI fight three times (physics on, off, off) under Time.captureFramerate = 60; per-frame FNV hash of (tick, both fighters' health / posture / stamina / x / z /
//             state / t, the event list). on == off must be byte-identical; the second off run is the control that shows whether the harness is deterministic on its own.
// Output (default <exe dir>\physproof\): physproof_log.txt, physproof_summary.txt, proof_*.png, physproof_sheet.png, simhash_run<i>_<mode>.txt.
// Exit codes: 0 PASS, 1 a check FAILED, 2 exception / setup failure, 3 timeout, 4 simhash INCONCLUSIVE (the harness itself was not deterministic, or no death occurred).
// Nothing on disk is changed (data overrides are in memory and restored). Run the player small for speed: -screen-width 640 -screen-height 360.
[DefaultExecutionOrder(-60)]
public partial class PhysProof : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    static string F(float v) { return v.ToString("F3", IC); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string mode = Arg("-physproof:");
        if (mode == null) { if (Flag("-physproof")) mode = "all"; else return; }
        Application.runInBackground = true;
        if (FindFirstObjectByType<PhysProof>() != null) return;
        var go = new GameObject("PhysProof"); DontDestroyOnLoad(go); go.AddComponent<PhysProof>().mode = mode.ToLowerInvariant();
    }

    public string mode = "all";
    string dir, logPath; float t0, timeoutS; bool exiting; int exitCode; float exitAt;
    PhysProofCfg C;
    DuelFighter D, P;                       // D = Roster.PlayerObject, P = Roster.EnemyObject (as in PostureProof)
    bool sceneReloaded;
    readonly List<string> checks = new List<string>(); int failed;
    readonly List<Color32[]> sheet = new List<Color32[]>(); readonly List<string> sheetRows = new List<string>(); int sheetCols;
    readonly List<string> events = new List<string>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(6) + "s tick " + Duel.TickCount + " step " + PhysicsClock.StepCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[physproof] " + s);
    }
    void Check(string name, bool ok, string detail) { string l = (ok ? "PASS " : "FAIL ") + name + " : " + detail; checks.Add(l); if (!ok) failed++; Log(l); }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup; C = PhysicsCfg.Get().proof; timeoutS = Mathf.Max(30f, C.timeoutSeconds);
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "physproof"); } catch { dir = "physproof"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "physproof_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        float v; var ps = Arg("-proofsecs:"); if (ps != null && float.TryParse(ps, System.Globalization.NumberStyles.Float, IC, out v) && v > 0f) timeoutS = v;
        ContactRelay.OnEvent += OnEvent; SceneManager.sceneLoaded += OnSceneLoaded;
    }
    void OnDestroy() { ContactRelay.OnEvent -= OnEvent; SceneManager.sceneLoaded -= OnSceneLoaded; }
    void OnSceneLoaded(Scene s, LoadSceneMode m) { sceneReloaded = true; }
    void OnEvent(CombatEvent e) { events.Add(e.type + ":" + (e.actor != null ? e.actor.name : "?") + ">" + (e.target != null ? e.target.name : "?") + ":" + e.moveId + "@" + e.tick); }
    void Start() { StartCoroutine(Guarded(Main())); }

    IEnumerator Guarded(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Log("EXCEPTION: " + ex); Finish(2, "exception"); yield break; }
            var nested = cur as IEnumerator;
            if (nested != null) { yield return StartCoroutine(Guarded(nested)); if (exiting) yield break; }   // nested iterators get the same guard
            else yield return cur;
        }
    }

    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > timeoutS) { Log("TIMEOUT after " + timeoutS.ToString("F0", IC) + " s"); Finish(3, "timeout"); }
    }

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; Time.captureFramerate = 0;
        try { WriteSummary(code, why); } catch (Exception e) { Log("summary failed: " + e.Message); }
        try { WriteSheet(); } catch (Exception e) { Log("sheet failed: " + e.Message); }
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    void WriteSummary(int code, string why)
    {
        var sb = new StringBuilder();
        sb.AppendLine("physproof mode=" + mode + " unity " + Application.unityVersion + " res " + Screen.width + "x" + Screen.height + " scripted=" + PhysicsClock.Scripted + " exit=" + code + " (" + why + ")");
        foreach (var c in checks) sb.AppendLine(c);
        sb.AppendLine("checks " + checks.Count + ", failed " + failed + ", physics step avg " + F(PhysicsClock.AvgMs) + " ms, p95 " + F(PhysicsClock.P95Ms) + " ms, max " + F(PhysicsClock.MaxMs) + " ms");
        sb.AppendLine("VERDICT " + (code == 0 && failed == 0 ? "PASS" : (code == 4 ? "INCONCLUSIVE" : "FAIL")));
        File.WriteAllText(Path.Combine(dir, "physproof_summary.txt"), sb.ToString());
    }

    // ---------------------------------------------------------------- helpers
    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    bool FindFighters() { D = Find(Roster.PlayerObject); P = Find(Roster.EnemyObject); return D != null && P != null; }

    IEnumerator WaitFighters()
    {
        float w0 = Time.realtimeSinceStartup;
        while (!FindFighters()) { if (Time.realtimeSinceStartup - w0 > 20f) throw new Exception("fighters not found"); yield return null; }
        for (int i = 0; i < Mathf.Max(1, C.settleFrames); i++) yield return null;
    }

    // the real game's restart (GameHUD Retry): reload the active scene; this object survives
    IEnumerator Reload()
    {
        var oldD = D; var oldP = P; sceneReloaded = false; events.Clear();
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        float w0 = Time.realtimeSinceStartup;
        while (true)
        {
            yield return null;
            if (sceneReloaded && FindFighters() && D != oldD && P != oldP) break;
            if (Time.realtimeSinceStartup - w0 > 25f) throw new Exception("scene reload: no fresh fighters");
        }
        for (int i = 0; i < Mathf.Max(1, C.settleFrames); i++) yield return null;
    }

    void ScriptedFighters()
    {
        Time.captureFramerate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 1f;
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        D.opponent = P; P.opponent = D;
        D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; D.guardHeld = false; P.guardHeld = false; D.run = false; P.run = false;
        D.stamina = D.maxStamina; P.stamina = P.maxStamina;
    }

    void Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }

    float GroundY(Vector3 p, float fallback) { return Physics.Raycast(new Vector3(p.x, p.y + 3f, p.z), Vector3.down, out var hit, 20f, PhysMasks.Ground, QueryTriggerInteraction.Ignore) ? hit.point.y : fallback; }

    IEnumerator Ticks(int n) { int end = Duel.TickCount + n; int guard = 0; while (Duel.TickCount < end && guard++ < n * 4 + 600) yield return null; }
    IEnumerator WaitFor(Func<bool> cond, int maxTicks) { int end = Duel.TickCount + maxTicks; int frames = 0; while (!cond() && Duel.TickCount < end && frames++ < maxTicks * 4 + 600) yield return null; }

    // ---------------------------------------------------------------- script
    IEnumerator Main()
    {
        yield return WaitFighters();
        Log("start mode=" + mode + " res " + Screen.width + "x" + Screen.height + " unity " + Application.unityVersion + " scripted=" + PhysicsClock.Scripted + " simulationMode=" + Physics.simulationMode + " fixedDeltaTime=" + F(Time.fixedDeltaTime)
            + " solver " + Physics.defaultSolverIterations + "/" + Physics.defaultSolverVelocityIterations);
        bool all = mode == "all";
        if (all || mode == "clock") yield return ClockProof();
        if (all || mode == "masks") yield return MasksProof();
        if (all || mode == "death") yield return DeathProof();
        if (all || mode == "hit") yield return HitProof();   // PHYS2 v1: -physproof:hit (Physics/PhysProofHit.cs)
        if (all || mode == "simhash") { yield return SimHashProof(); if (exiting) yield break; }
        if (!all && mode != "clock" && mode != "masks" && mode != "death" && mode != "hit" && mode != "simhash") { Log("unknown mode '" + mode + "' (clock | masks | death | hit | simhash | all)"); Finish(2, "unknown mode"); yield break; }
        Finish(failed == 0 ? 0 : 1, failed == 0 ? "all checks passed" : failed + " check(s) failed");
    }

    // ---------------------------------------------------------------- clock
    IEnumerator ClockProof()
    {
        ScriptedFighters();
        var cl = C.clock;
        Check("clock.scripted", PhysicsClock.Scripted && Physics.simulationMode == SimulationMode.Script, "PhysicsClock.Scripted=" + PhysicsClock.Scripted + " Physics.simulationMode=" + Physics.simulationMode + " (physics.json clock.enabled must be true)");
        Check("clock.solver", Physics.defaultSolverIterations >= PhysicsCfg.Get().world.solverIterations && Physics.defaultSolverVelocityIterations >= PhysicsCfg.Get().world.solverVelocityIterations, "default iterations " + Physics.defaultSolverIterations + "/" + Physics.defaultSolverVelocityIterations + " (data " + PhysicsCfg.Get().world.solverIterations + "/" + PhysicsCfg.Get().world.solverVelocityIterations + ")");
        if (!PhysicsClock.Scripted) yield break;
        yield return Ticks(10);
        // one step per tick
        int s0 = PhysicsClock.StepCount, t0k = Duel.TickCount; yield return Ticks(cl.frames);
        int ds = PhysicsClock.StepCount - s0, dt = Duel.TickCount - t0k;
        Check("clock.oneStepPerTick", ds == dt, ds + " steps for " + dt + " ticks over " + cl.frames + " ticks requested");
        // hit-stop freezes the physics
        float fixedBase = Time.fixedDeltaTime;
        PresentationClock.RequestStop(cl.hitStopMs);
        int s1 = PhysicsClock.StepCount, frozenFrames = 0, stepsWhileFrozen = 0, guard = 0;
        yield return null;
        while (PresentationClock.Frozen && guard++ < 600) { frozenFrames++; stepsWhileFrozen = PhysicsClock.StepCount - s1; yield return null; }
        Check("clock.hitStopFreezes", frozenFrames > 0 && stepsWhileFrozen == 0, frozenFrames + " frozen frames (" + F(cl.hitStopMs) + " ms requested), steps taken while frozen " + stepsWhileFrozen);
        int s2 = PhysicsClock.StepCount; yield return Ticks(10);
        Check("clock.resumesAfterStop", PhysicsClock.StepCount - s2 >= 9, (PhysicsClock.StepCount - s2) + " steps in the 10 ticks after the stop");
        // slow-mo must not touch the fixed step
        bool ok = CombatSlowMo.Request(cl.slowMoClass); float minFixed = Time.fixedDeltaTime, maxFixed = Time.fixedDeltaTime; float minScale = 1f;
        int s3 = PhysicsClock.StepCount, k3 = Duel.TickCount;
        for (int i = 0; i < 40; i++) { yield return null; minFixed = Mathf.Min(minFixed, Time.fixedDeltaTime); maxFixed = Mathf.Max(maxFixed, Time.fixedDeltaTime); minScale = Mathf.Min(minScale, Time.timeScale); }
        if (!ok) Log("NOTE: CombatSlowMo.Request(" + cl.slowMoClass + ") was refused (slow_mo.json disabled / cooldown / finisher owns the clock): the slow-mo rows below prove nothing this run");
        Check("clock.slowMoKeepsFixedStep", Mathf.Approximately(minFixed, fixedBase) && Mathf.Approximately(maxFixed, fixedBase), "CombatSlowMo.Request(" + cl.slowMoClass + ")=" + ok + ", min timeScale " + F(minScale) + ", fixedDeltaTime " + F(minFixed) + ".." + F(maxFixed) + " (base " + F(fixedBase) + ")");
        Check("clock.stepsFollowTicksInSlowMo", (PhysicsClock.StepCount - s3) == (Duel.TickCount - k3), (PhysicsClock.StepCount - s3) + " steps / " + (Duel.TickCount - k3) + " ticks during the dip");
        CombatSlowMo.Abort(); Time.timeScale = 1f;
        FinisherTime.SlowMo(0.5f, 0.2f); float dev = 0f; for (int i = 0; i < 5; i++) { yield return null; dev = Mathf.Max(dev, Mathf.Abs(Time.fixedDeltaTime - fixedBase)); }
        Check("clock.finisherTimeKeepsFixedStep", dev <= 1e-7f, "FinisherTime.SlowMo: largest fixedDeltaTime deviation " + dev.ToString("E2", IC) + " (base " + F(fixedBase) + ", timeScale " + F(Time.timeScale) + ")");
        FinisherTime.Restore(); Time.timeScale = 1f; yield return Ticks(5);
        Log("clock stats: avg " + F(PhysicsClock.AvgMs) + " ms, p95 " + F(PhysicsClock.P95Ms) + " ms, max " + F(PhysicsClock.MaxMs) + " ms over " + PhysicsClock.StatSteps + " steps (empty scene)");
    }

    // ---------------------------------------------------------------- masks / rig
    IEnumerator MasksProof()
    {
        ScriptedFighters();
        int h = PhysMasks.HurtboxLayer, r = PhysMasks.RagdollLayer, p = PhysMasks.PropLayer, g = PhysMasks.Ground;
        Check("masks.layers", h != r && r != p && h != p && h > 9 && r > 9 && p > 9 && h < 32 && r < 32 && p < 32, "hurtbox " + h + ", ragdollBody " + r + ", physProp " + p);
        Check("masks.groundExcludes", (g & ((1 << h) | (1 << r) | (1 << p) | (1 << 8) | (1 << 9))) == 0 && (g & 1) == 1, "Ground mask 0x" + g.ToString("X8"));
        Check("masks.matrix", Physics.GetIgnoreLayerCollision(h, 0) && Physics.GetIgnoreLayerCollision(h, h) && Physics.GetIgnoreLayerCollision(h, r) && !Physics.GetIgnoreLayerCollision(r, 0) && !Physics.GetIgnoreLayerCollision(p, 0) && Physics.GetIgnoreLayerCollision(r, 8) && Physics.GetIgnoreLayerCollision(r, 9),
            "hurtbox-default " + Physics.GetIgnoreLayerCollision(h, 0) + ", hurtbox-hurtbox " + Physics.GetIgnoreLayerCollision(h, h) + ", ragdoll-default " + Physics.GetIgnoreLayerCollision(r, 0) + ", ragdoll-8 " + Physics.GetIgnoreLayerCollision(r, 8) + ", prop-default " + Physics.GetIgnoreLayerCollision(p, 0));
        // a solid RagdollBody collider above the floor: a Ground ray must pass through it, ~0 must hit it
        Vector3 toP = P.transform.position - D.transform.position; toP.y = 0f; if (toP.sqrMagnitude < 0.01f) toP = Vector3.forward; Vector3 at = D.transform.position - toP.normalized * 1.2f; float gy = GroundY(at, at.y);
        var go = new GameObject("physproof_box"); go.layer = r; go.transform.position = new Vector3(at.x, gy + 0.6f, at.z);
        var bc = go.AddComponent<BoxCollider>(); bc.size = new Vector3(1f, 0.2f, 1f); Physics.SyncTransforms();
        RaycastHit hg, ha; bool hitGround = Physics.Raycast(new Vector3(at.x, gy + 3f, at.z), Vector3.down, out hg, 10f, g, QueryTriggerInteraction.Ignore);
        bool hitAll = Physics.Raycast(new Vector3(at.x, gy + 3f, at.z), Vector3.down, out ha, 10f, ~0, QueryTriggerInteraction.Ignore);
        Check("masks.groundRayIgnoresRagdoll", hitGround && Mathf.Abs(hg.point.y - gy) < 0.05f && hitAll && ha.point.y > gy + 0.5f, "Ground ray y " + (hitGround ? F(hg.point.y) : "none") + " (floor " + F(gy) + "), ~0 ray y " + (hitAll ? F(ha.point.y) : "none"));
        Destroy(go); yield return null;
        // rigs
        var d = RagdollDeath.Cfg;
        if (d.enabled && d.rig.prebuild)
            foreach (var f0 in new[] { D, P })
            {
                var cc0 = f0.GetComponent<CharacterController>();
                Check("masks.ccExcludesRagdoll." + f0.name, cc0 != null && ((cc0.excludeLayers.value >> r) & 1) == 1, cc0 == null ? "no CharacterController" : "CharacterController.excludeLayers 0x" + cc0.excludeLayers.value.ToString("X8") + " (bit " + r + " must be set: it survives the cc.enabled toggles that drop IgnoreCollision pairs)");
            }
        foreach (var f in new[] { D, P })
        {
            var rig = f.GetComponent<RagdollRig>();
            if (!d.enabled || !d.rig.prebuild) { Log("rig check skipped for " + f.name + " (ragdoll.json enabled/prebuild off)"); continue; }
            bool built = rig != null && rig.Built;
            string detail = f.name + ": built=" + built;
            bool ok = built;
            if (built)
            {
                int trig = 0, kin = 0, onHurt = 0, cape = 0;
                foreach (var pt in rig.parts)
                {
                    if (pt.cap != null && pt.cap.isTrigger) trig++; if (pt.rb != null && pt.rb.isKinematic) kin++; if (pt.bone.gameObject.layer == h) onHurt++;
                    if (pt.cfg.name.StartsWith("cape") || pt.cfg.name.StartsWith("skirt")) cape++;
                }
                ok = rig.parts.Count >= d.minParts && trig == rig.parts.Count && kin == rig.parts.Count && onHurt == rig.parts.Count && cape == 0 && rig.RestHingeMaxDeg <= d.rig.restCheckMaxDeg;
                detail += " parts " + rig.parts.Count + " (min " + d.minParts + "), trigger " + trig + ", kinematic " + kin + ", Hurtbox layer " + onHurt + ", cape/skirt bodies " + cape + ", rest hinge flex max " + F(rig.RestHingeMaxDeg) + " deg, build " + F(rig.BuildMs) + " ms";
            }
            Check("rig.prebuilt." + f.name, ok, detail);
        }
    }

    // ---------------------------------------------------------------- death
    class Case { public string side, move, zone; public float deg; public bool momentum; public string Id { get { return side + "_" + (momentum ? "momentum" : move + "_" + deg.ToString("F0", IC)); } } }

    IEnumerator DeathProof()
    {
        var dc = C.death; var d = RagdollDeath.Cfg;
        if (!d.enabled || d.parts.Length == 0) { Log("death proof skipped: ragdoll.json enabled=false"); yield break; }
        var cases = new List<Case>();
        for (int a = 0; a < dc.approachDegrees.Length; a++) foreach (var m in dc.moves) cases.Add(new Case { side = "enemy", move = m.move, zone = m.zone, deg = dc.approachDegrees[a] });
        foreach (var m in dc.moves) cases.Add(new Case { side = "player", move = m.move, zone = m.zone, deg = dc.approachDegrees.Length > 0 ? dc.approachDegrees[0] : 0f });
        cases.Add(new Case { side = "enemy", move = "run", zone = "torso", deg = 0f, momentum = true });
        int cap; var cs = Arg("-physproofcases:"); if (cs != null && int.TryParse(cs, out cap) && cap > 0 && cap < cases.Count) cases = cases.GetRange(0, cap);
        sheetCols = Mathf.Max(1, dc.sheetFrames.Length);
        float savedScale = d.kill.impulseScale;
        for (int i = 0; i < cases.Count && !exiting; i++)
        {
            yield return Reload();
            ScriptedFighters();
            d.kill.impulseScale = cases[i].momentum ? dc.momentumImpulseScale : savedScale;
            yield return RunDeathCase(cases[i], i + 1, cases.Count);
            d.kill.impulseScale = savedScale;
        }
        d.kill.impulseScale = savedScale;
        Log("death avg step " + F(PhysicsClock.AvgMs) + " ms");
    }

    IEnumerator RunDeathCase(Case c, int n, int total)
    {
        var dc = C.death; var d = RagdollDeath.Cfg;
        DuelFighter V = c.side == "enemy" ? P : D, A = c.side == "enemy" ? D : P;
        string id = c.Id;
        Log("---- case " + n + "/" + total + " " + id + " victim " + V.name + " attacker " + A.name);
        V.maxHealth = Mathf.Max(V.maxHealth, dc.victimHealth); V.health = dc.victimHealth;
        Vector3 vp = V.transform.position; Vector3 dirA = Quaternion.Euler(0f, c.deg, 0f) * Vector3.forward;
        Place(V, vp, c.momentum ? new Vector3(dc.moveDirX, 0f, dc.moveDirZ) : dirA);
        Place(A, vp + dirA * dc.separationM, -dirA);
        yield return Ticks(20);
        var rig = V.GetComponent<RagdollRig>();
        Check(id + ".rigBuilt", rig != null && rig.Built, rig == null ? "no RagdollRig on " + V.name : "built, " + rig.parts.Count + " parts");
        if (rig == null || !rig.Built) yield break;

        if (c.momentum)
        {
            Vector3 mv = new Vector3(dc.moveDirX, 0f, dc.moveDirZ).normalized; V.moveDir = mv; V.run = dc.run;
            yield return Ticks(Mathf.Max(10, dc.runUpTicks));
            Log("run-up done: " + V.name + " speed state " + V.state);
            V.Die();
        }
        else
        {
            A.Press(c.move);
            yield return WaitFor(() => V.Dead, dc.killWaitTicks);
        }
        yield return null;
        Check(id + ".killed", V.Dead, V.Dead ? "dead at tick " + Duel.TickCount : "no kill within " + dc.killWaitTicks + " ticks (move " + c.move + ", victim state " + V.state + ", hp " + F(V.health) + ")");
        if (!V.Dead) yield break;

        bool conv = rig.Converted;
        Check(id + ".convertedSameTick", conv, "rig.Converted=" + conv);
        if (!conv) yield break;
        PhysicsClock.ResetStats();
        var pre = rig.LastSampledPositions(); var preCopy = pre != null ? (Vector3[])pre.Clone() : null;
        Vector3 hip0 = rig.parts[0].bone.position; float simT = rig.SimTime;
        Vector3 aStart = A.transform.position;
        string zoneGot = rig.BlowZone;
        if (!c.momentum) Check(id + ".zone", zoneGot == c.zone, "blow zone " + zoneGot + " (expected " + c.zone + "), dir (" + F(rig.BlowDir.x) + "," + F(rig.BlowDir.z) + "), knock used (" + F(rig.KnockUsed.x) + "," + F(rig.KnockUsed.z) + ")");

        // first-step pop (momentum case: no blow, so a free-particle prediction is valid)
        if (c.momentum)
        {
            float g = Physics.gravity.y, jump = 0f; string worst = "";
            for (int i = 0; i < rig.parts.Count; i++)
            {
                var pt = rig.parts[i]; Vector3 exp = pt.convertPos + pt.inheritV * simT + new Vector3(0f, 0.5f * g * simT * simT, 0f);
                float j = (pt.bone.position - exp).magnitude; if (j > jump) { jump = j; worst = pt.cfg.name; }
            }
            Check(id + ".noPop", jump <= dc.maxConvertJumpM, "max part deviation from the conversion pose + inherited velocity after " + F(simT * 1000f) + " ms of sim: " + F(jump * 100f) + " cm (" + worst + "), limit " + F(dc.maxConvertJumpM * 100f) + " cm");
            Vector3 v0 = rig.parts[0].inheritV, v1 = rig.parts[0].rb.linearVelocity;
            float allowed = dc.momentumTolerance * Mathf.Max(v0.magnitude, 1f) + 2f * Mathf.Abs(g) * simT;
            Check(id + ".momentum", (v1 - v0).magnitude <= allowed, "hips inherited " + F(v0.magnitude) + " m/s (" + F(v0.x) + "," + F(v0.y) + "," + F(v0.z) + "), after one step " + F(v1.magnitude) + " m/s, delta " + F((v1 - v0).magnitude) + " <= " + F(allowed) + "; root velocity sampled " + F(rig.RootVelUsed.magnitude) + " m/s");
        }

        // observe: travel in the first window, then wait for rest; screenshots at the sheet times
        float travel = float.NaN; bool nan = false; int nextShot = 0; var cells = new Color32[sheetCols][]; float maxSpan = 0f;
        int maxFrames = Mathf.CeilToInt(dc.observeSeconds * 60f) + 120; int fr = 0;
        while (fr++ < maxFrames)
        {
            float t = rig.SimTime;
            if (float.IsNaN(travel) && t >= dc.firstWindowSeconds) travel = Vector3.Dot(rig.parts[0].bone.position - hip0, rig.BlowDir);
            for (int i = 0; i < rig.parts.Count; i++) { var pp = rig.parts[i].bone.position; if (float.IsNaN(pp.x) || float.IsNaN(pp.y) || float.IsNaN(pp.z)) nan = true; }
            maxSpan = Mathf.Max(maxSpan, (rig.parts[rig.parts.Count - 1].bone.position - rig.parts[0].bone.position).magnitude);
            if (nextShot < dc.sheetFrames.Length && (t >= dc.sheetFrames[nextShot] || rig.Frozen))   // a frozen rig stops counting sim time: the later columns show the resting body
            {
                yield return new WaitForEndOfFrame();
                cells[nextShot] = GrabCell(dc.sheetCellWidth, dc.sheetCellHeight); nextShot++;
            }
            if (rig.Frozen && nextShot >= dc.sheetFrames.Length) break;
            if (t >= dc.observeSeconds && nextShot >= dc.sheetFrames.Length) break;
            yield return null;
        }
        if (float.IsNaN(travel)) travel = Vector3.Dot(rig.parts[0].bone.position - hip0, rig.BlowDir);
        float stepAvg = PhysicsClock.AvgMs, stepP95 = PhysicsClock.P95Ms;
        if (!c.momentum) Check(id + ".travelAlongBlow", travel >= dc.minTravelAlongBlowM, "hips travel along the blow in " + F(dc.firstWindowSeconds) + " s: " + F(travel) + " m (min " + F(dc.minTravelAlongBlowM) + ")");
        Check(id + ".noNaN", !nan, nan ? "NaN in a bone position" : "all bone positions finite");
        Check(id + ".noExplosion", maxSpan < 3f, "max hips-to-last-part span " + F(maxSpan) + " m");
        Check(id + ".rest", rig.Frozen && rig.SimTime < dc.maxRestSeconds, "frozen=" + rig.Frozen + " after " + F(rig.SimTime) + " sim s (limit " + F(dc.maxRestSeconds) + " s, hard cap " + F(d.death.maxSeconds) + " s)");
        // lowest collider point vs the floor
        float low = float.MaxValue; Vector3 lowAt = Vector3.zero;
        foreach (var pt in rig.parts) { if (pt.cap == null) continue; float y = pt.cap.bounds.min.y; if (y < low) { low = y; lowAt = pt.cap.bounds.center; } }
        float gyy = GroundY(new Vector3(lowAt.x, lowAt.y + 1f, lowAt.z), float.NaN);
        Check(id + ".onFloor", !float.IsNaN(gyy) && Mathf.Abs(low - gyy) <= dc.restToleranceM, "lowest collider point " + F(low) + " vs floor " + F(gyy) + " = " + F(low - gyy) + " m (tolerance " + F(dc.restToleranceM) + ")");
        // hinge ranges at rest
        string hing = ""; bool hok = true;
        foreach (var pt in rig.parts)
        {
            if (!pt.hinge) continue; float a = rig.HingeAngle(pt); float slack = dc.hingeSlackDeg;
            bool inr = a >= pt.cfg.hinge.min - slack && a <= pt.cfg.hinge.max + slack; if (!inr) hok = false; hing += " " + pt.cfg.name + "=" + a.ToString("F0", IC) + (inr ? "" : "!");
        }
        Check(id + ".hingeRange", hok, "flexion at rest:" + (hing.Length > 0 ? hing : " (no hinge parts active)") + " (slack " + F(dc.hingeSlackDeg) + " deg)");
        Check(id + ".survivorNotPushed", (A.transform.position - aStart).magnitude <= 0.05f, "survivor moved " + F((A.transform.position - aStart).magnitude) + " m after the conversion (corpse vs CharacterController)");
        Check(id + ".stepCost", stepAvg <= dc.maxAvgStepMs && stepP95 <= dc.maxP95StepMs, "physics ms per step avg " + F(stepAvg) + " (max " + F(dc.maxAvgStepMs) + "), p95 " + F(stepP95) + " (max " + F(dc.maxP95StepMs) + ")");
        // sheet row
        for (int i = 0; i < sheetCols; i++) sheet.Add(cells[i] ?? new Color32[dc.sheetCellWidth * dc.sheetCellHeight]);
        sheetRows.Add(id);
    }

    Color32[] GrabCell(int w, int h)
    {
        Texture2D tex = null;
        try { CaptureUtil.HideConsole(); tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("capture failed: " + e.Message); }
        if (tex == null) return new Color32[w * h];
        var src = tex.GetPixels32(); var o = new Color32[w * h];
        for (int y = 0; y < h; y++) { int sy = Mathf.Min(tex.height - 1, y * tex.height / h); for (int x = 0; x < w; x++) { int sx = Mathf.Min(tex.width - 1, x * tex.width / w); o[y * w + x] = src[sy * tex.width + sx]; } }
        Destroy(tex); return o;
    }

    void WriteSheet()
    {
        if (sheetRows.Count == 0 || sheetCols <= 0) return;
        var dc = C.death; int w = dc.sheetCellWidth, h = dc.sheetCellHeight, rows = sheetRows.Count;
        var t = new Texture2D(w * sheetCols, h * rows, TextureFormat.RGBA32, false);
        var all = new Color32[w * sheetCols * h * rows];
        for (int r = 0; r < rows; r++) for (int c = 0; c < sheetCols; c++)
        {
            var cell = sheet[r * sheetCols + c]; int x0 = c * w, y0 = (rows - 1 - r) * h;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) all[(y0 + y) * (w * sheetCols) + x0 + x] = cell[y * w + x];
        }
        t.SetPixels32(all); t.Apply(false);
        File.WriteAllBytes(Path.Combine(dir, "physproof_sheet.png"), t.EncodeToPNG());
        var sb = new StringBuilder("sheet rows top to bottom: "); for (int i = 0; i < rows; i++) sb.Append(sheetRows[i]).Append(i + 1 < rows ? ", " : ""); sb.Append("; columns = sim seconds after the kill:");
        foreach (var f in dc.sheetFrames) sb.Append(' ').Append(f.ToString("F1", IC));
        Log(sb.ToString());
    }

    // ---------------------------------------------------------------- sim hash
    static ulong Mix(ulong h, ulong v) { unchecked { for (int i = 0; i < 8; i++) { h ^= (v >> (i * 8)) & 0xFFUL; h *= 1099511628211UL; } } return h; }
    static ulong Bits(float f) { return unchecked((ulong)(uint)BitConverter.SingleToInt32Bits(f)); }

    class RunTrace { public string mode; public List<ulong> hashes = new List<ulong>(); public List<string> lines = new List<string>(); public int deaths, converted, ticks; public ulong final = 14695981039346656037UL; }

    IEnumerator SimHashProof()
    {
        var sc = C.simHash; var runs = new List<RunTrace>();
        for (int r = 0; r < sc.runs.Length && !exiting; r++)
        {
            string m = sc.runs[r].ToLowerInvariant();
            PhysicsClock.PhysicsOn = m == "on";
            // identical preconditions for every run, set BEFORE the reload so the settle frames are the same too
            Time.captureFramerate = 60; QualitySettings.vSyncCount = 0; Time.timeScale = 1f; PresentationClock.Cancel(); CombatSlowMo.Abort();
            AIBrain.GlobalSeed = sc.seed; UnityEngine.Random.InitState(sc.seed);
            yield return Reload();                                   // a fresh scene per run, so the rigs exist (on) or do not (off) from the start
            ScriptedFighters(); Log("simhash run " + (r + 1) + " physics " + m);
            var tr = new RunTrace { mode = m }; runs.Add(tr);
            yield return RunHashFight(sc, tr);
            Log("simhash run " + (r + 1) + " (" + m + "): " + tr.hashes.Count + " frames, final hash " + tr.final.ToString("X16") + ", deaths " + tr.deaths + ", ragdolls converted " + tr.converted);
            try { File.WriteAllText(Path.Combine(dir, "simhash_run" + (r + 1) + "_" + m + ".txt"), string.Join("\n", tr.lines.ToArray())); } catch { }
        }
        PhysicsClock.PhysicsOn = true;
        if (runs.Count < 2) { Log("simhash needs at least two runs"); Finish(2, "simhash setup"); yield break; }
        RunTrace A = null, B = null, Ctl = null;                 // A = the physics-on run, B = the first off run, Ctl = the second off run (control)
        foreach (var t in runs) { if (t.mode == "on" && A == null) A = t; else if (t.mode == "off" && B == null) B = t; else if (t.mode == "off" && Ctl == null) Ctl = t; }
        if (A == null || B == null) { Log("simhash needs one 'on' and one 'off' run (physics.json proof.simHash.runs)"); Finish(2, "simhash setup"); yield break; }
        int dAB = FirstDiff(A, B), dBC = Ctl != null ? FirstDiff(B, Ctl) : -1;
        bool anyDeath = A.deaths > 0, convertedOn = A.converted > 0;
        Check("simhash.deathHappened", anyDeath && convertedOn, "physics-on run: deaths " + A.deaths + ", ragdolls converted " + A.converted + " (a fight with no kill proves nothing about the ragdoll)");
        if (dAB < 0) Check("simhash.onEqualsOff", true, "on vs off: " + A.hashes.Count + " frames byte-identical, hash " + A.final.ToString("X16") + (dBC >= 0 ? " (note: the control off vs off differs at frame " + dBC + ": the harness is not fully deterministic on its own)" : ", control " + (Ctl != null ? "off vs off identical" : "not run")));
        else if (dBC < 0 && Ctl != null) Check("simhash.onEqualsOff", false, "on vs off DIFFER at frame " + dAB + " while the control (off vs off) is identical: PHYSICS LEAKED INTO THE SIM. " + Describe(A, B, dAB));
        else { Log("simhash INCONCLUSIVE: on vs off differ at frame " + dAB + (Ctl != null ? " and the control also differs at frame " + dBC + ". " + Describe(B, Ctl, dBC) : " and there is no control run")); checks.Add("INCONCLUSIVE simhash: the harness is not deterministic on its own (off vs off differ)"); Finish(4, "simhash inconclusive"); }
    }

    int FirstDiff(RunTrace a, RunTrace b)
    {
        int n = Mathf.Min(a.hashes.Count, b.hashes.Count);
        for (int i = 0; i < n; i++) if (a.hashes[i] != b.hashes[i]) return i;
        return a.hashes.Count == b.hashes.Count ? -1 : n;
    }
    string Describe(RunTrace a, RunTrace b, int i)
    {
        string la = i < a.lines.Count ? a.lines[i] : "(run ended)", lb = i < b.lines.Count ? b.lines[i] : "(run ended)";
        return "frame " + i + ": [" + a.mode + "] " + la + "  vs  [" + b.mode + "] " + lb;
    }

    IEnumerator RunHashFight(PhysProofSimHash sc, RunTrace tr)
    {
        int seed = sc.seed; AIBrain.GlobalSeed = seed; UnityEngine.Random.InitState(seed);
        var fld = typeof(AIBrain).GetField("auto", BindingFlags.NonPublic | BindingFlags.Instance);
        var pb = D.GetComponent<PlayerBrain>(); if (pb != null) pb.enabled = false;
        var dBrain = D.GetComponent<AIBrain>(); if (dBrain == null) dBrain = D.gameObject.AddComponent<AIBrain>();
        if (fld != null) fld.SetValue(dBrain, true); else Log("WARNING: AIBrain.auto field not found; the player's AI may stay off");
        var pBrain = P.GetComponent<AIBrain>();
        dBrain.enabled = false; dBrain.enabled = true; if (pBrain != null) { pBrain.enabled = false; pBrain.enabled = true; }
        var fc = FlowTable.Get(); bool on = fc.enabled && fc.applyToBot; D.flowBotOn = on; P.flowBotOn = false;
        var fb = D.GetComponent<FlowBot>(); if (on) { if (fb == null) fb = D.gameObject.AddComponent<FlowBot>(); fb.enabled = true; fb.Seed(); } else if (fb != null) fb.enabled = false;
        D.maxHealth = sc.victimHealth; D.health = sc.victimHealth; P.maxHealth = sc.victimHealth; P.health = sc.victimHealth;
        events.Clear(); int evSeen = 0; int deadAt = -1; var fighters = new[] { D, P }; int startTick = Duel.TickCount;
        Array.Sort(fighters, (a, b) => string.CompareOrdinal(a.name, b.name));
        for (int frame = 0; frame < sc.maxTicks; frame++)
        {
            ulong h = 14695981039346656037UL; var sb = new StringBuilder();
            h = Mix(h, (ulong)(Duel.TickCount - startTick)); sb.Append("tick ").Append(Duel.TickCount - startTick);
            foreach (var f in fighters)
            {
                var p = f.transform.position;
                h = Mix(h, Bits(f.health)); h = Mix(h, Bits(f.posture)); h = Mix(h, Bits(f.stamina)); h = Mix(h, Bits(p.x)); h = Mix(h, Bits(p.z)); h = Mix(h, (ulong)(int)f.state); h = Mix(h, (ulong)f.t);
                sb.Append(" | ").Append(f.name).Append(" hp ").Append(f.health.ToString("R", IC)).Append(" post ").Append(f.posture.ToString("R", IC)).Append(" st ").Append(f.stamina.ToString("R", IC)).Append(" x ").Append(p.x.ToString("R", IC)).Append(" z ").Append(p.z.ToString("R", IC)).Append(" ").Append(f.state).Append(" t").Append(f.t);
            }
            for (; evSeen < events.Count; evSeen++) { string e = events[evSeen]; sb.Append(" ev ").Append(e); for (int k = 0; k < e.Length; k++) h = Mix(h, e[k]); }
            tr.hashes.Add(h); tr.lines.Add(sb.ToString()); tr.final = Mix(tr.final, h); tr.ticks = Duel.TickCount;
            if (deadAt < 0 && (D.Dead || P.Dead)) { deadAt = frame; tr.deaths = (D.Dead ? 1 : 0) + (P.Dead ? 1 : 0); }
            if (deadAt >= 0 && frame - deadAt >= sc.postDeathTicks) break;
            yield return null;
        }
        foreach (var f in fighters) { var rg = f.GetComponent<RagdollRig>(); if (rg != null && rg.Converted) tr.converted++; }
    }
}
