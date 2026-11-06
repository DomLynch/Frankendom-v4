using System;
using System.IO;
using UnityEngine;

// Character roster (lane ROSTER). Who is the player and who is the enemy is DATA: Assets/Data/roster.json (copy in StreamingAssets).
// Every per-character JSON row elsewhere is keyed by the character id. Code never names a character: it asks Roster.
// A missing / broken file falls back to the built-in pair below; those two literals are the only character names allowed in code.
[Serializable] public class RosterCharacter
{
    public string id = "";
    public string displayName = "";         // HUD / log name
    public string objectName = "";          // scene GameObject name (root of the fighter)
    public string weapon = "";              // "sword" | "maul" ... (Assets/Characters/Weapons/<weapon>_start)
    public string controller = "";          // AnimatorController asset path
    public string modelGlb = "";            // combat model asset path
    public string animGlb = "";             // animation source glb asset path
    public string guardMask = "";           // upper-body guard AvatarMask asset path
    public string finisherClipFolder = "";  // baked finisher clips folder
    public float heightM = 0f;              // 0 = keep the scene / GameConfig size
    public bool armoured = false;           // metal: blood.json default sparks / flash when no victims[] row matches
    public string headRenderer = "";        // WOUNDSHOT v84: exact name of the head mesh object (VerifyGround HEAD check, wound close-ups); "" = verify_ground.json head.rendererName
    public float scaleFloor = 0f;           /*HCKIT1 v93 K2: floor for metre anchors, Max(scaleFloor, DuelFighter.scale); 0 = camera_framing.json scaleFloor (1) */
    public string notes = "";
}

public static class Roster
{
    [Serializable] public class Cfg { public string note = ""; public string player = ""; public string enemy = ""; public RosterCharacter[] characters = new RosterCharacter[0]; }

    const string FallbackEnemy = "Pitborn";   // the ONLY character literal in code /*NODRAC v93*/ FallbackPlayer literal removed: the fallback player is the first roster row that is not the enemy (FirstNonEnemy)

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
        if (string.IsNullOrEmpty(id)) { if (c != null && defEnemy.Length > 0) c.enemy = defEnemy; return; }   // no file load just to reset
        if (id == C.enemy) return;
        if (!Has(id)) { Debug.LogWarning("[Roster] SetEnemy('" + id + "'): no such roster row, enemy stays '" + C.enemy + "'"); return; }
        if (id == C.player) { Debug.LogWarning("[Roster] SetEnemy('" + id + "'): that is the player, enemy stays '" + C.enemy + "'"); return; }
        C.enemy = id;
    }
    public static string OtherOf(string id) { return id == PlayerId ? EnemyId : id == EnemyId ? PlayerId : ""; }

    // character id of a scene object: matched on its own name, then its root's name, against each row's objectName (then id).
    // Same "name contains" rule the combat code used before the roster. "" = not a roster character.
    public static string IdOf(Transform t)
    {
        if (t == null) return "";
        string id = IdOfName(t.name); if (id.Length > 0) return id;
        return IdOfName(t.root.name);
    }
    public static string IdOf(Component comp) { return comp == null ? "" : IdOf(comp.transform); }
    public static string IdOfName(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        // enemy first: keeps the pre-roster check order (enemy name tested before player name)
        foreach (var id in new[] { EnemyId, PlayerId }) { var r = Get(id); if (Match(n, r)) return r.id; }
        foreach (var r in C.characters) if (r != null && Match(n, r)) return r.id;
        return "";
    }
    static bool Match(string n, RosterCharacter r)
    {
        if (r == null || r.id.Length == 0) return false;
        return (r.objectName.Length > 0 && n.Contains(r.objectName)) || n.Contains(r.id);
    }

    public static void Reload()
    {
        Cfg k = null; Source = "(fallback)";
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "roster.json");
            if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/roster.json");
            if (File.Exists(p)) { k = JsonUtility.FromJson<Cfg>(File.ReadAllText(p)); Source = p; }
        }
        catch (Exception e) { Debug.LogWarning("[Roster] load failed, fallback pair used: " + e.Message); k = null; }
        if (k == null) k = new Cfg();
        if (k.characters == null) k.characters = new RosterCharacter[0];
        if (string.IsNullOrEmpty(k.enemy)) k.enemy = FallbackEnemy;
        if (string.IsNullOrEmpty(k.player)) k.player = FirstNonEnemy(k);   // NODRAC v93: was FallbackPlayer = "Dracula"
        if (string.IsNullOrEmpty(k.enemy)) k.enemy = FallbackEnemy;
        foreach (var r in k.characters) Fill(r);
        defEnemy = k.enemy;   // LADDER v92
        c = k;
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
