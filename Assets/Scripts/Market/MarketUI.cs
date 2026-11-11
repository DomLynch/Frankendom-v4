using System;
using UnityEngine;

// STALL v1 (lane laneSTALL): the camp trader stall. A DontDestroyOnLoad driver made at runtime (like LootUI); nothing in the scene.
//   1. Each frame: where is the player (GameLoop.Player), is he inside the camp rectangle (market.json stall.area), how far is the stall.
//   2. Inside the camp: MarketSafe.NoCombat is true (attack and guard presses are refused by the DuelFighter / PlayerBrain hunks), a SAFE tag and the coin count show.
//   3. Within stall.radius: the prompt "[ E / X ] SELL GEAR" shows over the stall; E / pad X (keys.interact) opens the panel. The panel freezes the duel (Duel.Frozen) like the equip screen.
//   4. The panel (ShopController in MarketCore.cs): up / down or the stick select a carried item, confirm (E / pad A) arms it, confirm again sells it for coins (LocalMarket -> LootSave, one file write).
// Keyboard and gamepad only (no mouse). Drawing: HudDraw / HudCfg (the gothic HUD kit), sizes in reference px x HudCfg.Scale(). Data: market.json (MarketCfg). Harness runs never start it.
// enabled=false (shipped) = this object is never created.
[DefaultExecutionOrder(-410)]   // before PlayerBrain (-200) and LootUI (-400): InReach and Blocking are set before the keys are read
public sealed class MarketUI : MonoBehaviour
{
    public static MarketUI Instance;
    static MenuLayoutCfg.MarketC ML { get { return MenuLayoutCfg.MK; } }   // UIDATAB v1: layout numbers of the stall UI: menu_layout.json (defaults = the old literals)
    readonly UiStr.Cat4 promptCat = new UiStr.Cat4(); readonly UiStr.IntFmt coinFmt = new UiStr.IntFmt(), priceFmt = new UiStr.IntFmt(); string statFor = null, statText = "";   // UIDATAB v1: cached strings (no per-frame concat / format / upper)
    public static bool Open { get; private set; }
    public static int ClosedFrame = -1;
    /// <summary>True while the panel is open (and on the frame it closed): GameLoopDriver must not read Enter / A / B then (LootUI.Blocking includes it).</summary>
    public static bool Blocking { get { return Open || ClosedFrame == Time.frameCount; } }
    /// <summary>True when the stall can be used this frame (PlayerBrain must not roll on E then).</summary>
    public static bool InReach { get; private set; }

