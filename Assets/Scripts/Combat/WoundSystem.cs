using System.Collections.Generic;
using UnityEngine;

// Wounds that build up over a fight (lane WOUND, build v70). Everything is data: Assets/Data/wounds.json via WoundTable, rows keyed by roster character id, "*" = default for a new character.
//  - every landed "Hit" (ContactRelay.OnEvent, same self-booting pattern as BloodGround, so NO hook edit is needed to get decals) -> WoundDecals.Spawn on the victim, capped per fighter, oldest fades.
//  - HP thresholds (stages[]): limp (move speed), breathing (stamina regen), guard drop (slower guard raise + guard layer weight), drips on the ground at the lowest stage (BloodGround.Drip).
//  - leg hit: V3's wound state, a timed walk slow. Each hit lowers the stamina ceiling a little (floored).
//  - the PLAYER (Roster.PlayerId) gets a low-HP screen-edge vignette (IMGUI, off-able).
// The sim reads four getters (MoveMul, RegenMul, GuardRaiseExtra, GuardLayerMul, StaminaCeil); each returns the neutral value when the system is off, the fighter is untracked or dead, so removing the file or setting enabled=false restores today's behaviour.
// Finishers: no decals / drips while RTFinisher.Busy; decals are plain children of bones (no Renderer on a DecalProjector), so GoreFx / MeshCut never see them; they follow the pieces that are cut off.
public class WoundSystem : MonoBehaviour
{
    class St
    {
        public DuelFighter f; public WoundDecals.Set decals = new WoundDecals.Set();
        public int wounds; public float legUntil = -1f, dripT, moveMul = 1f, regenMul = 1f, layerMul = 1f; public int raiseExtra; public string stage = "";
    }

