using System;
using System.Collections.Generic;
using UnityEngine;

// LOOTUI v85: the equip screen. Tab / Start opens it (outside a live fight; Tab is the lock-on key while fighting), three columns:
//   Worn      the slots of the open tab (loot_ui.json slots[] + tabs[]) with the item in each
//   Carried   every owned item that fits the selected slot, plus an Unequip row
//   Details   name (serif, rarity colour), slot, flavour text and the stats, with an up / down arrow and the difference against what is worn in that slot
// K26: three TABS (loot_ui.json tabs[]): GEAR (armour, main / off hand), MOUNTS (the owned mounts, a Summon / Dismiss row and the summon key), REMNANTS & MOVES (the two remnant slots and the move list of the wearer, abilities.json).
//   Tabs switch with a click on the tab or the controls.json keys equipTabPrev / equipTabNext (Q / R, pad LB / RB). Without tabs[] the screen is the old single list of every slot.
// Keys (loot_ui.json keys{}): arrows / WASD / stick move, left / right switch column, E / pad A equip (again = take off), U / pad X take off, Tab / Start / Esc / pad B close; the mouse clicks rows
// (double click equips). While open Duel.Frozen is set (no brain decides, the player cannot move) and LootUI.Blocking is true (GameLoop must not read Enter / A / B, see MANIFEST hunk).
public static class LootScreen
{
    static MenuLayoutCfg.LootScreenC ML { get { return MenuLayoutCfg.LS; } }   // UIDATAB v1: layout numbers of the equip screen: menu_layout.json (defaults = the old literals)
    static readonly UiStr.CatSep hintCat = new UiStr.CatSep(); static readonly List<string> statKeys = new List<string>(); static readonly string[] statFmts = { "F0", "F1", "F2", "F3", "F4" };   // UIDATAB v1: cached hint line / reused list / cached number formats
    public static bool Open { get; private set; }
    public static int ClosedFrame = -1;

