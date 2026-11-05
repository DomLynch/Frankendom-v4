using System;
using System.Collections.Generic;
using UnityEngine;

// Ported from Frankendom V3 data/difficulty.json: a thin override layer of nine brain knobs + health/damage scales, ladder 1-10 (10 = V1 level-18 enemy untouched).
[Serializable]
public class DifficultyRow
{
    public int id;
    public float reaction, accuracy, parry, dodge, aggression, pressure, discipline, lapse, read, foeHealthScale, foeDamageScale;
}

public class DifficultyTable : ScriptableObject
{
    public List<DifficultyRow> rows = new List<DifficultyRow>();
    public int defaultLevel = 7;
    public DifficultyRow Get(int level)
    {
        foreach (var r in rows) if (r.id == level) return r;
        return rows.Count > 0 ? rows[rows.Count - 1] : null;
    }
}
