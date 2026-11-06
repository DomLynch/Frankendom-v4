using System;

// LADDER v92 (laneLADDER). The one decision a ladder row makes before a fight: WHICH roster enemy and WHICH scene. Pure strings, no UnityEngine type, so it can be compiled and tested outside Unity
// (_agents/laneLADDER/test). GameLoop.Reload() feeds it the current opponents.json row; with no `character` / `scene` columns, no `baseScene` and no `scenePattern` the answer is always
// "enemy = roster.json default, reload the active scene", which is exactly what Reload() did before this file existed.
public static class LadderResolve
{
    public struct Plan
    {
        public bool setEnemy;        // false = leave Roster alone (ladder off / harness)
        public string enemy;         // "" = the roster.json default enemy (Roster.SetEnemy("") resets the override)
        public string scene;         // "" = reload the active scene by build index (the old behaviour); else a scene name for SceneManager.LoadScene
        public string why;           // log text
    }

    /// <summary>Scene name without folder and extension ("Assets/Scenes/HadesSlice_Foo.unity" -> "HadesSlice_Foo").</summary>
    public static string SceneName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\\', '/'); int i = s.LastIndexOf('/'); if (i >= 0) s = s.Substring(i + 1);
        if (s.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 6);
        return s;
    }

    /// <param name="ladderOn">GameLoop.LadderOn (opponents.json enabled and no proof harness)</param>
    /// <param name="character">row.character ("" = roster default)</param>
    /// <param name="rowScene">row.scene ("" = by convention, see below)</param>
    /// <param name="defaultEnemy">roster.json "enemy" (what Roster had before any override)</param>
    /// <param name="baseScene">opponents.json baseScene, else the scene the run started in ("" = unknown)</param>
    /// <param name="activeScene">the scene that is loaded now</param>
    /// <param name="scenePattern">opponents.json scenePattern with {id} ("" = off)</param>
    public static Plan Resolve(bool ladderOn, string character, string rowScene, string defaultEnemy, string baseScene, string activeScene, string scenePattern)
    {
        var p = new Plan { setEnemy = false, enemy = "", scene = "", why = "ladder off: active scene, roster untouched" };
        if (!ladderOn) return p;
        character = character ?? ""; rowScene = rowScene ?? ""; defaultEnemy = defaultEnemy ?? ""; baseScene = baseScene ?? ""; activeScene = activeScene ?? ""; scenePattern = scenePattern ?? "";
        p.setEnemy = true; p.enemy = character;
        string target;
        if (rowScene.Length > 0) { target = SceneName(rowScene); p.why = "row.scene"; }
        else if (character.Length > 0 && character != defaultEnemy && scenePattern.Length > 0) { target = SceneName(scenePattern.Replace("{id}", character)); p.why = "scenePattern"; }
        else { target = SceneName(baseScene); p.why = target.Length > 0 ? "base scene" : "active scene"; }
        // the active scene is reloaded by build index (identical to the pre-ladder-columns code): same name or unknown = ""
        if (target.Length == 0 || string.Equals(target, SceneName(activeScene), StringComparison.Ordinal)) { p.scene = ""; if (target.Length > 0) p.why += " (already active)"; }
        else p.scene = target;
        return p;
    }

    /// <summary>Scene list for a player build: the base scene first, then each enemy scene once. No enemy scenes = exactly { baseScene } (the pre-ladder literal).</summary>
    public static string[] SceneList(string baseScene, string[] enemyScenes)
    {
        var l = new System.Collections.Generic.List<string> { baseScene };
        if (enemyScenes != null) foreach (var s in enemyScenes) if (!string.IsNullOrEmpty(s) && !l.Contains(s)) l.Add(s);
        return l.ToArray();
    }

    /// <summary>First load of a run: can the scene that is already loaded host the start row? True when the row names no other scene and no other character, or names exactly this scene.</summary>
    public static bool StartSceneFits(bool ladderOn, string character, string rowScene, string defaultEnemy, string activeScene, string scenePattern)
    {
        if (!ladderOn) return true;
        var p = Resolve(true, character, rowScene, defaultEnemy, "", activeScene, scenePattern);
        return p.scene.Length == 0;
    }
}
