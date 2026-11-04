using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// Alpha step 1, lane D: IMGUI UI for the blind simultaneous duel. Dark, restrained, like GameHUD.
// 4 stance buttons, 3 move slots (click or drag from the hand), a separate single-use Special button, the plan timer,
// HP for both fighters, wound marks, the beat replay strip with reason text, round banner, log, win / lose screens.
// There is NO enemy information of any kind (the enemy plan is blind). Everything tunable lives in the "ui" block of
// duel_rules.json (Resources, StreamingAssets, then Assets/Data; a fragment file duel_rules_ui.json is accepted too).
public class ADUI : MonoBehaviour
{
    // ---------------- data ----------------
    [Serializable] public class StanceDef { public string id, label, key, tip; }
    [Serializable] public class MoveDef { public string id, label, key, tip, sub; public bool special; }
    [Serializable] public class OutcomeDef { public string id, label, glyph, color; }
    [Serializable]
    public class UICfg
    {
        public float refHeight = 1080f;
        public string colPanel = "#121214DD", colBorder = "#2E2E33FF", colText = "#CFC9BDFF", colDim = "#6F6A62FF", colDisabled = "#4A4743FF";
        public string colCrimson = "#8A1C1CFF", colEmber = "#C4602AFF", colHp = "#8A1414FF", colSlot = "#1A1A1DFF", colGood = "#9C8F5AFF", colBad = "#8A1C1CFF", colSel = "#4D1717F2";
        public string[] woundColors;
        public float barPanelW = 520f, barPanelH = 84f, margin = 24f;
        public float cardW = 112f, cardH = 92f, cardGap = 8f, specialGap = 30f, slotW = 150f, slotH = 100f, slotGap = 14f, stanceW = 170f, stanceH = 44f, stanceGap = 8f;
        public float confirmW = 190f, confirmH = 60f, beatW = 300f, beatH = 142f, beatGap = 12f, beatY = 150f;
        public float timerW = 360f, timerWarn = 5f, timerCrit = 2.5f;
        public float logW = 400f, logH = 210f;
        public int logMax = 200, slots = 3;
        public float bannerTime = 1.8f, toastTime = 1.6f, dragThreshold = 6f, planSeconds = 10f;
        public bool allowDuplicates = true, requireFull = true;
        public string keyConfirm = "Return", keyClear = "Backspace", keyRestart = "R", defaultStance = "Neutral";
        public string tRound = "ROUND {0}", tPlanHint = "Choose a stance and queue {0} moves. The enemy plan is hidden.", tWaiting = "Awaiting the clash...", tResolve = "The clash", tReplay = "Replay", tNext = "Round over",
            tConfirm = "CONFIRM", tClear = "CLEAR", tRestart = "RESTART", tNextOpp = "NEXT OPPONENT", tWin = "VICTORY", tLose = "YOU DIED",
            tHp = "HP", tVs = "vs", tDmg = "-{0}", tLog = "LOG", tCrit = "CRIT", tSpecial = "SPECIAL", tSpecialUsed = "Special already used", tSpecialQueued = "Special already queued",
            tSlotsFull = "All slots are filled", tNeedFull = "Queue all moves first", tDuplicate = "Move already queued", tYou = "you", tHim = "him",
            nameDracula = "DRACULA", namePitborn = "PITBORN", tTimeout = "Time ran out";
        public StanceDef[] stances;
        public MoveDef[] moves;
        public OutcomeDef[] outcomes;
    }
    [Serializable] class Root { public UICfg ui; }

    class BeatView { public string a, b, outA, outB, reason; public int dmgToA, dmgToB; public bool crit, filled; }
    struct Side { public float hp, hpMax; public bool set, specialUsed; public int arm, leg, torso, head; }
    enum Phase { Idle, Plan, Waiting, Resolve, Replay, End }
    struct Drag { public int hand, slot; public Vector2 start; public bool active; }

    // ---------------- public API ----------------
    public static ADUI Instance;
    public UICfg cfg;
    public event Action<ADPlan> OnPlanConfirmed;   // null = timeout with an incomplete plan (rules repeat the last plan)
    public event Action OnNextOpponent;            // default (no subscriber): reload the scene
    public event Action OnRestart;                 // default (no subscriber): reload the scene
    public Func<ADStance, string> stanceTip;       // optional override of the stance tooltip (e.g. built from the rules block)

