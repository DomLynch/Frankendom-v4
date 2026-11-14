using System;
using System.IO;
using UnityEngine;

// K1 (bandits dev, file-only, NOT applied). Escort attacker cap: at most boss_escort.json maxAttackers escorts (spawn ids starting with idPrefix) may be in an attack phase at once,
// so three escorts cannot stun-lock the player. Default (file missing, "enabled": false) = old behaviour, byte for byte (the hook returns false at once).
// Hook: AIBrain.Decide, one line before `intent.action = action;` :  if (BossEscort.Blocked(df)) return Done();
public static class BossEscort
{
    [Serializable] public class Cfg { public bool enabled = false; public int maxAttackers = 2; public string idPrefix = "#bandit_z"; public float rescanS = 0.25f, graceAfterHitS = 0f; public bool proofLog = false; public int proofSeconds = 20; }
    static Cfg C; static DuelFighter[] cache = new DuelFighter[0]; static float nextScan;
    static Cfg Get()
    {
        if (C != null) return C;
        C = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "boss_escort.json");
            if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/boss_escort.json");
            if (File.Exists(p)) C = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)) ?? new Cfg();
        }
        catch (Exception e) { Debug.LogWarning("[BossEscort] load failed, off: " + e.Message); C = new Cfg(); }
        Debug.Log("[BossEscort] enabled " + C.enabled + " maxAttackers " + C.maxAttackers + " prefix " + C.idPrefix);
        return C;
    }
    // graceAfterHitS: Joan's health dropping = a hit landed; no escort may START a swing for that long (the hitter is in recovery, so the others cannot chain). Noticed when an escort next decides (polled, no Duel hook).
    static float lastHp = -1f, lastHitT = -999f;
    static void NoteHit(DuelFighter p) { if (p == null) return; if (lastHp >= 0f && p.health < lastHp - 0.01f) lastHitT = Time.time; lastHp = p.health; }
    // K1 proof helper (proofLog true): one line per second, Joan's health lost per second; after proofSeconds a total line. Compare before / after with the escorts standing on her.
    class Proof : MonoBehaviour
    {
        float t0, hp0, next; int sec;
        void Start() { t0 = Time.time; next = t0 + 1f; var p = GameLoop.Player; hp0 = p != null ? p.health : 0f; }
        void Update()
        {
            var p = GameLoop.Player; if (p == null || Time.time < next) return; next += 1f; sec++;
            float lost = Mathf.Max(0f, hp0 - p.health); Debug.Log("[EscortProof] t=" + sec + "s hp " + p.health.ToString("F0") + " lost " + lost.ToString("F0") + " (" + (lost / sec).ToString("F1") + "/s)");
            if (sec >= Mathf.Max(1, Get().proofSeconds)) Destroy(this);
        }
    }
    static bool proofStarted;
    static bool Escort(DuelFighter f, Cfg c) { return f != null && f.name.IndexOf(c.idPrefix, StringComparison.Ordinal) >= 0; }
    // true = this escort must not START a swing now: the cap is already used by other escorts mid-attack (an escort already in its own attack is never blocked: Decide only gates NEW swings)
    public static bool Blocked(DuelFighter me)
    {
        var c = Get();
        if (c.proofLog && !proofStarted && Escort(me, c)) { proofStarted = true; new GameObject("EscortProof").AddComponent<Proof>(); }   // starts at the first escort decision, works with enabled false (the BEFORE number)
        if (!c.enabled || !Escort(me, c)) return false;
        NoteHit(GameLoop.Player);
        if (c.graceAfterHitS > 0f && Time.time - lastHitT < c.graceAfterHitS) { return true; }
        if (Time.unscaledTime >= nextScan) { cache = UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None); nextScan = Time.unscaledTime + Mathf.Max(0.05f, c.rescanS); }
        int n = 0;
        for (int i = 0; i < cache.Length; i++)
        {
            var f = cache[i]; if (f == null || f == me || f.Dead || !Escort(f, c)) continue;
            if (AiWorld.Phase(f) == Ph.Attack) n++;
        }
        bool blocked = n >= Mathf.Max(1, c.maxAttackers);
        if (blocked) Debug.Log("[BossEscort] hold " + me.name + " (" + n + " escorts attacking, cap " + c.maxAttackers + ")");
        return blocked;
    }
}
