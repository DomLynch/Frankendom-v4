#if !FRANKEN_NO_HARNESS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// DIRPROOF v1 (laneDIRPROOF, UNPROVEN in Unity). Harness-only directed-input proof driver for Dom's core rule (multi-directional combat).
//   -dirproof:overhead,thrust,slashL,slashR,guardL,guardR,guardUp,guardLow,parry,dodge   [-dirprooflog:<dir>]   (run next to -zones -camscene -camtrace:<dir> so a boss engages Joan; -dirguardproof adds the per-contact [DIRPROOF] lines of CombatCues)
// What it does: after Joan has an opponent, it scripts the SAME fields the keyboard / mouse path ends in (PlayerBrain writes them every frame, this runs after it, order -140, like ZoneCamScene -150):
//   attack  = DuelFighter.PressDirected(<move id>) with the id picked from controls.json attack.map exactly as DirectionalInput.MoveFor does (heavy rows also hold heavyHeld),
//   guard   = df.guardHeld + df.guardSide (side ids from DirectionalGuard.SideIdFor, the ids PlayerBrain / DirectionalInput.GuardStep write),
//   parry   = guard raised when the foe's wind-up has raiseWhenWindupLeftTicks left (parry window = DuelFighter.ParryWindowTicks),
//   dodge   = SetDodgeDir + Press("roll" | "backstep") / PressSidestep, the calls PlayerBrain.DodgeFromKey makes,
//   hitreact = guard down, wait for a hit on Joan.
// It logs one line per move:  [DIRPROOF-DRV] idx=.. move=.. kind=.. id=.. attempt=.. VERDICT=PASS|FAIL|WARN|NOCONTACT .. anim=.. result=.. hitdir=.. react=.. why=..   and writes <dir>\dirproof.csv + dirproof_summary.txt + dirproof_done.txt.
// ALL thresholds and the move rows are data: Assets/Data/dirproof.json (a missing file or enabled:false = the driver does nothing). The class only exists in harness builds and only starts with -dirproof:.
// It never edits a fight number. It writes hp only up to general.hpFloorFrac (a proof run is not a balance run, like camscene.scene.hpFloorFrac).
[DefaultExecutionOrder(-140)]
public sealed class DirProof : MonoBehaviour
{
    [Serializable] class Row
    {
        public string name = "", kind = "attack", dir = "", side = "", press = "", dirName = "", expectKind = "", missIs = "FAIL", poseClipContains = "";
        public bool heavy = false, requireClip = true, needBlockedIsFail = false, needDirection = false, expectDistinctSide = false;
        public string[] expectDirs = new string[0], allowResults = new string[0], reactStates = new string[0];
        public int startWithinTicks = 20, observeTicks = 150, reactDelayTicks = 8, maxAttempts = 2, enterWithinTicks = 20, minContacts = 1, recheckGuardTicks = 6, raiseWhenWindupLeftTicks = 8, holdAfterTicks = 40, stateWithinTicks = 12, reactWithinTicks = 20;
        public float holdS = 16f, minTravelM = 1f, minOppDamage = 0f;
    }
    [Serializable] class General
    {
        public float waitEngageS = 90f, prepTimeoutS = 12f, prepStaminaMinFrac = 0.5f, approachGapM = 2.2f, attackGapM = 2.6f, hpFloorFrac = 0f, maxTotalS = 240f;
        public int clipSampleEveryFrames = 1, settleTicksBetweenMoves = 45;
        public string[] badClips = new string[0];
        public bool writeCsv = true, quitWhenDone = true;
        public string csvName = "dirproof.csv", summaryName = "dirproof_summary.txt", doneName = "dirproof_done.txt", defaultOutDir = "";
    }
    [Serializable] class Cfg { public bool enabled = false; public General general = new General(); public Row[] rows = new Row[0]; }

    struct Ct { public int tick; public string type, moveId, moveDir, swingDir, hitDir, dside, actor, target; public bool byMe, onMe, covers, inArc, guardUp; public float ang, arcMax; }

