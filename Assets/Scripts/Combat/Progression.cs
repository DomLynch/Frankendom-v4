using System;
using System.IO;
using UnityEngine;

// FK2 (Lead): Joan's progression. progression.json (StreamingAssets first, then Assets/Data): one row per zone boss. Killing the boss unlocks its step for good
// (stored as a gate id "prog_step_<n>" in the existing zones save, so New game erases it with everything else) and shows a text toast in the HUD style.
// Unlocked steps multiply Joan's max health / max stamina / damage, and give her that step's perk (a multiplier on a stance-layer seam that already exists).
// No file, "enabled": false or no unlocked step = old behaviour byte for byte (every Mul returns 1). All numbers are data rows.
// Hooks (one line each): FightBalance.HitMul (damage), StanceLayer BlockCostMul / BlockChipMul / ParryWindowMul / AttackCostMul / MoveSpeedMul / PostureMul (perks).
public static class Progression
{
    [Serializable] public class Perk { public string label = ""; public float blockCostMul = 1f, blockChipMul = 1f, attackCostMul = 1f, postureTakenMul = 1f, parryWindowMul = 1f, moveSpeedMul = 1f; }
    [Serializable] public class Step { public string boss = "", zone = "", spawn = "", title = ""; public float hpMul = 1f, staminaMul = 1f, damageMul = 1f; public Perk perk = new Perk(); }
    [Serializable] public class Toast { public float seconds = 4f, fadeInS = 0.25f, fadeOutS = 0.5f, lineH = 1.6f, bottom = 240f; public int size = 22; public float[] color = new float[0]; public string text = "{0}  -  {1}"; }
    [Serializable] public class Cfg { public bool enabled = false, log = true; public string gatePrefix = "prog_step_"; public float pollS = 1f; public Toast toast = new Toast(); public Step[] steps = new Step[0]; }

