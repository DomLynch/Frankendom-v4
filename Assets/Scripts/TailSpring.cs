using UnityEngine;

// SOBEKDATA v104: PRESENTATION-ONLY secondary motion for the Sobek tail (7 bones on the existing fk_* joints) and, only if the mesh has them, dorsal sails and forearm fins.
// Data: Assets/Data/tail_spring.json (StreamingAssets in the player). Every number lives there: master switch `enabled`, per-character `enabled`, solver step, spring / damping / gravity per chain, angle limit, influence.
// Runs in LateUpdate, i.e. AFTER the Animator wrote the pose, and only rotates the listed bones (their children follow). It never writes anything the sim reads:
//   * no reference to the sim, hit boxes, RNG or Random (the motion is a pure function of the animated bone positions and Time.deltaTime), no physics query, no Rigidbody, no collider;
//   * it uses render time only (Time.deltaTime, never fixedDeltaTime), so the 60 Hz sim state and the simhash are identical with the switch on or off.
// A tail bone must therefore never be read by combat code (hit capsules, cut units, probes): the tail is cosmetic. enabled=false (master or character) = this component is not even attached.
// No scene edit: auto-attaches to every fighter whose roster object matches an enabled row (RosterResolver, like ArmHold).
public class TailSpring : MonoBehaviour
{
    [System.Serializable]
    public class Chain
    {
        public string name = "";
        public bool enabled = true;
        public string[] bones = new string[0];          // parents first; a bone whose NEXT listed bone is its direct child aims at that child, otherwise at a tip point (tipAxis * tipLengthM)
        public float stiffness = 60f;                   // spring rate toward the animated tip, 1/s^2
        public float damping = 6f;                      // velocity decay, 1/s
        public float gravity = 0f;                      // multiple of world.gravityMps2 applied along world.gravityDir
        public float maxAngleDeg = 30f;                 // largest deviation of one bone from its animated direction
        public float rootInfluence = 1f;                // effect weight at the first bone of the chain (0 = animated pose only)
        public float tipInfluence = 1f;                 // effect weight at the last bone
        public Vector3 tipAxis = new Vector3(0f, 1f, 0f); // bone-local aim axis used when the bone has no listed child
        public float tipLengthM = 0.3f;                 // tip distance (model metres) when the bone has no listed child
    }
    [System.Serializable]
    public class Row
    {
        public string name = ""; public bool enabled = true; public bool debug = false; public float debugEvery = 120f;
        public Chain[] chains = new Chain[0];
    }
    [System.Serializable]
    public class World
    {
        public Vector3 gravityDir = new Vector3(0f, -1f, 0f); public float gravityMps2 = 9.81f;
        public float solverStepS = 0.0083f;             // solver step (render time)
        public int maxSubsteps = 4;                     // per frame
        public float maxFrameS = 0.1f;                  // a longer frame is clamped to this (hitch, load)
        public float teleportM = 2f;                    // tip farther than this from its animated place = snap (respawn, scene reset)
        public float fadeInS = 0.5f;                    // influence ramps 0 -> 1 after the component starts
        public float minBoneLenM = 0.001f;
    }
    [System.Serializable] public class Root { public string note = ""; public int version; public bool enabled; public World world = new World(); public Row[] characters = new Row[0]; }