    static readonly CultureInfo IC = CultureInfo.InvariantCulture;
    static string Arg(string prefix) { foreach (var x in Environment.GetCommandLineArgs()) if (x.StartsWith(prefix, StringComparison.Ordinal)) return x.Substring(prefix.Length); return null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string list = Arg("-dirproof:"); if (string.IsNullOrEmpty(list)) return;
        var go = new GameObject("DirProof"); DontDestroyOnLoad(go); var d = go.AddComponent<DirProof>(); d.list = list;
    }

    string list, outDir; Cfg C; General G; DuelFighter me; Fighter meFx;
    readonly List<Ct> cts = new List<Ct>();
    readonly List<string> csv = new List<string>();
    MoveDef myStarted; bool myMissed; string lastDodgeKind = ""; Vector3 lastDodgeDir; int dodgeSeen;
    MoveDef foeStarted; int foeStartTick = -9999;
    bool drive, wantGuard, holdHeavy; int wantSide; Vector3 wantMove; bool wantMoveOn; bool guardSnap; int guardSideSnap;
    int pass, fail, warn, nocontact, idx; float tStart;

    DuelFighter Me() { if (me == null || me.Dead) { me = GameLoop.Player; meFx = me != null ? me.GetComponent<Fighter>() : null; } return me; }
    string Clip() { return meFx != null && meFx.Current != null ? meFx.Current : ""; }
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }
    static bool In(string[] a, string v) { if (a == null) return false; for (int i = 0; i < a.Length; i++) if (a[i] == v) return true; return false; }

    void OnEnable() { ContactRelay.OnEvent += OnEvent; ContactRelay.OnAttackStarted += OnStarted; ContactRelay.OnAttackMissed += OnMissed; ContactRelay.OnDodge += OnDodge; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; ContactRelay.OnAttackStarted -= OnStarted; ContactRelay.OnAttackMissed -= OnMissed; ContactRelay.OnDodge -= OnDodge; }

    void OnStarted(DuelFighter f, MoveDef m)
    {
        if (f == null) return;
        if (f == me) myStarted = m; else if (me != null && f == me.opponent) { foeStarted = m; foeStartTick = Duel.TickCount; }
    }
    void OnMissed(DuelFighter f, MoveDef m) { if (f != null && f == me) myMissed = true; }
    void OnDodge(DuelFighter f, string kind, Vector3 dir) { if (f != null && f == me) { lastDodgeKind = kind; lastDodgeDir = dir; dodgeSeen++; } }

    void OnEvent(CombatEvent e)
    {
        if (me == null || e.actor == null || e.target == null) return;
        bool byMe = e.actor == me, onMe = e.target == me; if (!byMe && !onMe) return;
        if (Duel.RangedNow) return;
        var m = e.actor.CurrentMove; var c = new Ct { tick = Duel.TickCount, type = e.type, moveId = m != null ? m.id : (e.moveId ?? ""), moveDir = m != null ? m.direction : "", swingDir = e.direction ?? "", hitDir = e.hitDirection ?? "", actor = e.actor.name, target = e.target.name, byMe = byMe, onMe = onMe };
        c.dside = DirectionalGuard.NameOf(onMe ? guardSideSnap : e.target.guardSide); c.guardUp = onMe ? guardSnap : e.target.IsGuarding;
        if (onMe && m != null)
        {
            Vector3 ad = me.transform.position - e.actor.transform.position; ad.y = 0f;
            c.ang = Vector3.Angle(me.transform.forward, -ad);
            c.arcMax = e.actor.Rule("guardArc", Mathf.PI / 3f) * Mathf.Rad2Deg * StanceLayer.GuardArcMul(me);   // the cone Duel.Resolve and CombatCues use
            c.inArc = c.ang <= c.arcMax;
            bool heavy = m.charges || m.damage >= e.actor.HeavyDamage;
            c.covers = DirectionalGuard.SideOk(e.actor, me, m, heavy);
        }
        cts.Add(c);
    }

    void LateUpdate() { var m = Me(); if (m != null) { guardSnap = m.IsGuarding; guardSideSnap = m.guardSide; } }   // the state one frame BEFORE a contact (Duel.Resolve staggers the victim first)

    void Update()
    {
        var m = Me(); if (!drive || m == null || m.Dead || Duel.Frozen) return;
        if (wantMoveOn) { m.moveDir = wantMove; m.run = false; }
        m.guardHeld = wantGuard; if (wantGuard) m.guardSide = wantSide;
        if (holdHeavy) m.heavyHeld = true;
        float fl = G != null ? G.hpFloorFrac : 0f; if (fl > 0f && m.maxHealth > 0f && m.health < m.maxHealth * fl) { m.health = m.maxHealth * fl; var h = m.GetComponent<Health>(); if (h != null) h.current = m.health; }
    }

    IEnumerator Ticks(int n) { int t0 = Duel.TickCount; float r0 = Time.realtimeSinceStartup; while (Duel.TickCount - t0 < n && Time.realtimeSinceStartup - r0 < n / 20f + 2f) yield return null; }

    IEnumerator Start()
    {
        yield return null; tStart = Time.realtimeSinceStartup;
        outDir = Arg("-dirprooflog:") ?? "";
        try { string t = DataPath.ReadText("dirproof.json"); C = string.IsNullOrEmpty(t) ? null : JsonUtility.FromJson<Cfg>(t); } catch (Exception ex) { Debug.LogWarning("[DIRPROOF-DRV] dirproof.json unreadable: " + ex.Message); C = null; }
        if (C == null || !C.enabled) { Debug.LogWarning("[DIRPROOF-DRV] no dirproof.json or enabled:false: nothing to do"); yield break; }
        G = C.general; if (outDir.Length == 0) outDir = G.defaultOutDir.Length > 0 ? G.defaultOutDir : Path.Combine(Application.persistentDataPath, "dirproof");
        try { Directory.CreateDirectory(outDir); } catch (Exception) { }
        csv.Add("idx,move,kind,moveId,attempt,verdict,state_seq,anim_clip,anim_expected_ok,move_dir,expect_dir,result,hit_dir,swing_dir,guard_side_set,guard_side_seen,covers,contacts,foe_react,opp_dhp,travel_m,dodge_kind,ticks,why");
        // wait for an engaged opponent (camscene walks Joan into the boss)
        float w0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - w0 < G.waitEngageS && (Me() == null || me.opponent == null || me.opponent.Dead)) yield return null;
        if (Me() == null || me.opponent == null || me.opponent.Dead) { Note("no engaged opponent after " + G.waitEngageS.ToString("F0", IC) + " s: nothing was proven"); Finish("no opponent"); yield break; }
        drive = true; Note("start: player " + me.name + " vs " + me.opponent.name + ", list " + list);
        foreach (var raw in list.Split(','))
        {
            string nm = raw.Trim(); if (nm.Length == 0) continue;
            if (Time.realtimeSinceStartup - tStart > G.maxTotalS) { AddFail(nm, "?", "total time cap " + G.maxTotalS.ToString("F0", IC) + " s reached before this move", "FAIL"); continue; }
            Row r = null; foreach (var x in C.rows) if (x != null && x.name == nm) { r = x; break; }
            if (r == null) { AddFail(nm, "?", "no row named " + nm + " in dirproof.json (unknown move: a gap in the driver, not a pass)", "FAIL"); continue; }
            if (Me() == null || me.opponent == null || me.opponent.Dead) { AddFail(nm, r.kind, "foe dead or lost before this move", "FAIL"); continue; }
            idx++; wantGuard = false; holdHeavy = false; wantMoveOn = false;
            switch (r.kind)
            {
                case "attack": yield return Attack(r); break;
                case "guard": yield return Guard(r); break;
                case "parry": yield return Parry(r); break;
                case "dodge": yield return Dodge(r); break;
                case "hitreact": yield return HitReact(r); break;
                default: AddFail(nm, r.kind, "unknown kind " + r.kind, "FAIL"); break;
            }
            wantGuard = false; holdHeavy = false; if (Me() != null) me.heavyHeld = false;
            yield return Ticks(G.settleTicksBetweenMoves);
        }
        Finish("ok");
    }

    // ---- shared pieces
    bool Ready() { var m = Me(); return m != null && m.state == FState.Loco && !m.Exhausted && (m.maxStamina <= 0f || m.stamina / m.maxStamina >= G.prepStaminaMinFrac); }
    IEnumerator Prep(string nm)
    {
        float t0 = Time.realtimeSinceStartup; wantGuard = false;
        while (Time.realtimeSinceStartup - t0 < G.prepTimeoutS)
        {
            var m = Me(); var o = m != null ? m.opponent : null; if (m == null || o == null || o.Dead) break;
            Vector3 to = o.transform.position - m.transform.position; to.y = 0f; float d = to.magnitude;
            wantMoveOn = true; wantMove = d > G.approachGapM ? to / Mathf.Max(0.01f, d) : Vector3.zero;
            if (Ready() && d <= G.attackGapM) { wantMove = Vector3.zero; wantMoveOn = false; yield break; }
            yield return null;
        }
        wantMoveOn = false; Note(nm + ": not ready after " + G.prepTimeoutS.ToString("F0", IC) + " s (state " + (me != null ? me.state.ToString() : "none") + ")");
    }
    string MoveFor(Row r)   // the same pick as DirectionalInput.MoveFor
    {
        var M = ControlsTable.Get().attack.map;
        for (int i = 0; i < M.Length; i++) if (M[i] != null && M[i].dir == r.dir) { string id = me.FirstMove(r.heavy ? M[i].heavy : M[i].light); if (id != null) return id; break; }
        return r.heavy ? me.FirstMove(new[] { "heavy_overhead" }) : null;
    }
    static string Seq(StringBuilder sb, FState s) { char c = s.ToString()[0]; if (sb.Length == 0 || sb[sb.Length - 1] != c) sb.Append(c); return sb.ToString(); }
    bool ClipOk(string clip) { return !string.IsNullOrEmpty(clip) && !In(G.badClips, clip); }

    // ---- ATTACK
    IEnumerator Attack(Row r)
    {
        string verdict = "FAIL", why = "";
        for (int at = 1; at <= Mathf.Max(1, r.maxAttempts); at++)
        {
            yield return Prep(r.name); if (Me() == null) break;
            string id = MoveFor(r);
            if (id == null) { Add(r, "", at, "FAIL", "", "", false, "", string.Join("|", r.expectDirs), "", "", "", 0, "", false, 0, "", 0f, 0f, "", 0, "no move in the MoveTable for attack.map dir " + r.dir + (r.heavy ? " heavy" : " light") + " (animation / move gap)"); yield break; }
            var o = me.opponent; float hp0 = o != null ? o.health : 0f; cts.Clear(); myStarted = null; myMissed = false;
            int t0 = Duel.TickCount; float r0 = Time.realtimeSinceStartup; var seq = new StringBuilder(); string clipAct = "", clipAny = "", react = "", actualDir = ""; Ct hit = default(Ct); bool gotHit = false; int hitTick = 0;
            holdHeavy = r.heavy; me.PressDirected(id);
            while (Duel.TickCount - t0 < r.observeTicks && Time.realtimeSinceStartup - r0 < r.observeTicks / 30f + 3f)
            {
                var m = Me(); if (m == null) break; Seq(seq, m.state);
                string cl = Clip(); if (m.state == FState.Windup || m.state == FState.Active) { if (clipAny.Length == 0 || !ClipOk(clipAny)) clipAny = cl; }
                if (m.state == FState.Active && clipAct.Length == 0) clipAct = cl;
                if (holdHeavy && m.state != FState.Windup && myStarted != null) holdHeavy = false;
                for (int i = 0; i < cts.Count; i++) if (cts[i].byMe && !gotHit && cts[i].tick >= t0) { hit = cts[i]; gotHit = true; hitTick = Duel.TickCount; }
                if (gotHit && Duel.TickCount - hitTick >= r.reactDelayTicks) break;
                if (myMissed) break;
                if (myStarted != null && m.state == FState.Loco && Duel.TickCount - t0 > r.startWithinTicks) break;
                yield return null;
            }
            holdHeavy = false; me.heavyHeld = false;
            if (gotHit && o != null) react = o.state.ToString();
            string clip = clipAct.Length > 0 ? clipAct : clipAny; if (myStarted != null) actualDir = myStarted.direction ?? "";
            var rs = new List<string>(); verdict = "PASS";
            if (myStarted == null) { verdict = "FAIL"; rs.Add("move " + id + " never started within " + r.observeTicks + " ticks (state seq " + seq + ")"); }
            else
            {
                if (myStarted.id != id) { verdict = "FAIL"; rs.Add("started " + myStarted.id + " instead of " + id); }
                if (r.expectDirs.Length > 0 && !In(r.expectDirs, actualDir)) { verdict = "FAIL"; rs.Add("direction " + actualDir + " not in expected " + string.Join("|", r.expectDirs)); }
                if (r.requireClip && !ClipOk(clip)) { verdict = "FAIL"; rs.Add("no attack animation seen (clip '" + clip + "')"); }
                if (gotHit)
                {
                    if (r.allowResults.Length > 0 && !In(r.allowResults, hit.type)) { verdict = "FAIL"; rs.Add("result " + hit.type + " not in allowed " + string.Join("|", r.allowResults)); }
                    if (r.reactStates.Length > 0 && hit.type == "Hit" && !In(r.reactStates, react)) { if (verdict == "PASS") verdict = "WARN"; rs.Add("foe state " + react + " after the hit (poise may absorb)"); }
                    if (r.minOppDamage > 0f && o != null && hp0 - o.health < r.minOppDamage && hit.type == "Hit") { verdict = "FAIL"; rs.Add("foe lost " + (hp0 - o.health).ToString("F1", IC) + " hp, min " + r.minOppDamage.ToString("F1", IC)); }
                }
                else if (verdict == "PASS")
                {
                    verdict = r.missIs == "WARN" ? "WARN" : r.missIs == "PASS" ? "PASS" : "FAIL"; rs.Add(myMissed ? "swing missed (foe out of reach / dodged)" : "no contact event in " + r.observeTicks + " ticks");
                }
            }
            why = string.Join("; ", rs);
            Add(r, myStarted != null ? myStarted.id : id, at, verdict, seq.ToString(), clip, ClipOk(clip), actualDir, string.Join("|", r.expectDirs), gotHit ? hit.type : (myMissed ? "Missed" : "none"), gotHit ? hit.hitDir : "", gotHit ? hit.swingDir : "", 0, "", false, gotHit ? 1 : 0, react, o != null ? o.health - hp0 : 0f, 0f, "", Duel.TickCount - t0, why);
            if (verdict == "PASS") yield break;
            yield return Ticks(G.settleTicksBetweenMoves);
        }
    }

    // ---- GUARD
    IEnumerator Guard(Row r)
    {
        int sideId = DirectionalGuard.SideIdFor(r.side);
        yield return Prep(r.name); if (Me() == null) yield break;
        cts.Clear(); int t0 = Duel.TickCount; float r0 = Time.realtimeSinceStartup; var seq = new StringBuilder(); string clip = ""; bool entered = false; int judged = 0, bad = 0, okc = 0, ign = 0; var rs = new List<string>(); string results = "", lastHit = "";
        wantSide = sideId; wantGuard = true; int seen = 0; bool distinctOk = !r.expectDistinctSide || (sideId != 0);
        if (!distinctOk) rs.Add("side '" + r.side + "' has no guard side id of its own: it maps to centre (id 0). guard.sides has only top / left / right (gap)");
        while (Time.realtimeSinceStartup - r0 < r.holdS)
        {
            var m = Me(); if (m == null || m.opponent == null || m.opponent.Dead) break; Seq(seq, m.state);
            if (m.state == FState.Guard) { entered = true; string cl = Clip(); if (cl.Length > 0) clip = cl; }
            if (!entered && Duel.TickCount - t0 > r.enterWithinTicks + 120 && m.state != FState.Loco) { }   // still busy: the loop keeps waiting
            for (; seen < cts.Count; seen++)
            {
                var c = cts[seen]; if (!c.onMe) continue;
                if (!c.guardUp || !c.inArc) { ign++; continue; }
                bool covered = c.type == "Blocked" || c.type == "Parried"; bool through = c.type == "Hit" || c.type == "GuardBroken";
                bool ok = c.covers ? covered : through;
                judged++; results += (results.Length > 0 ? "|" : "") + c.type + (c.covers ? "+" : "-"); lastHit = c.hitDir;
                if (ok) okc++; else { bad++; rs.Add("move " + c.moveId + " dir " + c.moveDir + " vs guard " + c.dside + ": covers=" + (c.covers ? 1 : 0) + " but ev=" + c.type); }
            }
            if (judged >= r.minContacts) break;
            yield return null;
        }
        wantGuard = false;
        string verdict;
        if (!distinctOk) verdict = "FAIL";
        else if (!entered) { verdict = "FAIL"; rs.Add("guard state never reached (state seq " + seq + ")"); }
        else if (bad > 0) verdict = "FAIL";
        else if (judged == 0) { verdict = "NOCONTACT"; rs.Add("no foe contact inside the guard arc in " + r.holdS.ToString("F0", IC) + " s: nothing proven for this side"); }
        else verdict = "PASS";
        if (verdict == "PASS" && r.poseClipContains.Length > 0 && clip.IndexOf(r.poseClipContains, StringComparison.OrdinalIgnoreCase) < 0) { verdict = "WARN"; rs.Add("guard clip '" + clip + "' does not contain '" + r.poseClipContains + "'"); }
        if (ign > 0) rs.Add(ign + " contact(s) ignored (guard not up a frame before, or outside the arc)");
        Add(r, "", 1, verdict, seq.ToString(), clip, ClipOk(clip), "", "", results, lastHit, "", sideId, DirectionalGuard.NameOf(sideId), false, judged, "", 0f, 0f, "", Duel.TickCount - t0, string.Join("; ", rs));
    }

    // ---- PARRY
    IEnumerator Parry(Row r)
    {
        yield return Prep(r.name); if (Me() == null) yield break;
        float r0 = Time.realtimeSinceStartup; int t0 = Duel.TickCount; var seq = new StringBuilder(); string verdict = "NOCONTACT"; var rs = new List<string>(); string res = "", hd = ""; int attempts = 0; string clip = ""; int win = me.ParryWindowTicks();
        wantGuard = false;
        while (Time.realtimeSinceStartup - r0 < r.holdS && attempts < Mathf.Max(1, r.maxAttempts))
        {
            var m = Me(); var o = m != null ? m.opponent : null; if (m == null || o == null || o.Dead) break; Seq(seq, m.state);
            if (o.state == FState.Windup && o.CurrentMove != null && o.WindupLeft <= r.raiseWhenWindupLeftTicks && o.WindupLeft >= 0 && Ready())
            {
                attempts++; var fm = o.CurrentMove; int side = DirectionalGuard.BestSideFor(fm); cts.Clear(); int a0 = Duel.TickCount;
                wantSide = side; wantGuard = true; bool got = false; Ct c = default(Ct);
                while (Duel.TickCount - a0 < r.holdAfterTicks)
                {
                    if (me.state == FState.Guard || me.state == FState.ParryRecover) { string cl = Clip(); if (cl.Length > 0) clip = cl; }
                    for (int i = 0; i < cts.Count; i++) if (cts[i].onMe && !got) { c = cts[i]; got = true; }
                    if (got) break; yield return null;
                }
                wantGuard = false;
                if (!got) { rs.Add("attempt " + attempts + ": foe move " + fm.id + " made no contact in " + r.holdAfterTicks + " ticks"); yield return Ticks(30); continue; }
                res += (res.Length > 0 ? "|" : "") + c.type; hd = c.hitDir;
                if (In(r.allowResults, c.type)) { verdict = "PASS"; rs.Add("attempt " + attempts + ": " + c.type + " on foe move " + fm.id + " (window " + win + " ticks, raised at windup-left <= " + r.raiseWhenWindupLeftTicks + ")"); break; }
                verdict = "FAIL"; rs.Add("attempt " + attempts + ": " + c.type + " on foe move " + fm.id + " (needs " + string.Join("|", r.allowResults) + (c.type == "Blocked" && r.needBlockedIsFail ? "; a block is not a parry: window " + win + " ticks missed or raised too early" : "") + ")");
                yield return Ticks(45); continue;
            }
            yield return null;
        }
        wantGuard = false;
        if (attempts == 0) rs.Add("foe never reached a wind-up with <= " + r.raiseWhenWindupLeftTicks + " ticks left in " + r.holdS.ToString("F0", IC) + " s");
        Add(r, "", Mathf.Max(1, attempts), verdict, seq.ToString(), clip, ClipOk(clip), "", "", res, hd, "", 0, "", false, attempts, "", 0f, 0f, "", Duel.TickCount - t0, string.Join("; ", rs));
    }

    // ---- DODGE
    IEnumerator Dodge(Row r)
    {
        string verdict = "FAIL", why = "";
        for (int at = 1; at <= Mathf.Max(1, r.maxAttempts); at++)
        {
            yield return Prep(r.name); if (Me() == null) break;
            Vector3 p0 = me.transform.position; int t0 = Duel.TickCount; float r0 = Time.realtimeSinceStartup; var seq = new StringBuilder(); bool rolled = false; float maxD = 0f; int seen0 = dodgeSeen; string clip = "";
            me.SetDodgeDir(r.dirName);
            if (r.press == "backstep") me.Press("backstep");
            else if (r.press == "sidestepL") me.PressSidestep(-1);
            else if (r.press == "sidestepR") me.PressSidestep(1);
            else me.Press("roll");
            while (Duel.TickCount - t0 < r.observeTicks && Time.realtimeSinceStartup - r0 < r.observeTicks / 30f + 2f)
            {
                var m = Me(); if (m == null) break; Seq(seq, m.state);
                if (m.state == FState.Roll) { rolled = true; string cl = Clip(); if (cl.Length > 0) clip = cl; }
                maxD = Mathf.Max(maxD, Flat(m.transform.position, p0));
                if (rolled && m.state != FState.Roll && Duel.TickCount - t0 > r.stateWithinTicks) break;
                yield return null;
            }
            var rs = new List<string>(); verdict = "PASS"; string dk = dodgeSeen > seen0 ? lastDodgeKind : "";
            if (!rolled) { verdict = "FAIL"; rs.Add("never entered the Roll state (state seq " + seq + ")"); }
            if (r.expectKind.Length > 0 && dk != r.expectKind) { verdict = "FAIL"; rs.Add("dodge kind '" + dk + "' expected '" + r.expectKind + "'"); }
            if (maxD < r.minTravelM) { verdict = "FAIL"; rs.Add("travelled " + maxD.ToString("F2", IC) + " m, min " + r.minTravelM.ToString("F2", IC)); }
            if (rolled && !ClipOk(clip)) { if (verdict == "PASS") verdict = "WARN"; rs.Add("no dodge animation clip name seen"); }
            why = string.Join("; ", rs);
            Add(r, r.press, at, verdict, seq.ToString(), clip, ClipOk(clip), "", "", "", "", "", 0, "", false, 0, "", 0f, maxD, dk, Duel.TickCount - t0, why);
            if (verdict == "PASS") yield break;
            yield return Ticks(G.settleTicksBetweenMoves);
        }
    }

    // ---- HITREACT (passive: Joan does nothing and takes a hit)
    IEnumerator HitReact(Row r)
    {
        yield return Prep(r.name); if (Me() == null) yield break;
        wantGuard = false; cts.Clear(); int t0 = Duel.TickCount; float r0 = Time.realtimeSinceStartup; var seq = new StringBuilder(); Ct hit = default(Ct); bool got = false; string react = "", clip = "";
        while (Time.realtimeSinceStartup - r0 < r.holdS)
        {
            var m = Me(); if (m == null) break; Seq(seq, m.state);
            for (int i = 0; i < cts.Count; i++) if (cts[i].onMe && cts[i].type == "Hit" && !got) { hit = cts[i]; got = true; }
            if (got) break; yield return null;
        }
        if (got)
        {
            int h0 = Duel.TickCount;
            while (Duel.TickCount - h0 < r.reactWithinTicks) { var m = Me(); if (m == null) break; Seq(seq, m.state); if (In(r.reactStates, m.state.ToString())) { react = m.state.ToString(); string cl = Clip(); if (cl.Length > 0) clip = cl; break; } yield return null; }
        }
        var rs = new List<string>(); string verdict = "PASS";
        if (!got) { verdict = "NOCONTACT"; rs.Add("Joan was not hit in " + r.holdS.ToString("F0", IC) + " s"); }
        else
        {
            if (react.Length == 0) { verdict = "FAIL"; rs.Add("no reaction state (" + string.Join("|", r.reactStates) + ") within " + r.reactWithinTicks + " ticks of a Hit (state seq " + seq + ")"); }
            if (r.needDirection && string.IsNullOrEmpty(hit.hitDir)) { verdict = "FAIL"; rs.Add("the hit event carries no hitDirection"); }
        }
        Add(r, hit.moveId ?? "", 1, verdict, seq.ToString(), clip, ClipOk(clip), "", "", got ? hit.type : "none", got ? hit.hitDir : "", got ? hit.swingDir : "", 0, "", false, got ? 1 : 0, react, 0f, 0f, "", Duel.TickCount - t0, string.Join("; ", rs));
    }

    // ---- output
    void AddFail(string nm, string kind, string why, string verdict)
    {
        idx++; var r = new Row { name = nm, kind = kind };
        Add(r, "", 0, verdict, "", "", false, "", "", "", "", "", 0, "", false, 0, "", 0f, 0f, "", 0, why);
    }
    static string Q(string s) { s = (s ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " "); return "\"" + s + "\""; }
    void Add(Row r, string moveId, int attempt, string verdict, string seq, string clip, bool clipOk, string moveDir, string expDir, string result, string hitDir, string swingDir, int sideSet, string sideSeen, bool covers, int contacts, string react, float oppDhp, float travel, string dodgeKind, int ticks, string why)
    {
        if (verdict == "PASS") pass++; else if (verdict == "FAIL") fail++; else if (verdict == "WARN") warn++; else nocontact++;
        string line = "[DIRPROOF-DRV] idx=" + idx + " move=" + r.name + " kind=" + r.kind + " id=" + moveId + " attempt=" + attempt + " VERDICT=" + verdict + " state=" + seq + " anim=" + clip + " mdir=" + moveDir + " result=" + result + " hitdir=" + hitDir + " swing=" + swingDir + " gside=" + sideSeen + " contacts=" + contacts + " react=" + react + " dhp=" + oppDhp.ToString("F1", IC) + " travel=" + travel.ToString("F2", IC) + " dodge=" + dodgeKind + " ticks=" + ticks + " why=" + why;
        Debug.Log(line);
        csv.Add(string.Join(",", new[] { idx.ToString(IC), r.name, r.kind, moveId, attempt.ToString(IC), verdict, seq, Q(clip), clipOk ? "1" : "0", moveDir, expDir, result, hitDir, swingDir, sideSet.ToString(IC), sideSeen, covers ? "1" : "0", contacts.ToString(IC), react, oppDhp.ToString("F1", IC), travel.ToString("F2", IC), dodgeKind, ticks.ToString(IC), Q(why) }));
        FlushCsv();
    }
    void Note(string s) { Debug.Log("[DIRPROOF-DRV] " + s); }
    void FlushCsv() { if (G == null || !G.writeCsv) return; try { File.WriteAllText(Path.Combine(outDir, G.csvName), string.Join("\n", csv.ToArray()) + "\n"); } catch (Exception) { } }
    void Finish(string why)
    {
        drive = false; wantGuard = false; FlushCsv();
        string sum = "DIRPROOF " + (fail == 0 && pass > 0 ? "PASS" : "FAIL") + " pass=" + pass + " fail=" + fail + " warn=" + warn + " nocontact=" + nocontact + " (" + why + ", list " + list + ")";
        Debug.Log("[DIRPROOF-DRV] " + sum);
        try { if (G != null) { File.WriteAllText(Path.Combine(outDir, G.summaryName), sum + "\n"); File.WriteAllText(Path.Combine(outDir, G.doneName), why + "\n" + sum + "\n"); } } catch (Exception) { }
        if (G != null && G.quitWhenDone) Application.Quit(fail == 0 && pass > 0 ? 0 : 1);
    }
}
#endif
