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
    readonly CombatHudBars.Lag lagP = new CombatHudBars.Lag(), lagE = new CombatHudBars.Lag();
    float guardA; float[] cdMax = new float[2]; float[] readyT = new float[2]; bool[] wasReady = new bool[2];
    float lastNow;

    void Start()
    {
        startT = Time.unscaledTime;
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-fps" || a == "-gaps" || a == "-autoplay") showFps = true;
    }

    void Update()
    {
        var H = HudCfg.Get();
        if (toggleKeyName != H.hint.toggleKey) { toggleKeyName = H.hint.toggleKey; System.Enum.TryParse(toggleKeyName, true, out toggleKey); }
        if (H.hint.enabled && Input.GetKeyDown(toggleKey)) { hintPinned = true; hintOn = !hintOn; }
        if (Input.GetKeyDown(KeyCode.F3)) showFps = !showFps;
        fpsAcc += Time.unscaledDeltaTime; fpsN++; if (fpsAcc >= 0.5f) { fpsShown = fpsN / fpsAcc; fpsAcc = 0f; fpsN = 0; }
        if (Input.GetKeyDown(KeyCode.Escape)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
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
        float s = HudCfg.Scale(), vw = Screen.width / s, vh = Screen.height / s, now = Time.unscaledTime, dt = Mathf.Clamp(now - lastNow, 0f, 0.1f); lastNow = now;
        var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        HudDraw.Alpha = 1f - Mathf.Clamp01(GameLoop.ScreenAlpha) * Mathf.Clamp01(H.screens.hudFade);   // the end screen fades the HUD out
        try
        {
            DrawPlayer(H, vw, now, dt);
            DrawBoss(H, vw, now, dt);
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
        float nameH = P.nameSize * 1.35f;
        HudDraw.Caps(x, y, 0f, Roster.PlayerName, "title", P.nameSize, P.nameSmall, P.nameTracking, Ink());
        var hp = new Rect(x, y + nameH + P.nameGap, P.hp.w, P.hp.h);
        float f = player != null && player.max > 0f ? Mathf.Clamp01(player.current / player.max) : 0f;
        lagP.Step(f, now, dt);
        CombatHudBars.Bar(hp, P.hp.taper, f, lagP.v, HudCfg.Col(H.colors.hpPlayer), true, true);
        string cue = null; Color cc = Color.white; bool big = false;
        if (pf != null && !pf.Dead) CombatHudBars.Draw(   // kitV76 (Goose v75): no stamina / posture strips at 0 HP
hp.x, hp.yMax + H.bars.framePad + P.stamina.gap, 0f, pf, true, P.stamina, P.posture, P.showStamina, P.showPosture, out cue, out cc, out big);
        if (cue != null) HudDraw.Caps(hp.xMax, y + P.cueDy, 1f, cue, "title", P.cueSize, Mathf.Max(8, P.cueSize - 3), 2f, cc);
    }

    // ---------------- boss bar (top-centre) ----------------
    void DrawBoss(HudCfg.Data H, float vw, float now, float dt)
    {
        var Bc = H.boss; if (!Bc.enabled || (enemy == null && ef == null)) return;
        float maxW = vw - 2f * (H.player.x + H.player.hp.w + Bc.sideMargin);
        float w = Mathf.Max(300f, Mathf.Min(Bc.w, maxW)), x = (vw - w) * 0.5f, y = Bc.top;
        float nameH = Bc.nameSize * 1.35f;
        HudDraw.Caps(x, y, 0f, Roster.EnemyName, "title", Bc.nameSize, Bc.nameSmall, Bc.nameTracking, Ink());
        float ty = y + nameH;
        if (Bc.showTitle && GameLoop.LadderOn)
        {
            string title = GameLoop.CurrentRow().title;
            if (!string.IsNullOrEmpty(title)) { HudDraw.Text(new Rect(x, ty + Bc.titleGap, w, Bc.titleSize * 1.5f), title, "serif", Bc.titleSize, TextAnchor.UpperLeft, false, HudCfg.Col(H.colors.dim), true); ty += Bc.titleSize * 1.4f + Bc.titleGap; }
        }
        var hp = new Rect(x, ty + Bc.nameGap, w, Bc.hp.h);
        float f = enemy != null && enemy.max > 0f ? Mathf.Clamp01(enemy.current / enemy.max) : 0f;
        lagE.Step(f, now, dt);
        CombatHudBars.Bar(hp, Bc.hp.taper, f, lagE.v, HudCfg.Col(H.colors.hpEnemy), true, true);
        string cue = null; Color cc = Color.white; bool big = false; float after = hp.yMax + H.bars.framePad;
        if (ef != null && !ef.Dead) after = CombatHudBars.Draw(   // kitV76 (Goose v75): no stamina / posture strips at 0 HP
hp.center.x, hp.yMax + H.bars.framePad + Bc.posture.gap, 0.5f, ef, false, Bc.stamina, Bc.posture, Bc.showStamina, Bc.showPosture, out cue, out cc, out big);
        if (cue != null) HudDraw.Caps(hp.center.x, after + Bc.cueGap - 2f, 0.5f, cue, "title", Bc.cueSize, Mathf.Max(8, Bc.cueSize - 3), 3f, cc);
    }

    // ---------------- stance (bottom-left) ----------------
    void DrawStance(HudCfg.Data H, float vw, float vh)
    {
        var S = H.stance; if (!S.enabled || pf == null || !StanceLayer.Active) return;
        var row = StanceLayer.Row(StanceLayer.StanceOf(pf)); if (row == null) return;
        Color col = HudCfg.Col(row.color, 1f); Color dim = HudCfg.Col(H.colors.dim);
        float y = vh - S.bottom, x = S.x;
        HudDraw.Text(new Rect(x, y - S.captionSize * 1.5f, 200f, S.captionSize * 1.6f), S.caption, "serif", S.captionSize, TextAnchor.UpperLeft, false, dim);
        float d = S.diamond;
        HudDraw.Tex(new Rect(x - 1f, y + (S.nameSize * 1.1f - d) * 0.5f, d, d), HudDraw.Diamond(), col);
        float w = HudDraw.Caps(x + d + 8f, y, 0f, row.label, "title", S.nameSize, S.nameSmall, S.tracking, col);
        if (!string.IsNullOrEmpty(S.hint)) HudDraw.Text(new Rect(x + d + 16f + w, y + S.nameSize * 0.2f, 160f, S.hintSize * 1.6f), S.hint, "serif", S.hintSize, TextAnchor.UpperLeft, false, new Color(dim.r, dim.g, dim.b, dim.a * 0.8f));
        // switch toast: the new stance's name, lower centre, fades out
        string tt; Color tc; float ta;
        if (StanceLayer.StanceDriver.ToastState(out tt, out tc, out ta)) HudDraw.Caps(vw * 0.5f, vh * S.toastY, 0.5f, tt, "title", S.toastSize, S.toastSmall, S.tracking + 1.5f, new Color(tc.r, tc.g, tc.b, ta));
    }

    // ---------------- controls panel (F1) ----------------
    void DrawHint(HudCfg.Data H, float vw, float vh, float dt)
    {
        var K = H.hint; if (!K.enabled) return;
        hintA = Mathf.MoveTowards(hintA, hintOn ? 1f : 0f, dt / Mathf.Max(0.02f, K.fadeSeconds));
        Color dim = HudCfg.Col(H.colors.dim);
        // the small corner prompt, only for the first cornerSeconds of the fight and while the panel is closed
        if (!string.IsNullOrEmpty(K.cornerText) && hintA < 0.01f && !hintPinned && Time.unscaledTime - startT < K.cornerSeconds && H.stance.enabled)
        {
            float ca = K.cornerAlpha * Mathf.Clamp01((K.cornerSeconds - (Time.unscaledTime - startT)) / 1.5f);
            HudDraw.Text(new Rect(H.stance.x, vh - H.stance.bottom + H.stance.nameSize * 1.5f, 260f, K.cornerSize * 1.6f), K.cornerText, "serif", K.cornerSize, TextAnchor.UpperLeft, false, new Color(dim.r, dim.g, dim.b, ca));
        }
        if (hintA < 0.01f || K.rows == null || K.rows.Length == 0) return;
        int cols = Mathf.Max(1, K.columns), perCol = Mathf.CeilToInt(K.rows.Length / (float)cols);
        float w = cols * K.colWidth + 2f * K.pad, h = perCol * K.rowHeight + 2f * K.pad + K.titleSize * 1.7f + K.footerSize * 1.7f;
        var r = new Rect(K.margin, vh - K.bottom - h, w, h);
        float old = HudDraw.Alpha; HudDraw.Alpha = old * hintA;
        HudDraw.Fill(r, new Color(0.02f, 0.02f, 0.025f, K.panelAlpha));
        var fc = HudCfg.Col(H.colors.frame); HudDraw.Fill(new Rect(r.x, r.y, r.width, 1f), fc); HudDraw.Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), HudCfg.Col(H.colors.frameDim));
        HudDraw.Caps(r.x + K.pad, r.y + K.pad * 0.6f, 0f, K.title, "title", K.titleSize, K.titleSize - 3, 3f, Ink());
        float y0 = r.y + K.pad + K.titleSize * 1.7f; var gold = HudCfg.Col(H.colors.gold);
        for (int i = 0; i < K.rows.Length; i++)
        {
            int c = i / perCol, rr = i % perCol; var row = K.rows[i]; if (row == null) continue;
            float rx = r.x + K.pad + c * K.colWidth, ry = y0 + rr * K.rowHeight;
            HudDraw.Text(new Rect(rx, ry, K.keyWidth, K.rowHeight), row.k, "serif", K.fontSize, TextAnchor.UpperLeft, true, gold);
            HudDraw.Text(new Rect(rx + K.keyWidth, ry, K.colWidth - K.keyWidth, K.rowHeight), row.a, "serif", K.fontSize, TextAnchor.UpperLeft, false, Ink(0.9f));
        }
        HudDraw.Text(new Rect(r.x + K.pad, r.yMax - K.footerSize * 1.8f - 2f, r.width - 2f * K.pad, K.footerSize * 1.6f), K.footer, "serif", K.footerSize, TextAnchor.UpperLeft, false, dim, true);
        HudDraw.Alpha = old;
    }

    // ---------------- special-move cooldown icons (bottom-right) ----------------
    void DrawSpecials(HudCfg.Data H, float vw, float vh, float now)
    {
        var S = H.specials; if (!S.enabled || pf == null || S.items == null) return;
        int n = Mathf.Min(2, S.items.Length);
        float total = n * S.size + (n - 1) * S.gap, x0 = vw - S.right - total, y0 = vh - S.bottom - S.size;
        for (int i = 0; i < n; i++)
        {
            var it = S.items[i]; if (it == null) continue;
            int cd = i == 0 ? pf.special1Cd : pf.special2Cd;
            if (cd > cdMax[i]) cdMax[i] = cd;
            float tot = Mathf.Max(1f, S.cooldownTicks, cdMax[i]);
            bool ready = cd <= 0; if (ready && !wasReady[i]) readyT[i] = now; wasReady[i] = ready;
            if (ready) cdMax[i] = 0f;
            var r = new Rect(x0 + i * (S.size + S.gap), y0, S.size, S.size);
            Color readyCol = HudCfg.Col(H.colors.ready), coolCol = HudCfg.Col(H.colors.cooling);
            HudDraw.Tex(r, HudDraw.Disc(), new Color(0.025f, 0.025f, 0.03f, 0.86f));
            HudDraw.Tex(r, HudDraw.Ring(S.ringPx), ready ? readyCol : HudCfg.Col(H.colors.frameDim));
            float gs = S.size * S.glyphScale; var gr = new Rect(r.center.x - gs * 0.5f, r.center.y - gs * 0.5f, gs, gs);
            HudDraw.Tex(gr, HudDraw.Glyph(it.glyph), ready ? HudCfg.Col(H.colors.ink, 0.95f) : new Color(coolCol.r, coolCol.g, coolCol.b, 0.75f));
            if (!ready)
            {
                var pie = HudDraw.Pie(cd / tot, S.pieSteps); if (pie != null) HudDraw.Tex(r, pie, new Color(0f, 0f, 0f, 0.62f));
                HudDraw.Text(r, Mathf.CeilToInt(cd / Mathf.Max(1f, S.ticksPerSecond)).ToString(), "title", S.cdSize, TextAnchor.MiddleCenter, true, HudCfg.Col(H.colors.ink, 0.95f));
            }
            else
            {
                float k = 1f - (now - readyT[i]) / Mathf.Max(0.05f, S.readyFlashS);
                if (k > 0f) { float g = S.size * (1.1f + 0.35f * (1f - k)); HudDraw.Tex(new Rect(r.center.x - g * 0.5f, r.center.y - g * 0.5f, g, g), HudDraw.Ring(S.ringPx), new Color(readyCol.r, readyCol.g, readyCol.b, k)); }
            }
            string label = string.IsNullOrEmpty(it.label) ? it.key : it.key + "  " + it.label;
            HudDraw.Text(new Rect(r.x - 20f, r.yMax + S.keyDy, S.size + 40f, S.keySize * 1.6f), label, "serif", S.keySize, TextAnchor.UpperCenter, false, ready ? HudCfg.Col(H.colors.ink, 0.85f) : HudCfg.Col(H.colors.dim));
        }
    }

    // ---------------- guard direction (around the crosshair, only while guarding) ----------------
    void DrawGuardDir(HudCfg.Data H, float vw, float vh, float dt, float now)
    {
        var G = H.guardDir; if (!G.enabled || pf == null) return;
        bool up = !pf.Dead && ControlsTable.SideRequired && (pf.state == FState.Guard || pf.guardHeld);
        guardA = Mathf.MoveTowards(guardA, up ? 1f : 0f, dt / Mathf.Max(0.02f, up ? G.fadeInS : G.fadeOutS));
        if (guardA < 0.01f) return;
        var sides = ControlsTable.Get().guard.sides; if (sides == null) return;
        Vector2 c = new Vector2(vw * 0.5f, vh * 0.5f);
        bool parry = pf.ParryWindowLeft > 0;
        Color live = parry ? HudCfg.Col(H.colors.parry) : HudCfg.Col(H.colors.guard);
        float grow = parry ? 1f + G.parryGrow * (0.5f + 0.5f * Mathf.Sin(now * Mathf.PI * 2f * G.parryPulseHz)) : 1f;
        float R = G.radius, half = R / HudDraw.ArcCentre; var arc = HudDraw.Arc(G.arcDeg, G.thickness);
        float oa = HudDraw.Alpha;
        // faint full ring so the three sides read as one instrument
        HudDraw.Tex(new Rect(c.x - R, c.y - R, 2f * R, 2f * R), HudDraw.Ring(0.5f), new Color(live.r, live.g, live.b, G.ringAlpha * guardA));
        var saved = GUI.matrix;
        for (int i = 0; i < sides.Length; i++)
        {
            var sd = sides[i]; if (sd == null) continue;
            bool active = pf.guardSide == sd.id;
            float hs = active ? half * grow : half; Color col = active ? live : HudCfg.Col(H.colors.guard);
            float a = (active ? 1f : G.idleAlpha) * guardA;
            GUIUtility.RotateAroundPivot(sd.deg, c);
            HudDraw.Tex(new Rect(c.x - hs, c.y - hs, 2f * hs, 2f * hs), arc, new Color(col.r, col.g, col.b, a));
            GUI.matrix = saved;
        }
        // centre guard (no side): the small diamond is lit
        bool centre = pf.guardSide == 0; float ds = G.dotSize * (centre ? 1.25f : 1f);
        HudDraw.Tex(new Rect(c.x - ds * 0.5f, c.y - ds * 0.5f, ds, ds), HudDraw.Diamond(), new Color(live.r, live.g, live.b, (centre ? 1f : G.idleAlpha) * guardA));
        HudDraw.Alpha = oa;
    }

    // ---------------- debug ----------------
    void DebugLines(HudCfg.Data H)
    {
        var dim = Ink(0.85f);
        HudDraw.Text(new Rect(Screen.width / 2 - 60, 8, 140, 24), "FPS " + fpsShown.ToString("F0"), "ui", H.debug.fpsLabelSize, TextAnchor.UpperLeft, false, dim);
        bool cam = H.debug.camLine; foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-camdebug") cam = true;
        if (cam && pf && Camera.main)
        {
            var c = Camera.main.transform; var rel = c.position - pf.transform.position; var flat = new Vector3(c.forward.x, 0f, c.forward.z).normalized; float back = -Vector3.Dot(rel, flat);
            var ax = ef ? ef.transform.position - pf.transform.position : Vector3.forward; ax.y = 0f; ax.Normalize(); float side = Vector3.Dot(rel, Quaternion.Euler(0f, 90f, 0f) * ax);
            float pitch = Mathf.Asin(c.forward.y) * Mathf.Rad2Deg; var sp = Camera.main.WorldToScreenPoint(pf.transform.position + Vector3.up);
            HudDraw.Text(new Rect(Screen.width / 2 - 200, 28, 520, 24), $"CAM h={rel.y:F2} back={back:F2} side={side:F2} pitch={pitch:F1} px={sp.x / Screen.width:F2}", "ui", H.debug.fpsLabelSize, TextAnchor.UpperLeft, false, dim);
        }
    }
}
