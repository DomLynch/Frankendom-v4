using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// lanePARRY: STANCES for real-time direct control. A light modifier layer, all numbers in stances.json (StreamingAssets first, then Assets/Data).
// Ported from the steer-mode stances (STStanceMods / steer.json "stances": Aggressive 1.25 dealt / 1.2 taken, Defensive 0.8 / 0.75, Trickster 25 % x1.3 / 25 % x1.3).
// Hooks (one call each, all return 1 when the layer is off or steer mode is on, so nothing changes without the file):
//   FightBalance.HitMul          -> DamageMul(a, d)       clean-hit damage: attacker dealtMul x defender takenMul x Trickster rolls
//   Duel.Resolve block branch    -> BlockCostMul(d)       stamina a block costs the defender
//   Duel.Resolve block + hit     -> PostureTakenMul(d)    posture the defender takes
//   DuelFighter.Feint            -> FeintCostMul(f)       feint stamina cost (V3 Trickster: cheap feints)
// Input (data): next / prev keys, mouse wheel, gamepad D-pad through an Input Manager axis or joystick buttons. HUD: a small label (OnGUI).
// STANCE2 (laneSTANCE2): BOTH fighters' stances now scale posture AND damage (attacker postureDealtMul x defender postureTakenMul), plus guard / tempo knobs
//   per stance: guardArcMul (block cone), parryWindowMul (ticks), blockChipMul, attackCostMul (stamina per swing), moveSpeedMul. New hooks (one call each):
//   Duel.Resolve block + hit + kick-vs-guard -> PostureMul(a, d, clean)    replaces PostureTakenMul(d)
//   Duel.Resolve guard cone                  -> GuardArcMul(d)             DuelFighter.ParryWindowTicks -> ParryWindowMul(f)
//   Duel.Resolve block chip                  -> BlockChipMul(d)            DuelFighter.MoveCost -> AttackCostMul(f)    DuelFighter.SpeedMulExhausted -> MoveSpeedMul(f)
//   AIBrain.Update                           -> AiStance.Tick(df)          the AI picks its own stance (stances.json "ai", AiStance.cs); a human's switch is rate-limited by switchLockS.
public static class StanceLayer
{
    [Serializable] public class StanceRow
    {
        public string id = "Neutral", label = "NEUTRAL";
        public float dealtMul = 1f, takenMul = 1f;              // flat damage dealt / taken
        public float postureDealtMul = 1f;                      // STANCE2: posture this fighter puts on the foe (hit and block), x the foe's postureTakenMul
        public float postureTakenMul = 1f, blockCostMul = 1f;   // defence: posture the fighter takes, stamina a block costs
        public float blockChipMul = 1f;                         // STANCE2: chip damage through his own block
        public float guardArcMul = 1f, parryWindowMul = 1f;     // STANCE2: width of his block cone, length of his parry window (ticks, rounded, >= 1)
        public float attackCostMul = 1f, moveSpeedMul = 1f;     // STANCE2: stamina his swings cost, walk / run / guard-walk speed
        public float feintCostMul = 1f;
        public float pDealt = 0f, dealtRollMul = 1f;            // per-hit chance of an extra multiplier on damage DEALT (Trickster)
        public float pTaken = 0f, takenRollMul = 1f;            // per-hit chance of an extra multiplier on damage TAKEN (Trickster)
        public float[] color = { 0.9f, 0.9f, 0.9f };
    }
    [Serializable] public class InputRow
    {
        public string[] nextKeys = new string[0], prevKeys = new string[0];
        public bool wheel = true, wheelInvert = false; public float wheelCooldownS = 0.15f;
        public string padAxisX = "", padAxisY = "";             // Input Manager axis names (D-pad = joystick axis 6 / 7 on Windows); "" or undefined = off
        public bool padAxisXNext = true;                        // true: D-pad right = next, left = prev; Y axis: up = next, down = prev
        public float padDeadzone = 0.5f;
        public string[] padNextButtons = new string[0], padPrevButtons = new string[0];   // KeyCode names (JoystickButtonN) for pads that report the D-pad as buttons
        public bool ignoreWhileFrozen = true;                   // no switching while a finisher owns the fight (Duel.Frozen)
    }
    [Serializable] public class HudRow
    {
        public bool enabled = true; public float x = 16f, yFromBottom = 64f, w = 260f, h = 22f; public int fontSize = 13;
        public string prefix = "STANCE  "; public float toastSeconds = 0.9f; public int toastFontSize = 16; public float alpha = 0.9f;
        public string hint = "";                                // appended in small text, e.g. "[3/4, wheel, D-pad]"
    }
    [Serializable] public class AiRow   // STANCE2: AiStance.cs. Hysteresis = a stance is kept until its EXIT threshold is crossed (exit looser than enter), and nothing changes before minDwellS.
    {
        public bool enabled = false, forAutoplayPlayer = false;   // forAutoplayPlayer: also drive the IsPlayer fighter when AIBrain runs it (-autoplay); the balance bot (FlowBot) is untouched
        public float decideEveryS = 0.5f, minDwellS = 4f;
        public float lowHpEnter = 0.35f, lowHpExit = 0.50f;                 // own HP fraction: Defensive from <= enter, until HP > exit
        public float selfPostureEnter = 0.65f, selfPostureExit = 0.40f;     // own posture fraction: Defensive from >= enter, until it falls <= exit
        public float oppPostureEnter = 0.60f, oppPostureExit = 0.35f;       // foe posture fraction: Aggressive (go for the break) from >= enter, until <= exit
        public float leadEnter = 0.15f, leadExit = 0.05f;                   // own HP fraction minus foe HP fraction: Aggressive from >= enter, until <= exit
        public float tricksterChance = 0.10f; public float[] tricksterHoldS = { 4f, 8f };   // per decision while Neutral; hold time range
        public string neutralId = "Neutral", aggressiveId = "Aggressive", defensiveId = "Defensive", tricksterId = "Trickster";
    }
    [Serializable] public class Cfg
    {
        public bool rollsScalePosture = true;                   // STANCE2: a Trickster dealt / taken proc also scales the posture of that same hit
        public bool keepLethalDeathblow = true;                 // STANCE2: Duel keeps a lethal deathblow lethal after the stance / balance multipliers (false = the old order)
        public float switchLockS = 1f;                         // STANCE2: a human cannot change stance again within this many seconds (the AI uses ai.minDwellS)
        public AiRow ai = new AiRow();
        public bool enabled = false, debugLog = false; public int seed = 4242;
        public string playerDefault = "Neutral", aiStance = "Neutral";
        public bool keepAcrossRestart = true;                   // the player's pick survives a scene reload
        public string[] order = new string[0];                  // cycle order; empty = stances[] file order
        public StanceRow[] stances = new StanceRow[0];
        public InputRow input = new InputRow(); public HudRow hud = new HudRow();
    }

