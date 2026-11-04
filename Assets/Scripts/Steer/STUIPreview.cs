using System;
using System.Collections;
using System.IO;
using UnityEngine;

// Steer UI screenshot / preview mode (inert without the flag). Fakes data (stance, cooldowns, HP, feed lines) and shows STUI over the live scene.
//   Dracula.exe -steershot=<all|bar|cooldown|lowhp> [-screen-width 1920 -screen-height 1080 -screen-fullscreen 0]
//   writes <exe dir>\steershots\<mode>_<W>x<H>.png (about 2 s per shot) and quits.
//   Dracula.exe -steerpreview   interactive (clickable, no capture, no quit); STUIPreview.On stays true.
public static class STUIPreview
{
    public static bool On;
    public static STStance Stance = STStance.Aggressive;
    public static readonly float[] Cd = new float[7];
    public static readonly bool[] En = { true, true, true, true, true, true, false };
    public static float Hp0 = 100f, Hp1 = 100f;

    static readonly string[] AllModes = { "bar", "cooldown", "lowhp" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string shot = "bar"; bool on = false, capture = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a == "-steerpreview") on = true;
            if (a.StartsWith("-steershot=")) { on = true; capture = true; shot = a.Substring(11); }
        }
        if (!on) return;
        On = true;
        STUI.Ensure();
        var host = new GameObject("STUIPreview"); UnityEngine.Object.DontDestroyOnLoad(host);
        var run = host.AddComponent<Runner>();
        run.StartCoroutine(run.Go(capture, shot));
    }

    static void Set(string mode)
    {
        for (int i = 0; i < 7; i++) { Cd[i] = 0f; En[i] = i != 6; }
        var ui = STUI.Ensure(); ui.ClearFeed();
        switch (mode)
        {
            case "cooldown":
                Stance = STStance.Trickster; Hp0 = 64f; Hp1 = 71f;
                Cd[0] = 0.92f; Cd[1] = 0.6f; Cd[2] = 0.35f; Cd[3] = 0.15f; Cd[4] = 0.75f; Cd[5] = 0f; En[6] = false;
                ui.PushFeed("Press! Dracula closes in"); ui.PushFeed("You parried his overhead"); ui.PushFeed("Feint: he bit, riposte window open");
                break;
            case "lowhp":
                Stance = STStance.Defensive; Hp0 = 22f; Hp1 = 14f; En[6] = true;
                Cd[3] = 0.4f; Cd[4] = 0.2f;
                ui.PushFeed("Go for the kill is ready"); ui.PushFeed("Dodged the maul: free hit"); ui.PushFeed("He is staggering");
                break;
            default:
                Stance = STStance.Aggressive; Hp0 = 86f; Hp1 = 100f;
                Cd[1] = 0.3f; ui.PushFeed("You parried his overhead"); ui.PushFeed("Press: Dracula closes in");
                break;
        }
    }

    class Runner : MonoBehaviour
    {
        public IEnumerator Go(bool capture, string shot)
        {
            yield return null;
            string[] modes = shot == "all" ? AllModes : new[] { shot };
            string dir = null;
            if (capture)
            {
                try { dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "steershots"); Directory.CreateDirectory(dir); }
                catch (Exception e) { Debug.LogError("STUIPreview: " + e.Message); dir = null; }
            }
            foreach (var mode in modes)
            {
                Set(mode);
                if (!capture) yield break;
                yield return new WaitForSecondsRealtime(1.2f);
                yield return new WaitForEndOfFrame();
                if (dir != null)
                {
                    string path = Path.Combine(dir, mode + "_" + Screen.width + "x" + Screen.height + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    Debug.Log("STUIPreview: wrote " + path);
                }
                yield return new WaitForSecondsRealtime(0.6f);
            }
            Application.Quit();
        }
    }
}
