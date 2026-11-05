using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

public class RuleTable : ScriptableObject
{
    public List<string> keys = new List<string>();
    public List<float> values = new List<float>();
    Dictionary<string, float> map;
    public float N(string key, float def = 0f)
    {
        if (map == null) { map = new Dictionary<string, float>(); for (int i = 0; i < keys.Count; i++) map[keys[i]] = values[i]; foreach (var kv in Overlay()) map[kv.Key] = kv.Value; }   // HCKIT6 v100 (HC3 K6): rules-defs.json wins over the baked asset
        return map.TryGetValue(key, out var v) ? v : def;
    }

    // HCKIT6 v100 (HC3 K6): rules-defs.json (Data == StreamingAssets, DataPath) is the one place to edit; the asset values are the fallback for ids the json does not list.
    // Row forms as in the file: {"id":..,"n":x} | {"id":..,"piDiv":d} (= pi / d) | {"id":..,"b":true|false} (= 1 / 0). tools/data_gates.py gate D proves asset == json (0 drift), so the overlay changes no value today.
    static Dictionary<string, float> overlay;
    static readonly Regex RowRe = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled), IdRe = new Regex(@"""id""\s*:\s*""([^""]+)""", RegexOptions.Compiled), NumRe = new Regex(@"""(n|piDiv)""\s*:\s*(-?[0-9.eE+-]+)", RegexOptions.Compiled), BoolRe = new Regex(@"""b""\s*:\s*(true|false)", RegexOptions.Compiled);
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { overlay = null; }
    static Dictionary<string, float> Overlay()
    {
        if (overlay != null) return overlay;
        overlay = new Dictionary<string, float>();
        try
        {
            string json = DataPath.ReadText("rules-defs.json"); if (string.IsNullOrEmpty(json)) return overlay;
            foreach (Match m in RowRe.Matches(json))
            {
                var id = IdRe.Match(m.Value); if (!id.Success) continue;
                var nm = NumRe.Match(m.Value); var bm = BoolRe.Match(m.Value);
                if (nm.Success) { float x = float.Parse(nm.Groups[2].Value, CultureInfo.InvariantCulture); overlay[id.Groups[1].Value] = nm.Groups[1].Value == "piDiv" ? Mathf.PI / x : x; }
                else if (bm.Success) overlay[id.Groups[1].Value] = bm.Groups[1].Value == "true" ? 1f : 0f;
            }
        }
        catch (System.Exception ex) { Debug.LogWarning("RuleTable: rules-defs.json overlay unreadable (" + ex.Message + "), asset values"); }
        return overlay;
    }
}

