using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// LOCKON v1 (pool-laneLOCKON, UNPROVEN in Unity): the lock-on target switch. Data: Assets/Data/lock_switch.json (StreamingAssets copy). File missing or "enabled": false = this file does nothing.
// Before: the lock target was only "the nearest engaged enemy" (ZoneRuntime.Retarget) and there was no switch input.
// Now: next / previous key (data) -> LockSwitch.PlayerInput (called from FlowTable.PlayerInput, next to the lock toggle and the riposte key) -> ZoneRuntime.LockSwitchPress.
//   Candidates = zone enemies only (ZoneRuntime.live: bosses, bandits, NPC humans) that are FIGHTING Joan, alive, active and within rangeM. Ambient animals and mounts are not DuelFighters and are not in that set; ignoreNamePrefixes is a second guard.
//   Order = class priority (data "classes": boss before human before other), then distance (order "priority"), or screen left to right (order "angle"). next = the following one, wrapping.
//   The choice is PINNED: ZoneRuntime.Retarget (every poll with 2+ engaged) would otherwise snap back to the nearest one. The pin ends when the target is dead, not fighting, out of range * releaseMul,
//   or after pinSeconds (0 = never). autoBossFirst (default false = today's nearest-first) makes the automatic pick prefer the higher class.
//   Camera: CameraRig is NOT changed. The V3 lock pose is the same, CamZone.YawStep still limits the swing to the target (150 deg/s). CamZone.ForceAccept tells CamZone.Pick the new target is ours (no 0.8 s hold-back).
// Proof harness (compiled unless FRANKEN_NO_HARNESS): -lockswitchproof [-lsspawn:<id,id>] [-lsseq:next,next,prev] [-lssettle:<s>] [-lsout:<file>] [-lskeep] [-lstimeout:<s>]  (see RESULTS.md).
public static class LockSwitch
{
    [Serializable] public class KeyRow { public string[] next = new[] { "Period", "Mouse4" }, prev = new[] { "Comma", "Mouse3" }; }
    [Serializable] public class ClassRow { public string id = ""; public int priority = 1; public string[] ids = new string[0], prefixes = new string[0]; }
    [Serializable] public class HarnessRow { public float startDelayS = 4f, settleS = 1.6f, maxYawErrDeg = 40f, timeoutS = 240f; public string seq = "next,next,prev,prev"; }
    [Serializable]
    public class Cfg
    {
        public bool enabled = false, log = true, wrap = true, autoBossFirst = false;
        public KeyRow keys = new KeyRow(), pad = new KeyRow();
        public ClassRow[] classes = new ClassRow[0]; public int otherPriority = 1; public string[] ignoreNamePrefixes = new string[0];
        public string order = "priority"; public float rangeM = 9f, releaseMul = 1.2f, cooldownS = 0.25f, pinSeconds = 0f;
        public HarnessRow harness = new HarnessRow();
    }
    static Cfg cfg; static KeyCode[] nextK, prevK;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; nextK = prevK = null; }
    public static Cfg Get()
    {
        if (cfg != null) return cfg;
        Cfg c = null; string t = DataPath.ReadText("lock_switch.json");
        if (t != null) { try { c = JsonUtility.FromJson<Cfg>(t.TrimStart('﻿')); } catch (Exception e) { Debug.LogWarning("[LockSwitch] lock_switch.json unreadable (" + e.Message + "): switch off"); } }
        if (c == null) c = new Cfg();
        if (c.keys == null) c.keys = new KeyRow(); if (c.pad == null) c.pad = new KeyRow(); if (c.classes == null) c.classes = new ClassRow[0]; if (c.ignoreNamePrefixes == null) c.ignoreNamePrefixes = new string[0]; if (c.harness == null) c.harness = new HarnessRow();
        nextK = Parse(c.keys.next, c.pad.next); prevK = Parse(c.keys.prev, c.pad.prev); cfg = c; return c;
    }
    static KeyCode[] Parse(string[] a, string[] b)
    {
        var l = new List<KeyCode>(); foreach (var arr in new[] { a, b }) if (arr != null) foreach (var n in arr) { KeyCode k; if (!string.IsNullOrEmpty(n) && Enum.TryParse(n, true, out k)) l.Add(k); }
        return l.ToArray();
    }
    static bool AnyDown(KeyCode[] ks) { if (ks == null) return false; for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }

    /// <summary>FlowTable.PlayerInput, once per frame. Does nothing with zones off, the pause menu open or a finisher scene running.</summary>
    public static void PlayerInput(CameraRig rig, DuelFighter df)
    {
        var C = Get(); if (!C.enabled || !ZoneRuntime.Owns || MenuUI.Open || RTFinisher.Busy) return;
        int dir = AnyDown(nextK) ? 1 : AnyDown(prevK) ? -1 : 0;
        if (dir != 0) ZoneRuntime.LockSwitchPress(dir, "key");
    }

    /// <summary>Class priority of a zone creature: 0 = never a switch target (ambient / ignored), higher = earlier in the order. cls = the class id.</summary>
    public static int Priority(string creature, string goName, out string cls)
    {
        var C = Get(); cls = "other";
        foreach (var p in C.ignoreNamePrefixes) if (!string.IsNullOrEmpty(p) && ((creature != null && creature.StartsWith(p, StringComparison.Ordinal)) || (goName != null && goName.StartsWith(p, StringComparison.Ordinal)))) { cls = "ignored"; return 0; }
        foreach (var r in C.classes)
        {
            if (r == null) continue;
            if (r.ids != null) foreach (var i in r.ids) if (i == creature) { cls = r.id; return r.priority; }
            if (r.prefixes != null) foreach (var p in r.prefixes) if (!string.IsNullOrEmpty(p) && creature != null && creature.StartsWith(p, StringComparison.Ordinal)) { cls = r.id; return r.priority; }
        }
        return C.otherPriority;
    }
}

