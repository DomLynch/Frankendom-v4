using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

// CAMZONE v1 (laneCAMZONE, UNPROVEN in Unity): -camscene (needs -zones and -camtrace:<dir>). A hands-off scripted Joan for the owner's case: walk past a live Pitborn into Sobek's zone so both engage, fight,
// run away, come back, fight. It writes df.moveDir / df.run (after PlayerBrain, same inputs the keys give) and df.Press("light"); no key is sent, no window focus is needed. The steps are DATA (camtrace.json scene.steps):
//  ready | restart (revive spawns, player spot, GameLoop.Retry as the death path does) | walk (zone+spot or pos [x,z]; untilEngaged N) | fight (seconds) | flee (to a spot, seconds) | hold (seconds).
// Joan's health is put back to scene.hpFloorFrac when it drops below (so the run is not ended by dying; this is a camera test, not a balance test). Every step id goes into the csv "step" column.
// Quits by itself at the end (scene.quit) after writing <dir>\camscene_done.txt. Without the flag nothing exists.
[DefaultExecutionOrder(-150)]
public sealed class ZoneCamScene : MonoBehaviour
{
    static string Arg(string a) { foreach (var x in Environment.GetCommandLineArgs()) if (x == a) return a; return null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Arg("-camscene") == null) return;
        var go = new GameObject("ZoneCamScene"); DontDestroyOnLoad(go); go.AddComponent<ZoneCamScene>();
    }

    CtCfg C; DuelFighter me; Vector3 wantDir; bool wantRun, drive; int assists; string log = "";
    static bool evtOn; static int reviveSeen, fallbackSeen, pulse; bool noFloor, stepDone; int stuck; Coroutine stepCo; float nextGate; readonly Dictionary<string, bool> gateSeen = new Dictionary<string, bool>();   // CODE5 v1
    DuelFighter Me() { if (me == null || me.Dead) { me = GameLoop.Player; } return me; }
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }

    IEnumerator Start()
    {
        yield return null;
        if (!ZoneRuntime.Owns && !ZoneSave.Active) { Debug.LogWarning("[CamScene] zones are off (needs -zones, no harness flag): nothing to do"); Done("zones off", false); yield break; }
        if (!ZoneCamTrace.Running) { Debug.LogWarning("[CamScene] needs -camtrace:<dir> next to -camscene"); Done("no -camtrace", false); yield break; }
        C = ZoneCamTrace.Cfg; var S = C.scene; if (!S.enabled) { Done("scene.enabled=false", false); yield break; }
        HookEvents(S);
        if (S.startZone.Length > 0) { yield return WaitReady(S); yield return StartCoroutine(Restart(S, new CtStep { id = "start", zone = S.startZone, spot = S.startSpot })); }   // CODE5 v1: begin at a zone spot
        float tAll = Time.realtimeSinceStartup; bool ok = true;
        foreach (var st in S.steps)
        {
            if (st == null) continue; if (Time.realtimeSinceStartup - tAll > S.globalTimeoutS) { Note("global timeout, stopping"); ok = false; break; }
            ZoneCamTrace.Step = st.id; Note("step " + st.id + " (" + st.kind + ")"); float t0 = Time.realtimeSinceStartup;
            if (st.startZone.Length > 0) yield return StartCoroutine(Restart(S, new CtStep { id = st.id, zone = st.startZone, spot = st.startSpot }));   // CODE5 v1
            if (st.killAfter > 0f && st.boss.Length > 0) StartCoroutine(KillBoss(st));   // CODE5 v1
            IEnumerator it = null;
            switch (st.kind)
            {
                case "ready": it = Ready(S); break;
                case "restart": it = Restart(S, st); break;
                case "walk": it = Walk(S, st, false); break;
                case "flee": it = Walk(S, st, true); break;
                case "fight": it = Fight(S, st); break;
                case "hold": it = Hold(S, st); break;
                case "forceDeath": it = ForceDeath(S, st); break;   // CODE5 v1
                case "hitPose": it = HitPose(S, st); break;   // dev2: play one Animator state on Joan, freeze at the peak, front camera (step.item = state name, step.seconds = hold)
                case "trade": it = Trade(S, st); break; case "equip": it = EquipStep(S, st); break; case "pickup": it = Pickup(S, st); break; case "inv": Inv(st.id); break;   // CLOSE5B v1
                default: Note("unknown kind " + st.kind); ok = false; break;
            }
            if (it != null)
            {
                stepDone = false; stepCo = StartCoroutine(RunStep(it)); float last = Time.realtimeSinceStartup; var m0 = Me(); Vector3 lp = m0 != null ? m0.transform.position : Vector3.zero; int lpulse = pulse, leng = ZoneCamTrace.EngagedNow;   // CODE5 v1: STUCK watchdog
                bool exempt = st.kind == "hold" || st.kind == "fight" || st.kind == "forceDeath";
                while (!stepDone)
                {
                    yield return null; var m = Me(); float now = Time.realtimeSinceStartup;
                    if (m != null && Flat(m.transform.position, lp) > 0.5f) { lp = m.transform.position; last = now; }
                    if (pulse != lpulse || ZoneCamTrace.EngagedNow != leng) { lpulse = pulse; leng = ZoneCamTrace.EngagedNow; last = now; }
                    if (!exempt && S.stuckS > 0f && now - last > S.stuckS)
                    {
                        Debug.LogWarning("[CamScene-STUCK] step " + st.id + ", zone " + ZoneCamTrace.Zone() + ", pos " + (m != null ? m.transform.position.ToString("F1") : "none") + ", no progress for " + S.stuckS.ToString("F0") + " s, forcing the next step");
                        stuck++; ok = false; if (stepCo != null) StopCoroutine(stepCo); wantDir = Vector3.zero; wantRun = false; noFloor = false; break;
                    }
                }
            }
            Note("step " + st.id + " took " + (Time.realtimeSinceStartup - t0).ToString("F1") + " s");
        }
        drive = false; ZoneCamTrace.Step = "done"; yield return new WaitForSeconds(1f); Done("ok", ok);
    }

    void Note(string s) { log += s + "\n"; Debug.Log("[CamScene] " + s); }
    void Done(string why, bool ok)
    {
        drive = false; try { var d = Environment.GetCommandLineArgs(); string dir = ""; foreach (var a in d) if (a.StartsWith("-camtrace:", StringComparison.Ordinal)) dir = a.Substring(10); if (dir.Length > 0) File.WriteAllText(Path.Combine(dir, "camscene_done.txt"), (ok ? "ok" : "problem") + ": " + why + ", hp assists " + assists + ", stuck " + stuck + "\n" + log); } catch (Exception) { }
        ZoneCamTrace.Finish(); if (C == null || C.scene.quit) Application.Quit(ok ? 0 : 1);
    }

    void Update()
    {
        GatePoll();   // CODE5 v1
        var m = Me(); if (!drive || m == null || m.Dead || Duel.Frozen) return;
        m.moveDir = wantDir; m.run = wantRun;
        float fl = C.scene.hpFloorFrac; if (fl > 0f && !noFloor && m.maxHealth > 0f && m.health < m.maxHealth * fl) { m.health = m.maxHealth * fl; var h = m.GetComponent<Health>(); if (h != null) h.current = m.health; assists++; }
    }

    IEnumerator WaitReady(CtScene S)
    {
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < S.startWaitS && (!ZoneRuntime.Owns || Me() == null || ZoneGroundHold.Active || Camera.main == null || GameLoopDriver.Transitioning)) yield return null;
        for (int i = 0; i < S.readyExtraFrames; i++) yield return null;
    }
    IEnumerator Ready(CtScene S) { yield return WaitReady(S); Note("ready: player at " + (Me() != null ? Me().transform.position.ToString("F1") : "none")); }

    IEnumerator Restart(CtScene S, CtStep st)
    {
        var old = Me(); long now = ZoneCore.NowMs();
        foreach (var id in st.revive)
        {
            ZZone z; var row = ZoneData.FindSpawn(id, out z); if (row == null) { Note("no spawn " + id); continue; }
            var prev = ZoneSave.Rec(z.id, row.id);
            ZoneSave.Put(new SpawnRec { zone = z.id, spawn = row.id, creature = prev != null ? prev.creature : "", generation = prev != null ? prev.generation : 0, killedAtMs = 0, respawnAtMs = 0 });
        }
        ZoneSave.SetPlayer(st.zone, st.spot, 1f); ZoneSave.Flush(true); drive = false; GameLoop.Retry();
        float t0 = Time.realtimeSinceStartup; while (old != null && Time.realtimeSinceStartup - t0 < 45f) yield return null;
        me = null; yield return WaitReady(S); Note("restarted: player at " + (Me() != null ? Me().transform.position.ToString("F1") : "none"));
    }

    bool Target(CtStep st, out Vector3 p)
    {
        p = Vector3.zero;
        if (st.zone.Length > 0) { var sp = ZoneData.Spot(ZoneData.Zone(st.zone), st.spot); if (sp != null && sp.pos != null && sp.pos.Length >= 2) { p = new Vector3(sp.pos[0], 0f, sp.pos[1]); return true; } }
        if (st.pos != null && st.pos.Length >= 2) { p = new Vector3(st.pos[0], 0f, st.pos[1]); return true; }
        return false;
    }

    IEnumerator Walk(CtScene S, CtStep st, bool flee)
    {
        Vector3 wp; if (!Target(st, out wp)) { Note("no target for " + st.id); yield break; }
        float t0 = Time.realtimeSinceStartup; drive = true; float lim = st.timeoutS > 0f ? st.timeoutS : 60f;
        while (Time.realtimeSinceStartup - t0 < lim)
        {
            var m = Me(); if (m == null) { yield return null; continue; }
            Vector3 to = wp - m.transform.position; to.y = 0f; float d = to.magnitude;
            if (d <= st.arriveM) break; if (st.untilEngaged > 0 && ZoneCamTrace.EngagedNow >= st.untilEngaged) { Note(st.id + ": " + ZoneCamTrace.EngagedNow + " engaged, stop"); break; }
            if (st.seconds > 0f && Time.realtimeSinceStartup - t0 >= st.seconds) break;
            if (st.untilGapM > 0f && ZoneCamTrace.NearestGap >= st.untilGapM) { Note(st.id + ": nearest enemy " + ZoneCamTrace.NearestGap.ToString("F1") + " m away, stop"); break; }
            wantDir = to / Mathf.Max(0.01f, d); wantRun = st.run; yield return null;
        }
        wantDir = Vector3.zero; wantRun = false;
    }

    // dev2 HITPOSE: Joan's sim is switched off, the state is forced on her Animator, played to ~40 percent of the clip, the Animator is frozen and the camera stands 2.4 m in front of her head for st.seconds. Frames come from the camtrace recorder; the log line gives the time window.
    IEnumerator HitPose(CtScene S, CtStep st)
    {
        var m = Me(); var cam = Camera.main; if (m == null || cam == null) { Note("hitPose: no Joan or camera"); yield break; }
        var fx = m.GetComponent<Fighter>(); var an = fx != null ? fx.anim : null; if (an == null) { Note("hitPose: no Animator"); yield break; }
        var rig = cam.GetComponent<CameraRig>(); bool rigWas = rig != null && rig.enabled; if (rig != null) rig.enabled = false;
        bool simWas = m.enabled; m.enabled = false; float fov0 = cam.fieldOfView;
        fx.Force(st.item, 0f); an.speed = 1f; yield return null; yield return null;
        var info = an.GetCurrentAnimatorStateInfo(0); float len = Mathf.Max(0.1f, info.length);
        bool has = an.HasState(0, Animator.StringToHash(st.item));
        float tw = Time.realtimeSinceStartup + len * 0.4f; while (Time.realtimeSinceStartup < tw) { PoseCam(cam, m); yield return null; }
        an.speed = 0f; float t1 = Time.realtimeSinceStartup;
        Debug.Log("[CamScene-POSE] " + st.item + " hasState " + has + " clipLen " + len.ToString("F2") + " freeze at t " + t1.ToString("F2") + " hold " + st.seconds.ToString("F1"));
        while (Time.realtimeSinceStartup < t1 + Mathf.Max(0.5f, st.seconds)) { PoseCam(cam, m); yield return null; }
        an.speed = 1f; m.enabled = simWas; cam.fieldOfView = fov0; if (rig != null) rig.enabled = rigWas;
    }
    void PoseCam(Camera cam, DuelFighter m)
    {
        Vector3 head = m.transform.position + Vector3.up * 1.55f; Vector3 f = m.transform.forward; f.y = 0f; f.Normalize();
        cam.transform.position = head + f * 2.4f + Vector3.up * 0.05f; cam.transform.rotation = Quaternion.LookRotation((head - Vector3.up * 0.25f) - cam.transform.position, Vector3.up); cam.fieldOfView = 38f;
    }

    // dev2 BANDIT-Y LOG (harness): -banditylog:<seconds> logs once per interval, for every fighter whose name has '#bandit_' or '#z2_' etc.: y, ground y under it (ray down), engaged state. Used to find floaters.
    static float byNext, byEvery = -1f;
    void LateUpdate()
    {
        if (byEvery < 0f) { byEvery = 0f; foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-banditylog:", System.StringComparison.Ordinal)) { float v; if (float.TryParse(a.Substring(12), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) byEvery = Mathf.Max(0.2f, v); } }
        if (byEvery <= 0f || Time.realtimeSinceStartup < byNext) return; byNext = Time.realtimeSinceStartup + byEvery;
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (f == null || f.name.IndexOf('#') < 0 || f.IsPlayer) continue; var p = f.transform.position; float gy = float.NaN;
            var hits = Physics.RaycastAll(p + Vector3.up * 0.3f, Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore); float bestD = 1e9f;   // origin just above the feet, own colliders skipped: the first foreign surface below
            foreach (var h in hits) { if (h.collider == null || h.collider.transform.IsChildOf(f.transform)) continue; if (h.distance < bestD) { bestD = h.distance; gy = h.point.y; } }
            Debug.Log("[BanditY] " + f.name + " t " + Time.realtimeSinceStartup.ToString("F0") + " pos " + p.x.ToString("F1") + "," + p.y.ToString("F2") + "," + p.z.ToString("F1") + " ground " + (float.IsNaN(gy) ? "none" : gy.ToString("F2")) + " dy " + (float.IsNaN(gy) ? "n/a" : (p.y - gy).ToString("F2")) + " state " + f.state + " dead " + f.Dead + " active " + f.gameObject.activeInHierarchy);
        }
    }

    IEnumerator Hold(CtScene S, CtStep st) { drive = true; wantDir = Vector3.zero; wantRun = false; yield return new WaitForSeconds(st.seconds); }

    IEnumerator Fight(CtScene S, CtStep st)
    {
        drive = true; float t0 = Time.realtimeSinceStartup, nextHit = 0f;
        float heavyUntil = 0f, nextSp = 0f; string[] cyc = { "light_overhead", "thrust", "light_left", "light_right" }; int thrown = 0, landed = 0, ci = 0; float lastHp = -1f; DuelFighter lastT = null;   // dev2: step.cycle = 4-direction cycling, thrown / landed counters
        while (Time.realtimeSinceStartup - t0 < st.seconds)
        {
            var m = Me(); var t = m != null ? m.opponent : null;
            if (t != lastT) { lastT = t; lastHp = t != null ? t.health : -1f; }
            else if (t != null) { if (t.health < lastHp - 0.01f) landed++; lastHp = t.health; }
            if (m == null || t == null || t.Dead) { wantDir = Vector3.zero; wantRun = false; yield return null; continue; }
            if (st.heavy && m.heavyHeld && Time.realtimeSinceStartup > heavyUntil) m.heavyHeld = false;   // HEAVYSTEP: release the charge after 0.35 s
            Vector3 to = t.transform.position - m.transform.position; to.y = 0f; float d = to.magnitude;
            wantRun = false; wantDir = d > S.approachGapM ? to / Mathf.Max(0.01f, d) : Vector3.zero;
            if (st.special != null && st.special.Length > 0 && d <= S.attackGapM && Time.realtimeSinceStartup >= nextSp) { m.Press(st.special); nextSp = Time.realtimeSinceStartup + Mathf.Max(0.5f, st.specialEveryS); thrown++; nextHit = Time.realtimeSinceStartup + S.strikeIntervalS; }   // SPECIALSTEP
            else if (d <= S.attackGapM && Time.realtimeSinceStartup >= nextHit && (m.maxStamina <= 0f || m.stamina / m.maxStamina >= S.staminaMinFrac)) { if (st.heavy) { m.PressDirected("heavy_overhead"); m.heavyHeld = true; heavyUntil = Time.realtimeSinceStartup + 0.35f; } else if (st.cycle) m.PressDirected(cyc[ci++ % cyc.Length]); else m.Press("light"); thrown++; nextHit = Time.realtimeSinceStartup + S.strikeIntervalS; }
            yield return null;
        }
        Debug.Log("[CamScene] fight " + st.id + ": strikes thrown " + thrown + ", hits landed (foe hp drops) " + landed + (st.cycle ? " (4-direction cycle)" : " (light)"));
        wantDir = Vector3.zero;
    }

    // ---- CODE5 v1: harness-only additions (nothing runs without -camscene; no gameplay value is written except the explicit kill / forceDeath steps) ----
    IEnumerator RunStep(IEnumerator it) { yield return it; stepDone = true; }
    public static void Evt(string s) { if (evtOn) Debug.Log("[CamScene-EVT] " + s); }
    void HookEvents(CtScene S)
    {
        Application.logMessageReceived += OnLog; evtOn = S.evtLog;
        if (!S.evtLog) return;
        GearDrop.OnDropped += EvDrop; GearDrop.OnCollected += EvCollect; GearSlots.OnEquipped += EvEquip; MarketHooks.OnSold += EvTrade;
    }
    void OnDestroy() { Application.logMessageReceived -= OnLog; GearDrop.OnDropped -= EvDrop; GearDrop.OnCollected -= EvCollect; GearSlots.OnEquipped -= EvEquip; MarketHooks.OnSold -= EvTrade; evtOn = false; }
    void OnLog(string msg, string st, LogType t)
    {
        if (msg == null) return;
        if (msg.StartsWith("[Zones] revive: player is up", StringComparison.Ordinal)) reviveSeen++; else if (msg.IndexOf("falling back to the scene reload", StringComparison.Ordinal) >= 0) fallbackSeen++;
        if (msg.StartsWith("[Zones]", StringComparison.Ordinal) || msg.StartsWith("[CamScene", StringComparison.Ordinal) || msg.StartsWith("[Gear]", StringComparison.Ordinal)) pulse++;
    }
    static void EvDrop(GearPickup p, DuelFighter killer) { pulse++; Evt("gear drop spawned " + (p != null ? p.itemId + " from " + p.victimId + " at " + p.transform.position.ToString("F1") : "?") + ", killer " + (killer != null ? killer.name : "-")); }
    static void EvCollect(GearPickup p, GearSlots w) { pulse++; Evt("gear pickup " + (p != null ? p.itemId : "?") + " by " + (w != null ? w.name : "?")); }
    static void EvEquip(GearSlots w, GearTable.Item it, string slot) { pulse++; Evt("equip " + (it != null ? it.id : "?") + " slot " + slot + " on " + (w != null ? w.name : "?")); }
    static void EvTrade(string item, int coins) { pulse++; Evt((coins < 0 ? "buy " : "sell ") + item + " coins " + coins); }   // MarketUI raises OnSold for every ok order: coinsDelta < 0 = a buy
    void GatePoll()
    {
        if (!evtOn || Time.unscaledTime < nextGate) return; nextGate = Time.unscaledTime + 0.5f;
        try
        {
            if (!ZoneSave.Active) return;
            foreach (var z in ZoneData.Get().zones) { if (z == null || z.gates == null) continue; foreach (var g in z.gates) { if (g == null || !g.sticky) continue; bool u = ZoneSave.GateUnlocked(g.id), old; if (!gateSeen.TryGetValue(g.id, out old)) { gateSeen[g.id] = u; continue; } if (u && !old) { gateSeen[g.id] = true; pulse++; Evt("gate opened " + g.id + " (zone " + z.id + ")"); } } }
        }
        catch (Exception) { }
    }

    static DuelFighter FindSpawn(string spawn, DuelFighter notMe) { string key = "#" + spawn; foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f != null && f != notMe && f.name.Contains(key)) return f; return null; }
    // killAfter: wait until the boss fights Joan (opponent == Joan, brain on), then N s later set its hp to 0: the sim's own Duel tick calls Die() at hp <= 0, so ZoneRuntime.KillWatch (kill record, gate unlock), the gear drop and the loot run unchanged.
    IEnumerator KillBoss(CtStep st)
    {
        float t0 = Time.realtimeSinceStartup; DuelFighter foe = null;
        while (Time.realtimeSinceStartup - t0 < st.killWaitS)
        {
            var m = Me(); foe = FindSpawn(st.boss, m); var b = foe != null ? foe.GetComponent<AIBrain>() : null;
            if (m != null && foe != null && !foe.Dead && foe.opponent == m && b != null && b.enabled) break; foe = null; yield return null;
        }
        if (foe == null) { Note("killAfter " + st.id + ": " + st.boss + " never engaged within " + st.killWaitS.ToString("F0") + " s, no kill"); yield break; }
        Note(st.boss + " engaged, killing in " + st.killAfter.ToString("F1") + " s"); yield return new WaitForSeconds(st.killAfter);
        if (foe == null || foe.Dead) yield break;
        if (st.hpLeft > 0f) { Evt("hpLeft " + st.boss + " (hp " + foe.health.ToString("F0") + " -> " + st.hpLeft.ToString("F0") + ")"); foe.health = st.hpLeft; var h0 = foe.GetComponent<Health>(); if (h0 != null) h0.current = st.hpLeft; float tw = Time.realtimeSinceStartup; while (foe != null && !foe.Dead && Time.realtimeSinceStartup - tw < 25f) yield return null; yield break; }   // HPLEFT v1
        Evt("kill " + st.boss + " (hp " + foe.health.ToString("F0") + " -> 0)"); foe.health = 0f; var h = foe.GetComponent<Health>(); if (h != null) h.current = 0f;
        float tk = Time.realtimeSinceStartup; while (foe != null && !foe.Dead && Time.realtimeSinceStartup - tk < 3f) yield return null;
        if (foe != null && !foe.Dead) { Note("sim did not call Die() within 3 s, calling it (same as ZoneRouteBot die step)"); foe.Die(); }
    }
    // forceDeath: Joan's own Die() (what the sim calls at 0 hp, same as ZoneRouteBot DieStep) so RespawnStep, the revive and the '[Zones] revive: player is up' line run; repeated st.count times.
    IEnumerator ForceDeath(CtScene S, CtStep st)
    {
        int n = Mathf.Max(1, st.count);
        for (int i = 0; i < n; i++)
        {
            var m = Me(); float tw = Time.realtimeSinceStartup;
            while ((m == null || m.Dead || Duel.Frozen) && Time.realtimeSinceStartup - tw < S.reviveWaitS) { yield return null; m = Me(); }
            if (m == null || m.Dead) { Note("forceDeath " + (i + 1) + "/" + n + ": no live player"); yield break; }
            int before = reviveSeen, fb = fallbackSeen; noFloor = true; wantDir = Vector3.zero; wantRun = false;
            Evt("forceDeath " + (i + 1) + "/" + n + " at " + m.transform.position.ToString("F1")); m.Die();
            float t0 = Time.realtimeSinceStartup; while (reviveSeen == before && fallbackSeen == fb && Time.realtimeSinceStartup - t0 < S.reviveWaitS) yield return null;
            Note("forceDeath " + (i + 1) + "/" + n + ": " + (reviveSeen > before ? "revived in " : fallbackSeen > fb ? "scene-reload fallback after " : "NO revive within ") + (Time.realtimeSinceStartup - t0).ToString("F1") + " s");
            noFloor = false; me = null; yield return WaitReady(S); yield return new WaitForSeconds(Mathf.Max(0f, st.gapS));
        }
    }

    // ---- CLOSE5B v1 (on top of CODE5): harness steps trade / equip / pickup / inv. Nothing runs without -camscene. Every change of game state goes through the game's own calls:
    //  trade  = MarketUI.OpenUi -> ShopController.ToggleTab / Move / Confirm (arm, then order: LocalMarket.Trade -> LootSave.CommitSale / CommitBuy) -> MarketHooks.RaiseSold, as MarketUI.TickOpen does on the confirm key;
    //  equip  = LootScreen.DoEquip (the inventory screen's confirm: IGearSlotsTo.EquipTo -> GearSlots.Equip, LootSave.Save, LootBridge.RaiseEquippedChanged);  pickup = LootUI.Collect (the pickup key: LootSave.Add, WeaponDrop.OnLootCollected).
    //  Private members are reached by reflection (as ZoneRouteBot.Shop() does); a missing one is logged as a result code, never thrown.
    string lastItem = "";
    const System.Reflection.BindingFlags PrivI = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, PrivS = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
    static MarketCfg.StallC FindStall(string id) { var C = MarketCfg.Get(); if (C.stall != null && (id.Length == 0 || C.stall.id == id)) return C.stall; foreach (var q in C.stalls) if (q != null && q.id == id) return q; return null; }
    static string WornSlot(string id) { foreach (var d in LootCfg.Get().slots) if (d != null && d.id.Length > 0 && LootBridge.Slots.GetEquipped(d.id) == id) return d.id; return null; }
    void Inv(string id)
    {
        string worn = ""; foreach (var d in LootCfg.Get().slots) { if (d == null || d.id.Length == 0) continue; string w = LootBridge.Slots.GetEquipped(d.id); if (!string.IsNullOrEmpty(w)) worn += (worn.Length > 0 ? "," : "") + d.id + "=" + w; }
        var own = new List<string>(LootSave.Owned); own.Sort(StringComparer.Ordinal);
        Note("inv coins " + LootSave.Coins + ", owned [" + string.Join(",", own.ToArray()) + "], worn [" + worn + "]");
    }

    IEnumerator Trade(CtScene S, CtStep st)
    {
        string act = st.action == "buy" ? "buy" : "sell", item = st.item, res = "?"; int c0 = LootSave.Coins;
        var mc = MarketCfg.Get(); var q = mc.enabled ? FindStall(st.stall) : null; var ui = MarketUI.Instance;
        if (!mc.enabled || ui == null) res = "market-off";
        else if (q == null) res = "no-stall";
        else
        {
            float sx, sz; MarketWorld.StallXZ(q, out sx, out sz);
            yield return StartCoroutine(Walk(S, new CtStep { id = st.id, pos = new[] { sx, sz }, run = st.run, timeoutS = st.timeoutS, arriveM = Mathf.Max(0.5f, q.radius * 0.6f) }, false));
            drive = true; wantDir = Vector3.zero; wantRun = false;
            var fStl = typeof(MarketUI).GetField("stl", PrivI); float t0 = Time.realtimeSinceStartup; bool reach = false;   // the game's own reach test (MarketUI.Update: in the camp area, within stall.radius, nothing else open) and the stall it picked
            while (!reach && Time.realtimeSinceStartup - t0 < 3f) { var cur = fStl != null ? fStl.GetValue(ui) as MarketCfg.StallC : null; reach = MarketUI.InReach && (cur == null || cur.id == q.id); if (!reach) yield return null; }
            var mOpen = typeof(MarketUI).GetMethod("OpenUi", PrivI); var mClose = typeof(MarketUI).GetMethod("CloseUi", PrivI); var fShop = typeof(MarketUI).GetField("shop", PrivI); var m = Me();
            if (!reach) res = "not-in-reach (" + (m != null ? Flat(m.transform.position, new Vector3(sx, 0f, sz)).ToString("F1") : "?") + " m, radius " + q.radius.ToString("F1") + ")";
            else if (mOpen == null || mClose == null || fShop == null) res = "no-ui-hook";
            else
            {
                mOpen.Invoke(ui, null); var shop = fShop.GetValue(ui) as ShopController; yield return new WaitForSecondsRealtime(0.5f);
                if (shop == null || !MarketUI.Open) res = "panel-not-open";
                else
                {
                    bool buy = act == "buy"; if (shop.BuyTab != buy) shop.ToggleTab();
                    if (shop.BuyTab != buy) res = buy ? "buy-side-off" : "sell-tab-stuck";
                    else
                    {
                        int at = -1; var L = shop.Stock.lines; for (int i = 0; i < L.Count && at < 0; i++) if (item.Length > 0 ? L[i].id == item : L[i].canSell) at = i;
                        if (at < 0) res = item.Length == 0 ? (buy ? "nothing-buyable" : "nothing-sellable") : "not-listed" + (buy ? "" : !LootSave.Owns(item) ? " (not-owned)" : MarketPrice.Sell(MarketPrice.Find(MarketCfg.Rows(), item), MarketCfg.Rules()) <= 0 ? " (not-sellable: no market.json row)" : "");
                        else
                        {
                            item = L[at].id; if (at != shop.Index) shop.Move(at - shop.Index, 8);
                            var r = shop.Confirm(); if (r == null && shop.Armed == item) { yield return new WaitForSecondsRealtime(0.3f); r = shop.Confirm(); }   // first confirm arms (rules.confirmSell), the second sends the order
                            if (r == null) res = shop.NoteKey.Length > 0 ? shop.NoteKey : "refused";
                            else { res = r.code; if (r.ok && !r.replayed) { MarketHooks.RaiseSold(r.item, r.coinsDelta); if (buy) lastItem = r.item; } }   // same line as MarketUI.TickOpen
                        }
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                if (MarketUI.Open) mClose.Invoke(ui, null);
                for (int i = 0; i < 4; i++) yield return null;
            }
        }
        Note("trade " + act + " " + (item.Length > 0 ? item : "-") + " at " + (st.stall.Length > 0 ? st.stall : "main") + ": " + res + ", coins " + c0 + " -> " + LootSave.Coins);
    }

    IEnumerator EquipStep(CtScene S, CtStep st)
    {
        string id = st.item.Length > 0 ? st.item : lastItem, res = "?", slot = "-", vis = "unknown"; GearItem it = null; bool un = st.action == "unequip";
        if (id.Length == 0) res = "no item (nothing picked up or bought yet)";
        else if (!LootBridge.Catalog.TryGet(id, out it)) res = "unknown item";
        else if (!LootSave.Owns(id)) res = "not owned";
        else
        {
            if (!LootSave.Applied) LootSave.Apply();   // as the equip screen / MarketUI.OpenUi: the saved selection is on the wearer first
            string worn = WornSlot(id), first = null, free = null;
            foreach (var d in LootCfg.Get().slots) { if (d == null || !LootCfg.Fits(it, d)) continue; if (first == null) first = d.id; if (free == null && string.IsNullOrEmpty(LootBridge.Slots.GetEquipped(d.id))) free = d.id; }
            slot = worn ?? free ?? first ?? "-";
            var mi = typeof(LootScreen).GetMethod(un ? "DoUnequip" : "DoEquip", PrivS);
            if (un) { if (worn == null) res = "not worn"; else if (mi == null) res = "no-screen-hook"; else { mi.Invoke(null, new object[] { worn }); res = WornSlot(id) == null ? "ok" : "still worn"; } }
            else if (slot == "-") res = "no slot fits '" + it.slot + "'";
            else if (worn != null) res = "ok (already worn)";
            else if (mi == null) res = "no-screen-hook";
            else { mi.Invoke(null, new object[] { it, slot }); string now = WornSlot(id); res = now != null ? "ok" : "refused"; if (now != null) slot = now; }
            if (!un && WornSlot(id) != null)
            {
                GearSlots gs = null; foreach (var g in FindObjectsByType<GearSlots>(FindObjectsSortMode.None)) if (g != null && Roster.IsPlayer(g.CharacterId)) { gs = g; break; }
                var def = GearTable.SlotOf(slot);
                if (gs == null || gs.ItemIn(slot) != id) vis = "unknown (no GearSlots on the player wears it)";
                else if (def != null && !def.visual) vis = "unknown (data-only slot)";
                else { float t0 = Time.realtimeSinceStartup; while (gs != null && !gs.VisualOk(slot) && Time.realtimeSinceStartup - t0 < 6f) yield return null; vis = gs != null && gs.VisualOk(slot) ? "true" : "false"; }   // GearSlots.VisualOk: the piece's renderers were built and bound (the load is async)
            }
        }
        Note((un ? "unequip " : "equip ") + (id.Length > 0 ? id : "-") + ": " + res + ", slot " + slot + ", renderer visible " + vis);
    }

    IEnumerator Pickup(CtScene S, CtStep st)
    {
        float lim = st.timeoutS > 0f ? Mathf.Min(st.timeoutS, 45f) : 30f, t0 = Time.realtimeSinceStartup; LootPickup best = null; string id = st.item.Length > 0 ? st.item : "-", res = "none found";
        while (best == null && Time.realtimeSinceStartup - t0 < lim)   // the pillar appears pickup.zoneDropDelayS after the kill (after the finisher)
        {
            var m = Me(); float bd = float.MaxValue;
            if (m != null) foreach (var p in FindObjectsByType<LootPickup>(FindObjectsSortMode.None)) { if (p == null || p.Collected || (st.item.Length > 0 && p.itemId != st.item)) continue; float d = Flat(p.Anchor, m.transform.position); if (d < bd) { bd = d; best = p; } }
            if (best == null) yield return null;
        }
        if (best != null)
        {
            id = best.itemId; float rad = LootCfg.Get().pickup.radius; Vector3 at = best.Anchor;
            yield return StartCoroutine(Walk(S, new CtStep { id = st.id, pos = new[] { at.x, at.z }, run = st.run, timeoutS = 30f, arriveM = Mathf.Max(0.4f, rad * 0.6f) }, false));
            drive = true; wantDir = Vector3.zero; wantRun = false; for (int i = 0; i < 3; i++) yield return null;
            var ui = LootUI.Instance; var mm = Me(); var mc = typeof(LootUI).GetMethod("Collect", PrivI); float dd = mm != null ? Flat(at, mm.transform.position) : 999f;
            if (best == null || best.Collected) res = LootSave.Owns(id) ? "ok (auto pickup)" : "gone";
            else if (ui == null) res = "no LootUI";
            else if (dd > rad) res = "out of reach (" + dd.ToString("F1") + " m, radius " + rad.ToString("F1") + ")";
            else if (mc == null) res = "no-collect-hook";
            else { mc.Invoke(ui, new object[] { best }); res = LootSave.Owns(id) ? "ok" : "not in inventory"; }
            if (LootSave.Owns(id)) lastItem = id;
        }
        Note("pickup " + id + ": " + res + ", carried items " + LootSave.Owned.Count);
    }
}
