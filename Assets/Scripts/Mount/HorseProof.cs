using System;
using System.IO;
using UnityEngine;

// HORSEPROOF v1 (unity-build): the -horseproof launch flag drives ONE scripted horse proof so hit / flee / kill / hooves can be shown without a human:
// wait for an ambient horse near Joan, walk to it, send virtual light hits (AmbientHorses.ProofHit, the same path as the F10 debug key) until it dies or the time is up,
// with [HorseProof] log lines and in-game screenshots (-out:<dir>): hit frame, then 6 frames 0.15 s apart (the flee gait, hooves), +1 s, +3 s, dead.
// Joan is driven here (brains off, like FeetClip / MountProof); the save is never written. Needs horse_behaviour.json enabled=true (the build's data). Nothing happens without the flag, never in harness runs.
[DefaultExecutionOrder(-150)]
public sealed class HorseProof : MonoBehaviour
{
    static string Arg(string prefix) { foreach (var a in Environment.GetCommandLineArgs()) if (a.StartsWith(prefix, StringComparison.Ordinal)) return a.Substring(prefix.Length); return null; }
    static bool Has(string flag) { foreach (var a in Environment.GetCommandLineArgs()) if (a == flag) return true; return false; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (!Has("-horseproof") || MenuCfg.Harness) return; var go = new GameObject("HorseProof"); DontDestroyOnLoad(go); go.AddComponent<HorseProof>(); }

    DuelFighter df; string dir; float t0 = -1f, nextBind, nextLog, tHit, tEnd; int hits, shots; Transform tgt; bool done, quitting, afterHit; float afterT, killT = -1f, tTele = -10f; int seq, killSeq;
    const float WarmS = 10f, FindMaxS = 50f, TotalMaxS = 110f, HitGapS = 1.3f, ReachM = 2.4f, SeeM = 90f;   // hc:ok seconds / metres of a proof tool (not gameplay values)

    void Awake() { dir = Arg("-out:") ?? Path.Combine(Application.dataPath, "..", "HorseProof"); try { Directory.CreateDirectory(dir); } catch (Exception) { } Debug.Log("[HorseProof] armed, frames -> " + dir); }

