#if !FRANKEN_NO_HARNESS // FIXHARNESS v1: harness code, compiled unless the release build passes FRANKEN_NO_HARNESS (BuildPlayer -noharness)
using System.IO;
using UnityEngine;

// Test harness: with -autoplay, captures screenshots, logs fps, asserts the enemy death pose, then quits.
public class AutoShot : MonoBehaviour
{
    bool on, hero, stand, profile, gaps, props, face, faceLit; float faceT; public static int RecFrame; bool cmp; int cmpHit = -1, cmpInit; string trailMode = "", rimArg = ""; static string evLog = ""; bool rec; int swingN, hitN, hitPending; bool slowmo; float slowUntil; System.Collections.Generic.List<(int frame, string name)> capQ = new System.Collections.Generic.List<(int, string)>(); float lastSwing; int[] burstMask = new int[2]; string hitType; float lastHit; float minSep = 99f; int propFrames; int gapIdx, gapFrames; int standFrames; float t; int frames, frames2; float[] at = { 3f, 7f, 11f, 15f }; int next; float minDt = 9f, maxDt;
    string framing = ""; bool framingFail; string dir; Health enemy, player; Transform enemyHips; float deadAt = -1f; bool deathShot; string deathResult = "death=NOT_REACHED";
    // watchdog (autoplay only): if the main thread makes no progress for 20 s, dump state + Player.log tail and exit so a hang is caught
    static volatile int frameTick; static long lastTickTime; static System.Threading.Timer wd;
    void StartWatchdog()
    {
        lastTickTime = System.Diagnostics.Stopwatch.GetTimestamp(); string outDir = dir;
        string logPath = Application.consoleLogPath; if (string.IsNullOrEmpty(logPath)) logPath = System.IO.Path.Combine(Application.persistentDataPath, "Player.log");   // PERF3 v100: was a hard-coded ...\LocalLow\DefaultCompany\HadesSlice path; read on the main thread, the timer thread must not touch Application
        wd = new System.Threading.Timer(_ =>
        {
            double idle = (System.Diagnostics.Stopwatch.GetTimestamp() - lastTickTime) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (idle > 20.0)
            {
                try
                {
                    string log = logPath;
                    string tail = System.IO.File.Exists(log) ? string.Join("\n", System.Linq.Enumerable.TakeLast(System.IO.File.ReadAllLines(log), 40)) : "no log";
                    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "watchdog.txt"), $"HANG: no frame for {idle:F0}s, last frame {frameTick}\n{tail}");
                }
                catch { }
                System.Environment.Exit(2);
            }
        }, null, 5000, 2000);
    }
    void Start()
    {
        Application.runInBackground = ReleaseCfg.RunInBackground();   // RCHYGIENE v100: a player build pauses when it loses focus; proof / gate runs (harness flags, -logFile, -out:, ... release.json) keep running
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-autoplay") on = true;
        dir = Path.Combine(Application.dataPath, "..", "PlayShots"); foreach (var oa in System.Environment.GetCommandLineArgs()) if (oa.StartsWith("-out:")) dir = oa.Substring(5); Directory.CreateDirectory(dir);
        if (on) StartWatchdog();
        ContactRelay.OnEvent += e => { if (evLog.Length < 600) evLog += $"{e.type}:{e.moveId}:{e.location}:{e.direction}{(e.counter ? ":counter" : "")}{(e.rear ? ":rear" : "")}{(e.perfect ? ":perfect" : "")}; "; };
        ContactRelay.OnContact += (a2, d2, ty, hv, pf2) => { if (on && hitN < 4 && capQ.Count == 0 && Time.unscaledTime - lastHit > 1.2f) { lastHit = Time.unscaledTime; string hn = $"hit_{hitN}_{ty}{(hv ? "H" : "")}"; hitN++; ScreenCapture.CaptureScreenshot(Path.Combine(dir, hn + "_f0.png")); if (slowmo) { Time.timeScale = 0.2f; slowUntil = Time.unscaledTime + 1.0f; capQ.Add((Time.frameCount + 2, hn + "_f2")); capQ.Add((Time.frameCount + 4, hn + "_f4")); capQ.Add((Time.frameCount + 7, hn + "_f7")); } else capQ.Add((Time.frameCount + 1, hn + "_f1")); } };
        {
            var ar = System.Environment.GetCommandLineArgs(); var ls = System.Array.IndexOf(ar, "-noshadowlight") >= 0; var np = System.Array.IndexOf(ar, "-noparticles") >= 0; var nr = System.Array.IndexOf(ar, "-norocks") >= 0; var nf = System.Array.IndexOf(ar, "-nofog") >= 0; var nsh = System.Array.IndexOf(ar, "-noshadows") >= 0; var npost = System.Array.IndexOf(ar, "-nopost") >= 0;
            if (ls) foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.type == LightType.Point) l.shadows = LightShadows.None;
            if (np) foreach (var p in FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)) p.gameObject.SetActive(false);
            if (nr) foreach (var g in FindObjectsByType<Transform>(FindObjectsSortMode.None)) if (g.name == "Boulders" || g.name == "SetDressing" || g.name == "gravel") g.gameObject.SetActive(false);
            foreach (var a2 in System.Environment.GetCommandLineArgs()) { if (a2 == "-notrees") foreach (var g in FindObjectsByType<Transform>(FindObjectsSortMode.None)) if (g.name.StartsWith("dead_tree")) g.gameObject.SetActive(false); if (a2 == "-nocards") foreach (var g in FindObjectsByType<Transform>(FindObjectsSortMode.None)) if (g.name == "DoorGlowCard" || g.name == "DoorVoidGradient") g.gameObject.SetActive(false); if (a2 == "-novoid" || a2 == "-noglow") foreach (var g in FindObjectsByType<Transform>(FindObjectsSortMode.None)) if (g.name == (a2 == "-novoid" ? "DoorVoidGradient" : "DoorGlowCard")) g.gameObject.SetActive(false); if (a2 == "-nospot") foreach (var g in FindObjectsByType<Light>(FindObjectsSortMode.None)) if (g.name == "DoorWarmLight" || g.name == "DoorSpill") g.gameObject.SetActive(false); if (a2 == "-noprops") { var pr = GameObject.Find("Props"); if (pr) pr.SetActive(false); } }
            if (nf) foreach (var g in FindObjectsByType<Transform>(FindObjectsSortMode.None)) if (g.name.StartsWith("WaterFog") || g.name.StartsWith("GroundFog")) g.gameObject.SetActive(false);
            if (nsh) foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) l.shadows = LightShadows.None;
            if (npost) foreach (var v in FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None)) v.enabled = false;
        }
        foreach (var a in System.Environment.GetCommandLineArgs()) { if (a == "-hero") hero = true; if (a == "-stand") stand = true; if (a == "-facecam") { face = true; stand = true; } if (a == "-profile" && !System.Array.Exists(System.Environment.GetCommandLineArgs(), x => x.StartsWith("-profiledata:", System.StringComparison.Ordinal))) profile = true; /* unity-dev: OPT4 ProfileMode also uses -profile; with -profiledata: it is the OPT4 mode, not this photo mode */ if (a == "-gaps") gaps = true; }
        if (gaps)
        {
            foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is Duel || b is DuelFighter) b.enabled = false;
            on = true;
        }
        foreach (var a in System.Environment.GetCommandLineArgs()) { if (a == "-props") props = true; if (a == "-slowmo") slowmo = true; if (a == "-record") rec = true; if (a == "-cmp") cmp = true; if (a.StartsWith("-trail:")) trailMode = a.Substring(7); if (a.StartsWith("-rim:")) rimArg = a.Substring(5);  if (a.StartsWith("-hide:")) foreach (var g in Resources.FindObjectsOfTypeAll<Transform>()) if (g.gameObject.scene.IsValid() && g.name == a.Substring(6)) g.gameObject.SetActive(false); }
        if (props)
        {
            var rig = GetComponent<CameraRig>(); if (rig) rig.enabled = false;
            foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is Duel || b is DuelFighter) b.enabled = false;
            on = true;
        }
        if (profile)
        {
            on = true; hero = true;
            // measure the stairway: raycast down the centreline every 5 cm, find riser jumps and tread lengths
            var sb = new System.Text.StringBuilder(); float prevY = float.NaN, treadStart = 0f; int steps = 0; float minRise = 9, maxRise = 0, minTread = 9, maxTread = 0;
            for (float z = -3f; z < 22f; z += 0.05f)
            {
                if (!Physics.Raycast(new Vector3(0f, 12f, z), Vector3.down, out var rh, 30f, ~0, QueryTriggerInteraction.Ignore)) continue;
                float y = rh.point.y;
                if (!float.IsNaN(prevY) && Mathf.Abs(y - prevY) > 0.04f) { float rise = y - prevY; if (rise > 0) { minRise = Mathf.Min(minRise, rise); maxRise = Mathf.Max(maxRise, rise); float tr = z - treadStart; if (steps > 0) { minTread = Mathf.Min(minTread, tr); maxTread = Mathf.Max(maxTread, tr); } steps++; treadStart = z; } }
                prevY = y;
            }
            File.WriteAllText(Path.Combine(dir, "stairs.txt"), $"steps={steps} riser={minRise:F2}-{maxRise:F2} tread={minTread:F2}-{maxTread:F2} (expect riser 0.18-0.20, tread 0.40-0.45; landings excepted)");
        }
        if (stand)
        {
            foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is Duel) b.enabled = false;
            on = true;
        }
        if (hero)
        {
            // concept camera: low, centred on the axis, looking up the causeway at the gate; no fight
            var rig = GetComponent<CameraRig>(); if (rig) rig.enabled = false;
            var d = FindFirstObjectByType<Duel>(); if (d) d.enabled = false;
            foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain) b.enabled = false;
            if (profile) { transform.position = new Vector3(-40f, 2.5f, 15f); transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.8f, 15f) - transform.position); }
            else { transform.position = new Vector3(0f, 1.4f, 0f); transform.rotation = Quaternion.LookRotation(new Vector3(0f, 8f, 27f) - transform.position); }
            GetComponent<Camera>().fieldOfView = 48f; on = true;
            foreach (var ca in System.Environment.GetCommandLineArgs()) if (ca.StartsWith("-cam:"))   // -cam:x,y,z,tx,ty,tz[,fov]  explicit camera for review shots (-camname:<file>)
            {
                var q = ca.Substring(5).Split(','); var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (q.Length >= 6) { transform.position = new Vector3(float.Parse(q[0], ci), float.Parse(q[1], ci), float.Parse(q[2], ci)); transform.rotation = Quaternion.LookRotation(new Vector3(float.Parse(q[3], ci), float.Parse(q[4], ci), float.Parse(q[5], ci)) - transform.position); if (q.Length >= 7) GetComponent<Camera>().fieldOfView = float.Parse(q[6], ci); }
            }
            foreach (var h in FindObjectsByType<Health>(FindObjectsSortMode.None)) h.gameObject.SetActive(false);
        }
        foreach (var h in FindObjectsByType<Health>(FindObjectsSortMode.None)) { if (h.name == Roster.EnemyObject) enemy = h; else player = h; }
        if (enemy) foreach (var tr in enemy.GetComponentsInChildren<Transform>()) if (tr.name == "mixamorig:Hips") enemyHips = tr;
    }
    static string GetPath(Transform t) { string p = t.name; while (t.parent) { t = t.parent; p = t.name + "/" + p; } return p; }
    void Update()
    {
        frameTick++; lastTickTime = System.Diagnostics.Stopwatch.GetTimestamp();
        if (player && enemy && !player.Dead && !enemy.Dead) { var dd = player.transform.position - enemy.transform.position; dd.y = 0f; minSep = Mathf.Min(minSep, dd.magnitude); }
        if (Time.unscaledDeltaTime > 0.1f) Debug.Log($"SLOW frame {Time.frameCount} dt={Time.unscaledDeltaTime * 1000f:F0}ms t={Time.realtimeSinceStartup:F1}s");
        if (rec && on)
        {
            if (RecFrame == 0) { Time.captureFramerate = 30; Directory.CreateDirectory(Path.Combine(dir, "rec")); }
            RecFrame++; if (RecFrame <= 600) ScreenCapture.CaptureScreenshot(Path.Combine(dir, "rec", $"f_{RecFrame:00000}.png"));
            if (RecFrame == 604) File.WriteAllLines(Path.Combine(dir, "cues.txt"), CueBus.Log);
        }
        if (cmp && on)
        {
            if (cmpInit == 0) { cmpInit = 1; Time.captureFramerate = 60; UnityEngine.Random.InitState(42);
                if (trailMode != "") foreach (var wt in FindObjectsByType<WeaponTrail>(FindObjectsSortMode.None)) { wt.ember = trailMode == "ember"; wt.Apply(); }
                if (rimArg != "") { var rl = GameObject.Find(Roster.EnemyObject + "RedRim"); if (rl) { var lt = rl.GetComponent<Light>(); float rv = float.Parse(rimArg, System.Globalization.CultureInfo.InvariantCulture); lt.intensity = rv; lt.enabled = rv > 0f; } }
                ContactRelay.OnContact += (a3, d3, ty3, hv3, pf3) => { if (cmpHit < 0 && ty3 == "Hit" && hv3) cmpHit = Time.frameCount + 3; }; }
            if (cmpHit >= 0 && Time.frameCount == cmpHit) ScreenCapture.CaptureScreenshot(Path.Combine(dir, "cmp_hit.png"));
            if (Time.frameCount == 300) ScreenCapture.CaptureScreenshot(Path.Combine(dir, "cmp_wide.png"));
            if (Time.frameCount > 330 && cmpHit >= 0 && Time.frameCount > cmpHit + 5) { File.WriteAllText(Path.Combine(dir, "cmp.txt"), "hitframe=" + cmpHit); Application.Quit(); }
        }
        if (!on) return;
        if (props)
        {
            propFrames++;
            var cam = GetComponent<Camera>(); cam.fieldOfView = 62f;
            if (propFrames < 60) { transform.position = new Vector3(0.5f, 1.7f, 10.5f); transform.rotation = Quaternion.LookRotation(new Vector3(0f, 4.5f, 24f) - transform.position); }
            else if (propFrames < 120) { transform.position = new Vector3(0.5f, 1.7f, 10.5f); transform.rotation = Quaternion.LookRotation(new Vector3(-14f, 2.2f, 13f) - transform.position); }
            else { transform.position = new Vector3(2f, 2.2f, 6f); transform.rotation = Quaternion.LookRotation(new Vector3(14.5f, 3.5f, 13f) - transform.position); }
            if (propFrames == 40) { var ray = cam.ScreenPointToRay(new Vector3(960f, 1080f - 450f, 0f)); var sb2 = new System.Text.StringBuilder(); foreach (var rr in FindObjectsByType<Renderer>(FindObjectsSortMode.None)) { if (!rr.enabled || !rr.gameObject.activeInHierarchy) continue; var bb = rr.bounds; bb.Expand(0.06f); if (bb.IntersectRay(ray, out float dd) && dd < 40f) { float mn = Mathf.Min(bb.size.x, Mathf.Min(bb.size.y, bb.size.z)); if (!(rr is ParticleSystemRenderer)) sb2.AppendLine($"{rr.GetType().Name} {GetPath(rr.transform)} d={dd:F1} size={rr.bounds.size}"); } } File.WriteAllText(Path.Combine(dir, "pick.txt"), sb2.ToString()); }
            if (propFrames == 50) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, "props_gate.png")); var sb = new System.Text.StringBuilder(); foreach (var ps in FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)) if (ps.name == "BrazierFire") sb.AppendLine($"{ps.name} pos={ps.transform.position} playing={ps.isPlaying} n={ps.particleCount} active={ps.gameObject.activeInHierarchy} mat={ps.GetComponent<ParticleSystemRenderer>().sharedMaterial} size={ps.main.startSize.constantMax}"); File.WriteAllText(Path.Combine(dir, "props.txt"), sb.ToString()); }
            if (propFrames == 110) ScreenCapture.CaptureScreenshot(Path.Combine(dir, "props_left.png"));
            if (propFrames == 170) ScreenCapture.CaptureScreenshot(Path.Combine(dir, "props_tree.png"));
            if (propFrames == 190) Application.Quit();
            return;
        }
        if (!props && !gaps && !stand && player && enemy)
        {
            var fsA = new[] { player.GetComponent<DuelFighter>(), enemy.GetComponent<DuelFighter>() };
            for (int fi = 0; fi < 2; fi++)
            {
                var f2 = fsA[fi]; if (f2 == null) continue;
                if (f2.state != FState.Active) { burstMask[fi] = 0; continue; }
                foreach (int k in new[] { 4, 6, 8 })
                    if (f2.t >= k && (burstMask[fi] & (1 << k)) == 0 && swingN < 4) { burstMask[fi] |= 1 << k; ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"swing_{swingN}_{f2.name}_t{k}.png")); if (k == 8) swingN++; }
            }
            for (int qi = capQ.Count - 1; qi >= 0; qi--) if (Time.frameCount >= capQ[qi].frame) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, capQ[qi].name + ".png")); capQ.RemoveAt(qi); }
            if (slowmo && Time.timeScale < 1f && Time.unscaledTime > slowUntil) Time.timeScale = 1f;
        }
        if (gaps)
        {
            // V3 camera test: 3 gaps x 3 places (flat pad, stair bottom, mid-stairs); both fighters upright, camera = the live rig
            float[] gp = { 1.5f, 2.5f, 4f }; float[] zs = { 12.8f, 16.9f, 19.2f };
            int total = gp.Length * zs.Length;
            if (gapIdx >= total) { Application.Quit(); return; }
            var dr = GameObject.Find(Roster.PlayerObject); var pi = GameObject.Find(Roster.EnemyObject);
            float z0 = zs[gapIdx / gp.Length], g = gp[gapIdx % gp.Length];
            if (gapFrames == 0)
            {
                foreach (var go in new[] { dr, pi }) { var cc = go.GetComponent<CharacterController>(); if (cc) cc.enabled = false; }
                Vector3 pp = new Vector3(0f, 0f, z0), ep = new Vector3(0f, 0f, z0 + g);
                foreach (var pr in new[] { (dr, pp), (pi, ep) }) { var p = pr.Item2; if (Physics.Raycast(new Vector3(p.x, 20f, p.z), Vector3.down, out var hit, 40f, PhysMasks.Ground)) p.y = hit.point.y; pr.Item1.transform.position = p; }
                dr.transform.rotation = Quaternion.LookRotation(Vector3.forward); pi.transform.rotation = Quaternion.LookRotation(Vector3.back);
            }
            gapFrames++;
            if (gapFrames == 45) { ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"gap_{(gapIdx % gp.Length)}_{gapIdx / gp.Length}.png")); framing += $"z{z0:F1}/gap{g}: P={GetComponent<CameraRig>().VisibleFraction(dr.transform):F2} E={GetComponent<CameraRig>().VisibleFraction(pi.transform):F2}; "; }
            if (gapFrames == 60) { gapIdx++; gapFrames = 0; }
            if (gapIdx >= total) File.WriteAllText(Path.Combine(dir, "gaps.txt"), framing);
            return;
        }
        if (face)
        {
            faceT += Time.unscaledDeltaTime; var rigF = GetComponent<CameraRig>(); if (rigF) rigF.enabled = false;
            var mh = GameObject.Find("MetaHumanHead"); var drF = GameObject.Find(Roster.PlayerObject);
            foreach (var fb in System.Environment.GetCommandLineArgs()) if (fb.StartsWith("-facebone:")) { var parts = fb.Substring(10).Split('/'); var ch = GameObject.Find(parts[0]); if (ch != null) { drF = ch; mh = null; foreach (var tt in ch.GetComponentsInChildren<Transform>(true)) if (tt.name == parts[1]) { mh = tt.gameObject; break; } } }
            if (mh && drF) { Vector3 hp = mh.transform.position; Vector3 fo = new Vector3(0f, 0.02f, 0.85f); foreach (var fa in System.Environment.GetCommandLineArgs()) if (fa.StartsWith("-faceoff:")) { var q = fa.Substring(9).Split(','); if (q.Length >= 3) fo = new Vector3(float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture)); }
                transform.position = hp + drF.transform.right * fo.x + Vector3.up * fo.y + drF.transform.forward * fo.z; transform.rotation = Quaternion.LookRotation((hp - transform.position).normalized); GetComponent<Camera>().fieldOfView = 26f; }
            if (faceT > 0.5f && !faceLit) { faceLit = true; foreach (var fa in System.Environment.GetCommandLineArgs()) if (fa.StartsWith("-facelight:")) { float lux = float.Parse(fa.Substring(11), System.Globalization.CultureInfo.InvariantCulture); foreach (var ol in FindObjectsByType<Light>(FindObjectsSortMode.None)) ol.enabled = false; var go = new GameObject("FaceTestKey"); var tl = go.AddComponent<Light>(); tl.type = LightType.Directional; tl.color = Color.white; var thd = go.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>(); thd.SetIntensity(lux, UnityEngine.Rendering.LightUnit.Lux); go.transform.rotation = transform.rotation; } }
            if (faceT > 4f && !deathShot) { deathShot = true; ScreenCapture.CaptureScreenshot(Path.Combine(dir, "face.png")); }
            if (faceT > 6f) Application.Quit();
        }
        if (stand)
        {
            t += Time.unscaledDeltaTime;
            standFrames++;
            if (standFrames < 90)
            {
                var dr = GameObject.Find(Roster.PlayerObject); var pi = GameObject.Find(Roster.EnemyObject);
                if (dr && pi) { var args = System.Environment.GetCommandLineArgs(); for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-pitH" && float.TryParse(args[i + 1], out var ph)) pi.transform.localScale = Vector3.one * (ph / 2.1f); dr.GetComponent<CharacterController>().enabled = false; pi.GetComponent<CharacterController>().enabled = false; dr.transform.position = new Vector3(-1.1f, 0.1f, 13.6f); pi.transform.position = new Vector3(1.1f, 0.1f, 13.6f); dr.transform.rotation = Quaternion.Euler(0, 0, 0); pi.transform.rotation = Quaternion.Euler(0, 0, 0); }
            }
            if (standFrames == 140) ScreenCapture.CaptureScreenshot(Path.Combine(dir, "stand.png"));
            if (standFrames > 170) Application.Quit();
            return;
        }
        if (hero)
        {
            t += Time.unscaledDeltaTime;
            if (t > 4f && !deathShot) { deathShot = true; { string hn = "hero"; foreach (var na in System.Environment.GetCommandLineArgs()) if (na.StartsWith("-camname:")) hn = na.Substring(9); ScreenCapture.CaptureScreenshot(Path.Combine(dir, hn + ".png")); } }
            if (t > 6f) Application.Quit();
            return;
        }
        t += Time.unscaledDeltaTime; if (t > 2f) frames2++; if (t > 1f) { frames++; minDt = Mathf.Min(minDt, Time.unscaledDeltaTime); maxDt = Mathf.Max(maxDt, Time.unscaledDeltaTime); }
        if (next < at.Length && t >= at[next]) { var rg = GetComponent<CameraRig>(); if (rg && enemy && player) { float vp = rg.VisibleFraction(player.transform), ve = rg.VisibleFraction(enemy.transform); framing += $"shot{next}:player={vp:F2},enemy={ve:F2} P=({player.transform.position.x:F1},{player.transform.position.y:F1},{player.transform.position.z:F1}) E=({enemy.transform.position.x:F1},{enemy.transform.position.y:F1},{enemy.transform.position.z:F1}) C=({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}) "; if (next > 0 && (vp < 0.7f || ve < 0.7f)) framingFail = true; } ScreenCapture.CaptureScreenshot(Path.Combine(dir, "play_" + next + ".png")); next++; }
        if (enemy && enemy.Dead && deadAt < 0f) deadAt = t;
        if (deadAt >= 0f && !deathShot && t - deadAt >= 2.5f)
        {
            deathShot = true;
            float hy = enemyHips ? enemyHips.position.y - enemy.transform.position.y : -1f;
            deathResult = $"death_hips_y={hy:F2} {(hy >= 0f && hy < 0.5f ? "PASS" : "FAIL")}";
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "death.png"));
        }
        if (rec ? RecFrame >= 606 : (t >= 40f || (deathShot && t - deadAt >= 3.5f)))
        {
            string s = "";
            foreach (var h in FindObjectsByType<Health>(FindObjectsSortMode.None)) s += h.name + "=" + h.current + " ";
            File.WriteAllText(Path.Combine(dir, "report.txt"), $"avg_fps={frames / (t - 1f):F1} steady_fps={frames2 / Mathf.Max(0.1f, t - 2f):F1} min_fps={1f / maxDt:F1} max_fps={1f / minDt:F1} gpu={SystemInfo.graphicsDeviceName} res={Screen.width}x{Screen.height} hp: {s} {deathResult} events=[{evLog}] min_sep={minSep:F2} cues={(FindFirstObjectByType<CueBus>() ? FindFirstObjectByType<CueBus>().played : -1)} framing={(framingFail ? "FAIL" : "PASS")} [{framing}]");
            Application.Quit();
        }
    }
}

















































#else // FIXHARNESS v1 stub nl=0
public static class AutoShot { public static int RecFrame; }   // release stub: CueBus.cs logs AutoShot.RecFrame
#endif // FIXHARNESS v1
