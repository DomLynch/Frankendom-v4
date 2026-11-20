// NEW FILE: Assets/Scripts/Finishers/FkDecap.cs   (NOT APPLIED; compile-checked by nobody yet)
// Real-time decapitation on a killing slash. No pause, no Time.timeScale, no camera, no new MonoBehaviour field, no RuntimeInitializeOnLoadMethod.
// Entry: RagdollDeath.OnKilledGlobal calls FkDecap.OnKilled(f) FIRST (existing boot path). Data: decap.json via DataPath.ReadText (enabled=false).
// Reuses MeshCut (split mesh + dark wet red cap + rigid head piece with cuts.units["head"] mass/launch numbers) and RagdollRig (already skips a bone scaled to ~0).
using System;
using System.Collections.Generic;
using UnityEngine;

public static class FkDecap
{
    [Serializable] class BloodCfg { public bool enabled = false; }
    [Serializable] class Cfg
    {
        public bool enabled = false; public float chance = 0.25f; public int seed = 20261120; public float cooldownSeconds = 0f;
        public string[] moves = new string[0], edgedWeapons = new string[0], bluntWeapons = new string[0], victimDeny = new string[0], victimAllowOverride = new string[0], attackerIds = new string[0];
        public string unit = "head"; public float pieceImpulseScale = 1f, headMassKg = 4.5f, headLifeSeconds = 25f; public int maxLiveHeads = 6; public BloodCfg blood = new BloodCfg(); public bool log = true;
    }
    static Cfg cfg; static bool loaded; static System.Random rng = new System.Random(20261120); static float lastAt = -100f; static readonly List<GameObject> live = new List<GameObject>();
    public static bool BloodOff;   // read by the one-line guard at the top of MeshCut.Blood (mesh_cut_blood.diff)
    public static int Total, Skipped, Failed;

    static Cfg Load()
    {
        if (loaded) return cfg; loaded = true;
        try { var t = DataPath.ReadText("decap.json"); if (!string.IsNullOrEmpty(t)) cfg = JsonUtility.FromJson<Cfg>(t); }
        catch (Exception e) { Debug.LogWarning("[decap] decap.json unreadable (" + e.Message + "): decapitation off"); }
        if (cfg == null) cfg = new Cfg(); else rng = new System.Random(cfg.seed);
        Debug.Log("[decap] loaded: enabled " + cfg.enabled + ", chance " + cfg.chance + ", blood " + cfg.blood.enabled);
        return cfg;
    }
    static bool Has(string[] a, string s) { if (a == null || s == null) return false; for (int i = 0; i < a.Length; i++) if (a[i] == s) return true; return false; }
    static bool HasSub(string[] a, string s) { if (a == null || s == null) return false; s = s.ToLowerInvariant(); for (int i = 0; i < a.Length; i++) if (!string.IsNullOrEmpty(a[i]) && s.Contains(a[i].ToLowerInvariant())) return true; return false; }

    /// <summary>Called from RagdollDeath.OnKilledGlobal before the ragdoll component is added. Never throws; any refusal = the normal kill runs unchanged.</summary>
    public static void OnKilled(DuelFighter victim)
    {
        try
        {
            var c = Load(); if (!c.enabled || victim == null) return;
            var atk = victim.opponent; var m = atk != null ? atk.CurrentMove : null;   // VERIFY on first run: the MoveDef passed to Killed() may be the victim's own move; the attacker's current move is the blow
            string why = Refuse(c, victim, atk, m); if (why != null) { Skipped++; if (c.log) Debug.Log("[decap] skip " + victim.name + " why=" + why); return; }
            if (rng.NextDouble() >= c.chance) { Skipped++; if (c.log) Debug.Log("[decap] skip " + victim.name + " why=chance"); return; }
            lastAt = Time.unscaledTime;
            Vector3 away = victim.transform.position - atk.transform.position; away.y = 0f; away = away.sqrMagnitude < 1e-4f ? atk.transform.forward : away.normalized;
            if (!MeshCut.CanCut(victim.transform, c.unit)) { Skipped++; if (c.log) Debug.Log("[decap] skip " + victim.name + " why=no-unit"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BloodOff = !c.blood.enabled;
            CutResult res; try { res = MeshCut.Cut(victim.transform, c.unit, c.seed + Total, away, 1f, 60f * c.pieceImpulseScale); } finally { BloodOff = false; }
            if (res == null || !res.ok) { Failed++; Debug.LogWarning("[decap] cut failed on " + victim.name + ": " + (res != null ? res.note : "null") + " (normal kill continues)"); return; }
            Total++; Track(res);
            if (c.log) Debug.Log("[decap] DECAP " + victim.name + " move=" + m.id + " ms=" + sw.ElapsedMilliseconds + " pieces=" + res.pieces.Count + " total=" + Total + " live=" + live.Count);
        }
        catch (Exception e) { Failed++; BloodOff = false; Debug.LogWarning("[decap] " + e.GetType().Name + ": " + e.Message + " (normal kill continues)"); }
    }

    static string Refuse(Cfg c, DuelFighter v, DuelFighter a, MoveDef m)
    {
        if (a == null || m == null) return "no-attacker";
        if (Duel.RangedNow) return "ranged";
        if (RTFinisher.Busy) return "finisher";
        if (!Has(c.moves, m.id)) return "move=" + m.id;
        if (c.attackerIds.Length > 0 && !HasSub(c.attackerIds, a.CharacterId)) return "attacker=" + a.CharacterId;
        string w = WeaponHooks.Of(a).id;
        if (HasSub(c.bluntWeapons, w)) return "blunt=" + w;
        if (c.edgedWeapons.Length > 0 && !Has(c.edgedWeapons, w)) return "weapon=" + w;
        if (HasSub(c.victimDeny, v.CharacterId) && !HasSub(c.victimAllowOverride, v.CharacterId)) return "victim-deny=" + v.CharacterId;
        if (c.cooldownSeconds > 0f && Time.unscaledTime - lastAt < c.cooldownSeconds) return "cooldown";
        return null;
    }

    static void Track(CutResult res)
    {
        var c = cfg; live.RemoveAll(g => g == null);
        foreach (var p in res.pieces)
        {
            if (p == null) continue; live.Add(p);
            var rb = p.GetComponent<Rigidbody>(); if (rb != null && c.headMassKg > 0f) rb.mass = c.headMassKg;
            if (c.headLifeSeconds > 0f) UnityEngine.Object.Destroy(p, c.headLifeSeconds);
        }
        while (live.Count > c.maxLiveHeads) { var o = live[0]; live.RemoveAt(0); if (o != null) UnityEngine.Object.Destroy(o); }
    }
}
