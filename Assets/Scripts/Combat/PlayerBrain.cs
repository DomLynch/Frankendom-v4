using UnityEngine;

// Desktop controls ported 1:1 from Frankendom V3 (kbm.ts / input.json):
// WASD/arrows move, Shift run. F or LMB = strike: tap = light (alternating), +A/D held = that-side slash, +W held = stab, HOLD 220 ms = heavy; G = heavy; T = stab;
// E/Space = roll (tap = backstep, hold > 150 ms = roll); Q or RMB hold = guard; C = kick; 1/Z = special 1 (Skull Crack); 2/X = special 2 (Whirlwind); Tab = lock-on toggle; R = recenter.
[DefaultExecutionOrder(-200)]   // P1: before Duel.Update (order 0) so a key pressed this frame is in the buffers when the tick runs (no extra frame of latency)
public class PlayerBrain : MonoBehaviour
{
    public Transform cam; public CameraRig rig;
    float awayT; DuelFighter df; float strikeDown = -1f; bool heavyFired; float rollDown = -1f;
    void Awake()
    {
        df = GetComponent<DuelFighter>();
        foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-autoplay") enabled = false;
    }
    void Update()
    {
        if (df.Dead || Duel.Frozen) { df.moveDir = Vector3.zero; df.guardHeld = false; return; }   // FN1: Frozen = a finisher scene runs
        if (TouchControls.Active)
        {
            // a touch screen laptop flips TouchControls.Active on the first touch and it never cleared: keyboard keys then did nothing (no roll, no guard). Real key input on desktop switches it back off.
            if (Application.isMobilePlatform || Input.touchCount > 0 || !(Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.F))) return;
            TouchControls.Active = false;
        }
        Vector2 inp = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        if (LatencyProbe.On) ProbeKeys();
        Vector3 fwd = cam ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized : Vector3.forward;
        Vector3 right = cam ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized : Vector3.right;
        df.moveDir = inp.sqrMagnitude > 0.01f ? (fwd * inp.y + right * inp.x).normalized : Vector3.zero;
        df.run = Input.GetKey(KeyCode.LeftShift);
        df.guardHeld = Input.GetMouseButton(1) || Input.GetKey(KeyCode.Q);
        // directional guard: Q + arrow keys = guard side L/R/up (V3 kbm.ts)
        df.guardSide = Input.GetKey(KeyCode.LeftArrow) ? 1 : Input.GetKey(KeyCode.RightArrow) ? 2 : Input.GetKey(KeyCode.UpArrow) ? 3 : 0;
        if (Input.GetKeyDown(KeyCode.V)) df.Feint();
        // lock-break: push away from the enemy (>120 deg) for 0.25 s; any attack re-locks
        if (rig && df.opponent && inp.sqrMagnitude > 0.01f && FlowTable.PushAwayBreaks)
        {
            Vector3 toE = df.opponent.transform.position - transform.position; toE.y = 0f;
            if (Vector3.Angle(df.moveDir, toE) > 120f) { awayT += Time.deltaTime; if (awayT > 0.25f) rig.lockEnabled = false; } else awayT = 0f;
        }
        else awayT = 0f;
        if (rig && FlowTable.AttackRelocks && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.F))) rig.lockEnabled = true;

        bool strike = Input.GetMouseButton(0) || Input.GetKey(KeyCode.F);
        bool strikeDownNow = Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.F);
        var fc = CombatFeel.Get(); bool fireOnDown = fc.enabled && fc.input.strikeFire == "down"; float heavyHold = fc.enabled ? fc.input.heavyHoldSeconds : 0.22f;   // cancel.json input: "down" fires the light on key-down (heavy then only by G)
        if (strikeDownNow) { strikeDown = Time.time; heavyFired = false; if (fireOnDown) { FireLight(inp); firedOnDown = true; } else firedOnDown = false; }
        if (strikeDown >= 0f)
        {
            df.heavyHeld = strike && heavyFired;
            if (strike && !heavyFired && !firedOnDown && Time.time - strikeDown >= heavyHold) { df.Press("heavy"); heavyFired = true; }
            if (strike && firedOnDown && !heavyFired && Time.time - strikeDown >= fc.input.holdToHeavyMs / 1000f)   // hold-to-heavy upgrade (cancel.json input.holdToHeavyMs)
            {
                if (df.UpgradeToHeavy(fc.input.holdToHeavyUntilTick)) heavyFired = true;
                else if (df.state == FState.Active || df.state == FState.Recovery || Time.time - strikeDown > 0.6f) heavyFired = true;   // too late for this light: stop trying
            }
            if (!strike)
            {
                if (!heavyFired && !firedOnDown) FireLight(inp);
                strikeDown = -1f; df.heavyHeld = false; firedOnDown = false;
            }
        }
        else df.heavyHeld = Input.GetKey(KeyCode.G);
        if (Input.GetKeyDown(KeyCode.G)) df.Press("heavy");
        if (Input.GetKeyDown(KeyCode.T)) df.Press("thrust");
        if (Input.GetKeyDown(KeyCode.C)) df.Press("kick");
        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Z)) df.Press("special1");
        if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.X)) df.Press("special2");
        // dodge keys (dodge.json input): "immediate" = the key decides on key-down (roll / sidestep by the held direction, see DodgeFromKey);
        // "tapHold" (or dodge.json "enabled": false) = V3 legacy: tap = backstep, hold > holdSeconds = roll
        var dd = DodgeTable.Get();
        if (dd.enabled && dd.input.mode != "tapHold")
        {
            rollDown = -1f;
            if (AnyDown(RollKeys(dd))) DodgeFromKey(dd);
            if (dd.input.doubleTap.enabled) DoubleTap(dd);
        }
        else
        {
            bool dn = AnyDown(RollKeys(dd)), hd = AnyHeld(RollKeys(dd));
            float holdS = dd.enabled ? dd.input.holdSeconds : 0.15f;
            if (dn) rollDown = Time.time;
            if (rollDown >= 0f)
            {
                if (hd && Time.time - rollDown > holdS) { df.Press("roll"); rollDown = -1f; }
                else if (!hd) { df.Press("backstep"); rollDown = -1f; }
            }
        }
        FlowTable.PlayerInput(rig, df);   // V64FLOW: lock toggle (flow.json lockOn.keys, default Tab / MMB / pad R3), df.lockFacing, riposte key (default B / pad B)
        if (Input.GetKeyDown(KeyCode.R) && rig) rig.Recenter();
    }

    bool firedOnDown;
    void FireLight(Vector2 inp)
    {
        if (inp.y > 0.5f && Mathf.Abs(inp.x) < 0.5f) df.Press("thrust");
        else if (inp.x < -0.5f) df.Press("light_left");
        else if (inp.x > 0.5f) df.Press("light_right");
        else df.Press("light");
    }
    // latency probe (flag only): label the physical key-down the sim / animator start is measured from
    void ProbeKeys()
    {
        if (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.F)) LatencyProbe.KeyDown("strike");
        else if (Input.GetKeyDown(KeyCode.G)) LatencyProbe.KeyDown("heavy");
        else if (Input.GetKeyDown(KeyCode.T)) LatencyProbe.KeyDown("thrust");
        else if (Input.GetKeyDown(KeyCode.C)) LatencyProbe.KeyDown("kick");
        else if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space)) LatencyProbe.KeyDown("dodge");
        else if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Z) || Input.GetKeyDown(KeyCode.X)) LatencyProbe.KeyDown("special");
        else if (df.moveDir.sqrMagnitude < 0.01f && (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D))) LatencyProbe.KeyDown("move");
    }

    // ---------------- dodge helpers (data: Assets/Data/dodge.json) ----------------
    object keyOwner; KeyCode[] kRoll, kLeft, kRight, kBack; float[] lastTap = { -9f, -9f, -9f };
    static KeyCode[] ParseKeys(string[] names, KeyCode[] fallback)
    {
        var list = new System.Collections.Generic.List<KeyCode>();
        if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (System.Enum.TryParse(names[i], true, out k)) list.Add(k); }
        return list.Count > 0 ? list.ToArray() : fallback;
    }
    void EnsureKeys(DodgeTable.Data d)
    {
        if (ReferenceEquals(keyOwner, d)) return;
        keyOwner = d;
        kRoll = ParseKeys(d.input.rollKeys, new[] { KeyCode.E, KeyCode.Space });
        kLeft = ParseKeys(d.input.doubleTap.leftKeys, new[] { KeyCode.A });
        kRight = ParseKeys(d.input.doubleTap.rightKeys, new[] { KeyCode.D });
        kBack = ParseKeys(d.input.doubleTap.backKeys, new[] { KeyCode.S });
    }
    KeyCode[] RollKeys(DodgeTable.Data d) { EnsureKeys(d); return kRoll; }
    static bool AnyDown(KeyCode[] ks) { for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }
    static bool AnyHeld(KeyCode[] ks) { for (int i = 0; i < ks.Length; i++) if (Input.GetKey(ks[i])) return true; return false; }
    bool Locked() { return rig != null && rig.lockEnabled && df.opponent != null && !df.opponent.Dead; }

    // roll key pressed: classify the held direction against the line to the locked opponent, then pick roll / sidestep / backstep from dodge.json input.onKey
    void DodgeFromKey(DodgeTable.Data d)
    {
        var inp = d.input; bool locked = Locked();
        string kind = "none"; int side = 0;
        if (df.moveDir.sqrMagnitude > 0.01f)
        {
            kind = "forward";
            if (locked)
            {
                Vector3 toE = df.opponent.transform.position - transform.position; toE.y = 0f;
                if (toE.sqrMagnitude > 1e-4f)
                {
                    toE.Normalize();
                    Vector3 rt = Vector3.Cross(Vector3.up, toE);
                    float fw = Vector3.Dot(df.moveDir, toE), lat = Vector3.Dot(df.moveDir, rt), th = inp.lateralThreshold;
                    if (Mathf.Abs(lat) >= th && Mathf.Abs(lat) >= Mathf.Abs(fw)) { kind = "lateral"; side = lat < 0f ? -1 : 1; }
                    else if (fw <= -th) kind = "back";
                }
            }
        }
        string act = kind == "lateral" ? inp.onKey.lateral : kind == "back" ? inp.onKey.back : kind == "none" ? inp.onKey.none : inp.onKey.forward;
        if (act == "sidestep" && inp.requireLock && !locked) act = "roll";
        df.SetDodgeDir(FlowTable.DirName(locked, kind, side));   // V64FLOW: forward / back / left / right / free, flow.json dodge.dirs
        if (act == "sidestep") df.PressSidestep(kind == "lateral" ? side : 0);
        else if (act == "backstep") df.Press("backstep");
        else df.Press("roll");
        if (d.debugLog) Debug.Log("[Dodge] key: held=" + kind + " side=" + side + " locked=" + locked + " -> " + act + " stamina=" + df.stamina + " state=" + df.state);
    }

    void DoubleTap(DodgeTable.Data d)
    {
        EnsureKeys(d);
        if (!Locked()) return;
        float w = d.input.doubleTap.seconds;
        if (AnyDown(kLeft)) TapSide(0, -1, w);
        if (AnyDown(kRight)) TapSide(1, 1, w);
        if (AnyDown(kBack)) TapSide(2, 0, w);
    }
    void TapSide(int slot, int dir, float window)
    {
        if (Time.time - lastTap[slot] <= window) { df.SetDodgeDir(dir < 0 ? "left" : dir > 0 ? "right" : "back"); df.PressSidestep(dir); lastTap[slot] = -9f; } else lastTap[slot] = Time.time;
    }
}

