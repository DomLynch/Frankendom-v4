using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// NPCARCHER v3 (Lead, OpenTrident clips 17 Nov): the NPC archers (Slag z1, Cedar z5, Huntsman z6) have no bow-draw animation in their Animator controllers. OpenTrident cut Joan's own bow_draw / bow_hold / bow_loose onto the same Mixamo-106 joint names
// (StreamingAssets/Npc/npc_bow_*.glb). They are loaded once with glTFast and played through a PlayableGraph on the archer's Animator while it aims (the controller is bypassed while the graph runs, and takes over again when it is destroyed).
// No serialized fields, no scene change: compile-only. Data: bandits.json ranged.useBowClips (default true), clip names fixed by file.
public static class NpcBowAnim
{
    public sealed class Set { public AnimationClip draw, hold, loose; public bool legsCut; }
    static readonly System.Collections.Generic.Dictionary<string, Set> sets = new System.Collections.Generic.Dictionary<string, Set>();
    static readonly System.Collections.Generic.HashSet<string> busy = new System.Collections.Generic.HashSet<string>();
    static readonly System.Collections.Generic.Dictionary<string, float> retryAt = new System.Collections.Generic.Dictionary<string, float>();
    /// <summary>Clip set per body kind: "" = Mixamo-106 bodies (Slag), "52" = the 52-bone bandit bodies (Cedar Archer, Huntsman: npc52_bow_*.glb, OpenTrident 17 Nov).</summary>
    public static Set Ready(string kind) { Set s; sets.TryGetValue(kind ?? "", out s); return s; }
    public static string KindOf(string characterId) { var t = BanditArcher.TypeOf(characterId); string r = t != null && t.ranged != null ? t.ranged.bowRig : ""; return r == "52" ? "52" : ""; }   // RIGDATA v1: bandits.json types[].ranged.bowRig, no hard-coded ids

    public static void Preload(string kind)
    {
        kind = kind ?? ""; float ra; if (sets.ContainsKey(kind) || busy.Contains(kind) || (retryAt.TryGetValue(kind, out ra) && Time.unscaledTime < ra)) return;   // LOADFIX v1: a failed load is retried after a backoff instead of blocking the rig for the whole process
        busy.Add(kind); Load(kind);
    }

    static async void Load(string kind)
    {
        string pre = kind == "52" ? "npc52_bow_" : "npc_bow_";
        try
        {
            var s = new Set();
            s.draw = await LoadClip(pre + "draw.glb"); s.hold = await LoadClip(pre + "hold.glb"); s.loose = await LoadClip(pre + "loose.glb");
            if (s.draw == null || s.hold == null || s.loose == null) { Debug.LogWarning("[NpcBow] clip load incomplete (" + pre + "): those archers keep the old pose, retry in 10 s"); retryAt[kind] = Time.unscaledTime + 10f; }
            else { sets[kind] = s; Debug.Log("[NpcBow] clips loaded (" + pre + "): draw " + s.draw.length.ToString("F2") + " s, hold " + s.hold.length.ToString("F2") + " s, loose " + s.loose.length.ToString("F2") + " s"); }
        }
        catch (Exception e) { Debug.LogWarning("[NpcBow] load failed (" + pre + "): " + e.Message + ", retry in 10 s"); retryAt[kind] = Time.unscaledTime + 10f; }
        finally { busy.Remove(kind); }   // LOADFIX v1
    }

    static async Task<AnimationClip> LoadClip(string file)
    {
        string p = Path.Combine(Path.Combine(Application.streamingAssetsPath, "Npc"), file);
        if (!File.Exists(p)) { Debug.LogWarning("[NpcBow] missing " + p); return null; }
        var g = new GLTFast.GltfImport();
        if (!await g.LoadFile(p)) return null;
        var cl = g.GetAnimationClips(); if (cl == null || cl.Length == 0 || cl[0] == null) return null;
        var c = cl[0]; c.legacy = false; c.wrapMode = WrapMode.Once; return c;
    }

