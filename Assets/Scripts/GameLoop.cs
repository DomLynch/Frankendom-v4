using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// V66HUD: the game loop around one fight. Fight counter, opponent ladder (opponents.json), fade in / out between fights, the VICTORY / YOU DIED screens (title, subtitle, stats line,
// Retry / Next opponent, keyboard + gamepad) and the state reset on every restart. IMGUI only, no art; every number is in hud.json / opponents.json.
//
// A restart is SceneManager.LoadScene(active scene) exactly like the old GameHUD Retry / Backspace and BalanceAuto, so the scene's own objects are rebuilt; what survives a load is only static:
// this class' counters, CombatSlowMo / PresentationClock / FinisherTime (reset below before the load and again after it) and the DontDestroyOnLoad singletons. Retry keeps the fight number
// (Attempt + 1); Next opponent after a victory adds one to FightNumber and OpponentIndex. Nothing here is created in the scene: the driver object is made at runtime.
public static class GameLoop
{
    public enum Result { None, Victory, Defeat }
    [Serializable] public class Row { public string id = ""; public string name = "PITBORN"; public string title = ""; public float hpMul = 1f, damageMul = 1f; public int difficulty = 0; }
    [Serializable] public class Table { public bool enabled = true; public int startIndex = 0, applyAfterFrames = 10; public float endlessHpStep = 0.08f, endlessDamageStep = 0.04f; public Row[] rows = new Row[0]; }
    [Serializable] class DiffFile { public DifficultyRow[] rows = new DifficultyRow[0]; }

