using System;
using System.Collections.Generic;
using UnityEngine;

// POTIONS v1 (lanePOTIONS): drinking. A DontDestroyOnLoad driver made at runtime (like MarketUI); nothing in the scene, no splice in Duel / PlayerBrain.
//   * Each frame it reads the use key and the cycle key (consumables.json useKeys / cycleKeys; R is the camera recenter, so the defaults are H / pad Back and J).
//   * Presses are applied on the 60 Hz sim clock: every Duel.TickCount step since the last frame runs one StepHot(); a press is judged at the current tick. A cooldown is counted in ticks
//     (cooldownS x 60), a heal "over N seconds" is paid out per tick, so the result does not depend on the display rate.
//   * Refused (nothing is consumed) while: no player / dead, the fighter state is in rules.blockStates (default Dead, Down, Stagger, Roll, Windup, Active), a finisher scene runs (RTFinisher.Busy / Duel.Frozen),
//     a menu is open (loot screen, market panel, scene change), the potion is on cooldown, none are carried, or the stat is already full (rules.refuseWhenFull).
//   * One drink = one potion taken from PotionStore, one LootSave.Save() (the count survives a crash), then the effect on DuelFighter.health / stamina (and Health.current, which the fighter mirrors).
// Master off (consumables.json enabled false, the shipped value) or a harness run: the object is never created.
[DefaultExecutionOrder(-405)]
public sealed class PotionUse : MonoBehaviour
{
    public static PotionUse Instance;
    /// <summary>(potion id) raised once per drink: a hook for audio / FX later.</summary>
    public static event Action<string> OnUsed;
    /// <summary>The potion the use key drinks ("" = none chosen / none carried).</summary>
    public static string Selected = "";
    public static string ToastText = ""; public static float ToastAt = -99f;

