using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// REBIND v100 (lane laneREBIND): the rebinding half of the Controls tab. Partial class of MenuUI (the menu itself is laneMENU's file; this adds the rows, the key capture and its overlay).
//   Row per action of InputMap (controls.json "bindings"): the key slots (default 2: e.g. LMB / F), the gamepad column (fixed, read from the data), then "Restore default controls", the fixed keys, the slide rows.
//   Up / Down pick the action, Left / Right pick the key slot, Enter (A, click) waits for a key or a mouse button: Esc / B cancels, a gamepad button is refused (fixed), a reserved key is refused,
//   a key another action already uses asks Enter = swap (the other action gets the key this slot had) / Esc = cancel. Delete (pad Y) unbinds the slot. Everything is saved through settings.json.
// When the key map is off (no bindings block, harness) the old read-only key map of laneMENU is shown instead.
public sealed partial class MenuUI
{
    Rect[] bindCellBuf = new Rect[0];   // UIDATAB v1: reused cell array
    bool capturing, capAsking; int capFrame = -1, capEndFrame = -1, capSlot, bindSlot; InputMap.Act capAct; KeyCode capKey; string capWarn = "", capConflict = "";
    static bool CaptureBusy { get { return inst != null && (inst.capturing || inst.capEndFrame == Time.frameCount); } }

    // ---------------- pause item "newgame" (menu.json items, with a confirm box): erase the save slot and restart on the first opponent ----------------
    void NewGame()
    {
        SaveSlot.Erase();
        if (GameLoopDriver.Instance != null) { CloseMenu(true); GameLoop.Retry(); } else CloseMenu();   // same restart as "Restart fight": the ladder row is back at the start
    }

    // ---------------- rows ----------------
    List<Row> BindRows(MenuCfg.Data C)
    {
        if (!InputMap.On) return ControlRows(C);   // the read-only map
        var U = InputMap.Ui; var K = C.controls; var cd = ControlsTable.Get(); var list = new List<Row>();
        foreach (var a in InputMap.Actions)
        {
            var act = a; string pad = BindPadText(K, cd, act);
            list.Add(new Row { kind = "bind", bindId = act.id, label = act.label, hint = act.hint, c2 = pad, act = () => BeginCapture(act), step = d => { bindSlot = Wrap(bindSlot + d, InputMap.Slots); } });
        }
        list.Add(new Row { kind = "action", label = U.resetAll, hint = U.resetAllHint, act = () => InputMap.ResetAll() });
        foreach (var f in InputMap.Data.fixedRows) if (f != null) list.Add(new Row { kind = "info", label = f.label, c1 = f.keys, c2 = f.pad, hint = U.fixedHead });
        if (K.fromControlsJson && cd.enabled)
        {
            foreach (var m in cd.attack.map)
            {
                if (m == null || string.IsNullOrEmpty(m.dir)) continue;
                string l = m.light != null && m.light.Length > 0 ? GameLoop.Pretty(m.light[0]) : "-", h = m.heavy != null && m.heavy.Length > 0 ? GameLoop.Pretty(m.heavy[0]) : "-";
                list.Add(new Row { kind = "info", label = string.Format(K.slideAction, GameLoop.Pretty(m.dir)), c1 = string.Format(K.slideKeys, l, h), c2 = K.slidePad });
            }
            list.Add(new Row { kind = "info", label = K.schemeAction, c1 = GameLoop.Pretty(cd.scheme), c2 = "" });
        }
        return list;
    }
    static string BindPadText(MenuCfg.ControlsC K, ControlsTable.Data cd, InputMap.Act a)
    {
        var names = new List<string>();
        if (!string.IsNullOrEmpty(a.padFrom)) foreach (var k in PadKeys(cd, a.padFrom)) names.Add(PadName(K, k));
        foreach (var k in a.pad) names.Add(PadName(K, k.ToString()));
        if (!string.IsNullOrEmpty(a.padText)) names.Add(a.padText);
        return string.Join(" / ", names.ToArray());
    }
    string ControlsFooter(MenuCfg.Data C) { return InputMap.On ? InputMap.Ui.footer : C.strings.footerControls; }
    bool OnBindTab() { var T = MenuCfg.Get().tabs; return scr == Scr.Settings && tab >= 0 && tab < T.Length && T[tab] != null && T[tab].type == "controls" && InputMap.On; }

