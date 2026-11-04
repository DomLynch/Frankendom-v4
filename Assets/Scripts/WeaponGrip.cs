using UnityEngine;

// Data-driven weapon-in-hand placement (lane D). The weapon sockets ("WeaponSocket_R") are parented to the hand bone in the scene; this component re-poses the socket
// relative to the hand bone every LateUpdate (absolute, never cumulative) from Assets/Data/weapon_grip.json (StreamingAssets in the player), so the grip can be tuned
// without touching the scene: where the hand sits on the shaft (slide), where the palm is across the shaft (offset) and how the shaft is rolled/tilted in the palm.
//   socketOffset   metres in the HAND BONE frame, added to the socket's original local position (converted by the hand's lossy scale, so it is real-world metres)
//   socketEuler    degrees, extra rotation applied to the socket's original local rotation (hand frame)
//   slide          metres: moves the weapon along its own shaft inside the socket (+ = the weapon moves towards its head, so the hand ends up nearer the butt)
// Self-attaching like FootYaw/GripPose (no scene edit). Rows are matched by the root name containing "name" and need "enabled": true. GripPose (finger curl) still runs on top.
[DefaultExecutionOrder(-50)]
public class WeaponGrip : MonoBehaviour
{
    [System.Serializable]
    public class Row
    {
        public string name; public bool enabled; public string hand = "RightHand", socket = "WeaponSocket_R";
        public float[] socketOffset = { 0f, 0f, 0f }, socketEuler = { 0f, 0f, 0f }; public float slide, blendSeconds = 0.1f;
        public string[] states = new string[0];   // Animator states this row applies to (empty = the default row); several rows may share a name
        public bool debugLog;
    }
    [System.Serializable] public class Root { public Row[] characters = new Row[0]; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var D = Load(); if (D.characters == null) return;
        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            string rn = a.transform.root.name; bool want = false;
            foreach (var c in D.characters) if (c != null && c.enabled && !string.IsNullOrEmpty(c.name) && rn.Contains(c.name)) want = true;
            if (want && a.GetComponent<WeaponGrip>() == null) a.gameObject.AddComponent<WeaponGrip>();
        }
    }

    static Root data;
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in new[] { Application.streamingAssetsPath + "/weapon_grip.json", Application.dataPath + "/Data/weapon_grip.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null && r.characters != null) data = r; } catch { }
            break;
        }
        return data;
    }

    Row row; Transform hand, socket; Vector3 pos0; Quaternion rot0; Vector3[] childPos0 = new Vector3[0]; bool init;
    readonly System.Collections.Generic.List<Row> rows = new System.Collections.Generic.List<Row>(); Animator anim; Vector3 curOff, curSlideV; Quaternion curRot = Quaternion.identity; float curSlide; bool curInit;

    void Init()
    {
        init = true; string rn = transform.root.name;
        foreach (var c in Load().characters) if (c != null && c.enabled && !string.IsNullOrEmpty(c.name) && rn.Contains(c.name)) { rows.Add(c); if (c.states == null || c.states.Length == 0 || row == null) row = c; }
        anim = GetComponent<Animator>();
        if (row == null) { enabled = false; return; }
        foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "mixamorig:" + row.hand) { hand = t; break; }
        if (hand == null) { enabled = false; return; }
        foreach (var t in hand.GetComponentsInChildren<Transform>(true)) if (t.name == row.socket) { socket = t; break; }
        if (socket == null) { enabled = false; return; }
        pos0 = socket.localPosition; rot0 = socket.localRotation;
        childPos0 = new Vector3[socket.childCount];
        for (int i = 0; i < childPos0.Length; i++) childPos0[i] = socket.GetChild(i).localPosition;
        if (row.debugLog) Debug.Log("[WeaponGrip] " + rn + " socket original local pos " + pos0 + " rot " + rot0.eulerAngles);
    }

    Row PickRow()
    {
        if (rows.Count < 2 || anim == null) return row;
        int h = anim.GetCurrentAnimatorStateInfo(0).shortNameHash; if (anim.IsInTransition(0) && anim.GetAnimatorTransitionInfo(0).normalizedTime > 0.5f) h = anim.GetNextAnimatorStateInfo(0).shortNameHash;
        for (int i = 0; i < rows.Count; i++) { var r = rows[i]; if (r.states == null) continue; for (int j = 0; j < r.states.Length; j++) if (Animator.StringToHash(r.states[j]) == h) return r; }
        return row;
    }

    static Vector3 V(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }

    void LateUpdate()
    {
        if (!init) Init();
        if (socket == null) return;
        var tr = PickRow();
        float hs = Mathf.Max(0.0001f, hand.lossyScale.x);
        Vector3 tOff = V(tr.socketOffset); Quaternion tRot = Quaternion.Euler(V(tr.socketEuler)); float tSlide = tr.slide;
        if (!curInit) { curInit = true; curOff = tOff; curRot = tRot; curSlide = tSlide; }
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.005f, tr.blendSeconds));
        curOff = Vector3.Lerp(curOff, tOff, k); curRot = Quaternion.Slerp(curRot, tRot, k); curSlide = Mathf.Lerp(curSlide, tSlide, k);
        socket.localPosition = pos0 + curOff / hs;
        socket.localRotation = curRot * rot0;
        float ss = Mathf.Max(0.0001f, socket.lossyScale.y);
        for (int i = 0; i < childPos0.Length && i < socket.childCount; i++) socket.GetChild(i).localPosition = childPos0[i] + Vector3.up * (-curSlide / ss);
    }
}
