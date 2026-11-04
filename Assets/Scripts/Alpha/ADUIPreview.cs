using System;
using UnityEngine;

// Offline preview for screenshots: run the player with -aduipreview [-aduishot=plan|plan_low|replay|win|lose].
// Fills ADUI with fake data. Only activates with the flag, so it is inert in normal builds.
public static class ADUIPreview
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string shot = "plan"; bool on = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a == "-aduipreview") on = true;
            if (a.StartsWith("-aduishot=")) { on = true; shot = a.Substring(10); }
        }
        if (!on) return;
        var go = new GameObject("ADUIPreview");
        var ui = go.AddComponent<ADUI>();
        ui.SetHp(ADSide.Dracula, 41f, 60f); ui.SetHp(ADSide.Pitborn, 55f, 90f);
        ui.SetWounds(ADSide.Dracula, 1, 0, 1, 0); ui.SetWounds(ADSide.Pitborn, 0, 2, 1, 0);
        ui.SetSpecialUsed(ADSide.Dracula, false); ui.SetSpecialUsed(ADSide.Pitborn, true);
        ui.BeginRound(3);
        ui.AddLog("Round 2: your heavy broke his guard (-14)");
        ui.AddLog("Round 2: his stab interrupted your heavy");
        switch (shot)
        {
            case "plan_low": ui.PreviewStance("Trickster"); ui.PreviewQueue(0, 6, -1); ui.PreviewTimer(1.8f); break;
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
    }
}
