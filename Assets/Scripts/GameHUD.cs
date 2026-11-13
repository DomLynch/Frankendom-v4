using UnityEngine;
using UnityEngine.SceneManagement;

// HUD2 (lane laneHUD2, v83): dark-gothic HUD, IMGUI, everything drawn procedurally (HudDraw / CombatHudBars), every size / colour / font / string in hud.json (HudCfg).
//   top-left     : the player's name (serif small caps), a thin tapered HP bar with an ornate frame, thinner stamina and posture bars under it, cue text (PARRY, GUARD BROKEN ...) on the name row
//   top-centre   : the enemy's boss bar: name in serif small caps, ladder title under the name, wide tapered HP bar, posture bar under it, DEATHBLOW / POSTURE BROKEN cue
//   bottom-left  : stance indicator (stances.json label + colour), the F1 controls panel above it (hidden until F1)
//   bottom-right : the two special-move cooldown icons
//   near the crosshair: the guard-direction indicator (left / top / right arcs, the held side lit, gold during the parry window)
// Names come from Roster (never literals). The VICTORY / YOU DIED screen is GameLoop.DrawEnd (this class no longer draws a message or reloads the scene: GameLoop owns Retry / Backspace).
// Public fields player / enemy / pf / ef are kept: the scene (BuildSlice) assigns them and STUI detaches them in steer mode (nothing is drawn then).
public class GameHUD : MonoBehaviour
{
    public Health player, enemy;
    public DuelFighter pf, ef;

    bool showFps, hintOn, hintPinned; float fpsAcc, fpsShown; int fpsN; float hintA, startT; KeyCode toggleKey = KeyCode.F1; string toggleKeyName = "";
    readonly CombatHudBars.Lag lagP = new CombatHudBars.Lag(); CombatHudBars.Lag lagE = new CombatHudBars.Lag(), lagS = new CombatHudBars.Lag();   // TWOFIGHT v1: lagE restarts on a target switch, lagS = the second bar
    DuelFighter zSecondDf; Health zSecondHp; string zSecondName = ""; float bossBottom;   // TWOFIGHT v1
    float guardA; float[] cdMax = new float[4]; float[] readyT = new float[4]; bool[] wasReady = new bool[4];   // ABILITIES v1: 4 slots
    float lastNow;
    bool camArg; string fpsTxt; float fpsTxtFor = -1f; string[] lblK = new string[4], lblS = new string[4], lblT = new string[4]; static readonly string[] nums = new string[128];   // UIDATA_A v1: caches, OnGUI allocates nothing per frame
    static string Num(int n) { if (n < 0 || n >= nums.Length) return n.ToString(); return nums[n] ?? (nums[n] = n.ToString()); }
    // HUDBUG v1 (zones): with zones on the scene enemy is an inactive template and every enemy is a clone, so enemy/ef (set by BuildSlice) never moved. Zones off: untouched.
    Health zEnemyHp; DuelFighter zEnemyDf; float zPlayerLast = -1f, zEnemyLast = -1f, zBarLastEngaged = -1f; DuelFighter zEnemyLogged;
    void ZonesBind()
    {
        if (!ZoneRuntime.Owns || pf == null && player == null) return;   // steer mode detaches the HUD (pf and player null): leave it
        var e = GameLoop.Enemy; if (e != zEnemyDf) { zEnemyDf = e; if (ZoneData.Get().twoFight.resetBarOnSwitch) lagE = new CombatHudBars.Lag(); /* TWOFIGHT v1: a switch from a full bar to a hurt one drew the whole difference as a fresh damage trail */ zEnemyHp = e != null ? e.GetComponent<Health>() : null; }
        ef = zEnemyDf; enemy = zEnemyHp;
        if (GameLoop.Player != null && GameLoop.Player != pf) { pf = GameLoop.Player; player = pf.GetComponent<Health>(); }
        // dev2 BOSSBAR v1 (code2.json bossBar): the enemy bar is drawn only while that fighter is engaged with Joan, plus lingerS after the last engaged moment (death card, hand-over); never for a fighter that stands idle / in the safe camp.
        var bb = Code2Cfg.Get(); if (bb != null && bb.bossBar.enabled && ef != null)
        {
            if (!ef.Dead && ef.opponent != null && ef.opponent == GameLoop.Player) zBarLastEngaged = Time.unscaledTime;
            if (zBarLastEngaged < 0f || Time.unscaledTime - zBarLastEngaged > bb.bossBar.lingerS) { ef = null; enemy = null; }
        }
        ZonesHpLog();
    }
    // exact -zones proof: one log line per change of the HUD's two health numbers
    void ZonesHpLog()
    {
        if (player != null && pf != null && Mathf.Abs(pf.health - zPlayerLast) > 0.01f) { if (zPlayerLast >= 0f) Debug.Log("[HudHp] " + pf.name + " " + zPlayerLast.ToString("F1") + " -> " + pf.health.ToString("F1") + " bar " + (player.max > 0f ? player.current / player.max : 0f).ToString("F2") + " tick " + Duel.TickCount); zPlayerLast = pf.health; }
        if (ef != null) { if (zEnemyLogged != ef) { zEnemyLogged = ef; zEnemyLast = ef.health; Debug.Log("[HudHp] boss bar now follows " + ef.name + " hp " + ef.health.ToString("F1") + " (" + Roster.IdOf(ef.transform) + ")"); } else if (Mathf.Abs(ef.health - zEnemyLast) > 0.01f) { Debug.Log("[HudHp] " + ef.name + " " + zEnemyLast.ToString("F1") + " -> " + ef.health.ToString("F1") + " bar " + (enemy != null && enemy.max > 0f ? enemy.current / enemy.max : 0f).ToString("F2") + " tick " + Duel.TickCount); zEnemyLast = ef.health; } }
    }

