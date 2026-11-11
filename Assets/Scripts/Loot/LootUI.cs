using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// LOOTUI v85: the loot pickup + equip flow driver (a DontDestroyOnLoad object made at runtime, like GameLoop's driver; nothing in the scene).
//   1. LootBridge.OnLootDropped(itemId, worldPos) (lane GEAR's GearDrop) queues a drop. After the fight, on VICTORY (+ pickup.spawnDelayS), each queued drop becomes a LootPickup:
//      a soft glow pillar and the item name in serif above it. No drop source registered = loot_ui.json pickup.fallbackItems drop at the fallen foe so the flow can be seen and tested.
//   2. Walk within pickup.radius and press E / pad X (keys.pickup) to take it: it goes into the carried list (LootSave) and a toast says so. Tab / Start then opens the equip screen (LootScreen).
//   3. LootSave writes the carried list + the worn items to loot_save.json in Application.persistentDataPath; LootSave.Apply() puts them back after every scene load (and when a new IGearSlots registers).
// Harness runs (-balanceauto, -autoplay, ... GameLoop.HarnessOn) never start it, so the gate numbers stay comparable. -lootdemo (loot_ui.json demo.flag) drops demo.items in front of the player 2 s after the scene starts and lets Tab open the screen at any time.
// Data: loot_ui.json (LootCfg). Keys E (roll) and pad X (special 2) double as combat keys: the pickup only works after the fight (victory), never while a fight is live.
[DefaultExecutionOrder(-400)]   // before GameLoopDriver (-150) and PlayerBrain (-200): Blocking is set before they read the keys
public sealed class LootUI : MonoBehaviour
{
    public static LootUI Instance;
    static MenuLayoutCfg.LootWorldC ML { get { return MenuLayoutCfg.LW; } }   // UIDATAB v1: layout numbers of the pickup labels / toast: menu_layout.json (defaults = the old literals)
    readonly UiStr.Cat4 promptCat = new UiStr.Cat4(); readonly System.Collections.Generic.Dictionary<string, string> subCache = new System.Collections.Generic.Dictionary<string, string>(); object subFor;   // UIDATAB v1: cached strings (no per-frame concat / format / upper)
    /// <summary>True while the equip screen is open (and on the frame it closed): GameLoopDriver must not read Enter / A / B then.</summary>
    public static bool Blocking { get { return LootScreen.Open || LootScreen.ClosedFrame == Time.frameCount || MarketUI.Blocking; } }   // STALL v1
    /// <summary>True when a pickup is within reach this frame (PlayerBrain may ignore the E roll then; optional hunk).</summary>
    public static bool PickupInReach { get; private set; }
    public static int PickupCount { get { return Instance != null ? Instance.live.Count : 0; } }

