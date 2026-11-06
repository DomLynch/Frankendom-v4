using UnityEngine;

// Closes the weapon hand: curls the right-hand finger chain on top of the animator pose (LateUpdate, after animation) so the haft reads as gripped.
// All numbers are data: Assets/Data/grip_pose.json (StreamingAssets in the player): defaults + per-character overrides (axes, angles, per-joint falloff, left hand for two-handed grips).
// WEAPONSWAP v88: a row may carry "weapon" (gear.json weapon id): while that weapon is on the socket (WeaponSwap.IdOf) the character's row for it replaces its weapon-less row (the sword's), live on equip / unequip.
// With a TwoHandIK on the Animator (two-handed weapon) the left hand closes with the IK weight, so the fist opens when the arm lets go.
public class GripPose : MonoBehaviour
{
    [System.Serializable] public class Row { public string name; public float fingerDeg = 62f, thumbDeg = 35f, thumbWeight = 0.7f, falloff = 1f; public float[] fingerAxis = { 0, 0, 1 }, thumbAxis = { 0, 1, 0 }; public string weapon = ""; public bool leftHand; public float leftWeight = 1f; public bool absolute; public string[] leftStates; }   // leftStates: Animator states in which the LEFT hand also grips (two-handed phases; empty = always when leftHand)   // absolute: the curl REPLACES the clip's finger rotation (rest * curl) so the fist holds in every clip/phase; false = old additive curl on top of the clip
    [System.Serializable] public class Root { public Row defaults; public Row[] characters; }
    public float fingerDeg = 62f, thumbDeg = 35f;
    Row row; Transform[][] chains, lchains; Quaternion[][] rest, lrest; bool init; static Root data; string curWeapon = "", rootName = ""; TwoHandIK ik; bool ikLooked;
    static Quaternion[][] Rest(Transform[][] ch) { var r = new Quaternion[ch.Length][]; for (int f = 0; f < ch.Length; f++) { r[f] = new Quaternion[ch[f].Length]; for (int j = 0; j < ch[f].Length; j++) r[f][j] = ch[f][j].localRotation; } return r; }
    void Awake() { chains = Chains("Right"); rest = Rest(chains); lchains = Chains("Left"); lrest = Rest(lchains); anim = GetComponent<Animator>(); }
    Animator anim; float lw = 1f;
    bool LeftOn() { if (row.leftStates == null || row.leftStates.Length == 0 || anim == null) return true; var si = anim.GetCurrentAnimatorStateInfo(0); bool on = Has(row.leftStates, si.shortNameHash); if (anim.IsInTransition(0) && Has(row.leftStates, anim.GetNextAnimatorStateInfo(0).shortNameHash)) on = true; return on; }
    static bool Has(string[] names, int hash) { for (int i = 0; i < names.Length; i++) if (Animator.StringToHash(names[i]) == hash) return true; return false; }   // Awake = before the first Animator evaluation: the bind pose (straight fingers)
    static Root Load()
    {
        if (data != null) return data; data = new Root { defaults = new Row() };
        foreach (var p in DataPath.Candidates("grip_pose.json")) /* // HCKIT2 v93 */ { if (!System.IO.File.Exists(p)) continue; try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null && r.defaults != null) data = r; } catch { } break; }
        return data;
    }
    Transform[][] Chains(string side)
    {
        string[] fingers = { "Index", "Middle", "Ring", "Pinky" }; var all = GetComponentsInChildren<Transform>(true); var c = new Transform[5][];
        for (int f = 0; f < 5; f++) { string nm = f < 4 ? fingers[f] : "Thumb"; var list = new System.Collections.Generic.List<Transform>(); for (int j = 1; j <= 3; j++) foreach (var t in all) if (t.name == "mixamorig:" + side + "Hand" + nm + j) { list.Add(t); break; } c[f] = list.ToArray(); }
        return c;
    }
    // WEAPONSWAP v88: the row for this character + the weapon on the socket: its own weapon row wins, else the last weapon-less row (the old rule), else the defaults
    Row PickRow(Root D, string root, string weapon)
    {
        Row any = null;
        if (D.characters != null)
        {
            int rrb = RosterResolver.Best(D.characters, rrx => rrx.name, root);   // HCKIT2 v93: only the best tier matches this character (id > roster name > "*")
            foreach (var c in D.characters)
            {
                if (c == null || rrb <= 0 || RosterResolver.Tier(c.name, root) != rrb) continue;
                if (string.IsNullOrEmpty(c.weapon)) any = c; else if (weapon.Length > 0 && c.weapon == weapon) return c;
            }
        }
        return any ?? D.defaults;
    }
    void Init()
    {
        init = true; var D = Load(); rootName = transform.root.name; curWeapon = WeaponSwap.IdOf(transform.root); row = PickRow(D, rootName, curWeapon);
        if (chains == null) { chains = Chains("Right"); rest = Rest(chains); }   // the left chains stay (v88: the row can change with the weapon); row.leftHand decides whether they are used
        if (!row.leftHand) lw = 0f;   // the old code never touched the left hand for such a row (lchains was nulled); lw starts at 1 for the rows that do use it (unchanged)
    }
    static Vector3 Ax(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.forward; }
    void Curl(Transform[][] ch, float w, Quaternion[][] rs)
    {
        for (int f = 0; f < 5; f++)
            for (int j = 0; j < ch[f].Length; j++)
            {
                float a = f < 4 ? row.fingerDeg : row.thumbDeg * row.thumbWeight; a *= w * Mathf.Pow(row.falloff, j);
                { var q = Quaternion.AngleAxis(a, f < 4 ? Ax(row.fingerAxis) : Ax(row.thumbAxis)); ch[f][j].localRotation = row.absolute && rs != null ? Quaternion.Slerp(ch[f][j].localRotation, rs[f][j] * q, w) : ch[f][j].localRotation * q; }
            }
    }
    void LateUpdate()
    {
        if (!init) Init();
        else { string w = WeaponSwap.IdOf(transform.root); if (w != curWeapon) { curWeapon = w; row = PickRow(Load(), rootName, w); } }
        Curl(chains, 1f, rest);
        if (lchains != null)
        {
            if (!ikLooked) { ikLooked = true; ik = GetComponent<TwoHandIK>(); } else if (ik == null && Time.frameCount % 30 == 0) ik = GetComponent<TwoHandIK>();   // WeaponSwap adds it later, on the first two-handed weapon
            float want = row.leftHand && LeftOn() ? 1f : 0f;
            if (want > 0f && ik != null && ik.Driving) want = Mathf.Min(want, ik.Weight);   // the fist follows the arm: opens when the IK lets go
            lw = Mathf.MoveTowards(lw, want, Time.deltaTime / 0.1f); if (lw > 0f) Curl(lchains, row.leftWeight * lw, lrest);
        }
    }
}
