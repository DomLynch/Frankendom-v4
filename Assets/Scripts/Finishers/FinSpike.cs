using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// FINREALTIME v1 (laneFINREALTIME). Measurement only: "did the finisher hitch, and did the game keep running in real time?"  Nothing here touches the sim, the clock or the camera.
// Run it with   HadesSlice.exe -autoplay -prewarm92harness -finspike [-finspikeout:<dir>] [-finspikemax:<n>]   (see kit\run_finspike.ps1). Off without -finspike. Numbers: finishers.json rt.realtime.spike.
// Per finisher scene (RTFinisher.OnStart .. OnEnd) one JSON line in <dir>\finspike.jsonl and one "[FinSpike]" log line:
//   preWorstMs / sceneWorstMs / postWorstMs  worst unscaled frame time in the preS seconds before the start, during the scene, in the postS seconds after the end  (the spike numbers, before = kit off, after = kit on)
//   sceneWorstAtS                            when in the scene (unscaled s from the start) the worst frame was
//   hitchFrames                              frames in the scene longer than spike.hitchMs
//   minTimeScale / slowS                     lowest Time.timeScale in the scene / unscaled seconds with timeScale below slowBelow (a real-time game reads ~0.5 s, an old pause-style scene several seconds)
//   hitStopMaxMs                             longest PresentationClock freeze (frame quantised)
//   frozenS                                  unscaled seconds Duel.Frozen was set (the AI/hit freeze of the whole world)
//   camCuts / camMaxJumpM                    shot changes ("shot" stages) and the biggest camera movement in ONE frame (a hard cut is a jump above spike.cutJumpM)
//   othersAlive / othersMovedM               fighters outside the pair and how far they walked during the scene (0 while they are frozen)
//   realtime                                 PASS when slowS, hitStopMaxMs and camMaxJumpM are inside the spike limits and (if there are others) they moved
public class FinSpike : MonoBehaviour
{
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    struct Sample { public float t, ms, ts; public bool frozen; }
    class Rec
    {
        public string id = "", killer = "", victim = ""; public int scene, frame0; public float t0, t1, preWorst, sceneWorst, sceneWorstAt, postWorst, minTs = 1f, slowS, hitStopMax, frozenS, camMaxJump, camMaxRot, othersMoved;
        public int hitchFrames, camCuts, othersAlive; public bool ended, ok, postDone;
    }

