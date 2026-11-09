using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// PADEXTRAS v1 (laneTOP20C): the gamepad actions the controller audit found missing, all from Assets/Data/pad_extras.json. NEW file; the only edit of an existing file is the marked hunk in CameraRig.cs.
// Default OFF: with no pad_extras.json, or "enabled": false, the boot hook returns before creating anything and PadExtras.Look returns false at once, so CameraRig behaves EXACTLY as before.
//
//   rows[]  action on a pad button or an Input Manager axis edge (D-pad = an axis in the legacy Input Manager), optional chord (buttons that must be held), the row with the most chord buttons wins:
//             "press:<move>"  -> DuelFighter.Press(<move>)   (press:kick)      "feint" -> DuelFighter.Feint()
//             "potionCycle"   -> PotionUse.Cycle() (private static, called by reflection; the potion HUD already follows PotionUse.Selected)      "recenter" -> CameraRig.Recenter()
//   look    right stick turns the FREE camera (CameraRig hunk calls PadExtras.Look). Never while locked on (the stick is the attack / guard direction there) and never while a listed button is held
//           (blockWhileHeld: LB / RB, so a guard or an attack flick never also turns the camera).
//   run is NOT here: it is a plain InputMap row (controls.json run.pad = JoystickButton8, L3 hold), see the MANIFEST.
// Stance switch stays on D-pad left / right (stances.json padAxisX = DPadX, StanceLayer); this file only uses the D-pad Y axis "DPadY" (kit part "input" adds it to InputManager.asset).
// An undefined axis (Input.GetAxisRaw throws) switches that axis off with ONE warning. Harness runs (MenuCfg.Harness) never start it. Nothing is hard-coded but algorithm constants; every number is in the json.

[Serializable]
public class PadExtrasData
{
    [Serializable] public class LookC { public bool enabled = true; public string axisX = "RightStickX", axisY = "RightStickY"; public float deadZone = 0.2f, yawDegPerS = 120f, pitchDegPerS = 70f, responsePower = 1.5f; public bool invertY = false; public string[] blockWhileHeld = new string[0]; }
    [Serializable] public class Row { public string id = "", action = "", axis = "", button = "", note = ""; public float axisSign = 1f; public string[] chord = new string[0]; public bool disabled = false; }
    public int version = 1; public bool enabled = false, debugLog = false; public float axisThreshold = 0.5f;
    public LookC look = new LookC(); public Row[] rows = new Row[0];
}

public static class PadExtras
{
    public static PadExtrasData D;   // null = off
    static PadExtrasDriver drv; static KeyCode[] blockKeys = new KeyCode[0];
    static readonly HashSet<string> brokenAxes = new HashSet<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { D = null; drv = null; brokenAxes.Clear(); blockKeys = new KeyCode[0]; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (drv != null) return;
        try
        {
            if (MenuCfg.Harness) return;
            string txt = DataPath.ReadText("pad_extras.json"); if (string.IsNullOrEmpty(txt)) return;
            var d = JsonUtility.FromJson<PadExtrasData>(txt); if (d == null || !d.enabled) return;
            if (d.rows == null) d.rows = new PadExtrasData.Row[0]; if (d.look == null) d.look = new PadExtrasData.LookC(); if (d.look.blockWhileHeld == null) d.look.blockWhileHeld = new string[0];
            blockKeys = Keys(d.look.blockWhileHeld); D = d;
            var go = new GameObject("PadExtras"); UnityEngine.Object.DontDestroyOnLoad(go); drv = go.AddComponent<PadExtrasDriver>(); drv.Init(d);
            Debug.Log("[PadExtras] on: " + d.rows.Length + " rows, look " + (d.look.enabled ? "on" : "off"));
        }
        catch (Exception e) { D = null; Debug.LogWarning("[PadExtras] off (" + e.GetType().Name + ": " + e.Message + ")"); }
    }