    static Cfg c; static string lastToast = ""; static float toastT0 = -99f;   // hc:ok unset-time sentinel
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "progression.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/progression.json");
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[Progression] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.steps == null) c.steps = new Step[0]; if (c.toast == null) c.toast = new Toast();
        return c;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; lastToast = ""; toastT0 = -99f; ContactRelay.OnKilled -= OnKilled; }   // hc:ok unset-time sentinel

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        ContactRelay.OnKilled -= OnKilled;
        if (!Get().enabled) return;
        ContactRelay.OnKilled += OnKilled;
        var go = new GameObject("Progression"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<ProgressionDriver>();
    }

    static string Gate(int i) { return Get().gatePrefix + (i + 1); }
    public static bool Unlocked(int i) { return ZoneSave.GateUnlocked(Gate(i)); }
    static bool Unlock(int i, bool toast)
    {
        if (Unlocked(i)) return false;
        ZoneSave.UnlockGate(Gate(i)); ZoneSave.Flush(true);
        var s = Get().steps[i];
        if (Get().log) Debug.Log("[Progression] step " + (i + 1) + " unlocked (" + s.boss + "): hp x" + s.hpMul + " stamina x" + s.staminaMul + " damage x" + s.damageMul + " perk '" + s.perk.label + "' | totals hp x" + Total(0).ToString("F3") + " stamina x" + Total(1).ToString("F3") + " damage x" + Total(2).ToString("F3"));
        if (toast) { lastToast = string.Format(Get().toast.text, s.title, s.perk.label); toastT0 = Time.unscaledTime; }
        return true;
    }
    static void OnKilled(DuelFighter victim, MoveDef m)
    {
        if (victim == null || victim == GameLoop.Player || !Get().enabled) return;
        var st = Get().steps; for (int i = 0; i < st.Length; i++) if (st[i] != null && st[i].boss == victim.CharacterId) Unlock(i, true);
    }

    // kind 0 hp, 1 stamina, 2 damage: product over unlocked steps
    public static float Total(int kind)
    {
        var k = Get(); if (!k.enabled) return 1f; float m = 1f;
        for (int i = 0; i < k.steps.Length; i++) { var s = k.steps[i]; if (s == null || !Unlocked(i)) continue; m *= kind == 0 ? s.hpMul : kind == 1 ? s.staminaMul : s.damageMul; }
        return m;
    }
    public static float DamageMul(DuelFighter a) { if (a == null || a != GameLoop.Player) return 1f; return Total(2); }
    // perk field by name: product over unlocked steps (Joan only)
    public static float PerkMul(DuelFighter f, string field)
    {
        var k = Get(); if (!k.enabled || f == null || f != GameLoop.Player) return 1f; float m = 1f;
        for (int i = 0; i < k.steps.Length; i++)
        {
            var s = k.steps[i]; if (s == null || s.perk == null || !Unlocked(i)) continue;
            m *= field == "blockCost" ? s.perk.blockCostMul : field == "blockChip" ? s.perk.blockChipMul : field == "attackCost" ? s.perk.attackCostMul : field == "postureTaken" ? s.perk.postureTakenMul : field == "parryWindow" ? s.perk.parryWindowMul : field == "moveSpeed" ? s.perk.moveSpeedMul : 1f;
        }
        return m;
    }

    internal static bool ToastActive(out string text, out float alpha)
    {
        text = lastToast; alpha = 0f; if (text.Length == 0) return false;
        var k = Get().toast; float t = Time.unscaledTime - toastT0, d = Mathf.Max(0.5f, k.seconds); if (t < 0f || t > d) return false;
        alpha = Mathf.Clamp01(Mathf.Min(t / Mathf.Max(0.05f, k.fadeInS), (d - t) / Mathf.Max(0.05f, k.fadeOutS))); return true;
    }

    // Joan's base health / stamina scaled by the unlocked steps; polled, so a new step applies at once and a scene reload re-applies after FightBalance set the base.
    public class ProgressionDriver : MonoBehaviour
    {
        DuelFighter cur; float appliedHp = 1f, appliedSt = 1f, next, born;
        void OnEnable() { born = Time.unscaledTime; }
        void Update()
        {
            if (Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(0.25f, Get().pollS);
            var p = GameLoop.Player; if (p == null) return;
            if (p != cur) { cur = p; appliedHp = 1f; appliedSt = 1f; born = Time.unscaledTime; RetroUnlock(); }
            if (Time.unscaledTime - born < 3f) return;   // after FightBalance wrote the base (8 frames) and the zone load restored the hp fraction
            float hp = Total(0), st = Total(1);
            if (Mathf.Abs(hp - appliedHp) > 0.0001f && p.maxHealth > 0f)
            {
                float frac = Mathf.Clamp01(p.health / p.maxHealth), baseMax = p.maxHealth / appliedHp, nm = baseMax * hp;
                p.maxHealth = nm; p.health = frac * nm; var h = p.GetComponent<Health>(); if (h != null) { h.max = nm; h.current = p.health; }
                appliedHp = hp; if (Get().log) Debug.Log("[Progression] Joan max health " + nm.ToString("F0") + " (x" + hp.ToString("F3") + ")");
            }
            if (Mathf.Abs(st - appliedSt) > 0.0001f && p.maxStamina > 0f)
            {
                float frac = Mathf.Clamp01(p.stamina / p.maxStamina), baseMax = p.maxStamina / appliedSt, nm = baseMax * st;
                p.maxStamina = nm; p.stamina = Mathf.Max(p.stamina, frac * nm);
                appliedSt = st; if (Get().log) Debug.Log("[Progression] Joan max stamina " + nm.ToString("F0") + " (x" + st.ToString("F3") + ")");
            }
        }
        // a save from before this system: a boss whose kill record is still pending a respawn counts as killed (no toast)
        void RetroUnlock()
        {
            var st = Get().steps;
            for (int i = 0; i < st.Length; i++)
            {
                var s = st[i]; if (s == null || Unlocked(i) || string.IsNullOrEmpty(s.zone) || string.IsNullOrEmpty(s.spawn)) continue;
                var rec = ZoneSave.Rec(s.zone, s.spawn); if (rec != null && rec.respawnAtMs > ZoneCore.NowMs()) Unlock(i, false);
            }
        }
        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            string text; float a; if (!ToastActive(out text, out a)) return;
            var k = Get().toast; GUI.depth = -1000;   // hc:ok IMGUI draw order, above the HUD like the silver toast
            HudDraw.Text(new Rect(0f, Screen.height - k.bottom, Screen.width, k.size * k.lineH), text, "serif", k.size, TextAnchor.UpperCenter, false, HudCfg.Col(k.color, a));
        }
    }
}
