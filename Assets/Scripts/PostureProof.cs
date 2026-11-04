using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// POSTURE PROOF HARNESS (lane P7): -postureproof [-out:<dir>] [-proofsecs:<n> (default 90)]
// Scripts Dracula (PlayerBrain / AIBrain / TouchControls off, direct DuelFighter calls like MoveSheet) against the Pitborn and takes FULL-SCREEN screenshots
// (ScreenCapture.CaptureScreenshotAsTexture after 2 frames + end of frame, so the HUD is in frame; run the player at 1920x1080):
//   (a) parry      : Pitborn swings, Dracula raises the guard just before contact (retries with other leads) -> shot at the Parried event
//   (b) guard break: heavy into a guarding low-stamina Pitborn -> shot at the GuardBroken event
//   (c) posture fill: 3 lights on the Pitborn (max overridden in memory to 30): after hit 1, hit 2, at POSTURE BREAK, +30 ticks (deathblow countdown),
//       at the deathblow hit, and after the window closed.
// Output (default <exe dir>\posture_proof\): proof_*.png, posture_proof_log.txt, posture_proof_summary.txt, posture_proof_contact.png (3x2 grid of the key frames).
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

    string dir, logPath; float t0, timeoutS = 90f; bool exiting; int exitCode; float exitAt;
    DuelFighter D, P;
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
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        if (cc) cc.enabled = false;
        f.transform.position = p; f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
    }

    // Dracula `sep` metres in front of the Pitborn on the line between them, both facing (called only while both are free to move)
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

    IEnumerator Shot(string name)
    {
        yield return null; yield return null;          // 2 frames, then the end of the frame (the HUD is drawn)
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("SHOT failed " + name + ": " + e.Message); }
        if (tex == null) yield break;
        try { File.WriteAllBytes(Path.Combine(dir, "proof_" + name + ".png"), tex.EncodeToPNG()); Log("SHOT proof_" + name + ".png " + tex.width + "x" + tex.height + (tex.width != 1920 || tex.height != 1080 ? " (NOT 1920x1080: run with -screen-width 1920 -screen-height 1080 -screen-fullscreen 0)" : "")); }
        catch (Exception e) { Log("SHOT write failed " + name + ": " + e.Message); }
        keyFrames.Add(tex); keyNames.Add(name);
        LogState("after shot " + name);
    }

    void Rest() { D.stamina = D.maxStamina; P.stamina = P.maxStamina; D.guardHeld = false; P.guardHeld = false; D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; }

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
        D.opponent = P; P.opponent = D;
        Log("start res " + Screen.width + "x" + Screen.height + " unity " + Application.unityVersion);

        // posture data overrides (memory only): enabled, Pitborn max 30 (3 lights), light 14, debug log on
        var pd = PostureTable.Get();
        pd.enabled = true; pd.debugLog = true; pd.hit.light = 14f;
        PostureTable.FighterRow row = null;
        if (pd.fighters != null) foreach (var r in pd.fighters) if (r != null && r.key == "Pitborn") row = r;
        if (row == null) { var list = new List<PostureTable.FighterRow>(pd.fighters ?? new PostureTable.FighterRow[0]); row = new PostureTable.FighterRow { key = "Pitborn" }; list.Add(row); pd.fighters = list.ToArray(); }
        row.max = 30f;
        Log("overrides: posture enabled, Pitborn max " + P.PostureMax.ToString("F0", IC) + ", Dracula max " + D.PostureMax.ToString("F0", IC) + ", hit.light " + pd.hit.light.ToString("F0", IC) + ", window " + pd.posBreak.windowTicks + " ticks");
        P.maxHealth = 1000f; P.health = 1000f; D.maxHealth = 1000f; D.health = 1000f;   // nobody dies during the proof (a lethal deathblow would end it)
        Rest(); Setup(2.0f);
        yield return Ticks(60);

        // ---- (a) parry
        int[] leads = { 5, 3, 7, 2, 9, 4 }; bool parried = false;
        for (int a = 0; a < leads.Length && !parried; a++)
        {
            Rest(); P.posture = 0f; D.posture = 0f; Setup(1.7f); yield return Ticks(130);   // parry cooldown + recoveries
            int pc = EventCount("Parried", "Pitborn") + EventCount("Parried", "Dracula"); int lead = leads[a];
            P.Press("light"); LogState("parry attempt " + (a + 1) + " Pitborn swings, guard lead " + lead);
            yield return WaitFor(() => P.state == FState.Windup, 30);
            yield return WaitFor(() => P.state != FState.Windup || P.WindupLeft <= lead, 60);
            D.guardHeld = true;
            yield return WaitFor(() => EventCount("Parried", "Pitborn") + EventCount("Parried", "Dracula") > pc || P.state == FState.Recovery || P.state == FState.Loco, 60);
            parried = EventCount("Parried", "Pitborn") + EventCount("Parried", "Dracula") > pc;
            if (parried) yield return Shot("a_parry_contact");
            D.guardHeld = false; yield return Ticks(10);
        }
        results["parry"] = parried; LogState("parry " + (parried ? "OK" : "NOT ACHIEVED"));

        // ---- (b) guard break: Pitborn guards at 6 stamina, Dracula throws a heavy
        bool gbreak = false;
        for (int a = 0; a < 3 && !gbreak; a++)
        {
            Rest(); P.posture = 0f; Setup(1.7f); yield return Ticks(130);
            int gc = EventCount("GuardBroken", "Pitborn");
            P.guardHeld = true; yield return Ticks(25);   // past the parry window: a plain block
            P.stamina = 6f; D.stamina = D.maxStamina; D.Press("heavy"); LogState("guard break attempt " + (a + 1));
            yield return WaitFor(() => { P.stamina = Mathf.Min(P.stamina, 6f); P.guardHeld = true; return EventCount("GuardBroken", "Pitborn") > gc || (D.state == FState.Recovery && D.t > 20); }, 150);
            gbreak = EventCount("GuardBroken", "Pitborn") > gc;
            if (gbreak) yield return Shot("b_guard_break");
            P.guardHeld = false; yield return Ticks(5);
        }
        results["guardbreak"] = gbreak; LogState("guard break " + (gbreak ? "OK" : "NOT ACHIEVED"));

        // ---- (c) posture fill with lights, break, deathblow
        Rest(); P.posture = 0f; D.posture = 0f; P.health = P.maxHealth; Setup(1.7f); yield return Ticks(200);
        Log("posture fill begin: Pitborn posture " + P.posture.ToString("F1", IC) + "/" + P.PostureMax.ToString("F0", IC));
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
            Log("window left " + P.DeathblowLeft + "/" + P.DeathblowTotal + " before the deathblow swing; Pitborn state " + P.state);
            D.Press("light"); LogState("deathblow light pressed");
            yield return WaitFor(() => D.statDeathblows > d0 || !P.PostureBroken, 90);
            if (D.statDeathblows > d0) yield return Shot("c_deathblow_hit");
            yield return Ticks(20); yield return Shot("c_window_closed");
        }
        else Log("NO posture break from the light sequence");
        LogState("end");
        bool ok = P.statPostureBreaks > 0 && D.statDeathblows > 0;
        Finish(0, ok ? "done" : "done, goals missed");
    }

    // ---------------------------------------------------------------- output
    void WriteSummary(int code, string why)
    {
        bool brk = P != null && P.statPostureBreaks > 0, db = D != null && D.statDeathblows > 0;
        string verdict = code == 2 ? "ERROR" : code == 3 ? "TIMEOUT" : (brk && db ? "PASS" : "FAIL");
        var sb = new StringBuilder();
        sb.AppendLine("VERDICT: " + verdict + "   (exit code " + code + ", " + why + ")");
        sb.AppendLine("posture break occurred: " + brk + "   deathblow occurred: " + db);
        foreach (var kv in results) sb.AppendLine("  step " + kv.Key + ": " + (kv.Value ? "ok" : "not achieved"));
        if (D != null && P != null) { sb.AppendLine("final " + Counters(D)); sb.AppendLine("final " + Counters(P)); }
        sb.AppendLine("screenshots: " + string.Join(", ", keyNames.ToArray()));
        sb.AppendLine("events: " + events.Count);
        try { File.AppendAllText(logPath, "\n=== SUMMARY ===\n" + sb); } catch { }
        try { File.WriteAllText(Path.Combine(dir, "posture_proof_summary.txt"), sb.ToString()); } catch { }
        Debug.Log("[postureproof] " + sb);
    }

    // 3x2 contact sheet of the key frames (the six wanted ones, else whatever exists)
    void MakeContact()
    {
        if (keyFrames.Count == 0) return;
        string[] want = { "a_parry_contact", "b_guard_break", "c_fill_after_hit2", "c_break", "c_countdown", "c_deathblow_hit" };
        var pick = new List<int>();
        foreach (var w in want) { int i = keyNames.IndexOf(w); if (i >= 0) pick.Add(i); }
        for (int i = 0; i < keyFrames.Count && pick.Count < 6; i++) if (!pick.Contains(i)) pick.Add(i);
        int tw = 640, th = 360, cols = 3, rows = 2;
        var sheet = new Texture2D(tw * cols, th * rows, TextureFormat.RGBA32, false);
        var fill = new Color32[tw * cols * th * rows]; for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(20, 20, 20, 255);
        sheet.SetPixels32(fill);
        for (int k = 0; k < pick.Count && k < 6; k++)
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
