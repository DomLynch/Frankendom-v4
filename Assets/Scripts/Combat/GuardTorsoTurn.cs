using System.Collections.Generic;
using UnityEngine;

// Directional guard torso turn (laneGUARD2 v82, NEW). While a fighter holds a side guard (DuelFighter.guardSide 1 left / 2 right, state Guard) the spine chain turns toward the guarded side
// by turnDeg in total (Frankendom V3 GUARD_TILT 0.45 rad = 25.8 deg, was about 10 deg: the guard_left / guard_right clips carry bakedDeg of it). Applied in LateUpdate, after the Animator
// and before HeadClamp (order 40 < 50, so the head limit is measured against the already turned torso), as a yaw about the fighter's up axis split over the spine bones by share:
// the code adds (turnDeg - bakedDeg), eased in over blendInSeconds and out over blendOutSeconds. Both numbers and every other value are data: Assets/Data/guard_turn.json
// (StreamingAssets in the player). No file / enabled false / no row for the fighter's roster id = no extra turn (the old look). sides[].dir flips the direction per side (left -1, right +1:
// a positive angle about up turns the body to its right) if the MoveSheet shows the lean the wrong way. Auto-attaches at scene load to every DuelFighter that has an enabled row (like HeadClamp).
[DefaultExecutionOrder(40)]
public class GuardTorsoTurn : MonoBehaviour
{
    [System.Serializable] public class BoneShare { public string bone = ""; public float share = 0f; }
    [System.Serializable] public class Side { public int id; public string name = ""; public float dir = 0f; }
    [System.Serializable] public class Row { public string id = ""; public bool enabled = true; public float turnDeg = -1f, bakedDeg = -1f; }   // -1 = the file's value
    [System.Serializable] public class Root
    {
        public bool enabled = false, debug = false;
        public float turnDeg = 26f, bakedDeg = 10f, blendInSeconds = 0.12f, blendOutSeconds = 0.15f;
        public BoneShare[] spine; public Side[] sides; public Row[] characters;
    }

    public static bool ForceOff;   // harness: -noguardturn
    public float Current => cur;   // degrees of extra turn applied right now (signed, + = to the fighter's right)

    static Root data;
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in new[] { Application.streamingAssetsPath + "/guard_turn.json", Application.dataPath + "/Data/guard_turn.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(DataPath.ReadFile(p)); if (r != null) data = r; } catch { }
            break;
        }
        return data;
    }
    static Row RowFor(string id)
    {
        var D = Load(); if (D.characters == null || string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < D.characters.Length; i++) if (D.characters[i] != null && D.characters[i].id == id) return D.characters[i];
        return null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { Boot(null); }   // ZONES-ATTACH v1 (INT107)
    public static void AttachTo(GameObject root) { Boot(root); }   // same want-check, one object (ZoneRuntime.SpawnOne for a prefab creature)
    static void Boot(GameObject root){
        foreach (var arg in System.Environment.GetCommandLineArgs()) if (arg == "-noguardturn") ForceOff = true;
        var D = Load(); if (!D.enabled || D.characters == null || D.spine == null) return;
        foreach (var df in (root != null ? root.GetComponentsInChildren<DuelFighter>(true) : Object.FindObjectsByType<DuelFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None)))
        {
            var r = RowFor(Roster.IdOf(df.transform));
            if (r != null && r.enabled && df.GetComponent<GuardTorsoTurn>() == null) df.gameObject.AddComponent<GuardTorsoTurn>();
        }
    }

    DuelFighter me; Row row; bool init;
    Transform[] bones; float[] shares; Quaternion[] pre, post; bool[] wrote; float cur; int logN;

    void Init()
    {
        init = true; me = GetComponent<DuelFighter>(); row = RowFor(Roster.IdOf(transform)); var D = Load();
        if (me == null || row == null || !row.enabled || D.spine == null || D.spine.Length == 0) return;
        int n = D.spine.Length; bones = new Transform[n]; shares = new float[n]; pre = new Quaternion[n]; post = new Quaternion[n]; wrote = new bool[n];
        int found = 0;
        foreach (var t in GetComponentsInChildren<Transform>(true))
            for (int i = 0; i < n; i++) if (bones[i] == null && D.spine[i] != null && t.name == D.spine[i].bone) { bones[i] = t; shares[i] = D.spine[i].share; found++; }
        if (found == 0) bones = null;   // no spine bone with these names: do nothing
        if (D.debug) Debug.Log("[GuardTorsoTurn] " + name + " spine bones found " + found + " of " + n);
    }

    static float DirOf(Root D, int side)
    {
        if (D.sides == null) return 0f;
        for (int i = 0; i < D.sides.Length; i++) if (D.sides[i] != null && D.sides[i].id == side) return D.sides[i].dir;
        return 0f;
    }

    void LateUpdate()
    {
        if (!init) Init();
        if (bones == null) return;
        // undo our own write of the last frame when the Animator did not rewrite the bone (Animator off / frozen): no compounding turn
        for (int i = 0; i < bones.Length; i++)
            if (wrote[i] && bones[i] != null && Quaternion.Angle(bones[i].localRotation, post[i]) < 0.001f) bones[i].localRotation = pre[i];
        for (int i = 0; i < wrote.Length; i++) wrote[i] = false;

        var D = Load();
        float turn = row.turnDeg >= 0f ? row.turnDeg : D.turnDeg, baked = row.bakedDeg >= 0f ? row.bakedDeg : D.bakedDeg;
        float extra = Mathf.Max(0f, turn - baked);
        float target = (!ForceOff && D.enabled && me.state == FState.Guard) ? DirOf(D, me.guardSide) * extra : 0f;
        float secs = Mathf.Abs(target) > Mathf.Abs(cur) ? D.blendInSeconds : D.blendOutSeconds;
        float step = secs > 0.0001f ? extra * Time.deltaTime / secs : 1000f;
        cur = Mathf.MoveTowards(cur, target, step);
        if (Mathf.Abs(cur) < 0.001f) return;

        Vector3 up = transform.up;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i]; if (b == null) continue;
            pre[i] = b.localRotation;
            b.rotation = Quaternion.AngleAxis(cur * shares[i], up) * b.rotation;
            post[i] = b.localRotation; wrote[i] = true;
        }
        if (D.debug && (++logN % 60) == 0) Debug.Log("[GuardTorsoTurn] " + name + " side " + me.guardSide + " extra turn " + cur.ToString("F1") + " deg (target " + target.ToString("F1") + ")");
    }
}
