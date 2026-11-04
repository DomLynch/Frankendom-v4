using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

// Steer UI (lane S3): dark gothic IMGUI bottom bar for the live-steered duel.
//   left: 4 stance buttons (1-4), right: 7 tactic buttons (Q W E R T Y G) with radial cooldown pies,
//   top corners: HP bars (Health of 'Dracula' / 'Pitborn'), above the bar: a 3-line fading feed (STSteer.OnFeed).
// Icons: PNGs from StreamingAssets\steer_icons (or Assets\Data\steer_icons) when present, else procedural glyphs (STIcons).
// Everything tunable is in the "ui" block of steer.json (code defaults below). Attaches only with -steer / -steerauto / -steershot
// (or "steer": true in runtime_flags.json); drawing happens only while STSteer.Active (or the screenshot preview is on).
public class STUI : MonoBehaviour
{
    // ---------------- data ----------------
    [Serializable] public class BtnDef { public string id, label, key, icon; }
    [Serializable]
    public class UICfg
    {
        public float refHeight = 1080f, minLayoutWidth = 1400f;
        public string colPanel = "#0C0C0EF2", colPanel2 = "#151517FF", colBorder = "#3A1A1CFF", colCrimson = "#9A2024FF", colEmber = "#E8742CFF",
            colEmberFill = "#7A3416F0", colText = "#F0E8D8FF", colDim = "#9A9384FF", colDisabled = "#7A746AFF", colHover = "#1E1416FF",
            colHpDracula = "#B01820FF", colHpPitborn = "#D2701CFF", colGhost = "#F0E0C8B0", colKey = "#FFD7A0FF", colBad = "#C0282CFF";
        public float barMaxW = 1340f, barH = 136f, barBottom = 14f, pad = 20f, groupGap = 56f, btnGap = 8f;
        public float stanceW = 112f, tactW = 100f, btnH = 102f, discD = 64f, glyph = 50f;
        public float hpW = 460f, hpH = 76f, hpMinPxW = 380f, hpMinPxH = 82f, hpMargin = 22f, hpTop = 8f;
        public int feedLines = 3;
        public float feedHold = 4.5f, feedFade = 1.5f, flashTime = 0.35f;
        public int pxTitle = 14, pxLabel = 15, pxKey = 15, pxFeed = 22, pxHp = 22, pxBanner = 64;
        public bool freeCursor = true, hideGameHud = true;
        public string iconDir = "steer_icons", tStance = "STANCE", tTactics = "TACTICS", nameDracula = "DRACULA", namePitborn = "PITBORN",
            tWin = "VICTORY", tLose = "YOU DIED", tNext = "NEXT OPPONENT  [Enter]";
        public BtnDef[] stances, tactics;
    }
    [Serializable] class Root { public UICfg ui; }

