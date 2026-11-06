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
    public static bool Open { get; private set; }
    public static int ClosedFrame = -1;
    /// <summary>True while the panel is open (and on the frame it closed): GameLoopDriver must not read Enter / A / B then (LootUI.Blocking includes it).</summary>
    public static bool Blocking { get { return Open || ClosedFrame == Time.frameCount; } }
    /// <summary>True when the stall can be used this frame (PlayerBrain must not roll on E then).</summary>
    public static bool InReach { get; private set; }

    ShopController shop; LocalMarket market; LootMarketStore store;
    readonly MarketArea area = new MarketArea(); bool inArea, havePlayer; float stallX, stallZ, dist; Vector3 playerPos;
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
    void OnLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) { if (Open) { ownFreeze = false; unfreezeFrame = -1; CloseUi(); } area.Inside = false; MarketSafe.NoCombat = false; }

    void EnsureKeys(MarketCfg.Data C)
    {
        if (ReferenceEquals(keyOwner, C.keys)) return; keyOwner = C.keys;
        kInteract = MarketCfg.Keys(C.keys.interact); kConfirm = MarketCfg.Keys(C.keys.confirm); kClose = MarketCfg.Keys(C.keys.close); kUp = MarketCfg.Keys(C.keys.up); kDown = MarketCfg.Keys(C.keys.down);
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
        if (!havePlayer) { InReach = false; MarketSafe.NoCombat = false; area.Inside = false; inArea = false; if (Open) CloseUi(); return; }
        playerPos = pl.transform.position;
        var A = C.stall.area; area.zMin = A.zMin; area.zMax = A.zMax; area.xHalf = A.xHalf; area.enterPad = A.enterPad; area.leavePad = A.leavePad;
        inArea = area.Update(playerPos.x, playerPos.z);
        MarketSafe.NoCombat = C.safe.noCombat && inArea;
        MarketWorld.StallXZ(C.stall, out stallX, out stallZ); dist = MarketArea.Dist(playerPos.x, playerPos.z, stallX, stallZ);

        InReach = inArea && !Open && dist <= C.stall.radius && !LootScreen.Open && !RTFinisher.Busy && !GameLoopDriver.Transitioning && !Duel.Frozen && Time.frameCount != LootScreen.ClosedFrame;
        if (InReach && MarketCfg.AnyDown(kInteract)) OpenUi();
        else if (Open) TickOpen(C);
    }

    void OpenUi()
    {
        EnsureShop(); if (!LootSave.Applied) LootSave.Apply();   // what is worn must be on the wearer before the list is built (same as the equip screen)
        shop.Open(); Open = true; openFrame = Time.frameCount; openAt = Time.unscaledTime; unfreezeFrame = -1; lastDy = 0; InReach = false;
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
        int rows = lay != null ? lay.visibleRows : 8;
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
        y += S.tagBig * 1.5f; HudDraw.Text(new Rect(0f, y, vw, S.subSize * 1.5f), T.safeSub, "serif", S.subSize, TextAnchor.UpperCenter, false, dim);
        y += S.subSize * 1.5f + S.coinDy; HudDraw.Text(new Rect(0f, y, vw, S.coinSize * 1.5f), string.Format(T.coins, LootSave.Coins), "serif", S.coinSize, TextAnchor.UpperCenter, true, gold);
    }

    void DrawPrompt(MarketCfg.Data C, HudCfg.Data H, float vw, float vh, float s)
    {
        if (!inArea || dist > C.stall.promptRadius) return; var cam = Camera.main; if (cam == null) return;
        var sp = cam.WorldToScreenPoint(new Vector3(stallX, playerPos.y + C.stall.promptHeight, stallZ)); if (sp.z <= 0f) return;
        float x = sp.x / s, y = (Screen.height - sp.y) / s, a = Mathf.Clamp01(1f - (dist - C.stall.radius) / Mathf.Max(0.1f, C.stall.promptRadius - C.stall.radius) * 0.5f);
        var L = C.layout; var T = C.text; bool reach = dist <= C.stall.radius; HudDraw.Alpha = a;
        HudDraw.Caps(x, y - L.labelNameBig * 1.6f, 0.5f, C.stall.title, "title", L.labelNameBig, L.labelNameSmall, L.labelTracking, HudCfg.Col(H.colors.ink));
        HudDraw.Fill(new Rect(x - 60f, y + 2f, 120f, 1f), HudCfg.Col(H.colors.frame, 0.75f));
        HudDraw.Text(new Rect(x - 300f, y + L.promptGap, 600f, L.promptSize * 1.6f), "[ " + T.promptKeys + " ]   " + T.prompt.ToUpperInvariant(), "serif", L.promptSize, TextAnchor.UpperCenter, true, reach ? HudCfg.Col(H.colors.gold) : HudCfg.Col(H.colors.dim));
        HudDraw.Alpha = 1f;
    }

    void DrawRefusal(MarketCfg.Data C, HudCfg.Data H, float vw, float vh)
    {
        float age = Time.unscaledTime - MarketSafe.RefusedAt, T = C.safe.refuseSeconds; if (age < 0f || age > T) return;
        HudDraw.Alpha = Mathf.Clamp01(Mathf.Min(age / 0.12f, (T - age) / 0.35f));
        HudDraw.Text(new Rect(0f, vh - C.layout.toastBottom, vw, C.layout.toastSize * 1.5f), C.text.noFight, "serif", C.layout.toastSize, TextAnchor.UpperCenter, true, HudCfg.Col(H.colors.ink)); HudDraw.Alpha = 1f;
    }

    MarketLayout Layout(MarketCfg.Data C, HudCfg.Data H)
    {
        var L = C.layout; if (lay != null && layW == Screen.width && layH == Screen.height && ReferenceEquals(layFor, L)) return lay;
        lay = MarketLayout.Compute(Screen.width, Screen.height, H.referenceHeight, H.minScale, H.maxScale, L.panelW, L.panelH, L.margin, L.pad, L.headerH, L.footerH, L.detailH, L.rowH, L.rowGap); layW = Screen.width; layH = Screen.height; layFor = L; return lay;
    }
    static Rect R(MRect r) { return new Rect(r.x, r.y, r.w, r.h); }
    static void FrameRect(Rect r, Color c, float t) { HudDraw.Fill(new Rect(r.x, r.y, r.width, t), c); HudDraw.Fill(new Rect(r.x, r.yMax - t, r.width, t), c); HudDraw.Fill(new Rect(r.x, r.y + t, t, r.height - 2f * t), c); HudDraw.Fill(new Rect(r.xMax - t, r.y + t, t, r.height - 2f * t), c); }

    void DrawPanel(MarketCfg.Data C, HudCfg.Data H, float vw, float vh)
    {
        var L = C.layout; var T = C.text; var K = H.colors; var lay = Layout(C, H);
        Color ink = HudCfg.Col(K.ink), dim = HudCfg.Col(K.dim), gold = HudCfg.Col(K.gold), frame = HudCfg.Col(K.frame), frameDim = HudCfg.Col(K.frameDim), crimson = HudCfg.Col(K.crimson);
        HudDraw.Alpha = Mathf.Clamp01((Time.unscaledTime - openAt) / Mathf.Max(0.01f, L.openFadeS));
        HudDraw.Fill(new Rect(0f, 0f, vw, vh), MarketCfg.Col(C.colors.dimmer));
        Rect p = R(lay.panel); HudDraw.Fill(p, MarketCfg.Col(C.colors.panel)); FrameRect(p, frame, L.frameW); FrameRect(new Rect(p.x + 6f, p.y + 6f, p.width - 12f, p.height - 12f), frameDim, 1f);
        HudDraw.Fill(new Rect(p.x, p.y, L.accentBar, p.height), crimson);
        // header: title (small caps, tracked), subtitle, a rule with a diamond at each end, the coin count on the right
        Rect h = R(lay.header);
        HudDraw.Caps(h.x, h.y, 0f, C.stall.title.Length > 0 ? C.stall.title : T.title, "title", L.titleBig, L.titleSmall, L.titleTracking, ink);
        HudDraw.Text(new Rect(h.x + 2f, h.y + L.titleBig * 1.35f, h.width * 0.7f, L.subSize * 1.5f), MarketBuyRules.Cur.Subtitle(shop.BuyTab, T.subtitle), "serif", L.subSize, TextAnchor.UpperLeft, false, dim, true);
        HudDraw.Text(new Rect(h.x, h.y + 4f, h.width, L.coinSize * 1.5f), string.Format(T.coins, shop.Stock.coins), "serif", L.coinSize, TextAnchor.UpperRight, true, gold);
        float ry = h.yMax - L.ruleH - 4f; HudDraw.Fill(new Rect(h.x, ry, h.width, L.ruleH), frame);
        var dia = HudDraw.Diamond(); HudDraw.Tex(new Rect(h.x - L.diamond * 0.5f, ry + L.ruleH * 0.5f - L.diamond * 0.5f, L.diamond, L.diamond), dia, gold); HudDraw.Tex(new Rect(h.xMax - L.diamond * 0.5f, ry + L.ruleH * 0.5f - L.diamond * 0.5f, L.diamond, L.diamond), dia, gold);
        // rows
        var lines = shop.Stock.lines; Rect list = R(lay.list);
        if (lines.Count == 0) HudDraw.Text(new Rect(list.x, list.y + 20f, list.width, L.rowNameSize * 1.6f), T.nothing, "serif", L.rowNameSize, TextAnchor.UpperCenter, false, dim, true);
        int vis = lay.visibleRows;
        for (int v = 0; v < vis; v++)
        {
            int i = shop.Scroll + v; if (i >= lines.Count) break; var ln = lines[i]; Rect r = R(lay.Row(v)); bool sel = i == shop.Index, armed = sel && shop.Armed == ln.id;
            if (sel) { HudDraw.Fill(r, MarketCfg.Col(armed ? C.colors.rowArmed : C.colors.rowSelected)); HudDraw.Fill(new Rect(r.x, r.y, 3f, r.height), armed ? gold : crimson); FrameRect(r, armed ? frame : frameDim, 1f); }
            Color nameCol = ln.worn ? dim : ink;
            HudDraw.Text(new Rect(r.x + L.rowPadX, r.y, r.width * 0.6f, r.height), ln.name, "serif", L.rowNameSize, TextAnchor.MiddleLeft, true, nameCol);
            if (ln.worn) HudDraw.Text(new Rect(r.x + r.width * 0.55f, r.y, r.width * 0.2f, r.height), (ln.tag.Length > 0 ? ln.tag : T.worn).ToUpperInvariant(), "serif", L.rowTagSize, TextAnchor.MiddleLeft, false, dim);
            HudDraw.Text(new Rect(r.x, r.y, r.width - L.rowPadX, r.height), string.Format(T.price, ln.price), "serif", L.rowPriceSize, TextAnchor.MiddleRight, true, ln.worn ? dim : gold);
        }
        if (lines.Count > vis)   // thin scroll track on the right edge of the list
        {
            float th = list.height, thumb = Mathf.Max(18f, th * vis / lines.Count), ty = list.y + (th - thumb) * (shop.Scroll / (float)Mathf.Max(1, lines.Count - vis));
            HudDraw.Fill(new Rect(list.xMax + 6f, list.y, 2f, th), frameDim); HudDraw.Fill(new Rect(list.xMax + 6f, ty, 2f, thumb), gold);
        }
        // detail strip: stats of the selected item + what the trader pays, then the last message
        Rect d = R(lay.detail); HudDraw.Fill(new Rect(d.x, d.y, d.width, 1f), frameDim); var sl = shop.Selected;
        if (sl != null)
        {
            GearItem gi; bool has = LootBridge.Catalog.TryGet(sl.id, out gi);
            HudDraw.Text(new Rect(d.x, d.y + 6f, d.width * 0.55f, L.detailNameSize * 1.5f), sl.name, "serif", L.detailNameSize, TextAnchor.UpperLeft, true, has ? LootCfg.RarityColor(gi) : ink);
            string st = ""; if (has && gi.stats != null) for (int q = 0; q < gi.stats.Length; q++) { var row = LootCfg.StatRow(gi.stats[q].stat); string lb = row != null && row.label.Length > 0 ? row.label : LootCfg.Pretty(gi.stats[q].stat); st += (st.Length > 0 ? "    " : "") + lb + " " + gi.stats[q].value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture); }
            HudDraw.Text(new Rect(d.x, d.y + 6f + L.detailNameSize * 1.5f, d.width, L.detailSize * 1.5f), st, "serif", L.detailSize, TextAnchor.UpperLeft, false, dim);
            HudDraw.Text(new Rect(d.x, d.y + 6f, d.width, L.detailSize * 1.5f), sl.worn ? (shop.BuyTab ? MarketCfg.Code(sl.why) : T.detailWorn) : string.Format(shop.BuyTab && MarketBuyRules.Cur.detailAsks.Length > 0 ? MarketBuyRules.Cur.detailAsks : T.detailPays, sl.price), "serif", L.detailSize, TextAnchor.UpperRight, false, sl.worn ? dim : gold);
        }
        if (shop.NoteKey.Length > 0)
        {
            string msg = shop.NoteKey == "bought" ? string.Format(MarketBuyRules.Cur.bought, shop.NoteItem, shop.NoteCoins) : shop.NoteKey == "sold" ? string.Format(T.sold, shop.NoteItem, shop.NoteCoins) : shop.NoteKey == "armed" ? string.Format(shop.BuyTab && MarketBuyRules.Cur.armedBuy.Length > 0 ? MarketBuyRules.Cur.armedBuy : T.armed, shop.NoteItem, shop.NoteCoins) : MarketCfg.Code(shop.NoteKey);
            HudDraw.Text(new Rect(d.x, d.yMax - L.messageSize * 1.6f, d.width, L.messageSize * 1.6f), msg, "serif", L.messageSize, TextAnchor.LowerLeft, true, MarketCfg.Col(shop.NoteGood ? C.colors.good : C.colors.bad));
        }
        // footer: the three hints
        Rect f = R(lay.footer); float w3 = f.width / 3f;
        HudDraw.Text(new Rect(f.x, f.y, w3, f.height), MarketBuyRules.Cur.HintMove(T.hintMove).ToUpperInvariant(), "serif", L.hintSize, TextAnchor.MiddleLeft, false, dim);
        HudDraw.Text(new Rect(f.x + w3, f.y, w3, f.height), MarketBuyRules.Cur.HintConfirm(shop.BuyTab, T.hintSell).ToUpperInvariant(), "serif", L.hintSize, TextAnchor.MiddleCenter, true, gold);
        HudDraw.Text(new Rect(f.x + 2f * w3, f.y, w3, f.height), T.hintClose.ToUpperInvariant(), "serif", L.hintSize, TextAnchor.MiddleRight, false, dim);
    }
}
