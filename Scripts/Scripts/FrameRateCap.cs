using System;
using System.IO;
using UnityEngine;

// v98 (laneDRILL, Dom's "annoying drilling noise"): the game had no frame cap (vSyncCount 0 in every quality level, Application.targetFrameRate never set): menus / victory screens rendered at hundreds of fps
// = possible GPU coil whine. Data: frameRate.json { enabled, gameplay, victory, menu } (fps; 0 / -1 = uncapped). Applied once at boot and on the state change (the end screen = GameLoop.Outcome != None).
// "menu" is for the menu lane (MenuState.Open) - it is applied while Time.timeScale == 0 and no fight is running. Harness runs (any -xxxproof / -balanceauto / -looptest flag) are never capped.
public class FrameRateCap : MonoBehaviour
{
    [Serializable] class Cfg { public bool enabled = true; public int gameplay = 120, victory = 60, menu = 60; }
    static Cfg cfg; int last = -2;

    static Cfg Load()
    {
        var c = new Cfg();
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "frameRate.json"), Path.Combine(Application.dataPath, "Data/frameRate.json") })
        {
            try { if (File.Exists(p)) { c = JsonUtility.FromJson<Cfg>(File.ReadAllText(p)) ?? c; break; } } catch (Exception) { }
        }
        return c;
    }

    static bool Harness()
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.EndsWith("proof", StringComparison.OrdinalIgnoreCase) || a == "-balanceauto" || a == "-looptest" || a == "-verifyGround" || a == "-finisherauto" || a == "-woundshot" || a == "-perfab" /* PERFAB */ || a == "-floorshot" || a.StartsWith("-physproof", StringComparison.OrdinalIgnoreCase) || a == "-autoplay" || a == "-perfharness") return true;
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Harness()) return;
        cfg = Load(); if (!cfg.enabled) return;
        var go = new GameObject("FrameRateCap"); DontDestroyOnLoad(go); go.AddComponent<FrameRateCap>();
    }

    void Update()
    {
        int want = GameLoop.Outcome != GameLoop.Result.None ? cfg.victory : (Time.timeScale == 0f ? cfg.menu : cfg.gameplay);
        if (want == last) return;
        last = want;
        QualitySettings.vSyncCount = 0;   // the cap only applies with vSync off
        Application.targetFrameRate = want > 0 ? want : -1;
    }
}
