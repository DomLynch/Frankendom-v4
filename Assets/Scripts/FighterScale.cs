using System.Collections.Generic;
using UnityEngine;

// HCKIT1 v93 (K2): the "anchors are metres x max(1, scale)" floor as DATA. Before: Mathf.Max(1f, scale) at 12 sites, i.e. "no fighter is smaller than scale 1" (true for the
// first player, Dracula 1.713 m; Joan is 1.58 m and a short enemy would be anchored at the wrong height). Now the floor is read from
//   roster.json  characters[].scaleFloor   (> 0 wins; 0 / missing = the default below)
//   camera_framing.json  scaleFloor        (the default for every character; missing = 1)
// Both ship as 1.0 = today's behaviour. Lower a character's floor (e.g. Joan 1.58 / 1.713 = 0.922) to anchor his blood / hit point / clash / HUD at his own height.
public static class FighterScale
{
    [System.Serializable] class Row { public float scaleFloor = 1f; }
    static float defFloor = 1f; static bool defLoaded;
    static readonly Dictionary<Component, float> byObject = new Dictionary<Component, float>();   // keyed by the component (no instance-id API: obsolete in Unity 6.6)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { defLoaded = false; byObject.Clear(); UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnScene; UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnScene; }
    static void OnScene(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) { byObject.Clear(); }   // a new scene = new fighter objects

    static void LoadDefault()
    {
        defLoaded = true; defFloor = 1f;
        foreach (var p in new[] { Application.streamingAssetsPath + "/camera_framing.json", Application.dataPath + "/Data/camera_framing.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Row>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (r != null && r.scaleFloor > 0f) defFloor = r.scaleFloor; } catch { }
            break;
        }
    }

    /// <summary>The default floor (camera_framing.json scaleFloor, 1 without the file).</summary>
    public static float DefaultFloor { get { if (!defLoaded) LoadDefault(); return defFloor; } }

    /// <summary>The floor for one character: its roster row scaleFloor when > 0, else DefaultFloor. Cached per scene object.</summary>
    public static float FloorOf(Component who)
    {
        if (who == null) return DefaultFloor;
        float f;
        if (byObject.TryGetValue(who, out f)) return f;
        var row = Roster.Get(Roster.IdOf(who));
        f = row != null && row.scaleFloor > 0f ? row.scaleFloor : DefaultFloor;
        byObject[who] = f; return f;
    }

    /// <summary>Effective scale of a fighter for metre anchors: Max(floor, scale).</summary>
    public static float Of(DuelFighter d) { return d == null ? DefaultFloor : Mathf.Max(FloorOf(d), d.scale); }
    /// <summary>Same for a raw scale value (lossyScale.y) of the fighter object `who`.</summary>
    public static float Eff(float scale, Component who) { return Mathf.Max(FloorOf(who), scale); }
}
