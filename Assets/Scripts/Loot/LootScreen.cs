using System;
using System.Collections.Generic;
using UnityEngine;

// LOOTUI v85: the equip screen. Tab / Start opens it (outside a live fight; Tab is the lock-on key while fighting), three columns:
//   Worn      the 8 slots (4 armour + main / off hand + 2 remnant slots, loot_ui.json slots[]) with the item in each
//   Carried   every owned item that fits the selected slot, plus an Unequip row
//   Details   name (serif, rarity colour), slot, flavour text and the stats, with an up / down arrow and the difference against what is worn in that slot
// Keys (loot_ui.json keys{}): arrows / WASD / stick move, left / right switch column, E / pad A equip (again = take off), U / pad X take off, Tab / Start / Esc / pad B close; the mouse clicks rows
// (double click equips). While open Duel.Frozen is set (no brain decides, the player cannot move) and LootUI.Blocking is true (GameLoop must not read Enter / A / B, see MANIFEST hunk).
public static class LootScreen
{
    public static bool Open { get; private set; }
    public static int ClosedFrame = -1;

    static int focus, slotIdx, candIdx, candScroll; static readonly List<GearItem> cands = new List<GearItem>();
    static float openAt; static bool ownFreeze, cursorSaved; static int unfreezeFrame = -1; static CursorLockMode prevLock; static bool prevVis;
    static string message = ""; static float messageAt;
    static object keyOwner; static KeyCode[] kToggle, kClose, kConfirm, kUnequip, kUp, kDown, kLeft, kRight;
    static int lastDx, lastDy; static float nextNav; static bool axisBroken;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { Open = false; ClosedFrame = -1; focus = slotIdx = candIdx = candScroll = 0; cands.Clear(); ownFreeze = false; unfreezeFrame = -1; cursorSaved = false; keyOwner = null; axisBroken = false; message = ""; }

    static void EnsureKeys()
    {
        var K = LootCfg.Get().keys; if (ReferenceEquals(keyOwner, K)) return; keyOwner = K;
        kToggle = LootCfg.Keys(K.toggle); kClose = LootCfg.Keys(K.close); kConfirm = LootCfg.Keys(K.confirm); kUnequip = LootCfg.Keys(K.unequip);
        kUp = LootCfg.Keys(K.up); kDown = LootCfg.Keys(K.down); kLeft = LootCfg.Keys(K.left); kRight = LootCfg.Keys(K.right);
    }
    public static KeyCode[] PickupKeys { get { return LootCfg.Keys(LootCfg.Get().keys.pickup); } }