public sealed partial class ZoneRuntime
{
    Live lsPin; float lsPinAt = -99f, lsLastPress = -99f;

    /// <summary>Retarget calls this with its pick: a live pin wins; else autoBossFirst may prefer a higher class. No pin and autoBossFirst false = returns `best` unchanged.</summary>
    Live LockPinOverride(Live best)
    {
        var C = LockSwitch.Get(); if (!C.enabled || player == null) return best;
        if (lsPin != null)
        {
            bool ok = !lsPin.killed && lsPin.df != null && !lsPin.df.Dead && lsPin.go != null && lsPin.go.activeInHierarchy && Fighting(lsPin)
                && Flat(player.transform.position, lsPin.df.transform.position) <= C.rangeM * C.releaseMul && (C.pinSeconds <= 0f || Time.unscaledTime - lsPinAt < C.pinSeconds);
            if (ok) return lsPin;
            if (C.log) Debug.Log("[LockSwitch] pin on " + lsPin.row.id + " ended"); lsPin = null;
        }
        if (C.autoBossFirst && best != null)
        {
            string c0; int bp = LockSwitch.Priority(best.creature, best.go != null ? best.go.name : "", out c0); Live pick = best; float bd = Flat(player.transform.position, best.df.transform.position);
            foreach (var q in live.Values)
            {
                if (q.df == null || q.df.Dead || !Fighting(q) || q == best) continue; string c1; int p = LockSwitch.Priority(q.creature, q.go != null ? q.go.name : "", out c1); if (p <= bp) continue;
                float d = Flat(player.transform.position, q.df.transform.position); if (d <= C.rangeM) { bp = p; pick = q; bd = d; }
            }
            return pick;
        }
        return best;
    }

    List<Live> LsCands(LockSwitch.Cfg C)
    {
        var res = new List<Live>(); if (player == null) return res; Vector3 pp = player.transform.position;
        foreach (var q in live.Values)
        {
            if (q.df == null || q.go == null || !q.go.activeInHierarchy || q.df.Dead || q.killed || !Fighting(q)) continue;
            string cls; if (LockSwitch.Priority(q.creature, q.go.name, out cls) <= 0) continue;
            if (Flat(pp, q.df.transform.position) > C.rangeM) continue; res.Add(q);
        }
        var cam = Camera.main; Vector3 cf = cam != null ? cam.transform.forward : player.transform.forward; cf.y = 0f; if (cf.sqrMagnitude < 1e-4f) cf = Vector3.forward;
        bool byAngle = C.order == "angle";
        res.Sort((a, b) =>
        {
            string ca, cb; int pa = LockSwitch.Priority(a.creature, a.go.name, out ca), pb = LockSwitch.Priority(b.creature, b.go.name, out cb);
            if (byAngle) { float aa = Ang(cf, pp, a), ab = Ang(cf, pp, b); int r = aa.CompareTo(ab); if (r != 0) return r; }
            else { if (pa != pb) return pb.CompareTo(pa); int r = Flat(pp, a.df.transform.position).CompareTo(Flat(pp, b.df.transform.position)); if (r != 0) return r; }
            return string.CompareOrdinal(a.row.id, b.row.id);
        });
        return res;
    }
    static float Ang(Vector3 camFwd, Vector3 pp, Live l) { var to = l.df.transform.position - pp; to.y = 0f; return to.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(camFwd, to, Vector3.up); }

