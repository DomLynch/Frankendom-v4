using UnityEngine;

// Per-foot yaw correction (toe-out / "bandy legs") + upper-arm tilt on a Mixamo-style rig, applied in LateUpdate on top of the animator pose.
// Ported from OpenTrident clip-panel.js (fitClip): the FOOT is yawed about the shin axis (Leg->Foot offset), knees and hips untouched;
// the upper arms are tilted about the parent (shoulder) X axis. All numbers are data: Assets/Data/foot_yaw.json (StreamingAssets in the player).
// Put this on the character model root (the object that has the Animator, or a parent of it).
public class FootYaw : MonoBehaviour
{
    [System.Serializable] public class Category { public string id; public string[] states; public float[] footYaw; public float armWeight = 1f; }
    [System.Serializable] public class Row
    {
        public string name; public bool enabled; public bool debug; public float debugEvery = 30f; public float handedSign = -1f;
        public float[] footYaw = { 0f, 0f }; public float[] armTilt = { 0f, 0f }; public float[] armAxis = { 1f, 0f, 0f };
        public float armWeight = 1f; public Category[] categories;
    }
    [System.Serializable] public class Root { public Row defaults; public Row[] characters; }

    public Animator anim;            // auto-found when empty
    public bool debugLog;

    // attach to every Animator whose character root has an enabled row in foot_yaw.json (data-driven, no scene edit)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { Boot(null); }   // ZONES-ATTACH v1 (INT107)
    public static void AttachTo(GameObject root) { Boot(root); }   // same want-check, one object (ZoneRuntime.SpawnOne for a prefab creature)
    static void Boot(GameObject root){
        var D = Load(); if (D.characters == null) return;
        foreach (var a in (root != null ? root.GetComponentsInChildren<Animator>(true) : Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None)))
        {
            string rn = a.transform.root.name; bool want = false;
            { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null && rrp.enabled) want = true; }   // HCKIT2 v93
            if (want && a.GetComponent<FootYaw>() == null) a.gameObject.AddComponent<FootYaw>();
        }
    }

    static Root data;
    Row row; bool init; Transform lFoot, rFoot, lArm, rArm, lToe, rToe; int frameN; Vector3 lAxis, rAxis; int[][] catHashes;

    static Root Load()
    {
        if (data != null) return data;
        data = new Root { defaults = new Row() };
        foreach (var p in DataPath.Candidates("foot_yaw.json")) /* // HCKIT2 v93 */
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null && r.defaults != null) data = r; } catch { }
            break;
        }
        return data;
    }

    void Init()
    {
        init = true;
        var D = Load(); row = D.defaults;
        var rootName = transform.root.name;
        if (D.characters != null) { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rootName); if (rrp != null) row = rrp; }   // HCKIT2 v93
        if (row == null || !row.enabled) return;
        if (anim == null) { anim = GetComponentInChildren<Animator>(true); if (anim == null) anim = GetComponentInParent<Animator>(); }
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            switch (Roster.BoneRole(Roster.BonePrefix(this), t.name)) /* HCAUDIT1 v104 */
            {
                case "LeftFoot": lFoot = t; break;
                case "RightFoot": rFoot = t; break;
                case "LeftToeBase": lToe = t; break;
                case "RightToeBase": rToe = t; break;
                case "LeftArm": lArm = t; break;
                case "RightArm": rArm = t; break;
            }
        }
        // shin axis = Leg->Foot offset in the Leg frame (the Foot's local position)
        lAxis = lFoot != null ? lFoot.localPosition.normalized : Vector3.up;
        rAxis = rFoot != null ? rFoot.localPosition.normalized : Vector3.up;
        if (row.categories != null)
        {
            catHashes = new int[row.categories.Length][];
            for (int i = 0; i < row.categories.Length; i++)
            {
                var st = row.categories[i].states != null ? row.categories[i].states : new string[0];
                catHashes[i] = new int[st.Length];
                for (int j = 0; j < st.Length; j++) catHashes[i][j] = Animator.StringToHash(st[j]);
            }
        }
        if (debugLog) Debug.Log("[FootYaw] " + rootName + " row=" + row.name + " lFoot=" + (lFoot != null) + " rFoot=" + (rFoot != null) + " lArm=" + (lArm != null) + " rArm=" + (rArm != null));
    }

    int CategoryOf(int shortNameHash)
    {
        if (catHashes == null) return -1;
        for (int i = 0; i < catHashes.Length; i++) for (int j = 0; j < catHashes[i].Length; j++) if (catHashes[i][j] == shortNameHash) return i;
        return -1;
    }

    static float Side(float[] a, int i) { return a != null && a.Length > i ? a[i] : 0f; }

    // yaw (deg) and arm weight for one state hash
    void Sample(int hash, out float yawL, out float yawR, out float armW)
    {
        int c = CategoryOf(hash);
        if (c >= 0 && row.categories[c].footYaw != null && row.categories[c].footYaw.Length >= 2)
        { yawL = row.categories[c].footYaw[0]; yawR = row.categories[c].footYaw[1]; armW = row.categories[c].armWeight; return; }
        yawL = Side(row.footYaw, 0); yawR = Side(row.footYaw, 1); armW = row.armWeight;
    }

    void LateUpdate()
    {
        if (!init) Init();
        if (row == null || !row.enabled) return;

        float yL, yR, aW;
        if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
        {
            var cur = anim.GetCurrentAnimatorStateInfo(0);
            Sample(cur.shortNameHash, out yL, out yR, out aW);
            if (anim.IsInTransition(0))
            {
                var nxt = anim.GetNextAnimatorStateInfo(0); var tr = anim.GetAnimatorTransitionInfo(0);
                float nL, nR, nA; Sample(nxt.shortNameHash, out nL, out nR, out nA);
                float k = Mathf.Clamp01(tr.normalizedTime);
                yL = Mathf.Lerp(yL, nL, k); yR = Mathf.Lerp(yR, nR, k); aW = Mathf.Lerp(aW, nA, k);
            }
        }
        else { yL = Side(row.footYaw, 0); yR = Side(row.footYaw, 1); aW = row.armWeight; }

        bool dbg = (row.debug || debugLog) && PerfFlags.VerboseLogs; float bL = 0, bR = 0;
        if (dbg) { bL = ToeYaw(lFoot, lToe); bR = ToeYaw(rFoot, rToe); }
        // premultiply in the parent frame: local_new = AngleAxis(deg, axis) * local_old
        float s = row.handedSign == 0f ? -1f : row.handedSign;
        if (lFoot != null && Mathf.Abs(yL) > 0.001f) lFoot.localRotation = Quaternion.AngleAxis(yL * s, lAxis) * lFoot.localRotation;
        if (rFoot != null && Mathf.Abs(yR) > 0.001f) rFoot.localRotation = Quaternion.AngleAxis(yR * s, rAxis) * rFoot.localRotation;

        var ax = row.armAxis != null && row.armAxis.Length >= 3 ? new Vector3(row.armAxis[0], row.armAxis[1], row.armAxis[2]) : Vector3.right;
        if (ax.sqrMagnitude < 0.0001f) ax = Vector3.right;
        if (lArm != null && Mathf.Abs(Side(row.armTilt, 0)) > 0.001f) lArm.localRotation = Quaternion.AngleAxis(Side(row.armTilt, 0) * aW, ax) * lArm.localRotation;
        if (rArm != null && Mathf.Abs(Side(row.armTilt, 1)) > 0.001f) rArm.localRotation = Quaternion.AngleAxis(Side(row.armTilt, 1) * aW, ax) * rArm.localRotation;
    

        if (dbg && (++frameN % Mathf.Max(1, (int)row.debugEvery)) == 0)
            Debug.Log("[FootYaw] t=" + Time.time.ToString("F2") + " yawL(in)=" + yL.ToString("F1") + " yawR(in)=" + yR.ToString("F1") + " sign=" + s
                + " toeOut L before/after=" + bL.ToString("F1") + "/" + ToeYaw(lFoot, lToe).ToString("F1")
                + " R before/after=" + bR.ToString("F1") + "/" + ToeYaw(rFoot, rToe).ToString("F1") + " (deg, + = toes outward; ref = model forward)");
    }

    // signed toe yaw vs the body forward on the ground plane; positive = toe points away from the midline (outward)
    float ToeYaw(Transform foot, Transform toe)
    {
        if (foot == null || toe == null) return float.NaN;
        Vector3 bf = anim != null ? anim.transform.forward : transform.forward; bf.y = 0; bf.Normalize();
        Vector3 tf = toe.position - foot.position; tf.y = 0; if (tf.sqrMagnitude < 1e-8f) return float.NaN; tf.Normalize();
        float a = Vector3.SignedAngle(bf, tf, Vector3.up);
        return foot == lFoot ? -a : a;
    }
}
