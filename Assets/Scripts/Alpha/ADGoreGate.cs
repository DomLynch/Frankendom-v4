using System;
using UnityEngine;

// ALPHA lane B2: one switchboard for wounds / gore / finishers. Flags live in the "rules" block of duel_rules.json (lane A2 owns that block):
//   enableWounds, enableGore, enableFinishers  (bool). A flag that is MISSING counts as FALSE (inert by default).
// When a flag is false the lane B scripts do nothing: no component attached, no decals, no shader/material lookups, no reflection, neutral rule effects.
public static class ADGoreGate
{
    [Serializable] class Flags { public bool enableWounds, enableGore, enableFinishers; }
    [Serializable] class Root { public Flags rules; }

    static bool loaded, wounds, gore, finishers;
    static int forced = -1;   // test override: -1 none, 0 all off, 1 all on

    public static bool Wounds { get { Load(); return wounds; } }
    public static bool Gore { get { Load(); return gore; } }
    public static bool Finishers { get { Load(); return finishers; } }

    public static void ForceAll(bool on) { forced = on ? 1 : 0; }
    public static void Reload() { loaded = false; forced = -1; Load(); }

    static void Load()
    {
        if (forced >= 0) { wounds = gore = finishers = forced == 1; loaded = true; return; }
        if (loaded) return;
        loaded = true; wounds = gore = finishers = false;
        try
        {
            string t = ADData.LoadText();
            if (string.IsNullOrEmpty(t)) return;
            var r = JsonUtility.FromJson<Root>(t);
            if (r != null && r.rules != null) { wounds = r.rules.enableWounds; gore = r.rules.enableGore; finishers = r.rules.enableFinishers; }
        }
        catch (Exception e) { Debug.LogWarning("ADGoreGate: flags unreadable, wounds/gore/finishers stay OFF: " + e.Message); }
    }
}
