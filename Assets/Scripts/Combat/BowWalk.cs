using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

// WALKFIRE v1 (laneLG, file-only patch, NOT applied). Dom: the bow must be fireable while walking (about half speed, no run) so Joan can kite.
// Two parts, both data (bow.json "move") and both off when move.whileShooting is false (= today, bit for bit):
//  1. SIM  Wants / Vel: DuelFighter.MoveTick only moves in Loco / Guard / Roll / Stagger; Windup / Active / Recovery give v = 0 (stepIn only). While the bow is up and the swing is a bow lane move
//     or special, the legs keep moving at walkSpeed x move.speedMul (x exhausted / wound / mount multipliers), never at runSpeed unless move.allowSprint.
//  2. VIEW Present: the shot clip (bow_loose ...) is a whole-body state on layer 0, so the legs would slide in the standing pose of the clip. While walking, a runtime PlayableGraph replaces the Animator output:
//     input 0 = the walk / strafe / idle state of the SAME controller (legs, hips), input 1 = the shot state of the same controller, masked to the upper body (an AvatarMask built at runtime from the bone names,
//     same split as GuardLayerSetup: spine chain, arms, head, hands on; hips and legs off). No asset, no controller change, no new serialized field: compile-only. move.upperLayer false = legs slide (sim part only).
// Nothing numeric lives here: bow.json move. The graph is destroyed at the end of the shot (the Animator takes over again, like NpcBowAnim).
public static class BowWalk
{
    static readonly Dictionary<DuelFighter, Runner> runners = new Dictionary<DuelFighter, Runner>();
    static readonly Dictionary<Animator, AvatarMask> masks = new Dictionary<Animator, AvatarMask>();
    static int lastLogTick = -999;

    // called from the existing BowHooks.ResetStatics / OnScene (a NEW RuntimeInitializeOnLoadMethod would never run in a compile-only take)
    public static void Reset()
    {
        foreach (var kv in runners) if (kv.Value != null) kv.Value.Stop(null, false);
        runners.Clear();
        foreach (var kv in masks) if (kv.Value != null) Object.Destroy(kv.Value);
        masks.Clear();
    }

    static bool Shooting(DuelFighter f) { return f.state == FState.Windup || f.state == FState.Active || f.state == FState.Recovery; }

    /// <summary>MoveTick: true while this fighter walks through a bow shot.</summary>
    public static bool Wants(DuelFighter f)
    {
        if (!BowHooks.AnyBowUp || f == null) return false;
        var M = BowCfg.Get().move; if (M == null || !M.whileShooting) return false;
        if (!Shooting(f) || f.guardHeld || (M.playerOnly && !f.IsPlayer)) return false;
        var m = f.CurrentMove; if (m == null || !BowHooks.Active(f) || Duel.FrozenFor(f)) return false;
        return BowCfg.LaneOf(m.id) != null || (M.specialsToo && BowCfg.SpecialByMove(m.id) != null);
    }

    /// <summary>MoveTick: the velocity of this sim tick (replaces the 0 / stepIn of the shot states).</summary>
    public static Vector3 Vel(DuelFighter f, Vector3 vNow)
    {
        var M = BowCfg.Get().move; Vector3 d = f.moveDir; d.y = 0f;
        if (d.sqrMagnitude < 1e-4f) return Vector3.zero;   // standing: no step-in toward the foe either (the bow does not close the gap)
        float sp = (f.run && M.allowSprint ? f.runSpeed * MountRider.SpeedMul(f, true) : f.walkSpeed * MountRider.SpeedMul(f, false) * M.speedMul) * f.MoveMulNow;
        if (M.log && Duel.TickCount - lastLogTick >= 30) { lastLogTick = Duel.TickCount; Debug.Log("[bow] walk-fire " + f.name + " " + f.state + " speed " + sp.ToString("0.00") + " m/s (walk " + f.walkSpeed.ToString("0.00") + " x " + M.speedMul.ToString("0.00") + ")"); }
        return d.normalized * sp;
    }

    /// <summary>DuelFighter.Update (per rendered frame, after the Animator layers): keep the upper-body shot over the walking legs.</summary>
    public static void Present(DuelFighter df, Fighter fi, float dt)
    {
        Runner r; runners.TryGetValue(df, out r);
        if (r == null && !BowHooks.AnyBowUp) return;
        var M = BowCfg.Get().move; if (M == null || !M.whileShooting || !M.upperLayer || fi == null || fi.anim == null) { if (r != null) { r.Stop(df, true); runners.Remove(df); } return; }
        bool want = Wants(df);
        if (r != null && !want) { r.Stop(df, Shooting(df)); runners.Remove(df); return; }
        if (!want) return;
        bool moving = df.moveDir.sqrMagnitude > 0.01f;
        if (r == null)
        {
            if (!moving || PresentationClock.Frozen) return;   // the graph starts the first frame the legs move; a standing shot stays on the Animator
            r = new Runner(); if (!r.Start(df, fi, M)) { r = null; return; }
            runners[df] = r;
        }
        r.Tick(df, fi, M, moving);
    }

