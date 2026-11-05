using UnityEngine;

// Foot grounding pass (lane C): runs in LateUpdate after the Animator (and before Fighter.LateUpdate's coarse plant).
//  1. a ground raycast per foot (steps / stairs handled: each foot gets its own height and normal),
//  2. PELVIS offset: the LOWEST foot defines the hips height, so the hips DROP (or rise) instead of both feet leaving the floor,
//  3. analytic two-bone leg IK (UpLeg-Leg-Foot, knee pole = the clip's own knee direction) that plants a foot whose ankle is within
//     [plantSnap .. maxLift] above its ground (deliberate swing lifts above maxLift are left alone), blended by smoothstep,
//  4. foot levelled to the ground normal and the toe segment flattened (ToeBase back to flat) while the foot is planted.
// Everything is data: Assets/Data/foot_ground.json (StreamingAssets in the player). Auto-attaches to every Animator whose root has an enabled row.
[DefaultExecutionOrder(-100)]
public class FootGround : MonoBehaviour
{
    [System.Serializable] public class StateRow { public string[] states; public float weight = 1f; }
    [System.Serializable] public class Row
    {
        public string name; public bool enabled; public bool debug; public float debugEvery = 10f;
        public float weight = 1f;            // global weight
        public float ankleHeight = 0.10f;    // ankle (Foot bone) height above the sole/ground when the foot is flat (m, in world units)
        public float rayUp = 0.6f, rayDown = 0.9f;
        public int[] ignoreLayers = { 8, 9 };
        public float pelvisDownMax = 0.25f, pelvisUpMax = 0.10f, pelvisSmooth = 0.06f;
        public float plantSnap = 0.02f;      // ankle within this of the target height: fully planted
        public float maxLift = 0.16f;        // above this height the IK lets go (deliberate swing / step)
        public float levelWeight = 1f;       // foot levelled to the ground normal while planted
        public float toeFlatWeight = 1f;     // toe segment flattened while planted
        public float stateBlend = 0.1f;      // seconds to ease the per-state weight
        public StateRow[] states;            // unlisted states use `weight` only
    }
    [System.Serializable] public class Root { public Row[] characters; }

