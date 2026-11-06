using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// WOUND SHOT HARNESS (lane WOUNDSHOT, v84): -woundshot [-out:<dir>] [-woundshotdata:<json>]   (run at 1920x1080: -screen-width 1920 -screen-height 1080 -screen-fullscreen 0; NO -fast / -flags: = the real runtime_flags.json)
// Why: wound decals ship ON (wounds.json decals:true) but no verify / balance capture ever lands a hit while a camera is looking at the victim, so visual-qc could never judge them.
// What: a real fight scene (the scene's own fighters, camera rig, runtime_flags.json, wounds.json, fight_balance.json; brains off so nothing else lands a hit) in which the harness lands N scripted hits from EACH side
// onto the other through the real hit resolver (Duel.Resolve, the same entry point FinisherAuto.ForceKill uses; WoundSystem reacts to the real ContactRelay "Hit" event, nothing is faked). After every landed hit:
//   wait waitAfterHitSec (0.3 s, unscaled), then capture (a) a CLOSE camera on the new wound / hit zone of the victim and (b) the normal gameplay camera frame, each as PNG + CaptureMeta json beside it
//   (hit zone, move, damage, hp, wound count, decal count / live / cap, camera pose, time scale, working set ...).
// Guards (any FAIL = exit 1): a hit that did not land; a landed hit that spawned no decal while wounds.json decals is on; live decals above the row's maxDecals (or total above cap + decalSlack); working set above
// maxWorkingSetGB (sampled every sampleEverySec and at every capture; logged every 5 s); resolution not 1920x1080; -fast flags; WoundSystem missing; a black / white frame; any exception.
// No capture ever happens when a fighter is dead; the run ends right after the last hit (no 140 s tail). Victim hp is topped up to healToFrac (never to full: a full-hp fighter loses its wounds) when the next hit could kill it.
// Output (default <exe dir>\woundshot\): wound_<seq>_<attacker>_to_<victim>_h<k>_<zone>_close.png / _game.png (+ .json each), woundshot_log.txt, woundshot_summary.txt. Exit codes: 0 PASS, 1 FAIL, 2 exception / setup failure, 3 timeout.
// Every number is Assets/Data/woundshot.json (StreamingAssets copy); characters come from Roster.
[DefaultExecutionOrder(-59)]
public class WoundShot : MonoBehaviour
{
    [Serializable]
    public class Cfg
    {
        public bool enabled = true, requireResolution = true, requireRealFlags = true, muteAudio = true;
        public int width = 1920, height = 1080, hitsPerSide = 6, maxRetries = 4;
        public string[] moves = { "thrust", "heavy_overhead", "light_left", "kick", "light_right", "light_overhead" };   // one per hit, in order (torso, head, arm, legs, arm, head); missing ids are skipped
        public float contactDistM = 1.5f, contactMinM = 1.0f, contactStepM = 0.2f;
        public float bootWaitSec = 30f, settleSec = 1f, waitAfterHitSec = 0.3f, recoverMaxSec = 3f, betweenHitsSec = 0.3f, finisherWaitSec = 10f, timeoutSec = 300f, exitGraceSec = 3f;
        public float minLeftFrac = 0.12f, healToFrac = 0.5f, headroomMul = 1.6f;
        public float closeDistM = 0.9f, closeFov = 32f, closeYawDeg = 20f, closeNear = 0.05f, closeLiftM = 0.05f;
        public int closeW = 1920, closeH = 1080;
        public float sampleEverySec = 5f, maxWorkingSetGB = 12f, fadeTol = 0.0005f, lumaMin = 0.01f, lumaMax = 0.95f;
        public int decalSlack = 4;
        // WOUNDGATE v105: every number is woundshot.json (defaults here = the json)
        public float memProbeMinGB = 0.3f, memMaxGrowthGB = 2.0f, decalTexMaxMB = 48f;                         // probe must read at least this (else FAIL probe dead); working-set growth over the run; decal texture memory
        public bool visFail = true, visGameFail = false;                                                        // close frame: wound not visible = FAIL; game frame: info only (the game camera sits behind Joan)
        public float visEpsM = 0.04f, visMargin = 0.08f, visMinFrac = 0.66f;                                    // raycast: a collider closer than this before the decal point blocks it; free border of the frame; share of sample points that must pass
        public float[] visAlong = new float[] { -0.4f, 0f, 0.4f };                                              // sample points along the decal's long axis, as fractions of its length
        public float[] closeYawCandidatesDeg = new float[] { 70f, -70f, 45f, -45f, 25f, -25f, 0f, 90f, -90f, 110f, -110f };   // close camera orbit around the attacker line, the first pose that sees the wound wins
        public bool proofEnabled = true, proofFail = true;                                                      // pixel proof: decal projector on vs off, same frame
        public int proofSettleFrames = 3, proofChannelThr = 12, proofMinPx = 600, proofPadMinPx = 12;          // frames after the toggle (history settle), per-channel diff (0..255), min changed pixels inside the projected box, min padding
        public float proofPadFrac = 0.25f, proofNoiseMaxFrac = 0.25f, proofCentreMin = 0.08f, proofCentreMax = 0.92f;   // box padding; on-vs-on control must stay under this share of the real diff; changed pixels centred inside this band
    }

    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;
    const double GB = 1024.0 * 1024.0 * 1024.0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-woundshot")) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<WoundShot>() != null) return;
            var go = new GameObject("WoundShot"); DontDestroyOnLoad(go); go.AddComponent<WoundShot>();
        }
        catch (Exception e) { Debug.LogError("[woundshot] boot failed: " + e); }
    }

    // ---------------- state ----------------
    Cfg C = new Cfg(); string dir, logPath, dataSrc = "(defaults)";
    float t0, nextSample, exitAt; bool exiting; int exitCode;
    DuelFighter Pl, En; CameraRig rig; Camera mainCam, closeCam; RenderTexture closeRT; Texture2D closeTex;
    readonly List<string> rows = new List<string>(), fails = new List<string>();
    readonly Dictionary<DuelFighter, int> wounds = new Dictionary<DuelFighter, int>();
    DuelFighter watchVictim; bool evSeen; CombatEvent lastEv;
    int seq, landedTotal, plannedTotal, shots, foreignExceptions; double wsMaxGB, privMaxGB; bool aborted; string endReason = "";
    float fpsSum; int fpsN;
    double wsFirstGB = -1, wsLastGB; string memSrc = "none"; bool probeFailed; byte[] proofBuf; int proofPass, proofRun, visPass, visRun, visGamePass, visGameRun; float decalTexMaxSeenMB;   // WOUNDGATE v105
    Vector3 anchor, axis; readonly HashSet<string> errSeen = new HashSet<string>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(7) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[woundshot] " + s);
    }
    void Fail(string s) { fails.Add(s); Log("FAIL " + s); }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:"); if (string.IsNullOrEmpty(o)) o = Arg("-woundout:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "woundshot"); } catch { dir = "woundshot"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "woundshot_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        LoadCfg();
        if (C.muteAudio) AudioListener.volume = 0f;
        Application.logMessageReceived += OnUnityLog; ContactRelay.OnEvent += OnEvent;
    }
    void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityLog; ContactRelay.OnEvent -= OnEvent;
        if (closeRT != null) closeRT.Release();
    }
    void Start() { StartCoroutine(Guarded(Main())); }

    void LoadCfg()
    {
        var paths = new List<string>(); string cli = Arg("-woundshotdata:"); if (!string.IsNullOrEmpty(cli)) paths.Add(cli);
        paths.Add(Path.Combine(Application.streamingAssetsPath, "woundshot.json")); paths.Add(Path.Combine(Application.dataPath, "Data/woundshot.json"));
        foreach (var p in paths)
        {
            try { if (!File.Exists(p)) continue; var c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart('﻿')); if (c != null) { C = c; dataSrc = p; if (C.moves == null || C.moves.Length == 0) C.moves = new Cfg().moves; return; } }
            catch (Exception e) { Debug.LogWarning("[woundshot] cannot read " + p + ": " + e.Message); dataSrc = p + " (unreadable: defaults)"; }
        }
    }

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

    void OnUnityLog(string msg, string stack, LogType type)
    {
        if (exiting) return;
        // v74 died on a per-frame HDRP error about a wound decal with a null material: any error / exception that names a decal or a wound is a FAIL (first 5 distinct ones)
        if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && ((msg ?? "").IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0 || (msg ?? "").IndexOf("wound", StringComparison.OrdinalIgnoreCase) >= 0))
        { string key = (msg ?? "").Length > 80 ? msg.Substring(0, 80) : msg; if (errSeen.Count < 5 && errSeen.Add(key)) Fail("error log naming a decal / wound: " + msg); }
        if (type != LogType.Exception) return;
        bool mine = (stack ?? "").Contains("WoundShot") || (msg ?? "").Contains("WoundShot");
        if (mine) { Log("UNHANDLED EXCEPTION (woundshot): " + msg + "\n" + stack); Fail("exception: " + msg); }
        else { foreignExceptions++; Log("exception outside the harness (counted): " + msg); if (foreignExceptions == 1) Fail("exception outside the harness: " + msg); }
    }

    void OnEvent(CombatEvent e)
    {
        if (e.type != "Hit" || e.target == null) return;
        int n; wounds.TryGetValue(e.target, out n); if (!RTFinisher.Busy) wounds[e.target] = n + 1;
        if (e.target == watchVictim) { evSeen = true; lastEv = e; }
    }

    // ---------------- per frame: time limit, fps, working set every sampleEverySec ----------------
    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > C.exitGraceSec) Environment.Exit(exitCode); return; }
        float dt = Time.unscaledDeltaTime; if (dt > 0f) { fpsSum += 1f / dt; fpsN++; }
        if (Time.realtimeSinceStartup - t0 > C.timeoutSec) { Log("TIMEOUT after " + C.timeoutSec.ToString("F0", IC) + " s"); Finish(3, "timeout"); return; }
        if (Time.realtimeSinceStartup >= nextSample)
        {
            nextSample = Time.realtimeSinceStartup + Mathf.Max(0.5f, C.sampleEverySec);
            try { SampleMemory("sample"); } catch (Exception e) { Log("sample failed: " + e.Message); }
        }
    }

    // working set / private bytes of this process (what Task Manager shows); FAIL + abort over maxWorkingSetGB. Also the live decal count per fighter against its cap.
    void SampleMemory(string why)
    {
        double ws, pv; string src; ReadMem(out ws, out pv, out src);   // WOUNDGATE v105: kernel32 probe (Process.WorkingSet64 read 0.00 GB in v103)
        string d = ""; foreach (var f in new[] { Pl, En }) if (f != null) { int tot, live; Decals(f, out tot, out live); d += " " + f.name + " decals " + live + "/" + tot + " cap " + WoundTable.For(f.CharacterId).maxDecals; }
        float fps = fpsN > 0 ? fpsSum / fpsN : 0f; fpsSum = 0f; fpsN = 0;
        float texMB = DecalTexMB();
        Log("MEM " + why + " workingSet " + ws.ToString("F2", IC) + " GB (max " + wsMaxGB.ToString("F2", IC) + ", limit " + C.maxWorkingSetGB.ToString("F1", IC) + ", first " + wsFirstGB.ToString("F2", IC) + ", growth limit " + C.memMaxGrowthGB.ToString("F1", IC) + ") private " + pv.ToString("F2", IC) + " GB source " + src + " managed " + (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0)).ToString("F0", IC) + " MB decalTex " + texMB.ToString("F1", IC) + " MB fps " + fps.ToString("F0", IC) + " tick " + Duel.TickCount + d);
        CheckMemory(why, ws);
    }

    // WOUNDGATE v105: one probe, used at every sample and every capture. A dead probe (reads under memProbeMinGB) is a FAIL, never a silent 0.00 GB pass.
    void ReadMem(out double ws, out double pv, out string src)
    {
        bool ok = WoundGate.ReadMemory(out ws, out pv, out src); memSrc = src;
        if (ws > wsMaxGB) wsMaxGB = ws; if (pv > privMaxGB) privMaxGB = pv; wsLastGB = ws;
        if (ok && wsFirstGB < 0) wsFirstGB = ws;
        if ((!ok || ws < C.memProbeMinGB) && !probeFailed) { probeFailed = true; Fail("memory probe dead: read " + ws.ToString("F2", IC) + " GB from source " + src + " (need at least " + C.memProbeMinGB.ToString("F2", IC) + " GB): the RAM-leak guard measures nothing"); }
    }
    void CheckMemory(string why, double ws)
    {
        if (ws > C.maxWorkingSetGB) { Fail("working set " + ws.ToString("F2", IC) + " GB is over the " + C.maxWorkingSetGB.ToString("F1", IC) + " GB limit"); aborted = true; endReason = "working set over limit"; Finish(1, "working set over limit"); return; }
        if (wsFirstGB >= 0 && ws - wsFirstGB > C.memMaxGrowthGB) { Fail("working set grew " + (ws - wsFirstGB).ToString("F2", IC) + " GB since the first sample (limit " + C.memMaxGrowthGB.ToString("F1", IC) + " GB): decal / texture leak"); aborted = true; endReason = "working set growth"; Finish(1, "working set growth"); return; }
        float tex = DecalTexMB(); if (tex > C.decalTexMaxMB) Fail("decal textures use " + tex.ToString("F1", IC) + " MB (limit " + C.decalTexMaxMB.ToString("F0", IC) + " MB)");
    }
    // the generated decal maps (WoundDecals.MapSet names them "Wound_*"): runtime size in MB
    float DecalTexMB()
    {
        long bytes = 0; try { foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>()) if (t != null && t.name.StartsWith("Wound_", StringComparison.Ordinal)) bytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t); } catch (Exception) { }
        float mb = (float)(bytes / (1024.0 * 1024.0)); if (mb > decalTexMaxSeenMB) decalTexMaxSeenMB = mb; return mb;
    }

    // ---------------- helpers ----------------
    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }
    IEnumerator Wait(float s) { float u = Time.unscaledTime + s; while (Time.unscaledTime < u && !exiting) yield return null; }

    bool Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>(); bool ground = false;
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) { p.y = hit.point.y; ground = true; }
        if (cc) cc.enabled = false;
        f.transform.position = p; facing.y = 0f; if (facing.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
        return ground;
    }

    // wound decals under a fighter: total (projectors named WoundDecal_*), live = not fading (a fading projector's fadeFactor is below the row's opacity)
    void Decals(DuelFighter f, out int total, out int live)
    {
        total = 0; live = 0; if (f == null) return;
        float op = Mathf.Clamp01(WoundTable.For(f.CharacterId).opacity);
        foreach (var t in f.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("WoundDecal_", StringComparison.Ordinal)) continue;
            total++; var pj = t.GetComponent<DecalProjector>();
            if (pj == null || pj.fadeFactor >= op - C.fadeTol) live++;
        }
    }
    HashSet<Transform> DecalSet(DuelFighter f)
    {
        var set = new HashSet<Transform>(); foreach (var t in f.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("WoundDecal_", StringComparison.Ordinal)) set.Add(t);
        return set;
    }

    static string Esc(string s) { return (s ?? "").Replace("\\", "/").Replace("\"", "'"); }
    static string Num(float v) { return float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("0.###", IC); }
    static float MeanLuma(Texture2D tex)
    {
        float lum = 0f; int n = 0;
        for (int yy = 1; yy < 9; yy++) for (int xx = 1; xx < 16; xx++) { Color c = tex.GetPixel(tex.width * xx / 16, tex.height * yy / 9); lum += (c.r + c.g + c.b) / 3f; n++; }
        return lum / Mathf.Max(1, n);
    }

    void MakeCloseCam()
    {
        closeRT = new RenderTexture(C.closeW, C.closeH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 }; closeRT.Create();
        closeTex = new Texture2D(C.closeW, C.closeH, TextureFormat.RGB24, false, false);
        var go = new GameObject("WoundShotCloseCam"); go.transform.SetParent(transform, false);
        closeCam = go.AddComponent<Camera>(); closeCam.CopyFrom(mainCam);
        closeCam.targetTexture = closeRT; closeCam.fieldOfView = C.closeFov; closeCam.nearClipPlane = C.closeNear; closeCam.enabled = false; closeCam.depth = -50f; closeCam.tag = "Untagged";
        var hd = go.GetComponent<HDAdditionalCameraData>(); if (hd == null) hd = go.AddComponent<HDAdditionalCameraData>();
        var hdMain = mainCam.GetComponent<HDAdditionalCameraData>(); if (hdMain != null) hdMain.CopyTo(hd);
        var al = go.GetComponent<AudioListener>(); if (al) Destroy(al);
    }

    // the close camera: on the target, from the attacker's side, orbiting closeYawCandidatesDeg around the attacker line, closeDistM away (scaled by the victim's size), pulled in when a wall is in the way.
    // WOUNDGATE v105: the first candidate pose from which the decal is in the frame AND not hidden behind the victim's own body / a wall wins (v103 Joan head wound: pose chosen blind, camera at the back of her head).
    // No pose sees it: the best-scoring one is used and the gate below FAILs it.
    float lastYaw;
    Vector3 ClosePose(Vector3 target, DuelFighter victim, DuelFighter attacker, float yawDeg)
    {
        Vector3 toA = attacker.transform.position - target; toA.y = 0f; if (toA.sqrMagnitude < 1e-4f) toA = -victim.transform.forward; toA.Normalize();
        Vector3 d = Quaternion.AngleAxis(yawDeg, Vector3.up) * toA; float dist = C.closeDistM * Mathf.Max(0.5f, victim.scale);
        Vector3 pos = target + d * dist + Vector3.up * C.closeLiftM;
        Vector3 to = pos - target; float len = to.magnitude;
        if (len > 1e-3f)
        {
            var hits = Physics.RaycastAll(target, to / len, len, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore); float best = len;
            foreach (var h in hits) { if (h.collider.GetComponentInParent<DuelFighter>() != null) continue; if (h.distance < best) best = h.distance; }
            if (best < len) pos = target + to / len * Mathf.Max(0.3f, best - 0.05f);
        }
        return pos;
    }
    void AimClose(Vector3 target, DuelFighter victim, DuelFighter attacker, Transform decal)
    {
        float[] yaws = C.closeYawCandidatesDeg != null && C.closeYawCandidatesDeg.Length > 0 ? C.closeYawCandidatesDeg : new[] { C.closeYawDeg };
        var pts = WoundGate.SamplePoints(decal, target, C.visAlong); int mask = ~((1 << 8) | (1 << 9)); float aspect = (float)C.closeW / Mathf.Max(1, C.closeH);
        Vector3 bestPos = Vector3.zero; Quaternion bestRot = Quaternion.identity; float bestFrac = -1f, bestYaw = yaws[0];
        for (int i = 0; i < yaws.Length; i++)
        {
            Vector3 pos = ClosePose(target, victim, attacker, yaws[i]); Quaternion rot = Quaternion.LookRotation(target - pos, Vector3.up);
            var vr = WoundGate.Check(pos, rot, C.closeFov, aspect, C.closeNear, pts, mask, C.visEpsM, C.visMargin);
            if (vr.Frac > bestFrac + 1e-4f) { bestFrac = vr.Frac; bestPos = pos; bestRot = rot; bestYaw = yaws[i]; }
            if (vr.Frac >= 1f - 1e-4f) break;   // everything seen: keep the first (preferred) pose
        }
        lastYaw = bestYaw;
        closeCam.transform.SetPositionAndRotation(bestPos, bestRot);
    }

    // ---------------- main ----------------
    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((Pl = Find(Roster.PlayerObject)) == null || (En = Find(Roster.EnemyObject)) == null || (rig = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > C.bootWaitSec) { Log("fighters / CameraRig not found (" + Roster.PlayerObject + "/" + Roster.EnemyObject + ")"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        mainCam = rig.GetComponent<Camera>(); if (mainCam == null) mainCam = Camera.main;
        if (!C.enabled) { Log("woundshot.json enabled=false"); Finish(0, "disabled"); yield break; }

        // resolution + flags
        if ((Screen.width != C.width || Screen.height != C.height) && C.width > 0 && C.height > 0) { Log("resolution " + Screen.width + "x" + Screen.height + ", asking for " + C.width + "x" + C.height); Screen.SetResolution(C.width, C.height, false); yield return Wait(1.5f); }
        bool resOk = Screen.width == C.width && Screen.height == C.height;
        if (!resOk && C.requireResolution) Fail("resolution is " + Screen.width + "x" + Screen.height + ", need " + C.width + "x" + C.height + " (-screen-width/-screen-height)");
        if (C.requireRealFlags && RuntimeQuality.Fast) Fail("-fast given: the wound frames must use the real runtime_flags.json (no preset)");
        if (FindAnyObjectByType<WoundSystem>() == null) Fail("WoundSystem is not running (wounds.json enabled=false or the file is missing)");
        var WD = WoundTable.Get();
        Log("start res " + Screen.width + "x" + Screen.height + " flags " + RuntimeQuality.FlagsPath + " fast=" + RuntimeQuality.Fast + " data " + dataSrc + " roster " + Roster.PlayerId + " vs " + Roster.EnemyId
            + " wounds enabled=" + WD.enabled + " decals=" + WD.decals + " states=" + WD.states + " maxDecals " + Roster.PlayerId + "=" + WoundTable.For(Roster.PlayerId).maxDecals + " " + Roster.EnemyId + "=" + WoundTable.For(Roster.EnemyId).maxDecals
            + " fightBalance=" + FightBalance.Active + " hitsPerSide " + C.hitsPerSide + " out " + dir);
        SampleMemory("start");
        if (exiting) yield break;

        // a real fight scene: both fighters at their scene start, the real lock-on camera; brains off so only the scripted hits land
        yield return Frames(30);
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        Pl.opponent = En; En.opponent = Pl; Pl.moveDir = Vector3.zero; En.moveDir = Vector3.zero; Pl.guardHeld = false; En.guardHeld = false;
        rig.lockEnabled = true; rig.lockRange = 80f; rig.enabled = true;
        Time.timeScale = 1f;
        Vector3 pa = Pl.transform.position, pb = En.transform.position; axis = pb - pa; axis.y = 0f; if (axis.sqrMagnitude < 0.01f) axis = Vector3.forward; axis.Normalize();
        anchor = (pa + pb) * 0.5f;
        MakeCloseCam();
        yield return Wait(C.settleSec);

        plannedTotal = 2 * C.hitsPerSide;
        // player hits the enemy, then the enemy hits the player
        for (int side = 0; side < 2 && !aborted && !exiting; side++)
        {
            DuelFighter A = side == 0 ? Pl : En, V = side == 0 ? En : Pl;
            Log("=== " + A.name + " -> " + V.name + ": " + C.hitsPerSide + " hits ===");
            for (int k = 0; k < C.hitsPerSide && !aborted && !exiting; k++) yield return OneHit(A, V, k);
        }
        if (exiting) yield break;

        // end: let the evicted decals finish fading, then the cap must hold exactly (no leak)
        if (!aborted)
        {
            float fade = Mathf.Max(WoundTable.For(Roster.PlayerId).fadeOutSec, WoundTable.For(Roster.EnemyId).fadeOutSec);
            yield return Wait(fade + 0.5f);
            foreach (var f in new[] { Pl, En })
            {
                int tot, live; Decals(f, out tot, out live); int cap = WoundTable.For(f.CharacterId).maxDecals;
                string row = "final " + f.name + ": decals total " + tot + " live " + live + " cap " + cap + " wounds taken " + (wounds.ContainsKey(f) ? wounds[f] : 0);
                rows.Add(row); Log(row);
                if (WD.decals && tot > cap) Fail(f.name + " has " + tot + " decals after the fade-out, cap " + cap + " (leak)");
            }
            if (landedTotal < plannedTotal) Fail("only " + landedTotal + " of " + plannedTotal + " hits landed and were captured");
            endReason = "all hits done";
        }
        SampleMemory("end");
        Finish(fails.Count == 0 ? 0 : 1, fails.Count == 0 ? "all checks passed" : fails.Count + " check(s) failed");
    }

    // ---------------- one scripted hit + its two captures ----------------
    IEnumerator OneHit(DuelFighter A, DuelFighter V, int k)
    {
        // 1) both fighters upright and in contact range at their natural sides of the arena, nobody guarding, no posture break (a deathblow would start a finisher), a finisher never running
        float fw = Time.unscaledTime + C.finisherWaitSec; while ((RTFinisher.Busy || Duel.Frozen || PresentationClock.Frozen) && Time.unscaledTime < fw) yield return null;
        if (RTFinisher.Busy) { Fail("a finisher is still running after " + C.finisherWaitSec.ToString("F0", IC) + " s"); aborted = true; endReason = "finisher stuck"; yield break; }
        if (A.Dead || V.Dead) { Die("before hit " + (k + 1), A, V); yield break; }
        string moveId = null; MoveDef mv = null;
        for (int i = 0; i < C.moves.Length && mv == null; i++) { string id = C.moves[(k + i) % C.moves.Length]; var m = A.moves != null ? A.moves.Get(id) : null; if (m != null) { mv = m; moveId = id; } else if (i == 0) Log("move '" + id + "' missing for " + A.name + ", trying the next one"); }
        if (mv == null) { Fail("no usable move in woundshot.json moves for " + A.name); aborted = true; endReason = "no move"; yield break; }

        // hp headroom: never kill, never refill to full (a full-hp fighter loses its wounds)
        float expect = Mathf.Max(1f, mv.damage) * A.DamageScaleNow * C.headroomMul, healed = 0f;
        if (V.health - expect < C.minLeftFrac * V.maxHealth) { float to = Mathf.Min(V.maxHealth - 1f, V.maxHealth * C.healToFrac); healed = to - V.health; V.health = to; var hp = V.GetComponent<Health>(); if (hp != null) hp.current = to; }
        int woundsBefore; wounds.TryGetValue(V, out woundsBefore);
        float hp0 = V.health; var before = DecalSet(V); int tries = 0; bool landed = false; float dist = C.contactDistM;
        watchVictim = V;
        while (tries <= C.maxRetries && !landed)
        {
            tries++; evSeen = false;
            Stand(A, V, dist);
            yield return Frames(2);
            if (A.Dead || V.Dead) { Die("while setting up hit " + (k + 1), A, V); yield break; }
            MoveDef prev = A.move; A.move = mv;                                                  // the event carries the move id / zone from CurrentMove
            bool contact = Duel.Resolve(A, V, mv, 1f);
            A.move = prev;
            landed = evSeen;
            if (!landed) { Log("  hit " + (k + 1) + " try " + tries + ": " + (contact ? "contact but no clean hit (blocked / parried)" : "out of reach / arc") + " at " + dist.ToString("F2", IC) + " m"); dist = Mathf.Max(C.contactMinM, dist - C.contactStepM); V.guardHeld = false; yield return Frames(2); }
        }
        watchVictim = null;
        if (!landed) { Fail(A.name + " -> " + V.name + " hit " + (k + 1) + " (" + moveId + ") did not land after " + tries + " tries"); yield break; }
        float tHit = Time.unscaledTime; var ev = lastEv;
        string zone = !string.IsNullOrEmpty(ev.hitLocation) ? ev.hitLocation : ev.location;

        // 2) the new wound (decal) on the victim; the close camera follows it for the 0.3 s so its HDRP history is warm
        Transform nd = null; foreach (var t in V.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("WoundDecal_", StringComparison.Ordinal) && !before.Contains(t)) { nd = t; }
        var WD = WoundTable.Get(); var row = WoundTable.For(V.CharacterId);
        if (WD.enabled && WD.decals && nd == null) Fail(A.name + " -> " + V.name + " hit " + (k + 1) + " (" + moveId + ", zone " + zone + ") landed but no WoundDecal_* appeared on " + V.name);
        float zoneY = zone == "head" ? row.headY : (zone == "arm" || zone == "arms") ? row.armY : zone == "legs" ? row.legsY : row.torsoY;
        closeCam.enabled = true;
        while (Time.unscaledTime - tHit < C.waitAfterHitSec)
        {
            AimClose(TargetOf(nd, V, zoneY), V, A, nd); yield return null;
            if (exiting) yield break;
            if (V.Dead || A.Dead) { closeCam.enabled = false; Die("within " + C.waitAfterHitSec.ToString("F1", IC) + " s of hit " + (k + 1), A, V); yield break; }
        }
        Vector3 target = TargetOf(nd, V, zoneY); AimClose(target, V, A, nd);
        float waited = Time.unscaledTime - tHit;
        yield return new WaitForEndOfFrame();
        if (exiting) yield break;
        if (V.Dead || A.Dead) { closeCam.enabled = false; Die("at the capture of hit " + (k + 1), A, V); yield break; }   // no capture past death

        // 3) capture: close RT + gameplay screen, same frame
        seq++; landedTotal++;
        int tot, live; Decals(V, out tot, out live); int cap = row.maxDecals; int wn; wounds.TryGetValue(V, out wn);
        double ws, wsPriv; string wsSrc; ReadMem(out ws, out wsPriv, out wsSrc);   // WOUNDGATE v105
        string stem = "wound_" + seq.ToString("00") + "_" + A.CharacterId + "_to_" + V.CharacterId + "_h" + (k + 1) + "_" + zone;
        float dmg = hp0 - V.health;
        Vector3 vpC = closeCam.WorldToViewportPoint(target), vpM = mainCam.WorldToViewportPoint(target);
        bool boundsClose = vpC.z > 0f && vpC.x > 0f && vpC.x < 1f && vpC.y > 0f && vpC.y < 1f, boundsGame = vpM.z > 0f && vpM.x > 0f && vpM.x < 1f && vpM.y > 0f && vpM.y < 1f;   // v103's whole "inView": bounds only, kept as info
        // WOUNDGATE v105: real visibility = in the frame with a free border AND no collider (the victim's own body included) between the camera and the decal samples
        var visPts = WoundGate.SamplePoints(nd, target, C.visAlong); int visMask = ~((1 << 8) | (1 << 9));
        var visC = WoundGate.Check(closeCam.transform.position, closeCam.transform.rotation, closeCam.fieldOfView, (float)C.closeW / Mathf.Max(1, C.closeH), C.closeNear, visPts, visMask, C.visEpsM, C.visMargin);
        var visG = WoundGate.Check(mainCam.transform.position, mainCam.transform.rotation, mainCam.fieldOfView, mainCam.aspect, mainCam.nearClipPlane, visPts, visMask, C.visEpsM, C.visMargin);
        bool inClose = visC.Frac >= C.visMinFrac, inGame = visG.Frac >= C.visMinFrac;
        visRun++; if (inClose) visPass++; visGameRun++; if (inGame) visGamePass++;
        var sb = new StringBuilder();
        sb.Append("\"harness\":\"woundshot\",\"seq\":" + seq + ",\"attacker\":\"" + Esc(A.CharacterId) + "\",\"victim\":\"" + Esc(V.CharacterId) + "\",\"side\":\"" + (A == Pl ? "player>enemy" : "enemy>player") + "\",\"hitIndex\":" + (k + 1) + ",\"hitsPerSide\":" + C.hitsPerSide
            + ",\"moveId\":\"" + Esc(moveId) + "\",\"hitZone\":\"" + Esc(zone) + "\",\"swingZone\":\"" + Esc(ev.location) + "\",\"hitLocation\":\"" + Esc(ev.hitLocation) + "\",\"hitDirection\":\"" + Esc(ev.hitDirection) + "\",\"swingDirection\":\"" + Esc(ev.direction)
            + "\",\"heavy\":" + (ev.heavy ? "true" : "false") + ",\"counter\":" + (ev.counter ? "true" : "false") + ",\"rear\":" + (ev.rear ? "true" : "false") + ",\"moveDamage\":" + Num(mv.damage) + ",\"damage\":" + Num(dmg)
            + ",\"victimHp\":" + Num(V.health) + ",\"victimMaxHp\":" + Num(V.maxHealth) + ",\"healedBefore\":" + Num(healed) + ",\"woundCount\":" + wn + ",\"woundCountBefore\":" + woundsBefore
            + ",\"decalNew\":" + (nd != null ? "true" : "false") + ",\"decalCount\":" + tot + ",\"decalLive\":" + live + ",\"decalCap\":" + cap + ",\"decalPos\":\"" + target.ToString("F2") + "\",\"decalInCloseView\":" + (inClose ? "true" : "false") + ",\"decalInGameView\":" + (inGame ? "true" : "false") + ",\"decalBoundsInClose\":" + (boundsClose ? "true" : "false") + ",\"decalBoundsInGame\":" + (boundsGame ? "true" : "false") + ",\"visCloseFrac\":" + Num(visC.Frac) + ",\"visCloseBlocker\":\"" + Esc(visC.blocker) + "\",\"visGameFrac\":" + Num(visG.Frac) + ",\"visGameBlocker\":\"" + Esc(visG.blocker) + "\",\"closeYawDeg\":" + Num(lastYaw)
            + ",\"staminaCeil\":" + Num(WoundSystem.StaminaCeil(V)) + ",\"moveMul\":" + Num(WoundSystem.MoveMul(V)) + ",\"waitedSec\":" + Num(waited) + ",\"timeScale\":" + Num(Time.timeScale) + ",\"presentationFrozen\":" + (PresentationClock.Frozen ? "true" : "false")
            + ",\"tick\":" + Duel.TickCount + ",\"screen\":\"" + Screen.width + "x" + Screen.height + "\",\"workingSetGB\":" + Num((float)ws) + ",\"memSource\":\"" + wsSrc + "\",\"decalTexMB\":" + Num(DecalTexMB()) + ",\"wallSec\":" + Num(Time.realtimeSinceStartup - t0));
        string common = sb.ToString();
        CaptureUtil.HideConsole();
        bool bad = false; float lumaC = -1f, lumaG = -1f; string pathC = Path.Combine(dir, stem + "_close.png"), pathG = Path.Combine(dir, stem + "_game.png");
        try
        {
            var prev = RenderTexture.active; RenderTexture.active = closeRT;
            closeTex.ReadPixels(new Rect(0, 0, C.closeW, C.closeH), 0, 0, false); closeTex.Apply(false); RenderTexture.active = prev;
            lumaC = MeanLuma(closeTex); File.WriteAllBytes(pathC, closeTex.EncodeToPNG());
            CaptureMeta.WriteFor(pathC, common + ",\"kind\":\"close\",\"closeCamPos\":\"" + closeCam.transform.position.ToString("F2") + "\",\"closeFov\":" + Num(closeCam.fieldOfView) + ",\"luma\":" + Num(lumaC));
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            shots++;
            if (tex != null) { lumaG = MeanLuma(tex); File.WriteAllBytes(pathG, tex.EncodeToPNG()); Destroy(tex); CaptureMeta.WriteFor(pathG, common + ",\"kind\":\"game\",\"luma\":" + Num(lumaG)); shots++; }
            else { Fail(stem + ": game screenshot failed"); bad = true; }
        }
        catch (Exception e) { Fail(stem + ": capture failed: " + e.Message); bad = true; }
        // WOUNDGATE v105: pixel proof (decal projector on vs off in the same close frame), then the visibility FAILs
        string proofTxt = "n/a";
        if (C.proofEnabled && nd != null && lumaC >= 0f) { yield return ProveClose(nd, stem); proofTxt = lastProofTxt; if (exiting) yield break; }
        if (C.visFail && !inClose) { Fail(stem + ": wound not visible in the close frame (visible " + visC.Frac.ToString("F2", IC) + " < " + C.visMinFrac.ToString("F2", IC) + ", blocker " + (visC.blocker.Length > 0 ? visC.blocker : "none / outside the frame") + ", yaw " + lastYaw.ToString("F0", IC) + ")"); bad = true; }
        if (C.visGameFail && !inGame) { Fail(stem + ": wound not visible in the game frame (visible " + visG.Frac.ToString("F2", IC) + ", blocker " + (visG.blocker.Length > 0 ? visG.blocker : "none / outside the frame") + ")"); bad = true; }
        if (proofBad) { bad = true; proofBad = false; }
        closeCam.enabled = false;
        if (lumaC >= 0f && (lumaC < C.lumaMin || lumaC > C.lumaMax)) { Fail(stem + ": close frame is black / white (luma " + lumaC.ToString("F3", IC) + ")"); bad = true; }
        if (lumaG >= 0f && (lumaG < C.lumaMin || lumaG > C.lumaMax)) { Fail(stem + ": game frame is black / white (luma " + lumaG.ToString("F3", IC) + ")"); bad = true; }

        // 4) checks on the decal set
        if (WD.decals)
        {
            if (live > cap) { Fail(stem + ": " + live + " live decals on " + V.name + ", cap " + cap); bad = true; }
            if (tot > cap + C.decalSlack) { Fail(stem + ": " + tot + " decals on " + V.name + " (cap " + cap + " + " + C.decalSlack + " fading): leak"); bad = true; }
        }
        if (ws > C.maxWorkingSetGB) { Fail(stem + ": working set " + ws.ToString("F2", IC) + " GB over the " + C.maxWorkingSetGB.ToString("F1", IC) + " GB limit"); aborted = true; endReason = "working set over limit"; Finish(1, "working set over limit"); yield break; }
        string line = stem + ": " + A.name + " " + moveId + " -> " + V.name + " zone " + zone + " dmg " + dmg.ToString("F1", IC) + " hp " + V.health.ToString("F0", IC) + "/" + V.maxHealth.ToString("F0", IC) + " wounds " + wn + " decals live " + live + "/total " + tot + "/cap " + cap
            + " new=" + (nd != null) + " inView close=" + inClose + "(" + visC.Frac.ToString("F2", IC) + ", bounds " + boundsClose + ", yaw " + lastYaw.ToString("F0", IC) + ") game=" + inGame + "(" + visG.Frac.ToString("F2", IC) + ", bounds " + boundsGame + ") proof " + proofTxt + " luma " + lumaC.ToString("F3", IC) + "/" + lumaG.ToString("F3", IC) + " waited " + waited.ToString("F2", IC) + " s ws " + ws.ToString("F2", IC) + " GB" + (bad ? "  <== FAIL" : "");
        rows.Add(line); Log(line);

        // 5) let the victim recover, then the next hit
        float rec = Time.unscaledTime + C.recoverMaxSec; while (V.state != FState.Loco && !V.Dead && Time.unscaledTime < rec) yield return null;
        yield return Wait(C.betweenHitsSec);
    }

    // WOUNDGATE v105: the pixel proof. closeTex holds the close frame with the wound ON (already read and written). Switch the decal projector (or fallback quad) off, let HDRP's history settle,
    // read the same camera again (OFF), switch it back on, settle, read again (control, ON vs ON = noise + motion). The wound counts as in frame when enough pixels inside its projected box differ
    // between ON and OFF, the control stays small and the changed pixels sit inside the centre band. A read problem is a FAIL, never a pass.
    string lastProofTxt = ""; bool proofBad;
    IEnumerator ProveClose(Transform nd, string stem)
    {
        proofRun++; lastProofTxt = "run";
        int w = C.closeW, h = C.closeH; string why = null; int cnt = 0, noise = 0, full = 0, boxW = 0, boxH = 0, rx0 = 0, ry0 = 0, rx1 = -1, ry1 = -1; float ctrX = -1f, ctrY = -1f;
        var pj = nd.GetComponent<DecalProjector>(); var mr = nd.GetComponent<MeshRenderer>(); bool pjWas = pj != null && pj.enabled, mrWas = mr != null && mr.enabled;
        var raw0 = closeTex.GetRawTextureData<byte>();
        if (raw0.Length != w * h * 3) why = "unexpected raw texture size " + raw0.Length + " (need " + (w * h * 3) + ")";
        else
        {
            if (proofBuf == null || proofBuf.Length != raw0.Length) proofBuf = new byte[raw0.Length];
            raw0.CopyTo(proofBuf);
            WoundGate.ProjectBox(closeCam, nd, w, h, C.proofPadFrac, C.proofPadMinPx, out rx0, out ry0, out rx1, out ry1, out full);
            if (rx1 < rx0 || ry1 < ry0 || full <= 0) why = "decal box not in front of the close camera";
        }
        if (why == null)
        {
            if (pj != null) pj.enabled = false;
            if (mr != null) mr.enabled = false;
            for (int i = 0; i < C.proofSettleFrames; i++) { yield return null; if (exiting) yield break; }
            yield return new WaitForEndOfFrame(); if (exiting) yield break;
            try
            {
                var prev = RenderTexture.active; RenderTexture.active = closeRT; closeTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); RenderTexture.active = prev;
                var dr = new WoundGate.DiffResult(); cnt = WoundGate.CountChanged(proofBuf, closeTex.GetRawTextureData<byte>(), w, rx0, ry0, rx1, ry1, C.proofChannelThr, dr); ctrX = dr.CentreX(w); ctrY = dr.CentreY(h);
            }
            catch (Exception e) { why = "read OFF frame failed: " + e.Message; }
            if (pj != null) pj.enabled = pjWas;   // always put the wound back
            if (mr != null) mr.enabled = mrWas;
            for (int i = 0; i < C.proofSettleFrames; i++) { yield return null; if (exiting) yield break; }
            yield return new WaitForEndOfFrame(); if (exiting) yield break;
            try
            {
                var prev = RenderTexture.active; RenderTexture.active = closeRT; closeTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); RenderTexture.active = prev;
                noise = WoundGate.CountChanged(proofBuf, closeTex.GetRawTextureData<byte>(), w, rx0, ry0, rx1, ry1, C.proofChannelThr, null);
            }
            catch (Exception e) { if (why == null) why = "read control frame failed: " + e.Message; }
        }
        if (why == null)
        {
            boxW = rx1 - rx0 + 1; boxH = ry1 - ry0 + 1;
            if (cnt < C.proofMinPx) why = "only " + cnt + " pixels change with the wound switched off (need " + C.proofMinPx + ") in box " + boxW + "x" + boxH;
            else if (noise > C.proofNoiseMaxFrac * cnt) why = "control (ON vs ON) changes " + noise + " pixels vs " + cnt + " for ON vs OFF: the diff is noise / motion, not the wound";
            else if (ctrX < C.proofCentreMin || ctrX > C.proofCentreMax || ctrY < C.proofCentreMin || ctrY > C.proofCentreMax) why = "changed pixels centre at (" + ctrX.ToString("F2", IC) + ", " + ctrY.ToString("F2", IC) + "), outside the frame band " + C.proofCentreMin.ToString("F2", IC) + ".." + C.proofCentreMax.ToString("F2", IC);
        }
        lastProofTxt = (why == null ? "PASS" : "FAIL") + " diff " + cnt + " px (control " + noise + ", box " + boxW + "x" + boxH + ", centre " + ctrX.ToString("F2", IC) + "/" + ctrY.ToString("F2", IC) + ")";
        if (why == null) proofPass++;
        else if (C.proofFail) { Fail(stem + ": pixel proof, the wound is not in the close frame: " + why); proofBad = true; }
        else Log("  pixel proof (info, proofFail=false): " + why);
        Log("  proof " + stem + " " + lastProofTxt);
    }

    Vector3 TargetOf(Transform decal, DuelFighter V, float zoneY)
    {
        if (decal != null) return decal.position;
        return V.transform.position + Vector3.up * zoneY * Mathf.Max(0.01f, V.scale);
    }

    // attacker and victim upright, facing each other, `dist` apart across the arena anchor (player on the near side, as in the real fight), no guard, no posture, full stamina
    void Stand(DuelFighter A, DuelFighter V, float dist)
    {
        Vector3 half = axis * (dist * 0.5f);
        Vector3 plPos = anchor - half, enPos = anchor + half;
        Place(Pl, plPos, axis); Place(En, enPos, -axis);
        foreach (var f in new[] { A, V })
        {
            f.moveDir = Vector3.zero; f.guardHeld = false; f.run = false; f.SetKnock(Vector3.zero);
            if (!f.Dead && f.state != FState.Loco) { f.state = FState.Loco; f.t = 0; }
            f.posture = 0f; f.stamina = f.maxStamina;
        }
        Pl.opponent = En; En.opponent = Pl;
    }

    void Die(string when, DuelFighter A, DuelFighter V)
    {
        string who = (A != null && A.Dead ? A.name : V.name) + " died " + when;
        Fail(who + ": run ends, no capture past death"); aborted = true; endReason = who;
    }

    // ---------------- end ----------------
    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; Time.timeScale = 1f;
        if (closeCam != null) closeCam.enabled = false;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("woundshot_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height + "  flags " + RuntimeQuality.FlagsPath + " fast=" + RuntimeQuality.Fast);
            sb.AppendLine("VERDICT: " + (code == 0 ? "PASS" : "FAIL") + "   (exit code " + code + ", " + why + ")");
            sb.AppendLine("hits landed+captured " + landedTotal + "/" + plannedTotal + ", PNGs " + shots + ", ended: " + endReason + ", working set max " + wsMaxGB.ToString("F2", IC) + " GB (limit " + C.maxWorkingSetGB.ToString("F1", IC) + "), private max " + privMaxGB.ToString("F2", IC) + " GB, memory probe " + memSrc + " first " + wsFirstGB.ToString("F2", IC) + " last " + wsLastGB.ToString("F2", IC) + " GB, decal textures max " + decalTexMaxSeenMB.ToString("F1", IC) + " MB, visible close " + visPass + "/" + visRun + " game " + visGamePass + "/" + visGameRun + ", pixel proof " + proofPass + "/" + proofRun + ", wall " + (Time.realtimeSinceStartup - t0).ToString("F0", IC) + " s");
            foreach (var r in rows) sb.AppendLine("  " + r);
            foreach (var f in fails) sb.AppendLine("  FAIL " + f);
            File.WriteAllText(Path.Combine(dir, "woundshot_summary.txt"), sb.ToString());
        }
        catch (Exception e) { Log("summary failed: " + e.Message); }
        Log("EXIT code " + code + " (" + why + ")");
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}