    // ---------------- geometry (shared by drawing and the mouse) ----------------
    Rect[] BindCells(Rect rr, MenuCfg.LayoutC L)
    {
        int n = InputMap.Slots; float x0 = rr.x + rr.width * L.colKeys, x1 = rr.x + rr.width * L.colPad - ML.controls.keysTrim, w = (x1 - x0) / n; if (bindCellBuf.Length != n) bindCellBuf = new Rect[n]; var c = bindCellBuf;   // UIDATAB v1: reused array
        for (int i = 0; i < n; i++) c[i] = new Rect(x0 + i * w + ML.controls.bindCellDx, rr.y + ML.controls.bindCellDy, w - ML.controls.bindCellWTrim, rr.height - ML.controls.bindCellHTrim);
        return c;
    }
    void BindClick(Row r, Rect rr, Vector2 p)
    {
        var cells = BindCells(rr, MenuCfg.Get().layout); int hit = -1;
        for (int i = 0; i < cells.Length; i++) if (cells[i].Contains(p)) hit = i;
        if (hit < 0) return;   // label / gamepad side: only selects the row
        bindSlot = hit; if (r.act != null) r.act();
    }

    // ---------------- drawing ----------------
    void DrawBindRow(Row r, Rect rr, bool sel, float lx, Color lab, Color val, MenuCfg.LayoutC L)
    {
        var a = InputMap.Find(r.bindId); if (a == null) return;
        bool noKey = true; foreach (var k0 in a.cur) if (k0 != KeyCode.None) noKey = false;
        HudDraw.Text(new Rect(lx, rr.y, rr.width * ML.controls.labelColK, rr.height), r.label, "serif", L.labelSize - ML.controls.sizeSub, TextAnchor.MiddleLeft, false, noKey ? HudCfg.Col(HudCfg.Get().colors.crimson) : lab);   // no key at all = crimson
        var cells = BindCells(rr, L); var fd = HudCfg.Col(HudCfg.Get().colors.frameDim); var cr = HudCfg.Col(HudCfg.Get().colors.crimson);
        for (int i = 0; i < cells.Length; i++)
        {
            bool cur = sel && i == bindSlot, waiting = capturing && capAct == a && capSlot == i;
            KeyCode k = i < a.cur.Length ? a.cur[i] : KeyCode.None;
            HudDraw.Fill(cells[i], new Color(0f, 0f, 0f, cur ? ML.controls.bindCellAlphaSel : ML.controls.bindCellAlpha));
            Outline(cells[i], cur ? ML.controls.bindOutlineSel : ML.controls.bindOutline, waiting ? cr : cur ? Gold() : fd);
            string t = waiting ? ML.controls.bindWaiting : InputMap.Name(k);
            HudDraw.Text(cells[i], t, "serif", L.valueSize - ML.controls.sizeSub, TextAnchor.MiddleCenter, false, k == KeyCode.None && !waiting ? Dim() : cur ? Gold() : val);
        }
        HudDraw.Text(new Rect(rr.x + rr.width * L.colPad, rr.y, rr.width * (1f - L.colPad) - ML.controls.padTrim, rr.height), r.c2, "serif", L.valueSize - ML.controls.sizeSub, TextAnchor.MiddleLeft, false, val);
    }

