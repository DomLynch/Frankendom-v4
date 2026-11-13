using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// DiagContactGrab (unity-dev2, QA only, flag -contactgrab:<dir>[:maxPerKind]): the game's own back buffer (ScreenCapture, full window size, no rescale) saved at the END OF THE FRAME in which a contact event fires.
// kinds: heavy (a heavy hit on a non-player), joanhit (Joan is hit), kill (the target is dead: a 4-frame burst at 0 / 0.12 / 0.3 / 0.6 s of unscaled time so the slow-mo dip shows).
// Files: <dir>\<kind>_<n>_<move>_<target>_t<tick>[_k<burst>].png and a [ContactGrab] log line. Nothing happens without the flag.
public sealed class DiagContactGrab : MonoBehaviour
{
    string dir; int maxPer = 3; readonly Dictionary<string, int> count = new Dictionary<string, int>(); readonly Dictionary<string, float> last = new Dictionary<string, float>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith("-contactgrab:", System.StringComparison.OrdinalIgnoreCase)) continue;
            var rest = a.Substring(13); int mp = 3; int i = rest.LastIndexOf(':'); if (i > 2 && int.TryParse(rest.Substring(i + 1), out mp)) rest = rest.Substring(0, i); else mp = 3;
            var g = new GameObject("DiagContactGrab"); DontDestroyOnLoad(g); var d = g.AddComponent<DiagContactGrab>(); d.dir = rest; d.maxPer = Mathf.Max(1, mp);
            try { Directory.CreateDirectory(rest); } catch (System.Exception) { }
            Debug.Log("[ContactGrab] armed -> " + rest + " (max " + d.maxPer + " per kind)"); return;
        }
    }

    void OnEnable() { ContactRelay.OnEvent += OnEvt; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvt; }

    void OnEvt(CombatEvent e)
    {
        if (e.type != "Hit" || e.target == null) return;
        if (e.actor != null && e.actor == GameLoop.Player) Debug.Log("[HitLog] t" + e.tick + " move=" + e.moveId + " target=" + e.target.name + " hpNow=" + e.target.health.ToString("F1") + " dead=" + e.target.Dead + " heavy=" + e.heavy);   // QA: per-hit proof that a special lands (damage = diff of hpNow between lines)
        MaybeBurst(e);
        string kind = e.target.Dead ? "kill" : (e.target == GameLoop.Player ? "joanhit" : (e.heavy || e.weight == "heavy" ? "heavy" : null));
        if (kind == null) return; int n; count.TryGetValue(kind, out n); if (n >= maxPer) return;
        float lt; if (last.TryGetValue(kind, out lt) && Time.unscaledTime - lt < 2f) return;
        count[kind] = n + 1; last[kind] = Time.unscaledTime;
        string tn = e.target.name.Replace('#', '_'); string baseName = kind + "_" + (n + 1) + "_" + (e.moveId ?? "x") + "_" + tn + "_t" + e.tick;
        Debug.Log("[ContactGrab] " + kind + " " + baseName + " clk=" + Time.unscaledTime.ToString("F2") + " timeScale " + Time.timeScale.ToString("F2") + " heavy=" + e.heavy + " move=" + e.moveId + " target=" + e.target.name + " actor=" + (e.actor != null ? e.actor.name : "-"));
        StartCoroutine(Grab(baseName, kind == "kill"));
    }

    // -hitburst:<dir>[:n]: a 2 s burst (every frame ~30 Hz, unscaled time) of the back buffer from the first heavy or skill_ hit Joan lands, max n bursts (default 2), 8 s apart.
    string burstDir; int burstMax = 2, burstDone; float burstLast = -99f;
    void MaybeBurst(CombatEvent e)
    {
        if (burstDir == null) { burstDir = ""; foreach (var a in System.Environment.GetCommandLineArgs()) if (a.StartsWith("-hitburst:", System.StringComparison.OrdinalIgnoreCase)) { var r = a.Substring(10); int i = r.LastIndexOf(':'); int n; if (i > 2 && int.TryParse(r.Substring(i + 1), out n)) { r = r.Substring(0, i); burstMax = n; } burstDir = r; try { Directory.CreateDirectory(r); } catch (System.Exception) { } } }
        if (burstDir.Length == 0 || burstDone >= burstMax || e.actor == null || e.actor != GameLoop.Player || e.target == GameLoop.Player) return;
        if (!(e.heavy || (e.moveId != null && e.moveId.StartsWith("skill_")))) return;
        if (Time.unscaledTime - burstLast < 8f) return; burstLast = Time.unscaledTime; burstDone++;
        string nm = "burst" + burstDone + "_" + e.moveId + "_t" + e.tick; Debug.Log("[HitBurst] start " + nm + " clk=" + Time.unscaledTime.ToString("F2"));
        StartCoroutine(Burst(nm));
    }
    IEnumerator Burst(string nm)
    {
        float t0 = Time.unscaledTime, next = 0f; int k = 0;
        while (Time.unscaledTime - t0 < 2f)
        {
            yield return new WaitForEndOfFrame();
            if (Time.unscaledTime - t0 >= next) { try { ScreenCapture.CaptureScreenshot(Path.Combine(burstDir, nm + "_f" + k.ToString("D2") + "_" + (Time.unscaledTime - t0).ToString("F2") + "s.png")); } catch (System.Exception) { } k++; next += 1f / 30f; }
        }
        Debug.Log("[HitBurst] done " + nm + " frames " + k);
    }

    IEnumerator Grab(string baseName, bool burst)
    {
        float[] at = burst ? new[] { 0f, 0.12f, 0.3f, 0.6f } : new[] { 0f }; float t0 = Time.unscaledTime;
        for (int k = 0; k < at.Length; k++)
        {
            while (Time.unscaledTime - t0 < at[k]) yield return null;
            yield return new WaitForEndOfFrame();
            try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, baseName + (burst ? "_k" + k : "") + ".png")); } catch (System.Exception ex) { Debug.LogWarning("[ContactGrab] " + ex.Message); }
            Debug.Log("[ContactGrab]   grab " + baseName + (burst ? "_k" + k : "") + " timeScale " + Time.timeScale.ToString("F2"));
        }
    }
}
