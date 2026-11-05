using System;
using System.Collections.Generic;
using UnityEngine;

// REBIND v100 (lane laneREBIND): the player's key map. An ACTION (strike, guard, dodge, lock ...) -> keyboard / mouse keys (rebindable) + gamepad buttons (fixed, listed in the Controls tab).
// Defaults: Assets/Data/controls.json "bindings" (StreamingAssets copy for the player). Overrides: settings.json "keys" (MenuSettings.S.keys, only the actions the player changed).
// The old hard-coded Input.GetKey calls ask this class instead: InputMap.Down("strike", KeyCode.Mouse0, KeyCode.F) = the mapped keys of "strike" when the map is on, else EXACTLY the old keys given
// as the fallback. So a missing bindings block, "enabled": false, or a harness run (MenuCfg.Harness: batch mode, -balanceauto ... never read the saved keys) is the old behaviour bit for bit.
// Gamepad: only the buttons listed in a row's "pad" are read here (lock, riposte); the right-stick / RB / LB / Y / X handling stays in DirectionalInput + controls.json pad{} (shown as "padFrom").
public static class InputMap
{
    [Serializable] public class NameRow { public string k = "", n = ""; }
    [Serializable] public class ActionRow { public string id = "", label = "", hint = "", padFrom = "", padText = ""; public string[] keys = new string[0], pad = new string[0], overlap = new string[0]; }
    [Serializable] public class FixedRow { public string label = "", keys = "", pad = ""; }
    [Serializable]
    public class UiC
    {
        public string footer = "Up / Down  Select      Left / Right  Key slot      Enter  Change      Del  Unbind      Q / E  Tab      Esc / B  Back";
        public string captureTitle = "{0}", captureSlot = "Key {0}", capturePress = "Press a key or a mouse button", captureCancel = "Esc / B  Cancel";
        public string padFixed = "Gamepad buttons are fixed.", reserved = "{0} is reserved.", conflict = "{0} is already used by {1}.", conflictHint = "Enter  Swap keys      Esc  Cancel";
        public string unbound = "-", resetAll = "Restore default controls", resetAllHint = "Every action goes back to the keys in controls.json.";
        public string[] clearKeys = { "Delete", "JoystickButton3" };
        public string fixedHead = "Fixed keys";
    }
    [Serializable]
    public class Cfg
    {
        public bool enabled = true; public int slots = 2; public string moveAxisX = "Horizontal", moveAxisY = "Vertical";
        public string[] reserved = new string[0]; public NameRow[] keyNames = new NameRow[0]; public ActionRow[] actions = new ActionRow[0]; public FixedRow[] fixedRows = new FixedRow[0]; public UiC ui = new UiC();
    }
    [Serializable] class FileC { public Cfg bindings = new Cfg(); }
    /// <summary>One saved override (settings.json "keys"): the key names of every slot ("None" = empty slot) of an action that differs from its default.</summary>
    [Serializable] public class Bind { public string id = ""; public string[] k = new string[0]; }

    public sealed class Act
    {
        public string id = "", label = "", hint = "", padFrom = "", padText = ""; public string[] overlap = new string[0];
        public KeyCode[] def, cur, pad, all, primary;   // def / cur: one entry per slot (None = empty); all = cur (no None) + pad; primary = first key of cur
        public bool Changed { get { for (int i = 0; i < cur.Length; i++) if (cur[i] != def[i]) return true; return false; } }
    }

