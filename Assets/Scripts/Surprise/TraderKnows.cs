using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// S9 THE TRADER WHO KNOWS (pool-laneS9, Dom's "surprise mechanics"). DARK: trader_knows.json "enabled": false (or no file, or -s9:0) = this class builds nothing: no driver, no event hook, no allocation.
// (a) A vendor greeting that knows how Joan last died. The game had no death record, so this class keeps one: it listens to ContactRelay.OnKilled, and when the PLAYER dies it notes the zone, the killer's
//     creature id, the killer's weapon item, the move, the weapon Joan held, and a count per (zone, killer). The record lives in its own file (trader_deaths.json) in the SAVE DIR (SavePaths, so a -savePath sandbox
//     is honoured; never written when RunMode.RealSaveBlocked) and NEVER touches zones_save.json / loot_save.json. The greeting is one line from trader_knows.json (per vendor, matched by killer / zone / weapon /
//     minCount, most specific wins, ties picked by the seeded stream), shown ONCE per camp visit in the shop panel's own message line (MarketUI hunk, same font / colours as the shop notes).
//     Sell-back: the vendor lists, on its Buy tab, the weapon Joan held when she last died IF she no longer owns it (the game does not drop gear on death, so this is mostly a "you sold it" hook), at the price
//     of its market.json row (priceRows adds a row for an item that has none).
// (b) RARE impostor: a seeded roll once per run (impostor.chancePerRun) picks ONE vendor (impostorWeight). When Joan presses interact at that stall, the shop does NOT open: the vendor body is hidden, a hostile
//     BanditAssassin (existing prefab, existing AI) spawns at the vendor's stand point and engages, and once it is dead (or Joan fell) the real vendor is found dead behind the stall (the vendor body, posed with a
//     camp "Death" clip when it binds, else lying down by data). Cap impostor.maxPerRun per run, claimed through the shared SurpriseGate (one surprise per zone visit). While the impostor lives the camp safe rules
//     are off (AmbushLive: HubSafe.PlayerInside, MarketSafe.NoCombat, SafeZoneGiveUp), and come back when it is dead. The vendor comes back standing when Joan leaves the camp.
// Harness / proof: -s9:0 off | 1 greeting with the fake death record from the json | 2 impostor forced at proof.impostorNpcId (ignores chance, gate, enabled) | 3 both. The proof press: after proof.autoOpenAfterS in
// reach of the proof stall the "interact" is pressed for you (MarketUI hunk), once. Args are ignored under FRANKEN_NO_HARNESS.
// Every number, id, line and path is in trader_knows.json. Pairs with V21 (VendorReveal): VendorTaken(npcId) is true while a vendor is hidden / dead, so the skull glimpse can skip it.
[Serializable] public class TkLine { public string killer = "", zone = "", weapon = "", text = ""; public int minCount; public float weight = 1f; }
[Serializable] public class TkName { public string id = "", name = ""; }
[Serializable] public class TkVendor { public string stallId = "", npcId = "", role = ""; public float impostorWeight = 1f; public TkLine[] lines = new TkLine[0]; public TkLine[] fallback = new TkLine[0]; }
[Serializable] public class TkPrice { public string item = ""; public int buyValue; }
[Serializable] public class TkSellBack { public bool enabled = true, requireNotOwned = true; public string slot = "", noteKey = ""; public string[] stallIds = new string[0], sources = new string[0]; public TkPrice[] priceRows = new TkPrice[0]; }
[Serializable] public class TkGreeting { public bool enabled = true; public float visitResetSeconds = 20f; public string tokKiller = "", tokZone = "", tokWeapon = "", tokMine = "", tokCount = "", tokTotal = ""; public string unknown = ""; }
[Serializable] public class TkDeath { public string zone = "", killer = "", move = "", killerWeapon = "", playerWeapon = "", utc = ""; public int count; }
[Serializable] public class TkLedger { public int version = 1, total; public TkDeath last = new TkDeath(); public TkDeath[] byKey = new TkDeath[0]; }
[Serializable] public class TkCorpse { public float delayS = 2f, behindM = 1.5f, probeS = 0.6f, minMoveM = 0.05f, fallbackLiftM = 0.12f; public bool waitForImpostor = true; public string clipGlb = "", clipName = "", alias = "", idleAlias = "", probeBone = ""; public float[] fallbackEuler = new float[3]; }
[Serializable] public class TkImpostor
{
    public bool enabled = false, restoreOnLeave = true, despawnOnPlayerDeath = true; public float chancePerRun = 0f, hpMul = 1f, damageMul = 1f, aggro = 30f, leash = 40f; public int maxPerRun = 1, difficulty;
    public string gateKey = "", creature = "", ladderRow = "", spawnId = ""; public TkCorpse corpse = new TkCorpse();
}
[Serializable] public class TkProof { public TkDeath fakeDeath = new TkDeath(); public string stallId = "", impostorNpcId = ""; public float autoOpenAfterS = 3f; }
[Serializable] public class TkData
{
    public bool enabled = false, log = true; public string ledgerFile = "trader_deaths.json"; public TkGreeting greeting = new TkGreeting(); public TkName[] killers = new TkName[0], zones = new TkName[0], weapons = new TkName[0];
    public TkVendor[] vendors = new TkVendor[0]; public TkSellBack sellBack = new TkSellBack(); public TkImpostor impostor = new TkImpostor(); public TkProof proof = new TkProof();
}