    /// <summary>One playing sequence on one archer's Animator.</summary>
    public sealed class Player
    {
        PlayableGraph graph; AnimationClipPlayable cur; AnimationPlayableOutput outp; bool alive; float untilT; string phase = "";
        public bool Alive { get { return alive; } }
        public string Phase { get { return phase; } }
        // 52-bone bodies: the clip's leg / foot / toe curves and the hips translation were authored for another rest pose and left the Cedar hanging ~0.5 m above the ground on pointed feet. They are cut from the clips once (the legs then stay in the body's own bind stance, feet flat); the arms, spine and head keep the clip.
        static void CutLegs(Animator an, Set s)
        {
            if (s.legsCut) return; s.legsCut = true; int cut = 0;
            foreach (var t in an.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name; bool leg = n.IndexOf("Leg", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Toe", StringComparison.OrdinalIgnoreCase) >= 0; bool hips = n.EndsWith("Hips");
                if (!leg && !hips) continue;
                string path = PathOf(an.transform, t); if (path == null) continue;
                foreach (var c in new[] { s.draw, s.hold, s.loose })
                {
                    if (hips) { c.SetCurve(path, typeof(Transform), "localPosition.x", null); c.SetCurve(path, typeof(Transform), "localPosition.y", null); c.SetCurve(path, typeof(Transform), "localPosition.z", null); }
                    else foreach (var p in new[] { "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w", "localPosition.x", "localPosition.y", "localPosition.z" }) c.SetCurve(path, typeof(Transform), p, null);
                }
                cut++;
            }
            Debug.Log("[NpcBow] 52-bone clips: leg/foot/toe curves and hips translation cut on " + cut + " bones");
        }
        static string PathOf(Transform root, Transform t)
        {
            if (t == root) return ""; var parts = new System.Collections.Generic.List<string>(); var c = t;
            while (c != null && c != root) { parts.Add(c.name); c = c.parent; } if (c == null) return null; parts.Reverse(); return string.Join("/", parts);
        }
        public bool Start(Animator an, Set s, bool cutLegs = false)
        {
            if (an == null || s == null) return false;
            if (cutLegs) { try { CutLegs(an, s); } catch (Exception e) { Debug.LogWarning("[NpcBow] leg cut failed: " + e.Message); } }
            try
            {
                Stop();
                graph = PlayableGraph.Create("NpcBow"); graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                outp = AnimationPlayableOutput.Create(graph, "NpcBowOut", an);
                cur = AnimationClipPlayable.Create(graph, s.draw); outp.SetSourcePlayable(cur); graph.Play(); alive = true; phase = "draw"; untilT = Time.time + s.draw.length;
                return true;
            }
            catch (Exception e) { Debug.LogWarning("[NpcBow] start failed: " + e.Message); Stop(); return false; }
        }
        void Swap(AnimationClip c, string ph, bool loop)
        {
            var next = AnimationClipPlayable.Create(graph, c); next.SetDuration(loop ? double.MaxValue : c.length);
            outp.SetSourcePlayable(next); if (cur.IsValid()) cur.Destroy(); cur = next; phase = ph; untilT = Time.time + c.length;
        }
        /// <summary>Per frame: draw -> hold (loops) while aiming; loose = switch to the release clip; the graph ends when the loose clip is over.</summary>
        public void Tick(Set s, bool aiming, bool loosed)
        {
            if (!alive) return;
            try
            {
                if (loosed && phase != "loose") { Swap(s.loose, "loose", false); return; }
                if (phase == "draw" && Time.time >= untilT && aiming) { Swap(s.hold, "hold", true); return; }
                if (phase == "hold" && Time.time >= untilT && aiming) { Swap(s.hold, "hold", true); return; }
                if (phase == "loose" && Time.time >= untilT) Stop();
                else if (!aiming && phase != "loose") Stop();   // aborted (target too close, dead, frozen)
            }
            catch (Exception e) { Debug.LogWarning("[NpcBow] tick failed: " + e.Message); Stop(); }
        }
        public void Stop() { if (graph.IsValid()) graph.Destroy(); alive = false; phase = ""; }
    }
}