    // ---- run state (statics survive scene loads) ----
    public static int FightNumber = 1;      // the opponent's ordinal in this run (shown as FIGHT n)
    public static int OpponentIndex = 0;    // index into opponents.json rows (clamped; extra fights add the endless steps)
    public static int Attempt = 1;          // tries at this fight number
    public static int FightsStarted = 0;    // scene loads that began a fight (every Retry / Next counts)
    public static int SceneLoads = 0;
    // ---- this fight ----
    public static Result Outcome = Result.None;
    public static float ScreenAlpha = 0f;   // 0..1, the end screen
    public static float FadeAlpha = 1f;     // 0..1, the black fade
    public static int Landed, Taken, Parries; public static string Finisher = ""; public static float Seconds;
    public static bool Ready;               // fighters found, ladder applied, fade-in done
    public static bool LadderApplied; public static float BaseEnemyHp, AppliedHpMul = 1f, AppliedDmgMul = 1f; public static int AppliedDifficulty;
    public static DuelFighter Player, Enemy;
    public static string LastError = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        FightNumber = 1; OpponentIndex = 0; Attempt = 1; FightsStarted = 0; SceneLoads = 0; Outcome = Result.None; ScreenAlpha = 0f; FadeAlpha = 1f; Landed = Taken = Parries = 0; Finisher = ""; Seconds = 0f;
        Ready = false; LadderApplied = false; BaseEnemyHp = 0f; AppliedHpMul = AppliedDmgMul = 1f; AppliedDifficulty = 0; Player = Enemy = null; table = null; harness = null;
    }

    // ---- harness switches ----
    static bool? harness;
    static bool Has(string a) { return Array.IndexOf(Environment.GetCommandLineArgs(), a) >= 0; }
    public static bool LoopTestOn { get { return Has("-looptest"); } }
    /// <summary>True when another proof harness drives the game: no fades, no ladder (their numbers stay comparable). -looptest turns the loop features on again.</summary>
    public static bool HarnessOn
    {
        get
        {
            if (harness.HasValue) return harness.Value;
            bool h = false;
            foreach (var a in Environment.GetCommandLineArgs())
                if (a == "-balanceauto" || a == "-autoplay" || a == "-finisherauto" || a == "-postureproof" || a == "-camproof" || a == "-flowproof" || a == "-gaps" || a == "-props" || a == "-record" || a.StartsWith("-sheet:", StringComparison.Ordinal)) h = true;
            if (LoopTestOn) h = false;
            harness = h; return h;
        }
    }
    public static bool FadeOn { get { return HudCfg.Get().fade.enabled && !HarnessOn; } }
    public static bool LadderOn { get { return Opponents().enabled && !HarnessOn; } }

    // ---- opponents ----
    static Table table;
    public static Table Opponents()
    {
        if (table != null) return table;
        table = new Table();
        var txt = HudCfg.ReadData("opponents.json");
        if (txt != null) { try { var t = JsonUtility.FromJson<Table>(txt); if (t != null) table = t; } catch (Exception e) { Debug.LogWarning("[GameLoop] opponents.json parse failed: " + e.Message); } }
        if (table.rows == null) table.rows = new Row[0];
        return table;
    }
    public static void ReloadData() { table = null; HudCfg.Reload(); }

    /// <summary>The row for an ordinal index (past the end: the last row, plus the endless steps in the returned multipliers).</summary>
    public static Row RowAt(int index, out float hpMul, out float dmgMul)
    {
        var t = Opponents(); hpMul = 1f; dmgMul = 1f;
        if (t.rows.Length == 0) return new Row { id = "default", name = Roster.EnemyName.ToUpperInvariant(), title = "" };
        int i = Mathf.Clamp(index, 0, t.rows.Length - 1); var r = t.rows[i];
        int extra = Mathf.Max(0, index - (t.rows.Length - 1));
        hpMul = r.hpMul * (1f + t.endlessHpStep * extra); dmgMul = r.damageMul * (1f + t.endlessDamageStep * extra);
        return r;
    }
    public static Row CurrentRow() { float a, b; return RowAt(OpponentIndex, out a, out b); }
    public static Row NextRow() { float a, b; return RowAt(OpponentIndex + 1, out a, out b); }
    static string Cap(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant(); }
    public static string EnemyName() { var r = CurrentRow(); return string.IsNullOrEmpty(r.name) ? Roster.EnemyName.ToUpperInvariant() : r.name; }

    static DifficultyRow DifficultyRowFor(int id)
    {
        var txt = HudCfg.ReadData("difficulty.json"); if (txt == null) return null;
        try { var f = JsonUtility.FromJson<DiffFile>(txt); if (f != null && f.rows != null) foreach (var r in f.rows) if (r != null && r.id == id) return r; } catch { }
        return null;
    }

    /// <summary>Scale the freshly loaded Pitborn by the current ladder row. Called by the driver a few frames after the load (after fight_balance.json).</summary>
    public static void ApplyLadder(DuelFighter enemy)
    {
        LadderApplied = true; AppliedHpMul = 1f; AppliedDmgMul = 1f; AppliedDifficulty = 0; BaseEnemyHp = enemy != null ? enemy.maxHealth : 0f;
        if (enemy == null || !LadderOn) return;
        float hm, dm; var row = RowAt(OpponentIndex, out hm, out dm);
        if (Mathf.Abs(hm - 1f) > 0.0005f)
        {
            enemy.maxHealth *= hm; enemy.health = enemy.maxHealth;
            var hp = enemy.GetComponent<Health>(); if (hp != null) { hp.max = enemy.maxHealth; hp.current = enemy.maxHealth; }
        }
        if (Mathf.Abs(dm - 1f) > 0.0005f) enemy.damageScale *= dm;
        AppliedHpMul = hm; AppliedDmgMul = dm;
        if (row.difficulty > 0)
        {
            var dr = DifficultyRowFor(row.difficulty); var br = enemy.GetComponent<AIBrain>();
            if (dr != null && br != null) { br.diff = dr; bool en = br.enabled; br.enabled = false; br.enabled = en; AppliedDifficulty = row.difficulty; }   // OnDisable resets the brain, Init reads the new row
        }
        Debug.Log("[GameLoop] fight " + FightNumber + " attempt " + Attempt + " opponent '" + row.id + "' hp x" + hm.ToString("F2") + " (" + enemy.maxHealth.ToString("F0") + ") damage x" + dm.ToString("F2") + " ai difficulty " + (AppliedDifficulty > 0 ? AppliedDifficulty.ToString() : "scene"));
    }

    // ---- actions (the buttons, the keys and LoopTest all call these) ----
    public static void Retry() { Driver()?.Go(delegate { Attempt++; Reload(); }); }
    public static void Next()
    {
        if (Outcome != Result.Victory && !HudCfg.Get().screens.nextOnDefeat) { Retry(); return; }
        Driver()?.Go(delegate { FightNumber++; OpponentIndex++; Attempt = 1; Reload(); });
    }
    static void Reload()
    {
        ResetGlobals();
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
    /// <summary>Everything that can outlive a scene and still be wrong in the next one.</summary>
    public static void ResetGlobals()
    {
        try { CombatSlowMo.Abort(); } catch { }
        try { PresentationClock.Cancel(); } catch { }
        Time.timeScale = 1f; Duel.Frozen = false;
    }
    static GameLoopDriver Driver() { return GameLoopDriver.Instance; }

    public static string TimeText(float s) { int t = Mathf.Max(0, Mathf.RoundToInt(s)); return (t / 60) + ":" + (t % 60).ToString("00"); }
    public static string StatsLine()
    {
        var L = HudCfg.Get().screens; var sb = new System.Text.StringBuilder();
        sb.Append(L.labels.time).Append(' ').Append(TimeText(Seconds)).Append(L.statSep).Append(L.labels.landed).Append(' ').Append(Landed).Append(L.statSep)
          .Append(L.labels.taken).Append(' ').Append(Taken).Append(L.statSep).Append(L.labels.parries).Append(' ').Append(Parries);
        if (!string.IsNullOrEmpty(Finisher)) sb.Append(L.statSep).Append(L.labels.finisher).Append(' ').Append(Finisher);
        return sb.ToString();
    }
    public static string Pretty(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var parts = id.Replace('-', '_').Split('_'); for (int i = 0; i < parts.Length; i++) parts[i] = Cap(parts[i]);
        return string.Join(" ", parts);
    }

    // ---- drawing helpers shared with GameHUD ----
    static readonly Dictionary<int, GUIStyle> styles = new Dictionary<int, GUIStyle>();
    public static GUIStyle Style(int size, TextAnchor a, bool bold)
    {
        int key = size * 64 + (int)a * 2 + (bold ? 1 : 0); GUIStyle s;
        if (!styles.TryGetValue(key, out s) || s == null)
        {
            s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = a, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal, wordWrap = false, clipping = TextClipping.Overflow, richText = false };
            styles[key] = s;
        }
        return s;
    }
    public static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }
    public static void Frame(Rect r, Color c, float t)
    {
        Fill(new Rect(r.x, r.y, r.width, t), c); Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
        Fill(new Rect(r.x, r.y + t, t, r.height - 2f * t), c); Fill(new Rect(r.xMax - t, r.y + t, t, r.height - 2f * t), c);
    }
    public static void Label(Rect r, string t, int size, TextAnchor a, bool bold, Color c)
    {
        var st = Style(size, a, bold);
        st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.85f); GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), t, st);
        st.normal.textColor = c; GUI.Label(r, t, st);
    }

    // ---- the end screen (driver OnGUI; vw / vh = virtual size inside GUI.matrix scale s) ----
    public static void DrawEnd(float vw, float vh)
    {
        float a = ScreenAlpha; if (a <= 0.001f || Outcome == Result.None) return;
        var H = HudCfg.Get(); var S = H.screens; var C = H.colors; bool win = Outcome == Result.Victory;
        var E = win ? S.victory : S.defeat; Color accent = HudCfg.Col(E.color);
        Fill(new Rect(0, 0, vw, vh), new Color(0f, 0f, 0f, S.dimAlpha * a));
        float bh = S.bandHeight, y0 = vh * 0.5f - bh * 0.5f - S.bandLift;
        Fill(new Rect(0, y0, vw, bh), new Color(0.018f, 0.018f, 0.022f, S.bandAlpha * a));
        Fill(new Rect(0, y0, vw, 2f), HudCfg.Col(C.crimson, 0.9f * a)); Fill(new Rect(0, y0 + bh - 2f, vw, 2f), HudCfg.Col(C.crimson, 0.9f * a));
        float cx = vw * 0.5f;
        var row = CurrentRow();
        string fight = string.Format(S.fightText, FightNumber, string.IsNullOrEmpty(row.title) ? Cap(row.name) : row.title);
        Label(new Rect(0, y0 + 16f, vw, 20f), fight.ToUpperInvariant(), S.nextLabelSize, TextAnchor.UpperCenter, true, HudCfg.Col(C.dim, a * 0.9f));
        float ty = y0 + 38f, th = S.titleSize * 1.3f;
        Label(new Rect(0, ty, vw, th), E.title, S.titleSize, TextAnchor.MiddleCenter, true, new Color(accent.r, accent.g, accent.b, a));
        Fill(new Rect(cx - 60f, ty + th + 2f, 120f, 1f), new Color(accent.r, accent.g, accent.b, 0.7f * a));
        float sy = ty + th + 12f;
        Label(new Rect(0, sy, vw, 30f), string.Format(E.subtitle, Cap(row.name)), S.subtitleSize, TextAnchor.UpperCenter, false, HudCfg.Col(C.ink, a * 0.95f));
        Label(new Rect(0, sy + 36f, vw, 24f), StatsLine(), S.statsSize, TextAnchor.UpperCenter, false, HudCfg.Col(C.dim, a));

        bool canNext = win || S.nextOnDefeat;
        float bw = S.buttonW, bhh = S.buttonH + 4f, gap = S.buttonGap, by = y0 + bh - bhh - 30f;
        bool live = a >= 0.5f && !GameLoopDriver.Transitioning;
        var gold = HudCfg.Col(C.gold); var crim = HudCfg.Col(C.crimson);
        if (canNext)
        {
            var rr = new Rect(cx - bw - gap * 0.5f, by, bw, bhh); var rn = new Rect(cx + gap * 0.5f, by, bw, bhh);
            if (Button(rr, S.retryText, S.retryHint, false, win ? gold : crim, a, live)) Retry();
            if (Button(rn, S.nextText, S.confirmHint, true, win ? gold : crim, a, live)) Next();
            if (win) Label(new Rect(0, by + bhh + 4f, vw, 18f), string.Format(S.nextPreview, NextRow().title.Length > 0 ? NextRow().title : Cap(NextRow().name)), S.nextLabelSize, TextAnchor.UpperCenter, false, HudCfg.Col(C.dim, a * 0.8f));
        }
        else
        {
            var rr = new Rect(cx - bw * 0.5f, by, bw, bhh);
            if (Button(rr, S.retryText, S.retryHintDefeat, true, crim, a, live)) Retry();
        }
    }

    static bool Button(Rect r, string label, string keyHint, bool primary, Color accent, float a, bool live)
    {
        var S = HudCfg.Get().screens; var C = HudCfg.Get().colors;
        bool hover = live && r.Contains(Event.current.mousePosition);
        Fill(r, hover ? new Color(0.13f, 0.11f, 0.11f, 0.95f * a) : new Color(0.05f, 0.05f, 0.055f, 0.92f * a));
        Frame(r, primary ? new Color(accent.r, accent.g, accent.b, a) : HudCfg.Col(C.dim, 0.55f * a), primary ? 2f : 1f);
        Label(new Rect(r.x, r.y + 5f, r.width, S.buttonSize * 1.4f), label.ToUpperInvariant(), S.buttonSize, TextAnchor.UpperCenter, true, hover || primary ? HudCfg.Col(C.ink, a) : HudCfg.Col(C.ink, 0.8f * a));
        Label(new Rect(r.x, r.yMax - S.keyHintSize * 1.7f, r.width, S.keyHintSize * 1.5f), keyHint, S.keyHintSize, TextAnchor.UpperCenter, false, HudCfg.Col(C.dim, 0.9f * a));
        return GUI.Button(r, GUIContent.none, GUIStyle.none) && live;
    }

    // ---- boot ----
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (GameLoopDriver.Instance != null) return;
            var go = new GameObject("GameLoop"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<GameLoopDriver>();
        }
        catch (Exception e) { Debug.LogWarning("[GameLoop] boot failed: " + e.Message); }
    }
}