    void Start()
    {
        startT = Time.unscaledTime;
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-fps" || a == "-gaps" || a == "-autoplay") showFps = true; else if (a == "-camdebug") camArg = true; /* UIDATA_A v1 */
    }

    void Update()
    {
        ZonesBind();   // HUDBUG v1
        var H = HudCfg.Get();
        if (toggleKeyName != H.hint.toggleKey) { toggleKeyName = H.hint.toggleKey; System.Enum.TryParse(toggleKeyName, true, out toggleKey); }
        if (H.hint.enabled && InputMap.Down("hints", toggleKey)) { hintPinned = true; hintOn = !hintOn; }
        if (Input.GetKeyDown(KeyCode.F3)) showFps = !showFps;
        fpsAcc += Time.unscaledDeltaTime; fpsN++; if (fpsAcc >= HudLayout.Get().game.fpsEvery) { fpsShown = fpsN / fpsAcc; fpsAcc = 0f; fpsN = 0; }
        if (Input.GetKeyDown(KeyCode.Escape) && !MenuUI.Enabled) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        // steer mode has no GameLoop key handling: keep the old Backspace restart there only
        if (STSteer.Active && Input.GetKeyDown(KeyCode.Backspace)) SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        // the controls panel is up for hint.showSeconds after the fight starts (0 = only on F1), unless F1 has been used
        if (!hintPinned && H.hint.showSeconds > 0f) hintOn = Time.unscaledTime - startT >= H.hint.delayS && Time.unscaledTime - startT < H.hint.delayS + H.hint.showSeconds;
    }

    static Color Ink(float a = 1f) { return HudCfg.Col(HudCfg.Get().colors.ink, a); }

    void OnGUI()
    {
        var ev = Event.current;
        if (ev.type != EventType.Repaint) return;   // nothing here takes input; skip the Layout pass (the old HUD drew and allocated in both)
        var H = HudCfg.Get();
        if (showFps) DebugLines(H);
        if (STSteer.Active) return;   // steer mode: STUI draws the bar, hints and banners
        float s = HudCfg.Scale(), vw = Screen.width / s, vh = Screen.height / s, now = Time.unscaledTime, dt = Mathf.Clamp(now - lastNow, 0f, HudLayout.Get().game.dtMax); lastNow = now;
        var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        HudDraw.Alpha = 1f - Mathf.Clamp01(GameLoop.ScreenAlpha) * Mathf.Clamp01(H.screens.hudFade);   // the end screen fades the HUD out
        try
        {
            DrawPlayer(H, vw, now, dt);
            DrawBoss(H, vw, now, dt); DrawSecond(H, vw, now, dt);   // TWOFIGHT v1
            DrawStance(H, vw, vh);
            DrawHint(H, vw, vh, dt);
            DrawSpecials(H, vw, vh, now);
            DrawGuardDir(H, vw, vh, dt, now);
        }
        finally { HudDraw.Alpha = 1f; GUI.matrix = m; }
    }