    static AvatarMask MaskFor(Animator an, BowCfg.Move M)
    {
        AvatarMask m; if (masks.TryGetValue(an, out m) && m != null) return m;
        var list = new List<string>(); var all = an.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++) { var t = all[i]; if (t == an.transform) continue; string p = PathOf(an.transform, t); if (p != null) list.Add(p); }
        m = new AvatarMask(); m.transformCount = list.Count; int on = 0;
        for (int i = 0; i < list.Count; i++) { bool up = IsUpper(list[i], M); m.SetTransformPath(i, list[i]); m.SetTransformActive(i, up); if (up) on++; }
        masks[an] = m;
        Debug.Log("[bow] walk-fire upper mask built at runtime: " + on + " of " + list.Count + " paths active");
        return m;
    }
    static string PathOf(Transform root, Transform t)
    {
        var parts = new List<string>(); var c = t;
        while (c != null && c != root) { parts.Add(c.name); c = c.parent; }
        if (c == null) return null; parts.Reverse(); return string.Join("/", parts.ToArray());
    }
    static bool Has(string s, string[] toks) { if (toks == null) return false; for (int i = 0; i < toks.Length; i++) if (!string.IsNullOrEmpty(toks[i]) && s.IndexOf(toks[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
    // upper = the bone's own name has an upper token and NO path segment has a lower token (UpLeg / Foot / Toe stay on the legs); the hips bone (and anything above the spine) is never upper
    static bool IsUpper(string path, BowCfg.Move M)
    {
        int k = path.LastIndexOf('/'); string leaf = k >= 0 ? path.Substring(k + 1) : path;
        if (leaf.EndsWith("Hips", System.StringComparison.OrdinalIgnoreCase)) return false;
        return Has(leaf, M.upperTokens) && !Has(path, M.lowerTokens);
    }

    sealed class Runner
    {
        PlayableGraph graph; AnimatorControllerPlayable legs, upper; AnimationLayerMixerPlayable mix; bool alive; string legState = "";
        public bool Start(DuelFighter df, Fighter fi, BowCfg.Move M)
        {
            var an = fi.anim; var ctrl = an.runtimeAnimatorController; if (ctrl == null || string.IsNullOrEmpty(fi.Current)) return false;
            try
            {
                var si = an.IsInTransition(0) ? an.GetNextAnimatorStateInfo(0) : an.GetCurrentAnimatorStateInfo(0);   // where the shot clip is right now
                graph = PlayableGraph.Create("BowWalk"); graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                var outp = AnimationPlayableOutput.Create(graph, "BowWalkOut", an);
                legs = AnimatorControllerPlayable.Create(graph, ctrl); upper = AnimatorControllerPlayable.Create(graph, ctrl);
                mix = AnimationLayerMixerPlayable.Create(graph, 2);
                graph.Connect(legs, 0, mix, 0); graph.Connect(upper, 0, mix, 1);
                mix.SetInputWeight(0, 1f); mix.SetInputWeight(1, 1f); mix.SetLayerMaskFromAvatarMask(1u, MaskFor(an, M));
                upper.Play(Animator.StringToHash(fi.Current), 0, si.normalizedTime);
                legs.Play(Animator.StringToHash("idle"), 0, 0f); legState = "idle";
                outp.SetSourcePlayable(mix); graph.Play(); alive = true;
                if (M.log) Debug.Log("[bow] walk-fire graph up on " + df.name + ": upper '" + fi.Current + "' at " + si.normalizedTime.ToString("0.00") + ", animator speed " + an.speed.ToString("0.00"));
                return true;
            }
            catch (System.Exception e) { Debug.LogWarning("[bow] walk-fire graph failed, the shot keeps the Animator (legs slide): " + e.Message); Stop(df, false); return false; }
        }
        public void Tick(DuelFighter df, Fighter fi, BowCfg.Move M, bool moving)
        {
            if (!alive || !graph.IsValid()) return;
            try
            {
                var an = fi.anim; bool frozen = PresentationClock.Frozen;
                upper.SetSpeed(frozen ? 0f : an.speed);   // the sim writes the segment speed (windup / active / recovery) to Animator.speed; read it, do not recompute it
                string want = "idle";
                if (moving)
                {
                    Vector3 lm = df.transform.InverseTransformDirection(df.moveDir);
                    bool strafe = df.opponent != null && !df.opponent.Dead && Mathf.Abs(lm.x) > Mathf.Abs(lm.z) * M.strafeRatio;
                    want = strafe ? (lm.x < 0f ? "strafe_L" : "strafe_R") : "walk";
                    if (!legs.HasState(0, Animator.StringToHash(want))) want = "walk";
                }
                if (want != legState) { legState = want; legs.CrossFadeInFixedTime(Animator.StringToHash(want), 0.12f, 0, 0f); }
                legs.SetSpeed(frozen ? 0f : (moving ? Mathf.Max(0.05f, M.legAnimSpeed) * fi.animWeight : 1f));
            }
            catch (System.Exception e) { Debug.LogWarning("[bow] walk-fire tick failed: " + e.Message); Stop(df, true); }
        }
        // resume = true: the shot is still on (rare: bow dropped / stagger): hand the Animator the shot state at the upper clip's time, so nothing pops back to its pre-graph pose
        public void Stop(DuelFighter df, bool resume)
        {
            if (!alive) return; alive = false;
            float nt = 0f; try { if (upper.IsValid()) nt = upper.GetCurrentAnimatorStateInfo(0).normalizedTime; } catch { }
            if (graph.IsValid()) graph.Destroy();
            if (resume && df != null) { var fi = df.GetComponent<Fighter>(); if (fi != null && fi.anim != null && !string.IsNullOrEmpty(fi.Current)) fi.anim.Play(Animator.StringToHash(fi.Current), 0, Mathf.Clamp01(nt)); }
        }
    }
}
