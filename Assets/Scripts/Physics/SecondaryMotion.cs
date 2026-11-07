using UnityEngine;

// PHYSCLOTH v1: PRESENTATION-ONLY secondary motion (hair, skirt / tabard flaps, cape, tail, fins, hanging gear) as bone spring chains on the EXISTING 106 joints.
// No new joint name, no cloth mesh, no PhysX body, no collider. Data: Assets/Data/secondary_motion.json (StreamingAssets in the player). Every number lives there.
// SWITCH: physics.json  "secondary": { "enabled": false }  (sub-switch of the physics layer, ships OFF; independent of the physics master and of the PhysX clock).
//         Command line twins for A/B captures: -secondary:on / -secondary:off. Off = this component is never attached (zero cost) and the pose is untouched.
// WHY IT CANNOT CHANGE THE SIM: LateUpdate only (after the Animator wrote the pose), render time only (Time.deltaTime, never fixedDeltaTime or the 60 Hz tick),
//   no reference to the Duel sim, hit boxes, RNG, Random, Physics.* queries, Rigidbody or colliders. It only rotates the listed bones; nothing in combat reads those bones
//   (grep of Assets/Scripts for fk_hair / fk_cape / fk_skirt: only gear.json zone names). The simhash is therefore identical with the switch on or off.
// DIFFERENCE FROM TailSpring (KAIJUTAIL): the animated pose is recovered every frame (see Restore), so a bone the clips do NOT key cannot drift or lose its rest pose.
// A chain only runs when its row says "skinned": true, i.e. some mesh really has vertex weight on those joints (probed with lanePHYSCLOTH\tools\glb_rig_probe.py).
// If a TailSpring component sits on the same object this one stays out of its way (never two writers on one bone).
public class SecondaryMotion : MonoBehaviour
{
    [System.Serializable]
    public class Chain
    {
        public string name = "";
        public string kind = "";                        // label only: hair / skirt / cape / tail / fin / gear
        public bool enabled = true;
        public bool skinned = false;                    // true only when a mesh has vertex weight on these joints; false = nothing to see, nothing runs
        public string[] bones = new string[0];          // parents first; a bone whose NEXT listed bone is its direct child aims at that child, else at tipAxis * tipLengthM
        public float stiffness = 60f;                   // spring rate toward the animated tip, 1/s^2
        public float damping = 6f;                      // velocity decay, 1/s
        public float gravity = 0f;                      // multiple of world.gravityMps2
        public float maxAngleDeg = 20f;                 // largest deviation of one bone from its animated direction
        public float rootInfluence = 1f;                // effect weight at the first bone of the chain (0 = animated pose only)
        public float tipInfluence = 1f;                 // effect weight at the last bone
        public Vector3 tipAxis = new Vector3(0f, 1f, 0f); // bone-local aim axis when the bone has no listed child (the rig's chain bones point along local +Y)
        public float tipLengthM = 0.2f;                 // tip distance in model metres when the bone has no listed child
        public string note = "";
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
        public float maxSubstepS = 0.0083f;             // solver step (render time)
        public int maxSubsteps = 4;                     // per frame
        public float maxFrameS = 0.1f;                  // a longer frame is clamped to this (hitch, load)
        public float teleportM = 2f;                    // tip farther than this from its animated place = snap (respawn, scene reset)
        public float fadeInS = 0.5f;                    // influence ramps 0 -> 1 after the component starts
        public float minBoneLenM = 0.001f;
        public float writtenEpsDeg = 0.01f;             // a bone whose local rotation is within this of what we wrote last frame was NOT touched by the Animator
        public float maxStiffnessFrac = 0.5f;           // stiffness is capped at this / h^2 so a data typo cannot blow up the integrator
        public int setupRetries = 20;                   // the bones are looked up again this many times if none were found (model swapped in after boot)
        public float setupRetryS = 0.5f;
    }
    [System.Serializable] public class Root { public string note = ""; public int version; public World world = new World(); public Row[] characters = new Row[0]; }
    [System.Serializable] public class Sw { public bool enabled; }
    [System.Serializable] public class SwRoot { public Sw secondary = new Sw(); }

