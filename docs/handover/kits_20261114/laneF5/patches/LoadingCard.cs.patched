using System;
using UnityEngine;

// LOADINGCARD v1 (dev2, 12 Nov 2026, staged in hh, not released). Plain launches showed the world black with the whole HUD drawn for the first 15-20 s (prewarm hitches, 2-6 fps).
// This puts one full-screen black card with the game name over everything until the prewarm has finished (Prewarm92.TotalSeconds >= 0) plus extraAfterPrewarmS, then fades it out over fadeS.
// Data: loading_card.json (StreamingAssets first, then Assets/Data via DataPath). Missing file or "enabled": false = nothing. Never in harness runs (RunMode.IsHarness / MenuCfg.Harness / skipArgs).
public class LoadingCard : MonoBehaviour
{
    [Serializable] class Cfg { public bool enabled = true; public string title = "FRANKENDOM", sub = "loading"; public float extraAfterPrewarmS = 0.3f, fadeS = 0.6f, maxSeconds = 40f, minSeconds = 3.0f, calmFrameS = 0.06f, calmForS = 1.5f; public int titleSize = 54, subSize = 20; public string[] skipArgs = new[] { "-camscene", "-dirproof", "-camtrace", "-zoneshot", "-routebot", "-balanceauto", "-perfbench", "-autoplay" }; public bool forceInHarness = false, waitForStartupWarm = false; }
    static Cfg cfg; float t0, doneAt = -1f, calmSince = -1f;
    static bool cleared = true;   // true when no card exists (harness runs, file missing or off); false while a card is up
    public static bool Cleared { get { return cleared; } }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cleared = true; cfg = null; }
    void OnDestroy() { cleared = true; } GUIStyle sTitle, sSub; Texture2D black;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            string t = DataPath.ReadText("loading_card.json"); if (string.IsNullOrEmpty(t)) return;
            cfg = JsonUtility.FromJson<Cfg>(t); if (cfg == null || !cfg.enabled) return;
            bool force = Array.IndexOf(Environment.GetCommandLineArgs(), "-loadcard") >= 0;   // one-off capture flag: keeps the card on in a harness run so the start a player sees can be photographed in-engine
            if (!force)
            {
                foreach (var a in Environment.GetCommandLineArgs()) foreach (var s in cfg.skipArgs) if (a.StartsWith(s, StringComparison.Ordinal)) { Debug.Log("[LoadingCard] off: harness run (" + s + ")"); return; }
                if (RunMode.IsHarness || MenuCfg.Harness) { Debug.Log("[LoadingCard] off: harness run"); return; }
            }
            var go = new GameObject("LoadingCard"); DontDestroyOnLoad(go); go.AddComponent<LoadingCard>(); cleared = false;
            Debug.Log("[LoadingCard] armed: black card until the prewarm is done + " + cfg.extraAfterPrewarmS.ToString("F1") + " s, fade " + cfg.fadeS.ToString("F1") + " s");
        }
        catch (Exception e) { Debug.LogWarning("[LoadingCard] off: " + e.Message); }
    }

    void Awake() { t0 = Time.realtimeSinceStartup; black = new Texture2D(1, 1); black.SetPixel(0, 0, Color.black); black.Apply(); }

    void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (Time.unscaledDeltaTime < cfg.calmFrameS) { if (calmSince < 0f) calmSince = now; } else calmSince = -1f;   // the world only draws once the texture / zone streaming hitches (100+ ms frames) have stopped
        if (doneAt < 0f && now - t0 >= cfg.minSeconds && Prewarm92.TotalSeconds >= 0f && calmSince >= 0f && now - calmSince >= cfg.calmForS && (!cfg.waitForStartupWarm || StartupWarm.Done)) { doneAt = now; Debug.Log("[LoadingCard] prewarm done and frames calm (" + cfg.calmForS.ToString("F1") + " s under " + (cfg.calmFrameS * 1000f).ToString("F0") + " ms) after " + (now - t0).ToString("F1") + " s: fading out"); }
        if (doneAt < 0f && now - t0 >= cfg.maxSeconds) { doneAt = now - cfg.extraAfterPrewarmS; Debug.Log("[LoadingCard] gave up waiting for the prewarm after " + cfg.maxSeconds.ToString("F0") + " s: fading out"); }
        if (doneAt >= 0f && now - doneAt >= cfg.extraAfterPrewarmS + cfg.fadeS) { Debug.Log("[LoadingCard] gone"); Destroy(gameObject); }
    }

    void OnGUI()
    {
        float now = Time.realtimeSinceStartup; float a = 1f;
        if (doneAt >= 0f) a = 1f - Mathf.Clamp01((now - doneAt - cfg.extraAfterPrewarmS) / Mathf.Max(0.05f, cfg.fadeS));
        if (a <= 0f) return;
        GUI.depth = -32000;
        var c = GUI.color; GUI.color = new Color(1f, 1f, 1f, a);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), black);
        if (sTitle == null)
        {
            sTitle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(12, Mathf.RoundToInt(cfg.titleSize * Screen.height / 1080f)), fontStyle = FontStyle.Bold }; sTitle.normal.textColor = new Color(0.85f, 0.78f, 0.62f, 1f);
            sSub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(10, Mathf.RoundToInt(cfg.subSize * Screen.height / 1080f)) }; sSub.normal.textColor = new Color(0.6f, 0.58f, 0.52f, 1f);
        }
        GUI.Label(new Rect(0, Screen.height * 0.42f, Screen.width, Screen.height * 0.1f), cfg.title, sTitle);
        GUI.Label(new Rect(0, Screen.height * 0.52f, Screen.width, Screen.height * 0.05f), cfg.sub, sSub);
        GUI.color = c;
    }
}
