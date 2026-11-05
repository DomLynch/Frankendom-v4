using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// CAMERA PROOF HARNESS (lane V65CAM): -camproof [-out:<dir>] [-proofsecs:<n> (default 150)]   (run at 1920x1080: -screen-width 1920 -screen-height 1080 -screen-fullscreen 0)
// Scripts both fighters (PlayerBrain / AIBrain / TouchControls off, direct placement like PostureProof) in front of the REAL lock-on CameraRig and asserts, with a screenshot of each pose:
//   (1) FRAMING   : separations camera_framing.json proof.seps (1 .. 10 m) at the arena middle and at both side edges: VisibleFraction of BOTH fighters >= proof.steadyMin (0.9) once settled
//                   (settleFrames), mean luma of the frame between lumaMin and lumaMax (no black / white frame). Lock range is raised to 80 m for the proof.
//   (2) KNOCKBACK : the enemy is moved from knockFromM to knockToM metres in knockFrames frames and back (a hard knockback / a retreat): the minimum VisibleFraction on any frame stays >= transientMin.
//   (3) SHAKE     : every class of camera_impact.json shake is fired alone (peak logged), then a burst of all classes at once: peak offset <= maxPosM and peak roll <= maxRollDeg; amplitudes rise with weight.
//   (4) SLOW-MO   : a parry dip + a merged crit request (one dip, never stacked, never below minScale), timeScale back to exactly 1 afterwards, unscaledDeltaTime untouched (HUD / UI clocks);
//                   the watchdog: timeScale forced to 0.5 by the harness is put back to 1 within watchdogS.
// Output (default <exe dir>\camproof\): cam_*.png, camproof_log.txt, camproof_summary.txt, camproof_contact.png. Exit codes: 0 PASS, 1 FAIL, 2 exception / setup failure, 3 timeout.
// Nothing on disk is changed. Time.captureFramerate = 60 so one frame = one tick.
[DefaultExecutionOrder(-61)]
public class CamProof : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Flag(string n) { return Array.IndexOf(Environment.GetCommandLineArgs(), n) >= 0; }
    static readonly System.Globalization.CultureInfo IC = System.Globalization.CultureInfo.InvariantCulture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Flag("-camproof")) return;
        Application.runInBackground = true;
        if (FindFirstObjectByType<CamProof>() != null) return;
        var go = new GameObject("CamProof"); DontDestroyOnLoad(go); go.AddComponent<CamProof>();
    }

    string dir, logPath; float t0, timeoutS = 150f; bool exiting; int exitCode; float exitAt;
    DuelFighter D, P; CameraRig rig;
    readonly List<string> rows = new List<string>(); readonly List<string> fails = new List<string>();
    readonly List<Texture2D> frames = new List<Texture2D>();

    void Log(string s)
    {
        string line = "t" + (Time.realtimeSinceStartup - t0).ToString("F2", IC).PadLeft(6) + "s f" + Time.frameCount + " " + s;
        try { File.AppendAllText(logPath, line + "\n"); } catch { }
        Debug.Log("[camproof] " + s);
    }
    void Fail(string s) { fails.Add(s); Log("FAIL " + s); }

    void Awake()
    {
        t0 = Time.realtimeSinceStartup;
        string o = Arg("-out:");
        try { dir = !string.IsNullOrEmpty(o) ? o : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)), "camproof"); } catch { dir = "camproof"; }
        try { Directory.CreateDirectory(dir); } catch { }
        logPath = Path.Combine(dir, "camproof_log.txt");
        try { File.WriteAllText(logPath, ""); } catch { }
        float v; var ps = Arg("-proofsecs:"); if (ps != null && float.TryParse(ps, System.Globalization.NumberStyles.Float, IC, out v) && v > 0f) timeoutS = v;
    }
    void Start() { StartCoroutine(Guarded(Main())); }
    void Update()
    {
        if (exiting) { if (Time.unscaledTime - exitAt > 3f) Environment.Exit(exitCode); return; }
        if (Time.realtimeSinceStartup - t0 > timeoutS) { Log("TIMEOUT after " + timeoutS.ToString("F0", IC) + " s"); Finish(3, "timeout"); }
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

    DuelFighter Find(string n) { foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f.name == n) return f; return null; }
    bool Place(DuelFighter f, Vector3 p, Vector3 facing)
    {
        var cc = f.GetComponent<CharacterController>(); bool ground = false;
        if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, ~((1 << 8) | (1 << 9)), QueryTriggerInteraction.Ignore)) { p.y = hit.point.y; ground = true; }
        if (cc) cc.enabled = false;
        f.transform.position = p; if (facing.sqrMagnitude > 1e-4f) f.transform.rotation = Quaternion.LookRotation(facing);
        if (cc) cc.enabled = true;
        Physics.SyncTransforms();
        return ground;
    }
    // the enemy at `anchor` (x,z), the player `sep` metres away along `axis` (flat); returns false when a fighter has no ground below
    bool Setup(Vector3 anchor, Vector3 axis, float sep)
    {
        axis.y = 0f; axis.Normalize();
        bool a = Place(P, anchor, -axis), b = Place(D, anchor - axis * sep, axis);
        D.moveDir = Vector3.zero; P.moveDir = Vector3.zero; D.guardHeld = false; P.guardHeld = false;
        return a && b;
    }

    float Sample(out float vd, out float vp) { vd = rig.VisibleFraction(D.transform); vp = rig.VisibleFraction(P.transform); return Mathf.Min(vd, vp); }

    IEnumerator Grab(string name, Action<float> lumaOut)
    {
        yield return new WaitForEndOfFrame();
        Texture2D tex = null;
        CaptureUtil.HideConsole(); try { tex = ScreenCapture.CaptureScreenshotAsTexture(); } catch (Exception e) { Log("SHOT failed " + name + ": " + e.Message); }
        float lu = -1f;
        if (tex != null)
        {
            lu = FinisherLook.MeanLuma(tex);
            try { File.WriteAllBytes(Path.Combine(dir, "cam_" + name + ".png"), tex.EncodeToPNG()); } catch (Exception e) { Log("SHOT write failed " + name + ": " + e.Message); }
            frames.Add(tex);
        }
        lumaOut(lu);
    }

    IEnumerator Main()
    {
        float w0 = Time.realtimeSinceStartup;
        while ((D = Find(Roster.PlayerObject)) == null || (P = Find(Roster.EnemyObject)) == null || (rig = FindFirstObjectByType<CameraRig>()) == null)
        {
            if (Time.realtimeSinceStartup - w0 > 20f) { Log("fighters / CameraRig not found"); Finish(2, "setup"); yield break; }
            yield return null;
        }
        for (int i = 0; i < 30; i++) yield return null;
        Time.captureFramerate = 60; QualitySettings.vSyncCount = 0;
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        D.opponent = P; P.opponent = D; P.maxHealth = 1000f; P.health = 1000f; D.maxHealth = 1000f; D.health = 1000f;
        rig.lockEnabled = true; rig.lockRange = 80f;
        var fr = CamFraming.Cfg(); var pr = fr.proof;
        Log("start res " + Screen.width + "x" + Screen.height + " aspect " + rig.GetComponent<Camera>().aspect.ToString("F3", IC) + " framing enabled=" + fr.enabled + " fovMax " + fr.fovMax + " safe x " + fr.safeMinX + ".." + fr.safeMaxX + " y " + fr.safeMinY + ".." + fr.safeMaxY);

        Vector3 anchor0 = P.transform.position; Vector3 axis0 = anchor0 - D.transform.position; axis0.y = 0f; if (axis0.sqrMagnitude < 0.01f) axis0 = Vector3.forward; axis0.Normalize();
        Vector3 side = new Vector3(axis0.z, 0f, -axis0.x);   // across the arena
        yield return Frames(30);

        // ---- (1) framing: separations x {middle, left edge, right edge}
        var spots = new List<KeyValuePair<string, Vector3>>();
        spots.Add(new KeyValuePair<string, Vector3>("mid", anchor0));
        spots.Add(new KeyValuePair<string, Vector3>("edgeL", new Vector3(-10f, anchor0.y, anchor0.z)));
        spots.Add(new KeyValuePair<string, Vector3>("edgeR", new Vector3(10f, anchor0.y, anchor0.z)));
        foreach (var sp in spots)
            foreach (float sep in pr.seps)
            {
                string id = sp.Key + "_sep" + sep.ToString("0.0", IC).Replace('.', 'p');
                if (!Setup(sp.Value, axis0, sep)) { rows.Add(id + ": SKIP (no ground below a fighter at this spot)"); Log("SKIP " + id + " no ground"); continue; }
                yield return Frames(pr.settleFrames);
                float minV = 1f, dMin = 1f, pMin = 1f;
                for (int i = 0; i < 12; i++) { yield return null; float vd, vp; float m = Sample(out vd, out vp); minV = Mathf.Min(minV, m); dMin = Mathf.Min(dMin, vd); pMin = Mathf.Min(pMin, vp); }
                float luma = -1f; yield return Grab(id, l => luma = l);
                string row = id + ": VisibleFraction " + Roster.PlayerName + " " + dMin.ToString("F2", IC) + " " + Roster.EnemyName + " " + pMin.ToString("F2", IC) + " (need >= " + pr.steadyMin.ToString("F2", IC) + ")  fov " + rig.GetComponent<Camera>().fieldOfView.ToString("F1", IC)
                    + " (widen " + CamFraming.Widen.ToString("F1", IC) + ")  cam dist to look " + Vector3.Distance(rig.transform.position, (D.transform.position + P.transform.position) * 0.5f).ToString("F1", IC) + " m  luma " + luma.ToString("F3", IC);
                rows.Add(row); Log(row);
                if (minV < pr.steadyMin) Fail(id + " framing " + minV.ToString("F2", IC));
                if (luma < pr.lumaMin || luma > pr.lumaMax) Fail(id + " black / white frame, luma " + luma.ToString("F3", IC));
            }

        // ---- (2) knockback sweep
        Setup(anchor0, axis0, pr.knockFromM); yield return Frames(pr.settleFrames);
        float tMin = 1f; int tAt = -1;
        for (int leg = 0; leg < 2; leg++)
            for (int i = 0; i <= pr.knockFrames; i++)
            {
                float u = (float)i / Mathf.Max(1, pr.knockFrames); if (leg == 1) u = 1f - u;
                float sep = Mathf.Lerp(pr.knockFromM, pr.knockToM, u);
                Place(P, D.transform.position + axis0 * sep, -axis0); P.moveDir = Vector3.zero;
                yield return null; yield return new WaitForEndOfFrame();
                float vd, vp; float m = Sample(out vd, out vp); if (m < tMin) { tMin = m; tAt = leg * 1000 + i; }
            }
        yield return Frames(pr.settleFrames);
        { float vd, vp; float m = Sample(out vd, out vp); string row = "knockback " + pr.knockFromM + " -> " + pr.knockToM + " m in " + pr.knockFrames + " frames and back: min VisibleFraction " + tMin.ToString("F2", IC) + " at step " + tAt + " (need >= " + pr.transientMin.ToString("F2", IC) + "), settled " + m.ToString("F2", IC);
          rows.Add(row); Log(row); if (tMin < pr.transientMin) Fail("knockback transient " + tMin.ToString("F2", IC)); if (m < pr.steadyMin) Fail("knockback settle " + m.ToString("F2", IC)); }

        // ---- (3) shake
        Setup(anchor0, axis0, 2.5f); yield return Frames(60);
        var sh = CamShake.Cfg(); string[] cls = { "blockLight", "hitLight", "parry", "kick", "blockHeavy", "hitHeavy", "guardBreak", "special", "crit", "deathblow" };
        var sb = new StringBuilder("shake peaks (m / deg, mul 1, single pulse): ");
        float lastAmp = -1f; bool mono = true; float maxOff = 0f, maxRoll = 0f;
        foreach (var c in cls)
        {
            CamShake.Clear(); CamShake.PeakOffset = 0f; CamShake.PeakRoll = 0f; yield return Frames(40);
            CamShake.Trigger(c, 1f); yield return Frames(45);
            sb.Append(c + " " + CamShake.PeakOffset.ToString("F3", IC) + "/" + CamShake.PeakRoll.ToString("F2", IC) + "  ");
            maxOff = Mathf.Max(maxOff, CamShake.PeakOffset); maxRoll = Mathf.Max(maxRoll, CamShake.PeakRoll);
            foreach (var cr in sh.classes) if (cr != null && cr.id == c) { if (cr.amp + 1e-6f < lastAmp && c != "kick" && c != "parry") mono = false; if (c != "kick" && c != "parry") lastAmp = cr.amp; }
        }
        Log(sb.ToString()); rows.Add(sb.ToString());
        CamShake.Clear(); CamShake.PeakOffset = 0f; CamShake.PeakRoll = 0f;
        for (int i = 0; i < 16; i++) { CamShake.Trigger(cls[i % cls.Length], 1.5f); yield return null; }
        yield return Frames(60);
        { string row = "shake burst (16 pulses, mul 1.5): peak offset " + CamShake.PeakOffset.ToString("F3", IC) + " m (cap " + sh.maxPosM.ToString("F3", IC) + "), peak roll " + CamShake.PeakRoll.ToString("F2", IC) + " deg (cap " + sh.maxRollDeg.ToString("F2", IC) + "), classes weight-ordered " + mono;
          rows.Add(row); Log(row);
          if (CamShake.PeakOffset > sh.maxPosM + 1e-4f) Fail("shake offset above cap"); if (CamShake.PeakRoll > sh.maxRollDeg + 1e-3f) Fail("shake roll above cap"); if (!mono) Fail("shake amplitudes not weight ordered in the data");
          if (maxOff <= 0f) Fail("shake never moved the camera (is CamShake.Apply wired into CameraRig?)"); }
        CamShake.Clear();

        // ---- (4) slow-mo
        var sm = CombatSlowMo.Cfg();
        yield return Frames(Mathf.CeilToInt((sm.cooldownS + 0.2f) * 60f));
        int s0 = CombatSlowMo.Started, m0 = CombatSlowMo.Merged; float minTs = 1f, minUdt = 1f, maxUdt = 0f; bool ok1 = CombatSlowMo.Request("parry"); yield return null; bool ok2 = CombatSlowMo.Request("crit");
        int guard = 0;
        while ((CombatSlowMo.Active || guard < 3) && guard++ < 600) { minTs = Mathf.Min(minTs, Time.timeScale); minUdt = Mathf.Min(minUdt, Time.unscaledDeltaTime); maxUdt = Mathf.Max(maxUdt, Time.unscaledDeltaTime); yield return null; }
        yield return Frames(10);
        { string row = "slow-mo: parry request " + ok1 + ", crit request (merge) " + ok2 + ", started " + (CombatSlowMo.Started - s0) + " merged " + (CombatSlowMo.Merged - m0) + ", min timeScale " + minTs.ToString("F3", IC) + " (floor " + sm.minScale.ToString("F2", IC) + "), after " + Time.timeScale.ToString("F4", IC) + ", fixedDeltaTime " + Time.fixedDeltaTime.ToString("F4", IC) + ", unscaledDeltaTime " + minUdt.ToString("F4", IC) + ".." + maxUdt.ToString("F4", IC);
          rows.Add(row); Log(row);
          if (!ok1) Fail("slow-mo parry request refused"); if (CombatSlowMo.Started - s0 != 1) Fail("slow-mo started " + (CombatSlowMo.Started - s0) + " dips (should be 1: merged, never stacked)");
          if (minTs < sm.minScale - 1e-3f) Fail("slow-mo below minScale"); if (minTs > 0.999f) Fail("slow-mo never slowed the clock"); if (Mathf.Abs(Time.timeScale - 1f) > 1e-4f) Fail("timeScale not restored: " + Time.timeScale);
          if (maxUdt > minUdt * 1.6f + 0.002f) Fail("unscaledDeltaTime changed during slow-mo (UI clocks affected)"); }
        // watchdog: a foreign stuck timeScale
        Time.timeScale = 0.5f; float wd0 = Time.unscaledTime; int wf0 = CombatSlowMo.WatchdogFires; guard = 0;
        while (Time.timeScale < 0.999f && guard++ < 1200) yield return null;
        { string row = "watchdog: timeScale 0.5 left by 'someone' -> " + Time.timeScale.ToString("F3", IC) + " after " + (Time.unscaledTime - wd0).ToString("F2", IC) + " s (watchdogS " + sm.watchdogS.ToString("F1", IC) + "), fires " + (CombatSlowMo.WatchdogFires - wf0);
          rows.Add(row); Log(row); if (Time.timeScale < 0.999f || CombatSlowMo.WatchdogFires - wf0 != 1) Fail("watchdog did not restore the timeScale"); }
        Time.timeScale = 1f;

        Finish(fails.Count == 0 ? 0 : 1, fails.Count == 0 ? "all checks passed" : fails.Count + " check(s) failed");
    }

    IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

    void Finish(int code, string why)
    {
        if (exiting) return;
        exiting = true; exitCode = code; exitAt = Time.unscaledTime; Time.captureFramerate = 0; Time.timeScale = 1f;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("camproof_summary  " + DateTime.Now.ToString("s") + "  unity " + Application.unityVersion + "  " + Screen.width + "x" + Screen.height);
            sb.AppendLine("VERDICT: " + (code == 0 ? "PASS" : "FAIL") + "   (exit code " + code + ", " + why + ")");
            foreach (var r in rows) sb.AppendLine("  " + r);
            foreach (var f in fails) sb.AppendLine("  FAIL " + f);
            File.WriteAllText(Path.Combine(dir, "camproof_summary.txt"), sb.ToString());
        }
        catch (Exception e) { Log("summary failed: " + e.Message); }
        try { MakeContact(); } catch (Exception e) { Log("contact sheet failed: " + e.Message); }
        Application.Quit(code);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    void MakeContact()
    {
        if (frames.Count == 0) return;
        int cols = 4, cw = 480, ch = 270, rowsN = (frames.Count + cols - 1) / cols;
        var sheet = new Texture2D(cols * cw, rowsN * ch, TextureFormat.RGB24, false);
        for (int i = 0; i < frames.Count; i++)
        {
            var t = frames[i]; int ox = (i % cols) * cw, oy = (rowsN - 1 - i / cols) * ch;
            for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++) sheet.SetPixel(ox + x, oy + y, t.GetPixel(x * t.width / cw, y * t.height / ch));
        }
        sheet.Apply(); File.WriteAllBytes(Path.Combine(dir, "camproof_contact.png"), sheet.EncodeToPNG());
    }
}