    string dir, path; int maxScenes = 6, sceneNo; float timeoutS = 900f, born;
    readonly List<Sample> ring = new List<Sample>(); readonly List<Rec> recs = new List<Rec>();
    Rec cur, post; DuelFighter K, V; readonly List<DuelFighter> others = new List<DuelFighter>(); readonly List<Vector3> otherPos = new List<Vector3>();
    Vector3 camPos; Quaternion camRot; bool camHave; float pfT = -1f, freezeStart = -1f; bool frozenNow;
    FinisherRealtimeSpike C;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        bool on = false; foreach (var a in Environment.GetCommandLineArgs()) if (a.Equals("-finspike", StringComparison.OrdinalIgnoreCase)) on = true;
        if (!on || FindFirstObjectByType<FinSpike>() != null) return;
        var go = new GameObject("FinSpike"); DontDestroyOnLoad(go); go.AddComponent<FinSpike>();
    }

    static string Arg(string key) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return a.Substring(key.Length); return null; }

    void Awake()
    {
        var rt = FinisherConfig.Get().rt; C = rt.realtime != null && rt.realtime.spike != null ? rt.realtime.spike : new FinisherRealtimeSpike();
        maxScenes = C.maxScenes; timeoutS = C.timeoutS; born = Time.realtimeSinceStartup;
        string n = Arg("-finspikemax:"); int mv; if (n != null && int.TryParse(n, NumberStyles.Integer, IC, out mv) && mv > 0) maxScenes = mv;
        dir = Arg("-finspikeout:"); if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Application.persistentDataPath, "finspike");
        try { Directory.CreateDirectory(dir); path = Path.Combine(dir, "finspike.jsonl"); File.WriteAllText(path, ""); } catch (Exception e) { Debug.LogWarning("[FinSpike] output dir: " + e.Message); path = null; }
        RTFinisher.OnStart += OnStart; RTFinisher.OnEnd += OnEnd; RTFinisher.OnStage += OnStage; PresentationClock.OnFrozenChanged += OnFrozen;
        Debug.Log("[FinSpike] armed: " + maxScenes + " finisher scenes max, " + C.preS.ToString("0.0", IC) + " s before / " + C.postS.ToString("0.0", IC) + " s after, out " + dir + ", realtime.enabled " + (rt.realtime != null && rt.realtime.enabled));
    }
    void OnDestroy() { RTFinisher.OnStart -= OnStart; RTFinisher.OnEnd -= OnEnd; RTFinisher.OnStage -= OnStage; PresentationClock.OnFrozenChanged -= OnFrozen; }

    void OnFrozen(bool on)
    {
        if (on) { freezeStart = Time.unscaledTime; frozenNow = true; }
        else { frozenNow = false; if (cur != null && freezeStart >= 0f) cur.hitStopMax = Mathf.Max(cur.hitStopMax, (Time.unscaledTime - freezeStart) * 1000f); freezeStart = -1f; }
    }

    void OnStart(FinisherEntry e, DuelFighter k, DuelFighter v)
    {
        if (cur != null) Finish(cur);
        cur = new Rec { id = e != null ? e.id : "", scene = ++sceneNo, frame0 = Time.frameCount, t0 = Time.unscaledTime };
        K = k; V = v; cur.killer = Roster.IdOf(k); cur.victim = Roster.IdOf(v);
        for (int i = 0; i < ring.Count; i++) if (ring[i].t >= cur.t0 - C.preS) cur.preWorst = Mathf.Max(cur.preWorst, ring[i].ms);
        others.Clear(); otherPos.Clear();
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f != null && f != k && f != v && !f.Dead) { others.Add(f); otherPos.Add(f.transform.position); }
        cur.othersAlive = others.Count; camHave = false;
    }
    void OnStage(string kind, string detail) { if (cur != null && !cur.ended && kind == "shot") cur.camCuts++; }
    void OnEnd(bool ok, string msg) { if (cur == null || cur.ended) return; cur.ended = true; cur.ok = ok; cur.t1 = Time.unscaledTime; post = cur; }

    void LateUpdate()
    {
        float now = Time.unscaledTime, ms = Time.unscaledDeltaTime * 1000f; if (Time.frameCount < 3) return;   // the first frames are the scene load, not a finisher
        ring.Add(new Sample { t = now, ms = ms, ts = Time.timeScale, frozen = Duel.Frozen });
        float keep = Mathf.Max(C.preS, 1f) + 1f; while (ring.Count > 0 && ring[0].t < now - keep) ring.RemoveAt(0);
        if (cur != null && !cur.ended)
        {
            float dt = Time.unscaledDeltaTime;
            if (ms > cur.sceneWorst) { cur.sceneWorst = ms; cur.sceneWorstAt = now - cur.t0; }
            if (ms > C.hitchMs) cur.hitchFrames++;
            cur.minTs = Mathf.Min(cur.minTs, Time.timeScale); if (Time.timeScale < C.slowBelow) cur.slowS += dt;
            if (Duel.Frozen) cur.frozenS += dt;
            var cam = Camera.main;
            if (cam != null)
            {
                if (camHave) { cur.camMaxJump = Mathf.Max(cur.camMaxJump, Vector3.Distance(camPos, cam.transform.position)); cur.camMaxRot = Mathf.Max(cur.camMaxRot, Quaternion.Angle(camRot, cam.transform.rotation)); }
                camPos = cam.transform.position; camRot = cam.transform.rotation; camHave = true;
            }
            for (int i = 0; i < others.Count; i++) if (others[i] != null) { var p = others[i].transform.position; cur.othersMoved += Vector3.Distance(p, otherPos[i]); otherPos[i] = p; }
        }
        else if (post != null && !post.postDone)
        {
            post.postWorst = Mathf.Max(post.postWorst, ms);
            if (now - post.t1 >= C.postS) { post.postDone = true; var r = post; post = null; if (cur == r) cur = null; Finish(r); }
        }
        if (maxScenes > 0 && recs.Count >= maxScenes && cur == null && post == null) Quit("done: " + recs.Count + " scenes");
        else if (Time.realtimeSinceStartup - born > timeoutS) Quit("timeout after " + timeoutS.ToString("0", IC) + " s, " + recs.Count + " scenes");
    }

    void Finish(Rec r)
    {
        if (recs.Contains(r)) return;
        if (!r.ended) { r.t1 = Time.unscaledTime; r.ended = true; }
        recs.Add(r);
        bool slowOk = r.slowS <= C.maxSlowS, stopOk = r.hitStopMax <= C.maxHitStopMs, cutOk = r.camMaxJump <= C.cutJumpM, worldOk = r.othersAlive == 0 || r.othersMoved >= C.minOthersMovedM;
        bool rtOk = slowOk && stopOk && cutOk && worldOk;
        var sb = new StringBuilder("{\"scene\":" + r.scene + ",\"id\":\"" + r.id + "\",\"killer\":\"" + r.killer + "\",\"victim\":\"" + r.victim + "\",\"sceneS\":" + F(r.t1 - r.t0)
            + ",\"preWorstMs\":" + F(r.preWorst) + ",\"sceneWorstMs\":" + F(r.sceneWorst) + ",\"sceneWorstAtS\":" + F(r.sceneWorstAt) + ",\"postWorstMs\":" + F(r.postWorst) + ",\"hitchFrames\":" + r.hitchFrames
            + ",\"minTimeScale\":" + F(r.minTs) + ",\"slowS\":" + F(r.slowS) + ",\"hitStopMaxMs\":" + F(r.hitStopMax) + ",\"frozenS\":" + F(r.frozenS)
            + ",\"camCuts\":" + r.camCuts + ",\"camMaxJumpM\":" + F(r.camMaxJump) + ",\"camMaxRotDeg\":" + F(r.camMaxRot) + ",\"othersAlive\":" + r.othersAlive + ",\"othersMovedM\":" + F(r.othersMoved)
            + ",\"realtime\":\"" + (rtOk ? "PASS" : "FAIL") + "\",\"why\":\"" + (slowOk ? "" : "slow ") + (stopOk ? "" : "hitstop ") + (cutOk ? "" : "cut ") + (worldOk ? "" : "world-frozen") + "\",\"prewarmDone\":" + (Prewarm92.TotalSeconds >= 0f ? "true" : "false")
            + ",\"timeScaleNow\":" + F(Time.timeScale) + "}");
        Debug.Log("[FinSpike] " + sb);
        if (path != null) { try { File.AppendAllText(path, sb + "\n"); } catch { } }
    }

    bool quitting;
    void Quit(string why)
    {
        if (quitting) return; quitting = true;
        foreach (var r in new List<Rec>(new[] { cur, post })) if (r != null && !recs.Contains(r)) Finish(r);
        Debug.Log("[FinSpike] " + why + " -> " + path);
        Application.Quit(0);
    }
    static string F(float v) { return v.ToString("0.###", IC); }
}