    bool LsSwitch(int dir, string why)
    {
        var C = LockSwitch.Get(); if (!C.enabled || player == null || player.Dead || RTFinisher.Busy || phase != Phase.Play) return false;
        if (Time.unscaledTime - lsLastPress < C.cooldownS) return false;
        var list = LsCands(C); if (list.Count == 0) { if (C.log) Debug.Log("[LockSwitch] " + why + ": no target in range"); return false; }
        Live cur = player.opponent != null ? Find(player.opponent) : null; int ci = cur != null ? list.IndexOf(cur) : -1, ni;
        if (ci < 0) ni = dir > 0 ? 0 : list.Count - 1; else { ni = ci + dir; if (ni >= list.Count || ni < 0) { if (!C.wrap) return false; ni = (ni + list.Count) % list.Count; } }
        var t = list[ni]; lsLastPress = Time.unscaledTime;
        if (t == cur) { if (C.log) Debug.Log("[LockSwitch] " + why + ": only one target (" + t.row.id + ")"); return false; }
        lsPin = t; lsPinAt = Time.unscaledTime; Retarget(true);   // the pin wins inside Retarget: player.opponent, GameLoop.Enemy, FoeId, rig.enemy, ladder row all follow
        var rig = FindFirstObjectByType<CameraRig>(); if (rig != null && rig.enemy != null) CamZone.ForceAccept(rig.enemy);
        string cls; LockSwitch.Priority(t.creature, t.go.name, out cls);
        if (C.log) Debug.Log("[LockSwitch] " + why + " " + (dir > 0 ? "next" : "prev") + ": " + (cur != null ? cur.row.id : "none") + " -> " + t.row.id + " (" + t.creature + ", class " + cls + ", " + Flat(player.transform.position, t.df.transform.position).ToString("F1") + " m, " + (ni + 1) + "/" + list.Count + ")");
        return true;
    }
    /// <summary>Called by LockSwitch.PlayerInput and the harness. dir +1 next, -1 previous.</summary>
    public static bool LockSwitchPress(int dir, string why) { var z = Inst; return z != null && z.on && z.LsSwitch(dir, why); }
    /// <summary>Harness: "id:class:dist" of every candidate in switch order.</summary>
    public static string LockInfo()
    {
        var z = Inst; if (z == null || !z.on || z.player == null) return "(zones off)"; var C = LockSwitch.Get(); var sb = new System.Text.StringBuilder();
        foreach (var q in z.LsCands(C)) { string cls; LockSwitch.Priority(q.creature, q.go.name, out cls); if (sb.Length > 0) sb.Append(' '); sb.Append(q.row.id + ":" + cls + ":" + Flat(z.player.transform.position, q.df.transform.position).ToString("F1")); }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }
    public static string LockPinName { get { var z = Inst; return z != null && z.lsPin != null ? z.lsPin.row.id : ""; } }
}

