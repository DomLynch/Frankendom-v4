using System;
using System.Collections.Generic;
using UnityEngine;

// HCKIT4 v95 (HC3 K12, residual after laneREBIND): PlayerBrain hands the player back to the keyboard when a key is held after a touch (TouchControls.Active). The key list (W A S D Space E Q F)
// was a literal chain of Input.GetKey calls. It is controls.json "touchRelease.keys" now (KeyCode names). Per-action rebinding is laneREBIND's InputMap; this list is the stock set.
public static class TouchRelease
{
    [Serializable] class Cfg { public string[] keys = { "W", "A", "S", "D", "Space", "E", "Q", "F" }; }
    [Serializable] class Root { public Cfg touchRelease; }
    static KeyCode[] keys;

    static void Load()
    {
        var names = new Cfg().keys;
        try
        {
            string s = DataPath.ReadText("controls.json");
            if (!string.IsNullOrEmpty(s)) { var r = JsonUtility.FromJson<Root>(s); if (r != null && r.touchRelease != null && r.touchRelease.keys != null && r.touchRelease.keys.Length > 0) names = r.touchRelease.keys; }
        }
        catch (Exception ex) { Debug.LogWarning("TouchRelease: controls.json read failed: " + ex.Message); }
        var list = new List<KeyCode>();
        for (int i = 0; i < names.Length; i++) { KeyCode k; if (Enum.TryParse(names[i], true, out k)) list.Add(k); }
        keys = list.ToArray();
    }

    /// <summary>true while any key of controls.json touchRelease.keys is held</summary>
    public static bool AnyHeld()
    {
        if (keys == null) Load();
        for (int i = 0; i < keys.Length; i++) if (Input.GetKey(keys[i])) return true;
        return false;
    }
}