    static Cfg cfg = new Cfg(); static bool inited, enabled, moveRemapped, loadedFile;
    static readonly Dictionary<string, Act> map = new Dictionary<string, Act>(); static readonly List<Act> list = new List<Act>();
    static Act mL, mR, mF, mB; static KeyCode[] legacyMove = new KeyCode[0];
    static KeyCode[] allCodes; static readonly KeyCode[] noKeys = new KeyCode[0];
    /// <summary>Bumped on every change of the map (the menu redraws from live data, so this is only for observers).</summary>
    public static int Version;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inited = false; enabled = false; loadedFile = false; map.Clear(); list.Clear(); cfg = new Cfg(); Version = 0; }

    // ---------------- load ----------------
    /// <summary>The map is in use (data present and enabled, not a harness run). Everything else falls back to the old keys.</summary>
    public static bool On { get { if (!inited) Ensure(); return enabled; } }
    public static IList<Act> Actions { get { if (!inited) Ensure(); return list; } }
    public static Cfg Data { get { if (!inited) Ensure(); return cfg; } }
    public static UiC Ui { get { if (!inited) Ensure(); return cfg.ui; } }
    public static int Slots { get { if (!inited) Ensure(); return Mathf.Max(1, cfg.slots); } }
    public static Act Find(string id) { if (!inited) Ensure(); Act a; return enabled && id != null && map.TryGetValue(id, out a) ? a : null; }

    /// <summary>MenuSettings calls this after settings.json was read: the overrides are applied on the next query.</summary>
    public static void SettingsLoaded() { inited = false; }

    static void Ensure()
    {
        inited = true; enabled = false; map.Clear(); list.Clear(); moveRemapped = false; mL = mR = mF = mB = null;
        if (!loadedFile)
        {
            loadedFile = true; cfg = new Cfg();
            string txt = HudCfg.ReadData("controls.json");
            if (txt != null)
            {
                try { var f = JsonUtility.FromJson<FileC>(txt); if (f != null && f.bindings != null) cfg = f.bindings; }
                catch (Exception e) { Debug.LogWarning("[InputMap] controls.json bindings unreadable, old keys used: " + e.Message); cfg = new Cfg(); }
            }
            if (cfg.reserved == null) cfg.reserved = new string[0]; if (cfg.keyNames == null) cfg.keyNames = new NameRow[0]; if (cfg.actions == null) cfg.actions = new ActionRow[0]; if (cfg.fixedRows == null) cfg.fixedRows = new FixedRow[0]; if (cfg.ui == null) cfg.ui = new UiC();
            if (cfg.ui.clearKeys == null) cfg.ui.clearKeys = new string[0];
        }
        if (!cfg.enabled || cfg.actions.Length == 0 || MenuCfg.Harness) return;   // harness runs: the old keys, never the saved ones
        int slots = Mathf.Max(1, cfg.slots);
        foreach (var r in cfg.actions)
        {
            if (r == null || string.IsNullOrEmpty(r.id) || map.ContainsKey(r.id)) continue;
            var a = new Act { id = r.id, label = r.label, hint = r.hint, padFrom = r.padFrom, padText = r.padText, overlap = r.overlap ?? new string[0] };
            var d = ParseKeys(r.keys); a.def = new KeyCode[Mathf.Max(slots, d.Count)]; for (int i = 0; i < d.Count; i++) a.def[i] = d[i];
            a.cur = (KeyCode[])a.def.Clone(); a.pad = ParseKeys(r.pad).ToArray();
            map[a.id] = a; list.Add(a);
        }
        var saved = MenuSettings.S != null ? MenuSettings.S.keys : null;
        if (saved != null)
            foreach (var b in saved)
            {
                Act a; if (b == null || b.k == null || !map.TryGetValue(b.id, out a)) continue;
                for (int i = 0; i < a.cur.Length; i++) { KeyCode k = KeyCode.None; if (i < b.k.Length) Enum.TryParse(b.k[i], true, out k); a.cur[i] = Usable(k) ? k : KeyCode.None; }
            }
        foreach (var a in list) Rebuild(a);
        map.TryGetValue("moveLeft", out mL); map.TryGetValue("moveRight", out mR); map.TryGetValue("moveForward", out mF); map.TryGetValue("moveBack", out mB);
        if (mL != null && mR != null && mF != null && mB != null)
        {
            moveRemapped = mL.Changed || mR.Changed || mF.Changed || mB.Changed;
            var lm = new List<KeyCode>(); foreach (var a in new[] { mL, mR, mF, mB }) foreach (var k in a.def) if (k != KeyCode.None && !lm.Contains(k)) lm.Add(k);
            legacyMove = lm.ToArray();
        }
        enabled = true;
    }

    static List<KeyCode> ParseKeys(string[] names)
    {
        var l = new List<KeyCode>(); if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (!string.IsNullOrEmpty(names[i]) && Enum.TryParse(names[i], true, out k)) l.Add(k); }
        return l;
    }
    static void Rebuild(Act a)
    {
        var all = new List<KeyCode>(); KeyCode first = KeyCode.None;
        for (int i = 0; i < a.cur.Length; i++) if (a.cur[i] != KeyCode.None) { all.Add(a.cur[i]); if (first == KeyCode.None) first = a.cur[i]; }
        all.AddRange(a.pad); a.all = all.ToArray(); a.primary = first == KeyCode.None ? noKeys : new[] { first };
    }

    // ---------------- queries (the replacements of Input.GetKey / GetKeyDown) ----------------
    static bool Any(KeyCode[] ks, bool down) { for (int i = 0; i < ks.Length; i++) if (down ? Input.GetKeyDown(ks[i]) : Input.GetKey(ks[i])) return true; return false; }

    public static bool Down(string id, KeyCode a) { var x = Find(id); return x != null ? Any(x.all, true) : Input.GetKeyDown(a); }
    public static bool Down(string id, KeyCode a, KeyCode b) { var x = Find(id); return x != null ? Any(x.all, true) : (Input.GetKeyDown(a) || Input.GetKeyDown(b)); }
    public static bool Held(string id, KeyCode a) { var x = Find(id); return x != null ? Any(x.all, false) : Input.GetKey(a); }
    public static bool Held(string id, KeyCode a, KeyCode b) { var x = Find(id); return x != null ? Any(x.all, false) : (Input.GetKey(a) || Input.GetKey(b)); }
    /// <summary>The mapped keys of an action, or the old list when the map is off / has no such action. Allocation free (arrays are cached per change).</summary>
    public static KeyCode[] Keys(string id, KeyCode[] legacy) { var x = Find(id); return x != null ? x.all : legacy; }
    /// <summary>Only the first mapped keyboard / mouse key (the double-tap side-step follows the first move key, not the arrows).</summary>
    public static KeyCode[] Primary(string id, KeyCode[] legacy) { var x = Find(id); return x != null ? x.primary : legacy; }
    public static bool AnyDown(string id, KeyCode[] legacy) { return Any(Keys(id, legacy), true); }
    public static bool AnyHeld(string id, KeyCode[] legacy) { return Any(Keys(id, legacy), false); }

    /// <summary>Movement vector (x right, y forward). Default keys: the Input Manager axes exactly as before (keys + left stick). Once a move key is rebound: the mapped keys, plus the stick (the axis is only read while no default keyboard key is down, because the axis also carries those keys).</summary>
    public static Vector2 Move()
    {
        if (!inited) Ensure();
        string ax = string.IsNullOrEmpty(cfg.moveAxisX) ? "Horizontal" : cfg.moveAxisX, ay = string.IsNullOrEmpty(cfg.moveAxisY) ? "Vertical" : cfg.moveAxisY;
        if (!enabled || !moveRemapped || mL == null) return new Vector2(Input.GetAxisRaw(ax), Input.GetAxisRaw(ay));
        float x = (Any(mR.all, false) ? 1f : 0f) - (Any(mL.all, false) ? 1f : 0f), y = (Any(mF.all, false) ? 1f : 0f) - (Any(mB.all, false) ? 1f : 0f);
        if (!Any(legacyMove, false)) { x += Input.GetAxisRaw(ax); y += Input.GetAxisRaw(ay); }
        return new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
    }

    // ---------------- names ----------------
    public static string Name(KeyCode k)
    {
        if (k == KeyCode.None) return cfg.ui.unbound;
        if (!inited) Ensure();
        string s = k.ToString(); foreach (var n in cfg.keyNames) if (n != null && n.k == s) return n.n; return s;
    }
    public static bool IsPad(KeyCode k) { return k.ToString().StartsWith("Joystick", StringComparison.Ordinal); }
    public static bool IsReserved(KeyCode k)
    {
        if (!inited) Ensure(); string s = k.ToString(); foreach (var r in cfg.reserved) if (string.Equals(r, s, StringComparison.OrdinalIgnoreCase)) return true; return false;
    }
    static bool Usable(KeyCode k) { return k != KeyCode.None && !IsPad(k) && !IsReserved(k); }
    /// <summary>Every KeyCode the capture loop polls.</summary>
    public static KeyCode[] AllCodes { get { if (allCodes == null) allCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode)); return allCodes; } }

    // ---------------- editing (the Controls tab) ----------------
    static bool Overlaps(Act a, Act b) { return Array.IndexOf(a.overlap, b.id) >= 0 || Array.IndexOf(b.overlap, a.id) >= 0; }
    public static bool Has(Act a, KeyCode k) { for (int i = 0; i < a.cur.Length; i++) if (a.cur[i] == k) return true; return false; }

    /// <summary>Other actions that already use this key (actions that are allowed to share it, "overlap" in the data, are not listed).</summary>
    public static List<Act> Conflicts(Act a, KeyCode k)
    {
        var l = new List<Act>(); if (!inited) Ensure();
        foreach (var o in list) if (o != a && Has(o, k) && !Overlaps(a, o)) l.Add(o);
        return l;
    }
    /// <summary>Put key k into slot of a. A key already in another slot of a swaps places; a conflicting action loses the key and gets the one that was in this slot (or nothing).</summary>
    public static void Assign(Act a, int slot, KeyCode k)
    {
        if (slot < 0 || slot >= a.cur.Length || !Usable(k)) return;
        KeyCode old = a.cur[slot];
        for (int i = 0; i < a.cur.Length; i++) if (i != slot && a.cur[i] == k) a.cur[i] = old;   // same action, other slot: swap
        foreach (var o in Conflicts(a, k))
            for (int i = 0; i < o.cur.Length; i++) if (o.cur[i] == k) { o.cur[i] = old != KeyCode.None && !Has(o, old) ? old : KeyCode.None; Rebuild(o); }
        a.cur[slot] = k; Rebuild(a); Commit();
    }
    public static void Clear(Act a, int slot) { if (a == null || slot < 0 || slot >= a.cur.Length || a.cur[slot] == KeyCode.None) return; a.cur[slot] = KeyCode.None; Rebuild(a); Commit(); }
    public static void ResetAll() { if (!inited) Ensure(); foreach (var a in list) { a.cur = (KeyCode[])a.def.Clone(); Rebuild(a); } Commit(); }
    /// <summary>Names of the actions that have no keyboard / mouse key at all (a swap or an unbind can leave one).</summary>
    public static string Unbound() { var s = new List<string>(); foreach (var a in list) { bool any = false; foreach (var k in a.cur) if (k != KeyCode.None) any = true; if (!any) s.Add(a.label); } return string.Join(", ", s.ToArray()); }

    static void Commit()
    {
        Version++; moveRemapped = mL != null && mR != null && mF != null && mB != null && (mL.Changed || mR.Changed || mF.Changed || mB.Changed);
        if (MenuSettings.S == null) return;
        var rows = new List<Bind>();
        foreach (var a in list)
        {
            if (!a.Changed) continue; var b = new Bind { id = a.id, k = new string[a.cur.Length] };
            for (int i = 0; i < a.cur.Length; i++) b.k[i] = a.cur[i].ToString();
            rows.Add(b);
        }
        MenuSettings.S.keys = rows.ToArray(); MenuSettings.MarkDirty();
    }
}