    static Root data;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; }
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in DataPath.Candidates("tail_spring.json"))
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(DataPath.ReadFile(p)); if (r != null) data = r; } catch { }
            break;
        }
        return data;
    }
    /// <summary>The master switch (tail_spring.json enabled). Read-only for QA / A-B captures.</summary>
    public static bool MasterOn { get { return Load().enabled; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { Boot(null); }   // ZONES-ATTACH v1 (INT107)
    public static void AttachTo(GameObject root) { Boot(root); }   // same want-check, one object (ZoneRuntime.SpawnOne for a prefab creature)
    static void Boot(GameObject root){
        var D = Load(); if (!D.enabled || D.characters == null) return;      // master switch off: nothing is attached
        foreach (var a in (root != null ? root.GetComponentsInChildren<Animator>(true) : Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None)))
        {
            var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, a.transform.root.name);
            if (rrp != null && rrp.enabled && a.GetComponent<TailSpring>() == null) a.gameObject.AddComponent<TailSpring>();
        }
    }

    class Node { public Transform t, child; public Chain ch; public float w; public Vector3 s, v; public bool init; public float len; }
    Node[] nodes; bool ready; Row row; World W; float age; int frameN;

    void Setup()
    {
        ready = true; var D = Load(); W = D.world ?? new World();
        row = RosterResolver.Pick(D.characters, rrx => rrx.name, transform.root.name);
        if (!D.enabled || row == null || !row.enabled || row.chains == null) { row = null; return; }
        var all = GetComponentsInChildren<Transform>(true); var list = new System.Collections.Generic.List<Node>();
        foreach (var ch in row.chains)
        {
            if (ch == null || !ch.enabled || ch.bones == null) continue;
            int n = ch.bones.Length;
            for (int i = 0; i < n; i++)
            {
                Transform t = null; foreach (var x in all) if (x.name == ch.bones[i]) { t = x; break; }
                if (t == null) { if (row.debug) Debug.LogWarning("[TailSpring] bone '" + ch.bones[i] + "' (chain " + ch.name + ") not found on " + transform.root.name); continue; }
                Transform child = null;
                if (i + 1 < n) foreach (var x in all) if (x.name == ch.bones[i + 1] && x.parent == t) { child = x; break; }
                float u = n > 1 ? (float)i / (n - 1) : 0f;
                list.Add(new Node { t = t, child = child, ch = ch, w = Mathf.Lerp(ch.rootInfluence, ch.tipInfluence, u) });
            }
        }
        nodes = list.ToArray(); if (nodes.Length == 0) row = null;
    }

    void LateUpdate()
    {
        if (!ready) Setup();
        if (row == null || nodes == null) return;
        float dt = Mathf.Min(Time.deltaTime, Mathf.Max(0.001f, W.maxFrameS));
        if (dt <= 0f) return;
        age += dt; float fade = W.fadeInS > 0f ? Mathf.Clamp01(age / W.fadeInS) : 1f;
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / Mathf.Max(1e-4f, W.solverStepS)), 1, Mathf.Max(1, W.maxSubsteps)); float h = dt / steps;
        Vector3 g = W.gravityDir.sqrMagnitude > 1e-6f ? W.gravityDir.normalized * W.gravityMps2 : Vector3.zero;
        float maxDev = 0f;
        for (int i = 0; i < nodes.Length; i++)
        {
            var nd = nodes[i]; var ch = nd.ch; if (nd.t == null) continue;
            Vector3 root = nd.t.position;
            Vector3 target = nd.child != null ? nd.child.position
                : root + nd.t.TransformDirection(ch.tipAxis.sqrMagnitude > 1e-6f ? ch.tipAxis.normalized : Vector3.up) * ch.tipLengthM * Mathf.Abs(nd.t.lossyScale.y);
            Vector3 anim = target - root; float len = anim.magnitude; if (len < W.minBoneLenM) continue;
            Vector3 animDir = anim / len; nd.len = len;
            if (!nd.init || (nd.s - target).sqrMagnitude > W.teleportM * W.teleportM) { nd.s = target; nd.v = Vector3.zero; nd.init = true; }
            for (int k = 0; k < steps; k++)
            {
                nd.v += ((target - nd.s) * ch.stiffness + g * ch.gravity) * h;
                nd.v *= Mathf.Exp(-ch.damping * h);
                nd.s += nd.v * h;
            }
            Vector3 d = nd.s - root; if (d.sqrMagnitude < 1e-10f) d = animDir;
            d = Vector3.RotateTowards(animDir, d.normalized, Mathf.Max(0f, ch.maxAngleDeg) * Mathf.Deg2Rad, 0f);
            float w = Mathf.Clamp01(nd.w) * fade;
            Vector3 fin = w >= 0.999f ? d : Vector3.Slerp(animDir, d, w);
            nd.t.rotation = Quaternion.FromToRotation(animDir, fin) * nd.t.rotation;
            nd.s = root + fin * len;                                   // keep the solver state on the bone length
            if (row.debug) maxDev = Mathf.Max(maxDev, Vector3.Angle(animDir, fin));
        }
        if (row.debug && PerfFlags.VerboseLogs && ++frameN % Mathf.Max(1, (int)row.debugEvery) == 0)
            Debug.Log("[TailSpring] " + transform.root.name + " bones=" + nodes.Length + " fade=" + fade.ToString("F2") + " maxDeviationDeg=" + maxDev.ToString("F1"));
    }
}
