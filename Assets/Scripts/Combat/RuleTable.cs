using System.Collections.Generic;
using UnityEngine;

public class RuleTable : ScriptableObject
{
    public List<string> keys = new List<string>();
    public List<float> values = new List<float>();
    Dictionary<string, float> map;
    public float N(string key, float def = 0f)
    {
        if (map == null) { map = new Dictionary<string, float>(); for (int i = 0; i < keys.Count; i++) map[keys[i]] = values[i]; }
        return map.TryGetValue(key, out var v) ? v : def;
    }
}

