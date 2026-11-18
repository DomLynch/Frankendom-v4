using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

// Character roster (lane ROSTER). Who is the player and who is the enemy is DATA: Assets/Data/roster.json (copy in StreamingAssets).
// Every per-character JSON row elsewhere is keyed by the character id. Code never names a character: it asks Roster.
// A missing / broken file falls back to the built-in pair below; those two literals are the only character names allowed in code.
[Serializable] public class RosterCharacter
{
    public string id = "";
    public string displayName = "";         // HUD / log name
    public string objectName = "";          // scene GameObject name (root of the fighter)
    public string weapon = ""; public string[] specials = { "skill_cleave", "skill_reaping" }; /* DATALAYER b6 */ // special slot 1 / slot 2 move ids of this character (moves table ids); default = today's two specials              // "sword" | "maul" ... (Assets/Characters/Weapons/<weapon>_start)
    public string controller = "";          // AnimatorController asset path
    public string modelGlb = "";            // combat model asset path
    public string animGlb = "";             // animation source glb asset path
    public string guardMask = "";           // upper-body guard AvatarMask asset path
    public string finisherClipFolder = "";  // baked finisher clips folder
    public float hitFwdM = 0f, hitHeightM = 0f;   // KITHIT v1: hit point offset toward the attacker (m) / height override (m); 0 = today
    public float heightM = 0f;              // 0 = keep the scene / GameConfig size
    public bool armoured = false;           // metal: blood.json default sparks / flash when no victims[] row matches
    public string headRenderer = "";        // WOUNDSHOT v84: exact name of the head mesh object (VerifyGround HEAD check, wound close-ups); "" = verify_ground.json head.rendererName
    public float scaleFloor = 0f;           /*HCKIT1 v93 K2: floor for metre anchors, Max(scaleFloor, DuelFighter.scale); 0 = camera_framing.json scaleFloor (1) */
    public string bonePrefix = "mixamorig:";   /* HCAUDIT1 v104: skeleton bone-name prefix of this character ("" = bones carry no prefix); code asks Roster.BonePrefix / BoneName / BoneRole */
    public string notes = "";
}

public static class Roster
{
    [Serializable] public class Cfg { public string note = ""; public string player = ""; public string enemy = ""; public string fallbackEnemy = ""; /* PITDATA K1 */ public RosterCharacter[] characters = new RosterCharacter[0]; }

    // PITDATA K1: the fallback enemy is roster.json "fallbackEnemy" (data), no character literal in code; unset = the first roster row
    const string FallbackEnemy = "";   // the ONLY character literal in code /*NODRAC v93*/ FallbackPlayer literal removed: the fallback player is the first roster row that is not the enemy (FirstNonEnemy)

    static Cfg c;
    public static string Source = "(none)";
    static string defEnemy = "";                       // LADDER v92: roster.json 'enemy' as loaded; SetEnemy() overrides Cfg.enemy for one run, SetEnemy("") returns to this
    static Cfg C { get { if (c == null) Reload(); return c; } }

    public static string PlayerId => C.player;
    public static string EnemyId => C.enemy;
    public static RosterCharacter Player => Get(PlayerId);
    public static RosterCharacter Enemy => Get(EnemyId);
    public static string PlayerName => Player.displayName;
    public static string EnemyName => Enemy.displayName;
    public static string PlayerObject => Player.objectName;
    public static string EnemyObject => Enemy.objectName;
    public static RosterCharacter[] All => C.characters;