[DefaultExecutionOrder(-150)]
public sealed class GameLoopDriver : MonoBehaviour
{
    public static GameLoopDriver Instance;
    public static bool Transitioning { get { return Instance != null && Instance.pending != null; } }

    Action pending; float fadeTarget, fadeDur = 0.5f;
    bool active, ended, cursorFreed, subscribed; int frames, lastBeginFrame = -1; float nextFind, endT;
    object keyOwner; KeyCode[] kConfirm, kRetry, kMid;

    void Awake() { Instance = this; }
    void OnEnable()
    {
        Instance = this;
        SceneManager.sceneLoaded += OnLoaded;
        if (!subscribed) { ContactRelay.OnContact += OnContact; subscribed = true; }
        BeginFight(SceneManager.GetActiveScene());
    }
    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnLoaded;
        if (subscribed) { ContactRelay.OnContact -= OnContact; subscribed = false; }
        if (Instance == this) Instance = null;
    }
    void OnLoaded(Scene s, LoadSceneMode m) { BeginFight(s); }

    public void Go(Action a)
    {
        if (pending != null) return;
        if (!GameLoop.FadeOn) { a(); return; }
        pending = a; fadeTarget = 1f; fadeDur = HudCfg.Get().fade.outS;
    }

    void BeginFight(Scene s)
    {
        if (lastBeginFrame == Time.frameCount) return;   // OnEnable and sceneLoaded of the same load
        lastBeginFrame = Time.frameCount; GameLoop.SceneLoads++;
        GameLoop.ResetGlobals();
        pending = null; ended = false; cursorFreed = false; frames = 0; nextFind = 0f;
        GameLoop.Outcome = GameLoop.Result.None; GameLoop.ScreenAlpha = 0f; GameLoop.Landed = GameLoop.Taken = GameLoop.Parries = 0; GameLoop.Finisher = ""; GameLoop.Seconds = 0f;
        GameLoop.Ready = false; GameLoop.LadderApplied = false; GameLoop.Player = GameLoop.Enemy = null;
        active = FindFirstObjectByType<DuelFighter>() != null;
        if (active)
        {
            if (GameLoop.SceneLoads == 1) GameLoop.OpponentIndex = Mathf.Max(0, GameLoop.Opponents().startIndex);
            GameLoop.FightsStarted++;
            GameLoop.FadeAlpha = GameLoop.FadeOn ? 1f : 0f; fadeTarget = 0f; fadeDur = HudCfg.Get().fade.inS;
        }
        else { GameLoop.FadeAlpha = 0f; fadeTarget = 0f; }
    }

    void FindFighters()
    {
        nextFind = Time.unscaledTime + 0.2f;
        DuelFighter d = null, p = null;
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) { if (f.name.Contains("Dracula")) d = f; else if (f.name.Contains("Pitborn")) p = f; }
        if (d != null && p != null) { GameLoop.Player = d; GameLoop.Enemy = p; frames = 0; }
    }

    void OnContact(DuelFighter a, DuelFighter d, string type, bool heavy, bool perfect)
    {
        if (!active || ended || GameLoop.Player == null) return;
        if (type == "Hit" && a == GameLoop.Player) GameLoop.Landed++;
        else if (type == "Hit" && d == GameLoop.Player) GameLoop.Taken++;
        else if (type == "Parried" && d == GameLoop.Player) GameLoop.Parries++;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime; var H = HudCfg.Get();
        if (!Mathf.Approximately(GameLoop.FadeAlpha, fadeTarget)) GameLoop.FadeAlpha = Mathf.MoveTowards(GameLoop.FadeAlpha, fadeTarget, dt / Mathf.Max(0.01f, fadeDur));
        if (pending != null && GameLoop.FadeAlpha >= 0.999f) { var p = pending; pending = null; try { p(); } catch (Exception e) { GameLoop.LastError = e.Message; Debug.LogError("[GameLoop] transition failed: " + e); } return; }
        if (!active) return;
        if (GameLoop.Player == null || GameLoop.Enemy == null) { if (Time.unscaledTime >= nextFind) FindFighters(); return; }

        frames++;
        if (!GameLoop.LadderApplied && frames >= Mathf.Max(1, GameLoop.Opponents().applyAfterFrames)) GameLoop.ApplyLadder(GameLoop.Enemy);
        if (!GameLoop.Ready && GameLoop.LadderApplied && GameLoop.FadeAlpha < 0.05f) GameLoop.Ready = true;

        if (RTFinisher.Busy && RTFinisher.Current != null && !string.IsNullOrEmpty(RTFinisher.Current.id)) GameLoop.Finisher = GameLoop.Pretty(RTFinisher.Current.id);
        bool pd = GameLoop.Player.Dead, ed = GameLoop.Enemy.Dead;
        if (!ended && (pd || ed))
        {
            ended = true; GameLoop.Outcome = pd ? GameLoop.Result.Defeat : GameLoop.Result.Victory; GameLoop.Seconds = Duel.TickCount / 60f; endT = Time.unscaledTime;
            Debug.Log("[GameLoop] fight " + GameLoop.FightNumber + " " + GameLoop.Outcome + " | " + GameLoop.StatsLine());
        }
        if (ended)
        {
            if (RTFinisher.Busy) endT = Time.unscaledTime;
            var S = H.screens; GameLoop.ScreenAlpha = Mathf.Clamp01((Time.unscaledTime - endT - S.delayS) / Mathf.Max(0.05f, S.fadeInS));
            if (GameLoop.ScreenAlpha > 0f && !cursorFreed) { cursorFreed = true; if (!Application.isBatchMode) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } }
        }

        if (pending != null) return;
        if (!ReferenceEquals(keyOwner, H)) { keyOwner = H; kConfirm = HudCfg.Keys(H.input.confirmKeys, new[] { KeyCode.Return }); kRetry = HudCfg.Keys(H.input.retryKeys, new[] { KeyCode.Backspace }); kMid = HudCfg.Keys(H.input.midFightRetryKeys, new[] { KeyCode.Backspace }); }
        if (STSteer.Active) return;
        if (HudCfg.AnyDown(kMid)) { GameLoop.Retry(); return; }
        if (GameLoop.ScreenAlpha >= 0.5f)
        {
            if (HudCfg.AnyDown(kRetry)) GameLoop.Retry();
            else if (HudCfg.AnyDown(kConfirm)) { if (GameLoop.Outcome == GameLoop.Result.Victory) GameLoop.Next(); else GameLoop.Retry(); }
        }
    }

    void OnGUI()
    {
        GUI.depth = -1000;
        var ev = Event.current;
        float s = HudCfg.Scale();
        if (!STSteer.Active && GameLoop.ScreenAlpha > 0.001f)
        {
            var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            GameLoop.DrawEnd(Screen.width / s, Screen.height / s);
            GUI.matrix = m;
        }
        if (GameLoop.FadeAlpha > 0.001f && ev.type == EventType.Repaint)
            GameLoop.Fill(new Rect(0, 0, Screen.width, Screen.height), HudCfg.Col(HudCfg.Get().fade.color, GameLoop.FadeAlpha));
    }
}
