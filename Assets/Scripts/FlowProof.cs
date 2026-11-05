using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// FLOW PROOF HARNESS (lane V64FLOW): -flowproof [-out:<dir>] [-proofsecs:<n> (default 420)]
// Scripts Dracula (PlayerBrain / AIBrain / TouchControls off; FlowTable.ForceApply makes the flow layer treat him as the human) against a passive Pitborn through the same DuelFighter / FlowTable
// entry points PlayerBrain uses, at Time.captureFramerate = 60 (1 frame = 1 sim tick), and logs PASS / FAIL per item:
//   combo:<name>        each flow.json chain.combos entry: the steps start in order, each link is a chain (flowChains), gaps between starts logged
//   cancel_attack_dodge a light cancelled into a roll inside the cancel.json window (before the recovery ends)
//   cancel_onhit_hit / cancel_onhit_whiff   heavy -> light is an onHitOnly row: chains early after a hit, waits for the recovery end after a whiff
//   lock_toggle / lock_strafe_locked / lock_free_unlocked   FlowTable.ToggleLock flips CameraRig.lockEnabled + df.lockFacing; locked strafing keeps facing the target with strafe clips; unlocked turns to the move direction
//   magnet_snap / magnet_outside_cone / magnet_outside_range   the swing snaps to the target inside the cone + range (aligned within snapMs), not outside
//   turn_cap_active     a target orbiting at 400 deg/s cannot drag the body faster than attack.turn.activeDegPerSec during the Active frames
//   dodge_<dir>         forward / back / left / right: i-frame tick range, direction of travel, distance and stamina cost equal the flow.json row
//   avoid_<dir>         a baseline swing hits; the same swing with the dodge timed into the i-frames does not (health unchanged); a late dodge is hit
//   riposte_key / riposte_auto   after a parry the dedicated key (and a plain strike press) starts the riposte
//   ai_unaffected       the Pitborn and a Dracula whose PlayerBrain is off (BalanceAuto bot, MoveSheet) are not touched by the flow layer
// Output (default <exe dir>\flow_proof\): flowproof_*.png (full-screen, HUD in frame; run the player at 1920x1080), flow_proof_log.txt, flow_proof_summary.txt, flow_proof_results.json, flow_proof_contact.png (4x3).
// Exit codes: 0 all PASS, 1 some FAIL, 2 exception / setup failure, 3 timeout. In-memory overrides only; nothing on disk is changed.
[DefaultExecutionOrder(-60)]
public class FlowProof : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-flowproof")) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<FlowProof>() != null) return;
        var go = new GameObject("FlowProof"); DontDestroyOnLoad(go); go.AddComponent<FlowProof>();
    }

    string dir, logPath; float t0, timeoutS = 420f; bool exiting; int exitCode; float exitAt;
    DuelFighter D, P; CameraRig rig; Vector3 home, axis0;
    struct AtkStart { public int tick; public string who, move; public int chains; }
    readonly List<AtkStart> starts = new List<AtkStart>();
    int parried, missesD, lastHitOnD = -1, hitsOnD;
    readonly List<Texture2D> keyFrames = new List<Texture2D>(); readonly List<string> keyNames = new List<string>();
    class Item { public string name; public bool ok; public string detail; }
    readonly List<Item> items = new List<Item>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(6) + "s tick " + Duel.TickCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[flowproof] " + s);
    }
    void Check(string name, bool ok, string detail)
    {
        items.Add(new Item { name = name, ok = ok, detail = detail });
        Log((ok ? "PASS " : "FAIL ") + name + " :: " + detail);
    }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "flow_proof"); } catch { dir = "flow_proof"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "flow_proof_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        float v; var ps = Arg("-proofsecs:"); if (ps != null && float.TryParse(ps, System.Globalization.NumberStyles.Float, IC, out v) && v > 0f) timeoutS = v;
        ContactRelay.OnEvent += OnEvent; ContactRelay.OnAttackStarted += OnStarted; ContactRelay.OnAttackMissed += OnMissed;
    }
    void OnDestroy() { ContactRelay.OnEvent -= OnEvent; ContactRelay.OnAttackStarted -= OnStarted; ContactRelay.OnAttackMissed -= OnMissed; }
    void Start() { StartCoroutine(Guarded(Main())); }

    void OnStarted(DuelFighter f, MoveDef m) { starts.Add(new AtkStart { tick = Duel.TickCount, who = f.name, move = m.id, chains = f.flowChains }); Log("  START " + f.name + " " + m.id + " (chains " + f.flowChains + ")"); }
    void OnMissed(DuelFighter f, MoveDef m) { if (f == P) missesD++; Log("  MISSED " + f.name + " " + m.id); }
    void OnEvent(CombatEvent e)
    {
        if (e.type == "Parried") parried++;
        if (e.target == D && (e.type == "Hit" || e.type == "Blocked")) { hitsOnD++; lastHitOnD = Duel.TickCount; }
        Log("  EVENT " + e.type + " " + (e.actor != null ? e.actor.name : "?") + "->" + (e.target != null ? e.target.name : "?") + " move=" + e.moveId);
    }
    int CountStarts(string who) { int n = 0; foreach (var s in starts) if (s.who == who) n++; return n; }

    IEnumerator Guarded(IEnumerator inner)
    {
        while (true)
        {
            object cur;
            try { if (!inner.MoveNext()) yield break; cur = inner.Current; }
            catch (Exception ex) { Log("EXCEPTION: " + ex); Finish(2, "exception"); yield break; }
            yield return cur;
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
        exiting = true; exitAt = Time.unscaledTime; Time.captureFramerate = 0;
        bool allOk = items.Count > 0; foreach (var i in items) if (!i.ok) allOk = false;
        exitCode = code == 0 && !allOk ? 1 : code;
        try { WriteSummary(exitCode, why); } catch (Exception e) { Log("summary failed: " + e.Message); }
        try { MakeContact(); } catch (Exception e) { Log("contact sheet failed: " + e.Message); }
        Application.Quit(exitCode);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    // ---------------------------------------------------------------- helpers
    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    void Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }
    // Pitborn at the fixed home spot, Dracula `sep` metres in front of him on the fixed axis, both facing
    void Setup(float sep)
    {
        Place(D, home - axis0 * sep, axis0); Place(P, home, -axis0);
        D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; D.guardHeld = false; P.guardHeld = false;
    }
    Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    Vector3 Ax() { Vector3 a = Flat(P.transform.position - D.transform.position); return a.sqrMagnitude < 1e-4f ? axis0 : a.normalized; }
    Vector3 Rt() { return Vector3.Cross(Vector3.up, Ax()); }
    float AngleToP() { return Vector3.Angle(Flat(D.transform.forward), Flat(P.transform.position - D.transform.position)); }
    void SetLock(bool on) { if (rig != null) rig.lockEnabled = on; D.lockFacing = on; }

    IEnumerator Ticks(int n) { int end = Duel.TickCount + n; int guard = 0; while (Duel.TickCount < end && guard++ < n * 4 + 600) yield return null; }
    IEnumerator WaitFor(Func<bool> cond, int maxTicks)
    {
        int end = Duel.TickCount + maxTicks; int frames = 0;
        while (!cond() && Duel.TickCount < end && frames++ < maxTicks * 4 + 600) yield return null;
    }
    // everything back to a clean slate: both idle, full health / stamina, no posture, lock set, positions fixed
    IEnumerator Reset(float sep, bool lockOn = true)
    {
        D.guardHeld = false; P.guardHeld = false; D.moveDir = Vector3.zero; P.moveDir = Vector3.zero;
        yield return WaitFor(() => D.state == FState.Loco && P.state == FState.Loco, 260);
        D.health = D.maxHealth; P.health = P.maxHealth; D.stamina = D.maxStamina; P.stamina = P.maxStamina; D.posture = 0f; P.posture = 0f;
        SetLock(lockOn); Setup(sep);
        yield return Ticks(12);
        D.stamina = D.maxStamina; P.stamina = P.maxStamina;
    }

    IEnumerator Shot(string name)
    {
        yield return null; yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        CaptureUtil.HideConsole(); try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("SHOT failed " + name + ": " + e.Message); }
        if (tex == null) yield break;
        try { File.WriteAllBytes(Path.Combine(dir, "flowproof_" + name + ".png"), tex.EncodeToPNG()); Log("SHOT flowproof_" + name + ".png " + tex.width + "x" + tex.height + (tex.width != 1920 || tex.height != 1080 ? " (NOT 1920x1080: run with -screen-width 1920 -screen-height 1080 -screen-fullscreen 0)" : "")); }
        catch (Exception e) { Log("SHOT write failed " + name + ": " + e.Message); }
        keyFrames.Add(tex); keyNames.Add(name);
    }

    static bool Matches(string type, string moveId) { return type == "light" ? moveId.StartsWith("light", StringComparison.Ordinal) : type == "heavy" ? moveId == "heavy_overhead" : moveId == type; }
    string F(float v) { return v.ToString("F2", IC); }

    // ---------------------------------------------------------------- script
    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((D = Find("Dracula")) == null || (P = Find("Pitborn")) == null)
        {
            if (Time.realtimeSinceStartup - w0 > 20f) { Log("fighters not found"); Finish(2, "fighters not found"); yield break; }
            yield return null;
        }
        for (int i = 0; i < 30; i++) yield return null;
        Time.captureFramerate = 60; QualitySettings.vSyncCount = 0;
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        D.opponent = P; P.opponent = D; rig = FindFirstObjectByType<CameraRig>();
        var fd = FlowTable.Get(); if (!fd.enabled) { fd.enabled = true; Log("NOTE flow.json missing or disabled: forced on in memory for the proof"); }
        Log("start res " + Screen.width + "x" + Screen.height + " unity " + Application.unityVersion + " rig " + (rig != null) + " chain rows " + fd.chain.rows.Length + " combos " + fd.chain.combos.Length);
        P.maxHealth = 5000f; P.health = 5000f; D.maxHealth = 5000f; D.health = 5000f;

        // ---- ai_unaffected (before the harness forces the flow layer on)
        {
            FlowTable.ForceApply = false;
            bool dOff = !FlowTable.Applies(D), pOff = !FlowTable.Applies(P);
            FlowTable.ForceApply = true; FlowTable.ForceHud = true;
            bool dOn = FlowTable.Applies(D), pStill = !FlowTable.Applies(P);
            Check("ai_unaffected", dOff && pOff && dOn && pStill, "Dracula with PlayerBrain off: applies=" + !dOff + " (want false); Pitborn applies=" + !pOff + " (want false); harness force on: Dracula " + dOn + " (want true), Pitborn " + !pStill + " (want false)");
        }
        home = P.transform.position; home.y = 0f;
        Vector3 a0 = Flat(P.transform.position - D.transform.position); axis0 = a0.sqrMagnitude < 0.01f ? Vector3.forward : a0.normalized;
        yield return Reset(1.7f);

        // ---- (1) combos from flow.json
        for (int ci = 0; ci < fd.chain.combos.Length; ci++)
        {
            var c = fd.chain.combos[ci]; if (c == null || c.steps == null || c.steps.Length < 2) continue;
            yield return Reset(1.45f);
            int s0 = starts.Count, ch0 = D.flowChains;
            D.Press(c.steps[0]);
            for (int i = 1; i < c.steps.Length; i++)
            {
                int want = s0 + i;
                yield return WaitFor(() => starts.Count >= want, 170);
                if (starts.Count < want) break;
                D.Press(c.steps[i]);
            }
            yield return WaitFor(() => starts.Count >= s0 + c.steps.Length, 170);
            var mine = new List<AtkStart>(); for (int i = s0; i < starts.Count; i++) if (starts[i].who == "Dracula") mine.Add(starts[i]);
            bool seq = mine.Count == c.steps.Length; var sb = new StringBuilder();
            for (int i = 0; i < mine.Count; i++) { if (i < c.steps.Length && !Matches(c.steps[i], mine[i].move)) seq = false; sb.Append(mine[i].move).Append(i + 1 < mine.Count ? " >" + (mine[i + 1].tick - mine[i].tick) + "t> " : ""); }
            int chains = D.flowChains - ch0; bool chained = chains == c.steps.Length - 1;
            Check("combo:" + c.name, seq && chained, "started " + mine.Count + "/" + c.steps.Length + " [" + sb + "], chain links " + chains + "/" + (c.steps.Length - 1) + ", last " + D.flowLastChain);
            if (ci < 2) { yield return WaitFor(() => D.state == FState.Active, 80); yield return Shot("combo_" + c.name); }
        }

        // ---- (2) cancels
        {
            yield return Reset(1.45f);
            D.Press("light"); int lastRec = -1, recLen = 22; bool pressed = false, rolled = false; int g = 0;
            int earliest = CombatFeel.Earliest("light_right", "roll");
            while (g++ < 140)
            {
                yield return null;
                if (D.state == FState.Recovery) { lastRec = D.MoveT; if (D.CurrentMove != null) recLen = D.CurrentMove.Recovery(false); if (!pressed) { D.Press("roll"); pressed = true; } }
                if (D.state == FState.Roll) { rolled = true; break; }
            }
            Check("cancel_attack_dodge", rolled && lastRec + 1 >= earliest - 1 && lastRec + 1 <= earliest + 2 && lastRec < recLen - 1, "roll started at recovery t=" + (lastRec + 1) + " (cancel.json earliest " + earliest + ", recovery length " + recLen + "), rolled=" + rolled);
            if (rolled) { yield return Ticks(3); yield return Shot("cancel_roll"); }
        }
        {   // heavy -> light, onHitOnly
            yield return Reset(1.45f);
            int ch0 = D.flowChains, s0 = CountStarts("Dracula"); int lastRec = -1; bool pressed = false; int g = 0; var row = FlowTable.FindChain("heavy_overhead", "light", 999, true); int eMin = row != null ? row.earliest : 14;
            D.Press("heavy");
            while (g++ < 200) { yield return null; if (D.state == FState.Recovery) { lastRec = D.MoveT; if (!pressed) { D.Press("light"); pressed = true; } } if (CountStarts("Dracula") > s0 + 1) break; }
            bool hitDone = true;
            Check("cancel_onhit_hit", D.flowChains == ch0 + 1 && lastRec + 1 >= eMin - 1 && lastRec + 1 <= eMin + 2, "after a landed heavy the light chained at recovery t=" + (lastRec + 1) + " (row earliest " + eMin + "), chain links " + (D.flowChains - ch0) + ", hitDone " + hitDone);
            yield return Reset(1.45f);
            ch0 = D.flowChains; s0 = CountStarts("Dracula"); lastRec = -1; pressed = false; g = 0;
            D.Press("heavy");
            yield return WaitFor(() => D.state == FState.Windup, 20);
            Place(P, home + axis0 * 6f, -axis0);   // target gone: the heavy whiffs
            while (g++ < 240) { yield return null; if (D.state == FState.Recovery) { lastRec = D.MoveT; if (!pressed) { D.Press("light"); pressed = true; } } if (CountStarts("Dracula") > s0 + 1) break; }
            int recLen = D.moves.Get("heavy_overhead").Recovery(false);
            Check("cancel_onhit_whiff", D.flowChains == ch0 && lastRec >= recLen - 2, "after a whiffed heavy the light waited for the recovery end: last recovery t=" + lastRec + " of " + recLen + ", chain links " + (D.flowChains - ch0) + " (want 0)");
        }

        // ---- (3) lock-on
        if (rig == null) { Check("lock_toggle", false, "no CameraRig in the scene"); }
        else
        {
            yield return Reset(3.0f, true);
            bool start = rig.lockEnabled && D.lockFacing;
            FlowTable.ToggleLock(rig, D); bool off = !rig.lockEnabled && !D.lockFacing;
            yield return Shot("lock_off");
            FlowTable.ToggleLock(rig, D); bool on = rig.lockEnabled && D.lockFacing;
            yield return Shot("lock_on");
            Check("lock_toggle", start && off && on, "start locked=" + start + ", after toggle off=" + off + ", after second toggle on=" + on + " (keys " + string.Join("/", FlowTable.Get().lockOn.keys) + ", pushAwayBreaks " + FlowTable.PushAwayBreaks + ", attackRelocks " + FlowTable.AttackRelocks + ")");

            // strafe while locked
            yield return Reset(3.0f, true);
            Vector3 rt = Rt(), p0 = D.transform.position; float maxAng = 0f; int samples = 0, strafeFrames = 0; var fi = D.GetComponent<Fighter>();
            D.moveDir = rt;
            for (int i = 0; i < 70; i++) { yield return null; if (i >= 12) { maxAng = Mathf.Max(maxAng, AngleToP()); samples++; if (fi != null && fi.Current != null && fi.Current.StartsWith("strafe", StringComparison.Ordinal)) strafeFrames++; } }
            yield return Shot("lock_strafe");
            D.moveDir = Vector3.zero;
            float lat = Vector3.Dot(Flat(D.transform.position - p0), rt);
            Check("lock_strafe_locked", lat > 1.0f && maxAng < 25f && strafeFrames >= samples * 0.6f, "lateral " + F(lat) + " m, max angle to target " + F(maxAng) + " deg (want < 25), strafe clip on " + strafeFrames + "/" + samples + " frames (last '" + (fi != null ? fi.Current : "?") + "')");

            // same move direction with the lock off: the body turns to the move direction
            yield return Reset(3.0f, true); FlowTable.ToggleLock(rig, D);
            rt = Rt(); strafeFrames = 0; samples = 0; D.moveDir = rt;
            for (int i = 0; i < 50; i++) { yield return null; if (i >= 25) { samples++; if (fi != null && fi.Current != null && fi.Current.StartsWith("strafe", StringComparison.Ordinal)) strafeFrames++; } }
            float toMove = Vector3.Angle(Flat(D.transform.forward), rt), toTarget = AngleToP();
            D.moveDir = Vector3.zero;
            Check("lock_free_unlocked", toMove < 30f && toTarget > 45f && strafeFrames == 0, "lock off, moving sideways: facing within " + F(toMove) + " deg of the move direction (want < 30), " + F(toTarget) + " deg off the target (want > 45), strafe clip frames " + strafeFrames);
            FlowTable.ToggleLock(rig, D);
        }

        // ---- (4) magnetism + turn cap
        {
            FlowTable.AtkParams ap = FlowTable.Attack("light_right");
            float snapFrames = Mathf.Ceil(ap.snapMs / 16.667f) + 4f;
            for (int k = 0; k < 3; k++)
            {
                string nm = k == 0 ? "magnet_snap" : k == 1 ? "magnet_outside_cone" : "magnet_outside_range";
                float sep = k == 2 ? ap.maxDist + 1.6f : 2.2f, off = k == 0 ? 35f : k == 1 ? 80f : 35f; bool expect = k == 0;
                yield return Reset(sep, false);
                Vector3 ax = Ax(); D.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(off, Vector3.up) * ax);
                float e0 = AngleToP(); int alignedAt = -1; D.Press("light");
                for (int i = 0; i < 24; i++) { yield return null; if (alignedAt < 0 && AngleToP() < 4f) alignedAt = i + 1; }
                float eEnd = AngleToP();
                bool ok = expect ? (alignedAt > 0 && alignedAt <= snapFrames && e0 > 25f && D.flowMagnetUsed) : (alignedAt < 0 && eEnd > e0 - 8f && !D.flowMagnetUsed);
                Check(nm, ok, "start " + F(sep) + " m, " + F(e0) + " deg off the target (cone " + F(ap.maxAngle) + " deg, range " + F(ap.maxDist) + " m, snap " + F(ap.snapMs) + " ms): aligned (<4 deg) after " + alignedAt + " frames (want " + (expect ? "<= " + snapFrames : "never") + "), end error " + F(eEnd) + " deg, magnet used " + D.flowMagnetUsed);
                if (k == 0) yield return Shot("magnet_snap_end");
            }
            // turn cap: a target orbiting at 400 deg/s
            yield return Reset(1.8f, true);
            D.FlowResetTurnStats(); Vector3 ax0 = Ax(); float th = 0f; D.Press("light");
            for (int i = 0; i < 60; i++)
            {
                th += 400f / 60f; Place(P, D.transform.position + Quaternion.AngleAxis(th, Vector3.up) * (ax0 * 1.8f), -ax0);
                yield return null;
            }
            float capA = ap.active;
            Check("turn_cap_active", D.flowMaxTurnActive <= capA * 1.12f + 2f && D.flowMaxTurnActive >= capA * 0.5f, "orbiting target 400 deg/s: max body turn in Active " + F(D.flowMaxTurnActive) + " deg/s (cap " + F(capA) + "), in Windup " + F(D.flowMaxTurnWindup) + " (cap " + F(ap.windup) + " + snap), in Recovery " + F(D.flowMaxTurnRecovery) + " (cap " + F(ap.recovery) + ")");
            Setup(1.7f);
        }

        // ---- (5) directional dodge + i-frame hit avoidance
        string[] dirs = { "forward", "back", "left", "right" };
        foreach (var dn in dirs)
        {
            var row = FlowTable.DodgeDir(dn);
            if (row == null) { Check("dodge_" + dn, false, "no flow.json dodge row"); continue; }
            yield return Reset(dn == "forward" ? 4.8f : 2.6f, true);
            Vector3 ax = Ax(), rt = Rt(), p0 = D.transform.position; float st0 = D.stamina;
            Vector3 want = dn == "forward" ? ax : dn == "back" ? -ax : dn == "left" ? -rt : rt;
            if (dn == "forward") D.moveDir = ax;
            FlowTable.DodgePress(D, dn);
            int firstI = -1, lastI = -1, g = 0; bool started = false; float stStart = st0;
            while (g++ < 200)
            {
                yield return null;
                if (D.state == FState.Roll) { if (!started) { started = true; stStart = D.stamina; } if (D.IFrames) { if (firstI < 0) firstI = D.MoveT; lastI = D.MoveT; } }
                else if (started) break;
            }
            D.moveDir = Vector3.zero;
            Vector3 disp = Flat(D.transform.position - p0); float along = Vector3.Dot(disp, want), offAx = (disp - want * along).magnitude, cost = st0 - stStart;
            bool iOk = started && Mathf.Abs(firstI - row.iframeStart) <= 1 && Mathf.Abs(lastI - row.iframeEnd) <= 1;
            bool dOk = along >= 0.6f * row.distance * D.scale && offAx <= Mathf.Max(0.6f, along * 0.5f);
            bool cOk = Mathf.Abs(cost - row.stamina) < 0.75f;
            Check("dodge_" + dn, started && iOk && dOk && cOk, "i-frames t=" + firstI + ".." + lastI + " (row " + row.iframeStart + ".." + row.iframeEnd + "), travelled " + F(along) + " m along the " + dn + " axis, " + F(offAx) + " off-axis (row distance " + F(row.distance) + "), stamina cost " + F(cost) + " (row " + F(row.stamina) + "), kind " + D.DodgeKind + ", label '" + D.flowDodgeDir + "'");
        }
        foreach (var dn in dirs)
        {
            var row = FlowTable.DodgeDir(dn); if (row == null) continue;
            // baseline: no dodge, the swing lands
            yield return Reset(1.6f, true);
            float hp0 = D.health; int T0 = Duel.TickCount, hits0 = hitsOnD; P.Press("light");
            yield return WaitFor(() => hitsOnD > hits0 || P.state == FState.Recovery, 120);
            bool baseHit = hitsOnD > hits0 && D.health < hp0; int cT = lastHitOnD - T0;
            if (!baseHit) { Check("avoid_" + dn, false, "baseline swing did not land (hit " + baseHit + "): geometry"); continue; }
            // dodge timed so the i-frames start 1 tick before the contact tick (then 0, then 2 ticks) - the first offset that avoids it counts
            bool avoided = false; int usedOff = 0; string tried = "";
            int[] offs = { 1, 0, 2 };
            for (int oi = 0; oi < offs.Length && !avoided; oi++)
            {
                yield return Reset(1.6f, true);
                hp0 = D.health; int T1 = Duel.TickCount, h1 = hitsOnD, m1 = missesD; P.Press("light");
                int pressTick = T1 + cT - row.iframeStart - 1 - offs[oi];
                yield return WaitFor(() => Duel.TickCount >= pressTick, 200);
                if (dn == "forward") D.moveDir = Ax();
                FlowTable.DodgePress(D, dn);
                if (dn == "forward" || dn == "right") { yield return WaitFor(() => P.state == FState.Active, 40); if (oi == 0 || !keyNames.Contains("avoid_" + dn)) yield return Shot("avoid_" + dn); }
                yield return WaitFor(() => P.state == FState.Recovery || P.state == FState.Loco, 120);
                D.moveDir = Vector3.zero;
                avoided = D.health >= hp0 - 0.01f && hitsOnD == h1 && missesD > m1; usedOff = offs[oi]; tried += "(offset " + offs[oi] + (avoided ? " avoided" : " hit") + ") ";
            }
            // late dodge: pressed after the contact tick, must be hit
            yield return Reset(1.6f, true);
            hp0 = D.health; int T2 = Duel.TickCount, h2 = hitsOnD; P.Press("light");
            int lateTick = T2 + cT + 3; yield return WaitFor(() => Duel.TickCount >= lateTick, 200);
            FlowTable.DodgePress(D, dn);
            yield return WaitFor(() => P.state == FState.Recovery || P.state == FState.Loco, 120);
            D.moveDir = Vector3.zero;
            bool lateHit = hitsOnD > h2 && D.health < hp0;
            Check("avoid_" + dn, baseHit && avoided && lateHit, "baseline hit at +" + cT + " ticks: " + baseHit + "; dodge with i-frames " + row.iframeStart + ".." + row.iframeEnd + " " + tried + "-> avoided " + avoided + " (health unchanged, swing missed); late dodge (+3 after contact) was hit: " + lateHit);
        }

        // ---- (6) riposte after a parry: dedicated key, then the plain strike press
        for (int mode = 0; mode < 2; mode++)
        {
            string nm = mode == 0 ? "riposte_key" : "riposte_auto"; bool got = false; string note = "";
            int[] leads = { 5, 3, 7, 2, 9, 4 };
            for (int a = 0; a < leads.Length && !got; a++)
            {
                yield return Reset(1.7f, true); yield return Ticks(130);
                int pc = parried, lead = leads[a]; P.Press("light");
                yield return WaitFor(() => P.state == FState.Windup, 30);
                yield return WaitFor(() => P.state != FState.Windup || P.WindupLeft <= lead, 60);
                D.guardHeld = true;
                yield return WaitFor(() => parried > pc || P.state == FState.Recovery || P.state == FState.Loco, 60);
                D.guardHeld = false;
                if (parried <= pc) { yield return Ticks(10); continue; }
                bool windowOpen = D.RiposteLeft > 0;
                if (mode == 0) { yield return Shot("riposte_window"); }
                int s0 = CountStarts("Dracula"); int r0 = D.flowRipostes;
                if (mode == 0) D.PressRiposte(); else D.Press("light");
                yield return WaitFor(() => CountStarts("Dracula") > s0, 40);
                string mv = ""; for (int i = starts.Count - 1; i >= 0; i--) if (starts[i].who == "Dracula") { mv = starts[i].move; break; }
                got = CountStarts("Dracula") > s0 && mv == "riposte" && (mode == 1 || D.flowRipostes == r0 + 1);
                note = "parry achieved (attempt " + (a + 1) + "), window open after the parry " + windowOpen + ", move started '" + mv + "', flowRipostes +" + (D.flowRipostes - r0);
            }
            Check(nm, got, got ? note : "no parry achieved or no riposte started " + note);
        }
        Log("end");
        Finish(0, "done");
    }

    // ---------------------------------------------------------------- output
    void WriteSummary(int code, string why)
    {
        int pass = 0, fail = 0; foreach (var i in items) { if (i.ok) pass++; else fail++; }
        string verdict = code == 2 ? "ERROR" : code == 3 ? "TIMEOUT" : (fail == 0 && pass > 0 ? "PASS" : "FAIL");
        var sb = new StringBuilder(); var js = new StringBuilder("{\"verdict\":\"" + verdict + "\",\"pass\":" + pass + ",\"fail\":" + fail + ",\"items\":[");
        sb.AppendLine("VERDICT: " + verdict + "   (exit code " + code + ", " + why + ")   items: " + pass + " PASS / " + fail + " FAIL");
        bool first = true;
        foreach (var i in items)
        {
            sb.AppendLine((i.ok ? "PASS  " : "FAIL  ") + i.name + "  -  " + i.detail);
            if (!first) js.Append(','); first = false;
            js.Append("{\"name\":\"" + i.name + "\",\"ok\":" + (i.ok ? "true" : "false") + ",\"detail\":\"" + i.detail.Replace("\\", "\\\\").Replace("\"", "'") + "\"}");
        }
        js.Append("]}");
        sb.AppendLine("screenshots: " + string.Join(", ", keyNames.ToArray()));
        try { File.AppendAllText(logPath, "\n=== SUMMARY ===\n" + sb); } catch { }
        try { File.WriteAllText(Path.Combine(dir, "flow_proof_summary.txt"), sb.ToString()); File.WriteAllText(Path.Combine(dir, "flow_proof_results.json"), js.ToString()); } catch { }
        Debug.Log("[flowproof] " + sb);
    }

    // 4x3 contact sheet (480x270 tiles) of the key frames, in this order when present
    void MakeContact()
    {
        if (keyFrames.Count == 0) return;
        string[] wantNames = { "combo_light-light-heavy", "combo_light-thrust", "cancel_roll", "lock_off", "lock_on", "lock_strafe", "magnet_snap_end", "avoid_forward", "avoid_right", "riposte_window" };
        var pick = new List<int>();
        foreach (var w in wantNames) { int i = keyNames.IndexOf(w); if (i >= 0) pick.Add(i); }
        for (int i = 0; i < keyFrames.Count && pick.Count < 12; i++) if (!pick.Contains(i)) pick.Add(i);
        int tw = 480, th = 270, cols = 4, rows = 3;
        var sheet = new Texture2D(tw * cols, th * rows, TextureFormat.RGBA32, false);
        var fill = new Color32[tw * cols * th * rows]; for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(20, 20, 20, 255);
        sheet.SetPixels32(fill);
        for (int k = 0; k < pick.Count && k < cols * rows; k++)
        {
            var src = keyFrames[pick[k]]; int ox = (k % cols) * tw, oy = (rows - 1 - k / cols) * th;
            var px = new Color[tw * th];
            for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) px[y * tw + x] = src.GetPixelBilinear((x + 0.5f) / tw, (y + 0.5f) / th);
            sheet.SetPixels(ox, oy, tw, th, px);
        }
        sheet.Apply();
        File.WriteAllBytes(Path.Combine(dir, "flow_proof_contact.png"), sheet.EncodeToPNG());
        var names = new List<string>(); foreach (var i in pick) names.Add(keyNames[i]);
        Log("contact sheet (row-major, tile 0 top-left): " + string.Join(", ", names.ToArray()));
        Destroy(sheet);
    }
}