    public void SetHp(ADSide side, float hp, float hpMax) { var s = sides[(int)side]; s.hp = hp; s.hpMax = hpMax; s.set = true; sides[(int)side] = s; }
    public void SetSpecialUsed(ADSide side, bool used) { var s = sides[(int)side]; s.specialUsed = used; sides[(int)side] = s; }
    public void SetWounds(ADSide side, int arm, int leg, int torso, int head) { var s = sides[(int)side]; s.arm = arm; s.leg = leg; s.torso = torso; s.head = head; sides[(int)side] = s; }
    public void SetFighter(ADFighter f)
    { SetHp(f.side, f.hp, f.hpMax); SetSpecialUsed(f.side, f.specialUsed); SetWounds(f.side, f.Wound(ADLoc.Arm), f.Wound(ADLoc.Leg), f.Wound(ADLoc.Torso), f.Wound(ADLoc.Head)); }

    public void BeginRound(int round, float planSeconds = -1f)
    {
        this.round = round; phase = Phase.Plan; ClearBeats();
        for (int i = 0; i < queue.Length; i++) queue[i] = -1;
        if (!stanceChosen) SetStance(ParseStance(cfg.defaultStance));
        planLen = planSeconds >= 0f ? planSeconds : cfg.planSeconds; planEnd = Time.unscaledTime + planLen;
        BannerText(string.Format(cfg.tRound, round));
    }
    public void BeginResolve() { phase = Phase.Resolve; ClearBeats(); beatCursor = 0; }
    public void ShowBeat(ADBeat r)
    { ShowBeatRaw(r.a.ToString(), r.b.ToString(), r.outA.ToString(), r.outB.ToString(), r.dmgToA, r.dmgToB, r.crit, r.reason); }
    public void ShowBeatRaw(string moveA, string moveB, string outA, string outB, int dmgToA, int dmgToB, bool crit, string reason)
    {
        if (beatCursor >= beats.Length) return;
        beats[beatCursor] = new BeatView { a = moveA, b = moveB, outA = outA, outB = outB, dmgToA = dmgToA, dmgToB = dmgToB, crit = crit, reason = reason, filled = true };
        curBeat = beatCursor++; if (!string.IsNullOrEmpty(reason)) AddLog(reason);
    }
    public void ShowReplay() { phase = Phase.Replay; curBeat = -1; }   // clash finished: the strip stays on screen until the next BeginRound
    public void ShowEnd(bool dracula_won) { phase = Phase.End; won = dracula_won; }
    public void AddLog(string s) { log.Add(s); if (log.Count > cfg.logMax) log.RemoveAt(0); logScrollToEnd = true; }
    public void Toast(string s) { toast = s; toastT = Time.unscaledTime; }
    public bool PlanOpen { get { return phase == Phase.Plan; } }
    public ADPlan CurrentPlan() { return BuildPlan(); }

    // preview hooks
    public void PreviewQueue(params int[] idx) { for (int i = 0; i < queue.Length; i++) queue[i] = i < idx.Length ? idx[i] : -1; }
    public void PreviewPhase(string p) { Phase ph; if (Enum.TryParse(p, true, out ph)) phase = ph; }
    public void PreviewTimer(float remaining) { planLen = cfg.planSeconds; planEnd = Time.unscaledTime + remaining; frozenTimer = remaining; }
    public void PreviewStance(string id) { stance = ParseStance(id); stanceChosen = true; }

    // ---------------- state ----------------
    Side[] sides = new Side[2];
    BeatView[] beats = new BeatView[3];
    int[] queue = new int[3];
    int round = 1, curBeat = -1, beatCursor; Phase phase = Phase.Idle;
    ADStance stance; bool stanceChosen, won;
    float planEnd, planLen, frozenTimer = -1f;
    string toast, bannerStr; float toastT = -99f, bannerT = -99f;
    List<string> log = new List<string>(); Vector2 logScroll; bool logScrollToEnd;
    Drag drag = new Drag { hand = -1, slot = -1 };
    Dictionary<KeyCode, Action> keys = new Dictionary<KeyCode, Action>();
    Dictionary<string, Color> col = new Dictionary<string, Color>();
    GUIStyle txt;
    float S { get { return Screen.height / cfg.refHeight; } }
    float W { get { return Screen.width / S; } }
    float H { get { return cfg.refHeight; } }

    // ---------------- load ----------------
    public static UICfg LoadCfg()
    {
        var ta = Resources.Load<TextAsset>("duel_rules");
        var list = new List<string>();
        if (ta != null) list.Add("@" + ta.text);
        foreach (var n in new[] { "duel_rules.json", "duel_rules_ui.json" })
        { list.Add(Path.Combine(Application.streamingAssetsPath, n)); list.Add(Path.Combine(Application.dataPath, "Data/" + n)); }
        foreach (var p in list)
        {
            try
            {
                string text = p.StartsWith("@") ? p.Substring(1) : (File.Exists(p) ? File.ReadAllText(p) : null);
                if (text == null) continue;
                var r = JsonUtility.FromJson<Root>(text);
                if (r != null && r.ui != null && r.ui.moves != null && r.ui.moves.Length > 0) return r.ui;
            }
            catch (Exception e) { Debug.LogWarning("ADUI: cannot read " + p + ": " + e.Message); }
        }
        Debug.LogError("ADUI: no 'ui' block with moves found in duel_rules.json");
        return new UICfg { stances = new StanceDef[0], moves = new MoveDef[0], outcomes = new OutcomeDef[0] };
    }

