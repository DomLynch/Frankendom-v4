using System.Collections.Generic;
using UnityEngine;

public static class TestFog
{
    public static string Check()
    {
        var list = new List<string>();
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t.name.StartsWith("GroundFog") || t.name.StartsWith("WaterFog"))
            {
                list.Add($"{t.name}(active={t.gameObject.activeSelf})");
            }
        }
        return string.Join(", ", list);
    }
}
