using System;
using UnityEngine;

// Port of Frankendom V3 src/fight/tactic-rows.ts: a profile may name a tactic row whose weights draw the answer to a noticed swing.
// Rows live in ai_rows.json ("tactics"). A profile without `tactic` never reaches this table (no draw, no branch).
[Serializable] public class AiWeighted { public string choice; public float weight; }
[Serializable] public class AiTacticRow { public string id; public AiWeighted[] heroWinding = new AiWeighted[0]; public bool holdBand; }
[Serializable] public class AiTacticLimits { public int maxEntries = 6; }

public static class AiTactic
{
    // Vocabulary of the reaction slot (V3): parry; brace = the guard that meets it; backstep = the step out.
    static readonly string[] Reactions = new string[] { "parry", "brace", "backstep" };

    // At most maxEntries entries, a known choice each, positive weights (V3 checkTactic). Logs instead of throwing.
    public static void Validate(AiRowsFile f)
    {
        if (f == null || f.tactics == null) return;
        foreach (var row in f.tactics)
        {
            if (row == null || row.heroWinding == null || row.heroWinding.Length == 0) continue;
            if (row.heroWinding.Length > f.tacticLimits.maxEntries) Debug.LogWarning("[AI] tactic " + row.id + ": heroWinding has more than " + f.tacticLimits.maxEntries + " entries");
            foreach (var e in row.heroWinding)
                if (e == null || Array.IndexOf(Reactions, e.choice) < 0 || !(e.weight > 0f)) Debug.LogWarning("[AI] tactic " + row.id + ": entry is not a known reaction with a positive weight");
        }
    }

    // The weighted reaction list for a tactic id, or null (no tactic / empty list).
    public static AiWeighted[] HeroWinding(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var rows = AiRows.I.tactics;
        if (rows == null) return null;
        for (int i = 0; i < rows.Length; i++)
            if (rows[i] != null && rows[i].id == id) return rows[i].heroWinding != null && rows[i].heroWinding.Length > 0 ? rows[i].heroWinding : null;
        return null;
    }

    // One draw over the weights in row order; u is the caller's own roll in [0,1).
    public static string Pick(AiWeighted[] list, float u)
    {
        float total = 0f;
        for (int i = 0; i < list.Length; i++) total += list[i].weight;
        float at = u * total;
        for (int i = 0; i < list.Length; i++)
        {
            if (at < list[i].weight) return list[i].choice;
            at -= list[i].weight;
        }
        return list[list.Length - 1].choice;
    }
}
