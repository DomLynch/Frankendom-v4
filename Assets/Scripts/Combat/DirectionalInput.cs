using UnityEngine;

// Swipe controls (lane CTRL, v68; data Assets/Data/controls.json via ControlsTable). Added by PlayerBrain at Awake and driven from PlayerBrain.Update (order -200), old Input Manager like the rest of the project.
// Locked on + scheme "swipe": LMB / F / pad RB with no slide = stab, slide left / right = slash, slide up = overhead (the slide is summed from flickWindowMs before the press until the move is decided);
// release before holdHeavyMs = light, hold = heavy (charges while held). Guard (RMB / Q / pad LB): the slide (mouse) or the right stick picks the side (controls.json guard.sides).
// Lock off (or scheme "classic"): PlayerBrain keeps the old WASD + click rules and the mouse turns the camera. Also: combat camera auto lock enter / exit, pad specials, the direction marker (OnGUI).
public class DirectionalInput : MonoBehaviour
{
    // CameraRig reads this: true while the mouse is the swipe direction (it then leaves yaw / pitch alone)
    static bool reserved; static int reservedFrame = -9;
    public static bool MouseReserved => reserved && reservedFrame >= Time.frameCount - 1;
    // CameraRig lock blend speed per second (controls.json camera.easeIn / easeOut; 3 = the old value)
    static DuelFighter swipeFighter;
    public static bool IsSwiping(DuelFighter f) { return f != null && MouseReserved && ReferenceEquals(swipeFighter, f); }
    public static float LockEase(bool lockingIn) { var C = ControlsTable.Get(); if (!C.enabled) return 3f; return Mathf.Max(0.01f, lockingIn ? C.camera.easeIn : C.camera.easeOut); }

    DuelFighter df; CameraRig rig; Transform camT;
    const int N = 256;
    readonly float[] mt = new float[N]; readonly Vector2[] md = new Vector2[N], ps = new Vector2[N]; int head = -1, count;
    Vector2 mouseNow, stickNow; bool padOk, padChecked; int sampleFrame = -9;
    KeyCode[] kAtk, kGuard, kSp1, kSp2; object keySrc;

    // strike
    float pressT = -1f, heavyT; bool fromPad, heavyFired, lightOnDown; string firedDir = "", firedId;
    // guard
    Vector2 guardCur; bool guardWas; int gSide;
    // combat camera
    bool camInit, startDone, inCombat; float farT;

    public bool SwipeNow { get; private set; }
    public bool PadGuardHeld { get { return padOn && AnyHeld(kGuard); } }
    public bool PadAttackHeld { get { return padOn && AnyHeld(kAtk); } }
    public bool PadAttackDown { get { return padOn && AnyDown(kAtk); } }
    bool padOn { get { var C = ControlsTable.Get(); return C.enabled && C.pad.enabled; } }

    void OnDisable() { reserved = false; }

    // ---------------- per frame (PlayerBrain.Update, before the attack / guard code and before FlowTable.PlayerInput) ----------------
    public void Sample(CameraRig r, DuelFighter f, Transform cam)
    {
        rig = r; df = f; camT = cam; sampleFrame = Time.frameCount;
        var C = ControlsTable.Get();
        EnsureKeys(C);
        float now = Time.unscaledTime;
        mouseNow = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * C.mouse.axisToPx;
        stickNow = ReadStick(C);
        head = (head + 1) % N; mt[head] = now; md[head] = mouseNow; ps[head] = stickNow; if (count < N) count++;
        CombatCam(C);
        bool locked = rig != null && rig.lockEnabled && df != null && df.opponent != null && !df.opponent.Dead;
        SwipeNow = C.enabled && C.scheme == "swipe" && locked;
        reserved = SwipeNow; reservedFrame = Time.frameCount; swipeFighter = df;
        if (!SwipeNow) { ResetStrike(); }
        if (df != null && padOn)
        {
            if (AnyDown(kSp1)) df.Press("special1");
            if (AnyDown(kSp2)) df.Press("special2");
        }
    }

    Vector2 ReadStick(ControlsTable.Data C)
    {
        if (!C.enabled || !C.pad.enabled) return Vector2.zero;
        if (!padChecked)
        {
            padChecked = true; padOk = true;
            try { Input.GetAxisRaw(C.pad.stickX); Input.GetAxisRaw(C.pad.stickY); }
            catch (System.Exception) { padOk = false; Debug.LogWarning("[DirInput] Input Manager has no axis '" + C.pad.stickX + "' / '" + C.pad.stickY + "': right stick off (Tools > Controls > Add Right Stick Axes)"); }
        }
        if (!padOk) return Vector2.zero;
        float y = Input.GetAxisRaw(C.pad.stickY); if (C.pad.invertY) y = -y;
        return new Vector2(Input.GetAxisRaw(C.pad.stickX), y);
    }

