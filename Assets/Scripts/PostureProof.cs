using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// POSTURE PROOF HARNESS (lane P7): -postureproof [-out:<dir>] [-proofsecs:<n> (default 90)]
// Scripts the player (PlayerBrain / AIBrain / TouchControls off, direct DuelFighter calls like MoveSheet) against the enemy and takes FULL-SCREEN screenshots
// (ScreenCapture.CaptureScreenshotAsTexture after 2 frames + end of frame, so the HUD is in frame; run the player at 1920x1080):
//   (a) parry      : the enemy swings, the player raises the guard just before contact (retries with other leads) -> shot at the Parried event
//   (b) guard break: heavy into a guarding low-stamina enemy -> shot at the GuardBroken event
//   (c) posture fill: 3 lights on the enemy (ONLY here the enemy max is lowered in memory to 30; (a) and (b) run on the real posture.json values): after hit 1, hit 2,
//       at POSTURE BREAK, +30 ticks (deathblow countdown), then the deathblow hit with the flash frames d_flash_before / d_flash_during (~0.05 s) / d_flash_after (~0.3 s)
//       and a log of the enemy renderer colours (shared _BaseColor, property-block value, flash amount) plus the mean screen luma of its bounds, before / during / after,
//       so a black tint or a black pose shows as numbers (V61A). Posture, window and stamina of both fighters are reset between the steps.
// Output (default <exe dir>\posture_proof\): proof_*.png, posture_proof_log.txt, posture_proof_summary.txt, posture_proof_contact.png (3x3 grid of the key frames).
// Exit codes: 0 finished (PASS or FAIL in the log/summary), 2 exception / setup failure, 3 timeout. PASS = a posture break and a deathblow actually occurred (DuelFighter stat counters).
// Overrides are in memory only (PostureTable.Get() object); nothing on disk is changed. Time.captureFramerate = 60 so one frame = one sim tick.
[DefaultExecutionOrder(-60)]
public class PostureProof : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-postureproof")) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<PostureProof>() != null) return;
        var go = new GameObject("PostureProof"); DontDestroyOnLoad(go); go.AddComponent<PostureProof>();
    }

    // POSTURE v102: posture_proof.json (StreamingAssets / Data) holds the numbers of steps (a) and (b); the defaults here mirror the file
    [Serializable] public class Cfg
    {
        public bool requireParry = true, requireGuardBreak = true; public int failExitCode = 1;
        public float[] parryLeadFractions = { 0.5f, 0.3f, 0.7f, 0.2f, 0.9f, 0.4f };
        public int parrySettleTicks = 130, parryPressWaitTicks = 30, parryContactWaitTicks = 60;
        public int guardBreakAttempts = 3, guardBreakSettleTicks = 130, guardBreakRaiseTicks = 25, guardBreakWindupWaitTicks = 30, guardBreakWaitTicks = 150, guardBreakRecoveryGiveUpTick = 20;
        public float guardBreakStamina = 6f;
        static Cfg c;
        public static Cfg Get()
        {
            if (c != null) return c;
            try
            {
                string p = Path.Combine(Application.streamingAssetsPath, "posture_proof.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/posture_proof.json");
                if (File.Exists(p)) c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p));
            }
            catch (Exception) { }
            return c ?? (c = new Cfg());
        }
    }
    bool StepsOk() { var C = Cfg.Get(); bool v; return (!C.requireParry || (results.TryGetValue("parry", out v) && v)) && (!C.requireGuardBreak || (results.TryGetValue("guardbreak", out v) && v)); }

    string dir, logPath; float t0, timeoutS = 90f; bool exiting; int exitCode; float exitAt;
    DuelFighter D, P;
    PostureTable.FighterRow pRow; float realPitMax;
    readonly Dictionary<string, float> lumas = new Dictionary<string, float>();
    readonly List<string> events = new List<string>();
    readonly List<Texture2D> keyFrames = new List<Texture2D>(); readonly List<string> keyNames = new List<string>();
    readonly Dictionary<string, bool> results = new Dictionary<string, bool>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(6) + "s tick " + Duel.TickCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[postureproof] " + s);
    }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "posture_proof"); } catch { dir = "posture_proof"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "posture_proof_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        float v; var ps = Arg("-proofsecs:"); if (ps != null && float.TryParse(ps, System.Globalization.NumberStyles.Float, IC, out v) && v > 0f) timeoutS = v;
        ContactRelay.OnEvent += OnEvent;
    }
    void OnDestroy() { ContactRelay.OnEvent -= OnEvent; }
    void Start() { StartCoroutine(Guarded(Main())); }

    void OnEvent(CombatEvent e)
    {
        string s = e.type + " " + (e.actor != null ? e.actor.name : "?") + "->" + (e.target != null ? e.target.name : "?") + " move=" + e.moveId;
        events.Add(s + "@" + Duel.TickCount); Log("  EVENT " + s);
    }
    int EventCount(string type, string targetName) { int n = 0; foreach (var s in events) if (s.StartsWith(type + " ", StringComparison.Ordinal) && s.Contains("->" + targetName)) n++; return n; }

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
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; Time.captureFramerate = 0;
        try { WriteSummary(code, why); } catch (Exception e) { Log("summary failed: " + e.Message); }
        try { MakeContact(); } catch (Exception e) { Log("contact sheet failed: " + e.Message); }
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    string Counters(DuelFighter f)
    {
        return f.name + " [posture " + f.posture.ToString("F1", IC) + "/" + f.PostureMax.ToString("F0", IC) + " broken " + f.PostureBroken + " window " + f.DeathblowLeft + "/" + f.DeathblowTotal
            + " hp " + f.health.ToString("F0", IC) + " stam " + f.stamina.ToString("F0", IC) + " state " + f.state + " | breaks " + f.statPostureBreaks + " deathblows " + f.statDeathblows + " taken " + f.statDeathblowsTaken
            + " missed " + f.statDeathblowsMissed + " parries " + f.statParries + " blocks " + f.statBlocks + " guardBreaks " + f.statGuardBreaks + " gained " + f.statPostureGained.ToString("F0", IC) + "]";
    }
    void LogState(string step) { Log(step + ": " + Counters(D) + "  " + Counters(P)); }

    // ---------------------------------------------------------------- helpers
    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }

    void Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>();
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, PhysMasks.Ground, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }

    // the player `sep` metres in front of the enemy on the line between them, both facing (called only while both are free to move)
    void Setup(float sep)
    {
        Vector3 pp = P.transform.position; Vector3 dd = D.transform.position; dd.y = 0f; pp.y = 0f;
        Vector3 axis = (pp - dd); if (axis.sqrMagnitude < 0.01f) axis = Vector3.forward; axis.Normalize();
        Place(D, pp - axis * sep, axis); Place(P, pp, -axis);
        D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; D.guardHeld = false; P.guardHeld = false;
    }

    IEnumerator Ticks(int n) { int end = Duel.TickCount + n; int guard = 0; while (Duel.TickCount < end && guard++ < n * 4 + 600) yield return null; }

    IEnumerator WaitFor(Func<bool> cond, int maxTicks)
    {
        int end = Duel.TickCount + maxTicks; int frames = 0;
        while (!cond() && Duel.TickCount < end && frames++ < maxTicks * 4 + 600) yield return null;
    }

    IEnumerator Shot(string name, string alias = null, bool luma = false)
    {
        yield return null; yield return null;          // 2 frames, then the end of the frame (the HUD is drawn)
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        CaptureUtil.HideConsole(); try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("SHOT failed " + name + ": " + e.Message); }
        if (tex == null) yield break;
        try { File.WriteAllBytes(Path.Combine(dir, "proof_" + name + ".png"), tex.EncodeToPNG()); Log("SHOT proof_" + name + ".png " + tex.width + "x" + tex.height + (tex.width != 1920 || tex.height != 1080 ? " (NOT 1920x1080: run with -screen-width 1920 -screen-height 1080 -screen-fullscreen 0)" : "")); }
        catch (Exception e) { Log("SHOT write failed " + name + ": " + e.Message); }
        if (!string.IsNullOrEmpty(alias)) { try { File.WriteAllBytes(Path.Combine(dir, "proof_" + alias + ".png"), tex.EncodeToPNG()); } catch { } }
        if (luma) ScreenLuma(tex, name);
        keyFrames.Add(tex); keyNames.Add(name);
        LogState("after shot " + name);
    }

    // posture / window / stamina / guard / move input of BOTH fighters back to a clean slate (a pending break closes on the next tick: posture 0 ends it), then verified
    IEnumerator ResetState(string why)
    {
        P.posture = 0f; D.posture = 0f; Rest();
        yield return Ticks(3);
        if (P.PostureBroken || P.DeathblowLeft > 0 || D.PostureBroken || D.DeathblowLeft > 0) { P.posture = 0f; D.posture = 0f; yield return Ticks(3); }
        Log("reset (" + why + "): " + Roster.EnemyName + " posture " + P.posture.ToString("F1", IC) + "/" + P.PostureMax.ToString("F0", IC) + " broken " + P.PostureBroken + " window " + P.DeathblowLeft
            + " | " + Roster.PlayerName + " posture " + D.posture.ToString("F1", IC) + " broken " + D.PostureBroken + " window " + D.DeathblowLeft + (P.PostureBroken || P.DeathblowLeft > 0 || D.PostureBroken || D.DeathblowLeft > 0 ? "  RESET INCOMPLETE" : "  clean"));
    }

    // colour samples of the enemy (shared material _BaseColor / _Color, the renderer's property-block value, emissive) + flash amount: a black tint shows here as numbers.
    // Reads sharedMaterials only (no material instances are created).
    void SampleColors(string tag)
    {
        var mpb = new MaterialPropertyBlock(); int slots = 0, withBlock = 0; float sumShared = 0f, sumBlock = 0f; var sb = new StringBuilder();
        foreach (var r in P.GetComponentsInChildren<Renderer>(false))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || !r.enabled) continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue;
                string prop = m.HasProperty("_BaseColor") ? "_BaseColor" : m.HasProperty("_Color") ? "_Color" : null; if (prop == null) continue;
                Color sh = m.GetColor(prop), cur = sh; bool blk = false;
                r.GetPropertyBlock(mpb, i);
                if (!mpb.isEmpty && mpb.HasColor(prop)) { cur = mpb.GetColor(prop); blk = true; withBlock++; }
                Color em = m.HasProperty("_EmissiveColor") ? m.GetColor("_EmissiveColor") : Color.black;
                if (!mpb.isEmpty && mpb.HasColor("_EmissiveColor")) em = mpb.GetColor("_EmissiveColor");
                slots++; sumShared += sh.grayscale; sumBlock += cur.grayscale;
                if (slots <= 6) sb.Append("\n      ").Append(r.name).Append("[").Append(i).Append("] ").Append(m.name).Append(" ").Append(prop).Append(" shared ").Append(Rgb(sh)).Append(" now ").Append(Rgb(cur)).Append(blk ? " (MPB)" : "").Append(" emissive ").Append(Rgb(em));
            }
        }
        Log("COLORS " + tag + ": " + Roster.EnemyName + " slots " + slots + " with property block " + withBlock + " mean base luma shared " + (slots > 0 ? sumShared / slots : 0f).ToString("F3", IC) + " now " + (slots > 0 ? sumBlock / slots : 0f).ToString("F3", IC)
            + " | flash active " + HitFlash.IsFlashing(P) + " amount " + HitFlash.Amount(P).ToString("F2", IC) + " (flashes " + HitFlash.FlashCount + " restores " + HitFlash.RestoreCount + ")" + sb);
    }
    static string Rgb(Color c) { return "(" + c.r.ToString("F2", IC) + "," + c.g.ToString("F2", IC) + "," + c.b.ToString("F2", IC) + ")"; }

    // mean luma (0..1) of the screenshot inside the enemy's screen-space bounds (0 = black); a lighting/pose darkening shows here even when no material changed
    float ScreenLuma(Texture2D tex, string tag)
    {
        var cam = Camera.main; if (cam == null || tex == null) return -1f;
        Bounds b = new Bounds(P.transform.position + Vector3.up, Vector3.zero); bool any = false;
        foreach (var r in P.GetComponentsInChildren<Renderer>(false)) { if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || !r.enabled) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
        Vector3 mn = b.min, mx = b.max; float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z); var sp = cam.WorldToScreenPoint(c); if (sp.z <= 0f) continue;
            x0 = Mathf.Min(x0, sp.x); x1 = Mathf.Max(x1, sp.x); y0 = Mathf.Min(y0, sp.y); y1 = Mathf.Max(y1, sp.y);
        }
        int ix0 = Mathf.Clamp((int)x0, 0, tex.width - 1), ix1 = Mathf.Clamp((int)x1, ix0 + 1, tex.width), iy0 = Mathf.Clamp((int)y0, 0, tex.height - 1), iy1 = Mathf.Clamp((int)y1, iy0 + 1, tex.height);
        int w = ix1 - ix0, h = iy1 - iy0; if (w < 4 || h < 4) return -1f;
        var px = tex.GetPixels32(); double sum = 0; int n = 0;
        for (int y = iy0; y < iy1; y += 3) for (int x = ix0; x < ix1; x += 3) { var c32 = px[y * tex.width + x]; sum += (0.2126 * c32.r + 0.7152 * c32.g + 0.0722 * c32.b) / 255.0; n++; }
        float l = n > 0 ? (float)(sum / n) : -1f; lumas[tag] = l;
        Log("LUMA " + tag + ": " + Roster.EnemyName + " screen box " + w + "x" + h + " px mean luma " + l.ToString("F3", IC));
        return l;
    }

    void Rest() { D.stamina = D.maxStamina; P.stamina = P.maxStamina; D.guardHeld = false; P.guardHeld = false; D.guardSide = 0; P.guardSide = 0; D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; }

    // ---------------------------------------------------------------- script
    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((D = Find(Roster.PlayerObject)) == null || (P = Find(Roster.EnemyObject)) == null)
        {
            if (Time.realtimeSinceStartup - w0 > 20f) { Log("fighters not found"); Finish(2, "fighters not found"); yield break; }
            yield return null;
        }
        for (int i = 0; i < 30; i++) yield return null;
        Time.captureFramerate = 60; QualitySettings.vSyncCount = 0;
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        D.opponent = P; P.opponent = D;
        Log("start res " + Screen.width + "x" + Screen.height + " unity " + Application.unityVersion);

        // posture data: the REAL posture.json values are used for (a) and (b). Only the in-memory enable + debug log are forced; (c) alone lowers the enemy max (see below).
        var pd = PostureTable.Get();
        if (!pd.enabled) Log("NOTE posture.json had enabled=false: forced on in memory for the proof");
        pd.enabled = true; pd.debugLog = true;
        if (pd.fighters != null) foreach (var r in pd.fighters) if (r != null && r.key == Roster.EnemyId) pRow = r;
        if (pRow == null) { var list = new List<PostureTable.FighterRow>(pd.fighters ?? new PostureTable.FighterRow[0]); pRow = new PostureTable.FighterRow { key = Roster.EnemyId }; list.Add(pRow); pd.fighters = list.ToArray(); }
        realPitMax = P.PostureMax;
        Log("real values: " + Roster.EnemyName + " max " + P.PostureMax.ToString("F0", IC) + ", " + Roster.PlayerName + " max " + D.PostureMax.ToString("F0", IC) + ", hit.light " + pd.hit.light.ToString("F0", IC) + ", parry.attacker " + pd.parry.attacker.ToString("F0", IC) + ", window " + pd.posBreak.windowTicks + " ticks");
        P.maxHealth = 1000f; P.health = 1000f; D.maxHealth = 1000f; D.health = 1000f;   // nobody dies during the proof (a lethal deathblow would end it)
        Rest(); Setup(2.0f);
        yield return Ticks(60);

        // ---- (a) parry
        // POSTURE v102: the guard SIDE has to cover the swing (controls.json guard.guardRequiresSide). The proof's fighters are not FlowHuman (PlayerBrain is off), so Duel.Resolve applies DirectionalGuard.SideOk.
        // v101 never set guardSide (0 = centre): every overhead / left / right swing went through the guard as a plain Hit and a parry or block could not happen.
        var C = Cfg.Get(); bool parried = false;
        for (int a = 0; a < C.parryLeadFractions.Length && !parried; a++)
        {
            yield return ResetState("parry attempt " + (a + 1)); Setup(1.7f); yield return Ticks(C.parrySettleTicks);   // parry cooldown + recoveries
            int pc = EventCount("Parried", Roster.EnemyObject) + EventCount("Parried", Roster.PlayerObject); int sp0 = D.statParries;
            int lead = Mathf.Max(1, Mathf.RoundToInt(C.parryLeadFractions[a] * D.ParryWindowTicks()));
            P.Press("light"); LogState("parry attempt " + (a + 1) + " " + Roster.EnemyName + " swings, guard lead " + lead);
            yield return WaitFor(() => P.state == FState.Windup, C.parryPressWaitTicks);
            D.guardSide = DirectionalGuard.BestSideFor(P.CurrentMove);   // the side that covers this swing (data: controls.json guard.match)
            yield return WaitFor(() => P.state != FState.Windup || P.WindupLeft <= lead, C.parryContactWaitTicks);
            D.guardHeld = true;
            yield return WaitFor(() => EventCount("Parried", Roster.EnemyObject) + EventCount("Parried", Roster.PlayerObject) > pc || P.state == FState.Recovery || P.state == FState.Loco, C.parryContactWaitTicks);
            parried = EventCount("Parried", Roster.EnemyObject) + EventCount("Parried", Roster.PlayerObject) > pc && D.statParries > sp0;
            if (!parried) Log("parry attempt " + (a + 1) + " diag: swing " + (P.CurrentMove != null ? P.CurrentMove.id + " dir " + P.CurrentMove.direction : "none") + " guardSide " + D.guardSide + " (best " + DirectionalGuard.BestSideFor(P.CurrentMove) + ") state " + D.state + " window " + D.ParryWindowTicks() + " windowLeft " + D.ParryWindowLeft + " isGuarding " + D.IsGuarding);
            if (parried) yield return Shot("a_parry_contact");
            D.guardHeld = false; yield return Ticks(10);
        }
        results["parry"] = parried; LogState("parry " + (parried ? "OK" : "NOT ACHIEVED"));

        // ---- (b) guard break: the enemy guards at guardBreakStamina, the player throws a heavy; the enemy's guardSide follows the side that covers that heavy (as in (a))
        bool gbreak = false;
        for (int a = 0; a < C.guardBreakAttempts && !gbreak; a++)
        {
            yield return ResetState("guard break attempt " + (a + 1)); Setup(1.7f); yield return Ticks(C.guardBreakSettleTicks);
            int gc = EventCount("GuardBroken", Roster.EnemyObject); int gb0 = P.statGuardBreaks;
            P.guardHeld = true; yield return Ticks(C.guardBreakRaiseTicks);   // past the parry window: a plain block
            P.stamina = C.guardBreakStamina; D.stamina = D.maxStamina; D.Press("heavy"); LogState("guard break attempt " + (a + 1));
            yield return WaitFor(() => D.state == FState.Windup, C.guardBreakWindupWaitTicks);
            yield return WaitFor(() => { P.stamina = Mathf.Min(P.stamina, C.guardBreakStamina); P.guardHeld = true; var cm = D.CurrentMove; if (cm != null) P.guardSide = DirectionalGuard.BestSideFor(cm); return EventCount("GuardBroken", Roster.EnemyObject) > gc || (D.state == FState.Recovery && D.t > C.guardBreakRecoveryGiveUpTick); }, C.guardBreakWaitTicks);
            gbreak = EventCount("GuardBroken", Roster.EnemyObject) > gc && P.statGuardBreaks > gb0;
            if (!gbreak) Log("guard break attempt " + (a + 1) + " diag: swing " + (D.CurrentMove != null ? D.CurrentMove.id + " dir " + D.CurrentMove.direction : "none") + " guardSide " + P.guardSide + " state " + P.state + " isGuarding " + P.IsGuarding + " stamina " + P.stamina.ToString("F1", IC));
            if (gbreak) yield return Shot("b_guard_break");
            P.guardHeld = false; yield return Ticks(5);
        }
        results["guardbreak"] = gbreak; LogState("guard break " + (gbreak ? "OK" : "NOT ACHIEVED"));

        // ---- (c) posture fill with lights, break, deathblow
        // only here the enemy max is lowered (3 lights = break); (a) and (b) ran on the real posture.json values
        pRow.max = 30f; yield return ResetState("before (c)"); P.health = P.maxHealth; Setup(1.7f); yield return Ticks(200);
        Log("(c) override: " + Roster.EnemyName + " max " + realPitMax.ToString("F0", IC) + " -> " + P.PostureMax.ToString("F0", IC) + " (this step only)");
        Log("posture fill begin: " + Roster.EnemyName + " posture " + P.posture.ToString("F1", IC) + "/" + P.PostureMax.ToString("F0", IC));
        int hits = 0; int b0 = P.statPostureBreaks;
        for (int h = 1; h <= 4 && P.statPostureBreaks == b0; h++)
        {
            yield return WaitFor(() => D.state == FState.Loco, 120);
            if (Vector3.Distance(D.transform.position, P.transform.position) > 2.0f) Setup(1.7f);
            Rest(); float pPrev = P.posture;
            D.Press("light"); LogState("light " + h + " pressed");
            yield return WaitFor(() => P.posture > pPrev + 0.5f || P.statPostureBreaks > b0, 90);
            if (P.posture > pPrev + 0.5f || P.statPostureBreaks > b0) hits++;
            if (P.statPostureBreaks > b0) break;
            if (h <= 2) yield return Shot("c_fill_after_hit" + h);
            yield return Ticks(8);
        }
        results["postureHits"] = hits >= 1;
        if (P.statPostureBreaks > b0)
        {
            yield return Shot("c_break");
            yield return Ticks(28); yield return Shot("c_countdown");   // ~30 ticks after the break
            int d0 = D.statDeathblows; Rest();
            yield return WaitFor(() => D.state == FState.Loco, 120);
            if (Vector3.Distance(D.transform.position, P.transform.position) > 2.0f) Setup(1.7f);
            Log("window left " + P.DeathblowLeft + "/" + P.DeathblowTotal + " before the deathblow swing; " + Roster.EnemyName + " state " + P.state);
            SampleColors("d_flash_before (pre-shot)");
            yield return Shot("d_flash_before", null, true);
            SampleColors("d_flash_before (post-shot)");
            D.Press("light"); LogState("deathblow light pressed");
            yield return WaitFor(() => D.statDeathblows > d0 || !P.PostureBroken, 90);
            float tHit = Time.unscaledTime;
            if (D.statDeathblows > d0)
            {
                SampleColors("d_flash_during (hit tick)");
                yield return Shot("d_flash_during", "c_deathblow_hit", true);   // 2 frames + end of frame at 60 fps = ~0.05 s after the hit
                SampleColors("d_flash_during (post-shot, +" + (Time.unscaledTime - tHit).ToString("F2", IC) + " s)");
                int guard = 0; while (Time.unscaledTime - tHit < 0.28f && guard++ < 60) yield return null;   // ~0.3 s after the hit, counting the shot's own frames
                SampleColors("d_flash_after (pre-shot)");
                yield return Shot("d_flash_after", null, true);
                SampleColors("d_flash_after (post-shot, +" + (Time.unscaledTime - tHit).ToString("F2", IC) + " s)");
                Log("flash check: restored=" + !HitFlash.IsFlashing(P) + " luma before " + Lu("d_flash_before") + " during " + Lu("d_flash_during") + " after " + Lu("d_flash_after"));
            }
            yield return Ticks(20); yield return Shot("c_window_closed", null, true);
            Log("window closed: " + Roster.EnemyName + " broken " + P.PostureBroken + " window " + P.DeathblowLeft + " (the HUD must show no DEATHBLOW label)");
        }
        else Log("NO posture break from the light sequence");
        LogState("end");
        bool ok = P.statPostureBreaks > 0 && D.statDeathblows > 0 && StepsOk();   // POSTURE v102: parry + guard break are asserted too
        Finish(ok ? 0 : Cfg.Get().failExitCode, ok ? "done" : "done, goals missed (parry " + (results.ContainsKey("parry") && results["parry"]) + " guardbreak " + (results.ContainsKey("guardbreak") && results["guardbreak"]) + ")");
    }

    string Lu(string k) { float v; return lumas.TryGetValue(k, out v) ? v.ToString("F3", IC) : "n/a"; }

    // ---------------------------------------------------------------- output
    void WriteSummary(int code, string why)
    {
        bool brk = P != null && P.statPostureBreaks > 0, db = D != null && D.statDeathblows > 0;
        string verdict = code == 2 ? "ERROR" : code == 3 ? "TIMEOUT" : (brk && db && StepsOk() ? "PASS" : "FAIL");
        var sb = new StringBuilder();
        sb.AppendLine("VERDICT: " + verdict + "   (exit code " + code + ", " + why + ")");
        sb.AppendLine("posture break occurred: " + brk + "   deathblow occurred: " + db + "   parry+guardbreak asserted: " + StepsOk());
        foreach (var kv in results) sb.AppendLine("  step " + kv.Key + ": " + (kv.Value ? "ok" : "not achieved"));
        if (D != null && P != null) { sb.AppendLine("final " + Counters(D)); sb.AppendLine("final " + Counters(P)); }
        sb.AppendLine("flash frames: " + Roster.EnemyName + " screen luma before " + Lu("d_flash_before") + " during " + Lu("d_flash_during") + " after " + Lu("d_flash_after") + "; flashes " + HitFlash.FlashCount + " restores " + HitFlash.RestoreCount + " still flashing " + (P != null && HitFlash.IsFlashing(P)));
        sb.AppendLine("screenshots: " + string.Join(", ", keyNames.ToArray()));
        sb.AppendLine("events: " + events.Count);
        try { File.AppendAllText(logPath, "\n=== SUMMARY ===\n" + sb); } catch { }
        try { File.WriteAllText(Path.Combine(dir, "posture_proof_summary.txt"), sb.ToString()); } catch { }
        Debug.Log("[postureproof] " + sb);
    }

    // 3x3 contact sheet of the key frames (the nine wanted ones incl. the three flash frames, else whatever exists)
    void MakeContact()
    {
        if (keyFrames.Count == 0) return;
        string[] want = { "a_parry_contact", "b_guard_break", "c_break", "c_countdown", "d_flash_before", "d_flash_during", "d_flash_after", "c_window_closed", "c_fill_after_hit2" };
        var pick = new List<int>();
        foreach (var w in want) { int i = keyNames.IndexOf(w); if (i >= 0) pick.Add(i); }
        for (int i = 0; i < keyFrames.Count && pick.Count < 9; i++) if (!pick.Contains(i)) pick.Add(i);
        int tw = 640, th = 360, cols = 3, rows = 3;
        var sheet = new Texture2D(tw * cols, th * rows, TextureFormat.RGBA32, false);
        var fill = new Color32[tw * cols * th * rows]; for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(20, 20, 20, 255);
        sheet.SetPixels32(fill);
        for (int k = 0; k < pick.Count && k < 9; k++)
        {
            var src = keyFrames[pick[k]]; int ox = (k % cols) * tw, oy = (rows - 1 - k / cols) * th;
            var px = new Color[tw * th];
            for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) px[y * tw + x] = src.GetPixelBilinear((x + 0.5f) / tw, (y + 0.5f) / th);
            sheet.SetPixels(ox, oy, tw, th, px);
        }
        sheet.Apply();
        File.WriteAllBytes(Path.Combine(dir, "posture_proof_contact.png"), sheet.EncodeToPNG());
        var names = new List<string>(); foreach (var i in pick) names.Add(keyNames[i]);
        Log("contact sheet: " + string.Join(", ", names.ToArray()));
        Destroy(sheet);
    }
}