    static Cfg C; static bool loaded;
    public static Cfg Get() { if (!loaded) Load(); return C; }
    public static bool Active => Get() != null && C.enabled && !STSteer.Active;
    public static bool KeepLethal => Active && C.keepLethalDeathblow;   // STANCE2 (Duel.Resolve deathblow)

    static readonly Dictionary<DuelFighter, string> stanceOf = new Dictionary<DuelFighter, string>();
    static string playerPick; static uint rng = 1;
    public static int Rolls, DealtProcs, TakenProcs;            // diagnostics
    public static float LastMul = 1f;

    static void Load()
    {
        loaded = true; C = null;
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "stances.json");
            if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/stances.json");
            if (File.Exists(p)) C = JsonUtility.FromJson<Cfg>(File.ReadAllText(p));
        }
        catch (Exception e) { Debug.LogWarning("[Stance] load failed, layer off: " + e.Message); C = null; }
        if (C == null) C = new Cfg { enabled = false };
        if (C.stances == null) C.stances = new StanceRow[0];
        if (C.input == null) C.input = new InputRow();
        if (C.hud == null) C.hud = new HudRow();
        if (C.ai == null) C.ai = new AiRow();
        if (C.ai.tricksterHoldS == null || C.ai.tricksterHoldS.Length < 2) C.ai.tricksterHoldS = new float[] { 4f, 8f };
        rng = unchecked((uint)C.seed * 2654435761u + 12345u); if (rng == 0) rng = 1;
    }
    public static void Reload() { loaded = false; }

    static float Roll() { rng = unchecked(rng * 1664525u + 1013904223u); return (float)(rng / 4294967296.0); }

    public static StanceRow Row(string id)
    {
        var c = Get(); if (c == null || string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < c.stances.Length; i++) if (c.stances[i] != null && c.stances[i].id == id) return c.stances[i];
        return null;
    }
    public static string StanceOf(DuelFighter f)
    {
        if (f == null) return "";
        string s; if (stanceOf.TryGetValue(f, out s)) return s;
        s = f.IsPlayer ? (string.IsNullOrEmpty(playerPick) ? Get().playerDefault : playerPick) : Get().aiStance;
        stanceOf[f] = s; return s;
    }
    static StanceRow For(DuelFighter f) { return Active && f != null ? Row(StanceOf(f)) : null; }

    static readonly Dictionary<DuelFighter, int> lastSetTick = new Dictionary<DuelFighter, int>();
    // STANCE2: returns false when a HUMAN switch is refused by switchLockS (the AI passes force = true: AiStance has its own dwell time). Callers that ignore the result still compile.
    public static bool Set(DuelFighter f, string id, bool force = false)
    {
        if (f == null || Row(id) == null) return false;
        int now = Duel.TickCount, last;
        if (!force && f.IsPlayer && lastSetTick.TryGetValue(f, out last) && now >= last && (now - last) < LockTicks()) return false;
        lastSetTick[f] = now;
        stanceOf[f] = id; if (f.IsPlayer) playerPick = id;
        if (Get().debugLog) Debug.Log("[Stance] " + f.name + " -> " + id);
        if (f.IsPlayer) StanceDriver.Toast(Row(id));   // STANCE2: the toast is for the human's own switch, not the AI's
        return true;
    }
    static int LockTicks() { return Mathf.RoundToInt(Mathf.Max(0f, Get().switchLockS) * Mathf.Max(1f, AiRows.I.unity.ticksPerSecond)); }
    public static void ResetAi() { lastSetTick.Clear(); AiStance.Reset(); }

    // ---- hooks ----
    static DuelFighter procA, procD; static int procTick = -1; static float procMul = 1f;   // STANCE2: the Trickster proc of the clean hit being resolved, read back by PostureMul in the same tick
    public static float DamageMul(DuelFighter a, DuelFighter d)
    {
        if (!Active) return 1f;
        float m = 1f, roll = 1f; var ra = For(a); var rd = For(d);
        if (ra != null) { m *= ra.dealtMul; if (ra.pDealt > 0f) { Rolls++; if (Roll() < ra.pDealt) { roll *= ra.dealtRollMul; DealtProcs++; } } }
        if (rd != null) { m *= rd.takenMul; if (rd.pTaken > 0f) { Rolls++; if (Roll() < rd.pTaken) { roll *= rd.takenRollMul; TakenProcs++; } } }
        procA = a; procD = d; procTick = Duel.TickCount; procMul = roll;
        m *= roll; LastMul = m; return m;
    }
    public static float BlockCostMul(DuelFighter d) { var r = For(d); return r != null ? r.blockCostMul : 1f; }
    public static float PostureTakenMul(DuelFighter d) { var r = For(d); return r != null ? r.postureTakenMul : 1f; }   // kept for older callers; Duel now calls PostureMul
    // STANCE2: posture one blow puts on the defender = attacker postureDealtMul x defender postureTakenMul (x the Trickster proc of this very hit when it was a clean hit and rollsScalePosture)
    public static float PostureMul(DuelFighter a, DuelFighter d, bool cleanHit)
    {
        if (!Active) return 1f;
        float m = 1f; var ra = For(a); var rd = For(d);
        if (ra != null) m *= ra.postureDealtMul;
        if (rd != null) m *= rd.postureTakenMul;
        if (cleanHit && Get().rollsScalePosture && procTick == Duel.TickCount && procA == a && procD == d) m *= procMul;
        return m;
    }
    // kitV81: the attacker half of PostureMul (postureDealtMul x the Trickster proc of this very hit). Duel's clean-hit line uses it next to the unchanged PostureTakenMul(d) factor, so kitGEAR's hook text on that line still matches whichever kit is applied first
    public static float PostureDealtMul(DuelFighter a, DuelFighter d, bool cleanHit)
    {
        if (!Active) return 1f;
        float m = 1f; var ra = For(a);
        if (ra != null) m *= ra.postureDealtMul;
        if (cleanHit && Get().rollsScalePosture && procTick == Duel.TickCount && procA == a && procD == d) m *= procMul;
        return m;
    }
    public static float BlockChipMul(DuelFighter d) { var r = For(d); return r != null ? r.blockChipMul : 1f; }
    public static float GuardArcMul(DuelFighter d) { var r = For(d); return r != null ? r.guardArcMul : 1f; }
    public static float ParryWindowMul(DuelFighter f) { var r = For(f); return r != null ? r.parryWindowMul : 1f; }
    public static float AttackCostMul(DuelFighter f) { var r = For(f); return r != null ? r.attackCostMul : 1f; }
    public static float MoveSpeedMul(DuelFighter f) { var r = For(f); return r != null ? r.moveSpeedMul : 1f; }
    public static float FeintCostMul(DuelFighter f) { var r = For(f); return r != null ? r.feintCostMul : 1f; }

    // ---- cycling ----
    static string[] Order()
    {
        var c = Get(); if (c.order != null && c.order.Length > 0) return c.order;
        var l = new string[c.stances.Length]; for (int i = 0; i < l.Length; i++) l[i] = c.stances[i] != null ? c.stances[i].id : ""; return l;
    }
    public static void Cycle(DuelFighter f, int step)
    {
        var o = Order(); if (o.Length == 0 || f == null) return;
        int i = Array.IndexOf(o, StanceOf(f)); if (i < 0) i = 0; else i = ((i + step) % o.Length + o.Length) % o.Length;
        Set(f, o[i]);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded = false; C = null; stanceOf.Clear(); ResetAi(); procA = procD = null; procTick = -1; playerPick = null; Rolls = DealtProcs = TakenProcs = 0; LastMul = 1f; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("StanceLayer"); go.hideFlags = HideFlags.HideInHierarchy; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<StanceDriver>();
    }

    public class StanceDriver : MonoBehaviour
    {
        static StanceDriver inst; DuelFighter player; float wheelT, toastUntil; string toastText = ""; Color toastCol = Color.white;
        bool axisXOk = true, axisYOk = true; float lastX, lastY, nextFind; GUIStyle st, stToast;
        KeyCode[] nextK, prevK, padN, padP;

        void OnEnable() { inst = this; SceneManager.sceneLoaded += OnLoaded; }
        void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; if (inst == this) inst = null; }
        void OnLoaded(Scene s, LoadSceneMode m)
        {
            Reload(); stanceOf.Clear(); ResetAi(); player = null; nextFind = 0f; nextK = prevK = padN = padP = null; axisXOk = axisYOk = true;
            if (!Get().keepAcrossRestart) playerPick = null;
        }
        public static void Toast(StanceRow r)
        {
            if (inst == null || r == null) return;
            inst.toastText = r.label; inst.toastCol = Col(r.color, 1f); inst.toastUntil = Time.unscaledTime + Get().hud.toastSeconds;
        }
        // laneHUD2 v83: GameHUD draws the stance label and the switch toast in the gothic style (hud.json stance{}); it reads the toast through this
        public static bool ToastState(out string text, out Color col, out float alpha)
        {
            text = ""; col = Color.white; alpha = 0f;
            if (inst == null || Time.unscaledTime >= inst.toastUntil) return false;
            alpha = Mathf.Clamp01((inst.toastUntil - Time.unscaledTime) / Mathf.Max(0.05f, Get().hud.toastSeconds * 0.5f)); text = inst.toastText; col = inst.toastCol; return true;
        }
        static Color Col(float[] a, float alpha) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], alpha) : new Color(1f, 1f, 1f, alpha); }
        static KeyCode[] Keys(string[] names)
        {
            var l = new List<KeyCode>(); if (names != null) foreach (var n in names) { KeyCode k; if (!string.IsNullOrEmpty(n) && Enum.TryParse(n, true, out k)) l.Add(k); }
            return l.ToArray();
        }
        static bool AnyDown(KeyCode[] ks) { for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }

        DuelFighter Player()
        {
            if (player != null) return player;
            if (Time.unscaledTime < nextFind) return null;   // no player in this scene (menu): look again twice a second, not every frame
            nextFind = Time.unscaledTime + 0.5f;
            foreach (var f in FindObjectsByType<DuelFighter>()) if (f != null && f.IsPlayer) { player = f; break; }
            return player;
        }

        // one edge per D-pad push: returns -1 / 0 / +1; an undefined axis turns itself off (Input.GetAxisRaw throws)
        float Axis(string n, ref bool ok)
        {
            if (!ok || string.IsNullOrEmpty(n)) return 0f;
            try { return Input.GetAxisRaw(n); }
            catch (Exception) { ok = false; Debug.Log("[Stance] Input Manager axis '" + n + "' is not defined: D-pad stance switching on that axis is off (add it in Project Settings > Input Manager)."); return 0f; }
        }
        int Edge(float v, ref float last, float dz) { int s = v > dz ? 1 : v < -dz ? -1 : 0; int l = last > dz ? 1 : last < -dz ? -1 : 0; last = v; return s != 0 && s != l ? s : 0; }

        void Update()
        {
            if (!Active) return;
            var c = Get(); var I = c.input;
            if (I.ignoreWhileFrozen && Duel.Frozen) return;
            var p = Player(); if (p == null || p.Dead) return;
            if (nextK == null) { nextK = Keys(I.nextKeys); prevK = Keys(I.prevKeys); padN = Keys(I.padNextButtons); padP = Keys(I.padPrevButtons); }
            int step = 0;
            if (AnyDown(nextK) || AnyDown(padN)) step = 1;
            else if (AnyDown(prevK) || AnyDown(padP)) step = -1;
            if (step == 0 && I.wheel && Time.unscaledTime >= wheelT)
            {
                float w = Input.mouseScrollDelta.y; if (I.wheelInvert) w = -w;
                if (w > 0.01f) step = 1; else if (w < -0.01f) step = -1;
                if (step != 0) wheelT = Time.unscaledTime + I.wheelCooldownS;
            }
            if (step == 0)
            {
                int ex = Edge(Axis(I.padAxisX, ref axisXOk), ref lastX, I.padDeadzone), ey = Edge(Axis(I.padAxisY, ref axisYOk), ref lastY, I.padDeadzone);
                if (ex != 0) step = I.padAxisXNext ? ex : -ex; else if (ey != 0) step = ey;
            }
            if (step != 0) Cycle(p, step);
        }

        void OnGUI()
        {
            if (!Active) return;
            if (HudCfg.Get().stance.enabled) return;   // laneHUD2 v83: GameHUD.DrawStance draws the label and the toast (hud.json stance{}); set stance.enabled false there to get this old label back
            var H = Get().hud; if (!H.enabled) return;
            var p = Player(); if (p == null) return;
            var r = Row(StanceOf(p)); if (r == null) return;
            if (st == null) { st = new GUIStyle(GUI.skin.label) { fontSize = H.fontSize, fontStyle = FontStyle.Bold }; stToast = new GUIStyle(GUI.skin.label) { fontSize = H.toastFontSize, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter }; }
            var rect = new Rect(H.x, Screen.height - H.yFromBottom, H.w, H.h);
            string txt = H.prefix + r.label + (string.IsNullOrEmpty(H.hint) ? "" : "  <size=" + Mathf.Max(8, H.fontSize - 3) + ">" + H.hint + "</size>");
            st.richText = true;
            st.normal.textColor = new Color(0f, 0f, 0f, 0.7f * H.alpha); GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), txt, st);
            st.normal.textColor = Col(r.color, H.alpha); GUI.Label(rect, txt, st);
            if (Time.unscaledTime < toastUntil)
            {
                float a = Mathf.Clamp01((toastUntil - Time.unscaledTime) / Mathf.Max(0.05f, H.toastSeconds * 0.5f));
                var tr = new Rect(Screen.width * 0.5f - 160f, Screen.height * 0.70f, 320f, 30f);
                stToast.normal.textColor = new Color(0f, 0f, 0f, 0.7f * a); GUI.Label(new Rect(tr.x + 1f, tr.y + 1f, tr.width, tr.height), toastText, stToast);
                stToast.normal.textColor = new Color(toastCol.r, toastCol.g, toastCol.b, a); GUI.Label(tr, toastText, stToast);
            }
        }
    }
}
