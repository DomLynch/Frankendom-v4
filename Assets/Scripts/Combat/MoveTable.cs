using System.Collections.Generic;
using UnityEngine;

public class MoveTable : ScriptableObject
{
    public List<MoveDef> moves = new List<MoveDef>();
    Dictionary<string, MoveDef> map;
    public MoveDef Get(string id)
    {
        if (map == null) { map = new Dictionary<string, MoveDef>(); foreach (var m in moves) map[m.id] = m; }
        return map.TryGetValue(id, out var r) ? r : null;
    }
}


