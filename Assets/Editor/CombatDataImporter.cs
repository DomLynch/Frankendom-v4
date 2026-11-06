using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Imports Frankendom V3 move-defs.json + rules-defs.json into ScriptableObjects (Assets/Data).
public static class CombatDataImporter
{
    static string Src { get { return ProjectPaths.Get("handoverDir") + "/extracted/7-repos/frankendom-v3/src/fight/"; } } /* HCKIT5 v95 K9: path from Tools/paths.json (default = the old literal) */

    [Serializable] class Chained { public int windup, active, recovery; }
    [Serializable] class Chain { public int window; public string[] follow; }
    [Serializable] class VsGuard { public float stagger, staminaDamage; }
    [Serializable] class MoveJ
    {
        public string id, direction, path;
        public int windup, active, recovery;
        public Chained chained; public Chain chain; public VsGuard vsGuard;
        public float damage, stamina, staminaDamage, stagger, poise, poiseFrom, chip, knockback, stepIn, reach, posture, chamber;
        public bool breaksGuard, parryable, charges;
    }
    [Serializable] class MovesJ { public MoveJ[] moves; }
    [Serializable] class RuleJ { public string id; public float n, piDiv; public bool b; }
    [Serializable] class RulesJ { public RuleJ[] rules; }

    [Serializable] class DiffJ { public DifficultyRow[] rows; }

    public static void ImportDifficulty()
    {
        Directory.CreateDirectory("Assets/Data");
        if (!ProjectPaths.Flag("importV3")) { Debug.LogWarning("CombatDataImporter: one-shot V3 import skipped (it overwrites the tuned difficulty / move / rule data and re-creates the .asset files): pass -importV3 to run it"); return; } /* HCKIT5 v95 K9 guard */ File.Copy(ProjectPaths.Get("handoverDir") + "/extracted/7-repos/frankendom-v3/data/difficulty.json", "Assets/Data/difficulty.json", true);
        var dj = JsonUtility.FromJson<DiffJ>(File.ReadAllText("Assets/Data/difficulty.json"));
        var t = ScriptableObject.CreateInstance<DifficultyTable>();
        t.rows.AddRange(dj.rows);
        AssetDatabase.DeleteAsset("Assets/Data/Difficulty.asset");
        AssetDatabase.CreateAsset(t, "Assets/Data/Difficulty.asset");
        if (AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Data/GameConfig.asset") == null)
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<GameConfig>(), "Assets/Data/GameConfig.asset");
        AssetDatabase.SaveAssets();
        Debug.Log("DIFFICULTY rows=" + t.rows.Count);
    }

    public static void Import()
    {
        ImportDifficulty();
        Directory.CreateDirectory("Assets/Data");
        if (!ProjectPaths.Flag("importV3")) return; /* HCKIT5 v95 K9 guard */
        SafeIO.Copy(Src + "move-defs.json", "Assets/Data/move-defs.json", true); /* LOCKFIX v103 */
        SafeIO.Copy(Src + "rules-defs.json", "Assets/Data/rules-defs.json", true); /* LOCKFIX v103 */

        // chamber/vsGuard may be null in the JSON; JsonUtility leaves defaults for those
        var mj = JsonUtility.FromJson<MovesJ>(File.ReadAllText("Assets/Data/move-defs.json"));
        var mt = ScriptableObject.CreateInstance<MoveTable>();
        foreach (var m in mj.moves)
        {
            var d = new MoveDef
            {
                id = m.id, direction = m.direction, path = m.path, windup = m.windup, active = m.active, recovery = m.recovery,
                damage = m.damage, stamina = m.stamina, staminaDamage = m.staminaDamage, stagger = m.stagger, poise = m.poise, poiseFrom = m.poiseFrom,
                chip = m.chip, knockback = m.knockback, stepIn = m.stepIn, reach = m.reach, posture = m.posture, chamber = m.chamber,
                breaksGuard = m.breaksGuard, parryable = m.parryable, charges = m.charges
            };
            if (m.chained != null && m.chained.windup > 0) { d.hasChained = true; d.chainedWindup = m.chained.windup; d.chainedActive = m.chained.active; d.chainedRecovery = m.chained.recovery; }
            if (m.chain != null && m.chain.follow != null) { d.chainWindow = m.chain.window; d.chainFollow = m.chain.follow; }
            if (m.vsGuard != null && m.vsGuard.stagger > 0) { d.hasVsGuard = true; d.vsGuardStagger = m.vsGuard.stagger; d.vsGuardStaminaDamage = m.vsGuard.staminaDamage; }
            mt.moves.Add(d);
        }
        AssetDatabase.DeleteAsset("Assets/Data/Moves.asset");
        AssetDatabase.CreateAsset(mt, "Assets/Data/Moves.asset");

        var rj = JsonUtility.FromJson<RulesJ>(File.ReadAllText("Assets/Data/rules-defs.json"));
        var rt = ScriptableObject.CreateInstance<RuleTable>();
        foreach (var r in rj.rules)
        {
            rt.keys.Add(r.id);
            rt.values.Add(r.piDiv > 0 ? Mathf.PI / r.piDiv : r.b ? 1f : r.n);
        }
        AssetDatabase.DeleteAsset("Assets/Data/Rules.asset");
        AssetDatabase.CreateAsset(rt, "Assets/Data/Rules.asset");
        AssetDatabase.SaveAssets();
        Debug.Log("COMBAT DATA moves=" + mt.moves.Count + " rules=" + rt.keys.Count);
    }
}
