using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// ZONE2DUP v1: the crossing test. ONLY runs with the command line flag -zonexing (never in a normal start, never in a harness run). It stands the player at test.startZ, records a baseline,
// walks z = startZ -> turnZ (through the doorway into zone 2) and back, records every frame, and judges each crossing window against the baseline with XingStats (limits in zone_span.json "test").
// Flags: -zonexing[:<outdir>]  -zonexing-force (treat the gate as open)  -zonexing-quit (Application.Quit(0 pass / 1 fail) when done). Output: zonexing_<stamp>.csv + .json in <outdir>
// (default Application.persistentDataPath). Moves the player with CharacterController.Move only (ArenaLimits and colliders still apply). UNPROVEN IN UNITY: if DuelFighter overrides the move, walk by hand
// (the recorder also works then: press F9 to start the baseline, the same verdict is printed when you press F10).
[Serializable] public class XingLeg { public string name = ""; public float startS, endS; public XingVerdict v; public string events = ""; }
[Serializable] public class XingReport
{
    public string result = "", stamp = ""; public float offsetZ, startZ, turnZ; public int baselineFrames, totalFrames; public bool gateForced;
    public float worstFrameExtraMs, meanFactor, softHitchExtraMs; public int maxSoftHitches; public string worstFrames = ""; /* ZONEHITCH v1 */ public XingLeg outbound = new XingLeg(), inbound = new XingLeg(); public string note = "";
}