    void Awake()
    {
        Instance = this;
        if (cfg == null) cfg = LoadCfg();
        queue = new int[Mathf.Max(1, cfg.slots)]; for (int i = 0; i < queue.Length; i++) queue[i] = -1;
        beats = new BeatView[queue.Length]; ClearBeats();
        foreach (var s in cfg.stances) { var id = s.id; BindKey(s.key, () => SetStance(ParseStance(id))); }
        for (int i = 0; i < cfg.moves.Length; i++) { int mi = i; BindKey(cfg.moves[i].key, () => HandClick(mi)); }
        BindKey(cfg.keyConfirm, Confirm); BindKey(cfg.keyClear, ClearQueue);
        BindKey(cfg.keyRestart, () => { });
    }
    void OnDestroy() { if (Instance == this) Instance = null; }
    void BindKey(string name, Action a) { KeyCode k; if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, true, out k)) keys[k] = a; }
    void ClearBeats() { for (int i = 0; i < beats.Length; i++) beats[i] = new BeatView(); }
    static ADStance ParseStance(string s) { return (ADStance)Enum.Parse(typeof(ADStance), s, true); }
    static ADMove ParseMove(string s) { return (ADMove)Enum.Parse(typeof(ADMove), s, true); }
    Color C(string hex)
    {
        Color c; if (col.TryGetValue(hex, out c)) return c;
        if (!ColorUtility.TryParseHtmlString(hex, out c)) c = Color.magenta; col[hex] = c; return c;
    }

    float Remaining() { return frozenTimer >= 0f ? frozenTimer : Mathf.Max(0f, planEnd - Time.unscaledTime); }

    void Update()
    {
        if (phase == Phase.Plan)
        {
            foreach (var kv in keys) if (Input.GetKeyDown(kv.Key)) kv.Value();
            if (cfg.planSeconds > 0f && frozenTimer < 0f && Time.unscaledTime >= planEnd)
            {
                Toast(cfg.tTimeout);
                if (QueueFull()) Confirm();
                else { phase = Phase.Waiting; if (OnPlanConfirmed != null) OnPlanConfirmed(null); }
            }
        }
        else if (phase == Phase.End && Input.GetKeyDown(KeyCode.R) && cfg.keyRestart == "R") EndAction();
    }

    // ---------------- actions ----------------
    public void SetStance(ADStance s) { if (phase != Phase.Plan && phase != Phase.Idle) return; stance = s; stanceChosen = true; }
    bool SpecialOpen() { return !sides[(int)ADSide.Dracula].specialUsed; }
    // null = playable, "" = not in the plan phase, else the reason
    string MoveBlock(int idx, int intoSlot)
    {
        if (phase != Phase.Plan) return "";
        var md = cfg.moves[idx];
        if (md.special)
        {
            if (!SpecialOpen()) return cfg.tSpecialUsed;
            for (int i = 0; i < queue.Length; i++) if (queue[i] == idx && i != intoSlot) return cfg.tSpecialQueued;
        }
        else if (!cfg.allowDuplicates) for (int i = 0; i < queue.Length; i++) if (queue[i] == idx && i != intoSlot) return cfg.tDuplicate;
        return null;
    }
    void Place(int moveIdx, int slot)
    {
        string why = MoveBlock(moveIdx, slot); if (why != null) { if (why.Length > 0) Toast(why); return; }
        queue[slot] = moveIdx;
    }
    void HandClick(int moveIdx)
    {
        if (phase != Phase.Plan) return;
        for (int i = 0; i < queue.Length; i++) if (queue[i] < 0) { Place(moveIdx, i); return; }
        Toast(cfg.tSlotsFull);
    }
    void ClearQueue() { if (phase == Phase.Plan) for (int i = 0; i < queue.Length; i++) queue[i] = -1; }
    bool QueueFull() { foreach (int q in queue) if (q < 0) return false; return true; }
    ADPlan BuildPlan()
    {
        var mv = new ADMove[queue.Length];
        for (int i = 0; i < queue.Length; i++) mv[i] = queue[i] >= 0 ? ParseMove(cfg.moves[queue[i]].id) : ADMove.Guard;
        return new ADPlan { stance = stance, moves = mv };
    }
    void Confirm()
    {
        if (phase != Phase.Plan) return;
        if (cfg.requireFull && !QueueFull()) { Toast(cfg.tNeedFull); return; }
        phase = Phase.Waiting;
        if (OnPlanConfirmed != null) OnPlanConfirmed(BuildPlan());
    }
    void EndAction()
    {
        if (won) { if (OnNextOpponent != null) OnNextOpponent(); else Reload(); }
        else { if (OnRestart != null) OnRestart(); else Reload(); }
    }
    static void Reload() { SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex); }
    void BannerText(string s) { bannerStr = s; bannerT = Time.unscaledTime; }

    // ---------------- layout ----------------
    float HandTotal()
    { float w = cfg.moves.Length * cfg.cardW + (cfg.moves.Length - 1) * cfg.cardGap; foreach (var m in cfg.moves) if (m.special) w += cfg.specialGap; return w; }
    float HandX0() { return Mathf.Max(cfg.logW + cfg.margin * 2f, (W - HandTotal()) * 0.5f); }
    Rect HandRect(int i)
    {
        float x = HandX0() + i * (cfg.cardW + cfg.cardGap);
        for (int k = 0; k <= i; k++) if (cfg.moves[k].special) x += cfg.specialGap;
        return new Rect(x, H - cfg.margin - cfg.cardH, cfg.cardW, cfg.cardH);
    }
    float SlotsW() { return queue.Length * cfg.slotW + (queue.Length - 1) * cfg.slotGap; }
    float SlotsX0() { return HandX0() + (HandTotal() - SlotsW() - cfg.confirmW - cfg.slotGap) * 0.5f; }
    float SlotsY() { return H - cfg.margin - cfg.cardH - 18f - cfg.slotH; }
    Rect SlotRect(int i) { return new Rect(SlotsX0() + i * (cfg.slotW + cfg.slotGap), SlotsY(), cfg.slotW, cfg.slotH); }
    Rect ConfirmRect() { var l = SlotRect(queue.Length - 1); return new Rect(l.xMax + cfg.slotGap, l.y + cfg.slotH - cfg.confirmH, cfg.confirmW, cfg.confirmH); }
    Rect ClearRect() { var c = ConfirmRect(); return new Rect(c.x, c.y - 38f, c.width, 30f); }
    Rect StanceRect(int i)
    {
        float w = cfg.stances.Length * cfg.stanceW + (cfg.stances.Length - 1) * cfg.stanceGap;
        return new Rect(SlotsX0() + (SlotsW() - w) * 0.5f + i * (cfg.stanceW + cfg.stanceGap), SlotsY() - 14f - cfg.stanceH, cfg.stanceW, cfg.stanceH);
    }
    Rect BeatRect(int i)
    { float w = beats.Length * cfg.beatW + (beats.Length - 1) * cfg.beatGap; return new Rect((W - w) * 0.5f + i * (cfg.beatW + cfg.beatGap), cfg.beatY, cfg.beatW, cfg.beatH); }
    Rect BarPanel(int side) { return new Rect(side == 0 ? cfg.margin : W - cfg.margin - cfg.barPanelW, cfg.margin * 0.6f, cfg.barPanelW, cfg.barPanelH); }
    Rect LogRect() { return new Rect(cfg.margin, H - cfg.margin - cfg.logH, cfg.logW, cfg.logH); }
    Rect EndButton() { return new Rect(W * 0.5f - 150f, H * 0.5f + 70f, 300f, 56f); }

    // ---------------- draw helpers ----------------
    void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
    void Frame(Rect r, Color c, float t = 1f)
    { Fill(new Rect(r.x, r.y, r.width, t), c); Fill(new Rect(r.x, r.yMax - t, r.width, t), c); Fill(new Rect(r.x, r.y, t, r.height), c); Fill(new Rect(r.xMax - t, r.y, t, r.height), c); }
    void Panel(Rect r, bool accent = false) { Fill(r, C(cfg.colPanel)); Frame(r, accent ? C(cfg.colCrimson) : C(cfg.colBorder), 1f); }
    void Txt(Rect r, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleCenter, bool bold = false, bool wrap = false)
    {
        if (txt == null) txt = new GUIStyle(GUI.skin.label);
        txt.fontSize = size; txt.alignment = a; txt.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal; txt.wordWrap = wrap; txt.clipping = TextClipping.Clip;
        txt.normal.textColor = new Color(0f, 0f, 0f, 0.7f * c.a); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, txt);
        txt.normal.textColor = c; GUI.Label(r, s, txt);
    }
    void Bar(Rect r, float v, float max, Color c, string label)
    {
        Fill(r, new Color(0f, 0f, 0f, 0.65f));
        float f = max > 0f ? Mathf.Clamp01(v / max) : 0f;
        Fill(new Rect(r.x + 1, r.y + 1, (r.width - 2) * f, r.height - 2), c);
        Frame(r, C(cfg.colBorder));
        if (!string.IsNullOrEmpty(label)) Txt(r, label, Mathf.RoundToInt(r.height * 0.72f), C(cfg.colText), TextAnchor.MiddleCenter);
    }
    OutcomeDef Outcome(string id)
    { foreach (var o in cfg.outcomes) if (string.Equals(o.id, id, StringComparison.OrdinalIgnoreCase)) return o; return null; }
    string MoveLabel(string id) { foreach (var c in cfg.moves) if (string.Equals(c.id, id, StringComparison.OrdinalIgnoreCase)) return c.label; return id; }
    Color WoundColor(int sev)
    {
        if (cfg.woundColors == null || cfg.woundColors.Length == 0) return C(cfg.colBorder);
        return C(cfg.woundColors[Mathf.Clamp(sev, 0, cfg.woundColors.Length - 1)]);
    }

    // ---------------- OnGUI ----------------
    void OnGUI()
    {
        if (cfg == null || cfg.moves == null) return;
        GUI.matrix = Matrix4x4.Scale(new Vector3(S, S, 1f));
        var e = Event.current;
        if (e.type == EventType.Repaint) Draw(e.mousePosition);
        else HandleEvent(e);
        GUI.matrix = Matrix4x4.identity;
    }

    void HandleEvent(Event e)
    {
        Vector2 m = e.mousePosition;
        if (phase == Phase.End)
        { if (e.type == EventType.MouseDown && e.button == 0 && EndButton().Contains(m)) { EndAction(); e.Use(); } return; }
        if (phase != Phase.Plan) return;
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            for (int i = 0; i < cfg.stances.Length; i++) if (StanceRect(i).Contains(m)) { SetStance(ParseStance(cfg.stances[i].id)); e.Use(); return; }
            if (ConfirmRect().Contains(m)) { Confirm(); e.Use(); return; }
            if (ClearRect().Contains(m)) { ClearQueue(); e.Use(); return; }
            for (int i = 0; i < cfg.moves.Length; i++) if (HandRect(i).Contains(m)) { drag = new Drag { hand = i, slot = -1, start = m }; e.Use(); return; }
            for (int i = 0; i < queue.Length; i++) if (queue[i] >= 0 && SlotRect(i).Contains(m)) { drag = new Drag { hand = -1, slot = i, start = m }; e.Use(); return; }
        }
        else if (e.type == EventType.MouseDrag && (drag.hand >= 0 || drag.slot >= 0))
        { if ((m - drag.start).magnitude > cfg.dragThreshold) drag.active = true; e.Use(); }
        else if (e.type == EventType.MouseUp && e.button == 0 && (drag.hand >= 0 || drag.slot >= 0))
        {
            int over = -1; for (int i = 0; i < queue.Length; i++) if (SlotRect(i).Contains(m)) over = i;
            if (drag.hand >= 0)
            {
                if (!drag.active) HandClick(drag.hand);
                else if (over >= 0) Place(drag.hand, over);
            }
            else
            {
                if (!drag.active) queue[drag.slot] = -1;                                                                              // click a filled slot: remove
                else if (over >= 0 && over != drag.slot) { int t = queue[over]; queue[over] = queue[drag.slot]; queue[drag.slot] = t; } // swap
                else if (over < 0) queue[drag.slot] = -1;                                                                              // dragged off: remove
            }
            drag = new Drag { hand = -1, slot = -1 }; e.Use();
        }
    }

    void Draw(Vector2 m)
    {
        DrawBars(0); DrawBars(1);
        DrawBanner();
        if (phase == Phase.Resolve || phase == Phase.Replay || phase == Phase.End) DrawBeats();
        DrawLog();
        if (phase == Phase.Plan || phase == Phase.Waiting) DrawPlan(m);
        if (Time.unscaledTime - toastT < cfg.toastTime && !string.IsNullOrEmpty(toast))
        {
            float a = Mathf.Clamp01((cfg.toastTime - (Time.unscaledTime - toastT)) * 3f); var ce = C(cfg.colEmber); ce.a = a;
            Txt(new Rect(W * 0.5f - 300f, SlotsY() - cfg.stanceH - 56f, 600f, 28f), toast, 20, ce, TextAnchor.MiddleCenter, true);
        }
        if (phase == Phase.End) DrawEnd();
    }

    void DrawWoundFigure(Rect r, Side s)
    {
        // r: ~36 x 62 area. head, torso, two arms, two legs.
        float cx = r.x + r.width * 0.5f;
        Action<Rect, int> part = (pr, sev) => { Fill(pr, WoundColor(sev)); Frame(pr, C(cfg.colBorder)); };
        part(new Rect(cx - 7, r.y, 14, 14), s.head);
        part(new Rect(cx - 9, r.y + 17, 18, 22), s.torso);
        part(new Rect(cx - 16, r.y + 17, 6, 22), s.arm); part(new Rect(cx + 10, r.y + 17, 6, 22), s.arm);
        part(new Rect(cx - 9, r.y + 42, 8, 22), s.leg); part(new Rect(cx + 1, r.y + 42, 8, 22), s.leg);
    }

    void DrawBars(int side)
    {
        var r = BarPanel(side); Panel(r);
        var b = sides[side];
        string name = side == 0 ? cfg.nameDracula : cfg.namePitborn;
        Txt(new Rect(r.x + 12, r.y + 4, 300, 26), name, 20, C(cfg.colText), TextAnchor.MiddleLeft, true);
        Fill(new Rect(r.x + 12, r.y + 30, 56, 2), C(cfg.colCrimson));
        if (!b.set) return;
        float wf = 44f, x = r.x + 12, w = r.width - 24 - wf - 8;
        Bar(new Rect(x, r.y + 38, w, 26), b.hp, b.hpMax, C(cfg.colHp), cfg.tHp + " " + Mathf.CeilToInt(b.hp) + " / " + Mathf.CeilToInt(b.hpMax));
        Txt(new Rect(x, r.y + 66, w, 14), !b.specialUsed ? cfg.tSpecial : "", 11, C(cfg.colEmber), TextAnchor.MiddleLeft);
        DrawWoundFigure(new Rect(r.xMax - 12 - wf, r.y + 12, wf, 64), b);
    }

    void DrawBanner()
    {
        string hint = phase == Phase.Plan ? string.Format(cfg.tPlanHint, queue.Length) : phase == Phase.Waiting ? cfg.tWaiting : phase == Phase.Resolve ? cfg.tResolve : phase == Phase.Replay ? cfg.tReplay : "";
        float cx = W * 0.5f;
        Txt(new Rect(cx - 200f, 8f, 400f, 38f), string.Format(cfg.tRound, round), 30, C(cfg.colText), TextAnchor.MiddleCenter, true);
        Fill(new Rect(cx - 40f, 47f, 80f, 2f), C(cfg.colCrimson));
        if (phase == Phase.Plan && cfg.planSeconds > 0f)
        {
            float rem = Remaining(), f = planLen > 0f ? Mathf.Clamp01(rem / planLen) : 0f;
            Color tc = rem <= cfg.timerCrit ? C(cfg.colBad) : rem <= cfg.timerWarn ? C(cfg.colEmber) : C(cfg.colText);
            var tr = new Rect(cx - cfg.timerW * 0.5f, 56f, cfg.timerW, 8f);
            Fill(tr, new Color(0, 0, 0, 0.6f)); Fill(new Rect(tr.x + 1, tr.y + 1, (tr.width - 2) * f, tr.height - 2), tc); Frame(tr, C(cfg.colBorder));
            Txt(new Rect(cx - 60f, 66f, 120f, 34f), rem.ToString("F1"), 28, tc, TextAnchor.MiddleCenter, true);
            Txt(new Rect(cx - 330f, 102f, 660f, 22f), hint, 15, C(cfg.colDim));
        }
        else Txt(new Rect(cx - 330f, 56f, 660f, 24f), hint, 16, C(cfg.colDim));
        float t = Time.unscaledTime - bannerT;
        if (t < cfg.bannerTime && !string.IsNullOrEmpty(bannerStr))
        {
            float a = t < 0.25f ? t / 0.25f : Mathf.Clamp01((cfg.bannerTime - t) / 0.5f);
            var cc = C(cfg.colText); cc.a = a;
            Fill(new Rect(0, H * 0.38f - 6f, W, 96f), new Color(0f, 0f, 0f, 0.5f * a));
            Txt(new Rect(0, H * 0.38f, W, 84f), bannerStr, 72, cc, TextAnchor.MiddleCenter, true);
            var ce = C(cfg.colCrimson); ce.a = a; Fill(new Rect(W * 0.5f - 160f, H * 0.38f + 86f, 320f, 2f), ce);
        }
    }

    void Badge(Rect g, string outcome, string who)
    {
        var od = Outcome(outcome); Color oc = od != null ? C(od.color) : C(cfg.colDim);
        Fill(g, new Color(oc.r * 0.25f, oc.g * 0.25f, oc.b * 0.25f, 0.9f)); Frame(g, oc, 1f);
        Txt(g, od != null ? od.glyph : "?", 22, oc, TextAnchor.MiddleCenter, true);
        Txt(new Rect(g.x - 30, g.yMax + 1, g.width + 60, 18), od != null ? od.label : outcome, 13, oc, TextAnchor.MiddleCenter, true);
    }

    void DrawBeats()
    {
        for (int i = 0; i < beats.Length; i++)
        {
            var r = BeatRect(i); var bv = beats[i]; Panel(r, i == curBeat);
            Txt(new Rect(r.x + 6, r.y + 2, 30, 16), (i + 1).ToString(), 13, C(cfg.colDim), TextAnchor.MiddleLeft);
            if (!bv.filled) { Txt(new Rect(r.x, r.y + 50, r.width, 30), "-", 28, C(cfg.colDisabled)); continue; }
            if (bv.crit) Txt(new Rect(r.x, r.y + 2, r.width - 8, 16), cfg.tCrit, 12, C(cfg.colEmber), TextAnchor.MiddleRight, true);
            float q = r.width * 0.25f;
            Txt(new Rect(r.x + q - 70, r.y + 18, 140, 20), cfg.tYou + ": " + MoveLabel(bv.a), 14, C(cfg.colText), TextAnchor.MiddleCenter, true);
            Txt(new Rect(r.xMax - q - 70, r.y + 18, 140, 20), cfg.tHim + ": " + MoveLabel(bv.b), 14, C(cfg.colText), TextAnchor.MiddleCenter, true);
            Txt(new Rect(r.center.x - 16, r.y + 40, 32, 22), cfg.tVs, 12, C(cfg.colDim));
            Badge(new Rect(r.x + q - 23, r.y + 40, 46, 30), bv.outA, cfg.tYou);
            Badge(new Rect(r.xMax - q - 23, r.y + 40, 46, 30), bv.outB, cfg.tHim);
            if (bv.dmgToA > 0) Txt(new Rect(r.x + q + 28, r.y + 40, 50, 30), string.Format(cfg.tDmg, bv.dmgToA), 18, C(cfg.colBad), TextAnchor.MiddleLeft, true);
            if (bv.dmgToB > 0) Txt(new Rect(r.xMax - q - 78, r.y + 40, 50, 30), string.Format(cfg.tDmg, bv.dmgToB), 18, C(cfg.colBad), TextAnchor.MiddleRight, true);
            Fill(new Rect(r.x + 10, r.y + 94, r.width - 20, 1), C(cfg.colBorder));
            Txt(new Rect(r.x + 10, r.y + 98, r.width - 20, r.height - 102), bv.reason, 14, C(cfg.colText), TextAnchor.UpperCenter, false, true);
        }
    }

    void DrawLog()
    {
        var r = LogRect(); Panel(r);
        Txt(new Rect(r.x + 10, r.y + 3, 100, 18), cfg.tLog, 12, C(cfg.colDim), TextAnchor.MiddleLeft);
        var view = new Rect(r.x + 6, r.y + 22, r.width - 12, r.height - 28);
        float lh = 20f, contentH = Mathf.Max(view.height, log.Count * lh);
        if (logScrollToEnd) { logScroll.y = contentH; logScrollToEnd = false; }
        logScroll = GUI.BeginScrollView(view, logScroll, new Rect(0, 0, view.width - 18, contentH));
        for (int i = 0; i < log.Count; i++) Txt(new Rect(2, i * lh, view.width - 22, lh), log[i], 14, i == log.Count - 1 ? C(cfg.colText) : C(cfg.colDim), TextAnchor.MiddleLeft);
        GUI.EndScrollView();
    }

    void DrawPlan(Vector2 m)
    {
        bool open = phase == Phase.Plan; string tip = null, tipTitle = null, tipWarn = null; Rect tipAt = default(Rect);
        for (int i = 0; i < cfg.stances.Length; i++)
        {
            var d = cfg.stances[i]; var r = StanceRect(i); bool sel = stance.ToString().Equals(d.id, StringComparison.OrdinalIgnoreCase);
            Fill(r, sel ? C(cfg.colSel) : C(cfg.colSlot)); Frame(r, sel ? C(cfg.colCrimson) : C(cfg.colBorder), sel ? 2f : 1f);
            Txt(r, d.label, 18, open ? (sel ? C(cfg.colText) : C(cfg.colDim)) : C(cfg.colDisabled), TextAnchor.MiddleCenter, sel);
            Txt(new Rect(r.x + 4, r.y + 1, 30, 14), d.key, 11, C(cfg.colDim), TextAnchor.UpperLeft);
            if (r.Contains(m)) { tipTitle = d.label; tip = stanceTip != null ? stanceTip(ParseStance(d.id)) : d.tip; tipAt = r; }
        }
        for (int i = 0; i < queue.Length; i++)
        {
            var r = SlotRect(i); Fill(r, C(cfg.colSlot)); Frame(r, queue[i] >= 0 ? C(cfg.colCrimson) : C(cfg.colBorder), queue[i] >= 0 ? 2f : 1f);
            Txt(new Rect(r.x + 6, r.y + 3, 30, 16), (i + 1).ToString(), 13, C(cfg.colDim), TextAnchor.UpperLeft);
            if (queue[i] >= 0 && !(drag.active && drag.slot == i))
            {
                var md = cfg.moves[queue[i]];
                Txt(new Rect(r.x, r.y + 28, r.width, 32), md.label, 24, md.special ? C(cfg.colEmber) : C(cfg.colText), TextAnchor.MiddleCenter, true);
                Txt(new Rect(r.x, r.y + 64, r.width, 22), md.sub, 13, C(cfg.colDim));
                if (r.Contains(m)) { tipTitle = md.label; tip = md.tip; tipAt = r; }
            }
        }
        bool ready = open && (!cfg.requireFull || QueueFull());
        var cr = ConfirmRect(); Fill(cr, ready ? C(cfg.colSel) : C(cfg.colSlot)); Frame(cr, ready ? C(cfg.colEmber) : C(cfg.colBorder), ready ? 2f : 1f);
        Txt(cr, cfg.tConfirm, 24, ready ? C(cfg.colText) : C(cfg.colDisabled), TextAnchor.MiddleCenter, true);
        var clr = ClearRect(); Fill(clr, C(cfg.colSlot)); Frame(clr, C(cfg.colBorder)); Txt(clr, cfg.tClear, 14, open ? C(cfg.colDim) : C(cfg.colDisabled));
        for (int i = 0; i < cfg.moves.Length; i++)
        {
            var d = cfg.moves[i]; var r = HandRect(i); string why = open ? MoveBlock(i, -1) : "";
            bool ok = open && why == null; bool hov = r.Contains(m);
            Fill(r, C(cfg.colSlot));
            Frame(r, ok && d.special ? C(cfg.colEmber) : C(cfg.colBorder), 1f);
            if (ok && hov) Frame(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), C(cfg.colCrimson), 2f);
            Color tc = !ok ? C(cfg.colDisabled) : d.special ? C(cfg.colEmber) : C(cfg.colText);
            Txt(new Rect(r.x + 5, r.y + 2, 30, 16), d.key, 12, ok ? C(cfg.colDim) : C(cfg.colDisabled), TextAnchor.UpperLeft);
            Txt(new Rect(r.x, r.y + 26, r.width, 30), d.label, 20, tc, TextAnchor.MiddleCenter, true);
            Txt(new Rect(r.x, r.y + 62, r.width, 22), d.sub, 12, ok ? C(cfg.colDim) : C(cfg.colDisabled));
            if (hov && !drag.active) { tipTitle = d.label; tip = d.tip; tipWarn = open ? why : null; tipAt = r; }
        }
        if (drag.active)
        {
            int ci = drag.hand >= 0 ? drag.hand : queue[drag.slot];
            if (ci >= 0)
            {
                var g = new Rect(m.x - cfg.cardW * 0.5f, m.y - cfg.cardH * 0.5f, cfg.cardW, cfg.cardH);
                Fill(g, new Color(0.18f, 0.06f, 0.06f, 0.92f)); Frame(g, C(cfg.colCrimson), 2f);
                Txt(g, cfg.moves[ci].label, 20, C(cfg.colText), TextAnchor.MiddleCenter, true);
            }
        }
        if (tip != null)
        {
            float tw = 330f, th = 78f + (string.IsNullOrEmpty(tipWarn) ? 0f : 22f);
            var t = new Rect(Mathf.Clamp(tipAt.center.x - tw * 0.5f, 8f, W - tw - 8f), tipAt.y - th - 8f, tw, th);
            Panel(t, true);
            Txt(new Rect(t.x + 10, t.y + 4, t.width - 20, 22), tipTitle, 16, C(cfg.colText), TextAnchor.MiddleLeft, true);
            Txt(new Rect(t.x + 10, t.y + 28, t.width - 20, 48), tip, 14, C(cfg.colDim), TextAnchor.UpperLeft, false, true);
            if (!string.IsNullOrEmpty(tipWarn)) Txt(new Rect(t.x + 10, t.yMax - 24, t.width - 20, 20), tipWarn, 14, C(cfg.colEmber), TextAnchor.MiddleLeft, true);
        }
    }

    void DrawEnd()
    {
        Fill(new Rect(0, 0, W, H), new Color(0f, 0f, 0f, 0.72f));
        Txt(new Rect(0, H * 0.5f - 90f, W, 120f), won ? cfg.tWin : cfg.tLose, 96, won ? C(cfg.colGood) : C(cfg.colBad), TextAnchor.MiddleCenter, true);
        Fill(new Rect(W * 0.5f - 120f, H * 0.5f + 40f, 240f, 2f), C(cfg.colCrimson));
        var r = EndButton(); Fill(r, C(cfg.colSel)); Frame(r, C(cfg.colEmber), 2f);
        Txt(r, won ? cfg.tNextOpp : cfg.tRestart + "  [" + cfg.keyRestart + "]", 24, C(cfg.colText), TextAnchor.MiddleCenter, true);
    }
}