    static UICfg DefaultCfg()
    {
        var c = new UICfg();
        c.stances = new[] {
            new BtnDef { id = "Aggressive", label = "AGGRESSIVE", key = "1" }, new BtnDef { id = "Defensive", label = "DEFENSIVE", key = "2" },
            new BtnDef { id = "Neutral", label = "NEUTRAL", key = "3" }, new BtnDef { id = "Trickster", label = "TRICKSTER", key = "4" } };
        c.tactics = new[] {
            new BtnDef { id = "Press", label = "PRESS", key = "Q" }, new BtnDef { id = "BackOff", label = "BACK OFF", key = "W" },
            new BtnDef { id = "Circle", label = "CIRCLE", key = "E" }, new BtnDef { id = "GuardUp", label = "GUARD UP", key = "R" },
            new BtnDef { id = "Dodge", label = "DODGE", key = "T" }, new BtnDef { id = "Feint", label = "FEINT", key = "Y" },
            new BtnDef { id = "GoForKill", label = "GO FOR KILL", key = "G" } };
        return c;
    }
    public static UICfg LoadCfg()
    {
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "steer.json"), Path.Combine(Application.dataPath, "Data/steer.json") })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var r = JsonUtility.FromJson<Root>(File.ReadAllText(p));
                if (r != null && r.ui != null)
                {
                    var d = DefaultCfg();
                    if (r.ui.stances == null || r.ui.stances.Length != 4) r.ui.stances = d.stances;
                    if (r.ui.tactics == null || r.ui.tactics.Length != 7) r.ui.tactics = d.tactics;
                    return r.ui;
                }
            }
            catch (Exception e) { Debug.LogWarning("STUI: cannot read " + p + ": " + e.Message); }
        }
        return DefaultCfg();
    }

    // ---------------- singleton / boot ----------------
    public static STUI Instance;
    public UICfg cfg;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (FlagOn()) Ensure(); }
    public static STUI Ensure()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("STUI"); DontDestroyOnLoad(go);
        return go.AddComponent<STUI>();
    }
    static bool FlagOn()
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (a == "-steer" || a == "-steerauto" || a.StartsWith("-steershot=")) return true;
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, "runtime_flags.json"), Path.Combine(Application.dataPath, "Data/runtime_flags.json") })
        {
            try { if (File.Exists(p) && Regex.IsMatch(File.ReadAllText(p), "\"steer\"\\s*:\\s*true")) return true; } catch { }
        }
        return false;
    }

    // ---------------- state ----------------
    static readonly STTactic[] AllTactics = (STTactic[])Enum.GetValues(typeof(STTactic));
    class FeedLine { public string text; public float t; }
    readonly List<FeedLine> feed = new List<FeedLine>();
    readonly float[] tFlash = new float[7]; readonly bool[] tFlashOk = new bool[7];
    float stanceFlash = -10f;
    readonly Dictionary<string, Color> colCache = new Dictionary<string, Color>();
    readonly Dictionary<int, GUIStyle> styles = new Dictionary<int, GUIStyle>();
    readonly HashSet<KeyCode> held = new HashSet<KeyCode>();
    readonly Dictionary<KeyCode, Action> keymap = new Dictionary<KeyCode, Action>();
    readonly Dictionary<string, Texture2D> ext = new Dictionary<string, Texture2D>();
    readonly Dictionary<string, bool> extMask = new Dictionary<string, bool>();
    readonly HashSet<string> extMissing = new HashSet<string>();
    Health hpD, hpP; float resolveT;
    float ghostD = 1f, ghostP = 1f;
    GameHUD hud; GameHUD hudSaved; Health hsPlayer, hsEnemy; DuelFighter hsPf, hsEf; float hudT;
    bool cursorTaken; CursorLockMode prevLock; bool prevVisible;

    // layout (rebuilt each OnGUI)
    float S; Rect bar; readonly Rect[] stRect = new Rect[4]; readonly Rect[] taRect = new Rect[7]; Rect bannerBtn;

    public static bool Visible { get { return STUIPreview.On || STSteer.Active; } }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this; cfg = LoadCfg(); if (cfg.minLayoutWidth < 800f) cfg.minLayoutWidth = 1400f; if (cfg.refHeight < 100f) cfg.refHeight = 1080f;
        for (int i = 0; i < 4; i++) { int k = i; Bind(cfg.stances[i].key, () => PickStance(k)); }
        for (int i = 0; i < 7; i++) { int k = i; Bind(cfg.tactics[i].key, () => PickTactic(k)); }
    }
    void OnEnable() { STSteer.OnStance += OnStance; STSteer.OnTactic += OnTactic; STSteer.OnFeed += PushFeed; }
    void OnDisable() { STSteer.OnStance -= OnStance; STSteer.OnTactic -= OnTactic; STSteer.OnFeed -= PushFeed; ReleaseCursor(); }
    void OnDestroy() { RestoreHud(); ReleaseCursor(); if (Instance == this) Instance = null; }
    void OnApplicationFocus(bool f) { if (!f) held.Clear(); }

    static string KeyName(string n) { return !string.IsNullOrEmpty(n) && n.Length == 1 && char.IsDigit(n[0]) ? "Alpha" + n : n; }
    void Bind(string name, Action a) { name = KeyName(name); KeyCode k; if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, true, out k)) keymap[k] = a; }
    Color C(string hex)
    {
        Color c; if (colCache.TryGetValue(hex, out c)) return c;
        if (!ColorUtility.TryParseHtmlString(hex, out c)) c = Color.magenta; colCache[hex] = c; return c;
    }
    static Color A(Color c, float a) { return new Color(c.r, c.g, c.b, c.a * a); }

    // ---------------- data access (real or preview) ----------------
    STStance CurStance { get { return STUIPreview.On ? STUIPreview.Stance : STSteer.Stance; } }
    float Cd(int i) { return STUIPreview.On ? STUIPreview.Cd[i] : STSteer.Cooldown01(AllTactics[i]); }
    bool En(int i) { return STUIPreview.On ? STUIPreview.En[i] : STSteer.Enabled(AllTactics[i]); }

    void OnStance(STStance s) { stanceFlash = Time.unscaledTime; }
    void OnTactic(STTactic t, bool ok) { int i = (int)t; if (i >= 0 && i < 7) { tFlash[i] = Time.unscaledTime; tFlashOk[i] = ok; } }
    public void ClearFeed() { feed.Clear(); }
    public void PushFeed(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        feed.Add(new FeedLine { text = line, t = Time.unscaledTime });
        while (feed.Count > Mathf.Max(1, cfg.feedLines)) feed.RemoveAt(0);
    }
    void PickStance(int i) { if (STUIPreview.On) { STUIPreview.Stance = (STStance)i; stanceFlash = Time.unscaledTime; } else STSteer.SetStance((STStance)i); }
    void PickTactic(int i)
    {
        if (STUIPreview.On) { tFlash[i] = Time.unscaledTime; tFlashOk[i] = STUIPreview.En[i] && STUIPreview.Cd[i] <= 0f; return; }
        STSteer.UseTactic(AllTactics[i]);   // OnTactic(t, accepted) drives the flash
    }

    // ---------------- cursor / GameHUD ----------------
    void TakeCursor()
    {
        if (!cfg.freeCursor || Application.isBatchMode) return;
        if (!cursorTaken) { prevLock = Cursor.lockState; prevVisible = Cursor.visible; cursorTaken = true; }
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }
    void ReleaseCursor() { if (!cursorTaken) return; cursorTaken = false; Cursor.lockState = prevLock; Cursor.visible = prevVisible; }

    // GameHUD is not ours: while steer is up we detach its fighter references so it stops drawing its own bars, the special rings and
    // the death message (our bar draws HP; the translucent panel boxes and the key-hint line are covered by our opaque panels).
    // Proper hook for S1: see the handoff (one line in GameHUD.OnGUI).
    void HideHud()
    {
        if (!cfg.hideGameHud) return;
        if (hud == null) hud = FindFirstObjectByType<GameHUD>();
        if (hud == null) return;
        if (hud.player != null || hud.enemy != null || hud.pf != null || hud.ef != null)
        { hsPlayer = hud.player; hsEnemy = hud.enemy; hsPf = hud.pf; hsEf = hud.ef; hudSaved = hud; hud.player = null; hud.enemy = null; hud.pf = null; hud.ef = null; }
    }
    void RestoreHud()
    {
        if (hudSaved != null) { hudSaved.player = hsPlayer; hudSaved.enemy = hsEnemy; hudSaved.pf = hsPf; hudSaved.ef = hsEf; }
        hudSaved = null;
    }

    void Update()
    {
        if (!Visible) { if (cursorTaken) ReleaseCursor(); return; }
        TakeCursor();
        float now = Time.unscaledTime;
        if (now >= hudT) { hudT = now + 0.5f; HideHud(); }
        if (now >= resolveT && !STUIPreview.On)
        {
            resolveT = now + 0.5f;
            if (hpD == null) { var g = GameObject.Find("Dracula"); if (g) hpD = g.GetComponentInChildren<Health>(); }
            if (hpP == null) { var g = GameObject.Find("Pitborn"); if (g) hpP = g.GetComponentInChildren<Health>(); }
        }
        float dt = Time.unscaledDeltaTime;
        float nd = HpFrac(0), np = HpFrac(1);
        ghostD = nd >= ghostD ? nd : Mathf.Max(nd, ghostD - 0.35f * dt);
        ghostP = np >= ghostP ? np : Mathf.Max(np, ghostP - 0.35f * dt);
        while (feed.Count > 0 && now - feed[0].t > cfg.feedHold + cfg.feedFade) feed.RemoveAt(0);
    }
    float HpFrac(int who)
    {
        if (STUIPreview.On) return Mathf.Clamp01((who == 0 ? STUIPreview.Hp0 : STUIPreview.Hp1) / 100f);
        var h = who == 0 ? hpD : hpP; return h != null && h.max > 0f ? Mathf.Clamp01(h.current / h.max) : 1f;
    }
    string HpText(int who)
    {
        if (STUIPreview.On) { float v = who == 0 ? STUIPreview.Hp0 : STUIPreview.Hp1; return Mathf.CeilToInt(v) + " / 100"; }
        var h = who == 0 ? hpD : hpP; return h == null ? "" : Mathf.CeilToInt(h.current) + " / " + Mathf.CeilToInt(h.max);
    }

    // ---------------- icons ----------------
    static string FileFor(string id)
    {
        switch (id) { case "BackOff": return "back_off"; case "GuardUp": return "guard_up"; case "GoForKill": return "kill"; default: return id.ToLowerInvariant(); }
    }
    // returns an external PNG if present (and whether it is a white mask to be tinted), else null
    Texture2D External(BtnDef d, out bool mask)
    {
        mask = false; string id = d.id;
        if (extMissing.Contains(id)) return null;
        Texture2D t;
        if (ext.TryGetValue(id, out t) && t != null) { mask = extMask[id]; return t; }
        string name = string.IsNullOrEmpty(d.icon) ? FileFor(id) + ".png" : d.icon;
        foreach (var dir in new[] { Path.Combine(Application.streamingAssetsPath, cfg.iconDir), Path.Combine(Application.dataPath, "Data/" + cfg.iconDir) })
        {
            try
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) continue;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                if (!tex.LoadImage(File.ReadAllBytes(p))) continue;
                bool m = true; var px = tex.GetPixels32(); int seen = 0;
                for (int i = 0; i < px.Length && m; i += 7) if (px[i].a > 200) { seen++; if (px[i].r < 215 || px[i].g < 215 || px[i].b < 215) m = false; }
                if (seen == 0) m = false;
                ext[id] = tex; extMask[id] = m; mask = m; return tex;
            }
            catch (Exception e) { Debug.LogWarning("STUI: icon " + name + ": " + e.Message); }
        }
        extMissing.Add(id); return null;
    }

    // ---------------- drawing helpers ----------------
    void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
    void Frame(Rect r, float t, Color c)
    {
        t = Mathf.Max(1f, Mathf.Round(t));
        Fill(new Rect(r.x, r.y, r.width, t), c); Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
        Fill(new Rect(r.x, r.y + t, t, r.height - 2 * t), c); Fill(new Rect(r.xMax - t, r.y + t, t, r.height - 2 * t), c);
    }
    void Tex(Rect r, Texture2D t, Color c) { if (t == null) return; GUI.color = c; GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true); GUI.color = Color.white; }
    GUIStyle St(int px, bool bold, TextAnchor a)
    {
        int key = px * 100 + (bold ? 10 : 0) + (int)a; GUIStyle s;
        if (!styles.TryGetValue(key, out s) || s == null)
        { s = new GUIStyle(GUI.skin.label) { fontSize = px, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal, alignment = a, wordWrap = false, clipping = TextClipping.Overflow, padding = new RectOffset(0, 0, 0, 0) }; styles[key] = s; }
        return s;
    }
    void Txt(Rect r, string s, int px, bool bold, TextAnchor a, Color c, bool shadow = true)
    {
        var st = St(px, bold, a);
        if (shadow) { float o = Mathf.Max(1f, px / 14f); st.normal.textColor = new Color(0, 0, 0, c.a * 0.9f); GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), s, st); }
        st.normal.textColor = c; GUI.Label(r, s, st);
    }
    int Fit(string s, int px, bool bold, float maxW)
    {
        while (px > 9) { var st = St(px, bold, TextAnchor.MiddleCenter); if (st.CalcSize(new GUIContent(s)).x <= maxW) break; px--; }
        return px;
    }
    int P(float refPx) { return Mathf.Max(9, Mathf.RoundToInt(refPx * S)); }

    // ---------------- layout ----------------
    void Layout()
    {
        S = Mathf.Max(0.3f, Mathf.Min(Screen.height / cfg.refHeight, Screen.width / cfg.minLayoutWidth));
        float vw = Screen.width / S, vh = Screen.height / S;
        float needW = 2 * cfg.pad + 4 * cfg.stanceW + 3 * cfg.btnGap + cfg.groupGap + 7 * cfg.tactW + 6 * cfg.btnGap;
        float bw = Mathf.Min(vw - 24f, Mathf.Max(cfg.barMaxW, needW));
        float bx = (vw - bw) * 0.5f, by = vh - cfg.barH - cfg.barBottom;
        bar = new Rect(bx * S, by * S, bw * S, cfg.barH * S);
        float y = by + 26f;
        float x = bx + cfg.pad;
        for (int i = 0; i < 4; i++) { stRect[i] = new Rect(x * S, y * S, cfg.stanceW * S, cfg.btnH * S); x += cfg.stanceW + cfg.btnGap; }
        float tw = 7 * cfg.tactW + 6 * cfg.btnGap; x = bx + bw - cfg.pad - tw;
        for (int i = 0; i < 7; i++) { taRect[i] = new Rect(x * S, y * S, cfg.tactW * S, cfg.btnH * S); x += cfg.tactW + cfg.btnGap; }
        bannerBtn = new Rect(Screen.width * 0.5f - 170f * S, Screen.height * 0.5f + 70f * S, 340f * S, 54f * S);
    }
    bool Over(Rect r) { return r.Contains(Event.current.mousePosition); }
    bool Dead(int who) { if (STUIPreview.On) return false; var h = who == 0 ? hpD : hpP; return !RTFinisher.Busy && h != null && h.Dead; }   // FN1: the end banner waits for the finisher scene

    // ---------------- OnGUI ----------------
    void OnGUI()
    {
        if (!Visible || cfg == null) return;
        GUI.depth = -1000;
        var e = Event.current;
        Layout();
        switch (e.type)
        {
            case EventType.KeyDown:
                if (e.keyCode != KeyCode.None && !held.Contains(e.keyCode))
                { held.Add(e.keyCode); Action a; if (keymap.TryGetValue(e.keyCode, out a)) { a(); e.Use(); } }
                break;
            case EventType.KeyUp: held.Remove(e.keyCode); break;
            case EventType.MouseDown:
                if (e.button != 0) break;
                for (int i = 0; i < 4; i++) if (stRect[i].Contains(e.mousePosition)) { PickStance(i); e.Use(); return; }
                for (int i = 0; i < 7; i++) if (taRect[i].Contains(e.mousePosition)) { PickTactic(i); e.Use(); return; }
                if ((Dead(0) || Dead(1)) && bannerBtn.Contains(e.mousePosition)) { e.Use(); SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex); }
                break;
            case EventType.Repaint: Draw(); break;
        }
    }

    void Draw()
    {
        Color ember = C(cfg.colEmber), crimson = C(cfg.colCrimson), text = C(cfg.colText), dim = C(cfg.colDim), border = C(cfg.colBorder);
        float now = Time.unscaledTime;
        // --- HP panels ---
        HpPanel(true, cfg.nameDracula, 0, ghostD, C(cfg.colHpDracula));
        HpPanel(false, cfg.namePitborn, 1, ghostP, C(cfg.colHpPitborn));

        // --- bar panel ---
        Fill(bar, C(cfg.colPanel));
        Fill(new Rect(bar.x, bar.y, bar.width, Mathf.Max(2f, 2f * S)), crimson);                                   // crimson top rule
        Fill(new Rect(bar.x, bar.y, 120f * S, Mathf.Max(2f, 2f * S)), ember);                                      // ember lead-in
        Fill(new Rect(bar.xMax - 120f * S, bar.y, 120f * S, Mathf.Max(2f, 2f * S)), ember);
        Frame(bar, 1f, border);
        Txt(new Rect(stRect[0].x, bar.y + 5f * S, 200f * S, 20f * S), cfg.tStance, P(cfg.pxTitle), true, TextAnchor.MiddleLeft, A(crimson, 1f) * 1.25f, false);
        Txt(new Rect(taRect[0].x, bar.y + 5f * S, 200f * S, 20f * S), cfg.tTactics, P(cfg.pxTitle), true, TextAnchor.MiddleLeft, A(crimson, 1f) * 1.25f, false);

        var cur = CurStance;
        for (int i = 0; i < 4; i++)
        {
            var d = cfg.stances[i]; bool act = (int)cur == i; bool hov = Over(stRect[i]);
            float flash = act ? Mathf.Clamp01(1f - (now - stanceFlash) / cfg.flashTime) : 0f;
            Button(stRect[i], d, act, hov, 0f, true, false, flash, true);
        }
        for (int i = 0; i < 7; i++)
        {
            var d = cfg.tactics[i]; bool en = En(i); float cd = Cd(i); bool hov = Over(taRect[i]);
            float fl = Mathf.Clamp01(1f - (now - tFlash[i]) / cfg.flashTime);
            Button(taRect[i], d, false, hov, cd, en, i == 6 && !en, fl, tFlashOk[i]);
        }

        // --- feed ---
        float fy = bar.y - 10f * S;
        for (int i = feed.Count - 1; i >= 0; i--)
        {
            float age = now - feed[i].t, a = age <= cfg.feedHold ? 1f : Mathf.Clamp01(1f - (age - cfg.feedHold) / cfg.feedFade);
            fy -= 30f * S;
            if (a <= 0f) continue;
            Txt(new Rect(bar.x, fy, bar.width, 30f * S), feed[i].text, P(cfg.pxFeed), true, TextAnchor.MiddleCenter, A(text, a));
        }

        // --- end banner ---
        if (Dead(0) || Dead(1))
        {
            Fill(new Rect(0, Screen.height * 0.5f - 90f * S, Screen.width, 180f * S), new Color(0, 0, 0, 0.55f));
            bool win = Dead(1) && !Dead(0);
            Txt(new Rect(0, Screen.height * 0.5f - 70f * S, Screen.width, 120f * S), win ? cfg.tWin : cfg.tLose, P(cfg.pxBanner), true, TextAnchor.MiddleCenter, win ? C(cfg.colKey) : C(cfg.colBad));
            Fill(bannerBtn, Over(bannerBtn) ? C(cfg.colEmberFill) : C(cfg.colPanel2)); Frame(bannerBtn, 2f * S, ember);
            Txt(bannerBtn, cfg.tNext, P(20f), true, TextAnchor.MiddleCenter, text);
        }
    }

    void Button(Rect r, BtnDef d, bool active, bool hover, float cd, bool enabled, bool locked, float flash, bool flashOk)
    {
        Color ember = C(cfg.colEmber), crimson = C(cfg.colCrimson), text = C(cfg.colText), dim = C(cfg.colDim), dis = C(cfg.colDisabled), key = C(cfg.colKey);
        float bt = Mathf.Max(1f, 1f * S);
        // body
        Color body = active ? C(cfg.colEmberFill) : hover && enabled ? C(cfg.colHover) : C(cfg.colPanel2);
        Fill(r, body);
        if (active) Fill(new Rect(r.x, r.yMax - 3f * S, r.width, 3f * S), ember);
        Color bc = active ? ember : !enabled ? new Color(0.2f, 0.17f, 0.16f, 1f) : cd > 0f ? new Color(0.35f, 0.15f, 0.16f, 1f) : crimson;
        if (hover && enabled && !active) bc = ember * 0.85f;
        Frame(r, active ? Mathf.Max(2f, 2f * S) : bt, bc);
        if (flash > 0f) Frame(new Rect(r.x - 2f * S, r.y - 2f * S, r.width + 4f * S, r.height + 4f * S), Mathf.Max(2f, 2f * S), A(flashOk ? C(cfg.colKey) : C(cfg.colBad), flash));

        // icon disc
        float dd = cfg.discD * S; var disc = new Rect(r.center.x - dd * 0.5f, r.y + 8f * S, dd, dd);
        Tex(disc, STIcons.Disc, active ? new Color(0.28f, 0.1f, 0.05f, 1f) : new Color(0.06f, 0.055f, 0.06f, 1f));
        Tex(disc, STIcons.ThinRing, active ? key : !enabled ? new Color(0.25f, 0.22f, 0.2f, 1f) : cd > 0f ? new Color(0.45f, 0.2f, 0.2f, 1f) : A(crimson, 1f) * 1.3f);
        float gd = cfg.glyph * S; var gr = new Rect(r.center.x - gd * 0.5f, disc.center.y - gd * 0.5f, gd, gd);
        bool mask; var xt = External(d, out mask);
        float bright = !enabled ? 0.28f : cd > 0f ? 0.6f : active ? 1f : 0.88f;
        if (xt != null)
        {
            Color c = mask ? (active ? key : text) * bright : new Color(bright, bright, bright, 1f);
            c.a = 1f; Tex(gr, xt, c);
        }
        else
        {
            Color c = (!enabled ? dis : active ? key : text) * (cd > 0f && enabled ? 0.65f : 1f); c.a = 1f;
            Tex(gr, STIcons.Get(d.id), c);
        }
        // radial cooldown: dark wedge over the not-yet-ready fraction
        if (cd > 0f && enabled) { Tex(disc, STIcons.Pie(cd), new Color(0f, 0f, 0f, 0.72f)); }
        if (locked)
        {
            float ld = dd * 0.42f; var lr = new Rect(disc.xMax - ld * 0.8f, disc.yMax - ld * 0.95f, ld, ld);
            Tex(new Rect(lr.x - 2f * S, lr.y - 2f * S, lr.width + 4f * S, lr.height + 4f * S), STIcons.Disc, new Color(0.04f, 0.03f, 0.03f, 0.95f));
            Tex(lr, STIcons.Get("Lock"), new Color(0.78f, 0.7f, 0.58f, 1f));
        }
        // label
        string lab = d.label; int lp = Fit(lab, P(cfg.pxLabel), true, r.width - 6f * S);
        Color lc = !enabled ? dis : cd > 0f ? dim : active ? key : text;
        Txt(new Rect(r.x, r.y + 76f * S, r.width, 22f * S), lab, lp, true, TextAnchor.MiddleCenter, lc);
        // key chip
        var kr = new Rect(r.x + 5f * S, r.y + 5f * S, 24f * S, 22f * S);
        // key hint stays readable even when the tactic is unavailable (it was drawn in the near-invisible disabled colour: Y / G looked empty)
        Color kc = active ? key : !enabled ? Color.Lerp(dim, text, 0.35f) : text; kc.a = 1f;
        Fill(kr, new Color(0f, 0f, 0f, 0.85f)); Frame(kr, 1f, !enabled ? dim : active ? key : ember * 0.9f);
        Txt(kr, string.IsNullOrEmpty(d.key) ? "?" : d.key.ToUpperInvariant(), P(cfg.pxKey), true, TextAnchor.MiddleCenter, kc, false);
    }

    void HpPanel(bool left, string name, int who, float ghost, Color fill)
    {
        float w = Mathf.Max(cfg.hpW * S, cfg.hpMinPxW), h = Mathf.Max(cfg.hpH * S, cfg.hpMinPxH);
        float x = left ? cfg.hpMargin : Screen.width - cfg.hpMargin - w, y = cfg.hpTop;
        var pr = new Rect(x, y, w, h);
        Fill(pr, C(cfg.colPanel)); Frame(pr, 1f, C(cfg.colBorder));
        Fill(new Rect(left ? pr.x : pr.xMax - 90f * S, pr.y, 90f * S, Mathf.Max(2f, 2f * S)), C(cfg.colEmber));
        Fill(new Rect(pr.x, pr.y, pr.width, Mathf.Max(1f, 1f * S)), C(cfg.colCrimson));
        float pad = 12f * S;
        Txt(new Rect(pr.x + pad, pr.y + 5f * S, pr.width * 0.5f, 28f * S), name, P(cfg.pxHp), true, left ? TextAnchor.MiddleLeft : TextAnchor.MiddleLeft, C(cfg.colText));
        Txt(new Rect(pr.xMax - pad - pr.width * 0.4f, pr.y + 5f * S, pr.width * 0.4f, 28f * S), HpText(who), P(cfg.pxHp * 0.85f), false, TextAnchor.MiddleRight, C(cfg.colDim));
        var br = new Rect(pr.x + pad, pr.y + 36f * S, pr.width - 2 * pad, 18f * S);
        Fill(br, new Color(0.02f, 0.02f, 0.02f, 1f)); Frame(br, 1f, C(cfg.colBorder));
        float f = HpFrac(who);
        var inner = new Rect(br.x + 2f, br.y + 2f, br.width - 4f, br.height - 4f);
        if (ghost > f) Fill(new Rect(inner.x, inner.y, inner.width * ghost, inner.height), C(cfg.colGhost));
        Fill(new Rect(inner.x, inner.y, inner.width * f, inner.height), fill);
        Fill(new Rect(inner.x, inner.y, inner.width * f, Mathf.Max(1f, inner.height * 0.3f)), new Color(1f, 1f, 1f, 0.10f));
        if (left)
            Txt(new Rect(pr.x + pad, pr.y + 56f * S, pr.width - 2 * pad, 20f * S), cfg.tStance + "  " + CurStance.ToString().ToUpperInvariant(), P(13f), true, TextAnchor.MiddleLeft, C(cfg.colEmber), false);
    }
}