    bool Bind()
    {
        if (df != null) return true; if (Time.unscaledTime < nextBind) return false; nextBind = Time.unscaledTime + 0.5f;   // hc:ok poll period
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { df = f; break; }
        if (df != null) foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)) if (b is PlayerBrain || b is AIBrain || b is TouchControls) b.enabled = false;
        return df != null;
    }

    Transform Nearest()
    {
        Transform best = null; float bd = SeeM * SeeM; var p = df.transform.position;
        foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!t.name.StartsWith("AmbientHorse_", StringComparison.Ordinal) || t.parent != null && t.parent.name.StartsWith("AmbientHorse_", StringComparison.Ordinal)) continue;   // the horses are "AmbientHorse_<row>_<n>"; "AmbientHorses" is the manager object (first proof run drove to it, 44 m away)
            var d = t.position - p; d.y = 0f; if (d.sqrMagnitude < bd) { bd = d.sqrMagnitude; best = t; }
        }
        return best;
    }

    void Shot(string n) { try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, n + ".png")); shots++; } catch (Exception e) { Debug.LogWarning("[HorseProof] shot failed " + e.Message); } }

    void Update()
    {
        if (!Bind()) return; if (t0 < 0f) t0 = Time.time; float t = Time.time - t0;
        if (done) { if (!quitting && Time.time > tEnd) { quitting = true; Debug.Log("[HorseProof] END hits=" + hits + " shots=" + shots + " quitting"); Invoke(nameof(Quit), 2f); } return; }   // hc:ok quit delay seconds
        if (t < WarmS) { df.moveDir = Vector3.zero; return; }
        if (Time.time >= nextLog) { nextLog = Time.time + 2f; Debug.Log("[HorseProof] t=" + t.ToString("F1") + " hits=" + hits + " target=" + (tgt != null ? tgt.name + " dist " + Flat(tgt).ToString("F1") : "none") + " player=" + df.transform.position.ToString("F1")); }   // hc:ok log period
        if (t > TotalMaxS) { Debug.Log("[HorseProof] TIMEOUT hits=" + hits); Finish(); return; }
        if (tgt == null || !tgt.gameObject.activeInHierarchy)
        {
            tgt = Nearest();
            if (tgt == null) { if (t > WarmS + FindMaxS) { Debug.Log("[HorseProof] FAIL no AmbientHorse object within " + SeeM + " m after " + FindMaxS + " s"); Finish(); } return; }
            Debug.Log("[HorseProof] target " + tgt.name + " dist " + Flat(tgt).ToString("F1") + " m"); return;
        }
        var to = tgt.position - df.transform.position; to.y = 0f; float d = to.magnitude; if (d < 0.01f) return;   // hc:ok divide guard
        df.transform.rotation = Quaternion.LookRotation(to / d, Vector3.up);   // face the horse (the duel only faces its opponent)
        if (afterHit) { float at = Time.time - afterT; if (seq == 0 && at >= 0.15f) { Shot("h_flee_" + 1); seq = 1; } else if (seq >= 1 && seq < 6 && at >= 0.15f * (seq + 1)) { Shot("h_flee_" + (seq + 1)); seq++; } else if (seq == 6 && at >= 1.0f) { Shot("h_p1s"); seq = 7; } else if (seq == 7 && at >= 3.0f) { Shot("h_p3s"); seq = 8; afterHit = false; } }   // hc:ok frame offsets in seconds
        if (killT > 0f) { float kt = Time.time - killT; if (killSeq == 0 && kt >= 0.9f) { Shot("h_kill_0p9s"); killSeq = 1; } else if (killSeq == 1 && kt >= 3.0f) { Shot("h_kill_3s"); killSeq = 2; } }   // hc:ok frame offsets in seconds (falling, lying)
        if (d > ReachM)
        {
            // HORSEPROOF v2: walking did not work (z5 run: Joan moved 9 m in 100 s and the distance GREW 29.1 -> 31.4 m), so the proof TELEPORTS Joan 1.8 m short of the horse (same CharacterController off / position / on as ZoneRuntime); the flee gait is then chased the same way
            if (Time.time - tTele >= 0.6f)   // hc:ok seconds between teleports
            {
                tTele = Time.time; var cc = df.GetComponent<CharacterController>(); bool en = cc != null && cc.enabled; if (en) cc.enabled = false;
                df.transform.position = new Vector3(tgt.position.x, df.transform.position.y, tgt.position.z) - to / d * 1.8f;   // hc:ok metres short of the horse
                if (en) cc.enabled = true; Debug.Log("[HorseProof] teleport next to " + tgt.name + " (was " + d.ToString("F1") + " m)");
            }
            df.moveDir = Vector3.zero; return;
        }
        df.moveDir = Vector3.zero; df.run = false;
        if (Time.time - tHit >= HitGapS)
        {
            tHit = Time.time; hits++; Debug.Log("[HorseProof] virtual hit #" + hits + " on " + tgt.name + " dist " + d.ToString("F1") + " m");
            if (hits == 4) { killT = Time.time; killSeq = 0; }   // the 4th light hit is the kill on the same horse (hitsToKill 4); the log says which horse died
            if (hits == 1) { Shot("h_hit_t0"); afterHit = true; afterT = Time.time; seq = 0; }
            AmbientHorses.ProofHit = true;
            if (hits >= 12) { Debug.Log("[HorseProof] 12 hits sent, stopping"); Finish(); }   // hc:ok safety cap
        }
    }
    float Flat(Transform x) { var v = x.position - df.transform.position; v.y = 0f; return v.magnitude; }
    void Finish() { done = true; tEnd = Time.time + 4f; Shot("h_end"); }   // hc:ok seconds for the last captures
    void Quit() { Application.Quit(); }
}