    void DrawCapture(float vw, float vh)
    {
        if (!capturing || capAct == null) return;
        var C = MenuCfg.Get(); var L = C.layout; var U = InputMap.Ui; float cx = vw * 0.5f, cy = vh * 0.5f;
        HudDraw.Fill(new Rect(0f, 0f, vw, vh), new Color(0f, 0f, 0f, ML.capture.dimAlpha));
        var box = new Rect(cx - L.confirmW * 0.5f, cy - L.confirmH * 0.5f, L.confirmW, L.confirmH);
        Frame(box);
        HudDraw.Caps(cx, box.y + L.confirmH * ML.capture.titleY, 0.5f, string.Format(U.captureTitle, capAct.label) + ML.capture.titleSep + string.Format(U.captureSlot, capSlot + 1), "title", L.confirmSize - ML.capture.titleSizeSub, L.confirmSmall - ML.capture.smallSizeSub, L.itemTracking, Ink());
        Rule(cx, box.y + L.confirmH * ML.capture.titleY + (L.confirmSize - ML.capture.titleSizeSub) * ML.capture.capsLine + ML.capture.ruleGap, L.ruleW * ML.confirm.ruleK);
        string line1 = capAsking ? string.Format(U.conflict, InputMap.Name(capKey), capConflict) : U.capturePress;
        string line2 = capAsking ? U.conflictHint : U.captureCancel;
        var sh = HudCfg.Col(HudCfg.Get().colors.shadow);
        HudDraw.Text(new Rect(box.x + ML.capture.marginX, box.y + L.confirmH * ML.capture.lineY, box.width - ML.capture.marginW, L.labelSize * ML.capture.lineK), line1, "serif", L.labelSize - ML.controls.sizeSub, TextAnchor.MiddleCenter, false, capAsking ? Gold() : Ink());
        if (!string.IsNullOrEmpty(capWarn)) HudDraw.Text(new Rect(box.x + ML.capture.marginX, box.y + L.confirmH * ML.capture.lineY + L.labelSize * ML.capture.lineK, box.width - ML.capture.marginW, L.labelSize * ML.capture.warnK), capWarn, "serif", L.hintSize, TextAnchor.MiddleCenter, false, HudCfg.Col(HudCfg.Get().colors.crimson));
        Footer(new Rect(box.x, box.yMax - ML.confirm.footerOff, box.width, ML.confirm.footerH), line2);
    }

    // ---------------- capture ----------------
    void BeginCapture(InputMap.Act a)
    {
        if (a == null) return;
        capturing = true; capAsking = false; capAct = a; capSlot = Mathf.Clamp(bindSlot, 0, Math.Max(0, a.cur.Length - 1)); capFrame = Time.frameCount; capWarn = ""; capConflict = "";
    }
    void EndCapture() { capturing = false; capAsking = false; capAct = null; capWarn = ""; capEndFrame = Time.frameCount; }

    /// <summary>Called from Update before the menu's own keys. Returns true while a key capture (or an unbind) consumed this frame.</summary>
    bool UpdateBind()
    {
        if (!capturing)
        {
            if (OnBindTab() && rowSel >= 0 && rowSel < rows.Count && rows[rowSel].bindId.Length > 0)
            {
                foreach (var kc in InputMap.Data.ui.clearKeys)
                {
                    KeyCode k; if (!Enum.TryParse(kc, true, out k) || !Input.GetKeyDown(k)) continue;
                    var a = InputMap.Find(rows[rowSel].bindId); if (a != null) InputMap.Clear(a, bindSlot);
                    return true;
                }
            }
            return false;
        }
        if (!InputMap.On || capAct == null) { EndCapture(); return true; }
        if (Time.frameCount <= capFrame) return true;   // the press that started the capture
        if (HudCfg.AnyDown(kBack)) { EndCapture(); return true; }   // Esc / pad B
        if (capAsking)
        {
            if (HudCfg.AnyDown(kConfirm)) { InputMap.Assign(capAct, capSlot, capKey); AfterAssign(); }
            return true;
        }
        foreach (var k in InputMap.AllCodes)
        {
            if (k == KeyCode.None || !Input.GetKeyDown(k)) continue;
            var U = InputMap.Ui;
            if (InputMap.IsPad(k)) { capWarn = U.padFixed; return true; }
            if (InputMap.IsReserved(k)) { capWarn = string.Format(U.reserved, InputMap.Name(k)); return true; }
            if (capSlot < capAct.cur.Length && capAct.cur[capSlot] == k) { EndCapture(); return true; }   // the same key again
            var conf = InputMap.Conflicts(capAct, k);
            if (conf.Count == 0) { InputMap.Assign(capAct, capSlot, k); AfterAssign(); return true; }
            capKey = k; capAsking = true; capWarn = ""; var names = new List<string>(); foreach (var o in conf) names.Add(o.label); capConflict = string.Join(", ", names.ToArray());
            return true;
        }
        return true;
    }
    void AfterAssign() { EndCapture(); }   // an action a swap left without a key shows its name in crimson (DrawBindRow)
}
