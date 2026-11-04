using System;
using System.Collections;
using System.IO;
using UnityEngine;

// Offline preview for screenshots (inert without the flag).
//   Interactive:  Dracula.exe -aduipreview [-aduishot=plan|plan_low|replay|win|lose]   (stays up, free cursor, clickable)
//   Screenshots:  Dracula.exe -aduishot=all|plan|plan_low|replay|win|lose [-aduiwounds] [-aduispecial] [-screen-width 1920 -screen-height 1080 -screen-fullscreen 0]
//                 writes <exe dir>\aduishots\<mode>_<W>x<H>.png and quits (about 3 s per shot, all = ~14 s).
public static class ADUIPreview
{
    static readonly string[] AllModes = { "plan", "plan_low", "replay", "win", "lose" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string shot = "plan"; bool on = false, capture = false, wounds = false, special = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a == "-aduipreview") on = true;
            if (a == "-aduiwounds") wounds = true;
            if (a == "-aduispecial") special = true;
            if (a.StartsWith("-aduishot=")) { on = true; capture = true; shot = a.Substring(10); }
        }
        if (!on) return;
        var host = new GameObject("ADUIPreview");
        UnityEngine.Object.DontDestroyOnLoad(host);
        var run = host.AddComponent<Runner>();
        run.StartCoroutine(run.Go(capture, shot, wounds, special));
    }

    class Runner : MonoBehaviour
    {
        public IEnumerator Go(bool capture, string shot, bool wounds, bool special)
        {
            yield return null;
            string[] modes = shot == "all" ? AllModes : new[] { shot };
            string dir = null;
            if (capture)
            {
                string exeDir = Path.GetDirectoryName(Application.dataPath);
                dir = Path.Combine(exeDir, "aduishots");
                try { Directory.CreateDirectory(dir); } catch (Exception e) { Debug.LogError("ADUIPreview: " + e.Message); dir = null; }
            }
            foreach (var mode in modes)
            {
                ADUI ui = Build(mode, wounds, special);
                if (!capture) yield break;                         // interactive: keep the first one
                yield return new WaitForSecondsRealtime(2.3f);     // round banner fades out first
                yield return new WaitForEndOfFrame();
                if (dir != null)
                {
                    string path = Path.Combine(dir, mode + "_" + Screen.width + "x" + Screen.height + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    Debug.Log("ADUIPreview: wrote " + path);
                }
                yield return new WaitForSecondsRealtime(0.5f);     // let the async write finish
                if (ui != null) Destroy(ui.gameObject);
                yield return null;
            }
            Application.Quit();
        }

        static ADUI Build(string mode, bool wounds, bool special)
        {
            var go = new GameObject("ADUIPreviewUI");
            go.SetActive(false);                                   // so cfg can be set before Awake
            var ui = go.AddComponent<ADUI>();
            ui.cfg = ADUI.LoadCfg();
            if (wounds) ui.cfg.showWounds = true;
            if (special) ui.cfg.showSpecial = true;
            go.SetActive(true);
            ui.SetHp(ADSide.Dracula, 41f, 80f); ui.SetHp(ADSide.Pitborn, 55f, 120f);
            ui.SetWounds(ADSide.Dracula, 1, 0, 1, 0); ui.SetWounds(ADSide.Pitborn, 0, 2, 1, 0);
            ui.SetSpecialUsed(ADSide.Dracula, false); ui.SetSpecialUsed(ADSide.Pitborn, true);
            ui.BeginRound(3);
            ui.AddLog("Round 2: your heavy broke his guard (-14)");
            ui.AddLog("Round 2: his stab interrupted your heavy");
            switch (mode)
            {
                case "plan_low": ui.PreviewStance("Trickster"); ui.PreviewQueue(0, 5, -1); ui.PreviewTimer(1.8f); break;
                case "replay":
                    ui.BeginResolve();
                    ui.ShowBeatRaw("Guard", "Light", "Parried", "Staggered", 0, 6, false, "Your guard beat his light.");
                    ui.ShowBeatRaw("Heavy", "Roll", "Whiff", "Dodged", 0, 0, false, "He rolled out of your heavy.");
                    ui.ShowBeatRaw("Slash", "Sidestep", "Hit", "Dodged", 0, 11, true, "Your slash caught his sidestep.");
                    ui.ShowReplay(); break;
                case "win": ui.PreviewPhase("End"); ui.ShowEnd(true); break;
                case "lose": ui.PreviewPhase("End"); ui.ShowEnd(false); break;
                default: ui.PreviewStance("Aggressive"); ui.PreviewQueue(0, 3, -1); ui.PreviewTimer(7.4f); break;
            }
            return ui;
        }
    }
}
