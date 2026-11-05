using UnityEngine;

// Data-driven weapon-in-hand placement (lane D). The weapon sockets ("WeaponSocket_R") are parented to the hand bone in the scene; this component re-poses the socket
// relative to the hand bone every LateUpdate (absolute, never cumulative) from Assets/Data/weapon_grip.json (StreamingAssets in the player), so the grip can be tuned
// without touching the scene: where the hand sits on the shaft (slide), where the palm is across the shaft (offset) and how the shaft is rolled/tilted in the palm.
//   socketOffset   metres in the HAND BONE frame, added to the socket's original local position (converted by the hand's lossy scale, so it is real-world metres)
//   socketEuler    degrees, extra rotation applied to the socket's original local rotation (hand frame)
//   slide          metres: moves the weapon along its own shaft inside the socket (+ = the weapon moves towards its head, so the hand ends up nearer the butt)
// Self-attaching like FootYaw/GripPose (no scene edit). Rows are matched by the root name containing "name" and need "enabled": true. GripPose (finger curl) still runs on top.
// WEAPONSWAP v88: rows may carry "weapon" (a gear.json items[].weapon.id). While a swapped-in weapon is on the socket (WeaponSwap.IdOf) a character's rows for THAT weapon replace its
// weapon-less rows (the sword); with no row for the weapon the weapon-less rows stay. Switching is live (equip / unequip in game). Socket children are slid by their own recorded base
// position, so a child added later (the swapped-in weapon) is slid too and a returning one (the trail tip) keeps its base.
[DefaultExecutionOrder(-50)]
public class WeaponGrip : MonoBehaviour
{
    [System.Serializable]
    public class Row
    {
        public string name; public bool enabled; public string hand = "RightHand", socket = "WeaponSocket_R";
        public string weapon = "";   // WEAPONSWAP v88: "" = the character's default weapon (and fallback); else the gear.json weapon id this row is for
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
            { int rrb = RosterResolver.Best(D.characters, rrx => rrx.name, rn); foreach (var c in D.characters) if (c != null && c.enabled && rrb > 0 && RosterResolver.Tier(c.name, rn) == rrb) want = true; }   // HCKIT2 v93
            if (want && a.GetComponent<WeaponGrip>() == null) a.gameObject.AddComponent<WeaponGrip>();
        }
    }

    static Root data;
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in DataPath.Candidates("weapon_grip.json")) /* // HCKIT2 v93 */
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null && r.characters != null) data = r; } catch { }
            break;
        }
        return data;
    }

    Row row; Transform hand, socket; Vector3 pos0; Quaternion rot0; bool init;
    readonly System.Collections.Generic.Dictionary<Transform, Vector3> childBase = new System.Collections.Generic.Dictionary<Transform, Vector3>();   // WEAPONSWAP v88: socket child -> its un-slid local position
    readonly System.Collections.Generic.List<Row> all = new System.Collections.Generic.List<Row>();   // every enabled row of this character
    readonly System.Collections.Generic.List<Row> rows = new System.Collections.Generic.List<Row>(); Animator anim; Vector3 curOff, curSlideV; Quaternion curRot = Quaternion.identity; float curSlide; bool curInit;
    string curWeapon = "";

    void Init()
    {
        init = true; string rn = transform.root.name; curWeapon = WeaponSwap.IdOf(transform.root);
        { var lc = Load().characters; int rrb = RosterResolver.Best(lc, rrx => rrx.name, rn); foreach (var c in lc) if (c != null && c.enabled && rrb > 0 && RosterResolver.Tier(c.name, rn) == rrb) all.Add(c); }   // HCKIT2 v93 (best tier, all its rows) + WEAPONSWAP v88 (every enabled row; Select() filters by weapon)
        anim = GetComponent<Animator>();
        if (all.Count == 0) { enabled = false; return; }
        Select();
        var r0 = all[0];
        foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "mixamorig:" + r0.hand) { hand = t; break; }
        if (hand == null) { enabled = false; return; }
        foreach (var t in hand.GetComponentsInChildren<Transform>(true)) if (t.name == r0.socket) { socket = t; break; }
        if (socket == null) { enabled = false; return; }
        pos0 = socket.localPosition; rot0 = socket.localRotation;
        for (int i = 0; i < socket.childCount; i++) childBase[socket.GetChild(i)] = socket.GetChild(i).localPosition;
        if (r0.debugLog) Debug.Log("[WeaponGrip] " + rn + " socket original local pos " + pos0 + " rot " + rot0.eulerAngles);
    }

    // WEAPONSWAP v88: the rows that apply to the weapon on the socket now: this weapon's own rows if the character has any, else the weapon-less rows. row = the default (state-less) one of them.
    void Select()
    {
        rows.Clear(); row = null; bool hasW = false;
        if (curWeapon.Length > 0) foreach (var c in all) if (c.weapon == curWeapon) hasW = true;
        foreach (var c in all)
        {
            string cw = c.weapon ?? "";
            if (hasW ? cw != curWeapon : cw.Length != 0) continue;
            rows.Add(c); if (c.states == null || c.states.Length == 0 || row == null) row = c;
        }
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
        string w = WeaponSwap.IdOf(transform.root);
        if (w != curWeapon) { curWeapon = w; Select(); curInit = false; }   // a weapon was swapped in / out: its rows, no blend from the other weapon's pose
        if (row == null) { socket.localPosition = pos0; socket.localRotation = rot0; return; }   // no row applies to this weapon: the socket as the scene has it
        var tr = PickRow();
        float hs = Mathf.Max(0.0001f, hand.lossyScale.x);
        Vector3 tOff = V(tr.socketOffset); Quaternion tRot = Quaternion.Euler(V(tr.socketEuler)); float tSlide = tr.slide;
        if (!curInit) { curInit = true; curOff = tOff; curRot = tRot; curSlide = tSlide; }
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.005f, tr.blendSeconds));
        curOff = Vector3.Lerp(curOff, tOff, k); curRot = Quaternion.Slerp(curRot, tRot, k); curSlide = Mathf.Lerp(curSlide, tSlide, k);
        socket.localPosition = pos0 + curOff / hs;
        socket.localRotation = curRot * rot0;
        float ss = Mathf.Max(0.0001f, socket.lossyScale.y);
        Vector3 off = Vector3.up * (-curSlide / ss);
        for (int i = 0; i < socket.childCount; i++)
        {
            var ch = socket.GetChild(i); Vector3 b;
            if (!childBase.TryGetValue(ch, out b)) { b = ch.localPosition; childBase[ch] = b; }   // first sight = its un-slid position (a swapped-in weapon is placed by WeaponSwap before)
            ch.localPosition = b + off;
        }
        if (childBase.Count > socket.childCount * 2 + 8) { var dead = new System.Collections.Generic.List<Transform>(); foreach (var kv in childBase) if (kv.Key == null) dead.Add(kv.Key); foreach (var d in dead) childBase.Remove(d); }
    }
}
