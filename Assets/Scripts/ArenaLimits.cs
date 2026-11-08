using UnityEngine;

// Keeps both fighters inside the arena lane (Strategy audit: fighters drifted to / past the gate threshold and the camera ended up inside the gate geometry).
// Data: Assets/Data/arena_limits.json (StreamingAssets in the player) { enabled, xMin, xMax, zMin, zMax }. Runs after DuelFighter (LateUpdate, order 600) and moves the
// CharacterController back inside the rectangle. Auto-attaches to every DuelFighter at scene load. No effect when the file is missing or enabled=false.
[DefaultExecutionOrder(600)]
public class ArenaLimits : MonoBehaviour
{
    [System.Serializable] class Row { public bool enabled = true; public float xMin = -3.8f, xMax = 3.8f, zMin = 8f, zMax = 22f; public bool stairEnabled = true; public float stairHalfWidthX = 2.45f, stairZMin = 4f, stairZMax = 22.5f; /*HCKIT1 v93*/ public float fallGravity = 20f, groundedMinY = -0.5f, fallDropM = 2f, restoreUpM = 0.05f, restoreWindowS = 6f; public int maxRestores = 0; /*ROBUST v1: fall recovery, defaults = the old literals, maxRestores 0 = off*/ }
    static Row row; static bool loaded; static readonly Row stairDefaults = new Row();   /*HCKIT1 v93 K1: the stair lane DuelFighter.Tick clamps to (arena_limits.json stair*); missing file = the old literals 2.45 / 4 / 22.5 */
    static Row Stair { get { return Load() ?? stairDefaults; } }
    public static float StairHalfWidthX { get { var r = Stair; return r.stairEnabled ? r.stairHalfWidthX : float.MaxValue; } }   // stairEnabled=false = no stair clamp (a new arena)
    public static float StairZMin { get { return Stair.stairZMin; } }
    public static float FallGravity { get { return Stair.fallGravity; } }   // ROBUST v1 (arena_limits.json)
    public static float GroundedMinY { get { return Stair.groundedMinY; } }
    public static float FallDropM { get { return Stair.fallDropM; } }
    public static float RestoreUpM { get { return Stair.restoreUpM; } }
    public static float RestoreWindowS { get { return Stair.restoreWindowS; } }
    public static int MaxRestores { get { return Stair.maxRestores; } }
    public static float ZoneZMax = float.NaN;   // ZONES v1: a zone with a gate sets a longer lane; NaN = off = the old arithmetic exactly
    public static float StairZMax { get { return float.IsNaN(ZoneZMax) ? Stair.stairZMax : Mathf.Max(Stair.stairZMax, ZoneZMax); } }
    CharacterController cc;

    static Row Load()
    {
        if (loaded) return row; loaded = true;
        foreach (var p in new[] { Application.streamingAssetsPath + "/arena_limits.json", Application.dataPath + "/Data/arena_limits.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { row = JsonUtility.FromJson<Row>(System.IO.File.ReadAllText(p)); } catch { }
            break;
        }
        return row;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var r = Load(); if (r == null || !r.enabled) return;
        foreach (var f in Object.FindObjectsByType<CharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if ((f.name == Roster.PlayerObject || f.name == Roster.EnemyObject) && f.GetComponent<ArenaLimits>() == null) { f.gameObject.AddComponent<ArenaLimits>(); Debug.Log("[ArenaLimits] active on " + f.name + " x " + r.xMin + ".." + r.xMax + " z " + r.zMin + ".." + r.zMax); }
    }

    // KNOCKBACK v1: metres the victim can slide from p along dir (<= want) before the arena rectangle, the per-zone play area or the stair width (the same limits Clamp and DuelFighter.MoveTick apply afterwards)
    public static float RoomAlong(Vector3 p, Vector3 dir, float want)
    {
        var r = Load(); if (r == null || !r.enabled) return want;
        float t = want; var pa = PlayArea.Row(p.z);
        if (pa == null) { if (dir.x > 0.0001f) t = Mathf.Min(t, (r.xMax - p.x) / dir.x); else if (dir.x < -0.0001f) t = Mathf.Min(t, (r.xMin - p.x) / dir.x); }   // hc:ok epsilon: axis-parallel guard
        float zMax = float.IsNaN(ZoneZMax) ? r.zMax : ZoneZMax;
        if (dir.z > 0.0001f) t = Mathf.Min(t, (zMax - p.z) / dir.z); else if (dir.z < -0.0001f) t = Mathf.Min(t, (r.zMin - p.z) / dir.z);
        if (pa == null && p.z > StairZMin && p.z < StairZMax && StairHalfWidthX < float.MaxValue && Mathf.Abs(dir.x) > 0.0001f)
        {
            float lim = StairHalfWidthX; t = Mathf.Min(t, ((dir.x > 0f ? lim : -lim) - p.x) / dir.x);
        }
        if (pa != null) { float x = p.x + dir.x * t, z = p.z + dir.z * t; PlayArea.Project(pa, ref x, ref z); t = Mathf.Min(t, Vector3.Dot(new Vector3(x - p.x, 0f, z - p.z), dir)); }
        return Mathf.Max(0f, t);
    }

    void Awake() { cc = GetComponent<CharacterController>(); }

    void LateUpdate() { if (!Duel.BrainsOnTick) Clamp(); }   // FPSSIM v1: with brainsOnTick DuelFighter.MoveTick clamps once per sim tick
    public static void ClampTick(Component c) { var a = c.GetComponent<ArenaLimits>(); if (a != null) a.Clamp(); }
    public void Clamp()
    {
        var r = row; if (r == null || !r.enabled || cc == null || !cc.enabled) return;
        var p = transform.position;
        float x = Mathf.Clamp(p.x, r.xMin, r.xMax), z = Mathf.Clamp(p.z, r.zMin, float.IsNaN(ZoneZMax) ? r.zMax : ZoneZMax);   // ZONES v1
        var pa = PlayArea.Row(p.z); if (pa != null) { x = p.x; PlayArea.Project(pa, ref x, ref z); }   // PLAYAREA v1: per-zone play area (data, OFF = null)
        if (x != p.x || z != p.z) cc.Move(new Vector3(x - p.x, 0f, z - p.z));
    }
}