    public static KeyCode[] Keys(string[] names)
    {
        var l = new List<KeyCode>(); if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (!string.IsNullOrEmpty(names[i]) && Enum.TryParse(names[i], true, out k)) l.Add(k); }
        return l.ToArray();
    }
    public static bool AllHeld(KeyCode[] ks) { for (int i = 0; i < ks.Length; i++) if (!Input.GetKey(ks[i])) return false; return true; }
    static bool AnyHeld(KeyCode[] ks) { for (int i = 0; i < ks.Length; i++) if (Input.GetKey(ks[i])) return true; return false; }

    /// <summary>Axis value, 0 when the axis is not defined in the Input Manager (one warning, then that axis is skipped).</summary>
    public static float Axis(string name)
    {
        if (string.IsNullOrEmpty(name) || brokenAxes.Contains(name)) return 0f;
        try { return Input.GetAxisRaw(name); }
        catch (Exception) { brokenAxes.Add(name); Debug.LogWarning("[PadExtras] Input Manager axis '" + name + "' is not defined: that binding is off (kit part 'input' adds DPadY)"); return 0f; }
    }

    /// <summary>CameraRig free-camera hunk: degrees per second to add to yaw / pitch from the right stick. False = leave the camera exactly as it was (off, locked on, a block button held, inside the dead zone).</summary>
    public static bool Look(bool lockedOn, out float yawDegPerS, out float pitchDegPerS)
    {
        yawDegPerS = 0f; pitchDegPerS = 0f; var d = D;
        if (d == null || !d.look.enabled || lockedOn || AnyHeld(blockKeys)) return false;
        float x = Axis(d.look.axisX), y = Axis(d.look.axisY); if (d.look.invertY) y = -y;
        float dz = Mathf.Clamp(d.look.deadZone, 0f, 0.95f), m = Mathf.Sqrt(x * x + y * y); if (m <= dz) return false;   // hc:ok algorithm constant (clamp / curve guard / prefix length); tunables are in pad_extras.json
        float k = Mathf.Pow((m - dz) / (1f - dz), Mathf.Max(0.1f, d.look.responsePower)) / m;   // radial dead zone + response curve   // hc:ok algorithm constant (clamp / curve guard / prefix length); tunables are in pad_extras.json
        yawDegPerS = x * k * d.look.yawDegPerS; pitchDegPerS = y * k * d.look.pitchDegPerS; return true;
    }

    // ---------------------------------------------------------------- the actions
    static MethodInfo cycleM; static bool cycleTried; static CameraRig rig;
    public static void Do(string action, DuelFighter df)
    {
        if (string.IsNullOrEmpty(action)) return;
        if (action.StartsWith("press:", StringComparison.Ordinal)) { if (df != null) df.Press(action.Substring(6)); return; }   // hc:ok algorithm constant (clamp / curve guard / prefix length); tunables are in pad_extras.json
        if (action == "feint") { if (df != null) df.Feint(); return; }
        if (action == "recenter") { if (rig == null) rig = UnityEngine.Object.FindFirstObjectByType<CameraRig>(); if (rig != null) rig.Recenter(); return; }
        if (action == "potionCycle")
        {
            if (!cycleTried) { cycleTried = true; cycleM = typeof(PotionUse).GetMethod("Cycle", BindingFlags.Static | BindingFlags.NonPublic); if (cycleM == null) Debug.LogWarning("[PadExtras] PotionUse.Cycle not found: potionCycle is off"); }
            if (cycleM != null) cycleM.Invoke(null, null);
            return;
        }
        Debug.LogWarning("[PadExtras] unknown action '" + action + "'");
    }
}

public sealed class PadExtrasDriver : MonoBehaviour
{
    PadExtrasData d; KeyCode[][] chords; KeyCode[] buttons; readonly Dictionary<string, bool> prev = new Dictionary<string, bool>();
    public void Init(PadExtrasData data)
    {
        d = data; chords = new KeyCode[d.rows.Length][]; buttons = new KeyCode[d.rows.Length];
        for (int i = 0; i < d.rows.Length; i++)
        {
            var r = d.rows[i]; if (r == null) { chords[i] = new KeyCode[0]; continue; }
            chords[i] = PadExtras.Keys(r.chord); var k = PadExtras.Keys(string.IsNullOrEmpty(r.button) ? null : new[] { r.button }); buttons[i] = k.Length > 0 ? k[0] : KeyCode.None;
        }
    }

    void Update()
    {
        var df = GameLoop.Player; if (df == null || df.Dead || MenuUI.Open || Duel.FrozenFor(df)) { prev.Clear(); return; }
        int n = d.rows.Length; var best = new Dictionary<string, int>(); var edge = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var r = d.rows[i]; if (r == null || r.disabled || string.IsNullOrEmpty(r.action)) continue;
            string key; bool down;
            if (buttons[i] != KeyCode.None) { key = "b:" + buttons[i]; down = Input.GetKeyDown(buttons[i]); }
            else if (!string.IsNullOrEmpty(r.axis))
            {
                key = "a:" + r.axis + (r.axisSign < 0f ? "-" : "+"); bool on = Mathf.Abs(PadExtras.Axis(r.axis)) >= d.axisThreshold && PadExtras.Axis(r.axis) * (r.axisSign < 0f ? -1f : 1f) > 0f;
                bool was; prev.TryGetValue(key, out was); down = on && !was;
            }
            else continue;
            if (!down || !PadExtras.AllHeld(chords[i])) continue;
            edge[i] = true; int cur; if (!best.TryGetValue(key, out cur) || chords[i].Length > cur) best[key] = chords[i].Length;
        }
        for (int i = 0; i < n; i++)
        {
            if (!edge[i]) continue; var r = d.rows[i]; string key = buttons[i] != KeyCode.None ? "b:" + buttons[i] : "a:" + r.axis + (r.axisSign < 0f ? "-" : "+");
            if (chords[i].Length < best[key]) continue;   // a chorded row for the same trigger wins over the plain one
            if (d.debugLog) Debug.Log("[PadExtras] " + r.id + " -> " + r.action);
            PadExtras.Do(r.action, df);
        }
        for (int i = 0; i < n; i++)   // remember the axis state AFTER the decision (edge = was off, now on)
        {
            var r = d.rows[i]; if (r == null || buttons[i] != KeyCode.None || string.IsNullOrEmpty(r.axis)) continue;
            prev["a:" + r.axis + (r.axisSign < 0f ? "-" : "+")] = Mathf.Abs(PadExtras.Axis(r.axis)) >= d.axisThreshold && PadExtras.Axis(r.axis) * (r.axisSign < 0f ? -1f : 1f) > 0f;
        }
    }
}