    static Root data; static int sw = -1; static bool hooked;                    // sw: -1 unread, 0 off, 1 on
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; sw = -1; hooked = false; }
    static Root Load()
    {
        if (data != null) return data;
        data = new Root();
        foreach (var p in DataPath.Candidates("secondary_motion.json"))
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Root>(System.IO.File.ReadAllText(p)); if (r != null) data = r; } catch { }
            break;
        }
        if (data.world == null) data.world = new World(); if (data.characters == null) data.characters = new Row[0];
        return data;
    }
    /// <summary>The sub-switch (physics.json secondary.enabled, or -secondary:on|off). Read-only for QA / A-B captures.</summary>
    public static bool SwitchOn
    {
        get
        {
            if (sw >= 0) return sw == 1;
            bool on = false;
            foreach (var p in DataPath.Candidates("physics.json"))
            {
                if (!System.IO.File.Exists(p)) continue;
                try { var r = JsonUtility.FromJson<SwRoot>(System.IO.File.ReadAllText(p).TrimStart('﻿')); if (r != null && r.secondary != null) on = r.secondary.enabled; } catch { }
                break;
            }
            foreach (var a in System.Environment.GetCommandLineArgs()) { if (a == "-secondary:on") on = true; else if (a == "-secondary:off") on = false; }
            sw = on ? 1 : 0; return on;
        }
    }
    /// <summary>Chains actually driven right now (all fighters), for the proof / debug line.</summary>
    public static int ActiveNodes;
    /// <summary>Wall ms the last LateUpdate of any instance took (only measured when the row has debug true).</summary>
    public static float LastMs;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!hooked) { hooked = true; UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => AttachAll(); }   // a scene reload (next opponent) re-attaches
        AttachAll();
    }
    static void AttachAll()
    {
        if (!SwitchOn) return;                                             // sub-switch off: nothing is attached
        var D = Load(); if (D.characters.Length == 0) return;
        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var rrp = RosterResolver.Pick(D.characters, rrx => rrx.name, a.transform.root.name);
            if (rrp != null && rrp.enabled && a.GetComponent<SecondaryMotion>() == null) a.gameObject.AddComponent<SecondaryMotion>();
        }
    }

    class Node
    {
        public Transform t, child; public Chain ch; public float w, len;
        public Vector3 s, v; public bool init;
        public Quaternion animLocal, written; public bool hasWritten;
    }
    Node[] nodes; bool ready; Row row; World W; float age; int frameN; float msSum; int msN;

    void Setup()
    {
        ready = true; var D = Load(); W = D.world ?? new World();
        row = RosterResolver.Pick(D.characters, rrx => rrx.name, transform.root.name);
        if (!SwitchOn || row == null || !row.enabled || row.chains == null) { row = null; return; }
        if (GetComponent("TailSpring") != null) { row = null; return; }   // TailSpring already owns this object's chain bones
        var all = GetComponentsInChildren<Transform>(true); var list = new System.Collections.Generic.List<Node>();
        foreach (var ch in row.chains)
        {
            if (ch == null || !ch.enabled || !ch.skinned || ch.bones == null) continue;
            int n = ch.bones.Length;
            for (int i = 0; i < n; i++)
            {
                Transform t = null; foreach (var x in all) if (x.name == ch.bones[i]) { t = x; break; }
                if (t == null) { if (row.debug) Debug.LogWarning("[SecondaryMotion] bone '" + ch.bones[i] + "' (chain " + ch.name + ") not found on " + transform.root.name); continue; }
                Transform child = null;
                if (i + 1 < n) foreach (var x in all) if (x.name == ch.bones[i + 1] && x.parent == t) { child = x; break; }
                float u = n > 1 ? (float)i / (n - 1) : 0f;
                list.Add(new Node { t = t, child = child, ch = ch, w = Mathf.Lerp(ch.rootInfluence, ch.tipInfluence, u), animLocal = t.localRotation });
            }
        }
        nodes = list.ToArray();
        if (nodes.Length == 0)                                              // the model may be swapped in after boot (joan_swap / GlbPreload): look again a few times
        {
            row = null; if (++tries < W.setupRetries) { ready = false; nextTry = Time.unscaledTime + Mathf.Max(0.1f, W.setupRetryS); }   // hc:ok min retry interval guard
        }
    }
    int tries; float nextTry;

    void OnDisable() { RestorePose(); }
    void RestorePose()
    {
        if (nodes == null || W == null) return;
        for (int i = 0; i < nodes.Length; i++)
        {
            var nd = nodes[i]; if (nd.t == null || !nd.hasWritten) continue;
            if (Quaternion.Angle(nd.t.localRotation, nd.written) < W.writtenEpsDeg) nd.t.localRotation = nd.animLocal;
            nd.hasWritten = false;
        }
    }

    void LateUpdate()
    {
        if (!ready) { if (Time.unscaledTime < nextTry) return; Setup(); }
        if (row == null || nodes == null) return;
        for (int i = 0; i < nodes.Length; i++) if (nodes[i].t == null) { nodes = null; row = null; ready = false; tries = 0; nextTry = Time.unscaledTime + 0.5f; return; }   // a bone was destroyed (model swapped): rebuild
        if (!SwitchOn) { RestorePose(); return; }                         // switched off at run time: give the Animator pose back
        float dt = Mathf.Min(Time.deltaTime, Mathf.Max(0.001f, W.maxFrameS));
        if (dt <= 0f) return;                                               // hit-stop / pause: leave the last pose exactly as it is
        long t0 = row.debug ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        age += dt; float fade = W.fadeInS > 0f ? Mathf.Clamp01(age / W.fadeInS) : 1f;
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / Mathf.Max(1e-4f, W.maxSubstepS)), 1, Mathf.Max(1, W.maxSubsteps)); float h = dt / steps;
        float kCap = W.maxStiffnessFrac > 0f ? W.maxStiffnessFrac / (h * h) : float.MaxValue;
        Vector3 g = W.gravityDir.sqrMagnitude > 1e-6f ? W.gravityDir.normalized * W.gravityMps2 : Vector3.zero;

        // 1. Restore the animated pose. A bone still holding exactly what we wrote last frame was not keyed by the clip: its animated rotation is the one we cached.
        for (int i = 0; i < nodes.Length; i++)
        {
            var nd = nodes[i]; if (nd.t == null) continue;
            Quaternion cur = nd.t.localRotation;
            if (nd.hasWritten && Quaternion.Angle(cur, nd.written) < W.writtenEpsDeg) cur = nd.animLocal;
            nd.animLocal = cur; nd.t.localRotation = cur;
        }

        // 2. Solve parent first; each bone springs toward where the animated pose (on top of its already deflected parent) puts its tip.
        float maxDev = 0f;
        for (int i = 0; i < nodes.Length; i++)
        {
            var nd = nodes[i]; var ch = nd.ch; if (nd.t == null) continue;
            Vector3 root = nd.t.position;
            Vector3 target = nd.child != null ? nd.child.position
                : root + nd.t.TransformDirection(ch.tipAxis.sqrMagnitude > 1e-6f ? ch.tipAxis.normalized : Vector3.up) * ch.tipLengthM * Mathf.Abs(nd.t.lossyScale.y);
            Vector3 anim = target - root; float len = anim.magnitude; if (len < W.minBoneLenM) continue;
            Vector3 animDir = anim / len; nd.len = len;
            if (!nd.init || (nd.s - target).sqrMagnitude > W.teleportM * W.teleportM || float.IsNaN(nd.s.x + nd.s.y + nd.s.z)) { nd.s = target; nd.v = Vector3.zero; nd.init = true; }
            float k = Mathf.Min(ch.stiffness, kCap);
            for (int s = 0; s < steps; s++)
            {
                nd.v += ((target - nd.s) * k + g * ch.gravity) * h;
                nd.v *= Mathf.Exp(-ch.damping * h);
                nd.s += nd.v * h;
            }
            Vector3 d = nd.s - root; if (d.sqrMagnitude < 1e-10f) d = animDir;
            d = Vector3.RotateTowards(animDir, d.normalized, Mathf.Max(0f, ch.maxAngleDeg) * Mathf.Deg2Rad, 0f);
            float w = Mathf.Clamp01(nd.w) * fade;
            Vector3 fin = w >= 0.999f ? d : Vector3.Slerp(animDir, d, w);   // hc:ok slerp epsilon
            nd.t.rotation = Quaternion.FromToRotation(animDir, fin) * nd.t.rotation;
            nd.written = nd.t.localRotation; nd.hasWritten = true;
            nd.s = root + fin * len;                                       // keep the solver state on the bone length
            if (row.debug) maxDev = Mathf.Max(maxDev, Vector3.Angle(animDir, fin));
        }
        ActiveNodes = nodes.Length;
        if (row.debug)
        {
            LastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency; msSum += LastMs; msN++;
            if (PerfFlags.VerboseLogs && ++frameN % Mathf.Max(1, (int)row.debugEvery) == 0)
            {
                Debug.Log("[SecondaryMotion] " + transform.root.name + " bones=" + nodes.Length + " fade=" + fade.ToString("F2") + " maxDeviationDeg=" + maxDev.ToString("F1") + " avgMs=" + (msSum / Mathf.Max(1, msN)).ToString("F4"));
                msSum = 0f; msN = 0;
            }
        }
    }
}
