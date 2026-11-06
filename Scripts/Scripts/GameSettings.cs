using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// TIERS v96, NOT COMPILED. Player-facing options: data + API only, the settings UI is another lane. Data: StreamingAssets/game_settings.json (Assets/Data copy identical).
// UI contract: GameSettings.MenuRows() -> one Row per 'menu' entry in the json (Id / Label / Kind "choice"|"toggle" / Hint / Choices / ChoiceLabels / Value / ValueLabel / Set(string) / Cycle(+1|-1)).
//   Direct API: GraphicsTier / SetGraphicsTier(id) / GraphicsTiers() / SuggestedTier(), FreeCameraInCombat (get / set). GameSettings.Changed fires after every Set.
// Values persist in PlayerPrefs (prefsPrefix + id; the graphics tier uses quality_tiers.json prefsKey). Harness runs (RuntimeQuality.IsHarness) ignore saved values and use the json defaults.
// Readers: QualityTiers (graphics), DirectionalInput.CombatCam + FlowTable.PlayerInput (FreeCameraInCombat).
public static class GameSettings
{
    [Serializable] class GfxCfg { public bool suggestOnFirstRun = false, applyAtRuntime = true; }
    [Serializable] class CamCfg { public bool freeInCombat = false; }
    [Serializable] public class MenuEntry { public string id = "", label = "", kind = "toggle", source = "", hint = ""; }
    [Serializable] class Data { public bool enabled = true; public string prefsPrefix = "gs."; public GfxCfg graphics = new GfxCfg(); public CamCfg camera = new CamCfg(); public MenuEntry[] menu = Fallback(); }
    // baked rows for a missing game_settings.json (the json is the source of labels and hints)
    static MenuEntry[] Fallback() { return new[] { new MenuEntry { id = "graphics.tier", label = "Graphics", kind = "choice" }, new MenuEntry { id = "camera.freeInCombat", label = "Free camera in combat", kind = "toggle" } }; }

    public sealed class Row
    {
        public string Id, Label, Kind, Hint; public string[] Choices, ChoiceLabels; public Func<string> Get; public Action<string> Set;
        public string Value { get { return Get(); } }
        public string ValueLabel { get { int i = Array.IndexOf(Choices, Get()); return i >= 0 ? ChoiceLabels[i] : Get(); } }
        public void Cycle(int dir) { if (Choices.Length == 0) return; int i = Array.IndexOf(Choices, Get()); Set(Choices[((i < 0 ? 0 : i + dir) % Choices.Length + Choices.Length) % Choices.Length]); }
    }

    public static event Action Changed;
    static readonly string[] OnOff = { "off", "on" };

    static Data D; static int freeCache = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { D = null; freeCache = -1; Changed = null; }

    static Data Get()
    {
        if (D != null) return D;
        D = new Data();
        try
        {
            string p = Application.streamingAssetsPath + "/game_settings.json";
            if (File.Exists(p)) { var d = JsonUtility.FromJson<Data>(File.ReadAllText(p)); if (d != null) D = d; }
        }
        catch (Exception ex) { Debug.LogWarning("GameSettings: read failed: " + ex.Message); }
        if (D.graphics == null) D.graphics = new GfxCfg(); if (D.camera == null) D.camera = new CamCfg(); if (D.menu == null) D.menu = new MenuEntry[0]; if (string.IsNullOrEmpty(D.prefsPrefix)) D.prefsPrefix = "gs.";
        return D;
    }
    static bool UsePrefs { get { return Get().enabled && !RuntimeQuality.IsHarness(); } }
    static void Fire() { if (Changed != null) Changed(); }

    // ---------------- graphics ----------------
    public static string GraphicsTier { get { return QualityTiers.Current; } }
    public static QualityTiers.TierInfo[] GraphicsTiers() { return QualityTiers.Tiers; }
    public static string SuggestedTier() { return QualityTiers.Suggested(); }
    // QualityTiers.Early asks for this when no tier was saved yet: the suggestion only when game_settings.json says so, otherwise the data default (ultra = MAX)
    public static string FirstRunTier(string fallback) { var g = Get(); return g.enabled && g.graphics.suggestOnFirstRun ? QualityTiers.Suggested() : fallback; }
    // applyAtRuntime true: switch now (new HDRP asset, expect a hitch). false: save it, it applies at the next launch.
    public static void SetGraphicsTier(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!Get().enabled || RuntimeQuality.IsHarness()) return;
        if (Get().graphics.applyAtRuntime) QualityTiers.SetTier(id, true); else QualityTiers.Save(id);
        Fire();
    }

    // ---------------- camera ----------------
    // false (default) = today's behaviour (controls.json camera.combatCamera auto lock). true = no auto lock, the player steers; Tab still toggles the lock by hand.
    public static bool FreeCameraInCombat
    {
        get
        {
            if (freeCache < 0)
            {
                var d = Get(); bool v = d.camera.freeInCombat;
                if (UsePrefs && PlayerPrefs.HasKey(d.prefsPrefix + "camera.freeInCombat")) v = PlayerPrefs.GetInt(d.prefsPrefix + "camera.freeInCombat", 0) != 0;
                freeCache = v ? 1 : 0;
            }
            return freeCache == 1;
        }
        set
        {
            if (!UsePrefs) return;
            PlayerPrefs.SetInt(Get().prefsPrefix + "camera.freeInCombat", value ? 1 : 0); PlayerPrefs.Save(); freeCache = value ? 1 : 0; Fire();
        }
    }

    // ---------------- menu hook ----------------
    public static List<Row> MenuRows()
    {
        var rows = new List<Row>();
        foreach (var e in Get().menu)
        {
            if (e == null || string.IsNullOrEmpty(e.id)) continue;
            var r = new Row { Id = e.id, Label = e.label, Kind = e.kind, Hint = e.hint };
            if (e.id == "graphics.tier")
            {
                var t = GraphicsTiers(); var ids = new List<string>(); var labs = new List<string>();
                foreach (var ti in t) if (ti != null && !string.IsNullOrEmpty(ti.name)) { ids.Add(ti.name.ToLowerInvariant()); labs.Add(string.IsNullOrEmpty(ti.label) ? ti.name : ti.label); }
                r.Choices = ids.ToArray(); r.ChoiceLabels = labs.ToArray(); r.Get = () => GraphicsTier; r.Set = v => SetGraphicsTier(v);
            }
            else if (e.id == "camera.freeInCombat")
            {
                r.Choices = OnOff; r.ChoiceLabels = new[] { "Off", "On" };
                r.Get = () => FreeCameraInCombat ? "on" : "off"; r.Set = v => FreeCameraInCombat = v == "on" || v == "true" || v == "1";
            }
            else continue;   // unknown id: a newer json than this build, skip
            rows.Add(r);
        }
        return rows;
    }
}