    // ---------------- player block (top-left) ----------------
    void DrawPlayer(HudCfg.Data H, float vw, float now, float dt)
    {
        var P = H.player; if (player == null && pf == null) return;
        float x = P.x, y = P.y;
        float nameH = P.nameSize * H.layout.nameLine; /* HCKIT6 v100 */
        HudDraw.Caps(x, y, 0f, Roster.PlayerName, "title", P.nameSize, P.nameSmall, P.nameTracking, Ink());
        var hp = new Rect(x, y + nameH + P.nameGap, P.hp.w, P.hp.h);
        float f = player != null && player.max > 0f ? Mathf.Clamp01(player.current / player.max) : 0f;
        lagP.Step(f, now, dt);
        CombatHudBars.Bar(hp, P.hp.taper, f, lagP.v, HudCfg.Col(H.colors.hpPlayer), true, true);
        string cue = null; Color cc = Color.white; bool big = false;
        if (pf != null && !pf.Dead) CombatHudBars.Draw(   // kitV76 (Goose v75): no stamina / posture strips at 0 HP
hp.x, hp.yMax + H.bars.framePad + P.stamina.gap, 0f, pf, true, P.stamina, P.posture, P.showStamina, P.showPosture, out cue, out cc, out big);
        if (cue != null) HudDraw.Caps(hp.xMax, y + P.cueDy, 1f, cue, "title", P.cueSize, Mathf.Max(HudLayout.Get().game.minFont, P.cueSize - HudLayout.Get().game.cueSmallDelta), HudLayout.Get().game.cueTracking, cc);
    }

    // ---------------- boss bar (top-centre) ----------------
    // BOSSNAME v1: the boss the bar follows decides the name; an opponents row only supplies name/title when its character is that fighter
    static bool RowIsFoe(GameLoop.Row r, DuelFighter f) { string id = f != null ? Roster.IdOf(f.transform) : ""; return id.Length == 0 || string.IsNullOrEmpty(r.character) || string.Equals(r.character, id, System.StringComparison.OrdinalIgnoreCase); }
    static string BossName(DuelFighter f, GameLoop.Row r) { string id = f != null ? Roster.IdOf(f.transform) : ""; if (id.Length > 0 && !RowIsFoe(r, f)) return Roster.Get(id).displayName.ToUpperInvariant(); return string.IsNullOrEmpty(r.name) ? Roster.EnemyName.ToUpperInvariant() : r.name; }
    void DrawBoss(HudCfg.Data H, float vw, float now, float dt)
    {
        var Bc = H.boss; if (!Bc.enabled || (enemy == null && ef == null)) return;
        float maxW = vw - 2f * (H.player.x + H.player.hp.w + Bc.sideMargin);
        float w = Mathf.Max(H.layout.bossMinW, Mathf.Min(Bc.w, maxW)), x = (vw - w) * 0.5f, y = Bc.top;
        float nameH = Bc.nameSize * H.layout.nameLine;
        // HUDALIGN v1: subtitle inline + bar at Joan's bar height (hud.json boss.subtitle / boss.align; defaults = the old layout)
        string title = Bc.showTitle && GameLoop.LadderOn ? GameLoop.CurrentRow().title : null; bool tgt = Bc.nameFrom == "target"; var crow = GameLoop.CurrentRow(); if (tgt && !RowIsFoe(crow, ef)) title = null;   /* BOSSNAME v1 */ bool hasTitle = !string.IsNullOrEmpty(title), inl = hasTitle && Bc.subtitle.inline;
        float below = hasTitle && !inl ? Bc.titleSize * H.layout.titleAdvance + Bc.titleGap : 0f, barY = -1f;
        if (Bc.align.enabled)
        {
            var P = H.player; barY = P.y + P.nameSize * H.layout.nameLine + P.nameGap + (P.hp.h - Bc.hp.h) * Bc.align.barPivot + Bc.align.barDy;
            y = Bc.align.nameBaseline && below <= 0f ? P.y + (P.nameSize - Bc.nameSize) * H.layout.lineH + Bc.subtitle.descent * (Bc.nameSmall - P.nameSmall) : barY - nameH - below - Bc.nameGap;
        }
        float nameW = HudDraw.Caps(x, y, 0f, tgt ? BossName(ef, crow) : ZoneRuntime.Owns ? GameLoop.EnemyName() : Roster.EnemyName, "title", Bc.nameSize, Bc.nameSmall, Bc.nameTracking, Ink());
        float ty = y + nameH;
        if (hasTitle)
        {
            var tcol = Bc.titleColor != null && Bc.titleColor.Length >= 3 ? HudCfg.Col(Bc.titleColor) : HudCfg.Col(H.colors.dim);
            if (inl) { int ts = Bc.subtitle.size > 0 ? Bc.subtitle.size : Bc.titleSize; float sx = x + nameW + Bc.subtitle.gap; HudDraw.Text(new Rect(sx, y + Bc.subtitle.dy - Bc.subtitle.descent * (Bc.nameSmall - ts), Mathf.Max(0f, x + w - sx), Bc.nameSize * H.layout.lineH), title, "serif", ts, TextAnchor.LowerLeft, true, tcol, Bc.subtitle.italic); }
            else { HudDraw.Text(new Rect(x, ty + Bc.titleGap, w, Bc.titleSize * H.layout.titleBox), title, "serif", Bc.titleSize, TextAnchor.UpperLeft, true, tcol, true); ty += below; }
        }
        var hp = new Rect(x, barY >= 0f ? barY : ty + Bc.nameGap, w, Bc.hp.h);
        float f = enemy != null && enemy.max > 0f ? Mathf.Clamp01(enemy.current / enemy.max) : 0f;
        lagE.Step(f, now, dt);
        CombatHudBars.Bar(hp, Bc.hp.taper, f, lagE.v, HudCfg.Col(H.colors.hpEnemy), true, true);
        string cue = null; Color cc = Color.white; bool big = false; float after = hp.yMax + H.bars.framePad;
        if (ef != null && !ef.Dead) after = CombatHudBars.Draw(   // kitV76 (Goose v75): no stamina / posture strips at 0 HP
hp.center.x, hp.yMax + H.bars.framePad + Bc.posture.gap, 0.5f, ef, false, Bc.stamina, Bc.posture, Bc.showStamina, Bc.showPosture, out cue, out cc, out big);
        bossBottom = after;   // TWOFIGHT v1
        if (cue != null) HudDraw.Caps(hp.center.x, after + Bc.cueGap - H.layout.cueLift, 0.5f, cue, "title", Bc.cueSize, Mathf.Max(HudLayout.Get().game.minFont, Bc.cueSize - (int)H.layout.cueSmallDelta), H.layout.cueTracking, cc);
    }