public sealed class TraderKnows : MonoBehaviour
{
    const string ArgOn = "-s9:", FileName = "trader_knows.json", Salt = "s9";
    enum St { None, Fight, After, Done }
    static TkData D; static TraderKnows inst; static int mode = -1; static bool hooked;
    static TkLedger ledger; static string ledgerPath = "";
    static readonly HashSet<string> greeted = new HashSet<string>();
    static string greeting = ""; static TkVendor chosen; static bool rolled, forced, proofPressed; static float proofSeenAt = -1f; static int fired;
    static St st = St.None; static DuelFighter imp; static GameObject vendorGo; static Vector3 vendorPos; static Quaternion vendorRot; static bool vendorMoved;
    static AnimationClip deadClip; static float afterAt = -1f, probeAt = -1f; static Vector3 probeFrom; static Transform probeBone; static bool probing;

    /// <summary>The impostor is alive: the camp's safe rules (no hit, no attack refusal, no give-up) stand down.</summary>
    public static bool AmbushLive { get { return st == St.Fight && imp != null && !imp.Dead; } }
    /// <summary>The greeting line for the shop panel that is open now ("" = none).</summary>
    public static string Greeting { get { return greeting; } }
    /// <summary>The vendor body is hidden or lying dead (V21 / VendorReveal can skip it).</summary>
    public static bool VendorTaken(string npcId) { return vendorGo != null && st != St.None && chosen != null && chosen.npcId == npcId; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        D = null; inst = null; mode = -1; ledger = null; ledgerPath = ""; greeted.Clear(); greeting = ""; chosen = null; rolled = false; forced = false; proofPressed = false; proofSeenAt = -1f; fired = 0;
        st = St.None; imp = null; vendorGo = null; vendorMoved = false; deadClip = null; afterAt = -1f; probeAt = -1f; probing = false; probeBone = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null) return;
        int force = -1;
#if !FRANKEN_NO_HARNESS
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.StartsWith(ArgOn, StringComparison.OrdinalIgnoreCase)) { int v; force = int.TryParse(a.Substring(ArgOn.Length), out v) ? v : 1; }
#endif
        if (force == 0) return;
        string txt = DataPath.ReadText(FileName); if (txt == null) return;
        TkData d; try { d = JsonUtility.FromJson<TkData>(txt.TrimStart((char)0xFEFF)); } catch (Exception e) { Debug.LogWarning("[S9] " + FileName + " unreadable, off: " + e.Message); return; }
        if (d == null || (!d.enabled && force < 1)) return;
        D = d; mode = force; ledgerPath = Path.Combine(SavePaths.Dir, D.ledgerFile);
        Duel_Hook();
        var go = new GameObject("TraderKnows"); DontDestroyOnLoad(go); inst = go.AddComponent<TraderKnows>();
        if (D.log) Debug.Log("[S9] on: " + D.vendors.Length + " vendors, impostor " + (D.impostor.enabled || mode >= 2) + ", mode arg " + force + ", ledger " + ledgerPath);
    }

    static void Duel_Hook() { if (hooked) { ContactRelay.OnKilled -= OnKilled; } ContactRelay.OnKilled += OnKilled; hooked = true; }

    // ---------------- the death record ----------------
    static TkLedger Ledger()
    {
        if (ledger != null) return ledger;
        ledger = new TkLedger();
        try { if (File.Exists(ledgerPath)) { var k = JsonUtility.FromJson<TkLedger>(File.ReadAllText(ledgerPath).TrimStart((char)0xFEFF)); if (k != null) ledger = k; } }
        catch (Exception e) { Debug.LogWarning("[S9] ledger unreadable, starting empty: " + e.Message); ledger = new TkLedger(); }
        if (ledger.last == null) ledger.last = new TkDeath(); if (ledger.byKey == null) ledger.byKey = new TkDeath[0];
        return ledger;
    }

    static string ItemIn(DuelFighter f, string slot)
    {
        if (f == null || string.IsNullOrEmpty(slot)) return ""; var gs = f.GetComponent<GearSlots>(); return gs != null ? gs.ItemIn(slot) : "";
    }

    static void OnKilled(DuelFighter f, MoveDef m)
    {
        try
        {
            if (D == null || f == null || !f.IsPlayer) return;
            var k = f.opponent; var L = Ledger(); string zone = ZoneRuntime.Inst != null ? ZoneRuntime.Inst.CurZoneId : "", killer = k != null ? k.CharacterId : "";
            int n = 0; var rows = new List<TkDeath>(L.byKey); TkDeath row = null;
            foreach (var r in rows) if (r != null && r.zone == zone && r.killer == killer) { row = r; break; }
            if (row == null) { row = new TkDeath { zone = zone, killer = killer }; rows.Add(row); }
            row.count++; n = row.count;
            var rec = new TkDeath { zone = zone, killer = killer, move = m != null ? m.id : "", killerWeapon = ItemIn(k, D.sellBack.slot), playerWeapon = ItemIn(f, D.sellBack.slot), utc = DateTime.UtcNow.ToString("o"), count = n };
            L.last = rec; L.total++; L.byKey = rows.ToArray(); row.utc = rec.utc;
            if (D.log) Debug.Log("[S9] death noted: " + zone + " / " + killer + " (" + n + "x), killer weapon '" + rec.killerWeapon + "', Joan held '" + rec.playerWeapon + "', total " + L.total);
            if (RunMode.RealSaveBlocked) { Debug.Log("[S9] real save blocked (harness without -savePath): ledger stays in memory"); return; }
            SafeFile.AtomicWrite(ledgerPath, JsonUtility.ToJson(L, true));
        }
        catch (Exception e) { Debug.LogWarning("[S9] death record failed: " + e.Message); }
    }

    // ---------------- the greeting and the sell-back ----------------
    static string NameOf(TkName[] t, string id) { if (t != null) foreach (var n in t) if (n != null && n.id == id && n.name.Length > 0) return n.name; return id.Length > 0 ? id : D.greeting.unknown; }

    static TkDeath Record()
    {
        if ((mode & 1) != 0) return D.proof.fakeDeath;   // proof: the fake record from the json (modes 1 and 3)
        var L = Ledger(); return L.total > 0 ? L.last : null;
    }

    static int Score(TkLine l, TkDeath r)
    {
        int s = 0;
        if (l.killer.Length > 0) { if (l.killer != r.killer) return -1; s++; }
        if (l.zone.Length > 0) { if (l.zone != r.zone) return -1; s++; }
        if (l.weapon.Length > 0) { if (l.weapon != r.killerWeapon) return -1; s++; }
        if (l.minCount > 0) { if (r.count < l.minCount) return -1; s++; }
        return s;
    }

    static TkLine Pick(TkLine[] ls, TkDeath r, System.Random rng)
    {
        int best = -1; float tot = 0f; foreach (var l in ls) { if (l == null) continue; int s = Score(l, r); if (s > best) { best = s; tot = 0f; } if (s == best && s >= 0) tot += Mathf.Max(0f, l.weight); }
        if (best < 0) return null;
        float x = (float)rng.NextDouble() * tot;
        foreach (var l in ls) { if (l == null || Score(l, r) != best) continue; x -= Mathf.Max(0f, l.weight); if (x <= 0f) return l; }
        foreach (var l in ls) if (l != null && Score(l, r) == best) return l;
        return null;
    }

    static string Fill(string t, TkDeath r)
    {
        var G = D.greeting; int total = (mode & 1) != 0 ? r.count : Ledger().total;
        return t.Replace(G.tokKiller, NameOf(D.killers, r.killer)).Replace(G.tokZone, NameOf(D.zones, r.zone)).Replace(G.tokWeapon, NameOf(D.weapons, r.killerWeapon)).Replace(G.tokMine, NameOf(D.weapons, r.playerWeapon))
                .Replace(G.tokCount, r.count.ToString()).Replace(G.tokTotal, total.ToString());
    }

    static TkVendor VendorByStall(string stallId) { if (D != null) foreach (var v in D.vendors) if (v != null && v.stallId == stallId) return v; return null; }

    /// <summary>MarketUI.OpenUi hunk: before the panel opens. Picks the once-per-visit greeting and adds the sell-back line to the Buy stock.</summary>
    public static void OnOpen(string stallId)
    {
        greeting = ""; if (D == null) return;
        try
        {
            var r = Record(); var v = VendorByStall(stallId); if (r == null || v == null) return;
            if (D.greeting.enabled && !greeted.Contains(stallId))
            {
                greeted.Add(stallId);
                var rng = new System.Random(unchecked((int)SurpriseGate.Seed(Salt + ".greet." + stallId) ^ Ledger().total));
                var l = Pick(v.lines, r, rng) ?? Pick(v.fallback, r, rng);
                if (l != null) { greeting = Fill(l.text, r); if (D.log) Debug.Log("[S9] greeting at " + stallId + ": " + greeting); }
            }
            SellBack(stallId, r);
        }
        catch (Exception e) { Debug.LogWarning("[S9] greeting failed: " + e.Message); }
    }

    static void SellBack(string stallId, TkDeath r)
    {
        var S = D.sellBack; var B = MarketBuyRules.Cur; if (!S.enabled || !B.enabled || Array.IndexOf(S.stallIds, stallId) < 0) return;
        foreach (var src in S.sources)
        {
            string id = src == "killerWeapon" ? r.killerWeapon : src == "playerWeapon" ? r.playerWeapon : ""; if (id.Length == 0) continue;
            if (S.requireNotOwned && LootSave.Owns(id)) continue;
            var rows = MarketCfg.Rows(); bool has = false; foreach (var q in rows) if (q != null && q.item == id) { has = q.buyValue > 0; break; }
            if (!has) { foreach (var p in S.priceRows) if (p != null && p.item == id && p.buyValue > 0) { rows.Add(new MarketRow { item = id, buyValue = p.buyValue, sellable = true }); has = true; break; } }
            if (!has) continue;
            if (Array.IndexOf(B.items, id) < 0) { var l = new List<string>(B.items) { id }; B.items = l.ToArray(); if (D.log) Debug.Log("[S9] sell-back listed at " + stallId + ": " + id); }
            return;
        }
    }

    // ---------------- the impostor ----------------
    static void Roll()
    {
        rolled = true; if (D == null) return;
        var I = D.impostor;
        if ((mode & 2) != 0) { foreach (var v in D.vendors) if (v != null && v.npcId == D.proof.impostorNpcId) chosen = v; forced = chosen != null; SurpriseGate.ForceEnable(true); if (D.log) Debug.Log("[S9] impostor FORCED at " + (chosen != null ? chosen.npcId : "(no such vendor)")); return; }
        if (!I.enabled || I.maxPerRun <= 0) return;
        var rng = new System.Random(unchecked((int)SurpriseGate.Seed(Salt + ".impostor")));
        if (rng.NextDouble() >= I.chancePerRun) { if (D.log) Debug.Log("[S9] impostor roll: not this run"); return; }
        float tot = 0f; foreach (var v in D.vendors) if (v != null) tot += Mathf.Max(0f, v.impostorWeight);
        if (tot <= 0f) return;
        float x = (float)rng.NextDouble() * tot;
        foreach (var v in D.vendors) { if (v == null) continue; x -= Mathf.Max(0f, v.impostorWeight); if (x <= 0f && v.impostorWeight > 0f) { chosen = v; break; } }
        if (D.log) Debug.Log("[S9] impostor roll: this run, at " + (chosen != null ? chosen.npcId : "?"));
    }

    /// <summary>MarketUI.Update hunk: true once, in reach of the proof stall, after proof.autoOpenAfterS (proof modes only). The same call then goes through Intercept.</summary>
    public static bool ProofPress(string stallId)
    {
        if (D == null || mode < 1 || proofPressed) return false;
        string want = (mode & 2) != 0 && chosen != null ? chosen.stallId : D.proof.stallId; if (stallId != want) return false;
        if (proofSeenAt < 0f) proofSeenAt = Time.unscaledTime;
        if (Time.unscaledTime - proofSeenAt < D.proof.autoOpenAfterS) return false;
        proofPressed = true; return true;
    }

    /// <summary>MarketUI.Update hunk: Joan pressed interact at `stallId`. True = the impostor took it (the shop must NOT open).</summary>
    public static bool Intercept(string stallId)
    {
        if (D == null || chosen == null || chosen.stallId != stallId || st != St.None) return false;
        var I = D.impostor; if (fired >= I.maxPerRun) return false;   // the cap per run (forced proofs obey it too)
        string zone = ZoneRuntime.Inst != null ? ZoneRuntime.Inst.CurZoneId : ""; if (zone.Length == 0) return false;
        var npc = HubNpcs.TryGet(chosen.npcId); if (npc == null) return false;
        if (!SurpriseGate.TryClaim(zone, I.gateKey)) return false;
        vendorGo = npc; vendorPos = npc.transform.position; vendorRot = npc.transform.rotation; vendorMoved = false;
        var df = ZoneRuntime.SpawnExtra(I.spawnId + "_" + chosen.npcId, I.creature, vendorPos, vendorRot.eulerAngles.y, I.ladderRow, I.hpMul, I.damageMul, I.difficulty, I.aggro, I.leash);
        if (df == null) { vendorGo = null; if (D.log) Debug.LogWarning("[S9] impostor not spawned (creature prefab / zone runtime missing): the shop opens as usual"); return false; }
        imp = df; fired++; st = St.Fight; npc.SetActive(false); inst.LoadDeathClip();
        if (D.log) Debug.Log("[S9] IMPOSTOR at " + chosen.npcId + " (" + I.creature + "), real vendor hidden");
        return true;
    }

    async void LoadDeathClip()
    {
        var C = D.impostor.corpse; deadClip = null; if (C.clipGlb.Length == 0) return;
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, C.clipGlb); if (!File.Exists(path)) { if (D.log) Debug.Log("[S9] death clip glb missing, fallback pose: " + path); return; }
            var gi = new GLTFast.GltfImport(); if (!await gi.LoadFile(path)) return;
            var clips = gi.GetAnimationClips(); if (clips != null) foreach (var c in clips) if (c != null && c.name == C.clipName) deadClip = c;
            if (D.log) Debug.Log("[S9] death clip '" + C.clipName + "' " + (deadClip != null ? "loaded" : "not found in glb"));
        }
        catch (Exception e) { Debug.LogWarning("[S9] death clip load failed, fallback pose: " + e.Message); deadClip = null; }
    }

    static Vector3 V3(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }
    static Transform FindDeep(Transform t, string n) { if (t.name == n) return t; for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), n); if (r != null) return r; } return null; }

    void RevealCorpse()
    {
        var C = D.impostor.corpse; var go = vendorGo; if (go == null) return;
        Vector3 fwd = vendorRot * Vector3.forward; go.transform.SetPositionAndRotation(vendorPos - fwd * C.behindM, vendorRot); vendorMoved = true; go.SetActive(true);
        var an = go.GetComponentInChildren<Animation>(true);
        if (deadClip != null && an != null)
        {
            if (an[C.alias] == null) an.AddClip(deadClip, C.alias);
            var s = an[C.alias]; if (s != null)
            {
                s.wrapMode = WrapMode.ClampForever; s.time = 0f; an.Play(C.alias);
                probeBone = C.probeBone.Length > 0 ? FindDeep(go.transform, C.probeBone) : null;
                if (probeBone != null) { probeFrom = probeBone.position; probeAt = Time.unscaledTime + C.probeS; probing = true; } return;
            }
        }
        Fallback(go, an);
    }

    void Fallback(GameObject go, Animation an)
    {
        var C = D.impostor.corpse; if (an != null) an.Stop();
        go.transform.rotation = Quaternion.Euler(V3(C.fallbackEuler)) * go.transform.rotation; go.transform.position += Vector3.up * C.fallbackLiftM;
        if (D.log) Debug.Log("[S9] corpse: fallback lying pose");
    }

    void Restore()
    {
        var go = vendorGo; if (go != null && vendorMoved)
        {
            go.transform.SetPositionAndRotation(vendorPos, vendorRot); go.SetActive(true);
            var an = go.GetComponentInChildren<Animation>(true); var ia = D.impostor.corpse.idleAlias; if (an != null) { an.Stop(); if (ia.Length > 0 && an[ia] != null) an.Play(ia); }
        }
        else if (go != null) go.SetActive(true);
        if (D.log) Debug.Log("[S9] vendor restored (Joan left the camp)");
        vendorGo = null; imp = null; probing = false; deadClip = null; st = St.Done;
    }

    // ---------------- the driver ----------------
    float outsideT; bool visitOver = true;
    void Update()
    {
        if (D == null) return; var pl = GameLoop.Player; float dt = Time.unscaledDeltaTime;
        if (!rolled && pl != null) Roll();
        var I = D.impostor;
        bool inside = pl != null && (st == St.Fight || HubSafe.PlayerInside() || MarketSafe.NoCombat);
        if (inside) { outsideT = 0f; visitOver = false; }
        else { outsideT += dt; if (!visitOver && outsideT >= D.greeting.visitResetSeconds) { visitOver = true; greeted.Clear(); greeting = ""; if (st == St.After) Restore(); } }
        if (st == St.Fight)
        {
            bool joanDown = pl != null && pl.Dead;
            if (joanDown && I.despawnOnPlayerDeath && imp != null) { Destroy(imp.gameObject); imp = null; }
            if (imp == null || imp.Dead || joanDown) { st = St.After; afterAt = Time.unscaledTime + I.corpse.delayS; if (D.log) Debug.Log("[S9] impostor over, vendor corpse in " + I.corpse.delayS + " s"); }
        }
        else if (st == St.After && afterAt >= 0f && Time.unscaledTime >= afterAt) { afterAt = -1f; RevealCorpse(); }
        if (probing && Time.unscaledTime >= probeAt)
        {
            probing = false;
            if (probeBone == null || (probeBone.position - probeFrom).magnitude < I.corpse.minMoveM) Fallback(vendorGo, vendorGo != null ? vendorGo.GetComponentInChildren<Animation>(true) : null);
            else if (D.log) Debug.Log("[S9] corpse: death clip drives the body");
        }
    }
}