    // row by id; an unknown id gets a synthetic row whose names are the id itself (never null)
    public static RosterCharacter Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return Synth("");
        foreach (var r in C.characters) if (r != null && r.id == id) return r;
        return Synth(id);
    }

    public static bool IsPlayer(string id) { return !string.IsNullOrEmpty(id) && id == PlayerId; }
    public static bool IsEnemy(string id) { return !string.IsNullOrEmpty(id) && id == EnemyId; }

    // LADDER v92: per-fight enemy. The opponents.json ladder row names a roster character; the scene that is loaded next must hold that character's object (EnemySwap builds one scene per enemy).
    // id empty = back to the roster.json default enemy. An id with no characters[] row (or the player's id) is refused: the previous enemy stays and a warning is logged.
    public static string DefaultEnemyId { get { if (c == null) Reload(); return defEnemy; } }
    public static bool Has(string id) { if (string.IsNullOrEmpty(id)) return false; foreach (var r in C.characters) if (r != null && r.id == id) return true; return false; }
    public static void SetEnemy(string id)
    {
        if (string.IsNullOrEmpty(id)) { if (c != null && defEnemy.Length > 0) { c.enemy = defEnemy; nameCache.Clear(); } return; }   // no file load just to reset
        if (id == C.enemy) return;
        if (!Has(id)) { Debug.LogWarning("[Roster] SetEnemy('" + id + "'): no such roster row, enemy stays '" + C.enemy + "'"); return; }
        if (id == C.player) { Debug.LogWarning("[Roster] SetEnemy('" + id + "'): that is the player, enemy stays '" + C.enemy + "'"); return; }
        C.enemy = id; nameCache.Clear();   // ROSTERID v1: the enemy row wins ties in the legacy scan
    }
    public static string OtherOf(string id) { return id == PlayerId ? EnemyId : id == EnemyId ? PlayerId : ""; }

    // ROSTERID v1 (audit 'Roster identity'): a spawned clone carries an EXPLICIT roster id, set by ZoneRuntime at spawn (Roster.Register). Static tables only: no serialized field, no scene component.
    // Lookup order: registry on the transform, then on its root, then the legacy rule below (name contains objectName / id) for baked scene objects that were never registered.
    static readonly Dictionary<GameObject, string> idByRoot = new Dictionary<GameObject, string>();   // clone root -> creature id
    static readonly Dictionary<string, string> idByName = new Dictionary<string, string>(StringComparer.Ordinal);   // exact clone name 'Objectname#spawnid' -> creature id (serves the callers that only hold a name)
    static readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.Ordinal);   // legacy scan result per name (cleared on Reload / SetEnemy)
    static readonly HashSet<string> warned = new HashSet<string>();
    static int regLogged;

    // set at the spawn site, right after the clone is named. id = the creature id of the spawn row (cid).
    public static void Register(GameObject go, string id)
    {
        if (go == null || string.IsNullOrEmpty(id)) return;
        if (idByRoot.Count >= 256)   // drop destroyed clones
        {
            var live = new List<KeyValuePair<GameObject, string>>();
            foreach (var kv in idByRoot) if (kv.Key != null) live.Add(kv);
            idByRoot.Clear(); foreach (var kv in live) idByRoot[kv.Key] = kv.Value;
        }
        if (idByName.Count >= 4096) idByName.Clear();
        idByRoot[go] = id; idByName[go.name] = id;
        string legacy = LegacyIdOfName(go.name);
        if (legacy != id) Debug.LogWarning("[Roster] ID-OVERRIDE '" + go.name + "': explicit id '" + id + "' beats the name match '" + (legacy.Length > 0 ? legacy : "(none)") + "'");
        else if (regLogged < 64) { regLogged++; Debug.Log("[Roster] REGISTER '" + go.name + "' = '" + id + "' (name match agrees)"); }
    }
    // explicit id of this transform or its root; "" = none registered
    public static string RegisteredId(Transform t)
    {
        if (t == null || idByRoot.Count == 0) return "";
        string s; if (idByRoot.TryGetValue(t.gameObject, out s)) return s;
        var r = t.root; if (r != null && r.gameObject != t.gameObject && idByRoot.TryGetValue(r.gameObject, out s)) return s;
        return "";
    }
    // explicit id of a registered clone NAME (exact); "" = none
    public static string RegisteredIdOfName(string n) { string s; return !string.IsNullOrEmpty(n) && idByName.Count > 0 && idByName.TryGetValue(n, out s) ? s : ""; }

    // character id of a scene object: the registry first (own transform, then root), then matched on its own name, then its root's name, against each row's objectName (then id).
    // Same "name contains" rule the combat code used before the roster, for objects nobody registered. "" = not a roster character.
    public static string IdOf(Transform t)
    {
        if (t == null) return "";
        string rid = RegisteredId(t); if (rid.Length > 0) return rid;
        string id = IdOfName(t.name); if (id.Length > 0) return id;
        return IdOfName(t.root.name);
    }
    public static string IdOf(Component comp) { return comp == null ? "" : IdOf(comp.transform); }
    // HCAUDIT1 v104: bone names. The prefix is roster data (roster.json bonePrefix, "mixamorig:" for the 106-joint Mixamo rig); code names only the role ("RightHand").
    public static string BonePrefix(Component who) { var r = Get(IdOf(who)); return r.bonePrefix ?? ""; }
    public static string BoneName(Component who, string role) { return BonePrefix(who) + role; }
    public static string BoneRole(string prefix, string boneName) { prefix = prefix ?? ""; return boneName != null && boneName.StartsWith(prefix, StringComparison.Ordinal) ? boneName.Substring(prefix.Length) : ""; }   // "" = not a bone of this rig
    public static string IdOfName(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        string reg = RegisteredIdOfName(n); if (reg.Length > 0) return reg;   // ROSTERID v1: a spawned clone's exact name
        return LegacyIdOfName(n);
    }
    static string LegacyIdOfName(string n)
    {
        string hit; if (nameCache.TryGetValue(n, out hit)) return hit;
        hit = ScanName(n);
        if (nameCache.Count >= 4096) nameCache.Clear();
        nameCache[n] = hit; return hit;
    }
    // ROSTERID v1: when several rows match by substring the LONGEST objectName / id match wins, a tie goes to the exact match, then to the old order (enemy, player, roster order).
    // 'NpcReedSkirmisherB#x' therefore resolves to NpcReedSkirmisherB, not NpcReedSkirmisher. An ambiguity is logged once per pair of ids (first name seen).
    static string ScanName(string n)
    {
        RosterCharacter best = null; int bl = 0; bool bx = false; string other = null;
        Consider(n, Get(EnemyId), ref best, ref bl, ref bx, ref other);
        Consider(n, Get(PlayerId), ref best, ref bl, ref bx, ref other);
        foreach (var r in C.characters) Consider(n, r, ref best, ref bl, ref bx, ref other);
        if (best == null) return "";
        if (other != null && warned.Add(best.id + "|" + other))
            Debug.LogWarning("[Roster] AMBIGUOUS name '" + n + "': ids '" + best.id + "' and '" + other + "' both match by substring; using '" + best.id + "' (longest match). Spawned clones carry an explicit id; baked objects should be renamed.");
        return best.id;
    }
    static void Consider(string n, RosterCharacter r, ref RosterCharacter best, ref int bl, ref bool bx, ref string other)
    {
        bool x; int sc = Score(n, r, out x); if (sc == 0) return;
        if (best == null) { best = r; bl = sc; bx = x; return; }
        if (r.id == best.id) return;   // the enemy / player rows are also in characters[]
        if (sc > bl || (sc == bl && x && !bx)) { other = best.id; best = r; bl = sc; bx = x; }
        else if (other == null) other = r.id;
    }
    // length of the longest objectName / id of the row contained in n (0 = no match); exact = n equals that objectName or id
    static int Score(string n, RosterCharacter r, out bool exact)
    {
        exact = false; if (r == null || r.id.Length == 0) return 0;
        int sc = 0;
        if (r.objectName.Length > 0 && n.Contains(r.objectName)) { sc = r.objectName.Length; if (n.Length == sc) exact = true; }
        if (n.Contains(r.id)) { if (r.id.Length > sc) sc = r.id.Length; if (n.Length == r.id.Length) exact = true; }
        return sc;
    }

    public static void Reload()
    {
        Cfg k = null; Source = "(fallback)";
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "roster.json");
            if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/roster.json");
            if (File.Exists(p)) { k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p)); Source = p; }
        }
        catch (Exception e) { Debug.LogWarning("[Roster] load failed, fallback pair used: " + e.Message); k = null; }
        if (k == null) k = new Cfg();
        if (k.characters == null) k.characters = new RosterCharacter[0];
        if (string.IsNullOrEmpty(k.enemy)) k.enemy = !string.IsNullOrEmpty(k.fallbackEnemy) ? k.fallbackEnemy : (k.characters.Length > 0 ? k.characters[0].id : FallbackEnemy);   // PITDATA K1
        if (string.IsNullOrEmpty(k.enemy)) Debug.LogError("[Roster] no enemy: set roster.json enemy or fallbackEnemy");
        if (string.IsNullOrEmpty(k.player)) k.player = FirstNonEnemy(k);   // NODRAC v93: was FallbackPlayer = "Dracula"
        if (string.IsNullOrEmpty(k.enemy)) k.enemy = FallbackEnemy;
        foreach (var r in k.characters) Fill(r);
        defEnemy = k.enemy;   // LADDER v92
        c = k; nameCache.Clear();
    }

    // NODRAC v93: first roster row that is not the enemy (roster.json lists the player first; "" when the roster is empty)
    static string FirstNonEnemy(Cfg k) { if (k.characters != null) foreach (var r in k.characters) if (r != null && !string.IsNullOrEmpty(r.id) && r.id != k.enemy) return r.id; return ""; }
    static RosterCharacter Synth(string id) { var r = new RosterCharacter { id = id }; Fill(r); return r; }
    static void Fill(RosterCharacter r)
    {
        if (r == null) return;
        if (r.id == null) r.id = "";
        if (string.IsNullOrEmpty(r.displayName)) r.displayName = r.id;
        if (string.IsNullOrEmpty(r.objectName)) r.objectName = r.id;
        if (r.weapon == null) r.weapon = ""; if (r.controller == null) r.controller = ""; if (r.modelGlb == null) r.modelGlb = "";
        if (r.animGlb == null) r.animGlb = ""; if (r.guardMask == null) r.guardMask = ""; if (r.headRenderer == null) r.headRenderer = ""; if (r.finisherClipFolder == null) r.finisherClipFolder = ""; if (r.notes == null) r.notes = "";
    }
}