    // ---------------- second enemy (TWOFIGHT v1; hud.json boss.second, dark until enabled) ----------------
    // With two enemies on the player the boss bar shows only the target. This draws the other one small under it: name from Roster, bar from its Health. No allocation per frame (name cached per fighter).
    void DrawSecond(HudCfg.Data H, float vw, float now, float dt)
    {
        var Bc = H.boss; var K = Bc.second; if (K == null || !K.enabled || !ZoneRuntime.Owns || !Bc.enabled || (enemy == null && ef == null) || bossBottom <= 0f) return;
        var o = ZoneRuntime.SecondFoe(ef); if (o == null) { zSecondDf = null; return; }
        if (o != zSecondDf) { zSecondDf = o; zSecondHp = o.GetComponent<Health>(); zSecondName = Roster.Get(Roster.IdOf(o.transform)).displayName.ToUpperInvariant(); lagS = new CombatHudBars.Lag(); }
        if (zSecondHp == null) return;
        float maxW = vw - 2f * (H.player.x + H.player.hp.w + Bc.sideMargin);
        float w = Mathf.Max(H.layout.bossMinW, Mathf.Min(Bc.w, maxW)) * K.widthScale, x = (vw - w) * 0.5f, y = bossBottom + K.gap;
        int ns = Mathf.Max(HudLayout.Get().game.minFont, Mathf.RoundToInt(Bc.nameSize * K.nameScale)), nsm = Mathf.Max(HudLayout.Get().game.minFont, Mathf.RoundToInt(Bc.nameSmall * K.nameScale));
        HudDraw.Caps(x, y, 0f, zSecondName, "title", ns, nsm, Bc.nameTracking, Ink());
        float f = zSecondHp.max > 0f ? Mathf.Clamp01(zSecondHp.current / zSecondHp.max) : 0f;
        lagS.Step(f, now, dt);
        var hp = new Rect(x, y + ns * H.layout.nameLine + Bc.nameGap * K.nameScale, w, Bc.hp.h * K.heightScale);
        CombatHudBars.Bar(hp, Bc.hp.taper * K.heightScale, f, lagS.v, HudCfg.Col(H.colors.hpEnemy), true, K.ticks);
    }

