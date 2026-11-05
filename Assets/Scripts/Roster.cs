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

    const string FallbackPlayer = "Dracula", FallbackEnemy = "Pitborn";   // the ONLY character literals in code

    static Cfg c;
    public static string Source = "(none)";
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
        if (string.IsNullOrEmpty(k.player)) k.player = FallbackPlayer;
        if (string.IsNullOrEmpty(k.enemy)) k.enemy = FallbackEnemy;
        foreach (var r in k.characters) Fill(r);
        c = k;
    }

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