    struct Hot { public bool health; public float left, per; }
    readonly List<Hot> hots = new List<Hot>();
    readonly Dictionary<string, int> readyAt = new Dictionary<string, int>();   // potion id -> sim tick it can be drunk again
    int lastTick; bool pressUse, pressCycle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Instance = null; OnUsed = null; Selected = ""; ToastText = ""; ToastAt = -99f; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (!PotionCfg.On || Instance != null || GameLoop.HarnessOn) return;
            var go = new GameObject("PotionUse"); DontDestroyOnLoad(go); go.AddComponent<PotionUse>();
        }
        catch (Exception e) { Debug.LogWarning("[Potions] boot failed: " + e.Message); }
    }

    void Awake() { Instance = this; lastTick = Duel.TickCount; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        var C = PotionCfg.Get(); if (!C.enabled) return;
        if (MarketCfg.AnyDown(PotionCfg.UseKeys())) pressUse = true;
        if (MarketCfg.AnyDown(PotionCfg.CycleKeys())) pressCycle = true;
        Step(C);
    }

    void Step(PotionCfg.Data C)
    {
        int now = Duel.TickCount;
        if (now < lastTick) { hots.Clear(); readyAt.Clear(); lastTick = now; }   // a new scene restarted the sim clock
        var f = GameLoop.Player;
        if (pressCycle) { pressCycle = false; Cycle(); }
        if (pressUse) { pressUse = false; Use(C, f, now); }
        while (lastTick < now) { lastTick++; StepHot(f); }
    }

    // ---- selection ----
    /// <summary>Make sure Selected is a potion that is carried; pick the first carried one (file order) when it is not.</summary>
    public static string EnsureSelected()
    {
        if (PotionStore.Qty(Selected) > 0 && PotionStore.IsStack(Selected)) return Selected;
        var P = PotionCfg.Get().potions;
        for (int i = 0; i < P.Length; i++) if (PotionStore.IsStack(P[i].id) && PotionStore.Qty(P[i].id) > 0) { Selected = P[i].id; return Selected; }
        Selected = ""; return Selected;
    }
    static void Cycle()
    {
        var P = PotionCfg.Get().potions; int start = 0;
        for (int i = 0; i < P.Length; i++) if (P[i].id == Selected) start = i;
        for (int k = 1; k <= P.Length; k++) { var p = P[(start + k) % P.Length]; if (PotionStore.IsStack(p.id) && PotionStore.Qty(p.id) > 0) { Selected = p.id; return; } }
    }
    /// <summary>How many carried kinds (the HUD shows the swap hint only when more than one).</summary>
    public static int KindsCarried()
    {
        int n = 0; var P = PotionCfg.Get().potions; for (int i = 0; i < P.Length; i++) if (PotionStore.IsStack(P[i].id) && PotionStore.Qty(P[i].id) > 0) n++;
        return n;
    }
    /// <summary>0..1 of the cooldown still to run for a potion (HUD bar).</summary>
    public static float CooldownLeft01(string id)
    {
        var inst = Instance; var p = PotionCfg.Find(id); int t;
        if (inst == null || p == null || p.cooldownS <= 0f || !inst.readyAt.TryGetValue(id, out t)) return 0f;
        float total = Mathf.Max(1f, Ticks(p.cooldownS)); return Mathf.Clamp01((t - Duel.TickCount) / total);
    }
    static int Ticks(float seconds) { return Mathf.Max(0, Mathf.CeilToInt(seconds * 60f - 1e-4f)); }

    // ---- the drink ----
    bool Refuse(PotionCfg.Data C, string text) { ToastText = text; ToastAt = Time.unscaledTime; if (C.debugLog) Debug.Log("[Potions] refused: " + text); return false; }

    bool Use(PotionCfg.Data C, DuelFighter f, int now)
    {
        var T = C.text; var R = C.rules;
        if (f == null || f.Dead || !f.gameObject.activeInHierarchy) return false;   // nobody to heal: no toast either
        if (R.blockInFinisher && (RTFinisher.Busy || FinisherTime.Slowed)) return Refuse(C, T.refuseBusy);
        if (R.blockInMenus && (LootScreen.Open || MarketUI.Open || GameLoopDriver.Transitioning || Duel.Frozen)) return false;
        if (PotionCfg.Blocks(f.state)) return Refuse(C, T.refuseBusy);
        string id = EnsureSelected(); var p = PotionCfg.Find(id);
        if (p == null || PotionStore.Qty(id) < 1) return Refuse(C, T.refuseNone);
        int t; if (readyAt.TryGetValue(id, out t) && now < t) return Refuse(C, T.refuseCooldown);
        bool hp = p.kind == "heal";
        if (R.refuseWhenFull && (hp ? f.health >= f.maxHealth - 0.01f : f.stamina >= f.maxStamina - 0.01f)) return Refuse(C, T.refuseFull);

        float amount = hp ? p.healFlat + p.healFracOfMax * f.maxHealth : p.restoreFracOfMax * f.maxStamina;
        if (amount <= 0f) return Refuse(C, T.refuseBusy);
        if (!PotionStore.Take(id)) return Refuse(C, T.refuseNone);
        LootSave.Save();   // the count is on disk before the effect (a crash never gives the potion back for free and never eats it without the effect twice)
        readyAt[id] = now + Ticks(p.cooldownS);
        if (p.overSeconds > 0f) hots.Add(new Hot { health = hp, left = amount, per = amount / Mathf.Max(1f, p.overSeconds * 60f) });
        else Apply(f, hp, amount);
        ToastText = hp ? string.Format(System.Globalization.CultureInfo.InvariantCulture, T.healed, Mathf.RoundToInt(amount)) : T.stamina; ToastAt = Time.unscaledTime;
        if (C.debugLog) Debug.Log("[Potions] drank " + id + " amount " + amount.ToString("F1") + " tick " + now + ", left " + PotionStore.Qty(id));
        if (OnUsed != null) OnUsed(id);
        EnsureSelected();
        return true;
    }

    void StepHot(DuelFighter f)
    {
        if (hots.Count == 0) return;
        if (f == null || f.Dead) { hots.Clear(); return; }
        for (int i = hots.Count - 1; i >= 0; i--)
        {
            var h = hots[i]; float x = Mathf.Min(h.per, h.left); Apply(f, h.health, x); h.left -= x;
            if (h.left <= 1e-4f) hots.RemoveAt(i); else hots[i] = h;
        }
    }

    static void Apply(DuelFighter f, bool health, float x)
    {
        if (health)
        {
            f.health = Mathf.Min(f.maxHealth, f.health + x);
            var hp = f.GetComponent<Health>(); if (hp != null) hp.current = f.health;   // DuelFighter mirrors health into Health (hp.current = health)
        }
        else f.stamina = Mathf.Min(f.maxStamina, f.stamina + x);
    }
}