    // ---------------- stance (bottom-left) ----------------
    void DrawStance(HudCfg.Data H, float vw, float vh)
    {
        var S = H.stance; if (!S.enabled || pf == null || !StanceLayer.Active) return;
        var row = StanceLayer.Row(StanceLayer.StanceOf(pf)); if (row == null) return;
        Color col = HudCfg.Col(row.color, 1f); Color dim = HudCfg.Col(H.colors.dim);
        float y = vh - S.bottom, x = S.x;
        HudDraw.Text(new Rect(x, y - S.captionSize * H.layout.captionLift, H.layout.captionW, S.captionSize * H.layout.captionH), S.caption, "serif", S.captionSize, TextAnchor.UpperLeft, false, dim);
        float d = S.diamond;
        HudDraw.Tex(new Rect(x - H.layout.diamondDx, y + (S.nameSize * H.layout.diamondLine - d) * 0.5f, d, d), HudDraw.Diamond(), col);
        float w = HudDraw.Caps(x + d + H.layout.diamondGap, y, 0f, row.label, "title", S.nameSize, S.nameSmall, S.tracking, col);
        if (!string.IsNullOrEmpty(S.hint)) HudDraw.Text(new Rect(x + d + H.layout.hintGap + w, y + S.nameSize * H.layout.hintDy, H.layout.hintW, S.hintSize * H.layout.hintH), S.hint, "serif", S.hintSize, TextAnchor.UpperLeft, false, new Color(dim.r, dim.g, dim.b, dim.a * H.layout.hintAlpha));
        // switch toast: the new stance's name, lower centre, fades out
        string tt; Color tc; float ta;
        if (StanceLayer.StanceDriver.ToastState(out tt, out tc, out ta)) HudDraw.Caps(vw * 0.5f, vh * S.toastY, 0.5f, tt, "title", S.toastSize, S.toastSmall, S.tracking + H.layout.toastTracking, new Color(tc.r, tc.g, tc.b, ta));
    }

    // ---------------- controls panel (F1) ----------------
    void DrawHint(HudCfg.Data H, float vw, float vh, float dt)
    {
        var K = H.hint; if (!K.enabled) return;
        hintA = Mathf.MoveTowards(hintA, hintOn ? 1f : 0f, dt / Mathf.Max(HudLayout.Get().game.fadeMinS, K.fadeSeconds));
        Color dim = HudCfg.Col(H.colors.dim);
        // the small corner prompt, only for the first cornerSeconds of the fight and while the panel is closed
        if (!string.IsNullOrEmpty(K.cornerText) && hintA < HudLayout.Get().game.hintCutoff && !hintPinned && Time.unscaledTime - startT < K.cornerSeconds && H.stance.enabled)
        {
            float ca = K.cornerAlpha * Mathf.Clamp01((K.cornerSeconds - (Time.unscaledTime - startT)) / H.layout.cornerFadeS);
            HudDraw.Text(new Rect(H.stance.x, vh - H.stance.bottom + H.stance.nameSize * H.layout.cornerDy, H.layout.cornerW, K.cornerSize * H.layout.cornerH), K.cornerText, "serif", K.cornerSize, TextAnchor.UpperLeft, false, new Color(dim.r, dim.g, dim.b, ca));
        }
        if (hintA < HudLayout.Get().game.hintCutoff || K.rows == null || K.rows.Length == 0) return;
        int cols = Mathf.Max(1, K.columns), perCol = Mathf.CeilToInt(K.rows.Length / (float)cols);
        float w = cols * K.colWidth + 2f * K.pad, h = perCol * K.rowHeight + 2f * K.pad + K.titleSize * H.layout.keysTitleLine + K.footerSize * H.layout.keysFooterAdvance;
        var r = new Rect(K.margin, vh - K.bottom - h, w, h);
        float old = HudDraw.Alpha; HudDraw.Alpha = old * hintA;
        HudDraw.Fill(r, new Color(H.layout.keysPanel[0], H.layout.keysPanel[1], H.layout.keysPanel[2], K.panelAlpha));
        var fc = HudCfg.Col(H.colors.frame); HudDraw.Fill(new Rect(r.x, r.y, r.width, H.layout.keysLinePx), fc); HudDraw.Fill(new Rect(r.x, r.yMax - H.layout.keysLinePx, r.width, H.layout.keysLinePx), HudCfg.Col(H.colors.frameDim));
        HudDraw.Caps(r.x + K.pad, r.y + K.pad * H.layout.keysTitleDy, 0f, K.title, "title", K.titleSize, K.titleSize - (int)H.layout.keysTitleSmall, H.layout.keysTitleTracking, Ink());
        float y0 = r.y + K.pad + K.titleSize * H.layout.keysTitleLine; var gold = HudCfg.Col(H.colors.gold);
        for (int i = 0; i < K.rows.Length; i++)
        {
            int c = i / perCol, rr = i % perCol; var row = K.rows[i]; if (row == null) continue;
            float rx = r.x + K.pad + c * K.colWidth, ry = y0 + rr * K.rowHeight;
            HudDraw.Text(new Rect(rx, ry, K.keyWidth, K.rowHeight), row.k, "serif", K.fontSize, TextAnchor.UpperLeft, true, gold);
            HudDraw.Text(new Rect(rx + K.keyWidth, ry, K.colWidth - K.keyWidth, K.rowHeight), row.a, "serif", K.fontSize, TextAnchor.UpperLeft, false, Ink(H.layout.keysRowInk));
        }
        HudDraw.Text(new Rect(r.x + K.pad, r.yMax - K.footerSize * H.layout.keysFooterLine - H.layout.keysFooterLift, r.width - 2f * K.pad, K.footerSize * H.layout.keysFooterBox), K.footer, "serif", K.footerSize, TextAnchor.UpperLeft, false, dim, true);
        HudDraw.Alpha = old;
    }