    ShopController shop; LocalMarket market; LootMarketStore store;
    readonly MarketArea area = new MarketArea(); bool inArea, havePlayer; float stallX, stallZ, dist; Vector3 playerPos;
    MarketArea[] areas; float[] ax, az; MarketCfg.StallC stl; int lastPk = -1;   // MARKET2 v1: areas[0] = area (main stall); stl = the stall the player is in (the main one when in none)
    int openFrame, unfreezeFrame = -1; float openAt; bool ownFreeze;
    KeyCode[] kInteract, kConfirm, kClose, kUp, kDown; object keyOwner; int lastDy; float nextNav; bool axisBroken;
    MarketLayout lay; int layW, layH; MarketCfg.LayoutC layFor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Instance = null; Open = false; ClosedFrame = -1; InReach = false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            var C = MarketCfg.Get(); if (!C.enabled || Instance != null) return;
            if (GameLoop.HarnessOn) return;
            var go = new GameObject("MarketUI"); DontDestroyOnLoad(go); go.AddComponent<MarketUI>();
        }
        catch (Exception e) { Debug.LogWarning("[Market] boot failed: " + e.Message); }
    }

    void Awake() { Instance = this; }
    void OnDisable() { if (Open) CloseUi(); MarketSafe.NoCombat = false; InReach = false; if (Instance == this) Instance = null; }
    void OnEnable() { Instance = this; UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnLoaded; }
    void OnLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) { if (Open) { ownFreeze = false; unfreezeFrame = -1; CloseUi(); } area.Inside = false; ResetExtra(); MarketSafe.NoCombat = false; }

    void EnsureKeys(MarketCfg.Data C)
    {
        if (ReferenceEquals(keyOwner, C.keys)) return; keyOwner = C.keys;
        kInteract = MarketCfg.Keys(C.keys.interact); kConfirm = MarketCfg.Keys(C.keys.confirm); kClose = MarketCfg.Keys(C.keys.close); kUp = MarketCfg.Keys(C.keys.up); kDown = MarketCfg.Keys(C.keys.down);
        if (kClose.Length == 0) kClose = new[] { KeyCode.Escape };   // CAMPPLAY v1: the panel can always be closed
    }
    void EnsureShop()
    {
        if (shop != null) return;
        store = new LootMarketStore(); market = new LocalMarket(store, MarketCfg.Rules(), MarketCfg.Rows()); shop = new ShopController(market, store, MarketCfg.Rules());
    }

    void Update()
    {
        var C = MarketCfg.Get(); if (!C.enabled) { MarketSafe.NoCombat = false; InReach = false; return; }
        EnsureKeys(C);
        if (unfreezeFrame >= 0 && Time.frameCount >= unfreezeFrame) { if (ownFreeze) { Duel.Frozen = false; ownFreeze = false; } unfreezeFrame = -1; }

        var pl = GameLoop.Player; havePlayer = pl != null && pl.gameObject.activeInHierarchy;
        if (!havePlayer) { InReach = false; MarketSafe.NoCombat = false; area.Inside = false; ResetExtra(); inArea = false; if (Open) CloseUi(); return; }
        playerPos = pl.transform.position;
        var A = C.stall.area; area.zMin = A.zMin; area.zMax = A.zMax; area.xHalf = A.xHalf; area.enterPad = A.enterPad; area.leavePad = A.leavePad;
        inArea = PickStall(C) >= 0;   // MARKET2 v1: with stalls[] empty this is exactly area.Update on the main rectangle
        MarketSafe.NoCombat = C.safe.noCombat && inArea && !TraderKnows.AmbushLive;   // S9
        MarketWorld.StallXZ(stl, out stallX, out stallZ); dist = MarketArea.Dist(playerPos.x, playerPos.z, stallX, stallZ);

        InReach = inArea && !Open && dist <= stl.radius && !LootScreen.Open && !RTFinisher.Busy && !GameLoopDriver.Transitioning && !Duel.Frozen && Time.frameCount != LootScreen.ClosedFrame;
        if (InReach && (MarketCfg.AnyDown(kInteract) || TraderKnows.ProofPress(stl.id))) { if (!TraderKnows.Intercept(stl.id)) OpenUi(); }   // S9: the impostor takes the press at its stall (dark: Intercept is false)
        else if (Open) TickOpen(C);
    }

    // MARKET2 v1: update the enter / leave state of every stall area, return the stall the player is in (-1 none), set stl, and swap the Buy stock when the stall changed.
    int PickStall(MarketCfg.Data C)
    {
        int nx = C.stalls.Length;
        if (nx == 0) { stl = C.stall; return area.Update(playerPos.x, playerPos.z) ? 0 : -1; }   // the old single-stall path, unchanged
        if (areas == null || areas.Length != nx + 1) { areas = new MarketArea[nx + 1]; areas[0] = area; for (int i = 1; i <= nx; i++) areas[i] = new MarketArea(); ax = new float[nx + 1]; az = new float[nx + 1]; lastPk = -1; }
        MarketWorld.StallXZ(C.stall, out ax[0], out az[0]);
        for (int i = 1; i <= nx; i++) { var q = C.stalls[i - 1]; var a2 = areas[i]; a2.zMin = q.area.zMin; a2.zMax = q.area.zMax; a2.xHalf = q.area.xHalf; a2.enterPad = q.area.enterPad; a2.leavePad = q.area.leavePad; MarketWorld.StallXZ(q, out ax[i], out az[i]); }
        int pk = MarketStallPick.Update(areas, ax, az, playerPos.x, playerPos.z);
        stl = pk > 0 ? C.stalls[pk - 1] : C.stall;
        if (pk != lastPk) { lastPk = pk; if (pk >= 0) HubTrade.UseStock(pk > 0 ? stl.items : null); }
        return pk;
    }
    void ResetExtra() { if (areas != null) for (int i = 1; i < areas.Length; i++) areas[i].Inside = false; lastPk = -1; }   // MARKET2 v1

    void OpenUi()
    {
        EnsureShop(); if (!LootSave.Applied) LootSave.Apply();   // what is worn must be on the wearer before the list is built (same as the equip screen)
        TraderKnows.OnOpen(stl.id); /* S9: greeting + sell-back line, before the stock is built */ shop.Open(); Open = true; openFrame = Time.frameCount; openAt = Time.unscaledTime; unfreezeFrame = -1; lastDy = 0; InReach = false;
        if (!Duel.Frozen) { Duel.Frozen = true; ownFreeze = true; }
    }
    void CloseUi()
    {
        if (!Open) return; Open = false; ClosedFrame = Time.frameCount; if (shop != null) shop.Close();
        if (ownFreeze) unfreezeFrame = Time.frameCount + 1;   // released next frame: the key that closed the panel must not reach PlayerBrain
    }

    void TickOpen(MarketCfg.Data C)
    {
        if (Time.frameCount == openFrame) return;   // the press that opened the panel is not a confirm
        if (MarketCfg.AnyDown(kClose)) { if (shop.Back()) CloseUi(); return; }
        float now = Time.unscaledTime; int dy = 0;
        for (int i = 0; i < kUp.Length; i++) if (Input.GetKey(kUp[i])) dy = -1;
        for (int i = 0; i < kDown.Length; i++) if (Input.GetKey(kDown[i])) dy = 1;
        if (dy == 0 && !axisBroken && C.keys.axisY.Length > 0)
        {
            try { float ay = Input.GetAxisRaw(C.keys.axisY); if (ay > C.keys.stickThreshold) dy = -1; else if (ay < -C.keys.stickThreshold) dy = 1; }
            catch (Exception) { axisBroken = true; Debug.LogWarning("[Market] input axis '" + C.keys.axisY + "' is not defined; keys only."); }
        }
        int rows = lay != null ? lay.visibleRows : ML.panel.fallbackRows;
        if (dy != lastDy) { lastDy = dy; nextNav = now + C.keys.repeatDelayS; if (dy != 0) shop.Move(dy, rows); }
        else if (dy != 0 && now >= nextNav) { nextNav = now + C.keys.repeatS; shop.Move(dy, rows); }
        if (HubTrade.TabPressed()) { shop.ToggleTab(); return; }   // laneNPCSPAWN: Buy / Sell tab
        if (MarketCfg.AnyDown(kConfirm))
        {
            var r = shop.Confirm();
            if (r != null && r.ok && !r.replayed) { MarketHooks.RaiseSold(r.item, r.coinsDelta); if (C.debugLog) Debug.Log("[Market] sold " + r.item + " for " + r.coinsDelta + ", coins " + r.coins); }
            else if (r != null && C.debugLog) Debug.Log("[Market] order refused: " + r.code);
        }
    }

    // ---------------- drawing ----------------
    void OnGUI()
    {
        var C = MarketCfg.Get(); if (!C.enabled || !havePlayer) return;
        if (Event.current.type != EventType.Repaint) return;
        GUI.depth = C.guiDepth; float s = HudCfg.Scale(); var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f)); float vw = Screen.width / s, vh = Screen.height / s;
        var H = HudCfg.Get();
        HudDraw.Alpha = 1f;
        if (!Open) { DrawCampTag(C, H, vw); DrawPrompt(C, H, vw, vh, s); DrawRefusal(C, H, vw, vh); }
        else DrawPanel(C, H, vw, vh);
        HudDraw.Alpha = 1f; GUI.matrix = m;
    }

    void DrawCampTag(MarketCfg.Data C, HudCfg.Data H, float vw)
    {
        if (!inArea) return; var S = C.safe; var T = C.text; Color gold = HudCfg.Col(H.colors.gold), dim = HudCfg.Col(H.colors.dim);
        float y = S.tagTop; HudDraw.Caps(vw * 0.5f, y, 0.5f, T.safeTag, "title", S.tagBig, S.tagSmall, S.tagTracking, gold);
        y += S.tagBig * ML.tag.bigK; HudDraw.Text(new Rect(0f, y, vw, S.subSize * ML.tag.subK), T.safeSub, "serif", S.subSize, TextAnchor.UpperCenter, false, dim);
        y += S.subSize * ML.tag.subK + S.coinDy; HudDraw.Text(new Rect(0f, y, vw, S.coinSize * ML.tag.coinK), coinFmt.Get(T.coins, LootSave.Coins), "serif", S.coinSize, TextAnchor.UpperCenter, true, gold);
    }

    void DrawPrompt(MarketCfg.Data C, HudCfg.Data H, float vw, float vh, float s)
    {
        if (!inArea) return; var cam = Camera.main; if (cam == null) return;   // STALLS vend v2: a label over EVERY stall of this camp
        int n = C.stalls.Length;
        for (int i = 0; i <= n; i++)   // 0 = the main stall, i > 0 = stalls[i - 1]
        {
            var q = i == 0 ? C.stall : C.stalls[i - 1];
            if (n > 0 && (areas == null || i >= areas.Length || !areas[i].Inside)) continue;   // only the stalls of the camp the player stands in
            float qx, qz; MarketWorld.StallXZ(q, out qx, out qz);
            float qd = MarketArea.Dist(playerPos.x, playerPos.z, qx, qz); if (qd > q.promptRadius) continue;
            DrawStallLabel(C, H, cam, s, q, qx, qz, qd, ReferenceEquals(q, stl));
        }
    }

    void DrawStallLabel(MarketCfg.Data C, HudCfg.Data H, Camera cam, float s, MarketCfg.StallC q, float qx, float qz, float qd, bool pick)
    {
        var sp = cam.WorldToScreenPoint(new Vector3(qx, playerPos.y + q.promptHeight, qz)); if (sp.z <= 0f) return;
        float x = sp.x / s, y = (Screen.height - sp.y) / s, a = Mathf.Clamp01(1f - (qd - q.radius) / Mathf.Max(ML.prompt.minSpan, q.promptRadius - q.radius) * ML.prompt.fadeK);
        var L = C.layout; var T = C.text; bool reach = pick && qd <= q.radius; HudDraw.Alpha = a;
        HudDraw.Caps(x, y - L.labelNameBig * ML.prompt.liftK, 0.5f, q.title, "title", L.labelNameBig, L.labelNameSmall, L.labelTracking, HudCfg.Col(H.colors.ink));
        HudDraw.Fill(new Rect(x - ML.prompt.ruleHalf, y + ML.prompt.ruleDy, ML.prompt.ruleW, ML.prompt.ruleH), HudCfg.Col(H.colors.frame, ML.prompt.ruleAlpha));
        if (pick) HudDraw.Text(new Rect(x - ML.prompt.halfW, y + L.promptGap, ML.prompt.boxW, L.promptSize * ML.prompt.boxK), promptCat.Get(ML.prompt.open, T.promptKeys, ML.prompt.close, T.prompt, true), "serif", L.promptSize, TextAnchor.UpperCenter, true, reach ? HudCfg.Col(H.colors.gold) : HudCfg.Col(H.colors.dim));
        HudDraw.Alpha = 1f;
    }

    void DrawRefusal(MarketCfg.Data C, HudCfg.Data H, float vw, float vh)
    {
        float age = Time.unscaledTime - MarketSafe.RefusedAt, T = C.safe.refuseSeconds; if (age < 0f || age > T) return;
        HudDraw.Alpha = Mathf.Clamp01(Mathf.Min(age / ML.refusal.fadeInS, (T - age) / ML.refusal.fadeOutS));
        HudDraw.Text(new Rect(0f, vh - C.layout.toastBottom, vw, C.layout.toastSize * ML.refusal.boxK), C.text.noFight, "serif", C.layout.toastSize, TextAnchor.UpperCenter, true, HudCfg.Col(H.colors.ink)); HudDraw.Alpha = 1f;
    }

    MarketLayout Layout(MarketCfg.Data C, HudCfg.Data H)
    {
        var L = C.layout; if (lay != null && layW == Screen.width && layH == Screen.height && ReferenceEquals(layFor, L)) return lay;
        lay = MarketLayout.Compute(Screen.width, Screen.height, H.referenceHeight, H.minScale, H.maxScale, L.panelW, L.panelH, L.margin, L.pad, L.headerH, L.footerH, L.detailH, L.rowH, L.rowGap); layW = Screen.width; layH = Screen.height; layFor = L; return lay;
    }
    static Rect R(MRect r) { return new Rect(r.x, r.y, r.w, r.h); }
    static void FrameRect(Rect r, Color c, float t) { HudDraw.Fill(new Rect(r.x, r.y, r.width, t), c); HudDraw.Fill(new Rect(r.x, r.yMax - t, r.width, t), c); HudDraw.Fill(new Rect(r.x, r.y + t, t, r.height - 2f * t), c); HudDraw.Fill(new Rect(r.xMax - t, r.y + t, t, r.height - 2f * t), c); }

    string StatText(string id, bool has, GearItem gi)   // UIDATAB v1: the stat line of the detail strip, cached per item id
    {
        if (statFor == id) return statText;
        string st = ""; if (has && gi.stats != null) for (int q = 0; q < gi.stats.Length; q++) { var row = LootCfg.StatRow(gi.stats[q].stat); string lb = row != null && row.label.Length > 0 ? row.label : LootCfg.Pretty(gi.stats[q].stat); st += (st.Length > 0 ? ML.detail.statSep : "") + lb + " " + gi.stats[q].value.ToString(ML.detail.statFmt, System.Globalization.CultureInfo.InvariantCulture); }
        statFor = id; statText = st; return st;
    }

    void DrawPanel(MarketCfg.Data C, HudCfg.Data H, float vw, float vh)
    {
        var L = C.layout; var T = C.text; var K = H.colors; var lay = Layout(C, H);
        Color ink = HudCfg.Col(K.ink), dim = HudCfg.Col(K.dim), gold = HudCfg.Col(K.gold), frame = HudCfg.Col(K.frame), frameDim = HudCfg.Col(K.frameDim), crimson = HudCfg.Col(K.crimson);
        HudDraw.Alpha = Mathf.Clamp01((Time.unscaledTime - openAt) / Mathf.Max(0.01f, L.openFadeS));
        HudDraw.Fill(new Rect(0f, 0f, vw, vh), MarketCfg.Col(C.colors.dimmer));
        Rect p = R(lay.panel); HudDraw.Fill(p, MarketCfg.Col(C.colors.panel)); FrameRect(p, frame, L.frameW); FrameRect(new Rect(p.x + ML.panel.insetPx, p.y + ML.panel.insetPx, p.width - ML.panel.insetTrim, p.height - ML.panel.insetTrim), frameDim, ML.panel.innerFrameW);
        HudDraw.Fill(new Rect(p.x, p.y, L.accentBar, p.height), crimson);
        // header: title (small caps, tracked), subtitle, a rule with a diamond at each end, the coin count on the right
        Rect h = R(lay.header);
        HudDraw.Caps(h.x, h.y, 0f, stl.title.Length > 0 ? stl.title : T.title, "title", L.titleBig, L.titleSmall, L.titleTracking, ink);
        HudDraw.Text(new Rect(h.x + ML.panel.subDx, h.y + L.titleBig * ML.panel.titleK, h.width * ML.panel.subW, L.subSize * ML.panel.subK), MarketBuyRules.Cur.Subtitle(shop.BuyTab, T.subtitle), "serif", L.subSize, TextAnchor.UpperLeft, false, dim, true);
        HudDraw.Text(new Rect(h.x, h.y + ML.panel.coinDy, h.width, L.coinSize * ML.panel.coinK), coinFmt.Get(T.coins, shop.Stock.coins), "serif", L.coinSize, TextAnchor.UpperRight, true, gold);
        float ry = h.yMax - L.ruleH - ML.panel.ruleBottom; HudDraw.Fill(new Rect(h.x, ry, h.width, L.ruleH), frame);
        var dia = HudDraw.Diamond(); HudDraw.Tex(new Rect(h.x - L.diamond * 0.5f, ry + L.ruleH * 0.5f - L.diamond * 0.5f, L.diamond, L.diamond), dia, gold); HudDraw.Tex(new Rect(h.xMax - L.diamond * 0.5f, ry + L.ruleH * 0.5f - L.diamond * 0.5f, L.diamond, L.diamond), dia, gold);
        // rows
        var lines = shop.Stock.lines; Rect list = R(lay.list);
        if (lines.Count == 0) HudDraw.Text(new Rect(list.x, list.y + ML.panel.nothingDy, list.width, L.rowNameSize * ML.panel.nothingK), T.nothing, "serif", L.rowNameSize, TextAnchor.UpperCenter, false, dim, true);
        int vis = lay.visibleRows;
        for (int v = 0; v < vis; v++)
        {
            int i = shop.Scroll + v; if (i >= lines.Count) break; var ln = lines[i]; Rect r = R(lay.Row(v)); bool sel = i == shop.Index, armed = sel && shop.Armed == ln.id;
            if (sel) { HudDraw.Fill(r, MarketCfg.Col(armed ? C.colors.rowArmed : C.colors.rowSelected)); HudDraw.Fill(new Rect(r.x, r.y, ML.panel.accentW, r.height), armed ? gold : crimson); FrameRect(r, armed ? frame : frameDim, ML.panel.rowFrameW); }
            Color nameCol = ln.worn ? dim : ink;
            HudDraw.Text(new Rect(r.x + L.rowPadX, r.y, r.width * ML.panel.nameW, r.height), ln.name, "serif", L.rowNameSize, TextAnchor.MiddleLeft, true, nameCol);
            if (ln.worn || ln.stack /* POTIONS v1: the "x3" tag */) HudDraw.Text(new Rect(r.x + r.width * ML.panel.tagX, r.y, r.width * ML.panel.tagW, r.height), UiStr.Up(ln.tag.Length > 0 ? ln.tag : T.worn), "serif", L.rowTagSize, TextAnchor.MiddleLeft, false, dim);
            HudDraw.Text(new Rect(r.x, r.y, r.width - L.rowPadX, r.height), priceFmt.Get(T.price, ln.price), "serif", L.rowPriceSize, TextAnchor.MiddleRight, true, ln.worn ? dim : gold);
        }
        if (lines.Count > vis)   // thin scroll track on the right edge of the list
        {
            float th = list.height, thumb = Mathf.Max(ML.panel.thumbMin, th * vis / lines.Count), ty = list.y + (th - thumb) * (shop.Scroll / (float)Mathf.Max(1, lines.Count - vis));
            HudDraw.Fill(new Rect(list.xMax + ML.panel.trackDx, list.y, ML.panel.trackW, th), frameDim); HudDraw.Fill(new Rect(list.xMax + ML.panel.trackDx, ty, ML.panel.trackW, thumb), gold);
        }
        // detail strip: stats of the selected item + what the trader pays, then the last message
        Rect d = R(lay.detail); HudDraw.Fill(new Rect(d.x, d.y, d.width, ML.detail.ruleH), frameDim); var sl = shop.Selected;
        if (sl != null)
        {
            GearItem gi; bool has = LootBridge.Catalog.TryGet(sl.id, out gi);
            HudDraw.Text(new Rect(d.x, d.y + ML.detail.dy, d.width * ML.detail.nameW, L.detailNameSize * ML.detail.nameK), sl.name, "serif", L.detailNameSize, TextAnchor.UpperLeft, true, has ? LootCfg.RarityColor(gi) : ink);
            string st = StatText(sl.id, has, gi);   // UIDATAB v1: built once per item (was rebuilt every frame)
            HudDraw.Text(new Rect(d.x, d.y + ML.detail.dy + L.detailNameSize * ML.detail.nameK, d.width, L.detailSize * ML.detail.textK), has ? st : PotionCfg.Describe(sl.id) /* POTIONS v1 */, "serif", L.detailSize, TextAnchor.UpperLeft, false, dim);
            HudDraw.Text(new Rect(d.x, d.y + ML.detail.dy, d.width, L.detailSize * ML.detail.textK), sl.worn ? (shop.BuyTab ? MarketCfg.Code(sl.why) : T.detailWorn) : string.Format(shop.BuyTab && MarketBuyRules.Cur.detailAsks.Length > 0 ? MarketBuyRules.Cur.detailAsks : T.detailPays, sl.price), "serif", L.detailSize, TextAnchor.UpperRight, false, sl.worn ? dim : gold);
        }
        if (shop.NoteKey.Length > 0)
        {
            string msg = shop.NoteKey == "boughtStack" || shop.NoteKey == "soldStack" ? PotionCfg.NoteText(shop.NoteKey, shop.NoteItem, shop.NoteCoins) /* POTIONS v1 */ : shop.NoteKey == "bought" ? string.Format(MarketBuyRules.Cur.bought, shop.NoteItem, shop.NoteCoins) : shop.NoteKey == "sold" ? string.Format(T.sold, shop.NoteItem, shop.NoteCoins) : shop.NoteKey == "armed" ? string.Format(shop.BuyTab && MarketBuyRules.Cur.armedBuy.Length > 0 ? MarketBuyRules.Cur.armedBuy : T.armed, shop.NoteItem, shop.NoteCoins) : MarketCfg.Code(shop.NoteKey);
            HudDraw.Text(new Rect(d.x, d.yMax - L.messageSize * ML.detail.msgK, d.width, L.messageSize * ML.detail.msgK), msg, "serif", L.messageSize, TextAnchor.LowerLeft, true, MarketCfg.Col(shop.NoteGood ? C.colors.good : C.colors.bad));
        }
        else if (TraderKnows.Greeting.Length > 0) HudDraw.Text(new Rect(d.x, d.yMax - L.messageSize * ML.detail.msgK, d.width, L.messageSize * ML.detail.msgK), TraderKnows.Greeting, "serif", L.messageSize, TextAnchor.LowerLeft, true, dim);   // S9: the trader knows (shown in the note line when no note is up)
        // footer: the three hints
        Rect f = R(lay.footer); float w3 = f.width / 3f;
        HudDraw.Text(new Rect(f.x, f.y, w3, f.height), UiStr.Up(MarketBuyRules.Cur.HintMove(T.hintMove)), "serif", L.hintSize, TextAnchor.MiddleLeft, false, dim);
        HudDraw.Text(new Rect(f.x + w3, f.y, w3, f.height), UiStr.Up(MarketBuyRules.Cur.HintConfirm(shop.BuyTab, T.hintSell)), "serif", L.hintSize, TextAnchor.MiddleCenter, true, gold);
        HudDraw.Text(new Rect(f.x + 2f * w3, f.y, w3, f.height), UiStr.Up(T.hintClose), "serif", L.hintSize, TextAnchor.MiddleRight, false, dim);
    }
}