    // ---- open / close ----
    public static bool CanOpen(bool demo)
    {
        var S = LootCfg.Get().screen;
        if (LootCfg.Get().slots.Length == 0) return false;
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
        slotIdx = Mathf.Clamp(slotIdx, 0, Mathf.Max(0, LootCfg.Get().slots.Length - 1)); Rebuild(true);
        if (!Duel.Frozen) { Duel.Frozen = true; ownFreeze = true; }
        if (!Application.isBatchMode) { prevLock = Cursor.lockState; prevVis = Cursor.visible; cursorSaved = true; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        lastDx = lastDy = 0;
    }
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
        cands.Clear(); cands.Add(null);
        var s = SlotAt(slotIdx); if (s != null)
        {
            var own = LootSave.Owned;
            for (int i = 0; i < own.Count; i++) { var it = Item(own[i]); if (it != null && LootCfg.Fits(it, s)) cands.Add(it); }
        }
        if (pickWorn || candIdx >= cands.Count)
        {
            candIdx = cands.Count > 1 ? 1 : 0;
            var w = s != null ? LootBridge.Slots.GetEquipped(s.id) : "";
            if (!string.IsNullOrEmpty(w)) for (int i = 1; i < cands.Count; i++) if (cands[i].id == w) { candIdx = i; break; }
        }
        candScroll = 0;
    }
    static void Say(string t) { message = t ?? ""; messageAt = Time.unscaledTime; }

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
        if (now != null && now != slotId) { for (int i = 0; i < LootCfg.Get().slots.Length; i++) if (LootCfg.Get().slots[i].id == now) { slotIdx = i; Rebuild(true); break; } }   // the wearer chose another slot of the group: follow it
    }
    static void Confirm()
    {
        var s = SlotAt(slotIdx); if (s == null) return;
        if (focus == 0) { focus = 1; return; }
        if (candIdx <= 0 || candIdx >= cands.Count) DoUnequip(s.id); else DoEquip(cands[candIdx], s.id);
    }
    static void SelectSlot(int i) { if (i == slotIdx) return; slotIdx = i; Rebuild(true); }

    // ---- input (every frame, from LootUI.Update) ----
    public static void Tick(bool demo)
    {
        EnsureKeys(); var K = LootCfg.Get().keys;
        if (unfreezeFrame >= 0 && Time.frameCount >= unfreezeFrame) { unfreezeFrame = -1; if (ownFreeze) { Duel.Frozen = false; ownFreeze = false; } }
        if (!Open) { if (LootCfg.AnyDown(kToggle) && CanOpen(demo)) OpenScreen(); return; }
        if (LootCfg.AnyDown(kToggle) || LootCfg.AnyDown(kClose)) { CloseScreen(); return; }
        if (Time.unscaledTime - messageAt > LootCfg.Get().screen.messageS /* HCKIT6 v100 */) message = "";
        if (LootCfg.AnyDown(kConfirm)) Confirm();
        if (LootCfg.AnyDown(kUnequip)) { var s = SlotAt(slotIdx); if (s != null) DoUnequip(s.id); }
        int dx, dy; Nav(K, out dx, out dy);
        if (dx < 0) focus = 0; else if (dx > 0 && cands.Count > 0) focus = 1;
        if (dy != 0)
        {
            if (focus == 0) SelectSlot(Mathf.Clamp(slotIdx + dy, 0, Mathf.Max(0, LootCfg.Get().slots.Length - 1)));
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
        var C = LootCfg.Get(); var S = C.screen; var Kc = C.colors; var T = C.text; var ev = Event.current;
        float a = Mathf.Clamp01((Time.unscaledTime - openAt) / Mathf.Max(0.01f, S.openFadeS)); LootDraw.Alpha = a;
        Color ink = LootCfg.Col(Kc.ink), dim = LootCfg.Col(Kc.dim), gold = LootCfg.Col(Kc.gold), crim = LootCfg.Col(Kc.crimson), frame = LootCfg.Col(Kc.frame), frameDim = LootCfg.Col(Kc.frameDim);
        LootDraw.Fill(new Rect(0, 0, vw, vh), LootCfg.Col(Kc.dimmer));
        float px = (vw - S.panelW) * 0.5f, py = (vh - S.panelH) * 0.5f; var P = new Rect(px, py, S.panelW, S.panelH);
        LootDraw.Fill(P, LootCfg.Col(Kc.panel)); LootDraw.Frame(P, frame, S.frameW); LootDraw.Frame(new Rect(P.x + S.insetPx, P.y + S.insetPx, P.width - 2f * S.insetPx, P.height - 2f * S.insetPx), frameDim, S.insetFrameW);

        // title
        LootDraw.Text(new Rect(px + S.pad, py + S.titleY, S.panelW - 2f * S.pad, S.titleSize * S.titleLine), T.title.ToUpperInvariant(), "serif", S.titleSize, TextAnchor.UpperLeft, true, gold);
        LootDraw.Text(new Rect(px + S.pad + S.subtitleDx, py + S.titleY + S.titleSize * S.subtitleDy, S.panelW - 2f * S.pad, S.subtitleSize * S.boxLine), T.subtitle, "serif", S.subtitleSize, TextAnchor.UpperLeft, false, dim, true);
        var closeR = new Rect(P.xMax - S.pad - S.closeBox, py + S.titleY, S.closeBox, S.closeBox);
        LootDraw.Frame(closeR, frameDim, 1f); LootDraw.Text(closeR, T.close, "serif", S.closeSize, TextAnchor.MiddleCenter, true, dim);
        if (GUI.Button(closeR, GUIContent.none, GUIStyle.none)) CloseScreen();

        float c1 = px + S.pad, c2 = c1 + S.slotColW + S.colGap, c3 = c2 + S.candColW + S.colGap, w3 = P.xMax - S.pad - c3;
        float yh = py + S.headerY, y0 = py + S.bodyTop, bodyH = S.panelH - S.bodyTop - S.bodyBottom;
        LootDraw.Fill(new Rect(c1, yh + S.headerSize * S.boxLine, P.xMax - S.pad - c1, S.ruleH), LootCfg.Col(Kc.crimson, S.ruleAlpha * a));
        LootDraw.Text(new Rect(c1, yh, S.slotColW, S.headerSize * S.boxLine), T.slotsHeader.ToUpperInvariant(), "serif", S.headerSize, TextAnchor.UpperLeft, true, focus == 0 ? gold : dim);
        LootDraw.Text(new Rect(c2, yh, S.candColW, S.headerSize * S.boxLine), T.candHeader.ToUpperInvariant(), "serif", S.headerSize, TextAnchor.UpperLeft, true, focus == 1 ? gold : dim);
        LootDraw.Text(new Rect(c3, yh, w3, S.headerSize * S.boxLine), T.detailHeader.ToUpperInvariant(), "serif", S.headerSize, TextAnchor.UpperLeft, true, dim);

        // slots
        var defs = C.slots;
        for (int i = 0; i < defs.Length; i++)
        {
            var s = defs[i]; if (s == null) continue;
            var r = new Rect(c1, y0 + i * (S.slotRowH + S.rowGap), S.slotColW, S.slotRowH); bool sel = i == slotIdx; var w = Worn(s.id);
            LootDraw.Fill(r, sel ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
            LootDraw.Frame(r, sel ? (focus == 0 ? frame : frameDim) : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), 1f);
            if (sel) LootDraw.Fill(new Rect(r.x, r.y, S.accentBar, r.height), crim);
            LootDraw.Text(new Rect(r.x + S.rowPadX, r.y + S.rowPadY, r.width - S.rowTrim, S.labelSize * S.labelLine), (s.label.Length > 0 ? s.label : LootCfg.Pretty(s.id)).ToUpperInvariant(), "serif", S.labelSize, TextAnchor.UpperLeft, true, sel ? gold : dim);
            var nr = new Rect(r.x + S.rowPadX, r.y + S.rowPadY + S.labelSize * S.labelLine + S.nameGap, r.width - S.nameTrim, S.rowNameSize * S.nameLine);
            if (w != null) LootDraw.Text(nr, w.name, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, LootCfg.RarityColor(w));
            else LootDraw.Text(nr, T.emptySlot, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, dim, true);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { SelectSlot(i); focus = 0; if (ev.clickCount >= 2) focus = 1; }
        }

        // candidates (scrolled so the cursor row is always visible)
        int maxRows = Mathf.Max(1, Mathf.FloorToInt(bodyH / (S.candRowH + S.rowGap)));
        if (candIdx < candScroll) candScroll = candIdx; if (candIdx >= candScroll + maxRows) candScroll = candIdx - maxRows + 1;
        string wornHere = SlotAt(slotIdx) != null ? LootBridge.Slots.GetEquipped(SlotAt(slotIdx).id) : "";
        for (int row = 0; row < maxRows; row++)
        {
            int i = candScroll + row; if (i >= cands.Count) break;
            var it = cands[i]; var r = new Rect(c2, y0 + row * (S.candRowH + S.rowGap), S.candColW, S.candRowH); bool sel = i == candIdx;
            LootDraw.Fill(r, sel ? LootCfg.Col(Kc.rowSelected) : LootCfg.Col(Kc.charcoal));
            LootDraw.Frame(r, sel ? (focus == 1 ? frame : frameDim) : new Color(frameDim.r, frameDim.g, frameDim.b, frameDim.a * S.idleFrameAlpha), 1f);
            if (sel) LootDraw.Fill(new Rect(r.x, r.y, S.accentBar, r.height), crim);
            if (it == null) LootDraw.Text(new Rect(r.x + 14f, r.y, r.width - 20f, r.height), T.emptyRow, "serif", S.rowNameSize, TextAnchor.MiddleLeft, false, dim, true);
            else
            {
                bool here = it.id == wornHere; string other = here ? null : SlotWearing(it.id);
                LootDraw.Text(new Rect(r.x + 14f, r.y + 5f, r.width - 100f, S.rowNameSize * 1.4f), it.name, "serif", S.rowNameSize, TextAnchor.UpperLeft, false, LootCfg.RarityColor(it));
                string sub = here ? "" : other != null ? string.Format(T.elsewhere, LootCfg.SlotLabel(other)) : "";
                if (sub.Length > 0) LootDraw.Text(new Rect(r.x + 14f, r.y + 5f + S.rowNameSize * 1.35f, r.width - 20f, S.labelSize * 1.5f), sub, "serif", S.labelSize, TextAnchor.UpperLeft, false, dim, true);
                if (here) LootDraw.Text(new Rect(r.xMax - 92f, r.y, 80f, r.height), T.equipped.ToUpperInvariant(), "serif", S.badgeSize, TextAnchor.MiddleRight, true, gold);
            }
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) { candIdx = i; focus = 1; if (ev.clickCount >= 2) Confirm(); }
        }
        if (cands.Count <= 1) LootDraw.Text(new Rect(c2 + 6f, y0 + S.candRowH + S.rowGap + 8f, S.candColW, S.bodySize * S.boxLine), T.nothing, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true);

        DrawDetail(c3, y0, w3, bodyH);

        // footer
        string hint = T.moveKeys + "     " + T.equipKeys + "     " + T.unequipKeys + "     " + T.closeKeys;
        LootDraw.Text(new Rect(px, P.yMax - S.hintY, S.panelW, S.hintSize * S.boxLine), hint, "serif", S.hintSize, TextAnchor.UpperCenter, false, dim);
        if (message.Length > 0) LootDraw.Text(new Rect(px, P.yMax - S.hintY - S.hintSize * 2.2f, S.panelW, S.hintSize * 1.8f), message, "serif", S.hintSize + 2, TextAnchor.UpperCenter, true, crim);

        // modal: nothing under the panel (GameLoop's Retry / Next buttons) sees a click
        if (ev.type == EventType.MouseDown) ev.Use();
        LootDraw.Alpha = 1f;
    }

    static void DrawDetail(float x, float y, float w, float h)
    {
        var C = LootCfg.Get(); var S = C.screen; var Kc = C.colors; var T = C.text;
        Color ink = LootCfg.Col(Kc.ink), dim = LootCfg.Col(Kc.dim), gold = LootCfg.Col(Kc.gold);
        var slot = SlotAt(slotIdx); if (slot == null) return;
        GearItem worn = Worn(slot.id), fi, vs; bool compare;
        if (focus == 0) { fi = worn; vs = null; compare = false; }
        else if (candIdx <= 0 || candIdx >= cands.Count) { fi = worn; vs = null; compare = false; }   // the Unequip row: show what comes off
        else { fi = cands[candIdx]; vs = worn; compare = fi.id != (worn != null ? worn.id : ""); }
        bool removing = focus == 1 && candIdx == 0 && worn != null;
        if (fi == null) { LootDraw.Text(new Rect(x, y, w, S.bodySize * S.boxLine), focus == 0 ? T.emptySlot : T.selectItem, "serif", S.bodySize + 2, TextAnchor.UpperLeft, false, dim, true); return; }

        float yy = y;
        if (removing) { LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), T.removing.ToUpperInvariant(), "serif", S.labelSize, TextAnchor.UpperLeft, true, LootCfg.Col(Kc.crimson)); yy += S.labelSize * 1.7f; }
        int ns = Mathf.RoundToInt(S.nameSize * 1.35f);
        LootDraw.Text(new Rect(x, yy, w, ns * 1.4f), fi.name, "serif", ns, TextAnchor.UpperLeft, true, LootCfg.RarityColor(fi)); yy += ns * 1.4f + S.detailNameGap;
        var rar = LootCfg.Rarity(fi.rarity); string rl = rar != null && rar.label.Length > 0 ? rar.label : "";
        string line = rl.Length > 0 ? string.Format(T.slotLine, rl, LootCfg.SlotLabel(LootCfg.Slot(fi.slot) != null ? fi.slot : slot.id)) : LootCfg.SlotLabel(LootCfg.Slot(fi.slot) != null ? fi.slot : slot.id);
        LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), line.ToUpperInvariant(), "serif", S.labelSize + 1, TextAnchor.UpperLeft, true, dim); yy += S.labelSize * S.boxLine + 6f;
        LootDraw.Fill(new Rect(x, yy, w, 1f), LootCfg.Col(Kc.frameDim)); yy += S.descGap * 0.6f;
        if (!string.IsNullOrEmpty(fi.desc))
        {
            float dh = LootDraw.Height(fi.desc, "serif", S.bodySize, w, false, true);
            LootDraw.Text(new Rect(x, yy, w, dh), fi.desc, "serif", S.bodySize, TextAnchor.UpperLeft, false, ink, true, true); yy += dh + S.descGap;
        }

        // compared-with line
        string cw = !compare ? "" : (vs != null ? string.Format(T.comparedWith, vs.name) : T.comparedEmpty);
        if (cw.Length > 0) { LootDraw.Text(new Rect(x, yy, w, S.labelSize * S.boxLine), cw.ToUpperInvariant(), "serif", S.labelSize, TextAnchor.UpperLeft, true, gold); yy += S.labelSize * 1.9f; }

        // stats: keys in loot_ui.json order first, then any other key the items carry
        var keys = new List<string>();
        for (int i = 0; i < C.stats.Length; i++) { var sr = C.stats[i]; if (sr != null && sr.stat.Length > 0 && (fi.Has(sr.stat) || (vs != null && vs.Has(sr.stat)))) keys.Add(sr.stat); }
        AddExtra(keys, fi); AddExtra(keys, vs);
        if (keys.Count == 0) { LootDraw.Text(new Rect(x, yy, w, S.bodySize * S.boxLine), T.noStats, "serif", S.bodySize, TextAnchor.UpperLeft, false, dim, true); return; }
        float labelW = w - S.statValueW - S.arrowSize - S.deltaW - 24f;
        for (int i = 0; i < keys.Count; i++)
        {
            string k = keys[i]; var row = LootCfg.StatRow(k); float v = fi.Stat(k), o = vs != null ? vs.Stat(k) : 0f; int dec = row != null ? Mathf.Max(0, row.decimals) : 0; string fmt = "F" + dec;
            var rr = new Rect(x, yy, w, S.statRowH);
            if ((i & 1) == 0) LootDraw.Fill(rr, new Color(1f, 1f, 1f, S.zebraAlpha));
            LootDraw.Text(new Rect(x + 8f, yy, labelW, S.statRowH), row != null && row.label.Length > 0 ? row.label : LootCfg.Pretty(k), "serif", S.statSize, TextAnchor.MiddleLeft, false, ink);
            float vx = x + 8f + labelW;
            LootDraw.Text(new Rect(vx, yy, S.statValueW, S.statRowH), v.ToString(fmt), "serif", S.statSize, TextAnchor.MiddleRight, true, ink);
            if (compare)
            {
                float d = v - o; bool better = row == null || row.higherIsBetter ? d > 0f : d < 0f; var ar = new Rect(vx + S.statValueW + S.arrowGap, yy + (S.statRowH - S.arrowSize) * 0.5f, S.arrowSize, S.arrowSize);
                float eps = Mathf.Pow(10f, -dec) * 0.5f;
                if (Mathf.Abs(d) < eps) { LootDraw.Tex(new Rect(ar.x + 2f, ar.y + ar.height * 0.5f - 1f, ar.width - 4f, 2f), LootDraw.Dash(), LootCfg.Col(Kc.even)); }
                else
                {
                    var col = better ? LootCfg.Col(Kc.better) : LootCfg.Col(Kc.worse); LootDraw.Tex(ar, LootDraw.Arrow(d > 0f), col);
                    LootDraw.Text(new Rect(ar.xMax + S.deltaGap, yy, S.deltaW, S.statRowH), (d > 0f ? "+" : "") + d.ToString(fmt), "serif", S.statSize - 2, TextAnchor.MiddleLeft, false, col);
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