public sealed class ZoneXing : MonoBehaviour
{
    enum Ph { Wait, Settle, Baseline, Out, DwellFar, Back, DwellNear, Done }
    static ZoneXing inst;
    Ph ph = Ph.Wait; float phT, t0; DuelFighter player; CharacterController cc; SpanTable K; string outDir = ""; bool force, quit, manual;
    readonly List<float> ms = new List<float>(4096), tt = new List<float>(4096), zz = new List<float>(4096), cpu = new List<float>(4096), gpu = new List<float>(4096);
    readonly List<string> ev = new List<string>(4096), phn = new List<string>(4096);
    readonly List<KeyValuePair<float, string>> events = new List<KeyValuePair<float, string>>();
    int baseStart = -1, baseEnd = -1; string pending = ""; float waited; readonly FrameTiming[] ft = new FrameTiming[1];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null) return; string od = null; bool on = false, fo = false, qu = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a == "-zonexing") on = true; else if (a.StartsWith("-zonexing:")) { on = true; od = a.Substring(10); } else if (a == "-zonexing-force") fo = true; else if (a == "-zonexing-quit") qu = true;
        }
        if (!on) return;
        if (!ZoneFlag.Allowed(SpanData.Get().enabled)) { Debug.LogWarning("[ZoneXing] zone_span.json enabled=false: nothing to test (apply_zone2dup.py --enable)"); return; }
        var go = new GameObject("ZoneXing"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneXing>(); inst.outDir = string.IsNullOrEmpty(od) ? Application.persistentDataPath : od; inst.force = fo; inst.quit = qu;
    }

    void OnEnable() { ZoneSpan.OnEvent += OnSpan; ZoneHitch.OnNote += OnSpan; }  /* ZONEHITCH v1 */
    void OnDisable() { ZoneSpan.OnEvent -= OnSpan; ZoneHitch.OnNote -= OnSpan; }  /* ZONEHITCH v1 */
    void OnSpan(string e) { events.Add(new KeyValuePair<float, string>(Time.unscaledTime, e)); pending += e + ";"; }

    void Update()
    {
        K = SpanData.Get(); float dt = Time.unscaledDeltaTime; phT += dt;
        bool f9 = false, f10 = false; try { f9 = Input.GetKeyDown(KeyCode.F9); f10 = Input.GetKeyDown(KeyCode.F10); } catch (Exception) { }   // legacy input may be off in a build: the auto walk does not need keys
        if (f9 && ph == Ph.Wait) { manual = true; Enter(Ph.Baseline); }
        if (f10 && manual && ph != Ph.Done) { Enter(Ph.Done); Finish(); return; }
        if (ph == Ph.Done) return;
        if (player == null || cc == null) { Find(); }
        if (ph == Ph.Wait)
        {
            waited += dt; if (player != null && ZoneSpan.Live && !ZoneHitch.Warming) {  /* ZONEHITCH v1: the warm renders finish before the baseline */ if (force) K.ForceOpenAll(); Enter(Ph.Settle); }
            else if (waited > 10f && (int)waited % 10 == 0 && (int)(waited - dt) % 10 != 0) Debug.Log("[ZoneXing] waiting " + (int)waited + " s: player " + (player != null) + ", span live " + ZoneSpan.Live + ", warming " + ZoneHitch.Warming);
            else if (waited > 60f) { Debug.LogError("[ZoneXing] FAIL: the span never bound in 60 s (Zone2 not built? zones.json off? see [ZoneSpan] lines)"); ph = Ph.Done; if (quit) Application.Quit(1); }
            return;
        }
        float z = player != null ? player.transform.position.z : 0f;
        if (ph != Ph.Wait) Record(dt, z);
        switch (ph)
        {
            case Ph.Settle: if (phT >= K.test.settleSeconds) { Place(K.test.startZ); Enter(Ph.Baseline); } break;
            case Ph.Baseline: if (!manual && phT >= K.test.baselineSeconds) { baseEnd = ms.Count; Enter(Ph.Out); } break;
            case Ph.Out: Step(+1f, dt); if (z >= K.test.turnZ) Enter(Ph.DwellFar); break;
            case Ph.DwellFar: if (phT >= K.test.dwellSeconds) Enter(Ph.Back); break;
            case Ph.Back: Step(-1f, dt); if (z <= K.test.startZ) Enter(Ph.DwellNear); break;
            case Ph.DwellNear: if (phT >= K.test.dwellSeconds) { Enter(Ph.Done); Finish(); } break;
        }
    }

    void Enter(Ph p) { Debug.Log("[ZoneXing] phase " + p + " (player " + (player != null) + ", span live " + ZoneSpan.Live + ", warming " + ZoneHitch.Warming + ")"); ph = p; phT = 0f; if (p == Ph.Baseline) baseStart = ms.Count; events.Add(new KeyValuePair<float, string>(Time.unscaledTime, "phase:" + p)); pending += "phase:" + p + ";"; }
    void Find()
    {
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { player = f; cc = f.GetComponent<CharacterController>(); break; }
    }
    void Place(float z)
    {
        if (player == null) return; bool en = cc != null && cc.enabled; if (en) cc.enabled = false;
        var p = player.transform.position; player.transform.position = new Vector3(0f, p.y, z); if (en) cc.enabled = true; Physics.SyncTransforms();
    }
    void Step(float dir, float dt) { if (cc != null && cc.enabled) cc.Move(new Vector3(0f, 0f, dir * K.test.walkSpeed * dt)); }

    void Record(float dt, float z)
    {
        ms.Add(dt * 1000f); tt.Add(Time.unscaledTime); zz.Add(z); ev.Add(pending); pending = ""; phn.Add(ph.ToString());
        FrameTimingManager.CaptureFrameTimings(); if (FrameTimingManager.GetLatestTimings(1, ft) > 0) { cpu.Add((float)ft[0].cpuFrameTime); gpu.Add((float)ft[0].gpuFrameTime); } else { cpu.Add(0f); gpu.Add(0f); }
    }

    // the crossing window of one leg: from its first wake event minus the pad to its last switch event plus the pad
    XingLeg Leg(string name, string wake, string[] ends, float[] baseline)
    {
        var L = new XingLeg { name = name }; float pad = K.test.windowPadSeconds; float s = float.NaN, e = float.NaN; var sb = new StringBuilder();
        foreach (var kv in events)
        {
            if (kv.Value == "phase:" + (name == "outbound" ? Ph.Back : Ph.Done)) break;
            bool inLeg = name == "outbound" ? true : kv.Key >= LegStart("phase:" + Ph.Back);
            if (!inLeg) continue;
            if (kv.Value == wake && float.IsNaN(s)) s = kv.Key;
            if (!float.IsNaN(s) && kv.Key >= s) { foreach (var en in ends) if (kv.Value.StartsWith(en)) e = kv.Key; sb.Append(kv.Value).Append('@').Append((kv.Key - s).ToString("F2", CultureInfo.InvariantCulture)).Append(' '); }
        }
        L.events = sb.ToString().Trim();
        if (float.IsNaN(s) || float.IsNaN(e)) { L.v = XingStats.Eval(baseline, new float[0], 0, 1, 0, 0); L.v.why = "no wake/switch events seen for this leg (the span did not run?)"; return L; }
        L.startS = s - pad; L.endS = e + pad; var win = new List<float>();
        for (int i = 0; i < tt.Count; i++) if (tt[i] >= L.startS && tt[i] <= L.endS) win.Add(ms[i]);
        L.v = XingStats.Eval(baseline, win.ToArray(), K.test.worstFrameExtraMs, K.test.meanFactor, K.test.softHitchExtraMs, K.test.maxSoftHitches); return L;
    }
    // ZONEHITCH v1: the k slowest frames after the first 3: z, phase, cpu / gpu ms, and every marker from 6 frames before to 3 after (a wake, a ring, an ms: step note = zone code; none = not zone code)
    string WorstFrames(int k)
    {
        var idx = new List<int>(); for (int i = 3; i < ms.Count; i++) idx.Add(i); idx.Sort((a, b) => ms[b].CompareTo(ms[a])); var sb = new StringBuilder(); var ic = CultureInfo.InvariantCulture;
        for (int j = 0; j < idx.Count && j < k; j++)
        {
            int i = idx[j]; string near = ""; for (int q = Mathf.Max(0, i - 6); q <= Mathf.Min(ms.Count - 1, i + 3); q++) if (ev[q].Length > 0) near += (q - i).ToString("+0;-0;0", ic) + ":" + ev[q] + " ";
            sb.Append("f").Append(i).Append(" ").Append(ms[i].ToString("F0", ic)).Append("ms z").Append(zz[i].ToString("F1", ic)).Append(" ").Append(phn[i]).Append(" cpu").Append(cpu[i].ToString("F0", ic)).Append("/gpu").Append(gpu[i].ToString("F0", ic)).Append(" near[").Append(near.Trim()).Append("] | ");
        }
        return sb.ToString();
    }
    float LegStart(string phase) { foreach (var kv in events) if (kv.Value == phase) return kv.Key; return float.MaxValue; }

    void Finish()
    {
        if (manual && baseEnd <= baseStart) { float w = float.MaxValue; foreach (var kv in events) if (kv.Value == "wake2") { w = kv.Key; break; } baseEnd = baseStart; while (baseEnd < tt.Count && tt[baseEnd] < w) baseEnd++; }   // manual walk: the baseline is everything before the first wake
        int b0 = Mathf.Max(0, baseStart), b1 = baseEnd > b0 ? baseEnd : Mathf.Min(ms.Count, b0 + 1); var bl = new List<float>(); for (int i = b0 + 2; i < b1 && i < ms.Count; i++) bl.Add(ms[i]);   // drop the first 2 frames of the baseline
        var baseline = bl.ToArray(); string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var rep = new XingReport { stamp = stamp, offsetZ = K.FirstOffset, startZ = K.test.startZ, turnZ = K.test.turnZ, baselineFrames = baseline.Length, totalFrames = ms.Count, gateForced = K.AnyForced,
            worstFrameExtraMs = K.test.worstFrameExtraMs, meanFactor = K.test.meanFactor, softHitchExtraMs = K.test.softHitchExtraMs, maxSoftHitches = K.test.maxSoftHitches };
        rep.outbound = Leg("outbound", "wake2", new[] { "on:", "off:", "enter:", "tunnel:" }, baseline); rep.inbound = Leg("inbound", "wake1", new[] { "on:", "off:", "enter:" }, baseline);
        rep.worstFrames = WorstFrames(6); rep.result = rep.outbound.v.pass && rep.inbound.v.pass ? "PASS" : "FAIL"; if (manual) rep.note = "manual walk";
        try
        {
            Directory.CreateDirectory(outDir); var sb = new StringBuilder("frame,t,ms,z,phase,cpuMs,gpuMs,events\n");
            for (int i = 0; i < ms.Count; i++) sb.Append(i).Append(',').Append(tt[i].ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(ms[i].ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(zz[i].ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(phn[i]).Append(',').Append(cpu[i].ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(gpu[i].ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(ev[i]).Append('\n');
            File.WriteAllText(Path.Combine(outDir, "zonexing_" + stamp + ".csv"), sb.ToString()); File.WriteAllText(Path.Combine(outDir, "zonexing_" + stamp + ".json"), JsonUtility.ToJson(rep, true));
        }
        catch (Exception e) { Debug.LogWarning("[ZoneXing] could not write the trace: " + e.Message); }
        Debug.Log("[ZoneXing] " + rep.result + " outbound: " + rep.outbound.v.why + " (worst " + rep.outbound.v.worst.ToString("F1") + " / limit " + rep.outbound.v.limitWorst.ToString("F1") + " ms) inbound: " + rep.inbound.v.why + " (worst " + rep.inbound.v.worst.ToString("F1") + " / limit " + rep.inbound.v.limitWorst.ToString("F1") + " ms) -> " + outDir);
        if (quit) Application.Quit(rep.result == "PASS" ? 0 : 1);
    }
}
