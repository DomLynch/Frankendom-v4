using UnityEngine;

// Head / neck over-rotation clamp (lane E). Applied in LateUpdate, AFTER the Animator, to the Neck and Head bones.
// The head's orientation is measured RELATIVE TO THE TORSO bone (Spine2): yaw (turn left/right) and pitch (nod) of the head's forward axis.
// Whatever produced the twist (a masked Animator layer, a clip, a stale attach offset) the head can never leave +-yaw / +-pitch of the torso.
// Optional look-at (off by default): the head turns toward the opponent by `lookWeight`, faded out when the target is more than `fadeStartDeg`
// off the body's forward (gone at `fadeEndDeg`), still inside the same clamp.
// All numbers are data: Assets/Data/head_clamp.json (StreamingAssets in the player). Auto-attaches to every Animator whose root name matches an enabled row (like FootYaw).
// Rest orientations of the bones are read from the skin bind poses, so no assumption is made about the glb import's bone axes.
[DefaultExecutionOrder(50)]
public class HeadClamp : MonoBehaviour
{
    [System.Serializable] public class Limit { public string id; public string[] states; public float yaw = 45f, pitch = 20f; public bool off; }
    [System.Serializable] public class Look { public bool enabled; public float weight = 0.5f, fadeStartDeg = 90f, fadeEndDeg = 120f, followSeconds = 0.15f, targetHeight = 1.5f; }
    [System.Serializable] public class Row
    {
        public string name; public bool enabled = true; public bool debug;
        public string torsoBone = "mixamorig:Spine2", neckBone = "mixamorig:Neck", headBone = "mixamorig:Head";
        public float yaw = 45f, pitch = 20f;      // default limits (deg, head relative to the torso)
        public float neckShare = 0.5f;            // the neck bone alone is limited to share x the limits (the rest is taken by the head bone)
        public float knee = 0.8f;                 // soft limit: no change below knee x limit, smooth approach to the limit above it
        public Limit[] categories; public Look look = new Look();
    }
    [System.Serializable] public class Root { public Row defaults = new Row(); public Row[] characters; }