#if !FRANKEN_NO_HARNESS
// LOCKON v1 proof: boots with -lockswitchproof. Waits for the zone run, optionally spawns extra engaged foes next to Joan (-lsspawn:Sobek or Bandit ids), presses next / prev through the SAME code path as the key,
// and after each press (settle seconds, the camera yaw swing is rate limited) logs and checks: target changed, it is a DuelFighter (never a horse), player.opponent == rig.enemy, the lock is on, camera forward within maxYawErrDeg of the target.
public sealed class LockSwitchProof : MonoBehaviour
{
    static string Arg(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(p, StringComparison.Ordinal)) return a.Substring(p.Length); return null; }
    static bool Flag(string p) { foreach (var a in Environment.GetCommandLineArgs()) if (a == p) return true; return false; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (!Flag("-lockswitchproof")) return; var go = new GameObject("LockSwitchProof"); DontDestroyOnLoad(go); go.AddComponent<LockSwitchProof>(); }
    readonly List<string> lines = new List<string>(); int fails;
    void L(string s) { lines.Add(s); Debug.Log("[LockSwitchProof] " + s); }
    IEnumerator Start()
    {
        var C = LockSwitch.Get(); var H = C.harness; float t0 = Time.realtimeSinceStartup, to = H.timeoutS; string v = Arg("-lstimeout:"); float f; if (v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) to = f;
        CameraRig rig = null; DuelFighter pl = null;
        while (Time.realtimeSinceStartup - t0 < to) { rig = FindFirstObjectByType<CameraRig>(); if (rig != null && rig.target != null) pl = rig.target.GetComponent<DuelFighter>(); if (ZoneRuntime.Owns && rig != null && pl != null && !pl.Dead && Time.realtimeSinceStartup - t0 > H.startDelayS) break; yield return null; }
        if (rig == null || pl == null || !ZoneRuntime.Owns) { L("SKIP: no zone run / player within " + to.ToString("F0") + " s"); Finish(false); yield break; }
        if (!C.enabled) { L("FAIL: lock_switch.json missing or enabled=false"); fails++; Finish(true); yield break; }
        string sp = Arg("-lsspawn:"); if (!string.IsNullOrEmpty(sp))
        {
            int i = 0; foreach (var id in sp.Split(',')) { if (id.Length == 0) continue; var fwd = pl.transform.forward; fwd.y = 0f; fwd.Normalize(); var at = pl.transform.position + fwd * (4f + 1.5f * i) + pl.transform.right * (i % 2 == 0 ? 2f : -2f); var yaw = Quaternion.LookRotation(-fwd).eulerAngles.y;
                var d = ZoneRuntime.SpawnExtra("lsproof" + i, id, at, yaw, "", 1f, 1f, 0, 40f, 80f); L("spawn " + id + (d != null ? " ok" : " FAILED")); i++; }
            yield return new WaitForSeconds(1.5f);
        }
        L("candidates (switch order): " + ZoneRuntime.LockInfo() + " | camera target " + (rig.enemy != null ? rig.enemy.name : "none"));
        string seq = Arg("-lsseq:") ?? H.seq; float settle = H.settleS; v = Arg("-lssettle:"); if (v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) settle = f;
        int n = 0, changed = 0;
        foreach (var tok in seq.Split(','))
        {
            int dir = tok == "prev" ? -1 : 1; var before = rig.enemy; bool did = ZoneRuntime.LockSwitchPress(dir, "proof"); yield return new WaitForSeconds(Mathf.Max(settle, C.cooldownS + 0.05f)); n++;
            var e = rig.enemy; var ef = e != null ? e.GetComponent<DuelFighter>() : null; var cam = Camera.main; string cls = "?"; if (e != null) LockSwitch.Priority(Roster.IdOf(e), e.name, out cls);
            float err = -1f; if (e != null && cam != null) { var cf = cam.transform.forward; cf.y = 0f; var tp = e.position - pl.transform.position; tp.y = 0f; err = Vector3.Angle(cf, tp); }
            bool ok = true; string why = "";
            if (did) { changed++; if (e == before) { ok = false; why += " target unchanged;"; } }
            if (e != null && ef == null) { ok = false; why += " camera lock on a non-fighter (animal/mount);"; }
            if (e != null && pl.opponent != ef) { ok = false; why += " player.opponent != camera target;"; }
            if (e != null && !rig.dbgLockOn) { ok = false; why += " lock not on;"; }
            if (err > H.maxYawErrDeg) { ok = false; why += " camera " + err.ToString("F0") + " deg off the target;"; }
            if (!ok) fails++;
            L("press " + n + " " + tok + ": switched=" + did + " target=" + (e != null ? e.name : "none") + " class=" + cls + " pin=" + ZoneRuntime.LockPinName + " lockW=" + rig.LockW.ToString("F2") + " camErrDeg=" + err.ToString("F0") + " " + (ok ? "ok" : "FAIL:" + why));
        }
        if (changed == 0) L("NOTE: no press switched (fewer than 2 candidates): rerun with -lsspawn:<second foe id>");
        Finish(true);
    }
    void Finish(bool ran)
    {
        string res = "RESULT " + (!ran ? "SKIP" : fails == 0 ? "PASS" : "FAIL") + " fails=" + fails; L(res);
        string o = Arg("-lsout:"); if (!string.IsNullOrEmpty(o)) { try { System.IO.File.WriteAllLines(o, lines.ToArray()); } catch (Exception e) { Debug.LogWarning("[LockSwitchProof] could not write " + o + ": " + e.Message); } }
        if (!Flag("-lskeep")) Application.Quit();
    }
}
#endif
