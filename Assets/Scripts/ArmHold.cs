using UnityEngine;

// Damps the sword-arm swing during locomotion (the blade swings out sideways like a pendulum): in LateUpdate, on top of the Animator pose,
// each listed arm bone's local rotation is blended toward its own slow running mean (time constant `tau`), which removes (1-keep) of the swing amplitude
// around the clip's mean pose. Weight per Animator state (short name) comes from data: Assets/Data/arm_hold.json (StreamingAssets in the player).
// Attack / hit clips are never listed, so they are untouched; the weight eases in/out over `blend` seconds. No scene edit: auto-attaches to every fighter with an enabled arm_hold.json row (rows keyed by roster id, matched on the roster objectName).
public class ArmHold : MonoBehaviour
{
    [System.Serializable] public class BoneRow { public string bone; public float w = 1f; }
    [System.Serializable] public class StateRow { public string[] states; public float weight; }
    [System.Serializable] public class Row
    {
        public string name; public bool enabled; public bool debug; public float tau = 0.7f; public float blend = 0.15f; public float debugEvery = 30f;
        public BoneRow[] bones; public StateRow[] states;
    }
    [System.Serializable] public class Root { public Row[] characters; }

    static Root data;
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in DataPath.Candidates("arm_hold.json")) /* // HCKIT2 v93 */
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null) data = r; } catch { }
            break;
        }
        return data;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var D = Load(); if (D.characters == null) return;
        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            string rn = a.transform.root.name; bool want = false;
            { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null && rrp.enabled) want = true; }   // HCKIT2 v93
            if (want && a.GetComponent<ArmHold>() == null) a.gameObject.AddComponent<ArmHold>();
        }
    }

    Animator anim; Row row; bool init; Transform[] bones; Quaternion[] avg; bool[] avgInit; float[] bw; int[][] stHash; float curW; int frameN;
    Transform shoulder, hand; float zMinB = 999, zMaxB = -999, zMinA = 999, zMaxA = -999, pMinB = 999, pMaxB = -999, pMinA = 999, pMaxA = -999, minB = 999, maxB = -999, minA = 999, maxA = -999;

    void Init()
    {
        init = true; var D = Load(); string rn = transform.root.name;
        { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null) row = rrp; }   // HCKIT2 v93
        if (row == null || !row.enabled || row.bones == null) { row = null; return; }
        anim = GetComponentInChildren<Animator>(true); if (anim == null) anim = GetComponentInParent<Animator>();
        var all = GetComponentsInChildren<Transform>(true);
        bones = new Transform[row.bones.Length]; avg = new Quaternion[bones.Length]; avgInit = new bool[bones.Length]; bw = new float[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            bw[i] = row.bones[i].w;
            foreach (var t in all) if (t.name == row.bones[i].bone) { bones[i] = t; break; }
        }
        foreach (var t in all) { if (t.name == "mixamorig:RightArm") shoulder = t; if (t.name == "mixamorig:RightHand") hand = t; }
        stHash = new int[row.states != null ? row.states.Length : 0][];
        for (int i = 0; i < stHash.Length; i++)
        {
            var st = row.states[i].states ?? new string[0]; stHash[i] = new int[st.Length];
            for (int j = 0; j < st.Length; j++) stHash[i][j] = Animator.StringToHash(st[j]);
        }
    }

    float StateWeight(int hash)
    {
        for (int i = 0; i < stHash.Length; i++) for (int j = 0; j < stHash[i].Length; j++) if (stHash[i][j] == hash) return row.states[i].weight;
        return 0f;
    }

    // lateral angle (deg) of shoulder->hand in the model frame: 0 = hanging straight down, + = away from the body (use abs spread only)
    float ArmAngle()
    {
        if (shoulder == null || hand == null) return 0f;
        Vector3 v = transform.InverseTransformVector(hand.position - shoulder.position);
        return Mathf.Atan2(v.x, -v.y) * Mathf.Rad2Deg;
    }
    // sagittal angle (deg) of shoulder->hand: + = hand in front of the shoulder
    float ArmAngleZ()
    {
        if (shoulder == null || hand == null) return 0f;
        Vector3 v = transform.InverseTransformVector(hand.position - shoulder.position);
        return Mathf.Atan2(v.z, -v.y) * Mathf.Rad2Deg;
    }
    // pitch (deg) of the hand's forward axis vs the ground in the model frame (the blade follows the hand)
    float HandPitch()
    {
        if (hand == null) return 0f;
        Vector3 f = transform.InverseTransformDirection(hand.up);
        return Mathf.Atan2(f.y, new Vector2(f.x, f.z).magnitude) * Mathf.Rad2Deg;
    }

    void LateUpdate()
    {
        if (!init) Init();
        if (row == null || bones == null) return;
        float target = 0f;
        if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
        {
            target = StateWeight(anim.GetCurrentAnimatorStateInfo(0).shortNameHash);
            if (anim.IsInTransition(0))
            {
                float n = StateWeight(anim.GetNextAnimatorStateInfo(0).shortNameHash);
                target = Mathf.Lerp(target, n, Mathf.Clamp01(anim.GetAnimatorTransitionInfo(0).normalizedTime));
            }
        }
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        curW = Mathf.MoveTowards(curW, target, dt / Mathf.Max(0.01f, row.blend));
        float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.05f, row.tau));
        bool dbg = row.debug && PerfFlags.VerboseLogs;   // per-frame diagnostics only when perf.json verboseLogs is on
        float angB = dbg ? ArmAngle() : 0f, zB = dbg ? ArmAngleZ() : 0f, pB = dbg ? HandPitch() : 0f;
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i]; if (b == null) continue;
            var cur = b.localRotation;
            if (!avgInit[i]) { avg[i] = cur; avgInit[i] = true; }
            else avg[i] = Quaternion.Slerp(avg[i], cur, k);
            float w = curW * bw[i];
            if (w > 0.001f) b.localRotation = Quaternion.Slerp(cur, avg[i], Mathf.Clamp01(w));
        }
        if (dbg)
        {
            float angA = ArmAngle(), zA = ArmAngleZ(), pA = HandPitch(); zMinB = Mathf.Min(zMinB, zB); zMaxB = Mathf.Max(zMaxB, zB); zMinA = Mathf.Min(zMinA, zA); zMaxA = Mathf.Max(zMaxA, zA); pMinB = Mathf.Min(pMinB, pB); pMaxB = Mathf.Max(pMaxB, pB); pMinA = Mathf.Min(pMinA, pA); pMaxA = Mathf.Max(pMaxA, pA); minB = Mathf.Min(minB, angB); maxB = Mathf.Max(maxB, angB); minA = Mathf.Min(minA, angA); maxA = Mathf.Max(maxA, angA);
            if (++frameN % Mathf.Max(1, (int)row.debugEvery) == 0)
            {
                Debug.Log("[ArmHold] t=" + Time.time.ToString("F2") + " w=" + curW.ToString("F2") + " target=" + target.ToString("F2")
                    + " armSpread(lateral deg) before " + minB.ToString("F0") + ".." + maxB.ToString("F0") + " (range " + (maxB - minB).ToString("F0")
                    + ") after " + minA.ToString("F0") + ".." + maxA.ToString("F0") + " (range " + (maxA - minA).ToString("F0") + ")"
                    + " | fwd-swing range before " + (zMaxB - zMinB).ToString("F0") + " after " + (zMaxA - zMinA).ToString("F0")
                    + " | hand-axis pitch range before " + (pMaxB - pMinB).ToString("F0") + " after " + (pMaxA - pMinA).ToString("F0"));
                zMinB = zMinA = pMinB = pMinA = 999; zMaxB = zMaxA = pMaxB = pMaxA = -999;
                minB = minA = 999; maxB = maxA = -999;
            }
        }
    }
}
