using System.Collections.Generic;
using System.IO;

// Editor-side view of the character roster (Assets/Data/roster.json, read by Roster with System.IO + JsonUtility).
// Editor tools that build per-character assets (controllers, guard masks, clips, imports) iterate these rows instead of naming characters.
public static class RosterEditor
{
    // every roster row (re-read from disk, so roster.json edits apply without a domain reload); the player and enemy when the file has no rows
    public static List<RosterCharacter> Characters()
    {
        Roster.Reload();
        var list = new List<RosterCharacter>();
        foreach (var r in Roster.All) if (r != null && r.id.Length > 0) list.Add(r);
        if (list.Count == 0) { list.Add(Roster.Player); if (Roster.EnemyId != Roster.PlayerId) list.Add(Roster.Enemy); }
        return list;
    }

    // scene object names of the two fighters in the duel
    public static string[] FighterObjects() { Roster.Reload(); return new[] { Roster.PlayerObject, Roster.EnemyObject }; }

    // the character's asset folder: the folder of its combat model (roster modelGlb), else Assets/Characters/<id>
    public static string Folder(RosterCharacter r)
    {
        if (!string.IsNullOrEmpty(r.modelGlb)) return Path.GetDirectoryName(r.modelGlb).Replace('\\', '/');
        return "Assets/Characters/" + r.id;
    }
}