    void EnsureKeys(ControlsTable.Data C)
    {
        if (ReferenceEquals(keySrc, C)) return;
        keySrc = C;
        kAtk = ControlsTable.Keys(C.pad.attackKeys); kGuard = ControlsTable.Keys(C.pad.guardKeys); kSp1 = ControlsTable.Keys(C.pad.special1Keys); kSp2 = ControlsTable.Keys(C.pad.special2Keys);
    }
    static bool AnyDown(KeyCode[] ks) { if (ks == null) return false; for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }
    static bool AnyHeld(KeyCode[] ks) { if (ks == null) return false; for (int i = 0; i < ks.Length; i++) if (Input.GetKey(ks[i])) return true; return false; }

    // mouse slide summed over the samples at or after t0 (px, y up)
    Vector2 MouseSince(float t0)
    {
        Vector2 s = Vector2.zero;
        for (int k = 0, i = head; k < count; k++, i = (i - 1 + N) % N) { if (mt[i] < t0) break; s += md[i]; }
        return s;
    }
    // strongest right stick sample at or after t0 (the stick springs back after a flick)
    Vector2 StickSince(float t0)
    {
        Vector2 best = Vector2.zero;
        for (int k = 0, i = head; k < count; k++, i = (i - 1 + N) % N) { if (mt[i] < t0) break; if (ps[i].sqrMagnitude > best.sqrMagnitude) best = ps[i]; }
        return best;
    }

    // ---------------- attack direction ----------------
    public static string Classify(Vector2 v, float dead)
    {
        var A = ControlsTable.Get().attack;
        if (v.magnitude < dead) return A.noSlideDir;
        float ang = ControlsTable.AngleOf(v);
        for (int i = 0; i < A.sectors.Length; i++) { var s = A.sectors[i]; if (s != null && ControlsTable.Diff(ang, s.deg) <= s.halfWidthDeg) return s.name; }
        return A.noSlideDir;
    }
    string DirFrom(float pressTime, bool pad)
    {
        var C = ControlsTable.Get(); float t0 = pressTime - C.mouse.flickWindowMs / 1000f;
        return pad ? Classify(StickSince(t0), C.pad.deadZone) : Classify(MouseSince(t0), C.mouse.deadZonePx);
    }
    string MoveFor(string dir, bool heavy)
    {
        var M = ControlsTable.Get().attack.map;
        for (int i = 0; i < M.Length; i++) if (M[i] != null && M[i].dir == dir) { string id = df.FirstMove(heavy ? M[i].heavy : M[i].light); if (id != null) return id; break; }
        return heavy ? df.FirstMove(new[] { "heavy_overhead" }) : null;   // no row / no move: the old default (alternating light, overhead heavy)
    }
    void Fire(string id) { if (id == null) df.Press("light"); else df.PressDirected(id); firedId = id; }
    float HoldSeconds()
    {
        var A = ControlsTable.Get().attack;
        bool counter = A.useCounterHeavyHold && A.heavyHoldMsInCounterWindow > 0f && df.opponent != null && df.opponent.InWindup;
        return (counter ? A.heavyHoldMsInCounterWindow : A.holdHeavyMs) / 1000f;
    }

    public void ResetStrike() { pressT = -1f; heavyFired = false; lightOnDown = false; firedId = null; firedDir = ""; }