    struct Drop { public string id; public Vector3 pos; }
    readonly List<Drop> queue = new List<Drop>();
    readonly List<LootPickup> live = new List<LootPickup>();
    bool victorySeen, spawnedVictory, fallbackDone, demoDone, applyDone; float victoryT, sceneStart; Vector3 enemyPos; int frames; IGearSlots lastSlots;
    string toastName = "", toastSub = ""; float toastAt = -99f; GearItem toastItem;
    LootPickup near; float nearDist; KeyCode[] kPickup; object keyOwner; static bool? demoFlag;
    static readonly RaycastHit[] hits = new RaycastHit[24];
    float zoneDropAt = -1f;   // TRADEFIN v1: zones never set GameLoop.Outcome (GameLoopDriver stands down), so a kill drop is timed from its own event

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Instance = null; PickupInReach = false; demoFlag = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            var C = LootCfg.Get(); if (!C.enabled || Instance != null) return;
            if (GameLoop.HarnessOn && !DemoOn()) return;
            var go = new GameObject("LootUI"); DontDestroyOnLoad(go); go.AddComponent<LootUI>();
        }
        catch (Exception e) { Debug.LogWarning("[LootUI] boot failed: " + e.Message); }
    }
    static bool DemoOn()
    {
        if (demoFlag.HasValue) return demoFlag.Value;
        string f = LootCfg.Get().demo.flag; demoFlag = !string.IsNullOrEmpty(f) && Array.IndexOf(Environment.GetCommandLineArgs(), f) >= 0; return demoFlag.Value;
    }

    void Awake() { Instance = this; }
    void OnEnable()
    {
        Instance = this; SceneManager.sceneLoaded += OnLoaded; LootBridge.OnLootDropped += OnDropped;
        ResetState(); LootSave.EnsureLoaded();
    }
    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnLoaded; LootBridge.OnLootDropped -= OnDropped; LootScreen.ForceClose();
        if (Instance == this) Instance = null;
    }
    void OnLoaded(Scene s, LoadSceneMode m) { ResetState(); }

    void ResetState()
    {
        LootScreen.ForceClose();
        for (int i = 0; i < live.Count; i++) if (live[i] != null) Destroy(live[i].gameObject);
        live.Clear(); queue.Clear(); zoneDropAt = -1f; victorySeen = spawnedVictory = fallbackDone = demoDone = applyDone = false; frames = 0; sceneStart = Time.unscaledTime; near = null; PickupInReach = false;
    }

    void OnDropped(string itemId, Vector3 pos)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        queue.Add(new Drop { id = itemId, pos = pos });
        if (ZoneRuntime.Owns && zoneDropAt < 0f) zoneDropAt = Time.unscaledTime;   // TRADEFIN v1
        if (LootCfg.Get().debugLog) Debug.Log("[LootUI] drop queued " + itemId + " at " + pos);
    }

    // ---- per frame ----
    void Update()
    {
        var C = LootCfg.Get(); if (!C.enabled) return;
        bool demo = DemoOn(); float now = Time.unscaledTime;
        LootScreen.Tick(demo);

        // put the saved selection back on the wearer (after the scene's gear component exists, and again when a new one registers)
        if (!applyDone) { if (++frames >= Mathf.Max(1, C.save.applyAfterFrames)) { applyDone = true; lastSlots = LootBridge.Slots; int n = LootSave.Apply(); if (C.debugLog) Debug.Log("[LootUI] saved gear applied: " + n); } }
        else if (!ReferenceEquals(lastSlots, LootBridge.Slots)) { lastSlots = LootBridge.Slots; LootSave.Apply(); }

        var pl = GameLoop.Player; var en = GameLoop.Enemy; var P = C.pickup;
        bool victory = GameLoop.Outcome == GameLoop.Result.Victory;
        if (victory)
        {
            if (!victorySeen || RTFinisher.Busy) victoryT = now;                    // the delay counts from the end of the finisher scene
            victorySeen = true;
            if (en != null && !spawnedVictory) enemyPos = en.transform.position;
        }
        else if (victorySeen && GameLoop.Outcome == GameLoop.Result.None) { victorySeen = false; }

        bool ready;
        if (demo) ready = now - sceneStart >= C.demo.spawnAfterS;
        else if (!P.requireVictory) ready = true;
        else if (ZoneRuntime.Owns && P.zoneDropDelayS >= 0f) { if (RTFinisher.Busy && zoneDropAt >= 0f) zoneDropAt = now; ready = zoneDropAt >= 0f && now - zoneDropAt >= P.zoneDropDelayS; }   // TRADEFIN v1: the delay counts from the end of the finisher scene
        else ready = victorySeen && now - victoryT >= P.spawnDelayS;

        if (ready)
        {
            if (queue.Count > 0) { SpawnQueued(); spawnedVictory = true; zoneDropAt = -1f; }  /* TRADEFIN v1 */
            else if (demo) { if (!demoDone) { demoDone = true; DropDemo(C, pl); spawnedVictory = true; } }
            else if (!fallbackDone && victorySeen && !LootBridge.HasDropSource && P.fallbackDrops && now - victoryT >= P.spawnDelayS + P.fallbackAfterS)
            {
                fallbackDone = true;
                for (int i = 0; i < P.fallbackItems.Length; i++) queue.Add(new Drop { id = P.fallbackItems[i], pos = enemyPos });
                SpawnQueued(); spawnedVictory = true;
            }
        }

        UpdatePickup(C, pl);
    }

    void DropDemo(LootCfg.Data C, DuelFighter pl)
    {
        Vector3 at; Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (pl != null) { Vector3 f = cam != null ? Vector3.ProjectOnPlane(cam.forward, Vector3.up) : pl.transform.forward; if (f.sqrMagnitude < 0.01f) f = pl.transform.forward; at = pl.transform.position + f.normalized * C.demo.distance; }
        else if (cam != null) at = cam.position + Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized * C.demo.distance;
        else at = Vector3.zero;
        for (int i = 0; i < C.demo.items.Length; i++) queue.Add(new Drop { id = C.demo.items[i], pos = at });
        SpawnQueued();
    }

    void SpawnQueued()
    {
        var P = LootCfg.Get().pickup;
        for (int i = 0; i < queue.Count; i++)
        {
            var d = queue[i]; GearItem it;
            if (!LootBridge.Catalog.TryGet(d.id, out it)) { Debug.LogWarning("[LootUI] unknown item id '" + d.id + "' (not in gear.json / loot_ui.json fallbackItems): no marker."); continue; }
            Vector3 p = WeaponDrop.PillarAt(d.id, d.pos); int n = 0;   // WEAPONDROP v1: the pillar of a listed loot item stands on the dropped weapon
            while (Crowded(p) && n < 12) { float ang = (n + live.Count) * 2.399963f; p = d.pos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * P.spread * (1f + n / 6); n++; }
            var pk = LootPickup.Spawn(d.id, it, Ground(p)); live.Add(pk);
            Debug.Log("[LootUI] marker '" + d.id + "' at " + pk.Anchor.ToString("F2"));
        }
        queue.Clear();
    }
    bool Crowded(Vector3 p)
    {
        float r = LootCfg.Get().pickup.spread * 0.6f;
        for (int i = 0; i < live.Count; i++) { if (live[i] == null) continue; var a = live[i].Anchor; if ((new Vector3(a.x - p.x, 0f, a.z - p.z)).sqrMagnitude < r * r) return true; }
        return false;
    }
    static Vector3 Ground(Vector3 p)
    {
        var Pl = LootCfg.Get().pillar;
        int n = Physics.RaycastNonAlloc(p + Vector3.up * Pl.groundRayUp, Vector3.down, hits, Pl.groundRayUp + Pl.groundRayDown, PhysMasks.NotPhys & Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; Vector3 pt = p;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i]; if (h.collider == null || h.rigidbody != null || h.collider.GetComponentInParent<DuelFighter>() != null) continue;   // not a ragdoll / fighter part
            if (h.distance < best) { best = h.distance; pt = h.point; }
        }
        return pt;
    }

    void UpdatePickup(LootCfg.Data C, DuelFighter pl)
    {
        near = null; nearDist = float.MaxValue; PickupInReach = false;
        for (int i = live.Count - 1; i >= 0; i--) if (live[i] == null) live.RemoveAt(i);
        if (live.Count == 0 || LootScreen.Open) return;
        Transform who = pl != null ? pl.transform : (Camera.main != null ? Camera.main.transform : null); if (who == null) return;
        Vector3 wp = who.position;
        for (int i = 0; i < live.Count; i++)
        {
            var a = live[i].Anchor; float d = Mathf.Sqrt((a.x - wp.x) * (a.x - wp.x) + (a.z - wp.z) * (a.z - wp.z));
            if (d < nearDist) { nearDist = d; near = live[i]; }
        }
        if (near == null || nearDist > C.pickup.radius) return;
        PickupInReach = true;
        if (!ReferenceEquals(keyOwner, C.keys)) { keyOwner = C.keys; kPickup = LootCfg.Keys(C.keys.pickup); }
        if (C.pickup.autoPickup || LootCfg.AnyDown(kPickup)) Collect(near);
    }

    void Collect(LootPickup p)
    {
        var T = LootCfg.Get().text; live.Remove(p); near = null; PickupInReach = false;
        WeaponDrop.OnLootCollected(p.itemId);   // WEAPONDROP v1: the dropped weapon for this item goes away with the pickup
        FakeDeath.OnLoot(p.Anchor);   // FAKEDEATH v1: a dead bandit beside the pillar may grab her ankle (dark by data)
        p.Collect(); bool fresh = LootSave.Add(p.itemId);
        toastItem = p.item; toastName = p.item != null ? p.item.name : LootCfg.Pretty(p.itemId); toastSub = T.acquiredSub; toastAt = Time.unscaledTime;
        Debug.Log("[LootUI] picked up '" + p.itemId + "'" + (fresh ? "" : " (already carried)") + " -> " + LootSave.Path_);
    }

    // ---- drawing ----
    void OnGUI()
    {
        var C = LootCfg.Get(); if (!C.enabled) return;
        GUI.depth = C.guiDepth;
        float s = LootCfg.Scale(); var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f)); float vw = Screen.width / s, vh = Screen.height / s;
        if (Event.current.type == EventType.Repaint && !LootScreen.Open)
        {
            LootDraw.Alpha = 1f;
            DrawLabels(C, vw, vh, s); DrawEquipHint(C, vw, vh); DrawToast(C, vw, vh);
        }
        LootScreen.Draw(vw, vh);
        GUI.matrix = m;
    }

    struct Slot { public LootPickup p; public float d, x, y; }   // HUDFIX v92
    readonly System.Collections.Generic.List<Slot> slots = new System.Collections.Generic.List<Slot>();

    void DrawLabels(LootCfg.Data C, float vw, float vh, float s)
    {
        var cam = Camera.main; if (cam == null || live.Count == 0) return;
        var L = C.label; var K = C.colors; var T = C.text; Vector3 cp = cam.transform.position;
        // HUDFIX v92: pass 1 = project + cull + de-overlap (stack apart by label.stackGapY), pass 2 = draw
        slots.Clear(); float topY = L.nameSize * ML.label.nameLineK + L.minScreenMargin, botY = vh - L.minScreenMargin - ML.label.bottomPad;
        bool plate = GameLoop.Outcome != GameLoop.Result.None && GameLoop.ScreenAlpha > 0.001f; float blockH0 = L.nameSize * ML.label.nameLineK + ML.label.ruleDy + L.lineGap + (L.showSub ? L.subSize * ML.label.subLineK + L.promptGap : 0f) + L.promptHeight;
        if (plate) { var SC = HudCfg.Get().screens; botY = Mathf.Min(botY, vh * 0.5f - SC.bandHeight * 0.5f - SC.bandLift - blockH0 - L.plateGap); }   // HUDPOLISH: labels stay above the end-screen plate, stacked upward
        for (int i = 0; i < live.Count; i++)
        {
            var p = live[i]; if (p == null || p.item == null) continue;
            float dist0 = Vector3.Distance(cp, p.Anchor); if (L.nearAlpha * (1f - Mathf.InverseLerp(L.fadeStartM, L.fadeEndM, dist0)) <= 0.01f) continue;
            Vector3 sp0 = cam.WorldToScreenPoint(p.Anchor + Vector3.up * L.height); if (sp0.z <= 0f) continue;
            float rx = sp0.x / s, ry = (Screen.height - sp0.y) / s;
            if (ry < -L.cullMargin || ry > vh + L.cullMargin || rx < -L.cullMargin || rx > vw + L.cullMargin) continue;   // off screen: no label pinned to the edge
            slots.Add(new Slot { p = p, d = dist0, x = Mathf.Clamp(rx, L.minScreenMargin + ML.label.edgeX, vw - L.minScreenMargin - ML.label.edgeX), y = Mathf.Clamp(ry, topY, botY) });
        }
        slots.Sort((a0, b0) => a0.d.CompareTo(b0.d));   // nearest keeps its spot, the rest move
        float blockH = L.nameSize * ML.label.nameLineK + ML.label.ruleDy + L.lineGap + (L.showSub ? L.subSize * ML.label.subLineK + L.promptGap : 0f);
        for (int i = 1; i < slots.Count; i++)
        {
            var sl0 = slots[i]; float step = blockH + L.promptHeight + L.stackGapY, y0 = sl0.y;
            for (int tries = 0; tries < slots.Count + 1; tries++)
            {
                bool hit = false;
                for (int j = 0; j < i && !hit; j++) if (Mathf.Abs(slots[j].x - sl0.x) < L.boxW && Mathf.Abs(slots[j].y - sl0.y) < step) hit = true;
                if (!hit) break;
                sl0.y -= step; if (sl0.y < topY) { if (plate) { sl0.y = Mathf.Min(y0, botY); sl0.x = slots[i].x + ((tries & 1) == 0 ? 1f : -1f) * L.boxW * (tries / 2 + 1); } else sl0.y = y0 + step * (tries + 1); }   // HUDPOLISH: no room above the plate = spread sideways   // out of room above: stack downward instead
            }
            sl0.y = Mathf.Clamp(sl0.y, topY, botY); sl0.x = Mathf.Clamp(sl0.x, L.minScreenMargin + ML.label.edgeX, vw - L.minScreenMargin - ML.label.edgeX); slots[i] = sl0;
        }
        for (int i = 0; i < slots.Count; i++)
        {
            var p = slots[i].p;
            float a = L.nearAlpha * (1f - Mathf.InverseLerp(L.fadeStartM, L.fadeEndM, slots[i].d)); float gx = slots[i].x, gy = slots[i].y;   // HUDFIX v92
            LootDraw.Alpha = a; Color col = LootCfg.RarityColor(p.item);
            LootDraw.Text(new Rect(gx - ML.label.halfW, gy - L.nameSize * ML.label.nameLineK, ML.label.boxW, L.nameSize * ML.label.nameLineK), p.item.name, "serif", L.nameSize, TextAnchor.LowerCenter, true, col);
            LootDraw.Fill(new Rect(gx - L.ruleW * 0.5f, gy + ML.label.ruleDy, L.ruleW, ML.label.ruleH), new Color(col.r, col.g, col.b, ML.label.ruleAlpha));
            float yy = gy + ML.label.ruleDy + L.lineGap;
            if (L.showSub)
            {
                var rar = LootCfg.Rarity(p.item.rarity); string rl = rar != null ? rar.label : ""; string sl = LootCfg.SlotLabel(p.item.slot);
                string sub = SubText(p.item, rl, sl, T);   // UIDATAB v1: cached per item (was Format + ToUpperInvariant every frame)
                LootDraw.Text(new Rect(gx - ML.label.halfW, yy, ML.label.boxW, L.subSize * ML.label.subLineK), sub, "serif", L.subSize, TextAnchor.UpperCenter, false, LootCfg.Col(K.dim)); yy += L.subSize * ML.label.subLineK + L.promptGap;
            }
            if (p == near && nearDist <= C.pickup.promptRadius)
            {
                bool reach = nearDist <= C.pickup.radius; Color pc = reach ? LootCfg.Col(K.gold) : LootCfg.Col(K.dim);
                LootDraw.Text(new Rect(gx - ML.label.halfW, yy, ML.label.boxW, L.promptHeight), promptCat.Get(ML.label.promptOpen, T.pickupKeys, ML.label.promptClose, T.pickup, true), "serif", L.promptSize, TextAnchor.UpperCenter, true, pc);
            }
        }
        LootDraw.Alpha = 1f;
    }

    string SubText(GearItem item, string rl, string sl, LootCfg.TextC T)   // UIDATAB v1: the rarity / slot line of a label, built once per item
    {
        if (!ReferenceEquals(subFor, T)) { subCache.Clear(); subFor = T; }
        string s; if (subCache.TryGetValue(item.id, out s)) return s;
        s = (rl.Length > 0 ? string.Format(T.slotLine, rl, sl) : sl).ToUpperInvariant(); subCache[item.id] = s; return s;
    }

    void DrawEquipHint(LootCfg.Data C, float vw, float vh)
    {
        var S = C.screen; if (!S.showEquipHint || LootSave.Owned.Count == 0) return;
        if (GameLoop.Outcome == GameLoop.Result.None && !DemoOn() && !ZoneRuntime.Owns) return;   // TRADEFIN v1
        if (!LootScreen.CanOpen(DemoOn())) return;
        LootDraw.Text(new Rect(0f, vh - S.equipHintBottom, vw, S.equipHintSize * ML.toast.equipHintK), UiStr.Up(C.text.equipHint), "serif", S.equipHintSize, TextAnchor.UpperCenter, false, LootCfg.Col(C.colors.dim));
    }

    void DrawToast(LootCfg.Data C, float vw, float vh)
    {
        var P = C.pickup; float age = Time.unscaledTime - toastAt; if (age < 0f || age > P.toastS + P.toastFadeS) return;
        float a = Mathf.Clamp01(Mathf.Min(age / ML.toast.fadeInS, (P.toastS + P.toastFadeS - age) / Mathf.Max(0.01f, P.toastFadeS))); LootDraw.Alpha = a;
        float y = vh - P.toastBottom; Color col = LootCfg.RarityColor(toastItem);
        LootDraw.Text(new Rect(0f, y, vw, P.toastSubSize * ML.toast.subK), UiStr.Up(C.text.acquired), "serif", P.toastSubSize, TextAnchor.UpperCenter, true, LootCfg.Col(C.colors.dim));
        LootDraw.Text(new Rect(0f, y + P.toastSubSize * ML.toast.gapK, vw, P.toastSize * ML.toast.nameK), toastName, "serif", P.toastSize, TextAnchor.UpperCenter, true, col);
        LootDraw.Text(new Rect(0f, y + P.toastSubSize * ML.toast.gapK + P.toastSize * ML.toast.nameK, vw, P.toastSubSize * ML.toast.subK), toastSub, "serif", P.toastSubSize, TextAnchor.UpperCenter, false, LootCfg.Col(C.colors.dim), true);
        LootDraw.Alpha = 1f;
    }
}