    // ---------------- special-move cooldown icons (bottom-right) ----------------
    void DrawSpecials(HudCfg.Data H, float vw, float vh, float now)
    {
        var S = H.specials; if (!S.enabled || pf == null || S.items == null) return;
        int n = Mathf.Min(2, S.items.Length); int an = AbilityTable.SlotCount(pf); if (an > 0) n = an;   // ABILITIES v1: a bar of up to 4 while ability rows apply (off: the two hud.json icons, as today)
        float total = n * S.size + (n - 1) * S.gap, x0 = vw - S.right - total, y0 = vh - S.bottom - S.size;
        for (int i = 0; i < n; i++)
        {
            var it = i < S.items.Length ? S.items[i] : null; var ab = an > 0 ? AbilityTable.For(pf, i) : null; if (it == null && ab == null) continue;   // ABILITIES v1: ab null = hud.json icon
            int cd = pf.SlotCd(i);
            if (cd > cdMax[i]) cdMax[i] = cd;
            float tot = Mathf.Max(1f, ab != null && ab.cooldownS > 0f ? ab.cooldownS * S.ticksPerSecond : S.cooldownTicks, cdMax[i]);
            bool ready = ab != null && ab.trigger == "window" ? (pf.RiposteLeft > 0 || pf.EvadeOpen) : cd <= 0; bool lit = ready && !(ab != null && ab.staminaCost > 0f && pf.stamina < ab.staminaCost); /* ABILITIES v1: window slot lights only while a window is open; a priced ability dims below its cost */ if (ready && !wasReady[i]) readyT[i] = now; wasReady[i] = ready;
            if (ready) cdMax[i] = 0f;
            var r = new Rect(x0 + i * (S.size + S.gap), y0, S.size, S.size);
            Color readyCol = HudCfg.Col(H.colors.ready), coolCol = HudCfg.Col(H.colors.cooling);
            HudDraw.Tex(r, HudDraw.Disc(), new Color(H.layout.iconBack[0], H.layout.iconBack[1], H.layout.iconBack[2], H.layout.iconBack[3]));
            HudDraw.Tex(r, HudDraw.Ring(S.ringPx), lit ? readyCol : HudCfg.Col(H.colors.frameDim));
            bool waitWin = ab != null && ab.trigger == "window" && !ready;   // ABILITYICONS v1: a window ability (riposte / dodge strike) waits for its window: a slow pulsing gold ring + "(parry)" instead of a plain dim slot
            if (waitWin) { var wc = readyCol; wc.a *= 0.22f + 0.28f * (0.5f + 0.5f * Mathf.Sin(now * 3.2f)); HudDraw.Tex(new Rect(r.x - 3f, r.y - 3f, r.width + 6f, r.height + 6f), HudDraw.Ring(S.ringPx), wc); }
            float gs = S.size * S.glyphScale; var gr = new Rect(r.center.x - gs * 0.5f, r.center.y - gs * 0.5f, gs, gs);
            HudDraw.Tex(gr, HudDraw.Glyph(ab != null ? ab.glyph : it.glyph), lit ? HudCfg.Col(H.colors.ink, H.layout.iconInk) : new Color(coolCol.r, coolCol.g, coolCol.b, H.layout.iconCoolAlpha));
            if (!ready && cd > 0)
            {
                var pie = HudDraw.Pie(cd / tot, S.pieSteps); if (pie != null) HudDraw.Tex(r, pie, new Color(H.layout.pieShade[0], H.layout.pieShade[1], H.layout.pieShade[2], H.layout.pieShade[3]));
                HudDraw.Text(r, Num(Mathf.CeilToInt(cd / Mathf.Max(1f, S.ticksPerSecond))), "title", S.cdSize, TextAnchor.MiddleCenter, true, HudCfg.Col(H.colors.ink, H.layout.cdInk));
            }
            else if (ready)
            {
                float k = 1f - (now - readyT[i]) / Mathf.Max(HudLayout.Get().game.readyFlashMinS, S.readyFlashS);
                if (k > 0f) { float g = S.size * (H.layout.flashBase + H.layout.flashGrow * (1f - k)); HudDraw.Tex(new Rect(r.center.x - g * 0.5f, r.center.y - g * 0.5f, g, g), HudDraw.Ring(S.ringPx), new Color(readyCol.r, readyCol.g, readyCol.b, k)); }
            }
            string kS = ab != null ? ab.key : it.key, lS = ab != null ? ab.name : it.label; string label = kS; if (!string.IsNullOrEmpty(lS)) { string sl = ab != null ? lS : BowHooks.SpecialLabel(pf, i, lS); if (lblT[i] == null || !string.Equals(lblK[i], kS) || !string.Equals(lblS[i], sl)) { lblK[i] = kS; lblS[i] = sl; lblT[i] = kS + "  " + sl; } label = lblT[i]; if (ab != null && ab.trigger == "window" && !ready) label += " (waiting)"; } /* BOW v2 */ /* UIDATA_A v1: cached concat */
            HudDraw.Text(new Rect(r.x - H.layout.keyLabelPad, r.yMax + S.keyDy, S.size + 2f * H.layout.keyLabelPad, S.keySize * H.layout.keyLabelBox), label, "serif", S.keySize, TextAnchor.UpperCenter, false, lit ? HudCfg.Col(H.colors.ink, H.layout.keyLabelInk) : HudCfg.Col(H.colors.dim));
        }
    }