    // swipe strike (only while SwipeNow): tap = light, hold = heavy, direction from the slide
    public void StrikeStep()
    {
        if (df == null) return;
        var C = ControlsTable.Get(); var A = C.attack; float now = Time.unscaledTime;
        bool mDown = Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.F), mHeld = Input.GetMouseButton(0) || Input.GetKey(KeyCode.F);
        bool pDown = PadAttackDown, pHeld = PadAttackHeld;
        bool held = mHeld || pHeld;
        if ((mDown || pDown) && pressT < 0f)
        {
            pressT = now; fromPad = pDown && !mDown; heavyFired = false; lightOnDown = false; firedId = null; firedDir = "";
            if (A.strikeFire == "down") { firedDir = DirFrom(pressT, fromPad); Fire(MoveFor(firedDir, false)); lightOnDown = true; }
        }
        if (pressT < 0f) { df.heavyHeld = Input.GetKey(KeyCode.G); return; }   // G = heavy key, as in the classic scheme
        if (held && !heavyFired && now - pressT >= HoldSeconds())
        {
            string dir = DirFrom(pressT, fromPad), id = MoveFor(dir, true);
            if (lightOnDown)
            {
                if (id != null && id != firedId) df.RestartDirected(id, CombatFeel.Get().input.holdToHeavyUntilTick);   // too late (the light is already active) = stays the light
            }
            else Fire(id);
            heavyFired = true; heavyT = now; firedDir = dir; firedId = id;
        }
        if (heavyFired && held && now - heavyT <= A.heavyGraceMs / 1000f)   // heavy grace: a late slide re-aims the heavy
        {
            string d2 = DirFrom(pressT, fromPad);
            if (d2 != firedDir)
            {
                string id2 = MoveFor(d2, true);
                if (id2 != null && id2 != firedId)
                {
                    int maxTick = Mathf.Max(1, Mathf.RoundToInt(A.heavyGraceMs * 60f / 1000f));
                    if (df.state == FState.Windup && df.CurrentMove != null && df.CurrentMove.id == firedId) { if (df.RestartDirected(id2, maxTick)) { firedDir = d2; firedId = id2; } }
                    else if (df.FlowAttackQueued) { df.PressDirected(id2); firedDir = d2; firedId = id2; }   // still in the buffer: replace it
                }
            }
        }
        df.heavyHeld = held && heavyFired;
        if (!held)
        {
            if (!heavyFired && !lightOnDown) { firedDir = DirFrom(pressT, fromPad); Fire(MoveFor(firedDir, false)); }
            if (C.debugLog) Debug.Log("[DirInput] strike " + firedDir + " " + (heavyFired ? "heavy" : "light") + " -> " + (firedId ?? "light(default)") + " held " + ((now - pressT) * 1000f).ToString("0") + " ms");
            pressT = -1f; df.heavyHeld = false; heavyFired = false; lightOnDown = false;
        }
    }

    // ---------------- guard side (while SwipeNow) ----------------
    int GuardClassify(Vector2 v, int cur)
    {
        var G = ControlsTable.Get().guard; float ang = ControlsTable.AngleOf(v);
        for (int i = 0; i < G.sides.Length; i++)
        {
            var s = G.sides[i]; if (s == null) continue;
            float d = ControlsTable.Diff(ang, s.deg); if (d > s.halfWidthDeg) continue;
            if (s.id == cur) return cur;
            if (cur != 0 && d > s.halfWidthDeg - G.deadBandDeg) return cur;   // hysteresis: stay until the new side is matched well inside its sector
            return s.id;
        }
        return cur;
    }
    public int GuardStep(bool held)
    {
        var C = ControlsTable.Get(); var G = C.guard;
        if (!held) { guardWas = false; gSide = 0; return 0; }
        bool pad = PadGuardHeld && padOk;
        if (!guardWas)
        {
            guardWas = true; gSide = 0;
            guardCur = Vector2.ClampMagnitude(MouseSince(Time.unscaledTime - C.mouse.flickWindowMs / 1000f), C.mouse.guardClampPx);   // a flick just before the press counts
            if (pad) { Vector2 s = StickSince(Time.unscaledTime - C.mouse.flickWindowMs / 1000f); if (s.magnitude >= C.pad.deadZone) gSide = GuardClassify(s, 0); }
            else if (guardCur.magnitude >= C.mouse.deadZonePx) gSide = GuardClassify(guardCur, 0);
            return gSide;
        }
        if (pad)
        {
            if (stickNow.magnitude >= C.pad.deadZone) gSide = GuardClassify(stickNow, gSide);
            else if (!C.pad.guardLatch) gSide = 0;
        }
        else
        {
            guardCur = Vector2.ClampMagnitude(guardCur + mouseNow, C.mouse.guardClampPx);
            if (guardCur.magnitude >= C.mouse.deadZonePx) gSide = GuardClassify(guardCur, gSide);
            else if (G.snap != "latch") gSide = 0;
        }
        return gSide;
    }

    // ---------------- combat camera: auto lock enter / exit (controls.json camera) ----------------
    void CombatCam(ControlsTable.Data C)
    {
        var K = C.camera;
        if (!C.enabled || K.combatCamera != "locked" || rig == null || df == null) return;
        if (!camInit) { camInit = true; return; }   // FlowTable.PlayerInput applies flow.json startLocked on the first frame
        var o = df.opponent; bool alive = o != null && !o.Dead;
        float d = 1e9f; if (alive) { Vector3 v = o.transform.position - df.transform.position; v.y = 0f; d = v.magnitude; }
        if (!startDone) { startDone = true; if (K.lockAtDuelStart && alive) { EnterCombat(); return; } }
        if (!inCombat) { if (alive && d <= K.enterRange) EnterCombat(); return; }
        if (!alive) { ExitCombat(); return; }
        if (d <= K.stickyRange) farT = 0f;
        else if (d > K.exitRange) { farT += Time.unscaledDeltaTime; if (farT >= K.exitSeconds) ExitCombat(); }
    }
    void EnterCombat() { inCombat = true; farT = 0f; if (!rig.lockEnabled) FlowTable.ToggleLock(rig, df); if (ControlsTable.Get().debugLog) Debug.Log("[DirInput] combat ON (auto lock)"); }
    void ExitCombat() { inCombat = false; farT = 0f; if (rig.lockEnabled) FlowTable.ToggleLock(rig, df); if (ControlsTable.Get().debugLog) Debug.Log("[DirInput] combat OFF (free camera)"); }

    // ---------------- marker ----------------
    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        var C = ControlsTable.Get(); var Mk = C.marker;
        if (!C.enabled || !Mk.enabled || !SwipeNow || sampleFrame < Time.frameCount - 1 || df == null || df.Dead) return;
        Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        if (Mk.anchor == "player")
        {
            Camera cm = camT != null ? camT.GetComponent<Camera>() : Camera.main; if (cm == null) return;
            Vector3 sp = cm.WorldToScreenPoint(df.transform.position + Vector3.up * Mk.yOffset * df.scale); if (sp.z <= 0f) return;
            c = new Vector2(sp.x, Screen.height - sp.y);
        }
        Color old = GUI.color;
        if (pressT >= 0f)
        {
            string dir = heavyFired || lightOnDown ? firedDir : DirFrom(pressT, fromPad);
            DrawDir(c, dir, ControlsTable.Col(heavyFired ? Mk.heavyColor : Mk.attackColor, Color.yellow), Mk);
        }
        else if (df.guardHeld) { if (!HudCfg.Get().guardDir.enabled) DrawGuard(c, df.guardSide, ControlsTable.Col(Mk.guardColor, Color.cyan), Mk); }   // laneHUD2 v83: GameHUD.DrawGuardDir draws the guard side (arcs, hud.json guardDir{}); the old bar only when that is off
        else if (Mk.showIdle) DrawDir(c, Classify(MouseSince(Time.unscaledTime - C.mouse.flickWindowMs / 1000f), C.mouse.deadZonePx), ControlsTable.Col(Mk.idleColor, Color.white), Mk);
        GUI.color = old;
    }
    static float SectorDeg(string dir, out bool found)
    {
        var S = ControlsTable.Get().attack.sectors; found = false;
        for (int i = 0; i < S.Length; i++) if (S[i] != null && S[i].name == dir) { found = true; return S[i].deg; }
        return 0f;
    }
    static void Box(Vector2 at, float w, float h, Color col) { GUI.color = col; GUI.DrawTexture(new Rect(at.x - w * 0.5f, at.y - h * 0.5f, w, h), Texture2D.whiteTexture); }
    void DrawDir(Vector2 c, string dir, Color col, ControlsTable.MarkerCfg Mk)
    {
        bool found; float deg = SectorDeg(dir, out found);
        Vector2 at = c;
        if (found && dir != ControlsTable.Get().attack.noSlideDir) { float r = deg * Mathf.Deg2Rad; at = c + new Vector2(Mathf.Sin(r), -Mathf.Cos(r)) * Mk.distPx; }
        Box(at, Mk.sizePx, Mk.sizePx, col);
        if (Mk.showLabel) Label(at, dir, col, Mk);
    }
    void DrawGuard(Vector2 c, int side, Color col, ControlsTable.MarkerCfg Mk)
    {
        var S = ControlsTable.Get().guard.sides; float deg = 0f; bool found = false;
        for (int i = 0; i < S.Length; i++) if (S[i] != null && S[i].id == side) { deg = S[i].deg; found = true; break; }
        if (!found) { Box(c, Mk.sizePx, Mk.sizePx, col); if (Mk.showLabel) Label(c, DirectionalGuard.NameOf(side), col, Mk); return; }
        float r = deg * Mathf.Deg2Rad; Vector2 dirv = new Vector2(Mathf.Sin(r), -Mathf.Cos(r)); Vector2 at = c + dirv * Mk.distPx;
        bool vertical = Mathf.Abs(dirv.x) > Mathf.Abs(dirv.y);   // a side bar stands upright, the top bar lies flat
        Box(at, vertical ? Mk.barThickPx : Mk.barLengthPx, vertical ? Mk.barLengthPx : Mk.barThickPx, col);
        if (Mk.showLabel) Label(at, DirectionalGuard.NameOf(side), col, Mk);
    }
    GUIStyle lbl;
    void Label(Vector2 at, string text, Color col, ControlsTable.MarkerCfg Mk)
    {
        if (lbl == null) lbl = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter };
        lbl.fontSize = Mk.fontSize; GUI.color = col;
        GUI.Label(new Rect(at.x - 50f, at.y + Mk.sizePx, 100f, Mk.fontSize * 2f), text, lbl);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { reserved = false; reservedFrame = -9; swipeFighter = null; }
}
