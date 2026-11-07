using System;
using System.Collections;
using System.IO;
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
    DuelFighter Me() { if (me == null || me.Dead) { me = GameLoop.Player; } return me; }
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }

    IEnumerator Start()
    {
        yield return null;
        if (!ZoneRuntime.Owns && !ZoneSave.Active) { Debug.LogWarning("[CamScene] zones are off (needs -zones, no harness flag): nothing to do"); Done("zones off", false); yield break; }
        if (!ZoneCamTrace.Running) { Debug.LogWarning("[CamScene] needs -camtrace:<dir> next to -camscene"); Done("no -camtrace", false); yield break; }
        C = ZoneCamTrace.Cfg; var S = C.scene; if (!S.enabled) { Done("scene.enabled=false", false); yield break; }
        float tAll = Time.realtimeSinceStartup; bool ok = true;
        foreach (var st in S.steps)
        {
            if (st == null) continue; if (Time.realtimeSinceStartup - tAll > S.globalTimeoutS) { Note("global timeout, stopping"); ok = false; break; }
            ZoneCamTrace.Step = st.id; Note("step " + st.id + " (" + st.kind + ")"); float t0 = Time.realtimeSinceStartup;
            IEnumerator it = null;
            switch (st.kind)
            {
                case "ready": it = Ready(S); break;
                case "restart": it = Restart(S, st); break;
                case "walk": it = Walk(S, st, false); break;
                case "flee": it = Walk(S, st, true); break;
                case "fight": it = Fight(S, st); break;
                case "hold": it = Hold(S, st); break;
                default: Note("unknown kind " + st.kind); ok = false; break;
            }
            if (it != null) yield return StartCoroutine(it);
            Note("step " + st.id + " took " + (Time.realtimeSinceStartup - t0).ToString("F1") + " s");
        }
        drive = false; ZoneCamTrace.Step = "done"; yield return new WaitForSeconds(1f); Done("ok", ok);
    }

    void Note(string s) { log += s + "\n"; Debug.Log("[CamScene] " + s); }
    void Done(string why, bool ok)
    {
        drive = false; try { var d = Environment.GetCommandLineArgs(); string dir = ""; foreach (var a in d) if (a.StartsWith("-camtrace:", StringComparison.Ordinal)) dir = a.Substring(10); if (dir.Length > 0) File.WriteAllText(Path.Combine(dir, "camscene_done.txt"), (ok ? "ok" : "problem") + ": " + why + ", hp assists " + assists + "\n" + log); } catch (Exception) { }
        ZoneCamTrace.Finish(); if (C == null || C.scene.quit) Application.Quit(ok ? 0 : 1);
    }

    void Update()
    {
        var m = Me(); if (!drive || m == null || m.Dead || Duel.Frozen) return;
        m.moveDir = wantDir; m.run = wantRun;
        float fl = C.scene.hpFloorFrac; if (fl > 0f && m.maxHealth > 0f && m.health < m.maxHealth * fl) { m.health = m.maxHealth * fl; var h = m.GetComponent<Health>(); if (h != null) h.current = m.health; assists++; }
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

    IEnumerator Hold(CtScene S, CtStep st) { drive = true; wantDir = Vector3.zero; wantRun = false; yield return new WaitForSeconds(st.seconds); }

    IEnumerator Fight(CtScene S, CtStep st)
    {
        drive = true; float t0 = Time.realtimeSinceStartup, nextHit = 0f;
        while (Time.realtimeSinceStartup - t0 < st.seconds)
        {
            var m = Me(); var t = m != null ? m.opponent : null;
            if (m == null || t == null || t.Dead) { wantDir = Vector3.zero; wantRun = false; yield return null; continue; }
            Vector3 to = t.transform.position - m.transform.position; to.y = 0f; float d = to.magnitude;
            wantRun = false; wantDir = d > S.approachGapM ? to / Mathf.Max(0.01f, d) : Vector3.zero;
            if (d <= S.attackGapM && Time.realtimeSinceStartup >= nextHit && (m.maxStamina <= 0f || m.stamina / m.maxStamina >= S.staminaMinFrac)) { m.Press("light"); nextHit = Time.realtimeSinceStartup + S.strikeIntervalS; }
            yield return null;
        }
        wantDir = Vector3.zero;
    }
}