    // ---------------- guard direction (around the crosshair, only while guarding) ----------------
    void DrawGuardDir(HudCfg.Data H, float vw, float vh, float dt, float now)
    {
        var G = H.guardDir; if (!G.enabled || pf == null) return;
        bool up = !pf.Dead && ControlsTable.SideRequired && (pf.state == FState.Guard || pf.guardHeld);
        guardA = Mathf.MoveTowards(guardA, up ? 1f : 0f, dt / Mathf.Max(HudLayout.Get().game.fadeMinS, up ? G.fadeInS : G.fadeOutS));
        if (guardA < HudLayout.Get().game.guardCutoff) return;
        var sides = ControlsTable.Get().guard.sides; if (sides == null) return;
        // STEERUI v93: v83 rotated with GUIUtility.RotateAroundPivot while the HUD scale matrix was set (the pivot is taken in UNSCALED screen px, the rects in scaled px), so left / right swung away from the centre whenever scale != 1
        // (Dom 1350x858: the left arc at x=310). Now: everything in screen px with an own rotation matrix, anchored to the player's projected body (feet..head), sizes = hud.json guardDir fractions of the player's screen height.
        Vector2 c; float hPx; GuardAnchor(G, out c, out hPx);
        float s = HudCfg.Scale();
        bool parry = pf.ParryWindowLeft > 0;
        Color live = parry ? HudCfg.Col(H.colors.parry) : HudCfg.Col(H.colors.guard); var AG = ArcFx.GuardOn(); if (AG != null) live = ArcFx.GuardColor(AG, true, parry);   // ARCS v105
        float grow = parry ? 1f + G.parryGrow * (0.5f + 0.5f * Mathf.Sin(now * Mathf.PI * 2f * G.parryPulseHz)) : 1f;
        float R = G.arcRadiusFrac * hPx, half = R / HudDraw.ArcCentre; var arc = AG != null ? ArcFx.GuardArc(AG) : HudDraw.Arc(G.arcDeg, G.thickness);   // ARCS v105
        float oa = HudDraw.Alpha;
        var saved = GUI.matrix; GUI.matrix = Matrix4x4.identity;
        // optional faint full ring (ringFrac 0 = off) so the three sides read as one instrument
        if (G.ringFrac > 0f) { float rr = G.ringFrac * hPx; HudDraw.Tex(new Rect(c.x - rr, c.y - rr, 2f * rr, 2f * rr), HudDraw.Ring(H.layout.guardRingPx), new Color(live.r, live.g, live.b, G.ringAlpha * guardA)); }
        for (int i = 0; i < sides.Length; i++)
        {
            var sd = sides[i]; if (sd == null) continue;
            bool active = pf.guardSide == sd.id;
            float hs = active ? half * grow : half; Color col = active ? live : HudCfg.Col(H.colors.guard);
            float a = (active ? 1f : G.idleAlpha) * guardA; if (AG != null) { col = ArcFx.GuardColor(AG, active, parry); a = ArcFx.GuardAlpha(AG, active, parry) * guardA; }   // ARCS v105
            float rad = sd.deg * Mathf.Deg2Rad; Vector2 dirv = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));   // screen px, y down: deg 0 = up, +90 = right
            float dist = Mathf.Lerp(G.sideDistFrac, G.topDistFrac, Mathf.Abs(dirv.y)) * hPx;                 // apex of the arc, from the body centre
            Vector2 piv = c + dirv * (dist - R);                                                              // centre of the arc's circle (texture centre)
            GUI.matrix = Matrix4x4.TRS(new Vector3(piv.x, piv.y, 0f), Quaternion.Euler(0f, 0f, sd.deg), Vector3.one) * Matrix4x4.TRS(new Vector3(-piv.x, -piv.y, 0f), Quaternion.identity, Vector3.one);
            HudDraw.Tex(new Rect(piv.x - hs, piv.y - hs, 2f * hs, 2f * hs), arc, new Color(col.r, col.g, col.b, a));
        }
        GUI.matrix = Matrix4x4.identity;
        // centre guard (no side): the small diamond is lit, on the body centre
        bool centre = pf.guardSide == 0; float ds = G.dotSize * s * (centre ? H.layout.guardDotCentre : 1f);
        HudDraw.Tex(new Rect(c.x - ds * 0.5f, c.y - ds * 0.5f, ds, ds), HudDraw.Diamond(), new Color(live.r, live.g, live.b, (centre ? 1f : G.idleAlpha) * guardA));
        GUI.matrix = saved;
        HudDraw.Alpha = oa;
    }

    // STEERUI v93: body centre (screen px, y down) and body height (screen px) of the player; falls back to the screen centre when the camera or the fighter is unusable (or anchorPlayer is false)
    float bodyH, bodyHT = -99f;
    void GuardAnchor(HudCfg.GuardDirC G, out Vector2 c, out float hPx)
    {
        c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f); hPx = Screen.height * G.fallbackHeightFrac;
        var cam = Camera.main; if (!G.anchorPlayer || cam == null || pf == null) return;
        float now = Time.unscaledTime;
        if (now - bodyHT >= G.heightRefreshS)
        {
            bodyHT = now; float lo = 1e9f, hi = -1e9f;
            foreach (var r in pf.GetComponentsInChildren<SkinnedMeshRenderer>()) { if (r == null || !r.enabled) continue; var bb = r.bounds; lo = Mathf.Min(lo, bb.min.y); hi = Mathf.Max(hi, bb.max.y); }
            float h = hi > lo ? hi - pf.transform.position.y : G.defaultHeightM;
            bodyH = Mathf.Clamp(h, G.minHeightM, G.maxHeightM);
        }
        Vector3 foot = pf.transform.position; Vector3 sf = cam.WorldToScreenPoint(foot), sh = cam.WorldToScreenPoint(foot + Vector3.up * bodyH);
        if (sf.z <= HudLayout.Get().game.nearZ || sh.z <= HudLayout.Get().game.nearZ) return;
        float hp = Mathf.Abs(sh.y - sf.y); if (hp < HudLayout.Get().game.minBodyPx) return;
        hPx = hp; c = new Vector2(sf.x, Screen.height - Mathf.Lerp(sf.y, sh.y, G.centreFrac));
    }

    // ---------------- debug ----------------
    void DebugLines(HudCfg.Data H)
    {
        var dim = Ink(HudLayout.Get().game.fpsInk);
        if (fpsTxt == null || fpsTxtFor != fpsShown) { fpsTxtFor = fpsShown; fpsTxt = "FPS " + fpsShown.ToString("F0"); }
        HudDraw.Text(new Rect(Screen.width / 2 - HudLayout.Get().game.fpsDx, HudLayout.Get().game.fpsY, HudLayout.Get().game.fpsW, HudLayout.Get().game.fpsH), fpsTxt, "ui", H.debug.fpsLabelSize, TextAnchor.UpperLeft, false, dim);
        bool cam = H.debug.camLine || camArg;
        if (cam && pf && Camera.main)
        {
            var c = Camera.main.transform; var rel = c.position - pf.transform.position; var flat = new Vector3(c.forward.x, 0f, c.forward.z).normalized; float back = -Vector3.Dot(rel, flat);
            var ax = ef ? ef.transform.position - pf.transform.position : Vector3.forward; ax.y = 0f; ax.Normalize(); float side = Vector3.Dot(rel, Quaternion.Euler(0f, 90f, 0f) * ax);
            float pitch = Mathf.Asin(c.forward.y) * Mathf.Rad2Deg; var sp = Camera.main.WorldToScreenPoint(pf.transform.position + Vector3.up);
            HudDraw.Text(new Rect(Screen.width / 2 - HudLayout.Get().game.camDx, HudLayout.Get().game.camY, HudLayout.Get().game.camW, HudLayout.Get().game.camH), $"CAM h={rel.y:F2} back={back:F2} side={side:F2} pitch={pitch:F1} px={sp.x / Screen.width:F2}", "ui", H.debug.fpsLabelSize, TextAnchor.UpperLeft, false, dim);
        }
    }
}