    public Animator anim; public bool debugLog;
    // debug readout (degrees, head relative to torso): raw = before the clamp this frame, now = after
    public float rawYaw, rawPitch, nowYaw, nowPitch, limitYaw, limitPitch; public bool restKnown; public int foundMask;
    public static bool ForceOff;   // harness: -noclamp

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var D = Load(); if (D.characters == null) return;
        foreach (var a in FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            string rn = a.transform.root.name; bool want = false;
            { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null && rrp.enabled) want = true; }   // HCKIT2 v93
            if (want && a.GetComponent<HeadClamp>() == null) a.gameObject.AddComponent<HeadClamp>();
        }
        foreach (var arg in System.Environment.GetCommandLineArgs()) if (arg == "-noclamp") ForceOff = true;
    }

    static Root data;
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in DataPath.Candidates("head_clamp.json")) /* // HCKIT2 v93 */
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null && r.defaults != null) data = r; } catch { }
            break;
        }
        return data;
    }

    Row row; bool init; Transform torso, neck, head, root; Quaternion qTorso = Quaternion.identity, qNeck = Quaternion.identity, qHead = Quaternion.identity;
    int[][] catHashes; DuelFighter me; float lookYaw, lookPitch;

    void Init()
    {
        init = true; var D = Load(); row = D.defaults;
        string rn = transform.root.name;
        if (D.characters != null) { var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, rn); if (rrp != null) row = rrp; }   // HCKIT2 v93
        if (row == null || !row.enabled) return;
        if (anim == null) { anim = GetComponent<Animator>(); if (anim == null) anim = GetComponentInChildren<Animator>(true); }
        root = anim != null ? anim.transform : transform;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == row.torsoBone) torso = t; else if (t.name == row.neckBone) neck = t; else if (t.name == row.headBone) head = t;
        }
        me = GetComponentInParent<DuelFighter>();
        // rest orientation of each bone in the root frame, from the skin bind poses: Q = inverse(root) * smr * rotation(inverse(bindpose))
        int found = 0;
        var smrRoot = me != null ? me.transform : transform.root;
        foreach (var smr in smrRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var m = smr.sharedMesh; var bones = smr.bones; if (m == null || bones == null) continue; var bp = m.bindposes;
            for (int i = 0; i < bones.Length && i < bp.Length; i++)
            {
                Quaternion q = Quaternion.Inverse(root.rotation) * smr.transform.rotation * bp[i].inverse.rotation;
                if (bones[i] == torso && (found & 1) == 0) { qTorso = q; found |= 1; }
                else if (bones[i] == neck && (found & 2) == 0) { qNeck = q; found |= 2; }
                else if (bones[i] == head && (found & 4) == 0) { qHead = q; found |= 4; }
            }
            if (found == 7) break;
        }
        restKnown = found == 7; foundMask = found;
        if (row.categories != null)
        {
            catHashes = new int[row.categories.Length][];
            for (int i = 0; i < row.categories.Length; i++)
            {
                var st = row.categories[i].states ?? new string[0]; catHashes[i] = new int[st.Length];
                for (int j = 0; j < st.Length; j++) catHashes[i][j] = Animator.StringToHash(st[j]);
            }
        }
        if (debugLog || row.debug) Debug.Log("[HeadClamp] " + rn + " torso=" + (torso != null) + " neck=" + (neck != null) + " head=" + (head != null) + " restFromBindposes=" + restKnown + " foundMask=" + found + " qTorso=" + qTorso.eulerAngles + " qHead=" + qHead.eulerAngles);
    }

    int CategoryOf(int hash)
    {
        if (catHashes == null) return -1;
        for (int i = 0; i < catHashes.Length; i++) for (int j = 0; j < catHashes[i].Length; j++) if (catHashes[i][j] == hash) return i;
        return -1;
    }

    // limits for one state: (yaw, pitch, on 0/1)
    void Sample(int hash, out float y, out float p, out float on)
    {
        int c = CategoryOf(hash);
        if (c >= 0) { var L = row.categories[c]; y = L.yaw; p = L.pitch; on = L.off ? 0f : 1f; }
        else { y = row.yaw; p = row.pitch; on = 1f; }
    }

    // pose of bone b in the root frame relative to its rest pose (identity = rest)
    Quaternion Pose(Transform b, Quaternion q) { return Quaternion.Inverse(root.rotation) * b.rotation * Quaternion.Inverse(q); }
    void SetPose(Transform b, Quaternion q, Quaternion pose) { b.rotation = root.rotation * pose * q; }

    // yaw / pitch (deg) of the forward axis of rotation r
    static void YawPitch(Quaternion r, out float yaw, out float pitch)
    {
        Vector3 f = r * Vector3.forward; yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
    static Vector3 Dir(float yaw, float pitch)
    {
        float y = yaw * Mathf.Deg2Rad, p = pitch * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
    }
    // soft clamp: unchanged below knee x limit, tanh approach to the limit above
    static float Soft(float a, float limit, float knee)
    {
        if (limit <= 0f) return 0f; float s = Mathf.Sign(a), v = Mathf.Abs(a), k = limit * Mathf.Clamp01(knee);
        if (v <= k) return a; float room = limit - k; if (room < 1e-3f) return s * limit;
        return s * (k + room * (float)System.Math.Tanh((v - k) / room));
    }

    // clamp bone `b` (pose frame) relative to the torso pose; returns true when it was changed
    bool ClampBone(Transform b, Quaternion q, Quaternion torsoPose, float limY, float limP, out float rawY, out float rawP, float w)
    {
        Quaternion rel = Quaternion.Inverse(torsoPose) * Pose(b, q); YawPitch(rel, out rawY, out rawP);
        float y2 = Soft(rawY, limY, row.knee), p2 = Soft(rawP, limP, row.knee);
        if (Mathf.Abs(y2 - rawY) < 0.01f && Mathf.Abs(p2 - rawP) < 0.01f) return false;
        y2 = Mathf.Lerp(rawY, y2, w); p2 = Mathf.Lerp(rawP, p2, w);
        Quaternion c = Quaternion.FromToRotation(rel * Vector3.forward, Dir(y2, p2));
        SetPose(b, q, torsoPose * (c * rel)); return true;
    }

    // measured now (after every LateUpdate that ran before the caller): head yaw / pitch relative to the torso
    public bool Measure(out float yaw, out float pitch, out float torsoYawRoot, out float hipsYawRoot)
    {
        yaw = pitch = torsoYawRoot = hipsYawRoot = 0f; if (!init) Init(); if (torso == null || head == null) return false;
        Quaternion tp = Pose(torso, qTorso); YawPitch(Quaternion.Inverse(tp) * Pose(head, qHead), out yaw, out pitch);
        float tpitch; YawPitch(tp, out torsoYawRoot, out tpitch);
        hipsYawRoot = 0f; return true;
    }

    void LateUpdate()
    {
        if (!init) Init();
        if (row == null || !row.enabled || torso == null || head == null) return;

        float y = row.yaw, p = row.pitch, on = 1f;
        if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
        {
            var cur = anim.GetCurrentAnimatorStateInfo(0); Sample(cur.shortNameHash, out y, out p, out on);
            if (anim.IsInTransition(0))
            {
                var nxt = anim.GetNextAnimatorStateInfo(0); float k = Mathf.Clamp01(anim.GetAnimatorTransitionInfo(0).normalizedTime);
                float ny, np, non; Sample(nxt.shortNameHash, out ny, out np, out non); y = Mathf.Lerp(y, ny, k); p = Mathf.Lerp(p, np, k); on = Mathf.Lerp(on, non, k);
            }
        }
        limitYaw = y; limitPitch = p;
        Quaternion tp = Pose(torso, qTorso);

        // optional look-at toward the opponent (data: look.enabled / weight), faded out when the target is far behind the body
        if (row.look != null && row.look.enabled && me != null && me.opponent != null && !ForceOff)
        {
            Vector3 to = me.opponent.transform.position + Vector3.up * row.look.targetHeight - head.position; Vector3 flat = to; flat.y = 0f;
            float off = Vector3.Angle(root.forward, flat.sqrMagnitude > 1e-6f ? flat : root.forward);
            float fade = 1f - Mathf.InverseLerp(row.look.fadeStartDeg, Mathf.Max(row.look.fadeStartDeg + 1f, row.look.fadeEndDeg), off);
            Vector3 local = Quaternion.Inverse(root.rotation * tp) * to.normalized; float ty, tpp; YawPitch(Quaternion.LookRotation(local), out ty, out tpp);
            float k = row.look.followSeconds > 0f ? 1f - Mathf.Exp(-Time.deltaTime / row.look.followSeconds) : 1f;
            lookYaw = Mathf.Lerp(lookYaw, ty * row.look.weight * fade, k); lookPitch = Mathf.Lerp(lookPitch, tpp * row.look.weight * fade, k);
            Quaternion rel = Quaternion.Inverse(tp) * Pose(head, qHead); float ry, rp; YawPitch(rel, out ry, out rp);
            // add the look turn on top of the animated head pose (about the torso up / right axes)
            Quaternion add = Quaternion.AngleAxis(lookYaw, Vector3.up) * Quaternion.AngleAxis(-lookPitch, Vector3.right);
            SetPose(head, qHead, tp * (add * rel));
        }

        float rawY, rawP; float w = ForceOff ? 0f : on;
        if (w > 0f && neck != null && row.neckShare > 0f) ClampBone(neck, qNeck, tp, y * row.neckShare, p * row.neckShare, out rawY, out rawP, w);
        ClampBone(head, qHead, tp, y, p, out rawYaw, out rawPitch, w);
        Quaternion rel2 = Quaternion.Inverse(tp) * Pose(head, qHead); YawPitch(rel2, out nowYaw, out nowPitch);
        if ((debugLog || row.debug) && PerfFlags.VerboseLogs) { if ((Time.frameCount % 60) == 0) Debug.Log("[HeadClamp] raw yaw/pitch=" + rawYaw.ToString("F1") + "/" + rawPitch.ToString("F1") + " now=" + nowYaw.ToString("F1") + "/" + nowPitch.ToString("F1") + " limit=" + y + "/" + p); }
    }
}
