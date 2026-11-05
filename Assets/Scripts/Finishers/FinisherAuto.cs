using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// -finisherauto verify harness for the real-time finishers (RTFinisher / SteerFinisherHost / FinisherPlayer.Run).
// For EACH enabled entry of finishers.json "entries" (or -finisherlist:id1,id2) it restarts the scene, steers the roster player with the aggressive AI (its AIBrain on, player brain off),
// sets the victim's HP to 1 and lets the killer land a hit. After auto.killWaitSeconds without a kill it forces the lethal blow by calling Duel.Resolve itself (logged "forced").
// The real Duel.Resolve -> RTFinisher.TryBegin hook is what starts the scene, so the whole chain is exercised. Entries with attackerSide "enemy" (or the enemy id) make the player the victim (its AI is switched off).
// Per finisher it logs: id, clips used, duration, camera cuts, slow-mo / gore counts, ok / failed + exceptions; takes auto.shotsPerFinisher (4) screenshots.
//   <out>\finisher_auto_log.txt       one line per event, written immediately
//   <out>\finisher_shots\NN_<id>_k_<label>.png
//   <out>\finisher_auto_summary.txt   VERDICT PASS|FAIL + one block per finisher + fps
//   exit code: 0 = every finisher PASS, 2 = a finisher FAILED or an exception, 3 = -finishertimeout:<s> (default boot + n * auto.perFinisherSeconds)
// Options: -finisherlist:<ids> -finisherout:<dir> (default the exe dir) -finishertimeout:<s> -finisherdisabled (include entries with enabled=false)
// Finishers stay OFF in the shipped data; this harness switches them on in memory only (RTFinisher.ForceEnable) and never writes the rotation file.
[DefaultExecutionOrder(-70)]
public class FinisherAuto : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    public static bool Requested { get { return Flag("-finisherauto"); } }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Requested) return;
        try
        {
            Application.runInBackground = true;
            if (FindAnyObjectByType<FinisherAuto>() != null) return;
            var go = new GameObject("FinisherAuto"); DontDestroyOnLoad(go); go.AddComponent<FinisherAuto>();
        }
        catch (Exception e) { Debug.LogError("[finisherauto] boot failed: " + e); }
    }

    class Rec
    {
        public string id = "", killerName = "", victimName = "", attackerClip = "", victimClip = "", attackerPlayed = "", victimPlayed = "", msg = "", cause = "";
        public bool started, ended, endOk, forced, contact, victimDead, timeOk, frozenClear, camOk, done, pass;
        public float tStart, tEnd, tContact = -1f, tKill = -1f, duration, fightSeconds;
        public int shots, slowmo, gore, exceptions;
        public float paced, pacedKill = -1f, refLuma = -1f, clearHead = float.NaN, clearHand = float.NaN; public int stalls; public bool lumaOk = true;   // V61C
        public readonly List<float> lumas = new List<float>(); public readonly List<string> lumaLabels = new List<string>();
        public string groundLine = "", headLine = "", vigLine = "", pairLine = "", lookLine = "";   // V62B
        public bool sampDone; public string sheetFile = ""; public Texture2D sheetSmall;   // V61B contact sheet
        public readonly List<Texture2D> thumbs = new List<Texture2D>(); public readonly List<string> thumbLabels = new List<string>();
        public readonly List<string> cuts = new List<string>(), notes = new List<string>(), files = new List<string>(), warns = new List<string>(), errors = new List<string>();
    }

    FinisherAutoCfg C = new FinisherAutoCfg();
    string dir, logPath, shotDir, summaryPath;
    float t0, timeout, exitAt, baseScale = 1f; bool exiting; int exitCode;
    readonly List<Rec> recs = new List<Rec>();
    Rec cur;
    DuelFighter D, P, K, V;
    AIBrain dBrain; PlayerBrain dPlayer;
    FinisherEntry entry;
    string[] only;
    readonly StringBuilder pending = new StringBuilder();
    int totalExceptions, foreignExceptions; readonly List<string> exceptionList = new List<string>();
    float fpsMin = 9999f, fpsSum; int fpsN; bool shotFrame; string timedOutWhy;
    bool rotOk = true; string rotReport = "";   // V62: FinisherRotationCheck result (every run; -finisherrotonly = only this, no scene)
    void OnPairProbe(string s) { if (cur != null) cur.notes.Add(s); Log(s); }

    // ------------------------------------------------------------------ lifecycle
    void Awake()
    {
        t0 = Time.realtimeSinceStartup; baseScale = Time.timeScale <= 0f ? 1f : Time.timeScale;
        try
        {
            string o = Arg("-finisherout:");
            try { dir = !string.IsNullOrEmpty(o) ? o : Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)); } catch { dir = "."; }
            if (string.IsNullOrEmpty(dir)) dir = ".";
            Directory.CreateDirectory(dir);
            logPath = Path.Combine(dir, "finisher_auto_log.txt"); shotDir = Path.Combine(dir, "finisher_shots"); summaryPath = Path.Combine(dir, "finisher_auto_summary.txt");
            try { File.WriteAllText(logPath, ""); } catch (Exception e) { Debug.LogWarning("[finisherauto] cannot write " + logPath + ": " + e.Message); }
            try { if (File.Exists(summaryPath)) File.Delete(summaryPath); } catch { }
            try { Directory.CreateDirectory(shotDir); } catch (Exception e) { Debug.LogWarning("[finisherauto] cannot create " + shotDir + ": " + e.Message); }
            C = RTFinisher.Cfg.auto ?? new FinisherAutoCfg();
            string lst = Arg("-finisherlist:"); if (!string.IsNullOrEmpty(lst)) only = lst.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            RTFinisher.ForceEnable = true; FinisherRotationStore.Suspended = true;
            FinisherPlayer.ProbeEnabled = C.pairProbe; FinisherPlayer.OnPairProbe += OnPairProbe;   // V62
            Application.logMessageReceived += OnUnityLog;
            RTFinisher.OnStart += OnStart; RTFinisher.OnStage += OnStage; RTFinisher.OnEnd += OnEnd;
            FinisherPlayer.OnGore += OnGore;
            ContactRelay.OnKilled += OnKilled;
            Log("finisherauto start " + DateTime.Now.ToString("s") + " unity " + Application.unityVersion + " res " + Screen.width + "x" + Screen.height + " out " + dir);
        }
        catch (Exception e) { Fail("Awake: " + e); }
    }

    void Start() { StartCoroutine(Guard(Main())); }

    void OnDestroy()
    {
        Application.logMessageReceived -= OnUnityLog;
        RTFinisher.OnStart -= OnStart; RTFinisher.OnStage -= OnStage; RTFinisher.OnEnd -= OnEnd;
        FinisherPlayer.OnGore -= OnGore; ContactRelay.OnKilled -= OnKilled;
        FinisherPlayer.OnPairProbe -= OnPairProbe; FinisherPlayer.ProbeEnabled = false;   // V62
    }

    // ------------------------------------------------------------------ logging / exit
    void Log(string line)
    {
        string s = "[" + (Time.realtimeSinceStartup - t0).ToString("0.00", IC) + "] " + line;
        Debug.Log("[finisherauto] " + line);
        try { File.AppendAllText(logPath, s + "\n"); } catch { }
    }

    void Fail(string msg)
    {
        totalExceptions++; exceptionList.Add(msg); Log("ERROR: " + msg);
        if (cur != null) { cur.errors.Add(msg); cur.exceptions++; }
        Exit(2, msg);
    }

    void Exit(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime;
        try { Time.timeScale = baseScale; } catch { }
        try { WriteSummary(code, why); } catch (Exception e) { Log("summary failed: " + e); }
        Log("EXIT code " + code + " (" + why + ")");
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    void OnUnityLog(string msg, string stack, LogType type)
    {
        if (type != LogType.Exception || exiting) return;
        string one = (msg ?? "") + " | " + FirstLine(stack);
        totalExceptions++; exceptionList.Add(one);
        if (cur != null && !cur.done) { cur.exceptions++; cur.errors.Add("exception: " + one); }
        else foreignExceptions++;
        Log("EXCEPTION: " + msg + "\n" + stack);
    }
    static string FirstLine(string s) { if (string.IsNullOrEmpty(s)) return ""; int i = s.IndexOf('\n'); return i < 0 ? s : s.Substring(0, i); }

    IEnumerator Guard(IEnumerator it)
    {
        while (!exiting)
        {
            bool more;
            try { more = it.MoveNext(); } catch (Exception e) { Fail("Main: " + e); yield break; }
            if (!more) yield break;
            yield return it.Current;
        }
    }

    void Update()
    {
        if (exiting)
        {
#if !UNITY_EDITOR
            if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode);
#endif
            return;
        }
        float dt = Time.unscaledDeltaTime;
        CaptureUtil.HideConsole();   // V61C: no red console lines in any captured frame
        if (cur != null && cur.started && !cur.ended) { if (dt > 0.25f) { cur.stalls++; cur.paced += 1f / 60f; } else cur.paced += dt; }   // V61C: scene length without capture stalls / hitches
        if (shotFrame) shotFrame = false;
        else if (dt > 0f && Time.frameCount > 10) { float fps = 1f / dt; if (fps < fpsMin) fpsMin = fps; fpsSum += fps; fpsN++; }
        if (timeout > 0f && Time.realtimeSinceStartup - t0 > timeout && timedOutWhy == null)
        {
            timedOutWhy = "timeout " + timeout.ToString("0") + " s";
            if (cur != null && !cur.done) { cur.errors.Add("timeout"); cur.cause = "timeout"; }
            Exit(3, timedOutWhy);
        }
    }

    // ------------------------------------------------------------------ events from RTFinisher
    void OnStart(FinisherEntry e, DuelFighter k, DuelFighter v)
    {
        if (cur == null) return;
        cur.started = true; cur.tStart = Time.unscaledTime; FinisherPlayer.ClearHead = FinisherPlayer.ClearHand = float.NaN; cur.killerName = k.name; cur.victimName = v.name;
        Log("  scene start id=" + e.id + " killer=" + k.name + " victim=" + v.name + (cur.forced ? " (forced blow)" : " (natural blow)"));
    }
    void OnStage(string what, string detail)
    {
        if (cur == null || !cur.started) return;
        if (what == "contact" && !cur.contact) { cur.contact = true; cur.tContact = Time.unscaledTime; Log("  contact " + detail); }
        else if (what == "shot") { cur.cuts.Add(detail); }
        else if (what == "slowmo") { cur.slowmo++; }
        else if (what == "notify") { cur.notes.Add(detail); }
    }
    void OnEnd(bool ok, string msg)
    {
        if (cur == null) return;
        cur.ended = true; cur.endOk = ok; cur.tEnd = Time.unscaledTime; cur.duration = cur.tEnd - cur.tStart; if (!ok) cur.errors.Add("scene: " + msg); else if (!string.IsNullOrEmpty(msg)) cur.warns.Add(msg);
        Log("  scene end ok=" + ok + " " + msg + " duration=" + cur.duration.ToString("0.00", IC) + "s");
    }
    void OnGore(FinisherGoreCall g) { if (cur != null && cur.started) { cur.gore++; Log("  gore " + g.type + " unit=" + g.unit); } }
    void OnKilled(DuelFighter f, MoveDef m) { if (cur != null && cur.started && cur.tKill < 0f && f == V) { cur.tKill = Time.unscaledTime; cur.pacedKill = cur.paced; Log("  victim Die() at +" + (cur.tKill - cur.tStart).ToString("0.00", IC) + "s"); } }

    // ------------------------------------------------------------------ main
    IEnumerator Main()
    {
        yield return null; yield return null;
        var cfg = FinisherConfig.Get();
        var list = new List<FinisherEntry>();
        bool incDisabled = Flag("-finisherdisabled");
        if (cfg != null && cfg.entries != null)
            foreach (var e in cfg.entries)
            {
                if (e == null) continue;
                if (only != null) { if (Array.IndexOf(only, e.id) < 0) continue; }
                else if (!e.enabled && !incDisabled) continue;
                list.Add(e);
            }
        timeout = C.bootWaitSeconds + Mathf.Max(1, list.Count) * C.perFinisherSeconds;
        float tv; var ts = Arg("-finishertimeout:"); if (ts != null && float.TryParse(ts, System.Globalization.NumberStyles.Float, IC, out tv) && tv > 0f) timeout = tv;
        Log("entries: " + list.Count + " [" + string.Join(", ", list.ConvertAll(x => x.id).ToArray()) + "] timeout " + timeout.ToString("0") + " s rotationFile " + (FinisherRotationStore.FilePath ?? "?") + " (suspended: not written)");
        try { rotOk = FinisherRotationCheck.Run(out rotReport); } catch (Exception ex) { rotOk = false; rotReport = "rotation check threw " + ex + "\n"; }   // V62
        foreach (var ln in rotReport.Split('\n')) if (ln.Trim().Length > 0) Log("rot| " + ln.TrimEnd());
        if (Flag("-finisherrotonly")) { Exit(rotOk ? 0 : 2, rotOk ? "rotation check passed" : "rotation check failed"); yield break; }
        if (list.Count == 0) { Fail("no finisher entries (duel_rules.json finishers.entries empty / all disabled / -finisherlist matches nothing)"); yield break; }

        for (int i = 0; i < list.Count && !exiting; i++)
        {
            if (i > 0)
            {
                Log("restart scene");
                Time.timeScale = baseScale;
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
                yield return null; yield return null;
            }
            yield return RunOne(list[i], i);
            if (exiting) yield break;
            float until = Time.realtimeSinceStartup + C.betweenSeconds; while (Time.realtimeSinceStartup < until) yield return null;
        }
        if (!exiting) { bool allPass = recs.Count == list.Count && recs.TrueForAll(r => r.pass) && totalExceptions == 0 && rotOk; Exit(allPass ? 0 : 2, allPass ? "all finishers passed" : "finisher failures or exceptions"); }
    }

    bool FindFighters()
    {
        D = P = null;
        var g = GameObject.Find(Roster.PlayerObject); if (g != null) D = g.GetComponent<DuelFighter>();
        g = GameObject.Find(Roster.EnemyObject); if (g != null) P = g.GetComponent<DuelFighter>();
        if (D == null || P == null)
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) { if (RTFinisher.SideOf(f) == 1) { if (P == null) P = f; } else if (D == null) D = f; }
        return D != null && P != null;
    }

    IEnumerator RunOne(FinisherEntry e, int index)
    {
        entry = e; cur = new Rec { id = e.id }; recs.Add(cur);
        RTFinisher.ForceEnable = true; RTFinisher.ForceId = e.id;
        Log("=== finisher " + (index + 1) + " id=" + e.id + " attackerSide=" + e.attackerSide + " attacker=" + (string.IsNullOrEmpty(e.attackerClip) ? "move:" + e.attackerMove : "clip:" + e.attackerClip) + " victim=" + (string.IsNullOrEmpty(e.victimClip) ? "death" : "clip:" + e.victimClip) + (e.placeholder ? " (placeholder)" : ""));
        float tb = Time.realtimeSinceStartup;
        while (!FindFighters() && Time.realtimeSinceStartup - tb < C.bootWaitSeconds) yield return null;
        if (D == null || P == null) { cur.errors.Add("fighters not found"); cur.cause = "no fighters"; Finish(); yield break; }
        yield return null; yield return null;

        int vs = FinisherSides.OnlySide(e.attackerSide) == 1 ? 0 : 1;   // "any" and "player" kill the enemy; "enemy" kills the player
        V = vs == 0 ? D : P; K = vs == 0 ? P : D;
        cur.killerName = K.name; cur.victimName = V.name;
        if (D.opponent == null) D.opponent = P; if (P.opponent == null) P.opponent = D;
        dPlayer = D.GetComponent<PlayerBrain>(); if (dPlayer != null) dPlayer.enabled = false;
        dBrain = D.GetComponent<AIBrain>(); if (dBrain == null) dBrain = D.gameObject.AddComponent<AIBrain>();
        if (vs == 1) { if (!dBrain.enabled) dBrain.enabled = true; }                       // the player is steered by the aggressive AI
        else { dBrain.enabled = false; D.moveDir = Vector3.zero; D.guardHeld = false; }    // the player is the victim: it stands still, the enemy AI does the killing
        SetVictimHp(1f);

        StartCoroutine(GameplayLuma(cur));   // V61C: reference luma of the gameplay camera before the scene
        // fight until the real hook starts the scene
        float fightStart = Time.realtimeSinceStartup, forcedAt = -1f;
        while (!cur.started && !exiting)
        {
            KeepAlive();
            float el = Time.realtimeSinceStartup - fightStart;
            if (V.Dead) { cur.errors.Add("victim died without a finisher scene (plain death). TryBegin refusal: " + RTFinisher.LastRefusal); cur.cause = "plain death"; break; }
            if (forcedAt < 0f && el > C.killWaitSeconds) { forcedAt = el; ForceKill(); }
            if (forcedAt >= 0f && el > forcedAt + C.forceExtraSeconds) { cur.errors.Add("no finisher scene started (forced blow did not kill). TryBegin refusal: " + RTFinisher.LastRefusal); cur.cause = "no start"; break; }
            yield return null;
        }
        cur.fightSeconds = Time.realtimeSinceStartup - fightStart;
        if (cur.started)
        {
            // shots while the scene runs
            int taken = 0, want = Mathf.Max(1, C.shotsPerFinisher); float sceneT0 = Time.unscaledTime; StartCoroutine(Sampler(cur, entry));
            string[] labels = { "start", "contact", "afterhit", "end" };
            while (RTFinisher.Busy && !exiting && Time.unscaledTime - sceneT0 < C.sceneMaxSeconds)
            {
                float now = Time.unscaledTime;
                if (taken < want && Due(taken, now)) { yield return Shot(taken, labels[Mathf.Min(taken, labels.Length - 1)]); taken++; }
                else yield return null;
            }
            if (RTFinisher.Busy && !exiting) { cur.errors.Add("scene watchdog " + C.sceneMaxSeconds + " s (still Busy)"); cur.cause = "scene hang"; }
            while (taken < want && !exiting) { yield return Shot(taken, labels[Mathf.Min(taken, labels.Length - 1)] + "_late"); taken++; }   // scene shorter than the schedule: fill up so there are always N shots
            { float sw = Time.unscaledTime; while (!cur.sampDone && !exiting && Time.unscaledTime - sw < C.sheetMaxSeconds) yield return null; ComposeSheet(cur); }   // V61B: contact sheet frames incl. the fight-end camera after the release
            // settle one frame, then verify the restored state
            { float vw = Time.unscaledTime; while (FinisherVignette.Alive && !exiting && Time.unscaledTime - vw < C.vignetteWaitS) yield return null; }   // V63G: let the finisher vignette override fade out and be removed
            yield return null; yield return null;
        }
        Finish();
    }

    // when is shot n due (unscaled)? 0: start + delay | 1: contact | 2: contact + after | 3: kill + after (or contact + 2 x after)
    bool Due(int n, float now)
    {
        if (!cur.started) return false;
        switch (n)
        {
            case 0: return now >= cur.tStart + C.shotStartDelay;
            case 1: return cur.contact ? now >= cur.tContact + 0.03f : now >= cur.tStart + C.shotStartDelay + 0.8f;
            case 2: return cur.contact ? now >= cur.tContact + C.shotAfterContact : now >= cur.tStart + C.shotStartDelay + 1.8f;
            default: return cur.tKill >= 0f ? now >= cur.tKill + C.shotAfterKill : (cur.contact ? now >= cur.tContact + 2f * C.shotAfterContact : now >= cur.tStart + C.shotStartDelay + 2.8f);
        }
    }

    void SetVictimHp(float hp)
    {
        if (V == null) return; V.health = hp; var h = V.GetComponent<Health>(); if (h != null) h.current = hp;
    }

    // the killer must survive and the victim stays at 1 HP until the blow lands
    void KeepAlive()
    {
        if (K != null && !K.Dead && K.health < K.maxHealth * 0.6f) K.health = K.maxHealth;
        if (V != null && !V.Dead && !RTFinisher.Busy && V.health > 1f) SetVictimHp(1f);
        if (dBrain != null && dBrain.enabled && dBrain.profile != null)   // aggressive steer on the player (Init() re-creates the profile after every enable, so write it every frame)
        {
            var p = dBrain.profile; p.aggression = C.aggression; p.pressure = C.pressure; p.parry = C.parry; p.dodge = C.dodge;
        }
    }

    // lethal blow by hand (the same entry point the AI uses): killer next to the victim, victim upright and not guarding, Duel.Resolve with a heavy
    void ForceKill()
    {
        try
        {
            cur.forced = true;
            Vector3 vp = V.transform.position, dirv = K.transform.position - vp; dirv.y = 0f; if (dirv.sqrMagnitude < 1e-3f) dirv = -V.transform.forward; dirv.Normalize();
            FinisherPlayer.Teleport(K, vp + dirv * 1.3f, -dirv);
            V.guardHeld = false; V.moveDir = Vector3.zero; if (!V.Dead && V.state != FState.Loco) { V.state = FState.Loco; V.t = 0; }
            K.state = FState.Loco; K.t = 0; SetVictimHp(1f);
            string mid = !string.IsNullOrEmpty(entry.attackerMove) ? entry.attackerMove : "heavy_overhead";
            MoveDef mv = K.moves != null ? K.moves.Get(mid) : null; if (mv == null && K.moves != null) mv = K.moves.Get("heavy_overhead");
            if (mv == null) { cur.errors.Add("ForceKill: no MoveDef '" + mid + "'"); return; }
            Log("  no kill after " + C.killWaitSeconds + " s of fighting: forcing the lethal blow (" + mv.id + ")");
            Duel.Resolve(K, V, mv, 1f);
        }
        catch (Exception ex) { cur.errors.Add("ForceKill: " + ex.Message); Log("ForceKill failed: " + ex); }
    }

    // ------------------------------------------------------------------ verdict of one finisher
    static bool HasState(DuelFighter f, string clip) { return FinisherPlayer.HasState(f != null ? f.GetComponent<Fighter>() : null, clip); }

    void Finish()
    {
        var r = cur; if (r == null || r.done) return;
        try
        {
            var e = entry;
            bool aClip = !string.IsNullOrEmpty(e.attackerClip), vClip = !string.IsNullOrEmpty(e.victimClip);
            bool aHas = aClip && HasState(K, e.attackerClip), vHas = vClip && HasState(V, e.victimClip);
            r.attackerClip = e.attackerClip; r.victimClip = e.victimClip;
            r.attackerPlayed = aHas ? "clip:" + e.attackerClip : "move:" + e.attackerMove + (aClip ? " (clip state '" + e.attackerClip + "' missing: fell back)" : "");
            r.victimPlayed = vHas ? "clip:" + e.victimClip : "death" + (vClip ? " (clip state '" + e.victimClip + "' missing: fell back)" : "");
            if (aClip && !aHas) { string w = "attackerClip '" + e.attackerClip + "' is not an Animator state of " + (K != null ? K.name : "?"); if (e.placeholder) r.warns.Add(w); else r.errors.Add(w); }
            if (vClip && !vHas) { string w = "victimClip '" + e.victimClip + "' is not an Animator state of " + (V != null ? V.name : "?"); if (e.placeholder) r.warns.Add(w); else r.errors.Add(w); }

            var hp = V != null ? V.GetComponent<Health>() : null;
            r.victimDead = V != null && V.Dead && (hp == null || hp.Dead);
            r.frozenClear = !Duel.Frozen && !RTFinisher.Busy;   // Busy false = GameHUD / STUI would now show VICTORY / YOU DIED
            r.timeOk = Mathf.Abs(Time.timeScale - baseScale) < 0.02f;
            var cr = FindFirstObjectByType<CameraRig>(); r.camOk = cr == null || !cr.Yield;
            if (r.started && r.cuts.Count == 0 && (e.shots != null && e.shots.Length > 0)) r.warns.Add("entry has shots but no camera cut happened (no CameraRig / shot ids unknown)");
            if (!r.started) { /* cause already set */ }
            else
            {
                if (!r.ended) r.errors.Add("scene never reported its end");
                if (!r.victimDead) r.errors.Add("victim not dead after the scene");
                if (!r.frozenClear) r.errors.Add("Duel.Frozen / RTFinisher.Busy still set after the scene");
                if (!r.timeOk) r.errors.Add("Time.timeScale not restored (" + Time.timeScale.ToString("0.###", IC) + ")");
                if (!r.camOk) r.errors.Add("CameraRig.Yield still true");
                if (!r.contact) r.warns.Add("no contact event seen");
                if (r.tKill < 0f) r.errors.Add("victim Die() never ran");
            }
            if (r.started)   // V61C: length, luma and clearance checks
            {
                float lim = C.maxFinisherSeconds;
                if (lim > 0f && r.paced > lim) r.errors.Add("too long: " + r.paced.ToString("0.0", IC) + " s paced (limit " + lim.ToString("0.0", IC) + " s), Die() at +" + (r.pacedKill >= 0f ? r.pacedKill.ToString("0.0", IC) : "?") + " s");
                if (r.refLuma > 0f && C.lumaTolerance > 0f)
                {
                    var bad = new List<string>();
                    for (int i = 0; i < r.lumas.Count; i++) { if (r.lumaLabels[i].StartsWith("~")) continue; float dev = r.lumas[i] / r.refLuma - 1f; if (Mathf.Abs(dev) > C.lumaTolerance) bad.Add(r.lumaLabels[i] + " " + (dev * 100f).ToString("+0;-0", IC) + "%"); }
                    r.lumaOk = bad.Count == 0;
                    if (!r.lumaOk) { string m = "luma differs from gameplay (" + r.refLuma.ToString("0.000", IC) + ", tolerance " + (C.lumaTolerance * 100f).ToString("0", IC) + "%): " + string.Join(", ", bad.ToArray()); if (C.lumaFails) r.errors.Add(m); else r.warns.Add(m); }
                }
                else r.warns.Add("no gameplay luma reference: luma not judged");
                r.clearHead = FinisherPlayer.ClearHead; r.clearHand = FinisherPlayer.ClearHand;
                float worstC = Mathf.Min(float.IsNaN(r.clearHead) ? 99f : r.clearHead, float.IsNaN(r.clearHand) ? 99f : r.clearHand);
                if (worstC < C.minClearanceM) r.warns.Add("clearance " + worstC.ToString("0.00", IC) + " m at the contact (want >= " + C.minClearanceM.ToString("0.00", IC) + "): bodies / weapon too close, see the NOTE clearance line");
                // V62B: foot-to-ground probe (both fighters, judged windows) and the severed piece / headless body check
                var gcf = FinisherConfig.Get().rt.ground;
                if (gcf != null && gcf.enabled)
                {
                    var gk = FinisherGround.Killer; var gv = FinisherGround.Victim; r.groundLine = gk.Line() + " | " + gv.Line();
                    foreach (var gs in new[] { gk, gv })
                    {
                        if (!gs.any || gs.n == 0) { r.warns.Add("ground probe: " + gs.who + " has no samples (no ground under the feet / never judged)"); continue; }
                        if (gs.max > gcf.failAboveM)
                        {
                            // V64F2: a victim lying with its feet in the air is judged by its lowest BODY point (below), not by the soles
                            bool bodyJudged = gcf.bodyClamp && gs.who.StartsWith("victim") && FinisherGround.Victim.bn > 0;
                            string fm = "foot-to-ground: " + gs.who + (bodyJudged ? " has the feet " : " floats ") + gs.max.ToString("0.00", IC) + " m above the ground (limit " + gcf.failAboveM.ToString("0.00", IC) + " m)" + (bodyJudged ? "; the lowest body point decides (body-to-ground)" : "");
                            if (bodyJudged) r.warns.Add(fm); else r.errors.Add(fm);
                        }
                        if (gs.min < -gcf.failBelowM) r.errors.Add("foot-to-ground: " + gs.who + " sunk " + (-gs.min).ToString("0.00", IC) + " m into the ground (limit " + gcf.failBelowM.ToString("0.00", IC) + " m)");
                        if (gcf.correct && (gs.rawMax > gcf.failAboveM || gs.rawMin < -gcf.failBelowM)) r.warns.Add("ground correction was needed: " + gs.who + " raw gap " + gs.rawMin.ToString("+0.00;-0.00", IC) + " .. " + gs.rawMax.ToString("+0.00;-0.00", IC) + " m (the clip / placement floats or sinks; the root was moved to the ground)");
                    }
                }
                // V64F: the victim is judged by its lowest BODY point too (corpse floating at an angle), the pair must not interpenetrate, the victim material must not get lighter
                if (gcf != null && gcf.enabled && gcf.bodyClamp)
                {
                    var gvb = FinisherGround.Victim;
                    if (gvb.bn == 0) r.warns.Add("body-to-ground: " + gvb.who + " has no samples");
                    else
                    {
                        if (gvb.bMax > gcf.failBodyAboveM) r.errors.Add("body-to-ground: " + gvb.who + " lowest body point floats " + gvb.bMax.ToString("0.00", IC) + " m above the ground (limit " + gcf.failBodyAboveM.ToString("0.00", IC) + " m)");
                        if (gvb.bMin < -gcf.failBodyBelowM) r.errors.Add("body-to-ground: " + gvb.who + " lowest body point sunk " + (-gvb.bMin).ToString("0.00", IC) + " m into the ground (limit " + gcf.failBodyBelowM.ToString("0.00", IC) + " m)");
                    }
                }
                { string pl, ll; FinisherPairProbe.Verify(r.id, r.errors, r.warns, out pl); r.pairLine = pl; Log("pair: " + pl); FinisherVictimLook.Verify(r.id, r.errors, r.warns, out ll); r.lookLine = ll; if (ll.Length > 0) Log(ll); }
                var cutsCfg = FinisherConfig.Get().cuts; var hl = new List<string>();
                if (e.gore != null && cutsCfg != null && cutsCfg.units != null && V != null)
                    foreach (var gk2 in e.gore)
                    {
                        if (gk2 == null || string.IsNullOrEmpty(gk2.cut)) continue;
                        CutUnit cu = null; foreach (var x in cutsCfg.units) if (x != null && x.id == gk2.cut) cu = x;
                        if (cu == null || cu.kind != "subtree") continue;
                        int alive, resc; float pgap; FinisherPieceCheck.Pieces(cu.id, C.pieceMaxGapM, out alive, out pgap, out resc);
                        int left = FinisherPieceCheck.TrianglesLeft(V.transform, cu.id);
                        hl.Add(cu.id + ": severed piece alive x" + alive + ", gap to ground " + pgap.ToString("+0.00;-0.00", IC) + " m, rescued x" + resc + ", destroyed x" + FinisherPiece.Destroyed + "; body triangles of the part left: " + left);
                        if (!C.pieceCheck) continue;
                        if (alive == 0) r.errors.Add("severed '" + cu.id + "' is gone at the end of the scene (destroyed x" + FinisherPiece.Destroyed + ")");
                        else if (Mathf.Abs(pgap) > C.pieceMaxGapM) r.errors.Add("severed '" + cu.id + "' is " + pgap.ToString("+0.00;-0.00", IC) + " m from the ground (limit " + C.pieceMaxGapM.ToString("0.00", IC) + " m): floating or under the floor");
                        if (resc > C.pieceMaxRescues) r.errors.Add("severed '" + cu.id + "' needed " + resc + " safety rescues (limit " + C.pieceMaxRescues + "): it tunnels under the floor, see gore.piece (collider / collisionMode)");
                        if (left > 0) r.errors.Add("the victim's body still has " + left + " triangles of '" + cu.id + "' (not headless at the end frame)");
                    }
                r.headLine = string.Join(" | ", hl.ToArray());
                { string vl; var ve = new List<string>(); var vwn = new List<string>(); FinisherVignette.Verify(C, out vl, ve, vwn); r.vigLine = vl; Log("vignette: " + vl); foreach (var x in ve) r.errors.Add(x); foreach (var x in vwn) r.warns.Add(x); }   // V63G: vignette / exposure Volume gone after the scene
            }
            r.pass = r.started && r.errors.Count == 0 && r.exceptions == 0;
        }
        catch (Exception ex) { r.errors.Add("verdict: " + ex.Message); r.pass = false; }
        r.done = true;
        Log("RESULT id=" + r.id + " " + (r.pass ? "ok" : "FAILED") + " clips=" + r.attackerPlayed + " / " + r.victimPlayed + " duration=" + r.duration.ToString("0.00", IC) + "s camera=[" + string.Join(",", r.cuts.ToArray()) + "] slowmo=" + r.slowmo + " gore=" + r.gore + " shots=" + r.shots + " exceptions=" + r.exceptions
            + (r.warns.Count > 0 ? " warn=" + string.Join("; ", r.warns.ToArray()) : "") + (r.errors.Count > 0 ? " errors=" + string.Join("; ", r.errors.ToArray()) : ""));
    }

    // ------------------------------------------------------------------ screenshots
    IEnumerator Shot(int n, string label)
    {
        yield return new WaitForEndOfFrame();
        string name = (recs.Count).ToString("D2", IC) + "_" + cur.id + "_" + (n + 1) + "_" + label;
        string path = Path.Combine(shotDir, name + ".png");
        try { string how = SaveShot(path); cur.shots++; cur.files.Add(name + ".png"); Log("  SHOT " + name + ".png (" + how + ")"); }
        catch (Exception e) { cur.warns.Add("shot failed " + name + ": " + e.Message); Log("  SHOT failed " + name + ": " + e.Message); }
        shotFrame = true;
    }

    string SaveShot(string path)
    {
        CaptureUtil.HideConsole();
        string mode = (C.shotMode ?? "auto").ToLowerInvariant();
        if (mode != "camera")
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex != null)
            {
                bool black = IsBlack(tex);
                if (!black || mode == "screen") { File.WriteAllBytes(path, tex.EncodeToPNG()); Destroy(tex); return black ? "screen, black" : "screen"; }
                Destroy(tex);
            }
            else if (mode == "screen") throw new Exception("CaptureScreenshotAsTexture returned null");
        }
        var cam = Camera.main; if (cam == null) throw new Exception("no main camera");
        int w = Mathf.Max(16, Screen.width), h = Mathf.Max(16, Screen.height);
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 }; rt.Create();
        var prevT = cam.targetTexture; var prevA = RenderTexture.active; Texture2D t2 = null;
        try
        {
            cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
            t2 = new Texture2D(w, h, TextureFormat.RGB24, false, false); t2.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); t2.Apply(false);
            File.WriteAllBytes(path, t2.EncodeToPNG());
        }
        finally { cam.targetTexture = prevT; RenderTexture.active = prevA; rt.Release(); Destroy(rt); if (t2 != null) Destroy(t2); }
        return "camera " + w + "x" + h;
    }

    static bool IsBlack(Texture2D tex)
    {
        float lum = 0f; int n = 0;
        for (int yy = 1; yy < 9; yy++) for (int xx = 1; xx < 16; xx++) { Color c = tex.GetPixel(tex.width * xx / 16, tex.height * yy / 9); lum += c.r + c.g + c.b; n++; }
        return lum / Mathf.Max(1, n) < 0.003f;
    }

    // ------------------------------------------------------------------ V61C luma check (finisher frames vs gameplay frames)
    IEnumerator GameplayLuma(Rec r)
    {
        float sum = 0f; int n = 0, want = Mathf.Max(1, C.lumaRefFrames); float next = Time.realtimeSinceStartup + 1.5f;
        while (!exiting && !r.started && n < want)
        {
            if (Time.realtimeSinceStartup >= next)
            {
                CaptureUtil.HideConsole();
                yield return new WaitForEndOfFrame();
                if (r.started || exiting) break;
                try { var tex = ScreenCapture.CaptureScreenshotAsTexture(); if (tex != null) { sum += FinisherLook.MeanLuma(tex); n++; Destroy(tex); } } catch (Exception ex) { r.warns.Add("luma ref: " + ex.Message); }
                shotFrame = true; next = Time.realtimeSinceStartup + 1.0f;
            }
            yield return null;
        }
        if (n > 0) { r.refLuma = sum / n; Log("  luma reference (gameplay camera, " + n + " frames) = " + r.refLuma.ToString("0.000", IC)); }
        else Log("  luma reference: none (the scene started before the first gameplay frame)");
    }

    void AddLuma(Rec r, Texture2D tex, ContactFrame k)
    {
        try
        {
            float l = FinisherLook.MeanLuma(tex); string lab = (string.IsNullOrEmpty(k.label) ? (k.rel ?? "") : k.label) + ShotTag() /* V62B: the camera shot id is part of the label (mean luma per shot) */; bool rel = string.Equals(k.rel, "release", StringComparison.OrdinalIgnoreCase);
            r.lumas.Add(l); r.lumaLabels.Add((rel ? "~" : "") + lab);   // "~" = after the release (the gameplay camera again): reported, not judged
        }
        catch (Exception ex) { r.warns.Add("luma: " + ex.Message); }
    }

    static string ShotTag() { var fc = FinisherCamera.Instance; return fc != null && fc.Active && fc.ShotId.Length > 0 ? "[" + fc.ShotId + "]" : ""; }   // V62B
    string LumaLine(Rec r)
    {
        var sb = new StringBuilder("gameplay ref " + (r.refLuma > 0f ? r.refLuma.ToString("0.000", IC) : "none"));
        for (int i = 0; i < r.lumas.Count; i++)
            sb.Append(" | " + r.lumaLabels[i] + " " + r.lumas[i].ToString("0.000", IC) + (r.refLuma > 0f ? " (" + (r.lumas[i] / r.refLuma - 1f).ToString("+0%;-0%", IC) + ")" : ""));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ V61B contact sheets
    // Per finisher a sheet of auto.sheetFrames frames (default 9 = 3 columns x 3 rows: start, contact, afterhit ..., kill, end of hold, then the fight-end camera after the release),
    // finisher_contact_<id>.png in the out dir; one combined sheet finisher_contact_all.png (all finishers stacked). Frames come from ScreenCapture.CaptureScreenshotAsTexture.
    IEnumerator Sampler(Rec r, FinisherEntry e)
    {
        var fr = e != null && e.sheetFrames != null && e.sheetFrames.Length > 0 ? e.sheetFrames : C.sheetFrames;   // V62: per-entry frames in data
        if (fr == null || fr.Length == 0) { r.sampDone = true; yield break; }
        float hardStop = Time.unscaledTime + Mathf.Max(5f, C.sheetMaxSeconds); int lastFrame = -1;
        int cw = Mathf.Max(64, C.sheetCellW), ch = Mathf.Max(36, Mathf.RoundToInt(cw * (float)Mathf.Max(1, Screen.height) / Mathf.Max(1, Screen.width)));
        for (int i = 0; i < fr.Length && !exiting; i++)
        {
            var k = fr[i];
            while (!exiting && Time.unscaledTime < hardStop && !FrameDue(r, e, k)) yield return null;
            if (exiting || Time.unscaledTime >= hardStop) break;
            CaptureUtil.HideConsole();
            if (Time.frameCount == lastFrame) yield return null;
            yield return new WaitForEndOfFrame();
            lastFrame = Time.frameCount;
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex != null) { AddLuma(r, tex, k); r.thumbs.Add(ScaleTex(tex, cw, ch)); r.thumbLabels.Add(string.IsNullOrEmpty(k.label) ? k.rel + (k.t >= 0f ? "+" : "") + k.t.ToString("0.##", IC) : k.label); Destroy(tex); }
            }
            catch (Exception ex) { r.warns.Add("sheet frame " + i + ": " + ex.Message); }
            shotFrame = true;
        }
        r.sampDone = true;
    }

    bool FrameDue(Rec r, FinisherEntry e, ContactFrame k)
    {
        float now = Time.unscaledTime; string rel = (k.rel ?? "start").ToLowerInvariant();
        if (rel == "release") return r.ended && now >= r.tEnd + k.t;                       // after RTFinisher.End: the fight-end camera (CameraDeath)
        if (r.ended) return true;                                                         // the scene is over: take what is left now
        if (rel == "contact") return r.contact ? now >= r.tContact + k.t : now >= r.tStart + 1f + k.t;
        if (rel == "kill") return r.tKill >= 0f && now >= r.tKill + k.t;
        if (rel == "hold") return r.tKill >= 0f && now >= r.tKill + Mathf.Max(0f, e.holdAfter) * k.t;   // t = fraction of the entry's holdAfter after Die()
        return now >= r.tStart + k.t;                                                     // "start"
    }

    static Texture2D ScaleTex(Texture2D src, int w, int h)
    {
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32); var prev = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt); RenderTexture.active = rt;
            var o = new Texture2D(w, h, TextureFormat.RGB24, false); o.ReadPixels(new Rect(0, 0, w, h), 0, 0, false); o.Apply(false);
            return o;
        }
        finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
    }

    void ComposeSheet(Rec r)
    {
        try
        {
            if (r.thumbs.Count == 0) return;
            int cols = Mathf.Max(1, C.sheetCols), rows = (r.thumbs.Count + cols - 1) / cols, cw = r.thumbs[0].width, ch = r.thumbs[0].height;
            var sheet = new Texture2D(cols * cw, rows * ch, TextureFormat.RGB24, false);
            var blank = new Color32[cols * cw * rows * ch]; sheet.SetPixels32(blank);
            for (int i = 0; i < r.thumbs.Count; i++)
            {
                int c = i % cols, rw = i / cols;   // row 0 = top of the sheet
                sheet.SetPixels32(c * cw, (rows - 1 - rw) * ch, cw, ch, r.thumbs[i].GetPixels32());
            }
            sheet.Apply(false);
            r.sheetFile = "finisher_contact_" + r.id + ".png";
            File.WriteAllBytes(Path.Combine(dir, r.sheetFile), sheet.EncodeToPNG());
            int cwAll = Mathf.Max(64, C.sheetCombinedW);
            r.sheetSmall = ScaleTex(sheet, cwAll, Mathf.Max(8, Mathf.RoundToInt(cwAll * (float)sheet.height / sheet.width)));
            Destroy(sheet);
            Log("  SHEET " + r.sheetFile + " " + r.thumbs.Count + " frames [" + string.Join(", ", r.thumbLabels.ToArray()) + "]");
            foreach (var t in r.thumbs) Destroy(t);
            r.thumbs.Clear();
        }
        catch (Exception ex) { r.warns.Add("sheet: " + ex.Message); Log("  SHEET failed: " + ex.Message); }
    }

    string combinedFile = "";
    void WriteCombinedSheet()
    {
        var list = recs.FindAll(x => x.sheetSmall != null);
        if (list.Count == 0) return;
        int w = list[0].sheetSmall.width, total = 0; foreach (var x in list) total += x.sheetSmall.height;
        var all = new Texture2D(w, total, TextureFormat.RGB24, false); all.SetPixels32(new Color32[w * total]);
        int y = total;
        foreach (var x in list) { y -= x.sheetSmall.height; all.SetPixels32(0, y, w, x.sheetSmall.height, x.sheetSmall.GetPixels32()); }
        all.Apply(false);
        combinedFile = "finisher_contact_all.png";
        File.WriteAllBytes(Path.Combine(dir, combinedFile), all.EncodeToPNG());
        Log("combined sheet " + combinedFile + " " + w + "x" + total + " (" + list.Count + " finishers, top to bottom: " + string.Join(", ", list.ConvertAll(x => x.id).ToArray()) + ")");
    }

    // ------------------------------------------------------------------ summary
    void WriteSummary(int code, string why)
    {
        bool allPass = code == 0 && (recs.Count > 0 || Flag("-finisherrotonly")) && recs.TrueForAll(r => r.pass) && totalExceptions == 0 && rotOk;
        try { WriteCombinedSheet(); } catch (Exception ex) { Log("combined sheet failed: " + ex.Message); }   // V61B
        var sb = new StringBuilder();
        sb.AppendLine("finisher_auto_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height);
        sb.AppendLine("VERDICT: " + (allPass ? "PASS" : "FAIL") + "   (exit code " + code + ", " + why + ")");
        sb.AppendLine("finishers: " + recs.Count + "  passed: " + recs.FindAll(r => r.pass).Count + "  exceptions: " + totalExceptions + (foreignExceptions > 0 ? " (outside a finisher: " + foreignExceptions + ")" : ""));
        sb.AppendLine("fps: avg " + (fpsN > 0 ? (fpsSum / fpsN).ToString("0.0", IC) : "?") + "  worst " + (fpsN > 0 ? fpsMin.ToString("0.0", IC) : "?") + "  (screenshot frames excluded)");
        sb.AppendLine("rotation file untouched: " + (FinisherRotationStore.FilePath ?? "?"));
        sb.Append(rotReport);   // V62: rotation check lines (temp file; the player's file is never written)
        if (combinedFile.Length > 0) sb.AppendLine("combined contact sheet: " + combinedFile);
        sb.AppendLine();
        foreach (var r in recs)
        {
            sb.AppendLine("--- " + r.id + ": " + (r.pass ? "ok" : "FAILED") + (r.cause.Length > 0 ? " (" + r.cause + ")" : ""));
            sb.AppendLine("    killer " + r.killerName + " -> victim " + r.victimName + (r.forced ? "  [blow forced after " + r.fightSeconds.ToString("0.0", IC) + " s]" : "  [natural blow after " + r.fightSeconds.ToString("0.0", IC) + " s]"));
            sb.AppendLine("    clips: attacker " + (r.attackerPlayed.Length > 0 ? r.attackerPlayed : "?") + " | victim " + (r.victimPlayed.Length > 0 ? r.victimPlayed : "?"));
            sb.AppendLine("    duration " + r.duration.ToString("0.00", IC) + " s (unscaled)  contact at +" + (r.tContact >= 0f ? (r.tContact - r.tStart).ToString("0.00", IC) : "-") + " s  Die() at +" + (r.tKill >= 0f ? (r.tKill - r.tStart).ToString("0.00", IC) : "-") + " s");
            sb.AppendLine("    camera: " + (r.cuts.Count > 0 ? string.Join(", ", r.cuts.ToArray()) : "(no cuts)") + "  slow-mo x" + r.slowmo + "  gore x" + r.gore);
            sb.AppendLine("    length: " + r.paced.ToString("0.00", IC) + " s paced (raw " + r.duration.ToString("0.00", IC) + " s, " + r.stalls + " capture stall frames excluded), Die() at +" + (r.pacedKill >= 0f ? r.pacedKill.ToString("0.00", IC) : "-") + " s paced, limit " + (C.maxFinisherSeconds > 0f ? C.maxFinisherSeconds.ToString("0.0", IC) + " s" : "none") + (C.maxFinisherSeconds > 0f ? (r.paced > C.maxFinisherSeconds ? "  TOO LONG" : "  ok") : ""));
            sb.AppendLine("    luma: " + LumaLine(r) + (r.refLuma > 0f && C.lumaTolerance > 0f ? "  tolerance " + (C.lumaTolerance * 100f).ToString("0", IC) + "%  " + (r.lumaOk ? "ok" : "OFF") : ""));
            sb.AppendLine("    clearance: head " + (float.IsNaN(r.clearHead) ? "-" : r.clearHead.ToString("0.00", IC)) + " m, hand " + (float.IsNaN(r.clearHand) ? "-" : r.clearHand.ToString("0.00", IC)) + " m (victim chest, estimated radius subtracted; negative = inside)");
            if (r.groundLine.Length > 0) sb.AppendLine("    ground: " + r.groundLine + "  (limit +" + FinisherConfig.Get().rt.ground.failAboveM.ToString("0.00", IC) + " / -" + FinisherConfig.Get().rt.ground.failBelowM.ToString("0.00", IC) + " m)");
            if (r.headLine.Length > 0) sb.AppendLine("    severed: " + r.headLine);
            if (r.vigLine.Length > 0) sb.AppendLine("    vignette: " + r.vigLine);
            if (r.pairLine.Length > 0) sb.AppendLine("    pair: " + r.pairLine);
            if (r.lookLine.Length > 0) sb.AppendLine("    " + r.lookLine);
            sb.AppendLine("    checks: victimDead=" + r.victimDead + " frozenClear=" + r.frozenClear + " timeScaleRestored=" + r.timeOk + " cameraReleased=" + r.camOk + " exceptions=" + r.exceptions);
            sb.AppendLine("    shots (" + r.shots + "): " + string.Join(", ", r.files.ToArray()));
            if (r.sheetFile.Length > 0) sb.AppendLine("    sheet: " + r.sheetFile);
            foreach (var w in r.warns) sb.AppendLine("    WARN  " + w);
            foreach (var nt in r.notes) sb.AppendLine("    NOTE  " + nt);   // V62: pair probe lines
            foreach (var er in r.errors) sb.AppendLine("    ERROR " + er);
        }
        if (exceptionList.Count > 0) { sb.AppendLine(); sb.AppendLine("exceptions:"); foreach (var x in exceptionList) sb.AppendLine("  " + x); }
        File.WriteAllText(summaryPath, sb.ToString());
    }
}