    static Root data;
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in DataPath.Candidates("foot_ground.json")) /* // HCKIT2 v93 */
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
        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsInactive.Include))
        {
            string rn = a.transform.root.name; bool want = false;
            { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null && rrp.enabled) want = true; }   // HCKIT2 v93
            if (want && a.GetComponent<FootGround>() == null) a.gameObject.AddComponent<FootGround>();
        }
    }

    Animator anim; Row row; bool init; Transform hips, lUp, lLeg, lFoot, lToe, lToeEnd, rUp, rLeg, rFoot, rToe, rToeEnd;
    Vector3 baseHipsLocal, lastSetLocal; bool haveBase; int[][] stHash; float curW = 1f, pelvis, pelvisVel; int frameN; int mask;

    void Init()
    {
        init = true; var D = Load(); string rn = transform.root.name;
        { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null) row = rrp; }   // HCKIT2 v93
        if (row == null || !row.enabled) { row = null; return; }
        anim = GetComponentInChildren<Animator>(true); if (anim == null) anim = GetComponentInParent<Animator>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
            switch (t.name)
            {
                case "mixamorig:Hips": hips = t; break;
                case "mixamorig:LeftUpLeg": lUp = t; break; case "mixamorig:LeftLeg": lLeg = t; break; case "mixamorig:LeftFoot": lFoot = t; break;
                case "mixamorig:LeftToeBase": lToe = t; break; case "mixamorig:LeftToe_End": lToeEnd = t; break;
                case "mixamorig:RightUpLeg": rUp = t; break; case "mixamorig:RightLeg": rLeg = t; break; case "mixamorig:RightFoot": rFoot = t; break;
                case "mixamorig:RightToeBase": rToe = t; break; case "mixamorig:RightToe_End": rToeEnd = t; break;
            }
        if (hips == null || lUp == null || lLeg == null || lFoot == null || rUp == null || rLeg == null || rFoot == null) { Debug.LogWarning("[FootGround] bones missing on " + rn); row = null; return; }
        int ig = 0; if (row.ignoreLayers != null) foreach (var l in row.ignoreLayers) ig |= 1 << l; mask = ~ig;
        stHash = new int[row.states != null ? row.states.Length : 0][];
        for (int i = 0; i < stHash.Length; i++)
        { var st = row.states[i].states ?? new string[0]; stHash[i] = new int[st.Length]; for (int j = 0; j < st.Length; j++) stHash[i][j] = Animator.StringToHash(st[j]); }
        Debug.Log("[FootGround] attached to " + rn + " (bones ok, toeEnd " + (lToeEnd != null) + "/" + (rToeEnd != null) + ")");
    }

    float StateWeight(int hash)
    {
        for (int i = 0; i < stHash.Length; i++) for (int j = 0; j < stHash[i].Length; j++) if (stHash[i][j] == hash) return row.states[i].weight;
        return 1f;
    }

    bool Ground(Vector3 p, out float gy, out Vector3 n)
    {
        gy = p.y - row.ankleHeight; n = Vector3.up;
        if (Physics.Raycast(p + Vector3.up * row.rayUp, Vector3.down, out var hit, row.rayUp + row.rayDown, mask, QueryTriggerInteraction.Ignore))
        { gy = hit.point.y; n = hit.normal; return true; }
        return false;
    }

    // two-bone IK: move ankle (foot.position) to T keeping the clip's knee plane
    static void TwoBone(Transform up, Transform leg, Transform foot, Vector3 T)
    {
        Vector3 H = up.position, K = leg.position, A = foot.position;
        float l1 = (K - H).magnitude, l2 = (A - K).magnitude; if (l1 < 1e-4f || l2 < 1e-4f) return;
        Vector3 toT = T - H; float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f); Vector3 dir = toT.normalized;
        float a = (l1 * l1 - l2 * l2 + d * d) / (2f * d); float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - a * a));
        Vector3 pv = (K - H) - Vector3.Dot(K - H, dir) * dir; if (pv.sqrMagnitude < 1e-8f) pv = up.forward; pv.Normalize();
        Vector3 newK = H + dir * a + pv * h;
        up.rotation = Quaternion.FromToRotation(K - H, newK - H) * up.rotation;
        Vector3 K2 = leg.position, A2 = foot.position, tgt = H + dir * d;
        leg.rotation = Quaternion.FromToRotation(A2 - K2, tgt - K2) * leg.rotation;
    }

    struct FootInfo { public float gy, delta; public Vector3 n; public bool hit; }

    FootInfo Measure(Transform foot)
    {
        var f = new FootInfo(); f.hit = Ground(foot.position, out f.gy, out f.n);
        f.delta = foot.position.y - (f.gy + row.ankleHeight); return f;
    }

    void LateUpdate()
    {
        if (!init) Init();
        if (row == null) return;
        float target = 1f;
        if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
        {
            target = StateWeight(anim.GetCurrentAnimatorStateInfo(0).shortNameHash);
            if (anim.IsInTransition(0))
                target = Mathf.Lerp(target, StateWeight(anim.GetNextAnimatorStateInfo(0).shortNameHash), Mathf.Clamp01(anim.GetAnimatorTransitionInfo(0).normalizedTime));
        }
        // the Animator does not rewrite a constant (in-place) hips translation, so undo our own offset from last frame first
        if (haveBase && (hips.localPosition - lastSetLocal).sqrMagnitude < 1e-10f) hips.localPosition = baseHipsLocal;
        baseHipsLocal = hips.localPosition; haveBase = true;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        curW = Mathf.MoveTowards(curW, target, dt / Mathf.Max(0.01f, row.stateBlend));
        float W = row.weight * curW;
        bool dbg = row.debug && PerfFlags.VerboseLogs;
        FootInfo bl = default, br = default; if (dbg) { bl = Measure(lFoot); br = Measure(rFoot); }
        if (W <= 0.001f) { pelvis = 0f; lastSetLocal = hips.localPosition; return; }

        // 1) pelvis: the lowest foot defines the hips height (drop when both are high, rise when both sink)
        var L = Measure(lFoot); var R = Measure(rFoot);
        float lowest = Mathf.Min(L.delta, R.delta);
        float want = 0f;
        if (L.hit || R.hit)
        {
            // only react to a lift that is a floor-contact problem (both feet within maxLift of the ground), never to a jump/air
            if (lowest > 0f && lowest <= row.maxLift * 2f) want = -Mathf.Min(lowest, row.pelvisDownMax);
            else if (lowest < 0f) want = Mathf.Min(-lowest, row.pelvisUpMax);
        }
        pelvis = Mathf.SmoothDamp(pelvis, want * W, ref pelvisVel, Mathf.Max(0.001f, row.pelvisSmooth), Mathf.Infinity, dt);
        hips.position += Vector3.up * pelvis; lastSetLocal = hips.localPosition;

        // 2) feet: IK-plant each foot within snap..maxLift, level to the normal, flatten the toe
        Plant(lUp, lLeg, lFoot, lToe, lToeEnd, W);
        Plant(rUp, rLeg, rFoot, rToe, rToeEnd, W);

        if (dbg && (++frameN % Mathf.Max(1, (int)row.debugEvery)) == 0)
        {
            var al = Measure(lFoot); var ar = Measure(rFoot);
            Debug.Log("[FootGround] t=" + Time.time.ToString("F2") + " w=" + W.ToString("F2") + " pelvis=" + (pelvis * 100f).ToString("F1") + "cm"
                + " ankleAboveGround(cm) L before/after " + (bl.delta * 100f).ToString("F1") + "/" + (al.delta * 100f).ToString("F1")
                + " R before/after " + (br.delta * 100f).ToString("F1") + "/" + (ar.delta * 100f).ToString("F1")
                + " toePitch(deg) L " + ToePitch(lFoot, lToe, lToeEnd).ToString("F0") + " R " + ToePitch(rFoot, rToe, rToeEnd).ToString("F0")
                + " groundY L " + al.gy.ToString("F2") + " R " + ar.gy.ToString("F2"));
        }
    }

    static float ToePitch(Transform foot, Transform toe, Transform toeEnd)
    {
        if (toe == null || toeEnd == null) return 0f;
        Vector3 v = toeEnd.position - toe.position;
        return Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
    }

    void Plant(Transform up, Transform leg, Transform foot, Transform toe, Transform toeEnd, float W)
    {
        var f = Measure(foot); if (!f.hit) return;
        // planted weight: 1 within plantSnap, 0 above maxLift (smoothstep between); feet BELOW the floor are always pushed up
        float w;
        if (f.delta <= row.plantSnap) w = 1f;
        else { float x = Mathf.Clamp01((f.delta - row.plantSnap) / Mathf.Max(1e-4f, row.maxLift - row.plantSnap)); w = 1f - x * x * (3f - 2f * x); }
        w *= W;
        if (w < 0.001f) return;
        Quaternion footRot = foot.rotation;
        float newY = Mathf.Lerp(foot.position.y, f.gy + row.ankleHeight, w);
        Vector3 T = new Vector3(foot.position.x, newY, foot.position.z);
        if ((T - foot.position).sqrMagnitude > 1e-8f) TwoBone(up, leg, foot, T);
        foot.rotation = footRot;
        // level the foot (heel-to-toe axis flattened onto the ground plane) and flatten the toe segment
        if (toe != null && row.levelWeight > 0f)
        {
            Vector3 fd = toe.position - foot.position;
            Vector3 flat = Vector3.ProjectOnPlane(fd, f.n);
            if (fd.sqrMagnitude > 1e-8f && flat.sqrMagnitude > 1e-8f)
                foot.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(fd, flat), w * row.levelWeight) * foot.rotation;
        }
        if (toe != null && toeEnd != null && row.toeFlatWeight > 0f)
        {
            Vector3 td = toeEnd.position - toe.position;
            Vector3 flat = Vector3.ProjectOnPlane(td, f.n);
            if (td.sqrMagnitude > 1e-8f && flat.sqrMagnitude > 1e-8f)
                toe.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(td, flat), w * row.toeFlatWeight) * toe.rotation;
        }
    }
}
