using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// Lane B1: data-driven fight balance. Reads fight_balance.json (StreamingAssets first, then Application.dataPath/Data) at every scene load
// (so at fight start and on every restart) and applies per-fighter knobs. Code holds no tuning numbers; a missing file or "enabled": false = untouched game.
// Hooks in other files (one line each): Duel.cs hit damage  `if (FightBalance.Active) dmg *= FightBalance.HitMul(a, d, m);`.
// Special cooldown / use cap are applied here by watching DuelFighter.special1Cd / special2Cd jump up after a cast (no DuelFighter edit).
public static class FightBalance
{
    [Serializable] public class Row
    {
        public string key = "";                 // roster.json character id (matched on the fighter's / root's name)
        public float maxHealth = 0f;            // 0 = keep the scene value
        public float maxStamina = 0f;           // 0 = keep (stamina.json fighters[].regenMul is the regen knob)
        public float damageMul = 1f;            // damage this fighter DEALS (all moves)
        public float takenMul = 1f;             // damage this fighter TAKES
        public float specialDamageMul = 1f;     // extra multiplier on this fighter's special moves (skill_*)
        public int specialCooldownTicks = 0;    // cooldown after a cast, 60 ticks = 1 s; 0 = keep the game value (540)
        public int specialInitialCooldownTicks = -1;   // first availability; -1 = keep (120)
        public int specialMaxUses = 0;          // casts per special slot per fight; 0 = unlimited
    }
    [Serializable] public class Cfg { public bool enabled = true; public string note = ""; public Row[] fighters = new Row[0]; }

    public static Cfg C = new Cfg();
    public static bool Active { get { return C != null && C.enabled; } }
    public static string Source = "(none)";
    public static string Summary = "";

    public static Row For(DuelFighter f)
    {
        if (f == null || C == null || C.fighters == null) return null;
        string n = f.name, r = f.transform.root.name;
        { var rrp = RosterResolver.Pick(C.fighters, rrx => rrx.key, n, false); if (rrp == null) rrp = RosterResolver.Pick(C.fighters, rrx => rrx.key, r, false); if (rrp != null) return rrp; }   // HCKIT2 v93
        return null;
    }

    // multiplier on one blow: attacker's damageMul (and special mul for skill_*) times the defender's takenMul
    public static float HitMul(DuelFighter a, DuelFighter d, MoveDef m)
    {
        float k = 1f; var ra = For(a); var rd = For(d);
        if (ra != null) { k *= ra.damageMul; if (m != null && m.id != null && m.id.StartsWith("skill_")) k *= ra.specialDamageMul; }
        if (rd != null) k *= rd.takenMul;
        k *= StanceLayer.DamageMul(a, d);   // lanePARRY: live stance layer (stances.json), off in steer mode (STStanceMods owns stances there)
        return k;
    }

    public static void Load()
    {
        C = new Cfg(); Source = "(none)";
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "fight_balance.json");
            if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/fight_balance.json");
            if (File.Exists(p)) { C = JsonUtility.FromJson<Cfg>(File.ReadAllText(p)) ?? new Cfg(); Source = p; }
        }
        catch (Exception e) { Debug.LogWarning("[FightBalance] load failed, defaults kept: " + e.Message); C = new Cfg(); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("FightBalance"); go.hideFlags = HideFlags.HideInHierarchy; UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<FightBalanceDriver>();
    }

    public class FightBalanceDriver : MonoBehaviour
    {
        class St { public int p1, p2, uses1, uses2; }
        readonly Dictionary<DuelFighter, St> st = new Dictionary<DuelFighter, St>();
        int frames = 99, lateScan;   // SOBEKBAL v1

        void OnEnable() { SceneManager.sceneLoaded += OnLoaded; BeginFight(); }
        void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
        void OnLoaded(Scene s, LoadSceneMode m) { BeginFight(); }
        void BeginFight() { Load(); st.Clear(); frames = 0; }

        void Update()
        {
            if (frames < 8) { frames++; ApplyNew(); }
            else if ((++lateScan & 31) == 0) ApplyNew();   // SOBEKBAL v1: clones spawned later (zone respawns, gate arrivals) get their rows too; known fighters are skipped by st
        }

        void ApplyNew()
        {
            if (!Active) return;
            foreach (var f in FindObjectsByType<DuelFighter>())
            {
                if (f == null || st.ContainsKey(f)) continue;
                var r = For(f); if (r == null) continue;
                bool fresh = f.health >= f.maxHealth - 0.01f;
                if (r.maxHealth > 0f) { f.maxHealth = r.maxHealth; if (fresh) f.health = r.maxHealth; var hp = f.GetComponent<Health>(); if (hp) { hp.max = r.maxHealth; if (fresh) hp.current = r.maxHealth; } }
                if (r.maxStamina > 0f) { f.maxStamina = r.maxStamina; f.stamina = r.maxStamina; }
                if (r.specialInitialCooldownTicks >= 0) { f.special1Cd = r.specialInitialCooldownTicks; f.special2Cd = r.specialInitialCooldownTicks; }
                st[f] = new St { p1 = f.special1Cd, p2 = f.special2Cd };
                Summary += (Summary.Length > 0 ? "; " : "") + r.key + " hp " + f.maxHealth + " dmgx" + r.damageMul + " takenx" + r.takenMul + " spx" + r.specialDamageMul + " spCd " + r.specialCooldownTicks;
            }
            if (Summary.Length > 0) { Debug.Log("[FightBalance] " + Summary + " (" + Source + ")"); Summary = ""; }
        }

        void LateUpdate()
        {
            if (!Active) return;
            foreach (var kv in st)
            {
                var f = kv.Key; if (f == null) continue; var r = For(f); if (r == null) continue; var s = kv.Value;
                if (f.special1Cd > s.p1 + 50) { s.uses1++; if (r.specialMaxUses > 0 && s.uses1 >= r.specialMaxUses) f.special1Cd = 600000; else if (r.specialCooldownTicks > 0) f.special1Cd = r.specialCooldownTicks; }
                if (f.special2Cd > s.p2 + 50) { s.uses2++; if (r.specialMaxUses > 0 && s.uses2 >= r.specialMaxUses) f.special2Cd = 600000; else if (r.specialCooldownTicks > 0) f.special2Cd = r.specialCooldownTicks; }
                s.p1 = f.special1Cd; s.p2 = f.special2Cd;
            }
        }
    }
}