    static int focus, slotIdx, candIdx, candScroll; static readonly List<GearItem> cands = new List<GearItem>();
    static float openAt; static bool ownFreeze, cursorSaved; static int unfreezeFrame = -1; static CursorLockMode prevLock; static bool prevVis;
    static string message = ""; static float messageAt;
    static object keyOwner; static KeyCode[] kToggle, kClose, kConfirm, kUnequip, kUp, kDown, kLeft, kRight, kTabPrev, kTabNext;
    static int lastDx, lastDy; static float nextNav; static bool axisBroken;
    // K26: tabs. vis = indices (into loot_ui.json slots[]) of the slots listed on the open tab; moveList = the wearer's ability rows (tab kind slotsMoves); mountList = owned mount items (tab kind mounts).
    // slotIdx is a slots[] index while onMove is false; onMove true = the cursor is on moveList[moveIdx]. listScroll = px the left list is scrolled.
    static int tab, moveIdx, mountIdx; static bool onMove; static float listScroll;
    static readonly List<int> vis = new List<int>(); static readonly List<AbilityTable.Row> moveList = new List<AbilityTable.Row>(); static readonly List<GearItem> mountList = new List<GearItem>();
    static readonly Dictionary<string, string> weaponsTxt = new Dictionary<string, string>();
    static string hintTxt = "", hintKind = ""; static int hintVer = -1; static object hintOwner;   // cached footer line
    static string mkTxt = "", mkFor = ""; static object mkOwner;                                    // cached "Key M calls ..." line

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Open = false; ClosedFrame = -1; focus = slotIdx = candIdx = candScroll = 0; cands.Clear(); ownFreeze = false; unfreezeFrame = -1; cursorSaved = false; keyOwner = null; axisBroken = false; message = ""; tab = moveIdx = mountIdx = 0; onMove = false; listScroll = 0f; vis.Clear(); moveList.Clear(); mountList.Clear(); weaponsTxt.Clear(); hintOwner = null; mkOwner = null; }

    static void EnsureKeys()
    {
        var K = LootCfg.Get().keys; if (ReferenceEquals(keyOwner, K)) return; keyOwner = K;
        kToggle = LootCfg.Keys(K.toggle); kClose = LootCfg.Keys(K.close); kConfirm = LootCfg.Keys(K.confirm); kUnequip = LootCfg.Keys(K.unequip);
        kUp = LootCfg.Keys(K.up); kDown = LootCfg.Keys(K.down); kLeft = LootCfg.Keys(K.left); kRight = LootCfg.Keys(K.right);
        kTabPrev = LootCfg.Keys(K.tabPrev); kTabNext = LootCfg.Keys(K.tabNext);   // K26: the fallback when controls.json bindings are off
    }
    public static KeyCode[] PickupKeys { get { return LootCfg.Keys(LootCfg.Get().keys.pickup); } }

    // ---- open / close ----
    public static bool CanOpen(bool demo)
    {
        var S = LootCfg.Get().screen;
        if (LootCfg.Get().slots.Length == 0) return false;
        if (S.blockWhileMenu && MenuUI.Blocking) return false;   // F5 LOOTPAD
        if (RTFinisher.Busy || GameLoopDriver.Transitioning || MarketUI.Open) return false;   // STALL v1: not on top of the stall panel
        if (demo || S.openInFight) return true;
        bool live = GameLoop.Player != null && GameLoop.Enemy != null && GameLoop.Outcome == GameLoop.Result.None && !(ZoneRuntime.Owns && (GameLoop.Enemy.Dead || GameLoop.Enemy.opponent == null));   // TRADEFIN v1: a zone corpse / an unengaged creature is not a live fight
        return !live;
    }

    public static void OpenScreen()
    {
        if (Open) return;
        Open = true; openAt = Time.unscaledTime; focus = 0; message = ""; unfreezeFrame = -1;
        if (!LootSave.Applied) LootSave.Apply();
        bool any = false; var defs = LootCfg.Get().slots;   // what is worn right now (a starting loadout) counts as carried, so it can be put back on after it comes off
        for (int i = 0; i < defs.Length; i++) any |= LootSave.Own(LootBridge.Slots.GetEquipped(defs[i].id));
        if (any) LootSave.Save();
        if (!LootCfg.Get().screen.rememberTab) tab = 0;   // K26
        onMove = false; moveIdx = 0; listScroll = 0f;
        slotIdx = Mathf.Clamp(slotIdx, 0, Mathf.Max(0, LootCfg.Get().slots.Length - 1)); BuildTab(true);
        if (!Duel.Frozen) { Duel.Frozen = true; ownFreeze = true; }
        if (!Application.isBatchMode) { prevLock = Cursor.lockState; prevVis = Cursor.visible; cursorSaved = true; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        lastDx = lastDy = 0;
    }
    /// <summary>Harness (ZoneCamScene lootscreen step): open and jump to tab t.</summary>
    public static void ShotTab(int t) { OpenScreen(); SetTab(t); }
    public static void CloseScreen()
    {
        if (!Open) return;
        Open = false; ClosedFrame = Time.frameCount;
        if (ownFreeze) unfreezeFrame = Time.frameCount + 1;   // released next frame: the Tab / E / WASD that closed the screen must not reach PlayerBrain (lock-on, roll)
        if (cursorSaved) { Cursor.lockState = prevLock; Cursor.visible = prevVis; cursorSaved = false; }
    }
    /// <summary>Scene change while open: the new scene's own Duel already reset Frozen.</summary>
    public static void ForceClose() { if (!Open) return; ownFreeze = false; unfreezeFrame = -1; CloseScreen(); }

    // ---- data helpers ----
    static LootCfg.SlotC SlotAt(int i) { var s = LootCfg.Get().slots; return i >= 0 && i < s.Length ? s[i] : null; }
    /// <summary>K26: the slot under the cursor on a slot tab (null on a move row, on the Mounts tab or when the tab lists no slot).</summary>
    static LootCfg.SlotC CurSlot() { return onMove || vis.Count == 0 || vis.IndexOf(slotIdx) < 0 ? null : SlotAt(slotIdx); }
    static GearItem Item(string id) { GearItem it; return !string.IsNullOrEmpty(id) && LootBridge.Catalog.TryGet(id, out it) ? it : null; }
    static GearItem Worn(string slotId) { return Item(LootBridge.Slots.GetEquipped(slotId)); }
    static string SlotWearing(string itemId)
    {
        var defs = LootCfg.Get().slots;
        for (int i = 0; i < defs.Length; i++) if (defs[i] != null && LootBridge.Slots.GetEquipped(defs[i].id) == itemId) return defs[i].id;
        return null;
    }
    static void Rebuild(bool pickWorn)
    {
        cands.Clear(); var s = CurSlot();
        if (s == null) { candIdx = 0; candScroll = 0; return; }   // K26: a move row / the Mounts tab has no carried list
        cands.Add(null);
        {
            var own = LootSave.Owned;
            for (int i = 0; i < own.Count; i++) { var it = Item(own[i]); if (it != null && LootCfg.Fits(it, s)) cands.Add(it); }
        }
        if (pickWorn || candIdx >= cands.Count)
        {
            candIdx = cands.Count > 1 ? 1 : 0;
            var w = LootBridge.Slots.GetEquipped(s.id);
            if (!string.IsNullOrEmpty(w)) for (int i = 1; i < cands.Count; i++) if (cands[i].id == w) { candIdx = i; break; }
        }
        candScroll = 0;
    }
    static void Say(string t) { message = t ?? ""; messageAt = Time.unscaledTime; }

    // ---- K26: tabs ----
    static LootCfg.TabC CurTab() { var t = LootCfg.Get().tabs; if (t.Length == 0) return null; tab = Mathf.Clamp(tab, 0, t.Length - 1); return t[tab]; }
    static string Kind() { var t = CurTab(); return t != null ? t.kind : "slots"; }
    static void SetTab(int t)
    {
        int n = LootCfg.Get().tabs.Length; if (n <= 1) return;
        t = ((t % n) + n) % n; if (t == tab) return;
        tab = t; focus = 0; listScroll = 0f; message = ""; onMove = false; moveIdx = 0; BuildTab(true);
    }
    /// <summary>Fill the lists of the open tab (slots, moves, mounts) and put the cursor on a valid row.</summary>
    static void BuildTab(bool pickWorn)
    {
        var C = LootCfg.Get(); var T = CurTab(); string kind = Kind();
        vis.Clear(); moveList.Clear(); mountList.Clear(); weaponsTxt.Clear();
        if (kind == "mounts")
        {
            onMove = false; cands.Clear(); candIdx = 0; candScroll = 0;
            var own = LootSave.Owned; string want = C.mounts.itemSlot;
            for (int i = 0; i < own.Count; i++) { var it = Item(own[i]); if (it != null && it.slot == want) mountList.Add(it); }
            mountIdx = Mathf.Clamp(mountIdx, 0, Mathf.Max(0, mountList.Count - 1));
            if (pickWorn) { string act = MountData.ActiveItem(); for (int i = 0; i < mountList.Count; i++) if (mountList[i].id == act) { mountIdx = i; break; } }
            return;
        }
        for (int i = 0; i < C.slots.Length; i++) if (C.slots[i] != null && (T == null || C.slots[i].tab == T.id)) vis.Add(i);
        if (vis.Count > 0 && vis.IndexOf(slotIdx) < 0) slotIdx = vis[0];
        if (kind == "slotsMoves")
        {
            var pl = GameLoop.Player; string who = C.moves.character.Length > 0 ? C.moves.character : (pl != null ? pl.CharacterId : "");
            if (!AbilityTable.Off && who.Length > 0)
            {
                var all = AbilityTable.Get().abilities;
                for (int i = 0; i < all.Length; i++) { var r = all[i]; if (r != null && r.characters != null && Array.IndexOf(r.characters, who) >= 0) moveList.Add(r); }
            }
        }
        if (vis.Count == 0 && moveList.Count > 0) onMove = true;
        if (onMove) { if (moveList.Count == 0) onMove = false; else moveIdx = Mathf.Clamp(moveIdx, 0, moveList.Count - 1); }
        Rebuild(pickWorn);
    }
    static int EntryPos() { return onMove ? vis.Count + moveIdx : Mathf.Max(0, vis.IndexOf(slotIdx)); }
    /// <summary>Cursor to the n-th entry of the left list (slots first, then moves).</summary>
    static void SetEntry(int pos)
    {
        int total = vis.Count + moveList.Count; if (total == 0) return; pos = Mathf.Clamp(pos, 0, total - 1);
        bool was = onMove;
        if (pos < vis.Count) { onMove = false; int g = vis[pos]; if (was || g != slotIdx) { slotIdx = g; Rebuild(true); } }
        else { onMove = true; moveIdx = pos - vis.Count; cands.Clear(); focus = 0; }
    }

    // ---- actions ----
    static void DoUnequip(string slotId)
    {
        if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(LootBridge.Slots.GetEquipped(slotId))) return;
        LootBridge.Slots.Unequip(slotId); LootSave.Save(); LootBridge.RaiseEquippedChanged(slotId, ""); Rebuild(false);
    }
    static void DoEquip(GearItem it, string slotId)
    {
        if (it == null || string.IsNullOrEmpty(slotId)) return;
        var S = LootBridge.Slots;
        if (S.GetEquipped(slotId) == it.id) { DoUnequip(slotId); return; }       // again = take off
        string where = SlotWearing(it.id); if (where != null) S.Unequip(where);   // it moves, an item sits in one slot only
        S.Unequip(slotId);
        var to = S as IGearSlotsTo; bool ok = to != null ? to.EquipTo(it.id, slotId) : S.Equip(it.id);
        if (!ok) { if (where != null) { if (to != null) to.EquipTo(it.id, where); else S.Equip(it.id); } Say(LootCfg.Get().text.cannotEquip); return; }   // refused: put it back where it was
        string now = SlotWearing(it.id);
        LootSave.Save(); LootBridge.RaiseEquippedChanged(now ?? slotId, it.id); Rebuild(false);
        if (now != null && now != slotId) { for (int i = 0; i < LootCfg.Get().slots.Length; i++) if (LootCfg.Get().slots[i].id == now && vis.Contains(i)) { slotIdx = i; Rebuild(true); break; } }   // the wearer chose another slot of the group: follow it (K26: only when it is listed on this tab)
    }
    static void Confirm()
    {
        if (Kind() == "mounts") { ConfirmMount(); return; }   // K26
        var s = CurSlot(); if (s == null) return;
        if (focus == 0) { focus = 1; return; }
        if (candIdx <= 0 || candIdx >= cands.Count) DoUnequip(s.id); else DoEquip(cands[candIdx], s.id);
    }
    static void SelectSlot(int i) { bool was = onMove; onMove = false; if (!was && i == slotIdx) return; slotIdx = i; Rebuild(true); }

    // K26: Summon / Dismiss. A mounted wearer is dismissed; otherwise the selected owned mount is picked (MountData.Pick) and MountRider is asked for the same toggle as the summon key.
    static string RefuseText(string code)
    {
        var M = LootCfg.Get().mounts;
        for (int i = 0; i < M.refuse.Length; i++) if (M.refuse[i] != null && M.refuse[i].code == code) return M.refuse[i].text;
        return LootCfg.Get().text.mountRefuseOther;
    }
    static void ConfirmMount()
    {
        var M = LootCfg.Get().mounts;
        if (MountRider.Inst == null) { Say(RefuseText("no rider")); return; }
        if (MountRider.Mounted) { MountRider.RequestToggle(); return; }
        if (mountIdx < 0 || mountIdx >= mountList.Count) return;
        string why = MountRider.UiRefuse();
        if (why.Length > 0 && why != "horse not loaded yet") { Say(RefuseText(why)); return; }
        MountData.Pick = mountList[mountIdx].id; MountRider.RequestToggle();
        if (M.closeOnSummon) CloseScreen();
    }
    /// <summary>K26: the summon key as the player has it: the controls.json binding of mount.json summonAction (rebindable), else mount.json summonKey.</summary>
    static string MountKey()
    {
        var K = MountData.Get(); var a = InputMap.Find(K.summonAction);
        if (a != null) return a.primary.Length > 0 ? InputMap.Name(a.primary[0]) : InputMap.Ui.unbound;
        return K.summonKey;
    }
    static string TabKeyName(string action, string[] legacy)
    {
        var a = InputMap.Find(action);
        if (a != null) return a.primary.Length > 0 ? InputMap.Name(a.primary[0]) : InputMap.Ui.unbound;
        return legacy != null && legacy.Length > 0 ? legacy[0] : "";
    }
    /// <summary>Footer line of the open tab, rebuilt only when the tab kind, the key map or the data change.</summary>
    static string HintLine(string kind)
    {
        var C = LootCfg.Get(); var T = C.text;
        if (hintVer == InputMap.Version && hintKind == kind && ReferenceEquals(hintOwner, T)) return hintTxt;
        hintVer = InputMap.Version; hintKind = kind; hintOwner = T;
        var parts = new List<string>(); parts.Add(T.moveKeys);
        if (kind == "mounts") parts.Add(T.summonKeys); else { parts.Add(T.equipKeys); parts.Add(T.unequipKeys); }
        if (C.tabs.Length > 1) parts.Add(string.Format(T.tabKeys, TabKeyName("equipTabPrev", C.keys.tabPrev), TabKeyName("equipTabNext", C.keys.tabNext)));
        parts.Add(T.closeKeys); hintTxt = string.Join(ML.screen.hintSep, parts.ToArray()); return hintTxt;
    }

    // ---- input (every frame, from LootUI.Update) ----
    public static void Tick(bool demo)
    {
        EnsureKeys(); var K = LootCfg.Get().keys;
        if (unfreezeFrame >= 0 && Time.frameCount >= unfreezeFrame) { unfreezeFrame = -1; if (ownFreeze) { Duel.Frozen = false; ownFreeze = false; } }
        if (!Open) { if (LootCfg.AnyDown(kToggle) && CanOpen(demo)) OpenScreen(); return; }
        if (LootCfg.AnyDown(kToggle) || LootCfg.AnyDown(kClose)) { CloseScreen(); return; }
        if (Time.unscaledTime - messageAt > LootCfg.Get().screen.messageS /* HCKIT6 v100 */) message = "";
        if (LootCfg.Get().tabs.Length > 1)   // K26: controls.json bindings equipTabPrev / equipTabNext (the keys of keys.tabPrev / tabNext when the map is off)
        {
            if (InputMap.AnyDown("equipTabPrev", kTabPrev)) { SetTab(tab - 1); return; }
            if (InputMap.AnyDown("equipTabNext", kTabNext)) { SetTab(tab + 1); return; }
        }
        if (LootCfg.AnyDown(kConfirm)) Confirm();
        if (LootCfg.AnyDown(kUnequip)) { var s = CurSlot(); if (s != null) DoUnequip(s.id); }
        int dx, dy; Nav(K, out dx, out dy);
        if (Kind() == "mounts") { if (dy != 0) mountIdx = Mathf.Clamp(mountIdx + dy, 0, Mathf.Max(0, mountList.Count - 1)); return; }   // K26
        if (dx < 0) focus = 0; else if (dx > 0 && cands.Count > 0) focus = 1;
        if (dy != 0)
        {
            if (focus == 0) SetEntry(EntryPos() + dy);
            else candIdx = Mathf.Clamp(candIdx + dy, 0, Mathf.Max(0, cands.Count - 1));
        }
    }

    static void Nav(LootCfg.KeysC K, out int dx, out int dy)
    {
        dx = (LootCfg.AnyHeld(kRight) ? 1 : 0) - (LootCfg.AnyHeld(kLeft) ? 1 : 0);
        dy = (LootCfg.AnyHeld(kDown) ? 1 : 0) - (LootCfg.AnyHeld(kUp) ? 1 : 0);
        if (!axisBroken)
        {
            try
            {
                float ax = string.IsNullOrEmpty(K.axisX) ? 0f : Input.GetAxisRaw(K.axisX), ay = string.IsNullOrEmpty(K.axisY) ? 0f : Input.GetAxisRaw(K.axisY);
                if (dx == 0 && Mathf.Abs(ax) >= K.stickThreshold) dx = ax > 0f ? 1 : -1;
                if (dy == 0 && Mathf.Abs(ay) >= K.stickThreshold) dy = ay > 0f ? -1 : 1;   // stick up = list up
            }
            catch (Exception) { axisBroken = true; Debug.LogWarning("[LootUI] input axis '" + K.axisX + "' / '" + K.axisY + "' missing: keys only."); }
        }
        if (dx == 0 && dy == 0) { lastDx = lastDy = 0; return; }
        float now = Time.unscaledTime;
        if (dx != lastDx || dy != lastDy) { lastDx = dx; lastDy = dy; nextNav = now + K.repeatDelayS; return; }   // first press: emit
        if (now >= nextNav) { nextNav = now + K.repeatS; return; }                                                   // held: repeat
        dx = 0; dy = 0;
    }

    // ---- drawing (vw, vh = virtual size inside GUI.matrix scale) ----
    public static void Draw(float vw, float vh)
    {
        if (!Open) return;
        var C = LootCfg.Get(); var S = C.screen; var Kc = C.colors; var T = C.text; var ev = Event.current; string kind = Kind();
        float a = Mathf.Clamp01((Time.unscaledTime - openAt) / Mathf.Max(0.01f, S.openFadeS)); LootDraw.Alpha = a;
        Color ink = LootCfg.Col(Kc.ink), dim = LootCfg.Col(Kc.dim), gold = LootCfg.Col(Kc.gold), crim = LootCfg.Col(Kc.crimson), frame = LootCfg.Col(Kc.frame), frameDim = LootCfg.Col(Kc.frameDim);
        LootDraw.Fill(new Rect(0, 0, vw, vh), LootCfg.Col(Kc.dimmer));
        float px = (vw - S.panelW) * 0.5f, py = (vh - S.panelH) * 0.5f; var P = new Rect(px, py, S.panelW, S.panelH);
        LootDraw.Fill(P, LootCfg.Col(Kc.panel)); LootDraw.Frame(P, frame, S.frameW); LootDraw.Frame(new Rect(P.x + S.insetPx, P.y + S.insetPx, P.width - 2f * S.insetPx, P.height - 2f * S.insetPx), frameDim, S.insetFrameW);

        // title
        LootDraw.Text(new Rect(px + S.pad, py + S.titleY, S.panelW - 2f * S.pad, S.titleSize * S.titleLine), UiStr.Up(T.title), "serif", S.titleSize, TextAnchor.UpperLeft, true, gold);
        LootDraw.Text(new Rect(px + S.pad + S.subtitleDx, py + S.titleY + S.titleSize * S.subtitleDy, S.panelW - 2f * S.pad, S.subtitleSize * S.boxLine), T.subtitle, "serif", S.subtitleSize, TextAnchor.UpperLeft, false, dim, true);
        var closeR = new Rect(P.xMax - S.pad - S.closeBox, py + S.titleY, S.closeBox, S.closeBox);
        LootDraw.Frame(closeR, frameDim, ML.screen.closeFrameW); LootDraw.Text(closeR, T.close, "serif", S.closeSize, TextAnchor.MiddleCenter, true, dim);
        if (GUI.Button(closeR, GUIContent.none, GUIStyle.none)) CloseScreen();

        // K26: the tab strip, right of the title, left of the close box
        int nt = C.tabs.Length;
        if (nt > 1)
        {
            float totalW = nt * S.tabW + (nt - 1) * S.tabGap, tx = closeR.x - S.tabGap * 2f - totalW;
            for (int i = 0; i < nt; i++)
            {
                var tr = new Rect(tx + i * (S.tabW + S.tabGap), py + S.tabY, S.tabW, S.tabH); bool on = i == tab; var td = C.tabs[i];
                LootDraw.Fill(tr, on ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
                LootDraw.Frame(tr, on ? frame : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), ML.screen.rowFrameW);
                if (on) LootDraw.Fill(new Rect(tr.x, tr.yMax - S.tabUnderH, tr.width, S.tabUnderH), crim);
                LootDraw.Text(tr, UiStr.Up(td.label), "serif", S.tabSize, TextAnchor.MiddleCenter, true, on ? gold : dim);
                if (GUI.Button(tr, GUIContent.none, GUIStyle.none)) SetTab(i);
            }
        }

        float c1 = px + S.pad, c2 = c1 + S.slotColW + S.colGap, c3 = c2 + S.candColW + S.colGap, w3 = P.xMax - S.pad - c3;
        float yh = py + S.headerY, y0 = py + S.bodyTop, bodyH = S.panelH - S.bodyTop - S.bodyBottom;
        LootDraw.Fill(new Rect(c1, yh + S.headerSize * S.boxLine, P.xMax - S.pad - c1, S.ruleH), LootCfg.Col(Kc.crimson, S.ruleAlpha * a));
        bool mounts = kind == "mounts";
        LootDraw.Text(new Rect(c1, yh, S.slotColW, S.headerSize * S.boxLine), UiStr.Up(mounts ? T.mountsHeader : T.slotsHeader), "serif", S.headerSize, TextAnchor.UpperLeft, true, focus == 0 ? gold : dim);
        LootDraw.Text(new Rect(c2, yh, S.candColW, S.headerSize * S.boxLine), UiStr.Up(mounts ? T.mountActionHeader : T.candHeader), "serif", S.headerSize, TextAnchor.UpperLeft, true, focus == 1 || mounts ? gold : dim);
        LootDraw.Text(new Rect(c3, yh, w3, S.headerSize * S.boxLine), UiStr.Up(T.detailHeader), "serif", S.headerSize, TextAnchor.UpperLeft, true, dim);

        if (mounts) DrawMounts(c1, c2, c3, w3, y0, bodyH);
        else
        {
            DrawLeftList(c1, y0, bodyH);

            // candidates (scrolled so the cursor row is always visible)
            int maxRows = Mathf.Max(1, Mathf.FloorToInt(bodyH / (S.candRowH + S.rowGap)));
            if (candIdx < candScroll) candScroll = candIdx; if (candIdx >= candScroll + maxRows) candScroll = candIdx - maxRows + 1;
            var curSlot = CurSlot(); string wornHere = curSlot != null ? LootBridge.Slots.GetEquipped(curSlot.id) : "";
            for (int row = 0; row < maxRows; row++)
            {
                int i = candScroll + row; if (i >= cands.Count) break;
                var it = cands[i]; var r = new Rect(c2, y0 + row * (S.candRowH + S.rowGap), S.candColW, S.candRowH); bool sel = i == candIdx;
                LootDraw.Fill(r, sel ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
                LootDraw.Frame(r, sel ? (focus == 1 ? frame : frameDim) : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), ML.screen.rowFrameW);
                if (sel) LootDraw.Fill(new Rect(r.x, r.y, S.accentBar, r.height), crim);
                if (it == null) LootDraw.Text(new Rect(r.x + ML.screen.candPadX, r.y, r.width - ML.screen.candTrimW, r.height), T.emptyRow, "serif", S.rowNameSize, TextAnchor.MiddleLeft, false, dim, true);
                else
                {
                    bool here = it.id == wornHere; string other = here ? null : SlotWearing(it.id);
                    LootDraw.Text(new Rect(r.x + ML.screen.candPadX, r.y + ML.screen.candPadY, r.width - ML.screen.candNameTrim, S.rowNameSize * ML.screen.candNameK), it.name, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, LootCfg.RarityColor(it));
                    string sub = here ? "" : other != null ? string.Format(T.elsewhere, LootCfg.SlotLabel(other)) : "";
                    if (sub.Length > 0) LootDraw.Text(new Rect(r.x + ML.screen.candPadX, r.y + ML.screen.candPadY + S.rowNameSize * ML.screen.candSubK, r.width - ML.screen.candTrimW, S.labelSize * ML.screen.candSubBoxK), sub, "serif", S.labelSize, TextAnchor.UpperLeft, false, dim, true);
                    if (here) LootDraw.Text(new Rect(r.xMax - ML.screen.badgeDx, r.y, ML.screen.badgeW, r.height), UiStr.Up(T.equipped), "serif", S.badgeSize, TextAnchor.MiddleRight, true, gold);
                }
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { candIdx = i; focus = 1; if (ev.clickCount >= 2) Confirm(); }
            }
            if (onMove) LootDraw.Text(new Rect(c2 + ML.screen.nothingDx, y0 + ML.screen.nothingDy, S.candColW, S.bodySize * S.boxLine * 2f), T.movesNote, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true, true);   // K26: a move row has no carried list
            else if (cands.Count <= 1) LootDraw.Text(new Rect(c2 + ML.screen.nothingDx, y0 + S.candRowH + S.rowGap + ML.screen.nothingDy, S.candColW, S.bodySize * S.boxLine), T.nothing, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true);

            DrawDetail(c3, y0, w3, bodyH);
        }

        // footer
        string hint = HintLine(kind);   // K26: per tab, cached
        LootDraw.Text(new Rect(px, P.yMax - S.hintY, S.panelW, S.hintSize * S.boxLine), hint, "serif", S.hintSize, TextAnchor.UpperCenter, false, dim);
        if (message.Length > 0) LootDraw.Text(new Rect(px, P.yMax - S.hintY - S.hintSize * ML.screen.msgOffK, S.panelW, S.hintSize * ML.screen.msgBoxK), message, "serif", S.hintSize + ML.screen.msgSizeAdd, TextAnchor.UpperCenter, true, crim);

        // modal: nothing under the panel (GameLoop's Retry / Next buttons) sees a click
        if (ev.type == EventType.MouseDown) ev.Use();
        LootDraw.Alpha = 1f;
    }

    // K26: the left column of a slot tab: its slots, then (tab kind slotsMoves) a Moves heading and the move rows. Scrolls so the cursor row is fully visible; rows that do not fit are not drawn.
    static void DrawLeftList(float c1, float y0, float bodyH)
    {
        var C = LootCfg.Get(); var S = C.screen; var Kc = C.colors; var T = C.text;
        Color dim = LootCfg.Col(Kc.dim), gold = LootCfg.Col(Kc.gold), crim = LootCfg.Col(Kc.crimson), frame = LootCfg.Col(Kc.frame), frameDim = LootCfg.Col(Kc.frameDim), ink = LootCfg.Col(Kc.ink); var ev = Event.current;
        float slotStep = S.slotRowH + S.rowGap, moveStep = S.moveRowH + S.rowGap, headH = S.sectionSize * S.boxLine + S.sectionGap;
        bool hasMoves = Kind() == "slotsMoves"; float moveBase = vis.Count * slotStep + (hasMoves ? headH : 0f);
        // scroll: the cursor row (the heading too for the first move) must be inside [listScroll, listScroll + bodyH]
        float selTop, selBot;
        if (onMove) { selTop = moveBase + moveIdx * moveStep; selBot = selTop + S.moveRowH; if (moveIdx == 0) selTop -= headH; }
        else { int p = Mathf.Max(0, vis.IndexOf(slotIdx)); selTop = p * slotStep; selBot = selTop + S.slotRowH; }
        if (selTop < listScroll) listScroll = selTop; if (selBot > listScroll + bodyH) listScroll = selBot - bodyH; if (listScroll < 0f) listScroll = 0f;
        var defs = C.slots; var ev0 = ev;
        for (int j = 0; j < vis.Count; j++)
        {
            int i = vis[j]; var s = defs[i]; if (s == null) continue;
            float top = j * slotStep - listScroll; if (top < -0.01f || top + S.slotRowH > bodyH + 0.01f) continue;
            var r = new Rect(c1, y0 + top, S.slotColW, S.slotRowH); bool sel = !onMove && i == slotIdx; var w = Worn(s.id);
            LootDraw.Fill(r, sel ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
            LootDraw.Frame(r, sel ? (focus == 0 ? frame : frameDim) : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), ML.screen.rowFrameW);
            if (sel) LootDraw.Fill(new Rect(r.x, r.y, S.accentBar, r.height), crim);
            LootDraw.Text(new Rect(r.x + S.rowPadX, r.y + S.rowPadY, r.width - S.rowTrim, S.labelSize * S.labelLine), UiStr.Up(s.label.Length > 0 ? s.label : LootCfg.Pretty(s.id)), "serif", S.labelSize, TextAnchor.UpperLeft, true, sel ? gold : dim);
            var nr = new Rect(r.x + S.rowPadX, r.y + S.rowPadY + S.labelSize * S.labelLine + S.nameGap, r.width - S.nameTrim, S.rowNameSize * S.nameLine);
            if (w != null) LootDraw.Text(nr, w.name, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, LootCfg.RarityColor(w));
            else LootDraw.Text(nr, T.emptySlot, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, dim, true);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { SelectSlot(i); focus = 0; if (ev0.clickCount >= 2) focus = 1; }
        }
        if (!hasMoves) return;
        float hy = vis.Count * slotStep - listScroll;
        if (hy >= -0.01f && hy + headH <= bodyH + 0.01f)
        {
            LootDraw.Text(new Rect(c1, y0 + hy + S.sectionGap * 0.5f, S.slotColW, S.sectionSize * S.boxLine), UiStr.Up(T.movesHeader), "serif", S.sectionSize, TextAnchor.UpperLeft, true, dim);
            LootDraw.Fill(new Rect(c1, y0 + hy + headH - S.ruleH, S.slotColW, S.ruleH), LootCfg.Col(Kc.crimson, S.ruleAlpha * LootDraw.Alpha));
        }
        if (moveList.Count == 0) { float ny = moveBase - listScroll; if (ny + S.bodySize * S.boxLine <= bodyH + 0.01f) LootDraw.Text(new Rect(c1 + ML.screen.nothingDx, y0 + ny, S.slotColW, S.bodySize * S.boxLine), T.movesNone, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true); return; }
        for (int m = 0; m < moveList.Count; m++)
        {
            float top = moveBase + m * moveStep - listScroll; if (top < -0.01f || top + S.moveRowH > bodyH + 0.01f) continue;
            var row = moveList[m]; var r = new Rect(c1, y0 + top, S.slotColW, S.moveRowH); bool sel = onMove && m == moveIdx; bool ready = Ready(row);
            LootDraw.Fill(r, sel ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
            LootDraw.Frame(r, sel ? (focus == 0 ? frame : frameDim) : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), ML.screen.rowFrameW);
            if (sel) LootDraw.Fill(new Rect(r.x, r.y, S.accentBar, r.height), crim);
            LootDraw.Text(new Rect(r.x + S.rowPadX, r.y + ML.screen.candPadY * 0.5f, r.width - S.rowTrim - ML.screen.badgeW * 0.6f, S.bodySize * S.nameLine), row.name.Length > 0 ? row.name : LootCfg.Pretty(row.id), "serif", S.bodySize + 1, TextAnchor.UpperLeft, false, ready ? ink : dim);
            LootDraw.Text(new Rect(r.x + S.rowPadX, r.y + ML.screen.candPadY * 0.5f + S.bodySize * S.nameLine, r.width - S.rowTrim, S.labelSize * S.labelLine), SlotName(row), "serif", S.labelSize, TextAnchor.UpperLeft, false, dim, true);
            LootDraw.Text(new Rect(r.xMax - ML.screen.badgeDx, r.y, ML.screen.badgeW, r.height), UiStr.Up(ready ? T.moveReady : T.moveOther), "serif", S.badgeSize, TextAnchor.MiddleRight, true, ready ? gold : dim);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { SetEntry(vis.Count + m); focus = 0; }
        }
    }

    // ---- K26: moves ----
    static bool Ready(AbilityTable.Row r)
    {
        var pl = GameLoop.Player; if (pl == null || r == null) return false;
        if (r.trigger == "evade") return AbilityTable.EvadeMove(pl) == r.moveId;
        return ReferenceEquals(AbilityTable.For(pl, r.slot), r);   // the row this weapon casts on that slot
    }
    static LootCfg.SlotNameC SlotRow(AbilityTable.Row r)
    {
        var l = LootCfg.Get().moves.slots;
        for (int i = 0; i < l.Length; i++) if (l[i] != null && l[i].slot == r.slot && (l[i].trigger.Length == 0 || l[i].trigger == r.trigger)) return l[i];
        return null;
    }
    static string SlotName(AbilityTable.Row r) { var n = SlotRow(r); return n != null ? n.label : ""; }
    static string MoveKey(AbilityTable.Row r)
    {
        var n = SlotRow(r); var a = n != null && n.action.Length > 0 ? InputMap.Find(n.action) : null;
        if (a != null && a.primary.Length > 0) return InputMap.Name(a.primary[0]);
        return r.key;
    }
    static string WeaponsOf(AbilityTable.Row r)
    {
        string s; if (weaponsTxt.TryGetValue(r.id, out s)) return s;
        var T = LootCfg.Get().text; var l = new List<string>();
        if (r.weapons != null) for (int i = 0; i < r.weapons.Length; i++) l.Add(r.weapons[i] == "*" ? T.weaponAny : r.weapons[i].Length == 0 ? T.weaponStart : LootCfg.Pretty(r.weapons[i]));
        s = string.Format(T.moveWeapons, string.Join(", ", l.ToArray())); weaponsTxt[r.id] = s; return s;
    }
    static bool MoveStat(AbilityTable.Row r, string key, out float v)
    {
        v = 0f; if (key == "cooldown") { v = r.cooldownS; return true; }
        var pl = GameLoop.Player; MoveDef m = pl != null && pl.moves != null ? pl.moves.Get(r.moveId) : AbilityTable.ExtraMove(r.moveId); if (m == null) return false;
        switch (key)
        {
            case "damage": v = m.damage; return true;
            case "posture": v = m.posture; return true;
            case "stagger": v = m.stagger; return true;
            case "reach": v = m.reach; return true;
            case "knockback": v = m.knockback; return true;
            case "stamina": v = m.stamina; return true;
            case "windup": v = m.windup; return true;
            case "active": v = m.active; return true;
            case "recovery": v = m.recovery; return true;
        }
        return false;
    }
    static float StatLine(float x, float yy, float w, int i, string label, float v, int decimals)
    {
        var C = LootCfg.Get(); var S = C.screen; Color ink = LootCfg.Col(C.colors.ink);
        int dec = Mathf.Max(0, decimals); string fmt = dec < statFmts.Length ? statFmts[dec] : "F" + dec; float labelW = w - S.statValueW - S.arrowSize - S.deltaW - ML.detail.statTrim;
        var rr = new Rect(x, yy, w, S.statRowH); if ((i & 1) == 0) LootDraw.Fill(rr, new Color(1f, 1f, 1f, S.zebraAlpha));
        LootDraw.Text(new Rect(x + ML.detail.statPadX, yy, labelW, S.statRowH), label, "serif", S.statSize, TextAnchor.MiddleLeft, false, ink);
        LootDraw.Text(new Rect(x + ML.detail.statPadX + labelW, yy, S.statValueW, S.statRowH), v.ToString(fmt), "serif", S.statSize, TextAnchor.MiddleRight, true, ink);
        return yy + S.statRowH;
    }
    /// <summary>Name (serif, big), a dim line and a rule, as the gear detail has them; returns the y below.</summary>
    static float DetailHead(float x, float yy, float w, string name, Color nameCol, string line)
    {
        var C = LootCfg.Get(); var S = C.screen; Color dim = LootCfg.Col(C.colors.dim); int ns = Mathf.RoundToInt(S.nameSize * ML.detail.nameBigK);
        LootDraw.Text(new Rect(x, yy, w, ns * ML.detail.nameBoxK), name, "serif", ns, TextAnchor.UpperLeft, true, nameCol); yy += ns * ML.detail.nameBoxK + S.detailNameGap;
        if (line.Length > 0) LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), UiStr.Up(line), "serif", S.labelSize + ML.detail.lineSizeAdd, TextAnchor.UpperLeft, true, dim);
        yy += S.labelSize * S.boxLine + ML.detail.lineGapAdd;
        LootDraw.Fill(new Rect(x, yy, w, ML.detail.ruleH), LootCfg.Col(C.colors.frameDim)); return yy + S.descGap * ML.detail.ruleGapK;
    }
    static void DrawMoveDetail(float x, float y, float w)
    {
        var C = LootCfg.Get(); var S = C.screen; var T = C.text; Color ink = LootCfg.Col(C.colors.ink), dim = LootCfg.Col(C.colors.dim), gold = LootCfg.Col(C.colors.gold);
        if (moveIdx < 0 || moveIdx >= moveList.Count) return; var r = moveList[moveIdx];
        float yy = DetailHead(x, y, w, r.name.Length > 0 ? r.name : LootCfg.Pretty(r.id), ink, string.Format(T.moveKeyLine, SlotName(r), MoveKey(r)));
        bool ready = Ready(r); LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), UiStr.Up(ready ? T.moveReady : T.moveOther), "serif", S.labelSize, TextAnchor.UpperLeft, true, ready ? gold : dim); yy += S.labelSize * ML.detail.compareK;
        string wp = WeaponsOf(r); float dh = LootDraw.Height(wp, "serif", S.bodySize, w, false, true); LootDraw.Text(new Rect(x, yy, w, dh), wp, "serif", S.bodySize, TextAnchor.UpperLeft, false, ink, true, true); yy += dh + S.descGap;
        var st = C.moves.stats; int n = 0;
        for (int i = 0; i < st.Length; i++) { float v; if (st[i] == null || !MoveStat(r, st[i].key, out v)) continue; yy = StatLine(x, yy, w, n++, st[i].label.Length > 0 ? st[i].label : LootCfg.Pretty(st[i].key), v, st[i].decimals); }
    }

    // ---- K26: mounts ----
    static bool MountStat(GearItem it, string key, out float v)
    {
        v = 0f; var K = MountData.Get(); MtVariant vr = null;
        for (int i = 0; i < K.variants.Length; i++) if (K.variants[i] != null && K.variants[i].item == it.id) { vr = K.variants[i]; break; }
        MtSpeed sp = vr != null ? vr.speed : K.speed; MtStamina sm = vr != null ? vr.stamina : K.stamina; MtHorse hs = vr != null ? vr.horse : K.horse;
        switch (key)
        {
            case "walk": v = sp.walkMul; return true;
            case "run": v = sp.runMul; return true;
            case "stamina": v = sm.max; return true;
            case "height": v = hs.heightM; return true;
        }
        return false;
    }
    static void DrawMounts(float c1, float c2, float c3, float w3, float y0, float bodyH)
    {
        var C = LootCfg.Get(); var S = C.screen; var Kc = C.colors; var T = C.text; var ev = Event.current;
        Color ink = LootCfg.Col(Kc.ink), dim = LootCfg.Col(Kc.dim), gold = LootCfg.Col(Kc.gold), crim = LootCfg.Col(Kc.crimson), frame = LootCfg.Col(Kc.frame), frameDim = LootCfg.Col(Kc.frameDim);
        bool mounted = MountRider.Mounted; string active = MountData.ActiveItem();
        // left: the owned mounts
        int maxRows = Mathf.Max(1, Mathf.FloorToInt(bodyH / (S.slotRowH + S.rowGap)));
        for (int row = 0; row < maxRows; row++)
        {
            if (row >= mountList.Count) break;
            int i = row; var it = mountList[i]; var r = new Rect(c1, y0 + row * (S.slotRowH + S.rowGap), S.slotColW, S.slotRowH); bool sel = i == mountIdx; bool here = it.id == active;
            LootDraw.Fill(r, sel ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
            LootDraw.Frame(r, sel ? frame : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), ML.screen.rowFrameW);
            if (sel) LootDraw.Fill(new Rect(r.x, r.y, S.accentBar, r.height), crim);
            LootDraw.Text(new Rect(r.x + S.rowPadX, r.y + S.rowPadY, r.width - S.rowTrim, S.labelSize * S.labelLine), UiStr.Up(LootCfg.SlotLabel(it.slot)), "serif", S.labelSize, TextAnchor.UpperLeft, true, sel ? gold : dim);
            LootDraw.Text(new Rect(r.x + S.rowPadX, r.y + S.rowPadY + S.labelSize * S.labelLine + S.nameGap, r.width - S.nameTrim - ML.screen.badgeW * 0.5f, S.rowNameSize * S.nameLine), it.name, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, LootCfg.RarityColor(it));
            if (here) LootDraw.Text(new Rect(r.xMax - ML.screen.badgeDx, r.y, ML.screen.badgeW, r.height), UiStr.Up(mounted ? T.mountRidden : T.mountChosen), "serif", S.badgeSize, TextAnchor.MiddleRight, true, gold);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { mountIdx = i; if (ev.clickCount >= 2) ConfirmMount(); }
        }
        if (mountList.Count == 0)
        {
            LootDraw.Text(new Rect(c1 + ML.screen.nothingDx, y0, S.slotColW, S.bodySize * S.boxLine), T.mountsNone, "serif", S.bodySize + ML.detail.emptySizeAdd, TextAnchor.UpperLeft, false, dim, true);
            LootDraw.Text(new Rect(c1 + ML.screen.nothingDx, y0 + S.bodySize * S.boxLine, S.slotColW, S.bodySize * S.boxLine * 2f), T.mountsNoneSub, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true, true);
        }
        // middle: Summon / Dismiss, the summon key, why not
        string why = MountRider.UiRefuse(); bool usable = mountList.Count > 0 && MountRider.Inst != null && (mounted || why.Length == 0 || why == "horse not loaded yet");
        var ar = new Rect(c2, y0, S.candColW, S.actionRowH);
        LootDraw.Fill(ar, LootCfg.Col(Kc.rowSelected)); LootDraw.Frame(ar, usable ? frame : frameDim, ML.screen.rowFrameW); if (usable) LootDraw.Fill(new Rect(ar.x, ar.y, S.accentBar, ar.height), crim);
        LootDraw.Text(ar, UiStr.Up(mounted ? T.dismiss : T.summon), "serif", S.rowNameSize + ML.detail.emptySizeAdd, TextAnchor.MiddleCenter, true, usable ? gold : dim);
        if (GUI.Button(ar, GUIContent.none, GUIStyle.none)) ConfirmMount();
        string key = MountKey(); if (!ReferenceEquals(mkOwner, T) || mkFor != key) { mkOwner = T; mkFor = key; mkTxt = string.Format(T.mountKeyLine, key); }
        float ky = y0 + S.actionRowH + S.rowGap * 2f, kh = LootDraw.Height(mkTxt, "serif", S.bodySize, S.candColW, false, true);
        LootDraw.Text(new Rect(c2, ky, S.candColW, kh), mkTxt, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true, true);
        if (!mounted && why.Length > 0 && why != "horse not loaded yet") LootDraw.Text(new Rect(c2, ky + kh + S.descGap, S.candColW, S.bodySize * S.boxLine * 2f), RefuseText(why), "serif", S.bodySize, TextAnchor.UpperLeft, false, crim, true, true);
        // right: the selected mount
        if (mountIdx < 0 || mountIdx >= mountList.Count) return; var m = mountList[mountIdx];
        var rar = LootCfg.Rarity(m.rarity); string rl = rar != null && rar.label.Length > 0 ? rar.label : "";
        string line = rl.Length > 0 ? string.Format(T.slotLine, rl, LootCfg.SlotLabel(m.slot)) : LootCfg.SlotLabel(m.slot);
        float yy = DetailHead(c3, y0, w3, m.name, LootCfg.RarityColor(m), line);
        if (!string.IsNullOrEmpty(m.desc)) { float dh = LootDraw.Height(m.desc, "serif", S.bodySize, w3, false, true); LootDraw.Text(new Rect(c3, yy, w3, dh), m.desc, "serif", S.bodySize, TextAnchor.UpperLeft, false, ink, true, true); yy += dh + S.descGap; }
        var st = C.mounts.stats; int n = 0;
        for (int i = 0; i < st.Length; i++) { float v; if (st[i] == null || !MountStat(m, st[i].key, out v)) continue; yy = StatLine(c3, yy, w3, n++, st[i].label.Length > 0 ? st[i].label : LootCfg.Pretty(st[i].key), v, st[i].decimals); }
    }

    static void DrawDetail(float x, float y, float w, float h)
    {
        var C = LootCfg.Get(); var S = C.screen; var Kc = C.colors; var T = C.text;
        Color ink = LootCfg.Col(Kc.ink), dim = LootCfg.Col(Kc.dim), gold = LootCfg.Col(Kc.gold);
        if (onMove) { DrawMoveDetail(x, y, w); return; }   // K26
        var slot = CurSlot(); if (slot == null) return;
        GearItem worn = Worn(slot.id), fi, vs; bool compare;
        if (focus == 0) { fi = worn; vs = null; compare = false; }
        else if (candIdx <= 0 || candIdx >= cands.Count) { fi = worn; vs = null; compare = false; }   // the Unequip row: show what comes off
        else { fi = cands[candIdx]; vs = worn; compare = fi.id != (worn != null ? worn.id : ""); }
        bool removing = focus == 1 && candIdx == 0 && worn != null;
        if (fi == null) { LootDraw.Text(new Rect(x, y, w, S.bodySize * S.boxLine), focus == 0 ? T.emptySlot : T.selectItem, "serif", S.bodySize + ML.detail.emptySizeAdd, TextAnchor.UpperLeft, false, dim, true); return; }

        float yy = y;
        if (removing) { LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), UiStr.Up(T.removing), "serif", S.labelSize, TextAnchor.UpperLeft, true, LootCfg.Col(Kc.crimson)); yy += S.labelSize * ML.detail.removeK; }
        int ns = Mathf.RoundToInt(S.nameSize * ML.detail.nameBigK);
        LootDraw.Text(new Rect(x, yy, w, ns * ML.detail.nameBoxK), fi.name, "serif", ns, TextAnchor.UpperLeft, true, LootCfg.RarityColor(fi)); yy += ns * ML.detail.nameBoxK + S.detailNameGap;
        var rar = LootCfg.Rarity(fi.rarity); string rl = rar != null && rar.label.Length > 0 ? rar.label : "";
        string line = rl.Length > 0 ? string.Format(T.slotLine, rl, LootCfg.SlotLabel(LootCfg.Slot(fi.slot) != null ? fi.slot : slot.id)) : LootCfg.SlotLabel(LootCfg.Slot(fi.slot) != null ? fi.slot : slot.id);
        LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), UiStr.Up(line), "serif", S.labelSize + ML.detail.lineSizeAdd, TextAnchor.UpperLeft, true, dim); yy += S.labelSize * S.boxLine + ML.detail.lineGapAdd;
        LootDraw.Fill(new Rect(x, yy, w, ML.detail.ruleH), LootCfg.Col(Kc.frameDim)); yy += S.descGap * ML.detail.ruleGapK;
        if (!string.IsNullOrEmpty(fi.desc))
        {
            float dh = LootDraw.Height(fi.desc, "serif", S.bodySize, w, false, true);
            LootDraw.Text(new Rect(x, yy, w, dh), fi.desc, "serif", S.bodySize, TextAnchor.UpperLeft, false, ink, true, true); yy += dh + S.descGap;
        }

        // compared-with line
        string cw = !compare ? "" : (vs != null ? string.Format(T.comparedWith, vs.name) : T.comparedEmpty);
        if (cw.Length > 0) { LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), UiStr.Up(cw), "serif", S.labelSize, TextAnchor.UpperLeft, true, gold); yy += S.labelSize * ML.detail.compareK; }

        // stats: keys in loot_ui.json order first, then any other key the items carry
        var keys = statKeys; keys.Clear();   // UIDATAB v1: reused list
        for (int i = 0; i < C.stats.Length; i++) { var sr = C.stats[i]; if (sr != null && sr.stat.Length > 0 && (fi.Has(sr.stat) || (vs != null && vs.Has(sr.stat)))) keys.Add(sr.stat); }
        AddExtra(keys, fi); AddExtra(keys, vs);
        if (keys.Count == 0) { LootDraw.Text(new Rect(x, yy, w, S.bodySize * S.boxLine), T.noStats, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true); return; }
        float labelW = w - S.statValueW - S.arrowSize - S.deltaW - ML.detail.statTrim;
        for (int i = 0; i < keys.Count; i++)
        {
            string k = keys[i]; var row = LootCfg.StatRow(k); float v = fi.Stat(k), o = vs != null ? vs.Stat(k) : 0f; int dec = row != null ? Mathf.Max(0, row.decimals) : 0; string fmt = dec < statFmts.Length ? statFmts[dec] : "F" + dec;   // UIDATAB v1: cached "F<n>"
            var rr = new Rect(x, yy, w, S.statRowH);
            if ((i & 1) == 0) LootDraw.Fill(rr, new Color(1f, 1f, 1f, S.zebraAlpha));
            LootDraw.Text(new Rect(x + ML.detail.statPadX, yy, labelW, S.statRowH), row != null && row.label.Length > 0 ? row.label : LootCfg.Pretty(k), "serif", S.statSize, TextAnchor.MiddleLeft, false, ink);
            float vx = x + ML.detail.statPadX + labelW;
            LootDraw.Text(new Rect(vx, yy, S.statValueW, S.statRowH), v.ToString(fmt), "serif", S.statSize, TextAnchor.MiddleRight, true, ink);
            if (compare)
            {
                float d = v - o; bool better = row == null || row.higherIsBetter ? d > 0f : d < 0f; var ar = new Rect(vx + S.statValueW + S.arrowGap, yy + (S.statRowH - S.arrowSize) * 0.5f, S.arrowSize, S.arrowSize);
                float eps = Mathf.Pow(10f, -dec) * 0.5f;
                if (Mathf.Abs(d) < eps) { LootDraw.Tex(new Rect(ar.x + ML.detail.dashInset, ar.y + ar.height * 0.5f - ML.detail.dashHalf, ar.width - ML.detail.dashTrim, ML.detail.dashH), LootDraw.Dash(), LootCfg.Col(Kc.even)); }
                else
                {
                    var col = better ? LootCfg.Col(Kc.better) : LootCfg.Col(Kc.worse); LootDraw.Tex(ar, LootDraw.Arrow(d > 0f), col);
                    LootDraw.Text(new Rect(ar.xMax + S.deltaGap, yy, S.deltaW, S.statRowH), (d > 0f ? ML.detail.plus : "") + d.ToString(fmt), "serif", S.statSize - ML.detail.deltaSizeSub, TextAnchor.MiddleLeft, false, col);
                }
            }
            yy += S.statRowH;
        }
    }

    static void AddExtra(List<string> keys, GearItem it)
    {
        if (it == null || it.stats == null) return;
        for (int i = 0; i < it.stats.Length; i++) { var s = it.stats[i]; if (s != null && s.stat.Length > 0 && !keys.Contains(s.stat)) keys.Add(s.stat); }
    }
}