    static WoundSystem instance;
    readonly Dictionary<DuelFighter, St> states = new Dictionary<DuelFighter, St>();
    int lastTick = -1; float refreshT, vigAlpha; Texture2D vigTex; DuelFighter player;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var d = WoundTable.Get(); if (!d.enabled) return;
        var go = new GameObject("WoundSystem"); DontDestroyOnLoad(go); instance = go.AddComponent<WoundSystem>();
    }

    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }

    // ---------------- getters read by DuelFighter (neutral = no change) ----------------
    static St Find(DuelFighter f) { St s; return instance != null && f != null && instance.states.TryGetValue(f, out s) ? s : null; }
    public static float MoveMul(DuelFighter f)
    {
        var s = Find(f); if (s == null || f.Dead || !WoundTable.Get().states) return 1f;
        var r = WoundTable.For(f.CharacterId); return s.moveMul * (Time.time < s.legUntil ? r.legSlowMul : 1f);
    }
    public static float RegenMul(DuelFighter f) { var s = Find(f); return s == null || f.Dead || !WoundTable.Get().states ? 1f : s.regenMul; }
    public static int GuardRaiseExtra(DuelFighter f) { var s = Find(f); return s == null || f.Dead || !WoundTable.Get().states ? 0 : s.raiseExtra; }
    public static float GuardLayerMul(DuelFighter f) { var s = Find(f); return s == null || f.Dead || !WoundTable.Get().states ? 1f : s.layerMul; }
    public static float StaminaCeil(DuelFighter f)
    {
        var s = Find(f); if (s == null || f.Dead || !WoundTable.Get().states) return 1f;
        var r = WoundTable.For(f.CharacterId); return Mathf.Clamp(1f - s.wounds * r.staminaCeilPerWound, Mathf.Clamp01(r.staminaCeilMin), 1f);
    }

    // ---------------- events ----------------
    St Get(DuelFighter f)
    {
        St s; if (states.TryGetValue(f, out s)) return s;
        s = new St { f = f }; s.decals.seed = (uint)(Mathf.Abs(f.name.GetHashCode()) % 100000 + WoundTable.Get().decal.seed); states[f] = s; return s;
    }

    void OnEvent(CombatEvent e)
    {
        var D = WoundTable.Get(); if (!D.enabled || e.type != "Hit" || e.target == null || e.actor == null) return;
        if (Duel.TickCount < lastTick) ResetAll(); lastTick = Duel.TickCount;
        if (RTFinisher.Busy) return;   // never touch the victim while a finisher cut runs
        var f = e.target; var s = Get(f); var row = WoundTable.For(f.CharacterId);
        s.wounds++;
        string loc = !string.IsNullOrEmpty(e.hitLocation) ? e.hitLocation : e.location;
        if (loc == "legs" && row.legSlowSec > 0f) s.legUntil = Time.time + row.legSlowSec;
        if (!D.decals) return;
        var d = WoundDecals.Spawn(s.decals, f, e, row, D.decal, f.Dead);
        int ev = WoundDecals.Enforce(s.decals, row.maxDecals, Time.unscaledTime);
        if (D.debugLog) Debug.Log("[Wound] " + f.name + " hit#" + s.wounds + " loc=" + loc + " placed=" + (d != null) + " live=" + WoundDecals.Live(s.decals) + " evicted=" + ev + " hp=" + f.health.ToString("F0") + "/" + f.maxHealth.ToString("F0"));
    }

    void ResetAll()
    {
        foreach (var kv in states) { WoundDecals.Clear(kv.Value.decals); kv.Value.wounds = 0; kv.Value.legUntil = -1f; kv.Value.dripT = 0f; }
    }

    // ---------------- per frame ----------------
    static WoundTable.Stage StageFor(WoundTable.Row r, float frac)
    {
        WoundTable.Stage best = null;
        if (r.stages != null) for (int i = 0; i < r.stages.Length; i++) { var st = r.stages[i]; if (st != null && frac < st.hpFrac && (best == null || st.hpFrac < best.hpFrac)) best = st; }
        return best;
    }

    void Update()
    {
        var D = WoundTable.Get(); if (!D.enabled) return;
        float dt = Time.deltaTime, now = Time.unscaledTime;
        refreshT -= dt;
        if (refreshT <= 0f)
        {
            refreshT = Mathf.Max(0.1f, D.refreshSec);
            foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) { Get(f); if (f.CharacterId == Roster.PlayerId) player = f; }
            List<DuelFighter> gone = null;
            foreach (var kv in states) if (kv.Key == null) { if (gone == null) gone = new List<DuelFighter>(); gone.Add(kv.Key); }
            if (gone != null) foreach (var g in gone) { WoundDecals.Clear(states[g].decals); states.Remove(g); }
        }
        foreach (var kv in states)
        {
            var s = kv.Value; var f = kv.Key; if (f == null) continue;
            var row = WoundTable.For(f.CharacterId);
            // a refilled fighter (new fight / heal to full) loses its wounds
            if (f.health >= f.maxHealth - 0.01f && s.wounds > 0) { WoundDecals.Clear(s.decals); s.wounds = 0; s.legUntil = -1f; }
            WoundDecals.Tick(s.decals, row, now);
            float frac = f.maxHealth > 0f ? f.health / f.maxHealth : 1f;
            var st = D.states ? StageFor(row, frac) : null;
            float tMove = st != null ? st.moveMul : 1f, tRegen = st != null ? st.regenMul : 1f, tLayer = st != null ? st.guardLayerMul : 1f; int tRaise = st != null ? st.guardRaiseExtraTicks : 0;
            float step = dt / Mathf.Max(0.05f, row.blendSec);
            s.moveMul = Mathf.MoveTowards(s.moveMul, tMove, step); s.regenMul = Mathf.MoveTowards(s.regenMul, tRegen, step); s.layerMul = Mathf.MoveTowards(s.layerMul, tLayer, step);
            s.raiseExtra = tRaise; string sid = st != null ? st.id : "";
            if (sid != s.stage) { if (D.debugLog) Debug.Log("[Wound] " + f.name + " stage '" + s.stage + "' -> '" + sid + "' at " + (frac * 100f).ToString("F0") + "% hp"); s.stage = sid; s.dripT = 0f; }
            if (st != null && st.dripEverySec > 0f && !f.Dead && !RTFinisher.Busy)
            {
                s.dripT += dt;
                if (s.dripT >= st.dripEverySec) { s.dripT -= st.dripEverySec; BloodGround.Drip(f.transform.position, Mathf.Max(1, st.dripSplats), st.dripSizeMul * FighterScale.Of(f)); }
            }
        }
        // player vignette alpha (eased so a hit does not pop)
        var V = D.vignette; float target = 0f;
        if (V.enabled && player != null && player.maxHealth > 0f)
        {
            float frac = player.health / player.maxHealth;
            if (frac < V.startFrac && V.startFrac > 0f) target = V.maxAlpha * Mathf.Clamp01((V.startFrac - frac) / V.startFrac);
        }
        vigAlpha = Mathf.MoveTowards(vigAlpha, target, Mathf.Max(0.0001f, V.maxAlpha) * dt / Mathf.Max(0.05f, V.fadeInSec));
    }

    // ---------------- player low-HP vignette (IMGUI, like GameHUD / BloodEdge) ----------------
    Texture2D Vig(WoundTable.VignetteCfg V)
    {
        if (vigTex != null) return vigTex;
        int n = Mathf.Clamp(V.texSize, 16, 512); var px = new Color32[n * n]; float inner = Mathf.Clamp(V.inner, 0f, 0.95f);
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;   // 0 centre .. 1 corner
            float a = Mathf.Clamp01((r - inner) / (1f - inner)); a = a * a * (3f - 2f * a);
            px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
        }
        vigTex = new Texture2D(n, n, TextureFormat.RGBA32, false, true) { name = "WoundVignette", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        vigTex.SetPixels32(px); vigTex.Apply(false, true); return vigTex;
    }

    void OnGUI()
    {
        var V = WoundTable.Get().vignette; if (!V.enabled || vigAlpha <= 0.002f || Event.current.type != EventType.Repaint) return;
        float pulse = 1f + V.pulseDepth * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * V.pulseHz);
        GUI.depth = V.guiDepth; var old = GUI.color;
        GUI.color = new Color(V.color[0], V.color[1], V.color[2], Mathf.Clamp01(vigAlpha * pulse));
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Vig(V), ScaleMode.StretchToFill, true);
        GUI.color = old;
    }
}
